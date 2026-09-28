// ============================================================
//  Doriath (PROGRESSIVE) — Plugin.cs
// ============================================================

namespace DoriathMod
{
using System;
using System.Reflection;
using BepInEx;
    using BepInEx.Logging;
    using HarmonyLib;
using HouseRules.Core;
using HouseRules.Core.Types;
using DoriathMod.Rules;

    [BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
    [BepInDependency("com.orendain.demeomods.houserules.core")]
    [BepInDependency("com.orendain.demeomods.houserules.configuration",
        BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource? Log;
        internal static Harmony? HarmonyInstance;
        private Harmony? _harmony;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginInfo.NAME} v{PluginInfo.VERSION} starting...");

            try
            {
                HR.Rulebook.Register(typeof(DoriathLevelUpRule));
                HR.Rulebook.Register(typeof(DoriathXpLossRule));
                HR.Rulebook.Register(typeof(FreeThingsOnLastMoveAndCritRule));
                HR.Rulebook.Register(typeof(FreeRevolutionsAbilityOnCritRule));
                HR.Rulebook.Register(typeof(EnemyPartyScaledRule));
                HR.Rulebook.Register(typeof(AIDirectorConfigRule));
                Log.LogInfo("[Plugin] Rules registered.");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Plugin] Error registering rules: {ex.Message}");
            }

            try
            {
                _harmony = new Harmony(PluginInfo.GUID);
                HarmonyInstance = _harmony;
                _harmony.PatchAll(typeof(Plugin).Assembly);
                Log.LogInfo("[Plugin] Harmony patches applied.");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Plugin] Error in PatchAll: {ex.Message}");
            }

            // ── "Hardcoded" rules (outside HouseRules/JSON/Panel 1) ──
            // Fixed patches with no reason to be tweaked via JSON — taken out
            // of the Rule system to stop cluttering the panel of active
            // rules. Always active, no JSON toggle.
            try
            {
                if (_harmony != null)
                {
                    DoriathMod.Rules.AbilityMayNotTargetSelfHardcoded.Patch(_harmony);
                    DoriathMod.Rules.AbilityNoAllyDamageHardcoded.Patch(_harmony);
                    DoriathMod.Rules.BerserkEndsTurnHardcoded.Patch(_harmony);
                    // Gives Berserk pieces back +1 ActionPoint/turn (via the native
                    // EffectStateType.ExtraAction mechanism), in a capped way — completes
                    // BerserkEndsTurnHardcoded without reintroducing the infinite-turn bug.
                    DoriathMod.Rules.BerserkExtraActionHardcoded.Patch(_harmony);
                    // Prevents MotherCy, ElvenSummoner and RootLord from automatically
                    // ending their turn after an ability while they still have AP left
                    // (native game behavior: TryEndTurnAfterAttack was called without
                    // ever checking remaining AP).
                    DoriathMod.Rules.BossExtraActionsHardcoded.Patch(_harmony);
                    // Spawn budget around the boss and total power index budget —
                    // taken out of the Rule/JSON system to free up space in Panel 1,
                    // values fixed to the last active settings (see the two files
                    // for details).
                    DoriathMod.Rules.BossSpawnBudgetAdjustedHardcoded.Patch(_harmony);
                    DoriathMod.Rules.BossSpawnPowerIndexBudgetAdjustedHardcoded.Patch(_harmony);
                    // Hotfix v0.0.1 — see BossSpawnBudgetAdjustedRule.cs.
                    DoriathMod.Rules.DreadElvenSummonersDisabledHardcoded.Patch(_harmony);
                    DoriathMod.Rules.RevolutionsElementImmunityDisabledHardcoded.Patch(_harmony);
                    DoriathMod.Rules.BardZapHitsEnemyPropsHardcoded.Patch(_harmony);
                    Log.LogInfo("[Plugin] Hardcoded rules applied.");
                }
                else
                {
                    Log.LogWarning("[Plugin] _harmony is null — hardcoded rules skipped.");
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"[Plugin] Error in hardcoded rules: {ex.Message}");
            }

            // Removal of GrayAlien stats (loaded after us)
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;

            Log.LogInfo($"{PluginInfo.NAME} v{PluginInfo.VERSION} loaded!");
        }

        private void OnAssemblyLoaded(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                var asmName = args.LoadedAssembly.GetName().Name;
                if (!asmName.Contains("AdvancedStats")) return;

                Log?.LogInfo("[Plugin] AdvancedStats detected — removing GrayAlien patches...");

                var vrType = args.LoadedAssembly.GetType("AdvancedStats.VRAdvancedStatsView");
                var nonVrType = args.LoadedAssembly.GetType("AdvancedStats.NonVRAdvancedStatsView");

                if (vrType != null)
                {
                    var vrMethod = AccessTools.Method(vrType, "GrabbedPieceHudInstantiator_CloneCurrentHudState_Postfix");
                    if (vrMethod != null)
                    {
                        _harmony?.Patch(vrMethod, prefix: new HarmonyMethod(typeof(Plugin), nameof(SuppressGrayAlien)));
                        Log?.LogInfo("[Plugin] VR AdvancedStats removed.");
                    }
                }

                if (nonVrType != null)
                {
                    var nonVrMethod = AccessTools.Method(nonVrType, "NonVrInfoPanelController_OnSelectPiece_Postfix");
                    if (nonVrMethod != null)
                    {
                        _harmony?.Patch(nonVrMethod, prefix: new HarmonyMethod(typeof(Plugin), nameof(SuppressGrayAlien)));
                        Log?.LogInfo("[Plugin] NonVR AdvancedStats removed.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[Plugin] Error removing AdvancedStats: {ex.Message}");
            }
        }

        private static bool SuppressGrayAlien() => false;

        // The perks panel (DoriathPerksPanel) is managed directly by
        // DoriathLevelUpRule.OnActivate()/OnDeactivate() in ProgressiveLevelRule.cs,
        // at the same hook point as SuppressGrayAlienAdvancedStats().

        private void OnDestroy()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoaded;
            _harmony?.UnpatchSelf();
        }
    }

    internal static class PluginInfo
    {
        public const string GUID    = "com.monnom.demeomods.progressive";
        public const string NAME    = "DoriathMod";
        public const string VERSION = "1.0.0";
    }
}
