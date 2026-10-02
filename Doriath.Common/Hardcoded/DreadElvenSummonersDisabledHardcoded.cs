// ============================================================
//  Doriath — DreadElvenSummonersDisabledHardcoded.cs
// ============================================================
//
// Suppresses the extra Elven Summoners that Demeo's Dread mode spawns on
// top of the normal enemy decks. Without this patch an extra ElvenSummoner
// appears near the level exit on each floor (DreadLevel.FloorOne/Two/Three
// ElvenSummoners), outside of any deck, on top of the floor's intended
// population. The key holder spawn is a separate code path and is
// unaffected.
//
// Patch: Prefix (skip original) on AIDirectorController2.SpawnSpecialEnemies

namespace DoriathMod.Hardcoded
{
    using DataKeys;
    using HarmonyLib;

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

        // The original method returns void and does nothing but the spawning,
        // so skipping it entirely is safe.
        private static bool Skip() => false;
    }
}
