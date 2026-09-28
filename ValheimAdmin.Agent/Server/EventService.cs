using System.Text.Json.Nodes;
using ValheimAdmin.Agent.Bridge;
using ValheimAdmin.Agent.Data;
using ValheimAdmin.Agent.Snapshots;
using ValheimAdmin.Agent.Web;
using static ValheimAdmin.Agent.I18n;

namespace ValheimAdmin.Agent.Server;

public sealed record EventRow(long Id, long Ts, string Kind, string? Player, string Message, JsonObject? Data);

/// <summary>Stores events from the plugin and from the agent itself and pushes them to the panel.</summary>
public sealed class EventService
{
    private readonly Db db;
    private readonly SnapshotStore snapshots;
    private readonly LiveHub hub;
    private readonly ILogger<EventService> log;

    public EventService(Db db, SnapshotStore snapshots, LiveHub hub, PluginBridge bridge, ILogger<EventService> log)
    {
        this.db = db;
        this.snapshots = snapshots;
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
                Record("join", player, T("join", player), d, ev.Ts);
                hub.Broadcast("players", new { });
                break;

            case "leave":
                if (player != null) db.CloseSession(player, ts);
                Record("leave", player, T("leave", player), d, ev.Ts);
                hub.Broadcast("players", new { });
                break;

            case "mod":
                hub.Broadcast("players", new { });
                break;

            case "death":
                string? attacker = Str(d, "attacker");
                Record("death", player, T("death", player, DeathCause(Str(d, "cause"))) + (attacker != null ? $" ({attacker})" : ""), d, ev.Ts);
                break;

            case "chat":
                string prefix = Str(d, "type") switch { "Shout" => T("chat.shout"), "Whisper" => T("chat.whisper"), _ => "" };
                Record("chat", player, $"{prefix}{player}: {Str(d, "text")}", d, ev.Ts);
                break;

            case "boss":
                Record("boss", player, T("boss", BossName(Str(d, "key"))), d, ev.Ts);
                break;

            case "globalkey":
                Record("globalkey", player, T("globalkey", Str(d, "key")), d, ev.Ts);
                break;

            case "raid":
                string? near = Str(d, "near");
                Record("raid", near, near != null ? T("raid.near", Str(d, "name"), near) : T("raid", Str(d, "name")), d, ev.Ts);
                break;

            case "raid_end":
                Record("raid", null, T("raid_end", Str(d, "name")), d, ev.Ts);
                break;

            case "save":
                int n = d["snapshots"]?.GetValue<int>() ?? 0;
                Record("save", null, n > 0 ? T("save.snapshots", n) : T("save"), d, ev.Ts);
                break;

            case "started":
                db.CloseSessionsExcept(null, ts);
                Record("server", null, T("started", Str(d, "world")), d, ev.Ts);
                break;

            case "stopping":
                Record("server", null, T("stopping"), d, ev.Ts);
                break;

            case "snapshot":
                StoreSnapshot(d, ts);
                break;

            default:
                log.LogDebug("Unhandled plugin event {Kind}", ev.Kind);
                break;
        }
    }

    private void StoreSnapshot(JsonObject d, long ts)
    {
        string trigger = Str(d, "trigger") ?? "save";
        if (trigger == "live" || d["snapshot"] is not JsonObject snap) return;
        long id = snapshots.Add(snap, trigger, Str(d, "host"), ts);
        hub.Broadcast("snapshot", new { id, player = Str(d, "player"), trigger });
    }

    private static string? Str(JsonObject d, string key) => SnapshotLogic.Str(d, key) is { Length: > 0 } s ? s : null;
}
