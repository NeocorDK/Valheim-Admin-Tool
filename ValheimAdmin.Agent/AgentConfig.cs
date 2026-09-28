using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ValheimAdmin.Agent;

public sealed class ServerArgs
{
    public string Name { get; set; } = "My server";
    public int Port { get; set; } = 2456;
    public string World { get; set; } = "Dedicated";
    public string Password { get; set; } = "secret";
    /// <summary>
    /// Valheim's own default. With -public 0 the server stops answering Steam status queries,
    /// so Favorites/Recent show it as unreachable even though joining still works.
    /// </summary>
    public bool Public { get; set; } = true;
    public bool Crossplay { get; set; }
    /// <summary>Empty = %USERPROFILE%\AppData\LocalLow\IronGate\Valheim of the account running the agent.</summary>
    public string SaveDir { get; set; } = "";
    public int SaveInterval { get; set; } = 1800;
    public int Backups { get; set; } = 4;
    public int BackupShort { get; set; } = 7200;
    public int BackupLong { get; set; } = 43200;
    public string ExtraArgs { get; set; } = "";
}

public sealed class WatchdogConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>No heartbeat from the plugin for this long (after the world loaded) counts as a hang.</summary>
    public int HangSeconds { get; set; } = 180;
    public int MaxRestartsPerHour { get; set; } = 4;
}

public sealed class RestartSchedule
{
    public bool Enabled { get; set; }
    /// <summary>Local times of day, "HH:mm".</summary>
    public List<string> Times { get; set; } = ["06:00"];
    public List<int> WarnMinutes { get; set; } = [15, 5, 1];
}

public sealed class SnapshotRetention
{
    public int KeepAllDays { get; set; } = 3;
    public int KeepDailyDays { get; set; } = 60;
    /// <summary>
    /// customData keys (or "prefix*") that mods change during normal play; Compare ignores them
    /// so an item is not reported as lost just because such a value changed.
    /// </summary>
    public List<string> IgnoreDataKeys { get; set; } = [];
}

/// <summary>Optional HTTPS listener, recommended when the panel is reachable from the internet.</summary>
public sealed class HttpsConfig
{
    /// <summary>0 = off.</summary>
    public int Port { get; set; }
    /// <summary>PFX/PKCS#12 file with the certificate and its private key.</summary>
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
}

public sealed class AgentConfig
{
    /// <summary>"en" or "ru": event messages, agent log lines and in-game announcements.</summary>
    public string Language { get; set; } = "en";
    public int HttpPort { get; set; } = 8095;
    /// <summary>Addresses the panel listens on besides the auto-detected Tailscale one.</summary>
    public List<string> BindAddresses { get; set; } = ["127.0.0.1"];
    public bool ListenTailscale { get; set; } = true;
    public HttpsConfig Https { get; set; } = new();

    /// <summary>Plain password; the agent replaces it with <see cref="PanelPasswordHash"/> on start.</summary>
    public string PanelPassword { get; set; } = "";
    public string PanelPasswordHash { get; set; } = "";

    public int BridgePort { get; set; } = 27961;
    public string ServerDir { get; set; } = @"C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server";
    public ServerArgs Server { get; set; } = new();
    public bool AutoStart { get; set; } = true;
    public WatchdogConfig Watchdog { get; set; } = new();
    public RestartSchedule Restarts { get; set; } = new();
    public SnapshotRetention Snapshots { get; set; } = new();
    public string SteamCmdPath { get; set; } = "";
    public string DataDir { get; set; } = "data";

    [JsonIgnore] public string FilePath { get; private set; } = "";
    [JsonIgnore] public string DataPath => Path.GetFullPath(DataDir, Path.GetDirectoryName(FilePath)!);
    [JsonIgnore] public string ServerExe => Path.GetFullPath(Path.Combine(ServerDir, "valheim_server.exe"));
    [JsonIgnore] public string BepInExLog => Path.Combine(ServerDir, "BepInEx", "LogOutput.log");
    [JsonIgnore] public string BepInExConfigDir => Path.Combine(ServerDir, "BepInEx", "config");
    [JsonIgnore] public string BepInExPluginsDir => Path.Combine(ServerDir, "BepInEx", "plugins");

    [JsonIgnore]
    public string SaveDirResolved => string.IsNullOrWhiteSpace(Server.SaveDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "IronGate", "Valheim")
        : Server.SaveDir;

    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    private readonly Lock saveLock = new();

    public static AgentConfig Load(string path, ILogger? log = null)
    {
        path = Path.GetFullPath(path);
        AgentConfig config;
        if (File.Exists(path))
        {
            try
            {
                config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), jsonOptions) ?? new AgentConfig();
            }
            catch (JsonException e)
            {
                throw new InvalidOperationException(
                    $"{path} is not valid JSON: {e.Message}{Environment.NewLine}" +
                    @"Hint: backslashes in paths must be doubled (C:\\Games\\Valheim) or written as forward slashes (C:/Games/Valheim).", e);
            }
        }
        else
        {
            // A fresh config holds placeholder server settings; don't launch anything until they are edited.
            config = new AgentConfig { AutoStart = false };
            string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('/', 'x').Replace('+', 'y');
            config.PanelPassword = password;
            Console.WriteLine($"Created {path}. Panel password: {password}");
            log?.LogWarning("Created {Path}. Panel password: {Password}", path, password);
        }
        config.FilePath = path;

        if (!string.IsNullOrEmpty(config.PanelPassword))
        {
            config.PanelPasswordHash = HashPassword(config.PanelPassword);
            config.PanelPassword = "";
        }
        config.Save();
        return config;
    }

    public void Save()
    {
        lock (saveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, jsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool CheckPassword(string password)
    {
        string[] parts = PanelPasswordHash.Split('$');
        if (parts.Length != 3 || parts[0] != "pbkdf2") return false;
        byte[] salt = Convert.FromBase64String(parts[1]);
        byte[] expected = Convert.FromBase64String(parts[2]);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Arguments for valheim_server.exe.</summary>
    public List<string> BuildServerArguments()
    {
        var s = Server;
        var args = new List<string>
        {
            "-nographics", "-batchmode",
            "-name", s.Name,
            "-port", s.Port.ToString(),
            "-world", s.World,
            "-password", s.Password,
            "-public", s.Public ? "1" : "0",
            "-savedir", SaveDirResolved,
            "-saveinterval", s.SaveInterval.ToString(),
            "-backups", s.Backups.ToString(),
            "-backupshort", s.BackupShort.ToString(),
            "-backuplong", s.BackupLong.ToString(),
        };
        if (s.Crossplay) args.Add("-crossplay");
        args.AddRange(SplitArgs(s.ExtraArgs));
        return args;
    }

    /// <summary>Splits on spaces, keeping "quoted parts" together.</summary>
    public static List<string> SplitArgs(string text) => Shared.ConsoleLine.SplitArgs(text);
}
