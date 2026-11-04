using System.Collections.Generic;
using System.IO;
using UnityEngine;
using RobotEngine.Views;
using RobotEngine.World;

namespace RobotEngine.Sim
{
    /// <summary>
    /// Demo UI: live log panel (colour-coded, collapsible, click a row to see that step's
    /// frame), FPP/TPP view toggle with PiP, top-down mini-map with the path trail, replay
    /// scrubber, and the recording mode that hides all of it. Rendered on the display only -
    /// the POV recorder captures from its own camera, so none of this can reach a Phase 2 clip.
    /// </summary>
    public sealed class HudDisplay : MonoBehaviour
    {
        private const int PanelWidth = 470;

        private EngineCore engine;
        private RobotEngine.Robot.RobotController robot;
        private ViewRig viewRig;
        private PovRecorder povRecorder;

        private readonly Dictionary<string, Texture2D> frameCache = new Dictionary<string, Texture2D>();
        private readonly Dictionary<Color, Texture2D> colourCache = new Dictionary<Color, Texture2D>();
        private GUIStyle rowStyle;
        private GUIStyle headerStyle;

        private bool logOpen = true;
        private Vector2 logScroll;
        private int selectedEntry = -1;
        private string commandInput = "";
        private string lastVoiceSeen = "";
        private bool autoRunVoice = true;

        public void Build(EngineCore engineCore, RobotEngine.Robot.RobotController robotController,
            ViewRig view, PovRecorder recorder)
        {
            engine = engineCore;
            robot = robotController;
            viewRig = view;
            povRecorder = recorder;
        }

        private void Update()
        {
            if (engine == null)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.V))
            {
                viewRig.ToggleView();
            }

            if (Input.GetKeyDown(KeyCode.M))
            {
                viewRig.ToggleMinimap();
            }

            if (Input.GetKeyDown(KeyCode.L))
            {
                logOpen = !logOpen;
            }

            if (Input.GetKeyDown(KeyCode.R))
            {
                ToggleReplay();
            }
        }

        private void ToggleReplay()
        {
            if (engine.ReplayActive)
            {
                engine.ExitReplay();
                selectedEntry = -1;
            }
            else
            {
                engine.SetReplay(engine.Logs.Count - 1);
            }
        }

        private void OnGUI()
        {
            if (engine == null || viewRig == null)
            {
                return;
            }

            EnsureStyles();

            if (engine.IsRecording)
            {
                DrawRecordingOverlay();
                return;
            }

            DrawStatusBar();
            DrawCommandBox();
            DrawButtons();
            DrawPip();
            DrawMinimap();
            DrawLogPanel();
            DrawReplayPanel();
        }

        private void DrawRecordingOverlay()
        {
            float elapsed = engine.RecordingElapsedSeconds;
            float motion = povRecorder != null ? povRecorder.MotionSeconds : 0f;

            GUI.Box(new Rect(8f, 8f, 300f, 34f), "");
            GUI.DrawTexture(new Rect(16f, 18f, 14f, 14f), Solid(new Color(0.9f, 0.1f, 0.1f)));
            GUI.Label(new Rect(38f, 14f, 260f, 22f),
                string.Format("REC  {0:0.0}s motion (clip cap 20s)", motion));

            // The typed command stays on screen for the first two seconds only.
            if (elapsed <= 2f)
            {
                Vector2 size = GUI.skin.box.CalcSize(new GUIContent(engine.ActiveCommand));
                Rect rect = new Rect((Screen.width - size.x) / 2f, 40f, size.x + 24f, 34f);
                GUI.Box(rect, engine.ActiveCommand);
            }

            if (motion > 18f)
            {
                GUI.color = new Color(1f, 0.35f, 0.2f);
                GUI.Label(new Rect((Screen.width - 420f) / 2f, Screen.height - 60f, 420f, 24f),
                    string.Format("WARNING: motion is {0:0.0}s - the clip will exceed the 20s limit", motion),
                    headerStyle);
                GUI.color = Color.white;
            }
        }

        private void DrawStatusBar()
        {
            Vector2 pose = RobotEngine.Sim.CoordinateConverter.UnityToApiPosition(robot.transform.position);
            float heading = RobotEngine.Sim.CoordinateConverter.UnityYawToApiHeading(robot.transform.eulerAngles.y);
            string status = string.Format(
                "STATE {0}   x {1:F2}  y {2:F2}   heading {3:F0}   retries {4}   holding {5}   view {6}",
                engine.StateName, pose.x, pose.y, heading, engine.RetryCount,
                robot.HeldObject != null ? "yes" : "no",
                viewRig.Mode == ViewMode.FirstPerson ? "FPP" : "TPP");
            GUI.Box(new Rect(8f, 8f, PanelWidth + 60f, 34f), "");
            GUI.Label(new Rect(16f, 14f, PanelWidth + 44f, 22f), status);

            if (engine.BrainActive && engine.Brain != null)
            {
                GUI.Label(new Rect(16f, 40f, PanelWidth + 44f, 22f),
                    string.Format("BRAIN step {0}: {1}   [STOP]", engine.Brain.Step, engine.Brain.LastTool));
                if (GUI.Button(new Rect(PanelWidth + 30f, 38f, 60f, 22f), "STOP"))
                {
                    engine.StopBrain();
                }
            }
        }

        private void DrawCommandBox()
        {
            float width = 560f;
            Rect area = new Rect((Screen.width - width) / 2f, 50f, width, 46f);
            GUI.Box(area, "");

            // Voice text from the speech sidecar lands in the box automatically.
            if (!string.IsNullOrEmpty(engine.VoiceText) && engine.VoiceText != lastVoiceSeen)
            {
                lastVoiceSeen = engine.VoiceText;
                commandInput = engine.VoiceText;
            }

            commandInput = GUI.TextField(new Rect(area.x + 8f, area.y + 9f, width - 220f, 28f), commandInput);
            Event keyed = Event.current;
            bool pressedEnter = keyed.isKey && keyed.keyCode == KeyCode.Return &&
                                GUI.GetNameOfFocusedControl() == "command";
            GUI.SetNextControlName("command");

            if (GUI.Button(new Rect(area.x + width - 206f, area.y + 9f, 60f, 28f), "Run") ||
                (pressedEnter && commandInput.Length > 0))
            {
                if (engine.StartRun(commandInput))
                {
                    commandInput = "";
                }
            }

            autoRunVoice = GUI.Toggle(new Rect(area.x + width - 140f, area.y + 11f, 132f, 26f),
                autoRunVoice, "Auto-run voice");
            engine.AutoRunVoice = autoRunVoice;
        }

        private void DrawButtons()
        {
            Rect area = new Rect(Screen.width - 240f, 8f, 232f, 34f);
            GUI.Box(area, "");
            if (GUI.Button(new Rect(area.x + 8f, area.y + 5f, 74f, 24f),
                viewRig.Mode == ViewMode.FirstPerson ? "FPP (V)" : "TPP (V)"))
            {
                viewRig.ToggleView();
            }

            if (GUI.Button(new Rect(area.x + 88f, area.y + 5f, 66f, 24f),
                viewRig.MinimapEnabled ? "Map (M)" : "Map off"))
            {
                viewRig.ToggleMinimap();
            }

            if (GUI.Button(new Rect(area.x + 160f, area.y + 5f, 66f, 24f),
                engine.ReplayActive ? "Replay" : "Replay (R)"))
            {
                ToggleReplay();
            }
        }

        private void DrawPip()
        {
            RenderTexture agentTexture = viewRig.AgentTexture;
            if (agentTexture == null || viewRig.Mode != ViewMode.ThirdPerson)
            {
                return;
            }

            Rect rect = new Rect(Screen.width - 254f, 50f, 246f, 184f);
            GUI.Box(new Rect(rect.x - 4f, rect.y - 4f, rect.width + 8f, rect.height + 26f),
                "Agent view (/look)");
            GUI.DrawTexture(rect, agentTexture, ScaleMode.ScaleToFit);
        }

        private void DrawMinimap()
        {
            if (!viewRig.MinimapEnabled || viewRig.MinimapTexture == null)
            {
                return;
            }

            const float size = 210f;
            Rect rect = new Rect(Screen.width - size - 14f, Screen.height - size - 14f, size, size);
            GUI.Box(new Rect(rect.x - 4f, rect.y - 4f, size + 8f, size + 8f), "Mini-map");
            GUI.DrawTexture(rect, viewRig.MinimapTexture, ScaleMode.StretchToFill);

            // Path trail.
            Color trailColour = new Color(0.25f, 0.75f, 1f);
            foreach (Vector2 point in engine.Trail)
            {
                Vector2 uv = ViewRig.WorldToMinimap(point);
                GUI.DrawTexture(new Rect(rect.x + uv.x * size - 2f, rect.y + uv.y * size - 2f, 4f, 4f),
                    Solid(trailColour));
            }

            // Drop zone marker (engine UI only - never exposed to the agent).
            Vector3 zone = SceneRegistry.DropZoneCenter;
            Vector2 zoneUv = ViewRig.WorldToMinimap(new Vector2(zone.x, zone.z));
            float zonePx = SceneRegistry.DropZoneHalfSize * 2f / 11.2f * size;
            GUI.DrawTexture(new Rect(rect.x + zoneUv.x * size - zonePx / 2f, rect.y + zoneUv.y * size - zonePx / 2f,
                zonePx, zonePx), Solid(new Color(1f, 0f, 0.8f, 0.35f)));

            // Robot dot.
            Vector2 robotPose = RobotEngine.Sim.CoordinateConverter.UnityToApiPosition(robot.transform.position);
            Vector2 robotUv = ViewRig.WorldToMinimap(robotPose);
            GUI.DrawTexture(new Rect(rect.x + robotUv.x * size - 4f, rect.y + robotUv.y * size - 4f, 8f, 8f),
                Solid(Color.white));
        }

        private void DrawLogPanel()
        {
            float panelHeight = logOpen ? 260f : 30f;
            Rect area = new Rect(8f, Screen.height - panelHeight - 10f, PanelWidth, panelHeight);
            GUI.Box(area, "");

            Rect header = new Rect(area.x + 6f, area.y + 4f, area.width - 12f, 22f);
            string headerText = string.Format("LOG  [{0}]  {1} steps  (L collapse, click a row for its frame)",
                logOpen ? "-" : "+", engine.Logs.Count);
            if (GUI.Button(header, headerText, headerStyle))
            {
                logOpen = !logOpen;
            }

            if (!logOpen)
            {
                DrawSelectedFrame(new Rect(area.x, area.y - 230f, 306f, 226f));
                return;
            }

            float listTop = area.y + 30f;
            float listHeight = area.height - 38f;
            Rect scrollRect = new Rect(area.x + 6f, listTop, area.width - 12f, listHeight);
            logScroll = GUILayout.BeginScrollView(logScroll, GUI.skin.box,
                GUILayout.Width(scrollRect.width), GUILayout.Height(listHeight));
            {
                IReadOnlyList<LogEntry> entries = engine.Logs;
                for (int i = 0; i < entries.Count; i++)
                {
                    LogEntry entry = entries[i];
                    GUIStyle style = StyleFor(entry);
                    string line = string.Format("#{0} {1} {2} -> {3}",
                        entry.Step, entry.Tool, entry.Args, Shorten(entry.Result, 46));
                    if (!string.IsNullOrEmpty(entry.Note) && entry.Note.StartsWith("SEE"))
                    {
                        line += "  " + Shorten(entry.Note, 52);
                    }

                    if (GUILayout.Button(line, style))
                    {
                        selectedEntry = i;
                    }
                }
            }
            GUILayout.EndScrollView();

            DrawSelectedFrame(new Rect(area.x + PanelWidth + 14f, Screen.height - 240f, 306f, 226f));
        }

        private void DrawReplayPanel()
        {
            if (!engine.ReplayActive || engine.Logs.Count == 0)
            {
                return;
            }

            Rect area = new Rect(Screen.width / 2f - 230f, Screen.height - 120f, 460f, 104f);
            GUI.Box(area, "REPLAY  (R to exit)");
            int next = Mathf.RoundToInt(GUI.HorizontalSlider(
                new Rect(area.x + 14f, area.y + 30f, area.width - 28f, 22f),
                engine.ReplayIndex, 0, engine.Logs.Count - 1));
            if (next != engine.ReplayIndex && engine.SetReplay(next))
            {
                selectedEntry = next;
            }

            LogEntry entry = engine.ReplayEntry();
            if (entry != null)
            {
                GUI.Label(new Rect(area.x + 14f, area.y + 52f, area.width - 28f, 40f),
                    string.Format("#{0} {1} {2}\n{3}", entry.Step, entry.Tool, Shorten(entry.Args, 40),
                        Shorten(entry.Result, 60)));
            }
        }

        private void DrawSelectedFrame(Rect rect)
        {
            if (selectedEntry < 0 || selectedEntry >= engine.Logs.Count)
            {
                return;
            }

            LogEntry entry = engine.Logs[selectedEntry];
            GUI.Box(rect, "");
            Texture2D frame = LoadFrame(entry.FramePath);
            if (frame != null)
            {
                GUI.DrawTexture(new Rect(rect.x + 4f, rect.y + 4f, rect.width - 8f, rect.height - 8f),
                    frame, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Label(new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, 40f),
                    string.Format("#{0} {1}\nframe: {2}", entry.Step, entry.Tool,
                        string.IsNullOrEmpty(entry.FramePath) ? "(none this step)" : entry.FramePath));
            }
        }

        private GUIStyle StyleFor(LogEntry entry)
        {
            Color background;
            if (entry.Collided || entry.State == "Blocked")
            {
                background = new Color(0.55f, 0.12f, 0.1f);   // collision: red
            }
            else if (entry.Result.Contains("\"in_zone\":true") || entry.Result.Contains("\"success\":true") ||
                     (entry.Tool == "finish" && entry.Outcome == "pass"))
            {
                background = new Color(0.1f, 0.42f, 0.16f);   // success: green
            }
            else if (entry.Tool == "finish")
            {
                background = new Color(0.55f, 0.4f, 0.08f);   // failed outcome: amber
            }
            else
            {
                background = new Color(0.16f, 0.16f, 0.18f);
            }

            GUIStyle style = new GUIStyle(GUI.skin.button);
            style.alignment = TextAnchor.MiddleLeft;
            style.normal.textColor = Color.white;
            style.hover.textColor = Color.white;
            style.active.textColor = Color.white;
            style.normal.background = Solid(background);
            style.hover.background = Solid(Color.Lerp(background, Color.white, 0.18f));
            style.active.background = Solid(Color.Lerp(background, Color.black, 0.2f));
            style.fontSize = 11;
            style.fixedHeight = 20f;
            return style;
        }

        private void EnsureStyles()
        {
            if (headerStyle == null)
            {
                headerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    alignment = TextAnchor.MiddleLeft
                };
                headerStyle.normal.textColor = Color.white;
            }
        }

        private Texture2D Solid(Color color)
        {
            Texture2D texture;
            if (!colourCache.TryGetValue(color, out texture))
            {
                texture = new Texture2D(2, 2);
                Color[] pixels = new Color[4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = color;
                }

                texture.SetPixels(pixels);
                texture.Apply();
                colourCache[color] = texture;
            }

            return texture;
        }

        private Texture2D LoadFrame(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            Texture2D texture;
            if (frameCache.TryGetValue(path, out texture))
            {
                return texture;
            }

            try
            {
                string fullPath = Path.IsPathRooted(path)
                    ? path
                    : Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, path));
                if (!File.Exists(fullPath))
                {
                    frameCache[path] = null;
                    return null;
                }

                texture = new Texture2D(2, 2);
                texture.LoadImage(File.ReadAllBytes(fullPath));
                frameCache[path] = texture;
                return texture;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static string Shorten(string text, int length)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            text = text.Replace("\n", " ");
            return text.Length <= length ? text : text.Substring(0, length - 3) + "...";
        }
    }
}
