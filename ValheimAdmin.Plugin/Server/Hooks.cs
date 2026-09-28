using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>Harmony patches that register the RPCs and turn game activity into agent events.</summary>
    public static class Hooks
    {
        private static readonly HashSet<long> joined = new HashSet<long>();
        private static readonly Dictionary<string, float> recentDeaths = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<ZDOID> reportedTombstones = new HashSet<ZDOID>();
        private static readonly int chatHash = "ChatMessage".GetStableHashCode();
        private static string lastEventName;

        public static string HostOf(ZNetPeer peer)
        {
            try
            {
                return peer?.m_socket?.GetHostName() ?? "";
            }
            catch
            {
                return "";
            }
        }

        public static Dictionary<string, object> PeerInfo(ZNetPeer peer)
        {
            return new Dictionary<string, object>
            {
                { "uid", peer.m_uid },
                { "player", peer.m_playerName },
                { "host", HostOf(peer) },
            };
        }

        public static void NoteDeath(string player)
        {
            if (!string.IsNullOrEmpty(player))
                recentDeaths[player] = Time.realtimeSinceStartup;
        }

        [HarmonyPatch(typeof(Game), "Start")]
        private static class Game_Start_Patch
        {
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null || ZNet.instance == null) return;
                if (ZNet.instance.IsDedicated())
                {
                    ServerRole.Register();
                    AgentLink.Event("started", new Dictionary<string, object> { { "world", ZNet.instance.GetWorldName() } });
                }
                else
                {
                    ClientRole.Register();
                }
            }
        }

        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        private static class ZNet_RPC_PeerInfo_Patch
        {
            private static void Postfix(ZNet __instance, ZRpc rpc)
            {
                if (!ServerRole.IsServer) return;
                ZNetPeer peer = __instance.GetPeer(rpc);
                if (peer == null || !peer.IsReady() || !joined.Add(peer.m_uid)) return;
                BepInExPlugin.Log("Joined: " + peer.m_playerName + " (" + HostOf(peer) + ")");
                AgentLink.Event("join", PeerInfo(peer));
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        private static class ZNet_Disconnect_Patch
        {
            private static void Prefix(ZNetPeer peer)
            {
                if (!ServerRole.IsServer || peer == null) return;
                ServerRole.OnPeerLeft(peer.m_uid);
                if (joined.Remove(peer.m_uid))
                    AgentLink.Event("leave", PeerInfo(peer));
            }
        }

        [HarmonyPatch(typeof(ZNet), "SaveWorld")]
        private static class ZNet_SaveWorld_Patch
        {
            private static void Postfix()
            {
                if (!ServerRole.IsServer) return;
                int players = ServerRole.SuppressSaveSnapshots ? 0 : ServerRole.SnapshotAll("save");
                AgentLink.Event("save", new Dictionary<string, object> { { "snapshots", players } });
            }
        }

        /// <summary>Chat lines pass through the server as routed RPCs addressed to everybody.</summary>
        [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
        private static class ZRoutedRpc_RPC_RoutedRPC_Patch
        {
            private static void Prefix(ZPackage pkg)
            {
                if (!ServerRole.IsServer || pkg == null) return;
                try
                {
                    var copy = new ZPackage(pkg.GetArray());
                    var data = new ZRoutedRpc.RoutedRPCData();
                    data.Deserialize(copy);
                    if (data.m_methodHash != chatHash) return;

                    ZPackage p = data.m_parameters;
                    p.SetPos(0);
                    Vector3 pos = p.ReadVector3();
                    var type = (Talker.Type)p.ReadInt();
                    var user = new UserInfo();
                    user.Deserialize(ref p);
                    string text = p.ReadString();
                    if (type == Talker.Type.Ping) return;

                    ZNetPeer peer = ZNet.instance.GetPeer(data.m_senderPeerID);
                    AgentLink.Event("chat", new Dictionary<string, object>
                    {
                        { "uid", data.m_senderPeerID },
                        { "player", peer?.m_playerName ?? user.Name },
                        { "host", HostOf(peer) },
                        { "type", type.ToString() },
                        { "text", text },
                        { "pos", new[] { Math.Round(pos.x), Math.Round(pos.y), Math.Round(pos.z) } },
                    });
                }
                catch (Exception e)
                {
                    BepInExPlugin.Dbgl("Chat peek failed: " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), "RPC_SetGlobalKey")]
        private static class ZoneSystem_RPC_SetGlobalKey_Patch
        {
            private static void Prefix(ZoneSystem __instance, string name, out bool __state)
            {
                __state = __instance.m_globalKeys.Contains(name);
            }

            private static void Postfix(ZoneSystem __instance, long sender, string name, bool __state)
            {
                if (!ServerRole.IsServer || __state || !__instance.m_globalKeys.Contains(name)) return;
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                bool boss = name.StartsWith("defeated_", StringComparison.OrdinalIgnoreCase);
                AgentLink.Event(boss ? "boss" : "globalkey", new Dictionary<string, object>
                {
                    { "key", name },
                    { "player", peer?.m_playerName },
                });
            }
        }

        [HarmonyPatch(typeof(RandEventSystem), "SetRandomEvent")]
        private static class RandEventSystem_SetRandomEvent_Patch
        {
            private static void Postfix(RandomEvent ev, Vector3 pos)
            {
                if (!ServerRole.IsServer) return;
                string name = ev?.m_name;
                if (name == lastEventName) return;

                if (name != null)
                {
                    AgentLink.Event("raid", new Dictionary<string, object>
                    {
                        { "name", name },
                        { "near", NearestPlayer(pos) },
                        { "pos", new[] { Math.Round(pos.x), Math.Round(pos.y), Math.Round(pos.z) } },
                    });
                }
                else
                {
                    AgentLink.Event("raid_end", new Dictionary<string, object> { { "name", lastEventName } });
                }
                lastEventName = name;
            }
        }

        /// <summary>
        /// Death fallback for players without the client role: a tombstone that the server
        /// instantiates within a minute of its time of death.
        /// </summary>
        [HarmonyPatch(typeof(TombStone), "Awake")]
        private static class TombStone_Awake_Patch
        {
            private static void Postfix(TombStone __instance)
            {
                if (!ServerRole.IsServer) return;
                try
                {
                    ZDO zdo = __instance.m_nview?.GetZDO();
                    if (zdo == null || !reportedTombstones.Add(zdo.m_uid)) return;

                    long ticks = zdo.GetLong(ZDOVars.s_timeOfDeath, 0);
                    if (ticks == 0 || (ZNet.instance.GetTime() - new DateTime(ticks)).TotalSeconds > 60) return;

                    string owner = zdo.GetString(ZDOVars.s_ownerName, "");
                    if (recentDeaths.TryGetValue(owner, out float at) && Time.realtimeSinceStartup - at < 120) return;
                    NoteDeath(owner);

                    Vector3 pos = zdo.GetPosition();
                    AgentLink.Event("death", new Dictionary<string, object>
                    {
                        { "player", owner },
                        { "cause", "unknown" },
                        { "pos", new[] { Math.Round(pos.x), Math.Round(pos.y), Math.Round(pos.z) } },
                    });
                }
                catch (Exception e)
                {
                    BepInExPlugin.Dbgl("Tombstone check failed: " + e.Message);
                }
            }
        }

        private static string NearestPlayer(Vector3 pos)
        {
            string best = null;
            float bestDist = float.MaxValue;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady()) continue;
                float d = Vector3.Distance(peer.m_refPos, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = peer.m_playerName;
                }
            }
            return best;
        }
    }
}
