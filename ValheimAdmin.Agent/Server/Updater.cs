using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ValheimAdmin.Agent.Bridge;
using ValheimAdmin.Agent.Logs;
using static ValheimAdmin.Agent.I18n;

namespace ValheimAdmin.Agent.Server;

/// <summary>
/// Updates the dedicated server (Steam app 896660) with SteamCMD. The world and the BepInEx
/// configs are zipped first. BepInEx itself survives "validate" because its files are not part
/// of the depot.
/// </summary>
public sealed partial class Updater(
    AgentConfig config, ServerManager server, PluginBridge bridge, EventService events, LogTailer tailer, ILogger<Updater> log)
{
    private const string AppId = "896660";
    private int running;

    public bool Running => running != 0;
    public string BackupDir => Path.Combine(config.DataPath, "backups");

    [GeneratedRegex("\"buildid\"\\s+\"(\\d+)\"")]
    private static partial Regex BuildIdRegex();

    /// <summary>Build id from the app manifest (SteamCMD install or Steam library).</summary>
    public string? BuildId()
    {
        string[] candidates =
        [
            Path.Combine(config.ServerDir, "steamapps", $"appmanifest_{AppId}.acf"),
            Path.GetFullPath(Path.Combine(config.ServerDir, "..", "..", $"appmanifest_{AppId}.acf")),
        ];
        foreach (string file in candidates)
        {
            if (!File.Exists(file)) continue;
            var m = BuildIdRegex().Match(File.ReadAllText(file));
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    public void Start()
    {
        if (string.IsNullOrWhiteSpace(config.SteamCmdPath) || !File.Exists(config.SteamCmdPath))
            throw new InvalidOperationException("SteamCmdPath is not set or does not exist (agent.json).");
        if (Interlocked.Exchange(ref running, 1) == 1)
            throw new InvalidOperationException("An update is already running.");
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        string? before = BuildId();
        try
        {
            events.Record("update", null, T("update.start"));
            if (server.IsRunning && bridge.Connected)
            {
                await bridge.TryRequestAsync("broadcast", new { text = T("game.update"), center = true });
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            bool ok = false;
            await server.WithServerStoppedAsync("updating", T("reason.update"), async () =>
            {
                string zip = Backup();
                tailer.Append("[Agent] " + T("update.backup", zip));
                ok = await RunSteamCmd();
            });

            if (ok)
                events.Record("update", null, T("update.done", before ?? "?", BuildId() ?? "?"));
            else
                events.Record("update", null, T("update.failed", "SteamCMD"));
        }
        catch (Exception e)
        {
            log.LogError(e, "Update failed");
            events.Record("update", null, T("update.failed", e.Message));
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
        }
    }

    private string Backup()
    {
        Directory.CreateDirectory(BackupDir);
        string zip = Path.Combine(BackupDir, $"pre-update-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            AddDir(archive, Path.Combine(config.SaveDirResolved, "worlds_local"), "worlds_local");
            AddDir(archive, config.BepInExConfigDir, "BepInEx/config");
        }
        foreach (var old in new DirectoryInfo(BackupDir).GetFiles("pre-update-*.zip").OrderByDescending(f => f.Name).Skip(5))
            old.Delete();
        return zip;
    }

    private static void AddDir(ZipArchive archive, string dir, string prefix)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var entry = archive.CreateEntry(prefix + "/" + Path.GetRelativePath(dir, file).Replace('\\', '/'), CompressionLevel.Optimal);
            using var dst = entry.Open();
            src.CopyTo(dst);
        }
    }

    private async Task<bool> RunSteamCmd()
    {
        var psi = new ProcessStartInfo(config.SteamCmdPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(config.SteamCmdPath)!,
        };
        foreach (string a in new[] { "+force_install_dir", config.ServerDir, "+login", "anonymous", "+app_update", AppId, "validate", "+quit" })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        bool success = false;
        async Task Pump(StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.Length == 0) continue;
                if (line.Contains($"App '{AppId}' fully installed", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains($"App '{AppId}' already up to date", StringComparison.OrdinalIgnoreCase))
                    success = true;
                tailer.Append("[SteamCMD] " + line, line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ? "error" : "info");
            }
        }
        await Task.WhenAll(Pump(p.StandardOutput), Pump(p.StandardError), p.WaitForExitAsync());
        return success;
    }
}
