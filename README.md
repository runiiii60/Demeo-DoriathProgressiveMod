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

---
## Hotfix v1.0.3

### Fixes
- **A failing hardcoded patch no longer disables the others.** 

### Balance changes
- None. This release contains no gameplay or `Doriath (PROGRESSIVE).json` change.

- Plugin version bumped to 1.0.3.

---
## Hotfix v1.0.2

### Fixes
- **No more game freeze when a lamp explodes in gas**: it only concerns telemetry (metrics submission), with no effect on gameplay.
- **`ElvenFloor09` removed from the random level pool.**

### Balance changes (`Doriath (PROGRESSIVE).json` and code)
- **Floor 2: 15% fewer enemies.** The boss floor and the boss fight itself are unchanged.
- **Boss floor budget**: adjusted.
- **Smaller `BossDeck`**: cut by half.
- **`AIDirectorConfig`**: `AllowedSpawnZoneSaturation` 1.0 → 0.85, `MaxNumberOfUnitsOnBoardHardCap` 168 → 150, `ActivePowerIndexInLevelSoftRoof` 630 → 500.
- **Verochka removed** from `MonsterDeckOverridden`.
- **Level pool**: `DesertFloor09` replaced by `DesertFloor05`.
- Plugin version bumped to 1.0.2.

---
## Hotfix v1.0.1

### Fixes
- **Only one ElvenSummoner on floor 1**: the bonus ones came from the game's native Dread mode and are now disabled (only the KeyHolder remains).
- **Heroes no longer ignore damage of "their" element**: the Revolutions mode elemental immunities are removed (this also removes the Warlock's +1 AP when hit).
- **The Bard's Zap now damages `HealingBeacon`, `SmiteWard` and `SporeFungus`.**
- **Level-up now stands a knocked-down hero back up.**

### Balance changes (`Doriath (PROGRESSIVE).json` and code)
- **ElvenSummoner**: 2 AP, move range 4, `Banish` and `EmergencyTeleport` removed.
- **MotherCy** removed from the floor 2 decks (only appears as the floor 2 KeyHolder).
- **Sorcerer**: no longer immune to `StunSelf`.
- **Damage**: Zap and EnemyFireball 4–6, Fireball 10–15.
- **Mimic** and **ScabRat** attack 4.
- **`CardEnergyFromAttackMultiplied`**: 0.3.
- **`EnemyPartyScaled`**: enemy health x1.1 at average party level 0 up to x1.9 at level 9.
- Plugin version bumped to 1.0.1.
