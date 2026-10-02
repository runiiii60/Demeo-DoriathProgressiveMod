// ============================================================
//  Doriath — BossSpawnPowerIndexBudgetAdjustedHardcoded.cs
// ============================================================
//
// Scales the TOTAL power index budget the director may spend on the
// ambient spawn around the boss at the start of a boss fight — more
// extra monsters than the game would normally afford. Companion to
// BossSpawnBudgetAdjustedHardcoded, which instead changes the per-monster
// cost bounds (min/maxPowerIndexCost).
//
// In AIDirectorController2.SpawnBossAndMinions(), the budget comes from
// PowerIndexPoints = dataHelper.GetDifficultPowerIndexLevel(...). That
// helper is shared with GetDifficultPowerIndexDelta() (normal ambient
// spawn), HandleSpikeLock() and SpawnKeyholder(), so a Postfix on it
// would leak into three other systems. Instead a transpiler multiplies
// the returned value at this one call site, leaving the helper itself
// untouched.
//
// Parameters:
//   Multiplier  3f  — scales the boss-fight ambient spawn budget (native 1.0)
//
// Patch: Transpiler on Boardgame.AIDirector.AIDirectorController2.SpawnBossAndMinions


namespace DoriathMod.Hardcoded
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using HarmonyLib;
    using UnityEngine;

    public static class BossSpawnPowerIndexBudgetAdjustedHardcoded
    {
        // Native game value (unmodded): 1.0.
        private const float Multiplier = 3f;

        public static void Patch(Harmony harmony)
        {
            var targetType = AccessTools.TypeByName("Boardgame.AIDirector.AIDirectorController2");
            if (targetType == null)
            {
                Plugin.Log?.LogWarning("[BossSpawnPowerIndexBudgetAdjustedHardcoded] AIDirectorController2 not found — patch skipped, nothing changed.");
                return;
            }

            var method = AccessTools.Method(targetType, "SpawnBossAndMinions");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[BossSpawnPowerIndexBudgetAdjustedHardcoded] SpawnBossAndMinions not found — patch skipped, nothing changed.");
                return;
            }

            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(BossSpawnPowerIndexBudgetAdjustedHardcoded), nameof(Transpiler)));

            Plugin.Log?.LogInfo(
                $"[BossSpawnPowerIndexBudgetAdjustedHardcoded] Patch applied (outside HouseRules and the JSON, invisible in the panel) — " +
                $"multiplier x{Multiplier} applied to the result of GetDifficultPowerIndexLevel().");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();

            int callIndex = list.FindIndex(ci =>
                ci.opcode == OpCodes.Call && ci.operand is MethodInfo mi && mi.Name == "CustomSpawn");

            if (callIndex < 7)
            {
                Plugin.Log?.LogWarning("[BossSpawnPowerIndexBudgetAdjustedHardcoded] CustomSpawn call not found in SpawnBossAndMinions — nothing applied, native value kept.");
                return list;
            }

            // Anchor: callIndex-7 = callvirt GetDifficultPowerIndexLevel(...),
            // whose int result stays on the stack until consumed as the
            // PowerIndexPoints argument of CustomSpawn. Validated by METHOD NAME,
            // not by a literal value — so this patch is unaffected by whether
            // BossSpawnBudgetAdjustedHardcoded has already rewritten the min/max
            // literals at callIndex-5/-4 of this same method.
            var piInstr = list[callIndex - 7];
            bool piOk = piInstr.opcode == OpCodes.Callvirt &&
                        piInstr.operand is MethodInfo piMethod &&
                        piMethod.Name == "GetDifficultPowerIndexLevel";

            // If a game update moves this call, do nothing rather than risk
            // corrupting the method body.
            if (!piOk)
            {
                Plugin.Log?.LogWarning(
                    $"[BossSpawnPowerIndexBudgetAdjustedHardcoded] Pattern IL attendu non trouve autour de CustomSpawn " +
                    $"(instruction a callIndex-7 = {piInstr.opcode}) — the game has probably changed, patch cancelled " +
                    "to avoid corrupting the method. Native value kept.");
                return list;
            }

            var roundToIntMethod = AccessTools.Method(typeof(Mathf), "RoundToInt", new[] { typeof(float) });

            if (roundToIntMethod == null)
            {
                Plugin.Log?.LogWarning("[BossSpawnPowerIndexBudgetAdjustedHardcoded] Mathf.RoundToInt not found — patch cancelled.");
                return list;
            }

            // Inserted right after the GetDifficultPowerIndexLevel call, i.e.
            // before the ldc.i4.1/applyZoneSaturationRule at callIndex-6. Same
            // idiom the game itself uses in HandleSpikeLock() (conv.r4 ; mul ;
            // call Mathf.RoundToInt). The multiplier is baked in as an IL literal
            // since it no longer comes from any config.
            var insertion = new[]
            {
                new CodeInstruction(OpCodes.Conv_R4),
                new CodeInstruction(OpCodes.Ldc_R4, Multiplier),
                new CodeInstruction(OpCodes.Mul),
                new CodeInstruction(OpCodes.Call, roundToIntMethod),
            };

            list.InsertRange(callIndex - 6, insertion);

            return list;
        }
    }
}
