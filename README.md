# Valheim Admin

**A web panel for your Valheim dedicated server, with a live world map for your players.**

Out of the box, a Valheim dedicated server is just a console window: no admin commands, no readable event log, and no way to manage it from another computer. Valheim Admin gives you all of that in your browser, and it works with modded servers.

- 🗺️ **A world map anyone can open**, no login: players, bosses, dungeons, traders, portals and pins. You choose what the public sees.
- 🛠️ **An admin panel** behind a password: live console, event log, player management, character backups with restore, config editing.
- 🧩 **One mod** for the server and, optionally, for players. Runs on your own Windows PC or on a rented host (G-Portal, Nitrado, …) and Linux.

---

## Contents

- [What you get](#what-you-get)
- [Which setup do I need?](#which-setup-do-i-need)
- [Quick start: rented host or Linux](#quick-start-rented-host-or-linux)
- [Quick start: your own Windows PC](#quick-start-your-own-windows-pc)
- [For players](#for-players)
- [Using the panel](#using-the-panel): [map](#world-map) · [snapshots](#snapshots-and-restore) · [console](#console)
- [More setup: sharing the map, remote access, Windows service](#more-setup)
- [Troubleshooting](#troubleshooting)
- [Reference](#reference): [mod settings](#mod-settings) · [agent.json](#agentjson) · [data files](#where-data-is-kept) · [security](#security) · [how it works](#how-it-works) · [building](#building-from-source)

---

## What you get

### For everyone: the world map

The panel opens on a map of your world. No account is needed to look at it.

- The real map of **your** world, including worlds changed by generation mods (Expand World, Better Continents, …).
- Players on the map. Only players who turned on "Visible on map" in the game are shown, unless you choose otherwise.
- Optionally: portals, boss altars, dungeons, traders, mob spawners and the pins players shared on cartography tables.
- Optionally, only the **areas players have explored**. Unexplored land is never sent to visitors.
- Works with the `nomap` world setting.

### For the admin: the panel

Sign in with the admin password (**Admin sign-in** at the bottom of the menu) to get:

- **The full map.** Every location of the world (like [valheim.tools](https://www.valheim.tools/), but for your actual world, mods included), mob spawners, tombstones, all players, the pins players made in their own games, and your own pins.
- **Console.** The live server log with search and filters. Any game console command runs on the server, including commands from other mods. You can also run commands **on a player's game**, e.g. `@Ragnar god`.
- **Event log.** Joins and leaves, deaths with the cause and killer, chat, boss kills, raids, world saves, crashes. Stored and searchable.
- **Players.** Who is online, session history, play time, deaths. Kick, ban, whitelist and admin list. Give items.
- **Character snapshots.** Every world save stores each player's inventory, equipment and skills, including everything mods add to items (Epic Loot enchantments, Jewelcrafting gems, backpack contents, …). Lost items after a crash, a bad mod update or a griefing incident? Compare and **restore** them.
- **Configs.** Edit `BepInEx/config` files in the browser. The previous version is kept.
- **Server control** (own Windows PC only): start, stop, restart, automatic restart after crashes and hangs, scheduled daily restarts with an in-game countdown, game updates through SteamCMD with a backup first.
- English and Russian interface.

---

## Which setup do I need?

| | **Rented host or Linux** | **Your own Windows PC** |
|---|---|---|
| Examples | G-Portal, Nitrado, a Docker container | a spare PC, a Windows server |
| What you install | the mod | the mod + the **agent**, a small program that runs next to the server |
| Map, console, event log, players, snapshots, configs | ✅ | ✅ |
| Start / stop / auto-restart / scheduled restarts / updates | ❌ your host's panel does this | ✅ |
| Panel available while the game server is down | ❌ | ✅ |

You don't need to switch anything: the mod notices when the agent is present and uses it; otherwise it serves the panel itself.

---

## Quick start: rented host or Linux

**You need:** a Valheim server with [BepInEx](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/), and a host that lets you open one extra TCP port.

1. **Install the mod.** Install **ValheimAdmin** from Thunderstore through your host's mod manager or r2modman, or upload `ValheimAdmin.dll` to the server's `BepInEx/plugins` folder.
2. **Start the server once.** Look in the BepInEx log for the line **`Web panel admin password: …`**. That is your admin password.
   To choose your own, write it into `[Web] AdminPassword` in `BepInEx/config/neocor.ValheimAdmin.cfg`. It is replaced by a secure hash on the next start.
3. **Open the port.** The panel listens on TCP port **8095**. Many hosts let you add a port in their control panel. If yours gives you a different port, set it as `[Web] Port`.
4. **Open the panel:** `http://<server-address>:8095`

Share that address with your players so they can see the map.

---

## Quick start: your own Windows PC

**You need:** Windows 10/11 or Windows Server, the Valheim dedicated server (Steam app 896660) with [BepInEx](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/). Nothing else: the agent includes everything it needs.

1. **Download** `ValheimAdmin-<version>-win-x64.zip` from the [releases](https://github.com/NeocorDK/Valheim-Admin-Tool/releases) and unpack it, e.g. to `C:\ValheimAdmin`.
2. **Install the mod:** copy `mod\ValheimAdmin.dll` into the server's `BepInEx\plugins` folder (or install it with r2modman).
3. **Stop your server** if you start it with `start_headless_server.bat`. From now on the agent starts it.
4. **Run `agent\ValheimAdmin.Agent.exe` once.** It creates `agent.json` next to itself and prints a **random admin password**. Note it down, then close the window (Ctrl+C).
5. **Edit `agent.json`** in a text editor. In paths, write backslashes twice (`C:\\Games\\Valheim`) or use `/`.
   - `ServerDir`: the folder that contains `valheim_server.exe`.
   - `Server`: server name, world, password, port, the same values as in your `.bat` file.
   - `PanelPassword`: your own admin password, if you want one. It is replaced by a secure hash on the next start.
   - `AutoStart`: set to `true` when the settings are right. A new file has `false`, so the agent never launches a server with placeholder settings.
6. **Start the agent again** and open **http://127.0.0.1:8095** on that PC.

By default the panel can only be opened on the server PC itself (and over Tailscale). To let players see the map, see [Sharing the map with players](#sharing-the-map-with-players). To keep the agent running after you log off, see [Running as a Windows service](#running-as-a-windows-service).

---

## For players

Players **don't need** the mod to join and play. Installing it (same package, from Thunderstore or r2modman) gives them:

- **Character snapshots**, so the admin can bring back lost items and skills. Valheim keeps inventories and skills on the player's own computer, not on the server, so only the player's game can take a snapshot or restore it.
- Their own map pins on the admin's map.
- Real item icons in the panel.

Players stay in control, in `BepInEx\config\neocor.ValheimAdmin.cfg` on their computer:

| Setting | Default | What it does |
|---|---|---|
| `[Client] AllowServerCommands` | on | lets the admin run console commands on your game |
| `[Client] AllowRestore` | on | lets the admin restore items and skills, or give you items |
| `[Client] SharePins` | on | shows your map pins to the admin (never on the public map) |

The mod on a player's game only accepts requests from the server they are connected to. The public map shows a player only while "Visible on map" is on in the game. Cheat commands permanently mark a character as having used cheats (this affects achievements), so the admin has to confirm that in the panel first.

---

## Using the panel

### World map

- **Layers:** the panel on the right turns groups on and off: players, pins, each kind of location (boss altars, dungeons, vegvisirs, traders, camps, resources, runestones, other), mob spawners, portals, tombstones. The ☰ button collapses the panel.
- **Search:** type a name, e.g. `crypt`, `Haldor` or a pin label, and press **Enter** to jump to the nearest match. Press Enter again for the next one.
- **Details:** click a marker to see its name, coordinates and state. A faded location is one nobody has reached yet. A greyed-out boss altar means that boss is beaten.
- **Your pins** (admin): right-click the map to add a pin and choose whether the public sees it. Click a pin to remove it.
- **Players' pins** (admin): pins written to cartography tables, and the personal pins of players with the mod, including where they died. Filter them by player.
- **Explored areas** (admin): shows what the public map reveals.
- **Redraw map** (admin): do this after you add or change a world generation mod. The first drawing takes a minute or two.

What the public sees is set in the [mod settings](#mod-settings) (`[Map] Public…`). By default the public sees only explored areas, visible players and the pins you made public.

### Snapshots and restore

Snapshots are taken on every world save (autosave, the `save` command, shutdown), when you ask for one in the panel, and automatically right before any restore. Identical snapshots take no extra space. By default everything from the last 3 days is kept, then one snapshot per day for 60 days.

To bring back lost items (the player must be online and have the mod):

1. Open **Snapshots**, pick the character and a snapshot from before the loss.
2. Press **Compare with current**. Items the character no longer has are marked. A dashed frame means the character still has a similar item, but with different enchantments or mod data.
3. Press **Select missing** (or click items to pick them yourself).
4. Choose **Add selected items** or **Replace the whole inventory**. Skills can be left alone, raised to the snapshot's levels, or set exactly. Press **Restore**.

The current state is saved as a *pre-restore* snapshot first, so you can go back if something went wrong. Items that don't fit are dropped at the player's feet. Items or skills from mods that are no longer installed are listed instead of restored.

If a mod changes some item data during normal play and Compare keeps reporting such items as lost, add those data keys under **Maintenance → Custom data keys to ignore in Compare** (or `[Web] IgnoreDataKeys` on a rented host).

### Console

Type a command in the **Console** tab. Any game console command runs on the server, and its output is shown. `help` lists everything the server has, including commands from other mods.

| Command | What it does |
|---|---|
| `say <text>` | message in the middle of every player's screen and in chat |
| `save` | save the world (and take snapshots) |
| `players`, `status`, `plugins` | who is online, server stats, loaded mods |
| `kick <player>`, `ban <player or id>`, `unban <id>` | moderation |
| `admins`, `bans`, `permits`, `admin add\|remove <id>`, `permit add\|remove <id>` | admin list, ban list, whitelist |
| `give <player> <item> [count] [quality]` | give items (dropped at their feet if they don't have the mod) |
| `sleep` | skip to morning |
| `events`, `event <name> <player>`, `stopevent` | list, start or stop raids |
| `keys`, `setkey <key>`, `removekey <key>` | global keys (boss progress, world modifiers) |
| `snapshot [player]` | take snapshots now |
| `@Player <command>` | run a command **on that player's game**: `@Ragnar god`, `@Ragnar raiseskill Swords 10`, `@Ragnar spawn Wood 50` |
| `/<command>` | always the game's own command, when the panel has one with the same name (`/kick`, `/event`) |
| anything else | the game's console, e.g. `listkeys`, `skiptime 3600`, `event army_eikthyr` |

Names with spaces go in quotes: `@"Big Olaf" heal`.

---

## More setup

These apply to the Windows PC setup with the agent.

### Sharing the map with players

By default the agent only answers on the PC itself and over Tailscale. To let players open the map:

1. Add the PC's network address to `BindAddresses` in `agent.json` (or `0.0.0.0` for all addresses).
2. Allow TCP port 8095 in the Windows firewall. For access from the internet, also forward the port on your router.
3. Players open `http://<your-address>:8095`.

Only the map is public. Everything else needs the admin password.

> **About passwords over the internet:** over plain `http://` the admin password is sent unencrypted. When you open the panel to the internet, turn on HTTPS (`Https` in `agent.json`, with a PFX certificate), put it behind a reverse proxy with TLS, or sign in over Tailscale.

### Remote access over Tailscale

With [Tailscale](https://tailscale.com/) you can reach the panel from your own devices without opening anything to the internet. The agent listens on the PC's Tailscale address (100.x.y.z) by default. Allow the port for Tailscale addresses only (PowerShell as administrator):

```powershell
New-NetFirewallRule -DisplayName "Valheim Admin panel" -Direction Inbound -Protocol TCP -LocalPort 8095 -RemoteAddress 100.64.0.0/10 -Action Allow
```

Then open `http://<tailscale-ip>:8095` from your devices.

### Running as a Windows service

A service keeps the agent (and, with `AutoStart`, the game server) running without anyone logged in. Run it under **your own Windows account**, the one that owns the Valheim saves, not LocalSystem. In PowerShell as administrator:

```powershell
sc.exe create ValheimAdmin binPath= "C:\ValheimAdmin\agent\ValheimAdmin.Agent.exe" start= delayed-auto obj= ".\YourUser" password= "YourWindowsPassword"
sc.exe start ValheimAdmin
```

To remove it: `sc.exe stop ValheimAdmin`, then `sc.exe delete ValheimAdmin`.

### Updates, restarts and backups

Under **Maintenance** in the panel you can set daily restart times (players get an in-game countdown), the watchdog that restarts a crashed or frozen server, and snapshot retention. To update the game from the panel, set `SteamCmdPath` to your `steamcmd.exe`. The world and configs are backed up before every update.

---

## Troubleshooting

| Problem | What to do |
|---|---|
| **Forgot the admin password** | Own PC: run `ValheimAdmin.Agent.exe --set-password <new>`. Rented host: put a new one in `[Web] AdminPassword` and clear `[Web] AdminPasswordHash`. |
| **Rented host: the panel doesn't open** | Make sure your host accepts connections on the panel's port (`[Web] Port`, 8095 by default). The BepInEx log should say "Web panel (standalone mode) listening". |
| **The panel says "mod not connected"** | Check that `ValheimAdmin.dll` is in the server's `BepInEx\plugins` and that BepInEx loads (`BepInEx\LogOutput.log`). |
| **"Versions differ"** | Update the agent and the mod together. |
| **The agent won't start and mentions JSON** | A path in `agent.json` has single backslashes. Write them twice or use `/`. |
| **The map says the server is offline or preparing** | The map is drawn once the world has loaded. The first time takes a minute or two. |
| **Red cross next to the server in the in-game list, but joining works** | `Server.Public` is off (Valheim doesn't answer Steam status queries then). Turn on **Public** under Maintenance and restart the server. |
| **"A second server copy is running from the same folder"** | Something else (a `.bat`, another tool) started the server too. **Stop** or **Kill process** ends every copy; then press **Start**. |
| **Restored items or snapshots missing for a player** | That player needs the mod and must be online. |

---

## Reference

### Mod settings

File: `BepInEx/config/neocor.ValheimAdmin.cfg` on the server (the `[Client]` section is for players, see [For players](#for-players)).

| Setting | Default | Meaning |
|---|---|---|
| `[Map] Enabled` | `true` | draw the world map |
| `[Map] PublicFog` | `true` | the public map shows only explored areas |
| `[Map] PublicPlayers` | `respect` | `respect` = only players with "Visible on map" on, `all`, `none` |
| `[Map] PublicPortals` | `false` | portals with their names on the public map |
| `[Map] PublicLocations` | `false` | boss altars, dungeons, traders and other locations on the public map |
| `[Map] PublicSpawners` | `false` | mob spawners (nests, bone piles) on the public map |
| `[Map] PublicGamePins` | `false` | pins from cartography tables on the public map |
| `[Map] TextureSize`, `PixelSize` | `2048`, `12` | map resolution; raise `TextureSize` for worlds enlarged by mods |
| `[Map] DrawInBackground` | `true` | turn off only if a world generation mod misbehaves |
| `[Web] Enabled` | `true` | serve the panel from the mod when there is no agent |
| `[Web] Port`, `Bind` | `8095`, `*` | where that panel listens |
| `[Web] AdminPassword` | — | set a new admin password; it is replaced by a hash in `AdminPasswordHash` |
| `[Web] Language` | `en` | event log language (`en`, `ru`) |
| `[Web] SnapshotKeepAllDays`, `SnapshotKeepDailyDays` | `3`, `60` | snapshot retention |
| `[Web] DataDir`, `IgnoreDataKeys` | | data folder; item data keys Compare ignores |
| `[Server] AgentPort`, `AgentSecretFile` | — | only for a server started by hand that should still connect to the agent |

The public map only ever shows public layers in explored areas (when `PublicFog` is on).

### agent.json

Most of these can also be changed in the panel under **Maintenance**.

| Key | Default | Meaning |
|---|---|---|
| `Language` | `en` | `en` or `ru`: event texts and in-game announcements |
| `HttpPort` | `8095` | panel port |
| `BindAddresses` | `["127.0.0.1"]` | addresses to listen on (`0.0.0.0` = all) |
| `ListenTailscale` | `true` | also listen on the Tailscale address |
| `Https` | off | `Port`, `CertificatePath` (PFX), `CertificatePassword` |
| `PanelPassword` | — | set a new admin password; it is replaced by a hash in `PanelPasswordHash` |
| `ServerDir` | — | folder with `valheim_server.exe` |
| `Server.*` | — | `Name`, `Port`, `World`, `Password`, `Public`, `Crossplay`, `SaveDir`, `SaveInterval`, `Backups`, `BackupShort`, `BackupLong`, `ExtraArgs` (e.g. `-modifier raids none`) |
| `AutoStart` | `false` in a new file | start the game server together with the agent |
| `Watchdog` | on, 180 s, 4 per hour | `Enabled`, `HangSeconds`, `MaxRestartsPerHour` |
| `Restarts` | off | `Enabled`, `Times` (`["06:00"]`), `WarnMinutes` (`[15,5,1]`) |
| `Snapshots` | 3 / 60 days | `KeepAllDays`, `KeepDailyDays`, `IgnoreDataKeys` |
| `SteamCmdPath` | empty | path to `steamcmd.exe`, enables game updates |
| `BridgePort` | `27961` | local port between the agent and the mod (127.0.0.1 only) |
| `DataDir` | `data` | database, log archive, backups |

### Where data is kept

**Own PC (agent)**, in the agent's `data` folder:

```
data\valheim-admin.db        events, sessions, snapshots, item icons, audit log (SQLite)
data\logs\server-*.log       daily server log archive
data\config-backups\         previous versions of edited configs
data\backups\                world + config backups taken before updates
data\bridge.secret           shared secret between the agent and the mod
data\keys\                   sign-in cookie keys (protected by Windows)
```

**Rented host**: everything is in `BepInEx/config/ValheimAdmin` (event log, snapshots, icons, backups).

**Both**: the map picture, explored areas and pins are in `BepInEx/config/ValheimAdmin/map/`.

### Security

- One admin password, stored as a PBKDF2 hash. Sessions use HttpOnly, SameSite=Strict cookies. Sign-in is locked for 10 minutes after 5 wrong attempts, and rate-limited.
- The public part is read-only: the map picture (fogged when `PublicFog` is on), visible players and the layers you made public.
- The agent listens only on 127.0.0.1 and the Tailscale address unless you add others. Use HTTPS when you open the panel to the internet.
- Every admin action is written to the audit log with the caller's IP address.
- The agent and the mod talk over 127.0.0.1 only, with a shared secret. On players' computers the mod accepts requests only from the server they are playing on.

### How it works

```
Own Windows PC (agent)                                 Rented host / Linux (standalone)

browser ──HTTP──▶ ValheimAdmin.Agent.exe               browser ──HTTP──▶ valheim_server + ValheimAdmin.dll
                    │ starts and watches the server                       │
                    │ TCP 127.0.0.1 + secret                              │ Valheim network
                    ▼                                                     ▼
               valheim_server + ValheimAdmin.dll                  players' games with the mod (optional)
                    │ Valheim network
                    ▼
               players' games with the mod (optional)
```

- The **agent** (Windows only) serves the panel, starts and watches the server, and stores everything in a SQLite database.
- The **mod on the server** runs the panel's commands inside the game, reports events and draws the map from the game's own world generator (so world generation mods show up as they are). Without the agent, it serves the panel itself.
- The **mod on players' games** takes snapshots, restores items, runs console commands for the admin and renders item icons.

Snapshots store each item's full data, including everything mods attach to it, and restore it the way the game loads a saved character, so they work with mods without special support. Epic Loot additionally gets rarity colours and effects in the panel.

### Building from source

Requires the .NET 10 SDK (or newer) and a Valheim install with BepInEx for the reference assemblies.

```powershell
powershell -ExecutionPolicy Bypass -File package.ps1 -ValheimPath "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
```

This runs the tests, builds the mod, publishes the agent and writes both zips to `dist\`. For development: `dotnet run --project ValheimAdmin.Agent -- --config <path to agent.json>`. Notes for contributors: [CLAUDE.md](CLAUDE.md).

### Limitations

- The agent runs on Windows only. On other systems, use the rented host / Linux setup.
- Snapshots and restores need the mod on the player's computer, and the player must be online for a restore. Item icons appear once a player with the mod has been online.
- Items from mods that keep their state outside the item itself may not come back complete.
- Most mods read their config only at start, so config changes need a server restart.

## License

MIT, see [LICENSE](LICENSE). Third-party notices: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
