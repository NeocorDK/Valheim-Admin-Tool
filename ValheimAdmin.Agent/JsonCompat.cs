using System.Text.Json.Nodes;

namespace ValheimAdmin.Agent;

/// <summary>
/// Converts between System.Text.Json nodes (agent) and the Dictionary/List model of the shared
/// code in ValheimAdmin.Common (also compiled into the plugin).
/// </summary>
public static class JsonCompat
{
    public static Dictionary<string, object> ToDict(JsonObject o) => Json.ParseObject(o.ToJsonString());

    public static JsonNode? ToNode(object? value) => JsonNode.Parse(Json.Serialize(value));

    public static JsonObject ToObject(object? value) => ToNode(value) as JsonObject ?? [];
}
