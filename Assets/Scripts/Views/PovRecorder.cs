using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using UnityEngine;

namespace RobotEngine.Views
{
    /// <summary>
    /// Records the robot's point of view for Phase 2 clips: 1280x720 frames captured at 20 fps
    /// while the scene advances (moves, turns, drop settle), written to disk on a worker thread.
    /// tools/make_clip.py assembles the frames into the final MP4.
    /// </summary>
    public sealed class PovRecorder : MonoBehaviour
    {
        private const int Width = 1280;
        private const int Height = 720;
        private const float CaptureInterval = 1f / 20f;
        private const float HorizontalFieldOfView = 90f;
        private const float AspectRatio = 16f / 9f;

        private struct FramePayload
        {
            public int Index;
            public byte[] Jpeg;
        }

        private Camera povCamera;
        private RenderTexture renderTexture;
        private readonly ConcurrentQueue<FramePayload> writeQueue = new ConcurrentQueue<FramePayload>();
        private Thread writerThread;
        private volatile bool writerRunning;
        private bool sessionActive;
        private bool motionActive;
        private float nextCapture;
        private string sessionDirectory;
        private int frameCount;
        private float motionSeconds;

        public bool SessionActive
        {
            get { return sessionActive; }
        }

        /// <summary>Seconds of captured motion - drives the >20 s clip warning.</summary>
        public float MotionSeconds
        {
            get { return motionSeconds; }
        }

        public void Build(Transform robot)
        {
            transform.SetParent(robot);
            transform.localPosition = new Vector3(0f, 0.5f, 0.25f);
            transform.localRotation = Quaternion.Euler(10f, 0f, 0f);

            renderTexture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                name = "PovRecorderTexture",
                filterMode = FilterMode.Bilinear
            };
            renderTexture.Create();

            GameObject cameraObject = new GameObject("PovCamera");
            cameraObject.transform.SetParent(transform, false);
            povCamera = cameraObject.AddComponent<Camera>();
            povCamera.targetTexture = renderTexture;
            // Same framing as the agent camera so the clip shows what Claude saw.
            povCamera.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(HorizontalFieldOfView * 0.5f * Mathf.Deg2Rad) /
                AspectRatio) * Mathf.Rad2Deg;
            povCamera.aspect = AspectRatio;
            povCamera.nearClipPlane = 0.03f;
            povCamera.farClipPlane = 30f;
            povCamera.clearFlags = CameraClearFlags.SolidColor;
            povCamera.backgroundColor = new Color(0.13f, 0.11f, 0.18f);
            povCamera.enabled = false;
        }

        public void StartSession(string directory)
        {
            if (sessionActive)
            {
                StopSession();
            }

            sessionDirectory = directory;
            Directory.CreateDirectory(sessionDirectory);
            frameCount = 0;
            motionSeconds = 0f;
            writeQueue.Clear();
            writerRunning = true;
            writerThread = new Thread(WriterLoop) { IsBackground = true };
            writerThread.Start();
            povCamera.enabled = true;
            sessionActive = true;
            motionActive = false;
            nextCapture = 0f;
        }

        public void SetMotion(bool active)
        {
            motionActive = active;
            if (active)
            {
                nextCapture = 0f;
            }
        }

        public int StopSession()
        {
            if (!sessionActive)
            {
                return 0;
            }

            sessionActive = false;
            motionActive = false;
            povCamera.enabled = false;
            writerRunning = false;
            if (writerThread != null)
            {
                writerThread.Join(3000);
                writerThread = null;
            }

            while (writeQueue.TryDequeue(out FramePayload remaining))
            {
                WriteFrame(remaining);
            }

            return frameCount;
        }

        private void Update()
        {
            if (!sessionActive || !motionActive || Time.time < nextCapture)
            {
                return;
            }

            nextCapture = Time.time + CaptureInterval;
            motionSeconds += CaptureInterval;
            CaptureFrame();
        }

        private void CaptureFrame()
        {
            RenderTexture previousTarget = RenderTexture.active;
            RenderTexture.active = renderTexture;
            Texture2D frame = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            frame.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
            frame.Apply();
            byte[] jpeg = frame.EncodeToJPG(85);
            RenderTexture.active = previousTarget;
            Destroy(frame);

            frameCount += 1;
            writeQueue.Enqueue(new FramePayload { Index = frameCount, Jpeg = jpeg });
        }

        private void WriterLoop()
        {
            while (writerRunning)
            {
                if (writeQueue.TryDequeue(out FramePayload payload))
                {
                    WriteFrame(payload);
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }

        private void WriteFrame(FramePayload payload)
        {
            string path = Path.Combine(sessionDirectory, "frame_" + payload.Index.ToString("00000") + ".jpg");
            File.WriteAllBytes(path, payload.Jpeg);
        }

        private void OnDestroy()
        {
            StopSession();
        }
    }
}
