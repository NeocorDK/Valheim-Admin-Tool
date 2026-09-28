using System.Text.Json.Nodes;
using ValheimAdmin.Agent.Snapshots;
using Xunit;

namespace ValheimAdmin.Agent.Tests;

public class SnapshotLogicTests
{
    private const string MagicKey = "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent";

    internal static JsonObject Item(string prefab, int stack = 1, int maxStack = 1, int quality = 1, int x = 0, int y = 0,
        string? magic = null, bool equipped = false)
    {
        var data = new JsonObject();
        if (magic != null) data[MagicKey] = magic;
        return new JsonObject
        {
            ["prefab"] = prefab, ["label"] = prefab, ["tooltip"] = "tip", ["stack"] = stack, ["maxStack"] = maxStack,
            ["quality"] = quality, ["variant"] = 0, ["durability"] = 100.0, ["x"] = x, ["y"] = y, ["equipped"] = equipped,
            ["crafterId"] = 0, ["crafterName"] = "", ["worldLevel"] = 0, ["pickedUp"] = true, ["data"] = data,
        };
    }

    internal static JsonObject Snap(long characterId, params JsonObject[] items) => new()
    {
        ["name"] = "Ragnar",
        ["characterId"] = characterId,
        ["inventory"] = new JsonObject { ["w"] = 8, ["h"] = 4 },
        ["items"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()),
        ["skills"] = new JsonArray(
            new JsonObject { ["type"] = 1, ["name"] = "Swords", ["level"] = 40.5, ["acc"] = 1.0 },
            new JsonObject { ["type"] = 102, ["name"] = "Run", ["level"] = 20.0, ["acc"] = 0.0 }),
    };

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    private const string EpicSword = """{"Version":2,"Rarity":2,"Effects":[{"EffectType":"ModifyDamage","EffectValue":12.5},{"EffectType":"LifeSteal","EffectValue":0.05}],"DisplayName":"Doom Blade","LegendaryID":null,"SetID":""}""";

    [Fact]
    public void Magic_ReadsEpicLootJson()
    {
        var magic = SnapshotLogic.Magic(Item("SwordIron", magic: EpicSword));
        Assert.NotNull(magic);
        Assert.Equal(2, magic.Rarity);
        Assert.Equal("Epic", magic.RarityName);
        Assert.Equal("Doom Blade", magic.DisplayName);
        Assert.Null(magic.SetId);
        Assert.Equal(2, magic.Effects.Count);
        Assert.Equal(new MagicEffect("ModifyDamage", 12.5), magic.Effects[0]);
    }

    [Fact]
    public void Magic_AcceptsRarityAsNameAndLowercaseKeys()
    {
        var magic = SnapshotLogic.Magic(Item("Bow", magic: """{"rarity":"Legendary","effects":[]}"""));
        Assert.Equal(3, magic!.Rarity);
    }

    [Fact]
    public void Magic_IsNullForPlainAndBrokenItems()
    {
        Assert.Null(SnapshotLogic.Magic(Item("Wood")));
        Assert.Null(SnapshotLogic.Magic(Item("Bow", magic: "{not json")));
    }

    [Fact]
    public void ContentHash_IgnoresTooltipsOrderAndMetadata()
    {
        var a = Snap(1, Item("SwordIron", x: 0, magic: EpicSword), Item("Wood", 50, 50, x: 1));
        var b = Snap(1, Item("Wood", 50, 50, x: 1), Item("SwordIron", x: 0, magic: EpicSword));
        b["takenAt"] = "later";
        ((JsonObject)b["items"]![0]!)["tooltip"] = "different text";
        Assert.Equal(SnapshotLogic.ContentHash(a), SnapshotLogic.ContentHash(b));
    }

    [Fact]
    public void ContentHash_ChangesWithEnchantmentsStacksAndSkills()
    {
        string baseHash = SnapshotLogic.ContentHash(Snap(1, Item("SwordIron", magic: EpicSword), Item("Wood", 50, 50, x: 1)));
        Assert.NotEqual(baseHash, SnapshotLogic.ContentHash(Snap(1, Item("SwordIron"), Item("Wood", 50, 50, x: 1))));
        Assert.NotEqual(baseHash, SnapshotLogic.ContentHash(Snap(1, Item("SwordIron", magic: EpicSword), Item("Wood", 49, 50, x: 1))));

        var skills = Snap(1, Item("SwordIron", magic: EpicSword), Item("Wood", 50, 50, x: 1));
        ((JsonObject)skills["skills"]![0]!)["level"] = 41.0;
        Assert.NotEqual(baseHash, SnapshotLogic.ContentHash(skills));
    }

    [Fact]
    public void Diff_FindsLostEnchantedItemEvenIfSamePrefabRemains()
    {
        // The player still has an iron sword, but not the enchanted one.
        var snapshot = Snap(1, Item("SwordIron", magic: EpicSword), Item("Wood", 50, 50, x: 1));
        var live = Snap(1, Item("SwordIron", x: 3), Item("Wood", 50, 50, x: 1));
        var diff = SnapshotLogic.Diff(snapshot, live);
        Assert.Equal([new ItemDiff(0, 1)], diff);
    }

    [Fact]
    public void Diff_ComparesStackTotalsAcrossSlots()
    {
        var snapshot = Snap(1, Item("Wood", 50, 50), Item("Wood", 30, 50, x: 1), Item("Stone", 20, 50, x: 2));
        var live = Snap(1, Item("Wood", 45, 50, x: 5), Item("Stone", 20, 50, x: 2));
        var diff = SnapshotLogic.Diff(snapshot, live);
        // 80 wood before, 45 now: 35 missing, taken from the slots in order.
        Assert.Equal([new ItemDiff(0, 5), new ItemDiff(1, 30)], diff);
    }

    [Fact]
    public void Diff_EmptyWhenNothingLost()
    {
        var snapshot = Snap(1, Item("SwordIron", magic: EpicSword), Item("Wood", 50, 50, x: 1));
        Assert.Empty(SnapshotLogic.Diff(snapshot, snapshot));
    }

    [Fact]
    public void DiffSkills_ReportsOnlyLoweredSkills()
    {
        var snapshot = Snap(1);
        var live = Snap(1);
        ((JsonObject)live["skills"]![0]!)["level"] = 35.0;
        ((JsonObject)live["skills"]![1]!)["level"] = 25.0;
        var diff = SnapshotLogic.DiffSkills(snapshot, live);
        var d = Assert.Single(diff);
        Assert.Equal(1, d.Type);
        Assert.Equal(35.0, d.CurrentLevel);
    }

    [Fact]
    public void BuildRestore_SelectsItemsAndOverridesStack()
    {
        var snapshot = Snap(1, Item("SwordIron", magic: EpicSword), Item("Wood", 50, 50, x: 1));
        var payload = SnapshotLogic.BuildRestore(snapshot, "add", [new RestoreItem(1, 20), new RestoreItem(7, null)], "raise");
        var items = payload["items"]!.AsArray();
        var wood = Assert.Single(items)!.AsObject();
        Assert.Equal(20, SnapshotLogic.Int(wood, "stack"));
        Assert.Null(wood["tooltip"]);
        Assert.Equal("raise", payload["skillMode"]!.GetValue<string>());
        Assert.Equal(2, payload["skills"]!.AsArray().Count);
    }

    [Fact]
    public void BuildRestore_AllItemsKeepCustomData()
    {
        var snapshot = Snap(1, Item("SwordIron", magic: EpicSword), Item("Wood", 50, 50, x: 1));
        var payload = SnapshotLogic.BuildRestore(snapshot, "replace", null, "none");
        var items = payload["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal(EpicSword, items[0]!["data"]![MagicKey]!.GetValue<string>());
        Assert.Equal(50, SnapshotLogic.Int(items[1]!.AsObject(), "stack"));
    }

    [Fact]
    public void Summary_CountsItemsRaritiesAndEquipped()
    {
        var s = SnapshotLogic.Summary(Snap(1, Item("SwordIron", magic: EpicSword, equipped: true), Item("Wood", 50, 50, x: 1)));
        Assert.Equal(2, s["items"]!.GetValue<int>());
        Assert.Equal(1, s["magic"]!["Epic"]!.GetValue<int>());
        Assert.Equal("SwordIron", s["equipped"]![0]!.GetValue<string>());
        Assert.Equal(60.5, s["skillTotal"]!.GetValue<double>());
    }

    [Fact]
    public void Retention_KeepsRecentDailyAndNewest()
    {
        var tz = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        long At(double daysAgo) => now.AddDays(-daysAgo).ToUnixTimeMilliseconds();
        var rows = new List<(long, long, long)>
        {
            (1, 7, At(0.1)), (2, 7, At(1)), (3, 7, At(2.9)),          // within 3 days: all kept
            (4, 7, At(5.0)), (5, 7, At(5.1)), (6, 7, At(5.2)),         // same day: only the latest kept
            (7, 7, At(70)),                                            // beyond 60 days: deleted
            (8, 9, At(200)),                                           // newest of another character: kept
            (9, 9, At(201)),
        };
        var delete = SnapshotLogic.Retention(rows, now, 3, 60, tz);
        Assert.Equal([5L, 6L, 7L, 9L], delete.Order());
    }

    [Fact]
    public void Parse_HandlesSnapshotFromPlugin()
    {
        // Shape produced by the plugin's MiniJson: numbers without decimals are integers.
        var snap = Parse("""{"v":1,"name":"Ragnar","characterId":123456789012,"inventory":{"w":8,"h":4},"items":[{"prefab":"Wood","stack":5,"maxStack":50,"quality":1,"x":0,"y":0,"data":{}}],"skills":[{"type":1,"name":"Swords","level":12.5,"acc":0}]}""");
        Assert.Single(SnapshotLogic.Items(snap));
        Assert.Equal(5, SnapshotLogic.Int(SnapshotLogic.Items(snap)[0]!.AsObject(), "stack"));
        Assert.Equal(12.5, SnapshotLogic.Summary(snap)["skillTotal"]!.GetValue<double>());
    }
}
