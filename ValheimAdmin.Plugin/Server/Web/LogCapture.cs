using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace ValheimAdmin.Web
{
    /// <summary>
    /// The server log for the panel's console in standalone mode: every BepInEx log event (which
    /// includes Unity's own log) goes into a ring buffer, the same lines the agent tails from
    /// LogOutput.log.
    /// </summary>
    public sealed class LogCapture : ILogListener
    {
        private const int RingSize = 5000;
        private readonly LinkedList<Dictionary<string, object>> ring = new LinkedList<Dictionary<string, object>>();
        private long seq;

        public void LogEvent(object sender, LogEventArgs e)
        {
            string level = (e.Level & (LogLevel.Error | LogLevel.Fatal)) != 0 ? "error"
                : (e.Level & LogLevel.Warning) != 0 ? "warning" : "info";
            string text = "[" + e.Level + ": " + e.Source?.SourceName + "] " + e.Data;
            var line = new Dictionary<string, object>
            {
                { "seq", Interlocked.Increment(ref seq) },
                { "ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "level", level },
                { "text", text },
            };
            lock (ring)
            {
                ring.AddLast(line);
                while (ring.Count > RingSize) ring.RemoveFirst();
            }
        }

        public List<object> Recent(int count, long after)
        {
            lock (ring)
                return ring.Where(l => l.Long("seq") > after).Reverse().Take(count).Reverse().Cast<object>().ToList();
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Calls into the game from web server threads; the game itself runs on the Unity main thread.</summary>
    public static class GameCalls
    {
        /// <summary>Runs a bridge command (the same ones the agent sends) and returns its data, or throws HttpError 409.</summary>
        public static object Command(string cmd, Dictionary<string, object> args = null, int timeoutMs = 20000)
        {
            var done = new ManualResetEventSlim();
            bool ok = false;
            object data = null;
            string error = null;
            int answered = 0;
            MainThread.Post(() => Commands.Handle((o, d, e) =>
            {
                if (Interlocked.Exchange(ref answered, 1) != 0) return;
                ok = o;
                data = d;
                error = e;
                done.Set();
            }, cmd, args ?? new Dictionary<string, object>()));
            if (!done.Wait(timeoutMs)) throw new HttpError(409, "The server did not answer in time");
            if (!ok) throw new HttpError(409, error ?? "Command failed");
            return data;
        }

        /// <summary>A command's data as plain JSON values (client replies arrive as raw JSON text).</summary>
        public static object Plain(object data) => data == null ? null : Json.Parse(Json.Serialize(data));

        public static T OnMain<T>(Func<T> work, int timeoutMs = 10000)
        {
            var done = new ManualResetEventSlim();
            T result = default(T);
            Exception failure = null;
            MainThread.Post(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    done.Set();
                }
            });
            if (!done.Wait(timeoutMs)) throw new HttpError(409, "The server did not answer in time");
            if (failure is HttpError) throw failure;
            if (failure != null) throw new HttpError(409, failure.Message);
            return result;
        }
    }
}
