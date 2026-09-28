#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ValheimAdmin.Shared
{
    /// <summary>A plugin event turned into an event-log row.</summary>
    public sealed class EventEntry
    {
        public string Kind;
        public string Player;
        public string Message;
    }

    /// <summary>
    /// Event-log texts in English and Russian, and how plugin events become log rows. Shared by the
    /// agent's event log and the plugin's own one in standalone mode.
    /// </summary>
    public static class EventText
    {
        public static string Language = "en";

        public static readonly Dictionary<string, KeyValuePair<string, string>> Texts = new Dictionary<string, KeyValuePair<string, string>>
        {
            // events
            { "join", P("{0} joined the server", "{0} зашёл на сервер") },
            { "leave", P("{0} left the server", "{0} вышел с сервера") },
            { "death", P("{0} died: {1}", "{0} погиб: {1}") },
            { "chat.shout", P("(shout) ", "(крик) ") },
            { "chat.whisper", P("(whisper) ", "(шёпот) ") },
            { "boss", P("Boss defeated: {0}", "Побеждён босс: {0}") },
            { "globalkey", P("Global key set: {0}", "Глобальный ключ: {0}") },
            { "raid", P("Raid: {0}", "Набег: {0}") },
            { "raid.near", P("Raid: {0} (near {1})", "Набег: {0} (рядом с {1})") },
            { "raid_end", P("Raid ended: {0}", "Набег закончился: {0}") },
            { "save", P("World saved", "Мир сохранён") },
            { "save.snapshots", P("World saved, snapshots requested: {0}", "Мир сохранён, запрошено слепков: {0}") },
            { "started", P("World \"{0}\" loaded", "Мир «{0}» загружен") },
            { "stopping", P("Server is saving the world and shutting down", "Сервер сохраняет мир и выключается") },
            { "restore", P("{0}: restored from snapshot #{1} — items {2}, dropped {3}, skills {4}", "{0}: восстановление из слепка №{1} — предметов {2}, на землю {3}, навыков {4}") },
            { "config.saved", P("Config saved: {0} (applies after restart)", "Конфиг сохранён: {0} (применится после рестарта)") },

            // death causes (HitData.HitType)
            { "cause.EnemyHit", P("killed by an enemy", "убит врагом") },
            { "cause.PlayerHit", P("killed by a player", "убит игроком") },
            { "cause.Fall", P("fall", "падение") },
            { "cause.Drowning", P("drowned", "утонул") },
            { "cause.Burning", P("burned", "сгорел") },
            { "cause.Freezing", P("froze", "замёрз") },
            { "cause.Poisoned", P("poison", "отравление") },
            { "cause.Water", P("water", "вода") },
            { "cause.Smoke", P("smoke", "задохнулся в дыму") },
            { "cause.EdgeOfWorld", P("edge of the world", "край мира") },
            { "cause.Impact", P("impact", "удар") },
            { "cause.Cart", P("cart", "телега") },
            { "cause.Tree", P("tree", "дерево") },
            { "cause.Self", P("self", "сам себя") },
            { "cause.Structural", P("collapse", "обрушение") },
            { "cause.Turret", P("turret", "турель") },
            { "cause.Boat", P("boat", "лодка") },
            { "cause.Stalagtite", P("stalactite", "сталактит") },
            { "cause.Catapult", P("catapult", "катапульта") },
            { "cause.CinderFire", P("cinder fire", "пепельный огонь") },
            { "cause.AshlandsOcean", P("boiling ocean", "кипящий океан") },
            { "cause.AshlandsLava", P("lava", "лава") },
            { "cause.Incinerator", P("obliterator", "мусоросжигатель") },
            { "cause.DrawBridge", P("drawbridge", "подъёмный мост") },
            { "cause.unknown", P("unknown cause", "причина неизвестна") },

            // bosses
            { "boss.defeated_eikthyr", P("Eikthyr", "Эйктюр") },
            { "boss.defeated_gdking", P("The Elder", "Древний") },
            { "boss.defeated_bonemass", P("Bonemass", "Масса костей") },
            { "boss.defeated_dragon", P("Moder", "Модер") },
            { "boss.defeated_goblinking", P("Yagluth", "Яглут") },
            { "boss.defeated_queen", P("The Queen", "Королева") },
            { "boss.defeated_fader", P("Fader", "Фейдер") },
        };

        private static KeyValuePair<string, string> P(string en, string ru) => new KeyValuePair<string, string>(en, ru);

        public static bool Has(string key) => Texts.ContainsKey(key);

        public static string T(string key, params object[] args)
        {
            if (!Texts.TryGetValue(key, out var t)) return key;
            string format = Language == "ru" ? t.Value : t.Key;
            return args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args);
        }

        public static string DeathCause(string hitType) =>
            !string.IsNullOrEmpty(hitType) && Has("cause." + hitType) ? T("cause." + hitType) :
            string.IsNullOrEmpty(hitType) || hitType == "unknown" || hitType == "Unknown" || hitType == "Undefined" ? T("cause.unknown") : hitType;

        public static string BossName(string key) =>
            key != null && Has("boss." + key.ToLowerInvariant()) ? T("boss." + key.ToLowerInvariant()) : key ?? "?";

        private static string S(Dictionary<string, object> d, string key)
        {
            string s = d.Str(key);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>The log row for a plugin event, or null for events that are not logged (mod, snapshot).</summary>
        public static EventEntry Describe(string kind, Dictionary<string, object> d)
        {
            string player = S(d, "player");
            switch (kind)
            {
                case "join":
                    return Row("join", player, T("join", player));
                case "leave":
                    return Row("leave", player, T("leave", player));
                case "death":
                    string attacker = S(d, "attacker");
                    return Row("death", player, T("death", player, DeathCause(S(d, "cause"))) + (attacker != null ? " (" + attacker + ")" : ""));
                case "chat":
                    string type = S(d, "type");
                    string prefix = type == "Shout" ? T("chat.shout") : type == "Whisper" ? T("chat.whisper") : "";
                    return Row("chat", player, prefix + player + ": " + S(d, "text"));
                case "boss":
                    return Row("boss", player, T("boss", BossName(S(d, "key"))));
                case "globalkey":
                    return Row("globalkey", player, T("globalkey", S(d, "key")));
                case "raid":
                    string near = S(d, "near");
                    return Row("raid", near, near != null ? T("raid.near", S(d, "name"), near) : T("raid", S(d, "name")));
                case "raid_end":
                    return Row("raid", null, T("raid_end", S(d, "name")));
                case "save":
                    int n = d.Int("snapshots");
                    return Row("save", null, n > 0 ? T("save.snapshots", n) : T("save"));
                case "started":
                    return Row("server", null, T("started", S(d, "world")));
                case "stopping":
                    return Row("server", null, T("stopping"));
                default:
                    return null;
            }
        }

        private static EventEntry Row(string kind, string player, string message) =>
            new EventEntry { Kind = kind, Player = player, Message = message };

        public static IEnumerable<(string Key, string En, string Ru)> All() => Texts.Select(kv => (kv.Key, kv.Value.Key, kv.Value.Value));
    }
}
