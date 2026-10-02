// ============================================================
//  Doriath — LevelRecapHardcoded.cs
// ============================================================
//
// Writes an end-of-level summary to the BepInEx log: how many creatures
// actually spawned (with a per-type breakdown), how many were killed, and
// the party's levels. Purely diagnostic — nothing here changes gameplay.
// It exists so spawn budgets and deck changes can be judged on observed
// numbers rather than guesses.
//
// The recap is printed when a player carrying the key interacts with the
// level exit, then the counters reset for the next floor.
//
// Patch: Postfix on Piece.CreatePiece, Prefix on
//        MotherTracker.TrackUnitDefeated, Prefix on Interactable.OnInteraction,
//        Postfix on the RearrangePlayerTurnOrder(TurnQueue) constructor

namespace DoriathMod.Hardcoded
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Boardgame;
    using Boardgame.BoardEntities;
    using Boardgame.Data;
    using Boardgame.TurnOrder;
    using DataKeys;
    using HarmonyLib;

    internal static class LevelRecapHardcoded
    {
        // Spawns for the current level, keyed by piece type.
        private static readonly Dictionary<BoardPieceId, int> _spawned = new Dictionary<BoardPieceId, int>();
        private static int _killed;
        private static int _levelIndex = 1;
        private static List<Piece> _playerPieces = new List<Piece>();

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Piece), "CreatePiece"),
                postfix: new HarmonyMethod(typeof(LevelRecapHardcoded), nameof(Piece_CreatePiece_Postfix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(MotherTracker), "TrackUnitDefeated"),
                prefix: new HarmonyMethod(typeof(LevelRecapHardcoded), nameof(TrackUnitDefeated_Prefix)));

            harmony.Patch(
                original: AccessTools.Method(
                    typeof(Interactable),
                    "OnInteraction",
                    new[] { typeof(int), typeof(IntPoint2D), typeof(GameContext), typeof(int) }),
                prefix: new HarmonyMethod(typeof(LevelRecapHardcoded), nameof(OnInteraction_Prefix)));

            harmony.Patch(
                original: AccessTools.Constructor(typeof(RearrangePlayerTurnOrder), new[] { typeof(TurnQueue) }),
                postfix: new HarmonyMethod(typeof(LevelRecapHardcoded), nameof(TurnOrder_Postfix)));

            Plugin.Log?.LogInfo(
                "[LevelRecapHardcoded] End-of-level recap active (spawns, kills, party levels).");
        }

        private static void TurnOrder_Postfix(TurnQueue turnQueue)
        {
            try
            {
                _playerPieces = turnQueue.GetPlayerPieces();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[LevelRecapHardcoded] Player list unavailable: {ex.Message}");
            }
        }

        private static void Piece_CreatePiece_Postfix(ref Piece __result)
        {
            try
            {
                if (__result == null || __result.IsPlayer()) return;

                // Count creatures only. Filtering on IsProp()/IsBot() instead lets
                // gold piles, chests, doors and level portals through and inflates
                // the total by roughly half. PieceType.Creature is also the filter
                // DoriathPointEnemyPartyScaledRule uses to decide what gets scaled,
                // so the recap counts exactly the population the scaling targets.
                if (!__result.HasPieceType(PieceType.Creature)) return;

                var id = __result.boardPieceId;
                _spawned[id] = _spawned.TryGetValue(id, out var n) ? n + 1 : 1;
            }
            catch
            {
                // A counter must never break piece creation.
            }
        }

        private static void TrackUnitDefeated_Prefix(Piece defeatedUnit, Piece attackerUnit)
        {
            try
            {
                // Same creature filter as on spawn, otherwise destroyed chests and
                // broken doors land in the kill count.
                if (defeatedUnit != null && !defeatedUnit.IsPlayer()
                    && defeatedUnit.HasPieceType(PieceType.Creature))
                {
                    _killed++;
                }
            }
            catch
            {
            }
        }

        private static void OnInteraction_Prefix(int pieceId, GameContext gameContext, IntPoint2D targetTile)
        {
            try
            {
                var interactable = gameContext.pieceAndTurnController.GetInteractableAtPosition(targetTile);
                if (interactable == null || interactable.type != Interactable.Type.LevelExit) return;

                // The exit only opens with the key; without it the player merely
                // touched the door and the level is not over.
                if (!gameContext.pieceAndTurnController.TryGetPiece(pieceId, out var piece)) return;
                if (!piece.IsPlayer() || !piece.HasEffectState(EffectStateType.Key)) return;

                LogRecap();
                Reset();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[LevelRecapHardcoded] Recap impossible : {ex.Message}");
            }
        }

        private static void LogRecap()
        {
            var total = _spawned.Values.Sum();
            var sb = new StringBuilder();
            sb.Append($"[LevelRecapHardcoded] End of level {_levelIndex} — ");
            sb.Append($"{total} creatures apparues, {_killed} tues");

            if (_playerPieces != null && _playerPieces.Count > 0)
            {
                // Displayed level = CritChance - 1 (mod convention).
                var levels = _playerPieces
                    .Where(p => p != null && p.IsPlayer())
                    .Select(p => p.GetStatMax(Stats.Type.CritChance) - 1)
                    .ToList();
                if (levels.Count > 0)
                {
                    sb.Append($", party levels {string.Join("/", levels.Select(l => l.ToString()).ToArray())}");
                    sb.Append($" (moyenne {levels.Average():0.0})");
                }
            }

            Plugin.Log?.LogInfo(sb.ToString());

            // Per-type breakdown, most numerous first: this is the line used to
            // judge a deck or a spawn budget.
            foreach (var kv in _spawned.OrderByDescending(kv => kv.Value))
            {
                Plugin.Log?.LogInfo($"[LevelRecapHardcoded]     {kv.Value,4} x {kv.Key}");
            }
        }

        private static void Reset()
        {
            _spawned.Clear();
            _killed = 0;
            _levelIndex++;
        }
    }
}
