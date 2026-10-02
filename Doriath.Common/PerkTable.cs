// ============================================================
//  Doriath — PerkTable.cs
// ============================================================
//
// Single source of truth for the stat perks granted at character levels.
// Each perk is declared once as a data row; Apply() walks the table forward
// on level-up and Revert() walks it backward on level loss, so the two
// directions cannot drift apart.
//
// Scope: STAT perks only. Abilities made free are driven elsewhere by a
// level threshold re-evaluated every turn, so they enable and disable
// themselves. Cards granted into the inventory are also handled elsewhere,
// since removing them depends on whether the card was already consumed.
//
// Parameters:
//   Table     one row per stat perk  — level, stat, delta, scope, optional hero filter
//   Casters   Bard, Warlock, Sorcerer      — classes that gain MagicBonus at level 8
//   Martials  Guardian, Hunter, Rogue, Barbarian — classes that gain Strength at level 8

namespace DoriathMod.Rules
{
    using System.Collections.Generic;
    using System.Linq;
    using Boardgame.BoardEntities;
    using DataKeys;

    internal static class PerkTable
    {
        /// <summary>Which value(s) of the stat a perk acts on.</summary>
        internal enum Scope
        {
            /// <summary>Current value only (TrySetStatBaseValue).</summary>
            Base,

            /// <summary>Maximum only (TrySetStatMaxValue).</summary>
            Max,

            /// <summary>Both, the most common case.</summary>
            BaseAndMax,
        }

        internal readonly struct StatPerk
        {
            /// <summary>Level reached (CritChance value after the level-up).</summary>
            public readonly int Level;

            /// <summary>Heroes affected. Empty = all.</summary>
            public readonly BoardPieceId[] Heroes;

            public readonly Stats.Type Stat;

            /// <summary>Change applied on level-up. Level loss applies the opposite.</summary>
            public readonly int Delta;

            public readonly Scope Scope;

            public StatPerk(int level, Stats.Type stat, int delta, Scope scope, params BoardPieceId[] heroes)
            {
                Level = level;
                Stat = stat;
                Delta = delta;
                Scope = scope;
                Heroes = heroes ?? new BoardPieceId[0];
            }

            public bool AppliesTo(BoardPieceId hero)
            {
                return Heroes.Length == 0 || Heroes.Contains(hero);
            }
        }

        // Spellcasters gain Magic where the others gain Strength (level 8).
        private static readonly BoardPieceId[] Casters =
        {
            BoardPieceId.HeroBard, BoardPieceId.HeroWarlock, BoardPieceId.HeroSorcerer,
        };

        private static readonly BoardPieceId[] Martials =
        {
            BoardPieceId.HeroGuardian, BoardPieceId.HeroHunter,
            BoardPieceId.HeroRogue, BoardPieceId.HeroBarbarian,
        };

        /// <summary>
        /// The table. One row = one stat perk. Adding a level perk happens here
        /// and nowhere else.
        ///
        /// Knockdowns: lowering DownedCounter grants one extra knockdown (it
        /// counts falls already taken) and DownedTimer rises accordingly. That
        /// is the game's own convention, not an inversion.
        /// </summary>
        private static readonly StatPerk[] Table =
        {
            // Level 3 — one extra knockdown.
            new StatPerk(3, Stats.Type.DownedCounter, -1, Scope.Base),
            new StatPerk(3, Stats.Type.DownedTimer,   +1, Scope.Base),

            // Level 4 — +2 HP, current and max.
            new StatPerk(4, Stats.Type.Health,        +2, Scope.BaseAndMax),

            // Level 7 — one extra knockdown. No HP here: the HP bonus is
            // consolidated at level 4.
            new StatPerk(7, Stats.Type.DownedCounter, -1, Scope.Base),
            new StatPerk(7, Stats.Type.DownedTimer,   +1, Scope.Base),

            // Level 8 — class-dependent attribute bonus.
            new StatPerk(8, Stats.Type.MagicBonus,    +1, Scope.BaseAndMax, BoardPieceId.HeroBard, BoardPieceId.HeroWarlock, BoardPieceId.HeroSorcerer),
            new StatPerk(8, Stats.Type.Strength,      +1, Scope.BaseAndMax, BoardPieceId.HeroGuardian, BoardPieceId.HeroHunter, BoardPieceId.HeroRogue, BoardPieceId.HeroBarbarian),
        };

        /// <summary>Applies the stat perks of the level reached.</summary>
        internal static void Apply(Piece piece, int level)
        {
            Walk(piece, level, +1);
        }

        /// <summary>Undoes the stat perks of the level lost.</summary>
        internal static void Revert(Piece piece, int level)
        {
            Walk(piece, level, -1);
        }

        private static void Walk(Piece piece, int level, int sign)
        {
            if (piece == null) return;

            var hero = piece.boardPieceId;
            foreach (var perk in Table)
            {
                if (perk.Level != level || !perk.AppliesTo(hero)) continue;
                Mutate(piece, perk.Stat, perk.Delta * sign, perk.Scope);
            }
        }

        private static void Mutate(Piece piece, Stats.Type stat, int delta, Scope scope)
        {
            var sink = piece.effectSink;

            // Health has its own accessors (float values); every other stat
            // goes through GetStat/GetStatMax.
            if (stat == Stats.Type.Health)
            {
                if (scope != Scope.Max) sink.TrySetStatBaseValue(stat, piece.GetHealth() + delta);
                if (scope != Scope.Base) sink.TrySetStatMaxValue(stat, piece.GetMaxHealth() + delta);
                return;
            }

            if (scope != Scope.Max) sink.TrySetStatBaseValue(stat, piece.GetStat(stat) + delta);
            if (scope != Scope.Base) sink.TrySetStatMaxValue(stat, piece.GetStatMax(stat) + delta);
        }

        /// <summary>
        /// Sanity check run when the ruleset loads: flags out-of-range levels
        /// and duplicate (level, hero, stat) rows, i.e. typos in the table.
        /// Anomalies are logged only, nothing is blocked.
        /// </summary>
        internal static void SelfCheck(int maxLevel)
        {
            var seen = new HashSet<string>();

            foreach (var perk in Table)
            {
                if (perk.Level < 2 || perk.Level > maxLevel)
                {
                    Plugin.Log?.LogWarning(
                        $"[PerkTable] level {perk.Level} is outside 2..{maxLevel} — this perk is never applied.");
                }

                foreach (var hero in perk.Heroes.Length == 0 ? AllHeroes() : perk.Heroes)
                {
                    var key = $"{perk.Level}|{hero}|{perk.Stat}";
                    if (!seen.Add(key))
                    {
                        Plugin.Log?.LogWarning(
                            $"[PerkTable] {perk.Stat} is modified twice at level {perk.Level} " +
                            $"for {hero} — duplicate entry in the table?");
                    }
                }
            }

            Plugin.Log?.LogInfo(
                $"[PerkTable] {Table.Length} perks de stat charges, paliers " +
                string.Join(", ", Table.Select(p => p.Level.ToString()).Distinct().ToArray()) + ".");
        }

        private static IEnumerable<BoardPieceId> AllHeroes()
        {
            return Casters.Concat(Martials);
        }
    }
}
