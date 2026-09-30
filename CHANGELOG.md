# Changelog

## 0.4.0

- **Every location of the world on the map**, like valheim.tools but for your actual world, modded locations included: boss altars (beaten bosses greyed out), dungeons, vegvisirs, traders, camps and nests, resources, runestones and the rest; faded until someone reaches them. Layers per category, search with jump to the nearest match, details on click.
- **Mob spawners** (greydwarf nests, body and bone piles, modded ones) on the map.
- **Pins from the game's map:** cartography table pins (their explored area is added to the public fog too) and, for the admin, personal pins of players with the mod, filterable by player. Players can opt out with `[Client] SharePins`.
- New public map options `[Map] PublicSpawners` and `PublicGamePins` (off); `PublicLocations` now covers all locations.
- The map's layer panel can be collapsed.

## 0.3.0

- **Public world map** as the landing page, no login needed: drawn on the server from the world generator (world generation mods included), explored areas tracked on the server, players (respecting "Visible on map"), admin pins; portals and places optional. Unexplored areas never leave the server. Works with the `nomap` key; the admin sees everything, including tombstones.
- **Standalone mode:** on hosts where the agent cannot run (rented servers, Linux), the mod serves the panel itself: map, console, event log, players, snapshots, configs.
- **Console:** any game console command, vanilla or from other mods, runs on the server with its output shown; `/command` forces the game's command; `help` and autocomplete list the server's commands. Fixed `@Player` cheat commands, which the game refused on clients of a dedicated server. The panel asks before a cheat marks a character as cheated; server-only commands sent to a player run on the server.
- **Snapshots work with any mod:** items are rebuilt through the game's own item loading; inventories that mods add to the player are included; mod skills are named properly; restoring a skill whose mod is missing no longer corrupts the character; real item icons; Compare can ignore volatile mod data and marks look-alike items. Epic Loot support is now an optional extra.
- Admin sign-in from the menu, rate limits, optional HTTPS for the agent, warning when agent and mod versions differ.

## 0.2.0

- Character snapshots and restores, event log, players, configs, scheduled restarts, watchdog, SteamCMD updates.
