using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>
    /// Things drawn on the map that change during play: players, portals, tombstones, mob spawners,
    /// pins from the game's map and the admin's own pins. World locations are in <see cref="MapLocations"/>.
    /// </summary>
    public sealed class MapMarkers
    {
        private readonly string pinsFile;
        private readonly List<Dictionary<string, object>> pins = new List<Dictionary<string, object>>();
        private readonly ZdoScanner scanner;
        private readonly GamePins gamePins;

        public MapMarkers(string pinsFile, ZdoScanner scanner, GamePins gamePins)
        {
            this.pinsFile = pinsFile;
            this.scanner = scanner;
            this.gamePins = gamePins;
            LoadPins();
        }

        /// <summary>The character's position when known, else the position the client reports.</summary>
        public static Vector3 PositionOf(ZNetPeer peer)
        {
            ZDO zdo = peer.m_characterID.IsNone() ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
            return zdo != null ? zdo.GetPosition() : peer.m_refPos;
        }

        /// <summary>
        /// Markers for the admin or the public. Pins from the game's map change rarely and can be
        /// many, so the admin gets them only when asking (withPins); the public gets table pins
        /// whenever they are public.
        /// </summary>
        public Dictionary<string, object> Build(bool admin, FogTracker fog, bool withPins)
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

            if (admin)
                result["tombstones"] = scanner.Tombstones.Where(z => scanner.StillIs(z, ZdoScanner.Kind.Tombstone))
                    .Select(z => Point(z.GetPosition(), "owner", z.GetString(ZDOVars.s_ownerName, ""))).ToList();

            if (admin || BepInExPlugin.MapPublicSpawners.Value)
                result["spawners"] = scanner.Spawners.Where(z => scanner.StillIs(z, ZdoScanner.Kind.Spawner))
                    .Select(z => new KeyValuePair<Vector3, string>(z.GetPosition(), scanner.PrefabName(z)))
                    .Where(p => visible(p.Key)).Select(p => Point(p.Key, "prefab", p.Value)).ToList();

            if (admin ? withPins : BepInExPlugin.MapPublicGamePins.Value)
                result["gamePins"] = gamePins.TablePins(visible);

            if (admin && withPins)
                result["playerPins"] = gamePins.PlayerPins();

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
