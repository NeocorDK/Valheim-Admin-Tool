#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimAdmin.Shared
{
    /// <summary>
    /// Text console of the panel. "@Player command" runs a game console command (god, fly, spawn, ...)
    /// on that player's machine. The panel's own commands below come next; any other line, or a line
    /// starting with "/", is run as a game console command (vanilla or modded) on the server itself.
    /// </summary>
    public static class ConsoleLine
    {
        public static readonly string[] Commands =
        {
            "help", "status", "players", "save", "plugins", "kick", "ban", "unban", "say", "keys", "setkey", "removekey",
            "sleep", "events", "event", "stopevent", "give", "snapshot", "admins", "bans", "permits", "admin", "permit",
        };

        /// <summary>Returns (bridge command, args), or throws FormatException with a short usage hint.</summary>
        public static KeyValuePair<string, Dictionary<string, object>> Parse(string line)
        {
            line = (line ?? "").Trim();
            if (line.Length == 0) throw new FormatException("empty");

            if (line[0] == '/')
            {
                string game = line.Substring(1).Trim();
                if (game.Length == 0) throw new FormatException("/<game console command>");
                return Exec(game);
            }

            if (line[0] == '@')
            {
                var parts = SplitArgs(line.Substring(1));
                if (parts.Count < 2) throw new FormatException("@<player> <console command>");
                string player = parts[0];
                int at = line.IndexOf(player, 1, StringComparison.Ordinal) + player.Length;
                if (line.Length > at && line[at] == '"') at++;
                return Result("cheat", new Dictionary<string, object> { { "player", player }, { "command", line.Substring(at).Trim() } });
            }

            var tokens = SplitArgs(line);
            string name = tokens[0].ToLowerInvariant();
            string Arg(int i, string usage) => i < tokens.Count ? tokens[i] : throw new FormatException(usage);

            switch (name)
            {
                case "help":
                case "status":
                case "players":
                case "save":
                case "plugins":
                case "keys":
                case "sleep":
                case "events":
                    return Result(name, new Dictionary<string, object>());
                case "stopevent":
                    return Result("event_stop", new Dictionary<string, object>());
                case "kick":
                case "ban":
                case "unban":
                    return Result(name, new Dictionary<string, object> { { "player", Arg(1, name + " <player>") } });
                case "say":
                    int space = line.IndexOf(' ');
                    string text = space < 0 ? "" : line.Substring(space + 1).Trim();
                    if (text.Length == 0) throw new FormatException("say <text>");
                    return Result("broadcast", new Dictionary<string, object> { { "text", text }, { "center", true } });
                case "setkey":
                    return Result("key_set", new Dictionary<string, object> { { "key", Arg(1, "setkey <key>") } });
                case "removekey":
                    return Result("key_remove", new Dictionary<string, object> { { "key", Arg(1, "removekey <key>") } });
                case "event":
                    return Result("event_start", new Dictionary<string, object>
                    {
                        { "name", Arg(1, "event <name> <player>") }, { "player", Arg(2, "event <name> <player>") },
                    });
                case "give":
                    const string giveUsage = "give <player> <prefab> [count] [quality]";
                    var args = new Dictionary<string, object> { { "player", Arg(1, giveUsage) }, { "prefab", Arg(2, giveUsage) } };
                    if (tokens.Count > 3) args["count"] = int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : throw new FormatException(giveUsage);
                    if (tokens.Count > 4) args["quality"] = int.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int q) ? q : throw new FormatException(giveUsage);
                    return Result("give", args);
                case "snapshot":
                    var snap = new Dictionary<string, object> { { "trigger", "manual" } };
                    if (tokens.Count > 1) snap["player"] = tokens[1];
                    return Result("snapshot", snap);
                case "admins":
                    return Result("list", new Dictionary<string, object> { { "list", "admin" } });
                case "bans":
                    return Result("list", new Dictionary<string, object> { { "list", "banned" } });
                case "permits":
                    return Result("list", new Dictionary<string, object> { { "list", "permitted" } });
                case "admin":
                case "permit":
                    string usage = name + " add|remove <id>";
                    string op = Arg(1, usage).ToLowerInvariant();
                    if (op != "add" && op != "remove") throw new FormatException(usage);
                    return Result(op == "add" ? "list_add" : "list_remove", new Dictionary<string, object>
                    {
                        { "list", name == "admin" ? "admin" : "permitted" }, { "value", Arg(2, usage) },
                    });
                default:
                    return Exec(line);
            }
        }

        private static KeyValuePair<string, Dictionary<string, object>> Exec(string line) =>
            Result("exec", new Dictionary<string, object> { { "line", line } });

        private static KeyValuePair<string, Dictionary<string, object>> Result(string cmd, Dictionary<string, object> args) =>
            new KeyValuePair<string, Dictionary<string, object>>(cmd, args);

        /// <summary>Splits on spaces, keeping "quoted parts" together.</summary>
        public static List<string> SplitArgs(string text)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool quoted = false, any = false;
            foreach (char c in text ?? "")
            {
                if (c == '"') { quoted = !quoted; any = true; continue; }
                if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (any) result.Add(current.ToString());
                    current.Clear();
                    any = false;
                    continue;
                }
                current.Append(c);
                any = true;
            }
            if (any) result.Add(current.ToString());
            return result;
        }
    }
}
