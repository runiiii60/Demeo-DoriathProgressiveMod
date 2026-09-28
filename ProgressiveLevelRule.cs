namespace DoriathMod.Rules
{
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Boardgame;
using Boardgame.BoardEntities;
using Boardgame.BoardEntities.Abilities;
using Boardgame.SerializableEvents;
using DataKeys;
using HarmonyLib;
using HouseRules.Core.Types;
using UnityEngine;

    public sealed class DoriathLevelUpRule : Rule, IConfigWritable<bool>, IPatchable, IMultiplayerSafe
    {
        /// <summary>
        /// Short one-line description shown in the native Panel 1 ("Active Rules").
        /// </summary>
        /// <remarks>
        /// Panel 1 must list ONLY the rule name — the perk/level details live
        /// exclusively on Panel 2 (<c>DoriathPerksPanel</c>). The description is
        /// intentionally short, matching the one-liner style used by the ~38 other
        /// rules in the ruleset.
        /// </remarks>
        public override string Description => "Heroes level up by filling the mana bar.";

        private static Context? _context;
        private static bool _isActivated;
        private const int MaxLevel = 10; // formerly 11: the "Gold on CRIT" level was merged into level 4, 9 levels total
        private static readonly List<MethodInfo> _suppressedMethods = new();
        private static GameObject? _perksPanel;
        private static MethodInfo? _panel1IntroPatchedMethod;
        private static MethodInfo? _bossFallbackPatchedMethod;
        private static MethodInfo? _followPlayerMeleePlanMethod;
        private static MethodInfo? _pieceIsBotMethod;

        /// <summary>
        /// Instances of FollowPlayerMeleeBehaviour created by our Postfix below (one per
        /// spawned target piece).
        /// </summary>
        /// <remarks>
        /// Used to restrict the IsBot() patch to ONLY the evaluation of these specific
        /// instances (see <see cref="PatchFollowPlayerMeleeBehaviourGate"/>).
        /// </remarks>
        private static readonly HashSet<object> _ourFallbackInstances = new();
        private static bool _forceIsBotForFallback;

        /// <summary>
        /// BoardPieceIds of the pieces affected by the "wasted actions" fix below.
        /// </summary>
        /// <remarks>
        /// ActionPoint is raised via PieceConfigAdjusted, but the native AI is limited
        /// to ~2 useful actions per turn (see <see cref="PatchBossFallbackBehaviour"/>).
        /// The FollowPlayerMeleeBehaviour fallback is active for these 3 bosses.
        /// </remarks>
        private static readonly BoardPieceId[] BossesNeedingFallbackBehaviour =
        {
            BoardPieceId.MotherCy, BoardPieceId.ElvenSummoner, BoardPieceId.RootLord
        };

        /// <summary>
        /// Replacement text for Panel 1 only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// "Doriath (PROGRESSIVE)" is shown in dark purple (distinct from the orange
        /// used by the button/RoomFinder, which reads the JSON "Name" field directly —
        /// see Doriath (PROGRESSIVE).json). "by ruNIIII" is dark gray and bold, to
        /// visually match the "Playing ... ruleset!" style (black).
        /// </para>
        /// <para>
        /// The button/RoomFinder, which reads the JSON "Name" field directly, is not
        /// affected by this replacement text.
        /// </para>
        /// </remarks>
        private const string Panel1CleanIntroText =
            "<color=#000000>Playing</color> <color=#9400D3>Doriath (PROGRESSIVE)</color> <color=#000000>ruleset!</color>\n<color=#333333><b>by ruNIIII</b></color>";

        public DoriathLevelUpRule() { }
        public DoriathLevelUpRule(bool value) { }

        public bool GetConfigObject() => true;

        protected override void OnActivate(Context context)
        {
            _context = context;
            _isActivated = true;
            _level0KnockdownBonusApplied.Clear();
            SuppressGrayAlienAdvancedStats();
            SpawnDoriathPerksPanel();
            PatchPanel1IntroCredit();
            PatchBossFallbackBehaviour();
            PatchFollowPlayerMeleeBehaviourGate();
        }

        protected override void OnDeactivate(Context context)
        {
            _isActivated = false;
            _level0KnockdownBonusApplied.Clear();
            RestoreGrayAlienAdvancedStats();
            DespawnDoriathPerksPanel();
            UnpatchPanel1IntroCredit();
            UnpatchBossFallbackBehaviour();
            UnpatchFollowPlayerMeleeBehaviourGate();
        }

        /// <summary>
        /// Panel 1 ("Active Rules"): cleans up the "Playing X ruleset!" line.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The Ruleset.Name field is multi-line/colored (RoomFinder and Panel 1 both
        /// reuse it as-is), which makes "ruleset!" appear stuck right after
        /// "by ruNIIII" on the same colored line in the native panel.
        /// </para>
        /// <para>
        /// We patch HouseRulesUiGameVr.Initialize() with a Postfix to find, after the
        /// fact, the text component containing this phrase and rewrite it cleanly on
        /// 2 lines. The search uses generic reflection (a public "text" property of
        /// type string) so it depends on neither UnityEngine.UI.Text nor
        /// TMPro.TMP_Text (neither DLL is referenced in this project).
        /// </para>
        /// </remarks>
        private static void PatchPanel1IntroCredit()
        {
            try
            {
                var harmony = Plugin.HarmonyInstance;
                if (harmony == null) return;

                var type = AccessTools.TypeByName("HouseRulesUiGameVr");
                if (type == null) return;

                var method = AccessTools.Method(type, "Initialize");
                if (method == null) return;

                harmony.Patch(
                    method,
                    postfix: new HarmonyMethod(typeof(DoriathLevelUpRule), nameof(HouseRulesUiGameVr_Initialize_Postfix)));
                _panel1IntroPatchedMethod = method;
            }
            catch { }
        }

        private static void UnpatchPanel1IntroCredit()
        {
            try
            {
                var harmony = Plugin.HarmonyInstance;
                if (harmony == null || _panel1IntroPatchedMethod == null) return;
                harmony.Unpatch(_panel1IntroPatchedMethod, HarmonyPatchType.Postfix, harmony.Id);
                _panel1IntroPatchedMethod = null;
            }
            catch { }
        }

        /// <summary>
        /// Fix: bosses stuck at ~2 actions/turn despite ActionPoint=4.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Root cause (from IL analysis of Assembly-CSharp.dll — see PieceAI.CreatePlan
        /// / Behaviour.Prepare): Behaviour.Prepare() resets CurrentScore to -1 before
        /// each evaluation, and PieceAI.CreatePlan() IGNORES any Behaviour whose score
        /// stays &lt;= -1. If ALL of the boss's Behaviours (melee attack, special
        /// spells like Rain/Electricity...) are unavailable this turn (out of range,
        /// internal cooldown, failed random roll), CreatePlan() returns null →
        /// PopulateEnemyAIEvents sends EndTurn IMMEDIATELY, even if ActionPoints
        /// remain (the "ActionPoints &gt; 1" check is NEVER reached in that case).
        /// Since MotherCy/ElvenSummoner/BossTown natively only have 2-3 "special"
        /// actions (designed for their native ActionPoint, lower than 4), they
        /// quickly exhaust their repertoire and end their turn early, independent of
        /// this mod's ActionPoint=4.0 buff from PieceConfigAdjusted.
        /// </para>
        /// <para>
        /// Fix: on spawn (Postfix on PieceSpawner.CreatePieceInternal), we add the
        /// generic native Behaviour FollowPlayerMeleeBehaviour (key
        /// DataKeys.Behaviour.FollowPlayerMeleeAttacker) to these pieces — the same
        /// Behaviour ordinary melee enemies use to close in on/attack a player. It
        /// participates in the SAME scoring as the piece's special Behaviours
        /// (PieceAI.CreatePlan keeps the best score, whether native or added here),
        /// so it only takes over when no special action is usable — a safety net
        /// that consumes the remaining ActionPoint instead of wasting it, without
        /// changing behaviour the rest of the time.
        /// </para>
        /// <para>
        /// IMPORTANT: unlike the 2 previous fixes (RegainAbilityIfMaxxedOut,
        /// replenishCooldownAfterEffectsEnd), which were validated by direct,
        /// unambiguous IL proof, the exact construction/scoring of
        /// FollowPlayerMeleeBehaviour remains a reasonable hypothesis, to be
        /// confirmed in-game. Confirmed by log for ElvenSummoner (it does use its
        /// ActionPoint=4 on nearly all of its turns after this fix, except the very
        /// first turn before any player has been detected).
        /// </para>
        /// </remarks>
        private static void PatchBossFallbackBehaviour()
        {
            try
            {
                var harmony = Plugin.HarmonyInstance;
                if (harmony == null) return;

                var method = AccessTools.Method(typeof(Boardgame.PieceSpawner), "CreatePieceInternal");
                if (method == null) return;

                harmony.Patch(
                    method,
                    postfix: new HarmonyMethod(typeof(DoriathLevelUpRule), nameof(PieceSpawner_CreatePieceInternal_Postfix)));
                _bossFallbackPatchedMethod = method;
            }
            catch { }
        }

        private static void UnpatchBossFallbackBehaviour()
        {
            try
            {
                var harmony = Plugin.HarmonyInstance;
                if (harmony == null || _bossFallbackPatchedMethod == null) return;
                harmony.Unpatch(_bossFallbackPatchedMethod, HarmonyPatchType.Postfix, harmony.Id);
                _bossFallbackPatchedMethod = null;
            }
            catch { }
        }

        private static void PieceSpawner_CreatePieceInternal_Postfix(Piece __result)
        {
            try
            {
                if (!_isActivated || __result == null || _context == null) return;

                var id = __result.boardPieceId;
                bool isTargetBoss = false;
                foreach (var bossId in BossesNeedingFallbackBehaviour)
                {
                    if (id == bossId) { isTargetBoss = true; break; }
                }
                if (!isTargetBoss) return;

                var pieceAI = __result.pieceAI;
                if (pieceAI == null) return;

                var behavioursField = Traverse.Create(pieceAI)
                    .Field<List<KeyValuePair<DataKeys.Behaviour, Boardgame.BoardEntities.AI.Behaviour>>>("behaviours");
                var behaviours = behavioursField.Value;
                if (behaviours == null) return;

                // Already present (in case CreatePieceInternal is called again for this
                // piece, e.g. reconnection): don't duplicate the entry.
                foreach (var kvp in behaviours)
                {
                    if (kvp.Key == DataKeys.Behaviour.FollowPlayerMeleeAttacker) return;
                }

                var fallback = new Boardgame.BoardEntities.AI.FollowPlayerMeleeBehaviour(_context.AbilityFactory);
                _ourFallbackInstances.Add(fallback);
                behaviours.Add(new KeyValuePair<DataKeys.Behaviour, Boardgame.BoardEntities.AI.Behaviour>(
                    DataKeys.Behaviour.FollowPlayerMeleeAttacker, fallback));
            }
            catch { }
        }

        /// <summary>
        /// The safety net above wasn't triggering.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Decompiling FollowPlayerMeleeBehaviour.PlanNextAction: the very first thing
        /// this native method does is
        /// <c>if (!piece.IsConfused() &amp;&amp; !piece.IsBot()) return;</c> — WITHOUT
        /// ever touching CurrentScore (which therefore stays at -1, set by
        /// Behaviour.Prepare() just before, and ignored by PieceAI.CreatePlan()).
        /// Boardgame.PieceType.Bot (value 22, confirmed via the Constant table) is
        /// NOT set on an ordinary hostile boss/mob — this Behaviour is clearly meant
        /// for "Bot" pieces (AI-controlled allies, e.g. WarlockMinion) or for the
        /// Confused state, not to serve as a generic safety net for a normal hostile
        /// boss. Result: our instance added in the Postfix above did STRICTLY
        /// NOTHING for the targeted bosses — confirmed empirically by the logs.
        /// </para>
        /// <para>
        /// Fix: we do NOT touch the native IsBot()/IsConfused() behaviour for the
        /// rest of the game (too risky — IsBot() is used by ~20 other methods: UI
        /// colors, turn order, saving...). We restrict the workaround to the SOLE
        /// execution window of PlanNextAction on OUR specific instances
        /// (<see cref="_ourFallbackInstances"/>, tracked by reference):
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// Prefix on FollowPlayerMeleeBehaviour.PlanNextAction: if __instance is one
        /// of ours, sets the static flag <see cref="_forceIsBotForFallback"/> (saving
        /// the previous value in __state, guarding against re-entrancy).
        /// </description></item>
        /// <item><description>
        /// Prefix on Piece.IsBot(): if the flag is set, short-circuits and returns
        /// true without running the native body.
        /// </description></item>
        /// <item><description>
        /// Postfix on PlanNextAction: restores the flag to its value from before the
        /// call.
        /// </description></item>
        /// </list>
        /// <para>
        /// Verified by decompilation that IsBot() is not called anywhere else in
        /// PlanNextAction's call chain (ChooseAttackTarget/OwnerHeatMap/
        /// GetPlayerHeatmap/MoveTowardsTile/FindPortalToShortcutThrough/
        /// GetPlayerPieces — none of these names appear among IsBot()'s callers), so
        /// there is no possible leak into evaluating another piece during this same
        /// window.
        /// </para>
        /// </remarks>
        private static void PatchFollowPlayerMeleeBehaviourGate()
        {
            try
            {
                var harmony = Plugin.HarmonyInstance;
                if (harmony == null) return;

                // AccessTools.Method(type, "PlanNextAction") WITHOUT parameter types throws
                // System.Reflection.AmbiguousMatchException — root cause confirmed by IL
                // disassembly: Boardgame.BoardEntities.AI.Behaviour (base class) declares
                // TWO methods named PlanNextAction:
                //   - PlanNextAction(BoardQuery, PieceAndTurnController, BoardModel) — the
                //     one FollowPlayerMeleeBehaviour overrides (same signature, confirmed
                //     by comparing IL signature blobs).
                //   - PlanNextAction(BoardQuery, PieceAndTurnController, BoardModel,
                //     LevelLoaderAndInitializer) — a 2nd, 4-parameter overload, inherited
                //     AS-IS (not overridden) by FollowPlayerMeleeBehaviour.
                // Type.GetMethod(name) (used internally by AccessTools.Method without
                // explicit types) sees both via the flattened hierarchy → ambiguous, which
                // prevented the entire PatchFollowPlayerMeleeBehaviourGate() patch from
                // being installed (the exception was swallowed by an empty catch). Fix:
                // explicitly specify the parameter types of the 3-parameter overload to
                // resolve the ambiguity.
                var planMethod = AccessTools.Method(
                    typeof(Boardgame.BoardEntities.AI.FollowPlayerMeleeBehaviour), "PlanNextAction",
                    new[]
                    {
                        typeof(Boardgame.Board.BoardQuery),
                        typeof(Boardgame.PieceAndTurnController),
                        typeof(Boardgame.BoardModel),
                    });
                var isBotMethod = AccessTools.Method(typeof(Piece), "IsBot");
                if (planMethod == null || isBotMethod == null)
                {
                    Plugin.Log?.LogWarning("[BossFallbackBehaviour] PlanNextAction or IsBot not found — fallback net stays inactive (behavior unchanged).");
                    return;
                }

                harmony.Patch(
                    planMethod,
                    prefix: new HarmonyMethod(typeof(DoriathLevelUpRule), nameof(FollowPlayerMeleeBehaviour_PlanNextAction_Prefix)),
                    postfix: new HarmonyMethod(typeof(DoriathLevelUpRule), nameof(FollowPlayerMeleeBehaviour_PlanNextAction_Postfix)));
                harmony.Patch(
                    isBotMethod,
                    prefix: new HarmonyMethod(typeof(DoriathLevelUpRule), nameof(Piece_IsBot_Prefix)));

                _followPlayerMeleePlanMethod = planMethod;
                _pieceIsBotMethod = isBotMethod;

                Plugin.Log?.LogInfo("[BossFallbackBehaviour] FollowPlayerMeleeBehaviour fallback net now active for MotherCy/ElvenSummoner/BossTown (targeted bypass of the native IsBot gate).");
            }
            catch (Exception ex)
            {
                // Explicit logging rather than an empty catch, to diagnose a possible
                // silent exception while installing the patch.
                Plugin.Log?.LogWarning($"[BossFallbackBehaviour] Exception dans PatchFollowPlayerMeleeBehaviourGate — filet de repli inactif : {ex}");
            }
        }

        private static void UnpatchFollowPlayerMeleeBehaviourGate()
        {
            try
            {
                var harmony = Plugin.HarmonyInstance;
                if (harmony == null) return;
                if (_followPlayerMeleePlanMethod != null)
                {
                    harmony.Unpatch(_followPlayerMeleePlanMethod, HarmonyPatchType.All, harmony.Id);
                    _followPlayerMeleePlanMethod = null;
                }
                if (_pieceIsBotMethod != null)
                {
                    harmony.Unpatch(_pieceIsBotMethod, HarmonyPatchType.All, harmony.Id);
                    _pieceIsBotMethod = null;
                }
                _ourFallbackInstances.Clear();
                _forceIsBotForFallback = false;
            }
            catch { }
        }

        private static void FollowPlayerMeleeBehaviour_PlanNextAction_Prefix(object __instance, out bool __state)
        {
            __state = _forceIsBotForFallback; // previous value, restored in Postfix (re-entrancy guard)
            if (_ourFallbackInstances.Contains(__instance))
            {
                _forceIsBotForFallback = true;
            }
        }

        private static void FollowPlayerMeleeBehaviour_PlanNextAction_Postfix(bool __state)
        {
            _forceIsBotForFallback = __state;
        }

        private static bool Piece_IsBot_Prefix(ref bool __result)
        {
            if (!_forceIsBotForFallback) return true; // run the native body normally
            __result = true;
            return false; // short-circuits the native body
        }

        private static void HouseRulesUiGameVr_Initialize_Postfix(object __instance)
        {
            try
            {
                if (!_isActivated) return;
                if (__instance is not MonoBehaviour mb) return;

                foreach (var comp in mb.GetComponentsInChildren<Component>(true))
                {
                    var textProp = comp.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                    if (textProp == null || textProp.PropertyType != typeof(string)) continue;
                    if (textProp.GetValue(comp) is not string current) continue;
                    if (!current.Contains("ruleset!") || !current.Contains("by ruNIIII")) continue;

                    textProp.SetValue(comp, Panel1CleanIntroText);
                    break;
                }
            }
            catch { }
        }

        private static void SpawnDoriathPerksPanel()
        {
            try
            {
                if (!MotherbrainGlobalVars.IsRunningOnVRPlatform) return;
                if (_perksPanel != null) return;
                _perksPanel = new GameObject("DoriathPerksPanel", typeof(DoriathMod.DoriathPerksPanel));
            }
            catch { }
        }

        private static void DespawnDoriathPerksPanel()
        {
            try
            {
                if (_perksPanel != null)
                {
                    UnityEngine.Object.Destroy(_perksPanel);
                    _perksPanel = null;
                }
            }
            catch { }
        }

        private static void SuppressGrayAlienAdvancedStats()
        {
            try
            {
                var harmony = Plugin.HarmonyInstance;
                if (harmony == null) return;

                var nonVRType = AccessTools.TypeByName("NonVRAdvancedStatsView");
                if (nonVRType != null)
                {
                    var method = AccessTools.Method(nonVRType, "NonVrInfoPanelController_OnSelectPiece_Postfix");
                    if (method != null)
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(DoriathLevelUpRule), nameof(SuppressPatch)));
                        _suppressedMethods.Add(method);
                    }
                }

                var vrType = AccessTools.TypeByName("VRAdvancedStatsView");
                if (vrType != null)
                {
                    var method = AccessTools.Method(vrType, "GrabbedPieceHudInstantiator_CloneCurrentHudState_Postfix");
                    if (method != null)
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(DoriathLevelUpRule), nameof(SuppressPatch)));
                        _suppressedMethods.Add(method);
                    }
                }
            }
            catch { }
        }

        private static void RestoreGrayAlienAdvancedStats()
        {
            try
            {
                var harmony = Plugin.HarmonyInstance;
                if (harmony == null) return;

                foreach (var method in _suppressedMethods)
                {
                    harmony.Unpatch(method, HarmonyPatchType.Prefix, harmony.Id);
                }
                _suppressedMethods.Clear();
            }
            catch { }
        }

        private static bool SuppressPatch() => false;

        private static void Patch(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Piece), "CreatePiece"),
                postfix: new HarmonyMethod(
                    typeof(DoriathLevelUpRule),
                    nameof(CreatePiece_Progression_Postfix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(SerializableEventQueue), "RespondToRequest"),
                prefix: new HarmonyMethod(
                    typeof(DoriathLevelUpRule),
                    nameof(SerializableEventQueue_RespondToRequest_Prefix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(Inventory), "RestoreReplenishables"),
                prefix: new HarmonyMethod(
                    typeof(DoriathLevelUpRule),
                    nameof(Inventory_RestoreReplenishables_Prefix)));
        }

        private static void CreatePiece_Progression_Postfix(ref Piece __result)
        {
            if (!_isActivated || !__result.IsPlayer()) return;
            __result.effectSink.TrySetStatMaxValue(Stats.Type.CritChance, 1);
            __result.EnableEffectState(EffectStateType.Flying);
            __result.effectSink.SetStatusEffectDuration(EffectStateType.Flying, 1);

            // The "level 0 knockdown" bonus can't be applied here: CreatePiece runs too
            // early (before/during the native piece stat init completes, which would
            // overwrite the change) — applied instead in
            // ApplyLevel0KnockdownBonusOnce(), on each player piece's first StartTurn
            // (see SerializableEventQueue_RespondToRequest_Prefix below), a point where
            // all native stats are already stabilized.
        }

        /// <summary>
        /// pieceIds of player pieces that already received the "2 knockdowns at
        /// level 0" bonus.
        /// </summary>
        /// <remarks>
        /// Idempotence guard — only one StartTurn should count, never re-applied.
        /// </remarks>
        private static readonly HashSet<int> _level0KnockdownBonusApplied = new();

        private static void ApplyLevel0KnockdownBonusOnce(SerializableEventQueue instance, SerializableEvent request)
        {
            try
            {
                int pieceId = request.pieceId;
                if (_level0KnockdownBonusApplied.Contains(pieceId)) return;

                var gameContext = Traverse.Create(instance).Property<GameContext>("gameContext").Value;
                if (gameContext == null) return;
                if (!gameContext.pieceAndTurnController.TryGetPiece(pieceId, out var piece)) return;
                if (!piece.IsPlayer()) return;

                _level0KnockdownBonusApplied.Add(pieceId);

                // Level 0 only (CritChance == 1, per the convention "displayed level =
                // internal CritChance - 1") — this bonus must not be granted if the piece
                // has already leveled up before its very first turn (shouldn't happen,
                // but kept as a defensive check).
                if (piece.GetStatMax(Stats.Type.CritChance) != 1) return;

                // ── LEVEL 0: +1 knockdown bonus ──────────────────────────────────────
                // Native grants 2 knockdowns at baseline (confirmed by log: DownedTimer=3
                // with this bonus active, so native=2); this bonus brings it to 3 at the
                // start, which is the intended total. Total by end of game if the
                // character never dies: 2 (native) + 1 (here, level 0) + 1 (level 2) +
                // 1 (level 6) = 5.
                piece.effectSink.TrySetStatBaseValue(Stats.Type.DownedCounter,
                    piece.GetStat(Stats.Type.DownedCounter) - 1);
                piece.effectSink.TrySetStatBaseValue(Stats.Type.DownedTimer,
                    piece.GetStat(Stats.Type.DownedTimer) + 1);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[DoriathLevelUpRule] Erreur bonus knockdown niveau 0: {ex.Message}");
            }
        }

        private static void SerializableEventQueue_RespondToRequest_Prefix(
            SerializableEventQueue __instance,
            ref SerializableEvent request)
        {
            if (!_isActivated) return;

            if (request.type == SerializableEvent.Type.StartTurn)
            {
                ApplyLevel0KnockdownBonusOnce(__instance, request);
                return;
            }

            if (request.type != SerializableEvent.Type.AddCardToPiece) return;

            var addCardToPieceEvent = (SerializableEventAddCardToPiece)request;
            var gameContext = Traverse.Create(__instance).Property<GameContext>("gameContext").Value;
            var pieceId = Traverse.Create(addCardToPieceEvent).Field<int>("pieceId").Value;
            var cardSource = Traverse.Create(addCardToPieceEvent).Field<int>("cardSource").Value;

            if (cardSource != (int)MotherTracker.Context.Energy) return;
            if (!gameContext.pieceAndTurnController.TryGetPiece(pieceId, out var piece)) return;
            if (!piece.IsPlayer()) return;

            Inventory.Item value;
            int nextLevel = piece.GetStatMax(Stats.Type.CritChance);

            // Stands a downed hero back up BEFORE the heal. Piece.SetIsDowned is the
            // native method that actually changes the downed state (animation, sound,
            // network, stats). sourceAbility=default (PlayerMelee, not Revive) on
            // purpose: avoids the level-loss penalty of PieceProgressLostRule, which
            // only triggers on AbilityKey.Revive.
            if (piece.IsDowned())
            {
                piece.SetIsDowned(false, gameContext.pieceAndTurnController, null, default);
                Plugin.Log?.LogInfo($"[DoriathLevelUpRule] pieceId={pieceId} stood back up (was knocked down) on level-up.");
            }

            // Heal on level-up
            piece.effectSink.Heal(piece.GetMaxHealth());
            Plugin.Log?.LogInfo($"[DoriathLevelUpRule] pieceId={pieceId} fully healed on level-up (Health -> {piece.GetMaxHealth()}).");
            piece.DisableEffectState(EffectStateType.Heal);
            piece.EnableEffectState(EffectStateType.Heal, 1);

            if (nextLevel < MaxLevel)
            {
                piece.effectSink.TrySetStatMaxValue(Stats.Type.CritChance, nextLevel + 1);
                nextLevel++;
                piece.effectSink.SetStatusEffectDuration(EffectStateType.Flying, nextLevel);

                string msg = GetLevelMessage(piece.boardPieceId, nextLevel);
                GameUI.ShowCameraMessage(
                    $"<color=#F0F312>The party has</color> <color=#00FF00>LEVELED UP!</color> {msg}", 6);

                // ── LEVEL 1 ────────────────────────────────────────────
                if (nextLevel == 2)
                {
                    if (piece.boardPieceId == BoardPieceId.HeroBard)
                    {
                        var p = _context!.AbilityFactory.LoadAbility(AbilityKey.StrengthenCourage);
                        p.OnLoaded(a => { a.costActionPoint = false; });
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroGuardian)
                    {
                        var p = _context!.AbilityFactory.LoadAbility(AbilityKey.Charge);
                        p.OnLoaded(a => { a.costActionPoint = false; });
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroHunter)
                    {
                        var p = _context!.AbilityFactory.LoadAbility(AbilityKey.HunterArrow);
                        p.OnLoaded(a => { a.costActionPoint = false; });
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.TurretDamageProjectile,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 1));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroRogue)
                    {
                        var p = _context!.AbilityFactory.LoadAbility(AbilityKey.Stealth);
                        p.OnLoaded(a => { a.costActionPoint = false; });
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.DiseasedBite,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 1));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroSorcerer)
                    {
                        var p = _context!.AbilityFactory.LoadAbility(AbilityKey.EnemyFireball);
                        p.OnLoaded(a => { a.costActionPoint = false; });

                        // ── Grant Fireball to the Sorcerer at level 4 ──
                        bool hasFireball = false;
                        for (var i = 0; i < piece.inventory.Items.Count; i++)
                        {
                            if (piece.inventory.Items[i].AbilityKey == AbilityKey.Fireball)
                            {
                                hasFireball = true;
                                break;
                            }
                        }
                        if (!hasFireball)
                        {
                            piece.inventory.Items.Add(new Inventory.Item(AbilityKey.Fireball,
                                flags: 0, originalOwner: -1, replenishCooldown: 2));
                            piece.AddGold(0);
                        }

                        // ── Grant DeathFlurry to the Sorcerer (RF=1, normal cost 1 AP, not free) ──
                        // Replaces DeathBeam. DeathBeam was only granted here to feed the
                        // level 9 perk (FreeRevolutionsAbilityOnCrit, see
                        // Doriath (PROGRESSIVE).json) — both are therefore changed together so
                        // the Sorcerer actually holds the card that perk makes free on CRIT.
                        bool hasDeathFlurry = false;
                        for (var i = 0; i < piece.inventory.Items.Count; i++)
                        {
                            if (piece.inventory.Items[i].AbilityKey == AbilityKey.DeathFlurry)
                            {
                                hasDeathFlurry = true;
                                break;
                            }
                        }
                        if (!hasDeathFlurry)
                        {
                            Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                            piece.inventory.Items.Add(new Inventory.Item(AbilityKey.DeathFlurry,
                                flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 1));
                            piece.AddGold(0);
                        }
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroWarlock)
                    {
                        var mp = _context!.AbilityFactory.LoadAbility(AbilityKey.MagicMissile);
                        mp.OnLoaded(a => { a.costActionPoint = false; });

                        for (var i = 0; i < piece.inventory.Items.Count; i++)
                        {
                            var item = piece.inventory.Items[i];
                            if (item.AbilityKey == AbilityKey.MinionCharge)
                            {
                                if (item.IsReplenishing)
                                    Traverse.Create(piece.inventory)
                                        .Field<int>("numberOfReplenishableCards").Value -= 1;
                                piece.inventory.Items.RemoveAt(i);
                                piece.AddGold(0);
                                break;
                            }
                        }
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.EnemyFrostball,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 1));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroBarbarian)
                    {
                        var p = _context!.AbilityFactory.LoadAbility(AbilityKey.Grapple);
                        p.OnLoaded(a => { a.costActionPoint = false; });
                    }
                }
                // ── LEVEL 3: last-action crit buffs (handled by FreeThingsOnLastMoveAndCritRule) + 2nd KD ──
                else if (nextLevel == 3)
                {
                    piece.effectSink.TrySetStatBaseValue(Stats.Type.DownedCounter,
                        piece.GetStat(Stats.Type.DownedCounter) - 1);
                    piece.effectSink.TrySetStatBaseValue(Stats.Type.DownedTimer,
                        piece.GetStat(Stats.Type.DownedTimer) + 1);
                }
                // ── LEVEL 4: +2 max HP (the "Gold on CRIT" bonus, formerly level 9, is
                // merged in here: handled by the level >= 4 threshold in
                // FreeThingsOnLastMoveAndCritRule, no extra code needed) ─────────────
                else if (nextLevel == 4)
                {
                    piece.effectSink.TrySetStatMaxValue(Stats.Type.Health, piece.GetMaxHealth() + 2);
                    piece.effectSink.TrySetStatBaseValue(Stats.Type.Health, piece.GetHealth() + 2);
                }
                // ── LEVEL 4: perk cards (formerly level 3) + CRIT buffs (handled by FreeThingsOnLastMoveAndCritRule) ──
                else if (nextLevel == 5)
                {
                    // Bard: nothing to do here. "Free Zap" is handled by the level >= 5
                    // threshold in Inventory_RestoreReplenishables_Prefix/SetCost (like
                    // HunterArrow/DiseasedBite/EnemyFrostball/SpawnRandomLamp for the other
                    // heroes at this same level). Overcharge has been removed from the
                    // Bard progression (no card to increment here anymore).
                    if (piece.boardPieceId == BoardPieceId.HeroGuardian)
                    {
                        RemoveAndAddReplenishable(piece, AbilityKey.PiercingSpear, 2);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroSorcerer)
                    {
                        // Fireball is a single-use starting card
                        // (ReplenishFrequency=0), removed from Items once consumed — a
                        // find-and-mutate-in-place approach doesn't work if it's already
                        // been used before this level. RemoveAndAddReplenishable() (the
                        // same helper as PiercingSpear/Whirlwind for the Guardian) removes
                        // any leftover copy, then unconditionally adds the replenishable
                        // card.
                        RemoveAndAddReplenishable(piece, AbilityKey.Fireball, 2);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroWarlock)
                    {
                        var controller = gameContext.pieceAndTurnController;
                        var piecesField = Traverse.Create(controller)
                            .Field<Dictionary<int, Piece>>("pieces");
                        if (piecesField.Value != null)
                        {
                            foreach (var kvp in piecesField.Value)
                            {
                                if (kvp.Value.boardPieceId == BoardPieceId.WarlockMinion)
                                {
                                    kvp.Value.effectSink.TrySetStatBaseValue(Stats.Type.Strength,
                                        kvp.Value.GetStat(Stats.Type.Strength) + 1);
                                    kvp.Value.effectSink.TrySetStatMaxValue(Stats.Type.Strength,
                                        kvp.Value.GetStatMax(Stats.Type.Strength) + 1);
                                    // Move Range +1
                                    kvp.Value.effectSink.TrySetStatBaseValue(Stats.Type.MoveRange,
                                        kvp.Value.GetStat(Stats.Type.MoveRange) + 1);
                                    kvp.Value.effectSink.TrySetStatMaxValue(Stats.Type.MoveRange,
                                        kvp.Value.GetStatMax(Stats.Type.MoveRange) + 1);
                                    break;
                                }
                            }
                        }
                    }
                }
                // ── LEVEL 5: new card ──
                else if (nextLevel == 6)
                {
                    if (piece.boardPieceId == BoardPieceId.HeroBard)
                    {
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.Electricity,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 1));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroGuardian)
                    {
                        RemoveAndAddReplenishable(piece, AbilityKey.Whirlwind, 2);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroHunter)
                    {
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.PoisonedTip,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 2));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroRogue)
                    {
                        // Same case as Fireball above — PoisonGasGrenade is a single-use
                        // starting card (ReplenishFrequency=0), removed from Items once
                        // consumed; the old find-and-mutate-in-place approach could no
                        // longer find it if it had already been used before this level.
                        RemoveAndAddReplenishable(piece, AbilityKey.PoisonGasGrenade, 2);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroSorcerer)
                    {
                        // Swapped with level 6: now receives FretsOfFire here.
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.FretsOfFire,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 2));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroWarlock)
                    {
                        // Swapped with level 6: now receives Freeze here. The Minion buff
                        // (WarlockMinion Strength+1 below) stays tied to this level (5),
                        // independent of which card is granted — as it was before the swap.
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.Freeze,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 3));
                        piece.AddGold(0);

                        var controller = gameContext.pieceAndTurnController;
                        var piecesField = Traverse.Create(controller)
                            .Field<Dictionary<int, Piece>>("pieces");
                        if (piecesField.Value != null)
                        {
                            foreach (var kvp in piecesField.Value)
                            {
                                if (kvp.Value.boardPieceId == BoardPieceId.WarlockMinion)
                                {
                                    kvp.Value.effectSink.TrySetStatBaseValue(Stats.Type.Strength,
                                        kvp.Value.GetStat(Stats.Type.Strength) + 1);
                                    kvp.Value.effectSink.TrySetStatMaxValue(Stats.Type.Strength,
                                        kvp.Value.GetStatMax(Stats.Type.Strength) + 1);
                                    break;
                                }
                            }
                        }
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroBarbarian)
                    {
                        // Swapped with level 6: now receives GrapplingSmash here.
                        // GrapplingSmash is a starting card
                        // (StartCardsModified.HeroBarbarian, RF=0) — we reuse the same
                        // defensive pattern (look for the existing copy, otherwise add)
                        // already in place for this card; only the triggering level
                        // changes.
                        for (var i = 0; i < piece.inventory.Items.Count; i++)
                        {
                            value = piece.inventory.Items[i];
                            if (value.AbilityKey == AbilityKey.GrapplingSmash)
                            {
                                value.flags |= (Inventory.ItemFlag)1;
                                value.replenishCooldown = 2;
                                piece.inventory.Items[i] = value;
                                Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                                piece.AddGold(0);
                                break;
                            }
                        }
                        // Add GrapplingSmash at level 5 if not already present
                        bool hasGrapplingSmash = false;
                        for (var i = 0; i < piece.inventory.Items.Count; i++)
                        {
                            if (piece.inventory.Items[i].AbilityKey == AbilityKey.GrapplingSmash)
                            {
                                hasGrapplingSmash = true;
                                break;
                            }
                        }
                        if (!hasGrapplingSmash)
                        {
                            piece.inventory.Items.Add(new Inventory.Item(AbilityKey.GrapplingSmash,
                                flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 2));
                            piece.AddGold(0);
                        }
                    }
                }
                // ── LEVEL 6: 1 KD (+2 HP removed: only level 3 grants +max HP) ──
                else if (nextLevel == 7)
                {
                    piece.effectSink.TrySetStatBaseValue(Stats.Type.DownedCounter,
                        piece.GetStat(Stats.Type.DownedCounter) - 1);
                    piece.effectSink.TrySetStatBaseValue(Stats.Type.DownedTimer,
                        piece.GetStat(Stats.Type.DownedTimer) + 1);
                    if (piece.boardPieceId == BoardPieceId.HeroBard)
                    {
                        // ScrollTsunami is a single-use starting card
                        // (ReplenishFrequency=0) —
                        // an unconditional Add() here created a DUPLICATE if the player
                        // hadn't yet consumed it at this level (2 ScrollTsunami entries in
                        // the inventory). RemoveAndAddReplenishable() removes any remaining
                        // copy before adding the replenishable version.
                        RemoveAndAddReplenishable(piece, AbilityKey.ScrollTsunami, 2);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroGuardian)
                    {
                        // Swapped with level 8 (formerly level 7 for the Guardian): now
                        // receives BeaconOfHealing here. RF changed from 4 to 3.
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.BeaconOfHealing,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 3));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroHunter)
                    {
                        // Swapped with level 8: now receives MarkOfAvalon here.
                        // MarkOfAvalon is not a starting card (absent from
                        // StartCardsModified.HeroHunter) — an unconditional Add() is
                        // correct.
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.MarkOfAvalon,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 3));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroRogue)
                    {
                        // Replaces Flashbang with ProximityMine. ProximityMine is not a
                        // starting card (absent from StartCardsModified.HeroRogue) — an
                        // unconditional Add() is correct, no need for
                        // RemoveAndAddReplenishable.
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.ProximityMine,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 1));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroSorcerer)
                    {
                        // Swapped with level 5: now receives MagicShield here.
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.MagicShield,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 3));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroWarlock)
                    {
                        // Swapped with level 5: now receives IceExplosion here.
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.IceExplosion,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 2));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroBarbarian)
                    {
                        // Swapped with level 5: now receives Implosion here.
                        // Implosion is not a starting card (absent from
                        // StartCardsModified.HeroBarbarian) — an unconditional Add() is
                        // correct.
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.Implosion,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 3));
                        piece.AddGold(0);
                    }
                }
                // ── LEVEL 7 (formerly level 8, swapped with level 8): stat bonus (+2 HP
                // removed) + FreeHealOnCrit via FreeThingsOnLastMoveAndCritRule ──
                else if (nextLevel == 8)
                {
                    if (piece.boardPieceId == BoardPieceId.HeroBard ||
                        piece.boardPieceId == BoardPieceId.HeroWarlock ||
                        piece.boardPieceId == BoardPieceId.HeroSorcerer)
                    {
                        piece.effectSink.TrySetStatBaseValue(Stats.Type.MagicBonus,
                            piece.GetStat(Stats.Type.MagicBonus) + 1);
                        piece.effectSink.TrySetStatMaxValue(Stats.Type.MagicBonus,
                            piece.GetStatMax(Stats.Type.MagicBonus) + 1);
                    }
                    else
                    {
                        piece.effectSink.TrySetStatBaseValue(Stats.Type.Strength,
                            piece.GetStat(Stats.Type.Strength) + 1);
                        piece.effectSink.TrySetStatMaxValue(Stats.Type.Strength,
                            piece.GetStatMax(Stats.Type.Strength) + 1);
                    }
                }
                // ── LEVEL 8 (formerly level 7, swapped with level 7): ultimate card per hero ─────────────────
                else if (nextLevel == 9)
                {
                    if (piece.boardPieceId == BoardPieceId.HeroBard)
                    {
                        // Same case as ScrollTsunami — Tornado is also a single-use
                        // starting card (RF=0); the unconditional Add() was replaced by
                        // RemoveAndAddReplenishable() to avoid a duplicate if the player
                        // hadn't yet consumed it at this level.
                        RemoveAndAddReplenishable(piece, AbilityKey.Tornado, 3);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroGuardian)
                    {
                        // Swapped with level 6 (formerly level 8 for the Guardian): now
                        // receives BeaconOfSmite here.
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.BeaconOfSmite,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 3));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroHunter)
                    {
                        // Swapped with level 6: now receives Exterminate here.
                        // Exterminate is not a starting card (absent from
                        // StartCardsModified.HeroHunter) — an unconditional Add() is
                        // correct.
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.Exterminate,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 2));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroRogue)
                    {
                        // Swapped with level 6: now receives CursedDagger here.
                        // CursedDagger is a single-use starting card
                        // (ReplenishFrequency=0) — RemoveAndAddReplenishable is required
                        // (same reasoning as the Fireball/ScrollTsunami fix above), not
                        // an unconditional Add().
                        RemoveAndAddReplenishable(piece, AbilityKey.CursedDagger, 2);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroSorcerer)
                    {
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.ExplosiveOrb,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 3));
                        piece.AddGold(0);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroWarlock)
                    {
                        // MissileSwarm is actually a single-use starting card for the
                        // Warlock (StartCardsModified,
                        // ReplenishFrequency=0) — wrongly treated here as "UNLOCKED" (a
                        // new card never owned). An unconditional Add() created a
                        // duplicate if the player hadn't yet consumed it at this level.
                        // RemoveAndAddReplenishable() (same pattern as ScrollTsunami/
                        // Tornado above) fixes this; see also PieceProgressLostRule.cs
                        // (reverting a lost level used to call RemoveInventoryCard, which
                        // would have deleted the card entirely instead of just clearing
                        // its replenishable flag — fixed as RevertReplenishable).
                        RemoveAndAddReplenishable(piece, AbilityKey.MissileSwarm, 2);
                    }
                    else if (piece.boardPieceId == BoardPieceId.HeroBarbarian)
                    {
                        Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
                        piece.inventory.Items.Add(new Inventory.Item(AbilityKey.PlayerLeap,
                            flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: 2));
                        piece.AddGold(0);
                    }
                }
                // ── LEVEL 9 (formerly level 9, swapped with level 10): FreeRevolutionsAbilityOnCrit (handled by Rule) ──
                else if (nextLevel == 10)
                {
                    // Handled by FreeRevolutionsAbilityOnCritRule
                }
            }
        }

        private static bool Inventory_RestoreReplenishables_Prefix(ref bool __result, Piece piece)
        {
            if (!_isActivated) return true;
            if (!piece.IsPlayer()) return true;

            if (piece.HasEffectState(EffectStateType.ConfusedPermanentVisualOnly))
            {
                piece.DisableEffectState(EffectStateType.ConfusedPermanentVisualOnly);
            }

            int level = piece.GetStatMax(Stats.Type.CritChance);
            var id = piece.boardPieceId;

            SetCost(AbilityKey.StrengthenCourage, level >= 2 && id == BoardPieceId.HeroBard);
            SetCost(AbilityKey.Charge, level >= 2 && id == BoardPieceId.HeroGuardian);
            SetCost(AbilityKey.HunterArrow, level >= 2 && id == BoardPieceId.HeroHunter);
            SetCost(AbilityKey.Stealth, level >= 2 && id == BoardPieceId.HeroRogue);
            SetCost(AbilityKey.EnemyFireball, level >= 2 && id == BoardPieceId.HeroSorcerer);
            // DeathFlurry keeps its normal cost (1 AP): deliberately no free SetCost
            // here (replaces DeathBeam).

            SetCost(AbilityKey.Grapple, level >= 2 && id == BoardPieceId.HeroBarbarian);
            SetCost(AbilityKey.MagicMissile, level >= 2 && id == BoardPieceId.HeroWarlock);

            SetCost(AbilityKey.TurretDamageProjectile, level >= 5 && id == BoardPieceId.HeroHunter);
            SetCost(AbilityKey.DiseasedBite, level >= 5 && id == BoardPieceId.HeroRogue);
            SetCost(AbilityKey.EnemyFrostball, level >= 5 && id == BoardPieceId.HeroWarlock);
            SetCost(AbilityKey.Zap, level >= 5 && id == BoardPieceId.HeroBard);
            SetCost(AbilityKey.SpawnRandomLamp, level >= 5 && id == BoardPieceId.HeroBarbarian);

            SetCost(AbilityKey.SummonMinion, true);

            // ── Fix: replenish cooldown that "resets" after first use ──
            // Root cause (confirmed by IL disassembly of Assembly-CSharp.dll):
            // Inventory.ExhaustReplenishableItem() (called when the card is used) does
            // NOT reread Item.replenishCooldown to reload the cooldown — it overwrites
            // Item.replenishCooldown with Ability.replenishCooldownAfterEffectsEnd, a
            // field on the SHARED/cached Ability object (loaded via
            // AbilityFactory.LoadAbility), NOT with the value passed to
            // `replenishCooldown:` in the Inventory.Item constructor above (which only
            // seeds the VERY FIRST cycle).
            // Result: as soon as the card is used for the first time, its real
            // cooldown resets to the Ability asset's native value (often 0 or 1)
            // instead of the RF we want — hence "MissileSwarm (configured RF=2)
            // renews every turn" once used.
            // Fix: also patch Ability.replenishCooldownAfterEffectsEnd (public field)
            // on the shared asset, using the same LoadAbility+OnLoaded helper as
            // SetCost above, for ALL replenishable cards granted by this Rule (levels
            // 2 to 9, all heroes). Values are aligned with the `replenishCooldown:`
            // used at grant time.
            SetReplenishCooldown(AbilityKey.TurretDamageProjectile, 1);
            SetReplenishCooldown(AbilityKey.DiseasedBite, 1);
            SetReplenishCooldown(AbilityKey.Fireball, 2);
            SetReplenishCooldown(AbilityKey.DeathFlurry, 1);
            SetReplenishCooldown(AbilityKey.EnemyFrostball, 1);
            SetReplenishCooldown(AbilityKey.PiercingSpear, 2);
            SetReplenishCooldown(AbilityKey.Electricity, 1);
            SetReplenishCooldown(AbilityKey.Whirlwind, 2);
            SetReplenishCooldown(AbilityKey.PoisonedTip, 2);
            SetReplenishCooldown(AbilityKey.PoisonGasGrenade, 2);
            SetReplenishCooldown(AbilityKey.FretsOfFire, 2);
            SetReplenishCooldown(AbilityKey.Freeze, 3);
            SetReplenishCooldown(AbilityKey.GrapplingSmash, 2);
            SetReplenishCooldown(AbilityKey.ScrollTsunami, 2);
            SetReplenishCooldown(AbilityKey.BeaconOfHealing, 3);
            SetReplenishCooldown(AbilityKey.MarkOfAvalon, 3);
            SetReplenishCooldown(AbilityKey.ProximityMine, 1);
            SetReplenishCooldown(AbilityKey.MagicShield, 3);
            SetReplenishCooldown(AbilityKey.IceExplosion, 2);
            SetReplenishCooldown(AbilityKey.Implosion, 3);
            SetReplenishCooldown(AbilityKey.Tornado, 3);
            SetReplenishCooldown(AbilityKey.BeaconOfSmite, 3);
            SetReplenishCooldown(AbilityKey.Exterminate, 2);
            SetReplenishCooldown(AbilityKey.CursedDagger, 2);
            SetReplenishCooldown(AbilityKey.ExplosiveOrb, 3);
            SetReplenishCooldown(AbilityKey.MissileSwarm, 2);
            SetReplenishCooldown(AbilityKey.PlayerLeap, 2);

            // ── Temporary diagnostic: CrossbowBolt (Hunter, AbilityKey.
            // TurretDamageProjectile) and EnemyFreeze (Warlock, AbilityKey.EnemyFrostball)
            // have been observed in-game with a ReplenishFrequency of 2 instead of the
            // intended 1, even though the code forces RF=1 at grant time (level 2,
            // replenishCooldown: 1) AND reasserts it here EVERY turn via
            // SetReplenishCooldown (above).
            // IL disassembly of Promise<T>.OnLoaded and
            // Inventory.RestoreReplenishables (called every turn for every player
            // piece): no timing/cache anomaly found that would explain a persistent
            // RF=2. The "native"/default value of
            // Ability.replenishCooldownAfterEffectsEnd for these two keys
            // (ScriptableObject asset data, not an IL constant) remains unverifiable
            // statically. This log only fires if Item.replenishCooldown is actually
            // observed to differ from 1 in-game — remove once the bug is
            // confirmed/fixed or confirmed to not exist.
            for (var i = 0; i < piece.inventory.Items.Count; i++)
            {
                var diagItem = piece.inventory.Items[i];
                if ((diagItem.AbilityKey == AbilityKey.TurretDamageProjectile
                     || diagItem.AbilityKey == AbilityKey.EnemyFrostball)
                    && diagItem.replenishCooldown != 1)
                {
                    Plugin.Log?.LogWarning(
                        $"[ReplenishFrequencyDiag] ANOMALIE : piece={piece.boardPieceId} " +
                        $"pieceId={piece.networkID} ability={diagItem.AbilityKey} " +
                        $"Item.replenishCooldown={diagItem.replenishCooldown} (attendu 1)");
                }
            }

            return true;
        }

        private static void SetCost(AbilityKey key, bool free)
        {
            var p = _context!.AbilityFactory.LoadAbility(key);
            p.OnLoaded(a => { a.costActionPoint = !free; });
        }

        private static void SetReplenishCooldown(AbilityKey key, int cooldown)
        {
            var p = _context!.AbilityFactory.LoadAbility(key);
            p.OnLoaded(a => { a.replenishCooldownAfterEffectsEnd = cooldown; });
        }

        private static string GetHeroName(BoardPieceId id)
        {
            switch (id)
            {
                case BoardPieceId.HeroBard:      return "Bard";
                case BoardPieceId.HeroGuardian:  return "Guardian";
                case BoardPieceId.HeroHunter:    return "Hunter";
                case BoardPieceId.HeroRogue:     return "Rogue";
                case BoardPieceId.HeroSorcerer:  return "Sorcerer";
                case BoardPieceId.HeroWarlock:   return "Warlock";
                case BoardPieceId.HeroBarbarian: return "Barbarian";
                default:                          return "Hero";
            }
        }

        internal static string GetLevelMessage(BoardPieceId id, int level)
        {
            switch (id)
            {
                case BoardPieceId.HeroBard:
                    switch (level)
                    {
                        case 2:  return "<color=#FFD700>StrengthenCourage is now FREE</color>";
                        case 3:  return "<color=#FF6600>DeflectionBarrier on CRIT + 1 Knockdown</color>";
                        case 4:  return "<color=#FF00FF>+2 HP Max!</color> <color=#FFD700>+ CRIT gives 20 Gold</color>";
                        case 5:  return "<color=#FFD700>Zap is now FREE</color>";
                        case 6:  return "<color=#FFD700>Electricity UNLOCKED</color>";
                        case 7:  return "<color=#FFD700>ScrollTsunami REPLENISHABLE + 1 Knockdown</color>";
                        case 8:  return "<color=#00FFFF>FreeHealOnCrit! +1 Magic Bonus</color>";
                        case 9:  return "<color=#FFD700>Tornado REPLENISHABLE</color>";
                        case 10: return "<color=#FF0000>CRIT now grants FREE AcidSpit</color>";
                    }
                    break;
                case BoardPieceId.HeroGuardian:
                    switch (level)
                    {
                        case 2:  return "<color=#FFD700>Charge is now FREE</color>";
                        case 3:  return "<color=#FF6600>Invulnerable1 on CRIT + 1 Knockdown</color>";
                        case 4:  return "<color=#FF00FF>+2 HP Max!</color> <color=#FFD700>+ CRIT gives 20 Gold</color>";
                        case 5:  return "<color=#FFD700>PiercingSpear REPLENISHABLE</color>";
                        case 6:  return "<color=#FFD700>Whirlwind REPLENISHABLE</color>";
                        case 7:  return "<color=#FFD700>BeaconOfHealing REPLENISHABLE + 1 Knockdown</color>";
                        case 8:  return "<color=#FF6600>FreeHealOnCrit! +1 Strength</color>";
                        case 9:  return "<color=#FFD700>BeaconOfSmite REPLENISHABLE</color>";
                        case 10: return "<color=#FF0000>CRIT now grants FREE EnemyInvulnerability</color>";
                    }
                    break;
                case BoardPieceId.HeroHunter:
                    switch (level)
                    {
                        case 2:  return "<color=#FFD700>HunterArrow FREE + Turret UNLOCKED</color>";
                        case 3:  return "<color=#FF6600>Invulnerable1 on CRIT + 1 Knockdown</color>";
                        case 4:  return "<color=#FF00FF>+2 HP Max!</color> <color=#FFD700>+ CRIT gives 20 Gold</color>";
                        case 5:  return "<color=#FFD700>TurretDamageProjectile is now FREE</color>";
                        case 6:  return "<color=#FFD700>PoisonedTip UNLOCKED</color>";
                        case 7:  return "<color=#FFD700>MarkOfAvalon UNLOCKED + 1 Knockdown</color>";
                        case 8:  return "<color=#FF6600>FreeHealOnCrit! +1 Strength</color>";
                        case 9:  return "<color=#FFD700>Exterminate UNLOCKED</color>";
                        case 10: return "<color=#FF0000>CRIT now grants FREE Corrupt</color>";
                    }
                    break;
                case BoardPieceId.HeroRogue:
                    switch (level)
                    {
                        case 2:  return "<color=#FFD700>Stealth FREE + DiseasedBite UNLOCKED</color>";
                        case 3:  return "<color=#FF6600>Invisibility on CRIT + 1 Knockdown</color>";
                        case 4:  return "<color=#FF00FF>+2 HP Max!</color> <color=#FFD700>+ CRIT gives 20 Gold</color>";
                        case 5:  return "<color=#FFD700>DiseasedBite is now FREE</color>";
                        case 6:  return "<color=#FFD700>PoisonGasGrenade REPLENISHABLE</color>";
                        case 7:  return "<color=#FFD700>ProximityMine UNLOCKED + 1 Knockdown</color>";
                        case 8:  return "<color=#FF6600>FreeHealOnCrit! +1 Strength</color>";
                        case 9:  return "<color=#FFD700>CursedDagger REPLENISHABLE</color>";
                        case 10: return "<color=#FF0000>CRIT now grants FREE Blink</color>";
                    }
                    break;
                case BoardPieceId.HeroSorcerer:
                    switch (level)
                    {
                        case 2:  return "<color=#FFD700>EnemyFireball is now FREE</color>";
                        case 3:  return "<color=#FF6600>MagicShield on CRIT + 1 Knockdown</color>";
                        case 4:  return "<color=#FF00FF>+2 HP Max!</color> <color=#FFD700>+ CRIT gives 20 Gold</color>";
                        case 5:  return "<color=#FFD700>Fireball REPLENISHABLE</color>";
                        case 6:  return "<color=#FFD700>FretsOfFire UNLOCKED</color>";
                        case 7:  return "<color=#FFD700>MagicShield UNLOCKED + 1 Knockdown</color>";
                        case 8:  return "<color=#00FFFF>FreeHealOnCrit! +1 Magic Bonus</color>";
                        case 9:  return "<color=#FFD700>ExplosiveOrb UNLOCKED</color>";
                        case 10: return "<color=#FF0000>CRIT now grants FREE DeathFlurry</color>";
                    }
                    break;
                case BoardPieceId.HeroWarlock:
                    switch (level)
                    {
                        case 2:  return "<color=#FFD700>MinionCharge → EnemyFrostball</color>";
                        case 3:  return "<color=#FF6600>Deflect on CRIT + 1 Knockdown</color>";
                        case 4:  return "<color=#FF00FF>+2 HP Max!</color> <color=#FFD700>+ CRIT gives 20 Gold</color>";
                        case 5:  return "<color=#FFD700>EnemyFrostball is now FREE</color>";
                        case 6:  return "<color=#FFD700>Freeze UNLOCKED</color>";
                        case 7:  return "<color=#FFD700>IceExplosion REPLENISHABLE + 1 Knockdown</color>";
                        case 8:  return "<color=#00FFFF>FreeHealOnCrit! +1 Magic Bonus</color>";
                        case 9:  return "<color=#FFD700>MissileSwarm REPLENISHABLE</color>";
                        case 10: return "<color=#FF0000>CRIT now grants MinionCharge (1 PA)</color>";
                    }
                    break;
                case BoardPieceId.HeroBarbarian:
                    switch (level)
                    {
                        case 2:  return "<color=#FFD700>Grapple is now FREE</color>";
                        case 3:  return "<color=#FF6600>MarkOfVerga on CRIT + 1 Knockdown</color>";
                        case 4:  return "<color=#FF00FF>+2 HP Max!</color> <color=#FFD700>+ CRIT gives 20 Gold</color>";
                        case 5:  return "<color=#FFD700>SpawnRandomLamp is now FREE</color>";
                        case 6:  return "<color=#FFD700>GrapplingSmash REPLENISHABLE</color>";
                        case 7:  return "<color=#FFD700>Implosion REPLENISHABLE + 1 Knockdown</color>";
                        case 8:  return "<color=#FF6600>FreeHealOnCrit! +1 Strength</color>";
                        case 9:  return "<color=#FFD700>PlayerLeap UNLOCKED</color>";
                        case 10: return "<color=#FF0000>CRIT now grants FREE MarkOfVerga</color>";
                    }
                    break;
            }
            return "";
        }

        private static void RemoveAndAddReplenishable(Piece piece, AbilityKey key, int cooldown)
        {
            for (var i = 0; i < piece.inventory.Items.Count; i++)
            {
                var item = piece.inventory.Items[i];
                if (item.AbilityKey == key)
                {
                    if (item.IsReplenishing)
                        Traverse.Create(piece.inventory)
                            .Field<int>("numberOfReplenishableCards").Value -= 1;
                    piece.inventory.Items.RemoveAt(i);
                    break;
                }
            }
            Traverse.Create(piece.inventory).Field<int>("numberOfReplenishableCards").Value += 1;
            piece.inventory.Items.Add(new Inventory.Item(key,
                flags: (Inventory.ItemFlag)1, originalOwner: -1, replenishCooldown: cooldown));
            piece.AddGold(0);
        }
    }
}
