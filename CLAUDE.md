# CLAUDE.md — Valheim Admin

Guide for AI agents and maintainers. User-facing docs live in `README.md` / `README.ru.md` and
`package/thunderstore/README.md`; this file explains how the code works and the traps in it.
Keep it current: update the relevant section in the same change that alters behaviour.

## What it is

A web admin panel for Valheim dedicated servers, shipped as two artifacts:

| Artifact | Tech | Runs where | Role |
|---|---|---|---|
| `ValheimAdmin.Agent.exe` | .NET 10, ASP.NET Core minimal API, SQLite | Windows host next to the server | Serves the panel, owns the `valheim_server.exe` process (start/stop/watchdog/scheduled restarts/SteamCMD update), stores events, sessions, snapshots, audit log |
| `ValheimAdmin.dll` | BepInEx 5 plugin, net472, Harmony | Inside `valheim_server.exe` **and** optionally in players' games | One DLL, two roles: *server role* executes agent commands and reports events; *client role* snapshots/restores the local character and runs console commands for the admin |

```
browser ──HTTP/WS──▶ Agent ──TCP 127.0.0.1, line JSON, secret──▶ plugin (server role)
                                                                     │ Valheim routed RPC (VA_*)
                                                                     ▼
                                                     plugin (client role) in players' games
```

Valheim keeps inventories and skills **on the player's machine**, so snapshots and restores only
work for players who have the mod and are online.

### Planned: two modes (release work in progress)

The public (Thunderstore) release adds a **standalone mode**: when no agent is present, the plugin
serves the same panel itself (embedded HTTP server) so rented/Linux hosts work. The agent stays
for self-hosted Windows. A public map (no login) becomes the landing page. The plan is in
`C:\Users\Neocor\.claude\plans\lovely-doodling-llama.md` (stages 0–7); the branch
`snapshots-v0.2` holds the pre-release 0.2.0 version. Update this file as stages land.

## Repository layout

```
ValheimAdmin.slnx
ValheimAdmin.Agent/            agent (net10.0, Microsoft.NET.Sdk.Web)
  Program.cs                   host setup, Kestrel bindings (127.0.0.1 + Tailscale 100.64/10 + BindAddresses), cookie auth, DI
  AgentConfig.cs               agent.json model, password hashing (PBKDF2), server argument builder, SplitArgs
  I18n.cs                      agent-side texts (events, in-game announcements), en/ru
  Bridge/PluginBridge.cs       TCP listener for the plugin; request/response by id; events; heartbeat stats
  Data/Db.cs                   SQLite schema + helpers (events, sessions, snapshots, snapshot_data, audit)
  Logs/LogTailer.cs            tails BepInEx/LogOutput.log, ring buffer (5000), daily archive, level classifier
  Server/ServerManager.cs      owns valheim_server.exe: start, graceful stop (Ctrl+C helper), adopt running server, watchdog, duplicate detection, bridge admission
  Server/Scheduler.cs          daily restarts with in-game countdown, one-off restart, snapshot retention
  Server/Updater.cs            SteamCMD update with world+config backup
  Server/AdminService.cs       players cache, item list, diff, restore orchestration, Epic Loot enrichment
  Server/CommandParser.cs      text console → (cmd, args) for the bridge
  Server/ConfigFiles.cs        BepInEx/config editor with backups; plugin DLL list
  Server/EventService.cs       plugin events → events table + live push; stores snapshots
  Snapshots/SnapshotLogic.cs   pure functions: content hash, diff, retention, restore payload, Epic Loot parse
  Snapshots/SnapshotStore.cs   dedup storage (snapshot_data by hash, deflated JSON)
  Snapshots/EpicLootAdapter.cs optional Epic Loot rarity/effects for the panel
  Snapshots/IconStore.cs       item icon cache (icons table), fetched from players' games
  Web/Api.cs                   every HTTP route
  Web/LiveHub.cs               WebSocket broadcast (status, log, event, players, snapshot)
  Web/StatusPump.cs            status every 2 s; StatusBuilder; LoginGuard (5 fails → 10 min lock per IP)
  wwwroot/                     panel: vanilla JS ES modules, no build step (index.html, app.js, app.css, i18n.js)
ValheimAdmin.Agent.Tests/      xUnit: SnapshotLogicTests, MiscTests (parser, log levels, i18n, config, store, scheduler)
ValheimAdmin.Plugin/           plugin (net472)
  BepInExPlugin.cs             entry point, config entries, Update() pumps MainThread + ServerRole
  Protocol.cs                  RPC names (Rpc.*), deflate helpers, MainThread queue
  Json.cs                      minimal JSON (Dictionary<string,object>/List<object>/long/double) + typed getters
  Server/AgentLink.cs          TCP client to the agent (background thread), buffering of events
  Server/Commands.cs           agent command dispatcher (runs on Unity main thread)
  Server/ServerRole.cs         modded peer registry, server→client requests with timeouts, snapshot rounds, heartbeat
  Server/Hooks.cs              Harmony patches → events (join/leave/save/chat/boss/globalkey/raid/death fallback), RPC registration
  ConsoleRunner.cs             runs game console commands with output capture and the cheat-check overrides (both roles)
  Client/ClientRole.cs         client RPC handlers: console command, give, snapshot, restore, chat line
  Client/Snapshot.cs           Snapshot.Build, ItemBytes (game item serialization), ExtraInventories, Restorer
  Client/Icons.cs              renders item icons to PNG for the panel
  Client/Texts.cs              in-game messages (ru if the game runs in Russian)
package/thunderstore/          manifest.json, icon.png, README.md for the Thunderstore package
package.ps1                    release build: tests → plugin → self-contained agent → two zips in dist/
```

## Build, test, run

- Requirements: .NET 10+ SDK (the machine has 11 RC; `RollForward=Major` lets it run), a Valheim
  install with BepInEx for reference assemblies. Default path `G:\Steam\steamapps\common\Valheim`,
  override with `-p:ValheimPath=...`.
- Plugin: `dotnet build ValheimAdmin.Plugin -c Release -p:ValheimPath="..."` → `dist/plugin/ValheimAdmin.dll`.
  `assembly_valheim`/`assembly_utils` are referenced with `Publicize="true"` (BepInEx.AssemblyPublicizer), so private game members are accessible directly.
- Tests: `dotnet test ValheimAdmin.Agent.Tests`.
- Agent dev run: `dotnet run --project ValheimAdmin.Agent -- --config <path>\agent.json`.
- Release: `powershell -ExecutionPolicy Bypass -File package.ps1 [-ValheimPath ...] [-SkipTests]`.
  It fails unless the version matches in **three places**: `package/thunderstore/manifest.json`,
  `BepInExPlugin.pluginVersion`, `<Version>` in `ValheimAdmin.Agent.csproj`.
- Inspecting the game: `ilspycmd` is installed as a dotnet tool but targets .NET 6; run it with
  `DOTNET_ROLL_FORWARD=Major ilspycmd -t <Type> -r <Managed> <Managed>/assembly_valheim.dll`.
  Always check real signatures before patching or calling game code.

## Agent ↔ plugin protocol

TCP on `127.0.0.1:<BridgePort>` (default 27961), UTF-8, one JSON object per `\n`-terminated line.
The agent passes `VA_AGENT_PORT` and `VA_AGENT_SECRET` env vars when it launches the server; a server
started by hand can use `[Server] AgentPort` + `AgentSecretFile` in the plugin config. The secret
lives in `data/bridge.secret` and survives agent restarts, so a running server reconnects.

| Direction | `t` | Fields | Meaning |
|---|---|---|---|
| plugin→agent | `hello` | `secret`, `version`, `pid` | first line; agent checks secret (constant time) and `Admission(pid)` |
| agent→plugin | `welcome` / `refused` | `reason` | link accepted / refused (stray second server); plugin backs off 30 s on refusal |
| agent→plugin | `req` | `id`, `cmd`, `args` | command; plugin queues it to the Unity main thread |
| plugin→agent | `res` | `id`, `ok`, `data`, `error` | answer; agent times out after 15 s by default |
| plugin→agent | `ev` | `kind`, `ts`, `data` | event; buffered (max 2000) while the agent is away |
| plugin→agent | `hb` | `stats` | every 5 s: ready, fps, players, modded, world, zdos, day, event; watchdog uses it |

Event kinds: `started`, `stopping`, `join`, `leave`, `mod` (client role said hello), `death`, `chat`,
`boss`, `globalkey`, `raid`, `raid_end`, `save`, `snapshot` (carries the snapshot JSON).

### Server ↔ client RPCs (`Rpc.*` in Protocol.cs)

Routed RPCs with a request id; the client answers with `VA_Reply(reqId, ok, ZPackage deflated JSON)`.
The client accepts requests **only from the server peer** (`FromServer`). Server-side pending
requests time out in `ServerRole.Update`, and are failed when the peer disconnects.

`VA_Hello` (client→server on spawn: version, characterId, name), `VA_Reply`, `VA_Death`,
`VA_Cmd` (console line, 0.2 protocol), `VA_Run` (JSON `{line, confirmCheats}`), `VA_Give` (prefab, count, quality), `VA_SnapReq` (trigger),
`VA_Restore` (payload), `VA_Chat` (server message line in chat), `VA_Icons` (render item icons).

## Commands (panel console → bridge → plugin)

`CommandParser.Parse` turns a console line into `(cmd, args)`; `Commands.Run` executes it.

| Console | Bridge `cmd` | Notes |
|---|---|---|
| `status`, `players`, `save`, `plugins`, `keys`, `sleep`, `events` | same | `save` = `ZNet.Save(false, true, false)`; answer means "started", the `save` event confirms |
| `stopevent` | `event_stop` | |
| `kick/ban/unban <player>` | same | `ZNet.Kick/Ban/Unban(string)` |
| `say <text>` | `broadcast {text, center:true}` | `ShowMessage` routed RPC to everybody + `VA_Chat` to modded players |
| `setkey/removekey <key>` | `key_set/key_remove` | |
| `event <name> <player>` | `event_start` | at the player's position |
| `give <player> <prefab> [count] [quality]` | `give` | via client role if modded, else dropped at the player's feet by the server |
| `snapshot [player]` | `snapshot {trigger:manual}` | |
| `admins/bans/permits`, `admin|permit add|remove <id>` | `list`, `list_add`, `list_remove` | SyncedList on ZNet |
| `@Player <line>` | `cheat {player, command, confirmCheats?}` | game console command on that player's machine (see below) |
| `/<line>` or any other line | `exec {line}` | game console command (vanilla or any mod's) run **on the server** via `ConsoleRunner`; answer `{output:[...]}` |
| — (panel only) | `items`, `commands`, `restore`, `shutdown` | item list, game command list (help/autocomplete, `/api/console/commands`), snapshot restore, graceful stop |

Panel commands win over game commands with the same name (`kick`, `event`, `setkey`…); a leading
`/` forces the game command. `@Player` flow (`Commands.PlayerCommand` → `VA_Run`, or `VA_Cmd` for
clients older than 0.3): the client answers `{output}`, `{needsConfirm, command}` when a cheat would
mark a not-yet-cheated character (the panel asks the admin and resends with `confirmCheats`), or
`{runOnServer}` for server-only/remote commands, which the server then runs itself (`ranOnServer`).

## Console facts (verified against assembly_valheim, 2026-09)

- `Terminal.TryRunCommand(text, silentFail, skipAllowedCheck)` looks the command up in the static
  `Terminal.commands` and checks `ConsoleCommand.IsValid(context, skipAllowedCheck)`.
- `IsValid` requires `context.IsCheatsEnabled()` for `IsCheat` commands, and
  `IsCheatsEnabled()` is `m_cheat && ZNet.instance.IsServer()`. **On a client connected to a
  dedicated server it is always false**, so setting `Terminal.m_cheat = true` is not enough; cheat
  commands on clients fail with "not valid in the current context" unless `IsCheatsEnabled` is patched.
- `ConsoleCommand.RunAction` refuses cheat commands with `$achievements_confirm_cheat` unless
  `Achievements.IsCheatedAtAll()` (profile `m_usedCheats`, cheated world modifiers, cheated items,
  or `Game.isModded`). Running a cheat sets `playerProfile.m_usedCheats = true` — it **permanently
  marks the character** (achievements). Never do that to a player without explicit admin confirmation.
- `IsWorldCheated()` dereferences `ServerOptionsGUI.m_instance`; be careful calling cheat paths on
  a headless server.
- `IsNetwork`/`RemoteCommand` commands on a non-server client are sent to the server through
  `ZNet.RemoteCommand`, which requires the client's host id in the admin list. The dedicated server
  runs them with `Console.instance.TryRunCommand` (so `Console.instance` exists on the server).
- `Terminal.AddString(string)` is the output sink; the plugin captures output with a Harmony postfix.
- `ConsoleRunner` (plugin) wraps all of this: while it runs a line it forces `IsCheatsEnabled()`
  and, only when allowed (server side, or admin-confirmed on a client), `Achievements.IsCheatedAtAll()`.
- `ShowMessage` is registered by `MessageHud` as `<int type, string text>`.
- `ZNet.Save(bool sync, bool saveOtherPlayerProfiles = false, bool waitForNextFrame = false)`.

## Snapshots (mod-agnostic)

- Taken by the client role (`Snapshot.Build`) on every world save (`ZNet.SaveWorld` postfix →
  `SnapshotAll("save")`), on demand, and as `pre-restore` before a restore. `live` snapshots (used
  by *Compare*) are not stored.
- JSON `v`=2 (v1 = no `raw`/`itemVersion`/`containers`/`playerData`/`displayName`; still restorable):
  `name`, `characterId` (`PlayerProfile.GetPlayerID()`), `itemVersion` (header int of
  `Inventory.Save`, read at runtime), `inventory {w,h}`, `containers [{key,w,h}]`, `items[]`,
  `skills[]`, `playerData` (`Player.m_customData`, view only, never restored), `health`, `pos`,
  `trigger`, `takenAt`.
  - Item: `prefab`, `token`, `label`, `tooltip` (localized `GetTooltip(-1)`, so mods that patch
    tooltips show up), `itemType`, `maxStack`, `stack`, `durability`, `maxDurability`, `x`, `y`,
    `equipped`, `quality`, `variant`, `crafterId`, `crafterName`, `worldLevel`, `pickedUp`,
    `cheated` (`m_cheated`, achievements), `data` (whole `m_customData`), `raw` (base64 of
    `ItemDrop.ItemData.Save`), `container` (only for items in a mod inventory).
  - Skill: `type` (int of `Skills.SkillType`; SkillManager skills are hashes), `name`, `displayName`
    (`Localize("$skill_" + type.ToLower())`, same as the skills dialog), `level`, `acc`.
- Mod inventories: `ExtraInventories.Find` reflects over non-game components on the player
  GameObject for `Inventory` fields other than the main one (key `Type.FullName.field`). Mods that
  only enlarge the main inventory are covered by the main grid anyway.
- Items without `m_dropPrefab` are resolved by name token (`ItemBytes.FindPrefab`).
- Storage (agent): `snapshots` rows point at `snapshot_data` by `ContentHash` (SHA-256 of the
  fields a restore can bring back incl. `container`/`cheated`, customData with sorted keys, skill
  levels rounded to 0.01). Unchanged snapshots cost one row. Retention: everything for
  `KeepAllDays`, then the last of each local day for `KeepDailyDays`, never the newest per character.
- Diff (`SnapshotLogic.Diff`): items that do not stack, or carry customData, must match
  `prefab|quality|variant|customData` minus `Snapshots.IgnoreDataKeys` (exact or `prefix*`);
  stackable plain items compare totals per `prefab|quality`. A lost item is flagged `similar` when
  the character still has an unmatched item of the same prefab/quality/variant (enchantment or mod
  data differs) — it stays "missing".
- Restore: the agent builds `{mode, itemVersion, containers, items, skills, skillMode}`. The client
  rebuilds each item from `raw` via `Inventory.Load` into a scratch inventory (`ItemBytes.Load`, the
  path the game uses for saved characters, so mods hooking item loading behave) and falls back to
  `ObjectDB` + `Clone()` + fields for v1 snapshots. A reduced stack overrides `m_stack`. Replace mode
  empties the main inventory and the listed mod inventories, then places items at their slots;
  everything else is added or dropped at the player's feet. Skills: `GetSkillDef(type)` is checked
  first — **`Skills.GetSkill` on an unknown type stores a Skill with null info and breaks the
  character**, so a skill whose mod is missing goes to `failed`.
- Icons: `VA_Icons` asks any online 0.3+ client to render `m_icons[variant]` to 64×64 PNG
  (`Icons.Render`: blit sprite rect to a RenderTexture, ReadPixels, EncodeToPNG). The agent's
  `IconStore` keeps them in the `icons` table and fetches missing ones after each stored snapshot
  and when a snapshot is opened; `/api/icons/{prefab}/{variant}`. The server itself cannot render
  (`-nographics`).
- Only mod-specific code: `EpicLootAdapter` reads Epic Loot's `MagicItemComponent` for rarity
  colours/effects in the panel (`AdminService.Enrich`). Nothing else depends on it.

## Web panel

- `wwwroot/app.js`: tiny `h()` DOM helper, `api()` fetch wrapper (401 → login screen), views
  registered in `views.*` and routed by `location.hash`, live updates over `/ws`.
  Tabs: overview, console, events, players, characters (snapshots), configs, maintenance.
- `wwwroot/i18n.js`: `t(key, ...args)`, en/ru dictionaries; `MiscTests.EveryKeyHasBothLanguages`
  checks the **agent** I18n only — keep the JS dictionaries in sync by hand.
- Auth: single panel password, PBKDF2 (100k, SHA-256). Cookie `va_auth` (HttpOnly, SameSite=Strict,
  30 days sliding), keys in `data/keys` (DPAPI on Windows). `/api/login` and `/api/logout` are
  anonymous; the rest of `/api` and `/ws` use `RequireAuthorization()`. `/ws` also checks Origin.
- `Api.Run` maps exceptions: `BridgeException`→409, `KeyNotFound`/`FileNotFound`→404,
  `UnauthorizedAccess`→403, `Format`/`Argument`→400, `InvalidOperation`→409. Anything else is a 500.
- Every mutating action writes `db.Audit(ip, action, details)`.

## Code rules

Plugin (net472, runs inside the game):
- Game objects are touched **only on the Unity main thread**. Background threads (AgentLink,
  future web server) hand work over with `MainThread.Post`; `BepInExPlugin.Update` pumps it.
- No third-party DLLs; use `Json.cs`, not Newtonsoft/System.Text.Json (other mods ship conflicting versions).
- One DLL for both roles. Role checks: `ServerRole.IsServer` (dedicated server) and
  `ClientRole.IsClient` (`!ZNet.IsServer()`). RPC registration happens in `Game.Start` postfix.
- Client handlers must validate the sender (`FromServer`) and honour `AllowServerCommands` /
  `AllowRestore` config.
- Harmony patches live next to the code that uses them as nested `[HarmonyPatch]` classes;
  `PatchAll` on the assembly picks them up.

Agent (net10):
- Minimal API; business logic in services, routes in `Api.cs`. JSON: System.Text.Json nodes.
- Nullable enabled; file-scoped namespaces; primary constructors are fine.
- User-visible texts: agent-side via `I18n.T`, panel via `i18n.js` (en + ru both).

## Config reference

`agent.json` (next to the exe): see README "agent.json reference". Plugin config
`BepInEx/config/neocor.ValheimAdmin.cfg`: `[Server] AgentPort`, `AgentSecretFile`;
`[Client] AllowServerCommands`, `AllowRestore`; `[General] Debug`.

## Data folder (agent)

`data/valheim-admin.db` (SQLite WAL), `data/logs/server-YYYY-MM-DD.log`, `data/logs/stdout-latest.log`,
`data/config-backups/`, `data/backups/`, `data/bridge.secret`, `data/keys/`.

## Testing on a real server

There is no automated in-game test. After plugin changes, check on a real dedicated server with at
least one client that has the mod: console commands (server and `@Player`), snapshot on `save`,
Compare, restore add/replace, events (join/leave/death/chat). Look at `BepInEx/LogOutput.log` on
both sides; set `[General] Debug = true` for verbose plugin logs.

## Known limitations / traps

- The agent is Windows-only (service, Ctrl+C helper, DPAPI, Tailscale detection).
- Agent and plugin must be updated together; there is no protocol version negotiation yet.
- Items from mods that keep state outside the item itself (neither `m_customData` nor the item bytes) may not restore completely.
- `ZNet.IsDedicated()` gates the server role; a listen-server host gets neither role's server side.
- Snapshots of players without the mod are impossible (data is client-side).
