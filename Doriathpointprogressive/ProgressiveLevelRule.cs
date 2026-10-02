namespace DoriathMod.Rules
{
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Boardgame;
using Boardgame.BoardEntities;
using Boardgame.BoardEntities.Abilities;
using Boardgame.BoardEntities.AI;
using Boardgame.BoardgameActions;
using Boardgame.Data;
using Boardgame.SerializableEvents;
using Boardgame.TurnOrder;
using DataKeys;
using HarmonyLib;
using HouseRules.Core.Types;
using UnityEngine;

    public sealed class DoriathPointLevelUpRule : Rule, IConfigWritable<DoriathPointLevelUpRule.PointsConfig>, IPatchable, IMultiplayerSafe
    {
        // Short one-line description shown in the native Panel 1 ("Active Rules").
        //
        // Panel 1 must list ONLY the rule name — the perk/level details live
        // exclusively on Panel 2 (DoriathPerksPanel). The description is
        // intentionally short, matching the one-liner style used by the ~38 other
        // rules in the ruleset.
        public override string Description => "Heroes level up by earning points through their actions.";

        private static Context? _context;
        private static bool _isActivated;
        private const int MaxLevel = 10; // formerly 11: the "Gold on CRIT" level was merged into level 4, 9 levels total
        private static readonly List<MethodInfo> _suppressedMethods = new();
        private static GameObject? _perksPanel;
        private static MethodInfo? _panel1IntroPatchedMethod;
        private static MethodInfo? _bossFallbackPatchedMethod;
        private static MethodInfo? _followPlayerMeleePlanMethod;
        private static MethodInfo? _pieceIsBotMethod;

        // Instances of FollowPlayerMeleeBehaviour created by our Postfix below
        // (one per spawned target piece).
        //
        // Used to restrict the IsBot() patch to ONLY the evaluation of these
        // specific instances (see PatchFollowPlayerMeleeBehaviourGate).
        private static readonly HashSet<object> _ourFallbackInstances = new();
        private static bool _forceIsBotForFallback;

        // BoardPieceIds of the pieces affected by the "wasted actions" fix below.
        //
        // ActionPoint is raised via PieceConfigAdjusted, but the native AI is
        // limited to ~2 useful actions per turn (see PatchBossFallbackBehaviour).
        // The FollowPlayerMeleeBehaviour fallback is active for these 3 bosses.
        private static readonly BoardPieceId[] BossesNeedingFallbackBehaviour =
        {
            BoardPieceId.MotherCy, BoardPieceId.ElvenSummoner, BoardPieceId.RootLord
        };

        // Replacement text for Panel 1 only.
        //
        // "Doriath (Point Progressive)" is shown in dark purple (distinct from the
        // orange used by the button/RoomFinder, which reads the JSON "Name" field
        // directly — see Doriath (Point Progressive).json). "by ruNIIII" is dark gray
        // and bold, to visually match the "Playing ... ruleset!" style (black).
        //
        // The button/RoomFinder, which reads the JSON "Name" field directly, is
        // not affected by this replacement text.
        private const string Panel1CleanIntroText =
            "<color=#000000>Playing</color> <color=#9400D3>Doriath (Point Progressive)</color> <color=#000000>ruleset!</color>\n<color=#333333><b>by ruNIIII</b></color>";

        // Point values: see the PointsConfig class in the "POINT PROGRESSION"
        // region below. Editable from the ruleset JSON.
        private readonly PointsConfig _config;

        public DoriathPointLevelUpRule()
            : this(new PointsConfig())
        {
        }

        public DoriathPointLevelUpRule(PointsConfig config)
        {
            _config = config ?? new PointsConfig();
        }

        public PointsConfig GetConfigObject() => _config;

        protected override void OnActivate(Context context)
        {
            _context = context;
            _pts = _config;
            ResolveBossPieces();
            _isActivated = true;
            _level0KnockdownBonusApplied.Clear();

            // Fails loudly at activation if the perk table and MaxLevel disagree,
            // rather than silently granting or reverting the wrong perk mid-run.
            PerkTable.SelfCheck(MaxLevel);

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

        // Panel 1 ("Active Rules"): cleans up the "Playing X ruleset!" line.
        //
        // The Ruleset.Name field is multi-line/colored (RoomFinder and Panel 1
        // both reuse it as-is), which makes "ruleset!" appear stuck right after
        // "by ruNIIII" on the same colored line in the native panel.
        //
        // We patch HouseRulesUiGameVr.Initialize() with a Postfix to find, after
        // the fact, the text component containing this phrase and rewrite it
        // cleanly on 2 lines. The search uses generic reflection (a public "text"
        // property of type string) so it depends on neither UnityEngine.UI.Text
        // nor TMPro.TMP_Text (neither DLL is referenced in this project).
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
                    postfix: new HarmonyMethod(typeof(DoriathPointLevelUpRule), nameof(HouseRulesUiGameVr_Initialize_Postfix)));
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

        // Fix: bosses stuck at ~2 actions/turn despite ActionPoint=4.
        //
        // Root cause (from IL analysis of Assembly-CSharp.dll — see
        // PieceAI.CreatePlan / Behaviour.Prepare): Behaviour.Prepare() resets
        // CurrentScore to -1 before each evaluation, and PieceAI.CreatePlan()
        // IGNORES any Behaviour whose score stays <= -1. If ALL of the boss's
        // Behaviours (melee attack, special spells like Rain/Electricity...) are
        // unavailable this turn (out of range, internal cooldown, failed random
        // roll), CreatePlan() returns null → PopulateEnemyAIEvents sends EndTurn
        // IMMEDIATELY, even if ActionPoints remain (the "ActionPoints > 1" check
        // is NEVER reached in that case). Since MotherCy/ElvenSummoner/BossTown
        // natively only have 2-3 "special" actions (designed for their native
        // ActionPoint, lower than 4), they quickly exhaust their repertoire and
        // end their turn early, independent of this mod's ActionPoint=4.0 buff
        // from PieceConfigAdjusted.
        //
        // Fix: on spawn (Postfix on PieceSpawner.CreatePieceInternal), we add the
        // generic native Behaviour FollowPlayerMeleeBehaviour (key
        // DataKeys.Behaviour.FollowPlayerMeleeAttacker) to these pieces — the same
        // Behaviour ordinary melee enemies use to close in on/attack a player. It
        // participates in the SAME scoring as the piece's special Behaviours
        // (PieceAI.CreatePlan keeps the best score, whether native or added here),
        // so it only takes over when no special action is usable — a safety net
        // that consumes the remaining ActionPoint instead of wasting it, without
        // changing behaviour the rest of the time.
        //
        // IMPORTANT: unlike the 2 previous fixes (RegainAbilityIfMaxxedOut,
        // replenishCooldownAfterEffectsEnd), which were validated by direct,
        // unambiguous IL proof, the exact construction/scoring of
        // FollowPlayerMeleeBehaviour remains a reasonable hypothesis, to be
        // confirmed in-game. Confirmed by log for ElvenSummoner (it does use its
        // ActionPoint=4 on nearly all of its turns after this fix, except the very
        // first turn before any player has been detected).
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
                    postfix: new HarmonyMethod(typeof(DoriathPointLevelUpRule), nameof(PieceSpawner_CreatePieceInternal_Postfix)));
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

        // The safety net above wasn't triggering.
        //
        // Decompiling FollowPlayerMeleeBehaviour.PlanNextAction: the very first
        // thing this native method does is if (!piece.IsConfused() &&
        // !piece.IsBot()) return; — WITHOUT ever touching CurrentScore (which
        // therefore stays at -1, set by Behaviour.Prepare() just before, and
        // ignored by PieceAI.CreatePlan()). Boardgame.PieceType.Bot (value 22,
        // confirmed via the Constant table) is NOT set on an ordinary hostile
        // boss/mob — this Behaviour is clearly meant for "Bot" pieces
        // (AI-controlled allies, e.g. WarlockMinion) or for the Confused state,
        // not to serve as a generic safety net for a normal hostile boss. Result:
        // our instance added in the Postfix above did STRICTLY NOTHING for the
        // targeted bosses — confirmed empirically by the logs.
        //
        // Fix: we do NOT touch the native IsBot()/IsConfused() behaviour for the
        // rest of the game (too risky — IsBot() is used by ~20 other methods: UI
        // colors, turn order, saving...). We restrict the workaround to the SOLE
        // execution window of PlanNextAction on OUR specific instances
        // (_ourFallbackInstances, tracked by reference):
        //
        //   1. Prefix on FollowPlayerMeleeBehaviour.PlanNextAction: if __instance
        //      is one of ours, sets the static flag _forceIsBotForFallback (saving
        //      the previous value in __state, guarding against re-entrancy).
        //   2. Prefix on Piece.IsBot(): if the flag is set, short-circuits and
        //      returns true without running the native body.
        //   3. Postfix on PlanNextAction: restores the flag to its value from
        //      before the call.
        //
        // Verified by decompilation that IsBot() is not called anywhere else in
        // PlanNextAction's call chain (ChooseAttackTarget/OwnerHeatMap/
        // GetPlayerHeatmap/MoveTowardsTile/FindPortalToShortcutThrough/
        // GetPlayerPieces — none of these names appear among IsBot()'s callers),
        // so there is no possible leak into evaluating another piece during this
        // same window.
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
                    prefix: new HarmonyMethod(typeof(DoriathPointLevelUpRule), nameof(FollowPlayerMeleeBehaviour_PlanNextAction_Prefix)),
                    postfix: new HarmonyMethod(typeof(DoriathPointLevelUpRule), nameof(FollowPlayerMeleeBehaviour_PlanNextAction_Postfix)));
                harmony.Patch(
                    isBotMethod,
                    prefix: new HarmonyMethod(typeof(DoriathPointLevelUpRule), nameof(Piece_IsBot_Prefix)));

                _followPlayerMeleePlanMethod = planMethod;
                _pieceIsBotMethod = isBotMethod;

                Plugin.Log?.LogInfo("[BossFallbackBehaviour] FollowPlayerMeleeBehaviour fallback net now active for MotherCy/ElvenSummoner/RootLord (targeted bypass of the native IsBot gate).");
            }
            catch (Exception ex)
            {
                // Explicit logging rather than an empty catch, to diagnose a possible
                // silent exception while installing the patch.
                Plugin.Log?.LogWarning($"[BossFallbackBehaviour] Exception in PatchFollowPlayerMeleeBehaviourGate — fallback net inactive: {ex}");
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
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(DoriathPointLevelUpRule), nameof(SuppressPatch)));
                        _suppressedMethods.Add(method);
                    }
                }

                var vrType = AccessTools.TypeByName("VRAdvancedStatsView");
                if (vrType != null)
                {
                    var method = AccessTools.Method(vrType, "GrabbedPieceHudInstantiator_CloneCurrentHudState_Postfix");
                    if (method != null)
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(DoriathPointLevelUpRule), nameof(SuppressPatch)));
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
                    typeof(DoriathPointLevelUpRule),
                    nameof(CreatePiece_Progression_Postfix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(SerializableEventQueue), "RespondToRequest"),
                prefix: new HarmonyMethod(
                    typeof(DoriathPointLevelUpRule),
                    nameof(SerializableEventQueue_RespondToRequest_Prefix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(Inventory), "RestoreReplenishables"),
                prefix: new HarmonyMethod(
                    typeof(DoriathPointLevelUpRule),
                    nameof(Inventory_RestoreReplenishables_Prefix)));

            // ── Hooks feeding the point counter ──────────────────────────
            // Damage dealt, units defeated, interactions (chests, doors, stands,
            // fountains, level exit), revives, gold pickups, and the turn queue
            // (needed to credit unattached summons back to their owner).
            harmony.Patch(
                original: AccessTools.Method(typeof(Ability), "GenerateAttackDamage"),
                postfix: new HarmonyMethod(
                    typeof(DoriathPointLevelUpRule),
                    nameof(Ability_GenerateAttackDamage_Postfix)));
            harmony.Patch(
                original: AccessTools.Method(typeof(MotherTracker), "TrackUnitDefeated"),
                prefix: new HarmonyMethod(
                    typeof(DoriathPointLevelUpRule),
                    nameof(MotherTracker_TrackUnitDefeated_Prefix)));
            harmony.Patch(
                original: AccessTools.Method(
                    typeof(Interactable),
                    "OnInteraction",
                    new[] { typeof(int), typeof(IntPoint2D), typeof(GameContext), typeof(int) }),
                prefix: new HarmonyMethod(
                    typeof(DoriathPointLevelUpRule),
                    nameof(Interactable_OnInteraction_Prefix)));
            harmony.Patch(
                original: AccessTools.Method(typeof(MotherTracker), "TrackRevive"),
                prefix: new HarmonyMethod(
                    typeof(DoriathPointLevelUpRule),
                    nameof(MotherTracker_TrackRevive_Prefix)));
            harmony.Patch(
                original: AccessTools.Constructor(
                    typeof(BoardgameActionPiecePickup),
                    new[] { typeof(GameContext), typeof(int), typeof(IntPoint2D), typeof(int), typeof(int) }),
                prefix: new HarmonyMethod(
                    typeof(DoriathPointLevelUpRule),
                    nameof(BoardgameActionPiecePickup_Prefix)));
            harmony.Patch(
                original: AccessTools.Constructor(typeof(RearrangePlayerTurnOrder), new[] { typeof(TurnQueue) }),
                postfix: new HarmonyMethod(
                    typeof(DoriathPointLevelUpRule),
                    nameof(RearrangePlayerTurnOrder_Constructor_Postfix)));
        }

        private static void CreatePiece_Progression_Postfix(ref Piece __result)
        {
            if (!_isActivated || !__result.IsPlayer()) return;
            __result.effectSink.TrySetStatMaxValue(Stats.Type.CritChance, 1);
            __result.EnableEffectState(EffectStateType.Flying);
            __result.effectSink.SetStatusEffectDuration(EffectStateType.Flying, 1);

            // Point counter starts at 0 (see the "POINT PROGRESSION" region below).
            __result.effectSink.AddStatusEffect(EffectStateType.StrengthInNumbers, 0);

            // The "level 0 knockdown" bonus can't be applied here: CreatePiece runs too
            // early (before/during the native piece stat init completes, which would
            // overwrite the change) — applied instead in
            // ApplyLevel0KnockdownBonusOnce(), on each player piece's first StartTurn
            // (see SerializableEventQueue_RespondToRequest_Prefix below), a point where
            // all native stats are already stabilized.
        }

        // pieceIds of player pieces that already received the "2 knockdowns at
        // level 0" bonus.
        //
        // Idempotence guard — only one StartTurn should count, never re-applied.
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
                Plugin.Log?.LogWarning($"[DoriathPointLevelUpRule] Error applying the level 0 knockdown bonus: {ex.Message}");
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
            }

            // In Doriath (PROGRESSIVE) the level-up trigger lived here: an
            // AddCardToPiece event with cardSource == Energy, meaning "the mana bar
            // just filled up". In Doriath (Point Progressive) that bar goes back to
            // vanilla behaviour (it hands out its bonus card) and level-ups are
            // driven by LevelUp(), called from the point accumulator in the
            // "POINT PROGRESSION" region below.
        }

        // Raises one hero by a level: stands them up, heals them, increments
        // CritChance, then grants the perks of the level reached. AddPoints() is the
        // only caller.
        private static void LevelUp(Piece piece)
        {
            // The event prefix used to hand us the GameContext. The point hooks have
            // no event queue at hand, so it is read from GameHub instead (same access
            // as DoriathPointEnemyPartyScaledRule).
            var gameContext = Traverse.Create(typeof(GameHub)).Field<GameContext>("gameContext").Value;
            var pieceId = piece.networkID;

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
                Plugin.Log?.LogInfo($"[DoriathPointLevelUpRule] pieceId={pieceId} stood back up (was knocked down) on level-up.");
            }

            // Heal on level-up
            piece.effectSink.Heal(piece.GetMaxHealth());
            piece.DisableEffectState(EffectStateType.Heal);
            piece.EnableEffectState(EffectStateType.Heal, 1);
            Plugin.Log?.LogInfo($"[DoriathPointLevelUpRule] pieceId={pieceId} fully healed on level-up (Health -> {piece.GetMaxHealth()}).");

            if (nextLevel < MaxLevel)
            {
                piece.effectSink.TrySetStatMaxValue(Stats.Type.CritChance, nextLevel + 1);
                nextLevel++;
                piece.effectSink.SetStatusEffectDuration(EffectStateType.Flying, nextLevel);

                string msg = GetLevelMessage(piece.boardPieceId, nextLevel);
                // Progression is per hero, so the message names whoever levelled up
                // rather than the party.
                GameUI.ShowCameraMessage(
                    $"<color=#F0F312>The </color><b>{GetHeroName(piece.boardPieceId)}</b> <color=#F0F312>has</color> <color=#00FF00>LEVELED UP!</color> {msg}", 6);

                // Stat perks (max HP, knockdowns, Magic/Strength) all come from one
                // shared table, read in the same order on the way up and on the way
                // down. See PerkTable.cs; DoriathPointLevelLossRule calls PerkTable.Revert.
                PerkTable.Apply(piece, nextLevel);

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
                        // Doriath (Point Progressive).json) — both are therefore changed together so
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
                // ── LEVEL 3: last-action crit buffs (handled by DoriathPointFreeThingsOnLastMoveAndCritRule) + 2nd KD ──
                // The knockdown comes from PerkTable above. No card at this level.
                else if (nextLevel == 3)
                {
                }
                // ── LEVEL 4: +2 max HP (the "Gold on CRIT" bonus, formerly level 9, is
                // merged in here: handled by the level >= 4 threshold in
                // DoriathPointFreeThingsOnLastMoveAndCritRule, no extra code needed) ─────────────
                else if (nextLevel == 4)
                {
                }
                // ── LEVEL 4: perk cards (formerly level 3) + CRIT buffs (handled by DoriathPointFreeThingsOnLastMoveAndCritRule) ──
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
                // removed) + FreeHealOnCrit via DoriathPointFreeThingsOnLastMoveAndCritRule ──
                // The stat bonus comes from PerkTable above (Magic for the Bard,
                // Warlock and Sorcerer, Strength for the others). No card at this level.
                else if (nextLevel == 8)
                {
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
                    // Handled by DoriathPointFreeRevolutionsAbilityOnCritRule
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  POINT PROGRESSION  (replaces the mana bar)
        // ════════════════════════════════════════════════════════════════
        // Each hero's XP counter is stored in the DURATION of the
        // EffectStateType.StrengthInNumbers effect on their piece. That is a native
        // value the game already synchronises over the network (so it is
        // multiplayer-safe with no home-made netcode) and displays in-game as a
        // numbered buff icon. 0..99 is the percentage of the current level; at 100
        // the hero gains a level and 100 is subtracted, so any surplus is kept and
        // nothing is lost.
        //
        // Point values and multiplier are taken as they are from TheGrayAlien's
        // "Demeo Revolutions (Points Progressive)" ruleset, for an identical pace.

        // Point values for the ruleset, serialised in the HouseRules JSON.
        //
        // Gains are floats: a value below 1 lets an action be worth less than a
        // whole point (0.4 x LevelPercentage 3.25 = 1 point earned). Any field
        // absent from the JSON keeps the default below (TheGrayAlien's values).
        public sealed class PointsConfig
        {
            // ── Combat ───────────────────────────────────────────────
            public float KillEnemy { get; set; } = 1;
            public float HurtEnemy { get; set; } = 1;
            public float HurtBoss { get; set; } = 2;
            public float KillBoss { get; set; } = 10;
            public float HurtSelf { get; set; } = -1;
            public float KillSelf { get; set; } = -2;

            // ── Player-to-player ─────────────────────────────────────
            // Negative in co-op, where the gain is simply cancelled. Set PVPisOn to
            // true to turn them into real losses.
            public float BuffPlayer { get; set; } = 1;
            public float HurtPlayer { get; set; } = -1;
            public float KillPlayer { get; set; } = -2;
            public float RevivePlayer { get; set; } = 4;

            // ── Exploration and loot ─────────────────────────────────
            public float LootGold { get; set; } = 2;
            public float LootChest { get; set; } = 3;
            public float LootStand { get; set; } = 4;
            public float OpenDoor { get; set; } = 1;
            public float UnlockDoor { get; set; } = 2;
            public float UseFountain { get; set; } = 4;

            // Bonus added to EVERY gain while the hero carries the key.
            public float Keyholder { get; set; } = 0;

            // Critical-hit bonus, paid once per successful attack rather than per
            // target: a crit is one dice roll, so an AoE hitting six enemies does not
            // multiply the bonus by six. 0 disables it.
            public float CritBonus { get; set; } = 0;

            // Flat amount paid to the WHOLE party when the floor exit is crossed.
            //
            // Guarantees a floor pace for support heroes, who hit rarely and would
            // otherwise fall behind, and rewards descending through the dungeon
            // rather than farming in place. 0 disables it.
            public float FloorClearBonus { get; set; } = 0;

            // Kill bonus by enemy size: +1 point per N max HP of the enemy killed.
            // 0 disables it. Does NOT apply to bosses, which get the flat KillBoss
            // amount instead.
            public int KillHealthDivisor { get; set; } = 10;

            // PVP: the revived hero has RevivePlayer points taken from them. false in
            // co-op.
            public bool PVPisOn { get; set; } = false;

            // Which summons credit their actions back to their owner: 0 none, 1 Cana
            // only, 2 Arly only, 3 Cana and Arly, 4 all of them (plus Verochka,
            // Tornado, the grapple totem, Sword of Avalon, SmiteWard and charmed
            // elementals).
            public int Points4Minions { get; set; } = 4;

            // Single pace dial: every raw gain is multiplied by this value. At 3.25,
            // hitting an enemy is worth 3.25% of a level.
            public float LevelPercentage { get; set; } = 3.25f;

            // Per-boss kill value: BoardPieceId name to raw amount, replacing
            // KillBoss for that piece. Also used to pay boss rates for a piece the
            // game does not mark as PieceType.Boss. For example:
            // { "ElvenSummoner": 12.48, "MotherCy": 15.15, "RootLord": 17.82 }.
            //
            // A real boss absent from the table falls back to KillBoss.
            //
            // Only the point value is affected: the game's PieceType is left alone,
            // so the boss health bar, the music, the spawn budget and the Consuming
            // Vortex all stay as they are. An unknown name is ignored with a warning
            // in the log rather than breaking the ruleset.
            public Dictionary<string, float> BossPieces { get; set; } = new Dictionary<string, float>();
        }

        // Active point values (copied from _config when the ruleset is activated).
        private static PointsConfig _pts = new();

        // BossPieces resolved once at activation: the table is consulted on every
        // hit landed, so it must not re-parse strings each time.
        private static readonly Dictionary<BoardPieceId, float> _bossForPoints = new();

        // True for a real game boss, or for a piece listed in BossPieces. Used ONLY
        // for point values.
        private static bool IsBossForPoints(Piece piece) =>
            piece.HasPieceType(PieceType.Boss) || _bossForPoints.ContainsKey(piece.boardPieceId);

        // Boss kill value: its own amount when BossPieces gives one, KillBoss
        // otherwise.
        private static float GetKillBossValue(Piece piece) =>
            _bossForPoints.TryGetValue(piece.boardPieceId, out var value) ? value : _pts.KillBoss;

        // Translates BossPieces (names) into BoardPieceId, at activation.
        private static void ResolveBossPieces()
        {
            _bossForPoints.Clear();
            if (_pts.BossPieces == null) return;

            foreach (var entry in _pts.BossPieces)
            {
                if (string.IsNullOrWhiteSpace(entry.Key)) continue;
                if (Enum.TryParse<BoardPieceId>(entry.Key.Trim(), out var id))
                {
                    _bossForPoints[id] = entry.Value;
                }
                else
                {
                    Plugin.Log?.LogWarning(
                        $"[DoriathPoint] BossPieces: '{entry.Key}' is not a known BoardPieceId — ignored.");
                }
            }

            foreach (var kv in _bossForPoints)
            {
                Plugin.Log?.LogInfo(
                    $"[DoriathPoint] Boss value: {kv.Key} = {kv.Value} raw.");
            }
        }

        private const int PointsPerLevel = 100;

        // Player pieces for the current turn. Used to credit the owning hero with
        // the kills and damage of their unattached summons (Verochka, Tornado, the
        // grapple totem, Sword of Avalon, SmiteWard, charmed elementals).
        private static List<Piece> _playerPieces = new();

        // The game attributes an exploding lamp's damage to the lamp itself, not to
        // the hero who set it off, so that hero is remembered here to be credited.
        private static Piece? _lampSource;

        // Current XP counter (0..99) of the hero.
        private static int GetPoints(Piece piece)
        {
            var pts = piece.effectSink.GetEffectStateDurationTurnsLeft(EffectStateType.StrengthInNumbers);

            // Above 998 means the "near permanent" duration the game sets for a real
            // StrengthInNumbers buff, not a score — start back from 0 rather than
            // handing out nine levels at once.
            return pts > 998 ? 0 : pts;
        }

        // Credits rawPoints raw points to a hero, after multiplication by
        // _pts.LevelPercentage, and triggers as many level-ups as the counter
        // crosses multiples of 100.
        private static void AddPoints(Piece? piece, float rawPoints)
        {
            if (!_isActivated || piece == null || !piece.IsPlayer()) return;

            // As in TheGrayAlien's ruleset: a negative total never takes points away,
            // it just cancels the gain, otherwise a missed AoE would push the hero
            // backwards.
            if (rawPoints <= 0) return;

            int points = GetPoints(piece) + (int)Math.Round(rawPoints * _pts.LevelPercentage);

            while (points >= PointsPerLevel && piece.GetStatMax(Stats.Type.CritChance) < MaxLevel)
            {
                points -= PointsPerLevel;
                LevelUp(piece);
            }

            // Max level reached: the bar stays full, there is nothing left to unlock.
            if (points >= PointsPerLevel) points = PointsPerLevel - 1;

            piece.effectSink.RemoveStatusEffect(EffectStateType.StrengthInNumbers);
            piece.effectSink.AddStatusEffect(EffectStateType.StrengthInNumbers, points);
        }

        // Walks up from the unit that acted to the hero to credit (summons, totems,
        // charmed elementals). Returns null when the action belongs to no hero.
        private static Piece? ResolveHero(Piece? unit)
        {
            if (unit == null) return null;
            if (unit.IsPlayer()) return unit;

            // Summons the game itself attaches to their master (Cana, Arly).
            // Points4Minions: 1 = Cana only, 2 = Arly only, 3 = both, 4 = all.
            if (unit.boardPieceId == BoardPieceId.WarlockMinion ||
                unit.boardPieceId == BoardPieceId.SellswordArbalestierActive)
            {
                bool credited = unit.boardPieceId == BoardPieceId.WarlockMinion
                    ? _pts.Points4Minions == 1 || _pts.Points4Minions > 2
                    : _pts.Points4Minions == 2 || _pts.Points4Minions > 2;
                if (!credited) return null;

                var pieceAI = unit.pieceAI;
                if (pieceAI == null) return null;

                var gameContext = Traverse.Create(typeof(GameHub)).Field<GameContext>("gameContext").Value;
                return pieceAI.memory.TryGetAssociatedPiece(gameContext.pieceAndTurnController, out var owner)
                    ? owner
                    : null;
            }

            // Summons with no native attachment: credited back to the class that
            // places them. Reserved for Points4Minions = 4 (all).
            if (_pts.Points4Minions < 4) return null;

            BoardPieceId heroId;
            switch (unit.boardPieceId)
            {
                case BoardPieceId.Verochka:
                    // A "confused" Verochka is the native enemy, not the Hunter's pet.
                    if (unit.HasEffectState(EffectStateType.ConfusedPermanentVisualOnly)) return null;
                    heroId = BoardPieceId.HeroHunter;
                    break;
                case BoardPieceId.Tornado:        heroId = BoardPieceId.HeroBard; break;
                case BoardPieceId.GrapplingTotem: heroId = BoardPieceId.HeroBarbarian; break;
                case BoardPieceId.SwordOfAvalon:  heroId = BoardPieceId.HeroRogue; break;
                case BoardPieceId.SmiteWard:      heroId = BoardPieceId.HeroGuardian; break;
                case BoardPieceId.IceElemental:
                case BoardPieceId.FireElemental:
                    // The reverse of Verochka: an elemental counts ONLY when charmed.
                    if (!unit.HasEffectState(EffectStateType.ConfusedPermanentVisualOnly)) return null;
                    heroId = BoardPieceId.HeroSorcerer;
                    break;
                default: return null;
            }

            foreach (var p in _playerPieces)
            {
                if (p.boardPieceId == heroId) return p;
            }

            return null;
        }

        private static bool IsLamp(Piece? piece)
        {
            return piece != null && piece.HasPieceType(PieceType.Prop) && piece.ToString().Contains("Lamp");
        }

        private static void RearrangePlayerTurnOrder_Constructor_Postfix(TurnQueue turnQueue)
        {
            if (!_isActivated) return;
            _playerPieces = turnQueue.GetPlayerPieces();
        }

        private static void Ability_GenerateAttackDamage_Postfix(
            Piece source, Piece mainTarget, Dice.Outcome diceResult, Piece[] targets)
        {
            if (!_isActivated || source == null) return;
            if (mainTarget != null && mainTarget.HasEffectState(EffectStateType.WizardDoppelganger)) return;

            if (source.IsPlayer())
            {
                // The hero hits a lamp: the explosion that follows will have the lamp
                // as its source, so it is credited back to them.
                if (IsLamp(mainTarget))
                {
                    _lampSource = source;
                    return;
                }

                if (targets != null)
                {
                    foreach (var t in targets)
                    {
                        if (!IsLamp(t)) continue;
                        _lampSource = source;
                        return;
                    }
                }

                _lampSource = null;
            }
            else if (_lampSource != null)
            {
                source = _lampSource;
            }

            var hero = ResolveHero(source);
            if (hero == null || targets == null) return;

            float add = 0f;
            foreach (var t in targets)
            {
                if (t == null || t.boardPieceId == BoardPieceId.GoldPile) continue;
                if (t.HasEffectState(EffectStateType.WizardDoppelganger)) continue;

                if (t == hero)
                {
                    if (!hero.IsDowned() && diceResult != Dice.Outcome.None) add += _pts.HurtSelf;
                    continue;
                }

                if (t.IsDowned() || t.IsImmuneToDamage()) continue;

                if (t.IsPlayer())
                {
                    // A diceResult of None means no attack roll: this is a buff on the ally.
                    add += diceResult == Dice.Outcome.None ? _pts.BuffPlayer : _pts.HurtPlayer;
                    if (hero.HasEffectState(EffectStateType.Key)) add += _pts.Keyholder;
                }
                else if (IsBossForPoints(t))
                {
                    add += _pts.HurtBoss;
                }
                else if (!t.IsBot() && !t.IsProp())
                {
                    add += _pts.HurtEnemy;
                    if (hero.HasEffectState(EffectStateType.Key)) add += _pts.Keyholder;
                }
            }

            // The crit bonus is paid ONCE per attack rather than per target, since it
            // is a single dice roll. Gated on add > 0 so a crit into thin air, or on
            // an immune target, is worth nothing.
            if (add > 0 && diceResult == Dice.Outcome.Crit) add += _pts.CritBonus;

            AddPoints(hero, add);
        }

        private static void MotherTracker_TrackUnitDefeated_Prefix(Piece defeatedUnit, Piece attackerUnit)
        {
            if (!_isActivated || defeatedUnit == null || attackerUnit == null) return;
            if (defeatedUnit.HasEffectState(EffectStateType.WizardDoppelganger)) return;

            if (attackerUnit.IsPlayer())
            {
                if (IsLamp(defeatedUnit))
                {
                    _lampSource = attackerUnit;
                    return;
                }
            }
            else if (_lampSource != null)
            {
                attackerUnit = _lampSource;
            }

            var hero = ResolveHero(attackerUnit);
            if (hero == null) return;

            float add;
            if (!defeatedUnit.IsPlayer())
            {
                add = _pts.KillEnemy;

                if (IsBossForPoints(defeatedUnit))
                {
                    // Flat amount, WITHOUT the size bonus: every boss has its own
                    // value in BossPieces, and max HP no longer makes it vary.
                    add += GetKillBossValue(defeatedUnit);
                }
                else if (_pts.KillHealthDivisor > 0)
                {
                    // Bonus by enemy size: +1 point per KillHealthDivisor max HP.
                    // At 10: trash with 5-15 HP gives +0 or +1, an elite with 40 HP
                    // gives +4. 0 disables it.
                    add += (int)defeatedUnit.GetMaxHealth() / _pts.KillHealthDivisor;
                }
            }
            else if (defeatedUnit == hero)
            {
                add = _pts.KillSelf;
            }
            else
            {
                add = _pts.KillPlayer;
            }

            if (hero.HasEffectState(EffectStateType.Key)) add += _pts.Keyholder;
            AddPoints(hero, add);
        }

        private static void Interactable_OnInteraction_Prefix(
            int pieceId, GameContext gameContext, IntPoint2D targetTile)
        {
            if (!_isActivated) return;

            var interactable = gameContext.pieceAndTurnController.GetInteractableAtPosition(targetTile);
            if (interactable == null) return;
            if (!gameContext.pieceAndTurnController.TryGetPiece(pieceId, out var piece)) return;
            if (!piece.IsPlayer()) return;

            float add;
            switch (interactable.type)
            {
                case Interactable.Type.Chest:           add = _pts.LootChest; break;
                case Interactable.Type.PotionStand:     add = _pts.LootStand; break;
                case Interactable.Type.Door:            add = _pts.OpenDoor; break;
                case Interactable.Type.AltarOfBlessing: add = _pts.UseFountain; break;

                case Interactable.Type.LevelExit:
                    // The exit only pays the hero carrying the key. Locked is the
                    // number of locks left, each worth one point.
                    if (!piece.HasEffectState(EffectStateType.Key)) return;
                    add = piece.HasEffectState(EffectStateType.Locked)
                        ? piece.effectSink.GetEffectStateDurationTurnsLeft(EffectStateType.Locked)
                        : _pts.UnlockDoor;
                    add += _pts.Keyholder;
                    AddPoints(piece, add);
                    AwardFloorClearBonus();
                    return;

                default: return;
            }

            if (piece.HasEffectState(EffectStateType.Key)) add += _pts.Keyholder;
            AddPoints(piece, add);
        }

        // End-of-floor amount, paid to the whole party when the key holder crosses
        // the exit — including heroes who are dead or downed, who played the floor too.
        private static void AwardFloorClearBonus()
        {
            if (_pts.FloorClearBonus <= 0 || _playerPieces == null) return;

            foreach (var p in _playerPieces)
            {
                if (p == null || !p.IsPlayer()) continue;
                AddPoints(p, _pts.FloorClearBonus);
            }
        }

        private static void MotherTracker_TrackRevive_Prefix(Piece revivedPiece, Piece sourcePiece)
        {
            if (!_isActivated || sourcePiece == null) return;
            if (revivedPiece == sourcePiece) return;

            // Co-op: the rescuer gains and the revived hero loses nothing. Points are
            // only taken away when PVPisOn.
            AddPoints(sourcePiece, _pts.RevivePlayer);

            if (_pts.PVPisOn && revivedPiece != null)
            {
                RemovePoints(revivedPiece, _pts.RevivePlayer);
            }
        }

        // Takes raw points away (PVP only), never going below 0.
        private static void RemovePoints(Piece piece, float rawPoints)
        {
            if (!_isActivated || !piece.IsPlayer() || rawPoints <= 0) return;

            int points = GetPoints(piece) - (int)Math.Round(rawPoints * _pts.LevelPercentage);
            if (points < 0) points = 0;

            piece.effectSink.RemoveStatusEffect(EffectStateType.StrengthInNumbers);
            piece.effectSink.AddStatusEffect(EffectStateType.StrengthInNumbers, points);
        }

        private static void BoardgameActionPiecePickup_Prefix(GameContext gameContext, int pieceId)
        {
            if (!_isActivated) return;
            if (!gameContext.pieceAndTurnController.TryGetPiece(pieceId, out var piece)) return;

            var hero = ResolveHero(piece);
            if (hero == null) return;

            float add = _pts.LootGold;
            if (hero.HasEffectState(EffectStateType.Key)) add += _pts.Keyholder;
            AddPoints(hero, add);
        }

        // Resets the XP counter to zero. Called by DoriathPointLevelLossRule: after a
        // level is lost, the hero restarts the previous level at 0%.
        internal static void ResetPoints(Piece piece)
        {
            if (piece == null || !piece.IsPlayer()) return;
            piece.effectSink.RemoveStatusEffect(EffectStateType.StrengthInNumbers);
            piece.effectSink.AddStatusEffect(EffectStateType.StrengthInNumbers, 0);
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
