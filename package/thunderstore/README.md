# Valheim Admin

A web panel for your Valheim dedicated server, with a **public world map** for your players. One mod for the server and for players.

Full manual: [project page](https://github.com/NeocorDK/Valheim-Admin-Tool).

## What you get

- **World map in the browser, no login needed.** The landing page is a map of your world with players, pins and, if you like, portals and places. It can show only the areas players have explored (the rest never leaves the server). Works with the `nomap` key; the admin always sees the whole map, all players, portals and tombstones.
- **Every location of your world** on the map, like valheim.tools but for your actual (modded) world: boss altars, dungeons, vegvisirs, traders, camps, tar pits, runestones and more, with filters and search. Plus mob spawners (nests, bone piles) and **pins from the game's map**: cartography table pins and, for the admin, players' own pins.
- **Admin panel** behind a password:
  - **Console:** the server log live, and **any console command, vanilla or from other mods, run on the server**; `@Player god` and other commands on a player's game.
  - **Event log:** joins, leaves, deaths with cause, chat, boss kills, raids, saves.
  - **Players:** online list, sessions, play time, kick/ban, admin and whitelist editing, give items.
  - **Character snapshots:** inventory, equipment and skills saved on every world save, **with everything mods keep on items** (Epic Loot, Jewelcrafting, Adventure Backpacks, Therzie's mods, …) and skills from mods. Compare with the live character and **restore** lost items and skills. Real item icons and tooltips.
  - **Configs:** edit `BepInEx/config` files in the browser.

## Install on the server

The mod works in two ways and picks one by itself:

| | Rented host / Linux (**standalone**) | Your own Windows PC (**with the agent**) |
|---|---|---|
| Install | this mod | this mod + the agent from the [releases](https://github.com/NeocorDK/Valheim-Admin-Tool/releases) |
| Panel | served by the mod on TCP port 8095 | served by the agent |
| Start/stop, watchdog, scheduled restarts, SteamCMD updates | your host's panel | yes |

**Standalone:** install the mod, start the server, and find **"Web panel admin password: …"** in the BepInEx log (or set your own in `[Web] AdminPassword`). Make sure your host lets players reach port 8095 (`[Web] Port`), then open `http://<server-address>:8095`.

**With the agent:** see the [installation guide](https://github.com/NeocorDK/Valheim-Admin-Tool#quick-start-your-own-windows-pc).

## Players (optional, recommended)

Players who install the same mod get **character snapshots** and can have lost items restored; the admin can run console commands on their game. The panel's item icons are drawn by a player's game. Players without the mod can join and play normally.

Privacy and control, in `BepInEx/config/neocor.ValheimAdmin.cfg` on the player's side:

- the mod only accepts requests from the server you are connected to;
- `[Client] AllowServerCommands` turns off remote console commands, `[Client] AllowRestore` turns off restores and item gifts;
- `[Client] SharePins` (on) lets the server admin see the pins of your map on the admin panel's map (never on the public one);
- the public map shows you only while "Visible on map" is on in the game.

Cheat commands mark a character as having used cheats (Valheim achievements); the admin has to confirm that in the panel before it happens.

## Server settings (`[Map]`, `[Web]`)

- `[Map] PublicFog` (on): the public map shows explored areas only. `[Map] PublicPlayers`: `respect` / `all` / `none`. `[Map] PublicPortals`, `PublicLocations`, `PublicSpawners`, `PublicGamePins`: off by default.
- `[Map] TextureSize` / `PixelSize`: map resolution; raise the size for worlds enlarged by mods.
- `[Web] Port`, `Bind`, `AdminPassword`, `Language`, `DataDir`: the standalone panel.

Data of the standalone panel (event log, snapshots, icons, map) is in `BepInEx/config/ValheimAdmin`.

Security: one admin password (PBKDF2), HttpOnly cookie sessions, lockout after 5 wrong attempts. Plain HTTP sends the password unencrypted; for access over the internet prefer HTTPS (agent) or a VPN such as Tailscale.
