# Demeo Mods

Personal collection of mods for [Demeo](https://demeogame.com/) (BepInEx / [HouseRules](https://github.com/orendain/demeo-mods)).

This repo gathers every mod over time: each one lives in its own folder at the root. For now there's only **Doriath (PROGRESSIVE)**.

## Doriath (PROGRESSIVE)

A HouseRules ruleset that turns Demeo into a progressive mode: heroes level up by filling their mana bar (instead of the classic card system), unlocking abilities and bonuses specific to each level (1 to 10), while enemies scale dynamically based on the party's average level.

Key points:
- Level-based progression (XP via mana), with a level lost if a player is revived without a potion/spell.
- Detailed level tiers (knockdowns, free abilities, restockable cards, ultimate ability, etc.).
- Dynamic enemy scaling based on the party's average level.
- Several custom rules registered via BepInEx/HouseRules.

### Installation

Prerequisites: [BepInEx](https://github.com/BepInEx/BepInEx) and [HouseRules](https://github.com/orendain/demeo-mods) already installed on your copy of Demeo.

1. Download the latest release file.
2. Copy `DoriathMod.dll` into the game's `BepInEx/plugins/` folder.
3. Copy `Doriath (PROGRESSIVE).json` into the game's `HouseRules/` folder.
4. Launch Demeo and select the **Doriath (PROGRESSIVE)** ruleset from the HouseRules menu.
