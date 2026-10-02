// ============================================================
//  Doriath — BardZapHitsEnemyPropsHardcoded.cs
// ============================================================
//
// Lets player electric abilities (Zap, LightningBolt, Overload) damage
// hostile props again. HouseRules' PartyDamageOverriddenRule, in its
// "electric only" mode, cancels player electric damage against every Prop
// outside a short native whitelist in order to protect friendly props —
// which also makes enemy props immune. This skips that cancellation for the
// enemy props listed below.
//
// Parameters:
//   EnemyProps  HealingBeacon, SmiteWard, SporeFungus  — props the protection is lifted for
//
// Patch: Prefix on HouseRules.Essentials.Rules.PartyDamageOverriddenRule.Damage_DealDamage_Prefix

namespace DoriathMod.Hardcoded
{
    using System.Collections.Generic;
    using DataKeys;
    using HarmonyLib;

    // ponytail: explicit list, add any other enemy prop here.
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
            Plugin.Log?.LogInfo("[BardZapHitsEnemyPropsHardcoded] Players' Zap/LightningBolt/Overload hit HealingBeacon, SmiteWard and SporeFungus again.");
        }

        // Returning false skips the HouseRules prefix; __result = true tells
        // Harmony to let DealDamage run normally.
        private static bool Prefix(Boardgame.GameplayEffects.Target target, ref bool __result)
        {
            if (target.piece == null || !EnemyProps.Contains(target.piece.boardPieceId)) return true;
            __result = true;
            return false;
        }
    }
}
