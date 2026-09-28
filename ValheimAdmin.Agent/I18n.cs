using System.Globalization;

namespace ValheimAdmin.Agent;

/// <summary>
/// Texts the agent writes itself: event messages, its own log lines and messages shown in game.
/// The language comes from AgentConfig.Language ("en" or "ru"); the web panel has its own dictionary.
/// </summary>
public static class I18n
{
    public static string Language { get; set; } = "en";

    private static readonly Dictionary<string, (string En, string Ru)> texts = new()
    {
        // events
        ["join"] = ("{0} joined the server", "{0} зашёл на сервер"),
        ["leave"] = ("{0} left the server", "{0} вышел с сервера"),
        ["death"] = ("{0} died: {1}", "{0} погиб: {1}"),
        ["chat.shout"] = ("(shout) ", "(крик) "),
        ["chat.whisper"] = ("(whisper) ", "(шёпот) "),
        ["boss"] = ("Boss defeated: {0}", "Побеждён босс: {0}"),
        ["globalkey"] = ("Global key set: {0}", "Глобальный ключ: {0}"),
        ["raid"] = ("Raid: {0}", "Набег: {0}"),
        ["raid.near"] = ("Raid: {0} (near {1})", "Набег: {0} (рядом с {1})"),
        ["raid_end"] = ("Raid ended: {0}", "Набег закончился: {0}"),
        ["save"] = ("World saved", "Мир сохранён"),
        ["save.snapshots"] = ("World saved, snapshots requested: {0}", "Мир сохранён, запрошено слепков: {0}"),
        ["started"] = ("World \"{0}\" loaded", "Мир «{0}» загружен"),
        ["stopping"] = ("Server is saving the world and shutting down", "Сервер сохраняет мир и выключается"),
        ["restore"] = ("{0}: restored from snapshot #{1} — items {2}, dropped {3}, skills {4}", "{0}: восстановление из слепка №{1} — предметов {2}, на землю {3}, навыков {4}"),

        // server lifecycle
        ["server.starting"] = ("Server is starting ({0})", "Сервер запускается ({0})"),
        ["server.stopping"] = ("Server is stopping ({0})", "Сервер останавливается ({0})"),
        ["server.stopped"] = ("Server stopped", "Сервер остановлен"),
        ["server.killed"] = ("Server process killed ({0})", "Процесс сервера принудительно завершён ({0})"),
        ["server.adopted"] = ("Took over the already running server (pid {0})", "Подхвачен уже запущенный сервер (pid {0})"),
        ["server.pluginMissing"] = ("ValheimAdmin.dll was not found in {0}: the panel will not see the server. Put the mod back into BepInEx\\plugins.",
            "ValheimAdmin.dll не найден в {0}: панель не увидит сервер. Верните мод в BepInEx\\plugins."),
        ["server.pluginDuplicate"] = ("ValheimAdmin.dll is installed more than once ({0}); keep a single copy in BepInEx\\plugins.",
            "ValheimAdmin.dll установлен несколько раз ({0}); оставьте одну копию в BepInEx\\plugins."),
        ["server.duplicate"] = ("A second server copy is running from the same folder (pid {0}, managed: pid {1}). Use Stop or Kill to end both, then Start.",
            "Из той же папки запущена вторая копия сервера (pid {0}, управляемая: pid {1}). Нажмите «Остановить» или «Убить процесс», чтобы завершить обе, затем «Запустить»."),
        ["server.forcekill"] = ("Server did not stop, process killed", "Сервер не остановился, процесс завершён принудительно"),
        ["server.crashed"] = ("Server crashed (exit code {0})", "Сервер упал (код выхода {0})"),
        ["server.hung"] = ("Server hung: no response for {0} s, restarting the process", "Сервер завис: нет ответа {0} с, процесс будет перезапущен"),
        ["server.noplugin"] = ("The Valheim Admin mod did not connect within 10 minutes — check that ValheimAdmin.dll is in BepInEx\\plugins",
            "Мод Valheim Admin не подключился к агенту за 10 минут — проверьте, что ValheimAdmin.dll лежит в BepInEx\\plugins"),
        ["server.newlog"] = ("──────── new server log ────────", "──────── новый лог сервера ────────"),
        ["server.notfound"] = ("{0} not found. Check ServerDir in agent.json.", "Не найден {0}. Проверьте ServerDir в agent.json."),
        ["watchdog.restart"] = ("Watchdog: restarting in {0} s ({1})", "Вахта: перезапуск через {0} с ({1})"),
        ["watchdog.giveup"] = ("Watchdog: {0} restarts within an hour, automatic restarts stopped", "Вахта: {0} перезапусков за час, автоматический перезапуск остановлен"),
        ["watchdog.failed"] = ("Watchdog could not start the server: {0}", "Вахта не смогла запустить сервер: {0}"),

        // reasons
        ["reason.autostart"] = ("agent start", "запуск агента"),
        ["reason.manual"] = ("from the panel", "из панели"),
        ["reason.crash"] = ("crash", "падение"),
        ["reason.hang"] = ("hang", "зависание"),
        ["reason.watchdog"] = ("watchdog: {0}", "вахта: {0}"),
        ["reason.scheduled"] = ("scheduled restart", "плановый рестарт"),
        ["reason.update"] = ("update", "обновление"),
        ["reason.after"] = ("after {0}", "после: {0}"),
        ["reason.agentstop"] = ("agent is stopping", "агент останавливается"),

        // in game
        ["game.shutdown"] = ("Server is shutting down", "Сервер выключается"),
        ["game.restart.in"] = ("Server restart in {0} min", "Рестарт сервера через {0} мин"),
        ["game.restart.now"] = ("Server is restarting now", "Сервер перезапускается"),
        ["game.update"] = ("Server is going down for an update", "Сервер выключается на обновление"),

        // update
        ["update.start"] = ("Server update started", "Началось обновление сервера"),
        ["update.backup"] = ("Backup before update: {0}", "Резервная копия перед обновлением: {0}"),
        ["update.done"] = ("Server update finished (build {0} → {1})", "Обновление сервера завершено (сборка {0} → {1})"),
        ["update.failed"] = ("Server update failed: {0}", "Обновление сервера не удалось: {0}"),

        // configs
        ["config.saved"] = ("Config saved: {0} (applies after restart)", "Конфиг сохранён: {0} (применится после рестарта)"),

        // death causes (HitData.HitType)
        ["cause.EnemyHit"] = ("killed by an enemy", "убит врагом"),
        ["cause.PlayerHit"] = ("killed by a player", "убит игроком"),
        ["cause.Fall"] = ("fall", "падение"),
        ["cause.Drowning"] = ("drowned", "утонул"),
        ["cause.Burning"] = ("burned", "сгорел"),
        ["cause.Freezing"] = ("froze", "замёрз"),
        ["cause.Poisoned"] = ("poison", "отравление"),
        ["cause.Water"] = ("water", "вода"),
        ["cause.Smoke"] = ("smoke", "задохнулся в дыму"),
        ["cause.EdgeOfWorld"] = ("edge of the world", "край мира"),
        ["cause.Impact"] = ("impact", "удар"),
        ["cause.Cart"] = ("cart", "телега"),
        ["cause.Tree"] = ("tree", "дерево"),
        ["cause.Self"] = ("self", "сам себя"),
        ["cause.Structural"] = ("collapse", "обрушение"),
        ["cause.Turret"] = ("turret", "турель"),
        ["cause.Boat"] = ("boat", "лодка"),
        ["cause.Stalagtite"] = ("stalactite", "сталактит"),
        ["cause.Catapult"] = ("catapult", "катапульта"),
        ["cause.CinderFire"] = ("cinder fire", "пепельный огонь"),
        ["cause.AshlandsOcean"] = ("boiling ocean", "кипящий океан"),
        ["cause.AshlandsLava"] = ("lava", "лава"),
        ["cause.Incinerator"] = ("obliterator", "мусоросжигатель"),
        ["cause.DrawBridge"] = ("drawbridge", "подъёмный мост"),
        ["cause.unknown"] = ("unknown cause", "причина неизвестна"),

        // bosses
        ["boss.defeated_eikthyr"] = ("Eikthyr", "Эйктюр"),
        ["boss.defeated_gdking"] = ("The Elder", "Древний"),
        ["boss.defeated_bonemass"] = ("Bonemass", "Масса костей"),
        ["boss.defeated_dragon"] = ("Moder", "Модер"),
        ["boss.defeated_goblinking"] = ("Yagluth", "Яглут"),
        ["boss.defeated_queen"] = ("The Queen", "Королева"),
        ["boss.defeated_fader"] = ("Fader", "Фейдер"),
    };

    public static string T(string key, params object?[] args)
    {
        if (!texts.TryGetValue(key, out var t)) return key;
        string format = Language == "ru" ? t.Ru : t.En;
        return args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args);
    }

    public static bool Has(string key) => texts.ContainsKey(key);

    public static string DeathCause(string? hitType) =>
        !string.IsNullOrEmpty(hitType) && Has("cause." + hitType) ? T("cause." + hitType) :
        string.IsNullOrEmpty(hitType) || hitType is "unknown" or "Unknown" or "Undefined" ? T("cause.unknown") : hitType;

    public static string BossName(string? key) =>
        key != null && Has("boss." + key.ToLowerInvariant()) ? T("boss." + key.ToLowerInvariant()) : key ?? "?";

    /// <summary>Every key must have both translations; used by tests.</summary>
    public static IEnumerable<(string Key, string En, string Ru)> All() => texts.Select(kv => (kv.Key, kv.Value.En, kv.Value.Ru));
}
