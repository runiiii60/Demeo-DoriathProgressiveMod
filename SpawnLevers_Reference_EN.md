# Additional spawn levers — reference

Documentation for Doriath's 4 spawn levers that do **not** go through
`AIDirectorConfig` (see `AIDirectorConfig_Reference.md` for that one):

| Lever | Where | Type |
|---|---|---|
| `BossSpawnPowerIndexBudgetAdjustedHardcoded` | `Doriath.Common/Hardcoded/` | Transpiler, no JSON |
| `BossSpawnBudgetAdjustedHardcoded` | `Doriath.Common/Hardcoded/` | Transpiler, no JSON |
| `Floor2SpawnBudgetReducedHardcoded` | `Doriath.Common/Hardcoded/` | Postfix, no JSON |
| `MonsterDeckOverridden` | ruleset JSON | native HouseRules rule |

Repository: **https://github.com/runiiii60/Demeo-DoriathProgressiveMod** — the
three patches live under
[`Doriath.Common/Hardcoded/`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/tree/master/Doriath.Common/Hardcoded),
and are invoked from
[`Plugin.cs`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/Plugin.cs).

The first three are shared by both modes (Doriath.Common) and applied from
`Plugin.cs`, outside the HouseRules rule system: they therefore do **not** appear
in the active-rules panel in game, and cannot be tuned without recompiling. The
fourth is pure JSON.

---

## 1. The calculation chain, and where each lever fits

```
                 ┌─ level Dread data (not moddable)
                 │
 round budget    │  GetAmbientPowerIndexDelta()      ← normal exploration
                 │  GetDifficultPowerIndexDelta()    ← difficulty spikes
                 └──────────────┬───────────────────────────────────
                                │  ×0.85 on floor 2 only
                                │  [Floor2SpawnBudgetReducedHardcoded]
                                ▼
        × EasySpawn/NormalSpawnBudgetMultiplier   [AIDirectorConfig]
                                ▼
                       CustomSpawn() spends the budget
                                │
                                │  per-monster cost bounds: 1 to 99
                                │  (native 5 to 99, boss fight only)
                                │  [BossSpawnBudgetAdjustedHardcoded]
                                ▼
                  draws from the floor's deck  [MonsterDeckOverridden]
                                ▼
            caps: zone saturation, per-zone counters,
            board hard cap, power soft roof  [AIDirectorConfig]
```

Separate path for the start of the boss fight:

```
 SpawnBossAndMinions()
   └─ GetDifficultPowerIndexLevel()  ──×3──▶  CustomSpawn's PowerIndexPoints
                                     [BossSpawnPowerIndexBudgetAdjustedHardcoded]
```

**The two "Boss" levers are complementary, not redundant**: one changes the total
budget available, the other changes which monsters are eligible to spend it.

---

## 2. `BossSpawnPowerIndexBudgetAdjustedHardcoded` — ×3 on the boss budget

| | |
|---|---|
| Parameter | `Multiplier = 3f` |
| Native | 1.0 (no multiplication at all) |
| Target | `AIDirectorController2.SpawnBossAndMinions` |
| Technique | Transpiler |

### What it does

`SpawnBossAndMinions()` first places the fixed pieces of the boss type (the boss,
its escort, the throne tiles — positions hardcoded per boss type), then calls
`CustomSpawn(...)` **exactly once** to add extra monsters around it. That call's
budget comes from
`PowerIndexPoints = dataHelper.GetDifficultPowerIndexLevel(currentLevelIndex)`.

This patch multiplies the returned value **at that one site**: ×3 the extra
escort monsters (in volume or in strength, since the budget buys either
indifferently).

### Why a transpiler and not a Postfix on the helper

`GetDifficultPowerIndexLevel` is shared by **four** systems:
`GetDifficultPowerIndexDelta()` (normal ambient spawning), `HandleSpikeLock()`,
`SpawnKeyholder()` and this boss spawn. A Postfix on the helper would have
tripled the other three along the way. The transpiler multiplies the result on
the stack at the call site, leaving the helper untouched.

The insertion uses exactly the game's own idiom (`conv.r4` ; `ldc.r4` ; `mul` ;
`Mathf.RoundToInt`), lifted from `HandleSpikeLock()`.

### Anchoring and safety

The anchor is `callIndex - 7` = the `callvirt GetDifficultPowerIndexLevel`
instruction, validated **by method name**, not by a literal value. That is
deliberate: it makes this patch immune to `BossSpawnBudgetAdjustedHardcoded`
having already rewritten the literals at `callIndex-5/-4` of the *same* method.
The two transpilers apply one after the other without stepping on each other.

If the pattern is not found (game update), the patch **does nothing** and logs a
warning — native value kept, no risk of corrupting the method body.

### Implications

- The ×3 applies **only when the boss fight triggers**, not to the rest of the
  boss floor (which goes through the normal ambient deltas).
- It is still subject to `AIDirectorConfig`'s caps: if
  `MaxNumberOfUnitsOnBoardHardCap`, zone saturation or the per-zone counters are
  hit, part of the tripled budget is lost **silently**. Raising this multiplier
  without checking the caps guarantees nothing.
- It is also the lever that burns the most `BossDeck` cards in one go (see § 5).
- Value history: 1.5 → 2.025 → 2.5 → 3.84 → **3**.

---

## 3. `BossSpawnBudgetAdjustedHardcoded` — widens the eligible monsters

| | |
|---|---|
| Parameters | `MinCost = 1`, `MaxCost = 99` |
| Native | `minPowerIndexCost = 5`, `maxPowerIndexCost = 99` |
| Target | `AIDirectorController2.SpawnBossAndMinions` (same method as § 2) |
| Technique | Transpiler |

### What it does

The two cost bounds passed to `CustomSpawn` are IL literals (native 5 and 99) —
no field, no config to mutate, hence the transpiler. They filter which deck
monsters are eligible for the budget: a monster whose power index cost falls
outside the range is rejected.

`MinCost` lowered from 5 to 1 makes the cheapest monsters eligible again. Direct
consequence: **more enemies for the same budget**, rather than a few big ones.
`MaxCost` is left at native (99 = no effective ceiling).

### Anchoring and safety

The insertion point is located by the `CustomSpawn` call, **not** by scanning for
the literals 5 and 99 — those values are too common and would match elsewhere in
the method. Then `callIndex-5` must be `ldc.i4.5` and `callIndex-4` must be
`ldc.i4.s 99`: if either does not match (arguments reordered, code inserted
between them by an update), the patch is cancelled entirely rather than applying
half of itself.

### Implications

- A **qualitative** effect, not a quantitative one: it does not grant more
  budget, it changes what the budget can buy. Combined with § 2's ×3 the effects
  compound (more budget AND cheaper units = far more units).
- Side effect on the deck: every rejection for being out of cost bounds
  **consumes a card too** (§ 5). Widening the range therefore reduces the number
  of rejections, which is actually good for the deck reserve — the only lever in
  this document that relieves pressure on the `BossDeck` instead of adding to it.
- A lower bound of 1 lets the weakest units through: if the `BossDeck` holds many
  small units, the boss fight can turn into a mass of trash mobs rather than an
  elite escort. That is a design choice, to be settled through the `BossDeck`
  composition, not through this bound.
- Normal ambient spawning has its own literals (`DynamicSpawning`, native
  **1 / 99**) — already equivalent to what we force here, so left unpatched.

---

## 4. `Floor2SpawnBudgetReducedHardcoded` — −15 % on floor 2 only

| | |
|---|---|
| Parameters | `Multiplier = 0.85f`, `TargetFloorIndex = 2` |
| Target | `AIDirectorDataHelper.GetAmbientPowerIndexDelta` and `GetDifficultPowerIndexDelta` |
| Technique | Postfix (×2 methods) |

### Why this patch exists

In-game observation: many more enemies on floor 2 than on floor 3. Three native
causes were identified, explaining why floor 3 has **fewer**:

1. `GetPowerIndexForCrossover` applies **−3.0** difficulty when
   `IsForestAdventure() && GetCurrentPlayableFloorIndex() == 3` (decompiled) —
   Doriath's adventure ending on RootLord (forest boss), that applies.
2. On the boss floor, `CustomSpawn` draws from the `BossDeck` for **all** spawns
   (not just the fight): a deck full of large units = fewer units per budget.
3. `ActivePowerIndexInLevelSoftRoof` counts the power already on the board: the
   RootLord and its guards eat a large share of it.

**The blocker:** no `AIDirectorConfig` field is per-floor — the full field list
was verified by decompilation, they are all global. Hence this patch, which
reduces the budget where it is computed.

### What it does

A Postfix on both budget "deltas" (both `public`, returning `int`, verified by
signature):

- `GetAmbientPowerIndexDelta()` — normal spawns during exploration;
- `GetDifficultPowerIndexDelta(context, levelIndex)` — difficulty spikes.

`__result` is multiplied by 0.85 **only if**:

```csharp
if (__result <= 0) return;                 // guardrail, see below
var floor = ...GetCurrentPlayableFloorIndex();
if (floor != TargetFloorIndex) return;     // floors 1 and 3 untouched
__result = (int)(__result * Multiplier);
```

### The `__result <= 0` guardrail — important

The delta is "target power minus enemy power already on the board". **A negative
delta means there are already too many enemies.** Multiplying it by 0.85 would
move it closer to zero, which would allow **more** spawns — the exact opposite of
the intended effect. Hence the `return` on negative or zero values. Do not remove
it thinking you are simplifying.

### Implications

- The boss fight budget is **not** affected: it goes through
  `GetDifficultPowerIndexLevel` (§ 2), not through the deltas.
- The `(int)` truncation rounds down, so the real reduction is slightly more than
  15 % on small deltas (a delta of 3 → 2, i.e. −33 %). Negligible on large
  deltas, but it is why this setting is judged in game rather than on paper.
- The value was settled after a first attempt at 0.75 (−25 %), judged too strong.
- To target another floor, change `TargetFloorIndex`. To apply a different
  coefficient per floor you would have to replace the two constants with a table
  — not done, not needed today.

---

## 5. `MonsterDeckOverridden` — the card reserve

A **native** HouseRules rule (not a Doriath patch), configured in JSON. It
defines, per floor, which monster types can be drawn and in what quantity, plus
the special roles.

### How the deck actually works

From decompiling `MonsterDeck.DrawEntry` and
`MonsterDeckOverriddenRule.CreateSubDeck`. The key point: **cards are consumed,
the deck is never reshuffled or refilled.**

- `CreateSubDeck`: a value **N > 0** creates N copies with
  `isRedrawEnabled = false`; a value of **0** creates **ONE** copy with
  `isRedrawEnabled = true`.
- `DrawEntry` takes element 0, does `RemoveAt(0)`, and **only puts it back at the
  end if `isRedrawEnabled`**. So: an entry of N runs out, an entry of 0 is
  recycled forever.
- The `DrawEntry(deckType, maxPowerIndex, ...)` overload loops up to 99 times
  while the drawn monster costs more than the remaining budget — **every
  rejection consumes a card too**. This is the most underestimated source of
  consumption.
- `CustomSpawn` calls `DrawEntry(..., dataHelper.IsBossLevel(), ...)`: on the
  boss floor, **all** spawns (ambient, spikes, boss fight) draw from the
  `BossDeck`.
- Empty deck → `get_Item(0)` on an empty list → unhandled exception → the
  level-load coroutine dies silently: **the level never loads, with no error
  message**. That is the explanation for the "level never loads" bug.

### What deck size does NOT do

A deck's size **does not drive** the number of enemies — the budget does. The
deck only defines:

1. the **proportions** between monster types (an entry of 16 is drawn twice as
   often as one of 8);
2. the **reserve** available before exhaustion.

Growing a deck does not make a map harder; it only makes it survivable at a high
budget.

### Doriath (PROGRESSIVE)'s current config

| Deck | Types | Consumable cards |
|---|---|---|
| `EntranceDeckFloor1` | 24 | 183 |
| `ExitDeckFloor1` | 25 | 199 |
| `EntranceDeckFloor2` | 21 | 127 |
| `ExitDeckFloor2` | 20 | 132 |
| `BossDeck` | 20 | 159 + 2 infinite entries |

Special roles: `Boss` = `RootLord`, `KeyHolderFloor1` = `ElvenSummoner`,
`KeyHolderFloor2` = `MotherCy`.

### The anti-crash net: two entries at 0

In the `BossDeck`, `ElvenSpearman: 0` and `GoblinFighter: 0` are cards recycled
forever. Consequence: **the `BossDeck` can never run dry**, so the load crash is
structurally impossible on the boss floor.

Accepted trade-off: once the 159 consumable cards are spent, only ElvenSpearman
and GoblinFighter would keep appearing. A very long boss fight would therefore
degenerate into waves of those two units rather than a crash — a deliberate
compromise.

**The four floor 1 and 2 decks do not have this net.** That is the remaining weak
point: adding one entry at 0 there (a weak, thematically fitting monster) would
cost one line of JSON and close the last path to the crash.

### `Boss` / `KeyHolderFloorN` roles — inherited behaviour

The role decides the behaviour on the map, not the monster:

- `Boss` → `SpawnType.Boss` → placed in the **dedicated boss room**, marked on
  the map from the start of the floor (native level-design behaviour, nothing to
  do with fog of war).
- `KeyHolderFloorN` → `SpawnType.Keyholder` → placed in an ordinary ambient spawn
  zone, so **hidden by fog of war**, to be found by exploring.

Any `BoardPieceId` assigned to `Boss` would inherit the visible fixed room, and
any one assigned to `KeyHolderFloorN` would inherit the fog of war.

---

## 6. Interactions and pitfalls

**Two transpilers on the same method.** §2 and §3 both patch
`SpawnBossAndMinions`. That is deliberate and safe, because §2 anchors on a
*method name* and §3 on the `CustomSpawn` call — neither depends on a literal the
other modifies. The application order is still the order of the list in
`Plugin.cs`, though: touching it means re-verifying both anchors.

**The caps always win.** All three budget levers can be nullified in practice by
`MaxNumberOfUnitsOnBoardHardCap`, the per-zone counters or
`AllowedSpawnZoneSaturation`. No log reports a budget lost for that reason: if a
budget increase changes nothing in game, look at `AIDirectorConfig`'s caps first.

**The boss floor stacks everything.** On that floor: the `BossDeck` serves all
spawns, the boss fight gets ×3 budget, the cost bounds are widened, and the
native −3.0 of the forest adventure lowers base difficulty. Four compounding
effects — it is the hardest floor to predict on paper and the one that needs the
most testing.

**None of these three patches can be tuned without a rebuild.** Deliberate
choice: these are settled values, and the JSON remains the entry point for what
needs to move often (`AIDirectorConfig`, `MonsterDeckOverridden`). Changing
`Multiplier`, `MinCost` or `TargetFloorIndex` = edit the constant and recompile
the DLL.

**Failure isolation.** Each patch is invoked inside its own `try` in `Plugin.cs`
(13 `*Hardcoded` patches in total): one that fails — a method renamed by an
update — does not stop the others. The log reports
`Hardcoded patches applied (n/13)`.

---

## 7. Verification in the BepInEx log

At game startup (not at ruleset activation):

```
[BossSpawnBudgetAdjustedHardcoded] Patch applied ... minPowerIndexCost=5 -> 1, maxPowerIndexCost=99 -> 99.
[BossSpawnPowerIndexBudgetAdjustedHardcoded] Patch applied ... multiplier x3 applied to the result of GetDifficultPowerIndexLevel().
[Floor2SpawnBudgetReducedHardcoded] Spawn budget for floor 2 multiplied by 0.85 (2 method(s) patched) — other floors unchanged.
[Plugin] Hardcoded patches applied (13/13).
```

Lines to watch:

| Line | Meaning |
|---|---|
| `Pattern IL attendu non trouve ...` | Game update: the patch is cancelled, native value kept. Harmless, but the lever now does nothing. |
| `... not found — patch skipped` | Type or method renamed. Same. |
| `(2 method(s) patched)` for Floor2 | Must read **2**. At 1, only one of the two deltas is covered → the reduction applies to half the spawning. |
| `applied (12/13)` or less | A patch threw — look for `Hardcoded patch <name> failed`. |

There is no dedicated log for the deck: the real number of draws per floor is not
measurable without instrumentation (the budget is computed from an internal
balancing table). Deck sizes remain a reasonable bet, not a guarantee.

---

## 8. What remains uncertain

- The exact budget-spending formula inside `CustomSpawn`: how
  `minPowerIndexCost` / `maxPowerIndexCost` arbitrate between "more monsters" and
  "stronger monsters". The observed effect is "both", not proven.
- The real number of draws per floor, and therefore the real safety margin of the
  four decks without an entry at 0.
- Whether the native −3.0 on floor 3 (forest adventure) applies to every boss
  configuration or only to some.

---

*Sources: `BossSpawnPowerIndexBudgetAdjustedHardcoded.cs`,
`BossSpawnBudgetAdjustedHardcoded.cs`, `Floor2SpawnBudgetReducedHardcoded.cs`,
`Plugin.cs`, `Doriath Progressive.json`, and the decompilation of
`Assembly-CSharp.dll` (`SpawnBossAndMinions`, `CustomSpawn`,
`MonsterDeck.DrawEntry`, `MonsterDeckOverriddenRule.CreateSubDeck`,
`GetPowerIndexForCrossover`).*
