// ============================================================
//  Doriath (PROGRESSIVE) — BossSpawnBudgetAdjustedRule.cs
// ============================================================
//
// Makes moddable the "power index" budget the game spends to spawn extra
// monsters around the boss at the start of a boss fight. Found via
// targeted decompilation of
// Boardgame.AIDirector.AIDirectorController2.SpawnBossAndMinions(): this
// method handles several boss fight types (Forest, ElvenQueen, RatKing...)
// with fixed piece placements, THEN calls, once and for ALL boss types
// combined, a CustomSpawn(...) with spawnType=SpawnType.Ambient and
// hardcoded power-index cost bounds: minPowerIndexCost=5,
// maxPowerIndexCost=99. These two values limit how many extra monsters
// (and how "strong", based on their cost) the director can add around the
// boss with the power index budget available at that point in the run.
//
// These two values are LITERALS hardcoded in the method's IL body — there
// is no field/config to mutate directly. The only way to change them is a
// Harmony TRANSPILER patch (rewriting IL instructions when the method is
// loaded).
//
// This patch locates the call to CustomSpawn(...) then checks the 2
// instructions right before it (exact position confirmed by decompilation:
// minPowerIndexCost is the 5th argument before the call, maxPowerIndexCost
// the 4th). If a game update changes this call (argument order, code
// inserted between the two), the patch will DO NOTHING (warning log +
// native values kept) rather than risk corrupting the method.
//
// HARDCODED: taken out of the Rule/JSON system — fixed values
// (minPowerIndexCost=1, maxPowerIndexCost=99), no longer appearing or
// configurable in Panel 1 (same pattern as
// AbilityMayNotTargetSelfHardcoded / AbilityNoAllyDamageHardcoded /
// BerserkEndsTurnHardcoded — see Plugin.Awake()). Native game values (i.e.
// original behavior, without any mod): [5, 99].

namespace DoriathMod.Rules
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using DataKeys;
    using HarmonyLib;

    public static class BossSpawnBudgetAdjustedHardcoded
    {
        // Fixed values (last active ruleset setting before hardcoding).
        // Native game values (no mod): minCost=5, maxCost=99.
        private const int MinCost = 1;
        private const int MaxCost = 99;

        public static void Patch(Harmony harmony)
        {
            var targetType = AccessTools.TypeByName("Boardgame.AIDirector.AIDirectorController2");
            if (targetType == null)
            {
                Plugin.Log?.LogWarning("[BossSpawnBudgetAdjustedHardcoded] AIDirectorController2 introuvable — patch ignore, aucune modification.");
                return;
            }

            var method = AccessTools.Method(targetType, "SpawnBossAndMinions");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[BossSpawnBudgetAdjustedHardcoded] SpawnBossAndMinions introuvable — patch ignore, aucune modification.");
                return;
            }

            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(BossSpawnBudgetAdjustedHardcoded), nameof(Transpiler)));

            Plugin.Log?.LogInfo(
                $"[BossSpawnBudgetAdjustedHardcoded] Patch applique (hors HouseRules/JSON, invisible du panneau) — " +
                $"minPowerIndexCost=5 -> {MinCost}, maxPowerIndexCost=99 -> {MaxCost}.");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();

            int callIndex = list.FindIndex(ci =>
                ci.opcode == OpCodes.Call && ci.operand is MethodInfo mi && mi.Name == "CustomSpawn");

            if (callIndex < 5)
            {
                Plugin.Log?.LogWarning("[BossSpawnBudgetAdjustedHardcoded] Appel CustomSpawn introuvable dans SpawnBossAndMinions — aucune modification appliquee, valeurs natives conservees.");
                return list;
            }

            // Exact position confirmed by decompilation (see the class-level doc
            // comment above): callIndex-5 = minPowerIndexCost (ldc.i4.5),
            // callIndex-4 = maxPowerIndexCost (ldc.i4.s 99).
            var minInstr = list[callIndex - 5];
            var maxInstr = list[callIndex - 4];

            bool minOk = minInstr.opcode == OpCodes.Ldc_I4_5;
            bool maxOk = maxInstr.opcode == OpCodes.Ldc_I4_S && maxInstr.operand is sbyte sb && sb == 99;

            if (!minOk || !maxOk)
            {
                Plugin.Log?.LogWarning(
                    $"[BossSpawnBudgetAdjustedHardcoded] Pattern IL attendu non trouve autour de CustomSpawn " +
                    $"(min={minInstr.opcode}, max={maxInstr.opcode}) — le jeu a probablement change, patch annule " +
                    "pour eviter de corrompre la methode. Valeurs natives conservees.");
                return list;
            }

            list[callIndex - 5] = new CodeInstruction(OpCodes.Ldc_I4, MinCost);
            list[callIndex - 4] = new CodeInstruction(OpCodes.Ldc_I4, MaxCost);

            return list;
        }
    }

    // Hotfix v1.0.1 — removes the extra ElvenSummoners spawned by the native
    // Dread mode. AIDirectorController2.PrepareLoadNewLevel() calls
    // SpawnSpecialEnemies(), which spawns
    // DreadLevel.FloorOne/Two/ThreeElvenSummoners ElvenSummoners near the
    // exit, outside of any monster deck. That method does nothing else, so it
    // is skipped: only the KeyHolder ElvenSummoner remains.
    public static class DreadElvenSummonersDisabledHardcoded
    {
        public static void Patch(Harmony harmony)
        {
            var method = AccessTools.Method(
                AccessTools.TypeByName("Boardgame.AIDirector.AIDirectorController2"), "SpawnSpecialEnemies");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[DreadElvenSummonersDisabledHardcoded] SpawnSpecialEnemies not found — patch skipped.");
                return;
            }
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(DreadElvenSummonersDisabledHardcoded), nameof(Skip)));
            Plugin.Log?.LogInfo("[DreadElvenSummonersDisabledHardcoded] Dread-mode bonus ElvenSummoners disabled (only the KeyHolder remains).");
        }

        private static bool Skip() => false;
    }

    // Hotfix v1.0.1 — removes the hero elemental immunities of HouseRules'
    // "Revolutions" mode. PartyDamageOverriddenRule.OnActivate turns its
    // static "revolutions" flag on as soon as a rule of the ruleset has
    // "Revolutions" in its name (here FreeRevolutionsAbilityOnCrit). In that
    // mode, damage from any non-Boss attacker is cancelled depending on the
    // hero: Sorcerer/Electricity, Guardian/Fire, Hunter/Ice,
    // Barbarian/Acid+Petrify, Warlock/untagged damage (which also gave the
    // Warlock +1 AP), Warlock minion, Verochka/Ice. The flag is reset to false
    // right after activation.
    public static class RevolutionsElementImmunityDisabledHardcoded
    {
        public static void Patch(Harmony harmony)
        {
            harmony.Patch(
                AccessTools.Method(typeof(HouseRules.Essentials.Rules.PartyDamageOverriddenRule), "OnActivate"),
                postfix: new HarmonyMethod(typeof(RevolutionsElementImmunityDisabledHardcoded), nameof(Postfix)));
            Plugin.Log?.LogInfo("[RevolutionsElementImmunityDisabledHardcoded] Hero elemental immunities (PartyDamageOverridden Revolutions mode) disabled.");
        }

        private static void Postfix() =>
            Traverse.Create(typeof(HouseRules.Essentials.Rules.PartyDamageOverriddenRule)).Field("revolutions").SetValue(false);
    }

    // Hotfix v1.0.1 — the Bard's Zap dealt no damage to HealingBeacon /
    // SmiteWard / SporeFungus.
    // PartyDamageOverriddenRule.Damage_DealDamage_Prefix (Config true =
    // "electric only" mode) cancels player Zap/LightningBolt/Overload damage
    // on every Prop (except Lamp/SandPile/Corruption/EnemyTurret/RootVine) to
    // protect allied props — which also caught these enemy props. That
    // HouseRules prefix is skipped for the listed enemy props.
    public static class BardZapHitsEnemyPropsHardcoded
    {
        private static readonly HashSet<BoardPieceId> EnemyProps = new HashSet<BoardPieceId>
        {
            BoardPieceId.HealingBeacon,
            BoardPieceId.SmiteWard,
            BoardPieceId.SporeFungus,
        };

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(
                AccessTools.Method(typeof(HouseRules.Essentials.Rules.PartyDamageOverriddenRule), "Damage_DealDamage_Prefix"),
                prefix: new HarmonyMethod(typeof(BardZapHitsEnemyPropsHardcoded), nameof(Prefix)));
            Plugin.Log?.LogInfo("[BardZapHitsEnemyPropsHardcoded] Player Zap/LightningBolt/Overload damage HealingBeacon, SmiteWard and SporeFungus again.");
        }

        // Returning false skips the HouseRules prefix; __result = true tells Harmony to
        // run DealDamage normally. Target is a struct (no null-conditional).
        private static bool Prefix(Boardgame.GameplayEffects.Target target, ref bool __result)
        {
            if (target.piece == null || !EnemyProps.Contains(target.piece.boardPieceId)) return true;
            __result = true;
            return false;
        }
    }

    // Hotfix v1.0.2 — the game froze when a lamp exploded inside gas (e.g. the
    // Barbarian's ScrollTsunami pushing an oil lamp into gas). The game's
    // telemetry (MotherTracker.TrackDamageDealt) reads the attacker's team; a
    // lamp is a prop with Team.None, and TeamData.GetOtherTeam(Team.None)
    // throws an ArgumentException. Inside the gas-ignition coroutine
    // (IgniteGasCoroutine) that exception kills the coroutine, so the sequence
    // never ends and the game hangs. A Harmony finalizer swallows the
    // exception: it is pure telemetry (metrics submission), with no gameplay
    // effect.
    public static class TelemetryDamageCrashGuardHardcoded
    {
        public static void Patch(Harmony harmony)
        {
            var method = AccessTools.Method(AccessTools.TypeByName("MotherTracker"), "TrackDamageDealt");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[TelemetryDamageCrashGuardHardcoded] MotherTracker.TrackDamageDealt not found — patch skipped.");
                return;
            }

            harmony.Patch(method, finalizer: new HarmonyMethod(typeof(TelemetryDamageCrashGuardHardcoded), nameof(Finalizer)));
            Plugin.Log?.LogInfo("[TelemetryDamageCrashGuardHardcoded] Damage telemetry exceptions neutralized (freeze when a lamp explodes in gas).");
        }

        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
                Plugin.Log?.LogWarning($"[TelemetryDamageCrashGuardHardcoded] Exception swallowed in MotherTracker.TrackDamageDealt: {__exception.GetType().Name} — {__exception.Message}");

            return null;
        }
    }

    // Hotfix v1.0.2 — 15% fewer enemies on floor 2 only. No AIDirectorConfig
    // field is per floor (they are all global), so the spawn budget
    // calculation itself is patched. AIDirectorDataHelper exposes two "deltas"
    // (available budget = target level minus the enemy power already on the
    // board): GetAmbientPowerIndexDelta (normal spawns while exploring) and
    // GetDifficultPowerIndexDelta (difficulty spikes). Both return an int. The
    // result is multiplied only when it is POSITIVE (a negative delta means
    // "too many enemies already present": shrinking it would allow even more).
    // Floors 1 and 3 and the boss fight budget
    // (BossSpawnPowerIndexBudgetAdjustedHardcoded, a different path) are
    // untouched.
    public static class Floor2SpawnBudgetReducedHardcoded
    {
        private const float Multiplier = 0.85f;   // -15 %
        private const int TargetFloorIndex = 2;

        public static void Patch(Harmony harmony)
        {
            var type = AccessTools.TypeByName("Boardgame.AIDirector.AIDirectorDataHelper");
            var postfix = new HarmonyMethod(typeof(Floor2SpawnBudgetReducedHardcoded), nameof(Postfix));
            var patched = 0;

            foreach (var name in new[] { "GetAmbientPowerIndexDelta", "GetDifficultPowerIndexDelta" })
            {
                var method = AccessTools.Method(type, name);
                if (method == null)
                {
                    Plugin.Log?.LogWarning($"[Floor2SpawnBudgetReducedHardcoded] {name} not found — not patched.");
                    continue;
                }

                harmony.Patch(method, postfix: postfix);
                patched++;
            }

            if (patched > 0)
                Plugin.Log?.LogInfo($"[Floor2SpawnBudgetReducedHardcoded] Floor {TargetFloorIndex} spawn budget multiplied by {Multiplier} ({patched} method(s) patched) — other floors unchanged.");
        }

        private static void Postfix(object __instance, ref int __result)
        {
            if (__result <= 0) return;

            var floor = Traverse.Create(__instance).Method("GetCurrentPlayableFloorIndex").GetValue<int>();
            if (floor != TargetFloorIndex) return;

            __result = (int)(__result * Multiplier);
        }
    }
}
