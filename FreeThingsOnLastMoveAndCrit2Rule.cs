namespace DoriathMod.Rules
{
    using System.Collections.Generic;
    using Boardgame.BoardEntities;
    using Boardgame.BoardEntities.Abilities;
    using DataKeys;
    using HarmonyLib;
    using HouseRules.Core.Types;

    public sealed class FreeThingsOnLastMoveAndCrit2Rule : Rule, IConfigWritable<List<BoardPieceId>>, IPatchable, IMultiplayerSafe
    {
        public override string Description => "Heroes gain extra effects on critical hits at level 9+";

        private static bool _isActivated;

        private static readonly List<BoardPieceId> AllHeroes = new List<BoardPieceId>
        {
            BoardPieceId.HeroBard,
            BoardPieceId.HeroGuardian,
            BoardPieceId.HeroHunter,
            BoardPieceId.HeroRogue,
            BoardPieceId.HeroSorcerer,
            BoardPieceId.HeroWarlock,
            BoardPieceId.HeroBarbarian,
        };

        private readonly List<BoardPieceId> _adjustments;

        public FreeThingsOnLastMoveAndCrit2Rule(List<BoardPieceId> adjustments)
        {
            _adjustments = adjustments;
        }

        public List<BoardPieceId> GetConfigObject() => _adjustments;

        protected override void OnActivate(Context context) => _isActivated = true;
        protected override void OnDeactivate(Context context) => _isActivated = false;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Ability), "GenerateAttackDamage"),
                postfix: new HarmonyMethod(
                    typeof(FreeThingsOnLastMoveAndCrit2Rule),
                    nameof(Ability_GenerateAttackDamage_Postfix)));
        }

        private static void Ability_GenerateAttackDamage_Postfix(Piece source, Dice.Outcome diceResult)
        {
            if (!_isActivated) return;
            if (!source.IsPlayer()) return;
            if (diceResult != Dice.Outcome.Crit) return;
            if (!AllHeroes.Contains(source.boardPieceId)) return;

            source.effectSink.TryGetStat(Stats.Type.ActionPoints, out int currentAP);
            if (currentAP > 0) return;

            var level = source.GetStatMax(Stats.Type.CritChance);

            // Displayed level 5 (Panel 2) = CritChance max 5: +1 AP
            if (level >= 5)
            {
                source.effectSink.TrySetStatBaseValue(Stats.Type.ActionPoints, currentAP + 1);
            }

            // Displayed level 9 (Panel 2) = CritChance max 9: +1 heal + recharge ultimate card
            if (level >= 9)
            {
                source.effectSink.Heal(1);
                source.DisableEffectState(EffectStateType.Heal);
                source.EnableEffectState(EffectStateType.Heal, 1);

                Inventory.Item value;
                switch (source.boardPieceId)
                {
                    case BoardPieceId.HeroRogue:
                        for (int i = 0; i < source.inventory.Items.Count; i++)
                        {
                            value = source.inventory.Items[i];
                            if (value.AbilityKey == AbilityKey.Flashbang && value.IsReplenishing)
                            {
                                value.flags &= (Inventory.ItemFlag)(-3);
                                source.inventory.Items[i] = value;
                                source.AddGold(0);
                                break;
                            }
                        }
                        break;

                    case BoardPieceId.HeroBarbarian:
                        for (int i = 0; i < source.inventory.Items.Count; i++)
                        {
                            value = source.inventory.Items[i];
                            if (value.AbilityKey == AbilityKey.PlayerLeap && value.IsReplenishing)
                            {
                                value.flags &= (Inventory.ItemFlag)(-3);
                                source.inventory.Items[i] = value;
                                source.AddGold(0);
                                break;
                            }
                        }
                        break;

                    case BoardPieceId.HeroBard:
                        for (int i = 0; i < source.inventory.Items.Count; i++)
                        {
                            value = source.inventory.Items[i];
                            if (value.AbilityKey == AbilityKey.Tornado && value.IsReplenishing)
                            {
                                value.flags &= (Inventory.ItemFlag)(-3);
                                source.inventory.Items[i] = value;
                                source.AddGold(0);
                                break;
                            }
                        }
                        break;

                    case BoardPieceId.HeroHunter:
                        for (int i = 0; i < source.inventory.Items.Count; i++)
                        {
                            value = source.inventory.Items[i];
                            if (value.AbilityKey == AbilityKey.MarkOfAvalon && value.IsReplenishing)
                            {
                                value.flags &= (Inventory.ItemFlag)(-3);
                                source.inventory.Items[i] = value;
                                source.AddGold(0);
                                break;
                            }
                        }
                        break;

                    case BoardPieceId.HeroSorcerer:
                        for (int i = 0; i < source.inventory.Items.Count; i++)
                        {
                            value = source.inventory.Items[i];
                            if (value.AbilityKey == AbilityKey.ExplosiveOrb && value.IsReplenishing)
                            {
                                value.flags &= (Inventory.ItemFlag)(-3);
                                source.inventory.Items[i] = value;
                                source.AddGold(0);
                                break;
                            }
                        }
                        break;

                    case BoardPieceId.HeroGuardian:
                        for (int i = 0; i < source.inventory.Items.Count; i++)
                        {
                            value = source.inventory.Items[i];
                            if (value.AbilityKey == AbilityKey.BeaconOfHealing && value.IsReplenishing)
                            {
                                value.flags &= (Inventory.ItemFlag)(-3);
                                source.inventory.Items[i] = value;
                                source.AddGold(0);
                                break;
                            }
                        }
                        break;

                    case BoardPieceId.HeroWarlock:
                        for (int i = 0; i < source.inventory.Items.Count; i++)
                        {
                            value = source.inventory.Items[i];
                            if (value.AbilityKey == AbilityKey.MissileSwarm && value.IsReplenishing)
                            {
                                value.flags &= (Inventory.ItemFlag)(-3);
                                source.inventory.Items[i] = value;
                                source.AddGold(0);
                                break;
                            }
                        }
                        break;
                }
            }
        }
    }
}
