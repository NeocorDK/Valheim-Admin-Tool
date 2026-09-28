using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ValheimAdmin.Agent.Bridge;
using ValheimAdmin.Agent.Logs;
using static ValheimAdmin.Agent.I18n;

namespace ValheimAdmin.Agent.Server;

public enum ServerState { Stopped, Starting, Running, Stopping, Crashed }

public sealed record ServerStatus(
    ServerState State, string? Operation, int? Pid, bool Adopted, DateTimeOffset? StartedAt, long? UptimeSec,
    double? CpuPercent, long? MemoryMb, string? LastExit, bool PluginConnected, string? PluginVersion,
    bool WorldReady, JsonObject? Stats, double? HeartbeatAgoSec);

/// <summary>
/// Owns valheim_server.exe: start, graceful stop, restart, crash and hang watchdog.
/// A server that is already running when the agent starts is adopted, so restarting the agent
/// does not take the game down.
/// </summary>
public sealed class ServerManager(
    AgentConfig config, PluginBridge bridge, EventService events, LogTailer tailer, ILogger<ServerManager> log) : BackgroundService
{
    private readonly SemaphoreSlim opLock = new(1, 1);
    private readonly List<DateTimeOffset> autoRestarts = [];
    private Process? process;
    private bool adopted;
    private bool expectExit;
    private bool worldWasReady;
    private bool warnedNoPlugin;
    private readonly HashSet<int> warnedDuplicates = [];
    private DateTimeOffset? startedAt;
    private string? lastExit;
    private TimeSpan lastCpu;
    private DateTimeOffset lastCpuAt;
    private double? cpuPercent;

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? Operation { get; private set; }
    public bool IsRunning => process is { HasExited: false };

    /// <summary>Raised whenever the tracked server process ends, for any reason.</summary>
    public event Action? Exited;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bridge.Admission = Admit;
        Adopt();
        if (process == null && config.AutoStart)
        {
            try
            {
                await StartAsync(T("reason.autostart"));
            }
            catch (Exception e)
            {
                // A wrong ServerDir must not take the panel down with it.
                log.LogError("Auto start failed: {Error}", e.Message);
                tailer.Append("[Agent] " + e.Message, "error");
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Watch();
            }
            catch (Exception e)
            {
                log.LogError(e, "Watchdog tick failed");
            }
            await Task.Delay(5000, stoppingToken).ContinueWith(_ => { });
        }
    }

    /// <summary>Takes over a server from our ServerDir that the agent does not track yet. True if one was found.</summary>
    private bool Adopt()
    {
        var p = FindUntracked();
        if (p == null) return false;
        Attach(p, isAdopted: true);
        try { startedAt = p.StartTime.ToUniversalTime(); } catch (InvalidOperationException) { startedAt = DateTimeOffset.UtcNow; }
        State = ServerState.Running;
        log.LogInformation("Adopted running server, pid {Pid}", p.Id);
        tailer.Append("[Agent] " + T("server.adopted", p.Id));
        events.Record("server", null, T("server.adopted", p.Id));
        return true;
    }

    private Process? FindUntracked() => Untracked().FirstOrDefault();

    /// <summary>valheim_server processes from our ServerDir other than the tracked one.</summary>
    private List<Process> Untracked()
    {
        int? tracked = TrackedPid();
        var found = new List<Process>();
        foreach (var p in Process.GetProcessesByName("valheim_server"))
        {
            try
            {
                if (p.Id == tracked || p.HasExited) continue;
                if (string.Equals(p.MainModule?.FileName, config.ServerExe, StringComparison.OrdinalIgnoreCase)) found.Add(p);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }
        return found;
    }

    private int? TrackedPid()
    {
        try { return process is { HasExited: false } p ? p.Id : null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Only the plugin of the server we manage gets the link; a stray copy must not answer for it.</summary>
    private string? Admit(int? pid)
    {
        int? tracked = TrackedPid();
        if (pid == null || tracked == null || pid == tracked) return null;
        return $"the agent manages another server process (pid {tracked}); this one (pid {pid}) is a duplicate";
    }

    /// <summary>Warns when ValheimAdmin.dll is missing from BepInEx\plugins or installed more than once.</summary>
    private void CheckPluginInstalled()
    {
        try
        {
            var copies = Directory.Exists(config.BepInExPluginsDir)
                ? Directory.GetFiles(config.BepInExPluginsDir, "ValheimAdmin.dll", SearchOption.AllDirectories)
                : [];
            if (copies.Length == 1) return;
            string message = copies.Length == 0
                ? T("server.pluginMissing", config.BepInExPluginsDir)
                : T("server.pluginDuplicate", string.Join(", ", copies.Select(c => Path.GetRelativePath(config.BepInExPluginsDir, c))));
            tailer.Append("[Agent] " + message, "warning");
            events.Record("server", null, message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogDebug("Plugin check failed: {Error}", e.Message);
        }
    }

    private void Attach(Process p, bool isAdopted)
    {
        process = p;
        adopted = isAdopted;
        expectExit = false;
        worldWasReady = false;
        warnedNoPlugin = false;
        lastCpu = TimeSpan.Zero;
        cpuPercent = null;
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => OnExited(p);
    }

    public ServerStatus Status()
    {
        var p = process;
        int? pid = null;
        long? memory = null;
        if (p != null)
        {
            try
            {
                p.Refresh();
                if (!p.HasExited)
                {
                    pid = p.Id;
                    memory = p.WorkingSet64 / (1024 * 1024);
                    var now = DateTimeOffset.UtcNow;
                    var cpu = p.TotalProcessorTime;
                    if (lastCpu != TimeSpan.Zero && now > lastCpuAt)
                        cpuPercent = Math.Round((cpu - lastCpu).TotalMilliseconds / (now - lastCpuAt).TotalMilliseconds / Environment.ProcessorCount * 100, 1);
                    lastCpu = cpu;
                    lastCpuAt = now;
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        return new ServerStatus(State, Operation, pid, adopted, startedAt,
            startedAt.HasValue && pid.HasValue ? (long)(DateTimeOffset.UtcNow - startedAt.Value).TotalSeconds : null,
            pid.HasValue ? cpuPercent : null, memory, lastExit, bridge.Connected, bridge.PluginVersion, bridge.WorldReady,
            bridge.Stats, bridge.LastHeartbeat.HasValue ? Math.Round((DateTimeOffset.UtcNow - bridge.LastHeartbeat.Value).TotalSeconds, 1) : null);
    }

    public async Task StartAsync(string reason)
    {
        await opLock.WaitAsync();
        try
        {
            StartCore(reason);
        }
        finally
        {
            opLock.Release();
        }
    }

    public async Task StopAsync(string reason)
    {
        await opLock.WaitAsync();
        try
        {
            Operation = "stopping";
            await StopCore(reason);
        }
        finally
        {
            Operation = null;
            opLock.Release();
        }
    }

    public async Task RestartAsync(string reason)
    {
        await opLock.WaitAsync();
        try
        {
            Operation = "restarting";
            await StopCore(reason);
            StartCore(reason);
        }
        finally
        {
            Operation = null;
            opLock.Release();
        }
    }

    /// <summary>Runs work that needs the server down (update, world restore), restarting it afterwards if it was up.</summary>
    public async Task WithServerStoppedAsync(string operation, string reason, Func<Task> work)
    {
        await opLock.WaitAsync();
        bool wasRunning = IsRunning;
        try
        {
            Operation = operation;
            await StopCore(reason);
            await work();
        }
        finally
        {
            try
            {
                if (wasRunning) StartCore(T("reason.after", reason));
            }
            finally
            {
                Operation = null;
                opLock.Release();
            }
        }
    }

    /// <summary>Kills the tracked server and any other copy running from our ServerDir.</summary>
    public void Kill(string reason)
    {
        var targets = Untracked();
        var p = process;
        bool tracked = p is { HasExited: false };
        if (tracked) targets.Insert(0, p!);
        if (targets.Count == 0) return;

        expectExit = true;
        if (!tracked) State = ServerState.Stopped; // also cancels a pending watchdog restart
        events.Record("server", null, T("server.killed", reason));
        foreach (var target in targets)
        {
            try { target.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private void StartCore(string reason)
    {
        if (IsRunning) return;
        // Never launch a second copy next to one the agent lost track of: it would fail on the busy port.
        if (Adopt()) return;
        if (!File.Exists(config.ServerExe))
            throw new InvalidOperationException(T("server.notfound", config.ServerExe));
        CheckPluginInstalled();

        var psi = new ProcessStartInfo(config.ServerExe)
        {
            WorkingDirectory = config.ServerDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in config.BuildServerArguments()) psi.ArgumentList.Add(arg);
        psi.Environment["SteamAppId"] = "892970";
        psi.Environment["VA_AGENT_PORT"] = config.BridgePort.ToString();
        psi.Environment["VA_AGENT_SECRET"] = bridge.Secret;

        var p = Process.Start(psi) ?? throw new InvalidOperationException("The server process did not start");
        // Track the process before anything else can fail, or it would run on unseen by the agent.
        Attach(p, isAdopted: false);
        startedAt = DateTimeOffset.UtcNow;
        State = ServerState.Starting;
        CaptureStdout(p);
        log.LogInformation("Server started, pid {Pid} ({Reason})", p.Id, reason);
        tailer.Append("[Agent] " + T("server.starting", reason));
        events.Record("server", null, T("server.starting", reason));
    }

    /// <summary>
    /// The panel reads BepInEx\LogOutput.log; raw stdout goes to data\logs\stdout-latest.log,
    /// which also covers crashes that happen before BepInEx starts logging.
    /// </summary>
    private void CaptureStdout(Process p)
    {
        // The pipes must be drained even without a log file, or the server blocks once they fill up.
        StreamWriter? writer = null;
        try
        {
            string dir = Path.Combine(config.DataPath, "logs");
            Directory.CreateDirectory(dir);
            var fs = new FileStream(Path.Combine(dir, "stdout-latest.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer = new StreamWriter(fs) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("stdout-latest.log is not writable: {Error}", e.Message);
        }

        var gate = new object();
        DataReceivedEventHandler write = (_, e) =>
        {
            if (e.Data == null || writer == null) return;
            lock (gate)
            {
                try { writer.WriteLine(e.Data); } catch (ObjectDisposedException) { }
            }
        };
        p.OutputDataReceived += write;
        p.ErrorDataReceived += write;
        if (writer != null)
            p.Exited += (_, _) => Task.Delay(2000).ContinueWith(_ => { lock (gate) writer.Dispose(); });
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    /// <summary>Stops the tracked server, then any copy the agent had lost track of, so a start never meets a busy port.</summary>
    private async Task StopCore(string reason)
    {
        var p = process;
        bool any = false;
        if (p is { HasExited: false })
        {
            any = true;
            await StopOne(p, reason);
        }
        for (int i = 0; i < 3 && FindUntracked() is { } extra; i++)
        {
            any = true;
            Attach(extra, isAdopted: true);
            await StopOne(extra, reason);
        }
        if (!any) State = ServerState.Stopped;
    }

    private async Task StopOne(Process p, string reason)
    {
        expectExit = true;
        State = ServerState.Stopping;
        tailer.Append("[Agent] " + T("server.stopping", reason));
        events.Record("server", null, T("server.stopping", reason));

        if (bridge.Connected && (bridge.PluginPid == null || bridge.PluginPid == p.Id))
        {
            try
            {
                await bridge.RequestAsync("shutdown", new { message = T("game.shutdown") }, TimeSpan.FromSeconds(10));
                if (await WaitExit(p, TimeSpan.FromSeconds(120))) return;
                log.LogWarning("Server ignored the shutdown command; sending Ctrl+C");
            }
            catch (BridgeException e)
            {
                log.LogWarning("Shutdown via plugin failed: {Error}", e.Message);
            }
        }

        SendCtrlC(p.Id);
        if (await WaitExit(p, TimeSpan.FromSeconds(90))) return;

        log.LogWarning("Server did not stop; killing it");
        tailer.Append("[Agent] " + T("server.forcekill"), "warning");
        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        await WaitExit(p, TimeSpan.FromSeconds(15));
    }

    private static async Task<bool> WaitExit(Process p, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return p.HasExited;
        }
    }

    /// <summary>
    /// Ctrl+C makes valheim_server save and quit. Attaching to another console detaches the
    /// caller from its own, so a short-lived copy of the agent does it (see CtrlC.Send).
    /// </summary>
    private void SendCtrlC(int pid)
    {
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
            var helper = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--ctrlc", pid.ToString() }, UseShellExecute = false, CreateNoWindow = true });
            helper?.WaitForExit(10000);
        }
        catch (Exception e)
        {
            log.LogWarning("Ctrl+C helper failed: {Error}", e.Message);
        }
    }

    private void OnExited(Process p)
    {
        if (p != process) return;
        try { Exited?.Invoke(); } catch (Exception e) { log.LogError(e, "Exited handler failed"); }
        string code;
        try { code = p.ExitCode.ToString(); } catch (InvalidOperationException) { code = "?"; }
        process = null;
        adopted = false;
        lastExit = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} (exit code {code})";

        if (expectExit)
        {
            State = ServerState.Stopped;
            tailer.Append("[Agent] " + T("server.stopped"));
            events.Record("server", null, T("server.stopped"));
            return;
        }

        State = ServerState.Crashed;
        log.LogWarning("Server exited unexpectedly with code {Code}", code);
        tailer.Append("[Agent] " + T("server.crashed", code), "error");
        events.Record("crash", null, T("server.crashed", code));
        ScheduleAutoRestart(T("reason.crash"));
    }

    private void ScheduleAutoRestart(string why)
    {
        if (!config.Watchdog.Enabled) return;
        var now = DateTimeOffset.UtcNow;
        autoRestarts.RemoveAll(t => now - t > TimeSpan.FromHours(1));
        if (autoRestarts.Count >= config.Watchdog.MaxRestartsPerHour)
        {
            events.Record("crash", null, T("watchdog.giveup", autoRestarts.Count));
            return;
        }
        int[] delays = [10, 30, 60, 120, 300];
        int delay = delays[Math.Min(autoRestarts.Count, delays.Length - 1)];
        autoRestarts.Add(now);
        tailer.Append("[Agent] " + T("watchdog.restart", delay, why), "warning");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(delay));
            if (State != ServerState.Crashed) return;
            try
            {
                await StartAsync(T("reason.watchdog", why));
            }
            catch (Exception e)
            {
                log.LogError(e, "Auto restart failed");
                events.Record("crash", null, T("watchdog.failed", e.Message));
            }
        });
    }

    private void Watch()
    {
        var p = process;
        if (p == null || p.HasExited)
        {
            // A server that runs without the agent knowing (started by hand, or left over) is taken over.
            if (opLock.CurrentCount > 0 && State != ServerState.Stopping) Adopt();
            return;
        }
        if (State == ServerState.Stopping) return;

        // A link left over from another process (e.g. before a takeover) is dropped; the right plugin reconnects.
        if (bridge.Connected && bridge.PluginPid is { } linked && linked != p.Id) bridge.Drop();

        foreach (var dup in Untracked())
        {
            if (!warnedDuplicates.Add(dup.Id)) continue;
            string message = T("server.duplicate", dup.Id, p.Id);
            log.LogWarning("Duplicate server process {Pid}", dup.Id);
            tailer.Append("[Agent] " + message, "warning");
            events.Record("server", null, message);
        }

        if (bridge.Connected && bridge.WorldReady)
        {
            worldWasReady = true;
            if (State == ServerState.Starting) State = ServerState.Running;
        }

        if (!bridge.Connected && !warnedNoPlugin && startedAt.HasValue && DateTimeOffset.UtcNow - startedAt.Value > TimeSpan.FromMinutes(10))
        {
            warnedNoPlugin = true;
            tailer.Append("[Agent] " + T("server.noplugin"), "warning");
        }

        if (!config.Watchdog.Enabled || !worldWasReady || Operation != null) return;
        var last = bridge.LastHeartbeat;
        if (last.HasValue && DateTimeOffset.UtcNow - last.Value > TimeSpan.FromSeconds(config.Watchdog.HangSeconds))
        {
            worldWasReady = false;
            log.LogWarning("No heartbeat for {Seconds}s, server considered hung", config.Watchdog.HangSeconds);
            events.Record("crash", null, T("server.hung", config.Watchdog.HangSeconds));
            tailer.Append("[Agent] " + T("server.hung", config.Watchdog.HangSeconds), "error");
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}

/// <summary>Entry point of the "--ctrlc &lt;pid&gt;" helper process.</summary>
public static partial class CtrlC
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    /// <summary>
    /// A process started with Ctrl+C ignored (e.g. from Git Bash) passes that on to its children,
    /// and the server would then ignore our Ctrl+C. Clearing it here makes the server inherit
    /// normal Ctrl+C handling.
    /// </summary>
    public static void EnableForChildren()
    {
        if (OperatingSystem.IsWindows())
            SetConsoleCtrlHandler(IntPtr.Zero, false);
    }

    public static int Send(int pid)
    {
        FreeConsole();
        if (!AttachConsole((uint)pid)) return 1;
        SetConsoleCtrlHandler(IntPtr.Zero, true);
        bool ok = GenerateConsoleCtrlEvent(0, 0);
        Thread.Sleep(1000);
        FreeConsole();
        return ok ? 0 : 2;
    }
}
