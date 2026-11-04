using System.IO;
using UnityEditor;
using UnityEngine;

namespace RobotEngine.EditorTools
{
    /// <summary>
    /// Batch-mode helper that boots the engine and keeps play mode running until a stop file
    /// appears next to Assets (or 5 minutes pass). Used to serve the API while the Python agent
    /// is tested from the command line:
    /// Unity.exe -batchmode -projectPath . -executeMethod RobotEngine.EditorTools.EngineServe.Run
    /// </summary>
    public static class EngineServe
    {
        private static bool wasPlaying;
        private static double playStart;

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
            if (!playing)
            {
                return;
            }

            string stopFile = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "serve_stop");
            bool timedOut = EditorApplication.timeSinceStartup - playStart > 300.0;
            if (File.Exists(stopFile) || timedOut)
            {
                if (File.Exists(stopFile))
                {
                    File.Delete(stopFile);
                }

                EditorApplication.Exit(0);
            }
        }
    }
}
