using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ValheimAdmin.Agent.Web;

/// <summary>Pushes log lines, events and status to every open panel over WebSockets.</summary>
public sealed class LiveHub(ILogger<LiveHub> log)
{
    private sealed class Client(WebSocket socket)
    {
        public readonly WebSocket Socket = socket;
        public readonly SemaphoreSlim Lock = new(1, 1);
    }

    private readonly ConcurrentDictionary<Guid, Client> clients = new();
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public int Count => clients.Count;

    public async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        clients[id] = new Client(socket);
        try
        {
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            clients.TryRemove(id, out _);
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
            }
        }
    }

    public void Broadcast(string type, object payload)
    {
        if (clients.IsEmpty) return;
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, payload }, JsonOptions));
        foreach (var (id, client) in clients)
            _ = SendAsync(id, client, bytes);
    }

    private async Task SendAsync(Guid id, Client client, byte[] bytes)
    {
        if (!await client.Lock.WaitAsync(TimeSpan.FromSeconds(5))) return;
        try
        {
            if (client.Socket.State == WebSocketState.Open)
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogDebug("WebSocket send failed: {Error}", e.Message);
            clients.TryRemove(id, out _);
        }
        finally
        {
            client.Lock.Release();
        }
    }
}
