// ============================================================
//  Doriath — TelemetryDamageCrashGuardHardcoded.cs
// ============================================================
//
// Prevents a game freeze when damage is dealt by a piece that has no team,
// such as a lamp pushed into gas and exploding. The game's telemetry reads
// the attacker's team (MotherTracker.TrackDamageDealt ->
// TeamData.GetOtherTeam), which throws an ArgumentException for Team.None.
// Thrown from inside the gas-ignition coroutine, that exception kills the
// coroutine, the sequence never completes and the game hangs.
//
// The finalizer swallows the exception. TrackDamageDealt only submits
// metrics, so discarding its failures has no gameplay effect.
//
// Patch: Finalizer on MotherTracker.TrackDamageDealt

namespace DoriathMod.Hardcoded
{
    using System;
    using HarmonyLib;

    public static class TelemetryDamageCrashGuardHardcoded
    {
        public static void Patch(Harmony harmony)
        {
            var method = AccessTools.Method(AccessTools.TypeByName("MotherTracker"), "TrackDamageDealt");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[TelemetryDamageCrashGuardHardcoded] MotherTracker.TrackDamageDealt not found — patch skipped.");
                return;
            }

            harmony.Patch(method, finalizer: new HarmonyMethod(typeof(TelemetryDamageCrashGuardHardcoded), nameof(Finalizer)));
            Plugin.Log?.LogInfo("[TelemetryDamageCrashGuardHardcoded] Damage telemetry exceptions neutralised (freeze when a lamp explodes in gas).");
        }

        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
                Plugin.Log?.LogWarning($"[TelemetryDamageCrashGuardHardcoded] Exception avalee dans MotherTracker.TrackDamageDealt : {__exception.GetType().Name} — {__exception.Message}");

            // Returning null tells Harmony the exception is handled.
            return null;
        }
    }
}
