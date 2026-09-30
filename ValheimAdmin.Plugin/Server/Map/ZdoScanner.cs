using System.Collections.Generic;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>
    /// Finds the world objects the map shows (tombstones, mob spawners, cartography tables) in one
    /// pass over the ZDO sectors every 30 s, a slice of sectors per frame like the game's own
    /// <c>GetAllZDOsWithPrefabIterative</c>. Spawners and tables are found by component, so modded
    /// ones are included. Only zones that have been generated have objects.
    /// </summary>
    public sealed class ZdoScanner
    {
        private const float Rescan = 30f;
        private const int SectorsPerFrame = 400;

        public enum Kind { Tombstone, Spawner, MapTable }

        private Dictionary<int, Kind> kinds;
        private readonly Dictionary<int, string> names = new Dictionary<int, string>();
        private List<ZDO>[] scanning;
        private int index;
        private float timer = Rescan;

        public List<ZDO> Tombstones { get; private set; } = new List<ZDO>();
        public List<ZDO> Spawners { get; private set; } = new List<ZDO>();
        public List<ZDO> MapTables { get; private set; } = new List<ZDO>();

        /// <summary>Scans a slice; true when a pass has just finished and the lists were replaced.</summary>
        public bool Update(float dt)
        {
            if (scanning == null)
            {
                timer += dt;
                if (timer < Rescan || ZNetScene.instance == null) return false;
                timer = 0;
                if (kinds == null) FindPrefabs();
                scanning = new[] { new List<ZDO>(), new List<ZDO>(), new List<ZDO>() };
                index = 0;
            }

            List<ZDO>[] sectors = ZDOMan.instance.m_objectsBySector;
            int visited = 0;
            while (index < sectors.Length && visited < SectorsPerFrame)
            {
                List<ZDO> sector = sectors[index++];
                if (sector == null) continue;
                visited++;
                foreach (ZDO zdo in sector)
                    if (kinds.TryGetValue(zdo.GetPrefab(), out Kind kind))
                        scanning[(int)kind].Add(zdo);
            }
            if (index < sectors.Length) return false;

            Tombstones = scanning[(int)Kind.Tombstone];
            Spawners = scanning[(int)Kind.Spawner];
            MapTables = scanning[(int)Kind.MapTable];
            scanning = null;
            return true;
        }

        public string PrefabName(ZDO zdo) => names.TryGetValue(zdo.GetPrefab(), out string name) ? name : "";

        /// <summary>The game reuses the ZDOs of destroyed objects, so a found one is checked again before use.</summary>
        public bool StillIs(ZDO zdo, Kind kind) =>
            zdo.IsValid() && kinds != null && kinds.TryGetValue(zdo.GetPrefab(), out Kind k) && k == kind;

        private void FindPrefabs()
        {
            kinds = new Dictionary<int, Kind> { { "Player_tombstone".GetStableHashCode(), Kind.Tombstone } };
            foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null) continue;
                Kind kind;
                if (prefab.GetComponent<SpawnArea>() != null) kind = Kind.Spawner;
                else if (prefab.GetComponent<MapTable>() != null) kind = Kind.MapTable;
                else continue;
                int hash = prefab.name.GetStableHashCode();
                kinds[hash] = kind;
                names[hash] = prefab.name;
            }
            BepInExPlugin.Dbgl("Map: " + kinds.Count + " prefabs to look for (spawners, cartography tables, tombstones)");
        }
    }
}
