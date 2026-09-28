using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>
    /// The server's world map: picture, explored areas and markers, for the agent's panel or the
    /// plugin's own web server. Files live under BepInEx/config/ValheimAdmin/map/&lt;world&gt;-&lt;seed&gt;/.
    /// </summary>
    public static class MapService
    {
        private static MapGenerator generator;
        private static FogTracker fog;
        private static MapMarkers markers;
        private static string worldDir;

        public static string DataRoot => Path.Combine(Paths.ConfigPath, "ValheimAdmin");

        public static bool Enabled => BepInExPlugin.MapEnabled.Value;

        /// <summary>Called every frame on the dedicated server.</summary>
        public static void Update()
        {
            if (!Enabled) return;
            if (generator == null)
            {
                if (ZNet.World == null || WorldGenerator.instance == null || ZoneSystem.instance == null || ZDOMan.instance == null) return;
                Init();
            }
            float dt = Time.unscaledDeltaTime;
            generator.Update();
            fog.Update(dt);
            markers.Update(dt);
        }

        private static void Init()
        {
            string world = new string(ZNet.World.m_name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            worldDir = Path.Combine(Path.Combine(DataRoot, "map"), world + "-" + ZNet.World.m_seed);
            Directory.CreateDirectory(worldDir);
            int size = Mathf.Clamp(Mathf.ClosestPowerOfTwo(BepInExPlugin.MapTextureSize.Value), 512, 8192);
            float pixel = Mathf.Clamp(BepInExPlugin.MapPixelSize.Value, 2f, 64f);
            generator = new MapGenerator(worldDir, size, pixel);
            fog = new FogTracker(Path.Combine(worldDir, "fog.bin"), size, pixel);
            markers = new MapMarkers(Path.Combine(worldDir, "pins.json"));
            generator.Start(false, BepInExPlugin.MapDrawInBackground.Value);
        }

        /// <summary>Called when the world is saved.</summary>
        public static void Save()
        {
            fog?.Save();
        }

        public static Dictionary<string, object> Info()
        {
            if (!Enabled) return new Dictionary<string, object> { { "enabled", false } };
            if (generator == null) return new Dictionary<string, object> { { "enabled", true }, { "state", "waiting" } };
            fog.RefreshPngs(generator);
            return new Dictionary<string, object>
            {
                { "enabled", true },
                { "state", generator.State.ToString().ToLowerInvariant() },
                { "progress", Math.Round(generator.Progress, 3) },
                { "error", generator.Error },
                { "world", ZNet.World?.m_name },
                { "size", generator.Size },
                { "pixelSize", generator.PixelSize },
                { "mapFile", generator.State == MapGenerator.Status.Ready ? generator.MapFile : null },
                { "mapVersion", generator.Version },
                { "fogFile", File.Exists(fog.PngFile) ? fog.PngFile : null },
                { "publicMapFile", File.Exists(fog.PublicMapFile) ? fog.PublicMapFile : null },
                { "fogVersion", fog.PngVersion },
                { "publicFog", BepInExPlugin.MapPublicFog.Value },
            };
        }

        /// <summary>Handles the map_* commands; returns false for other commands.</summary>
        public static bool Handle(string cmd, Dictionary<string, object> args, out object result)
        {
            result = null;
            if (!cmd.StartsWith("map_", StringComparison.Ordinal)) return false;
            if (cmd == "map_info")
            {
                result = Info();
                return true;
            }
            if (!Enabled) throw new InvalidOperationException("The map is turned off in the Valheim Admin config ([Map] Enabled)");
            if (generator == null) throw new InvalidOperationException("The world is not loaded yet");
            switch (cmd)
            {
                case "map_markers":
                    result = markers.Build(args.Bool("admin"), fog);
                    return true;
                case "map_regen":
                    generator.Start(true, BepInExPlugin.MapDrawInBackground.Value);
                    result = Info();
                    return true;
                case "map_pin_add":
                    result = markers.AddPin(args);
                    return true;
                case "map_pin_remove":
                    if (!markers.RemovePin(args.Str("id"))) throw new ArgumentException("No such pin");
                    return true;
                default:
                    throw new ArgumentException("Unknown map command: " + cmd);
            }
        }
    }
}
