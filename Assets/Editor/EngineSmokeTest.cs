using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace RobotEngine.EditorTools
{
    /// <summary>
    /// Batch-mode smoke test: boots play mode, drives every endpoint over real HTTP and audits
    /// /state for object-data leakage. Run with:
    /// Unity.exe -batchmode -projectPath . -executeMethod RobotEngine.EditorTools.EngineSmokeTest.Run
    /// Writes smoke_test_result.txt next to Assets and exits 0 only when everything passes.
    /// </summary>
    public static class EngineSmokeTest
    {
        private const string BaseUrl = "http://localhost:8080";
        private static readonly StringBuilder Log = new StringBuilder();
        private static int failures;
        private static bool wasPlaying;
        private static bool testStarted;
        private static double playStart;
        private static int exitCode = -1;

        public static void Run()
        {
            EditorApplication.update += Update;
            EditorApplication.isPlaying = true;
        }

        private static void Update()
        {
            bool playing = EditorApplication.isPlaying;
            if (playing && !wasPlaying)
            {
                playStart = EditorApplication.timeSinceStartup;
            }

            wasPlaying = playing;
            if (playing && !testStarted && EditorApplication.timeSinceStartup - playStart > 4.0)
            {
                testStarted = true;
                Thread worker = new Thread(RunTests) { IsBackground = true };
                worker.Start();
            }

            if (exitCode >= 0)
            {
                EditorApplication.Exit(exitCode);
            }
        }

        private static void RunTests()
        {
            try
            {
                Execute();
            }
            catch (Exception ex)
            {
                Check("no unhandled exception", false, ex.ToString());
            }

            string resultPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "smoke_test_result.txt");
            File.WriteAllText(resultPath, Log.ToString());
            exitCode = failures == 0 ? 0 : 1;
        }

        private static void Execute()
        {
            WaitForEngine();

            string health = Get("/health");
            Check("health ok", health.Contains("\"ok\":true"), health);

            string state = Get("/state");
            Check("state idle", state.Contains("\"state\":\"Idle\""), state);
            Check("state starts at -3.4/-3.4", state.Contains("\"x\":-3.4") && state.Contains("\"y\":-3.4"), state);
            Check("state retries zero", state.Contains("\"retries\":0"), state);
            AuditLeakage("state", state);

            byte[] look = GetBytes("/look");
            Check("look is jpeg", look.Length > 1000 && look[0] == 0xFF && look[1] == 0xD8, look.Length + " bytes");

            string depth = Get("/depth");
            Check("depth field present", depth.Contains("\"depth\""), depth);
            float depthValue = ExtractNumber(depth, "depth");
            Check("depth reads floor ahead", depthValue > 2f && depthValue < 4f, depth);
            AuditLeakage("depth", depth);

            string move = Post("/move", "{\"distance\":20}");
            Check("long move hits wall", move.Contains("\"collided\":true"), move);
            Check("long move distance sane", ExtractNumber(move, "moved") > 5f && ExtractNumber(move, "moved") < 10f, move);

            string blocked = Get("/state");
            Check("state blocked after collision", blocked.Contains("\"state\":\"Blocked\""), blocked);
            Check("retries counted", blocked.Contains("\"retries\":1"), blocked);

            string turn = Post("/turn", "{\"degrees\":90}");
            Check("turn executed", ExtractNumber(turn, "turned") > 89f, turn);
            Check("heading updated", ExtractNumber(Get("/state"), "heading") > 89f, "");

            string reverse = Post("/move", "{\"distance\":-0.5}");
            Check("reverse move clean", reverse.Contains("\"collided\":false") &&
                Math.Abs(ExtractNumber(reverse, "moved") - 0.5f) < 0.05f, reverse);

            string pickup = Post("/pickup", "");
            Check("pickup fails far from objects", pickup.Contains("\"success\":false"), pickup);

            string drop = Post("/drop", "");
            Check("drop without holding", drop.Contains("\"in_zone\":false"), drop);

            string recordStart = Post("/record/start", "{\"command\":\"smoke test command\"}");
            Check("record starts", recordStart.Contains("\"ok\":true"), recordStart);

            string carry = Post("/move", "{\"distance\":1}");
            Check("carry move executes", carry.Contains("\"collided\""), carry);
            Thread.Sleep(500);

            string recordStop = Post("/record/stop", "");
            Check("record captured frames", ExtractNumber(recordStop, "frames") >= 1, recordStop);
            Check("frame file on disk", Directory.GetFiles(FindRunDirectory(), "frame_*.jpg").Length >= 1, "");

            string reset = Post("/reset", "");
            Check("reset ok", reset.Contains("\"ok\":true"), reset);
            string after = Get("/state");
            Check("reset restores retries", after.Contains("\"retries\":0"), after);
            Check("reset restores pose", after.Contains("\"x\":-3.4"), after);

            string seeded = Post("/reset", "{\"seed\":42}");
            Check("seeded reset ok", seeded.Contains("\"seed\":42"), seeded);
            string seededAgain = Post("/reset", "{\"seed\":42}");
            Check("seeded reset repeatable", seededAgain.Contains("\"seed\":42"), seededAgain);
            Post("/reset", "");

            string pushed = Post("/log", "{\"step\":1,\"state\":\"GoToObject\",\"tool\":\"move\",\"args\":\"{\\\"distance\\\":1}\",\"result\":\"{\\\"collided\\\":false}\",\"collided\":false,\"note\":\"SEE: test\",\"retries\":0,\"outcome\":\"\",\"frame_path\":\"\",\"x\":-3.4,\"y\":-3.4,\"heading\":0,\"holding\":false}");
            Check("log push ok", pushed.Contains("\"ok\":true"), pushed);
            string logs = Get("/logs");
            Check("logs stored", logs.Contains("\"count\":1") && logs.Contains("move"), logs);

            string replay = Post("/replay", "{\"active\":true,\"index\":0}");
            Check("replay scrubs", replay.Contains("\"ok\":true"), replay);
            string replayOff = Post("/replay", "{\"active\":false}");
            Check("replay exits", replayOff.Contains("\"ok\":true"), replayOff);

            // Voice command -> internal brain -> full fetch, then display-view captures.
            // Curated layout (seed 0) so the fetch is deterministic for verification.
            Post("/reset", "");
            string voice = Post("/voice", "{\"text\":\"Bring the blue cube.\",\"auto\":true}");
            Check("voice command starts brain", voice.Contains("\"ok\":true") && voice.Contains("\"started\":true"), voice);
            string brainStatus = "";
            bool brainFinished = false;
            for (int poll = 0; poll < 150; poll++)
            {
                Thread.Sleep(1500);
                brainStatus = Get("/run/status");
                if (brainStatus.Contains("\"active\":false"))
                {
                    brainFinished = true;
                    break;
                }
            }

            Check("internal brain finished", brainFinished, brainStatus);
            Check("internal brain passed", brainStatus.Contains("\"outcome\":\"pass\""), brainStatus);
            string brainLogs = Get("/logs");
            Check("brain pushed log entries", !brainLogs.Contains("\"count\":0"), brainLogs);

            Get("/view?mode=tpp");
            Thread.Sleep(1500);
            byte[] tppView = GetBytes("/view");
            Check("TPP display capture", tppView.Length > 5000 && tppView[0] == 0xFF && tppView[1] == 0xD8,
                tppView.Length + " bytes");
            Get("/view?mode=fpp");
            Thread.Sleep(1500);
            byte[] fppView = GetBytes("/view");
            Check("FPP display capture", fppView.Length > 5000 && fppView[0] == 0xFF && fppView[1] == 0xD8,
                fppView.Length + " bytes");

            Log.AppendLine("SMOKE TEST " + (failures == 0 ? "PASSED" : "FAILED") + " (" + failures + " failures)");
        }

        private static void WaitForEngine()
        {
            for (int attempt = 0; attempt < 240; attempt++)
            {
                try
                {
                    if (Get("/health").Contains("\"ok\":true"))
                    {
                        return;
                    }
                }
                catch (Exception)
                {
                    // Engine not up yet.
                }

                Thread.Sleep(1000);
            }

            Check("engine reachable", false, "timeout waiting for /health - check HttpListener binding");
        }

        private static void AuditLeakage(string endpoint, string payload)
        {
            string[] forbidden = { "obj_", "cube", "sphere", "cylinder", "cone", "red", "blue", "yellow", "green", "zone_pos", "target" };
            foreach (string word in forbidden)
            {
                Check("leak audit " + endpoint + " clean of '" + word + "'", !payload.ToLowerInvariant().Contains(word), "");
            }
        }

        private static string FindRunDirectory()
        {
            string runsRoot = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "runs");
            if (!Directory.Exists(runsRoot))
            {
                return runsRoot;
            }

            string[] runs = Directory.GetDirectories(runsRoot);
            Array.Sort(runs);
            for (int i = runs.Length - 1; i >= 0; i--)
            {
                if (!Path.GetFileName(runs[i]).StartsWith("platform"))
                {
                    return runs[i];
                }
            }

            return runsRoot;
        }

        private static float ExtractNumber(string json, string key)
        {
            int index = json.IndexOf("\"" + key + "\":", StringComparison.Ordinal);
            if (index < 0)
            {
                return float.MinValue;
            }

            int start = index + key.Length + 3;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '.'))
            {
                end++;
            }

            float.TryParse(json.Substring(start, end - start), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value);
            return value;
        }

        private static string Get(string path)
        {
            return Encoding.UTF8.GetString(GetBytes(path));
        }

        private static byte[] GetBytes(string path)
        {
            HttpWebRequest httpRequest = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
            httpRequest.Timeout = 90000;
            using (HttpWebResponse response = (HttpWebResponse)httpRequest.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static string Post(string path, string body)
        {
            HttpWebRequest httpRequest = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
            httpRequest.Method = "POST";
            httpRequest.Timeout = 90000;
            httpRequest.ContentType = "application/json";
            byte[] payload = Encoding.UTF8.GetBytes(body);
            httpRequest.ContentLength = payload.Length;
            using (Stream stream = httpRequest.GetRequestStream())
            {
                stream.Write(payload, 0, payload.Length);
            }

            using (HttpWebResponse response = (HttpWebResponse)httpRequest.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        private static void Check(string name, bool condition, string detail)
        {
            Log.AppendLine((condition ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  |  " + detail : ""));
            if (!condition)
            {
                failures++;
            }
        }
    }
}
