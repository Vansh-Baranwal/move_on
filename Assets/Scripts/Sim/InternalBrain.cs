using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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

        private struct BlobMatch
        {
            public Blob Blob;
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
        private bool afterPivot;
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
        private readonly List<KeyValuePair<float, float>> measureQueue = new List<KeyValuePair<float, float>>();
        private readonly List<KeyValuePair<float, float>> measurements = new List<KeyValuePair<float, float>>();
        private float bestHeight = -1f;
        private float bestBearing;
        private float lastDepth = float.NaN;
        private bool attachImage = true;

        public int Step { get; private set; }
        public string LastTool { get; private set; } = "";
        public string Outcome { get; private set; } = "";
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
            measureQueue.Clear();
            measurements.Clear();
            bestHeight = -1f;
            attachImage = true;
            Cancelled = false;

            string lower = command.ToLowerInvariant();
            foreach (string candidate in new[] { "red", "blue", "yellow", "green" })
            {
                if (lower.Contains(candidate))
                {
                    targetColour = candidate;
                    break;
                }
            }

            if (lower.Contains("round") || lower.Contains("ball") || lower.Contains("sphere"))
            {
                targetShape = "round";
            }
            else if (lower.Contains("cube") || lower.Contains("box") || lower.Contains("square"))
            {
                targetShape = "boxy";
            }
            else if (lower.Contains("cylinder"))
            {
                targetShape = "cylinder";
            }
            else if (lower.Contains("cone"))
            {
                targetShape = "cone";
            }

            if (lower.Contains("tallest") || lower.Contains("biggest"))
            {
                mode = "tallest";
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

                if (retries >= 3)
                {
                    outcome = "fail_collision";
                    reason = "gave up after 3 collision retries";
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
                    prevRetries = retries;
                    if (pending == "pickup")
                    {
                        pending = null;
                        yield return engine.ExecuteOp("pickup", 0f, result =>
                        {
                            lastResult = result;
                            LogStep(retries, pose, heading, holding, "pickup", "{}", result, null, false);
                        });
                        if (lastResult.ContainsKey("success") && (bool)lastResult["success"])
                        {
                            phase = "return";
                        }

                        continue;
                    }

                    afterPivot = true;
                    LogStep(retries, pose, heading, holding, "turn", "{\"degrees\":70}",
                        new Dictionary<string, object>(), null, true);
                    yield return engine.ExecuteOp("turn", 70f * collisionTurnSign, result => { });
                    continue;
                }

                if (afterPivot)
                {
                    afterPivot = false;
                    yield return engine.ExecuteOp("move", 0.5f, result => { });
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
                yield return engine.ExecuteOp(tool, value, r => opResult = r);
                lastResult = opResult;
                bool moved = tool == "move" || tool == "turn";
                if (moved && opResult.ContainsKey("collided") && (bool)opResult["collided"])
                {
                    LogStep(retries + (engine.RetryCount > retries ? 1 : 0), CurrentPose(), CurrentHeading(),
                        robot.HeldObject != null, "collision", "{}",
                        new Dictionary<string, object> { { "collided", true } }, null, true);
                }

                attachImage = moved;
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

            if (phase == "measure")
            {
                Measure(s, blobs, ref note, out tool, out value);
                return;
            }

            if (phase == "scan")
            {
                Scan(s, blobs, ref note, out tool, out value);
                return;
            }

            Approach(s, blobs, ref note, out tool, out value);
        }

        private void Scan(BrainState s, List<Blob> blobs, ref string note, out string tool, out float value)
        {
            if (blobs != null)
            {
                Blob match = BestMatch(blobs, s.Heading);
                bool sweepDone = scanTurned >= 340f;
                if (match != null && (mode != "tallest" || sweepDone))
                {
                    targetBearing = BearingOf(s.Heading, match.Cx);
                    phase = "approach";
                    float offset = OffsetDegrees(match.Cx);
                    note += " | target spotted, bearing " + targetBearing.ToString("F0", CultureInfo.InvariantCulture);
                    tool = "turn";
                    value = Mathf.Round(offset * 10f) / 10f;
                    return;
                }

                if (mode == "tallest")
                {
                    foreach (Blob blob in blobs)
                    {
                        if (blob.Colour == "magenta")
                        {
                            continue;
                        }

                        float bearing = BearingOf(s.Heading, blob.Cx);
                        bool seen = false;
                        foreach (KeyValuePair<float, float> entry in measureQueue)
                        {
                            if (Mathf.Abs(Mathf.Repeat(bearing - entry.Key + 540f, 360f) - 180f) < 25f)
                            {
                                seen = true;
                                break;
                            }
                        }

                        if (!seen)
                        {
                            measureQueue.Add(new KeyValuePair<float, float>(bearing, blob.Area));
                        }
                    }
                }

                if (sweepDone && mode == "tallest" && measureQueue.Count > 0)
                {
                    phase = "measure";
                    GoMeasure(s, ref note, out tool, out value);
                    return;
                }

                if (scanTurned >= 360f)
                {
                    if (mode == "tallest" && measureQueue.Count > 0)
                    {
                        phase = "measure";
                        GoMeasure(s, ref note, out tool, out value);
                        return;
                    }

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
                    note = "target never appeared in any scan frame";
                    return;
                }

                scanTurned += 60f;
                tool = "turn";
                value = 60f;
                return;
            }

            tool = "turn";
            value = 60f;
            note = "scan continues";
        }

        private void GoMeasure(BrainState s, ref string note, out string tool, out float value)
        {
            if (measureQueue.Count == 0)
            {
                tool = "finish";
                value = 0f;
                note = "nothing to measure";
                return;
            }

            targetBearing = measureQueue[0].Key;
            float delta = Mathf.Repeat(targetBearing - s.Heading + 540f, 360f) - 180f;
            note = "measuring candidate height";
            tool = "turn";
            value = Mathf.Round(delta * 10f) / 10f;
        }

        private void Measure(BrainState s, List<Blob> blobs, ref string note, out string tool, out float value)
        {
            if (!float.IsNaN(lastDepth) && lastDepth < 2.8f && blobs != null)
            {
                Blob tallest = null;
                foreach (Blob blob in blobs)
                {
                    if (blob.Colour == "magenta")
                    {
                        continue;
                    }

                    if (tallest == null || blob.H > tallest.H)
                    {
                        tallest = blob;
                    }
                }

                if (tallest != null)
                {
                    float estimate = PhysicalHeight(tallest.H, lastDepth);
                    measurements.Add(new KeyValuePair<float, float>(targetBearing, estimate));
                    if (estimate > bestHeight)
                    {
                        bestHeight = estimate;
                        bestBearing = targetBearing;
                    }
                }
            }

            if (measureQueue.Count > 0)
            {
                measureQueue.RemoveAt(0);
            }

            if (measureQueue.Count > 0)
            {
                GoMeasure(s, ref note, out tool, out value);
                return;
            }

            if (bestBearing < 0f && bestHeight < 0f)
            {
                tool = "finish";
                value = 0f;
                note = "no measurable object found";
                return;
            }

            phase = "approach";
            targetBearing = bestBearing;
            float delta = Mathf.Repeat(targetBearing - s.Heading + 540f, 360f) - 180f;
            note = "tallest candidate selected (" + bestHeight.ToString("F2", CultureInfo.InvariantCulture) + " m est.)";
            tool = "turn";
            value = Mathf.Round(delta * 10f) / 10f;
        }

        private void Approach(BrainState s, List<Blob> blobs, ref string note, out string tool, out float value)
        {
            Blob blob = blobs != null ? BestMatch(blobs, s.Heading) : null;
            if (blob == null)
            {
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

                float delta = Mathf.Repeat(targetBearing - s.Heading + 540f, 360f) - 180f;
                if (Mathf.Abs(delta) > 15f)
                {
                    tool = "turn";
                    value = Mathf.Round(delta * 10f) / 10f;
                    note += " | reacquiring target";
                    return;
                }

                tool = "move";
                value = 0.5f;
                note += " | driving on memory";
                return;
            }

            float offset = OffsetDegrees(blob.Cx);
            float height = blob.H;
            if (s.Retries > 0)
            {
                offset += 30f * collisionTurnSign;
            }

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

            if (Mathf.Abs(offset) > 14f)
            {
                tool = "turn";
                value = Mathf.Round(offset * 10f) / 10f;
                note += " | centring target";
                return;
            }

            // 0.7 m camera depth == ~0.45 m face distance == within reach once closed.
            if (!float.IsNaN(lastDepth) && lastDepth <= 0.7f)
            {
                pending = "pickup";
                tool = "move";
                value = 0.2f;
                note += " | in pickup range, closing";
                return;
            }

            if (float.IsNaN(lastDepth) && height >= 200f)
            {
                pending = "pickup";
                tool = "move";
                value = 0.2f;
                note += " | filling the frame, closing";
                return;
            }

            tool = "move";
            value = 0.8f;
            note += " | approaching";
        }

        private void Deliver(BrainState s, List<Blob> blobs, ref string note, out string tool, out float value)
        {
            Blob zone = null;
            if (blobs != null)
            {
                foreach (Blob blob in blobs)
                {
                    if (blob.Colour == "magenta")
                    {
                        zone = blob;
                        break;
                    }
                }

                if (zone != null)
                {
                    zoneBearing = BearingOf(s.Heading, zone.Cx);
                }
            }

            if (zone != null)
            {
                float offset = OffsetDegrees(zone.Cx);
                if (Mathf.Abs(offset) > 14f)
                {
                    tool = "turn";
                    value = Mathf.Round(offset * 10f) / 10f;
                    note = "centring drop zone";
                    return;
                }

                float zoneDistance = 1.25f * 320f / Mathf.Max(1f, zone.W);
                if (zone.W >= 420 || zoneDistance <= 0.85f)
                {
                    pending = "drop";
                    tool = "move";
                    value = 0.25f;
                    note = "over the zone, dropping next";
                    return;
                }

                tool = "move";
                value = Mathf.Clamp(zoneDistance - 0.8f, 0.3f, 0.7f);
                note = "carrying to zone";
                return;
            }

            if (zoneBearing >= 0f)
            {
                float delta = Mathf.Repeat(zoneBearing - s.Heading + 540f, 360f) - 180f;
                if (Mathf.Abs(delta) > 15f)
                {
                    tool = "turn";
                    value = Mathf.Round(delta * 10f) / 10f;
                    note = "turning toward remembered zone";
                    return;
                }

                tool = "move";
                value = 0.8f;
                note = "driving toward remembered zone";
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
            if (blob.Colour == "magenta")
            {
                return false;
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
                if (blob.Colour == "magenta")
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
