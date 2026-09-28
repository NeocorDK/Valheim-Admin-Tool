using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ValheimAdmin.Agent.Bridge;
using ValheimAdmin.Agent.Data;

namespace ValheimAdmin.Agent.Snapshots;

/// <summary>
/// Item icons as PNG, keyed "prefab|variant". The dedicated server has no GPU, so icons are
/// rendered by the game of any online player with the mod (vanilla and modded items alike) and
/// kept here for good.
/// </summary>
public sealed class IconStore(Db db, PluginBridge bridge, ILogger<IconStore> log)
{
    private const int Batch = 48;
    private readonly ConcurrentDictionary<string, byte> unavailable = new();
    private readonly SemaphoreSlim fetching = new(1, 1);

    public static string Key(string prefab, int variant) => $"{prefab}|{variant}";

    public byte[]? Get(string prefab, int variant) =>
        db.Query("SELECT png FROM icons WHERE key=$k", r => (byte[])r[0], ("$k", Key(prefab, variant))).FirstOrDefault();

    /// <summary>Icons for a snapshot's items, fetched in the background; best effort.</summary>
    public void EnsureFor(JsonObject snapshot)
    {
        var wanted = SnapshotLogic.Items(snapshot).OfType<JsonObject>()
            .Select(i => (Prefab: SnapshotLogic.Str(i, "prefab"), Variant: SnapshotLogic.Int(i, "variant")))
            .Where(i => !string.IsNullOrEmpty(i.Prefab))
            .Select(i => (i.Prefab!, i.Variant))
            .ToList();
        if (wanted.Count > 0) _ = Task.Run(() => EnsureAsync(wanted));
    }

    public async Task EnsureAsync(IReadOnlyCollection<(string Prefab, int Variant)> items)
    {
        if (!bridge.Connected || !bridge.WorldReady) return;
        if (!await fetching.WaitAsync(0)) return; // one fetch at a time; the next snapshot tries again
        try
        {
            var known = db.Query("SELECT key FROM icons", r => r.GetString(0)).ToHashSet();
            var missing = items.Distinct()
                .Where(i => !known.Contains(Key(i.Prefab, i.Variant)) && !unavailable.ContainsKey(Key(i.Prefab, i.Variant)))
                .ToList();
            foreach (var chunk in missing.Chunk(Batch))
            {
                var args = new JsonObject
                {
                    ["items"] = new JsonArray(chunk.Select(i => (JsonNode)new JsonObject { ["prefab"] = i.Prefab, ["variant"] = i.Variant }).ToArray()),
                };
                if (await bridge.TryRequestAsync("icons", args, TimeSpan.FromSeconds(40)) is not JsonObject result)
                    return; // nobody online who can render
                int stored = 0;
                foreach (var (prefab, variant) in chunk)
                {
                    string key = Key(prefab, variant);
                    if (result[key] is JsonValue v && v.TryGetValue(out string? b64) && b64.Length > 0)
                    {
                        db.Exec("INSERT OR REPLACE INTO icons(key, png, ts) VALUES($k, $p, $t)",
                            ("$k", key), ("$p", Convert.FromBase64String(b64)), ("$t", Db.Now));
                        stored++;
                    }
                    else
                        unavailable[key] = 0;
                }
                log.LogDebug("Stored {Count} item icons", stored);
            }
        }
        catch (Exception e)
        {
            log.LogWarning("Fetching item icons failed: {Error}", e.Message);
        }
        finally
        {
            fetching.Release();
        }
    }
}
