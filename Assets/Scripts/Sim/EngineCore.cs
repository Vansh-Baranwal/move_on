using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using RobotEngine.Core;
using RobotEngine.Robot;
using RobotEngine.Views;
using RobotEngine.World;

namespace RobotEngine.Sim
{
    /// <summary>One agent-pushed log line, mirrored into the live panel and replay.</summary>
    public sealed class LogEntry
    {
        public int Step;
        public string State = "";
        public string Tool = "";
        public string Args = "";
        public string Result = "";
        public bool Collided;
        public string Note = "";
        public int Retries;
        public string Outcome = "";
        public string FramePath = "";
        public float X;
        public float Y;
        public float Heading;
        public bool Holding;
        public string Timestamp = "";
    }

    /// <summary>
    /// Central engine coordinator. Executes API requests on the main thread, owns the robot
    /// state machine and retry counter, the live log store, the path trail and replay, and
    /// enforces the engine boundary: no object data ever reaches a response. The simulation
    /// advances only while /move, /turn or /drop execute.
    /// </summary>
    public sealed class EngineCore : MonoBehaviour
    {
        private const float MaxMoveDistance = 12f;
        private const float MaxTurnDegrees = 720f;
        private const int MaxLogEntries = 600;

        private readonly ConcurrentQueue<ApiRequest> requests = new ConcurrentQueue<ApiRequest>();
        private readonly List<LogEntry> logEntries = new List<LogEntry>();
        private readonly List<Vector2> trail = new List<Vector2>();

        private WorldBuilder world;
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

        private bool replayActive;
        private int replayIndex;
        private Vector2 replayReturnPose;
        private float replayReturnHeading;
        private bool replayReturnHolding;

        private float recordingStartedAt = -1f;
        private InternalBrain brain;
        private bool brainActive;
        private string voiceText = "";
        private ViewRig viewRig;

        /// <summary>Last voice-command text pushed by the speech sidecar.</summary>
        public string VoiceText
        {
            get { return voiceText; }
        }

        /// <summary>When true, a voice command auto-starts a run (UI toggle).</summary>
        public bool AutoRunVoice { get; set; }

        public bool BrainActive
        {
            get { return brainActive; }
        }

        public InternalBrain Brain
        {
            get { return brain; }
        }

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

        public float RecordingElapsedSeconds
        {
            get { return recordingStartedAt < 0f ? 0f : Time.time - recordingStartedAt; }
        }

        public IReadOnlyList<LogEntry> Logs
        {
            get { return logEntries; }
        }

        public IReadOnlyList<Vector2> Trail
        {
            get { return trail; }
        }

        public bool ReplayActive
        {
            get { return replayActive; }
        }

        public int ReplayIndex
        {
            get { return replayIndex; }
        }

        public void Build(WorldBuilder worldBuilder, RobotController robotController, AgentCamera camera,
            PovRecorder recorder, ViewRig view)
        {
            world = worldBuilder;
            robot = robotController;
            agentCamera = camera;
            povRecorder = recorder;
            viewRig = view;
            startPose = CoordinateConverter.UnityToApiPosition(robot.transform.position);
            startHeading = CoordinateConverter.UnityYawToApiHeading(robot.transform.eulerAngles.y);
        }

        /// <summary>UI-only: mirror an agent log line into the live panel and replay store.</summary>
        public void AddLogEntry(LogEntry entry)
        {
            logEntries.Add(entry);
            if (logEntries.Count > MaxLogEntries)
            {
                logEntries.RemoveAt(0);
            }
        }

        /// <summary>
        /// One engine operation for the internal brain, with exactly the semantics of the
        /// matching HTTP endpoint (state machine, retries, trail, recording).
        /// </summary>
        public IEnumerator ExecuteOp(string tool, float value, Action<Dictionary<string, object>> done)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            switch (tool)
            {
                case "move":
                {
                    float distance = Mathf.Clamp(value, -MaxMoveDistance, MaxMoveDistance);
                    povRecorder.SetMotion(true);
                    bool carrying = robot.HeldObject != null;
                    engineState = carrying ? "Carry" : "GoToObject";
                    yield return StartCoroutine(robot.MoveRoutine(distance, moveResult =>
                    {
                        povRecorder.SetMotion(false);
                        if (moveResult.Collided)
                        {
                            retries += 1;
                            engineState = "Blocked";
                        }
                        else
                        {
                            engineState = carrying ? "Carry" : "GoToObject";
                            RecordTrailPoint();
                        }

                        result["collided"] = moveResult.Collided;
                        result["moved"] = moveResult.Moved;
                    }));
                    break;
                }
                case "turn":
                {
                    float degrees = Mathf.Clamp(value, -MaxTurnDegrees, MaxTurnDegrees);
                    povRecorder.SetMotion(true);
                    yield return StartCoroutine(robot.TurnRoutine(degrees, turned =>
                    {
                        povRecorder.SetMotion(false);
                        RecordTrailPoint();
                        result["turned"] = turned;
                    }));
                    break;
                }
                case "pickup":
                {
                    bool success = robot.TryPickup();
                    if (success)
                    {
                        engineState = "PickUp";
                    }

                    result["success"] = success;
                    break;
                }
                case "drop":
                {
                    if (robot.HeldObject == null)
                    {
                        result["in_zone"] = false;
                        result["error"] = "nothing_held";
                        break;
                    }

                    povRecorder.SetMotion(true);
                    engineState = "Drop";
                    yield return StartCoroutine(robot.DropRoutine(inZone =>
                    {
                        povRecorder.SetMotion(false);
                        engineState = inZone ? "Done" : "Idle";
                        RecordTrailPoint();
                        result["in_zone"] = inZone;
                    }));
                    break;
                }
                case "depth":
                {
                    float? depth = agentCamera.CaptureDepth();
                    result["depth"] = depth ?? float.NaN;
                    break;
                }
            }

            done(result);
        }

        /// <summary>Starts an internal-brain run from a plain-language command (UI or voice).</summary>
        public bool StartRun(string command)
        {
            if (busy || brainActive || string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "runs", "platform_" + stamp));
            povRecorder.StartSession(Path.Combine(directory, "pov"));
            recordingStartedAt = Time.time;
            activeCommand = command;
            engineState = "Idle";
            ResetWorld();
            brainActive = true;

            brain = gameObject.AddComponent<InternalBrain>();
            StartCoroutine(brain.Run(command, directory, this, agentCamera, robot, outcome =>
            {
                brainActive = false;
                int frames = povRecorder.StopSession();
                recordingStartedAt = -1f;
                float seconds = frames / 20f;
                string metaPath = Path.Combine(directory, "pov_meta.json");
                File.WriteAllText(metaPath, ApiJson.Obj(
                    ApiJson.Str("command", activeCommand),
                    ApiJson.Str("outcome", outcome),
                    ApiJson.Int("frames", frames),
                    ApiJson.Num("seconds", seconds),
                    ApiJson.Bool("too_long", seconds > 20f)) + "\n");
                AddLogEntry(new LogEntry
                {
                    Step = brain != null ? brain.Step : 0,
                    State = outcome,
                    Tool = "run_summary",
                    Args = "{}",
                    Result = "{\"outcome\":\"" + outcome + "\"}",
                    Collided = false,
                    Note = "internal brain finished",
                    Retries = retries,
                    Outcome = outcome,
                    X = 0f,
                    Y = 0f,
                    Heading = 0f,
                    Holding = false,
                    Timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                });
            }));

            return true;
        }

        public void StopBrain()
        {
            if (brain != null && brainActive)
            {
                brain.Cancel();
            }
        }

        /// <summary>Receives a voice command from the speech sidecar (UI-only text).</summary>
        public bool PushVoice(string text, bool autoRun)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            voiceText = text;
            return autoRun && StartRun(text);
        }

        /// <summary>Captures the audience display camera as JPEG (UI-only, verification).</summary>
        public byte[] CaptureDisplayView()
        {
            if (viewRig == null)
            {
                return null;
            }

            Camera display = viewRig != null ? viewRig.DisplayCamera : null;
            if (display == null)
            {
                return null;
            }

            RenderTexture temp = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = display.targetTexture;
            display.targetTexture = temp;
            display.Render();
            display.targetTexture = previous;
            RenderTexture.active = temp;
            Texture2D frame = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            frame.ReadPixels(new Rect(0f, 0f, 1280f, 720f), 0, 0);
            frame.Apply();
            byte[] jpeg = frame.EncodeToJPG(85);
            RenderTexture.active = previous;
            Destroy(frame);
            temp.Release();
            Destroy(temp);
            return jpeg;
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
                case "/log":
                    HandleLog(request);
                    break;
                case "/logs":
                    if (request.Method == "GET") { HandleLogs(request); } else { MethodNotAllowed(request); }
                    break;
                case "/replay":
                    HandleReplay(request);
                    break;
                case "/record/start":
                    HandleRecordStart(request);
                    break;
                case "/record/stop":
                    HandleRecordStop(request);
                    break;
                case "/run":
                    HandleRun(request);
                    break;
                case "/run/status":
                    if (request.Method == "GET") { HandleRunStatus(request); } else { MethodNotAllowed(request); }
                    break;
                case "/run/stop":
                    HandleRunStop(request);
                    break;
                case "/voice":
                    HandleVoice(request);
                    break;
                case "/view":
                    if (request.Method == "GET") { HandleView(request); } else { MethodNotAllowed(request); }
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
            if (brainActive) { RespondBrain(request); return; }
            if (replayActive) { RespondReplay(request); return; }

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
                    RecordTrailPoint();
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
            if (brainActive) { RespondBrain(request); return; }
            if (replayActive) { RespondReplay(request); return; }

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
                RecordTrailPoint();
                RespondJson(request, 200, ApiJson.Obj(
                    ApiJson.Num("turned", turned),
                    ApiJson.Int("retries", retries)));
            }));
        }

        private void HandlePickup(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }
            if (busy) { RespondBusy(request); return; }
            if (brainActive) { RespondBrain(request); return; }
            if (replayActive) { RespondReplay(request); return; }

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
            if (brainActive) { RespondBrain(request); return; }
            if (replayActive) { RespondReplay(request); return; }

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
                RecordTrailPoint();
                RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("in_zone", inZone)));
            }));
        }

        private void HandleReset(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }
            if (busy) { RespondBusy(request); return; }
            if (brainActive) { RespondBrain(request); return; }

            ResetBody body;
            if (!TryParseBody(request, out body))
            {
                return;
            }

            replayActive = false;
            // Always rebuild: seed 0 restores the curated layout after a seeded run.
            world.Rebuild(Mathf.Max(body.seed, 0));

            ResetWorld();
            RespondJson(request, 200, ApiJson.Obj(
                ApiJson.Bool("ok", true),
                ApiJson.Int("seed", body.seed)));
        }

        private void HandleLog(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }

            LogBody body;
            if (!TryParseBody(request, out body))
            {
                return;
            }

            logEntries.Add(new LogEntry
            {
                Step = body.step,
                State = body.state ?? "",
                Tool = body.tool ?? "",
                Args = body.args ?? "",
                Result = body.result ?? "",
                Collided = body.collided,
                Note = body.note ?? "",
                Retries = body.retries,
                Outcome = body.outcome ?? "",
                FramePath = body.frame_path ?? "",
                X = body.x,
                Y = body.y,
                Heading = body.heading,
                Holding = body.holding,
                Timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            });
            if (logEntries.Count > MaxLogEntries)
            {
                logEntries.RemoveAt(0);
            }

            RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("ok", true), ApiJson.Int("count", logEntries.Count)));
        }

        private void HandleLogs(ApiRequest request)
        {
            string lastTool = logEntries.Count > 0 ? logEntries[logEntries.Count - 1].Tool : "";
            RespondJson(request, 200, ApiJson.Obj(
                ApiJson.Int("count", logEntries.Count),
                ApiJson.Str("last_tool", lastTool)));
        }

        private void HandleReplay(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }
            if (busy) { RespondBusy(request); return; }

            ReplayBody body;
            if (!TryParseBody(request, out body))
            {
                return;
            }

            if (!body.active)
            {
                ExitReplay();
                RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("ok", true)));
                return;
            }

            if (logEntries.Count == 0)
            {
                RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("ok", false),
                    ApiJson.Str("error", "no log entries to replay")));
                return;
            }

            if (SetReplay(body.index))
            {
                RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("ok", true),
                    ApiJson.Int("index", replayIndex),
                    ApiJson.Str("tool", logEntries[replayIndex].Tool)));
            }
            else
            {
                RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("ok", false)));
            }
        }

        /// <summary>Scrubs the finished run: teleports the robot to the entry's pose.</summary>
        public bool SetReplay(int index)
        {
            if (busy || logEntries.Count == 0)
            {
                return false;
            }

            if (!replayActive)
            {
                // Remember the live pose so leaving replay can restore it.
                replayReturnPose = CoordinateConverter.UnityToApiPosition(robot.transform.position);
                replayReturnHeading = CoordinateConverter.UnityYawToApiHeading(robot.transform.eulerAngles.y);
                replayReturnHolding = robot.HeldObject != null;
            }

            replayIndex = Mathf.Clamp(index, 0, logEntries.Count - 1);
            LogEntry entry = logEntries[replayIndex];
            robot.transform.position = CoordinateConverter.ApiToUnityPosition(entry.X, entry.Y);
            robot.transform.rotation = Quaternion.Euler(0f,
                CoordinateConverter.ApiHeadingToUnityYaw(entry.Heading), 0f);
            replayActive = true;
            return true;
        }

        /// <summary>Leaves replay and restores the live pose captured on entry.</summary>
        public void ExitReplay()
        {
            if (!replayActive)
            {
                return;
            }

            replayActive = false;
            robot.transform.position = CoordinateConverter.ApiToUnityPosition(replayReturnPose.x, replayReturnPose.y);
            robot.transform.rotation = Quaternion.Euler(0f,
                CoordinateConverter.ApiHeadingToUnityYaw(replayReturnHeading), 0f);
        }

        public LogEntry ReplayEntry()
        {
            return replayActive && logEntries.Count > 0 ? logEntries[replayIndex] : null;
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
            recordingStartedAt = Time.time;
            File.WriteAllText(Path.Combine(framesDirectory, "engine_meta.json"),
                ApiJson.Obj(ApiJson.Str("command", activeCommand)) + "\n");
            RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("ok", true),
                ApiJson.Str("frames_dir", framesDirectory)));
        }

        private void HandleRecordStop(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }

            int frames = povRecorder.StopSession();
            float seconds = frames / 20f;
            recordingStartedAt = -1f;
            string metaPath = Path.Combine(runDirectory, "engine_meta.json");
            if (File.Exists(metaPath))
            {
                File.WriteAllText(metaPath, ApiJson.Obj(
                    ApiJson.Str("command", activeCommand),
                    ApiJson.Int("frames", frames),
                    ApiJson.Num("seconds", seconds)) + "\n");
            }

            RespondJson(request, 200, ApiJson.Obj(
                ApiJson.Int("frames", frames),
                ApiJson.Num("seconds", seconds),
                ApiJson.Bool("too_long", seconds > 20f)));
        }

        private void RecordTrailPoint()
        {
            Vector2 pose = CoordinateConverter.UnityToApiPosition(robot.transform.position);
            trail.Add(pose);
            if (trail.Count > 500)
            {
                trail.RemoveAt(0);
            }
        }

        private void ResetWorld()
        {
            robot.ReleaseHeldQuiet();
            SceneRegistry.Reset();
            robot.transform.position = CoordinateConverter.ApiToUnityPosition(startPose.x, startPose.y);
            robot.transform.rotation = Quaternion.Euler(0f, CoordinateConverter.ApiHeadingToUnityYaw(startHeading), 0f);
            retries = 0;
            engineState = "Idle";
            trail.Clear();
        }

        private bool TryParseBody<T>(ApiRequest request, out T body) where T : new()
        {
            // Empty body (pickup, reset, ...) means "all defaults".
            string payload = request.Body;
            body = string.IsNullOrWhiteSpace(payload) ? new T() : JsonUtility.FromJson<T>(payload);
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

        private void RespondReplay(ApiRequest request)
        {
            RespondJson(request, 409, ApiJson.Obj(ApiJson.Str("error", "replay active")));
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

        private void HandleRun(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }

            RunBody body;
            if (!TryParseBody(request, out body))
            {
                return;
            }

            bool started = StartRun(body.command);
            RespondJson(request, started ? 200 : 409, ApiJson.Obj(
                ApiJson.Bool("ok", started),
                ApiJson.Str("error", started ? "" : "engine busy or empty command")));
        }

        private void HandleRunStatus(ApiRequest request)
        {
            RespondJson(request, 200, ApiJson.Obj(
                ApiJson.Bool("active", brainActive),
                ApiJson.Int("step", brain != null ? brain.Step : 0),
                ApiJson.Str("tool", brain != null ? brain.LastTool : ""),
                ApiJson.Str("outcome", brain != null ? brain.Outcome : ""),
                ApiJson.Str("voice", voiceText),
                ApiJson.Bool("auto_run_voice", AutoRunVoice)));
        }

        private void HandleRunStop(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }
            StopBrain();
            RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("ok", true)));
        }

        private void HandleVoice(ApiRequest request)
        {
            if (request.Method == "GET") { MethodNotAllowed(request); return; }

            VoiceBody body;
            if (!TryParseBody(request, out body))
            {
                return;
            }

            bool started = PushVoice(body.text, body.auto);
            RespondJson(request, 200, ApiJson.Obj(
                ApiJson.Bool("ok", true),
                ApiJson.Bool("started", started),
                ApiJson.Str("text", voiceText)));
        }

        private void HandleView(ApiRequest request)
        {
            string query = request.Query ?? "";
            bool modeRequested = false;
            if (query.Contains("mode=tpp"))
            {
                if (viewRig != null)
                {
                    viewRig.SetMode(ViewMode.ThirdPerson);
                }
                modeRequested = true;
            }
            else if (query.Contains("mode=fpp"))
            {
                if (viewRig != null)
                {
                    viewRig.SetMode(ViewMode.FirstPerson);
                }
                modeRequested = true;
            }

            if (modeRequested)
            {
                // The display camera blends to the new pose over ~0.6s; let the caller
                // capture later with a plain GET /view.
                RespondJson(request, 200, ApiJson.Obj(ApiJson.Bool("ok", true)));
                return;
            }

            byte[] jpeg = CaptureDisplayView();
            if (jpeg == null)
            {
                RespondJson(request, 500, ApiJson.Obj(ApiJson.Str("error", "no display camera")));
                return;
            }

            request.ResponseStatus = 200;
            request.ResponseContentType = "image/jpeg";
            request.ResponseData = jpeg;
            request.Done.Set();
        }

        private void RespondBrain(ApiRequest request)
        {
            RespondJson(request, 409, ApiJson.Obj(ApiJson.Str("error", "internal brain is running")));
        }

        [Serializable]
        private sealed class RunBody
        {
            public string command;
        }

        [Serializable]
        private sealed class VoiceBody
        {
            public string text;
            public bool auto;
        }

        [Serializable]
        private sealed class ResetBody
        {
            public int seed;
        }

        [Serializable]
        private sealed class ReplayBody
        {
            public bool active;
            public int index;
        }

        [Serializable]
        private sealed class LogBody
        {
            public int step;
            public string state;
            public string tool;
            public string args;
            public string result;
            public bool collided;
            public string note;
            public int retries;
            public string outcome;
            public string frame_path;
            public float x;
            public float y;
            public float heading;
            public bool holding;
        }
    }
}
