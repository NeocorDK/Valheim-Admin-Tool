using System.Collections.Concurrent;
using ValheimAdmin.Agent.Server;

namespace ValheimAdmin.Agent.Web;

/// <summary>Sends the overview status to open panels every two seconds.</summary>
public sealed class StatusPump(LiveHub hub, StatusBuilder status) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (hub.Count > 0)
            {
                try
                {
                    hub.Broadcast("status", await status.BuildAsync());
                }
                catch (Exception)
                {
                    // Status is best effort; the next tick tries again.
                }
            }
            await Task.Delay(2000, stoppingToken).ContinueWith(_ => { });
        }
    }
}

public sealed class StatusBuilder(AgentConfig config, ServerManager server, Scheduler scheduler, Updater updater, AdminService admin)
{
    public async Task<object> BuildAsync() => new
    {
        agent = new { version = typeof(StatusBuilder).Assembly.GetName().Version?.ToString(3), language = config.Language, world = config.Server.World },
        server = server.Status(),
        players = await admin.PlayersAsync(),
        nextRestart = scheduler.Next,
        update = new { running = updater.Running, buildId = updater.BuildId(), available = !string.IsNullOrWhiteSpace(config.SteamCmdPath) },
    };
}

/// <summary>Slows down password guessing: five failures from one address lock it out for ten minutes.</summary>
public sealed class LoginGuard
{
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Since)> state = new();

    public bool IsLocked(string who) =>
        state.TryGetValue(who, out var s) && s.Failures >= 5 && DateTimeOffset.UtcNow - s.Since < TimeSpan.FromMinutes(10);

    public void Fail(string who) =>
        state.AddOrUpdate(who, _ => (1, DateTimeOffset.UtcNow),
            (_, s) => DateTimeOffset.UtcNow - s.Since > TimeSpan.FromMinutes(10) ? (1, DateTimeOffset.UtcNow) : (s.Failures + 1, s.Since));

    public void Success(string who) => state.TryRemove(who, out _);
}
