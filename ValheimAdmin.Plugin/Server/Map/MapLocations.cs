using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>
    /// Every location of the world (boss altars, dungeons, traders, runestones, camps, ...), as the
    /// world generator placed them when the world was created: <c>ZoneSystem.m_locationInstances</c>.
    /// Locations added by mods are included. "placed" = its zone has been generated, so it exists.
    /// </summary>
    public static class MapLocations
    {
        /// <summary>Changes when locations are added or their zones get generated.</summary>
        public static long Version()
        {
            var instances = ZoneSystem.instance?.m_locationInstances;
            if (instances == null) return 0;
            long placed = 0;
            foreach (ZoneSystem.LocationInstance l in instances.Values)
                if (l.m_placed) placed++;
            return instances.Count * 1000003L + placed;
        }

        /// <summary>{version, defeated: [global keys], types: {prefab: [[x, z, placed], ...]}}.</summary>
        public static Dictionary<string, object> Build(bool admin, FogTracker fog)
        {
            var result = new Dictionary<string, object> { { "version", Version() } };
            if (!admin && !BepInExPlugin.MapPublicLocations.Value)
            {
                result["types"] = new Dictionary<string, object>();
                return result;
            }
            bool fogged = !admin && BepInExPlugin.MapPublicFog.Value && fog != null;

            var types = new Dictionary<string, object>();
            foreach (ZoneSystem.LocationInstance l in ZoneSystem.instance.m_locationInstances.Values)
            {
                string prefab = l.m_location?.m_prefabName;
                if (string.IsNullOrEmpty(prefab)) continue;
                Vector3 p = l.m_position;
                if (fogged && !fog.IsExplored(p.x, p.z)) continue;
                if (!types.TryGetValue(prefab, out object list)) types[prefab] = list = new List<object>();
                ((List<object>)list).Add(new object[] { Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.z), l.m_placed ? 1 : 0 });
            }
            result["types"] = types;
            // Boss altars of beaten bosses are shown as such.
            result["defeated"] = ZoneSystem.instance.GetGlobalKeys()
                .Where(k => k.StartsWith("defeated_", StringComparison.OrdinalIgnoreCase)).Cast<object>().ToList();
            return result;
        }
    }
}
