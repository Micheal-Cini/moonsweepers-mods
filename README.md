# Moon Sweepers Mods

Quality-of-life mods for [Moon Sweepers](https://store.steampowered.com/app/5335510/Moon_Sweepers/) co-op, with a one-click installer and automatic updates.

Built and maintained by **[VettaTech](https://vettatech.ca)**: custom websites and apps.

> Unofficial fan project. Not affiliated with or endorsed by DragDrop Games.

## Mods

| Mod | What it does |
| --- | --- |
| **Team Upgrades** | Money is shared in Moon Sweepers, but tools and upgrades are per player. With this mod, when anyone buys a tool, container upgrade, upgrade-tree unlock or inventory tier, the **whole crew gets it**. You pay once. |
| **Team Vehicles** | Buy a vehicle and **every crewmate gets their own**; upgrade one and **every vehicle of that model** gets it. **Linked garages:** every garage holds your whole fleet, so call any vehicle out of any garage. Demolishing a garage never loses vehicles (they wait in your other garages, or the next one you build); its refund covers just the building. (You can already drive each other's vehicles: walk up to a parked one.) |
| **Sleep** | At night, lie down in a bunk. When the whole crew is in a bunk, the night is skipped to morning. |
| **Logistics Fix** | Fixes a game bug: splitters and mergers built by a crewmate who joined your world never take capsules. The host renumbers them so they work (existing ones too). |
| **Full Refunds** | Removing a building, garage or belt gives back **100%** of what it cost instead of 75%. |

## Install

1. Download **`MoonSweepersModsInstaller.exe`** from the [latest release](https://github.com/Micheal-Cini/moonsweepers-mods/releases/latest).
2. Close the game and run the installer. Press Enter to install.
   - Windows may show "Windows protected your PC" because the installer isn't code-signed. Click **More info**, then **Run anyway**.
3. Start Moon Sweepers from Steam.

**Everyone in the crew should install it, especially the host.** The host's game decides everyone's upgrades.

### Updates

- **Mods update themselves.** When the game starts, the loader checks this repository for a newer release and downloads it. The update applies the next time you start the game.
- **After a game update**, Steam puts the original game file back and mods turn off (the game keeps working normally). Run the installer again to switch them back on.

### Uninstall

Run the installer and choose **2**, or in Steam use *Properties > Installed Files > Verify integrity of game files*. Upgrades that were already shared in a save stay shared.

## How it works

- The installer adds **one call** to the game's startup code (in your own copy of the game, on your PC) that starts the mod loader after the menu loads. If the loader is missing, that call does nothing. A clean copy of the game file is kept as `Assembly-CSharp.dll.original`.
- The **mod loader** loads every `Mods/*.dll` and calls its `ModEntry.Init()`.
- **Updates are signed.** The loader only accepts a release whose `manifest.txt` is signed with the VettaTech mods key, and each file must match the SHA-256 checksum in that manifest. Downloads are only accepted from this repository.
- To turn auto-update off, create an empty `no-update.txt` in the game's `Mods` folder.
- Mods that change game rules (like Full Refunds) use [Harmony](https://github.com/BepInEx/HarmonyX), which the installer puts in `Mods/lib`. Those library files come from the installer, not from auto-update.
- Logs go to the game's log: `%USERPROFILE%\AppData\LocalLow\DragDropGames\MoonSweepers\Player.log` (look for `[ModLoader]`, `[TeamUpgrades]`, `[TeamVehicles]`, `[Sleep]`, `[LogisticsFix]` and `[FullRefunds]`).

This project does not include or redistribute any of the game's files. Third-party libraries: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Building

See [BUILDING.md](BUILDING.md).

---

Need a website, web app or custom software for your business? **[VettaTech](https://vettatech.ca)** builds them.
