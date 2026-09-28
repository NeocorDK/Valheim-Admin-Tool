using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ValheimAdmin.Agent.Bridge;

public sealed class BridgeException(string message) : Exception(message);

public sealed record PluginEvent(string Kind, DateTimeOffset Ts, JsonObject Data);

/// <summary>
/// TCP endpoint on 127.0.0.1 that the plugin inside valheim_server.exe connects to.
/// One line of JSON per message; the first line must carry the shared secret.
/// </summary>
public sealed class PluginBridge : BackgroundService
{
    private readonly AgentConfig config;
    private readonly ILogger<PluginBridge> log;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private long nextId;
    private StreamWriter? writer;
    private TcpClient? current;

    public string Secret { get; }
    public bool Connected => writer != null;
    public string? PluginVersion { get; private set; }
    public int? PluginPid { get; private set; }
    public JsonObject? Stats { get; private set; }
    public DateTimeOffset? LastHeartbeat { get; private set; }
    public DateTimeOffset? ConnectedAt { get; private set; }
    public bool WorldReady => Stats?["ready"]?.GetValue<bool>() == true;

    /// <summary>
    /// Decides whether a plugin in the given server process may take the link; returns the refusal
    /// reason, or null to accept. Keeps a stray second server from taking over the panel.
    /// </summary>
    public Func<int?, string?>? Admission { get; set; }

    public event Action<PluginEvent>? EventReceived;

    /// <summary>Closes the current plugin link; the plugin reconnects on its own and goes through Admission again.</summary>
    public void Drop()
    {
        try { current?.Dispose(); } catch (ObjectDisposedException) { }
    }
    public event Action? ConnectionChanged;

    public PluginBridge(AgentConfig config, ILogger<PluginBridge> log)
    {
        this.config = config;
        this.log = log;
        string secretFile = Path.Combine(config.DataPath, "bridge.secret");
        Directory.CreateDirectory(config.DataPath);
        if (!File.Exists(secretFile))
            File.WriteAllText(secretFile, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Secret = File.ReadAllText(secretFile).Trim();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, config.BridgePort);
        listener.Start();
        log.LogInformation("Plugin bridge listening on 127.0.0.1:{Port}", config.BridgePort);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, new UTF8Encoding(false));
        var myWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            string? first = await reader.ReadLineAsync(helloTimeout.Token);
            var hello = first == null ? null : JsonNode.Parse(first) as JsonObject;
            string secret = hello?["secret"]?.GetValue<string>() ?? "";
            if (hello?["t"]?.GetValue<string>() != "hello" ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(Secret)))
            {
                log.LogWarning("Rejected bridge connection with a wrong secret");
                client.Dispose();
                return;
            }

            int? pid = hello["pid"]?.GetValue<int>();
            if (Admission?.Invoke(pid) is { } refusal)
            {
                log.LogWarning("Refused plugin link from pid {Pid}: {Reason}", pid, refusal);
                await myWriter.WriteLineAsync(JsonSerializer.Serialize(new { t = "refused", reason = refusal }));
                client.Dispose();
                return;
            }
            await myWriter.WriteLineAsync("{\"t\":\"welcome\"}");

            TcpClient? old = current;
            current = client;
            writer = myWriter;
            old?.Dispose();
            PluginVersion = hello["version"]?.GetValue<string>();
            PluginPid = pid;
            ConnectedAt = DateTimeOffset.UtcNow;
            LastHeartbeat = DateTimeOffset.UtcNow;
            Stats = null;
            log.LogInformation("Plugin {Version} connected (pid {Pid})", PluginVersion, PluginPid);
            ConnectionChanged?.Invoke();

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
                Dispatch(line);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Bridge connection failed");
        }
        finally
        {
            if (current == client)
            {
                current = null;
                writer = null;
                Stats = null;
                foreach (var kv in pending)
                    if (pending.TryRemove(kv.Key, out var tcs))
                        tcs.TrySetException(new BridgeException("The server disconnected"));
                log.LogInformation("Plugin disconnected");
                ConnectionChanged?.Invoke();
            }
            client.Dispose();
        }
    }

    private void Dispatch(string line)
    {
        JsonObject? msg;
        try
        {
            msg = JsonNode.Parse(line) as JsonObject;
        }
        catch (Exception e)
        {
            log.LogWarning("Bad message from plugin: {Error}", e.Message);
            return;
        }
        if (msg == null) return;

        switch (msg["t"]?.GetValue<string>())
        {
            case "hb":
                LastHeartbeat = DateTimeOffset.UtcNow;
                Stats = msg["stats"] as JsonObject;
                break;

            case "res":
                long id = msg["id"]?.GetValue<long>() ?? 0;
                if (!pending.TryRemove(id, out var tcs)) break;
                if (msg["ok"]?.GetValue<bool>() == true)
                    tcs.TrySetResult(msg["data"]?.DeepClone());
                else
                    tcs.TrySetException(new BridgeException(msg["error"]?.GetValue<string>() ?? "Command failed"));
                break;

            case "ev":
                LastHeartbeat = DateTimeOffset.UtcNow;
                var ts = DateTimeOffset.TryParse(msg["ts"]?.GetValue<string>(), out var parsed) ? parsed : DateTimeOffset.UtcNow;
                var ev = new PluginEvent(msg["kind"]?.GetValue<string>() ?? "?", ts, msg["data"] as JsonObject ?? []);
                try
                {
                    EventReceived?.Invoke(ev);
                }
                catch (Exception e)
                {
                    log.LogError(e, "Handling plugin event {Kind} failed", ev.Kind);
                }
                break;
        }
    }

    /// <summary>Sends a command to the plugin and waits for its answer.</summary>
    public async Task<JsonNode?> RequestAsync(string cmd, object? args = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        StreamWriter w = writer ?? throw new BridgeException("The server is not connected to the agent");
        long id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;

        JsonNode? argsNode = args switch
        {
            null => new JsonObject(),
            JsonNode node => node,
            _ => System.Text.Json.JsonSerializer.SerializeToNode(args),
        };
        var msg = new JsonObject { ["t"] = "req", ["id"] = id, ["cmd"] = cmd, ["args"] = argsNode };

        await writeLock.WaitAsync(ct);
        try
        {
            await w.WriteLineAsync(msg.ToJsonString());
        }
        catch (Exception e)
        {
            pending.TryRemove(id, out _);
            throw new BridgeException("Sending to the server failed: " + e.Message);
        }
        finally
        {
            writeLock.Release();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        await using var reg = cts.Token.Register(() =>
        {
            if (pending.TryRemove(id, out var t))
                t.TrySetException(new BridgeException("The server did not answer in time"));
        });
        return await tcs.Task;
    }

    /// <summary>Like <see cref="RequestAsync"/> but returns null instead of throwing.</summary>
    public async Task<JsonNode?> TryRequestAsync(string cmd, object? args = null, TimeSpan? timeout = null)
    {
        try
        {
            return await RequestAsync(cmd, args, timeout);
        }
        catch (BridgeException)
        {
            return null;
        }
    }
}
