using System;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;
using RobotEngine.Sim;

namespace RobotEngine.Core
{
    /// <summary>
    /// Local HTTP API on localhost:8080. A listener thread accepts requests, hands them to the
    /// EngineCore main-thread queue, and blocks until the engine finishes executing the step -
    /// the scene advances only during /move, /turn and /drop, exactly as the spec requires.
    /// </summary>
    public sealed class GameServer : MonoBehaviour
    {
        private const string Prefix = "http://localhost:8080/";
        private const float RequestTimeoutSeconds = 120f;

        private EngineCore engine;
        private HttpListener listener;
        private Thread listenerThread;
        private volatile bool running;

        public void Build(EngineCore engineCore)
        {
            engine = engineCore;
            running = true;
            listener = new HttpListener();
            listener.Prefixes.Add(Prefix);
            // A just-killed previous instance can hold the port briefly; retry the bind.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 30)
                {
                    Thread.Sleep(1000);
                }
            }
            listenerThread = new Thread(ListenLoop) { IsBackground = true };
            listenerThread.Start();
            Debug.Log("Robot engine API listening on " + Prefix);
        }

        private void OnDestroy()
        {
            running = false;
            if (listener != null)
            {
                try
                {
                    listener.Stop();
                }
                catch (Exception)
                {
                    // Listener already gone; nothing to clean up.
                }
            }
        }

        private void ListenLoop()
        {
            while (running)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception)
                {
                    break;
                }

                try
                {
                    Handle(context);
                }
                catch (Exception ex)
                {
                    Debug.Log("API request failed: " + ex.Message);
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            string path = context.Request.Url.AbsolutePath;
            if (path == "/health")
            {
                WriteBytes(context, 200, "application/json",
                    Encoding.UTF8.GetBytes(ApiJson.Obj(ApiJson.Bool("ok", true))));
                return;
            }

            ApiRequest request = new ApiRequest
            {
                Method = context.Request.HttpMethod,
                Path = path,
                Query = context.Request.Url.Query,
                Body = ReadBody(context.Request),
                Done = new ManualResetEventSlim(false)
            };

            engine.Enqueue(request);
            if (!request.Done.Wait(TimeSpan.FromSeconds(RequestTimeoutSeconds)))
            {
                WriteBytes(context, 504, "application/json",
                    Encoding.UTF8.GetBytes(ApiJson.Obj(ApiJson.Str("error", "engine timeout"))));
                return;
            }

            WriteBytes(context, request.ResponseStatus, request.ResponseContentType, request.ResponseData);
        }

        private static string ReadBody(HttpListenerRequest httpRequest)
        {
            if (!httpRequest.HasEntityBody)
            {
                return "";
            }

            using (System.IO.Stream stream = httpRequest.InputStream)
            using (System.IO.StreamReader reader = new System.IO.StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteBytes(HttpListenerContext context, int status, string contentType, byte[] data)
        {
            HttpListenerResponse response = context.Response;
            response.StatusCode = status;
            response.ContentType = contentType;
            if (data == null)
            {
                data = Array.Empty<byte>();
            }

            response.ContentLength64 = data.Length;
            response.OutputStream.Write(data, 0, data.Length);
            response.OutputStream.Close();
        }
    }

    /// <summary>One API call in flight; the response fields are filled by the main thread.</summary>
    public sealed class ApiRequest
    {
        public string Method;
        public string Path;
        public string Query;
        public string Body;
        public ManualResetEventSlim Done;
        public int ResponseStatus = 200;
        public string ResponseContentType = "application/json";
        public byte[] ResponseData;
    }
}
