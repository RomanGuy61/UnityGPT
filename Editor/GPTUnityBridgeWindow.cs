using System;
using UnityEditor;
using UnityEngine;

namespace GPTUnity
{
    public class GPTUnityBridgeWindow : EditorWindow
    {
        string tokenField = "";
        int portField = Bridge.DefaultPort;
        bool autoStart;
        string ping = "";
        Vector2 scroll;

        [MenuItem("Window/GPTUnity Bridge")]
        public static void Open()
        {
            var win = GetWindow<GPTUnityBridgeWindow>("GPTUnity Bridge");
            win.minSize = new Vector2(420, 520);
        }

        void OnEnable()
        {
            tokenField = Bridge.Token;
            portField = Bridge.Port;
            autoStart = EditorPrefs.GetBool("GPTUnity.AutoStart", false);
            Bridge.Logged += OnLogged;
        }

        void OnDisable()
        {
            Bridge.Logged -= OnLogged;
        }

        void OnLogged()
        {
            if (this != null) Repaint();
        }

        void OnGUI()
        {
            GUILayout.Label("GPTUnity Bridge", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Exposes your Unity Editor as a local HTTP API so a custom ChatGPT GPT "
                + "can inspect and edit your project. Everything binds to 127.0.0.1.",
                MessageType.Info);

            EditorGUI.BeginDisabledGroup(Bridge.ServerRunning);
            portField = EditorGUILayout.IntField("Port", portField);
            if (portField < 1 || portField > 65535) portField = Bridge.DefaultPort;
            EditorGUI.EndDisabledGroup();

            tokenField = EditorGUILayout.PasswordField("Auth token", tokenField);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy token"))
            {
                EditorGUIUtility.systemCopyBuffer = Bridge.Token;
                Bridge.Log("Token copied to clipboard.");
            }
            if (GUILayout.Button("Regenerate"))
            {
                Bridge.RegenerateToken();
                tokenField = Bridge.Token;
            }
            GUILayout.EndHorizontal();

            autoStart = EditorGUILayout.Toggle("Auto-start on load", autoStart);
            EditorPrefs.SetBool("GPTUnity.AutoStart", autoStart);

            bool changed = portField != Bridge.Port;
            if (changed) EditorGUILayout.HelpBox("Port changed. Restart the server to apply.", MessageType.Warning);

            GUILayout.Space(6);

            if (!Bridge.ServerRunning)
            {
                if (GUILayout.Button("Start server"))
                {
                    if (changed) Bridge.Port = portField;
                    Bridge.TryStartServer();
                    tokenField = Bridge.Token;
                }
            }
            else
            {
                if (GUILayout.Button("Stop server"))
                    Bridge.StopServer();
            }

            if (Bridge.ServerRunning)
            {
                EditorGUILayout.LabelField("URL", Bridge.BaseUrl);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Copy URL"))
                    EditorGUIUtility.systemCopyBuffer = Bridge.BaseUrl;

                if (GUILayout.Button("Ping"))
                {
                    ping = "Pinging...";
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            using (var wc = new System.Net.WebClient())
                            {
                                wc.Headers["X-Auth-Token"] = Bridge.Token;
                                string r = wc.DownloadString(Bridge.BaseUrl + "/health");
                                ping = "HTTP OK: " + r.Substring(0, Math.Min(120, r.Length));
                            }
                        }
                        catch (Exception ex)
                        {
                            ping = "Ping failed: " + ex.Message;
                        }
                        try { Repaint(); } catch { }
                    });
                }
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(ping)) EditorGUILayout.HelpBox(ping, MessageType.None);
            }

            GUILayout.Space(8);
            GUILayout.Label("Activity log", EditorStyles.boldLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            var lines = Bridge.GetLogLines();
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                GUIStyle style = new GUIStyle(EditorStyles.label) { wordWrap = true };
                var line = lines[i];
                bool isWarn = line.Contains("WARN");
                if (isWarn) style.normal.textColor = new Color(0.9f, 0.7f, 0.2f);
                else style.normal.textColor = new Color(0.75f, 0.85f, 0.95f);
                GUILayout.Label(line, style);
            }
            EditorGUILayout.EndScrollView();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear log"))
                Bridge.ClearLog();
            GUILayout.FlexibleSpace();
            GUILayout.Label("GPTUnity v" + Bridge.Version, EditorStyles.miniLabel);
            GUILayout.EndHorizontal();
        }
    }
}