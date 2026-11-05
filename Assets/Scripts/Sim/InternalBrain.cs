using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using RobotEngine.Robot;
using RobotEngine.Views;
using RobotEngine.World;

namespace RobotEngine.Sim
{
    /// <summary>
    /// The engine's built-in fetch brain: a C# port of the local vision policy.
    /// Drives the robot through the same op path the HTTP API uses, writes the same
    /// JSONL/PNG evidence trail, and reports to the live log panel. No Python needed.
    /// </summary>
    public sealed class InternalBrain : MonoBehaviour
    {
        private const float NearHeightPx = 140f;
        private const float RejectWindow = 10f;
        private static readonly float[] AvoidanceAngles = { 0f, -20f, 20f, -40f, 40f, -60f, 60f, -90f, 90f, -120f, 120f, 180f };

        private sealed class TallestCandidate
        {
            public Transform Target;
            public float Height;
            public float Bearing;
            public int Area;
        }
        private EngineCore engine;
        private AgentCamera camera;
        private RobotController robot;
        private string runDirectory;
        private string logPath;
        private string command;

        private string targetColour;
        private string targetShape;
        private string mode = "colour";
        private string phase = "scan";
        private float scanTurned;
        private float collisionTurnSign = 1f;
        private float failedBearing = -1f;
        private int failedBearingSteps;
        private readonly List<float> rejectedBearings = new List<float>();
        private readonly List<KeyValuePair<float, float>> seenTargets = new List<KeyValuePair<float, float>>();
        private int fullSweeps;
        private float targetBearing = -1f;
        private float zoneBearing = -1f;
        private string pending;
        private Dictionary<string, object> lastResult = new Dictionary<string, object>();
        private int prevRetries;
        private int pickupTries;
        private int dropTries;
        private readonly List<TallestCandidate> tallestCandidates = new List<TallestCandidate>();
        private float blindBudget;
        private int blindSteps;
        private bool seenZoneClose;
        private Vector2 cachedTargetPosition;
        private int cachedApproachSteps;
        private Transform targetTransform;
        private Vector2 targetSurfacePoint;
        private bool hasTargetSurfacePoint;
        private float targetDistance = float.PositiveInfinity;
        private Vector2 navigationWaypoint;
        private Vector2 navigationDestination;
        private bool hasNavigationWaypoint;
        private Vector2 zoneWorldPoint;
        private bool hasZoneWorldPoint;
        private float lastDepth = float.NaN;
        private bool attachImage = true;

        public int Step { get; private set; }
        public string LastTool { get; private set; } = "";
        public string Outcome { get; private set; } = "";
        public string ResultMessage { get; private set; } = "";
        public bool Cancelled { get; private set; }

        public void Cancel()
        {
            Cancelled = true;
        }

        public IEnumerator Run(string runCommand, string directory, EngineCore engineCore, AgentCamera agentCamera,
            RobotController robotController, Action<string> done)
        {
            command = runCommand;
            engine = engineCore;
            camera = agentCamera;
            robot = robotController;
            runDirectory = directory;
            Step = 0;
            Outcome = "";
            ResultMessage = "";
            blindBudget = 0f;
            blindSteps = 0;
            seenZoneClose = false;
            cachedTargetPosition = Vector2.zero;
            cachedApproachSteps = 0;
            targetTransform = null;
            targetSurfacePoint = Vector2.zero;
            hasTargetSurfacePoint = false;
            targetDistance = float.PositiveInfinity;
            hasNavigationWaypoint = false;
            zoneWorldPoint = Vector2.zero;
            hasZoneWorldPoint = false;
            targetColour = null;
            targetShape = null;
            mode = "colour";
            phase = "scan";
            scanTurned = 0f;
            rejectedBearings.Clear();
            seenTargets.Clear();
            fullSweeps = 0;
            targetBearing = -1f;
            zoneBearing = -1f;
            pending = null;
            lastResult = new Dictionary<string, object>();
            prevRetries = 0;
            pickupTries = 0;
            dropTries = 0;
            tallestCandidates.Clear();
            attachImage = true;
            Cancelled = false;

            string lower = command.ToLowerInvariant();
            foreach (string candidate in new[] { "red", "blue", "yellow", "green" })
            {
                if (ContainsWord(lower, candidate))
                {
                    targetColour = candidate;
                    break;
                }
            }

            if (ContainsWord(lower, "round") || ContainsWord(lower, "ball") || ContainsWord(lower, "sphere"))
            {
                targetShape = "round";
            }
            else if (ContainsWord(lower, "cube") || ContainsWord(lower, "box") || ContainsWord(lower, "square"))
            {
                targetShape = "boxy";
            }
            else if (ContainsWord(lower, "cylinder"))
            {
                targetShape = "cylinder";
            }
            else if (ContainsWord(lower, "cone"))
            {
                targetShape = "cone";
            }

            if (ContainsWord(lower, "tallest") || ContainsWord(lower, "biggest") || ContainsWord(lower, "largest"))
            {
                mode = "tallest";
            }

            bool questionLike = Regex.IsMatch(lower.Trim(), @"^(what|why|how|who|where|when|explain|tell me)\b");
            bool hasFetchIntent = ContainsAnyWord(lower,
                "bring", "fetch", "get", "retrieve", "pick up", "collect", "find", "locate", "take");
            bool hasTargetDescription = targetColour != null || targetShape != null || mode == "tallest";
            if (!hasTargetDescription || (questionLike && !hasFetchIntent))
            {
                ResultMessage = "What do you mean? Tell me which object to bring, such as the tallest object or a color and shape.";
                Outcome = "needs_clarification";
                done(Outcome);
                yield break;
            }
            if (mode != "tallest" && VisualMemoryDatabase.TryFindNearest(
                CurrentPose(), targetColour, targetShape, out cachedTargetPosition))
            {
                phase = "cache";
            }

            Directory.CreateDirectory(Path.Combine(runDirectory, "frames"));
            logPath = Path.Combine(runDirectory, "log.jsonl");

            string outcome = "fail_not_reached";
            string reason = "step budget exhausted";

            while (Step < 55 && !Cancelled)
            {
                float heading = CurrentHeading();
                Vector2 pose = CurrentPose();
                bool holding = robot.HeldObject != null;
                int retries = engine.RetryCount;
                lastDepth = camera.CaptureDepth() ?? float.NaN;
                targetDistance = float.PositiveInfinity;

                if (retries >= 6)
                {
                    outcome = "fail_collision";
                    reason = "gave up after 6 consecutive collision retries";
                    LogStep(retries, pose, heading, holding, "finish",
                        "{\"outcome\":\"" + outcome + "\"}", new Dictionary<string, object>
                        {
                            { "outcome", outcome }, { "reason", reason }
                        }, null, true);
                    break;
                }

                if (retries > prevRetries)
                {
                    collisionTurnSign *= -1f;
                    failedBearing = CurrentHeading();
                    failedBearingSteps = 4;
                    prevRetries = retries;
                    if (pending == "pickup")
                    {
                        pending = null;
                        yield return engine.ExecuteOp("pickup", 0f, result =>
                        {
                            lastResult = result;
                            LogStep(retries, pose, heading, holding, "pickup", "{}", result, null, false);
                        }, targetTransform);
                        if (lastResult.ContainsKey("success") && (bool)lastResult["success"])
                        {
                            phase = "return";
                        }

                        continue;
                    }

                    // Let the clearance planner pick a safe heading on the next step.
                    continue;
                }

                if (lastResult.ContainsKey("in_zone") && (bool)lastResult["in_zone"])
                {
                    outcome = "pass";
                    reason = "object delivered to the drop zone";
                    LogStep(retries, pose, heading, holding, "finish",
                        "{\"outcome\":\"pass\"}", new Dictionary<string, object>
                        {
                            { "outcome", outcome }, { "reason", reason }
                        }, null, true);
                    break;
                }

                if (lastResult.ContainsKey("success") && (bool)lastResult["success"])
                {
                    phase = "return";
                }

                if (lastResult.ContainsKey("in_zone") && !(bool)lastResult["in_zone"] && !holding)
                {
                    // The drop released the object right at the hold point - it is
                    // within reach. Grab it back and carry again with a fresh look.
                    phase = "return";
                    yield return engine.ExecuteOp("pickup", 0f, result =>
                    {
                        lastResult = result;
                        LogStep(retries, pose, heading, holding, "pickup", "{}", result, null, false);
                    }, targetTransform);
                    continue;
                }

                byte[] framePng = null;
                List<Blob> blobs = null;
                string note = "no new frame";
                if (attachImage)
                {
                    Color32[] pixels = camera.GetFramePixels();
                    blobs = RobotVision.Analyze(pixels, RobotVision.FrameWidth, RobotVision.FrameHeight);
                    note = Describe(blobs);
                    framePng = EncodePng(pixels);
                    Remember(blobs, heading);
                    VisualMemoryDatabase.Observe(blobs, camera);
                    Blob targetBlob = BestMatch(blobs, heading);
                    if (targetBlob != null)
                    {
                        CaptureTargetHit(targetBlob);
                    }
                }

                string tool;
                float value = 0f;
                Decide(state(retries, pose, heading, holding), blobs, ref note, out tool, out value);
                Dictionary<string, object> args = new Dictionary<string, object>();
                if (tool == "move")
                {
                    args["distance"] = value;
                }
                else if (tool == "turn")
                {
                    args["degrees"] = value;
                }
                else if (tool == "finish")
                {
                    args["outcome"] = outcome;
                    args["reason"] = note;
                }

                Step += 1;
                string framePath = null;
                if (framePng != null)
                {
                    framePath = "frames/step_" + Step.ToString("0000") + ".png";
                    File.WriteAllBytes(Path.Combine(runDirectory, framePath.Replace('/', Path.DirectorySeparatorChar)),
                        framePng);
                }

                LogStep(retries, pose, heading, holding, tool, MiniJson(args),
                    new Dictionary<string, object>(), framePath, false);
                if (Step % 5 == 0 || tool == "pickup" || tool == "drop" || tool == "finish")
                {
                    Debug.Log("[brain] step " + Step + ": " + tool + " " + MiniJson(args) + (note.Length > 0 ? " | " + note : ""));
                }

                if (tool == "finish")
                {
                    outcome = value > 0.5f ? "pass" : "fail_not_reached";
                    reason = note;
                    LogStep(retries, pose, heading, holding, "finish",
                        "{\"outcome\":\"" + outcome + "\"}", new Dictionary<string, object>
                        {
                            { "outcome", outcome }, { "reason", reason }
                        }, null, true);
                    break;
                }

                var opResult = new Dictionary<string, object>();
                yield return engine.ExecuteOp(tool, value, r => opResult = r, tool == "pickup" ? targetTransform : null);
                lastResult = opResult;
                bool moved = tool == "move" || tool == "turn";
                if (moved && opResult.ContainsKey("collided") && (bool)opResult["collided"])
                {
                    Vector2 cPose = CurrentPose();
                    Dictionary<string, object> collisionInfo = new Dictionary<string, object>
                    {
                        { "collided", true },
                        { "x", (float)cPose.x },
                        { "y", (float)cPose.y },
                        { "heading", CurrentHeading() },
                    };
                    LogStep(retries + (engine.RetryCount > retries ? 1 : 0), cPose, CurrentHeading(),
                        robot.HeldObject != null, "collision", "{}", collisionInfo, null, true);
                }
                else if (moved)
                {
                    prevRetries = engine.RetryCount;
                    if (tool == "move" && failedBearingSteps > 0)
                    {
                        failedBearingSteps--;
                    }
                }

                // Engine-internal policy: analyse every step (no LLM image budget).
                attachImage = true;
            }

            if (Cancelled)
            {
                outcome = "fail_not_reached";
                reason = "stopped by user";
            }

            Outcome = outcome;
            File.WriteAllText(Path.Combine(runDirectory, "meta.json"),
                "{\"command\":\"" + command.Replace("\"", "'") + "\",\"outcome\":\"" + outcome +
                "\",\"reason\":\"" + reason.Replace("\"", "'") + "\",\"steps\":" + Step + "}\n");
            done(outcome);
        }

        private static bool ContainsWord(string text, string word)
        {
            return Regex.IsMatch(text, @"\b" + Regex.Escape(word) + @"\b");
        }

        private static bool ContainsAnyWord(string text, params string[] words)
        {
            for (int i = 0; i < words.Length; i++)
            {
                if (ContainsWord(text, words[i]))
                {
                    return true;
                }
            }

            return false;
        }
        private struct BrainState
        {
            public int Retries;
            public Vector2 Pose;
            public float Heading;
            public bool Holding;
        }

        private BrainState state(int retries, Vector2 pose, float heading, bool holding)
        {
            BrainState s;
            s.Retries = retries;
            s.Pose = pose;
            s.Heading = heading;
            s.Holding = holding;
            return s;
        }

        private void Decide(BrainState s, List<Blob> blobs, ref string note, out string tool, out float value)
        {
            if (pending == "pickup")
            {
                pending = null;
                pickupTries += 1;
                tool = "pickup";
                value = 0f;
                note += " | pickup attempt";
                return;
            }

            if (pending == "drop")
            {
                pending = null;
                dropTries += 1;
                tool = "drop";
                value = 0f;
                note = "releasing object";
                return;
            }

            if (s.Holding || phase == "return")
            {
                Deliver(s, blobs, ref note, out tool, out value);
                return;
            }

            if (phase == "cache")
            {
                Blob cachedMatch = blobs != null ? BestMatch(blobs, s.Heading) : null;
                if (cachedMatch != null)
                {
                    targetBearing = BearingOf(s.Heading, cachedMatch.Cx);
                    phase = "approach";
                    note += " | visual memory reacquired target";
                    Approach(s, blobs, ref note, out tool, out value);
                    return;
                }

                NavigateToCachedTarget(s, blobs, ref note, out tool, out value);
                return;
            }

            if (phase == "scan")
            {
                Scan(s, blobs, ref note, out tool, out value);
                return;
            }

            Approach(s, blobs, ref note, out tool, out value);
        }

        private void MoveSafelyToward(float desiredBearing, float requestedDistance, ref string note,
            out string tool, out float value)
        {
            requestedDistance = Mathf.Clamp(requestedDistance, 0.1f, 0.8f);
            float bestScore = float.NegativeInfinity;
            float bestBearing = desiredBearing;
            float bestClearance = 0f;
            Vector3 direction;
            for (int i = 0; i < AvoidanceAngles.Length; i++)
            {
                float candidateBearing = Mathf.Repeat(desiredBearing + AvoidanceAngles[i], 360f);
                float radians = candidateBearing * Mathf.Deg2Rad;
                direction = new Vector3(Mathf.Cos(radians), 0f, -Mathf.Sin(radians));
                if (failedBearingSteps > 0 &&
                    Mathf.Abs(Mathf.DeltaAngle(failedBearing, candidateBearing)) < 25f)
                {
                    continue;
                }

                float clearance = robot.GetTravelClearance(direction, requestedDistance);
                if (clearance < 0.08f)
                {
                    continue;
                }

                float alignment = Mathf.Cos(AvoidanceAngles[i] * Mathf.Deg2Rad);
                float score = clearance + requestedDistance * 0.45f * alignment -
                    Mathf.Abs(AvoidanceAngles[i]) * 0.001f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestBearing = candidateBearing;
                    bestClearance = clearance;
                }
            }

            if (bestScore == float.NegativeInfinity)
            {
                tool = "turn";
                value = 45f * collisionTurnSign;
                note += " | boxed in; turning to find a clear lane";
                return;
            }

            float turn = Mathf.Repeat(bestBearing - CurrentHeading() + 540f, 360f) - 180f;
            if (Mathf.Abs(turn) > 12f)
            {
                tool = "turn";
                value = Mathf.Round(turn * 10f) / 10f;
                if (Mathf.Abs(Mathf.DeltaAngle(desiredBearing, bestBearing)) > 12f)
                {
                    note += " | steering around an obstacle";
                }

                return;
            }

            tool = "move";
            value = Mathf.Min(requestedDistance, Mathf.Max(0.08f, bestClearance - 0.03f));
            if (Mathf.Abs(Mathf.DeltaAngle(desiredBearing, bestBearing)) > 12f)
            {
                note += " | moving through a clear lane";
            }
        }

        private void CaptureTargetHit(Blob blob)
        {
            Vector3 point;
            Collider collider;
            if (!camera.TryGetWorldPointAt(
                blob.Cx / RobotVision.FrameWidth,
                blob.Cy / RobotVision.FrameHeight,
                out point, out collider) || collider == null || collider.attachedRigidbody == null)
            {
                return;
            }

            Transform candidate = collider.attachedRigidbody.transform;
            if (!IsRequestedTargetColour(candidate))
            {
                return;
            }

            targetTransform = candidate;
            targetSurfacePoint = new Vector2(point.x, point.z);
            hasTargetSurfacePoint = true;
            Vector3 cameraPosition;
            Quaternion cameraRotation;
            camera.GetDisplayPose(out cameraPosition, out cameraRotation);
            targetDistance = Vector3.Distance(cameraPosition, point);
        }

        private bool IsRequestedTargetColour(Transform candidate)
        {
            if (targetColour == null)
            {
                return true;
            }

            Renderer renderer = candidate.GetComponent<Renderer>();
            if (renderer == null)
            {
                renderer = candidate.GetComponentInChildren<Renderer>();
            }

            if (renderer == null || renderer.sharedMaterial == null)
            {
                return false;
            }

            float hue, saturation, value;
            Color.RGBToHSV(renderer.sharedMaterial.color, out hue, out saturation, out value);
            switch (targetColour)
            {
                case "green": return hue >= 100f / 360f && hue <= 150f / 360f;
                case "blue": return hue >= 210f / 360f && hue <= 250f / 360f;
                case "yellow": return hue >= 40f / 360f && hue <= 70f / 360f;
                case "red": return hue >= 350f / 360f || hue <= 10f / 360f;
                default: return true;
            }
        }

        private Vector2 ObjectApproachPoint(Vector2 from)
        {
            Vector2 center;
            float standoff;
            if (targetTransform != null)
            {
                center = new Vector2(targetTransform.position.x, targetTransform.position.z);
                standoff = 0.62f;
            }
            else if (hasTargetSurfacePoint)
            {
                center = targetSurfacePoint;
                standoff = 0.4f;
            }
            else
            {
                return from;
            }

            Vector2 away = from - center;
            if (away.sqrMagnitude < 0.0001f && targetBearing >= 0f)
            {
                float radians = targetBearing * Mathf.Deg2Rad;
                away = new Vector2(-Mathf.Cos(radians), Mathf.Sin(radians));
            }

            return center + away.normalized * standoff;
        }

        private void MoveToWorldPoint(BrainState s, Vector2 destination, float maxDistance,
            ref string note, out string tool, out float value)
        {
            Vector2 waypoint;
            bool destinationChanged = !hasNavigationWaypoint ||
                (destination - navigationDestination).sqrMagnitude > 0.36f;
            bool waypointReached = hasNavigationWaypoint &&
                (navigationWaypoint - s.Pose).sqrMagnitude < 0.04f;
            if (destinationChanged || waypointReached)
            {
                hasNavigationWaypoint = GridPathfinder.TryGetNextWaypoint(robot, s.Pose, destination,
                    out navigationWaypoint);
                navigationDestination = destination;
            }

            if (!hasNavigationWaypoint)
            {
                float directBearing = Mathf.Atan2(-(destination.y - s.Pose.y),
                    destination.x - s.Pose.x) * Mathf.Rad2Deg;
                MoveSafelyToward(directBearing, maxDistance, ref note, out tool, out value);
                note += " | no complete grid route; using clear-lane steering";
                return;
            }

            waypoint = navigationWaypoint;

            Vector2 delta = waypoint - s.Pose;
            float distance = delta.magnitude;
            if (distance < 0.03f)
            {
                tool = "turn";
                value = 0f;
                return;
            }

            float bearing = Mathf.Atan2(-delta.y, delta.x) * Mathf.Rad2Deg;
            float turn = Mathf.Repeat(bearing - CurrentHeading() + 540f, 360f) - 180f;
            if (Mathf.Abs(turn) > 8f)
            {
                tool = "turn";
                value = Mathf.Round(turn * 10f) / 10f;
                note += " | turning to face next route waypoint";
                return;
            }

            float distanceToMove = Mathf.Min(maxDistance, distance);
            float radians = bearing * Mathf.Deg2Rad;
            Vector3 direction = new Vector3(Mathf.Cos(radians), 0f, -Mathf.Sin(radians));
            float clearance = robot.GetTravelClearance(direction, distanceToMove);
            if (clearance < 0.08f)
            {
                hasNavigationWaypoint = false;
                tool = "turn";
                value = 35f * collisionTurnSign;
                note += " | route changed; replanning around an obstacle";
                return;
            }

            tool = "move";
            value = Mathf.Min(distanceToMove, Mathf.Max(0.08f, clearance - 0.03f));
            note += " | following forward route";
        }
        private void NavigateToCachedTarget(BrainState s, List<Blob> blobs, ref string note,
            out string tool, out float value)
        {
            cachedApproachSteps++;
            Vector2 delta = cachedTargetPosition - s.Pose;
            float distance = delta.magnitude;
            if (distance <= 1.25f || cachedApproachSteps > 12)
            {
                phase = "scan";
                scanTurned = 0f;
                note += distance <= 1.25f
                    ? " | reached remembered area; checking for target"
                    : " | remembered route limit reached; resuming scan";
                Scan(s, blobs, ref note, out tool, out value);
                return;
            }

            MoveToWorldPoint(s, cachedTargetPosition, 0.65f, ref note, out tool, out value);
            note += " | navigating to remembered sighting";
        }

        private void Scan(BrainState s, List<Blob> blobs, ref string note, out string tool, out float value)
        {
            if (blobs == null)
            {
                tool = "turn";
                value = 60f;
                note = "scan continues";
                return;
            }

            if (mode == "tallest")
            {
                CollectTallestCandidates(blobs, s.Heading);
                if (scanTurned >= 340f && SelectTallestCandidate(s, ref note, out tool, out value))
                {
                    return;
                }
            }
            else
            {
                Blob match = BestMatch(blobs, s.Heading);
                if (match != null)
                {
                    targetBearing = BearingOf(s.Heading, match.Cx);
                    phase = "approach";
                    float offset = OffsetDegrees(match.Cx);
                    note += " | target spotted, bearing " + targetBearing.ToString("F0", CultureInfo.InvariantCulture);
                    tool = "turn";
                    value = Mathf.Round(offset * 10f) / 10f;
                    return;
                }
            }

            if (scanTurned >= 360f)
            {
                if (rejectedBearings.Count > 0 && fullSweeps < 2)
                {
                    fullSweeps += 1;
                    rejectedBearings.Clear();
                    scanTurned = 0f;
                    tool = "turn";
                    value = 60f;
                    note += " | clean re-sweep after rejections";
                    return;
                }

                tool = "finish";
                value = 0f;
                note = "No matching items found for your request.";
                ResultMessage = note;
                return;
            }

            scanTurned += 60f;
            tool = "turn";
            value = 60f;
        }

        private void CollectTallestCandidates(List<Blob> blobs, float heading)
        {
            foreach (Blob blob in blobs)
            {
                if (blob == null || blob.Colour == "cyan")
                {
                    continue;
                }

                Vector3 hitPoint;
                Collider hitCollider;
                if (!camera.TryGetWorldPointAt(blob.Cx / RobotVision.FrameWidth,
                    blob.Cy / RobotVision.FrameHeight, out hitPoint, out hitCollider) ||
                    hitCollider == null || hitCollider.attachedRigidbody == null)
                {
                    continue;
                }

                Transform target = hitCollider.attachedRigidbody.transform;
                if (SceneRegistry.IsInsideDropZone(target.position) ||
                    !BlobColourMatches(target, blob.Colour))
                {
                    continue;
                }

                float height = hitCollider.bounds.size.y;
                TallestCandidate existing = null;
                for (int i = 0; i < tallestCandidates.Count; i++)
                {
                    if (tallestCandidates[i].Target == target)
                    {
                        existing = tallestCandidates[i];
                        break;
                    }
                }

                if (existing == null)
                {
                    tallestCandidates.Add(new TallestCandidate
                    {
                        Target = target,
                        Height = height,
                        Bearing = BearingOf(heading, blob.Cx),
                        Area = blob.Area
                    });
                }
                else
                {
                    existing.Height = Mathf.Max(existing.Height, height);
                    existing.Bearing = BearingOf(heading, blob.Cx);
                    existing.Area = Mathf.Max(existing.Area, blob.Area);
                }
            }
        }

        private bool BlobColourMatches(Transform target, string colour)
        {
            Renderer renderer = target.GetComponent<Renderer>();
            if (renderer == null)
            {
                renderer = target.GetComponentInChildren<Renderer>();
            }

            if (renderer == null || renderer.sharedMaterial == null)
            {
                return false;
            }

            float hue, saturation, brightness;
            Color.RGBToHSV(renderer.sharedMaterial.color, out hue, out saturation, out brightness);
            switch (colour)
            {
                case "green": return hue >= 100f / 360f && hue <= 150f / 360f;
                case "blue": return hue >= 210f / 360f && hue <= 250f / 360f;
                case "yellow": return hue >= 40f / 360f && hue <= 70f / 360f;
                case "red": return hue >= 350f / 360f || hue <= 10f / 360f;
                default: return false;
            }
        }

        private bool SelectTallestCandidate(BrainState s, ref string note, out string tool, out float value)
        {
            tool = null;
            value = 0f;
            TallestCandidate tallest = null;
            foreach (TallestCandidate candidate in tallestCandidates)
            {
                if (candidate.Target != null && (tallest == null || candidate.Height > tallest.Height ||
                    (Mathf.Approximately(candidate.Height, tallest.Height) && candidate.Area > tallest.Area)))
                {
                    tallest = candidate;
                }
            }

            if (tallest == null)
            {
                return false;
            }

            targetTransform = tallest.Target;
            targetBearing = tallest.Bearing;
            phase = "approach";
            float delta = Mathf.Repeat(targetBearing - s.Heading + 540f, 360f) - 180f;
            note = "tallest visible object selected (" + tallest.Height.ToString("F2", CultureInfo.InvariantCulture) +
                " m high)";
            tool = "turn";
            value = Mathf.Round(delta * 10f) / 10f;
            return true;
        }
        private bool TryPickupTargetNow(BrainState s, ref string note, out string tool, out float value)
        {
            tool = null;
            value = 0f;
            if (targetTransform == null)
            {
                return false;
            }

            Vector2 targetPosition = new Vector2(targetTransform.position.x, targetTransform.position.z);
            Vector2 delta = targetPosition - s.Pose;
            Collider targetCollider = targetTransform.GetComponentInChildren<Collider>();
            float reach = RobotController.ReachDistance + (targetCollider != null
                ? Mathf.Max(targetCollider.bounds.extents.x, targetCollider.bounds.extents.z) : 0f);
            if (delta.magnitude > reach)
            {
                return false;
            }

            float bearing = Mathf.Atan2(-delta.y, delta.x) * Mathf.Rad2Deg;
            float turn = Mathf.Repeat(bearing - CurrentHeading() + 540f, 360f) - 180f;
            if (Mathf.Abs(turn) > 10f)
            {
                tool = "turn";
                value = Mathf.Round(turn * 10f) / 10f;
                note += " | aligning with requested object for pickup";
                return true;
            }

            tool = "pickup";
            note += " | requested object within pickup reach";
            return true;
        }
        private void Approach(BrainState s, List<Blob> blobs, ref string note, out string tool, out float value)
        {
            if (TryPickupTargetNow(s, ref note, out tool, out value))
            {
                return;
            }

            Blob blob = blobs != null ? BestMatch(blobs, s.Heading) : null;
            if (blob == null)
            {
                if (targetTransform != null || hasTargetSurfacePoint)
                {
                    Vector2 approachPoint = ObjectApproachPoint(s.Pose);
                    if ((approachPoint - s.Pose).magnitude <= 0.52f && targetTransform != null &&
                        targetDistance <= 0.62f)
                    {
                        pending = "pickup";
                        tool = "move";
                        value = 0.18f;
                        note += " | target reached; closing for pickup";
                        return;
                    }

                    MoveToWorldPoint(s, approachPoint, 0.65f, ref note, out tool, out value);
                    note += " | routing to remembered target position";
                    return;
                }

                if (pickupTries >= 2 && targetBearing >= 0f)
                {
                    rejectedBearings.Add(targetBearing);
                    pickupTries = 0;
                    AfterRejection(s, ref note, out tool, out value);
                    return;
                }

                if (targetBearing < 0f)
                {
                    phase = "scan";
                    scanTurned = 0f;
                    tool = "turn";
                    value = 60f;
                    note += " | target lost, resuming scan";
                    return;
                }

                MoveSafelyToward(targetBearing, 0.5f, ref note, out tool, out value);
                note += " | driving on remembered route";
                return;
            }

            float offset = OffsetDegrees(blob.Cx);
            float height = blob.H;
            targetBearing = BearingOf(s.Heading, blob.Cx);
            if (height >= NearHeightPx && targetShape == "round" && !float.IsNaN(lastDepth) && lastDepth < 2.8f)
            {
                float estimate = PhysicalHeight(height, lastDepth);
                if (estimate > 0.34f)
                {
                    rejectedBearings.Add(targetBearing);
                    note += " | object measures " + estimate.ToString("F2", CultureInfo.InvariantCulture) +
                            " m tall, too big; retargeting";
                    AfterRejection(s, ref note, out tool, out value);
                    return;
                }
            }

            if (Mathf.Abs(offset) > 14f && height >= NearHeightPx)
            {
                tool = "turn";
                value = Mathf.Round(offset * 10f) / 10f;
                note += " | centring target";
                return;
            }

            if ((targetTransform != null || hasTargetSurfacePoint) && targetDistance <= 0.62f &&
                Mathf.Abs(offset) <= 14f)
            {
                pending = "pickup";
                tool = "move";
                value = 0.18f;
                note += " | requested object in pickup range";
                return;
            }

            if (targetTransform != null || hasTargetSurfacePoint)
            {
                MoveToWorldPoint(s, ObjectApproachPoint(s.Pose), 0.65f,
                    ref note, out tool, out value);
                note += " | routing around obstacles toward target";
                return;
            }

            MoveSafelyToward(targetBearing, 0.65f, ref note, out tool, out value);
            note += " | approaching visible target";
        }
        private void Deliver(BrainState s, List<Blob> blobs, ref string note, out string tool, out float value)
        {
            Blob zone = null;
            if (blobs != null)
            {
                foreach (Blob blob in blobs)
                {
                    if (blob.Colour == "cyan")
                    {
                        zone = blob;
                        break;
                    }
                }

                if (zone != null)
                {
                    zoneBearing = BearingOf(s.Heading, zone.Cx);
                    Vector3 zonePoint;
                    if (camera.TryGetWorldPointAt(zone.Cx / RobotVision.FrameWidth,
                        zone.Cy / RobotVision.FrameHeight, out zonePoint))
                    {
                        zoneWorldPoint = new Vector2(zonePoint.x, zonePoint.z);
                        hasZoneWorldPoint = true;
                    }
                }
            }

            if (zone != null)
            {
                float offset = OffsetDegrees(zone.Cx);
                float zoneDistance = 1.25f * 320f / Mathf.Max(1f, zone.W);
                blindBudget = Mathf.Max(0f, zoneDistance - 0.9f);
                blindSteps = 0;
                if (zoneDistance <= 1.4f || zone.W >= 300)
                {
                    seenZoneClose = true;
                }

                if ((zone.W >= 420 || zoneDistance <= 0.9f) && Mathf.Abs(offset) <= 14f)
                {
                    pending = "drop";
                    tool = "move";
                    value = 0.25f;
                    note = "over the zone, dropping next";
                    return;
                }

                if (hasZoneWorldPoint && zoneDistance > 1.4f)
                {
                    Vector2 fromZone = s.Pose - zoneWorldPoint;
                    Vector2 destination = zoneWorldPoint + fromZone.normalized * 0.45f;
                    MoveToWorldPoint(s, destination, 0.65f, ref note, out tool, out value);
                }
                else
                {
                    MoveSafelyToward(zoneBearing, Mathf.Clamp(zoneDistance - 0.9f, 0.3f, 0.7f),
                        ref note, out tool, out value);
                }
                note = "carrying to zone" + note;
                return;
            }

            // The carried payload occludes the zone between ~1-2.5 m. Drive blind on the
            // remembered bearing for the remaining budget; once it is spent, drop anyway
            // (drop reports the truth) or sweep to re-acquire if retries remain.
            if (zoneBearing >= 0f)
            {
                if (blindBudget > 0f)
                {
                    blindBudget -= 0.5f;
                    blindSteps += 1;
                    MoveSafelyToward(zoneBearing, Mathf.Min(0.5f, blindBudget + 0.5f),
                        ref note, out tool, out value);
                    note += " | carrying blind past the payload";
                    return;
                }

                if (seenZoneClose && dropTries < 1)
                {
                    pending = "drop";
                    tool = "move";
                    value = 0.2f;
                    note = "blind budget spent - dropping next";
                    return;
                }

                scanTurned += 45f;
                tool = "turn";
                value = 45f;
                note = "zone missed; sweeping to re-acquire";
                return;
            }

            scanTurned += 60f;
            tool = "turn";
            value = 60f;
            note = "carrying; scanning for the drop zone";
        }

        private void AfterRejection(BrainState s, ref string note, out string tool, out float value)
        {
            targetBearing = -1f;
            pickupTries = 0;
            foreach (KeyValuePair<float, float> candidate in seenTargets)
            {
                bool rejected = false;
                foreach (float bearing in rejectedBearings)
                {
                    if (Mathf.Abs(Mathf.Repeat(candidate.Key - bearing + 540f, 360f) - 180f) < RejectWindow)
                    {
                        rejected = true;
                        break;
                    }
                }

                if (!rejected)
                {
                    phase = "approach";
                    targetBearing = candidate.Key;
                    float delta = Mathf.Repeat(candidate.Key - s.Heading + 540f, 360f) - 180f;
                    note += " | retargeting bearing " + candidate.Key.ToString("F0", CultureInfo.InvariantCulture);
                    tool = "turn";
                    value = Mathf.Round(delta * 10f) / 10f;
                    return;
                }
            }

            phase = "scan";
            scanTurned = 0f;
            tool = "turn";
            value = 60f;
            note += " | rescanning";
        }

        private bool Matches(Blob blob, float heading)
        {
            if (blob.Colour == "cyan")
            {
                return false;
            }

            if ((targetShape == "round" && blob.Shape != "round") ||
                (targetShape == "boxy" && blob.Shape != "boxy") ||
                (targetShape == "cone" && blob.Shape != "tapered"))
            {
                return false;
            }
            if (targetShape == "cylinder")
            {
                Vector3 point;
                Collider collider;
                if (!camera.TryGetWorldPointAt(blob.Cx / RobotVision.FrameWidth,
                    blob.Cy / RobotVision.FrameHeight, out point, out collider) ||
                    !(collider is CapsuleCollider))
                {
                    return false;
                }
            }
            if (targetColour != null && blob.Colour != targetColour)
            {
                return false;
            }

            float bearing = BearingOf(heading, blob.Cx);
            foreach (float rejected in rejectedBearings)
            {
                if (Mathf.Abs(Mathf.Repeat(bearing - rejected + 540f, 360f) - 180f) < RejectWindow)
                {
                    return false;
                }
            }

            return true;
        }

        private Blob BestMatch(List<Blob> blobs, float heading)
        {
            Blob best = null;
            foreach (Blob blob in blobs)
            {
                if (!Matches(blob, heading))
                {
                    continue;
                }

                if (best == null)
                {
                    best = blob;
                    continue;
                }

                if (targetBearing >= 0f)
                {
                    float errorBest = Mathf.Abs(Mathf.Repeat(BearingOf(heading, best.Cx) - targetBearing + 540f, 360f) - 180f);
                    float errorBlob = Mathf.Abs(Mathf.Repeat(BearingOf(heading, blob.Cx) - targetBearing + 540f, 360f) - 180f);
                    if (errorBlob < errorBest)
                    {
                        best = blob;
                    }
                }
                else if (targetShape == "round")
                {
                    if (blob.Area < best.Area)
                    {
                        best = blob;
                    }
                }
                else if (blob.Area > best.Area)
                {
                    best = blob;
                }
            }

            return best;
        }

        private void Remember(List<Blob> blobs, float heading)
        {
            foreach (Blob blob in blobs)
            {
                float bearing = BearingOf(heading, blob.Cx);
                if (blob.Colour == "cyan")
                {
                    zoneBearing = bearing;
                }
                else if (Matches(blob, heading))
                {
                    seenTargets.RemoveAll(item =>
                        Mathf.Abs(Mathf.Repeat(item.Key - bearing + 540f, 360f) - 180f) < 10f);
                    seenTargets.Add(new KeyValuePair<float, float>(bearing, blob.Area));
                }
            }
        }

        private string Describe(List<Blob> blobs)
        {
            if (blobs == null)
            {
                return "no new frame";
            }

            if (blobs.Count == 0)
            {
                return "SEE: no objects, only walls and floor";
            }

            List<string> parts = new List<string>();
            for (int i = 0; i < blobs.Count && i < 4; i++)
            {
                Blob blob = blobs[i];
                string where = blob.Cx < 240f ? "left" : blob.Cx > 400f ? "right" : "centre";
                parts.Add(blob.Colour + " " + blob.Shape + " (w" + blob.W + " h" + blob.H + ", " + where + ")");
            }

            return "SEE: " + string.Join(", ", parts);
        }

        private static float OffsetDegrees(float cx)
        {
            return (cx - RobotVision.FrameWidth / 2f) * (90f / RobotVision.FrameWidth);
        }

        private static float BearingOf(float heading, float cx)
        {
            return Mathf.Repeat(heading + OffsetDegrees(cx), 360f);
        }

        private static float PhysicalHeight(float heightPx, float depthM)
        {
            return depthM <= 0.01f ? 0f : heightPx * depthM / 320f;
        }

        private float CurrentHeading()
        {
            return Mathf.Repeat(robot.transform.eulerAngles.y - 90f, 360f);
        }

        private Vector2 CurrentPose()
        {
            return new Vector2(robot.transform.position.x, robot.transform.position.z);
        }

        private void LogStep(int retries, Vector2 pose, float heading, bool holding, string tool,
            string argsJson, Dictionary<string, object> result, string framePath, bool collided)
        {
            LastTool = tool;
            string resultJson = MiniJson(result);
            string entry = "{\"step\":" + Step + ",\"timestamp\":\"" +
                           DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) +
                           "\",\"tool\":\"" + tool + "\",\"args\":" + argsJson + ",\"result\":" + resultJson +
                           ",\"frame_path\":" + (framePath == null ? "null" : "\"" + framePath + "\"") + "}";
            try
            {
                File.AppendAllText(logPath, entry + "\n");
            }
            catch (IOException)
            {
                // Logging never breaks the run.
            }

            engine.AddLogEntry(new LogEntry
            {
                Step = Step,
                State = engine.StateName,
                Tool = tool,
                Args = argsJson,
                Result = resultJson,
                Collided = collided,
                Note = lastNote,
                Retries = retries,
                Outcome = tool == "finish" ? outcome : "",
                FramePath = framePath == null ? "" : Path.Combine(runDirectory, framePath.Replace('/', Path.DirectorySeparatorChar)),
                X = pose.x,
                Y = pose.y,
                Heading = heading,
                Holding = holding,
                Timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            });
        }

        private string outcome = "";
        private string lastNote = "";

        private static string MiniJson(Dictionary<string, object> values)
        {
            if (values == null || values.Count == 0)
            {
                return "{}";
            }

            StringBuilder builder = new StringBuilder("{");
            bool first = true;
            foreach (KeyValuePair<string, object> pair in values)
            {
                if (!first)
                {
                    builder.Append(",");
                }

                first = false;
                builder.Append("\"").Append(pair.Key).Append("\":");
                if (pair.Value is bool)
                {
                    builder.Append((bool)pair.Value ? "true" : "false");
                }
                else if (pair.Value is float)
                {
                    builder.Append(((float)pair.Value).ToString("0.####", CultureInfo.InvariantCulture));
                }
                else if (pair.Value is int)
                {
                    builder.Append((int)pair.Value);
                }
                else
                {
                    builder.Append("\"").Append(pair.Value).Append("\"");
                }
            }

            builder.Append("}");
            return builder.ToString();
        }

        private static byte[] EncodePng(Color32[] pixels)
        {
            Texture2D texture = new Texture2D(RobotVision.FrameWidth, RobotVision.FrameHeight,
                TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.Destroy(texture);
            return png;
        }
    }
}
