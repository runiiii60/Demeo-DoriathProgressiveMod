namespace DoriathMod.Rules
{
    using System.Collections.Generic;
    using Boardgame.BoardEntities;
    using Boardgame.BoardEntities.Abilities;
    using DataKeys;
    using HarmonyLib;
    using HouseRules.Core.Types;

    public sealed class FreeRevolutionsAbilityOnCritRule : Rule, IConfigWritable<Dictionary<BoardPieceId, AbilityKey>>, IPatchable, IMultiplayerSafe
    {
        public override string Description => "Heroes gain a free ability card on critical hits (level 10+)";

        private static Context _context;
        private static Dictionary<BoardPieceId, AbilityKey> _globalAdjustments;
        private static bool _isActivated;

        private readonly Dictionary<BoardPieceId, AbilityKey> _adjustments;

        public FreeRevolutionsAbilityOnCritRule(Dictionary<BoardPieceId, AbilityKey> adjustments)
        {
            _adjustments = adjustments;
        }

        public Dictionary<BoardPieceId, AbilityKey> GetConfigObject() => _adjustments;

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
                prefix: new HarmonyMethod(
                    typeof(FreeRevolutionsAbilityOnCritRule),
                    nameof(Ability_GenerateAttackDamage_Prefix)));
        }

        private static void Ability_GenerateAttackDamage_Prefix(Piece source, Dice.Outcome diceResult)
        {
            if (!_isActivated) return;
            if (!source.IsPlayer()) return;
            if (diceResult != Dice.Outcome.Crit) return;
            if (!_globalAdjustments.TryGetValue(source.boardPieceId, out var abilityKey)) return;

            var level = source.GetStatMax(Stats.Type.CritChance);
            if (level < 10) return; // formerly level 9, swapped with level 10 (FreeHealOnCrit + stat bonus)

            source.inventory.Items.Add(new Inventory.Item(
                abilityKey,
                flags: 0,
                originalOwner: -1,
                replenishCooldown: 0));
            source.AddGold(0);
        }
    }
}
