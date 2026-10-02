// ============================================================
//  Doriath (Point Progressive) — Rulesets/ProgressiveRuleset.cs
// ============================================================

namespace DoriathMod.Rulesets
{
    using System.Collections.Generic;
    using DataKeys;
    using HouseRules.Core.Types;
    using DoriathMod.Config;
    using DoriathMod.Rules;

    public static class ProgressiveRuleset
    {
        public static Ruleset Create()
        {
            return Ruleset.NewInstance(
                name:        ProgressiveConfig.ModName,
                displayname: ProgressiveConfig.ModName,
                description: ProgressiveConfig.ModDescription,
                longdesc:    ProgressiveConfig.ModDescription,
                rules:       BuildRules()
            );
        }

        private static List<Rule> BuildRules()
        {
            return new List<Rule>
            {
                // ── XP / Level-up progression ─────────────────────────
                new DoriathPointLevelUpRule(),

                // ── Level loss on revive ─────────────────────────
                new DoriathMod.Rules.DoriathPointLevelLossRule(),

                // ── Effect on crit on last action (level 3+) ────────
                new DoriathMod.Rules.DoriathPointFreeThingsOnLastMoveAndCritRule(new List<BoardPieceId>
                {
                    BoardPieceId.HeroBard,
                    BoardPieceId.HeroGuardian,
                    BoardPieceId.HeroRogue,
                    BoardPieceId.HeroHunter,
                    BoardPieceId.HeroSorcerer,
                    BoardPieceId.HeroWarlock,
                    BoardPieceId.HeroBarbarian,
                }),

                // ── Free card on crit (level 10+) ──────────────
                new DoriathMod.Rules.DoriathPointFreeRevolutionsAbilityOnCritRule(new Dictionary<BoardPieceId, AbilityKey>
                {
                    { BoardPieceId.HeroHunter,    AbilityKey.Corrupt               },
                    { BoardPieceId.HeroSorcerer,  AbilityKey.DeathBeam             },
                    { BoardPieceId.HeroBard,      AbilityKey.AcidSpit              },
                    { BoardPieceId.HeroBarbarian, AbilityKey.MarkOfVerga           },
                    { BoardPieceId.HeroWarlock,   AbilityKey.MinionCharge          },
                    { BoardPieceId.HeroGuardian,  AbilityKey.EnemyInvulnerability  },
                    { BoardPieceId.HeroRogue,     AbilityKey.Blink                 },
                }),

                // ── Dynamic enemy scaling (Attack/Health) based on the party's
                // average level — replaces EnemyAttackScaled/EnemyHealthScaled ──
                new DoriathMod.Rules.DoriathPointEnemyPartyScaledRule(),

                // ── IceExplosion (Warlock) no longer hits its own caster —
                // applied as hardcode from Plugin.Awake(), outside JSON, invisible
                // from Panel 1 (see AbilityMayNotTargetSelfRule.cs) ─────────────

                // ── Spawn budget around the boss (min/max power index of eligible
                // monsters) AND total power index budget spent for that spawn —
                // applied as hardcode from Plugin.Awake(), outside JSON, invisible
                // from Panel 1 (see BossSpawnBudgetAdjustedRule.cs /
                // BossSpawnPowerIndexBudgetAdjustedRule.cs) ───────────────────

                // ── AI Director budget / percentages / counters, tuned for a
                // more intense enemy spawn (see DoriathPointAIDirectorConfigRule.cs for the
                // native values in detail) ──
                new DoriathMod.Rules.DoriathPointAIDirectorConfigRule(new Dictionary<string, double>
                {
                    { "EasySpawnBudgetMultiplier",   0.84 },
                    { "NormalSpawnBudgetMultiplier", 1.92 },
                    { "ActivePowerIndexInLevelSoftRoof", 420 },
                    { "MaxNumberOfUnitsOnBoardHardCap",  108 },
                    { "AllowedSpawnZoneSaturation",  0.9 },
                    { "SpikeTrigger_MediumMap_ZoneDiscoveryPercentage", 0.5 },
                    { "SpikeTrigger_LargeMap_ZoneDiscoveryPercentage",  0.6 },
                    { "MaxNumberOfEasyUnitsPerZone",   9 },
                    { "MaxNumberOfMediumUnitsPerZone", 6 },
                    { "MaxNumberOfStrongUnitsPerZone", 4 },
                    { "Zone1_Large_PercentCoverage",  0.25 },
                    { "Zone1_Medium_PercentCoverage", 0.32 },
                    { "Zone1_Small_PercentCoverage",  0.32 },
                    { "Zone2_Large_PercentCoverage",  0.46 },
                    { "Zone2_Medium_PercentCoverage", 0.44 },
                    { "Zone2_Small_PercentCoverage",  0.42 },
                    { "Zone2_OuterRing_Large_PercentCoverage",  0.0 },
                    { "Zone2_OuterRing_Medium_PercentCoverage", 0.0 },
                    { "Zone2_OuterRing_Small_PercentCoverage",  0.0 },
                }),

                // ── Native bug "boss stays permanently Berserk = never ends
                // its turn" — fixed as hardcode from Plugin.Awake(), outside
                // JSON, invisible from Panel 1, while keeping the Berserk
                // effect (see BerserkEndsTurnRule.cs) ─────────────────────
            };
        }
    }
}
