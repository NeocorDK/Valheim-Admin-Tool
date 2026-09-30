# Valheim Admin

**Run your Valheim dedicated server from a web browser, and give your players a live world map.**

A plain dedicated server is just a console window. With this mod you get a web page for your server:

- 🗺️ **A world map anyone can open**, no login needed. It shows players, portals, bosses, dungeons, traders and pins. By default only the areas players have explored are visible.
- 🛠️ **An admin panel** behind a password. It has a live console, an event log, player management, character backups with item restore, and config editing.

It works on **rented hosts and Linux** as well as on your own Windows PC, and it works with modded servers.

---

## What you can do

**On the map (for everyone)**
- See where players are. Only players with "Visible on map" turned on are shown.
- See the real map of your world, including worlds changed by generation mods.
- You choose what the public sees: portals, boss altars, dungeons, traders, mob nests, cartography table pins.

**In the admin panel**
- **See the whole world:** every boss altar, dungeon, trader, vegvisir, camp and tar pit. It's like valheim.tools, but for *your* world, with search and filters.
- **See players' own pins**, including where they died.
- **Console:** read the server log live and run any console command on the server, including commands from other mods. Commands like `@Ragnar god` run on a player's game.
- **Event log:** joins, leaves, deaths with the cause, chat, boss kills, raids.
- **Players:** who is online, play time, kick, ban, whitelist, give items.
- **Get lost items back:** each player's inventory and skills are saved on every world save, including enchantments and other mod data (Epic Loot, Jewelcrafting, backpacks, …). Compare with the current character and restore what was lost.
- **Edit mod configs** in the browser.

---

## Install on a rented host or Linux

1. Install this mod on the server.
2. Start the server. In the BepInEx log, find the line **`Web panel admin password: …`**. To set your own password, put it in `[Web] AdminPassword` in `BepInEx/config/neocor.ValheimAdmin.cfg`.
3. Allow incoming connections to TCP port **8095** in your host's panel. If your host gives you a different port, set it as `[Web] Port`.
4. Open `http://<server-address>:8095`. Share this address with your players for the map.

## Install on your own Windows PC

Install this mod on the server, then download the **agent** from [GitHub releases](https://github.com/NeocorDK/Valheim-Admin-Tool/releases). The agent is a small program that runs next to the server and adds the following:
- start, stop and restart the server from the panel;
- automatic restart after a crash or a freeze;
- scheduled daily restarts, with a countdown in the game;
- game updates through SteamCMD, with a backup first.

The setup guide is on [GitHub](https://github.com/NeocorDK/Valheim-Admin-Tool#quick-start-your-own-windows-pc).

---

## For players (optional)

Players can join without the mod. Players who install it get these extras:
- the admin can **restore lost items and skills** for them;
- their pins show up on the admin's map;
- the panel shows real item icons.

Players stay in control, in `BepInEx/config/neocor.ValheimAdmin.cfg`:
- `[Client] AllowServerCommands`: allow or refuse console commands from the admin;
- `[Client] AllowRestore`: allow or refuse restores and item gifts;
- `[Client] SharePins`: show or hide your map pins from the admin. Your pins are never shown on the public map.

The mod only accepts requests from the server you are playing on. Cheat commands permanently mark a character as having used cheats (this affects achievements), so the admin has to confirm them in the panel first.

---

## Main settings

Server settings, in `BepInEx/config/neocor.ValheimAdmin.cfg`:

| Setting | Default | What it does |
|---|---|---|
| `[Map] PublicFog` | on | the public map shows only explored areas |
| `[Map] PublicPlayers` | `respect` | which players the public map shows: `respect` (only those with "Visible on map"), `all`, `none` |
| `[Map] PublicPortals`, `PublicLocations`, `PublicSpawners`, `PublicGamePins` | off | extra layers on the public map |
| `[Web] Port` | `8095` | the panel's port on a rented host |
| `[Web] AdminPassword` | — | set a new admin password |

---

## More information

The full guide, all settings, troubleshooting and the **agent download** are on GitHub:
**https://github.com/NeocorDK/Valheim-Admin-Tool**

Please report bugs and ideas there too.

---

*This mod was made with the help of [Claude Code](https://claude.com/claude-code).*
