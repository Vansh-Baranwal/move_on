using UnityEngine;
using RobotEngine.Core;
using RobotEngine.Robot;
using RobotEngine.Sim;
using RobotEngine.Views;
using RobotEngine.World;

namespace RobotEngine
{
    /// <summary>
    /// Builds the whole engine at play start: world, robot, cameras, recorder, state machine and
    /// the HTTP API. Self-bootstraps via RuntimeInitializeOnLoadMethod, so no scene setup is
    /// required - press Play in any scene and the engine comes up on localhost:8080.
    /// </summary>
    public sealed class SceneBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void BootstrapFromRuntime()
        {
            if (FindAnyObjectByType<SceneBootstrap>() != null)
            {
                return;
            }

            new GameObject("Engine").AddComponent<SceneBootstrap>();
        }

        private void Start()
        {
            Application.runInBackground = true;
            RemoveSceneCamerasAndLights();

            GameObject worldObject = new GameObject("World");
            worldObject.transform.SetParent(transform);
            WorldBuilder world = worldObject.AddComponent<WorldBuilder>();
            world.Build(0);

            GameObject robotObject = new GameObject("Robot");
            robotObject.transform.SetParent(transform);
            robotObject.transform.SetPositionAndRotation(
                CoordinateConverter.ApiToUnityPosition(-3.4f, -3.4f),
                Quaternion.Euler(0f, CoordinateConverter.ApiHeadingToUnityYaw(0f), 0f));
            RobotController robot = robotObject.AddComponent<RobotController>();
            robot.Build();

            GameObject cameraRigObject = new GameObject("Views");
            cameraRigObject.transform.SetParent(transform);
            AgentCamera agentCamera = cameraRigObject.AddComponent<AgentCamera>();
            agentCamera.Build(robotObject.transform);
            PovRecorder povRecorder = cameraRigObject.AddComponent<PovRecorder>();
            povRecorder.Build(robotObject.transform);
            ViewRig viewRig = cameraRigObject.AddComponent<ViewRig>();
            viewRig.Build(robotObject.transform, agentCamera);

            EngineCore engine = gameObject.AddComponent<EngineCore>();
            engine.Build(world, robot, agentCamera, povRecorder, viewRig);

            GameServer server = gameObject.AddComponent<GameServer>();
            server.Build(engine);

            HudDisplay hud = gameObject.AddComponent<HudDisplay>();
            hud.Build(engine, robot, viewRig, povRecorder);
        }

        private static void RemoveSceneCamerasAndLights()
        {
            Camera[] cameras = FindObjectsByType<Camera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                Destroy(cameras[i].gameObject);
            }

            Light[] lights = FindObjectsByType<Light>();
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i].type == LightType.Directional || lights[i].type == LightType.Point)
                {
                    Destroy(lights[i].gameObject);
                }
            }
        }
    }
}
