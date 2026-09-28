using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ValheimAdmin
{
    /// <summary>
    /// Line-delimited JSON connection from the server process to the agent on 127.0.0.1.
    /// The agent passes port and secret through environment variables when it launches the
    /// server. Because the secret is persistent, a server that outlived an agent restart
    /// reconnects on its own.
    /// </summary>
    public static class AgentLink
    {
        private const int MaxBuffered = 2000;

        private static Thread thread;
        private static volatile bool running;
        private static TcpClient client;
        private static StreamWriter writer;
        private static readonly object sendLock = new object();
        private static readonly Queue<string> buffered = new Queue<string>();
        private static int port;
        private static string secret;
        private static volatile string refusedReason;
        private static volatile bool welcomed;
        private static bool refusedLogged;

        public static bool Enabled => thread != null;

        /// <summary>Also receives every event; set by the plugin's own web server in standalone mode.</summary>
        public static Action<string, Dictionary<string, object>> LocalEvent;
        public static bool Connected { get; private set; }

        public static void Start()
        {
            if (thread != null) return;

            string envPort = Environment.GetEnvironmentVariable("VA_AGENT_PORT");
            string envSecret = Environment.GetEnvironmentVariable("VA_AGENT_SECRET");
            if (!string.IsNullOrEmpty(envPort) && !string.IsNullOrEmpty(envSecret))
            {
                int.TryParse(envPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
                secret = envSecret;
            }
            else if (BepInExPlugin.AgentPort.Value > 0 && File.Exists(BepInExPlugin.AgentSecretFile.Value))
            {
                port = BepInExPlugin.AgentPort.Value;
                secret = File.ReadAllText(BepInExPlugin.AgentSecretFile.Value).Trim();
            }

            if (port <= 0 || string.IsNullOrEmpty(secret))
            {
                BepInExPlugin.Log("No agent configured; admin panel link disabled.");
                return;
            }

            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "ValheimAdmin.AgentLink" };
            thread.Start();
            BepInExPlugin.Log("Agent link started on 127.0.0.1:" + port);
        }

        public static void Stop()
        {
            running = false;
            try { client?.Close(); } catch { }
        }

        private static void Run()
        {
            while (running)
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        tcp.NoDelay = true;
                        tcp.Connect("127.0.0.1", port);
                        client = tcp;
                        var stream = tcp.GetStream();
                        var reader = new StreamReader(stream, new UTF8Encoding(false));
                        lock (sendLock)
                        {
                            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                            writer.WriteLine(Json.Serialize(new Dictionary<string, object>
                            {
                                { "t", "hello" },
                                { "secret", secret },
                                { "version", BepInExPlugin.pluginVersion },
                                { "pid", System.Diagnostics.Process.GetCurrentProcess().Id },
                            }));
                        }
                        // The link counts as up only once the agent answers "welcome": it may refuse it instead.

                        string line;
                        while (running && (line = reader.ReadLine()) != null)
                            Receive(line);
                    }
                }
                catch (Exception e)
                {
                    if (welcomed || BepInExPlugin.IsDebug.Value)
                        BepInExPlugin.Warn("Agent link: " + e.Message);
                }
                finally
                {
                    lock (sendLock)
                    {
                        writer = null;
                        Connected = false;
                    }
                    welcomed = false;
                    client = null;
                }

                if (!running) break;
                string refused = refusedReason;
                refusedReason = null;
                if (refused != null)
                {
                    // The agent manages another copy of this server; knocking every 3 s would only flood the log.
                    if (!refusedLogged) BepInExPlugin.Warn("The agent refused the link: " + refused);
                    refusedLogged = true;
                    Thread.Sleep(30000);
                }
                else
                    Thread.Sleep(3000);
            }
        }

        private static void Receive(string line)
        {
            Dictionary<string, object> msg;
            try
            {
                msg = Json.ParseObject(line);
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Bad message from agent: " + e.Message);
                return;
            }

            switch (msg.Str("t"))
            {
                case "welcome":
                    lock (sendLock)
                    {
                        if (writer == null) return;
                        while (buffered.Count > 0)
                            writer.WriteLine(buffered.Dequeue());
                        Connected = true;
                    }
                    welcomed = true;
                    refusedLogged = false;
                    BepInExPlugin.Log("Connected to agent.");
                    return;
                case "refused":
                    refusedReason = msg.Str("reason", "");
                    return;
            }
            if (msg.Str("t") != "req") return;

            long id = msg.Long("id");
            string cmd = msg.Str("cmd", "");
            var args = msg.Obj("args") ?? new Dictionary<string, object>();
            MainThread.Post(() => Commands.Handle((ok, data, error) => Reply(id, ok, data, error), cmd, args));
        }

        /// <summary>Sends a message. Events are buffered while the agent is away; everything else is dropped.</summary>
        public static void Send(Dictionary<string, object> msg, bool bufferIfOffline = false)
        {
            if (!Enabled) return;
            string line = Json.Serialize(msg);
            lock (sendLock)
            {
                if (writer != null && Connected)
                {
                    try
                    {
                        writer.WriteLine(line);
                        return;
                    }
                    catch (Exception e)
                    {
                        BepInExPlugin.Dbgl("Agent send failed: " + e.Message);
                        writer = null;
                    }
                }
                if (bufferIfOffline)
                {
                    if (buffered.Count >= MaxBuffered) buffered.Dequeue();
                    buffered.Enqueue(line);
                }
            }
        }

        public static void Reply(long id, bool ok, object data, string error = null)
        {
            Send(new Dictionary<string, object>
            {
                { "t", "res" },
                { "id", id },
                { "ok", ok },
                { "data", data },
                { "error", error },
            });
        }

        public static void Event(string kind, Dictionary<string, object> data)
        {
            LocalEvent?.Invoke(kind, data);
            Send(new Dictionary<string, object>
            {
                { "t", "ev" },
                { "kind", kind },
                { "ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "data", data },
            }, bufferIfOffline: true);
        }
    }
}
