# Valheim Admin

*Russian version: [README.ru.md](README.ru.md)*

A web panel for running a **Valheim dedicated server** on Windows. The vanilla server is a console window with no admin commands, no usable event log and no way to manage it from another machine. Valheim Admin adds all of that. You can reach it from any browser on your network or over **Tailscale**.

- **Server control:** start, graceful stop, restart, save and kill. A server that is already running is picked up when the agent starts.
- **Watchdog:** restarts the server after a crash or a hang, with back-off and a limit on restarts per hour.
- **Scheduled restarts** with an in-game countdown, plus one-off "restart in N minutes".
- **Live console:** the server log with filters and search, and a command line.
  - Server commands: kick, ban, broadcast, give items, global keys, raids, skip the night, and more.
  - **Cheat and console commands on a player's machine:** `@Player god`, `@Player spawn Wood 50` and any other game console command.
- **Event log:** joins and leaves, deaths with cause and killer, chat, boss kills, raids, world saves, crashes, restores and config changes. Everything is stored and searchable.
- **Players:** who is online (ping, position, mod version), session history, play time, deaths. Admin, ban and whitelist editing.
- **Character snapshots:** on every world save, each online player's inventory, equipment and skills are stored, **including Epic Loot enchantments and Adventure Backpacks contents**. You can browse old snapshots, compare one with the character as it is now, and **restore** lost items and skills.
- **Configs and plugins:** edit `BepInEx/config` files in the browser (the previous version is kept) and see the plugins the server loaded.
- **Updates:** updates the server through SteamCMD, with a backup of the world and configs first.
- **English and Russian** interface.

## How it works

```
Your PC: browser
    |
    |  HTTP + WebSocket, over Tailscale or LAN
    v
Agent: ValheimAdmin.Agent.exe, a Windows service on the server machine
    |
    |  starts and watches the process; talks to the mod over TCP 127.0.0.1 with a secret
    v
valheim_server.exe with ValheimAdmin.dll loaded by BepInEx
    |
    |  Valheim network RPC
    v
Players' games with ValheimAdmin.dll (optional)
```

- **The agent** (`ValheimAdmin.Agent.exe`) is a standalone program that runs next to the server. It keeps working when the server is down or crashes, so it can start it again. It serves the panel, stores logs, events and snapshots in SQLite, and owns the server process.
- **The mod** (`ValheimAdmin.dll`, BepInEx) runs inside the server and does what the agent asks.
- **Players can have the same mod too.** Valheim keeps inventories and skills on the player's computer, not on the server, so only the player's game can take a snapshot or restore items. Players without the mod can play normally; they just have no snapshots.

## Requirements

- Windows 10/11 or Windows Server with the Valheim dedicated server (Steam app 896660).
- [BepInEx for Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) installed into the server.
- Nothing else: the agent is self-contained (no .NET install needed).
- Optional: Tailscale for remote access; SteamCMD for updates.

## Installation

1. Download `ValheimAdmin-<version>-win-x64.zip` from the releases and unpack it, e.g. to `C:\ValheimAdmin`.
2. Copy `mod\ValheimAdmin.dll` into the server's `BepInEx\plugins` folder.
3. Stop the server if you run it with `start_headless_server.bat`: from now on the agent starts it.
4. Run `agent\ValheimAdmin.Agent.exe` once. It creates `agent\agent.json` and prints a **random panel password**. Close it again with Ctrl+C.
5. Edit `agent.json`. Backslashes in paths must be doubled (`C:\\Games\\Valheim`) or written as `/`.
   - `ServerDir`: the folder with `valheim_server.exe`.
   - `Server`: name, world, password, port and so on, the same values you had in the .bat file.
   - `Server.SaveDir`: leave empty for the default `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim`, or point it at your saves.
   - `PanelPassword`: set your own password here if you like. The agent replaces it with a hash on the next start.
   - `AutoStart`: set to `true` once the settings are right. A new `agent.json` starts with `false`, so the placeholder settings never launch a server.
6. Start the agent again and open `http://127.0.0.1:8095` on the server, or `http://<tailscale-ip>:8095` from your PC.

### Remote access over Tailscale

With `"ListenTailscale": true` (the default) the agent also listens on the machine's Tailscale address (100.x.y.z). Only the Tailscale address and `127.0.0.1` are used, never your public IP. Allow the port in the Windows firewall for Tailscale addresses only:

```powershell
New-NetFirewallRule -DisplayName "Valheim Admin panel" -Direction Inbound -Protocol TCP -LocalPort 8095 -RemoteAddress 100.64.0.0/10 -Action Allow
```

To use a LAN address instead, add it to `BindAddresses`. `0.0.0.0` means every interface; open it only on a trusted network.

### Running as a Windows service

Run the service under **your own Windows account**, the one that owns the Valheim saves, not LocalSystem. From an elevated PowerShell:

```powershell
sc.exe create ValheimAdmin binPath= "C:\ValheimAdmin\agent\ValheimAdmin.Agent.exe" start= delayed-auto obj= ".\YourUser" password= "YourWindowsPassword"
sc.exe start ValheimAdmin
```

With `"AutoStart": true` the agent starts the game server together with the service. To remove the service later: `sc.exe stop ValheimAdmin` and then `sc.exe delete ValheimAdmin`.

### Players

Players install `ValheimAdmin` from Thunderstore / r2modman, or copy `ValheimAdmin.dll` into their `BepInEx\plugins`. It is optional; without it a player has no snapshots, and console commands can't be run on their game. Players can turn off remote commands or restores in `BepInEx\config\neocor.ValheimAdmin.cfg`.

## Character snapshots

- **When:**
  - on every world save (autosave, the `save` command, shutdown);
  - on demand from the panel;
  - automatically right before any restore.
- **What:** every inventory item with prefab, stack, quality, durability, variant, crafter, slot and equipped state, and **all of the item's custom data**. Custom data is where Epic Loot keeps enchantments and Adventure Backpacks keeps backpack contents, so restores work without either mod's API. Skill levels are stored too. The panel shows each item's in-game tooltip, including the Epic Loot lines.
- **Storage:** identical snapshots are stored once.
  - Retention by default: everything from the last 3 days, then one snapshot per day for 60 days. The newest snapshot of a character is never removed.
- **Restore** (the player must be online with the mod):
  1. Select a snapshot. **Compare with current** marks what the character is missing now: an enchanted sword is told apart from a plain one of the same type, and stacks are compared by total count.
  2. Choose **Add selected items** or **Replace the whole inventory**. Skills can be left alone, raised to the snapshot levels, or set exactly.
  3. Before anything changes, the current state is saved as a *before restore* snapshot. Items that don't fit are dropped at the player's feet.

## Console commands

| Command | Action |
|---|---|
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
| `@Player <console command>` | run any game console command on that player's machine with cheats enabled, e.g. `@Ragnar god`, `@Ragnar raiseskill Swords 10`, `@Ragnar spawn Wood 50` |

Player names with spaces go in quotes: `@"Big Olaf" heal`.

## `agent.json` reference

| Key | Default | Meaning |
|---|---|---|
| `Language` | `en` | `en` or `ru`: event texts, agent log lines, in-game announcements |
| `HttpPort` | `8095` | panel port |
| `BindAddresses` | `["127.0.0.1"]` | extra addresses to listen on |
| `ListenTailscale` | `true` | also listen on the Tailscale address |
| `PanelPassword` / `PanelPasswordHash` | - | put a new password in `PanelPassword`; it is hashed on start |
| `BridgePort` | `27961` | local port between agent and mod (127.0.0.1 only) |
| `ServerDir` | - | folder with `valheim_server.exe` |
| `Server.*` | - | `Name`, `Port`, `World`, `Password`, `Public`, `Crossplay`, `SaveDir`, `SaveInterval`, `Backups`, `BackupShort`, `BackupLong`, `ExtraArgs` (e.g. `-modifier raids none`) |
| `AutoStart` | `false` in a new file | start the server with the agent |
| `Watchdog` | on, 180 s, 4/h | `Enabled`, `HangSeconds`, `MaxRestartsPerHour` |
| `Restarts` | off | `Enabled`, `Times` (`["06:00"]`), `WarnMinutes` (`[15,5,1]`) |
| `Snapshots` | 3 / 60 | `KeepAllDays`, `KeepDailyDays` |
| `SteamCmdPath` | empty | path to `steamcmd.exe` to enable updates |
| `DataDir` | `data` | database, log archive, backups |

Most of these can also be changed in the panel under **Maintenance**. The password can be reset from the command line: `ValheimAdmin.Agent.exe --set-password <new>`.

## Data folder

```
data\valheim-admin.db      events, sessions, snapshots, audit log (SQLite)
data\logs\server-*.log     daily server log archive
data\logs\stdout-latest.log raw output of the current server process
data\config-backups\       previous versions of edited configs
data\backups\              world + config zips taken before updates
data\bridge.secret         shared secret between agent and mod
data\keys\                 login cookie keys (DPAPI-protected)
```

## Troubleshooting

- **The server shows a red cross in the in-game Favorites / Recent list, but joining works.** `Server.Public` is off. With `-public 0` Valheim does not answer Steam status queries. Turn on **Public** under Maintenance (or set `"Public": true`) and restart the server. This is also Valheim's default when `-public` is not given.
- **The panel says "mod not connected".** Check that `ValheimAdmin.dll` is in the server's `BepInEx\plugins` and that BepInEx itself loads (`BepInEx\LogOutput.log`).
- **Update the agent and the mod together.** They speak a small protocol to each other; a new mod does not link up with an old agent.
- **"A second server copy is running from the same folder".** Something (a `.bat`, another tool, an older agent) started `valheim_server.exe` next to the managed one. The agent never launches a second copy itself and does not let the stray one take the panel link. **Stop** or **Kill process** ends every copy from the server folder; then press **Start**.
- **The agent does not start and mentions JSON.** A path in `agent.json` has single backslashes; double them or use `/`.

## Security

- The panel is protected by one password: PBKDF2 hash, cookie sessions, and a lockout after 5 wrong attempts.
- It listens only on 127.0.0.1 and the Tailscale address unless you add others.
- Every action from the panel is written to the audit log with the caller's IP.
- The mod accepts agent connections only from 127.0.0.1 with the secret. On players' machines it accepts requests only from the server they are connected to.

## Building from source

Requires the .NET 10 SDK (or newer) and a Valheim install with BepInEx for the reference assemblies.

```powershell
powershell -ExecutionPolicy Bypass -File package.ps1 -ValheimPath "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
```

This runs the tests, builds the mod, publishes the self-contained agent and writes both zips to `dist\`. For development, run `dotnet run --project ValheimAdmin.Agent -- --config <path to agent.json>`.

## Limitations

- The agent is Windows-only.
- Snapshots and restores need the mod on the player's machine, and the player must be online to restore.
- Restores go through Valheim's own inventory code. Items from mods that keep data outside the item's custom data may not come back complete.
- Most mods read their config only at start, so config edits need a server restart.

## License

MIT, see [LICENSE](LICENSE).
