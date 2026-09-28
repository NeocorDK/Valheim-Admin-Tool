using System.Text.Json;
using System.Text.Json.Nodes;

namespace ValheimAdmin.Agent.Snapshots;

public sealed record MagicEffect(string Type, double Value);

public sealed record MagicInfo(int Rarity, string RarityName, string? DisplayName, string? LegendaryId, string? SetId, List<MagicEffect> Effects);

/// <summary>
/// Optional extra for Epic Loot items: reads the MagicItemComponent JSON the mod keeps in the
/// item's customData so the panel can colour items by rarity and list effects. Snapshots, diffs
/// and restores never depend on it; every other mod's items are handled by the generic code
/// (customData, the game's tooltip and the item bytes).
/// </summary>
public static class EpicLootAdapter
{
    public static readonly string[] RarityNames = ["Magic", "Rare", "Epic", "Legendary", "Mythic"];

    public static MagicInfo? Read(JsonObject item)
    {
        if (item["data"] is not JsonObject data) return null;
        foreach (var kv in data)
        {
            if (!kv.Key.Contains("MagicItemComponent", StringComparison.OrdinalIgnoreCase)) continue;
            string? json = kv.Value?.GetValueKind() == JsonValueKind.String ? kv.Value.GetValue<string>() : kv.Value?.ToJsonString();
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                if (JsonNode.Parse(json) is not JsonObject magic) continue;
                int rarity = RarityOf(Get(magic, "Rarity"));
                var effects = new List<MagicEffect>();
                if (Get(magic, "Effects") is JsonArray arr)
                {
                    foreach (JsonObject e in arr.OfType<JsonObject>())
                        effects.Add(new MagicEffect(Get(e, "EffectType")?.ToString() ?? "?", NumberOf(Get(e, "EffectValue"))));
                }
                return new MagicInfo(rarity, rarity >= 0 && rarity < RarityNames.Length ? RarityNames[rarity] : rarity.ToString(),
                    NullIfEmpty(Get(magic, "DisplayName")?.ToString()), NullIfEmpty(Get(magic, "LegendaryID")?.ToString()),
                    NullIfEmpty(Get(magic, "SetID")?.ToString()), effects);
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static JsonNode? Get(JsonObject o, string name)
    {
        foreach (var kv in o)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    private static int RarityOf(JsonNode? node)
    {
        if (node == null) return 0;
        if (node.GetValueKind() == JsonValueKind.Number) return node.GetValue<int>();
        string s = node.ToString();
        int i = Array.FindIndex(RarityNames, r => r.Equals(s, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : int.TryParse(s, out int n) ? n : 0;
    }

    private static double NumberOf(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : double.TryParse(node?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
}
