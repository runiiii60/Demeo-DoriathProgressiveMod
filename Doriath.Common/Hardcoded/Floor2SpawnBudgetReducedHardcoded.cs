// ============================================================
//  Doriath — Floor2SpawnBudgetReducedHardcoded.cs
// ============================================================
//
// Reduces the number of enemies the AI Director is allowed to spawn on
// floor 2 only. AIDirectorConfig has no per-floor setting — all of its
// fields are global — so the spawn budget is scaled at the point where it
// is computed. Other floors keep vanilla behaviour, and the boss fight
// budget goes through a different path
// (BossSpawnPowerIndexBudgetAdjustedHardcoded).
//
// Parameters:
//   Multiplier        0.85f  — scale applied to floor 2's spawn budget
//   TargetFloorIndex  2      — the only floor affected
//
// Patch: Postfix on AIDirectorDataHelper.GetAmbientPowerIndexDelta and
//        GetDifficultPowerIndexDelta

namespace DoriathMod.Hardcoded
{
    using HarmonyLib;

    public static class Floor2SpawnBudgetReducedHardcoded
    {
        private const float Multiplier = 0.85f;   // -15%
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
                Plugin.Log?.LogInfo($"[Floor2SpawnBudgetReducedHardcoded] Spawn budget for floor {TargetFloorIndex} multiplied by {Multiplier} ({patched} method(s) patched) — other floors unchanged.");
        }

        private static void Postfix(object __instance, ref int __result)
        {
            // The delta is "target power level minus enemy power already on the
            // board". A negative delta means there are already too many enemies,
            // and shrinking it would allow MORE spawns, so only scale positives.
            if (__result <= 0) return;

            var floor = Traverse.Create(__instance).Method("GetCurrentPlayableFloorIndex").GetValue<int>();
            if (floor != TargetFloorIndex) return;

            __result = (int)(__result * Multiplier);
        }
    }
}
