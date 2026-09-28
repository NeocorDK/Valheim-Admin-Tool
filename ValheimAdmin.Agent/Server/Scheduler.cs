using System.Globalization;
using ValheimAdmin.Agent.Bridge;
using ValheimAdmin.Agent.Snapshots;
using static ValheimAdmin.Agent.I18n;

namespace ValheimAdmin.Agent.Server;

public sealed record PlannedRestart(DateTimeOffset At, string Reason, bool Manual);

/// <summary>Daily restarts with in-game countdown, one-off restarts from the panel, snapshot retention.</summary>
public sealed class Scheduler(AgentConfig config, ServerManager server, PluginBridge bridge, SnapshotStore snapshots, ILogger<Scheduler> log)
    : BackgroundService
{
    private readonly Lock gate = new();
    private readonly HashSet<int> warned = [];
    private PlannedRestart? manual;
    // Scheduled times at or before the cursor are done (fired, skipped or cancelled). Looking for the
    // next time after "now" instead would jump to tomorrow the moment today's time passes, and the
    // restart would never fire.
    private DateTimeOffset cursor = DateTimeOffset.Now;
    private DateTimeOffset? warnedFor;
    private DateTimeOffset lastRetention = DateTimeOffset.MinValue;
    private static readonly TimeSpan MissedGrace = TimeSpan.FromMinutes(10);

    public PlannedRestart? Next
    {
        get
        {
            lock (gate)
                return manual ?? NextScheduled(cursor);
        }
    }

    public void RestartIn(int minutes)
    {
        lock (gate)
            manual = new PlannedRestart(DateTimeOffset.Now.AddMinutes(Math.Max(0, minutes)), T("reason.manual"), true);
    }

    /// <summary>Cancels the one-off restart, or skips the next scheduled one.</summary>
    public void Cancel()
    {
        lock (gate)
        {
            if (manual != null)
                manual = null;
            else if (NextScheduled(cursor) is { } next)
                cursor = next.At;
        }
    }

    /// <summary>The first scheduled restart strictly after <paramref name="after"/>.</summary>
    public PlannedRestart? NextScheduled(DateTimeOffset after)
    {
        if (!config.Restarts.Enabled) return null;
        DateTimeOffset? best = null;
        foreach (string time in config.Restarts.Times)
        {
            if (!TimeOnly.TryParseExact(time, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
            for (int day = 0; day < 3; day++)
            {
                var local = after.LocalDateTime.Date.AddDays(day).Add(t.ToTimeSpan());
                var at = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
                if (at <= after) continue;
                if (best == null || at < best) best = at;
                break;
            }
        }
        return best == null ? null : new PlannedRestart(best.Value, T("reason.scheduled"), false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A one-off restart belongs to the server run it was set for: once that run ends
        // (stopped by hand, crashed, restarted), it must not fire on the next one.
        server.Exited += () =>
        {
            lock (gate)
                manual = null;
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Tick();
            }
            catch (Exception e)
            {
                log.LogError(e, "Scheduler tick failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ContinueWith(_ => { });
        }
    }

    private async Task Tick()
    {
        var now = DateTimeOffset.Now;
        if (now - lastRetention > TimeSpan.FromHours(1))
        {
            lastRetention = now;
            snapshots.ApplyRetention();
        }

        lock (gate)
        {
            // A one-off restart takes the place of a scheduled one that comes due meanwhile.
            if (manual != null && NextScheduled(cursor) is { } due && due.At <= now)
                cursor = due.At;
        }

        var next = Next;
        if (next == null) return;
        var left = next.At - now;

        if (!next.Manual && left <= TimeSpan.Zero)
        {
            // A scheduled time is used once: it fires now or is dropped (server down or still loading,
            // or the agent was not running at that time), and the cursor moves on to the next one.
            bool fire = server.IsRunning && server.Status().WorldReady && -left < MissedGrace;
            lock (gate)
                if (cursor < next.At) cursor = next.At;
            if (!fire)
            {
                log.LogInformation("Scheduled restart at {At} skipped: server not running or not ready", next.At);
                return;
            }
        }

        if (!server.IsRunning) return;
        if (warnedFor != next.At)
        {
            warnedFor = next.At;
            warned.Clear();
        }

        if (left > TimeSpan.Zero)
        {
            // Announce the smallest threshold already reached, once; skip the bigger ones already passed.
            var due = config.Restarts.WarnMinutes.Where(m => left <= TimeSpan.FromMinutes(m) && !warned.Contains(m)).ToList();
            if (due.Count == 0) return;
            foreach (int m in due) warned.Add(m);
            int minutes = (int)Math.Ceiling(left.TotalMinutes);
            await bridge.TryRequestAsync("broadcast", new { text = T("game.restart.in", minutes), center = true });
            return;
        }

        // A one-off restart waits until the world has loaded: a loading server cannot save.
        if (next.Manual && !server.Status().WorldReady) return;

        if (next.Manual)
            lock (gate)
                manual = null;
        await bridge.TryRequestAsync("broadcast", new { text = T("game.restart.now"), center = true });
        log.LogInformation("Restart: {Reason}", next.Reason);
        await server.RestartAsync(next.Reason);
    }
}
