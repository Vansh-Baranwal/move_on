using System;
using System.IO;
using UnityEngine;
using RobotEngine.Robot;

namespace RobotEngine.Views
{
    /// <summary>
    /// Spec camera: JPEG 640x480, 90 deg horizontal FOV, 0.5 m above the floor, tilted 10 deg
    /// down. Also serves the single-ray depth reading. The display camera mirrors the view to
    /// the Game window for live demos; it is not part of the API.
    /// </summary>
    public sealed class AgentCamera : MonoBehaviour
    {
        private const int Width = 640;
        private const int Height = 480;
        private const float HorizontalFieldOfView = 90f;
        private const float AspectRatio = 4f / 3f;
        private const float MaxDepth = 10f;
        private const float NormalEyeHeight = 0.5f;
        private const float MinimumCarryingEyeHeight = 1.0f;
        private const float CarriedObjectClearance = 0.3f;
        private Camera agentCamera;
        private RenderTexture renderTexture;
        private Transform robotRoot;

        /// <summary>The live /look render texture - used for the TPP picture-in-picture.</summary>
        public RenderTexture AgentTexture
        {
            get { return renderTexture; }
        }

        /// <summary>Pose the /look camera renders from, for the FPP display mode.</summary>
        public void GetDisplayPose(out Vector3 position, out Quaternion rotation)
        {
            position = agentCamera.transform.position;
            rotation = agentCamera.transform.rotation;
        }

        /// <summary>CPU pixel readback of the current /look frame (for the internal brain).</summary>
        public Color32[] GetFramePixels()
        {
            RenderTexture previousTarget = RenderTexture.active;
            RenderTexture.active = renderTexture;
            Texture2D frame = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            frame.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
            frame.Apply();
            Color32[] pixels = frame.GetPixels32();
            RenderTexture.active = previousTarget;
            Destroy(frame);
            return pixels;
        }

        public void Build(Transform robot)
        {
            robotRoot = robot;
            transform.SetParent(robot);
            transform.localPosition = new Vector3(0f, NormalEyeHeight, 0.25f);
            transform.localRotation = Quaternion.Euler(10f, 0f, 0f);

            renderTexture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                name = "AgentCameraTexture",
                filterMode = FilterMode.Bilinear
            };
            renderTexture.Create();

            GameObject agentObject = new GameObject("AgentCamera");
            agentObject.transform.SetParent(transform, false);
            agentCamera = agentObject.AddComponent<Camera>();
            ConfigureCamera(agentCamera, renderTexture);

            GameObject displayObject = new GameObject("DisplayCamera");
            displayObject.transform.SetParent(transform, false);
            Camera displayCamera = displayObject.AddComponent<Camera>();
            ConfigureCamera(displayCamera, null);
            displayCamera.depth = 0f;
        }

        private void Update()
        {
            Vector3 localPosition = transform.localPosition;
            localPosition.y = GetEyeHeight(robotRoot);
            transform.localPosition = localPosition;

            if (Input.GetKeyDown(KeyCode.F9))
            {
                SaveDebugFrame();
            }
        }

        public static float GetEyeHeight(Transform robot)
        {
            return MinimumCarryingEyeHeight;
        }
        /// <summary>Current camera frame as JPEG (640x480), Claude's only view of objects.</summary>
        public byte[] CaptureJpeg()
        {
            RenderTexture previousTarget = RenderTexture.active;
            RenderTexture.active = renderTexture;
            Texture2D frame = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            frame.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
            frame.Apply();
            byte[] jpeg = frame.EncodeToJPG(85);
            RenderTexture.active = previousTarget;
            Destroy(frame);
            return jpeg;
        }

        /// <summary>
        /// Single-ray distance from the camera centre straight ahead, max 10 m, null if nothing
        /// in range. Does not identify what it hits. Robot-owned colliders are ignored.
        /// </summary>
        public float? CaptureDepth()
        {
            Transform cameraTransform = agentCamera.transform;
            RaycastHit[] hits = Physics.RaycastAll(cameraTransform.position, cameraTransform.forward,
                MaxDepth, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider.transform.IsChildOf(robotRoot))
                {
                    continue;
                }

                return Mathf.Min(hits[i].distance, MaxDepth);
            }

            return null;
        }

        /// <summary>Projects a camera pixel into the scene and returns its first non-robot hit.</summary>
        public bool TryGetWorldPointAt(float viewportX, float viewportY, out Vector3 point)
        {
            Collider hitCollider;
            return TryGetWorldPointAt(viewportX, viewportY, out point, out hitCollider);
        }

        public bool TryGetWorldPointAt(float viewportX, float viewportY, out Vector3 point,
            out Collider hitCollider)
        {
            point = Vector3.zero;
            hitCollider = null;
            Ray ray = agentCamera.ViewportPointToRay(new Vector3(
                Mathf.Clamp01(viewportX), Mathf.Clamp01(viewportY), 0f));
            RaycastHit[] hits = Physics.RaycastAll(ray, MaxDepth, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider.transform.IsChildOf(robotRoot))
                {
                    continue;
                }

                point = hits[i].point;
                hitCollider = hits[i].collider;
                return true;
            }

            return false;
        }
        private static void ConfigureCamera(Camera camera, RenderTexture targetTexture)
        {
            camera.targetTexture = targetTexture;
            // vertical = 2 * atan(tan(horizontal / 2) / aspect), giving 73.74 degrees at 4:3.
            camera.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(HorizontalFieldOfView * 0.5f * Mathf.Deg2Rad) /
                AspectRatio) * Mathf.Rad2Deg;
            camera.aspect = AspectRatio;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 30f;
            // Solid moody backdrop: the default sky is saturated blue, which the
            // vision layer would read as one giant blue blob.
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.13f, 0.11f, 0.18f);
        }

        private void SaveDebugFrame()
        {
            byte[] jpeg = CaptureJpeg();
            string debugFolder = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "debug");
            Directory.CreateDirectory(debugFolder);
            File.WriteAllBytes(Path.Combine(debugFolder, "step_debug.jpg"), jpeg);
            Debug.Log("Saved debug frame.");
        }
    }
}
