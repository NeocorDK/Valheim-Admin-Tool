using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Threading;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>
    /// Areas of the map any player has been near, tracked on the server (players' own explored
    /// maps stay on their machines). Used to hide unexplored land on the public map.
    /// </summary>
    public sealed class FogTracker
    {
        private const float Interval = 2f;
        private const float Radius = 100f; // the game's minimap explore radius
        private static readonly TimeSpan PngInterval = TimeSpan.FromSeconds(30);

        private readonly string file;
        private readonly int size;
        private readonly float pixelSize;
        private readonly BitArray explored;
        private float timer;
        private bool unsaved;
        private long version;
        private long pngVersion = -1;
        private int encoding;
        private DateTime lastEncode = DateTime.MinValue;

        public FogTracker(string file, int size, float pixelSize)
        {
            this.file = file;
            this.size = size;
            this.pixelSize = pixelSize;
            explored = new BitArray(size * size);
            Load();
        }

        public string PngFile => Path.ChangeExtension(file, ".png");

        /// <summary>Version of the PNG on disk; changes when newly explored areas are written out.</summary>
        public long PngVersion => Interlocked.Read(ref pngVersion);

        public void Update(float dt)
        {
            timer += dt;
            if (timer < Interval) return;
            timer = 0;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer.IsReady())
                    Explore(MapMarkers.PositionOf(peer));
        }

        public bool IsExplored(float wx, float wz)
        {
            int x = Mathf.RoundToInt(wx / pixelSize + size / 2);
            int y = Mathf.RoundToInt(wz / pixelSize + size / 2);
            return x >= 0 && y >= 0 && x < size && y < size && explored[y * size + x];
        }

        private void Explore(Vector3 p)
        {
            int r = Mathf.CeilToInt(Radius / pixelSize);
            int px = Mathf.RoundToInt(p.x / pixelSize + size / 2);
            int py = Mathf.RoundToInt(p.z / pixelSize + size / 2);
            bool changed = false;
            for (int y = py - r; y <= py + r; y++)
            {
                if (y < 0 || y >= size) continue;
                for (int x = px - r; x <= px + r; x++)
                {
                    if (x < 0 || x >= size || (x - px) * (x - px) + (y - py) * (y - py) > r * r) continue;
                    int i = y * size + x;
                    if (explored[i]) continue;
                    explored[i] = true;
                    changed = true;
                }
            }
            if (changed)
            {
                unsaved = true;
                version++;
            }
        }

        /// <summary>
        /// Writes, in the background, the fog PNG and the public map (map pixels with unexplored
        /// areas blacked out, so nothing unexplored ever leaves the server) when explored areas or
        /// the map changed; at most every 30 s. Main thread only; returns at once.
        /// </summary>
        public void RefreshPngs(MapGenerator map)
        {
            long wanted = version * 1000003 + map.Version;
            if (map.State != MapGenerator.Status.Ready) return;
            if (wanted == PngVersion && File.Exists(PngFile) && File.Exists(PublicMapFile)) return;
            if (DateTime.UtcNow - lastEncode < PngInterval && File.Exists(PngFile) && File.Exists(PublicMapFile)) return;
            if (Interlocked.CompareExchange(ref encoding, 1, 0) != 0) return;
            lastEncode = DateTime.UtcNow;
            var copy = (BitArray)explored.Clone();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Write(PngFile, EncodeFog(copy));
                    byte[] pixels = map.Pixels();
                    if (pixels != null) Write(PublicMapFile, EncodePublicMap(copy, pixels));
                    Interlocked.Exchange(ref pngVersion, wanted);
                }
                catch (Exception e)
                {
                    BepInExPlugin.Warn("Writing the public map failed: " + e.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref encoding, 0);
                }
            });
        }

        public string PublicMapFile => Path.Combine(Path.GetDirectoryName(file), "map-public.png");

        private static void Write(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Grey + alpha, north up: unexplored pixels opaque, explored ones clear.</summary>
        private byte[] EncodeFog(BitArray bits)
        {
            var pixels = new byte[size * size * 2];
            for (int y = 0; y < size; y++)
            {
                int row = size - 1 - y;
                for (int x = 0; x < size; x++)
                {
                    if (bits[y * size + x]) continue;
                    int o = (row * size + x) * 2;
                    pixels[o] = 24;
                    pixels[o + 1] = 255;
                }
            }
            return Png.Encode(pixels, size, size, 4);
        }

        private byte[] EncodePublicMap(BitArray bits, byte[] map)
        {
            var pixels = (byte[])map.Clone();
            for (int y = 0; y < size; y++)
            {
                int row = size - 1 - y;
                for (int x = 0; x < size; x++)
                {
                    if (bits[y * size + x]) continue;
                    int o = (row * size + x) * 3;
                    pixels[o] = 24;
                    pixels[o + 1] = 26;
                    pixels[o + 2] = 30;
                }
            }
            return Png.Encode(pixels, size, size, 2);
        }

        public void Save()
        {
            if (!unsaved) return;
            try
            {
                var bytes = new byte[(explored.Length + 7) / 8];
                explored.CopyTo(bytes, 0);
                using (var output = new MemoryStream())
                {
                    using (var deflate = new DeflateStream(output, CompressionMode.Compress))
                        deflate.Write(bytes, 0, bytes.Length);
                    string tmp = file + ".tmp";
                    File.WriteAllBytes(tmp, output.ToArray());
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmp, file);
                }
                unsaved = false;
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Saving the explored map failed: " + e.Message);
            }
        }

        private void Load()
        {
            if (!File.Exists(file)) return;
            try
            {
                using (var input = new DeflateStream(new MemoryStream(File.ReadAllBytes(file)), CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    input.CopyTo(output);
                    var bits = new BitArray(output.ToArray());
                    for (int i = 0; i < explored.Length && i < bits.Length; i++)
                        explored[i] = bits[i];
                }
                version = 1;
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Loading the explored map failed: " + e.Message);
            }
        }
    }
}
