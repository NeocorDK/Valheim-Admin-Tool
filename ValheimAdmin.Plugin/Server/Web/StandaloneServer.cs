using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using ValheimAdmin.Shared;

namespace ValheimAdmin.Web
{
    /// <summary>
    /// Standalone mode: the plugin serves the web panel itself, for hosts where the agent cannot run
    /// (rented servers, Linux). Starts on a dedicated server when no agent is configured. Offers the
    /// same HTTP API as the agent minus process control (start/stop, watchdog, schedules, SteamCMD),
    /// which needs something outside the game.
    /// </summary>
    public static class StandaloneServer
    {
        private static readonly string[] features = { "map", "overview", "console", "events", "players", "characters", "configs" };
        private static readonly string[] configExtensions = { ".cfg", ".yml", ".yaml", ".json", ".txt", ".ini", ".toml" };

        private static HttpListener listener;
        private static Thread thread;
        private static volatile bool running;
        private static WebAuth auth;
        private static LocalStore store;
        private static LogCapture logs;
        private static readonly Router router = new Router();
        private static readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> iconsUnavailable = new ConcurrentDictionary<string, byte>();
        private static int fetchingIcons;
        private static DateTime startedAt = DateTime.UtcNow;
        private static DateTime lastRetention = DateTime.MinValue;
        private static object publicMarkers;
        private static DateTime publicMarkersAt = DateTime.MinValue;

        public static bool Running => running;

        public static string DataDir => string.IsNullOrWhiteSpace(BepInExPlugin.WebDataDir.Value)
            ? MapService.DataRoot
            : Path.GetFullPath(BepInExPlugin.WebDataDir.Value);

        /// <summary>Called once the dedicated server is up (Game.Start).</summary>
        public static void Start()
        {
            if (running || AgentLink.Enabled || !BepInExPlugin.WebEnabled.Value) return;
            try
            {
                Directory.CreateDirectory(DataDir);
                EventText.Language = BepInExPlugin.WebLanguage.Value;
                WebAuth.PreparePassword();
                auth = new WebAuth(DataDir);
                store = new LocalStore(DataDir);
                logs = new LogCapture();
                Logger.Listeners.Add(logs);
                LoadFiles();
                MapRoutes();
                startedAt = DateTime.UtcNow;

                listener = new HttpListener();
                string bind = string.IsNullOrWhiteSpace(BepInExPlugin.WebBind.Value) ? "*" : BepInExPlugin.WebBind.Value.Trim();
                listener.Prefixes.Add("http://" + bind + ":" + BepInExPlugin.WebPort.Value + "/");
                listener.Start();
                running = true;
                thread = new Thread(Loop) { IsBackground = true, Name = "ValheimAdmin.Web" };
                thread.Start();
                AgentLink.LocalEvent = OnEvent;
                BepInExPlugin.Log("Web panel (standalone mode) listening on port " + BepInExPlugin.WebPort.Value + "; data in " + DataDir);
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Starting the web panel failed: " + e);
                running = false;
            }
        }

        public static void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { }
        }

        private static void Loop()
        {
            while (running)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception e)
                {
                    if (running) BepInExPlugin.Dbgl("Web accept failed: " + e.Message);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(context));
            }
        }

        private static void Handle(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                var request = new Request(context);
                if (!request.Path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    ServeFile(request);
                    return;
                }
                var handler = router.Find(request, out bool admin, out bool known);
                if (handler == null) throw new HttpError(known ? 405 : 404, "Not found");
                if (admin && !auth.IsValid(request.Cookie(WebAuth.CookieName))) throw new HttpError(401, "Please sign in");
                if (request.Method != "GET" && !SameOrigin(request)) throw new HttpError(403, "Cross-site request refused");
                object result = handler(request);
                if (result is HttpResult file) Respond.Result(context.Request, response, file);
                else Respond.Json(response, 200, result ?? new Dictionary<string, object> { { "ok", true } });
            }
            catch (Exception e)
            {
                int status = e is HttpError h ? h.Status
                    : e is KeyNotFoundException || e is FileNotFoundException ? 404
                    : e is UnauthorizedAccessException ? 403
                    : e is FormatException || e is ArgumentException ? 400
                    : e is InvalidOperationException ? 409 : 500;
                if (status == 500) BepInExPlugin.Warn("Web request failed: " + e);
                try
                {
                    Respond.Json(response, status, new Dictionary<string, object> { { "error", status == 404 && !(e is HttpError) ? "Not found" : e.Message } });
                }
                catch
                {
                }
            }
        }

        /// <summary>Browsers send Origin on cross-site POSTs; the SameSite=Strict cookie is the main defence.</summary>
        private static bool SameOrigin(Request request)
        {
            string origin = request.Header("Origin");
            if (string.IsNullOrEmpty(origin)) return true;
            return Uri.TryCreate(origin, UriKind.Absolute, out Uri o) &&
                   string.Equals(o.Authority, request.Header("Host"), StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- static files

        private static void LoadFiles()
        {
            Assembly assembly = typeof(StandaloneServer).Assembly;
            foreach (string name in assembly.GetManifestResourceNames())
            {
                string normalized = name.Replace('\\', '/');
                if (!normalized.StartsWith("web/", StringComparison.Ordinal)) continue;
                using (Stream s = assembly.GetManifestResourceStream(name))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    files["/" + normalized.Substring(4)] = ms.ToArray();
                }
            }
        }

        private static void ServeFile(Request request)
        {
            string path = request.Path == "/" ? "/index.html" : request.Path;
            if (!files.TryGetValue(path, out byte[] body))
            {
                Respond.Write(request.Context.Response, 404, "text/plain", System.Text.Encoding.UTF8.GetBytes("Not found"));
                return;
            }
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string type = ext == ".html" ? "text/html; charset=utf-8"
                : ext == ".js" ? "application/javascript; charset=utf-8"
                : ext == ".css" ? "text/css; charset=utf-8"
                : ext == ".png" ? "image/png"
                : ext == ".svg" ? "image/svg+xml" : "text/plain; charset=utf-8";
            request.Context.Response.AddHeader("Cache-Control", "no-cache");
            Respond.Write(request.Context.Response, 200, type, body);
        }

        // ---------------------------------------------------------------- events from the game

        /// <summary>Plugin events, as the agent would receive them: event log, sessions, snapshots.</summary>
        private static void OnEvent(string kind, Dictionary<string, object> data)
        {
            try
            {
                long ts = LocalStore.Now;
                var d = GameCalls.Plain(data) as Dictionary<string, object> ?? new Dictionary<string, object>();
                string player = d.Str("player");
                switch (kind)
                {
                    case "join":
                        if (!string.IsNullOrEmpty(player)) store.OpenSession(player, d.Str("host"), ts);
                        break;
                    case "leave":
                        if (!string.IsNullOrEmpty(player)) store.CloseSession(player, ts);
                        break;
                    case "started":
                        store.CloseAllSessions(ts);
                        break;
                    case "snapshot":
                        StoreSnapshot(d, ts);
                        return;
                }
                var entry = EventText.Describe(kind, d);
                if (entry != null) store.AddEvent(entry.Kind, entry.Player, entry.Message, d, ts);
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Recording event " + kind + " failed: " + e.Message);
            }
        }

        private static void StoreSnapshot(Dictionary<string, object> d, long ts)
        {
            string trigger = d.Str("trigger", "save");
            var snap = d.Obj("snapshot");
            if (trigger == "live" || snap == null) return;
            store.AddSnapshot(snap, trigger, d.Str("host"), ts);
            EnsureIcons(snap);
            if (DateTime.UtcNow - lastRetention > TimeSpan.FromHours(1))
            {
                lastRetention = DateTime.UtcNow;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        int removed = store.ApplyRetention(BepInExPlugin.WebKeepAllDays.Value, BepInExPlugin.WebKeepDailyDays.Value);
                        if (removed > 0) BepInExPlugin.Log("Snapshot retention removed " + removed);
                    }
                    catch (Exception e)
                    {
                        BepInExPlugin.Warn("Snapshot retention failed: " + e.Message);
                    }
                });
            }
        }

        /// <summary>Icons for a snapshot's items, rendered by an online player's game; background, best effort.</summary>
        private static void EnsureIcons(Dictionary<string, object> snapshot)
        {
            var wanted = SnapshotRules.Items(snapshot).OfType<Dictionary<string, object>>()
                .Select(i => new KeyValuePair<string, int>(i.Str("prefab"), i.Int("variant")))
                .Where(i => !string.IsNullOrEmpty(i.Key)).Distinct().ToList();
            if (wanted.Count == 0 || Interlocked.CompareExchange(ref fetchingIcons, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var missing = wanted.Where(i => !store.HasIcon(i.Key, i.Value) && !iconsUnavailable.ContainsKey(i.Key + "|" + i.Value)).ToList();
                    for (int start = 0; start < missing.Count; start += 48)
                    {
                        var chunk = missing.Skip(start).Take(48).ToList();
                        var items = chunk.Select(i => (object)new Dictionary<string, object> { { "prefab", i.Key }, { "variant", i.Value } }).ToList();
                        var result = GameCalls.Plain(GameCalls.Command("icons", new Dictionary<string, object> { { "items", items } }, 40000)) as Dictionary<string, object>;
                        if (result == null) return;
                        foreach (var i in chunk)
                        {
                            string png = result.Str(i.Key + "|" + i.Value);
                            if (!string.IsNullOrEmpty(png)) store.PutIcon(i.Key, i.Value, Convert.FromBase64String(png));
                            else iconsUnavailable[i.Key + "|" + i.Value] = 0;
                        }
                    }
                }
                catch (Exception e)
                {
                    BepInExPlugin.Dbgl("Fetching icons failed: " + e.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref fetchingIcons, 0);
                }
            });
        }

        // ---------------------------------------------------------------- routes

        private static Dictionary<string, object> Obj(params object[] pairs)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1];
            return d;
        }

        private static object Cmd(string cmd, Dictionary<string, object> args = null, int timeoutMs = 20000) =>
            GameCalls.Command(cmd, args, timeoutMs);

        private static void MapRoutes()
        {
            // ----- public: map
            router.Map("GET", "/api/public/info", r => Obj(
                "mode", "standalone",
                "name", ServerName(),
                "language", BepInExPlugin.WebLanguage.Value,
                "admin", auth.IsValid(r.Cookie(WebAuth.CookieName)),
                "secure", false,
                "features", features,
                "map", PublicMapInfo()), admin: false);
            router.Map("GET", "/api/public/map.png", r => MapFile(PublicMapInfo().Bool("publicFog") ? "publicMapFile" : "mapFile",
                PublicMapInfo().Bool("publicFog") ? "fogVersion" : "mapVersion"), admin: false);
            router.Map("GET", "/api/public/markers", r => PublicMarkers(), admin: false);

            // ----- auth
            router.Map("POST", "/api/login", r =>
            {
                string token = auth.Login(r.Ip, r.JsonBody().Str("password"));
                r.Context.Response.AddHeader("Set-Cookie", WebAuth.CookieName + "=" + token + "; Path=/; HttpOnly; SameSite=Strict; Max-Age=2592000");
                store.Audit(r.Ip, "login");
                return null;
            }, admin: false);
            router.Map("POST", "/api/logout", r =>
            {
                auth.Logout(r.Cookie(WebAuth.CookieName));
                r.Context.Response.AddHeader("Set-Cookie", WebAuth.CookieName + "=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0");
                return null;
            }, admin: false);
            router.Map("GET", "/api/me", r => Obj("ok", true, "language", BepInExPlugin.WebLanguage.Value));

            // ----- status & console
            router.Map("GET", "/api/status", r => Status());
            router.Map("POST", "/api/server/{action}", r =>
            {
                if (r.R("action") != "save") throw new HttpError(409, "Starting and stopping the server needs the Valheim Admin agent; use your host's panel.");
                store.Audit(r.Ip, "server-save");
                Cmd("save");
                return null;
            });
            router.Map("POST", "/api/console", r =>
            {
                var body = r.JsonBody();
                var parsed = ConsoleLine.Parse(body.Str("line", ""));
                if (parsed.Key == "help") return Obj("help", ConsoleLine.Commands);
                bool confirm = body.Bool("confirmCheats");
                if (parsed.Key == "cheat" && confirm) parsed.Value["confirmCheats"] = true;
                store.Audit(r.Ip, "console", body.Str("line") + (confirm ? " (cheat mark confirmed)" : ""));
                int timeout = parsed.Key == "cheat" || parsed.Key == "snapshot" || parsed.Key == "give" ? 45000 : 20000;
                return Obj("cmd", parsed.Key, "player", parsed.Value.Str("player"), "result", Cmd(parsed.Key, parsed.Value, timeout));
            });
            router.Map("GET", "/api/console/commands", r => Cmd("commands"));
            router.Map("GET", "/api/logs", r => logs.Recent(Math.Max(1, Math.Min(r.QInt("limit") ?? 1000, 5000)), r.QLong("after") ?? 0));

            // ----- events & players
            router.Map("GET", "/api/events", r =>
            {
                var kinds = (r.Q("kind") ?? "").Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
                return store.Events(kinds, r.Q("player"), r.Q("q"), r.QLong("before"), r.QLong("from"), r.QLong("to"),
                    Math.Max(1, Math.Min(r.QInt("limit") ?? 200, 1000)));
            });
            router.Map("GET", "/api/players/online", r => OnlinePlayers());
            router.Map("GET", "/api/players/history", r => store.History());
            router.Map("GET", "/api/players/{name}/sessions", r => store.Sessions(r.R("name")));
            router.Map("POST", "/api/players/action", r =>
            {
                var body = r.JsonBody();
                string action = body.Str("action");
                if (action != "kick" && action != "ban" && action != "unban") throw new ArgumentException("action");
                store.Audit(r.Ip, action, body.Str("player"));
                Cmd(action, Obj("player", body.Str("player")));
                return null;
            });
            router.Map("GET", "/api/lists/{list}", r => Cmd("list", Obj("list", ListName(r.R("list")))));
            router.Map("POST", "/api/lists/{list}", r =>
            {
                var body = r.JsonBody();
                string op = body.Str("op"), value = (body.Str("value") ?? "").Trim();
                if ((op != "add" && op != "remove") || value.Length == 0) throw new ArgumentException("op/value");
                store.Audit(r.Ip, "list-" + op, r.R("list") + ": " + value);
                Cmd(op == "add" ? "list_add" : "list_remove", Obj("list", ListName(r.R("list")), "value", value));
                return null;
            });
            router.Map("GET", "/api/items", r => Cmd("items", null, 30000));

            // ----- characters & snapshots
            router.Map("GET", "/api/characters", r =>
            {
                var online = new HashSet<long>(OnlinePlayers().OfType<Dictionary<string, object>>().Where(p => p.ContainsKey("characterId")).Select(p => p.Long("characterId")));
                return store.Characters().Select(c => { c["online"] = online.Contains(c.Long("characterId")); return (object)c; }).ToList();
            });
            router.Map("GET", "/api/characters/{id}/snapshots", r => store.SnapshotList(long.Parse(r.R("id"))));
            router.Map("GET", "/api/snapshots/{id}", r =>
            {
                long id = long.Parse(r.R("id"));
                var row = store.SnapshotRow(id) ?? throw new KeyNotFoundException();
                var content = store.SnapshotContent(id) ?? throw new KeyNotFoundException();
                EnsureIcons(content);
                return Obj("row", row, "content", SnapshotRules.Enrich(content));
            });
            router.Map("POST", "/api/snapshots/take", r =>
            {
                string player = r.JsonBody().Str("player");
                store.Audit(r.Ip, "snapshot", string.IsNullOrWhiteSpace(player) ? "*" : player);
                var args = Obj("trigger", "manual");
                if (!string.IsNullOrWhiteSpace(player)) args["player"] = player;
                Cmd("snapshot", args, 40000);
                return null;
            });
            router.Map("GET", "/api/snapshots/{id}/diff", r => Diff(long.Parse(r.R("id"))));
            router.Map("POST", "/api/snapshots/{id}/restore", r => Restore(long.Parse(r.R("id")), r.JsonBody(), r.Ip));
            router.Map("GET", "/api/icons/{prefab}/{variant}", r =>
            {
                byte[] png = store.Icon(r.R("prefab"), int.Parse(r.R("variant")));
                if (png == null) throw new KeyNotFoundException();
                var result = HttpResult.File(png, "image/png");
                result.Headers["Cache-Control"] = "private, max-age=86400";
                return result;
            });

            // ----- configs & plugins
            router.Map("GET", "/api/configs", r => ConfigList());
            router.Map("GET", "/api/configs/file", r =>
            {
                string path = r.Q("path");
                return Obj("path", path, "content", File.ReadAllText(ResolveConfig(path, mustExist: true)));
            });
            router.Map("PUT", "/api/configs/file", r =>
            {
                var body = r.JsonBody();
                string path = body.Str("path");
                string full = ResolveConfig(path, mustExist: false);
                if (File.Exists(full))
                {
                    string backup = Path.Combine(DataDir, "config-backups");
                    Directory.CreateDirectory(backup);
                    File.Copy(full, Path.Combine(backup, path.Replace('/', '_').Replace('\\', '_') + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak"), true);
                }
                LocalStore.WriteAtomic(full, System.Text.Encoding.UTF8.GetBytes(body.Str("content", "")));
                store.Audit(r.Ip, "config-save", path);
                store.AddEvent("config", null, EventText.T("config.saved", path), null, LocalStore.Now);
                return null;
            });
            router.Map("GET", "/api/plugins", r => Obj("files", PluginFiles(), "loaded", Cmd("plugins")));

            // ----- map (admin)
            router.Map("GET", "/api/map/info", r => GameCalls.OnMain(MapService.Info));
            router.Map("GET", "/api/map/full.png", r => MapFile("mapFile", "mapVersion"));
            router.Map("GET", "/api/map/fog.png", r => MapFile("fogFile", "fogVersion"));
            router.Map("GET", "/api/map/markers", r => Cmd("map_markers", Obj("admin", true)));
            router.Map("POST", "/api/map/regen", r => { store.Audit(r.Ip, "map-regen"); return Cmd("map_regen"); });
            router.Map("POST", "/api/map/pins", r =>
            {
                var body = r.JsonBody();
                string label = (body.Str("label") ?? "").Trim();
                if (label.Length > 80) throw new ArgumentException("label");
                store.Audit(r.Ip, "map-pin", label);
                publicMarkersAt = DateTime.MinValue;
                return Cmd("map_pin_add", Obj("x", body.Double("x"), "z", body.Double("z"), "label", label, "icon", body.Str("icon", "pin"), "public", body.Bool("public")));
            });
            router.Map("DELETE", "/api/map/pins/{id}", r =>
            {
                store.Audit(r.Ip, "map-pin-remove", r.R("id"));
                publicMarkersAt = DateTime.MinValue;
                return Cmd("map_pin_remove", Obj("id", r.R("id")));
            });
        }

        // ---------------------------------------------------------------- handlers

        private static string ServerName()
        {
            try
            {
                return string.IsNullOrEmpty(ZNet.m_ServerName) ? "Valheim" : ZNet.m_ServerName;
            }
            catch
            {
                return "Valheim";
            }
        }

        private static Dictionary<string, object> PublicMapInfo()
        {
            var i = GameCalls.OnMain(MapService.Info);
            bool publicFog = i.Bool("publicFog", true);
            var result = new Dictionary<string, object>();
            foreach (string key in new[] { "enabled", "state", "progress", "world", "size", "pixelSize", "publicFog" })
                if (i.ContainsKey(key)) result[key] = i[key];
            result["online"] = true;
            result["hasMap"] = i.Str(publicFog ? "publicMapFile" : "mapFile") != null;
            result["version"] = publicFog ? i.Long("fogVersion") : i.Long("mapVersion");
            result["fullVersion"] = i.Long("mapVersion");
            return result;
        }

        private static object MapFile(string fileKey, string versionKey)
        {
            var i = GameCalls.OnMain(MapService.Info);
            string path = i.Str(fileKey);
            if (path == null || !File.Exists(path)) throw new KeyNotFoundException();
            return HttpResult.File(File.ReadAllBytes(path), "image/png", "\"" + i.Long(versionKey) + "\"");
        }

        private static object PublicMarkers()
        {
            if (DateTime.UtcNow - publicMarkersAt < TimeSpan.FromSeconds(2) && publicMarkers != null) return publicMarkers;
            publicMarkers = Cmd("map_markers", Obj("admin", false));
            publicMarkersAt = DateTime.UtcNow;
            return publicMarkers;
        }

        private static List<object> OnlinePlayers() => GameCalls.Plain(Cmd("players")) as List<object> ?? new List<object>();

        private static object Status()
        {
            var stats = GameCalls.OnMain(ServerRole.Stats);
            long? memory = null;
            try
            {
                memory = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
            }
            catch
            {
            }
            return Obj(
                "agent", Obj("version", BepInExPlugin.pluginVersion, "language", BepInExPlugin.WebLanguage.Value, "world", stats.Str("world")),
                "server", Obj(
                    "state", "Running", "operation", null, "pid", Process.GetCurrentProcess().Id, "adopted", false,
                    "startedAt", startedAt.ToString("o"), "uptimeSec", (long)(DateTime.UtcNow - startedAt).TotalSeconds,
                    "cpuPercent", null, "memoryMb", memory, "lastExit", null, "pluginConnected", true,
                    "pluginVersion", BepInExPlugin.pluginVersion, "worldReady", stats.Bool("ready"), "stats", stats, "heartbeatAgoSec", 0),
                "players", OnlinePlayers(),
                "nextRestart", null,
                "update", Obj("running", false, "buildId", null, "available", false));
        }

        private static string ListName(string list)
        {
            if (list == "admin" || list == "banned" || list == "permitted") return list;
            throw new KeyNotFoundException();
        }

        /// <summary>The online player who owns the snapshot's character: same character id, or same name with the mod.</summary>
        private static string OnlineOwner(Dictionary<string, object> row)
        {
            var online = OnlinePlayers().OfType<Dictionary<string, object>>().ToList();
            var match = online.FirstOrDefault(p => p.ContainsKey("characterId") && p.Long("characterId") == row.Long("characterId"))
                ?? online.FirstOrDefault(p => string.Equals(p.Str("player"), row.Str("player"), StringComparison.OrdinalIgnoreCase) && p.ContainsKey("mod"));
            if (match == null) throw new HttpError(409, row.Str("player") + ": not online or has no Valheim Admin mod");
            return match.Str("player");
        }

        private static Dictionary<string, object> LiveSnapshot(string player, string trigger) =>
            GameCalls.Plain(Cmd("snapshot", Obj("player", player, "trigger", trigger), 40000)) as Dictionary<string, object>
            ?? throw new HttpError(409, "Empty snapshot");

        private static object Diff(long id)
        {
            var row = store.SnapshotRow(id) ?? throw new KeyNotFoundException();
            var content = store.SnapshotContent(id) ?? throw new KeyNotFoundException();
            var live = LiveSnapshot(OnlineOwner(row), "live");
            var ignore = (BepInExPlugin.WebIgnoreDataKeys.Value ?? "").Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
            return Obj(
                "items", SnapshotRules.Diff(content, live, ignore).Select(d => (object)Obj("index", d.Index, "missing", d.Missing, "similar", d.Similar)).ToList(),
                "skills", SnapshotRules.DiffSkills(content, live).Select(d => (object)Obj("type", d.Type, "name", d.Name, "displayName", d.DisplayName,
                    "snapshotLevel", d.SnapshotLevel, "currentLevel", d.CurrentLevel)).ToList(),
                "live", SnapshotRules.Enrich(live));
        }

        private static object Restore(long id, Dictionary<string, object> body, string who)
        {
            string mode = body.Str("mode"), skillMode = body.Str("skillMode");
            if (mode != "add" && mode != "replace") throw new ArgumentException("mode must be add or replace");
            if (skillMode != "none" && skillMode != "raise" && skillMode != "set") throw new ArgumentException("skillMode must be none, raise or set");
            var row = store.SnapshotRow(id) ?? throw new KeyNotFoundException();
            var content = store.SnapshotContent(id) ?? throw new KeyNotFoundException();
            string name = OnlineOwner(row);

            // Safety net: the current state is stored before anything changes.
            LiveSnapshot(name, "pre-restore");

            List<KeyValuePair<int, int?>> wanted = null;
            if (body.List("items") is List<object> items)
                wanted = items.OfType<Dictionary<string, object>>()
                    .Select(i => new KeyValuePair<int, int?>(i.Int("index"), i.ContainsKey("stack") && i["stack"] != null ? i.Int("stack") : (int?)null)).ToList();
            var payload = SnapshotRules.BuildRestore(content, mode, wanted, skillMode);
            var report = GameCalls.Plain(Cmd("restore", Obj("player", name, "payload", payload), 40000)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            store.AddEvent("restore", name, EventText.T("restore", name, id, report.Int("added"), report.Int("dropped"), report.Int("skills")),
                Obj("snapshot", id, "mode", mode, "skillMode", skillMode, "report", report), LocalStore.Now);
            store.Audit(who, "restore", "snapshot #" + id + " -> " + name + ", mode " + mode + ", items " + payload.List("items").Count + ", skills " + skillMode);
            return report;
        }

        private static string ResolveConfig(string relative, bool mustExist)
        {
            if (string.IsNullOrWhiteSpace(relative)) throw new ArgumentException("path");
            string root = Path.GetFullPath(Paths.ConfigPath);
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Path is outside BepInEx/config");
            if (full.StartsWith(Path.GetFullPath(DataDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The panel's own data is not editable");
            if (!configExtensions.Contains(Path.GetExtension(full).ToLowerInvariant()))
                throw new UnauthorizedAccessException("File type is not editable");
            if (mustExist && !File.Exists(full)) throw new FileNotFoundException(relative);
            if (mustExist && new FileInfo(full).Length > 4 * 1024 * 1024) throw new InvalidOperationException("File is too large");
            return full;
        }

        private static List<object> ConfigList()
        {
            string root = Path.GetFullPath(Paths.ConfigPath);
            string data = Path.GetFullPath(DataDir);
            return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => configExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFullPath(f).StartsWith(data, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(f => (object)Obj("path", f.FullName.Substring(root.Length + 1).Replace('\\', '/'), "size", f.Length, "modified", f.LastWriteTime.ToString("o")))
                .ToList();
        }

        private static List<object> PluginFiles()
        {
            string root = Path.GetFullPath(Paths.PluginPath);
            return Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(f =>
                {
                    string version = null;
                    try { version = FileVersionInfo.GetVersionInfo(f.FullName).FileVersion; } catch { }
                    return (object)Obj("path", f.FullName.Substring(root.Length + 1).Replace('\\', '/'), "size", f.Length, "version", version, "modified", f.LastWriteTime.ToString("o"));
                })
                .ToList();
        }
    }
}
