namespace DoriathMod.Rules
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Boardgame.BoardEntities.Abilities;
    using DataKeys;
    using HarmonyLib;

    /// <summary>
    /// Fixes a native bug: IceExplosion (Warlock) deals damage to the Warlock
    /// themself, even though they should be immune to their own attack.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two mechanisms combined:
    /// </para>
    /// <para>
    /// 1) mayTargetSelf = false on IceExplosion (native Ability field), applied once
    /// at game startup via AbilityFactory.LoadAbility(key).OnLoaded(...). Not
    /// sufficient on its own: according to the logs, this field only governs initial
    /// target selection, not damage resolution for an area ability (the explosion
    /// hits everything within its radius, regardless of this flag).
    /// </para>
    /// <para>
    /// 2) The real safeguard: a Prefix on Damage.DealDamage() that blocks the damage
    /// (returns 0, skips the original method) as soon as the target piece IS
    /// LITERALLY THE SAME INSTANCE as the attacking piece (ReferenceEquals) AND the
    /// attack comes from an ability in the `Abilities` list below. Filtered via
    /// ToString() of the Damage object (the exact internal field storing the
    /// AbilityKey was not identified by decompilation).
    /// </para>
    /// <para>
    /// Both mechanisms are kept together: the first remains harmless and may have a
    /// real effect on other simple, non-AOE single-target abilities; the second is
    /// the safety net that guarantees the result regardless of the actual internal
    /// mechanism.
    /// </para>
    /// <para>
    /// Reliability: fully defensive (try/catch everywhere, log + skip if a
    /// type/field/method is not found, never crashes). The Prefix ONLY blocks the
    /// precise case "same piece on both sides + listed ability" — any other damage
    /// resolution proceeds normally.
    /// </para>
    /// </remarks>
    public static class AbilityMayNotTargetSelfHardcoded
    {
        /// <summary>
        /// Shared by both mechanisms below (mayTargetSelf mutation AND damage
        /// blocking on DealDamage).
        /// </summary>
        private static readonly List<AbilityKey> Abilities = new List<AbilityKey>
        {
            AbilityKey.IceExplosion,
        };

        /// <summary>
        /// Sub-list of names (string) used for the quick filter on damage.ToString()
        /// (which contains "for ability &lt;Name&gt;") — avoids having to locate by
        /// reflection the exact field that stores the AbilityKey on the Damage
        /// object (never confirmed by decompilation; the same caution was applied
        /// for Zap/BarkArmor elsewhere).
        /// </summary>
        private static readonly string[] AbilityNames = Abilities.Select(a => a.ToString()).ToArray();

        public static void Patch(Harmony harmony)
        {
            PatchMayTargetSelfMutation(harmony);
            PatchDealDamageSelfHitGuard(harmony);
        }

        /// <summary>Mechanism 1: mayTargetSelf mutation (kept, harmless).</summary>
        private static void PatchMayTargetSelfMutation(Harmony harmony)
        {
            var lifecycleDirectorType = AccessTools.TypeByName("HouseRules.Core.LifecycleDirector");
            if (lifecycleDirectorType == null)
            {
                Plugin.Log?.LogWarning("[AbilityMayNotTargetSelfHardcoded] HouseRules.Core.LifecycleDirector introuvable — mutation mayTargetSelf ignoree.");
                return;
            }

            var hookMethod = AccessTools.Method(lifecycleDirectorType, "GameStartup_InitializeGame_Postfix");
            if (hookMethod == null)
            {
                Plugin.Log?.LogWarning("[AbilityMayNotTargetSelfHardcoded] GameStartup_InitializeGame_Postfix introuvable — mutation mayTargetSelf ignoree.");
                return;
            }

            harmony.Patch(
                hookMethod,
                postfix: new HarmonyMethod(typeof(AbilityMayNotTargetSelfHardcoded), nameof(ApplyAfterContextReady)));

            Plugin.Log?.LogInfo(
                "[AbilityMayNotTargetSelfHardcoded] Mutation mayTargetSelf=false posee (mecanisme historique, conserve par securite) — capacites concernees : " +
                string.Join(", ", Abilities));
        }

        private static void ApplyAfterContextReady()
        {
            var lifecycleDirectorType = AccessTools.TypeByName("HouseRules.Core.LifecycleDirector");
            var field = lifecycleDirectorType == null ? null : AccessTools.Field(lifecycleDirectorType, "_abilityFactory");
            var abilityFactory = field?.GetValue(null) as AbilityFactory;

            if (abilityFactory == null)
            {
                Plugin.Log?.LogWarning("[AbilityMayNotTargetSelfHardcoded] AbilityFactory introuvable au demarrage de la partie — mutation mayTargetSelf ignoree pour cette partie.");
                return;
            }

            foreach (var key in Abilities)
            {
                var abilityKey = key;
                abilityFactory.LoadAbility(abilityKey).OnLoaded(ability =>
                {
                    ability.mayTargetSelf = false;
                    Plugin.Log?.LogInfo($"[AbilityMayNotTargetSelfHardcoded] mayTargetSelf=false applique a {abilityKey}.");
                });
            }
        }

        /// <summary>Mechanism 2: safeguard on DealDamage (the real safety net).</summary>
        private static void PatchDealDamageSelfHitGuard(Harmony harmony)
        {
            // Try the full name first (most likely namespace given the rest of the
            // mod, cf. Boardgame.BoardEntities.Abilities.Ability/AbilityFactory
            // already used here and in ProgressiveLevelRule.cs/PieceProgressLostRule.cs).
            var damageType = AccessTools.TypeByName("Boardgame.BoardEntities.Abilities.Damage");

            // Safety net: if the exact namespace guessed above is wrong, look up by
            // simple class name "Damage" across all loaded assemblies (avoids
            // depending on decompilation to confirm the namespace).
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
                Plugin.Log?.LogWarning("[AbilityMayNotTargetSelfHardcoded] Type Damage introuvable (namespace exact + recherche globale) — garde-fou auto-degats desactive.");
                return;
            }

            var methods = damageType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name == "DealDamage")
                .ToList();

            if (methods.Count == 0)
            {
                Plugin.Log?.LogWarning("[AbilityMayNotTargetSelfHardcoded] Aucune methode DealDamage trouvee — garde-fou auto-degats desactive.");
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
                    Plugin.Log?.LogWarning($"[AbilityMayNotTargetSelfHardcoded] Echec du patch garde-fou sur une surcharge de DealDamage : {ex.Message}");
                }
            }

            Plugin.Log?.LogInfo(
                "[AbilityMayNotTargetSelfHardcoded] Garde-fou auto-degats pose sur Damage.DealDamage — bloque tout degat ou la cible EST la piece attaquante, pour : " +
                string.Join(", ", Abilities));
        }

        /// <summary>
        /// Extracts the underlying Piece object from an argument that can be either a
        /// Piece directly, or a Target (a struct with a "piece" field).
        /// </summary>
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

                // Quick filter: is the ability involved part of the list?
                var damageText = damageArg.ToString() ?? "";
                var matchedAbility = AbilityNames.FirstOrDefault(name => damageText.Contains(name));
                if (matchedAbility == null) return true;

                var targetPiece = ExtractPiece(targetArg);
                var attackerPiece = ExtractPiece(attackerArg);
                if (targetPiece == null || attackerPiece == null) return true;

                // Self-damage in the strict sense: literally the same piece instance.
                if (!ReferenceEquals(targetPiece, attackerPiece)) return true;

                Plugin.Log?.LogInfo(
                    $"[AbilityMayNotTargetSelfHardcoded] Auto-degat bloque pour {matchedAbility} — la piece attaquante et la cible sont la meme instance, degats annules (etaient : {damageText}).");

                __result = 0;
                return false; // skip the original method: no damage applied, no side effects tied to the damage.
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[AbilityMayNotTargetSelfHardcoded] Erreur dans le garde-fou auto-degats (degats laisses inchanges par securite) : {ex.Message}");
                return true;
            }
        }
    }
}
