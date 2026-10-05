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

    internal static class AdvancedStatsView
    {
        private static string ColorizeString(string text, Color color)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";
        }

        internal static void BuildAndShow(Piece piece, GrabbedPieceHudInstantiator data, IPieceNameController nameController)
        {
            // Our Harmony patches are applied at plugin load, not at ruleset
            // activation, so they also fire in other players' modes. Stay silent
            // unless our own ruleset is the one running.
            if (!DoriathPointLevelUpRule.IsActivated) return;

            int critLevel = piece.GetStatMax(Stats.Type.CritChance);
            // Displayed level = critLevel as-is, range 1 to 10, consistent with
            // Panel 2 (DoriathPerksPanel.cs).
            int level = critLevel;
            if (level < 1) level = 1;

            int strength = piece.GetStat(Stats.Type.Strength);
            int maxstrength = piece.GetStatMax(Stats.Type.Strength);
            int speed = piece.GetStat(Stats.Type.Speed);
            int maxspeed = piece.GetStatMax(Stats.Type.Speed);
            int magic = piece.GetStat(Stats.Type.MagicBonus);
            int maxmagic = piece.GetStatMax(Stats.Type.MagicBonus);
            int resist = piece.GetStat(Stats.Type.DamageResist);
            int maxresist = piece.GetStatMax(Stats.Type.DamageResist);
            int numdowns = piece.GetStat(Stats.Type.DownedCounter);

            int replenishableCount = 0;
            for (int i = 0; i < piece.inventory.Items.Count; i++)
            {
                if (piece.inventory.Items[i].IsReplenishable)
                    replenishableCount++;
            }

            var pieceConfig = Traverse.Create(data).Field<PieceConfigData>("pieceConfig").Value;
            EffectStateType[] immuneList = pieceConfig.ImmuneToStatusEffects;

            string name = nameController.GetPieceName();
            var sb = new StringBuilder();

            sb.AppendLine(ColorizeString($"<u>{name}</u>", Color.yellow));

            sb.Append(ColorizeString("--", Color.gray));
            if (level >= 1 && level <= 10)
            {
                sb.Append(ColorizeString(" Level ", Color.white));
                sb.Append(ColorizeString($"{level}", level >= 10 ? Color.magenta : Color.cyan));
            }
            else
            {
                sb.Append(ColorizeString(" No level yet", Color.gray));
            }
            sb.AppendLine(ColorizeString(" --", Color.gray));

            if (level >= 1 && level <= 10)
            {
                string perkMsg = DoriathPointLevelUpRule.GetLevelMessage(piece.boardPieceId, critLevel);
                if (!string.IsNullOrEmpty(perkMsg))
                {
                    sb.AppendLine(ColorizeString(perkMsg, Color.white));
                }

                if (critLevel < 10)
                {
                    string nextMsg = DoriathPointLevelUpRule.GetLevelMessage(piece.boardPieceId, critLevel + 1);
                    if (!string.IsNullOrEmpty(nextMsg))
                    {
                        sb.Append(ColorizeString("Next: ", Color.green));
                        sb.AppendLine(ColorizeString(nextMsg, Color.white));
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine(ColorizeString("-- Stats --", Color.gray));
            sb.Append(ColorizeString("Knockdowns: ", Color.white));
            Color kdColor = numdowns switch
            {
                0 => Color.green,
                1 => Color.yellow,
                2 => new(1f, 0.5f, 0f),
                _ => Color.red
            };
            sb.AppendLine(ColorizeString($"{3 - numdowns}/3", kdColor));
            sb.Append(ColorizeString("Replenishable: ", Color.white));
            sb.AppendLine(ColorizeString($"{replenishableCount}", Color.yellow));

            if (strength > 0)
            {
                sb.Append(ColorizeString("Strength: ", Color.cyan));
                sb.AppendLine(ColorizeString($"{strength}/{maxstrength}", Color.green));
            }

            if (speed > 0)
            {
                sb.Append(ColorizeString("Swiftness: ", Color.cyan));
                sb.AppendLine(ColorizeString($"{speed}/{maxspeed}", Color.green));
            }

            if (magic > 0)
            {
                sb.Append(ColorizeString("Magic: ", Color.cyan));
                sb.AppendLine(ColorizeString($"{magic}/{maxmagic}", Color.green));
            }

            if (resist > 0)
            {
                sb.Append(ColorizeString("Resist: ", Color.cyan));
                sb.AppendLine(ColorizeString($"{resist}/{maxresist}", Color.green));
            }

            if (immuneList != null && immuneList.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine(ColorizeString("-- Immunities --", Color.gray));
                for (int i = 0; i < immuneList.Length; i++)
                {
                    string title = StatusEffectsConfig.GetLocalizedTitle(immuneList[i]);
                    if (!string.IsNullOrEmpty(title) && !title.Contains("Undefined"))
                    {
                        if (i > 0) sb.Append(ColorizeString(", ", Color.cyan));
                        sb.Append(ColorizeString(title, Color.cyan));
                    }
                }
                sb.AppendLine();
            }

            GameUI.ShowCameraMessage(sb.ToString(), 12);
        }
    }

    [HarmonyPatch(typeof(NonVrInfoPanelController), "OnSelectPiece")]
    internal static class AdvancedStatsViewNonVR
    {
        [HarmonyPostfix]
        private static void ShowAdvancedStats(PiceDetectionData selectData)
        {
            if (!selectData.Grabbable || !selectData.HasHudInstantiator) return;

            var data = selectData.HudInstantiator;
            Piece piece = data.MyPiece;
            if (!piece.IsPlayer()) return;

            var nameController = Traverse.Create(data).Field<IPieceNameController>("pieceNameController").Value;
            AdvancedStatsView.BuildAndShow(piece, data, nameController);
        }
    }

    [HarmonyPatch(typeof(GrabbedPieceHudInstantiator), "CloneCurrentHudState")]
    [HarmonyPriority(Priority.Last)]
    internal static class AdvancedStatsViewVR
    {
        [HarmonyPostfix]
        private static void ShowAdvancedStats(GrabbedPieceHudInstantiator __instance)
        {
            Piece piece = __instance.MyPiece;
            if (!piece.IsPlayer()) return;

            var nameController = Traverse.Create(__instance).Field<IPieceNameController>("pieceNameController").Value;
            AdvancedStatsView.BuildAndShow(piece, __instance, nameController);
        }
    }
}
