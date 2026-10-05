# AIDirectorConfig — complete reference

Documentation for the HouseRules rule `AIDirectorConfig` (file
`AIDirectorConfigRule.cs`, mods Doriath (PROGRESSIVE) and Doriath (Point
Progressive) — rule name `DoriathPointAIDirectorConfig` on the Point side).
It exposes **19 native constants** of Demeo's AI Director, normally frozen in
the game's code.

Native values extracted by decompiling `Assembly-CSharp.dll` (the `.cctor` of
`Data.GameData.AIDirectorConfig` and the `.ctor()` of `AIDirectorController2`) —
these are the real unmodded game values, not estimates.

---

## 1. What the AI Director does

Demeo does not load a fixed number of enemies at the start of a map. The system
(`Boardgame.AIDirector.AIDirectorController2`, same principle as Left 4 Dead)
splits the map into **zones** (`SpawnZone`) and progressively spends a **power
index budget** each round, as the party explores and depending on where the
players are.

The actual loop, simplified:

1. A zone is discovered → `HandleDiscoveredZone` computes a budget from the
   level's "Dread" data (`GetAmbientPowerIndexDelta` /
   `GetDifficultPowerIndexDelta`) multiplied by the current pacing pattern.
2. That budget is multiplied by `EasySpawnBudgetMultiplier` or
   `NormalSpawnBudgetMultiplier` depending on difficulty.
3. `CustomSpawn` spends the budget by drawing monsters from the floor's deck
   (`MonsterDeckOverridden` in the JSON), as long as:
   - the zone has not reached `AllowedSpawnZoneSaturation`,
   - the `MaxNumberOf{Easy,Medium,Strong}UnitsPerZone` counters are not full,
   - the board has not reached `MaxNumberOfUnitsOnBoardHardCap`,
   - total active power has not exceeded `ActivePowerIndexInLevelSoftRoof`.

**The 19 parameters in this rule only act on steps 2 and 3.** The base budget
(step 1) remains driven by the level's Dread data and is not exposed here.

---

## 2. Two families of fields, two mechanisms

| | Family A | Family B |
|---|---|---|
| Native class | `Data.GameData.AIDirectorConfig` | `Boardgame.AIDirector.AIDirectorController2` |
| Nature | static class, 10 fields | instance fields, 9 fields |
| Technique | direct reflective write | Harmony Postfix on the `.ctor()` |
| When | `OnActivate()` (once per ruleset activation, so once per map/floor) | on every controller construction |

**Why `OnActivate()` and not `Patch()` (bug fixed 2026-09-17):**
`Patch(Harmony)` is called once, at the very start of game loading, **before**
the ruleset's JSON is read. Writing the static fields in `Patch()` meant writing
them with `_config` still `null` — so always the native values, never the JSON's.
Family B never had this problem: its Postfix re-reads `_config` **live** on every
controller construction, which necessarily happens after activation.

`RuntimeHelpers.RunClassConstructor` is called before any write, to force the
native `.cctor` to run and guarantee it can never "catch up" and stomp on our
values afterwards.

**Type conversion:** the JSON stores everything as `double`. `TrySetNumericField`
converts to the native field's real type (`float`, `int` or `double`), checked by
reflection field by field — never assumed. An `int` field receives
`Math.Round(value)`.

**Robustness:** no IL transpiler here, only field writes. A field renamed by a
game update produces a plain warning in the log and is skipped — the other 18
still apply, and nothing crashes. That is far sturdier than
`BossSpawnBudgetAdjustedHardcoded` (an IL patch that depends on an exact
pattern).

---

## 3. Family A — `Data.GameData.AIDirectorConfig` (10 static fields)

The "Doriath" column is the current value in both Doriath rulesets (PROGRESSIVE
and Point Progressive currently use identical values).

### Budget

| Parameter | Type | Native | Doriath | Role |
|---|---|---|---|---|
| `EasySpawnBudgetMultiplier` | float | **0.3** | 1.26 | Ambient spawn budget multiplier on Easy difficulty |
| `NormalSpawnBudgetMultiplier` | float | **1.0** | 2.88 | Same, on Normal difficulty |
| `ActivePowerIndexInLevelSoftRoof` | int | **275** | 500 | **Soft** cap on total active power on the map |
| `MaxNumberOfUnitsOnBoardHardCap` | int | **50** | 150 | **Hard** cap on the total number of units on the board |

**`EasySpawnBudgetMultiplier` / `NormalSpawnBudgetMultiplier`** — the most direct
lever on enemy quantity. It multiplies the budget computed in step 1 before
anything is spent. Raising these increases both the number **and** the strength
of spawned enemies (the budget buys expensive or numerous monsters
indifferently). Note the native gap: Easy 0.3 against Normal 1.0, a ~3.3× factor
between difficulties — to preserve that ratio, both values must be raised
together (Doriath's 1.26 and 2.88 keep a closer ratio of ~2.3).

**`ActivePowerIndexInLevelSoftRoof`** — a *soft* cap: the director stops adding
pressure once the sum of living enemies' power index exceeds this number, but it
despawns nothing and does not cancel spawns already underway. It is the brake
that prevents endless accumulation when the party is not killing fast enough.
Left too low it makes the budget multipliers inert (you pay for a budget you
cannot spend); raised very high it makes the map's pacing depend entirely on the
party's kill speed.

**`MaxNumberOfUnitsOnBoardHardCap`** — a *hard* cap, all categories combined. No
further unit spawns, full stop. This is the performance guardrail (VR: every unit
costs rendering time and AI turn time). Raising it has two concrete costs: longer
enemy turns (turn time grows linearly with unit count) and more pressure on the
monster deck (see § 6).

### Percentages

| Parameter | Type | Native | Doriath | Role |
|---|---|---|---|---|
| `AllowedSpawnZoneSaturation` | float | **0.8** | 0.85 | Max fraction of a zone's tiles occupied before dynamic spawning stops in that zone |
| `SpikeTrigger_MediumMap_ZoneDiscoveryPercentage` | float | **0.5** | 0.5 | % of a medium map that must be discovered before a difficulty spike can trigger |
| `SpikeTrigger_LargeMap_ZoneDiscoveryPercentage` | float | **0.6** | 0.6 | Same, large map |

**`AllowedSpawnZoneSaturation`** — confirmed inside `CustomSpawn` by a direct
comparison `spawnCount / totalTiles >= AllowedSpawnZoneSaturation` → spawning
stops in that zone. So:
- **raising it** (0.8 → 0.85) = more densely populated zones, more enemies
  concentrated in one place;
- **lowering it** = sparser zones, but the leftover budget carries over to other
  zones, so the result is a wider spread across the map rather than fewer
  enemies overall.

At 1.0 you theoretically allow completely filled zones — avoid it: a saturated
corridor can make a zone impassable and block progression.

**`SpikeTrigger_*_ZoneDiscoveryPercentage`** — a timing gate, not a quantity.
A difficulty spike cannot trigger before this share of the map is discovered (on
top of a round condition: minimum 5 rounds, floor 3, on a medium map; 6 and 4 on
a large one — those values are not exposed). Lowering the percentage makes spikes
arrive earlier (party caught off guard at the start of the floor); raising it
pushes them towards the end, when the party is better equipped. Note there is
**no** small-map equivalent: that behaviour is decided elsewhere and is not
moddable through this rule.

### Per-zone counters

| Parameter | Type | Native | Doriath | Role |
|---|---|---|---|---|
| `MaxNumberOfEasyUnitsPerZone` | int | **7** | 16 | Cap on Easy enemies per zone (`SpawnZone.numEasyEnemies`) |
| `MaxNumberOfMediumUnitsPerZone` | int | **5** | 10 | Same, Medium (`numMediumEnemies`) |
| `MaxNumberOfStrongUnitsPerZone` | int | **4** | 7 | Same, Strong (`numStrongEnemies`) |

These three caps apply **per zone**, on top of the global cap. The Easy / Medium
/ Strong classification comes from the monster's power index, via two unexposed
native thresholds: `EasyEnemiesMaxPowerIndex` = 7 and
`MediumEnemiesMaxPowerIndex` = 15 (anything above = Strong). A monster whose
power index you change through another lever therefore changes category and cap.

Natively, the per-zone total caps at 7 + 5 + 4 = **16 units**; in Doriath,
16 + 10 + 7 = **33**. This is what sets the real maximum density of an encounter,
and it is frequently what *silently throttles* a budget increase: if the counters
are full, the surplus budget cannot be spent in that zone. Raising them without
raising the budget does nothing either — the two go together.

---

## 4. Family B — `AIDirectorController2` (9 instance fields)

These fields are used by `TagSpawnZones()` to classify discovered tiles into
Zone1 / Zone2 / Zone2_OuterRing — in other words **where** the map counts as near
the entrance, intermediate or peripheral, which determines what kind of spawning
applies there.

| Parameter | Type | Native | Doriath |
|---|---|---|---|
| `Zone1_Large_PercentCoverage` | float | **0.25** | 0.25 |
| `Zone1_Medium_PercentCoverage` | float | **0.32** | 0.32 |
| `Zone1_Small_PercentCoverage` | float | **0.32** | 0.32 |
| `Zone2_Large_PercentCoverage` | float | **0.46** | 0.46 |
| `Zone2_Medium_PercentCoverage` | float | **0.44** | 0.44 |
| `Zone2_Small_PercentCoverage` | float | **0.42** | 0.42 |
| `Zone2_OuterRing_Large_PercentCoverage` | float | **0.0** | 0.0 |
| `Zone2_OuterRing_Medium_PercentCoverage` | float | **0.0** | 0.0 |
| `Zone2_OuterRing_Small_PercentCoverage` | float | **0.0** | 0.0 |

The three `Zone2_OuterRing_*` fields are **a confirmed native 0.0**: the native
code never initializes them explicitly (verified by decompiling the `.ctor()` —
it is a real zero, not a gap in the research). In other words the
`Zone2_OuterRing` tag exists in the `SpawnZoneTag` enum but is not fed by this
mechanism in the current version of the game. Giving them a non-zero value
activates a native code path **the base game never exercises** — treat it as
untested.

**Practical implications.** These 9 values change the *geography* of spawning,
not its quantity: Zone1 ≈ 25-32 % of the map (near the entrance), Zone2 ≈ 42-46 %.
Raising them concentrates spawning across a larger "active" portion of the map;
lowering them leaves more tiles tagged `Normal`. Since Zone1 + Zone2 already
covers ~71-76 % natively, pushing the sum past 1.0 is incoherent and the
resulting behaviour is undocumented on the native side. **Doriath leaves all nine
at their native values** — these are the least understood of the 19 parameters
and the ones that return the least visible in-game feedback for the risk taken.

---

## 5. Native values found but NOT exposed

Spotted in the same `.cctor` (~53 constants in total), kept here for reference in
case the rule ever needs extending:

- `BalanceMultiplier` for 1 / 2 / 3 players = **0.33 / 0.55 / 0.77** — the
  party-size scaling. This directly explains why a map tuned for 4 players is
  brutal with 2: the budget is not halved, it is multiplied by 0.55 instead of
  1.0.
- `WeakUnitsPowerIndex` = 5; `EasyEnemiesMaxPowerIndex` = 7;
  `MediumEnemiesMaxPowerIndex` = 15 — the classification thresholds from § 3.
- `KeyHolderMinDistanceToExit` = 10, `KeyHolderMaxDistanceToExit` = 30,
  `KeyHolderMinDistanceToEntrance` = 10 — keyholder placement.
- `SpikeTrigger` rounds Medium / Large = 5 / 6 (minimum 3 / 4).
- `ZoneExclusionDistanceFromEntrance` = 10 — radius around the entrance where
  spawning is forbidden.
- `elvenSummonerMaxSpawnDistanceFromExit` — a literal, specific to the
  ElvenSummoner.
- `SpikeZoneMaxBudget` (`SpawnZone`) — **a dead field as far as gameplay goes**:
  all of its occurrences in `AIDirectorController2` are serialization copies
  (`SerializeTo` / `DeserializeFrom` / `GetSerializable`), never a spawn
  decision. Don't waste time exposing it.
- `minPowerIndexCost` / `maxPowerIndexCost` of normal ambient spawning
  (`DynamicSpawning`, native 1 / 99) — IL literals, only exposable through a
  transpiler.

---

## 6. ⚠ Critical implication: monster deck exhaustion

This is the only real risk of this rule, and it does not come from the rule
itself.

Every spawned unit **consumes one card** from the floor's deck
(`MonsterDeckOverridden`: `EntranceDeckFloor1/2`, `ExitDeckFloor1/2`,
`BossDeck`). If the deck is too small for the spawn volume these parameters
allow, the game draws from an empty list → **an unhandled exception that
silently kills the level-load coroutine**: the level never loads, with no visible
error message.

Because the number of draws depends on the map and the seed, **the crash is
intermittent, not systematic** — one successful test does not prove a config is
safe.

Rule of thumb: when raising `EasySpawnBudgetMultiplier`,
`NormalSpawnBudgetMultiplier`, `AllowedSpawnZoneSaturation` or
`MaxNumberOfUnitsOnBoardHardCap` beyond their current values, scale up the
quantities of **every** deck in `MonsterDeckOverridden` proportionally.

Related trick: a deck entry set to **0** is treated as a card recycled forever
(see `ElvenSpearman` and `GoblinFighter` at 0 in Doriath's `BossDeck`) — it is
the simplest safety net against exhaustion, provided you accept that monster type
appearing in unlimited quantity.

---

## 7. Interaction with the mod's other spawn levers

`AIDirectorConfig` only governs generic ambient spawning. The other levers are
cumulative:

| Lever | Where | Effect |
|---|---|---|
| `BossSpawnPowerIndexBudgetAdjustedHardcoded` | Doriath.Common, `Multiplier = 3f` (native 1.0) | ×3 on the power index budget of the spawn around the boss |
| `BossSpawnBudgetAdjustedHardcoded` | Doriath.Common, `MinCost = 1`, `MaxCost = 99` (native 5 / 99) | Lowers the minimum cost → cheaper monsters become eligible again, so more enemies for the same budget |
| `Floor2SpawnBudgetReducedHardcoded` | Doriath.Common, `Multiplier = 0.85f` | −15 % budget on floor 2 only |
| `MonsterDeckOverridden` | JSON, native HouseRules rule | Weighting of the types drawn (never their number) + the card reserve |

Important point: the spawn around the boss is tagged `SpawnType.Ambient`, not
`SpawnType.Boss` — the boss fight reuses the ambient system. So
`AIDirectorConfig`'s parameters (per-zone caps, hard cap, saturation) apply to
the boss fight **as well**, and can throttle the boss multipliers above without
anything reporting it.

The three `*Hardcoded` patches are no longer JSON-configurable (a deliberate
decision: these are stable bugfixes/tuning, changing them requires a rebuild).
Only `AIDirectorConfig` stays tunable live in the JSON, with no rebuild.

---

## 8. JSON entry and verification

### a) Native values — vanilla behaviour

This block reproduces the unmodded game exactly. Useful as a starting point for a
new ruleset, or to roll back and compare against an in-game test.

```json
{
  "Rule": "AIDirectorConfig",
  "Config": {
    "EasySpawnBudgetMultiplier": 0.3,
    "NormalSpawnBudgetMultiplier": 1.0,
    "ActivePowerIndexInLevelSoftRoof": 275,
    "MaxNumberOfUnitsOnBoardHardCap": 50,
    "AllowedSpawnZoneSaturation": 0.8,
    "SpikeTrigger_MediumMap_ZoneDiscoveryPercentage": 0.5,
    "SpikeTrigger_LargeMap_ZoneDiscoveryPercentage": 0.6,
    "MaxNumberOfEasyUnitsPerZone": 7,
    "MaxNumberOfMediumUnitsPerZone": 5,
    "MaxNumberOfStrongUnitsPerZone": 4,
    "Zone1_Large_PercentCoverage": 0.25,
    "Zone1_Medium_PercentCoverage": 0.32,
    "Zone1_Small_PercentCoverage": 0.32,
    "Zone2_Large_PercentCoverage": 0.46,
    "Zone2_Medium_PercentCoverage": 0.44,
    "Zone2_Small_PercentCoverage": 0.42,
    "Zone2_OuterRing_Large_PercentCoverage": 0.0,
    "Zone2_OuterRing_Medium_PercentCoverage": 0.0,
    "Zone2_OuterRing_Small_PercentCoverage": 0.0
  }
}
```

Shorter equivalent: omit the `AIDirectorConfig` entry entirely. The rule's
`NativeDefaults` *are* these values.

### b) Doriath values — the config actually in place

Identical in Doriath (PROGRESSIVE) and Doriath (Point Progressive). Only 7 of the
19 fields are pushed: the 4 budget fields and the 3 per-zone counters, plus
saturation. Both `SpikeTrigger_*` and all 9 `PercentCoverage` fields are left at
their native values.

```json
{
  "Rule": "AIDirectorConfig",
  "Config": {
    "EasySpawnBudgetMultiplier": 1.26,
    "NormalSpawnBudgetMultiplier": 2.88,
    "ActivePowerIndexInLevelSoftRoof": 500,
    "MaxNumberOfUnitsOnBoardHardCap": 150,
    "AllowedSpawnZoneSaturation": 0.85,
    "SpikeTrigger_MediumMap_ZoneDiscoveryPercentage": 0.5,
    "SpikeTrigger_LargeMap_ZoneDiscoveryPercentage": 0.6,
    "MaxNumberOfEasyUnitsPerZone": 16,
    "MaxNumberOfMediumUnitsPerZone": 10,
    "MaxNumberOfStrongUnitsPerZone": 7,
    "Zone1_Large_PercentCoverage": 0.25,
    "Zone1_Medium_PercentCoverage": 0.32,
    "Zone1_Small_PercentCoverage": 0.32,
    "Zone2_Large_PercentCoverage": 0.46,
    "Zone2_Medium_PercentCoverage": 0.44,
    "Zone2_Small_PercentCoverage": 0.42,
    "Zone2_OuterRing_Large_PercentCoverage": 0.0,
    "Zone2_OuterRing_Medium_PercentCoverage": 0.0,
    "Zone2_OuterRing_Small_PercentCoverage": 0.0
  }
}
```

(On the Point Progressive side the rule name is `DoriathPointAIDirectorConfig`,
with the same keys and, currently, the same values.)

Summary of block b)'s deltas against native:

| Field | Native | Doriath | Delta |
|---|---|---|---|
| `EasySpawnBudgetMultiplier` | 0.3 | 1.26 | ×4.2 |
| `NormalSpawnBudgetMultiplier` | 1.0 | 2.88 | ×2.9 |
| `ActivePowerIndexInLevelSoftRoof` | 275 | 500 | ×1.8 |
| `MaxNumberOfUnitsOnBoardHardCap` | 50 | 150 | ×3 |
| `AllowedSpawnZoneSaturation` | 0.8 | 0.85 | +0.05 |
| `MaxNumberOfEasyUnitsPerZone` | 7 | 16 | ×2.3 |
| `MaxNumberOfMediumUnitsPerZone` | 5 | 10 | ×2 |
| `MaxNumberOfStrongUnitsPerZone` | 4 | 7 | ×1.75 |
| the other 11 fields | — | — | native, unchanged |

Per-zone total: 16 units native → 33 in Doriath.

**Every key is optional.** An omitted key falls back to the native value, so an
empty config = vanilla behaviour. An unknown key is ignored with a warning in the
log.

**Verification at load time** — in the BepInEx log, look for:

```
[AIDirectorConfigRule] (OnActivate) AIDirectorConfig.<field> = <value>
[AIDirectorConfigRule] AIDirectorController2.<field> = <value>
```

19 lines expected (10 of the first kind, 9 of the second). A `... not found ...`
line flags a field renamed by a game update: harmless, but that field keeps its
native value and is no longer moddable until the name is fixed in the rule.

Mind when you read it: the `(OnActivate)` lines only appear on **ruleset
activation** (once per map/floor), not at game startup. Not seeing them right
after launching is normal.

---

## 9. Using this rule in another ruleset (the `DoriathMod.dll` dependency)

`AIDirectorConfig` is **not** a native HouseRules rule. It is a custom rule
provided by `DoriathMod.dll`: without that DLL the name `"AIDirectorConfig"`
exists nowhere, and any ruleset using it cannot be imported.

### Repository and files

Source and releases: **https://github.com/runiiii60/Demeo-DoriathProgressiveMod**
(branch `master`, monorepo holding both modes)

| File | Link |
|---|---|
| The rule itself | [`Doriathprogressive/AIDirectorConfigRule.cs`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/AIDirectorConfigRule.cs) |
| Raw version (copy/paste) | [raw](https://raw.githubusercontent.com/runiiii60/Demeo-DoriathProgressiveMod/master/Doriathprogressive/AIDirectorConfigRule.cs) |
| Rule registration | [`Doriathprogressive/Plugin.cs`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/Plugin.cs) |
| Full example ruleset | [`Doriathprogressive/Doriath Progressive.json`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/Doriath%20Progressive.json) |
| Decompilation notes | [`Doriathprogressive/AIDirector_Notes.md`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/AIDirector_Notes.md) |
| Compiled DLL (`DoriathMod.rar`) | [Releases](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/releases) |

Point Progressive variant: same layout under `Doriathpointprogressive/`.

### DLL identity

| | |
|---|---|
| Assembly name | `DoriathMod.dll` (project `ProgressiveMod.csproj`) |
| BepInEx GUID | `com.monnom.demeomods.progressive` |
| Version | 1.0.5 |
| Hard dependency | `com.orendain.demeomods.houserules.core` |
| Soft dependency | `com.orendain.demeomods.houserules.configuration` |

The DLL registers 6 rules at startup, `AIDirectorConfigRule` among them. A rule's
JSON name is the class name **minus the `Rule` suffix** — hence
`"AIDirectorConfig"`.

### Case 1 — "I just want to use the rule in MY JSON ruleset"

This is exactly what `MagicGarden.json` does: a pure-JSON ruleset that picks this
one custom rule out of `DoriathMod.dll`.

1. Install `DoriathMod.dll` into `BepInEx/plugins/` (next to the HouseRules
   DLLs). No configuration, no other file.
2. Add the entry to the ruleset's `Rules` array (see § 8 for the keys).
3. Restart the game, activate the ruleset, check the 19 log lines.

**There is no dependency field to declare in the JSON.** The HouseRules ruleset
format only knows `Name`, `Description`, `Enabled` and `Rules` — the dependency
is entirely implicit, resolved at import time by whether the rule name is present
in the Rulebook.

**Consequences to know about:**

- **Missing DLL = broken ruleset, not a degraded one.** An unknown rule name
  fails the import of the whole ruleset — not just that rule. Someone who
  downloads a JSON ruleset using it without having the DLL does not see "33 of 34
  rules", they see a ruleset that won't load. A publicly distributed ruleset must
  therefore state the dependency in its description or release post.
- **Other mods stay independent.** `DoriathMod.dll` also brings 5 other rules
  (progression, XP, etc.) and `*Hardcoded` patches that are always active as soon
  as the DLL loads. Installing the DLL purely for `AIDirectorConfig` therefore
  brings those patches along too — it is currently all-or-nothing, not a menu.
- **Multiplayer:** the rule is marked `IMultiplayerSafe`, so only the host needs
  the DLL; spawning being host-authoritative, guests install nothing.
- **No rebuild to tune the values.** All 19 values are read from the JSON on each
  ruleset activation: you can edit them and restart the game without recompiling
  anything. Only the `*Hardcoded` patches in § 7 require a rebuild.
- **Mind the name on the Point Progressive side.** That mode registers the rule
  as `DoriathPointAIDirectorConfig`. The two names are not interchangeable: using
  the Point name without `DoriathPointMod.dll` fails the same way.

### Case 2 — "I want the same capability in MY own C# mod"

Two routes, depending on whether you want to depend on `DoriathMod.dll` or be
self-contained.

**a) Depend on `DoriathMod.dll`** — nothing to write: just require it as a BepInEx
dependency of your own plugin and use `"AIDirectorConfig"` in your ruleset.

```csharp
[BepInDependency("com.monnom.demeomods.progressive")]
```

**b) Re-implement the rule inside your own plugin** — the cleanest route for a mod
that does not want to ship all of Doriath. The file to take:
[`AIDirectorConfigRule.cs`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/AIDirectorConfigRule.cs).
It is self-contained: it only depends on HarmonyLib, `HouseRules.Core` and
`Plugin.Log`. To reuse it:

1. Copy the file into your project, change the `namespace`, and replace
   `Plugin.Log` with your own logger.
2. **Rename the class.** Two mods registering the same rule name clash in the
   Rulebook: if `DoriathMod.dll` is also installed, a second
   `AIDirectorConfigRule` class produces a duplicate name. That is exactly why
   the Point Progressive side uses the `DoriathPoint*` prefix — apply the same
   logic (`MyModAIDirectorConfigRule` → JSON key `"MyModAIDirectorConfig"`).
3. Register the rule in your `Awake()`:
   `HR.Rulebook.Register(typeof(MyModAIDirectorConfigRule));`
4. Keep the three design points that make the rule work, and which are the file's
   real value:
   - writing the **static** fields in `OnActivate()`, never in `Patch()`
     (otherwise `_config` is still `null` → native values, see § 2);
   - `RuntimeHelpers.RunClassConstructor` before overwriting anything;
   - `TrySetNumericField`, which converts the JSON's `double` to the native
     field's real type (`int` for `MaxNumberOfUnitsOnBoardHardCap`, `float` for
     `AllowedSpawnZoneSaturation`…) — checked by reflection, never assumed.
5. Don't drop the `NativeDefaults`: they are what guarantees that an omitted key
   = vanilla behaviour.

In both cases, the § 6 warning about monster deck exhaustion applies in full: a
third-party ruleset that raises the budgets without raising its
`MonsterDeckOverridden` decks will produce intermittent level-load failures.

---

## 10. What remains uncertain

To be distinguished from the rest of this document, which is confirmed by
decompilation:

- The exact budget-spending formula inside `CustomSpawn` (how
  `minPowerIndexCost` / `maxPowerIndexCost` arbitrate between "more monsters" and
  "stronger monsters") has not been traced in detail.
- The priority order between `ActivePowerIndexInLevelSoftRoof` (soft) and the
  per-zone counters when both are close to saturation.
- Exactly what `MaxNumberOfUnitsOnBoardHardCap` counts: "all units combined" is
  the native wording, but whether summons, hostile props and player pieces count
  has not been verified.
- The behaviour of `Zone2_OuterRing_*` at a non-zero value (§ 4).

---

*Sources: decompilation of `Assembly-CSharp.dll` (in-house
`System.Reflection.Metadata` tool, sessions of 2026-09-16 and 2026-09-29),
`AIDirectorConfigRule.cs`, `AIDirector_Notes.md`, and the rulesets
`Doriath Progressive.json` / `Doriath PointProgressive.json`.*
