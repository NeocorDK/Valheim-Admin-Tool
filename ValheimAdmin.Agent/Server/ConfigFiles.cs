using System.Diagnostics;

namespace ValheimAdmin.Agent.Server;

public sealed record ConfigFileInfo(string Path, long Size, DateTimeOffset Modified);

public sealed record PluginFileInfo(string Path, long Size, string? Version, DateTimeOffset Modified);

/// <summary>Reads and writes files under BepInEx\config; every save keeps the previous version under data\config-backups.</summary>
public sealed class ConfigFiles(AgentConfig config)
{
    private static readonly string[] extensions = [".cfg", ".yml", ".yaml", ".json", ".txt", ".ini", ".toml"];
    private const long MaxSize = 4 * 1024 * 1024;

    public string BackupDir => Path.Combine(config.DataPath, "config-backups");

    public List<ConfigFileInfo> List()
    {
        string root = config.BepInExConfigDir;
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .Select(f => new ConfigFileInfo(Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTime))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Maps a relative path to a file inside the config directory, refusing anything outside it.</summary>
    public string Resolve(string relative)
    {
        string root = Path.GetFullPath(config.BepInExConfigDir);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path is outside BepInEx\\config");
        if (!extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("File type is not editable");
        return full;
    }

    public string Read(string relative)
    {
        string full = Resolve(relative);
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException(relative);
        if (info.Length > MaxSize) throw new InvalidOperationException("File is too large");
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    public void Write(string relative, string content)
    {
        string full = Resolve(relative);
        if (File.Exists(full))
        {
            Directory.CreateDirectory(BackupDir);
            string name = relative.Replace('/', '_').Replace('\\', '_');
            File.Copy(full, Path.Combine(BackupDir, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss}.bak"), overwrite: true);
        }
        string tmp = full + ".va-tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, full, overwrite: true);
    }

    public List<PluginFileInfo> Plugins()
    {
        string root = config.BepInExPluginsDir;
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Select(f => new PluginFileInfo(Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.Length,
                FileVersionInfo.GetVersionInfo(f.FullName).FileVersion, f.LastWriteTime))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
