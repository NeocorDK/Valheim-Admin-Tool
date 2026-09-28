using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ValheimAdmin.Agent.Bridge;
using ValheimAdmin.Agent.Data;
using ValheimAdmin.Agent.Logs;
using ValheimAdmin.Agent.Server;
using ValheimAdmin.Agent.Snapshots;
using static ValheimAdmin.Agent.I18n;

namespace ValheimAdmin.Agent.Web;

public sealed record LoginBody(string Password);
public sealed record ConsoleBody(string Line);
public sealed record MinutesBody(int Minutes);
public sealed record PlayerActionBody(string Action, string Player);
public sealed record ListChangeBody(string Op, string Value);
public sealed record TakeSnapshotBody(string? Player);
public sealed record ConfigWriteBody(string Path, string Content);
public sealed record PasswordBody(string Current, string New);

public sealed class SettingsBody
{
    public string? Language { get; set; }
    public string? ServerDir { get; set; }
    public ServerArgs? Server { get; set; }
    public bool? AutoStart { get; set; }
    public WatchdogConfig? Watchdog { get; set; }
    public RestartSchedule? Restarts { get; set; }
    public SnapshotRetention? Snapshots { get; set; }
    public string? SteamCmdPath { get; set; }
}

public static partial class Api
{
    private static readonly Dictionary<string, string> listFiles = new()
    {
        ["admin"] = "adminlist.txt",
        ["banned"] = "bannedlist.txt",
        ["permitted"] = "permittedlist.txt",
    };

    [GeneratedRegex(@"^server-\d{4}-\d{2}-\d{2}\.log$")]
    private static partial Regex ArchiveName();

    private static string Who(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

    /// <summary>Maps exceptions to HTTP errors with a message the panel shows as is.</summary>
    private static async Task<IResult> Run(Func<Task<object?>> action)
    {
        try
        {
            object? result = await action();
            return result is IResult r ? r : Results.Ok(result ?? new { ok = true });
        }
        catch (BridgeException e) { return Results.Json(new { error = e.Message }, statusCode: 409); }
        catch (KeyNotFoundException) { return Results.Json(new { error = "Not found" }, statusCode: 404); }
        catch (FileNotFoundException e) { return Results.Json(new { error = "Not found: " + e.Message }, statusCode: 404); }
        catch (UnauthorizedAccessException e) { return Results.Json(new { error = e.Message }, statusCode: 403); }
        catch (FormatException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
        catch (ArgumentException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
        catch (InvalidOperationException e) { return Results.Json(new { error = e.Message }, statusCode: 409); }
    }

    private static Task<IResult> Run(Func<object?> action) => Run(new Func<Task<object?>>(() => Task.FromResult(action())));

    public static void MapApi(this WebApplication app)
    {
        // ---------- auth ----------
        app.MapPost("/api/login", async (LoginBody body, HttpContext ctx, AgentConfig config, LoginGuard guard, Db db) =>
        {
            string who = Who(ctx);
            if (guard.IsLocked(who)) return Results.Json(new { error = "Too many attempts, try again later" }, statusCode: 429);
            if (!config.CheckPassword(body.Password ?? ""))
            {
                guard.Fail(who);
                await Task.Delay(700);
                db.Audit(who, "login-failed");
                return Results.Json(new { error = "Wrong password" }, statusCode: 401);
            }
            guard.Success(who);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });
            db.Audit(who, "login");
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        });

        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/me", (AgentConfig config) => new { ok = true, language = config.Language });

        app.Map("/ws", async (HttpContext ctx, LiveHub hub) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
            // Browsers send Origin on WebSocket handshakes; refuse pages from other sites.
            string origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var o) ||
                                      !string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
                return Results.StatusCode(403);
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(socket, ctx.RequestAborted);
            return Results.Empty;
        }).RequireAuthorization();

        // ---------- status & server control ----------
        api.MapGet("/status", (StatusBuilder status) => Run(async () => await status.BuildAsync()));

        api.MapPost("/server/{action}", (string action, HttpContext ctx, ServerManager server, Scheduler scheduler,
            PluginBridge bridge, Db db, IHostApplicationLifetime life) => Run(async () =>
        {
            string who = Who(ctx);
            db.Audit(who, "server-" + action);
            string reason = T("reason.manual");
            switch (action)
            {
                case "start":
                    await server.StartAsync(reason);
                    break;
                case "stop":
                    _ = Task.Run(object? () => server.StopAsync(reason));
                    break;
                case "restart":
                    _ = Task.Run(object? () => server.RestartAsync(reason));
                    break;
                case "kill":
                    server.Kill(reason);
                    break;
                case "save":
                    await bridge.RequestAsync("save");
                    break;
                case "restart-in":
                    var body = await ctx.Request.ReadFromJsonAsync<MinutesBody>();
                    scheduler.RestartIn(body?.Minutes ?? 5);
                    break;
                case "cancel-restart":
                    scheduler.Cancel();
                    break;
                default:
                    throw new KeyNotFoundException();
            }
            return null;
        }));

        // ---------- console ----------
        api.MapPost("/console", (ConsoleBody body, HttpContext ctx, PluginBridge bridge, Db db) => Run(async () =>
        {
            var parsed = CommandParser.Parse(body.Line ?? "");
            if (parsed.Cmd == "help") return new { help = CommandParser.Commands };
            db.Audit(Who(ctx), "console", body.Line);
            TimeSpan timeout = parsed.Cmd is "cheat" or "snapshot" or "give" ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(20);
            var result = await bridge.RequestAsync(parsed.Cmd, parsed.Args, timeout);
            return new { cmd = parsed.Cmd, result };
        }));

        // ---------- logs ----------
        api.MapGet("/logs", (LogTailer tailer, long? after, int? limit) =>
            tailer.Recent(Math.Clamp(limit ?? 1000, 1, 5000), after ?? 0));

        api.MapGet("/logs/archive", (LogTailer tailer) =>
            Directory.Exists(tailer.ArchiveDir)
                ? new DirectoryInfo(tailer.ArchiveDir).GetFiles("server-*.log").OrderByDescending(f => f.Name)
                    .Select(f => new { name = f.Name, size = f.Length }).ToList()
                : []);

        api.MapGet("/logs/archive/{name}", (string name, LogTailer tailer) =>
        {
            if (!ArchiveName().IsMatch(name)) return Results.NotFound();
            string path = Path.Combine(tailer.ArchiveDir, name);
            return File.Exists(path) ? Results.File(path, "text/plain; charset=utf-8", name) : Results.NotFound();
        });

        // ---------- events ----------
        api.MapGet("/events", (Db db, string? kind, string? player, string? q, long? before, long? from, long? to, int? limit) =>
        {
            var where = new List<string>();
            var args = new List<(string, object?)>();
            if (!string.IsNullOrWhiteSpace(kind))
            {
                var kinds = kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                where.Add($"kind IN ({string.Join(",", kinds.Select((_, i) => "$k" + i))})");
                args.AddRange(kinds.Select((k, i) => ("$k" + i, (object?)k)));
            }
            if (!string.IsNullOrWhiteSpace(player)) { where.Add("player = $p COLLATE NOCASE"); args.Add(("$p", player)); }
            if (!string.IsNullOrWhiteSpace(q)) { where.Add("message LIKE $q"); args.Add(("$q", "%" + q + "%")); }
            if (before.HasValue) { where.Add("id < $b"); args.Add(("$b", before)); }
            if (from.HasValue) { where.Add("ts >= $from"); args.Add(("$from", from)); }
            if (to.HasValue) { where.Add("ts <= $to"); args.Add(("$to", to)); }
            args.Add(("$l", Math.Clamp(limit ?? 200, 1, 1000)));
            string sql = "SELECT id, ts, kind, player, message, data FROM events" +
                         (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY id DESC LIMIT $l";
            return db.Query(sql, r => new EventRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), r.IsDBNull(5) ? null : JsonNode.Parse(r.GetString(5)) as JsonObject), args.ToArray());
        });

        // ---------- players ----------
        api.MapGet("/players/online", (AdminService admin) => Run(async () => await admin.PlayersAsync(fresh: true)));

        api.MapGet("/players/history", (Db db) => db.Query("""
            SELECT s.player, MAX(s.host), COUNT(*), SUM(COALESCE(s.end_ts, $now) - s.start_ts), MAX(COALESCE(s.end_ts, $now)),
                   (SELECT COUNT(*) FROM events e WHERE e.kind='death' AND e.player = s.player COLLATE NOCASE),
                   MAX(CASE WHEN s.end_ts IS NULL THEN 1 ELSE 0 END)
            FROM sessions s GROUP BY s.player ORDER BY 5 DESC
            """,
            r => new
            {
                player = r.GetString(0), host = r.IsDBNull(1) ? null : r.GetString(1), sessions = r.GetInt32(2),
                playedMs = r.GetInt64(3), lastSeen = r.GetInt64(4), deaths = r.GetInt32(5), online = r.GetInt32(6) == 1,
            }, ("$now", Db.Now)));

        api.MapGet("/players/{name}/sessions", (string name, Db db) => db.Query(
            "SELECT start_ts, end_ts, host FROM sessions WHERE player=$p COLLATE NOCASE ORDER BY start_ts DESC LIMIT 100",
            r => new { start = r.GetInt64(0), end = r.IsDBNull(1) ? (long?)null : r.GetInt64(1), host = r.IsDBNull(2) ? null : r.GetString(2) },
            ("$p", name)));

        api.MapPost("/players/action", (PlayerActionBody body, HttpContext ctx, PluginBridge bridge, Db db) => Run(async () =>
        {
            if (body.Action is not ("kick" or "ban" or "unban")) throw new ArgumentException("action");
            db.Audit(Who(ctx), body.Action, body.Player);
            await bridge.RequestAsync(body.Action, new { player = body.Player });
            return null;
        }));

        api.MapGet("/lists/{list}", (string list, PluginBridge bridge, AgentConfig config) => Run(async () =>
        {
            if (!listFiles.TryGetValue(list, out string? file)) throw new KeyNotFoundException();
            if (bridge.Connected && bridge.WorldReady) return await bridge.RequestAsync("list", new { list });
            string path = Path.Combine(config.SaveDirResolved, file);
            return File.Exists(path)
                ? File.ReadAllLines(path).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("//")).Select(l => l.Trim()).ToList()
                : new List<string>();
        }));

        api.MapPost("/lists/{list}", (string list, ListChangeBody body, HttpContext ctx, PluginBridge bridge, AgentConfig config, Db db) => Run(async () =>
        {
            if (!listFiles.TryGetValue(list, out string? file)) throw new KeyNotFoundException();
            if (body.Op is not ("add" or "remove") || string.IsNullOrWhiteSpace(body.Value)) throw new ArgumentException("op/value");
            db.Audit(Who(ctx), $"list-{body.Op}", $"{list}: {body.Value}");
            if (bridge.Connected && bridge.WorldReady)
            {
                await bridge.RequestAsync(body.Op == "add" ? "list_add" : "list_remove", new { list, value = body.Value.Trim() });
                return null;
            }
            string path = Path.Combine(config.SaveDirResolved, file);
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
            lines.RemoveAll(l => l.Trim() == body.Value.Trim());
            if (body.Op == "add") lines.Add(body.Value.Trim());
            File.WriteAllLines(path, lines);
            return null;
        }));

        api.MapGet("/items", (AdminService admin) => Run(async () => await admin.ItemsAsync()));

        // ---------- characters & snapshots ----------
        api.MapGet("/characters", (SnapshotStore store, AdminService admin) => Run(async () =>
        {
            var online = await admin.PlayersAsync();
            var onlineIds = online.OfType<JsonObject>().Select(p => p["characterId"]?.GetValue<long>()).ToHashSet();
            return store.Characters().Select(c => new
            {
                c.CharacterId, c.Player, c.Host, c.Count, c.LastTs,
                online = onlineIds.Contains(c.CharacterId),
            }).ToList();
        }));

        api.MapGet("/characters/{id:long}/snapshots", (long id, SnapshotStore store) => store.List(id));

        api.MapGet("/snapshots/{id:long}", (long id, SnapshotStore store) => Run(object? () =>
        {
            var row = store.Row(id) ?? throw new KeyNotFoundException();
            var content = store.Content(id) ?? throw new KeyNotFoundException();
            return new { row, content = AdminService.Enrich(content) };
        }));

        api.MapPost("/snapshots/take", (TakeSnapshotBody body, HttpContext ctx, PluginBridge bridge, Db db) => Run(async () =>
        {
            db.Audit(Who(ctx), "snapshot", body.Player ?? "*");
            var args = new JsonObject { ["trigger"] = "manual" };
            if (!string.IsNullOrWhiteSpace(body.Player)) args["player"] = body.Player;
            await bridge.RequestAsync("snapshot", args, TimeSpan.FromSeconds(40));
            return null;
        }));

        api.MapGet("/snapshots/{id:long}/diff", (long id, AdminService admin) => Run(async () => await admin.DiffAsync(id)));

        api.MapPost("/snapshots/{id:long}/restore", (long id, RestoreRequest body, HttpContext ctx, AdminService admin) =>
            Run(async () => await admin.RestoreAsync(id, body, Who(ctx))));

        // ---------- configs & plugins ----------
        api.MapGet("/configs", (ConfigFiles files) => files.List());

        api.MapGet("/configs/file", (string path, ConfigFiles files) => Run(object? () => new { path, content = files.Read(path) }));

        api.MapPut("/configs/file", (ConfigWriteBody body, HttpContext ctx, ConfigFiles files, EventService events, Db db) => Run(object? () =>
        {
            files.Write(body.Path, body.Content ?? "");
            db.Audit(Who(ctx), "config-save", body.Path);
            events.Record("config", null, T("config.saved", body.Path));
            return null;
        }));

        api.MapGet("/plugins", (ConfigFiles files, PluginBridge bridge) => Run(async () => new
        {
            files = files.Plugins(),
            loaded = bridge.Connected && bridge.WorldReady ? await bridge.TryRequestAsync("plugins") : null,
        }));

        // ---------- maintenance ----------
        api.MapPost("/update", (HttpContext ctx, Updater updater, Db db) => Run(object? () =>
        {
            db.Audit(Who(ctx), "update");
            updater.Start();
            return null;
        }));

        api.MapGet("/audit", (Db db, int? limit) => db.Query(
            "SELECT id, ts, who, action, details FROM audit ORDER BY id DESC LIMIT $l",
            r => new { id = r.GetInt64(0), ts = r.GetInt64(1), who = r.IsDBNull(2) ? null : r.GetString(2), action = r.GetString(3), details = r.IsDBNull(4) ? null : r.GetString(4) },
            ("$l", Math.Clamp(limit ?? 200, 1, 1000))));

        api.MapGet("/settings", (AgentConfig c) => new
        {
            c.Language, c.ServerDir, c.Server, c.AutoStart, c.Watchdog, c.Restarts, c.Snapshots, c.SteamCmdPath,
            c.HttpPort, c.BridgePort, saveDir = c.SaveDirResolved,
        });

        api.MapPut("/settings", (SettingsBody body, HttpContext ctx, AgentConfig c, Db db) => Run(object? () =>
        {
            if (body.Language is { } lang)
            {
                if (lang is not ("en" or "ru")) throw new ArgumentException("language");
                c.Language = lang;
                I18n.Language = lang;
            }
            if (body.ServerDir != null) c.ServerDir = body.ServerDir;
            if (body.Server != null) c.Server = body.Server;
            if (body.AutoStart != null) c.AutoStart = body.AutoStart.Value;
            if (body.Watchdog != null) c.Watchdog = body.Watchdog;
            if (body.Restarts != null)
            {
                foreach (string t in body.Restarts.Times)
                    if (!TimeOnly.TryParseExact(t, "H:mm", out _)) throw new ArgumentException("time: " + t);
                c.Restarts = body.Restarts;
            }
            if (body.Snapshots != null) c.Snapshots = body.Snapshots;
            if (body.SteamCmdPath != null) c.SteamCmdPath = body.SteamCmdPath;
            c.Save();
            db.Audit(Who(ctx), "settings");
            return null;
        }));

        api.MapPost("/settings/password", (PasswordBody body, HttpContext ctx, AgentConfig c, Db db) => Run(object? () =>
        {
            if (!c.CheckPassword(body.Current ?? "")) throw new UnauthorizedAccessException("Wrong password");
            if (string.IsNullOrWhiteSpace(body.New) || body.New.Length < 8) throw new ArgumentException("The new password needs at least 8 characters");
            c.PanelPasswordHash = AgentConfig.HashPassword(body.New);
            c.Save();
            db.Audit(Who(ctx), "password");
            return null;
        }));
    }
}
