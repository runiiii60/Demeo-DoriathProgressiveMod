// ============================================================
//  Doriath — BerserkExtraActionHardcoded.cs
// ============================================================
//
// Gives Berserk pieces one extra action point per turn, on top of the
// normal cap. Piece.RestoreActionPoints() (called at the start of a
// piece's turn to compute its action points) already grants
// numActionPoints++ when EffectStateType.ExtraAction (75) is active, so
// the state is enabled just before the native computation and removed
// right after — never left active between turns. An ExtraAction that was
// already active for some other reason is left untouched.
//
// Companion to BerserkEndsTurnHardcoded: that one stops the endless turn,
// this one restores a bounded amount of the power Berserk is meant to
// grant. The two use different states (Berserk=36 vs ExtraAction=75) and
// the same scope — any non-hero piece in Berserk, except SandScorpion,
// which the game already handles natively.
//
// Patch: Prefix + Postfix on Boardgame.BoardEntities.Piece.RestoreActionPoints

namespace DoriathMod.Hardcoded
{
    using System.Collections.Generic;
    using Boardgame.BoardEntities;
    using DataKeys;
    using HarmonyLib;

    public static class BerserkExtraActionHardcoded
    {
        private static readonly HashSet<BoardPieceId> NativelyExempt = new HashSet<BoardPieceId>
        {
            BoardPieceId.SandScorpion,
        };

        // Pieces for which we enabled ExtraAction ourselves this turn
        // (key = networkID), to remove in the Postfix.
        private static readonly HashSet<int> _pendingRemove = new HashSet<int>();

        public static void Patch(Harmony harmony)
        {
            var pieceType = AccessTools.TypeByName("Boardgame.BoardEntities.Piece");
            if (pieceType == null)
            {
                Plugin.Log?.LogWarning("[BerserkExtraActionHardcoded] Boardgame.BoardEntities.Piece not found — patch skipped, no extra action for Berserk.");
                return;
            }

            var method = AccessTools.Method(pieceType, "RestoreActionPoints");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[BerserkExtraActionHardcoded] RestoreActionPoints not found — patch skipped, no extra action for Berserk.");
                return;
            }

            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(BerserkExtraActionHardcoded), nameof(RestoreActionPoints_Prefix)),
                postfix: new HarmonyMethod(typeof(BerserkExtraActionHardcoded), nameof(RestoreActionPoints_Postfix)));

            Plugin.Log?.LogInfo(
                "[BerserkExtraActionHardcoded] Patch applied (outside HouseRules and the JSON, invisible in the panel) — " +
                "+1 ActionPoint per turn (through the native EffectStateType.ExtraAction) for every non-hero piece in Berserk, except SandScorpion.");
        }

        private static bool ShouldManage(Piece? thisPiece)
        {
            if (thisPiece == null) return false;
            if (thisPiece.IsPlayer()) return false;
            if (NativelyExempt.Contains(thisPiece.boardPieceId)) return false;

            return true;
        }

        private static void RestoreActionPoints_Prefix(Piece __instance)
        {
            if (!ShouldManage(__instance)) return;
            if (!__instance.HasEffectState(EffectStateType.Berserk)) return;
            // Already active natively for another reason: leave it alone, so we
            // neither duplicate it nor strip it in the Postfix.
            if (__instance.HasEffectState(EffectStateType.ExtraAction)) return;

            __instance.EnableEffectState(EffectStateType.ExtraAction, 1, null);
            _pendingRemove.Add(__instance.networkID);
        }

        private static void RestoreActionPoints_Postfix(Piece __instance)
        {
            if (__instance == null) return;
            if (!_pendingRemove.Remove(__instance.networkID)) return;

            __instance.DisableEffectState(EffectStateType.ExtraAction);
        }
    }
}
