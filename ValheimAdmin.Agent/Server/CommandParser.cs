using System.Text.Json.Nodes;

namespace ValheimAdmin.Agent.Server;

public sealed record ParsedCommand(string Cmd, JsonObject Args);

/// <summary>
/// Text console of the panel. "@Player command" runs a game console command (god, fly, spawn, ...)
/// on that player's machine; everything else is a server command.
/// </summary>
public static class CommandParser
{
    public static readonly string[] Commands =
    [
        "help", "status", "players", "save", "plugins", "kick", "ban", "unban", "say", "keys", "setkey", "removekey",
        "sleep", "events", "event", "stopevent", "give", "snapshot", "admins", "bans", "permits", "admin", "permit",
    ];

    /// <summary>Returns the command, or throws FormatException with a short usage hint.</summary>
    public static ParsedCommand Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0) throw new FormatException("empty");

        if (line[0] == '@')
        {
            var parts = AgentConfig.SplitArgs(line[1..]);
            if (parts.Count < 2) throw new FormatException("@<player> <console command>");
            string player = parts[0];
            int at = line.IndexOf(player, 1, StringComparison.Ordinal) + player.Length;
            if (line.Length > at && line[at] == '"') at++;
            return new("cheat", new JsonObject { ["player"] = player, ["command"] = line[at..].Trim() });
        }

        var tokens = AgentConfig.SplitArgs(line);
        string name = tokens[0].ToLowerInvariant();
        string Arg(int i, string usage) => i < tokens.Count ? tokens[i] : throw new FormatException(usage);
        string Rest() => line[(line.IndexOf(' ') is var i && i >= 0 ? i + 1 : line.Length)..].Trim();

        switch (name)
        {
            case "status":
            case "players":
            case "save":
            case "plugins":
            case "keys":
            case "sleep":
            case "events":
                return new(name, []);
            case "stopevent":
                return new("event_stop", []);
            case "kick":
            case "ban":
            case "unban":
                return new(name, new JsonObject { ["player"] = Arg(1, $"{name} <player>") });
            case "say":
                string text = Rest();
                if (text.Length == 0) throw new FormatException("say <text>");
                return new("broadcast", new JsonObject { ["text"] = text, ["center"] = true });
            case "setkey":
                return new("key_set", new JsonObject { ["key"] = Arg(1, "setkey <key>") });
            case "removekey":
                return new("key_remove", new JsonObject { ["key"] = Arg(1, "removekey <key>") });
            case "event":
                return new("event_start", new JsonObject { ["name"] = Arg(1, "event <name> <player>"), ["player"] = Arg(2, "event <name> <player>") });
            case "give":
                const string giveUsage = "give <player> <prefab> [count] [quality]";
                var args = new JsonObject { ["player"] = Arg(1, giveUsage), ["prefab"] = Arg(2, giveUsage) };
                if (tokens.Count > 3) args["count"] = int.TryParse(tokens[3], out int c) ? c : throw new FormatException(giveUsage);
                if (tokens.Count > 4) args["quality"] = int.TryParse(tokens[4], out int q) ? q : throw new FormatException(giveUsage);
                return new("give", args);
            case "snapshot":
                var snap = new JsonObject { ["trigger"] = "manual" };
                if (tokens.Count > 1) snap["player"] = tokens[1];
                return new("snapshot", snap);
            case "admins":
                return new("list", new JsonObject { ["list"] = "admin" });
            case "bans":
                return new("list", new JsonObject { ["list"] = "banned" });
            case "permits":
                return new("list", new JsonObject { ["list"] = "permitted" });
            case "admin":
            case "permit":
                string usage = $"{name} add|remove <id>";
                string op = Arg(1, usage).ToLowerInvariant();
                if (op is not ("add" or "remove")) throw new FormatException(usage);
                return new(op == "add" ? "list_add" : "list_remove",
                    new JsonObject { ["list"] = name == "admin" ? "admin" : "permitted", ["value"] = Arg(2, usage) });
            default:
                throw new FormatException("unknown");
        }
    }
}
