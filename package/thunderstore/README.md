# Valheim Admin (mod)

The in-game half of **[Valheim Admin](https://github.com/NeocorDK/ValheimAdmin)**, a web panel for dedicated servers. The panel starts and watches the server, keeps logs and events, and runs admin commands from your browser, including over Tailscale or a LAN.

Documentation in Russian: [README.ru.md](https://github.com/NeocorDK/ValheimAdmin/blob/main/README.ru.md).

One DLL covers both sides:

| Installed on | What it does |
|---|---|
| **Dedicated server** (required) | Connects to the Valheim Admin agent. Runs its commands: save, kick, ban, broadcast, global keys, raids, give items. Reports joins, leaves, deaths, chat, boss kills, raids and world saves. |
| **Players** (optional, recommended) | Takes a **character snapshot** on every world save: inventory, equipment and skills. Every item's custom data is included, so **Epic Loot enchantments** and **Adventure Backpacks contents** are kept. The admin can **restore** lost items and skills from the panel. Also runs console commands (`god`, `fly`, `spawn` and others) that the admin sends to this player. |

Players without the mod can still join. They just get no snapshots.

## Player privacy and control

The client side only accepts requests from the server you are connected to. In `BepInEx/config/neocor.ValheimAdmin.cfg`:

- `[Client] AllowServerCommands` turns off remote console commands.
- `[Client] AllowRestore` turns off remote restores and item gifts.

## Installation

- **Server:** put `ValheimAdmin.dll` into `BepInEx/plugins`, then install and run the Valheim Admin agent (see the project page).
- **Players:** install with r2modman / Thunderstore Mod Manager, or drop the DLL into `BepInEx/plugins`.
