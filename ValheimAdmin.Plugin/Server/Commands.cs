using BepInEx.Bootstrap;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>Receives a command's answer: (ok, data, error). May be called later, from an RPC reply.</summary>
    public delegate void Responder(bool ok, object data, string error);

    /// <summary>
    /// Commands for the server process, from the agent (AgentLink) or the plugin's own web server
    /// (standalone mode). Runs on the Unity main thread.
    /// </summary>
    public static class Commands
    {
        public static void Handle(Responder reply, string cmd, Dictionary<string, object> args)
        {
            try
            {
                if (cmd != "status" && !ServerRole.IsServer)
                {
                    reply(false, null, "The world is not loaded yet");
                    return;
                }
                Run(reply, cmd, args);
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Command " + cmd + " failed: " + e);
                reply(false, null, e.Message);
            }
        }

        private static void Ok(Responder reply, object data = null) => reply(true, data, null);

        private static void Fail(Responder reply, string error) => reply(false, null, error);

        private static void Run(Responder reply, string cmd, Dictionary<string, object> args)
        {
            switch (cmd)
            {
                case "status":
                    Ok(reply, ServerRole.Stats());
                    return;

                case "players":
                    Ok(reply, Players());
                    return;

                case "save":
                    // Returns once the save is under way; the "save" event confirms it.
                    ZNet.instance.Save(false, true, false);
                    Ok(reply, new Dictionary<string, object> { { "saving", true } });
                    return;

                case "shutdown":
                    Shutdown(reply, args.Str("message"));
                    return;

                case "kick":
                    ZNet.instance.Kick(Required(args, "player"));
                    Ok(reply);
                    return;

                case "ban":
                    ZNet.instance.Ban(Required(args, "player"));
                    Ok(reply);
                    return;

                case "unban":
                    ZNet.instance.Unban(Required(args, "player"));
                    Ok(reply);
                    return;

                case "list":
                    Ok(reply, ListOf(Required(args, "list")).GetList());
                    return;

                case "list_add":
                    ListOf(Required(args, "list")).Add(Required(args, "value"));
                    Ok(reply);
                    return;

                case "list_remove":
                    ListOf(Required(args, "list")).Remove(Required(args, "value"));
                    Ok(reply);
                    return;

                case "broadcast":
                    Broadcast(Required(args, "text"), args.Bool("center", true));
                    Ok(reply, new Dictionary<string, object> { { "recipients", ZNet.instance.GetNrOfPlayers() } });
                    return;

                case "exec":
                    Exec(reply, Required(args, "line"));
                    return;

                case "commands":
                    Ok(reply, ConsoleRunner.List());
                    return;

                case "keys":
                    Ok(reply, ZoneSystem.instance.GetGlobalKeys());
                    return;

                case "key_set":
                    ZoneSystem.instance.SetGlobalKey(Required(args, "key"));
                    Ok(reply);
                    return;

                case "key_remove":
                    ZoneSystem.instance.RemoveGlobalKey(Required(args, "key"));
                    Ok(reply);
                    return;

                case "sleep":
                    EnvMan.instance.SkipToMorning();
                    Ok(reply);
                    return;

                case "events":
                    Ok(reply, RandEventSystem.instance.m_events.Select(e => e.m_name).ToList());
                    return;

                case "event_start":
                    StartEvent(reply, args);
                    return;

                case "event_stop":
                    RandEventSystem.instance.ResetRandomEvent();
                    Ok(reply);
                    return;

                case "items":
                    Ok(reply, ObjectDB.instance.m_items
                        .Where(go => go != null && go.GetComponent<ItemDrop>() is ItemDrop d &&
                                     d.m_itemData.m_shared.m_icons != null && d.m_itemData.m_shared.m_icons.Length > 0)
                        .Select(go => new Dictionary<string, object>
                        {
                            { "prefab", go.name },
                            { "token", go.GetComponent<ItemDrop>().m_itemData.m_shared.m_name },
                            { "type", go.GetComponent<ItemDrop>().m_itemData.m_shared.m_itemType.ToString() },
                        }).ToList());
                    return;

                case "plugins":
                    Ok(reply, Chainloader.PluginInfos.Values.Select(p => new Dictionary<string, object>
                    {
                        { "guid", p.Metadata.GUID },
                        { "name", p.Metadata.Name },
                        { "version", p.Metadata.Version.ToString() },
                        { "file", System.IO.Path.GetFileName(p.Location) },
                    }).ToList());
                    return;

                case "cheat":
                    PlayerCommand(reply, args);
                    return;

                case "give":
                    Give(reply, args);
                    return;

                case "snapshot":
                    Snapshot(reply, args);
                    return;

                case "icons":
                    RenderIcons(reply, args);
                    return;

                case "restore":
                    if (!args.ContainsKey("payload")) throw new ArgumentException("payload is required");
                    ForwardToClient(reply, args, Rpc.Restore, 30f, Rpc.Pack(Json.Serialize(args["payload"])));
                    return;

                default:
                    if (MapService.Handle(cmd, args, out object mapResult))
                        Ok(reply, mapResult);
                    else
                        Fail(reply, "Unknown command: " + cmd);
                    return;
            }
        }

        private static string Required(Dictionary<string, object> args, string key)
        {
            string value = args.Str(key);
            if (string.IsNullOrEmpty(value)) throw new ArgumentException(key + " is required");
            return value;
        }

        private static SyncedList ListOf(string name)
        {
            switch (name)
            {
                case "admin": return ZNet.instance.m_adminList;
                case "banned": return ZNet.instance.m_bannedList;
                case "permitted": return ZNet.instance.m_permittedList;
                default: throw new ArgumentException("Unknown list: " + name);
            }
        }

        private static List<object> Players()
        {
            var result = new List<object>();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady()) continue;
                int ping = -1;
                try
                {
                    peer.m_socket.GetConnectionQuality(out _, out _, out ping, out _, out _);
                }
                catch
                {
                }
                var info = Hooks.PeerInfo(peer);
                info["ping"] = ping;
                info["pos"] = new[] { Math.Round(peer.m_refPos.x), Math.Round(peer.m_refPos.y), Math.Round(peer.m_refPos.z) };
                info["admin"] = ZNet.instance.IsAdmin(Hooks.HostOf(peer));
                if (ServerRole.Modded.TryGetValue(peer.m_uid, out var mod))
                {
                    info["mod"] = mod.Version;
                    info["characterId"] = mod.CharacterId;
                }
                result.Add(info);
            }
            return result;
        }

        /// <summary>Takes a last snapshot of everyone, then saves synchronously and quits.</summary>
        private static void Shutdown(Responder reply, string message)
        {
            Ok(reply);
            if (!string.IsNullOrEmpty(message))
                Broadcast(message, true);
            ServerRole.SnapshotAll("shutdown", () =>
            {
                ServerRole.SuppressSaveSnapshots = true;
                AgentLink.Event("stopping", new Dictionary<string, object>());
                ZNet.instance.Save(true, true, false);
                Application.Quit();
            });
        }

        public static void Broadcast(string text, bool center)
        {
            if (center)
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", (int)MessageHud.MessageType.Center, text);
            foreach (long uid in ServerRole.Modded.Keys.ToList())
                ZRoutedRpc.instance.InvokeRoutedRPC(uid, Rpc.Chat, text);
        }

        private static void StartEvent(Responder reply, Dictionary<string, object> args)
        {
            string name = Required(args, "name");
            ZNetPeer peer = ServerRole.FindPeer(Required(args, "player"));
            if (peer == null)
            {
                Fail(reply, "Player is not online");
                return;
            }
            if (!RandEventSystem.instance.m_events.Any(e => e.m_name == name))
            {
                Fail(reply, "Unknown event: " + name);
                return;
            }
            RandEventSystem.instance.SetRandomEventByName(name, peer.m_refPos);
            Ok(reply);
        }

        private static void ForwardToClient(Responder reply, Dictionary<string, object> args, string rpc, float timeout, params object[] rpcArgs)
        {
            ZNetPeer peer = ServerRole.FindPeer(Required(args, "player"));
            if (peer == null)
            {
                Fail(reply, "Player is not online");
                return;
            }
            if (!ServerRole.Modded.ContainsKey(peer.m_uid))
            {
                Fail(reply, "Player does not have the Valheim Admin mod");
                return;
            }
            ServerRole.SendToClient(peer.m_uid, rpc, timeout, (ok, json) =>
            {
                if (ok)
                    Ok(reply, new Json.Raw(json));
                else
                    Fail(reply, json);
            }, rpcArgs);
        }

        /// <summary>Runs a game console command (vanilla or modded) on the dedicated server itself.</summary>
        private static void Exec(Responder reply, string line, bool ranForPlayer = false)
        {
            if (Console.instance == null)
            {
                Fail(reply, "The game console is not available on this server");
                return;
            }
            Terminal.ConsoleCommand cmd = ConsoleRunner.Find(line);
            if (cmd == null)
            {
                Fail(reply, "Unknown game command: " + ConsoleRunner.FirstWord(line));
                return;
            }
            BepInExPlugin.Log("Admin command on the server: " + line);
            var result = ConsoleRunner.Run(Console.instance, line, allowCheatMark: true);
            var data = new Dictionary<string, object> { { "output", result.Output } };
            if (ranForPlayer) data["ranOnServer"] = true;
            Ok(reply, data);
        }

        /// <summary>
        /// "@Player command": runs on the player's game. Commands that only the server can run come
        /// back as {runOnServer} and are run here instead.
        /// </summary>
        private static void PlayerCommand(Responder reply, Dictionary<string, object> args)
        {
            string line = Required(args, "command");
            ZNetPeer peer = ServerRole.FindPeer(Required(args, "player"));
            if (peer == null)
            {
                Fail(reply, "Player is not online");
                return;
            }
            if (!ServerRole.Modded.TryGetValue(peer.m_uid, out var mod))
            {
                Fail(reply, "Player does not have the Valheim Admin mod, so commands can't run on their game. Server commands work without it.");
                return;
            }

            Action<bool, string> done = (ok, json) =>
            {
                if (!ok)
                {
                    Fail(reply, json);
                    return;
                }
                Dictionary<string, object> answer = null;
                try
                {
                    answer = Json.ParseObject(json);
                }
                catch
                {
                }
                if (answer == null || !answer.Bool("runOnServer"))
                {
                    Ok(reply, new Json.Raw(json));
                    return;
                }
                try
                {
                    Exec(reply, line, ranForPlayer: true);
                }
                catch (Exception e)
                {
                    BepInExPlugin.Warn("Command " + line + " failed: " + e);
                    Fail(reply, e.Message);
                }
            };

            if (ServerRole.AtLeast(mod, 0, 3))
            {
                string request = Json.Serialize(new Dictionary<string, object> { { "line", line }, { "confirmCheats", args.Bool("confirmCheats") } });
                ServerRole.SendToClient(peer.m_uid, Rpc.Run, 20f, done, Rpc.Pack(request));
            }
            else
                ServerRole.SendToClient(peer.m_uid, Rpc.Command, 20f, done, line);
        }

        /// <summary>Item icons rendered by a player's game: the named player, or any online player with a 0.3+ client role.</summary>
        private static void RenderIcons(Responder reply, Dictionary<string, object> args)
        {
            ZNetPeer peer = null;
            string player = args.Str("player");
            if (!string.IsNullOrEmpty(player))
                peer = ServerRole.FindPeer(player);
            else
                foreach (ZNetPeer p in ZNet.instance.GetPeers())
                    if (p.IsReady() && ServerRole.Modded.TryGetValue(p.m_uid, out var m) && ServerRole.AtLeast(m, 0, 3))
                    {
                        peer = p;
                        break;
                    }
            if (peer == null || !ServerRole.Modded.TryGetValue(peer.m_uid, out var mod) || !ServerRole.AtLeast(mod, 0, 3))
            {
                Fail(reply, "No online player with the Valheim Admin mod 0.3+ to render icons");
                return;
            }
            string request = Json.Serialize(new Dictionary<string, object> { { "items", args.List("items") ?? new List<object>() } });
            ServerRole.SendToClient(peer.m_uid, Rpc.Icons, 30f, (ok, json) =>
            {
                if (ok) Ok(reply, new Json.Raw(json));
                else Fail(reply, json);
            }, Rpc.Pack(request));
        }

        private static void Give(Responder reply, Dictionary<string, object> args)
        {
            string prefab = Required(args, "prefab");
            int count = Math.Max(1, args.Int("count", 1));
            int quality = Math.Max(1, args.Int("quality", 1));
            GameObject go = ObjectDB.instance.GetItemPrefab(prefab);
            if (go == null || go.GetComponent<ItemDrop>() == null)
            {
                Fail(reply, "Unknown item: " + prefab);
                return;
            }

            ZNetPeer peer = ServerRole.FindPeer(Required(args, "player"));
            if (peer == null)
            {
                Fail(reply, "Player is not online");
                return;
            }

            if (ServerRole.Modded.ContainsKey(peer.m_uid))
            {
                ForwardToClient(reply, args, Rpc.Give, 20f, prefab, count, quality);
                return;
            }

            // No client role: drop the items at the player's feet.
            int maxStack = Math.Max(1, go.GetComponent<ItemDrop>().m_itemData.m_shared.m_maxStackSize);
            int left = count;
            while (left > 0)
            {
                int stack = Math.Min(left, maxStack);
                left -= stack;
                Vector3 pos = peer.m_refPos + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.5f;
                var drop = UnityEngine.Object.Instantiate(go, pos, Quaternion.identity).GetComponent<ItemDrop>();
                drop.m_itemData.m_stack = stack;
                drop.m_itemData.m_quality = quality;
                drop.m_itemData.m_durability = drop.m_itemData.GetMaxDurability();
                drop.Save();
            }
            Ok(reply, new Dictionary<string, object> { { "dropped", count } });
        }

        private static void Snapshot(Responder reply, Dictionary<string, object> args)
        {
            string trigger = args.Str("trigger", "manual");
            string player = args.Str("player");
            if (string.IsNullOrEmpty(player))
            {
                Ok(reply, new Dictionary<string, object> { { "requested", ServerRole.SnapshotAll(trigger) } });
                return;
            }

            ZNetPeer peer = ServerRole.FindPeer(player);
            if (peer == null)
            {
                Fail(reply, "Player is not online");
                return;
            }
            if (!ServerRole.Modded.ContainsKey(peer.m_uid))
            {
                Fail(reply, "Player does not have the Valheim Admin mod");
                return;
            }
            ServerRole.RequestSnapshot(peer, trigger, (ok, json) =>
            {
                if (ok)
                    Ok(reply, new Json.Raw(json));
                else
                    Fail(reply, json);
            });
        }
    }
}
