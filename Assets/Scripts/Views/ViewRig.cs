using UnityEngine;
using RobotEngine.Robot;

namespace RobotEngine.Views
{
    public enum ViewMode
    {
        FirstPerson,
        ThirdPerson,
    }

    /// <summary>
    /// The audience-facing display rig. FPP shows the robot's eye view (same pose and FOV the
    /// /look camera uses); TPP is a smooth chase camera from behind and above. Toggling blends
    /// between the two over ~0.6 s and never touches the agent camera or /look.
    /// Also owns the top-down orthographic minimap camera.
    /// </summary>
    public sealed class ViewRig : MonoBehaviour
    {
        private const float FppFieldOfView = 73.74f;
        private const float TppFieldOfView = 60f;
        private const float BlendSeconds = 0.6f;

        private Transform robot;
        private AgentCamera agentCamera;
        private Camera displayCamera;
        private Camera minimapCamera;
        private RenderTexture minimapTexture;
        private ViewMode mode = ViewMode.FirstPerson;
        private float blend;

        public ViewMode Mode
        {
            get { return mode; }
        }

        public float Blend
        {
            get { return blend; }
        }

        public bool MinimapEnabled { get; private set; }

        public RenderTexture MinimapTexture
        {
            get { return minimapTexture; }
        }

        public RenderTexture AgentTexture
        {
            get { return agentCamera != null ? agentCamera.AgentTexture : null; }
        }

        public void Build(Transform robotTransform, AgentCamera agent)
        {
            robot = robotTransform;
            agentCamera = agent;
            MinimapEnabled = true;

            GameObject displayObject = new GameObject("DisplayCamera");
            displayCamera = displayObject.AddComponent<Camera>();
            displayCamera.fieldOfView = FppFieldOfView;
            displayCamera.nearClipPlane = 0.03f;
            displayCamera.farClipPlane = 40f;
            displayCamera.clearFlags = CameraClearFlags.Skybox;
            displayCamera.depth = 0f;

            minimapTexture = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32)
            {
                name = "MinimapTexture"
            };
            minimapTexture.Create();
            GameObject minimapObject = new GameObject("MinimapCamera");
            minimapCamera = minimapObject.AddComponent<Camera>();
            minimapCamera.orthographic = true;
            minimapCamera.orthographicSize = 5.6f;
            minimapCamera.transform.position = new Vector3(0f, 14f, 0f);
            minimapCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            minimapCamera.clearFlags = CameraClearFlags.SolidColor;
            minimapCamera.backgroundColor = new Color(0.13f, 0.13f, 0.15f);
            minimapCamera.cullingMask = ~0;
            minimapCamera.targetTexture = minimapTexture;
        }

        public void ToggleView()
        {
            mode = mode == ViewMode.FirstPerson ? ViewMode.ThirdPerson : ViewMode.FirstPerson;
        }

        public void ToggleMinimap()
        {
            MinimapEnabled = !MinimapEnabled;
        }

        /// <summary>World (x, z) in API metres to minimap-texture UV [0..1].</summary>
        public static Vector2 WorldToMinimap(Vector2 apiPose)
        {
            // Ortho camera covers 11.2 m centred on the room.
            return new Vector2((apiPose.x + 5.6f) / 11.2f, 1f - (apiPose.y + 5.6f) / 11.2f);
        }

        private void LateUpdate()
        {
            float target = mode == ViewMode.ThirdPerson ? 1f : 0f;
            blend = Mathf.MoveTowards(blend, target, Time.deltaTime / BlendSeconds);
            float eased = Mathf.SmoothStep(0f, 1f, blend);

            Vector3 fppPosition;
            Quaternion fppRotation;
            agentCamera.GetDisplayPose(out fppPosition, out fppRotation);

            Vector3 tppPosition = robot.position - robot.forward * 2.2f + Vector3.up * 1.6f;
            Quaternion tppRotation = Quaternion.LookRotation(
                (robot.position + robot.forward * 0.6f + Vector3.up * 0.3f) - tppPosition, Vector3.up);

            displayCamera.transform.position = Vector3.Lerp(fppPosition, tppPosition, eased);
            displayCamera.transform.rotation = Quaternion.Slerp(fppRotation, tppRotation, eased);
            displayCamera.fieldOfView = Mathf.Lerp(FppFieldOfView, TppFieldOfView, eased);

            minimapCamera.enabled = MinimapEnabled;
        }
    }
}
