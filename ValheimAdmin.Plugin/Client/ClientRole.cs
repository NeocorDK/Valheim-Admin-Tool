using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>
    /// Player side. Every request is accepted only from the server peer, and commands and
    /// restores can be turned off in the client config.
    /// </summary>
    public static class ClientRole
    {
        private static List<string> capturedOutput;

        public static void Register()
        {
            ZRoutedRpc.instance.Register<long, string>(Rpc.Command, RPC_Command);
            ZRoutedRpc.instance.Register<long, string, int, int>(Rpc.Give, RPC_Give);
            ZRoutedRpc.instance.Register<long, string>(Rpc.SnapshotRequest, RPC_SnapshotRequest);
            ZRoutedRpc.instance.Register<long, ZPackage>(Rpc.Restore, RPC_Restore);
            ZRoutedRpc.instance.Register<string>(Rpc.Chat, RPC_Chat);
        }

        private static bool IsClient => ZNet.instance != null && !ZNet.instance.IsServer();

        private static bool FromServer(long sender)
        {
            return IsClient && sender == ZRoutedRpc.instance.GetServerPeerID();
        }

        private static void Reply(long requestId, bool ok, object data)
        {
            string json = data is string s && !ok ? s : Json.Serialize(data);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), Rpc.Reply, requestId, ok, Rpc.Pack(json));
        }

        private static Player LivePlayer(long requestId)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Reply(requestId, false, "The character is not spawned");
                return null;
            }
            if (player.IsDead())
            {
                Reply(requestId, false, "The character is dead");
                return null;
            }
            return player;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Player_OnSpawned_Patch
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || !IsClient || ZRoutedRpc.instance == null) return;
                PlayerProfile profile = Game.instance.GetPlayerProfile();
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), Rpc.Hello,
                    BepInExPlugin.pluginVersion, profile.GetPlayerID(), __instance.GetPlayerName());
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
        private static class Player_OnDeath_Patch
        {
            private static void Prefix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || !IsClient || ZRoutedRpc.instance == null) return;
                try
                {
                    HitData hit = __instance.m_lastHit;
                    Character attacker = hit?.GetAttacker();
                    Vector3 pos = __instance.transform.position;
                    string json = Json.Serialize(new Dictionary<string, object>
                    {
                        { "player", __instance.GetPlayerName() },
                        { "cause", hit != null ? hit.m_hitType.ToString() : "Unknown" },
                        { "attacker", attacker != null ? attacker.GetHoverName() : null },
                        { "attackerIsPlayer", attacker != null && attacker.IsPlayer() },
                        { "pos", new[] { Math.Round(pos.x), Math.Round(pos.y), Math.Round(pos.z) } },
                    });
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), Rpc.Death, json);
                }
                catch (Exception e)
                {
                    BepInExPlugin.Warn("Death report failed: " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.AddString), typeof(string))]
        private static class Terminal_AddString_Patch
        {
            private static void Postfix(string text)
            {
                capturedOutput?.Add(text);
            }
        }

        private static void RPC_Command(long sender, long requestId, string command)
        {
            if (!FromServer(sender)) return;
            if (!BepInExPlugin.AllowServerCommands.Value)
            {
                Reply(requestId, false, "The player disabled remote commands");
                return;
            }
            if (Console.instance == null)
            {
                Reply(requestId, false, "Console is not available");
                return;
            }

            BepInExPlugin.Log("Admin command: " + command);
            bool cheat = Terminal.m_cheat;
            capturedOutput = new List<string>();
            try
            {
                Terminal.m_cheat = true;
                Console.instance.TryRunCommand(command, false, true);
                Reply(requestId, true, new Dictionary<string, object> { { "output", capturedOutput } });
            }
            catch (Exception e)
            {
                Reply(requestId, false, e.Message);
            }
            finally
            {
                capturedOutput = null;
                Terminal.m_cheat = cheat;
            }
        }

        private static void RPC_Give(long sender, long requestId, string prefab, int count, int quality)
        {
            if (!FromServer(sender)) return;
            if (!BepInExPlugin.AllowRestore.Value)
            {
                Reply(requestId, false, "The player disabled restores");
                return;
            }
            Player player = LivePlayer(requestId);
            if (player == null) return;

            GameObject go = ObjectDB.instance.GetItemPrefab(prefab);
            ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
            if (drop == null)
            {
                Reply(requestId, false, "Unknown item: " + prefab);
                return;
            }

            var report = new Restorer.Report();
            int maxStack = Math.Max(1, drop.m_itemData.m_shared.m_maxStackSize);
            for (int left = count; left > 0;)
            {
                int stack = Math.Min(left, maxStack);
                left -= stack;
                ItemDrop.ItemData item = drop.m_itemData.Clone();
                item.m_dropPrefab = go;
                item.m_stack = stack;
                item.m_quality = quality;
                item.m_durability = item.GetMaxDurability();
                Restorer.AddOrDrop(player, item, report);
            }
            player.Message(MessageHud.MessageType.Center, Texts.Given(Localization.instance.Localize(drop.m_itemData.m_shared.m_name), count));
            Reply(requestId, true, report.ToJson());
        }

        private static void RPC_SnapshotRequest(long sender, long requestId, string trigger)
        {
            if (!FromServer(sender)) return;
            Player player = LivePlayer(requestId);
            if (player == null) return;
            try
            {
                Reply(requestId, true, Snapshot.Build(player, trigger));
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Snapshot failed: " + e);
                Reply(requestId, false, "Snapshot failed: " + e.Message);
            }
        }

        private static void RPC_Restore(long sender, long requestId, ZPackage payload)
        {
            if (!FromServer(sender)) return;
            if (!BepInExPlugin.AllowRestore.Value)
            {
                Reply(requestId, false, "The player disabled restores");
                return;
            }
            Player player = LivePlayer(requestId);
            if (player == null) return;
            try
            {
                var request = Json.ParseObject(Rpc.Unpack(payload));
                Reply(requestId, true, Restorer.Apply(player, request).ToJson());
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Restore failed: " + e);
                Reply(requestId, false, "Restore failed: " + e.Message);
            }
        }

        private static void RPC_Chat(long sender, string text)
        {
            if (!FromServer(sender) || Chat.instance == null) return;
            Chat.instance.AddString("<color=orange>" + Texts.ServerTag + "</color>", text, Talker.Type.Shout, false);
            Chat.instance.m_hideTimer = 0f;
        }
    }
}
