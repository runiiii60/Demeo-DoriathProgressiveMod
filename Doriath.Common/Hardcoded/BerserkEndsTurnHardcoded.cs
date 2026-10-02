// ============================================================
//  Doriath — BerserkEndsTurnHardcoded.cs
// ============================================================
//
// Keeps the Berserk effect working while fixing the "piece never ends
// its turn" lock-up. Natively, EffectSink.CheckBerserk() applies the
// permanent Berserk state (duration -1) once a piece drops below its
// BerserkBelowHealth, and Behaviour.TryEndTurnAfterAttack() then refuses
// to create the EndTurn event for any Berserk piece whose BoardPieceId
// is not in the DataKeys.EndTurnAfterAttackWithBerserk enum (SandScorpion
// is its only member) — so the piece never hands control back.
//
// A .NET enum cannot be extended at runtime, so instead the Berserk state
// is disabled for the duration of the native call and re-enabled right
// after: the native method takes its normal path and queues EndTurn, and
// the piece keeps Berserk (and everything tied to that state) afterwards.
//
// Applies to every non-hero piece actually in Berserk at call time — no
// BoardPieceId whitelist. Native Berserk defaults live in serialized Unity
// PieceConfigData, not in .NET bytecode, so an exhaustive list cannot be
// built; matching on the live effect state covers current and future
// pieces alike and is a no-op for anything never in Berserk.
//
// Patch: Prefix + Postfix on Boardgame.BoardEntities.AI.Behaviour.TryEndTurnAfterAttack


namespace DoriathMod.Hardcoded
{
    using System.Collections.Generic;
    using Boardgame.BoardEntities;
    using DataKeys;
    using HarmonyLib;

    public static class BerserkEndsTurnHardcoded
    {
        // The only native exception (enum DataKeys.EndTurnAfterAttackWithBerserk):
        // SandScorpion is already handled correctly by the game.
        private static readonly HashSet<BoardPieceId> NativelyExempt = new HashSet<BoardPieceId>
        {
            BoardPieceId.SandScorpion,
        };

        // Pieces whose Berserk we turned off for the duration of the native
        // call, to restore right after (key = networkID).
        private static readonly HashSet<int> _pendingRestore = new HashSet<int>();

        public static void Patch(Harmony harmony)
        {
            var behaviourType = AccessTools.TypeByName("Boardgame.BoardEntities.AI.Behaviour");
            if (behaviourType == null)
            {
                Plugin.Log?.LogWarning("[BerserkEndsTurnHardcoded] Boardgame.BoardEntities.AI.Behaviour not found — patch skipped, native Berserk behaviour unchanged.");
                return;
            }

            var method = AccessTools.Method(behaviourType, "TryEndTurnAfterAttack");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[BerserkEndsTurnHardcoded] TryEndTurnAfterAttack not found — patch skipped, native Berserk behaviour unchanged.");
                return;
            }

            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(BerserkEndsTurnHardcoded), nameof(TryEndTurnAfterAttack_Prefix)),
                postfix: new HarmonyMethod(typeof(BerserkEndsTurnHardcoded), nameof(TryEndTurnAfterAttack_Postfix)));

            Plugin.Log?.LogInfo(
                "[BerserkEndsTurnHardcoded] Patch applied (outside HouseRules and the JSON, invisible in the panel) — now covers EVERY non-hero piece in Berserk, except SandScorpion, which the game already handles.");
        }

        private static bool ShouldManage(Piece? thisPiece)
        {
            if (thisPiece == null) return false;
            if (thisPiece.IsPlayer()) return false; // heroes use PlayerBerserk (106) and are never hit by this bug
            if (NativelyExempt.Contains(thisPiece.boardPieceId)) return false;

            return true; // any other piece may be affected — the real filter is HasEffectState(Berserk) below
        }

        private static void TryEndTurnAfterAttack_Prefix(Piece thisPiece)
        {
            if (!ShouldManage(thisPiece)) return;
            if (!thisPiece.HasEffectState(EffectStateType.Berserk)) return;

            thisPiece.DisableEffectState(EffectStateType.Berserk);
            _pendingRestore.Add(thisPiece.networkID);
        }

        private static void TryEndTurnAfterAttack_Postfix(Piece thisPiece)
        {
            if (thisPiece == null) return;
            if (!_pendingRestore.Remove(thisPiece.networkID)) return;

            // Duration -1 = permanent, exactly as EffectSink.CheckBerserk() does
            // natively — the piece stays Berserk.
            thisPiece.EnableEffectState(EffectStateType.Berserk, -1, null);
        }
    }
}
