namespace DoriathMod.Rules
{
    using System.Collections.Generic;
    using Boardgame.BoardEntities;
    using DataKeys;
    using HarmonyLib;

    /// <summary>
    /// Fixes the "boss that never ends its turn" bug WITHOUT disabling the Berserk effect itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Native mechanism recap (decompiled): EffectSink.CheckBerserk() applies the Berserk state (36) to a
    /// piece as soon as its health drops below its BerserkBelowHealth, with a PERMANENT duration (-1, never
    /// expires automatically). Behaviour.TryEndTurnAfterAttack(thisPiece): if thisPiece has the Berserk
    /// state AND its BoardPieceId is NOT in the native enum DataKeys.EndTurnAfterAttackWithBerserk (which
    /// contains only one member: SandScorpion), the method NEVER creates the EndTurn event -> the piece
    /// never gives back control.
    /// </para>
    /// <para>
    /// A member cannot be added to a .NET enum at runtime, so it is impossible to "sneak" ElvenSummoner
    /// into the native exception list directly. Chosen solution: a Prefix/Postfix that, only for the
    /// duration of the call to TryEndTurnAfterAttack, temporarily DISABLES the Berserk state (so the native
    /// method takes the normal path and does create the EndTurn event), then RE-ENABLES it right after
    /// (Postfix) - the piece therefore keeps the Berserk effect (visual, and any other effect tied to that
    /// state) for the rest of the game, but hands back control normally every turn instead of chaining
    /// indefinitely.
    /// </para>
    /// <para>
    /// HARDCODE: Outside the Rule/HR.Rulebook/Ruleset/JSON system (see AbilityMayNotTargetSelfRule.cs for
    /// details on why that removes the rule from Panel 1). This fix never needed
    /// HouseRules.Core.Types.Context - the original Harmony patch already only touched
    /// Boardgame.BoardEntities.AI.Behaviour via AccessTools, with no dependency on the
    /// Rule.OnActivate/OnDeactivate lifecycle. Direct conversion: patch applied once, unconditionally, from
    /// Plugin.Awake().
    /// </para>
    /// <para>
    /// UNIVERSAL: The JSON only lists pieces whose BerserkBelowHealth we explicitly modified (e.g.
    /// BossTown, MotherCy, WarlockMinion...) - but other pieces (e.g. Cavetroll/"the troll") have a NATIVE
    /// Berserk ability (the game's default value, never touched in our JSON, so invisible in
    /// "PieceConfigAdjusted") and would therefore also be exposed to the same native bug without us knowing
    /// it.
    /// </para>
    /// <para>
    /// Verification: the native default BerserkBelowHealth values per piece are stored in serialized Unity
    /// data (PieceConfigData, one object per piece), NOT in .NET bytecode - impossible to enumerate them
    /// exhaustively by decompilation (unlike Data.GameData.AIDirectorConfig, which is a static C# class
    /// with a .cctor of literal values). So a guaranteed-complete whitelist cannot be built.
    /// </para>
    /// <para>
    /// Chosen solution, more robust than a manual list: the patch no longer filters by BoardPieceId at all.
    /// It now applies to ANY non-hero piece that is actually in the Berserk state at the moment of
    /// TryEndTurnAfterAttack - exactly the same criterion the native method itself checks, minus the only
    /// existing native exception (SandScorpion, already handled correctly by the game). This automatically
    /// covers all current AND future pieces (trolls, bosses, minions...) without having to guess or
    /// maintain a manual list. The patch does strictly nothing for a piece that is never in Berserk (the
    /// HasEffectState(Berserk) check in the Prefix remains the real guard) - so there is no risk of side
    /// effects on unrelated pieces.
    /// </para>
    /// <para>
    /// Reliability: only uses public methods already used elsewhere in this mod
    /// (EnableEffectState/DisableEffectState/HasEffectState on Piece, same signatures as
    /// ProgressiveLevelRule.cs) - no reflection on private fields, no Transpiler. If Harmony cannot find
    /// TryEndTurnAfterAttack (name changed by a game update), it logs a warning and applies no patch,
    /// without ever crashing the rest of the mod.
    /// </para>
    /// </remarks>
    public static class BerserkEndsTurnHardcoded
    {
        // Only native exception confirmed by decompilation (enum
        // DataKeys.EndTurnAfterAttackWithBerserk): SandScorpion is already handled
        // correctly by the game, no need to touch it.
        private static readonly HashSet<BoardPieceId> NativelyExempt = new HashSet<BoardPieceId>
        {
            BoardPieceId.SandScorpion,
        };

        // Pieces for which we temporarily disabled Berserk during the native call,
        // to re-enable right after (key = networkID).
        private static readonly HashSet<int> _pendingRestore = new HashSet<int>();

        public static void Patch(Harmony harmony)
        {
            var behaviourType = AccessTools.TypeByName("Boardgame.BoardEntities.AI.Behaviour");
            if (behaviourType == null)
            {
                Plugin.Log?.LogWarning("[BerserkEndsTurnHardcoded] Boardgame.BoardEntities.AI.Behaviour introuvable — patch ignore, comportement natif du Berserk inchange.");
                return;
            }

            var method = AccessTools.Method(behaviourType, "TryEndTurnAfterAttack");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[BerserkEndsTurnHardcoded] TryEndTurnAfterAttack introuvable — patch ignore, comportement natif du Berserk inchange.");
                return;
            }

            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(BerserkEndsTurnHardcoded), nameof(TryEndTurnAfterAttack_Prefix)),
                postfix: new HarmonyMethod(typeof(BerserkEndsTurnHardcoded), nameof(TryEndTurnAfterAttack_Postfix)));

            Plugin.Log?.LogInfo(
                "[BerserkEndsTurnHardcoded] Patch applique (hors HouseRules/JSON, invisible du panneau) — s'applique desormais a TOUTE piece non-hero en Berserk (sauf SandScorpion, deja geree nativement).");
        }

        private static bool ShouldManage(Piece? thisPiece)
        {
            if (thisPiece == null) return false;
            if (thisPiece.IsPlayer()) return false; // only PlayerBerserk (106) for heroes, never affected by this bug
            if (NativelyExempt.Contains(thisPiece.boardPieceId)) return false;

            return true; // any other piece potentially exposed - the real filter is HasEffectState(Berserk) below
        }

        private static void TryEndTurnAfterAttack_Prefix(Piece thisPiece)
        {
            if (!ShouldManage(thisPiece)) return;
            if (!thisPiece.HasEffectState(EffectStateType.Berserk)) return;

            thisPiece.DisableEffectState(EffectStateType.Berserk);
            _pendingRestore.Add(thisPiece.networkID);
        }

        private static void TryEndTurnAfterAttack_Postfix(Piece thisPiece)
        {
            if (thisPiece == null) return;
            if (!_pendingRestore.Remove(thisPiece.networkID)) return;

            // Duration -1 = permanent, exactly as the game does natively in
            // EffectSink.CheckBerserk() - the piece stays in Berserk.
            thisPiece.EnableEffectState(EffectStateType.Berserk, -1, null);
        }
    }

    /// <summary>
    /// Follow-up to <see cref="BerserkEndsTurnHardcoded"/> above: gives Berserk pieces extra actions beyond
    /// the normal cap, in a controlled way (not the infinite-turn bug fixed above).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found by decompiling Piece.RestoreActionPoints() (native method called at the start of a piece's
    /// turn, which computes its number of ActionPoints for that turn): the game ALREADY has a native
    /// mechanism exactly for this - EffectStateType.ExtraAction (value 75, confirmed via the Constant
    /// table): if the piece has this state active at the moment of RestoreActionPoints(), it receives +1
    /// ActionPoint this turn (numActionPoints++). This is a native, bounded mechanism, unrelated to the
    /// "Berserk piece never ends its turn" bug fixed by BerserkEndsTurnHardcoded (different states:
    /// Berserk=36 vs ExtraAction=75).
    /// </para>
    /// <para>
    /// Fix: Prefix/Postfix on Piece.RestoreActionPoints(), same temporary-window technique as
    /// BerserkEndsTurnHardcoded above (minimal scope, nothing permanent): if the piece is in Berserk at the
    /// moment of the call, enable ExtraAction right before (so the native AP count calculation takes it
    /// into account, +1 this turn), then remove it right after (Postfix) - never leaving it active between
    /// turns, and without touching an ExtraAction already active natively for another reason (guarded by
    /// HasEffectState(ExtraAction) before acting, so as not to overwrite anything).
    /// </para>
    /// <para>
    /// Same scope as BerserkEndsTurnHardcoded (any non-hero piece in Berserk, except SandScorpion which is
    /// already handled natively) - the two fixes complement each other: one prevents the infinite turn, the
    /// other gives back, capped at +1/turn, some of the power Berserk was originally meant to provide.
    /// </para>
    /// </remarks>
    public static class BerserkExtraActionHardcoded
    {
        private static readonly HashSet<BoardPieceId> NativelyExempt = new HashSet<BoardPieceId>
        {
            BoardPieceId.SandScorpion,
        };

        // Pieces for which we ourselves enabled ExtraAction this turn (key =
        // networkID), to be removed in Postfix.
        private static readonly HashSet<int> _pendingRemove = new HashSet<int>();

        public static void Patch(Harmony harmony)
        {
            var pieceType = AccessTools.TypeByName("Boardgame.BoardEntities.Piece");
            if (pieceType == null)
            {
                Plugin.Log?.LogWarning("[BerserkExtraActionHardcoded] Boardgame.BoardEntities.Piece introuvable — patch ignore, aucun bonus d'action pour le Berserk.");
                return;
            }

            var method = AccessTools.Method(pieceType, "RestoreActionPoints");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[BerserkExtraActionHardcoded] RestoreActionPoints introuvable — patch ignore, aucun bonus d'action pour le Berserk.");
                return;
            }

            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(BerserkExtraActionHardcoded), nameof(RestoreActionPoints_Prefix)),
                postfix: new HarmonyMethod(typeof(BerserkExtraActionHardcoded), nameof(RestoreActionPoints_Postfix)));

            Plugin.Log?.LogInfo(
                "[BerserkExtraActionHardcoded] Patch applique (hors HouseRules/JSON, invisible du panneau) — " +
                "+1 ActionPoint/tour (via EffectStateType.ExtraAction natif) pour toute piece non-hero en Berserk (sauf SandScorpion).");
        }

        private static bool ShouldManage(Piece? thisPiece)
        {
            if (thisPiece == null) return false;
            if (thisPiece.IsPlayer()) return false;
            if (NativelyExempt.Contains(thisPiece.boardPieceId)) return false;

            return true;
        }

        private static void RestoreActionPoints_Prefix(Piece __instance)
        {
            if (!ShouldManage(__instance)) return;
            if (!__instance.HasEffectState(EffectStateType.Berserk)) return;
            // Already active natively for another reason: don't touch it (we don't
            // want to either duplicate it or remove it in Postfix afterward).
            if (__instance.HasEffectState(EffectStateType.ExtraAction)) return;

            __instance.EnableEffectState(EffectStateType.ExtraAction, 1, null);
            _pendingRemove.Add(__instance.networkID);
        }

        private static void RestoreActionPoints_Postfix(Piece __instance)
        {
            if (__instance == null) return;
            if (!_pendingRemove.Remove(__instance.networkID)) return;

            __instance.DisableEffectState(EffectStateType.ExtraAction);
        }
    }

    /// <summary>
    /// Prevents MotherCy, ElvenSummoner and RootLord from ending their turn automatically after a single
    /// successful ability while they still have Action Points (AP) left, by patching the shared
    /// TryEndTurnAfterAttack bottleneck.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Full decompilation of MotherCyBossBehaviour: its 3 most commonly used actions in practice
    /// (TryUseRain = LetItRain, TryUseElectricity, TryUseMeleeAttack) ALL call
    /// Behaviour.TryEndTurnAfterAttack(thisPiece) IMMEDIATELY AFTER successfully queuing an ability -
    /// without ever checking how many Action Points (AP) the piece has left. On a non-Berserk piece,
    /// TryEndTurnAfterAttack then unconditionally adds a CreateEndTurn event: the turn ends as soon as the
    /// first ability is used, regardless of remaining AP (4 in our config,
    /// PieceConfigAdjusted.ActionPoint). This is a native base-game design (these pieces most likely only
    /// had 1-2 AP originally, before our JSON's boost) - not a bug introduced by the mod.
    /// </para>
    /// <para>
    /// Decompiled comparison with RootLord (MotherCyRootLordBehaviour): out of its 5-6 action branches,
    /// only one (the very last, the "last resort" melee attack) calls TryEndTurnAfterAttack - all the
    /// others (RaiseRoots, walking to a water point, Whip...) simply queue the ability and `return`, never
    /// touching TryEndTurnAfterAttack. This is precisely what lets RootLord chain 4-5 actions per turn
    /// instead of just one.
    /// </para>
    /// <para>
    /// Extension to ElvenSummoner: Unlike MotherCy, ElvenSummonerBehaviour (its dedicated behaviour) NEVER
    /// calls TryEndTurnAfterAttack itself - verified against the full IL dump of CombatBehaviour and
    /// SummoningBehaviour (neither method calls it). Yet the log shows ElvenSummoner using
    /// "Zap"/"Electricity" before ending its turn - two abilities that appear NOWHERE in
    /// ElvenSummonerBehaviour's code (which only hardcodes TelekineticBurst/EmergencyTeleport/
    /// ElvenSummonerDeflect, confirmed by decoding the AbilityKey Constant table), but which ARE in the
    /// JSON list PieceAbilityListOverridden.ElvenSummoner. Cross-checking with PieceAI.SetupBuilder (same
    /// MethodSpec decoding technique): "RangedSpellCaster" (2nd in the list
    /// PieceBehavioursListOverridden.ElvenSummoner) resolves to the GENERIC class
    /// Boardgame.BoardEntities.AI.RangedAttackBehaviour - and RangedAttackBehaviour.TryActionWithAbility
    /// does call TryEndTurnAfterAttack (one of the 12 real call sites found during investigation, after
    /// correcting an earlier indexing error). So when ElvenSummonerBehaviour (1st in the list, supposed to
    /// always win selection with its fixed score of 999) doesn't "win" the selection on a given turn - the
    /// strongest hypothesis: an unhandled .NET exception inside SummoningBehaviour (a concrete candidate
    /// identified by direct bytecode reading: a `rem` - modulo - on `summoningRiftCycle[team-1].Length`,
    /// which throws if that array is empty for the team in question; silently caught by
    /// PieceAI.CreatePlan()'s try/catch, which just logs "Exception in behaviour" and moves on to the next
    /// behaviour in the list) - then it's "RangedSpellCaster" (= RangedAttackBehaviour) that takes over,
    /// picks a generic ability from the JSON list (Zap/Electricity...), and ends the turn via
    /// TryEndTurnAfterAttack. This last part (why ElvenSummonerBehaviour sometimes "loses" its selection
    /// turn) is not confirmed with the same certainty as for MotherCy - it is the strongest lead found, not
    /// a fact established by in-game testing.
    /// </para>
    /// <para>
    /// The important point for the fix: it doesn't matter WHICH path TryEndTurnAfterAttack is called
    /// through for a given piece (directly by its own Behaviour like MotherCy, or via a generic fallback
    /// behaviour like ElvenSummoner) - TryEndTurnAfterAttack remains the single bottleneck that actually
    /// adds the CreateEndTurn event. Patching THIS single point, rather than each Behaviour individually,
    /// therefore covers both cases with the same guarantee, without needing to fully nail down the "why" of
    /// the ElvenSummoner case.
    /// </para>
    /// <para>
    /// Goal: these pieces should never again end their own turn by themselves - remaining AP (not
    /// TryEndTurnAfterAttack) should decide when the turn stops, exactly like RootLord.
    /// </para>
    /// <para>
    /// Fix: a Prefix on Behaviour.TryEndTurnAfterAttack (the same method already patched by
    /// BerserkEndsTurnHardcoded earlier in this file - independent and compatible patches: Harmony runs ALL
    /// registered Prefixes on a method, the original method's execution is skipped as soon as ANY ONE
    /// Prefix returns false, and all Postfixes run regardless). For a targeted piece that still has at
    /// least 1 AP at the time of the call, the original method is skipped entirely: no CreateEndTurn event
    /// is added, Berserk or not - the turn then continues naturally via the engine's standard loop
    /// (SerializableEventQueue.PopulateSerializableEvents) as long as AP remains, exactly like RootLord. If
    /// the piece has no AP left (0), the original method is allowed to run normally (it will add
    /// CreateEndTurn as before - the engine would force the turn to end at that point anyway via its own
    /// "currentPiece out of action points so pushing EndTurn" check). And if the active behaviour finds
    /// nothing left to do at all in a given call, the already-documented native safety net ("No plan to
    /// execute" -> CreatePlan() returns null -> immediate EndTurn) remains intact and keeps applying
    /// normally - so there is no risk of an infinite turn on that side either, even for ElvenSummoner going
    /// through its fallback branch.
    /// </para>
    /// <para>
    /// Extension to RootLord: The same fix is also applied to RootLord, in addition to MotherCy and
    /// ElvenSummoner. RootLord did not have this problem as blatantly as MotherCy - only its very last
    /// branch (the "last resort" melee attack) calls TryEndTurnAfterAttack, as noted above - but nothing
    /// prevented precisely THAT branch from ending the turn prematurely if it gets chosen while AP remains.
    /// Same fix logic as for MotherCy/ElvenSummoner: TryEndTurnAfterAttack is skipped as long as AP
    /// remains, regardless of which Behaviour (RootLordBehaviour or its RangedSpellCaster =
    /// RangedAttackBehaviour fallback, same mechanism as for ElvenSummoner) called it.
    /// </para>
    /// <para>
    /// To extend this behaviour to other pieces later, just add their BoardPieceId to TargetPieces below.
    /// </para>
    /// </remarks>
    public static class BossExtraActionsHardcoded
    {
        private static readonly HashSet<BoardPieceId> TargetPieces = new HashSet<BoardPieceId>
        {
            BoardPieceId.MotherCy,
            BoardPieceId.ElvenSummoner,
            BoardPieceId.RootLord,
        };

        public static void Patch(Harmony harmony)
        {
            var behaviourType = AccessTools.TypeByName("Boardgame.BoardEntities.AI.Behaviour");
            if (behaviourType == null)
            {
                Plugin.Log?.LogWarning("[BossExtraActionsHardcoded] Boardgame.BoardEntities.AI.Behaviour introuvable — patch ignore, MotherCy/ElvenSummoner/RootLord gardent leur comportement natif.");
                return;
            }

            var method = AccessTools.Method(behaviourType, "TryEndTurnAfterAttack");
            if (method == null)
            {
                Plugin.Log?.LogWarning("[BossExtraActionsHardcoded] TryEndTurnAfterAttack introuvable — patch ignore, MotherCy/ElvenSummoner/RootLord gardent leur comportement natif.");
                return;
            }

            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(BossExtraActionsHardcoded), nameof(TryEndTurnAfterAttack_Prefix)));

            Plugin.Log?.LogInfo(
                "[BossExtraActionsHardcoded] Patch applique (hors HouseRules/JSON, invisible du panneau) — MotherCy/ElvenSummoner/RootLord ne terminent plus leur tour automatiquement apres une ability tant qu'il leur reste du PA.");
        }

        /// <summary>
        /// Returns false to completely skip the original TryEndTurnAfterAttack (no CreateEndTurn added)
        /// when it is a targeted piece that still has AP left. Returns true (native behavior unchanged) in
        /// all other cases.
        /// </summary>
        private static bool TryEndTurnAfterAttack_Prefix(Piece thisPiece)
        {
            if (thisPiece == null) return true;
            if (!TargetPieces.Contains(thisPiece.boardPieceId)) return true;
            if (thisPiece.GetActionPoints() > 0) return false;

            return true;
        }
    }
}
