using UnityEngine;
using RobotEngine.Sim;

namespace RobotEngine.Sim
{
    /// <summary>
    /// On-screen demo HUD. Rendered by the editor/Game view only - the POV recorder captures from
    /// its own camera, so none of this text can ever appear in a Phase 2 clip.
    /// </summary>
    public sealed class HudDisplay : MonoBehaviour
    {
        private EngineCore engine;
        private RobotEngine.Robot.RobotController robot;

        public void Build(EngineCore engineCore, RobotEngine.Robot.RobotController robotController)
        {
            engine = engineCore;
            robot = robotController;
        }

        private void OnGUI()
        {
            if (engine == null)
            {
                return;
            }

            Vector2 pose = CoordinateConverter.UnityToApiPosition(robot.transform.position);
            float heading = CoordinateConverter.UnityYawToApiHeading(robot.transform.eulerAngles.y);
            string status = string.Format(
                "STATE {0}   x {1:F2}  y {2:F2}   heading {3:F0}   retries {4}   holding {5}",
                engine.StateName, pose.x, pose.y, heading, engine.RetryCount,
                robot.HeldObject != null ? "yes" : "no");
            GUI.Box(new Rect(8f, 8f, 640f, 54f), "");
            GUI.Label(new Rect(16f, 14f, 620f, 22f), status);
            GUI.Label(new Rect(16f, 36f, 620f, 22f), engine.IsRecording
                ? "REC  command: " + engine.ActiveCommand
                : "engine ready on http://localhost:8080  (F9 saves a debug /look frame)");
        }
    }
}
