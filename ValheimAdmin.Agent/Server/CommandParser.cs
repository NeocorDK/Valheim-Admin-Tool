using System.Text.Json.Nodes;
using ValheimAdmin.Shared;

namespace ValheimAdmin.Agent.Server;

public sealed record ParsedCommand(string Cmd, JsonObject Args);

/// <summary>The panel's text console; the rules live in <see cref="ConsoleLine"/> (shared with the plugin).</summary>
public static class CommandParser
{
    public static string[] Commands => ConsoleLine.Commands;

    /// <summary>Returns the command, or throws FormatException with a short usage hint.</summary>
    public static ParsedCommand Parse(string line)
    {
        var parsed = ConsoleLine.Parse(line);
        return new(parsed.Key, JsonCompat.ToObject(parsed.Value));
    }
}
