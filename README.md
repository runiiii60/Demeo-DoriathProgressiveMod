# Demeo Mods

Personal collection of mods for [Demeo](https://demeogame.com/) (BepInEx / [HouseRules](https://github.com/orendain/demeo-mods)).

Each mod lives in its own folder at the root:

| Folder | Ruleset | Version |
|---|---|---|
| `Doriathprogressive/` | **Doriath (PROGRESSIVE)** — level up by filling the mana bar | 1.0.4 |
| `Doriathpointprogressive/` | **Doriath (Point Progressive)** — level up by earning points from your actions | 1.0.0 |
| `Doriath.Common/` | Sources shared by both (hardcoded patches, perk table) | — |

The two rulesets can be installed side by side: their DLL, BepInEx GUID, ruleset name and rule class names are all distinct. With both installed, the hardcoded patches are applied once, by Doriath (PROGRESSIVE).

---

## Doriath (PROGRESSIVE)

A HouseRules ruleset that turns Demeo into a progressive mode: heroes level up by filling the mana bar instead of the classic card system, unlocking abilities and bonuses specific to each level (1 to 10), while enemies scale dynamically with the party's average level.

Key points:
- Level progression driven by mana, with a level lost if a player is revived without a potion or magic.
- Detailed level tiers (knockdowns, free abilities, replenishable cards, an ultimate ability). `DoriathPerksPanel.cs` holds the text shown in game.
- Dynamic enemy scaling (`EnemyPartyScaledRule.cs`) following the party's average level.
- Several custom rules registered through BepInEx and HouseRules in `Plugin.cs`.

## Doriath (Point Progressive)

Same levels, same perks, same scaling enemies — **only the source of XP changes.** Heroes no longer level up by filling the party's mana bar but by earning **progression points** from their own actions: hitting, killing, looting, opening, reviving. Progression is therefore **individual**, each hero advancing at their own pace. The scale and multiplier follow TheGrayAlien's "Points Progressive" ruleset.

Key points:
- 100 points is one level. The counter is stored in the `StrengthInNumbers` effect, so it shows in game as a numbered buff icon and is synchronised by the game itself.
- Default scale (multiplied by 3.25): hitting an enemy 1, killing it 1 plus 1 per 10 max HP, bosses +2 and +10, gold 2, chest 3, potion stand 4, door 1, exit 2, fountain 4, reviving an ally 4. **Everything is editable in the JSON config of `DoriathPointLevelUpRule`**, with no rebuild.
- Per-boss kill values, an optional crit bonus and an optional end-of-floor bonus for the whole party, all off by default.
- A level lost to a revive also resets the point counter, so the hero restarts the previous level from scratch.
- The mana bar keeps its vanilla role of handing out a bonus card, and stays deliberately slow.

---

## Installation

Prerequisites: [BepInEx](https://github.com/BepInEx/BepInEx) and [HouseRules](https://github.com/orendain/demeo-mods) already installed on your copy of Demeo.

1. Download the latest release.
2. Copy the `.dll` into the game's `BepInEx/plugins/` folder — `DoriathMod.dll` for PROGRESSIVE, `DoriathPointMod.dll` for Point Progressive.
3. Copy the matching `.json` into the game's `HouseRules/` folder — `Doriath (PROGRESSIVE).json` or `Doriath (Point Progressive).json`.
4. Launch Demeo and pick the ruleset from the HouseRules menu.

## Building

Open the `.csproj` of the mod you want in Visual Studio and build it (`dotnet build`). The `DeployPlugin` target copies the DLL into `BepInEx/plugins/` and the JSON into `HouseRules/` of your game install.

**Where Demeo lives.** By default the projects point at an Oculus install. To build elsewhere, set the `DEMEO_PATH` environment variable to your install root — the folder containing `BepInEx\` and `Demeo_Data\` — without touching the `.csproj`:

```
setx DEMEO_PATH "D:\SteamLibrary\steamapps\common\Demeo"
```

Then **reopen Visual Studio**, which only reads environment variables at startup. If the path is wrong, the build stops on a single message saying so, instead of a dozen "reference not found" errors.

**Shared sources.** The hardcoded patches and `PerkTable` live in `Doriath.Common/`, compiled by both mods through a link. Each mod still produces its own standalone DLL, with nothing extra to deploy. The three folders must stay siblings: the path is relative.

**Ruleset check.** After every build, `tools/check_ruleset.py` validates the JSON: parsing, duplicate rules, deck sizes, map pool, point values, dead config. The build fails on a blocking error. Without Python installed the check is skipped with a warning.

---

## Doriath (Point Progressive) v1.0.0

First release of the points variant.

- **Level-ups driven by actions** instead of the mana bar: damage dealt, kills, gold, chests, potion stands, doors, fountains and revives all feed a per-hero counter, 100 points to a level.
- **Summons credit their owner** — Cana and Arly through the game's own association, and Verochka, Tornado, the grapple totem, Sword of Avalon, SmiteWard and charmed elementals mapped back to the class that places them. An exploding lamp is credited to the hero who set it off, not to the lamp.
- **Everything is JSON-configurable** through `DoriathPointLevelUpRule`: every point value, the `LevelPercentage` pace dial, which summons count, per-boss kill values, and the optional crit and end-of-floor bonuses.
- **A level lost resets the counter**, so the hero restarts the previous level at 0%.
- **Coexists with Doriath (PROGRESSIVE)**: separate DLL, GUID, ruleset name and rule class names, and a soft dependency that leaves the shared hardcoded patches to the classic mod when both are installed, so none of them is applied twice.

## Doriath (PROGRESSIVE) Patch v1.0.4

### Fixes
- **Losing a level no longer costs permanent max HP.** Level perks were granted by one piece of code and taken back by another, and the two had drifted apart: levels 7 and 8 removed 2 max HP that was never granted, and level 4 left its base-HP bonus behind when reverted. Both directions now read the same table, so a level lost gives back exactly what it gave.
- **Telekinetic Burst no longer damages the Barbarian who casts it**, the same way Ice Explosion spares the Warlock. The card is Barbarian-only and has been removed from every other hero's pool.
- **The end-of-level log recap no longer counts gold piles, chests, doors and portals as enemies** — only creatures are counted, on both the spawn and the kill side.

### Balance changes (`Doriath (PROGRESSIVE).json`)
- **Levelling slowed down**: `CardEnergyFromAttackMultiplied` 0.3 → 0.25 (`CardEnergyFromRecyclingMultiplied` stays at 0.3). Measured in play, the party was reaching level 9 of 10 by the exit of floor 2 — a whole floor before the end of the run.

### Tooling and build
- **Portable build**: the Demeo install path is read from the `DEMEO_PATH` environment variable, falling back to the default Oculus location. A wrong path now produces one error that says what to do, instead of a dozen "reference not found".
- **Ruleset validation on every build** (`tools/check_ruleset.py`): JSON parsing, duplicate rules, deck sizes, level pool and dead piece configuration. A broken ruleset fails the build instead of shipping.
- **Shared sources** (`Doriath.Common/`): the hardcoded Harmony patches and the perk table now live in a single folder instead of being duplicated. The ruleset still builds into one standalone DLL, with nothing extra to deploy.
- **End-of-level recap in the BepInEx log**: creatures spawned and killed, broken down by type, with the party's levels.
- **Comments and log messages are all in English**, across both mods and the shared sources.
- Temporary diagnostic patches and dead rule files removed.
- Plugin version bumped to 1.0.4.

## Hotfix v1.0.3

### Fixes
- **A failing hardcoded patch no longer disables the others.**

### Balance changes
- None. This release contains no gameplay or `Doriath (PROGRESSIVE).json` change.

- Plugin version bumped to 1.0.3.

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

---

## Credits

Built on [HouseRules](https://github.com/orendain/demeo-mods) by orendain. The level-loss rule is based on `PieceProgressLostRule` by TheGrayAlien, and the point scale of Point Progressive follows his "Demeo Revolutions (Points Progressive)" ruleset.
