// ============================================================
//  Doriath — BossSpawnBudgetAdjustedHardcoded.cs
// ============================================================
//
// Widens the range of extra monsters the director may spawn around the
// boss at the start of a boss fight. AIDirectorController2
// .SpawnBossAndMinions() ends with a single CustomSpawn(...) call
// (spawnType = SpawnType.Ambient) shared by every boss type, whose power
// index cost bounds are hardcoded IL literals (natively 5 and 99). They
// bound how many extra monsters — and how strong, since cost scales with
// strength — fit in the available power index budget. There is no field
// or config to mutate, hence a transpiler.
//
// Parameters:
//   MinCost   1   — minPowerIndexCost: cheapest monster eligible (native 5)
//   MaxCost  99   — maxPowerIndexCost: most expensive monster eligible (native 99)
//
// Patch: Transpiler on Boardgame.AIDirector.AIDirectorController2.SpawnBossAndMinions


namespace DoriathMod.Hardcoded
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using HarmonyLib;

    public static class BossSpawnBudgetAdjustedHardcoded
    {
        // Native game values (unmodded): minCost=5, maxCost=99.
        private const int MinCost = 1;
        private const int MaxCost = 99;

        public static void Patch(Harmony harmony)
        {
            var targetType = AccessTools.TypeByName("Boardgame.AIDirector.AIDirectorController2");
            if (targetType == null)
            {
                Plugin.Log?.LogWarning("[BossSpawnBudgetAdjustedHardcoded] AIDirectorController2 not found — patch skipped, nothing changed.");
                return;
            }

            var method = AccessTools.Method(targetType, "SpawnBossAndMinions");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[BossSpawnBudgetAdjustedHardcoded] SpawnBossAndMinions not found — patch skipped, nothing changed.");
                return;
            }

            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(BossSpawnBudgetAdjustedHardcoded), nameof(Transpiler)));

            Plugin.Log?.LogInfo(
                $"[BossSpawnBudgetAdjustedHardcoded] Patch applied (outside HouseRules and the JSON, invisible in the panel) — " +
                $"minPowerIndexCost=5 -> {MinCost}, maxPowerIndexCost=99 -> {MaxCost}.");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();

            // The insertion point is located by the CustomSpawn call, not by
            // scanning for the literals themselves: 5 and 99 are common values
            // and would match elsewhere in the method.
            int callIndex = list.FindIndex(ci =>
                ci.opcode == OpCodes.Call && ci.operand is MethodInfo mi && mi.Name == "CustomSpawn");

            if (callIndex < 5)
            {
                Plugin.Log?.LogWarning("[BossSpawnBudgetAdjustedHardcoded] CustomSpawn call not found in SpawnBossAndMinions — nothing applied, native values kept.");
                return list;
            }

            // Argument order at the call site: callIndex-5 = minPowerIndexCost
            // (ldc.i4.5), callIndex-4 = maxPowerIndexCost (ldc.i4.s 99).
            var minInstr = list[callIndex - 5];
            var maxInstr = list[callIndex - 4];

            bool minOk = minInstr.opcode == OpCodes.Ldc_I4_5;
            bool maxOk = maxInstr.opcode == OpCodes.Ldc_I4_S && maxInstr.operand is sbyte sb && sb == 99;

            // If a game update reorders the arguments or inserts code between
            // them, do nothing rather than risk corrupting the method body.
            if (!minOk || !maxOk)
            {
                Plugin.Log?.LogWarning(
                    $"[BossSpawnBudgetAdjustedHardcoded] Pattern IL attendu non trouve autour de CustomSpawn " +
                    $"(min={minInstr.opcode}, max={maxInstr.opcode}) — the game has probably changed, patch cancelled " +
                    "to avoid corrupting the method. Native values kept.");
                return list;
            }

            list[callIndex - 5] = new CodeInstruction(OpCodes.Ldc_I4, MinCost);
            list[callIndex - 4] = new CodeInstruction(OpCodes.Ldc_I4, MaxCost);

            return list;
        }
    }
}
