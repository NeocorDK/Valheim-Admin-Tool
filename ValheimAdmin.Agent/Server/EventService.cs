using System.Text.Json.Nodes;
using ValheimAdmin.Agent.Bridge;
using ValheimAdmin.Agent.Data;
using ValheimAdmin.Agent.Snapshots;
using ValheimAdmin.Agent.Web;

namespace ValheimAdmin.Agent.Server;

public sealed record EventRow(long Id, long Ts, string Kind, string? Player, string Message, JsonObject? Data);

/// <summary>Stores events from the plugin and from the agent itself and pushes them to the panel.</summary>
public sealed class EventService
{
    private readonly Db db;
    private readonly SnapshotStore snapshots;
    private readonly IconStore icons;
    private readonly LiveHub hub;
    private readonly ILogger<EventService> log;

    public EventService(Db db, SnapshotStore snapshots, IconStore icons, LiveHub hub, PluginBridge bridge, ILogger<EventService> log)
    {
        this.db = db;
        this.snapshots = snapshots;
        this.icons = icons;
        this.hub = hub;
        this.log = log;
        bridge.EventReceived += OnPluginEvent;
    }

    public void Record(string kind, string? player, string message, JsonObject? data = null, DateTimeOffset? ts = null)
    {
        long time = (ts ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        long id = db.AddEvent(time, kind, player, message, data?.ToJsonString());
        hub.Broadcast("event", new EventRow(id, time, kind, player, message, data));
    }

    private void OnPluginEvent(PluginEvent ev)
    {
        JsonObject d = ev.Data;
        string? player = Str(d, "player");
        long ts = ev.Ts.ToUnixTimeMilliseconds();
        switch (ev.Kind)
        {
            case "join":
                if (player != null) db.OpenSession(player, Str(d, "host"), ts);
                break;
            case "leave":
                if (player != null) db.CloseSession(player, ts);
                break;
            case "started":
                db.CloseSessionsExcept(null, ts);
                break;
            case "snapshot":
                StoreSnapshot(d, ts);
                return;
        }

        // The texts are shared with the plugin's own event log (standalone mode).
        var entry = Shared.EventText.Describe(ev.Kind, JsonCompat.ToDict(d));
        if (entry != null)
            Record(entry.Kind, entry.Player, entry.Message, d, ev.Ts);
        else if (ev.Kind != "mod")
            log.LogDebug("Unhandled plugin event {Kind}", ev.Kind);
        if (ev.Kind is "join" or "leave" or "mod")
            hub.Broadcast("players", new { });
    }

    private void StoreSnapshot(JsonObject d, long ts)
    {
        string trigger = Str(d, "trigger") ?? "save";
        if (trigger == "live" || d["snapshot"] is not JsonObject snap) return;
        long id = snapshots.Add(snap, trigger, Str(d, "host"), ts);
        icons.EnsureFor(snap);
        hub.Broadcast("snapshot", new { id, player = Str(d, "player"), trigger });
    }

    private static string? Str(JsonObject d, string key) => SnapshotLogic.Str(d, key) is { Length: > 0 } s ? s : null;
}
