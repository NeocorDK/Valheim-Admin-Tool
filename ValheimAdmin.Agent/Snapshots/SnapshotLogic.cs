using System.Text.Json;
using System.Text.Json.Nodes;
using ValheimAdmin.Shared;

namespace ValheimAdmin.Agent.Snapshots;

/// <summary>
/// How many of a snapshot item are missing from the character now. Similar: the character has an
/// item of the same prefab, quality and variant whose custom data differs (another enchantment,
/// or mod data that changed).
/// </summary>
public sealed record ItemDiff(int Index, int Missing, bool Similar = false);

public sealed record SkillDiff(int Type, string Name, double SnapshotLevel, double CurrentLevel, string? DisplayName = null);

public sealed record RestoreItem(int Index, int? Stack);

public sealed record MagicEffect(string Type, double Value);

public sealed record MagicInfo(int Rarity, string RarityName, string? DisplayName, string? LegendaryId, string? SetId, List<MagicEffect> Effects);

/// <summary>
/// The agent's view of the snapshot rules. The rules themselves are <see cref="SnapshotRules"/> in
/// ValheimAdmin.Common, shared with the plugin's standalone mode; this class converts JSON nodes.
/// </summary>
public static class SnapshotLogic
{
    public static JsonArray Items(JsonObject snap) => snap["items"] as JsonArray ?? [];

    public static JsonArray Skills(JsonObject snap) => snap["skills"] as JsonArray ?? [];

    public static string ContentHash(JsonObject snap) => SnapshotRules.ContentHash(JsonCompat.ToDict(snap));

    public static string CanonicalData(JsonObject item, IReadOnlyCollection<string>? ignore = null) =>
        SnapshotRules.CanonicalData(JsonCompat.ToDict(item), ignore?.ToList());

    public static bool IsIgnored(string key, IReadOnlyCollection<string>? ignore) => SnapshotRules.IsIgnored(key, ignore?.ToList());

    /// <summary>Epic Loot data of the item, if any (optional adapter, panel colours only).</summary>
    public static MagicInfo? Magic(JsonObject item)
    {
        var m = EpicLoot.Read(JsonCompat.ToDict(item));
        if (m == null) return null;
        var effects = (m.List("effects") ?? []).OfType<Dictionary<string, object>>()
            .Select(e => new MagicEffect(e.Str("type") ?? "?", e.Double("value"))).ToList();
        return new MagicInfo(m.Int("rarity"), m.Str("rarityName")!, m.Str("displayName"), m.Str("legendaryId"), m.Str("setId"), effects);
    }

    public static JsonObject Summary(JsonObject snap) => JsonCompat.ToObject(SnapshotRules.Summary(JsonCompat.ToDict(snap)));

    public static List<ItemDiff> Diff(JsonObject snapshot, JsonObject live, IReadOnlyCollection<string>? ignoreKeys = null) =>
        SnapshotRules.Diff(JsonCompat.ToDict(snapshot), JsonCompat.ToDict(live), ignoreKeys?.ToList())
            .Select(d => new ItemDiff(d.Index, d.Missing, d.Similar)).ToList();

    public static List<SkillDiff> DiffSkills(JsonObject snapshot, JsonObject live) =>
        SnapshotRules.DiffSkills(JsonCompat.ToDict(snapshot), JsonCompat.ToDict(live))
            .Select(d => new SkillDiff(d.Type, d.Name, d.SnapshotLevel, d.CurrentLevel, d.DisplayName)).ToList();

    public static List<long> Retention(IEnumerable<(long Id, long CharacterId, long Ts)> rows, DateTimeOffset now,
        int keepAllDays, int keepDailyDays, TimeZoneInfo tz) =>
        SnapshotRules.Retention(rows, now, keepAllDays, keepDailyDays, tz);

    public static JsonObject BuildRestore(JsonObject snapshot, string mode, IReadOnlyList<RestoreItem>? items, string skillMode) =>
        JsonCompat.ToObject(SnapshotRules.BuildRestore(JsonCompat.ToDict(snapshot), mode,
            items?.Select(i => new KeyValuePair<int, int?>(i.Index, i.Stack)), skillMode));

    /// <summary>Adds optional adapter data (Epic Loot) to each item for the panel.</summary>
    public static JsonObject Enrich(JsonObject snapshot) => JsonCompat.ToObject(SnapshotRules.Enrich(JsonCompat.ToDict(snapshot)));

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
