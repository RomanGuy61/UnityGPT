using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace GPTUnity
{
    /// <summary>
    /// Loopback-only HTTP/JSON server. Tokens authenticate every request.
    /// Bound to 127.0.0.1 so nothing outside the machine can reach it.
    /// </summary>
    public sealed class GPTUnityHttpServer
    {
        readonly int port;
        readonly string token;
        HttpListener listener;
        Thread listenThread;
        volatile bool running;

        public int Port => port;

        public GPTUnityHttpServer(int port, string token)
        {
            this.port = port;
            this.token = token;
        }

        public void Start()
        {
            listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            listener.Prefixes.Add("http://localhost:" + port + "/");
            listener.Start();
            running = true;
            listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "GPTUnity-HTTP" };
            listenThread.Start();
        }

        public void Stop()
        {
            running = false;
            try { listener?.Abort(); } catch { }
            try { listener?.Close(); } catch { }
            listener = null;
        }

        void ListenLoop()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch
                {
                    if (!running) break;
                    Thread.Sleep(50);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var res = ctx.Response;
                res.Headers["Access-Control-Allow-Origin"] = "*";
                res.Headers["Access-Control-Allow-Headers"] = "content-type, x-auth-token, authorization";
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Cache-Control"] = "no-store";

                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 200;
                    res.Close();
                    return;
                }

                if (!Authorized(req))
                {
                    Respond(res, 401, Json.Serialize(MakeError("Unauthorized. The X-Auth-Token header did not match the token shown in the GPTUnity Bridge window.")));
                    return;
                }

                string body = null;
                if (req.HasEntityBody)
                {
                    using (var rd = new StreamReader(req.InputStream, Encoding.UTF8))
                        body = rd.ReadToEnd();
                }

                string method = req.HttpMethod.ToUpperInvariant();
                string path = req.Url.AbsolutePath;
                string query = req.Url.Query;

                try
                {
                    ApiRouter.Handle(method, path, query, body, res);
                }
                catch (TimeoutException)
                {
                    Respond(res, 504, Json.Serialize(MakeError("The editor is busy (compiling/saving) and the operation timed out. Try again.")));
                }
                catch (ApiException aex)
                {
                    Respond(res, aex.StatusCode, Json.Serialize(MakeError(aex.Message, aex.Hint)));
                }
                catch (Exception ex)
                {
                    string m = ex.Message;
                    if (ex.InnerException != null) m += " | " + ex.InnerException.Message;
                    Bridge.Warn("Unhandled error in /" + path + ": " + m);
                    Respond(res, 500, Json.Serialize(MakeError("Internal error: " + m)));
                }
            }
            catch (Exception ex)
            {
                Bridge.Warn("HTTP handler fatal error: " + ex.Message);
                try { ctx.Response?.Abort(); } catch { }
            }
        }

        bool Authorized(HttpListenerRequest req)
        {
            string h = req.Headers["X-Auth-Token"];
            if (!string.IsNullOrEmpty(h) && FixedEquals(h, token)) return true;

            string auth = req.Headers["Authorization"];
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.Ordinal)
                && FixedEquals(auth.Substring(7), token)) return true;

            string q = req.QueryString["token"];
            if (!string.IsNullOrEmpty(q) && FixedEquals(q, token)) return true;

            return false;
        }

        static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static Dictionary<string, object> MakeError(string error, string hint = null)
        {
            var d = new Dictionary<string, object> { { "ok", false }, { "error", error } };
            if (hint != null) d["hint"] = hint;
            return d;
        }

        public static void Respond(HttpListenerResponse res, int code, string json)
        {
            if (res == null) return;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            res.StatusCode = code;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentEncoding = Encoding.UTF8;
            res.ContentLength64 = bytes.Length;
            try
            {
                res.OutputStream.Write(bytes, 0, bytes.Length);
            }
            finally
            {
                try { res.OutputStream.Close(); } catch { }
                try { res.Close(); } catch { }
            }
        }
    }
}