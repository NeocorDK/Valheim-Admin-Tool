#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ValheimAdmin.Shared
{
    /// <summary>How many of a snapshot item are missing now; Similar: a look-alike with other data remains.</summary>
    public sealed class ItemLoss
    {
        public int Index;
        public int Missing;
        public bool Similar;
    }

    public sealed class SkillLoss
    {
        public int Type;
        public string Name;
        public string DisplayName;
        public double SnapshotLevel;
        public double CurrentLevel;
    }

    /// <summary>
    /// Pure functions over snapshot JSON as the client role produces it, parsed with
    /// <see cref="Json"/>: { name, characterId, itemVersion, inventory, containers, items, skills }.
    /// Compiled into both the agent and the plugin (standalone mode), so there is one implementation.
    /// </summary>
    public static class SnapshotRules
    {
        private static readonly string[] itemFields =
            { "prefab", "container", "stack", "durability", "x", "y", "equipped", "quality", "variant", "crafterId", "crafterName", "worldLevel", "pickedUp", "cheated" };

        public static List<object> Items(Dictionary<string, object> snap) => snap.List("items") ?? new List<object>();

        public static List<object> Skills(Dictionary<string, object> snap) => snap.List("skills") ?? new List<object>();

        private static IEnumerable<Dictionary<string, object>> Objects(List<object> list) => list.OfType<Dictionary<string, object>>();

        public static string Str(Dictionary<string, object> o, string key) => o.Str(key);

        public static int Int(Dictionary<string, object> o, string key) => (int)Dbl(o, key);

        public static double Dbl(Dictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out object v) || v == null || v is string || v is bool) return 0;
            try
            {
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Hash of what a restore could bring back: items with their data, and skill levels.</summary>
        public static string ContentHash(Dictionary<string, object> snap)
        {
            var sb = new StringBuilder();
            var items = Objects(Items(snap))
                .OrderBy(i => Int(i, "y")).ThenBy(i => Int(i, "x")).ThenBy(i => Str(i, "prefab") ?? "", StringComparer.Ordinal);
            foreach (var item in items)
            {
                foreach (string field in itemFields)
                    sb.Append(item.TryGetValue(field, out object v) && v != null ? Json.Serialize(v) : "").Append('|');
                sb.Append(CanonicalData(item)).Append('\n');
            }
            foreach (var skill in Objects(Skills(snap)).OrderBy(s => Int(s, "type")))
                sb.Append(Int(skill, "type")).Append('=')
                    .Append(Math.Round(Dbl(skill, "level"), 2).ToString(CultureInfo.InvariantCulture)).Append('\n');
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        /// <summary>customData with sorted keys, so equal data compares equal. Ignored keys are left out.</summary>
        public static string CanonicalData(Dictionary<string, object> item, ICollection<string> ignore = null)
        {
            var data = item.Obj("data");
            if (data == null || data.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var kv in data.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (IsIgnored(kv.Key, ignore)) continue;
                sb.Append(kv.Key).Append('=').Append(Json.Serialize(kv.Value)).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>Exact key, or a prefix when the pattern ends with '*'.</summary>
        public static bool IsIgnored(string key, ICollection<string> ignore)
        {
            if (ignore == null || ignore.Count == 0) return false;
            foreach (string pattern in ignore)
            {
                if (string.IsNullOrEmpty(pattern)) continue;
                if (pattern.EndsWith("*", StringComparison.Ordinal)
                        ? key.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal)
                        : key == pattern)
                    return true;
            }
            return false;
        }

        /// <summary>Short description for snapshot lists.</summary>
        public static Dictionary<string, object> Summary(Dictionary<string, object> snap)
        {
            var items = Objects(Items(snap)).ToList();
            var magic = new Dictionary<string, object>();
            foreach (var info in items.Select(EpicLoot.Read).Where(m => m != null))
            {
                string rarity = info.Str("rarityName");
                magic[rarity] = magic.Int(rarity) + 1;
            }
            return new Dictionary<string, object>
            {
                { "items", items.Count },
                { "special", items.Count(i => i.Obj("data") is Dictionary<string, object> d && d.Count > 0) },
                { "equipped", items.Where(i => i.Bool("equipped")).Select(i => (object)(Str(i, "label") ?? Str(i, "prefab"))).ToList() },
                { "magic", magic },
                { "skillTotal", Math.Round(Objects(Skills(snap)).Sum(s => Dbl(s, "level")), 1) },
            };
        }

        /// <summary>
        /// Snapshot items that the live character lacks. Items that do not stack must match exactly,
        /// including their customData (enchantments, mod data) minus the ignored keys; stackable items
        /// compare total counts.
        /// </summary>
        public static List<ItemLoss> Diff(Dictionary<string, object> snapshot, Dictionary<string, object> live, ICollection<string> ignoreKeys = null)
        {
            var unique = new Dictionary<string, int>();
            var stacks = new Dictionary<string, int>();
            foreach (var item in Objects(Items(live)))
            {
                if (IsStackable(item, ignoreKeys))
                    Add(stacks, StackKey(item), Int(item, "stack"));
                else
                    Add(unique, UniqueKey(item, ignoreKeys), 1);
            }

            var result = new List<ItemLoss>();
            var unmatched = new List<KeyValuePair<int, Dictionary<string, object>>>();
            var snapItems = Items(snapshot);
            for (int i = 0; i < snapItems.Count; i++)
            {
                if (!(snapItems[i] is Dictionary<string, object> item)) continue;
                if (IsStackable(item, ignoreKeys))
                {
                    string key = StackKey(item);
                    int have = Get(stacks, key);
                    int want = Int(item, "stack");
                    int covered = Math.Min(have, want);
                    stacks[key] = have - covered;
                    if (want - covered > 0) result.Add(new ItemLoss { Index = i, Missing = want - covered });
                }
                else
                {
                    string key = UniqueKey(item, ignoreKeys);
                    int have = Get(unique, key);
                    if (have > 0) unique[key] = have - 1;
                    else unmatched.Add(new KeyValuePair<int, Dictionary<string, object>>(i, item));
                }
            }

            // Live items nobody matched, by prefab|quality|variant, flag a loss as "similar".
            var leftovers = new Dictionary<string, int>();
            foreach (var kv in unique)
                if (kv.Value > 0)
                    Add(leftovers, LooseKey(kv.Key), kv.Value);
            foreach (var kv in unmatched)
            {
                string loose = LooseKey(UniqueKey(kv.Value, ignoreKeys));
                bool similar = Get(leftovers, loose) > 0;
                if (similar) leftovers[loose]--;
                result.Add(new ItemLoss { Index = kv.Key, Missing = Math.Max(1, Int(kv.Value, "stack")), Similar = similar });
            }
            result.Sort((a, b) => a.Index.CompareTo(b.Index));
            return result;
        }

        public static List<SkillLoss> DiffSkills(Dictionary<string, object> snapshot, Dictionary<string, object> live)
        {
            var current = new Dictionary<int, double>();
            foreach (var s in Objects(Skills(live)))
                current[Int(s, "type")] = Dbl(s, "level");
            return Objects(Skills(snapshot))
                .Select(s => new SkillLoss
                {
                    Type = Int(s, "type"),
                    Name = Str(s, "name") ?? "",
                    DisplayName = Str(s, "displayName"),
                    SnapshotLevel = Dbl(s, "level"),
                    CurrentLevel = current.TryGetValue(Int(s, "type"), out double c) ? c : 0,
                })
                .Where(d => d.SnapshotLevel - d.CurrentLevel > 0.01)
                .ToList();
        }

        private static void Add(Dictionary<string, int> d, string key, int n) => d[key] = Get(d, key) + n;

        private static int Get(Dictionary<string, int> d, string key) => d.TryGetValue(key, out int v) ? v : 0;

        private static bool IsStackable(Dictionary<string, object> item, ICollection<string> ignore) =>
            Int(item, "maxStack") > 1 && CanonicalData(item, ignore).Length == 0;

        private static string StackKey(Dictionary<string, object> item) => Str(item, "prefab") + "|" + Int(item, "quality");

        private static string UniqueKey(Dictionary<string, object> item, ICollection<string> ignore) =>
            Str(item, "prefab") + "|" + Int(item, "quality") + "|" + Int(item, "variant") + "|" + CanonicalData(item, ignore);

        /// <summary>prefab|quality|variant part of a unique key.</summary>
        private static string LooseKey(string uniqueKey)
        {
            int bar = -1;
            for (int n = 0; n < 3; n++)
            {
                bar = uniqueKey.IndexOf('|', bar + 1);
                if (bar < 0) return uniqueKey;
            }
            return uniqueKey.Substring(0, bar);
        }

        /// <summary>
        /// Snapshot ids to delete: keep everything younger than keepAllDays, the latest snapshot
        /// of each local day up to keepDailyDays, and always the newest snapshot of a character.
        /// </summary>
        public static List<long> Retention(IEnumerable<(long Id, long CharacterId, long Ts)> rows, DateTimeOffset now,
            int keepAllDays, int keepDailyDays, TimeZoneInfo tz)
        {
            long allCutoff = now.AddDays(-keepAllDays).ToUnixTimeMilliseconds();
            long dailyCutoff = now.AddDays(-keepDailyDays).ToUnixTimeMilliseconds();
            var delete = new List<long>();
            foreach (var group in rows.GroupBy(r => r.CharacterId))
            {
                var days = new HashSet<DateTime>();
                bool first = true;
                foreach (var row in group.OrderByDescending(r => r.Ts).ThenByDescending(r => r.Id))
                {
                    DateTime day = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(row.Ts), tz).Date;
                    bool newDay = days.Add(day);
                    if (first || row.Ts >= allCutoff)
                    {
                        first = false;
                        continue;
                    }
                    if (row.Ts >= dailyCutoff && newDay) continue;
                    delete.Add(row.Id);
                }
            }
            return delete;
        }

        /// <summary>
        /// Builds the request the client role applies. wanted == null means every snapshot item;
        /// a stack override (value) restores only part of a stack.
        /// </summary>
        public static Dictionary<string, object> BuildRestore(Dictionary<string, object> snapshot, string mode,
            IEnumerable<KeyValuePair<int, int?>> wanted, string skillMode)
        {
            var snapItems = Items(snapshot);
            var selected = new List<object>();
            var picks = wanted ?? Enumerable.Range(0, snapItems.Count).Select(i => new KeyValuePair<int, int?>(i, null));
            foreach (var w in picks)
            {
                if (w.Key < 0 || w.Key >= snapItems.Count || !(snapItems[w.Key] is Dictionary<string, object> src)) continue;
                var copy = Clone(src);
                copy.Remove("tooltip");
                copy.Remove("label");
                if (w.Value is int stack && stack > 0 && stack < Int(src, "stack")) copy["stack"] = stack;
                selected.Add(copy);
            }
            return new Dictionary<string, object>
            {
                { "mode", mode },
                { "itemVersion", snapshot.TryGetValue("itemVersion", out object v) ? v : null },
                { "containers", snapshot.List("containers") ?? new List<object>() },
                { "items", selected },
                { "skills", Skills(snapshot) },
                { "skillMode", skillMode },
            };
        }

        public static Dictionary<string, object> Clone(Dictionary<string, object> o) => Json.ParseObject(Json.Serialize(o));

        /// <summary>Adds what optional adapters know about each item (Epic Loot) for the panel.</summary>
        public static Dictionary<string, object> Enrich(Dictionary<string, object> snapshot)
        {
            foreach (var item in Objects(Items(snapshot)))
            {
                var magic = EpicLoot.Read(item);
                if (magic != null) item["magic"] = magic;
            }
            return snapshot;
        }
    }

    /// <summary>
    /// Optional extra for Epic Loot items: reads the MagicItemComponent JSON the mod keeps in the
    /// item's customData, so the panel can colour items by rarity and list effects. Snapshots,
    /// diffs and restores never depend on it.
    /// </summary>
    public static class EpicLoot
    {
        public static readonly string[] RarityNames = { "Magic", "Rare", "Epic", "Legendary", "Mythic" };

        /// <summary>{rarity, rarityName, displayName, legendaryId, setId, effects: [{type, value}]} or null.</summary>
        public static Dictionary<string, object> Read(Dictionary<string, object> item)
        {
            var data = item.Obj("data");
            if (data == null) return null;
            foreach (var kv in data)
            {
                if (kv.Key.IndexOf("MagicItemComponent", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string json = kv.Value as string ?? (kv.Value != null ? Json.Serialize(kv.Value) : null);
                if (string.IsNullOrWhiteSpace(json)) continue;
                Dictionary<string, object> magic;
                try
                {
                    magic = Json.Parse(json) as Dictionary<string, object>;
                }
                catch (FormatException)
                {
                    continue;
                }
                if (magic == null) continue;
                int rarity = RarityOf(Get(magic, "Rarity"));
                var effects = new List<object>();
                if (Get(magic, "Effects") is List<object> list)
                    foreach (var e in list.OfType<Dictionary<string, object>>())
                        effects.Add(new Dictionary<string, object>
                        {
                            { "type", Convert.ToString(Get(e, "EffectType") ?? "?", CultureInfo.InvariantCulture) },
                            { "value", NumberOf(Get(e, "EffectValue")) },
                        });
                return new Dictionary<string, object>
                {
                    { "rarity", rarity },
                    { "rarityName", rarity >= 0 && rarity < RarityNames.Length ? RarityNames[rarity] : rarity.ToString(CultureInfo.InvariantCulture) },
                    { "displayName", NullIfEmpty(Get(magic, "DisplayName")) },
                    { "legendaryId", NullIfEmpty(Get(magic, "LegendaryID")) },
                    { "setId", NullIfEmpty(Get(magic, "SetID")) },
                    { "effects", effects },
                };
            }
            return null;
        }

        private static string NullIfEmpty(object o)
        {
            string s = o == null ? null : Convert.ToString(o, CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static object Get(Dictionary<string, object> o, string name)
        {
            foreach (var kv in o)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        private static int RarityOf(object node)
        {
            if (node == null) return 0;
            if (node is long || node is double || node is int) return Convert.ToInt32(node, CultureInfo.InvariantCulture);
            string s = Convert.ToString(node, CultureInfo.InvariantCulture);
            int i = Array.FindIndex(RarityNames, r => r.Equals(s, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? i : int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        }

        private static double NumberOf(object node)
        {
            if (node is long || node is double || node is int) return Convert.ToDouble(node, CultureInfo.InvariantCulture);
            return double.TryParse(Convert.ToString(node, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
        }
    }
}
