// ============================================================
//  Doriath (Point Progressive) — DoriathPointEnemyPartyScaledRule.cs
// ============================================================
//
// Replaces EnemyAttackScaled and EnemyHealthScaled (HouseRules.Essentials)
// with a DYNAMIC multiplier that tracks the party's AVERAGE displayed
// level (0-9, same convention as ProgressiveLevelRule.cs: displayed level
// = internal CritChance - 1). Recalculated every time an enemy is created
// (same hook point as the two native rules: Piece.CreatePiece, postfix),
// so it is fully automatic across the three maps of a run and reacts if
// the average level changes mid-run.
//
// Starting tier (party level 0) matches the native rules' values: Attack
// x1.1, Health x1.1 (so the very start of the game is unchanged), scaling
// up to a cap at average level 9 (full party) of Attack x1.4 / Health
// x1.9. The curve is LINEAR (exponent 1): each average level adds the same
// amount of scaling from start to end.
//
// IMPORTANT: EnemyAttackScaled and EnemyHealthScaled must stay DISABLED in
// the ruleset JSON (same hook point would otherwise cause double scaling).
// See Doriath (Point Progressive).json.

namespace DoriathMod.Rules
{
    using System.Collections.Generic;
    using Boardgame;
    using Boardgame.BoardEntities;
    using DataKeys;
    using HarmonyLib;
    using HouseRules.Core.Types;
    using UnityEngine;

    public sealed class DoriathPointEnemyPartyScaledRule : Rule, IConfigWritable<bool>, IPatchable, IMultiplayerSafe
    {
        // Scaling values by the party's average displayed level (linear formula
        // below, BaseXMultiplier -> MaxXMultiplier):
        //   Average level | Attack x | Health x
        //        0        |   1.10   |   1.10
        //        1        |   1.13   |   1.19
        //        2        |   1.17   |   1.28
        //        3        |   1.20   |   1.37
        //        4        |   1.23   |   1.46
        //        5        |   1.27   |   1.54
        //        6        |   1.30   |   1.63
        //        7        |   1.33   |   1.72
        //        8        |   1.37   |   1.81
        //        9        |   1.40   |   1.90
        public override string Description =>
            "Enemy attack/health scale dynamically with the party's average level";

        // ── Tiers, mirrored from EnemyAttackScaled(1.1) / EnemyHealthScaled(1.1) ──
        private const float BaseAttackMultiplier = 1.1f;
        private const float BaseHealthMultiplier = 1.1f;
        private const float MaxAttackMultiplier  = 1.4f;
        private const float MaxHealthMultiplier  = 1.9f;
        private const float MaxAverageLevel      = 9f;   // max displayed level (0-9)
        private const float CurveExponent        = 1f;   // linear curve

        private static Context? _context;
        private static bool _isActivated;

        public DoriathPointEnemyPartyScaledRule() { }
        public DoriathPointEnemyPartyScaledRule(bool value) { }

        public bool GetConfigObject() => true;

        protected override void OnActivate(Context context)
        {
            _context = context;
            _isActivated = true;
        }

        protected override void OnDeactivate(Context context) => _isActivated = false;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Piece), "CreatePiece"),
                postfix: new HarmonyMethod(
                    typeof(DoriathPointEnemyPartyScaledRule),
                    nameof(CreatePiece_EnemyScaling_Postfix)));
        }

        private static void CreatePiece_EnemyScaling_Postfix(ref Piece __result, PieceConfigData config)
        {
            if (!_isActivated) return;
            if (__result == null || __result.IsPlayer()) return;

            // Same filtering approach as the native EnemyAttackScaledRule/
            // EnemyHealthScaledRule (confirmed by decompilation): only actual enemy
            // "creatures", never the party's dog or props/pickups/explosive lamps.
            if (!config.HasPieceType(PieceType.Creature)) return;
            if (config.HasPieceType(PieceType.Canine)) return;

            float avgLevel = GetPartyAverageLevel();
            float frac = Mathf.Pow(Mathf.Clamp01(avgLevel / MaxAverageLevel), CurveExponent);

            float attackMultiplier = BaseAttackMultiplier + frac * (MaxAttackMultiplier - BaseAttackMultiplier);
            float healthMultiplier = BaseHealthMultiplier + frac * (MaxHealthMultiplier - BaseHealthMultiplier);

            if (config.AttackDamage >= 1)
            {
                int newAttack = Mathf.RoundToInt(config.AttackDamage * attackMultiplier);
                __result.effectSink.TrySetStatBaseValue(Stats.Type.AttackDamage, newAttack);
            }

            int newHealth = Mathf.RoundToInt(__result.GetMaxHealth(0) * healthMultiplier);
            __result.effectSink.TrySetStatMaxValue(Stats.Type.Health, newHealth);
            __result.effectSink.TrySetStatBaseValue(Stats.Type.Health, newHealth);
        }

        // Average of the DISPLAYED levels (0-9) of the heroes currently in the
        // game. Recalculated on every call (i.e. every time an enemy is created),
        // so it tracks progression in real time with no per-map configuration
        // needed.
        private static float GetPartyAverageLevel()
        {
            try
            {
                var gameContext = Traverse.Create(typeof(GameHub)).Field<GameContext>("gameContext").Value;
                if (gameContext?.pieceAndTurnController == null) return 0f;

                var piecesField = Traverse.Create(gameContext.pieceAndTurnController)
                    .Field<Dictionary<int, Piece>>("pieces");
                if (piecesField.Value == null) return 0f;

                int totalLevel = 0;
                int count = 0;
                foreach (var kvp in piecesField.Value)
                {
                    if (!kvp.Value.IsPlayer()) continue;
                    int displayLevel = kvp.Value.GetStatMax(Stats.Type.CritChance) - 1;
                    if (displayLevel < 0) displayLevel = 0;
                    totalLevel += displayLevel;
                    count++;
                }

                return count > 0 ? (float)totalLevel / count : 0f;
            }
            catch
            {
                return 0f;
            }
        }
    }
}
