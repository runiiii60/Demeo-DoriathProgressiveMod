namespace DoriathMod.Rules
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using DataKeys;
    using HarmonyLib;

    /// <summary>
    /// DeathBeam (Sorcerer) must not deal damage to allies, just like MinionCharge
    /// (which by its native design never hits an ally: a direct-target ability aimed
    /// at a single enemy). No HouseRules JSON parameter exposes this per ability
    /// (PartyDamageOverriddenRule, already active in the JSON, only covers
    /// Electricity/Zap damage between players). DeathBeam, on the other hand, is a
    /// beam that hits everything in its path, allies included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same technique as IceExplosion's self-damage safeguard (see
    /// AbilityMayNotTargetSelfRule.cs): a Harmony Prefix on Damage.DealDamage()
    /// (type looked up by full name, then by short name) that cancels the result
    /// (returns 0, skips the original method) as soon as:
    /// </para>
    /// <para>
    /// 1) the attacker AND the target are both "player" Pieces (IsPlayer(), the same
    /// method used natively in PartyDamageOverriddenRule),
    /// </para>
    /// <para>
    /// 2) AND the ability involved is part of the `Abilities` list below (filtered via
    /// ToString() of the Damage object, same method as the other rule — the exact
    /// internal field storing the AbilityKey on Damage was not confirmed by
    /// decompilation).
    /// </para>
    /// <para>
    /// Deliberately generic (a list, like AbilityMayNotTargetSelfHardcoded): adding an
    /// ability here is enough to extend this behavior later, without duplicating the
    /// mechanism.
    /// </para>
    /// <para>
    /// Reliability: fully defensive (try/catch everywhere, log + skip if a
    /// type/field/method is not found, never crashes). Blocks ONLY the precise case
    /// "listed ability + player on player" — everything else (damage to monsters,
    /// damage from a monster to a player) proceeds normally.
    /// </para>
    /// </remarks>
    public static class AbilityNoAllyDamageHardcoded
    {
        private static readonly List<AbilityKey> Abilities = new List<AbilityKey>
        {
            AbilityKey.DeathBeam,
        };

        private static readonly string[] AbilityNames = Abilities.Select(a => a.ToString()).ToArray();

        public static void Patch(Harmony harmony)
        {
            var damageType = AccessTools.TypeByName("Boardgame.BoardEntities.Abilities.Damage");

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
                    Plugin.Log?.LogWarning($"[AbilityNoAllyDamageHardcoded] Failed to patch the guard onto a DealDamage overload: {ex.Message}");
                }
            }

            Plugin.Log?.LogInfo(
                "[AbilityNoAllyDamageHardcoded] Ally-damage guard installed on Damage.DealDamage — blocks all player-to-player damage for: " +
                string.Join(", ", Abilities));
        }

        private static object ExtractPiece(object arg)
        {
            if (arg == null) return null;
            var t = arg.GetType();
            var pieceField = t.GetField("piece", BindingFlags.Public | BindingFlags.Instance);
            return pieceField != null ? pieceField.GetValue(arg) : arg;
        }

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
                Plugin.Log?.LogWarning($"[AbilityNoAllyDamageHardcoded] Error in the ally-damage guard (damage left unchanged as a safety fallback): {ex.Message}");
                return true;
            }
        }
    }
}
