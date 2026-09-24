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

    /// <summary>
    /// Makes a hero lose a level of experience when revived via MotherTracker.TrackRevive, reversing the
    /// perks gained at the lost level.
    /// </summary>
    /// <remarks>
    /// Based on PieceProgressLostRule by TheGrayAlien.
    /// </remarks>
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

            // Only lose a level if revived by another player (Revive ability)
            if (sourceAbility != AbilityKey.Revive) return;

            int level = revivedPiece.GetStatMax(Stats.Type.CritChance);
            if (level < 2) return;

            int newLevel = level - 1;

            // Decrement the level
            revivedPiece.effectSink.TrySetStatMaxValue(Stats.Type.CritChance, newLevel);
            revivedPiece.effectSink.SetStatusEffectDuration(EffectStateType.Flying, newLevel);
            revivedPiece.effectSink.AddStatusEffect(EffectStateType.ConfusedPermanentVisualOnly, -1);

            // Reverse the lost level's perk
            ReverseLevelPerk(revivedPiece, level);

            // In-game message
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
                    // Lose the level 1 cards
                    ReverseLevelCards(piece, id, 2);
                    break;

                case 3:
                    // Lose the 2nd knockdown + the CRIT buff from level 3
                    piece.effectSink.TrySetStatBaseValue(Stats.Type.DownedCounter,
                        piece.GetStat(Stats.Type.DownedCounter) + 1);
                    piece.effectSink.TrySetStatBaseValue(Stats.Type.DownedTimer,
                        piece.GetStat(Stats.Type.DownedTimer) - 1);
                    break;

                case 4:
                    // Lose the +2 max HP from level 4 (the "Gold on CRIT" merged in here, formerly
                    // level 9, needs no explicit removal: it is gated by the level >= 4 threshold and
                    // deactivates automatically as soon as CritChance drops below 4)
                    piece.effectSink.TrySetStatMaxValue(Stats.Type.Health,
                        piece.GetMaxHealth() - 2);
                    break;

                case 5:
                    // Lose the level 4 cards
                    ReverseLevelCards(piece, id, 5);
                    break;

                case 6:
                    // Lose the level 5 cards (the +1 MoveRange was already removed, nothing left to undo)
                    ReverseLevelCards(piece, id, 6);
                    break;

                case 7:
                    // Lose the +1 knockdown + the +2 max HP added at this level + the level 6 cards
                    piece.effectSink.TrySetStatMaxValue(Stats.Type.Health,
                        piece.GetMaxHealth() - 2);
                    piece.effectSink.TrySetStatBaseValue(Stats.Type.DownedCounter,
                        piece.GetStat(Stats.Type.DownedCounter) + 1);
                    piece.effectSink.TrySetStatBaseValue(Stats.Type.DownedTimer,
                        piece.GetStat(Stats.Type.DownedTimer) - 1);
                    ReverseLevelCards(piece, id, 7);
                    break;

                case 8:
                    // Level 7 (formerly level 8, swapped with level 8): lose +1 Magic/Strength
                    // + the +2 max HP added at this level
                    piece.effectSink.TrySetStatMaxValue(Stats.Type.Health,
                        piece.GetMaxHealth() - 2);
                    if (id == BoardPieceId.HeroBard ||
                        id == BoardPieceId.HeroWarlock ||
                        id == BoardPieceId.HeroSorcerer)
                    {
                        piece.effectSink.TrySetStatBaseValue(Stats.Type.MagicBonus,
                            piece.GetStat(Stats.Type.MagicBonus) - 1);
                        piece.effectSink.TrySetStatMaxValue(Stats.Type.MagicBonus,
                            piece.GetStatMax(Stats.Type.MagicBonus) - 1);
                    }
                    else
                    {
                        piece.effectSink.TrySetStatBaseValue(Stats.Type.Strength,
                            piece.GetStat(Stats.Type.Strength) - 1);
                        piece.effectSink.TrySetStatMaxValue(Stats.Type.Strength,
                            piece.GetStatMax(Stats.Type.Strength) - 1);
                    }
                    break;

                case 9:
                    // Level 8 (formerly level 7, swapped with level 7): lose the ultimate cards
                    ReverseLevelCards(piece, id, 9);
                    break;

                case 10:
                    // Level 9 (formerly level 9, swapped with level 10): FreeRevolutionsAbilityOnCrit (not reversible)
                    break;
            }
        }

        private static void ReverseLevelCards(Piece piece, BoardPieceId id, int lostLevel)
        {
            switch (lostLevel)
            {
                case 2:
                    // Level 1: Hunter->TurretDamageProjectile (added), Rogue->DiseasedBite (added)
                    // Warlock->EnemyFrostball added, MinionCharge removed -> restore MinionCharge
                    // Sorcerer->DeathFlurry (added, RF=1, normal cost — replaces DeathBeam)
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
                            // Remove DeathFlurry (given at level 1, together with a free EnemyFireball;
                            // replaces DeathBeam)
                            RemoveInventoryCard(piece, AbilityKey.DeathFlurry);
                            break;
                    }
                    break;

                case 5:
                    // Level 4: Guardian->PiercingSpear (revert replenish), Sorcerer->Fireball (revert replenish).
                    // Bard (Zap), Hunter (TurretDamageProjectile), Rogue (DiseasedBite), Warlock (EnemyFrostball)
                    // and Barbarian (SpawnRandomLamp) have nothing to undo here: their "free" access at this
                    // level is handled by a threshold (level >= 5) re-evaluated continuously in SetCost/
                    // Inventory_RestoreReplenishables_Prefix, which automatically becomes payable again as soon
                    // as CritChance drops below 5 - no explicit removal code needed.
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
                    // Level 5: Bard->Electricity (added), Guardian->Whirlwind (revert),
                    // Hunter->PoisonedTip (added), Rogue->PoisonGasGrenade (revert),
                    // Sorcerer->FretsOfFire (added, swapped with level 6 for the Sorcerer
                    // only), Warlock->Freeze (added, swapped with level 6 for the
                    // Warlock only), Barbarian->GrapplingSmash (revert, swapped with
                    // level 6 for the Barbarian only)
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
                    // Level 6: Bard->ScrollTsunami (revert), Guardian->BeaconOfHealing (revert,
                    // swapped with level 8 for the Guardian only),
                    // Hunter->MarkOfAvalon (added, swapped with level 8 for the Hunter
                    // only), Rogue->ProximityMine (added; replaces Flashbang),
                    // Sorcerer->MagicShield (added, swapped with level 5 for the Sorcerer
                    // only), Warlock->IceExplosion (added, swapped with level 5 for
                    // the Warlock only), Barbarian->Implosion (revert, swapped with
                    // level 5 for the Barbarian only)
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
                            // Replaces Flashbang with ProximityMine
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
                    // Level 8 (formerly level 7, swapped with level 7): Bard->Tornado (revert),
                    // Guardian->BeaconOfSmite (revert, swapped with level 6 for the Guardian
                    // only), Hunter->Exterminate (added, swapped with level 6 for the
                    // Hunter only), Rogue->CursedDagger (revert, swapped with level 6
                    // for the Rogue only), Sorcerer->ExplosiveOrb (added),
                    // Warlock->MissileSwarm (revert — MissileSwarm is an RF=0 starting card,
                    // not a newly granted one; RemoveInventoryCard() used to delete it entirely
                    // instead of just stripping its replenishable flag),
                    // Barbarian->PlayerLeap (added)
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
                // Note: the old "case 10" here (level 9, FreeRevolutionsAbilityOnCrit) was dead
                // code — ReverseLevelPerk never calls ReverseLevelCards with lostLevel
                // 10 (that tier adds/removes no cards). Removed during the renumbering
                // following the merge of "Gold on CRIT" into level 4.
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
