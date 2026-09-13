using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace GPTUnity
{
    /// <summary>
    /// Executes arbitrary C# inside the Unity Editor by writing it to a temporary
    /// asmdef'd file and waiting for compilation. Because a script change forces a
    /// domain reload, the pending job is persisted in SessionState and results are
    /// handed back through a temp file, so the caller (HTTP thread) is not lost in
    /// the reload. The generated bridge folder remains in the project between runs
    /// (it is an Editor-only assembly and is git-ignored) so each call only recompiles
    /// that one assembly.
    /// </summary>
    public sealed class CodeExecutor
    {
        public sealed class ExecutionResult
        {
            public bool ok;
            public string error;
            public List<string> logs = new List<string>();
            public object value;
        }

        const string Folder = "Assets/GPTUnity_Generated_Bridge";
        const string SessionKey = "GPTUnity.PendingExec";
        const string AsmdefAsset = Folder + "/GPTUnityBridge.asmdef";

        enum State { Idle, Writing, WaitingCompile, Ready, Done }

        static readonly List<CodeExecutor> Active = new List<CodeExecutor>();
        static readonly object Lock = new object();

        State state;
        DateTime wroteAt;
        DateTime deadline;
        readonly string code;
        readonly string resultFile;
        readonly int compileTimeoutMs;

        CodeExecutor(string code, string resultFile, State continueFrom, int compileTimeoutSec)
        {
            this.code = code;
            this.resultFile = resultFile;
            state = continueFrom;
            compileTimeoutMs = compileTimeoutSec * 1000;
            if (state == State.WaitingCompile) wroteAt = DateTime.Now;
            if (state == State.Writing) wroteAt = DateTime.MaxValue;
            deadline = DateTime.Now;
        }

        static void EnsureHooks()
        {
            EditorApplication.update -= TickAll;
            EditorApplication.update += TickAll;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
        }

        [InitializeOnLoadMethod]
        static void Init()
        {
            EnsureHooks();
            RecoverPending(SessionState.GetString(SessionKey, null));
        }

        static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            string name = Path.GetFileName(assemblyPath);
            if (name != "GPTUnityBridge.dll") return;
            lock (Lock)
            {
                foreach (var ex in Active)
                {
                    if (ex.state == State.WaitingCompile)
                        ex.deadline = DateTime.Now.AddMilliseconds(ex.compileTimeoutMs);
                }
            }
        }

        /// <summary>Called on the HTTP thread. Polls a temp result file so the Unity
        /// domain reload that compilation triggers cannot kill this call.</summary>
        public static ExecutionResult Execute(string code, int timeoutSec)
        {
            string opId = Guid.NewGuid().ToString("N");
            string resultFile = Path.Combine(Path.GetTempPath(), "gptunity_" + opId + ".json");
            try { File.Delete(resultFile); }
            catch { }

            var session = new Dictionary<string, object>
            {
                { "stage", "Writing" },
                { "code", code },
                { "resultFile", resultFile }
            };
            bool kicked = MainThread.Execute(() =>
            {
                SessionState.SetString(SessionKey, Json.Serialize(session));
                lock (Lock) Active.Clear();
                EnsureHooks();
                Active.Add(new CodeExecutor(code, resultFile, State.Writing, timeoutSec));
                return true;
            }, 60000);
            _ = kicked;

            var deadline = DateTime.Now.AddSeconds(timeoutSec);
            while (DateTime.Now < deadline)
            {
                if (File.Exists(resultFile))
                {
                    try { return ParseResultFile(resultFile); }
                    catch (Exception ex) { return new ExecutionResult { ok = false, error = "Failed reading result file: " + ex.Message }; }
                }
                Thread.Sleep(120);
            }

            // Give the editor a few seconds to finish reacting to the compile even
            // though the caller timed out (result file may still land, that's fine).
            return new ExecutionResult
            {
                ok = false,
                error = "Timed out after " + timeoutSec + "s waiting for the code to compile & run. " +
                        "Compilation may still be finishing — check the Unity Console for errors."
            };
        }

        static ExecutionResult ParseResultFile(string path)
        {
            string json = File.ReadAllText(path);
            var d = Json.Parse(json) as Dictionary<string, object>;
            var res = new ExecutionResult();
            if (d == null) { res.ok = false; res.error = "Malformed result file."; return res; }
            res.ok = Json.GetBool(d, "ok", false);
            res.error = Json.GetString(d, "error");
            res.value = d.ContainsKey("result") ? d["result"] : null;
            if (d.TryGetValue("logs", out object logs) && logs is List<object> list)
                foreach (var l in list) res.logs.Add(l?.ToString() ?? "null");
            try { File.Delete(path); } catch { }
            return res;
        }

        static void RecoverPending(string sessionJson)
        {
            if (string.IsNullOrEmpty(sessionJson)) return;
            var s = Json.Parse(sessionJson) as Dictionary<string, object>;
            if (s == null) return;
            string code = Json.GetString(s, "code");
            string resultFile = Json.GetString(s, "resultFile");
            string stage = Json.GetString(s, "stage", "Writing");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(resultFile)) return;
            var ex = new CodeExecutor(code, resultFile, stage == "WaitingCompile" ? State.WaitingCompile : State.Writing, 180);
            lock (Lock) Active.Add(ex);
        }

        static void TickAll()
        {
            lock (Lock)
            {
                for (int i = Active.Count - 1; i >= 0; i--)
                {
                    var ex = Active[i];
                    try { ex.Tick(); }
                    catch (Exception e) { ex.FailFast("Editor-side fault: " + e); }
                    if (ex.state == State.Done) Active.RemoveAt(i);
                }
            }
        }

        static bool FilesUpToDate(string csContent)
        {
            if (!File.Exists(AsmdefAsset)) return false;
            string csPath = Folder + "/GPTUnityBridge.cs";
            if (!File.Exists(csPath)) return false;
            try
            {
                string existing = File.ReadAllText(csPath);
                return string.Equals(existing, csContent, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        void Tick()
        {
            switch (state)
            {
                case State.Writing:
                {
                    string cs = BuildBridgeSource(code);
                    if (FilesUpToDate(cs) && TryFindHost())
                    {
                        state = State.WaitingCompile;
                        wroteAt = DateTime.Now;
                    }
                    else
                    {
                        Directory.CreateDirectory(Folder);
                        File.WriteAllText(AsmdefAsset, AsmdefJson, System.Text.Encoding.UTF8);
                        File.WriteAllText(Folder + "/GPTUnityBridge.cs", cs, System.Text.Encoding.UTF8);
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                        wroteAt = DateTime.Now;
                        state = State.WaitingCompile;
                        PersistStage("WaitingCompile");
                    }
                    break;
                }

                case State.WaitingCompile:
                {
                    string errs = GetBridgeCompileErrors();
                    if (!string.IsNullOrEmpty(errs))
                    {
                        WriteResult(new ExecutionResult { ok = false, error = "Compile failed:\n" + errs });
                        ClearSession();
                        state = State.Done;
                        break;
                    }
                    if (TryFindHost())
                    {
                        RunBridge();
                        ClearSession();
                        state = State.Done;
                        break;
                    }
                    if ((DateTime.Now - wroteAt).TotalMilliseconds > compileTimeoutMs)
                    {
                        WriteResult(new ExecutionResult { ok = false, error = "Bridge assembly did not compile within " + (compileTimeoutMs / 1000) + "s. Check the Unity Console for errors." });
                        ClearSession();
                        state = State.Done;
                    }
                    break;
                }
            }
        }

        void FailFast(string message)
        {
            WriteResult(new ExecutionResult { ok = false, error = message });
            ClearSession();
            state = State.Done;
        }

        void PersistStage(string stage)
        {
            var s = new Dictionary<string, object> { { "stage", stage }, { "code", code }, { "resultFile", resultFile } };
            SessionState.SetString(SessionKey, Json.Serialize(s));
        }

        static void ClearSession() => SessionState.EraseString(SessionKey);

        void RunBridge()
        {
            try
            {
                var hostType = FindHostType();
                var gptType = FindGptType();
                if (hostType == null)
                {
                    WriteResult(new ExecutionResult { ok = false, error = "Bridge Host type not found after compile." });
                    return;
                }
                var logs = new List<string>();
                object result = null;
                var run = hostType.GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (run == null)
                {
                    WriteResult(new ExecutionResult { ok = false, error = "Bridge Host.Run() method not found." });
                    return;
                }
                try
                {
                    run.Invoke(null, null);
                }
                catch (System.Reflection.TargetInvocationException tie)
                {
                    var inner = tie.InnerException;
                    string err = (inner != null ? inner.GetType().Name + ": " + inner.Message + "\n" + inner.StackTrace : tie.ToString());
                    if (gptType != null)
                    {
                        try
                        {
                            var logsField = gptType.GetField("Logs");
                            if (logsField != null && logsField.GetValue(null) is System.Collections.IEnumerable en)
                                foreach (var item in en) logs.Add(item?.ToString() ?? "null");
                        }
                        catch { }
                    }
                    WriteResult(new ExecutionResult { ok = false, error = err, logs = logs });
                    return;
                }
                catch (Exception ex)
                {
                    WriteResult(new ExecutionResult { ok = false, error = ex.ToString() });
                    return;
                }

                if (gptType != null)
                {
                    try
                    {
                        var logsField = gptType.GetField("Logs");
                        if (logsField != null && logsField.GetValue(null) is System.Collections.IEnumerable en)
                            foreach (var item in en) logs.Add(item?.ToString() ?? "null");
                        var resultField = gptType.GetField("ResultValue");
                        if (resultField != null) result = resultField.GetValue(null);
                    }
                    catch { }
                }

                WriteResult(new ExecutionResult { ok = true, logs = logs, value = result });
            }
            catch (Exception ex)
            {
                WriteResult(new ExecutionResult { ok = false, error = ex.ToString() });
            }
        }

        void WriteResult(ExecutionResult r)
        {
            try
            {
                var d = new Dictionary<string, object>
                {
                    { "ok", r.ok },
                    { "error", r.error },
                    { "logs", r.logs },
                    { "result", r.value }
                };
                File.WriteAllText(resultFile, Json.Serialize(d), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Bridge.Warn("Failed writing code result file: " + ex.Message);
            }
        }

        static string GetBridgeCompileErrors()
        {
            try
            {
                var msgs = CompilationPipeline.GetLastCompilationErrors();
                var sb = new System.Text.StringBuilder();
                foreach (var m in msgs)
                {
                    if (m.type != CompilerMessageType.Error) continue;
                    if (string.IsNullOrEmpty(m.fileName) || m.fileName.IndexOf("GPTUnity_Generated_Bridge", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sb.AppendLine(m.fileName + (m.line > 0 ? "(" + m.line + "): " : ": ") + m.message);
                }
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        static Type FindHostType()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a == null) continue;
                var t = a.GetType("GPTUnity.Runtime.Host");
                if (t != null) return t;
            }
            return null;
        }

        static Type FindGptType()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a == null) continue;
                var t = a.GetType("GPTUnity.Runtime.GPT");
                if (t != null) return t;
            }
            return null;
        }

        static bool TryFindHost() => FindHostType() != null;

        const string AsmdefJson = @"{
            ""name"": ""GPTUnityBridge"",
            ""rootNamespace"": ""GPTUnity.Runtime"",
            ""references"": [],
            ""includePlatforms"": [
                ""Editor""
            ],
            ""excludePlatforms"": [],
            ""allowUnsafeCode"": false,
            ""overrideReferences"": false,
            ""precompiledReferences"": [],
            ""autoReferenced"": true,
            ""defineConstraints"": [],
            ""versionDefines"": [],
            ""noEngineReferences"": false
        }";

        static string BuildBridgeSource(string userCode)
        {
            return
"using UnityEngine;\n" +
"using System.Collections.Generic;\n" +
"using UnityEditor;\n" +
"namespace GPTUnity.Runtime\n" +
"{\n" +
"    public static class GPT\n" +
"    {\n" +
"        public static List<string> Logs = new List<string>();\n" +
"        public static object ResultValue;\n" +
"        public static void Log(object o) { Logs.Add(o == null ? \"null\" : o.ToString()); }\n" +
"        public static void Result(object o) { ResultValue = o; }\n" +
"        public static GameObject Find(string name)\n" +
"        {\n" +
"            var go = GameObject.Find(name);\n" +
"            if (go != null) return go;\n" +
"            var all = Resources.FindObjectsOfTypeAll<GameObject>();\n" +
"            for (int i = 0; i < all.Length; i++) if (all[i].name == name && all[i].scene.isLoaded) return all[i];\n" +
"            return null;\n" +
"        }\n" +
"        public static List<GameObject> FindAll(string name)\n" +
"        {\n" +
"            var res = new List<GameObject>();\n" +
"            var all = Resources.FindObjectsOfTypeAll<GameObject>();\n" +
"            for (int i = 0; i < all.Length; i++) if (all[i].name == name && all[i].scene.isLoaded) res.Add(all[i]);\n" +
"            return res;\n" +
"        }\n" +
"        public static void Select(GameObject go) { Selection.activeGameObject = go; }\n" +
"        public static void Destroy(GameObject go)\n" +
"        {\n" +
"            if (go == null) return;\n" +
"            if (Application.isPlaying) Object.Destroy(go); else Object.DestroyImmediate(go);\n" +
"        }\n" +
"        public static void SetDirty(UnityEngine.Object o) { EditorUtility.SetDirty(o); }\n" +
"    }\n" +
"    public static class Host\n" +
"    {\n" +
"        public static void Run()\n" +
"        {\n" +
"            // USER CODE START\n" +
Indent(userCode) +
"            // USER CODE END\n" +
"        }\n" +
"    }\n" +
"}\n";
        }

        static string Indent(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            return code.Replace("\r\n", "\n")
                       .Replace("\r", "\n")
                       .Replace("\n", "\n            ");
        }
    }
}