using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>Things drawn on the map: players, portals, location icons, tombstones and the admin's pins.</summary>
    public sealed class MapMarkers
    {
        private const float TombstoneRescan = 30f;

        private readonly string pinsFile;
        private readonly List<Dictionary<string, object>> pins = new List<Dictionary<string, object>>();
        private List<ZDO> tombstonesFound = new List<ZDO>();
        private List<ZDO> tombstonesScanning;
        private int scanIndex;
        private float scanTimer = TombstoneRescan;

        public MapMarkers(string pinsFile)
        {
            this.pinsFile = pinsFile;
            LoadPins();
        }

        /// <summary>The character's position when known, else the position the client reports.</summary>
        public static Vector3 PositionOf(ZNetPeer peer)
        {
            ZDO zdo = peer.m_characterID.IsNone() ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
            return zdo != null ? zdo.GetPosition() : peer.m_refPos;
        }

        /// <summary>Spreads the tombstone scan over frames; the game's iterator walks a slice of sectors per call.</summary>
        public void Update(float dt)
        {
            if (tombstonesScanning == null)
            {
                scanTimer += dt;
                if (scanTimer < TombstoneRescan) return;
                scanTimer = 0;
                tombstonesScanning = new List<ZDO>();
                scanIndex = 0;
            }
            if (ZDOMan.instance.GetAllZDOsWithPrefabIterative("Player_tombstone", tombstonesScanning, ref scanIndex))
            {
                tombstonesFound = tombstonesScanning;
                tombstonesScanning = null;
            }
        }

        public Dictionary<string, object> Build(bool admin, FogTracker fog)
        {
            var result = new Dictionary<string, object>();
            string playersMode = admin ? "all" : BepInExPlugin.MapPublicPlayers.Value;
            bool fogged = !admin && BepInExPlugin.MapPublicFog.Value && fog != null;
            Func<Vector3, bool> visible = p => !fogged || fog.IsExplored(p.x, p.z);

            var players = new List<object>();
            if (playersMode != "none")
            {
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                {
                    if (!peer.IsReady()) continue;
                    // "Visible on map" in the game's settings; respected unless the admin asks.
                    if (playersMode != "all" && !peer.m_publicRefPos) continue;
                    Vector3 p = PositionOf(peer);
                    var entry = new Dictionary<string, object>
                    {
                        { "name", peer.m_playerName },
                        { "x", Math.Round(p.x, 1) },
                        { "z", Math.Round(p.z, 1) },
                    };
                    if (admin)
                    {
                        entry["uid"] = peer.m_uid;
                        entry["public"] = peer.m_publicRefPos;
                    }
                    players.Add(entry);
                }
            }
            result["players"] = players;

            if (admin || BepInExPlugin.MapPublicPortals.Value)
                result["portals"] = Portals().Where(p => visible(p.Key)).Select(p => Point(p.Key, "tag", p.Value)).ToList();

            if (admin || BepInExPlugin.MapPublicLocations.Value)
            {
                var icons = new Dictionary<Vector3, string>();
                ZoneSystem.instance.GetLocationIcons(icons);
                result["locations"] = icons.Where(kv => visible(kv.Key)).Select(kv => Point(kv.Key, "name", kv.Value)).ToList();
            }

            if (admin)
                result["tombstones"] = tombstonesFound.Where(z => z.IsValid())
                    .Select(z => Point(z.GetPosition(), "owner", z.GetString(ZDOVars.s_ownerName, ""))).ToList();

            result["pins"] = pins.Where(p => admin || p.Bool("public")).Select(p => (object)p).ToList();
            return result;
        }

        private static object Point(Vector3 p, string key, string value) => new Dictionary<string, object>
        {
            { "x", Math.Round(p.x, 1) },
            { "z", Math.Round(p.z, 1) },
            { key, value },
        };

        /// <summary>Every portal the world has, vanilla or modded: the game keeps them in their own list.</summary>
        private static IEnumerable<KeyValuePair<Vector3, string>> Portals()
        {
            foreach (List<ZDO> sector in ZDOMan.instance.m_portalObjects.Values)
                foreach (ZDO zdo in sector)
                    if (zdo.IsValid())
                        yield return new KeyValuePair<Vector3, string>(zdo.GetPosition(), zdo.GetString(ZDOVars.s_tag, ""));
        }

        public Dictionary<string, object> AddPin(Dictionary<string, object> args)
        {
            var pin = new Dictionary<string, object>
            {
                { "id", Guid.NewGuid().ToString("N").Substring(0, 12) },
                { "x", Math.Round(args.Double("x"), 1) },
                { "z", Math.Round(args.Double("z"), 1) },
                { "label", (args.Str("label", "") ?? "").Trim() },
                { "icon", args.Str("icon", "pin") },
                { "public", args.Bool("public") },
            };
            pins.Add(pin);
            SavePins();
            return pin;
        }

        public bool RemovePin(string id)
        {
            int removed = pins.RemoveAll(p => p.Str("id") == id);
            if (removed > 0) SavePins();
            return removed > 0;
        }

        private void LoadPins()
        {
            if (!File.Exists(pinsFile)) return;
            try
            {
                if (Json.Parse(File.ReadAllText(pinsFile)) is List<object> list)
                    pins.AddRange(list.OfType<Dictionary<string, object>>());
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Loading map pins failed: " + e.Message);
            }
        }

        private void SavePins()
        {
            try
            {
                File.WriteAllText(pinsFile, Json.Serialize(pins));
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Saving map pins failed: " + e.Message);
            }
        }
    }
}
