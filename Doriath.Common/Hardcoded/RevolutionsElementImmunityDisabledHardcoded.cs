// ============================================================
//  Doriath — RevolutionsElementImmunityDisabledHardcoded.cs
// ============================================================
//
// Restores elemental damage from bosses to the heroes. HouseRules'
// PartyDamageOverriddenRule.OnActivate turns on its static "revolutions"
// flag merely because some rule in the active ruleset has "Revolutions" in
// its name (here FreeRevolutionsAbilityOnCrit). Once that flag is set, the
// rule's DealDamage prefix cancels damage from every non-boss attacker
// according to each hero's element (Sorcerer/Electricity, Guardian/Fire,
// Hunter/Ice, Barbarian/Acid+Petrify, Warlock/Undefined tag, Warlock minion,
// Verochka/Ice), which in practice made heroes immune to boss attacks of
// their element. This patch clears the flag after OnActivate.
//
// Patch: Postfix on HouseRules PartyDamageOverriddenRule.OnActivate

namespace DoriathMod.Hardcoded
{
    using HarmonyLib;

    public static class RevolutionsElementImmunityDisabledHardcoded
    {
        public static void Patch(Harmony harmony)
        {
            harmony.Patch(
                AccessTools.Method(typeof(HouseRules.Essentials.Rules.PartyDamageOverriddenRule), "OnActivate"),
                postfix: new HarmonyMethod(typeof(RevolutionsElementImmunityDisabledHardcoded), nameof(Postfix)));
            Plugin.Log?.LogInfo("[RevolutionsElementImmunityDisabledHardcoded] Heroes' elemental immunities against bosses (the Revolutions mode of PartyDamageOverridden) disabled.");
        }

        private static void Postfix() =>
            Traverse.Create(typeof(HouseRules.Essentials.Rules.PartyDamageOverriddenRule)).Field("revolutions").SetValue(false);
    }
}
