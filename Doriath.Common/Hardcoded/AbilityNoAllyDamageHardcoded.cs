// ============================================================
//  Doriath — AbilityNoAllyDamageHardcoded.cs
// ============================================================
//
// Makes DeathBeam harmless to allies. DeathBeam is a ray that natively hits
// everything on its path, party members included, and neither the base game
// nor HouseRules exposes a per-ability "no friendly fire" setting
// (PartyDamageOverriddenRule only covers Electricity/Zap damage between
// players). This cancels the damage when a listed ability is resolved from
// one player piece onto another.
//
// Parameters:
//   Abilities  DeathBeam  — abilities that deal no player-to-player damage
//
// Patch: Prefix on every Damage.DealDamage overload
//
// Fully defensive: every lookup is guarded, a missing type/field/method logs
// a warning and skips. Only "listed ability + player on player" is blocked;
// damage to monsters and damage from monsters to players is untouched.


namespace DoriathMod.Hardcoded
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using DataKeys;
    using HarmonyLib;

    public static class AbilityNoAllyDamageHardcoded
    {
        private static readonly List<AbilityKey> Abilities = new List<AbilityKey>
        {
            AbilityKey.DeathBeam,
        };

        // Name strings used for the quick filter on damage.ToString(), which
        // contains "for ability <Name>". Avoids having to locate the field that
        // stores the AbilityKey on the Damage object, which is not confirmed.
        private static readonly string[] AbilityNames = Abilities.Select(a => a.ToString()).ToArray();

        public static void Patch(Harmony harmony)
        {
            var damageType = AccessTools.TypeByName("Boardgame.BoardEntities.Abilities.Damage");

            // Fallback if the namespace above is wrong: look up a class simply
            // named "Damage" that declares DealDamage, in any loaded assembly.
            if (damageType == null)
            {
                damageType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch { return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t => t.Name == "Damage" &&
                        t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                            .Any(m => m.Name == "DealDamage"));
            }

            if (damageType == null)
            {
                Plugin.Log?.LogWarning("[AbilityNoAllyDamageHardcoded] Damage type not found — ally-damage guard disabled.");
                return;
            }

            var methods = damageType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name == "DealDamage")
                .ToList();

            if (methods.Count == 0)
            {
                Plugin.Log?.LogWarning("[AbilityNoAllyDamageHardcoded] No DealDamage method found — ally-damage guard disabled.");
                return;
            }

            foreach (var m in methods)
            {
                try
                {
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(AbilityNoAllyDamageHardcoded), nameof(DealDamage_NoAllyDamage_Prefix)));
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[AbilityNoAllyDamageHardcoded] Failed to patch the guard on one DealDamage overload: {ex.Message}");
                }
            }

            Plugin.Log?.LogInfo(
                "[AbilityNoAllyDamageHardcoded] Ally-damage guard installed on Damage.DealDamage — blocks all player-to-player damage for: " +
                string.Join(", ", Abilities));
        }

        // Pulls the underlying Piece out of an argument that may be either a
        // Piece directly or a Target (struct with a "piece" field).
        private static object ExtractPiece(object arg)
        {
            if (arg == null) return null;
            var t = arg.GetType();
            var pieceField = t.GetField("piece", BindingFlags.Public | BindingFlags.Instance);
            return pieceField != null ? pieceField.GetValue(arg) : arg;
        }

        // Same IsPlayer() the game's own party-damage rule relies on.
        private static bool IsPlayerPiece(object piece)
        {
            if (piece == null) return false;
            var method = piece.GetType().GetMethod("IsPlayer", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return false;
            return method.Invoke(piece, null) is bool b && b;
        }

        private static bool DealDamage_NoAllyDamage_Prefix(object[] __args, MethodBase __originalMethod, ref int __result)
        {
            try
            {
                var parms = __originalMethod.GetParameters();
                object targetArg = null, attackerArg = null, damageArg = null;
                for (var i = 0; i < __args.Length && i < parms.Length; i++)
                {
                    switch (parms[i].Name)
                    {
                        case "target":   targetArg = __args[i];   break;
                        case "attacker": attackerArg = __args[i]; break;
                        case "damage":   damageArg = __args[i];   break;
                    }
                }

                if (damageArg == null) return true;

                var damageText = damageArg.ToString() ?? "";
                var matchedAbility = AbilityNames.FirstOrDefault(name => damageText.Contains(name));
                if (matchedAbility == null) return true;

                var targetPiece = ExtractPiece(targetArg);
                var attackerPiece = ExtractPiece(attackerArg);
                if (targetPiece == null || attackerPiece == null) return true;

                if (!IsPlayerPiece(targetPiece) || !IsPlayerPiece(attackerPiece)) return true;

                Plugin.Log?.LogInfo(
                    $"[AbilityNoAllyDamageHardcoded] Ally damage blocked for {matchedAbility} — attacker and target are both players, damage cancelled (was: {damageText}).");

                __result = 0;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[AbilityNoAllyDamageHardcoded] Error in the ally-damage guard (damage left unchanged to be safe): {ex.Message}");
                return true;
            }
        }
    }
}
