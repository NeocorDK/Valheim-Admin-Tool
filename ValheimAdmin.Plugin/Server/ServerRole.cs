using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>
    /// Server side: which peers run the client role, requests sent to them and their replies,
    /// snapshot rounds and the heartbeat to the agent.
    /// </summary>
    public static class ServerRole
    {
        public class ModdedPeer
        {
            public string Version;
            public long CharacterId;
            public string CharacterName;
        }

        private class Pending
        {
            public long PeerUid;
            public float Deadline;
            public Action<bool, string> Callback;
        }

        public static readonly Dictionary<long, ModdedPeer> Modded = new Dictionary<long, ModdedPeer>();
        private static readonly Dictionary<long, Pending> pending = new Dictionary<long, Pending>();
        private static long nextRequestId = 1;

        private static float heartbeatTimer;
        private static int frames;
        private static float frameTime;
        private static float lastFps;

        public static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated();

        /// <summary>Set while shutting down so the final world save does not start another snapshot round.</summary>
        public static bool SuppressSaveSnapshots;

        public static void Register()
        {
            ZRoutedRpc.instance.Register<string, long, string>(Rpc.Hello, RPC_Hello);
            ZRoutedRpc.instance.Register<long, bool, ZPackage>(Rpc.Reply, RPC_Reply);
            ZRoutedRpc.instance.Register<string>(Rpc.Death, RPC_Death);
        }

        private static void RPC_Hello(long sender, string version, long characterId, string characterName)
        {
            if (!IsServer) return;
            Modded[sender] = new ModdedPeer { Version = version, CharacterId = characterId, CharacterName = characterName };
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            BepInExPlugin.Log("Client role " + version + " on " + characterName);
            AgentLink.Event("mod", new Dictionary<string, object>
            {
                { "uid", sender },
                { "player", characterName },
                { "characterId", characterId },
                { "host", Hooks.HostOf(peer) },
                { "version", version },
            });
        }

        private static void RPC_Reply(long sender, long requestId, bool ok, ZPackage payload)
        {
            if (!IsServer) return;
            if (!pending.TryGetValue(requestId, out Pending p) || p.PeerUid != sender) return;
            pending.Remove(requestId);

            string json;
            try
            {
                json = Rpc.Unpack(payload);
            }
            catch (Exception e)
            {
                p.Callback(false, "Bad payload: " + e.Message);
                return;
            }
            p.Callback(ok, json);
        }

        private static void RPC_Death(long sender, string json)
        {
            if (!IsServer) return;
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            Dictionary<string, object> data;
            try
            {
                data = Json.ParseObject(json);
            }
            catch
            {
                data = new Dictionary<string, object>();
            }
            data["uid"] = sender;
            data["player"] = peer?.m_playerName ?? data.Str("player");
            data["host"] = Hooks.HostOf(peer);
            Hooks.NoteDeath(peer?.m_playerName);
            AgentLink.Event("death", data);
        }

        /// <summary>
        /// Sends a request to a client. The callback gets (ok, json): the client's JSON on success,
        /// an error message otherwise.
        /// </summary>
        public static void SendToClient(long peerUid, string rpc, float timeoutSeconds, Action<bool, string> callback, params object[] args)
        {
            long id = nextRequestId++;
            pending[id] = new Pending { PeerUid = peerUid, Deadline = Time.realtimeSinceStartup + timeoutSeconds, Callback = callback };
            var parameters = new object[args.Length + 1];
            parameters[0] = id;
            Array.Copy(args, 0, parameters, 1, args.Length);
            ZRoutedRpc.instance.InvokeRoutedRPC(peerUid, rpc, parameters);
        }

        public static void OnPeerLeft(long uid)
        {
            Modded.Remove(uid);
            var failed = new List<long>();
            foreach (var kv in pending)
                if (kv.Value.PeerUid == uid)
                    failed.Add(kv.Key);
            foreach (long id in failed)
            {
                var p = pending[id];
                pending.Remove(id);
                p.Callback(false, "Player disconnected");
            }
        }

        /// <summary>True when the peer's client role is at least major.minor.</summary>
        public static bool AtLeast(ModdedPeer peer, int major, int minor)
        {
            string[] parts = (peer?.Version ?? "").Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out int ma) || !int.TryParse(parts[1], out int mi)) return false;
            return ma > major || (ma == major && mi >= minor);
        }

        public static ZNetPeer FindPeer(string nameOrId)
        {
            if (ZNet.instance == null || string.IsNullOrEmpty(nameOrId)) return null;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady()) continue;
                if (string.Equals(peer.m_playerName, nameOrId, StringComparison.OrdinalIgnoreCase) ||
                    peer.m_uid.ToString() == nameOrId ||
                    Hooks.HostOf(peer) == nameOrId)
                    return peer;
            }
            return null;
        }

        /// <summary>
        /// Asks one modded peer for a snapshot. Every successful snapshot is reported to the agent
        /// as a "snapshot" event; the callback additionally receives it.
        /// </summary>
        public static void RequestSnapshot(ZNetPeer peer, string trigger, Action<bool, string> callback)
        {
            long uid = peer.m_uid;
            string player = peer.m_playerName;
            string host = Hooks.HostOf(peer);
            SendToClient(uid, Rpc.SnapshotRequest, 30f, (ok, json) =>
            {
                if (ok)
                {
                    AgentLink.Event("snapshot", new Dictionary<string, object>
                    {
                        { "uid", uid },
                        { "player", player },
                        { "host", host },
                        { "trigger", trigger },
                        { "snapshot", new Json.Raw(json) },
                    });
                }
                else
                {
                    BepInExPlugin.Log("Snapshot of " + player + " failed: " + json);
                }
                callback?.Invoke(ok, json);
            }, trigger);
        }

        /// <summary>Snapshots every online modded player. onDone runs once all of them answered or timed out.</summary>
        public static int SnapshotAll(string trigger, Action onDone = null)
        {
            var peers = new List<ZNetPeer>();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer.IsReady() && Modded.ContainsKey(peer.m_uid))
                    peers.Add(peer);

            int remaining = peers.Count;
            if (remaining == 0)
            {
                onDone?.Invoke();
                return 0;
            }
            foreach (ZNetPeer peer in peers)
            {
                RequestSnapshot(peer, trigger, (ok, json) =>
                {
                    if (--remaining == 0)
                        onDone?.Invoke();
                });
            }
            return peers.Count;
        }

        public static void Update()
        {
            if (!IsServer) return;

            try
            {
                MapService.Update();
            }
            catch (Exception e)
            {
                BepInExPlugin.Dbgl("Map update failed: " + e.Message);
            }

            float now = Time.realtimeSinceStartup;
            if (pending.Count > 0)
            {
                var expired = new List<long>();
                foreach (var kv in pending)
                    if (kv.Value.Deadline < now)
                        expired.Add(kv.Key);
                foreach (long id in expired)
                {
                    var p = pending[id];
                    pending.Remove(id);
                    p.Callback(false, "Timed out waiting for the player");
                }
            }

            frames++;
            frameTime += Time.unscaledDeltaTime;
            heartbeatTimer += Time.unscaledDeltaTime;
            if (heartbeatTimer < 5f) return;

            lastFps = frameTime > 0 ? frames / frameTime : 0;
            frames = 0;
            frameTime = 0;
            heartbeatTimer = 0;
            AgentLink.Send(new Dictionary<string, object>
            {
                { "t", "hb" },
                { "stats", Stats() },
            });
        }

        public static Dictionary<string, object> Stats()
        {
            var stats = new Dictionary<string, object>
            {
                { "ready", ZoneSystem.instance != null && ZNet.instance != null && ZNet.instance.IsServer() },
                { "fps", Math.Round(lastFps, 1) },
                { "players", ZNet.instance != null ? ZNet.instance.GetNrOfPlayers() : 0 },
                { "modded", Modded.Count },
                { "pluginVersion", BepInExPlugin.pluginVersion },
            };
            try
            {
                if (ZNet.instance != null) stats["world"] = ZNet.instance.GetWorldName();
                if (ZDOMan.instance != null) stats["zdos"] = ZDOMan.instance.NrOfObjects();
                if (EnvMan.instance != null)
                {
                    stats["day"] = EnvMan.instance.GetDay();
                    stats["dayFraction"] = Math.Round(EnvMan.instance.GetDayFraction(), 3);
                }
                if (RandEventSystem.instance != null)
                    stats["event"] = RandEventSystem.instance.GetCurrentRandomEvent()?.m_name;
            }
            catch (Exception e)
            {
                BepInExPlugin.Dbgl("Stats: " + e.Message);
            }
            return stats;
        }
    }
}
