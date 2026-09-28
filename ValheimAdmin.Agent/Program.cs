using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using ValheimAdmin.Agent;
using ValheimAdmin.Agent.Bridge;
using ValheimAdmin.Agent.Data;
using ValheimAdmin.Agent.Logs;
using ValheimAdmin.Agent.Server;
using ValheimAdmin.Agent.Snapshots;
using ValheimAdmin.Agent.Web;

// Helper mode used by ServerManager to deliver Ctrl+C to the server's console.
if (args.Length == 2 && args[0] == "--ctrlc")
    return CtrlC.Send(int.Parse(args[1]));

string configPath = Path.Combine(AppContext.BaseDirectory, "agent.json");
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--config") configPath = args[i + 1];

if (args.Length >= 2 && args[0] == "--set-password")
{
    var cfg = AgentConfig.Load(configPath);
    cfg.PanelPasswordHash = AgentConfig.HashPassword(args[1]);
    cfg.Save();
    Console.WriteLine("Panel password changed.");
    return 0;
}

var config = AgentConfig.Load(configPath);
I18n.Language = config.Language;
CtrlC.EnableForChildren();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Host.UseWindowsService(o => o.ServiceName = "ValheimAdmin");
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var addresses = await ListenAddresses(config);
bool https = config.Https.Port > 0 && !string.IsNullOrWhiteSpace(config.Https.CertificatePath);
builder.WebHost.ConfigureKestrel(k =>
{
    foreach (var address in addresses)
    {
        k.Listen(address, config.HttpPort);
        if (https)
            k.Listen(address, config.Https.Port, o => o.UseHttps(
                Path.GetFullPath(config.Https.CertificatePath, Path.GetDirectoryName(config.FilePath)!), config.Https.CertificatePassword));
    }
});

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
LiveHub.JsonOptions.Converters.Add(new JsonStringEnumConverter());

var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(config.DataPath, "keys")))
    .SetApplicationName("ValheimAdmin");
if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "va_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Per address: the login also locks an address out after 5 wrong passwords (LoginGuard).
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    // The public map polls markers every few seconds; this only stops floods.
    o.AddPolicy("public", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new Db(Path.Combine(config.DataPath, "valheim-admin.db")));
builder.Services.AddSingleton<LiveHub>();
builder.Services.AddSingleton<SnapshotStore>();
builder.Services.AddSingleton<IconStore>();
builder.Services.AddSingleton<ConfigFiles>();
builder.Services.AddSingleton<LoginGuard>();
builder.Services.AddSingletonHosted<PluginBridge>();
builder.Services.AddSingletonHosted<LogTailer>();
builder.Services.AddSingleton<EventService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingletonHosted<ServerManager>();
builder.Services.AddSingletonHosted<Scheduler>();
builder.Services.AddSingleton<Updater>();
builder.Services.AddSingleton<StatusBuilder>();
builder.Services.AddSingleton<MapProxy>();
builder.Services.AddHostedService<StatusPump>();

var app = builder.Build();

// Created eagerly so plugin events are recorded from the first connection on.
app.Services.GetRequiredService<EventService>();
app.Services.GetRequiredService<AdminService>();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapApi();

app.Logger.LogInformation("Panel: {Urls}", string.Join(", ", addresses.Select(a => $"http://{a}:{config.HttpPort}")
    .Concat(https ? addresses.Select(a => $"https://{a}:{config.Https.Port}") : [])));
await app.RunAsync();
return 0;

static async Task<List<IPAddress>> ListenAddresses(AgentConfig config)
{
    var list = new List<IPAddress>();
    foreach (string a in config.BindAddresses)
        if (IPAddress.TryParse(a, out var ip)) list.Add(ip);
    if (list.Any(ip => ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
        return list.Where(ip => ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)).ToList();

    if (config.ListenTailscale)
    {
        // At boot the service can start before Tailscale is up; give it a minute.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            var ts = TailscaleAddresses();
            if (ts.Count > 0)
            {
                list.AddRange(ts.Where(t => !list.Contains(t)));
                break;
            }
            if (attempt == 0) Console.WriteLine("Waiting for a Tailscale address (100.64.0.0/10)...");
            await Task.Delay(5000);
        }
    }
    if (list.Count == 0) list.Add(IPAddress.Loopback);
    return list;
}

static List<IPAddress> TailscaleAddresses() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(u => u.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [100, >= 64 and <= 127, _, _])
        .ToList();

internal static class ServiceCollectionExtensions
{
    /// <summary>Registers a hosted service that other services can also inject.</summary>
    public static IServiceCollection AddSingletonHosted<T>(this IServiceCollection services) where T : class, IHostedService
    {
        services.AddSingleton<T>();
        services.AddHostedService(sp => sp.GetRequiredService<T>());
        return services;
    }
}
