using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEditor;

namespace GPTUnity
{
    /// <summary>
    /// Marshals work onto the Unity main thread. HttpListener callbacks run on
    /// background threads, but (almost) every Unity API must be touched from the
    /// main thread, so requests enqueue here and EditorApplication.update drains
    /// the queue. The calling (HTTP) thread blocks until the work completes.
    /// </summary>
    public static class MainThread
    {
        sealed class Entry
        {
            public Func<object> Action;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public object Result;
            public Exception Error;
        }

        static readonly ConcurrentQueue<Entry> Queue = new ConcurrentQueue<Entry>();

        [InitializeOnLoadMethod]
        static void Init()
        {
            EditorApplication.update += Pump;
        }

        static void Pump()
        {
            while (Queue.TryDequeue(out Entry e))
            {
                try { e.Result = e.Action(); }
                catch (Exception ex) { e.Error = ex; }
                finally { e.Done.Set(); }
            }
        }

        /// <summary>Runs <paramref name="action"/> on the main thread and blocks the caller until it completes.</summary>
        public static object Execute(Func<object> action, int timeoutMs = 30000)
        {
            var e = new Entry { Action = action };
            Queue.Enqueue(e);
            if (e.Done.Wait(timeoutMs))
            {
                if (e.Error != null) throw e.Error;
                return e.Result;
            }
            throw new TimeoutException("Main-thread operation was not completed within " + timeoutMs + " ms.");
        }

        public static T Execute<T>(Func<T> action, int timeoutMs = 30000)
        {
            return (T)Execute(() => (object)action(), timeoutMs);
        }

        public static void Execute(Action action, int timeoutMs = 30000)
        {
            Execute(() => { action(); return null; }, timeoutMs);
        }
    }
}