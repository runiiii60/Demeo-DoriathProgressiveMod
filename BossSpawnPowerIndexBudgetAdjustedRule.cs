// ============================================================
//  Doriath (PROGRESSIVE) — BossSpawnPowerIndexBudgetAdjustedRule.cs
// ============================================================
//
// Complements BossSpawnBudgetAdjustedRule.cs (which makes moddable the
// power-index COST of individual monsters eligible for the boss's ambient
// spawn — minPowerIndexCost/maxPowerIndexCost). This rule modifies
// something different: the TOTAL power-index BUDGET (PowerIndexPoints)
// that the director is allowed to spend on this ambient spawn around the
// boss.
//
// Found via targeted decompilation: in
// Boardgame.AIDirector.AIDirectorController2.SpawnBossAndMinions(), just
// before the call to CustomSpawn(...), the game computes: PowerIndexPoints
// = dataHelper.GetDifficultPowerIndexLevel(currentLevelIndex, ...)
//
// AIDirectorDataHelper.GetDifficultPowerIndexLevel(...) (returns an int)
// is NOT specific to the boss fight: it is also called by
// GetDifficultPowerIndexDelta() ("normal" ambient spawn budget during
// exploration), HandleSpikeLock() (triggering difficulty spikes) and
// SpawnKeyholder() (spawning key guardians). Patching this method directly
// (a plain Postfix) would therefore have unwanted, untested side effects
// on those three other systems.
//
// To stay STRICTLY isolated to the boss fight, this patch also uses a
// Transpiler on SpawnBossAndMinions (like
// BossSpawnBudgetAdjustedHardcoded), but positioned differently: instead
// of replacing literals, it locates the precise call to
// GetDifficultPowerIndexLevel() that immediately precedes the call to
// CustomSpawn(...) in THIS method, and inserts right after it a multiplier
// (conv.r4 ; ldc.r4 <multiplier> ; mul ; call Mathf.RoundToInt) — the same
// idiom the game already uses natively in HandleSpikeLock() for a similar
// calculation. The resulting int then continues normally as CustomSpawn's
// PowerIndexPoints argument, without touching
// GetDifficultPowerIndexLevel() itself — so there is no effect on
// HandleSpikeLock/SpawnKeyholder/normal ambient spawn.
//
// The CustomSpawn call is located by name (like the other patch), but the
// position is validated by checking that the instruction at callIndex-7 is
// indeed a callvirt to GetDifficultPowerIndexLevel (verified by METHOD
// NAME, not literal value) — so it is insensitive to whatever
// BossSpawnBudgetAdjustedHardcoded does to the min/max literals
// (callIndex-5/-4), whether or not it has already run on this same method.
//
// WARNING - fragility specific to transpilers, to be tested in-game before
// treating this as reliable: if a game update changes this call (argument
// order, code inserted between the two), the patch WILL DO NOTHING
// (warning log + native value kept) rather than risk corrupting the
// method.
//
// Native multiplier (= unchanged behavior): 1.0.
//
// HARDCODED: taken out of the Rule/JSON system — multiplier fixed at 3.84x
// (more intense than native behavior), no longer appearing or configurable
// in Panel 1 (same pattern as AbilityMayNotTargetSelfHardcoded /
// AbilityNoAllyDamageHardcoded / BerserkEndsTurnHardcoded /
// BossSpawnBudgetAdjustedHardcoded — see Plugin.Awake()).

namespace DoriathMod.Rules
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using HarmonyLib;
    using UnityEngine;

    public static class BossSpawnPowerIndexBudgetAdjustedHardcoded
    {
        // Fixed value (see class doc comment above). Native game value
        // (without the mod): 1.0.
        private const float Multiplier = 3f;

        public static void Patch(Harmony harmony)
        {
            var targetType = AccessTools.TypeByName("Boardgame.AIDirector.AIDirectorController2");
            if (targetType == null)
            {
                Plugin.Log?.LogWarning("[BossSpawnPowerIndexBudgetAdjustedHardcoded] AIDirectorController2 introuvable — patch ignore, aucune modification.");
                return;
            }

            var method = AccessTools.Method(targetType, "SpawnBossAndMinions");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[BossSpawnPowerIndexBudgetAdjustedHardcoded] SpawnBossAndMinions introuvable — patch ignore, aucune modification.");
                return;
            }

            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(BossSpawnPowerIndexBudgetAdjustedHardcoded), nameof(Transpiler)));

            Plugin.Log?.LogInfo(
                $"[BossSpawnPowerIndexBudgetAdjustedHardcoded] Patch applique (hors HouseRules/JSON, invisible du panneau) — " +
                $"multiplicateur x{Multiplier} applique au resultat de GetDifficultPowerIndexLevel().");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();

            int callIndex = list.FindIndex(ci =>
                ci.opcode == OpCodes.Call && ci.operand is MethodInfo mi && mi.Name == "CustomSpawn");

            if (callIndex < 7)
            {
                Plugin.Log?.LogWarning("[BossSpawnPowerIndexBudgetAdjustedHardcoded] Appel CustomSpawn introuvable dans SpawnBossAndMinions — aucune modification appliquee, valeur native conservee.");
                return list;
            }

            // Exact position confirmed by decompilation (see class doc comment above):
            // callIndex-7 = callvirt GetDifficultPowerIndexLevel(...), whose result
            // (int) stays on the stack until consumed as CustomSpawn's
            // PowerIndexPoints argument.
            var piInstr = list[callIndex - 7];
            bool piOk = piInstr.opcode == OpCodes.Callvirt &&
                        piInstr.operand is MethodInfo piMethod &&
                        piMethod.Name == "GetDifficultPowerIndexLevel";

            if (!piOk)
            {
                Plugin.Log?.LogWarning(
                    $"[BossSpawnPowerIndexBudgetAdjustedHardcoded] Pattern IL attendu non trouve autour de CustomSpawn " +
                    $"(instruction a callIndex-7 = {piInstr.opcode}) — le jeu a probablement change, patch annule " +
                    "pour eviter de corrompre la methode. Valeur native conservee.");
                return list;
            }

            var roundToIntMethod = AccessTools.Method(typeof(Mathf), "RoundToInt", new[] { typeof(float) });

            if (roundToIntMethod == null)
            {
                Plugin.Log?.LogWarning("[BossSpawnPowerIndexBudgetAdjustedHardcoded] Mathf.RoundToInt introuvable — patch annule.");
                return list;
            }

            // Inserted right after the call to GetDifficultPowerIndexLevel (index
            // callIndex-7), so BEFORE the ldc.i4.1/applyZoneSaturationRule that
            // immediately follows (callIndex-6). Same idiom the game already uses
            // natively in HandleSpikeLock() for a similar calculation
            // (conv.r4 ; mul ; call Mathf.RoundToInt). Multiplier inserted as a fixed
            // IL literal (ldc.r4) since it no longer depends on any config/constructor
            // (hardcoded rule).
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
