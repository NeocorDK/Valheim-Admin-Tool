using System.Text.Json.Nodes;
using ValheimAdmin.Agent.Bridge;

namespace ValheimAdmin.Agent.Server;

/// <summary>
/// The world map drawn by the plugin (map picture, explored areas, markers). The plugin writes the
/// PNGs next to the server and tells the agent where; the agent serves them, also to visitors who
/// are not logged in. The last known state is kept, so the map stays up while the server restarts.
/// </summary>
public sealed class MapProxy(PluginBridge bridge)
{
    private static readonly TimeSpan InfoTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PublicMarkersTtl = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim infoLock = new(1, 1);
    private JsonObject? info;
    private DateTimeOffset infoAt = DateTimeOffset.MinValue;
    private JsonNode? publicMarkers;
    private DateTimeOffset publicMarkersAt = DateTimeOffset.MinValue;

    public async Task<JsonObject> InfoAsync()
    {
        if (DateTimeOffset.UtcNow - infoAt < InfoTtl && info != null) return info;
        await infoLock.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - infoAt >= InfoTtl || info == null)
            {
                if (bridge.Connected && bridge.WorldReady && await bridge.TryRequestAsync("map_info") is JsonObject fresh)
                {
                    fresh["online"] = true;
                    info = fresh;
                }
                else if (info != null)
                    info["online"] = false;
                else
                    info = new JsonObject { ["enabled"] = true, ["state"] = "offline", ["online"] = false };
                infoAt = DateTimeOffset.UtcNow;
            }
            return info;
        }
        finally
        {
            infoLock.Release();
        }
    }

    /// <summary>
    /// A PNG the plugin reported: "full" (the whole map, admins only), "public" (unexplored areas
    /// blacked out by the plugin when the public map has fog, else the whole map) or "fog".
    /// </summary>
    public async Task<(string Path, long Version)?> FileAsync(string which)
    {
        var i = await InfoAsync();
        bool publicFog = i["publicFog"]?.GetValue<bool>() != false;
        (string key, string versionKey) = which switch
        {
            "fog" => ("fogFile", "fogVersion"),
            "public" when publicFog => ("publicMapFile", "fogVersion"),
            _ => ("mapFile", "mapVersion"),
        };
        string? path = SnapshotString(i, key);
        long version = i[versionKey]?.GetValue<long>() ?? 0;
        if (path == null || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        return (path, version);
    }

    /// <summary>Map state without server file paths, for visitors.</summary>
    public async Task<JsonObject> PublicInfoAsync()
    {
        var i = await InfoAsync();
        var result = new JsonObject();
        foreach (string key in new[] { "enabled", "state", "progress", "world", "size", "pixelSize", "publicFog", "online" })
            if (i[key] != null) result[key] = i[key]!.DeepClone();
        bool publicFog = i["publicFog"]?.GetValue<bool>() != false;
        result["hasMap"] = SnapshotString(i, publicFog ? "publicMapFile" : "mapFile") != null;
        result["version"] = (publicFog ? i["fogVersion"] : i["mapVersion"])?.DeepClone();
        result["fullVersion"] = i["mapVersion"]?.DeepClone();
        return result;
    }

    public async Task<JsonNode?> MarkersAsync(bool admin)
    {
        if (admin) return await bridge.RequestAsync("map_markers", new JsonObject { ["admin"] = true });
        if (DateTimeOffset.UtcNow - publicMarkersAt < PublicMarkersTtl) return publicMarkers;
        publicMarkers = bridge.Connected && bridge.WorldReady
            ? await bridge.TryRequestAsync("map_markers", new JsonObject { ["admin"] = false })
            : null;
        publicMarkersAt = DateTimeOffset.UtcNow;
        return publicMarkers;
    }

    public async Task<JsonNode?> CommandAsync(string cmd, JsonObject? args = null)
    {
        var result = await bridge.RequestAsync(cmd, args ?? []);
        infoAt = DateTimeOffset.MinValue;
        publicMarkersAt = DateTimeOffset.MinValue;
        return result;
    }

    private static string? SnapshotString(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s) ? s : null;
}
