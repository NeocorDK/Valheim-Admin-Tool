using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using ValheimAdmin.Shared;

namespace ValheimAdmin
{
    /// <summary>
    /// Pins from the game's own map. Pins players wrote to a cartography table live in the table's
    /// ZDO, so the server reads them itself (and adds the table's explored area to the fog). Personal
    /// pins stay on the players' machines: clients with the mod are asked for them every minute
    /// (unless the player turned <c>[Client] SharePins</c> off) and the last answer is kept in
    /// player-pins.json, so pins of players who left stay visible to the admin.
    /// </summary>
    public sealed class GamePins
    {
        private const float PollInterval = 60f;
        /// <summary>Metres per cell of the game's minimap (Minimap.m_pixelSize); the table's data carries only the grid size.</summary>
        private const float GamePixelSize = 12f;
        private const int MaxPinsPerPlayer = 2000;

        private sealed class Table
        {
            public uint Revision;
            public List<SharedMapData.Pin> Pins = new List<SharedMapData.Pin>();
        }

        private readonly string file;
        private readonly FogTracker fog;
        private readonly Dictionary<ZDOID, Table> tables = new Dictionary<ZDOID, Table>();
        private List<SharedMapData.Pin> tablePins = new List<SharedMapData.Pin>();
        /// <summary>characterId → {characterId, player, updated, pins}</summary>
        private readonly Dictionary<long, Dictionary<string, object>> players = new Dictionary<long, Dictionary<string, object>>();
        /// <summary>characterId (the game's player id) → character name, to name the owners of table pins.</summary>
        private readonly Dictionary<long, string> names = new Dictionary<long, string>();
        private float pollTimer = PollInterval - 10f;
        private bool unsaved;

        public GamePins(string file, FogTracker fog)
        {
            this.file = file;
            this.fog = fog;
            Load();
        }

        public int TableCount => tables.Count;

        /// <summary>Asks the online modded players for their pins every minute.</summary>
        public void Update(float dt)
        {
            pollTimer += dt;
            if (pollTimer < PollInterval) return;
            pollTimer = 0;
            LearnNames();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady() || !ServerRole.Modded.TryGetValue(peer.m_uid, out ServerRole.ModdedPeer modded) || !ServerRole.AtLeast(modded, 0, 4))
                    continue;
                long characterId = modded.CharacterId;
                string player = peer.m_playerName;
                ServerRole.SendToClient(peer.m_uid, Rpc.Pins, 20f, (ok, json) => OnPlayerPins(characterId, player, ok, json));
            }
        }

        /// <summary>Character ids of online players, from their character's ZDO (every player, modded or not).</summary>
        private void LearnNames()
        {
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady() || peer.m_characterID.IsNone()) continue;
                ZDO zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                long id = zdo != null ? zdo.GetLong(ZDOVars.s_playerID) : 0;
                if (id != 0) Name(id, peer.m_playerName);
            }
            foreach (ServerRole.ModdedPeer modded in ServerRole.Modded.Values)
                Name(modded.CharacterId, modded.CharacterName);
        }

        private void Name(long characterId, string name)
        {
            if (characterId == 0 || string.IsNullOrEmpty(name) || (names.TryGetValue(characterId, out string known) && known == name)) return;
            names[characterId] = name;
            unsaved = true;
        }

        /// <summary>Called when the world is saved: keeps newly learned names.</summary>
        public void SaveIfChanged()
        {
            if (unsaved) Save();
        }

        private void OnPlayerPins(long characterId, string player, bool ok, string json)
        {
            if (!ok)
            {
                BepInExPlugin.Dbgl("Map pins of " + player + ": " + json);
                return;
            }
            Dictionary<string, object> reply;
            try
            {
                reply = Json.ParseObject(json);
            }
            catch (Exception e)
            {
                BepInExPlugin.Dbgl("Map pins of " + player + ": " + e.Message);
                return;
            }
            Name(characterId, player);

            if (reply.Bool("disabled"))
            {
                if (players.Remove(characterId)) Save();
                return;
            }
            var pins = new List<object>();
            foreach (var pin in (reply.List("pins") ?? new List<object>()).OfType<Dictionary<string, object>>().Take(MaxPinsPerPlayer))
            {
                string name = pin.Str("name", "") ?? "";
                pins.Add(new Dictionary<string, object>
                {
                    { "x", Math.Round(pin.Double("x"), 1) },
                    { "z", Math.Round(pin.Double("z"), 1) },
                    { "name", name.Length > 100 ? name.Substring(0, 100) : name },
                    { "type", pin.Int("type") },
                    { "checked", pin.Bool("checked") },
                });
            }
            bool changed = !players.TryGetValue(characterId, out var old) || old.Str("player") != player ||
                Json.Serialize(old.List("pins")) != Json.Serialize(pins);
            players[characterId] = new Dictionary<string, object>
            {
                { "characterId", characterId },
                { "player", player },
                { "updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                { "pins", pins },
            };
            if (changed) Save();
        }

        /// <summary>
        /// Called after each scan with the cartography tables found. Tables whose data changed are
        /// decompressed and parsed on a thread pool thread; results come back on the main thread.
        /// </summary>
        public void UpdateTables(List<ZDO> found, ZdoScanner scanner)
        {
            var seen = new HashSet<ZDOID>();
            var changed = new List<KeyValuePair<ZDOID, byte[]>>();
            foreach (ZDO zdo in found)
            {
                if (!scanner.StillIs(zdo, ZdoScanner.Kind.MapTable)) continue;
                seen.Add(zdo.m_uid);
                if (tables.TryGetValue(zdo.m_uid, out Table table) && table.Revision == zdo.DataRevision) continue;
                if (table == null) tables[zdo.m_uid] = table = new Table();
                table.Revision = zdo.DataRevision;
                byte[] data = zdo.GetByteArray(ZDOVars.s_data);
                if (data != null) changed.Add(new KeyValuePair<ZDOID, byte[]>(zdo.m_uid, data));
                else table.Pins = new List<SharedMapData.Pin>();
            }
            bool removed = false;
            foreach (ZDOID gone in tables.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                tables.Remove(gone);
                removed = true;
            }
            if (changed.Count == 0)
            {
                if (removed) MergeTables();
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var parsed = new List<KeyValuePair<ZDOID, SharedMapData>>();
                BitArray explored = null;
                foreach (var kv in changed)
                {
                    try
                    {
                        SharedMapData map = SharedMapData.Parse(Utils.Decompress(kv.Value));
                        parsed.Add(new KeyValuePair<ZDOID, SharedMapData>(kv.Key, map));
                        if (map.Explored == null) continue;
                        BitArray bits = fog.Resample(map.Explored, map.Size, GamePixelSize);
                        explored = explored == null ? bits : explored.Or(bits);
                    }
                    catch (Exception e)
                    {
                        BepInExPlugin.Warn("Reading a cartography table failed: " + e.Message);
                    }
                }
                MainThread.Post(() =>
                {
                    foreach (var kv in parsed)
                        if (tables.TryGetValue(kv.Key, out Table table))
                            table.Pins = kv.Value.Pins;
                    if (explored != null) fog.MergeExplored(explored);
                    MergeTables();
                });
            });
        }

        /// <summary>Tables usually share most pins; one entry per position.</summary>
        private void MergeTables()
        {
            var merged = new List<SharedMapData.Pin>();
            var cells = new HashSet<long>();
            foreach (Table table in tables.Values)
                foreach (SharedMapData.Pin pin in table.Pins)
                {
                    long cell = ((long)Mathf.RoundToInt(pin.X) << 32) ^ (uint)Mathf.RoundToInt(pin.Z);
                    if (cells.Add(cell)) merged.Add(pin);
                }
            tablePins = merged;
        }

        public List<object> TablePins(Func<Vector3, bool> visible) => tablePins
            .Where(p => visible(new Vector3(p.X, p.Y, p.Z)))
            .Select(p => (object)new Dictionary<string, object>
            {
                { "x", Math.Round(p.X, 1) },
                { "z", Math.Round(p.Z, 1) },
                { "name", p.Name ?? "" },
                { "type", p.Type },
                { "checked", p.Checked },
                { "owner", p.OwnerId != 0 && names.TryGetValue(p.OwnerId, out string owner) ? owner : "" },
            }).ToList();

        /// <summary>Personal pins, admin only: [{player, characterId, updated, pins}].</summary>
        public List<object> PlayerPins() => players.Values.OrderBy(p => p.Str("player")).Select(p => (object)p).ToList();

        private void Load()
        {
            if (!File.Exists(file)) return;
            try
            {
                var data = Json.ParseObject(File.ReadAllText(file));
                foreach (var kv in data.Obj("names") ?? new Dictionary<string, object>())
                    if (long.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && kv.Value is string name)
                        names[id] = name;
                foreach (var p in (data.List("players") ?? new List<object>()).OfType<Dictionary<string, object>>())
                    players[p.Long("characterId")] = p;
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Loading player map pins failed: " + e.Message);
            }
        }

        private void Save()
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    { "names", names.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => (object)kv.Value) },
                    { "players", players.Values.Cast<object>().ToList() },
                };
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(data));
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
                unsaved = false;
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Saving player map pins failed: " + e.Message);
            }
        }
    }
}
