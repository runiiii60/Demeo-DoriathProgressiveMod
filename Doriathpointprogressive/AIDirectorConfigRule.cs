// ============================================================
//  Doriath (Point Progressive) — DoriathPointAIDirectorConfigRule.cs
// ============================================================
//
// Makes a set of AI Director (Boardgame.AIDirector.*) constants moddable:
// spawn "budget", zone/saturation "percentages", and per-zone enemy count
// "caps" — beyond just the boss budget (see BossSpawnBudgetAdjustedRule).
//
// Two constant sources, two different mechanisms:
//
// 1) Data.GameData.AIDirectorConfig — a STATIC class, values set once in
//    its static constructor (.cctor) then read everywhere via ldsfld. No
//    Harmony patch is needed here: the static fields are written directly
//    via reflection, once, at mod load time (Patch()). The CLR guarantees
//    the native .cctor runs before any access (read OR write) to a static
//    field of this type once it's touched via reflection — its execution
//    is still forced explicitly before overwriting the values, to be
//    certain it can never "catch up" and stomp on them afterward.
//
// Exposed fields:
//
//   Parameter : Native / Role
//   - EasySpawnBudgetMultiplier : 0.3 — Ambient spawn budget multiplier on
//     Easy difficulty
//   - NormalSpawnBudgetMultiplier : 1.0 — Same, on Normal difficulty
//   - ActivePowerIndexInLevelSoftRoof : 275 — Soft cap on total active
//     power on the board
//   - MaxNumberOfUnitsOnBoardHardCap : 50 — HARD cap on the total number
//     of units on the board, all types combined
//   - AllowedSpawnZoneSaturation : 0.8 — Max fraction of a zone's tiles
//     "spent" before dynamic spawning stops in that zone (CustomSpawn)
//   - SpikeTrigger_MediumMap_ZoneDiscoveryPercentage : 0.5 — % of a medium
//     map that must be discovered before a difficulty spike can trigger
//   - SpikeTrigger_LargeMap_ZoneDiscoveryPercentage : 0.6 — Same, for a
//     large map
//   - MaxNumberOfEasyUnitsPerZone : 7 — Per-zone cap on Easy enemies
//     (SpawnZone's numEasyEnemies counter)
//   - MaxNumberOfMediumUnitsPerZone : 5 — Same for Medium enemies
//     (numMediumEnemies)
//   - MaxNumberOfStrongUnitsPerZone : 4 — Same for Strong enemies
//     (numStrongEnemies)
//
// 2) Boardgame.AIDirector.AIDirectorController2 — INSTANCE fields, set on
//    every construction (parameterless .ctor()) of the controller. This
//    requires a classic Harmony Postfix (no Transpiler: these are plain
//    field assignments, so much more reliable than
//    BossSpawnBudgetAdjustedRule) on this constructor, to overwrite the
//    values right after their native initialization.
//
// Exposed fields (per-zone map-coverage %, used by TagSpawnZones() to
// classify discovered tiles into Zone1/Zone2/Zone2_OuterRing):
//
//   Parameter : Native / Role
//   - Zone1_Large_PercentCoverage : 0.25 — Zone1 coverage % on a large map
//   - Zone1_Medium_PercentCoverage : 0.32 — Zone1 coverage % on a medium
//     map
//   - Zone1_Small_PercentCoverage : 0.32 — Zone1 coverage % on a small map
//   - Zone2_Large_PercentCoverage : 0.46 — Zone2 coverage % on a large map
//   - Zone2_Medium_PercentCoverage : 0.44 — Zone2 coverage % on a medium
//     map
//   - Zone2_Small_PercentCoverage : 0.42 — Zone2 coverage % on a small map
//   - Zone2_OuterRing_Large_PercentCoverage : 0.0 — Zone2 outer-ring
//     coverage %, large map (never natively initialized, so 0 confirmed by
//     decompilation)
//   - Zone2_OuterRing_Medium_PercentCoverage : 0.0 — Same, medium map
//   - Zone2_OuterRing_Small_PercentCoverage : 0.0 — Same, small map
//
// WARNING for aggressive settings: monster deck exhaustion. These
// parameters increase how many monsters the director draws to populate a
// map (spawn budget, zone saturation, unit cap). Each draw consumes one
// card from that floor's monster deck (MonsterDeckOverridden in the
// ruleset JSON). If the deck is too small for the spawn volume these
// values allow, it can run out before population finishes -> draw from an
// empty list -> an UNHANDLED exception that silently kills the level-load
// coroutine: the level never loads, with no visible error message. Since
// the number of draws depends on the map and the seed, the crash is
// intermittent, not systematic. When raising
// EasySpawnBudgetMultiplier/NormalSpawnBudgetMultiplier/
// AllowedSpawnZoneSaturation/MaxNumberOfUnitsOnBoardHardCap beyond this
// ruleset's values, scale up MonsterDeckOverridden's quantities
// proportionally for every floor deck (EntranceDeckFloor1/2,
// ExitDeckFloor1/2, BossDeck).
//
// Reliability: unlike BossSpawnBudgetAdjustedRule's Transpiler patch
// (which relies on an exact IL pattern), this rule uses ONLY simple field
// writes (reflection + a trivial Postfix) — much more robust against a
// game update, as long as field names stay the same (checked individually
// by name at load time, with a log warning if a field is missing, never
// crashing the rest of the mod).
//
// All default values below are the NATIVE values (so default config =
// unchanged behavior).
//
// Timing bug avoided: Patch(Harmony harmony) is called ONCE, globally, at
// the very start of game loading — BEFORE the active ruleset's JSON is
// even read. Writing Data.GameData.AIDirectorConfig's static fields
// DIRECTLY in Patch() would mean they are ALWAYS written with _config
// still null -> native default values, never the JSON's. Hence: the static
// field writes (part 1 above) happen in OnActivate(Context) — called by
// HouseRules on every (re)activation of the ruleset (once per map/floor),
// i.e. AFTER the JSON has been imported and the constructor with Config
// has already run. Part 2 (Postfix on AIDirectorController2's constructor)
// doesn't have this problem: its body re-reads _config LIVE every time a
// new controller is built (so after activation), not just once at load
// time.

namespace DoriathMod.Rules
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using HarmonyLib;
    using HouseRules.Core.Types;

    public sealed class DoriathPointAIDirectorConfigRule : Rule, IConfigWritable<Dictionary<string, double>>, IPatchable, IMultiplayerSafe
    {
        public override string Description =>
            "Adjusts AI Director spawn budget, zone saturation/coverage percentages, and per-zone enemy count caps (Doriath Progressive)";

        // Native default values.
        private static readonly Dictionary<string, double> NativeDefaults = new Dictionary<string, double>
        {
            // -- Data.GameData.AIDirectorConfig (static fields) --
            { "EasySpawnBudgetMultiplier",   0.3 },
            { "NormalSpawnBudgetMultiplier", 1.0 },
            { "ActivePowerIndexInLevelSoftRoof", 275 },
            { "MaxNumberOfUnitsOnBoardHardCap",  50 },
            { "AllowedSpawnZoneSaturation",  0.8 },
            { "SpikeTrigger_MediumMap_ZoneDiscoveryPercentage", 0.5 },
            { "SpikeTrigger_LargeMap_ZoneDiscoveryPercentage",  0.6 },
            { "MaxNumberOfEasyUnitsPerZone",   7 },
            { "MaxNumberOfMediumUnitsPerZone", 5 },
            { "MaxNumberOfStrongUnitsPerZone", 4 },

            // -- Boardgame.AIDirector.AIDirectorController2 (instance fields) --
            { "Zone1_Large_PercentCoverage",  0.25 },
            { "Zone1_Medium_PercentCoverage", 0.32 },
            { "Zone1_Small_PercentCoverage",  0.32 },
            { "Zone2_Large_PercentCoverage",  0.46 },
            { "Zone2_Medium_PercentCoverage", 0.44 },
            { "Zone2_Small_PercentCoverage",  0.42 },
            { "Zone2_OuterRing_Large_PercentCoverage",  0.0 },
            { "Zone2_OuterRing_Medium_PercentCoverage", 0.0 },
            { "Zone2_OuterRing_Small_PercentCoverage",  0.0 },
        };

        // Fields that live on AIDirectorController2 (so handled by the constructor Postfix) —
        // everything else is on AIDirectorConfig (static, handled directly in Patch()).
        private static readonly HashSet<string> InstanceFieldNames = new HashSet<string>
        {
            "Zone1_Large_PercentCoverage", "Zone1_Medium_PercentCoverage", "Zone1_Small_PercentCoverage",
            "Zone2_Large_PercentCoverage", "Zone2_Medium_PercentCoverage", "Zone2_Small_PercentCoverage",
            "Zone2_OuterRing_Large_PercentCoverage", "Zone2_OuterRing_Medium_PercentCoverage", "Zone2_OuterRing_Small_PercentCoverage",
        };

        private static Dictionary<string, double>? _config;

        public DoriathPointAIDirectorConfigRule() { }
        public DoriathPointAIDirectorConfigRule(Dictionary<string, double> value) { _config = value; }

        public Dictionary<string, double> GetConfigObject() => _config ?? new Dictionary<string, double>(NativeDefaults);

        protected override void OnActivate(Context context)
        {
            // Runs in OnActivate (instead of Patch()) to execute AFTER the ruleset's JSON has
            // been imported and _config already populated — see the class-level remarks.
            // Idempotent: safe to call again on every (re)activation (once per map/floor).
            ApplyStaticAIDirectorConfig();
        }

        protected override void OnDeactivate(Context context) { }

        // Part 1 — Data.GameData.AIDirectorConfig's static fields. Called from
        // OnActivate (NOT Patch() — see the class-level remarks) to be certain
        // _config is read AFTER it's loaded from the JSON.
        private static void ApplyStaticAIDirectorConfig()
        {
            var values = _config ?? NativeDefaults;

            var configType = AccessTools.TypeByName("Data.GameData.AIDirectorConfig");
            if (configType == null)
            {
                Plugin.Log?.LogWarning("[DoriathPointAIDirectorConfigRule] Data.GameData.AIDirectorConfig not found — static values (budget/percentage/caps) not applied.");
                return;
            }

            try
            {
                // Force the native .cctor to run BEFORE anything gets overwritten, so it can
                // never "catch up" and stomp on our values afterward.
                RuntimeHelpers.RunClassConstructor(configType.TypeHandle);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[DoriathPointAIDirectorConfigRule] Could not force AIDirectorConfig's .cctor: {ex.Message}");
            }

            foreach (var kvp in values)
            {
                if (InstanceFieldNames.Contains(kvp.Key)) continue; // handled by the Postfix (part 2)
                if (!NativeDefaults.ContainsKey(kvp.Key))
                {
                    Plugin.Log?.LogWarning($"[DoriathPointAIDirectorConfigRule] Unknown config key ignored: \"{kvp.Key}\".");
                    continue;
                }

                var field = AccessTools.Field(configType, kvp.Key);
                if (field == null || !field.IsStatic)
                {
                    Plugin.Log?.LogWarning($"[DoriathPointAIDirectorConfigRule] Static field \"{kvp.Key}\" not found on AIDirectorConfig — ignored, native value kept.");
                    continue;
                }

                if (!TrySetNumericField(field, null, kvp.Value, out var applied))
                {
                    Plugin.Log?.LogWarning($"[DoriathPointAIDirectorConfigRule] Unexpected field type for \"{kvp.Key}\" — ignored.");
                    continue;
                }

                Plugin.Log?.LogInfo($"[DoriathPointAIDirectorConfigRule] (OnActivate) AIDirectorConfig.{kvp.Key} = {applied}");
            }
        }

        private static void Patch(Harmony harmony)
        {
            // Part 2 — AIDirectorController2's instance fields, overwritten right after
            // construction via a classic Harmony Postfix on the constructor. The Harmony
            // patch is installed once, at load time, but its body (see below) re-reads
            // _config LIVE on every controller construction — so after ruleset activation,
            // no timing bug here.
            var controllerType = AccessTools.TypeByName("Boardgame.AIDirector.AIDirectorController2");
            if (controllerType == null)
            {
                Plugin.Log?.LogWarning("[DoriathPointAIDirectorConfigRule] AIDirectorController2 not found — zone coverage percentages (Zone1/Zone2/OuterRing) not applied.");
                return;
            }

            var ctor = AccessTools.Constructor(controllerType, Type.EmptyTypes);
            if (ctor == null)
            {
                Plugin.Log?.LogWarning("[DoriathPointAIDirectorConfigRule] AIDirectorController2's parameterless constructor not found — zone coverage percentages not applied.");
                return;
            }

            harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(DoriathPointAIDirectorConfigRule), nameof(AIDirectorController2_Ctor_Postfix)));
        }

        private static void AIDirectorController2_Ctor_Postfix(object __instance)
        {
            var values = _config ?? NativeDefaults;
            var controllerType = __instance.GetType();

            foreach (var name in InstanceFieldNames)
            {
                if (!values.TryGetValue(name, out var value)) continue;

                var field = AccessTools.Field(controllerType, name);
                if (field == null || field.IsStatic)
                {
                    Plugin.Log?.LogWarning($"[DoriathPointAIDirectorConfigRule] Instance field \"{name}\" not found on AIDirectorController2 — ignored.");
                    continue;
                }

                if (!TrySetNumericField(field, __instance, value, out var applied))
                {
                    Plugin.Log?.LogWarning($"[DoriathPointAIDirectorConfigRule] Unexpected field type for \"{name}\" — ignored.");
                    continue;
                }

                Plugin.Log?.LogInfo($"[DoriathPointAIDirectorConfigRule] AIDirectorController2.{name} = {applied}");
            }
        }

        // Writes a numeric value (stored as double in the JSON config) into a field whose
        // real type may be float OR int depending on the native field — converts to the
        // right type instead of assuming either one.
        private static bool TrySetNumericField(FieldInfo field, object? target, double value, out object applied)
        {
            applied = value;
            try
            {
                if (field.FieldType == typeof(float))
                {
                    var f = (float)value;
                    field.SetValue(target, f);
                    applied = f;
                    return true;
                }
                if (field.FieldType == typeof(int))
                {
                    var i = (int)Math.Round(value);
                    field.SetValue(target, i);
                    applied = i;
                    return true;
                }
                if (field.FieldType == typeof(double))
                {
                    field.SetValue(target, value);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[DoriathPointAIDirectorConfigRule] Failed to write field \"{field.Name}\": {ex.Message}");
                return false;
            }
        }
    }
}
