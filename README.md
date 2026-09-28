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

## Hotfix v1.0.1

### Fixes
- **Only one ElvenSummoner on floor 1.** The second one came from the game's native Dread mode, which spawns extra ElvenSummoners near the exit outside of any monster deck. Those bonus spawns are now disabled; only the KeyHolder remains.
- **Heroes no longer ignore damage of "their" element.** HouseRules' Revolutions mode was switched on by mistake (a rule of the ruleset has "Revolutions" in its name). It cancelled damage from non-boss enemies based on the hero: Sorcerer vs Electricity (ElvenSummoner, TheUnspoken...), Guardian vs Fire, Hunter vs Ice, Barbarian vs Acid/Petrify, Warlock vs untagged damage. It also gave the Warlock +1 AP when hit by such attacks — removed as well.
- **The Bard's Zap now damages HealingBeacon, SmiteWard and SporeFungus.** HouseRules' PartyDamageOverridden cancelled player Zap/LightningBolt/Overload damage on every prop to protect allied props, which also caught these enemy props.
- **Level-up now stands a knocked-down hero back up** (in addition to the full heal), without triggering the level-loss penalty of a Revive.

### Balance changes (`Doriath (PROGRESSIVE).json` and code)
- ElvenSummoner: 2 AP, move range 4, `Banish` and `EmergencyTeleport` removed from its abilities.
- MotherCy removed from the floor 2 decks: she only appears as the floor 2 KeyHolder.
- Sorcerer: no longer immune to `StunSelf`.
- Damage: Zap and EnemyFireball 4–6, Fireball 10–15.
- Mimic attack 4, ScabRat attack 4.
- `CardEnergyFromAttackMultiplied`: 0.3.
- Dynamic enemy health scaling (`EnemyPartyScaled`): now x1.1 at average party level 0 up to x1.9 at level 9 (attack unchanged, x1.1 to x1.4).
