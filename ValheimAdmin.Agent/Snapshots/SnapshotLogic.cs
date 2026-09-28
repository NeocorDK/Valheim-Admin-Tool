using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ValheimAdmin.Agent.Snapshots;

/// <summary>
/// How many of a snapshot item are missing from the character now. Similar: the character has an
/// item of the same prefab, quality and variant whose custom data differs (another enchantment,
/// or mod data that changed).
/// </summary>
public sealed record ItemDiff(int Index, int Missing, bool Similar = false);

public sealed record SkillDiff(int Type, string Name, double SnapshotLevel, double CurrentLevel, string? DisplayName = null);

public sealed record RestoreItem(int Index, int? Stack);

/// <summary>
/// Pure functions over snapshot JSON as the client role produces it:
/// { name, characterId, inventory: {w, h}, items: [...], skills: [...] }.
/// </summary>
public static class SnapshotLogic
{
    private static readonly string[] itemFields =
        ["prefab", "container", "stack", "durability", "x", "y", "equipped", "quality", "variant", "crafterId", "crafterName", "worldLevel", "pickedUp", "cheated"];

    public static JsonArray Items(JsonObject snap) => snap["items"] as JsonArray ?? [];

    public static JsonArray Skills(JsonObject snap) => snap["skills"] as JsonArray ?? [];

    /// <summary>Hash of what a restore could bring back: items with their data, and skill levels.</summary>
    public static string ContentHash(JsonObject snap)
    {
        var sb = new StringBuilder();
        var items = Items(snap).OfType<JsonObject>()
            .OrderBy(i => Int(i, "y")).ThenBy(i => Int(i, "x")).ThenBy(i => Str(i, "prefab"), StringComparer.Ordinal);
        foreach (JsonObject item in items)
        {
            foreach (string field in itemFields)
                sb.Append(item[field]?.ToJsonString()).Append('|');
            sb.Append(CanonicalData(item)).Append('\n');
        }
        foreach (JsonObject skill in Skills(snap).OfType<JsonObject>().OrderBy(s => Int(s, "type")))
            sb.Append(Int(skill, "type")).Append('=').Append(Math.Round(Dbl(skill, "level"), 2)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>customData with sorted keys, so equal data compares equal. Ignored keys are left out.</summary>
    public static string CanonicalData(JsonObject item, IReadOnlyCollection<string>? ignore = null)
    {
        if (item["data"] is not JsonObject data || data.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var kv in data.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (IsIgnored(kv.Key, ignore)) continue;
            sb.Append(kv.Key).Append('=').Append(kv.Value?.ToJsonString()).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>Exact key, or a prefix when the pattern ends with '*'.</summary>
    public static bool IsIgnored(string key, IReadOnlyCollection<string>? ignore)
    {
        if (ignore == null || ignore.Count == 0) return false;
        foreach (string pattern in ignore)
        {
            if (string.IsNullOrEmpty(pattern)) continue;
            if (pattern.EndsWith('*') ? key.StartsWith(pattern[..^1], StringComparison.Ordinal) : key == pattern) return true;
        }
        return false;
    }

    /// <summary>Epic Loot data of the item, if any (see <see cref="EpicLootAdapter"/>).</summary>
    public static MagicInfo? Magic(JsonObject item) => EpicLootAdapter.Read(item);

    /// <summary>Short description for snapshot lists.</summary>
    public static JsonObject Summary(JsonObject snap)
    {
        var items = Items(snap).OfType<JsonObject>().ToList();
        var magic = new JsonObject();
        foreach (var info in items.Select(Magic).Where(m => m != null))
            magic[info!.RarityName] = (magic[info.RarityName]?.GetValue<int>() ?? 0) + 1;
        return new JsonObject
        {
            ["items"] = items.Count,
            ["special"] = items.Count(i => i["data"] is JsonObject d && d.Count > 0),
            ["equipped"] = new JsonArray(items.Where(i => i["equipped"]?.GetValue<bool>() == true)
                .Select(i => (JsonNode?)JsonValue.Create(Str(i, "label") ?? Str(i, "prefab"))).ToArray()),
            ["magic"] = magic,
            ["skillTotal"] = Math.Round(Skills(snap).OfType<JsonObject>().Sum(s => Dbl(s, "level")), 1),
        };
    }

    /// <summary>
    /// Snapshot items that the live character lacks. Items that do not stack must match exactly,
    /// including their customData (enchantments, mod data) minus the ignored keys; stackable items
    /// compare total counts.
    /// </summary>
    public static List<ItemDiff> Diff(JsonObject snapshot, JsonObject live, IReadOnlyCollection<string>? ignoreKeys = null)
    {
        var unique = new Dictionary<string, int>();
        var stacks = new Dictionary<string, int>();
        foreach (JsonObject item in Items(live).OfType<JsonObject>())
        {
            if (IsStackable(item, ignoreKeys))
                stacks[StackKey(item)] = stacks.GetValueOrDefault(StackKey(item)) + Int(item, "stack");
            else
                unique[UniqueKey(item, ignoreKeys)] = unique.GetValueOrDefault(UniqueKey(item, ignoreKeys)) + 1;
        }

        var result = new List<ItemDiff>();
        var unmatched = new List<(int Index, JsonObject Item)>();
        var snapItems = Items(snapshot);
        for (int i = 0; i < snapItems.Count; i++)
        {
            if (snapItems[i] is not JsonObject item) continue;
            if (IsStackable(item, ignoreKeys))
            {
                string key = StackKey(item);
                int have = stacks.GetValueOrDefault(key);
                int want = Int(item, "stack");
                int covered = Math.Min(have, want);
                stacks[key] = have - covered;
                if (want - covered > 0) result.Add(new ItemDiff(i, want - covered));
            }
            else
            {
                string key = UniqueKey(item, ignoreKeys);
                int have = unique.GetValueOrDefault(key);
                if (have > 0) unique[key] = have - 1;
                else unmatched.Add((i, item));
            }
        }

        // Live items nobody matched, by prefab|quality|variant, flag a loss as "similar".
        var leftovers = new Dictionary<string, int>();
        foreach (var (key, count) in unique)
            if (count > 0)
                leftovers[LooseKey(key)] = leftovers.GetValueOrDefault(LooseKey(key)) + count;
        foreach (var (index, item) in unmatched)
        {
            string loose = LooseKey(UniqueKey(item, ignoreKeys));
            bool similar = leftovers.GetValueOrDefault(loose) > 0;
            if (similar) leftovers[loose]--;
            result.Add(new ItemDiff(index, Math.Max(1, Int(item, "stack")), similar));
        }
        result.Sort((a, b) => a.Index.CompareTo(b.Index));
        return result;
    }

    public static List<SkillDiff> DiffSkills(JsonObject snapshot, JsonObject live)
    {
        var current = Skills(live).OfType<JsonObject>().ToDictionary(s => Int(s, "type"), s => Dbl(s, "level"));
        return Skills(snapshot).OfType<JsonObject>()
            .Select(s => new SkillDiff(Int(s, "type"), Str(s, "name") ?? "", Dbl(s, "level"), current.GetValueOrDefault(Int(s, "type")), Str(s, "displayName")))
            .Where(d => d.SnapshotLevel - d.CurrentLevel > 0.01)
            .ToList();
    }

    private static bool IsStackable(JsonObject item, IReadOnlyCollection<string>? ignore) =>
        Int(item, "maxStack") > 1 && CanonicalData(item, ignore).Length == 0;

    private static string StackKey(JsonObject item) => $"{Str(item, "prefab")}|{Int(item, "quality")}";

    private static string UniqueKey(JsonObject item, IReadOnlyCollection<string>? ignore) =>
        $"{Str(item, "prefab")}|{Int(item, "quality")}|{Int(item, "variant")}|{CanonicalData(item, ignore)}";

    /// <summary>prefab|quality|variant part of a unique key.</summary>
    private static string LooseKey(string uniqueKey)
    {
        int bar = -1;
        for (int n = 0; n < 3; n++)
        {
            bar = uniqueKey.IndexOf('|', bar + 1);
            if (bar < 0) return uniqueKey;
        }
        return uniqueKey[..bar];
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
            var days = new HashSet<DateOnly>();
            bool first = true;
            foreach (var row in group.OrderByDescending(r => r.Ts).ThenByDescending(r => r.Id))
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(row.Ts), tz).DateTime);
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
    /// Builds the request the client role applies. items == null means every snapshot item;
    /// a stack override restores only part of a stack.
    /// </summary>
    public static JsonObject BuildRestore(JsonObject snapshot, string mode, IReadOnlyList<RestoreItem>? items, string skillMode)
    {
        var snapItems = Items(snapshot);
        var selected = new JsonArray();
        IEnumerable<RestoreItem> wanted = items ?? Enumerable.Range(0, snapItems.Count).Select(i => new RestoreItem(i, null));
        foreach (var w in wanted)
        {
            if (w.Index < 0 || w.Index >= snapItems.Count || snapItems[w.Index] is not JsonObject src) continue;
            var copy = (JsonObject)src.DeepClone();
            copy.Remove("tooltip");
            copy.Remove("label");
            if (w.Stack is int stack && stack > 0 && stack < Int(src, "stack")) copy["stack"] = stack;
            selected.Add(copy);
        }
        return new JsonObject
        {
            ["mode"] = mode,
            ["itemVersion"] = snapshot["itemVersion"]?.DeepClone(),
            ["containers"] = snapshot["containers"]?.DeepClone() ?? new JsonArray(),
            ["items"] = selected,
            ["skills"] = Skills(snapshot).DeepClone(),
            ["skillMode"] = skillMode,
        };
    }

    public static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : o[key]?.ToString();

    public static int Int(JsonObject o, string key) => (int)Dbl(o, key);

    public static double Dbl(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return 0;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out float f)) return f;
        return 0;
    }
}
