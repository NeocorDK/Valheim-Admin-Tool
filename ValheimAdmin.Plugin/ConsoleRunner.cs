using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimAdmin
{
    /// <summary>
    /// Runs game console commands (vanilla and modded) for the admin and captures what they print.
    ///
    /// Two game checks stand in the way of remote commands. Terminal.IsCheatsEnabled() is
    /// "m_cheat and ZNet.IsServer()", so it is always false on a client connected to a dedicated
    /// server; and ConsoleCommand.RunAction refuses cheats until the character is marked as having
    /// cheated (achievements). While a command runs here the first check is lifted, and the second one
    /// only when <see cref="Run"/> is told the admin accepted the mark (or on the server itself, which
    /// has no real character).
    /// </summary>
    public static class ConsoleRunner
    {
        public sealed class Result
        {
            public readonly List<string> Output = new List<string>();
        }

        private static List<string> capture;
        private static bool forceCheats;
        private static bool bypassCheatMark;

        public static Terminal.ConsoleCommand Find(string line)
        {
            string name = FirstWord(line);
            if (name.Length == 0) return null;
            return Terminal.commands.TryGetValue(name.ToLowerInvariant(), out var cmd) ? cmd : null;
        }

        public static string FirstWord(string line)
        {
            line = (line ?? "").Trim();
            int space = line.IndexOf(' ');
            return space < 0 ? line : line.Substring(0, space);
        }

        /// <summary>Would the command pass the game's validity check with cheats enabled?</summary>
        public static bool IsValidHere(Terminal context, Terminal.ConsoleCommand cmd)
        {
            bool old = forceCheats;
            forceCheats = true;
            try
            {
                return cmd.IsValid(context, true);
            }
            finally
            {
                forceCheats = old;
            }
        }

        /// <summary>
        /// Runs a console line on this machine. allowCheatMark lets cheat commands mark the local
        /// character as cheated, which the game requires before running any cheat.
        /// </summary>
        public static Result Run(Terminal context, string line, bool allowCheatMark)
        {
            var result = new Result();
            bool oldCheat = Terminal.m_cheat;
            capture = result.Output;
            forceCheats = true;
            bypassCheatMark = allowCheatMark;
            try
            {
                Terminal.m_cheat = true;
                context.TryRunCommand(line.Trim(), false, true);
            }
            finally
            {
                capture = null;
                forceCheats = false;
                bypassCheatMark = false;
                Terminal.m_cheat = oldCheat;
            }
            return result;
        }

        /// <summary>Game console commands for the panel's help and autocomplete.</summary>
        public static List<object> List()
        {
            return Terminal.commands.Values
                .Where(c => c != null && !c.IsSecret)
                .GroupBy(c => c.Command.ToLowerInvariant())
                .Select(g => g.First())
                .OrderBy(c => c.Command, StringComparer.OrdinalIgnoreCase)
                .Select(c => (object)new Dictionary<string, object>
                {
                    { "name", c.Command },
                    { "description", c.Description ?? "" },
                    { "cheat", c.IsCheat },
                    { "network", c.IsNetwork },
                    { "serverOnly", c.OnlyServer },
                })
                .ToList();
        }

        /// <summary>Strips Unity rich-text tags from captured output.</summary>
        public static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text ?? "";
            var sb = new System.Text.StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '<')
                {
                    int end = text.IndexOf('>', i);
                    if (end > i && end - i < 40 && IsTag(text.Substring(i + 1, end - i - 1)))
                    {
                        i = end + 1;
                        continue;
                    }
                }
                sb.Append(text[i++]);
            }
            return sb.ToString();
        }

        private static bool IsTag(string inner)
        {
            string name = inner.TrimStart('/').Split('=', ' ')[0].ToLowerInvariant();
            return name == "color" || name == "b" || name == "i" || name == "size" || name == "u" || name == "s" || name == "sprite" || name == "voffset";
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.AddString), typeof(string))]
        private static class Terminal_AddString_Patch
        {
            private static void Postfix(string text)
            {
                capture?.Add(Plain(text));
            }
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.IsCheatsEnabled))]
        private static class Terminal_IsCheatsEnabled_Patch
        {
            private static bool Prefix(ref bool __result)
            {
                if (!forceCheats) return true;
                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(Achievements), nameof(Achievements.IsCheatedAtAll))]
        private static class Achievements_IsCheatedAtAll_Patch
        {
            private static bool Prefix(ref bool __result)
            {
                if (!bypassCheatMark) return true;
                __result = true;
                return false;
            }
        }
    }
}
