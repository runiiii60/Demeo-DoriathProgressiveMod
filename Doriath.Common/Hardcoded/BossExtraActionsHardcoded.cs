// ============================================================
//  Doriath — BossExtraActionsHardcoded.cs
// ============================================================
//
// Stops MotherCy, ElvenSummoner and RootLord from ending their turn right
// after their first ability: as long as they still have action points
// left, the turn keeps going, so the AP pool decides when the turn ends.
//
// Natively, these bosses end up calling Behaviour.TryEndTurnAfterAttack()
// as soon as an ability is queued, without looking at remaining AP —
// MotherCyBossBehaviour does it directly from its main actions
// (TryUseRain, TryUseElectricity, TryUseMeleeAttack), RootLord only from
// its last-resort melee branch, and ElvenSummoner through the generic
// RangedAttackBehaviour ("RangedSpellCaster") it falls back to. Whichever
// path is taken, TryEndTurnAfterAttack is the single bottleneck that
// queues CreateEndTurn, so patching it once covers all of them.
//
// Safety: when the piece is out of AP the original method runs as before
// (and the engine would force the turn to end anyway), and the native
// "no plan to execute" fallback — CreatePlan() returning null ends the
// turn immediately — is untouched, so no infinite turn is possible.
//
// To cover more pieces later, add their BoardPieceId to TargetPieces.
//
// Patch: Prefix on Boardgame.BoardEntities.AI.Behaviour.TryEndTurnAfterAttack

namespace DoriathMod.Hardcoded
{
    using System.Collections.Generic;
    using Boardgame.BoardEntities;
    using DataKeys;
    using HarmonyLib;

    public static class BossExtraActionsHardcoded
    {
        private static readonly HashSet<BoardPieceId> TargetPieces = new HashSet<BoardPieceId>
        {
            BoardPieceId.MotherCy,
            BoardPieceId.ElvenSummoner,
            BoardPieceId.RootLord,
        };

        public static void Patch(Harmony harmony)
        {
            var behaviourType = AccessTools.TypeByName("Boardgame.BoardEntities.AI.Behaviour");
            if (behaviourType == null)
            {
                Plugin.Log?.LogWarning("[BossExtraActionsHardcoded] Boardgame.BoardEntities.AI.Behaviour not found — patch skipped, MotherCy/ElvenSummoner/RootLord keep their native behaviour.");
                return;
            }

            var method = AccessTools.Method(behaviourType, "TryEndTurnAfterAttack");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[BossExtraActionsHardcoded] TryEndTurnAfterAttack not found — patch skipped, MotherCy/ElvenSummoner/RootLord keep their native behaviour.");
                return;
            }

            // Same method as the one patched by BerserkEndsTurnHardcoded. The two
            // do not collide: Harmony runs every registered Prefix, the original
            // is skipped as soon as ONE Prefix returns false, and all Postfixes
            // run either way.
            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(BossExtraActionsHardcoded), nameof(TryEndTurnAfterAttack_Prefix)));

            Plugin.Log?.LogInfo(
                "[BossExtraActionsHardcoded] Patch applied (outside HouseRules and the JSON, invisible in the panel) — MotherCy/ElvenSummoner/RootLord no longer end their turn automatically after an ability while they still have AP left.");
        }

        // Returns false to skip the original TryEndTurnAfterAttack entirely (no
        // CreateEndTurn queued) for a targeted piece that still has AP left.
        // Returns true — native behaviour unchanged — in every other case.
        private static bool TryEndTurnAfterAttack_Prefix(Piece thisPiece)
        {
            if (thisPiece == null) return true;
            if (!TargetPieces.Contains(thisPiece.boardPieceId)) return true;
            if (thisPiece.GetActionPoints() > 0) return false;

            return true;
        }
    }
}
