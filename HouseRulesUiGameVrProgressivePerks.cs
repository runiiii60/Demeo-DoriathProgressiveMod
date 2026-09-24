namespace DoriathMod
{
    using System.Text;
    using Boardgame;
    using Boardgame.BoardEntities;
    using Boardgame.NonVR;
    using Boardgame.NonVR.Ui;
    using Boardgame.Ui;
    using DataKeys;
    using HarmonyLib;
    using DoriathMod.Rules;
    using UnityEngine;

    internal static class HouseRulesUiGameVrProgressivePerks
    {
        internal static void BuildPerksDescription(StringBuilder sb)
        {
            sb.AppendLine("<color=#FFD700>Doriath Progressive</color> -");
            sb.AppendLine("Heroes level up together by earning perks ...");

            // Table of common tiers (levels 2 to 11)
            sb.AppendLine("");
            sb.AppendLine("Level 2: Free StrengthenCourage/Charge/HunterArrow/Stealth");
            sb.AppendLine("Level 3: +2 Max HP");
            sb.AppendLine("Level 4: 2nd Knockdown");
            sb.AppendLine("Level 5: Perk cards unlocked");
            sb.AppendLine("Level 6: +1 Speed, new perks");
            sb.AppendLine("Level 7: Ultimate card per hero");
            sb.AppendLine("Level 8: +1 Gold on CRIT (sorcerers/warlocks/barbarians)");
            sb.AppendLine("Level 9: AcidSpit/MinionCharge/Blink/DeathBeam FREE");
            sb.AppendLine("Level 10: +1 Magic/Strength Bonus");
            sb.AppendLine("Level 11: FreeHealOnCrit! +1 Magic/Strength Bonus");
        }
    }
}