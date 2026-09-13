using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GPTUnity
{
    /// <summary>
    /// Central bridge state: server lifecycle, auth token, persisted settings and a
    /// small ring-buffer log that the EditorWindow renders. Survives domain reloads.
    /// </summary>
    public static class Bridge
    {
        public const string Version = "1.0.0";
        public const int DefaultPort = 8765;

        const string KeyAutoStart = "GPTUnity.AutoStart";
        const string KeyToken = "GPTUnity.Token";
        const string KeyPort = "GPTUnity.Port";

        static GPTUnityHttpServer server;
        static bool serverRunning;
        static int port = DefaultPort;
        static string token = "";

        public static bool ServerRunning
        {
            get => serverRunning;
            set => serverRunning = value;
        }

        public static int Port
        {
            get => port;
            set
            {
                port = value;
                EditorPrefs.SetInt(KeyPort, value);
            }
        }

        public static string Token => token;

        public static string BaseUrl => "http://127.0.0.1:" + port;

        public static event Action Logged;

        static readonly List<string> LogLines = new List<string>();

        public static IList<string> GetLogLines() => LogLines;

        public static void ClearLog()
        {
            LogLines.Clear();
            try { Logged?.Invoke(); }
            catch { }
        }

        public static void Log(object msg)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
            Debug.Log("[GPTUnity] " + msg);
            LogLines.Add(line);
            while (LogLines.Count > 500) LogLines.RemoveAt(0);
            try { Logged?.Invoke(); }
            catch { }
        }

        public static void Warn(object msg)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] WARN " + msg;
            Debug.LogWarning("[GPTUnity] " + msg);
            LogLines.Add(line);
            while (LogLines.Count > 500) LogLines.RemoveAt(0);
            try { Logged?.Invoke(); }
            catch { }
        }

        [InitializeOnLoadMethod]
        static void Init()
        {
            token = EditorPrefs.GetString(KeyToken, "");
            if (token.Length < 16) RegenerateToken();
            port = EditorPrefs.GetInt(KeyPort, DefaultPort);
            if (port <= 0 || port > 65535) port = DefaultPort;

            if (EditorPrefs.GetBool(KeyAutoStart, false))
                EditorApplication.delayCall += AutoStartAfterLoad;
        }

        static void AutoStartAfterLoad()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += AutoStartAfterLoad;
                return;
            }
            if (!serverRunning)
            {
                if (TryStartServer()) Log("Server auto-started on " + BaseUrl);
                else Warn("Auto-start failed. Open Window > GPTUnity Bridge.");
            }
        }

        public static bool TryStartServer()
        {
            if (serverRunning && server != null) return true;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += () =>
                {
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    {
                        EditorPrefs.SetBool(KeyAutoStart, true);
                    }
                    else if (!serverRunning)
                    {
                        if (TryStartServer()) Log("Server started on " + BaseUrl);
                        else Warn("Failed to start server, see Console.");
                    }
                };
                return true;
            }
            try
            {
                var srv = new GPTUnityHttpServer(port, token);
                srv.Start();
                server = srv;
                serverRunning = true;
                Log("Server started on " + BaseUrl);
                return true;
            }
            catch (Exception ex)
            {
                Warn("Server failed to start: " + ex.Message);
                return false;
            }
        }

        public static void StopServer()
        {
            if (server != null)
            {
                try { server.Stop(); }
                catch { }
                server = null;
            }
            serverRunning = false;
            Log("Server stopped");
        }

        public static void RegenerateToken()
        {
            token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            EditorPrefs.SetString(KeyToken, token);
            Log("Generated a new auth token.");
        }
    }
}