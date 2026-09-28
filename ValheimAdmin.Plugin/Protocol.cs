using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ValheimAdmin
{
    /// <summary>
    /// Routed RPCs between the server role and the client role. Every server-to-client call
    /// carries a request id that the client echoes back in <see cref="Reply"/>.
    /// </summary>
    public static class Rpc
    {
        /// <summary>client -> server: string version, long characterId, string characterName</summary>
        public const string Hello = "VA_Hello";
        /// <summary>client -> server: long reqId, bool ok, ZPackage payload (deflated JSON)</summary>
        public const string Reply = "VA_Reply";
        /// <summary>client -> server: string json (cause of death)</summary>
        public const string Death = "VA_Death";

        /// <summary>server -> client: long reqId, string console command line (0.2 servers)</summary>
        public const string Command = "VA_Cmd";
        /// <summary>server -> client: long reqId, ZPackage payload (deflated JSON {line, confirmCheats})</summary>
        public const string Run = "VA_Run";
        /// <summary>server -> client: long reqId, string prefab, int count, int quality</summary>
        public const string Give = "VA_Give";
        /// <summary>server -> client: long reqId, string trigger</summary>
        public const string SnapshotRequest = "VA_SnapReq";
        /// <summary>server -> client: long reqId, ZPackage payload (deflated JSON)</summary>
        public const string Restore = "VA_Restore";
        /// <summary>server -> client: string text</summary>
        public const string Chat = "VA_Chat";

        public static ZPackage Pack(string json)
        {
            var pkg = new ZPackage();
            pkg.Write(Deflate(Encoding.UTF8.GetBytes(json ?? "")));
            return pkg;
        }

        public static string Unpack(ZPackage pkg)
        {
            return Encoding.UTF8.GetString(Inflate(pkg.ReadByteArray()));
        }

        public static byte[] Deflate(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress))
                    deflate.Write(data, 0, data.Length);
                return output.ToArray();
            }
        }

        public static byte[] Inflate(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }
    }

    /// <summary>Runs work queued from background threads on the Unity main thread.</summary>
    public static class MainThread
    {
        private static readonly Queue<Action> queue = new Queue<Action>();

        public static void Post(Action action)
        {
            lock (queue)
                queue.Enqueue(action);
        }

        public static void Pump()
        {
            while (true)
            {
                Action action;
                lock (queue)
                {
                    if (queue.Count == 0) return;
                    action = queue.Dequeue();
                }
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    BepInExPlugin.Warn("Queued action failed: " + e);
                }
            }
        }
    }
}
