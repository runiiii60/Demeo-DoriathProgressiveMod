// ============================================================
//  Doriath — AbilityMayNotTargetSelfHardcoded.cs
// ============================================================
//
// Stops an area-of-effect ability from hurting the hero who cast it:
// IceExplosion (Warlock) and TelekineticBurst (Barbarian) no longer damage
// their own caster. Setting the ability's mayTargetSelf flag is not enough
// on its own — that flag governs initial target selection, not the damage
// resolution of an area effect, which hits everything in radius regardless.
// The flag is still cleared (harmless, and it may matter for single-target
// abilities that do consult it), while the actual guarantee comes from a
// guard on damage resolution itself.
//
// Parameters:
//   Abilities  IceExplosion, TelekineticBurst  — abilities whose caster is immune to their own damage
//
// Patch: Postfix on HouseRules.Core.LifecycleDirector.GameStartup_InitializeGame_Postfix
//        (mayTargetSelf mutation), plus a Prefix on every Damage.DealDamage
//        overload (self-damage guard)
//
// Fully defensive: every lookup is guarded, a missing type/field/method logs
// a warning and skips. The guard only cancels the exact case "same piece on
// both sides + listed ability"; all other damage resolution is untouched.


namespace DoriathMod.Hardcoded
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Boardgame.BoardEntities.Abilities;
    using DataKeys;
    using HarmonyLib;

    public static class AbilityMayNotTargetSelfHardcoded
    {
        // Shared by both mechanisms below (mayTargetSelf mutation and the
        // DealDamage guard).
        private static readonly List<AbilityKey> Abilities = new List<AbilityKey>
        {
            AbilityKey.IceExplosion,

            // The guard is deliberately not bound to a hero: TelekineticBurst is
            // currently a Barbarian-only card, but if it ever lands in another
            // class's pool it is already covered.
            AbilityKey.TelekineticBurst,
        };

        // Name strings used for the quick filter on damage.ToString(), which
        // contains "for ability <Name>". Avoids having to locate the field that
        // stores the AbilityKey on the Damage object, which is not confirmed.
        private static readonly string[] AbilityNames = Abilities.Select(a => a.ToString()).ToArray();

        public static void Patch(Harmony harmony)
        {
            PatchMayTargetSelfMutation(harmony);
            PatchDealDamageSelfHitGuard(harmony);
        }

        // ── mayTargetSelf mutation ──────────────────────────────────────
        private static void PatchMayTargetSelfMutation(Harmony harmony)
        {
            var lifecycleDirectorType = AccessTools.TypeByName("HouseRules.Core.LifecycleDirector");
            if (lifecycleDirectorType == null)
            {
                Plugin.Log?.LogWarning("[AbilityMayNotTargetSelfHardcoded] HouseRules.Core.LifecycleDirector not found — the mayTargetSelf change is skipped.");
                return;
            }

            var hookMethod = AccessTools.Method(lifecycleDirectorType, "GameStartup_InitializeGame_Postfix");
            if (hookMethod == null)
            {
                Plugin.Log?.LogWarning("[AbilityMayNotTargetSelfHardcoded] GameStartup_InitializeGame_Postfix not found — the mayTargetSelf change is skipped.");
                return;
            }

            harmony.Patch(
                hookMethod,
                postfix: new HarmonyMethod(typeof(AbilityMayNotTargetSelfHardcoded), nameof(ApplyAfterContextReady)));

            Plugin.Log?.LogInfo(
                "[AbilityMayNotTargetSelfHardcoded] mayTargetSelf=false set (older mechanism, kept as a safety net) — abilities affected: " +
                string.Join(", ", Abilities));
        }

        private static void ApplyAfterContextReady()
        {
            var lifecycleDirectorType = AccessTools.TypeByName("HouseRules.Core.LifecycleDirector");
            var field = lifecycleDirectorType == null ? null : AccessTools.Field(lifecycleDirectorType, "_abilityFactory");
            var abilityFactory = field?.GetValue(null) as AbilityFactory;

            if (abilityFactory == null)
            {
                Plugin.Log?.LogWarning("[AbilityMayNotTargetSelfHardcoded] AbilityFactory not found at game start — the mayTargetSelf change is skipped for this game.");
                return;
            }

            foreach (var key in Abilities)
            {
                var abilityKey = key;
                abilityFactory.LoadAbility(abilityKey).OnLoaded(ability =>
                {
                    ability.mayTargetSelf = false;
                    Plugin.Log?.LogInfo($"[AbilityMayNotTargetSelfHardcoded] mayTargetSelf=false applied to {abilityKey}.");
                });
            }
        }

        // ── Self-damage guard on Damage.DealDamage ──────────────────────
        private static void PatchDealDamageSelfHitGuard(Harmony harmony)
        {
            var damageType = AccessTools.TypeByName("Boardgame.BoardEntities.Abilities.Damage");

            // Fallback if the namespace guessed above is wrong: look up a class
            // simply named "Damage" that declares DealDamage, in any loaded
            // assembly.
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
                Plugin.Log?.LogWarning("[AbilityMayNotTargetSelfHardcoded] Damage type not found (exact namespace and global search) — self-damage guard disabled.");
                return;
            }

            var methods = damageType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name == "DealDamage")
                .ToList();

            if (methods.Count == 0)
            {
                Plugin.Log?.LogWarning("[AbilityMayNotTargetSelfHardcoded] No DealDamage method found — self-damage guard disabled.");
                return;
            }

            foreach (var m in methods)
            {
                try
                {
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(AbilityMayNotTargetSelfHardcoded), nameof(DealDamage_SelfHitGuard_Prefix)));
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[AbilityMayNotTargetSelfHardcoded] Failed to patch the guard on one DealDamage overload: {ex.Message}");
                }
            }

            Plugin.Log?.LogInfo(
                "[AbilityMayNotTargetSelfHardcoded] Self-damage guard installed on Damage.DealDamage — blocks any damage where the target IS the attacking piece, for: " +
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

        private static bool DealDamage_SelfHitGuard_Prefix(object[] __args, MethodBase __originalMethod, ref int __result)
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

                // Quick filter: is the ability in play one of ours?
                var damageText = damageArg.ToString() ?? "";
                var matchedAbility = AbilityNames.FirstOrDefault(name => damageText.Contains(name));
                if (matchedAbility == null) return true;

                var targetPiece = ExtractPiece(targetArg);
                var attackerPiece = ExtractPiece(attackerArg);
                if (targetPiece == null || attackerPiece == null) return true;

                // Self-damage in the strict sense: literally the same Piece
                // instance. Piece is a reference type, so ReferenceEquals never
                // matches two distinct pieces of the same kind.
                if (!ReferenceEquals(targetPiece, attackerPiece)) return true;

                Plugin.Log?.LogInfo(
                    $"[AbilityMayNotTargetSelfHardcoded] Self-damage blocked for {matchedAbility} — attacking piece and target are the same instance, damage cancelled (was: {damageText}).");

                __result = 0;
                return false; // Skip the original: no damage and no damage-driven side effects.
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[AbilityMayNotTargetSelfHardcoded] Error in the self-damage guard (damage left unchanged to be safe): {ex.Message}");
                return true;
            }
        }
    }
}
