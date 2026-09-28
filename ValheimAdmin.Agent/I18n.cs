using System.Globalization;

namespace ValheimAdmin.Agent;

/// <summary>
/// Texts the agent writes itself: event messages, its own log lines and messages shown in game.
/// The language comes from AgentConfig.Language ("en" or "ru"); the web panel has its own dictionary.
/// </summary>
public static class I18n
{
    public static string Language
    {
        get => Shared.EventText.Language;
        set => Shared.EventText.Language = value;
    }

    private static readonly Dictionary<string, (string En, string Ru)> texts = new()
    {
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

    };

    /// <summary>Agent texts, then the event-log texts shared with the plugin (<see cref="Shared.EventText"/>).</summary>
    public static string T(string key, params object?[] args)
    {
        if (!texts.TryGetValue(key, out var t)) return Shared.EventText.T(key, args!);
        string format = Language == "ru" ? t.Ru : t.En;
        return args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args);
    }

    public static bool Has(string key) => texts.ContainsKey(key) || Shared.EventText.Has(key);

    public static string DeathCause(string? hitType) => Shared.EventText.DeathCause(hitType);

    public static string BossName(string? key) => Shared.EventText.BossName(key);

    /// <summary>Every key must have both translations; used by tests.</summary>
    public static IEnumerable<(string Key, string En, string Ru)> All() =>
        texts.Select(kv => (kv.Key, kv.Value.En, kv.Value.Ru)).Concat(Shared.EventText.All());
}
