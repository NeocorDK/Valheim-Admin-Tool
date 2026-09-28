using System.Text.Json.Nodes;
using ValheimAdmin.Agent.Bridge;
using ValheimAdmin.Agent.Data;
using ValheimAdmin.Agent.Snapshots;
using static ValheimAdmin.Agent.I18n;

namespace ValheimAdmin.Agent.Server;

public sealed record RestoreRequest(string Mode, List<RestoreItem>? Items, string SkillMode);

/// <summary>Higher-level operations on top of the bridge: online players, item list, snapshot diff and restore.</summary>
public sealed class AdminService
{
    private readonly PluginBridge bridge;
    private readonly SnapshotStore snapshots;
    private readonly EventService events;
    private readonly Db db;
    private JsonArray? items;
    private JsonArray? gameCommands;
    private JsonArray players = [];
    private DateTimeOffset playersAt = DateTimeOffset.MinValue;

    public AdminService(PluginBridge bridge, SnapshotStore snapshots, EventService events, Db db)
    {
        this.bridge = bridge;
        this.snapshots = snapshots;
        this.events = events;
        this.db = db;
        bridge.ConnectionChanged += () =>
        {
            items = null;
            gameCommands = null;
            players = [];
            playersAt = DateTimeOffset.MinValue;
        };
        bridge.EventReceived += ev =>
        {
            if (ev.Kind is "join" or "leave" or "mod") playersAt = DateTimeOffset.MinValue;
        };
    }

    public async Task<JsonArray> PlayersAsync(bool fresh = false)
    {
        if (!bridge.Connected || !bridge.WorldReady) return [];
        if (fresh || DateTimeOffset.UtcNow - playersAt > TimeSpan.FromSeconds(5))
        {
            if (await bridge.TryRequestAsync("players") is JsonArray list)
            {
                players = list;
                playersAt = DateTimeOffset.UtcNow;
                ReconcileSessions(list);
            }
        }
        return players;
    }

    /// <summary>After agent or server restarts, make the session table match who is actually online.</summary>
    private void ReconcileSessions(JsonArray online)
    {
        var names = online.OfType<JsonObject>().Select(p => SnapshotLogic.Str(p, "player")).OfType<string>().ToList();
        long now = Db.Now;
        db.CloseSessionsExcept(names, now);
        foreach (var p in online.OfType<JsonObject>())
        {
            string? name = SnapshotLogic.Str(p, "player");
            if (name != null && !db.HasOpenSession(name))
                db.OpenSession(name, SnapshotLogic.Str(p, "host"), now);
        }
    }

    public async Task<JsonArray> ItemsAsync()
    {
        if (items == null && bridge.Connected && await bridge.TryRequestAsync("items", timeout: TimeSpan.FromSeconds(30)) is JsonArray list)
            items = list;
        return items ?? [];
    }

    /// <summary>Console commands the game and its mods registered on the server; they change only with a restart.</summary>
    public async Task<JsonArray> GameCommandsAsync()
    {
        if (gameCommands == null && bridge.Connected && bridge.WorldReady && await bridge.TryRequestAsync("commands") is JsonArray list)
            gameCommands = list;
        return gameCommands ?? [];
    }

    /// <summary>The online player who owns this character: same character id, or same name with the mod.</summary>
    public async Task<JsonObject> FindOnlineAsync(SnapshotRow row)
    {
        var online = await PlayersAsync(fresh: true);
        var byId = online.OfType<JsonObject>().FirstOrDefault(p => p["characterId"]?.GetValue<long>() == row.CharacterId);
        var match = byId ?? online.OfType<JsonObject>().FirstOrDefault(p =>
            string.Equals(SnapshotLogic.Str(p, "player"), row.Player, StringComparison.OrdinalIgnoreCase) && p["mod"] != null);
        return match ?? throw new BridgeException($"{row.Player}: not online or has no Valheim Admin mod");
    }

    public async Task<JsonObject> LiveSnapshotAsync(string player, string trigger)
    {
        var node = await bridge.RequestAsync("snapshot", new { player, trigger }, TimeSpan.FromSeconds(40));
        return node as JsonObject ?? throw new BridgeException("Empty snapshot");
    }

    public async Task<object> DiffAsync(long snapshotId)
    {
        var row = snapshots.Row(snapshotId) ?? throw new KeyNotFoundException();
        var content = snapshots.Content(snapshotId) ?? throw new KeyNotFoundException();
        var player = await FindOnlineAsync(row);
        var live = await LiveSnapshotAsync(SnapshotLogic.Str(player, "player")!, "live");
        return new
        {
            items = SnapshotLogic.Diff(content, live),
            skills = SnapshotLogic.DiffSkills(content, live),
            live = Enrich(live),
        };
    }

    public async Task<JsonNode?> RestoreAsync(long snapshotId, RestoreRequest request, string who)
    {
        if (request.Mode is not ("add" or "replace")) throw new ArgumentException("mode must be add or replace");
        if (request.SkillMode is not ("none" or "raise" or "set")) throw new ArgumentException("skillMode must be none, raise or set");

        var row = snapshots.Row(snapshotId) ?? throw new KeyNotFoundException();
        var content = snapshots.Content(snapshotId) ?? throw new KeyNotFoundException();
        var player = await FindOnlineAsync(row);
        string name = SnapshotLogic.Str(player, "player")!;

        // Safety net: the current state is stored before anything changes.
        await LiveSnapshotAsync(name, "pre-restore");

        var payload = SnapshotLogic.BuildRestore(content, request.Mode, request.Items, request.SkillMode);
        var report = await bridge.RequestAsync("restore", new JsonObject { ["player"] = name, ["payload"] = payload }, TimeSpan.FromSeconds(40));

        var r = report as JsonObject ?? [];
        int added = r["added"]?.GetValue<int>() ?? 0, dropped = r["dropped"]?.GetValue<int>() ?? 0, skills = r["skills"]?.GetValue<int>() ?? 0;
        events.Record("restore", name, T("restore", name, snapshotId, added, dropped, skills),
            new JsonObject { ["snapshot"] = snapshotId, ["mode"] = request.Mode, ["skillMode"] = request.SkillMode, ["report"] = r.DeepClone() });
        db.Audit(who, "restore", $"snapshot #{snapshotId} -> {name}, mode {request.Mode}, items {payload["items"]!.AsArray().Count}, skills {request.SkillMode}");
        return report;
    }

    /// <summary>Adds parsed Epic Loot data to each item for the panel.</summary>
    public static JsonObject Enrich(JsonObject snapshot)
    {
        foreach (var item in SnapshotLogic.Items(snapshot).OfType<JsonObject>())
        {
            var magic = SnapshotLogic.Magic(item);
            if (magic != null)
                item["magic"] = System.Text.Json.JsonSerializer.SerializeToNode(magic, Web.LiveHub.JsonOptions);
        }
        return snapshot;
    }
}
