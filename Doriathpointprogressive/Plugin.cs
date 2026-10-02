// ============================================================
//  Doriath (Point Progressive) — Plugin.cs
// ============================================================

namespace DoriathMod
{
    using System;
    using System.Linq;
    using System.Reflection;
    using BepInEx;
    using BepInEx.Logging;
    using HarmonyLib;
    using HouseRules.Core;
    using HouseRules.Core.Types;
    using DoriathMod.Hardcoded;
    using DoriathMod.Rules;

    [BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
    [BepInDependency("com.orendain.demeomods.houserules.core")]
    [BepInDependency("com.orendain.demeomods.houserules.configuration",
        BepInDependency.DependencyFlags.SoftDependency)]
    // Soft dependency on Doriath (PROGRESSIVE): no effect when it is absent, but
    // when it is installed BepInEx guarantees it loads BEFORE us, which is what
    // makes the Chainloader check below reliable.
    [BepInDependency("com.monnom.demeomods.progressive",
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
                HR.Rulebook.Register(typeof(DoriathPointLevelUpRule));
                HR.Rulebook.Register(typeof(DoriathPointLevelLossRule));
                HR.Rulebook.Register(typeof(DoriathPointFreeThingsOnLastMoveAndCritRule));
                HR.Rulebook.Register(typeof(DoriathPointFreeRevolutionsAbilityOnCritRule));
                HR.Rulebook.Register(typeof(DoriathPointEnemyPartyScaledRule));
                HR.Rulebook.Register(typeof(DoriathPointAIDirectorConfigRule));
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

            // ── Hardcoded patches (outside HouseRules, the JSON and Panel 1) ──
            // Fixed behaviour, always on, with no JSON switch. They live outside the
            // Rule system so they no longer take up room in the active-rules panel.
            // One file per patch under Hardcoded/, each documenting what it does.
            //
            // These patches are IDENTICAL to the ones in Doriath (PROGRESSIVE) and
            // apply when the plugin loads, not when the ruleset is activated. With
            // both DLLs installed, replaying them here would double them up (the
            // Floor2SpawnBudgetReduced -15% would land twice, for -27.75%). So when
            // the classic mod is present we let it carry them; the soft dependency
            // declared above guarantees it loads first, which is what makes this
            // check reliable.
            //
            // Every patch is isolated in its own try: one that fails (a game method
            // renamed by an update, for instance) must not stop the others from
            // applying.
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.monnom.demeomods.progressive"))
            {
                Log.LogInfo("[Plugin] Doriath (PROGRESSIVE) present — hardcoded patches left to it (no double patch).");
            }
            else if (_harmony == null)
            {
                Log.LogWarning("[Plugin] _harmony is null — hardcoded patches skipped.");
            }
            else
            {
                var hardcoded = new Action<Harmony>[]
                {
                    AbilityMayNotTargetSelfHardcoded.Patch,
                    AbilityNoAllyDamageHardcoded.Patch,
                    BerserkEndsTurnHardcoded.Patch,
                    BerserkExtraActionHardcoded.Patch,
                    BossExtraActionsHardcoded.Patch,
                    BossSpawnBudgetAdjustedHardcoded.Patch,
                    BossSpawnPowerIndexBudgetAdjustedHardcoded.Patch,
                    DreadElvenSummonersDisabledHardcoded.Patch,
                    RevolutionsElementImmunityDisabledHardcoded.Patch,
                    BardZapHitsEnemyPropsHardcoded.Patch,
                    TelemetryDamageCrashGuardHardcoded.Patch,
                    Floor2SpawnBudgetReducedHardcoded.Patch,
                    // Observability: end-of-level recap in the log.
                    LevelRecapHardcoded.Patch,
                };

                var applied = 0;
                foreach (var patch in hardcoded)
                {
                    var name = patch.Method.DeclaringType?.Name ?? "?";
                    try
                    {
                        patch(_harmony);
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        Log.LogError($"[Plugin] Hardcoded patch {name} failed: {ex.Message}");
                    }
                }

                Log.LogInfo($"[Plugin] Hardcoded patches applied ({applied}/{hardcoded.Length}).");
            }

            LogHarmonyReport();

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

        // The perks panel (DoriathPerksPanel) is driven directly by
        // DoriathPointLevelUpRule.OnActivate()/OnDeactivate() in ProgressiveLevelRule.cs,
        // at the same reliable hook point as SuppressGrayAlienAdvancedStats(), rather
        // than from here via OnSceneLoaded/OnSceneUnloaded.

        // Lists, at load time, the game methods this plugin actually patched.
        //
        // If a Demeo update renames or removes a method, the matching patch fails
        // silently and its line disappears from this report: the total drops, so the
        // gap is visible straight away instead of as behaviour that evaporates in the
        // middle of a run.
        //
        // Covers our Harmony instance only. Patches installed by HouseRules rules are
        // applied by the HouseRules instance when the ruleset is activated, and
        // AdvancedStats' own patches land after its assembly loads (see
        // OnAssemblyLoaded).
        private void LogHarmonyReport()
        {
            try
            {
                if (_harmony == null) return;

                var patched = _harmony.GetPatchedMethods()
                    .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
                    .Distinct()
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();

                Log.LogInfo($"[Plugin] {patched.Length} game methods patched: {string.Join(", ", patched)}");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Plugin] Harmony report unavailable: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoaded;
            _harmony?.UnpatchSelf();
        }
    }

    internal static class PluginInfo
    {
        public const string GUID    = "com.monnom.demeomods.pointprogressive";
        public const string NAME    = "DoriathPointMod";
        public const string VERSION = "1.0.0";
    }
}
