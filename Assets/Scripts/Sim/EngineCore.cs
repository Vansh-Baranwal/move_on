using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using UnityEngine;
using RobotEngine.Core;
using RobotEngine.Robot;
using RobotEngine.Views;
using RobotEngine.World;

namespace RobotEngine.Sim
{
    /// <summary>
    /// Central engine coordinator. Executes API requests on the main thread, owns the robot
    /// state machine and retry counter, and enforces the engine boundary: no object data ever
    /// reaches a response. The scene advances only while /move, /turn or /drop execute.
    /// </summary>
    public sealed class EngineCore : MonoBehaviour
    {
        private const float MaxMoveDistance = 12f;
        private const float MaxTurnDegrees = 720f;

        private readonly ConcurrentQueue<ApiRequest> requests = new ConcurrentQueue<ApiRequest>();

        private RobotController robot;
        private AgentCamera agentCamera;
        private PovRecorder povRecorder;
        private string engineState = "Idle";
        private string activeCommand = "";
        private string runDirectory = "";
        private int retries;
        private bool busy;
        private Vector2 startPose;
        private float startHeading;

        public string StateName
        {
            get { return engineState; }
        }

        public string ActiveCommand
        {
            get { return activeCommand; }
        }

        public int RetryCount
        {
            get { return retries; }
        }

        public bool IsRecording
        {
            get { return povRecorder != null && povRecorder.SessionActive; }
        }

        public void Build(RobotController robotController, AgentCamera camera, PovRecorder recorder)
        {
            robot = robotController;
            agentCamera = camera;
            povRecorder = recorder;
            startPose = CoordinateConverter.UnityToApiPosition(robot.transform.position);
            startHeading = CoordinateConverter.UnityYawToApiHeading(robot.transform.eulerAngles.y);
        }

        public void Enqueue(ApiRequest request)
        {
            requests.Enqueue(request);
        }

        private void Update()
        {
            while (requests.TryDequeue(out ApiRequest request))
            {
                try
                {
                    Process(request);
                }
                catch (Exception ex)
                {
                    RespondJson(request, 500, ApiJson.Obj(ApiJson.Str("error", ex.Message)));
                }
            }
        }

        private void Process(ApiRequest request)
        {
            switch (request.Path)
            {
                case "/state":
                    if (request.Method == "GET") { HandleState(request); } else { MethodNotAllowed(request); }
                    break;
                case "/look":
                    if (request.Method == "GET") { HandleLook(request); } else { MethodNotAllowed(request); }
                    break;
                case "/depth":
                    if (request.Method == "GET") { HandleDepth(request); } else { MethodNotAllowed(request); }
                    break;
                case "/move":
                    HandleMove(request);
                    break;
                case "/turn":
                    HandleTurn(request);
                    break;
                case "/pickup":
                    HandlePickup(request);
                    break;
                case "/drop":
                    HandleDrop(request);
                    break;
                case "/reset":
                    HandleReset(request);
                    break;
                case "/record/start":
                    HandleRecordStart(request);
                    break;
                case "/record/stop":
                    HandleRecordStop(request);
                    break;
                default:
                    RespondJson(request, 404, ApiJson.Obj(ApiJson.Str("error", "unknown endpoint: " + request.Path)));
                    break;
            }
        }

        private void HandleState(ApiRequest request)
        {
            Vector2 pose = CoordinateConverter.UnityToApiPosition(robot.transform.position);
            float heading = CoordinateConverter.UnityYawToApiHeading(robot.transform.eulerAngles.y);
            RespondJson(request, 200, ApiJson.Obj(
                ApiJson.Str("state", engineState),
                ApiJson.Num("x", pose.x),
                ApiJson.Num("y", pose.y),
                ApiJson.Num("heading", heading),
                ApiJson.Bool("holding", robot.HeldObject != null),
                ApiJson.Int("retries", retries)));
        }

        private void HandleLook(ApiRequest request)
        {
            byte[] jpeg = agentCamera.CaptureJpeg();
            request.ResponseStatus = 200;
            request.ResponseContentType = "image/jpeg";
            request.ResponseData = jpeg;
            request.Done.Set();
        }

        private void HandleDepth(ApiRequest request)
        {
            float? depth = agentCamera.CaptureDepth();
            string depthMember = depth.HasValue
                ? ApiJson.Num("depth", depth.Value)
                : ApiJson.NullNum("depth");
            RespondJson(request, 200, ApiJson.Obj(depthMember));
        }

        private void HandleMove(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }
            if (busy) { RespondBusy(request); return; }

            MoveBody body;
            if (!TryParseBody(request, out body))
            {
                return;
            }

            float distance = Mathf.Clamp(body.distance, -MaxMoveDistance, MaxMoveDistance);
            busy = true;
            povRecorder.SetMotion(true);
            bool carrying = robot.HeldObject != null;
            engineState = carrying ? "Carry" : "GoToObject";

            StartCoroutine(robot.MoveRoutine(distance, result =>
            {
                povRecorder.SetMotion(false);
                busy = false;
                if (result.Collided)
                {
                    retries += 1;
                    engineState = "Blocked";
                }
                else
                {
                    engineState = carrying ? "Carry" : "GoToObject";
                }

                RespondJson(request, 200, ApiJson.Obj(
                    ApiJson.Bool("collided", result.Collided),
                    ApiJson.Num("moved", result.Moved),
                    ApiJson.Int("retries", retries)));
            }));
        }

        private void HandleTurn(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }
            if (busy) { RespondBusy(request); return; }

            TurnBody body;
            if (!TryParseBody(request, out body))
            {
                return;
            }

            float degrees = Mathf.Clamp(body.degrees, -MaxTurnDegrees, MaxTurnDegrees);
            busy = true;
            povRecorder.SetMotion(true);

            StartCoroutine(robot.TurnRoutine(degrees, turned =>
            {
                povRecorder.SetMotion(false);
                busy = false;
                RespondJson(request, 200, ApiJson.Obj(
                    ApiJson.Num("turned", turned),
                    ApiJson.Int("retries", retries)));
            }));
        }

        private void HandlePickup(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }
            if (busy) { RespondBusy(request); return; }

            // Success/failure only; the engine never names the object.
            bool success = robot.TryPickup();
            if (success)
            {
                engineState = "PickUp";
            }

            RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("success", success)));
        }

        private void HandleDrop(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }
            if (busy) { RespondBusy(request); return; }

            if (robot.HeldObject == null)
            {
                RespondJson(request, 200, ApiJson.Obj(
                    ApiJson.Bool("in_zone", false),
                    ApiJson.Str("error", "nothing_held")));
                return;
            }

            busy = true;
            povRecorder.SetMotion(true);
            engineState = "Drop";

            StartCoroutine(robot.DropRoutine(inZone =>
            {
                povRecorder.SetMotion(false);
                busy = false;
                engineState = inZone ? "Done" : "Idle";
                RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("in_zone", inZone)));
            }));
        }

        private void HandleReset(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }
            if (busy) { RespondBusy(request); return; }

            ResetWorld();
            RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("ok", true)));
        }

        private void HandleRecordStart(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }

            RecordBody body;
            if (!TryParseBody(request, out body))
            {
                return;
            }

            activeCommand = body.command ?? "";
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string framesDirectory;
            if (!string.IsNullOrEmpty(body.frames_dir))
            {
                // The agent owns its run layout; write POV frames where it points.
                framesDirectory = Path.IsPathRooted(body.frames_dir)
                    ? body.frames_dir
                    : Path.GetFullPath(Path.Combine(Application.dataPath, "..", body.frames_dir));
            }
            else
            {
                framesDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "runs", stamp));
            }

            runDirectory = framesDirectory;
            povRecorder.StartSession(framesDirectory);
            File.WriteAllText(Path.Combine(framesDirectory, "engine_meta.json"),
                ApiJson.Obj(ApiJson.Str("command", activeCommand)) + "\n");
            RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("ok", true),
                ApiJson.Str("frames_dir", framesDirectory)));
        }

        private void HandleRecordStop(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }

            int frames = povRecorder.StopSession();
            string metaPath = Path.Combine(runDirectory, "engine_meta.json");
            if (File.Exists(metaPath))
            {
                File.WriteAllText(metaPath, ApiJson.Obj(
                    ApiJson.Str("command", activeCommand),
                    ApiJson.Int("frames", frames)) + "\n");
            }

            RespondJson(request, 200, ApiJson.Obj(ApiJson.Int("frames", frames)));
        }

        private void ResetWorld()
        {
            robot.ReleaseHeldQuiet();
            SceneRegistry.Reset();
            robot.transform.position = CoordinateConverter.ApiToUnityPosition(startPose.x, startPose.y);
            robot.transform.rotation = Quaternion.Euler(0f, CoordinateConverter.ApiHeadingToUnityYaw(startHeading), 0f);
            retries = 0;
            engineState = "Idle";
        }

        private bool TryParseBody<T>(ApiRequest request, out T body)
        {
            body = JsonUtility.FromJson<T>(request.Body ?? "");
            if (body == null)
            {
                RespondJson(request, 400, ApiJson.Obj(ApiJson.Str("error", "invalid JSON body")));
                return false;
            }

            return true;
        }

        private void MethodNotAllowed(ApiRequest request)
        {
            RespondJson(request, 405, ApiJson.Obj(ApiJson.Str("error", "method not allowed")));
        }

        private void RespondBusy(ApiRequest request)
        {
            RespondJson(request, 409, ApiJson.Obj(ApiJson.Str("error", "engine busy")));
        }

        private static void RespondJson(ApiRequest request, int status, string json)
        {
            request.ResponseStatus = status;
            request.ResponseContentType = "application/json";
            request.ResponseData = System.Text.Encoding.UTF8.GetBytes(json);
            request.Done.Set();
        }

        [Serializable]
        private sealed class MoveBody
        {
            public float distance;
        }

        [Serializable]
        private sealed class TurnBody
        {
            public float degrees;
        }

        [Serializable]
        private sealed class RecordBody
        {
            public string command;
            public string frames_dir;
        }
    }
}
