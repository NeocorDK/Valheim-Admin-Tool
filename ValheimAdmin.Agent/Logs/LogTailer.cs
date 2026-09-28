using System.Text;
using System.Text.RegularExpressions;
using ValheimAdmin.Agent.Web;

namespace ValheimAdmin.Agent.Logs;

public sealed record LogLine(long Seq, DateTimeOffset Ts, string Level, string Text);

public static partial class LogClassifier
{
    // BepInEx: "[Warning:   Unity Log] text", "[Error  : Epic Loot] text", "[Info   :Valheim Admin] text"
    [GeneratedRegex(@"^\[(?<level>Fatal|Error|Warning|Message|Info|Debug)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex BepInExLevel();

    public static string Level(string line)
    {
        var m = BepInExLevel().Match(line);
        if (m.Success)
        {
            return m.Groups["level"].Value.ToLowerInvariant() switch
            {
                "fatal" or "error" => "error",
                "warning" => "warning",
                "debug" => "debug",
                _ => "info",
            };
        }
        if (line.Contains("Exception", StringComparison.Ordinal) || line.StartsWith("  at ", StringComparison.Ordinal))
            return "error";
        return "info";
    }
}

/// <summary>
/// Follows BepInEx\LogOutput.log, which BepInEx recreates on every server start and which also
/// receives Valheim's own Unity log. Lines are kept in memory for the panel, archived per day
/// under data\logs and pushed to open panels.
/// </summary>
public sealed class LogTailer(AgentConfig config, LiveHub hub, ILogger<LogTailer> log) : BackgroundService
{
    private const int RingSize = 5000;
    private readonly LinkedList<LogLine> ring = new();
    private readonly Lock ringLock = new();
    private long seq;

    public string ArchiveDir => Path.Combine(config.DataPath, "logs");

    public List<LogLine> Recent(int count, long afterSeq = 0)
    {
        lock (ringLock)
            return ring.Where(l => l.Seq > afterSeq).TakeLast(count).ToList();
    }

    /// <summary>Lines produced by the agent itself (server start/stop, SteamCMD output) go to the same stream.</summary>
    public void Append(string text, string level = "info", bool archive = true)
    {
        var line = new LogLine(Interlocked.Increment(ref seq), DateTimeOffset.Now, level, text);
        lock (ringLock)
        {
            ring.AddLast(line);
            while (ring.Count > RingSize) ring.RemoveFirst();
        }
        if (archive) Archive(line);
        hub.Broadcast("log", line);
    }

    private void Archive(LogLine line)
    {
        try
        {
            Directory.CreateDirectory(ArchiveDir);
            File.AppendAllText(Path.Combine(ArchiveDir, $"server-{line.Ts:yyyy-MM-dd}.log"),
                $"{line.Ts:HH:mm:ss} {line.Text}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (IOException e)
        {
            log.LogDebug("Log archive write failed: {Error}", e.Message);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string path = config.BepInExLog;
        long position = -1;
        DateTime created = default;
        var pendingText = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    if (position < 0)
                    {
                        // First look: show the tail of the current file without archiving it again.
                        position = Math.Max(0, info.Length - 128 * 1024);
                        created = info.CreationTimeUtc;
                        foreach (string l in ReadFrom(path, ref position, decoder, pendingText, skipPartialFirst: position > 0))
                            Append(l, LogClassifier.Level(l), archive: false);
                    }
                    else
                    {
                        if (info.Length < position || info.CreationTimeUtc != created)
                        {
                            position = 0;
                            created = info.CreationTimeUtc;
                            pendingText.Clear();
                            decoder.Reset();
                            Append(I18n.T("server.newlog"), "info");
                        }
                        if (info.Length > position)
                        {
                            foreach (string l in ReadFrom(path, ref position, decoder, pendingText, skipPartialFirst: false))
                                Append(l, LogClassifier.Level(l));
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogDebug("Tail failed: {Error}", e.Message);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Tail failed");
            }

            await Task.Delay(500, stoppingToken).ContinueWith(_ => { });
        }
    }

    private static List<string> ReadFrom(string path, ref long position, Decoder decoder, StringBuilder pendingText, bool skipPartialFirst)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(position, SeekOrigin.Begin);
        var bytes = new byte[64 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        int n;
        while ((n = fs.Read(bytes, 0, bytes.Length)) > 0)
        {
            int c = decoder.GetChars(bytes, 0, n, chars, 0);
            pendingText.Append(chars, 0, c);
            position += n;
        }

        var lines = new List<string>();
        string text = pendingText.ToString();
        int start = 0;
        bool skip = skipPartialFirst;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            string line = text.Substring(start, i - start).TrimEnd('\r');
            start = i + 1;
            if (skip) { skip = false; continue; }
            if (line.Length > 0) lines.Add(line);
        }
        pendingText.Clear();
        pendingText.Append(text, start, text.Length - start);
        return lines;
    }
}
