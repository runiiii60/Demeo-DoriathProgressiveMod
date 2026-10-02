namespace DoriathMod.Rules
{
    using System.Collections.Generic;
    using Boardgame;
    using Boardgame.BoardEntities;
    using Boardgame.BoardEntities.Abilities;
    using DataKeys;
    using HarmonyLib;
    using HouseRules.Core.Types;

    public sealed class DoriathPointFreeThingsOnLastMoveAndCritRule : Rule, IConfigWritable<List<BoardPieceId>>, IPatchable, IMultiplayerSafe
    {
        public override string Description => "Heroes gain effect on critical hit (Level 3+)";

        private static Context _context;
        private static List<BoardPieceId> _globalAdjustments;
        private static bool _isActivated;

        private readonly List<BoardPieceId> _adjustments;

        public DoriathPointFreeThingsOnLastMoveAndCritRule(List<BoardPieceId> adjustments)
        {
            _adjustments = adjustments;
        }

        public List<BoardPieceId> GetConfigObject() => _adjustments;

        protected override void OnActivate(Context context)
        {
            _context = context;
            _globalAdjustments = _adjustments;
            _isActivated = true;
        }

        protected override void OnDeactivate(Context context) => _isActivated = false;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Ability), "GenerateAttackDamage"),
                postfix: new HarmonyMethod(
                    typeof(DoriathPointFreeThingsOnLastMoveAndCritRule),
                    nameof(Ability_GenerateAttackDamage_Postfix)));
        }

        private static void Ability_GenerateAttackDamage_Postfix(Piece source, Dice.Outcome diceResult)
        {
            if (!_isActivated) return;
            if (!source.IsPlayer()) return;
            if (diceResult != Dice.Outcome.Crit) return;
            if (!_globalAdjustments.Contains(source.boardPieceId)) return;

            var level = source.GetStatMax(Stats.Type.CritChance);

            if (level >= 8) // formerly level 9, swapped with level 8 (per-character ultimate card)
            {
                source.effectSink.Heal(1);
                source.DisableEffectState(EffectStateType.Heal);
                source.EnableEffectState(EffectStateType.Heal, 1);
            }

            // Gold on CRIT merged into level 4: threshold lowered so it activates
            // as soon as +2 Max HP is granted.
            if (level >= 4)
            {
                source.AddGold(20);
                source.AddGold(0);
                GameUI.ShowCameraMessage("<color=#FFD700>+20 Gold!</color>", 2);
            }

            if (level >= 3)
            {
                switch (source.boardPieceId)
                {
                    case BoardPieceId.HeroRogue:
                        source.EnableEffectState(EffectStateType.Invisibility, 1);
                        break;

                    case BoardPieceId.HeroGuardian:
                        source.EnableEffectState(EffectStateType.Invulnerable1, 1);
                        break;

                    case BoardPieceId.HeroSorcerer:
                        source.EnableEffectState(EffectStateType.MagicShield1, 1);
                        break;

                    case BoardPieceId.HeroWarlock:
                        source.EnableEffectState(EffectStateType.Deflect, 1);
                        break;

                    case BoardPieceId.HeroBard:
                        // DeflectionBarrier (the ElvenSummonerDeflect one) rather than
                        // Deflect — the Bard is the only one with this effect.
                        source.EnableEffectState(EffectStateType.DeflectionBarrier, 1);
                        break;

                    case BoardPieceId.HeroHunter:
                        source.EnableEffectState(EffectStateType.Invulnerable1, 1);
                        break;

                    case BoardPieceId.HeroBarbarian:
                        int myVargas = source.effectSink.GetEffectStateDurationTurnsLeft(EffectStateType.MarkOfVerga);
                        source.EnableEffectState(EffectStateType.MarkOfVerga, myVargas + 7);
                        break;
                }
            }

        }
    }
}
