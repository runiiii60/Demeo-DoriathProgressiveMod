// ============================================================
//  Doriath (PROGRESSIVE) — PieceProgressLostRule.cs
// ============================================================
//
// Level loss on revive. Based on PieceProgressLostRule by TheGrayAlien.
//
// A hero picked up by another player's Revive ability loses one level, and
// the perks granted by that level are taken back. Being picked up by magic,
// a potion or a fountain does not trigger this rule.
//
// Stat perks are reverted from PerkTable, the same table used on the way up
// (see PerkTable.cs), so a level lost gives back exactly what it gave.
// Cards are reverted here, level by level.

namespace DoriathMod.Rules
{
    using System;
    using Boardgame;
    using Boardgame.BoardEntities;
    using Boardgame.BoardEntities.Abilities;
    using DataKeys;
    using HarmonyLib;
    using HouseRules.Core;
    using HouseRules.Core.Types;

    public sealed class DoriathXpLossRule : Rule, IConfigWritable<bool>, IPatchable, IMultiplayerSafe
    {
        public override string Description => "Heroes lose a level if revived without using magic or a potion";

        private static bool _isActivated;

        public DoriathXpLossRule() { }
        public DoriathXpLossRule(bool value) { }

        public bool GetConfigObject() => true;

        protected override void OnActivate(Context context) => _isActivated = true;
        protected override void OnDeactivate(Context context) => _isActivated = false;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(MotherTracker), "TrackRevive"),
                prefix: new HarmonyMethod(
                    typeof(DoriathXpLossRule),
                    nameof(MotherTracker_TrackRevive_Prefix)));
        }

        private static void MotherTracker_TrackRevive_Prefix(Piece revivedPiece, AbilityKey sourceAbility)
        {
            if (!_isActivated) return;

            // A level is lost only when another player uses the Revive ability.
            if (sourceAbility != AbilityKey.Revive) return;

            int level = revivedPiece.GetStatMax(Stats.Type.CritChance);
            if (level < 2) return;

            int newLevel = level - 1;

            // Decrement the level.
            revivedPiece.effectSink.TrySetStatMaxValue(Stats.Type.CritChance, newLevel);
            revivedPiece.effectSink.SetStatusEffectDuration(EffectStateType.Flying, newLevel);
            revivedPiece.effectSink.AddStatusEffect(EffectStateType.ConfusedPermanentVisualOnly, -1);

            // Take back the cards granted by the lost level.
            ReverseLevelPerk(revivedPiece, level);

            // Stat perks: same table as on the way up, read in reverse.
            // See PerkTable.cs.
            PerkTable.Revert(revivedPiece, level);

            // In-game message.
            string heroName = GetHeroName(revivedPiece.boardPieceId);
            GameUI.ShowCameraMessage(
                $"<color=#F0F312>{heroName}</color> <color=#FF1C06>LOST a level!</color> Now level {newLevel}", 6);
        }

        private static void ReverseLevelPerk(Piece piece, int lostLevel)
        {
            var id = piece.boardPieceId;

            switch (lostLevel)
            {
                case 2:
                    // Loses the level 1 cards.
                    ReverseLevelCards(piece, id, 2);
                    break;

                case 3:
                    // The knockdown is reverted by PerkTable. The on-CRIT buff is driven
                    // by a threshold (level >= 3) and switches itself off.
                    break;

                case 4:
                    // The +2 max HP is reverted by PerkTable. The rest of level 4 (the
                    // "Gold on CRIT" effect merged into this level) needs no explicit
                    // removal: it is gated on level >= 4 and switches itself off as soon
                    // as CritChance drops back below 4.
                    break;

                case 5:
                    // Loses the level 4 cards.
                    ReverseLevelCards(piece, id, 5);
                    break;

                case 6:
                    // Loses the level 5 cards. Level 5 grants no stat bonus.
                    ReverseLevelCards(piece, id, 6);
                    break;

                case 7:
                    // The knockdown is reverted by PerkTable. Level 7 grants no max HP,
                    // so none is taken back here.
                    ReverseLevelCards(piece, id, 7);
                    break;

                case 8:
                    // The +1 Magic/Strength is reverted by PerkTable. Level 8 grants no
                    // max HP, so none is taken back here.
                    break;

                case 9:
                    // Loses the ultimate cards.
                    ReverseLevelCards(piece, id, 9);
                    break;

                case 10:
                    // FreeRevolutionsAbilityOnCrit: threshold-driven, nothing to revert.
                    break;
            }
        }

        private static void ReverseLevelCards(Piece piece, BoardPieceId id, int lostLevel)
        {
            switch (lostLevel)
            {
                case 2:
                    // Level 1: Hunter -> TurretDamageProjectile (added),
                    // Rogue -> DiseasedBite (added), Warlock -> EnemyFrostball added and
                    // MinionCharge removed, so MinionCharge is restored here,
                    // Sorcerer -> DeathFlurry (added, replenish factor 1, normal cost).
                    switch (id)
                    {
                        case BoardPieceId.HeroHunter:
                            RemoveInventoryCard(piece, AbilityKey.TurretDamageProjectile);
                            break;
                        case BoardPieceId.HeroRogue:
                            RemoveInventoryCard(piece, AbilityKey.DiseasedBite);
                            break;
                        case BoardPieceId.HeroWarlock:
                            RemoveInventoryCard(piece, AbilityKey.EnemyFrostball);
                            // Restore MinionCharge (was removed at level 1)
                            Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                            piece.inventory.Items.Add(new Inventory.Item(AbilityKey.MinionCharge,
                                flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 1));
                            piece.AddGold(0);
                            break;
                        case BoardPieceId.HeroSorcerer:
                            // Removes DeathFlurry, granted at level 1 alongside the free
                            // EnemyFireball.
                            RemoveInventoryCard(piece, AbilityKey.DeathFlurry);
                            break;
                    }
                    break;

                case 5:
                    // Level 4: Guardian -> PiercingSpear (revert replenish),
                    // Sorcerer -> Fireball (revert replenish).
                    //
                    // Bard (Zap), Hunter (TurretDamageProjectile), Rogue (DiseasedBite),
                    // Warlock (EnemyFrostball) and Barbarian (SpawnRandomLamp) have
                    // nothing to revert here: their card becoming free at this level is
                    // gated on level >= 5, re-evaluated continuously in SetCost and
                    // Inventory_RestoreReplenishables_Prefix, so it costs again as soon
                    // as CritChance drops back below 5.
                    switch (id)
                    {
                        case BoardPieceId.HeroGuardian:
                            RevertReplenishable(piece, AbilityKey.PiercingSpear);
                            break;
                        case BoardPieceId.HeroSorcerer:
                            RevertReplenishable(piece, AbilityKey.Fireball);
                            break;
                    }
                    break;

                case 6:
                    // Level 5: Bard -> Electricity (added), Guardian -> Whirlwind (revert),
                    // Hunter -> PoisonedTip (added), Rogue -> PoisonGasGrenade (revert),
                    // Sorcerer -> FretsOfFire (added), Warlock -> Freeze (added),
                    // Barbarian -> GrapplingSmash (revert).
                    switch (id)
                    {
                        case BoardPieceId.HeroBard:
                            RemoveInventoryCard(piece, AbilityKey.Electricity);
                            break;
                        case BoardPieceId.HeroGuardian:
                            RevertReplenishable(piece, AbilityKey.Whirlwind);
                            break;
                        case BoardPieceId.HeroHunter:
                            RemoveInventoryCard(piece, AbilityKey.PoisonedTip);
                            break;
                        case BoardPieceId.HeroRogue:
                            RevertReplenishable(piece, AbilityKey.PoisonGasGrenade);
                            break;
                        case BoardPieceId.HeroSorcerer:
                            RemoveInventoryCard(piece, AbilityKey.FretsOfFire);
                            break;
                        case BoardPieceId.HeroWarlock:
                            RemoveInventoryCard(piece, AbilityKey.Freeze);
                            break;
                        case BoardPieceId.HeroBarbarian:
                            RevertReplenishable(piece, AbilityKey.GrapplingSmash);
                            break;
                    }
                    break;

                case 7:
                    // Level 6: Bard -> ScrollTsunami (revert),
                    // Guardian -> BeaconOfHealing (revert), Hunter -> MarkOfAvalon (added),
                    // Rogue -> ProximityMine (added), Sorcerer -> MagicShield (added),
                    // Warlock -> IceExplosion (added), Barbarian -> Implosion (revert).
                    switch (id)
                    {
                        case BoardPieceId.HeroBard:
                            RevertReplenishable(piece, AbilityKey.ScrollTsunami);
                            break;
                        case BoardPieceId.HeroGuardian:
                            RevertReplenishable(piece, AbilityKey.BeaconOfHealing);
                            break;
                        case BoardPieceId.HeroHunter:
                            RemoveInventoryCard(piece, AbilityKey.MarkOfAvalon);
                            break;
                        case BoardPieceId.HeroRogue:
                            RemoveInventoryCard(piece, AbilityKey.ProximityMine);
                            break;
                        case BoardPieceId.HeroSorcerer:
                            RemoveInventoryCard(piece, AbilityKey.MagicShield);
                            break;
                        case BoardPieceId.HeroWarlock:
                            RemoveInventoryCard(piece, AbilityKey.IceExplosion);
                            break;
                        case BoardPieceId.HeroBarbarian:
                            RemoveInventoryCard(piece, AbilityKey.Implosion);
                            break;
                    }
                    break;
                case 9:
                    // Level 8: Bard -> Tornado (revert), Guardian -> BeaconOfSmite (revert),
                    // Hunter -> Exterminate (added), Rogue -> CursedDagger (revert),
                    // Sorcerer -> ExplosiveOrb (added), Warlock -> MissileSwarm (revert),
                    // Barbarian -> PlayerLeap (added).
                    //
                    // MissileSwarm is reverted, not removed: it is a starting card with a
                    // replenish factor of 0, so level 8 only makes it replenishable.
                    // Removing it outright would delete the card from the hero's hand.
                    switch (id)
                    {
                        case BoardPieceId.HeroBard:
                            RevertReplenishable(piece, AbilityKey.Tornado);
                            break;
                        case BoardPieceId.HeroGuardian:
                            RevertReplenishable(piece, AbilityKey.BeaconOfSmite);
                            break;
                        case BoardPieceId.HeroHunter:
                            RemoveInventoryCard(piece, AbilityKey.Exterminate);
                            break;
                        case BoardPieceId.HeroRogue:
                            RevertReplenishable(piece, AbilityKey.CursedDagger);
                            break;
                        case BoardPieceId.HeroSorcerer:
                            RemoveInventoryCard(piece, AbilityKey.ExplosiveOrb);
                            break;
                        case BoardPieceId.HeroWarlock:
                            RevertReplenishable(piece, AbilityKey.MissileSwarm);
                            break;
                        case BoardPieceId.HeroBarbarian:
                            RemoveInventoryCard(piece, AbilityKey.PlayerLeap);
                            break;
                    }
                    break;

                // There is no case 10: level 10 adds and removes no card, so
                // ReverseLevelPerk never calls this method with lostLevel 10.
            }
        }

        private static void RemoveInventoryCard(Piece piece, AbilityKey key)
        {
            for (int i = 0; i < piece.inventory.Items.Count; i++)
            {
                if (piece.inventory.Items[i].AbilityKey == key)
                {
                    var item = piece.inventory.Items[i];
                    if (item.IsReplenishing)
                        Traverse.Create(piece.inventory)
                            .Field<int>("numberOfReplenishableCards").Value -= 1;
                    piece.inventory.Items.RemoveAt(i);
                    piece.AddGold(0);
                    break;
                }
            }
        }

        private static void RevertReplenishable(Piece piece, AbilityKey key)
        {
            for (int i = 0; i < piece.inventory.Items.Count; i++)
            {
                var item = piece.inventory.Items[i];
                if (item.AbilityKey == key && item.IsReplenishing)
                {
                    item.flags &= ~(Inventory.ItemFlag)1;
                    item.replenishCooldown = 0;
                    piece.inventory.Items[i] = item;
                    Traverse.Create(piece.inventory)
                        .Field<int>("numberOfReplenishableCards").Value -= 1;
                    piece.AddGold(0);
                    break;
                }
            }
        }

        private static string GetHeroName(BoardPieceId id)
        {
            switch (id)
            {
                case BoardPieceId.HeroBard:      return "Bard";
                case BoardPieceId.HeroGuardian:  return "Guardian";
                case BoardPieceId.HeroHunter:    return "Hunter";
                case BoardPieceId.HeroRogue:     return "Rogue";
                case BoardPieceId.HeroSorcerer:  return "Sorcerer";
                case BoardPieceId.HeroWarlock:   return "Warlock";
                case BoardPieceId.HeroBarbarian: return "Barbarian";
                default:                          return "Hero";
            }
        }
    }
}
