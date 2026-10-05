// ============================================================
//  Doriath — TelemetryDamageCrashGuardHardcoded.cs
// ============================================================
//
// Prevents a game freeze when damage is dealt or a unit is killed by a piece
// that has no team, such as a lamp pushed into gas and exploding. The game's
// telemetry reads the attacker's team (MotherTracker -> TeamData.GetOtherTeam),
// which throws an ArgumentException for Team.None. Thrown from inside the
// gas-ignition coroutine, that exception kills the coroutine, the sequence
// never completes and the game hangs — the current player's turn never ends.
//
// Guarding MotherTracker.TrackDamageDealt alone was not enough: when the
// explosion also kills something, TrackUnitDefeated reads the same team and
// throws in the same way. So the finalizer goes on every Track* method of
// MotherTracker instead of one named method. These only submit metrics, so
// discarding their failures has no gameplay effect, and a new telemetry call
// added by a game update is covered without touching this file again.
//
// Patch: Finalizer on every MotherTracker.Track* method

namespace DoriathMod.Hardcoded
{
    using System;
    using System.Linq;
    using System.Reflection;
    using HarmonyLib;

    public static class TelemetryDamageCrashGuardHardcoded
    {
        public static void Patch(Harmony harmony)
        {
            var tracker = AccessTools.TypeByName("MotherTracker");
            var methods = tracker == null
                ? new MethodInfo[0]
                : tracker.GetMethods(AccessTools.all)
                    .Where(m => m.Name.StartsWith("Track", StringComparison.Ordinal)
                        && !m.ContainsGenericParameters
                        && m.GetMethodBody() != null)
                    .ToArray();

            if (methods.Length == 0)
            {
                Plugin.Log?.LogWarning("[TelemetryDamageCrashGuardHardcoded] No MotherTracker.Track* method found — patch skipped.");
                return;
            }

            var finalizer = new HarmonyMethod(typeof(TelemetryDamageCrashGuardHardcoded), nameof(Finalizer));
            foreach (var method in methods)
            {
                harmony.Patch(method, finalizer: finalizer);
            }

            Plugin.Log?.LogInfo($"[TelemetryDamageCrashGuardHardcoded] Damage telemetry exceptions neutralised on {methods.Length} MotherTracker.Track* method(s) (freeze when a lamp explodes in gas).");
        }

        private static Exception Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception != null)
                Plugin.Log?.LogWarning($"[TelemetryDamageCrashGuardHardcoded] Exception avalee dans MotherTracker.{__originalMethod?.Name} : {__exception.GetType().Name} — {__exception.Message}");

            // Returning null tells Harmony the exception is handled.
            return null;
        }
    }
}
