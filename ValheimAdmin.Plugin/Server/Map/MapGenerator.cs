using BepInEx.Bootstrap;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>
    /// Draws the world map on the server from the world generator, like the game's minimap does
    /// on a client: biome and height for every pixel of a grid centred on the world origin. Going
    /// through WorldGenerator means world-generation mods (Expand World, Better Continents, ...)
    /// show up as they are. The result is cached per world seed, game version and mod list.
    /// </summary>
    public sealed class MapGenerator
    {
        public enum Status { Idle, Generating, Ready, Failed }

        private const float WaterLevel = 30f;

        private readonly string dir;
        private readonly int size;
        private readonly float pixelSize;
        private Thread thread;
        private IEnumerator<float> mainThreadSteps;
        private volatile Status status = Status.Idle;
        private volatile float progress;
        private volatile string error;
        private byte[] rgb;
        private readonly object rgbLock = new object();

        public MapGenerator(string dir, int size, float pixelSize)
        {
            this.dir = dir;
            this.size = size;
            this.pixelSize = pixelSize;
        }

        public Status State => status;
        public float Progress => progress;
        public string Error => error;
        public int Size => size;
        public float PixelSize => pixelSize;
        public string MapFile => Path.Combine(dir, "map.png");
        private string RgbFile => Path.Combine(dir, "map.rgb");
        private string MetaFile => Path.Combine(dir, "map.json");
        public long Version { get; private set; }

        /// <summary>Identifies what the picture depends on: seed, grid, game version and the loaded mods.</summary>
        private string CacheKey()
        {
            string mods = string.Join(",", Chainloader.PluginInfos.Keys.OrderBy(k => k, StringComparer.Ordinal));
            return ZNet.World.m_seed + "|" + size + "|" + pixelSize + "|" + global::Version.GetVersionString() + "|" + mods.GetStableHashCode();
        }

        /// <summary>Uses the cached map when it still matches; otherwise draws a new one.</summary>
        public void Start(bool force, bool background)
        {
            if (status == Status.Generating) return;
            Directory.CreateDirectory(dir);
            string key = CacheKey();
            if (!force && File.Exists(MapFile) && File.Exists(MetaFile))
            {
                try
                {
                    var meta = Json.ParseObject(File.ReadAllText(MetaFile));
                    if (meta.Str("key") == key && File.Exists(RgbFile))
                    {
                        Version = meta.Long("generatedAt");
                        status = Status.Ready;
                        return;
                    }
                }
                catch (Exception e)
                {
                    BepInExPlugin.Dbgl("Map cache unreadable: " + e.Message);
                }
            }

            status = Status.Generating;
            progress = 0;
            error = null;
            BepInExPlugin.Log("Drawing the world map (" + size + "x" + size + ", " + pixelSize + " m per pixel)" + (background ? "" : " on the main thread"));
            if (background)
            {
                thread = new Thread(() =>
                {
                    try
                    {
                        var steps = Generate(key);
                        while (steps.MoveNext()) { }
                    }
                    catch (Exception e)
                    {
                        Fail(e);
                    }
                }) { IsBackground = true, Name = "ValheimAdmin.Map", Priority = System.Threading.ThreadPriority.BelowNormal };
                thread.Start();
            }
            else
                mainThreadSteps = Generate(key);
        }

        /// <summary>Main-thread mode: draws rows for a few milliseconds per frame.</summary>
        public void Update()
        {
            if (mainThreadSteps == null) return;
            var watch = Stopwatch.StartNew();
            try
            {
                while (watch.ElapsedMilliseconds < 4)
                {
                    if (!mainThreadSteps.MoveNext())
                    {
                        mainThreadSteps = null;
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                mainThreadSteps = null;
                Fail(e);
            }
        }

        private void Fail(Exception e)
        {
            error = e.Message;
            status = Status.Failed;
            BepInExPlugin.Warn("Drawing the world map failed: " + e);
        }

        private IEnumerator<float> Generate(string key)
        {
            var watch = Stopwatch.StartNew();
            int half = size / 2;
            float halfPixel = pixelSize / 2f;
            var heights = new float[size * size];
            var biomes = new Heightmap.Biome[size * size];
            var forest = new bool[size * size];
            WorldGenerator world = WorldGenerator.instance;

            for (int y = 0; y < size; y++)
            {
                float wy = (y - half) * pixelSize + halfPixel;
                for (int x = 0; x < size; x++)
                {
                    float wx = (x - half) * pixelSize + halfPixel;
                    int i = y * size + x;
                    Heightmap.Biome biome = world.GetBiome(wx, wy);
                    float height = world.GetBiomeHeight(biome, wx, wy, out _);
                    biomes[i] = biome;
                    heights[i] = height;
                    forest[i] = height >= WaterLevel && IsForest(biome, wx, wy);
                }
                progress = 0.95f * (y + 1) / size;
                yield return progress;
            }

            byte[] pixels = Shade(heights, biomes, forest);
            yield return 0.97f;
            byte[] png = Png.Encode(pixels, size, size, 2);
            WriteRgb(pixels);
            string tmp = MapFile + ".tmp";
            File.WriteAllBytes(tmp, png);
            if (File.Exists(MapFile)) File.Delete(MapFile);
            File.Move(tmp, MapFile);
            Version = DateTime.UtcNow.Ticks;
            File.WriteAllText(MetaFile, Json.Serialize(new Dictionary<string, object>
            {
                { "key", key },
                { "size", size },
                { "pixelSize", pixelSize },
                { "generatedAt", Version },
            }));
            progress = 1f;
            status = Status.Ready;
            BepInExPlugin.Log("World map drawn in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s (" + png.Length / 1024 + " KB)");
        }

        /// <summary>RGB pixels of the map (rows north first), for the public map with fog cut out. Any thread.</summary>
        public byte[] Pixels()
        {
            lock (rgbLock)
            {
                if (rgb != null || status != Status.Ready) return rgb;
                try
                {
                    using (var input = new DeflateStream(File.OpenRead(RgbFile), CompressionMode.Decompress))
                    using (var output = new MemoryStream())
                    {
                        input.CopyTo(output);
                        byte[] data = output.ToArray();
                        if (data.Length == size * size * 3) rgb = data;
                    }
                }
                catch (Exception e)
                {
                    BepInExPlugin.Warn("Reading the map pixels failed: " + e.Message);
                }
                return rgb;
            }
        }

        private void WriteRgb(byte[] pixels)
        {
            lock (rgbLock)
            {
                using (var output = File.Create(RgbFile))
                using (var deflate = new DeflateStream(output, CompressionMode.Compress))
                    deflate.Write(pixels, 0, pixels.Length);
                rgb = pixels;
            }
        }

        private static bool IsForest(Heightmap.Biome biome, float wx, float wy)
        {
            var pos = new Vector3(wx, 0f, wy);
            switch (biome)
            {
                case Heightmap.Biome.Meadows: return WorldGenerator.InForest(pos);
                case Heightmap.Biome.Plains: return WorldGenerator.GetForestFactor(pos) < 0.8f;
                case Heightmap.Biome.BlackForest: return true;
                case Heightmap.Biome.Mistlands: return WorldGenerator.GetForestFactor(pos) < 1.1f;
                default: return false;
            }
        }

        /// <summary>Biome colours, depth-tinted water and hill shading lit from the north-west. Rows are written north first.</summary>
        private byte[] Shade(float[] heights, Heightmap.Biome[] biomes, bool[] forest)
        {
            var rgb = new byte[size * size * 3];
            var light = new Vector3(-1f, 1.6f, 1f).normalized;
            for (int y = 0; y < size; y++)
            {
                int row = size - 1 - y;
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    float h = heights[i];
                    Color c;
                    if (h < WaterLevel)
                    {
                        float depth = Mathf.Clamp01((WaterLevel - h) / 30f);
                        c = Color.Lerp(new Color(0.42f, 0.60f, 0.72f), new Color(0.11f, 0.22f, 0.36f), depth);
                    }
                    else
                    {
                        c = BiomeColor(biomes[i]);
                        if (forest[i]) c *= 0.8f;
                        float dx = heights[y * size + Math.Min(x + 1, size - 1)] - heights[y * size + Math.Max(x - 1, 0)];
                        float dz = heights[Math.Min(y + 1, size - 1) * size + x] - heights[Math.Max(y - 1, 0) * size + x];
                        var normal = new Vector3(-dx, 2f * pixelSize, -dz).normalized;
                        float shade = Mathf.Clamp(0.45f + 0.75f * Vector3.Dot(normal, light), 0.35f, 1.25f);
                        c *= shade;
                    }
                    int o = (row * size + x) * 3;
                    rgb[o] = (byte)(Mathf.Clamp01(c.r) * 255f);
                    rgb[o + 1] = (byte)(Mathf.Clamp01(c.g) * 255f);
                    rgb[o + 2] = (byte)(Mathf.Clamp01(c.b) * 255f);
                }
            }
            return rgb;
        }

        private static Color BiomeColor(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows: return new Color(0.573f, 0.655f, 0.361f);
                case Heightmap.Biome.BlackForest: return new Color(0.420f, 0.455f, 0.247f);
                case Heightmap.Biome.Swamp: return new Color(0.639f, 0.447f, 0.345f);
                case Heightmap.Biome.Mountain: return new Color(0.92f, 0.93f, 0.95f);
                case Heightmap.Biome.Plains: return new Color(0.906f, 0.671f, 0.470f);
                case Heightmap.Biome.Mistlands: return new Color(0.38f, 0.37f, 0.42f);
                case Heightmap.Biome.AshLands: return new Color(0.690f, 0.192f, 0.192f);
                case Heightmap.Biome.DeepNorth: return new Color(0.85f, 0.90f, 1.0f);
                case Heightmap.Biome.Ocean: return new Color(0.42f, 0.60f, 0.72f);
                default: return new Color(0.55f, 0.58f, 0.45f); // biomes added by mods
            }
        }
    }
}
