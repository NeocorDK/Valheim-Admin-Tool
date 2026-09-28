using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ValheimAdmin.Shared;

namespace ValheimAdmin.Web
{
    /// <summary>
    /// Files that stand in for the agent's SQLite database in standalone mode (native SQLite is not
    /// available inside the game on every host). Everything lives under [Web] DataDir:
    /// events/YYYY-MM-DD.jsonl, players.json, snapshots/index.json + snapshots/data/HASH.json.gz,
    /// icons/*.png, audit.jsonl, config-backups/. Thread-safe; callers may be any thread.
    /// </summary>
    public sealed class LocalStore
    {
        private const int RecentEvents = 5000;

        private readonly string root;
        private readonly object gate = new object();
        private readonly List<Dictionary<string, object>> events = new List<Dictionary<string, object>>();
        private long lastEventId;
        private Dictionary<string, object> players = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private List<Dictionary<string, object>> snapshots = new List<Dictionary<string, object>>();
        private long lastSnapshotId;

        public LocalStore(string root)
        {
            this.root = root;
            Directory.CreateDirectory(Path.Combine(root, "events"));
            Directory.CreateDirectory(Path.Combine(root, "snapshots", "data"));
            Directory.CreateDirectory(Path.Combine(root, "icons"));
            LoadEvents();
            LoadPlayers();
            LoadSnapshots();
        }

        public string Root => root;

        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ---------------------------------------------------------------- events

        public Dictionary<string, object> AddEvent(string kind, string player, string message, Dictionary<string, object> data, long ts)
        {
            lock (gate)
            {
                var row = new Dictionary<string, object>
                {
                    { "id", ++lastEventId },
                    { "ts", ts },
                    { "kind", kind },
                    { "player", player },
                    { "message", message },
                    { "data", data },
                };
                events.Add(row);
                if (events.Count > RecentEvents) events.RemoveRange(0, events.Count - RecentEvents);
                string file = Path.Combine(root, "events", DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
                File.AppendAllText(file, Json.Serialize(row) + "\n", Encoding.UTF8);
                return row;
            }
        }

        /// <summary>Newest first; searches the last 5000 events kept in memory.</summary>
        public List<object> Events(ICollection<string> kinds, string player, string q, long? before, long? from, long? to, int limit)
        {
            lock (gate)
            {
                IEnumerable<Dictionary<string, object>> rows = events.AsEnumerable().Reverse();
                if (kinds != null && kinds.Count > 0) rows = rows.Where(e => kinds.Contains(e.Str("kind")));
                if (!string.IsNullOrEmpty(player)) rows = rows.Where(e => string.Equals(e.Str("player"), player, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(q)) rows = rows.Where(e => (e.Str("message") ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                if (before.HasValue) rows = rows.Where(e => e.Long("id") < before.Value);
                if (from.HasValue) rows = rows.Where(e => e.Long("ts") >= from.Value);
                if (to.HasValue) rows = rows.Where(e => e.Long("ts") <= to.Value);
                return rows.Take(limit).Cast<object>().ToList();
            }
        }

        public int Deaths(string player)
        {
            lock (gate)
                return events.Count(e => e.Str("kind") == "death" && string.Equals(e.Str("player"), player, StringComparison.OrdinalIgnoreCase));
        }

        private void LoadEvents()
        {
            try
            {
                var files = Directory.GetFiles(Path.Combine(root, "events"), "*.jsonl").OrderByDescending(f => f, StringComparer.Ordinal).ToList();
                var loaded = new List<Dictionary<string, object>>();
                foreach (string file in files)
                {
                    var lines = File.ReadAllLines(file, Encoding.UTF8);
                    for (int i = lines.Length - 1; i >= 0 && loaded.Count < RecentEvents; i--)
                    {
                        try
                        {
                            loaded.Add(Json.ParseObject(lines[i]));
                        }
                        catch (FormatException)
                        {
                        }
                    }
                    if (loaded.Count >= RecentEvents) break;
                }
                loaded.Reverse();
                events.AddRange(loaded);
                lastEventId = events.Count > 0 ? events.Max(e => e.Long("id")) : 0;
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Loading the event log failed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- players and sessions

        private Dictionary<string, object> PlayerEntry(string name)
        {
            if (!(players.TryGetValue(name, out object o) && o is Dictionary<string, object> p))
                players[name] = p = new Dictionary<string, object> { { "sessions", new List<object>() } };
            return p;
        }

        public void OpenSession(string name, string host, long ts)
        {
            lock (gate)
            {
                var p = PlayerEntry(name);
                if (!string.IsNullOrEmpty(host)) p["host"] = host;
                var sessions = p.List("sessions");
                if (sessions.OfType<Dictionary<string, object>>().Any(s => !s.ContainsKey("end") || s["end"] == null)) return;
                sessions.Insert(0, new Dictionary<string, object> { { "start", ts }, { "host", host } });
                if (sessions.Count > 100) sessions.RemoveRange(100, sessions.Count - 100);
                SavePlayers();
            }
        }

        public void CloseSession(string name, long ts)
        {
            lock (gate)
            {
                bool changed = false;
                foreach (var s in PlayerEntry(name).List("sessions").OfType<Dictionary<string, object>>())
                    if (!s.ContainsKey("end") || s["end"] == null)
                    {
                        s["end"] = ts;
                        changed = true;
                    }
                if (changed) SavePlayers();
            }
        }

        /// <summary>After a server start nobody is online; close whatever was left open.</summary>
        public void CloseAllSessions(long ts)
        {
            lock (gate)
            {
                foreach (string name in players.Keys.ToList())
                    foreach (var s in PlayerEntry(name).List("sessions").OfType<Dictionary<string, object>>())
                        if (!s.ContainsKey("end") || s["end"] == null)
                            s["end"] = ts;
                SavePlayers();
            }
        }

        /// <summary>Same shape as the agent's /api/players/history.</summary>
        public List<object> History()
        {
            lock (gate)
            {
                long now = Now;
                var result = new List<Dictionary<string, object>>();
                foreach (var kv in players)
                {
                    var sessions = PlayerEntry(kv.Key).List("sessions").OfType<Dictionary<string, object>>().ToList();
                    if (sessions.Count == 0) continue;
                    bool online = sessions.Any(s => !s.ContainsKey("end") || s["end"] == null);
                    long End(Dictionary<string, object> s) => s.ContainsKey("end") && s["end"] != null ? s.Long("end") : now;
                    result.Add(new Dictionary<string, object>
                    {
                        { "player", kv.Key },
                        { "host", PlayerEntry(kv.Key).Str("host") },
                        { "sessions", sessions.Count },
                        { "playedMs", sessions.Sum(s => End(s) - s.Long("start")) },
                        { "lastSeen", sessions.Max(End) },
                        { "deaths", events.Count(e => e.Str("kind") == "death" && string.Equals(e.Str("player"), kv.Key, StringComparison.OrdinalIgnoreCase)) },
                        { "online", online },
                    });
                }
                return result.OrderByDescending(r => r.Long("lastSeen")).Cast<object>().ToList();
            }
        }

        public List<object> Sessions(string name)
        {
            lock (gate)
            {
                if (!players.ContainsKey(name)) return new List<object>();
                return PlayerEntry(name).List("sessions").OfType<Dictionary<string, object>>()
                    .Select(s => (object)new Dictionary<string, object>
                    {
                        { "start", s.Long("start") },
                        { "end", s.ContainsKey("end") ? s["end"] : null },
                        { "host", s.Str("host") },
                    }).ToList();
            }
        }

        private void LoadPlayers()
        {
            string file = Path.Combine(root, "players.json");
            try
            {
                if (File.Exists(file))
                    players = new Dictionary<string, object>(Json.ParseObject(File.ReadAllText(file, Encoding.UTF8)), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Loading players.json failed: " + e.Message);
            }
        }

        private void SavePlayers() => WriteAtomic(Path.Combine(root, "players.json"), Encoding.UTF8.GetBytes(Json.Serialize(players)));

        // ---------------------------------------------------------------- snapshots

        /// <summary>Stores a snapshot; identical content is stored once (by <see cref="SnapshotRules.ContentHash"/>).</summary>
        public long AddSnapshot(Dictionary<string, object> snapshot, string trigger, string host, long ts)
        {
            string hash = SnapshotRules.ContentHash(snapshot);
            long characterId = snapshot.Long("characterId");
            lock (gate)
            {
                string dataFile = DataFile(hash);
                if (!File.Exists(dataFile)) WriteAtomic(dataFile, Deflate(Json.Serialize(snapshot)));
                var previous = snapshots.Where(r => r.Long("characterId") == characterId).OrderByDescending(r => r.Long("ts")).FirstOrDefault();
                var row = new Dictionary<string, object>
                {
                    { "id", ++lastSnapshotId },
                    { "ts", ts },
                    { "characterId", characterId },
                    { "player", snapshot.Str("name") ?? "?" },
                    { "host", host },
                    { "trigger", trigger },
                    { "hash", hash },
                    { "unchanged", previous != null && previous.Str("hash") == hash },
                    { "summary", SnapshotRules.Summary(snapshot) },
                };
                snapshots.Add(row);
                SaveSnapshots();
                return row.Long("id");
            }
        }

        /// <summary>Same shape as the agent's /api/characters rows (without "online").</summary>
        public List<Dictionary<string, object>> Characters()
        {
            lock (gate)
            {
                return snapshots.GroupBy(r => r.Long("characterId"))
                    .Select(g =>
                    {
                        var newest = g.OrderByDescending(r => r.Long("ts")).ThenByDescending(r => r.Long("id")).First();
                        return new Dictionary<string, object>
                        {
                            { "characterId", g.Key },
                            { "player", newest.Str("player") },
                            { "host", newest.Str("host") },
                            { "count", g.Count() },
                            { "lastTs", newest.Long("ts") },
                        };
                    })
                    .OrderByDescending(c => c.Long("lastTs")).ToList();
            }
        }

        public List<object> SnapshotList(long characterId)
        {
            lock (gate)
                return snapshots.Where(r => r.Long("characterId") == characterId)
                    .OrderByDescending(r => r.Long("ts")).ThenByDescending(r => r.Long("id")).Take(500)
                    .Select(Public).Cast<object>().ToList();
        }

        public Dictionary<string, object> SnapshotRow(long id)
        {
            lock (gate)
            {
                var row = snapshots.FirstOrDefault(r => r.Long("id") == id);
                return row == null ? null : Public(row);
            }
        }

        public Dictionary<string, object> SnapshotContent(long id)
        {
            string hash;
            lock (gate)
                hash = snapshots.FirstOrDefault(r => r.Long("id") == id)?.Str("hash");
            if (hash == null || !File.Exists(DataFile(hash))) return null;
            return Json.ParseObject(Inflate(File.ReadAllBytes(DataFile(hash))));
        }

        public int ApplyRetention(int keepAllDays, int keepDailyDays)
        {
            lock (gate)
            {
                var delete = new HashSet<long>(SnapshotRules.Retention(
                    snapshots.Select(r => (r.Long("id"), r.Long("characterId"), r.Long("ts"))),
                    DateTimeOffset.UtcNow, keepAllDays, keepDailyDays, TimeZoneInfo.Local));
                if (delete.Count == 0) return 0;
                snapshots.RemoveAll(r => delete.Contains(r.Long("id")));
                var used = new HashSet<string>(snapshots.Select(r => r.Str("hash")));
                foreach (string file in Directory.GetFiles(Path.Combine(root, "snapshots", "data"), "*.json.gz"))
                {
                    string hash = Path.GetFileName(file).Replace(".json.gz", "");
                    if (!used.Contains(hash)) File.Delete(file);
                }
                SaveSnapshots();
                return delete.Count;
            }
        }

        private static Dictionary<string, object> Public(Dictionary<string, object> row)
        {
            var copy = new Dictionary<string, object>(row);
            copy.Remove("hash");
            return copy;
        }

        private string DataFile(string hash) => Path.Combine(root, "snapshots", "data", hash + ".json.gz");

        private void LoadSnapshots()
        {
            string file = Path.Combine(root, "snapshots", "index.json");
            try
            {
                if (File.Exists(file) && Json.Parse(File.ReadAllText(file, Encoding.UTF8)) is List<object> list)
                    snapshots = list.OfType<Dictionary<string, object>>().ToList();
                lastSnapshotId = snapshots.Count > 0 ? snapshots.Max(r => r.Long("id")) : 0;
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Loading the snapshot index failed: " + e.Message);
            }
        }

        private void SaveSnapshots() =>
            WriteAtomic(Path.Combine(root, "snapshots", "index.json"), Encoding.UTF8.GetBytes(Json.Serialize(snapshots)));

        // ---------------------------------------------------------------- icons

        private string IconFile(string prefab, int variant)
        {
            var safe = new string(prefab.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
            return Path.Combine(root, "icons", safe + "_" + prefab.GetStableHashCode().ToString("x8") + "_" + variant + ".png");
        }

        public byte[] Icon(string prefab, int variant)
        {
            string file = IconFile(prefab, variant);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }

        public bool HasIcon(string prefab, int variant) => File.Exists(IconFile(prefab, variant));

        public void PutIcon(string prefab, int variant, byte[] png) => WriteAtomic(IconFile(prefab, variant), png);

        // ---------------------------------------------------------------- audit

        public void Audit(string who, string action, string details = null)
        {
            lock (gate)
            {
                File.AppendAllText(Path.Combine(root, "audit.jsonl"), Json.Serialize(new Dictionary<string, object>
                {
                    { "ts", Now }, { "who", who }, { "action", action }, { "details", details },
                }) + "\n", Encoding.UTF8);
            }
        }

        // ---------------------------------------------------------------- helpers

        public static void WriteAtomic(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static byte[] Deflate(string text)
        {
            using (var output = new MemoryStream())
            {
                using (var gz = new GZipStream(output, CompressionMode.Compress))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    gz.Write(bytes, 0, bytes.Length);
                }
                return output.ToArray();
            }
        }

        private static string Inflate(byte[] data)
        {
            using (var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}
