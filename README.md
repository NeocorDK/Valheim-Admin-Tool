# Valheim Admin

*Russian version: [README.ru.md](README.ru.md)*

A web panel for **Valheim dedicated servers**, with a **public world map** for your players. The vanilla server is a console window with no admin commands, no usable event log and no way to manage it from another machine. Valheim Admin adds all of that, and works with modded servers.

- **World map for everyone, no login:** the map of your world as the landing page, with players, pins and optional portals and places. Explored areas only, if you like. Works with the `nomap` world key; the admin always sees the whole map.
- **Live console:** the server log with filters and search, and a command line.
  - **Any game console command** runs on the server: vanilla (`listkeys`, `skiptime`, `event`, …) and commands added by other mods.
  - Panel commands: kick, ban, broadcast, give items, global keys, raids, skip the night, and more.
  - **Commands on a player's machine:** `@Player god`, `@Player spawn Wood 50`, any other console command. The panel asks before a cheat marks a character as cheated.
- **Event log:** joins and leaves, deaths with cause and killer, chat, boss kills, raids, world saves, crashes, restores and config changes. Stored and searchable.
- **Players:** who is online (ping, position, mod version), session history, play time, deaths. Admin, ban and whitelist editing.
- **Character snapshots that work with any mod:** on every world save, each online player's inventory, equipment and skills are stored, including everything mods keep on items (Epic Loot enchantments, Jewelcrafting gems, Adventure Backpacks contents, items of Therzie's mods, …) and skills added by mods. Browse, compare with the character as it is now, and **restore** lost items and skills. Items show their real in-game icons and tooltips.
- **Configs and plugins:** edit `BepInEx/config` files in the browser (the previous version is kept) and see the plugins the server loaded.
- **Server control** (with the agent): start, graceful stop, restart, watchdog after crashes and hangs, scheduled restarts with an in-game countdown, updates through SteamCMD with a backup first.
- **English and Russian** interface.

## Two ways to run it

| | **Agent mode** — your own Windows machine | **Standalone mode** — rented host (G-Portal, Nitrado, …) or Linux |
|---|---|---|
| What you install | the mod + the agent (`ValheimAdmin.Agent.exe`) | only the mod |
| Who serves the panel | the agent, also while the server is down | the mod, from inside the game |
| Start / stop / watchdog / scheduled restarts / SteamCMD updates | yes | no — use your host's panel |
| Map, console, events, players, snapshots, configs | yes | yes |
| Storage | SQLite in the agent's `data` folder | files in `BepInEx/config/ValheimAdmin` |

The mod picks the mode by itself: when the agent starts the server, the agent serves the panel; otherwise the mod does.

```
Agent mode                                            Standalone mode

browser ──HTTP──▶ ValheimAdmin.Agent.exe              browser ──HTTP──▶ valheim_server + ValheimAdmin.dll
                    │ starts/watches the server                              │
                    │ TCP 127.0.0.1 + secret                                 │ Valheim RPC
                    ▼                                                        ▼
               valheim_server + ValheimAdmin.dll                     players' games with the mod (optional)
                    │ Valheim RPC
                    ▼
               players' games with the mod (optional)
```

**Players can have the same mod too** (one Thunderstore package for everybody). Valheim keeps inventories and skills on the player's computer, not on the server, so only the player's game can take a snapshot or restore items, and the icons in the panel are rendered by a player's game. Players without the mod play normally; they just have no snapshots.

## Installation — agent mode (your own Windows server)

Requirements: Windows 10/11 or Windows Server with the Valheim dedicated server (Steam app 896660) and [BepInEx for Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/). The agent is self-contained (no .NET install needed). Optional: Tailscale for remote access, SteamCMD for updates.

1. Download `ValheimAdmin-<version>-win-x64.zip` from the releases and unpack it, e.g. to `C:\ValheimAdmin`.
2. Copy `mod\ValheimAdmin.dll` into the server's `BepInEx\plugins` folder (or install the mod with r2modman).
3. Stop the server if you run it with `start_headless_server.bat`: from now on the agent starts it.
4. Run `agent\ValheimAdmin.Agent.exe` once. It creates `agent\agent.json` and prints a **random panel password**. Close it again with Ctrl+C.
5. Edit `agent.json`. Backslashes in paths must be doubled (`C:\\Games\\Valheim`) or written as `/`.
   - `ServerDir`: the folder with `valheim_server.exe`.
   - `Server`: name, world, password, port and so on, the same values you had in the .bat file.
   - `Server.SaveDir`: leave empty for the default `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim`, or point it at your saves.
   - `PanelPassword`: set your own password here if you like. The agent replaces it with a hash on the next start.
   - `AutoStart`: set to `true` once the settings are right. A new `agent.json` starts with `false`, so the placeholder settings never launch a server.
6. Start the agent again and open `http://127.0.0.1:8095` on the server, or `http://<tailscale-ip>:8095` from your PC. The map opens; **Admin sign-in** is at the bottom of the menu.

### Letting players open the map

By default the agent listens only on `127.0.0.1` and the Tailscale address. To give players the map:

- add the machine's LAN or public address (or `0.0.0.0` for every interface) to `BindAddresses`, open TCP 8095 in the Windows firewall and, for internet access, forward the port on your router;
- players then open `http://<your-address>:8095`. Only the map is public: everything else needs the admin password, and unexplored areas never leave the server when `[Map] PublicFog` is on.

The admin sign-in works from anywhere. Over plain HTTP the password travels unencrypted, so for access over the internet turn on HTTPS (`Https` in `agent.json`, with a PFX certificate) or put the panel behind a reverse proxy with TLS. Over Tailscale the traffic is already encrypted.

### Remote access over Tailscale

With `"ListenTailscale": true` (the default) the agent also listens on the machine's Tailscale address (100.x.y.z). Allow the port in the Windows firewall for Tailscale addresses only:

```powershell
New-NetFirewallRule -DisplayName "Valheim Admin panel" -Direction Inbound -Protocol TCP -LocalPort 8095 -RemoteAddress 100.64.0.0/10 -Action Allow
```

### Running as a Windows service

Run the service under **your own Windows account**, the one that owns the Valheim saves, not LocalSystem. From an elevated PowerShell:

```powershell
sc.exe create ValheimAdmin binPath= "C:\ValheimAdmin\agent\ValheimAdmin.Agent.exe" start= delayed-auto obj= ".\YourUser" password= "YourWindowsPassword"
sc.exe start ValheimAdmin
```

With `"AutoStart": true` the agent starts the game server together with the service. To remove the service later: `sc.exe stop ValheimAdmin` and then `sc.exe delete ValheimAdmin`.

## Installation — standalone mode (rented host, Linux)

1. Install **Valheim Admin** from Thunderstore on the server (through your host's mod manager or r2modman), or upload `ValheimAdmin.dll` to `BepInEx/plugins`.
2. Start the server once. The mod writes `BepInEx/config/neocor.ValheimAdmin.cfg` and, in the BepInEx log, a line **"Web panel admin password: …"**. To choose your own password, put it in `[Web] AdminPassword`; it is replaced by a hash on the next start or sign-in.
3. The panel listens on TCP port **8095** (`[Web] Port`). Your host must allow incoming connections to that port; many hosts let you add a port in their control panel. Open `http://<server-address>:8095`.

Data (event log, snapshots, icons) is kept in `BepInEx/config/ValheimAdmin`. The server's own start, stop and restarts stay in your host's panel.

## Players

Players install `ValheimAdmin` from Thunderstore / r2modman, or copy `ValheimAdmin.dll` into their `BepInEx\plugins`. It is optional; without it a player has no snapshots, and console commands can't be run on their game. Players can turn off remote commands or restores in `BepInEx\config\neocor.ValheimAdmin.cfg` (`[Client] AllowServerCommands`, `AllowRestore`). The public map shows a player only while their in-game "Visible on map" setting is on (`[Map] PublicPlayers`).

## World map

- Drawn on the server from the world generator, the same way the game draws its minimap, so world generation mods (Expand World, Better Continents, …) show up as they are. The first drawing takes a minute or two and is cached; **Redraw map** in the admin view draws it again after you change such mods.
- Explored areas are tracked on the server from where players have been. With `[Map] PublicFog` (on by default) the public map shows only those areas; the full map never leaves the server.
- The admin sees everything: all players (also hidden ones), portals with their tags, location icons (bosses, traders, start), tombstones, and can add pins (right-click); pins can be public.

## Character snapshots

- **When:** on every world save (autosave, the `save` command, shutdown); on demand from the panel; automatically right before any restore.
- **What:** every inventory item with prefab, stack, quality, durability, variant, crafter, slot, equipped state, and **all of the item's custom data**, plus the game's own saved form of the item; inventories that mods attach to the player (extra equipment or quick slots); skill levels, also of skills added by mods; the player's custom data (for reference). The panel shows each item's in-game icon and tooltip, including lines added by mods.
- **Storage:** identical snapshots are stored once. Retention by default: everything from the last 3 days, then one snapshot per day for 60 days. The newest snapshot of a character is never removed.
- **Restore** (the player must be online with the mod):
  1. Select a snapshot. **Compare with current** marks what the character is missing now: an enchanted sword is told apart from a plain one of the same type (a dashed frame means the character has a similar item with different data), and stacks are compared by total count.
  2. Choose **Add selected items** or **Replace the whole inventory**. Skills can be left alone, raised to the snapshot levels, or set exactly.
  3. Before anything changes, the current state is saved as a *before restore* snapshot. Items are rebuilt the way the game loads a saved character, so mods that hook item loading see a normal load. Items that don't fit are dropped at the player's feet. Items or skills of mods that are no longer installed are reported instead of restored.
- If a mod changes some item data during normal play and Compare keeps reporting such items as lost, list those custom data keys under **Maintenance → Custom data keys to ignore** (agent) or `[Web] IgnoreDataKeys` (standalone).

## Console commands

| Command | Action |
|---|---|
| any game command, e.g. `listkeys`, `skiptime 3600`, `event army_eikthyr`, commands of other mods | runs in the game console **on the server**; its output is shown. `help` lists what the server has |
| `/<command>` | always the game's command, even when the panel has one with the same name (`/kick`, `/event`) |
| `players`, `status`, `plugins` | online players, server stats, loaded plugins |
| `save` | save the world (and take snapshots) |
| `say <text>` | message in the centre of the screen and in chat |
| `kick <player>`, `ban <player\|id>`, `unban <id>` | moderation |
| `admins`, `bans`, `permits`, `admin add\|remove <id>`, `permit add\|remove <id>` | admin list, ban list, whitelist |
| `give <player> <prefab> [count] [quality]` | give items (dropped at the feet if the player has no mod) |
| `keys`, `setkey <key>`, `removekey <key>` | global keys (boss progress, world modifiers) |
| `sleep` | skip to morning |
| `events`, `event <name> <player>`, `stopevent` | raids |
| `snapshot [player]` | take snapshots now |
| `@Player <console command>` | runs on that player's machine, e.g. `@Ragnar god`, `@Ragnar raiseskill Swords 10`, `@Ragnar spawn Wood 50`. Commands only the server can run are run on the server |

Player names with spaces go in quotes: `@"Big Olaf" heal`. Valheim runs cheat commands only on characters marked as having used cheats; the mark is permanent (achievements), so the panel asks before it sets it.

## Mod configuration (`BepInEx/config/neocor.ValheimAdmin.cfg`)

| Section / key | Default | Meaning |
|---|---|---|
| `[Map] Enabled` | `true` | draw the world map on the server |
| `[Map] TextureSize`, `PixelSize` | `2048`, `12` | map resolution; raise `TextureSize` for worlds enlarged by mods |
| `[Map] DrawInBackground` | `true` | draw on a background thread (turn off if a world generation mod misbehaves) |
| `[Map] PublicFog` | `true` | the public map shows only explored areas |
| `[Map] PublicPlayers` | `respect` | `respect` (only players with "Visible on map"), `all`, `none` |
| `[Map] PublicPortals`, `PublicLocations` | `false` | show portals / location icons on the public map (explored areas only) |
| `[Web] Enabled` | `true` | standalone mode when there is no agent |
| `[Web] Port`, `Bind` | `8095`, `*` | where the standalone panel listens |
| `[Web] AdminPassword` / `AdminPasswordHash` | — | put a new password in `AdminPassword`; it is hashed |
| `[Web] Language` | `en` | event log language in standalone mode |
| `[Web] DataDir`, `SnapshotKeepAllDays`, `SnapshotKeepDailyDays`, `IgnoreDataKeys` | | standalone storage and snapshot settings |
| `[Client] AllowServerCommands`, `AllowRestore` | `true` | a player can refuse remote commands or restores |
| `[Server] AgentPort`, `AgentSecretFile` | — | only for a server started without the agent that should still link to it |

## `agent.json` reference

| Key | Default | Meaning |
|---|---|---|
| `Language` | `en` | `en` or `ru`: event texts, agent log lines, in-game announcements |
| `HttpPort` | `8095` | panel port |
| `BindAddresses` | `["127.0.0.1"]` | extra addresses to listen on (`0.0.0.0` = all) |
| `ListenTailscale` | `true` | also listen on the Tailscale address |
| `Https` | off | `Port`, `CertificatePath` (PFX), `CertificatePassword` |
| `PanelPassword` / `PanelPasswordHash` | - | put a new password in `PanelPassword`; it is hashed on start |
| `BridgePort` | `27961` | local port between agent and mod (127.0.0.1 only) |
| `ServerDir` | - | folder with `valheim_server.exe` |
| `Server.*` | - | `Name`, `Port`, `World`, `Password`, `Public`, `Crossplay`, `SaveDir`, `SaveInterval`, `Backups`, `BackupShort`, `BackupLong`, `ExtraArgs` (e.g. `-modifier raids none`) |
| `AutoStart` | `false` in a new file | start the server with the agent |
| `Watchdog` | on, 180 s, 4/h | `Enabled`, `HangSeconds`, `MaxRestartsPerHour` |
| `Restarts` | off | `Enabled`, `Times` (`["06:00"]`), `WarnMinutes` (`[15,5,1]`) |
| `Snapshots` | 3 / 60 | `KeepAllDays`, `KeepDailyDays`, `IgnoreDataKeys` |
| `SteamCmdPath` | empty | path to `steamcmd.exe` to enable updates |
| `DataDir` | `data` | database, log archive, backups |

Most of these can also be changed in the panel under **Maintenance**. The password can be reset from the command line: `ValheimAdmin.Agent.exe --set-password <new>`.

## Data folder (agent)

```
data\valheim-admin.db      events, sessions, snapshots, item icons, audit log (SQLite)
data\logs\server-*.log     daily server log archive
data\logs\stdout-latest.log raw output of the current server process
data\config-backups\       previous versions of edited configs
data\backups\              world + config zips taken before updates
data\bridge.secret         shared secret between agent and mod
data\keys\                 login cookie keys (DPAPI-protected)
```

The map picture and explored areas are kept by the mod in `BepInEx\config\ValheimAdmin\map\`.

## Troubleshooting

- **The server shows a red cross in the in-game Favorites / Recent list, but joining works.** `Server.Public` is off. With `-public 0` Valheim does not answer Steam status queries. Turn on **Public** under Maintenance (or set `"Public": true`) and restart the server.
- **The panel says "mod not connected".** Check that `ValheimAdmin.dll` is in the server's `BepInEx\plugins` and that BepInEx itself loads (`BepInEx\LogOutput.log`).
- **"versions differ".** The agent and the mod speak a small protocol to each other; update them together.
- **"A second server copy is running from the same folder".** Something (a `.bat`, another tool, an older agent) started `valheim_server.exe` next to the managed one. **Stop** or **Kill process** ends every copy from the server folder; then press **Start**.
- **The map says the server is offline / preparing.** The map is drawn once the world is loaded; the first time takes a minute or two.
- **Standalone mode: the panel does not open.** Check the port with your host (it must accept TCP on `[Web] Port`), and look for "Web panel (standalone mode) listening" in the BepInEx log.
- **The agent does not start and mentions JSON.** A path in `agent.json` has single backslashes; double them or use `/`.

## Security

- One admin password: PBKDF2 hash, cookie sessions (HttpOnly, SameSite=Strict), a lockout after 5 wrong attempts and a rate limit on sign-in.
- The public part is read-only: the map picture (fogged when `PublicFog` is on), visible players and public pins. Everything else needs the password.
- The agent listens only on 127.0.0.1 and the Tailscale address unless you add others. Use HTTPS when you open the panel to the internet.
- Every action from the panel is written to the audit log with the caller's IP.
- The mod accepts agent connections only from 127.0.0.1 with the secret. On players' machines it accepts requests only from the server they are connected to.

## Building from source

Requires the .NET 10 SDK (or newer) and a Valheim install with BepInEx for the reference assemblies.

```powershell
powershell -ExecutionPolicy Bypass -File package.ps1 -ValheimPath "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
```

This runs the tests, builds the mod, publishes the self-contained agent and writes both zips to `dist\`. For development, run `dotnet run --project ValheimAdmin.Agent -- --config <path to agent.json>`. Notes for contributors and AI agents: [CLAUDE.md](CLAUDE.md).

## Limitations

- The agent is Windows-only; on other systems use standalone mode.
- Snapshots and restores need the mod on the player's machine, and the player must be online to restore. Item icons appear once a player with the mod has been online.
- Items from mods that keep state outside the item itself (neither in its custom data nor in its saved form) may not come back complete.
- Most mods read their config only at start, so config edits need a server restart.

## License

MIT, see [LICENSE](LICENSE). Third-party notices: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
