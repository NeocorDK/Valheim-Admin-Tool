using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ValheimAdmin.Agent.Data;
using ValheimAdmin.Agent.Logs;
using ValheimAdmin.Agent.Server;
using ValheimAdmin.Agent.Snapshots;
using Xunit;

namespace ValheimAdmin.Agent.Tests;

public class CommandParserTests
{
    [Theory]
    [InlineData("players", "players")]
    [InlineData("save", "save")]
    [InlineData("stopevent", "event_stop")]
    [InlineData("admins", "list")]
    public void SimpleCommands(string line, string cmd) => Assert.Equal(cmd, CommandParser.Parse(line).Cmd);

    [Fact]
    public void PlayerConsoleCommandKeepsTheRestOfTheLine()
    {
        var p = CommandParser.Parse("@Ragnar raiseskill Swords 10");
        Assert.Equal("cheat", p.Cmd);
        Assert.Equal("Ragnar", p.Args["player"]!.GetValue<string>());
        Assert.Equal("raiseskill Swords 10", p.Args["command"]!.GetValue<string>());
    }

    [Fact]
    public void QuotedPlayerNames()
    {
        var p = CommandParser.Parse("@\"Big Olaf\" god");
        Assert.Equal("Big Olaf", p.Args["player"]!.GetValue<string>());
        Assert.Equal("god", p.Args["command"]!.GetValue<string>());

        var g = CommandParser.Parse("give \"Big Olaf\" Wood 50 2");
        Assert.Equal("Big Olaf", g.Args["player"]!.GetValue<string>());
        Assert.Equal(50, g.Args["count"]!.GetValue<int>());
        Assert.Equal(2, g.Args["quality"]!.GetValue<int>());
    }

    [Fact]
    public void SayKeepsSpacing()
    {
        var p = CommandParser.Parse("say  Restart in 5 minutes!");
        Assert.Equal("broadcast", p.Cmd);
        Assert.Equal("Restart in 5 minutes!", p.Args["text"]!.GetValue<string>());
    }

    [Fact]
    public void ListChanges()
    {
        var p = CommandParser.Parse("permit add 76561198000000000");
        Assert.Equal("list_add", p.Cmd);
        Assert.Equal("permitted", p.Args["list"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("listkeys", "listkeys")]
    [InlineData("skiptime 100", "skiptime 100")]
    [InlineData("/event army_eikthyr", "event army_eikthyr")]
    [InlineData("/  kick Ragnar", "kick Ragnar")]
    [InlineData("expand_world_reload", "expand_world_reload")]
    public void OtherLinesRunAsGameCommands(string line, string game)
    {
        var p = CommandParser.Parse(line);
        Assert.Equal("exec", p.Cmd);
        Assert.Equal(game, p.Args["line"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("give Ragnar")]
    [InlineData("give Ragnar Wood many")]
    [InlineData("admin grant 1")]
    [InlineData("@Ragnar")]
    public void BadInputThrows(string line) => Assert.Throws<FormatException>(() => CommandParser.Parse(line));
}

public class LogClassifierTests
{
    [Theory]
    [InlineData("[Info   : Unity Log] 09/24/2026 12:00:00: Got connection SteamID 7656", "info")]
    [InlineData("[Warning:   Epic Loot] Missing effect", "warning")]
    [InlineData("[Error  : Unity Log] NullReferenceException", "error")]
    [InlineData("[Fatal  : BepInEx] boom", "error")]
    [InlineData("[Debug  :Valheim Admin] hb", "debug")]
    [InlineData("  at ZNet.Update () [0x00000]", "error")]
    [InlineData("plain line", "info")]
    public void Levels(string line, string level) => Assert.Equal(level, LogClassifier.Level(line));
}

public class I18nTests
{
    [Fact]
    public void EveryKeyHasBothLanguages()
    {
        foreach (var (key, en, ru) in I18n.All())
        {
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.False(string.IsNullOrWhiteSpace(ru), key);
            Assert.Equal(en.Count(c => c == '{'), ru.Count(c => c == '{'));
        }
    }

    [Fact]
    public void Formats()
    {
        I18n.Language = "ru";
        Assert.Equal("Ragnar погиб: утонул", I18n.T("death", "Ragnar", I18n.DeathCause("Drowning")));
        I18n.Language = "en";
        Assert.Equal("Boss defeated: Moder", I18n.T("boss", I18n.BossName("defeated_dragon")));
        Assert.Equal("SomethingNew", I18n.DeathCause("SomethingNew"));
    }
}

public class AgentConfigTests
{
    [Fact]
    public void SplitArgsKeepsQuotes() =>
        Assert.Equal(["-modifier", "raids", "none", "-name", "My server"], AgentConfig.SplitArgs("-modifier raids none -name \"My server\""));

    [Fact]
    public void PasswordHashRoundTrip()
    {
        var c = new AgentConfig { PanelPasswordHash = AgentConfig.HashPassword("correct horse") };
        Assert.True(c.CheckPassword("correct horse"));
        Assert.False(c.CheckPassword("wrong"));
    }

    [Fact]
    public void ServerArgumentsIncludeSaveDir()
    {
        var c = new AgentConfig();
        c.Server.SaveDir = @"D:\saves";
        c.Server.Crossplay = true;
        var args = c.BuildServerArguments();
        Assert.Equal(@"D:\saves", args[args.IndexOf("-savedir") + 1]);
        Assert.Contains("-crossplay", args);
        Assert.Contains("-nographics", args);
    }

    [Fact]
    public void ServerIsPublicByDefaultLikeValheim()
    {
        // -public 0 hides the server status in the in-game Favorites list.
        var args = new AgentConfig().BuildServerArguments();
        Assert.Equal("1", args[args.IndexOf("-public") + 1]);
    }
}

public class SnapshotStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "va-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public void StoresDeduplicatesAndReadsBack()
    {
        var db = new Db(Path.Combine(dir, "t.db"));
        var store = new SnapshotStore(db, new AgentConfig(), NullLogger<SnapshotStore>.Instance);
        var snap = SnapshotLogicTests.Snap(42, SnapshotLogicTests.Item("SwordIron"), SnapshotLogicTests.Item("Wood", 50, 50, x: 1));

        long a = store.Add(snap, "save", "Steam_1", 1000);
        long b = store.Add(snap, "save", "Steam_1", 2000);
        var changed = (JsonObject)snap.DeepClone();
        changed["items"]!.AsArray().RemoveAt(0);
        long c = store.Add(changed, "manual", "Steam_1", 3000);

        var rows = store.List(42);
        Assert.Equal([c, b, a], rows.Select(r => r.Id));
        Assert.False(store.Row(a)!.Unchanged);
        Assert.True(store.Row(b)!.Unchanged);
        Assert.False(store.Row(c)!.Unchanged);
        Assert.Equal(2L, db.Scalar("SELECT COUNT(*) FROM snapshot_data"));

        var content = store.Content(b)!;
        Assert.Equal(2, SnapshotLogic.Items(content).Count);
        var character = Assert.Single(store.Characters());
        Assert.Equal(3, character.Count);
        Assert.Equal(3000, character.LastTs);
    }

    [Fact]
    public void SessionsOpenAndClose()
    {
        var db = new Db(Path.Combine(dir, "s.db"));
        db.OpenSession("Ragnar", "Steam_1", 1000);
        db.OpenSession("Olaf", "Steam_2", 1000);
        Assert.True(db.HasOpenSession("Ragnar"));
        db.CloseSessionsExcept(["olaf"], 5000);
        Assert.False(db.HasOpenSession("Ragnar"));
        Assert.True(db.HasOpenSession("Olaf"));
        Assert.Equal(5000L, db.Scalar("SELECT end_ts FROM sessions WHERE player='Ragnar'"));
    }
}

public class SchedulerTests
{
    private static Scheduler Make(params string[] times) =>
        new(new AgentConfig { Restarts = { Enabled = true, Times = [.. times] } }, null!, null!, null!, NullLogger<Scheduler>.Instance);

    private static DateTimeOffset Local(int day, int hour, int minute) =>
        new(new DateTime(2026, 9, day, hour, minute, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, day, hour, minute, 0)));

    [Fact]
    public void TodaysTimeIsNextUntilItHasBeenUsed()
    {
        var s = Make("08:00");
        Assert.Equal(Local(28, 8, 0), s.NextScheduled(Local(28, 7, 59))!.At);
        // Once 08:00 itself is the cursor, the next one is tomorrow.
        Assert.Equal(Local(29, 8, 0), s.NextScheduled(Local(28, 8, 0))!.At);
    }

    [Fact]
    public void PicksTheEarliestOfSeveralTimes()
    {
        var s = Make("20:00", "06:00");
        Assert.Equal(Local(28, 20, 0), s.NextScheduled(Local(28, 7, 0))!.At);
        Assert.Equal(Local(29, 6, 0), s.NextScheduled(Local(28, 21, 0))!.At);
    }

    [Fact]
    public void DisabledScheduleHasNoRestarts()
    {
        var s = new Scheduler(new AgentConfig { Restarts = { Enabled = false, Times = ["08:00"] } }, null!, null!, null!, NullLogger<Scheduler>.Instance);
        Assert.Null(s.NextScheduled(Local(28, 7, 0)));
    }
}

public class SharedMapDataTests
{
    /// <summary>Bytes laid out like Minimap.GetSharedMapData writes them.</summary>
    private static byte[] Table(int version, int cells, bool[] explored, params (long owner, string name, float x, float z, int type, bool done, string author)[] pins)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(version);
            w.Write(cells);
            for (int i = 0; i < cells; i++) w.Write(i < explored.Length && explored[i]);
            if (version >= 2)
            {
                w.Write(pins.Length);
                foreach (var p in pins)
                {
                    w.Write(p.owner);
                    w.Write(p.name);
                    w.Write(p.x);
                    w.Write(31f);
                    w.Write(p.z);
                    w.Write(p.type);
                    w.Write(p.done);
                    if (version >= 3) w.Write(p.author);
                }
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void ReadsVersion3PinsAndExploredGrid()
    {
        var data = Table(3, 16, [true, false, false, true], (42L, "Крипта", -120.5f, 800f, 6, true, "Steam_1"), (0L, "", 1f, 2f, 0, false, ""));
        var map = ValheimAdmin.Shared.SharedMapData.Parse(data);
        Assert.Equal(3, map.Version);
        Assert.Equal(4, map.Size);
        Assert.True(map.Explored![0]);
        Assert.False(map.Explored[1]);
        Assert.True(map.Explored[3]);
        Assert.Equal(2, map.Pins.Count);
        Assert.Equal(42L, map.Pins[0].OwnerId);
        Assert.Equal("Крипта", map.Pins[0].Name);
        Assert.Equal(-120.5f, map.Pins[0].X);
        Assert.Equal(800f, map.Pins[0].Z);
        Assert.Equal(6, map.Pins[0].Type);
        Assert.True(map.Pins[0].Checked);
        Assert.Equal("Steam_1", map.Pins[0].Author);
    }

    [Fact]
    public void ReadsVersion2WithoutAuthor()
    {
        var map = ValheimAdmin.Shared.SharedMapData.Parse(Table(2, 4, [], (7L, "home", 5f, 6f, 1, false, "")));
        Assert.Single(map.Pins);
        Assert.Null(map.Pins[0].Author);
        Assert.Equal("home", map.Pins[0].Name);
    }

    [Fact]
    public void Version1HasNoPinsAndOddGridHasNoExplored()
    {
        var map = ValheimAdmin.Shared.SharedMapData.Parse(Table(1, 5, [true]));
        Assert.Empty(map.Pins);
        Assert.Null(map.Explored);
    }

    [Fact]
    public void TruncatedDataThrows()
    {
        var data = Table(3, 4, [], (1L, "x", 0f, 0f, 0, false, "a"));
        Assert.ThrowsAny<Exception>(() => ValheimAdmin.Shared.SharedMapData.Parse(data[..^3]));
    }
}
