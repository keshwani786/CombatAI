using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // ===================================================================================
    // PROOF-OF-CONCEPT #2: full AI combat override via the official AgentComponent
    // extension point -- no Harmony required (see combat_ai_research.txt, section 2
    // "Layer 2 -- The actual per-tick decision hand-off").
    //
    // Agent.OnAIInputSet (Agent.cs) is invoked by native code every tick once it has
    // computed its desired attack/defend/move input for this agent, and fans out to
    // every AgentComponent attached to the agent via AgentComponent.OnAIInputSet
    // (public virtual). Whatever we write into eventFlag/movementFlag/inputVector here
    // is what the engine actually acts on -- native's own choice is discarded.
    //
    // This component nulls BOTH the event flags and the movement flags (attack AND
    // defend) every tick, for every AI-controlled human agent it's attached to. If this
    // works, those agents will stand completely still in combat: no attacks, no
    // parries/blocks, no kicks, no directional movement. That is the simplest possible
    // proof that we have full, reliable control over native's combat decision before it
    // reaches the animation/mission-flow layer.
    //
    // Deliberately NOT using Harmony (unlike the archived CombatNullifierPreview.cs) --
    // Agent.AddComponent(AgentComponent) and AgentComponent.OnAIInputSet are both public,
    // first-party extension points.
    // ===================================================================================
    // ===================================================================================
    // PROOF-OF-CONCEPT #3: INJECTING a scripted action sequence, not just voiding input.
    //
    // Triggered by Ctrl+Shift+I (CombatAiMissionBehavior). While a sequence is running,
    // OnAIInputSet no longer nulls the flags -- it forces a SPECIFIC one every tick:
    //   1. ForceAttackRight: movementFlag = MovementControlFlag.AttackRight (no movement).
    //   2. Once Agent.GetCurrentActionStage(1) shows the attack actually started AND then
    //      left the Attack* stages (i.e. it completed), switch to:
    //   ForceDefendRight: movementFlag = MovementControlFlag.DefendRight, held for a fixed
    //      duration so it's observable, then the agent returns to whatever the normal
    //      null-override state was (CombatAiState.OverrideEnabled).
    //
    // Using GetCurrentActionStage(1)/ActionStage (see combat_ai_research.txt section 2,
    // "the windup/ready/release cycle") to detect real completion, instead of a blind
    // timer, means this rides the actual native animation state machine rather than
    // guessing a duration -- same idea as AutoBlockModel reading GetCurrentActionStage(1)
    // for the player's auto-block assist (section 6a).
    // ===================================================================================
    public enum InjectPhase
    {
        Idle,
        ForceAttackRight,
        ForceDefendRight
    }

    // Internal states of the ContinuousAttackMode press/release cycle. Attack flags
    // are LEVEL-triggered -- holding AttackRight means "hold the weapon ready",
    // clearing it means "release the swing", exactly like the player holding and
    // letting go of the attack key. Proven by the first attackloop test: a
    // constantly-held flag just froze the agent in the ready pose forever.
    enum AttackLoopState
    {
        Inactive,
        Holding,     // AttackRight asserted: readying / holding the attack
        Releasing    // flag cleared: swing releases, wait for it to finish + rearm gap
    }

    // Duel-mode attack flavor, chosen when a swing starts (research doc sec. 10).
    enum DuelAttackKind
    {
        Quick,    // release almost immediately after ready
        Normal,   // short hold
        Held,     // long randomized hold -- unreadable timing
        Feint     // start one direction, redirect once while ready, then release
    }

    enum DuelState
    {
        Approach,    // close distance / wait / decide
        Windup,      // attack flag asserted, weapon readied (hold duration varies)
        FeintCancel, // feint step: briefly assert block to CANCEL the ready
        FeintReady,  // feint step: assert the NEW direction until re-readied, then release
        Release,     // flag cleared, swing in flight
        Recover      // brief gap after the swing before next decision
    }

    public partial class CombatAiOverrideComponent : AgentComponent
    {
        // How long to hold the forced attack before giving up and moving on anyway, in
        // case the attack never actually starts (e.g. agent too far from any target).
        private const float MaxAttackPhaseDuration = 3f;

        // How long to hold the forced block pose so it's clearly observable.
        private const float DefendHoldDuration = 1.5f;

        // How long to wait, after forcing a reactive block, before giving up on seeing
        // a ParriedMelee/BlockedMelee outcome for the detailed log.
        private const float BlockOutcomeWatchDuration = 1.0f;

        // Caps the number of detailed [PERFECTBLOCK] trigger/outcome log lines across
        // ALL agents so a long test session doesn't flood combat_ai.log.
        private const int MaxDetailedBlockLogs = 40;
        private static int _detailedBlockLogCount;

        // Diagnostic heartbeat for attackloop/perfectblock: periodically logs raw state
        // regardless of whether anything "interesting" happened, so we can see (a) how
        // often OnAIInputSet/OnTick actually run for this agent, (b) what ActionStage
        // it's sitting in, and (c) whether Agent.MovementFlags (the ACTUAL flags the
        // engine currently has, read back after the fact) match what we forced -- this
        // directly tests whether something else is overwriting our override.
        private const float HeartbeatInterval = 2f;
        private const int MaxHeartbeatLogs = 600;
        private static int _heartbeatLogCount;
        private float _heartbeatTimer;

        // Agents farther than this from the player don't participate in the test
        // modes at all (and don't log heartbeats) -- in a town/arena mission there
        // can be 200+ NPCs and only the ones near the fight matter.
        private const float TestRangeMeters = 15f;

        // PerfectBlock only reacts if the player is close enough to actually hit
        // us and is roughly facing us -- otherwise every nearby NPC flinches at
        // every player swing and "timeout" outcomes become meaningless noise.
        // ~3m covers typical melee reach (long spears can reach slightly beyond;
        // raise this if testing against polearms).
        private const float BlockRangeMeters = 3f;
        private const float MinFacingDot = 0.2f;

        // ContinuousAttackMode press/release cycle timing (driven per-frame).
        private const float LoopReadyHoldSeconds = 0.12f;  // brief hold once ready, like a player pause
        private const float LoopRearmSeconds = 0.15f;      // gap between swings after the previous one ends
        private const float LoopFailsafeSeconds = 1.5f;    // if we never reach AttackReady, release+retry anyway

        private AttackLoopState _loopState = AttackLoopState.Inactive;
        private float _loopTimer;

        // ---- Duel mode state ----
        // Engage range is computed from the actual wielded weapon's length
        // (MeleeReach()) -- a 1h sword engages ~1.5m, polearms further. Fixed
        // numbers bait the AI into whiffing at unreachable targets, and a whiffed
        // swing can't defend (can't block mid-Release) = free hit for the player.
        private const float ReachMarginMeters = 0.55f; // arm extension + step-in
        private const float MinEngageRange = 1.15f;
        private const float MaxEngageRange = 3.2f;

        // Defense: the vulnerable window is intentionally TINY -- only for ~0.3s
        // right after we commit to a feint redirect (that's the readable trap the
        // player must punish). Every other time we block at 90%.
        private const float BlockChanceNormal = 0.90f;
        private const float BlockChanceVulnerable = 0.25f;
        private const float PostFeintVulnerableSeconds = 0.3f;
        // Session 45 (expectation system): our own attack is OUR vulnerable
        // window -- while we hold a ready/feint, our read of the player is
        // suppressed for OwnActionVulnerableSeconds after the action ends.
        // If the player never swings during it (hesitated/missed the window),
        // _recoverBoost shortens our next think delay -- we act first.
        private const float OwnActionVulnerableSeconds = 0.45f;
        private const float RecoverBoostSeconds = 0.50f;
        private const float ExpectCommitGate = 0.45f;   // live read needed for an early commit to actually fire
        private const float BlockChanceMin = 0.10f;   // even a surprise gets answered sometimes
        private const float BlockChanceMax = 0.85f;   // ceiling -- a fully read swing still slips through
        private const float BlockRatingLo = 0.15f;    // rating -> chance ramp edges
        private const float BlockRatingHi = 0.80f;
        private const float CameraRatingWeight = 0.20f;
        private const float ExpectAimRad = 0.35f;     // angle swing + camera pushed to the swing's side = expected
        private const float SquareOnAimRad = 0.17f;   // angle swing out of a square-on camera = surprise
        private const float StallPunishChance = 0.70f;  // roll when the player's chain is saturated/overheld

        private const float OpportunistChance = 0.4f;   // quick-attack into the player's ready
        private const float DuelThinkMin = 0.05f;       // short gap between decisions
        private const float DuelThinkMax = 0.30f;
        // Session 34 tempo spec: the old 0.4-0.9s answer-back beat read as
        // passivity -- the only real rule is "don't swing while THEIR swing
        // is in flight". 0.15-0.45s keeps the rally rhythm.
        private const float AfterAttackThinkMin = 0.15f;
        private const float AfterAttackThinkMax = 0.45f;
        private const float DuelRecoverSeconds = 0.15f;
        private const float DuelWindupFailsafe = 0.9f;
        private const float FeintCancelTimeout = 0.45f;   // give up the cancel if the ready still hasn't dropped
        private const float FeintReReadyFailsafe = 0.7f;  // bail if the new direction never readies
        private const int MaxFeintRedirects = 4;          // feint chains: block-tap -> new dir -> repeat -> release
        private static float FeintContinueChance => CombatAiTune.FeintContinue;  // per-link chance to chain ANOTHER redirect
        // While the player HOLDS an attack ready (not released) we concede a
        // ~1-1.5s chance window (user spec: "give player chance to attack")
        // -- then attack anyway. Rolled per swing chain; the elapsed clock
        // counts TOTAL ready time so their feint-cancels can't farm a fresh
        // grace on every re-ready.
        private const float PlayerReadyGraceMin = 1.0f;
        private const float PlayerReadyGraceMax = 1.5f;
        // Double-hold: when BOTH sides hold a ready, don't race a quick
        // release into theirs -- the player's angle lands first (user
        // spec). Extend our hold up to this extra while pressing IN, so
        // they have to commit (block / release into our defense / get
        // shoved off the ready).
        private static float DoubleHoldExtraSeconds => CombatAiTune.DoubleHoldExtra;
        // Kick evasion: kicks/bashes never touch the channel-1 combat
        // stages -- they ride channel 0 as ActionCodeType Kick..WeaponBash
        // (28..31; MasterStrikes' detection recipe) plus a clip-name
        // fallback for modded kicks. A block does NOT stop a kick (guard
        // break + stagger): the answer is a hard LATERAL step, ~90% when
        // the kicker is in range and roughly facing us.
        private const float KickEvadeChance = 0.90f;
        private const float KickEvadeRange = 1.9f;
        private const float KickFacingDot = 0.35f;

        // ---- Step dodge (combatai.dodge) ----
        // No animation assets: a release-edge threat in reach gets answered
        // with a hard side/back step instead of a block some share of the
        // time. TeleportToPosition supplies the displacement (SCO's proven
        // channel for dodges) while a matching Strafe/Backward flag plays
        // the leg gait so it reads as a step, not a slide. Direction is
        // open-space scored -- a surrounded defender steps toward the gap.
        private const float DodgeDurationSeconds = 0.32f;   // locomotion-step window (fallback path)
        private const float DodgeAnimSeconds = 0.45f;       // animated-dodge window (clips carry root motion)
        private const float DodgeAnimSpeed = 1.25f;         // clip playback speed -- bumped for faster-swing mods (RCA)
        private const float DodgeAimAngleRad = 0.6f;        // aim yaw offset (~34deg, was ~20) that marks a clearly-aimed attack -- below it the attack counts as square-on -> rule 3 plain block
        private const float DodgeBlockHoldSeconds = 0.4f;   // how long the rule-3 plain block stays raised (covers the incoming hit window)
        private const float DodgeSquareOnFlipChance = 0.4f; // square-on UP/DOWN: chance to dodge the LESS-open side so the player can't pre-track it
        private const float DodgeSpeedMultiplier = 2.5f;    // raised speed cap during the step (locomotion-driven)
        private const float DodgeClosingBiasSpeed = 2.0f;   // threat closing speed (m/s) that starts favoring side-steps
        private const float DodgeCooldownSeconds = 2.5f;    // per-agent anti-spam
        // Evade folded into duel mode (Session 26, raised again in Session 27,
        // then again in Session 29 for aggressive testing of the new back-
        // step+block combo): 0.30/0.55/0.40 -> 0.45/0.70/0.55 -> 0.60/0.80/
        // 0.70 -> 0.90 flat. 0.90 is explicitly a TESTING value (user: "for
        // testing now") -- dial back down once the back-step+block combo
        // feel is confirmed, block/riposte should still cover some of the
        // remainder in real play.
        private const float DodgeChanceSingle = 0.90f;      // vs 1 threat
        private const float DodgeChanceMulti = 0.90f;       // outnumbered: making space is the defense
        private const float DodgeChanceSide = 0.90f;        // blindside release (poor block arc anyway)
        private static float DuelDodgeChance => CombatAiTune.DuelDodge;        // Session 33: duel defense split -- 40% dodge / 60% plain block per committed swing (user-specified mix)
        private const float DodgeOpennessRadius = 3.5f;     // enemies inside this radius crowd the step

        private static readonly Random DuelRng = new Random();
        private static readonly Agent.MovementControlFlag[] AttackDirs =
        {
            Agent.MovementControlFlag.AttackLeft,
            Agent.MovementControlFlag.AttackRight,
            Agent.MovementControlFlag.AttackUp,
            Agent.MovementControlFlag.AttackDown
        };

        private DuelState _duelState = DuelState.Approach;
        private float _duelTimer;
        private float _thinkDelay;
        private float _duelHoldSeconds;
        private Agent.MovementControlFlag _attackDir = Agent.MovementControlFlag.AttackRight;

        // Learned direction support (Session, arena crossbowmen): weapon stats lie
        // -- the Vlandian 2h sidearms report thrust_speed > 0 but their "staff"
        // usage action set has no stab, so the engine silently ignores a held
        // AttackDown flag and the agent sits in Guard for the whole windup.
        // Ground truth is the failsafe: a direction that failed to ready twice
        // while the body was free is blacklisted until a different weapon is
        // wielded. First miss marks it suspect; reaching a Ready stage clears
        // the suspicion; a second miss removes it from every pick pool.
        private Agent.MovementControlFlag _dirSuspect = Agent.MovementControlFlag.None;
        private Agent.MovementControlFlag _dirUnsupported = Agent.MovementControlFlag.None;
        private ItemObject _dirWeapon;
        private bool _feintPlanned;
        private bool _feinted;
        private float _feintAt;
        private int _feintChain;             // redirects done in the current swing (0 = none)
        // NOTE: no strafe/turn flag is stored for the feint anymore -- movement
        // is suppressed while the agent is inside a combat action (ready/defend),
        // so any movement OR'd onto the feint is a silent no-op.
        private float _feintSettleSeconds;   // randomized settle after each re-ready before chaining

        // ---- Session 34: pressure/initiative/kick state ----
        private bool _kickPrevActive;          // channel-0 kick edge detect
        private float _pressureSide = 1f;      // band sidestep side (-1/+1), re-rolled per attack
        private bool _handOffToNative;         // beyond the leash -> base game owns the body
        private Agent.MovementControlFlag _approachGait = Agent.MovementControlFlag.Forward;
        private float _playerReadyGrace = 1.25f; // held-attack grace, rolled per chain
        private int _feintStreak;              // recent feints -- feeds the surprise quick
        private bool _pushRolled;              // double-hold push roll made for this hold
        private float _pushUntil;              // elapsed-time endpoint of the extra shove

        // ---- looktest oscillation state ----
        private const float LookTestHoldSeconds = 0.6f;
        private const float LookTestReleaseSeconds = 0.6f;
        private const float LookTestAngleRadians = 0.26f; // ~15 deg
        private float _lookTestTimer;
        private bool _lookTestHoldActive;
        private float _lookTestDir = 1f;
        private bool _targetFrameSet; // SetTargetPositionAndDirection installed a frame -- must ClearTargetFrame on mode switch
        private bool _autoTargetDisabled; // SetAutomaticTargetSelection(false) -- must re-enable on mode exit
        private bool _poiActive;        // SetLookToPointOfInterest pending -- must Disable on exit
        private float _lookTestBaseAngle; // captured LookDirectionAsAngle at each hold edge

        // Looktest "torso" variant: legs pinned by a static frame (like
        // perfectblock), torso aimed by LookDirectionAsAngle -- tests whether
        // the two channels are independent enough for a pure torso feint.
        private bool _lookTestTorsoCaptured;
        private Vec2 _torsoPinPos;
        private Vec3 _torsoPinFacing;

        // Spar defender: short hold on the last threat target so the torso
        // doesn't flicker between threat and the released null state.
        private const float DefendTargetHoldSeconds = 0.4f;
        private float _defendTargetHold;
        private Agent _lastThreat;
        // Duel group-fight free stance: true while target is deliberately
        // released (no commitment, 2+ nearby enemies) so the torso doesn't
        // trace anyone until it actually needs to attack or block. Movement
        // itself stays on the plain flag lever (PreferredStrafeSide below) --
        // an earlier scripted-frame "diagonal weave" experiment was reverted
        // (caused glitching / broke blocking).
        private bool _freeStance;

        // PerfectBlock experiment: pin legs to the start frame
        // (SetTargetPositionAndDirection with the agent's OWN position + its
        // starting facing -- freeze is WANTED here) while the target lock aims
        // the torso at the player. Hypothesis: legs stay at start facing,
        // torso rotates to block.
        private bool _pbFrameCaptured;
        private Vec2 _pbPos;
        private Vec3 _pbFacing;
        private float _pbTargetHold; // brief hold on the player target after a block, then release to null

        // SAFE liveness checks. Agent.Clear() (engine-side, on removal) nulls
        // the managed Mission reference AND zeroes every cached native pointer
        // (_pointer, _positionPointer, _statePointer, ...). After that ANY
        // call on the wrapper -- even IsActive()/Health/Index/Position -- goes
        // through UIntPtr.Zero and is a live 0xC0000005 candidate. Mission
        // != null is pure managed state: the ONLY test safe on a cleared
        // wrapper, so it must run before every native call.
        private static bool Live(Agent a) => a != null && a.Mission != null;

        // Usable as a combat target/threat: wrapper live AND active AND not
        // mid-death. The Health>0 half also keeps dying agents out of our
        // SetTargetAgent writes so the native target slot is cleared BEFORE
        // the engine frees the body -- a dangling native target resolved by
        // worker-thread agent code is the prime suspect for the repeated
        // ACCESS_VIOLATION crashes (observed on thread 10, ~10min into fights
        // where agents had been dying around the duel the whole time).
        private static bool CombatLive(Agent a) => Live(a) && a.IsActive() && a.Health > 0f;

        // ---- AV diagnosis instrumentation ------------------------------------
        // The recurring crash is 0xC0000005 under Mission.Tick. With
        // [HandleProcessCorruptedStateExceptions] + catch(AccessViolationException)
        // on DrivePerFrame/OnTick/OnAIInputSet we can turn the next crash into
        // evidence: _lastOp names the native call in flight when it faults.
        // String literals only (no alloc) and all reads inside the catch use
        // _dbgIndex/_dbgName captured while the wrapper was provably live --
        // calling Agent.Index/Name inside the catch could fault again if the
        // wrapper was cleared.
        private string _lastOp = "init";
        private int _avCount;
        private bool _driveDisabledByAv;
        private int _dbgIndex = -1;
        private string _dbgName = "?";

        private void LogAv(string where, AccessViolationException ex)
        {
            _avCount++;
            if (_avCount <= 20)
                CombatAiLog.Log($"[AV#{_avCount}] Agent #{_dbgIndex} ({_dbgName}) at={where} lastOp={_lastOp} " +
                    $"desired={_desiredMovementFlag} duelState={_duelState} freeStance={_freeStance} frameSet={_targetFrameSet} " +
                    $"stack={ex.StackTrace}");
            if (_avCount >= 10)
            {
                // Whatever this agent is doing keeps faulting -- stop driving it
                // so the mission can continue and the log stays readable.
                _driveDisabledByAv = true;
                _drivingMovementFlags = false;
                _targetFrameSet = false;
            }
        }

        // Write-coalescing wrapper for SetTargetAgent: the native call is only
        // made when the current target isn't already the desired one. Cuts a
        // big chunk of the per-frame MBAPI churn (every driven agent wrote a
        // SetTargetAgent every frame before this -- sustained native-call
        // volume is a leading crash suspect while the engine ticks agents
        // on parallel threads).
        private void EnsureTargetAgent(Agent t)
        {
            if (CombatAiState.LockThrashMode) return;   // thrash driver owns the slot
            // CombatLive(t): a dying/removed target is written as null, so the
            // native slot is cleared early instead of dangling. If the slot
            // already holds a cleared target the identity compare mismatches
            // and we heal it with null on the next write.
            Agent desired = CombatLive(t) ? t : null;
            // IntendedTarget = "who I'm fighting" -- managed intent, always
            // recorded even when the free policy below nulls the native write.
            IntendedTarget = desired;
            // FREE TARGET policy: every positive lock write in this file
            // funnels through here, so forcing desired=null keeps the native
            // slot at -1 for the whole drive. `attack` keeps the lock only
            // while we hold an attack (Windup..Recover) -- the transition is
            // this same coalesced write, never mid-block-decision. The release
            // itself goes through ReleaseCombatTarget below (guard is armed
            // whenever _freeTargetActive is true).
            if (_freeTargetActive && desired != null && !LockWantedNow())
                desired = null;
            // linger: an explicit null (no attack target this tick) must not drop a lock
            // that LockWantedNow still holds -- the release happens after the linger.
            if (_freeTargetActive && desired == null && CombatLive(Agent.GetTargetAgent()) && LockWantedNow())
                return;
            _lastOp = "GetTargetAgent";
            if (Agent.GetTargetAgent() != desired)
            {
                if (desired == null && NativeTargetGuard.GuardedRelease)
                {
                    // HARD INVARIANT (research 12): once the native-hook
                    // subsystem is armed, EVERY null-target write goes through
                    // ReleaseCombatTarget — it pokes {aiComp+0x19c=0 (the
                    // crash-gate mode), aiComp+0x1304=-1 (stale goal)} BEFORE
                    // queueing the SetTargetAgent(null) pending release, which
                    // closes the <=1-tick window where the path planner could
                    // deref record -1. Never call SetTargetAgent(null) raw
                    // while the guard is armed.
                    _lastOp = "ReleaseCombatTarget";
                    _lcRelAt = _brainClock;
                    NativeTargetGuard.ReleaseCombatTarget(Agent);
                }
                else
                {
                    _lastOp = "SetTargetAgent";
                    if (desired != null && _brainClock - _lcRelAt < 0.3f)
                    {
                        _lcCycles++;
                        if (_lcLogCount < 80) { _lcLogCount++; CombatAiLog.Log($"[LOCKCYC] Agent #{Agent.Index}: relock {_brainClock - _lcRelAt:F3}s after release (state={_duelState} stage={Agent.GetCurrentActionStage(1)} cycles={_lcCycles})"); }
                    }
                    if (desired != null)
                        // Legit re-lock by our own driving — the release no
                        // longer needs to hold, so its watchdog entry is done
                        // (the detour still logs the managed write itself).
                        NativeTargetGuard.UnmarkReleased(Agent);
                    Agent.SetTargetAgent(desired);
                }
            }
        }

        // Disable native auto-retargeting so OUR SetTargetAgent calls are
        // deterministic (no native fighting back to the nearest threat).
        // MUST be restored with SetAutoTargeting(true) when the mode ends or
        // the agent's targeting stays broken after our control stops.
        private void SetAutoTargeting(bool enabled)
        {
            if (enabled == !_autoTargetDisabled)
                return;
            _lastOp = "SetAutomaticTargetSelection";
            Agent.SetAutomaticTargetSelection(enabled);
            _autoTargetDisabled = !enabled;
            CombatAiLog.Log($"[MODES] Agent #{Agent.Index}: auto target selection {(enabled ? "RESTORED" : "disabled")}.");
        }

        // Is the scripted target frame legitimately in use this frame? Looktest
        // and PerfectBlock hold it continuously; duel only during feint
        // redirects (spar members: only the attacker runs the duel FSM).
        private bool FrameInUseByActiveMode(bool eligible)
        {
            if (!eligible)
                return false;
            if (CombatAiState.LookTestMode || CombatAiState.PerfectBlockMode)
                return true;
            if (CombatAiState.SparMode)
            {
                // Defender pins its legs every frame; attacker (duel FSM) uses
                // the frame during feints + held-swing sway.
                if (Agent.Index == CombatAiState.SparDefenderIndex)
                    return true;
                if (Agent.Index == CombatAiState.SparAttackerIndex)
                    return _duelState == DuelState.Windup
                        || _duelState == DuelState.FeintCancel
                        || _duelState == DuelState.FeintReady;
                return false;
            }
            if (!CombatAiState.DuelMode && !CombatAiState.GroupFightMode)
                return false;
            if (!WieldsMeleeWeapon())
                return false;
            return _duelState == DuelState.Windup
                || _duelState == DuelState.FeintCancel
                || _duelState == DuelState.FeintReady;
        }
        private float _vulnerableTimer;      // >0 while the post-feint window is open
        private bool _ownActionActive;       // our attack/feint was in flight last tick
        private bool _playerSwungDuringOwn;  // player attacked during our vulnerable window
        private float _recoverBoost;         // >0 briefly after a missed counter-window -> faster initiative
        private bool _stallPunished;         // already punished this overheld chain
        private bool _frozeThisRelease;      // this release gets no defensive answer (block-chance roll failed)
        // Session 67 "visible miss": a failed GROUP release-block roll still
        // raises a guard -- on a WRONG line (_misreadDefendFlag), so the miss
        // reads as a misread instead of a statue (zero animation). Set at the
        // locked-target roll; cleared wherever _frozeThisRelease clears.
        private Agent.MovementControlFlag _misreadDefendFlag = Agent.MovementControlFlag.None;
        private float _releaseRead = 0.5f;   // read latched at the release edge (it decays during flight)
        private float _releaseCam = 0.5f;    // camera-aim term latched alongside _releaseRead
        private float _feintReadyAt = -1f;   // _duelTimer value when the feint re-readied (-1 = not yet)
        private bool _feintLogged;
        // Movement flavor: drift while holding an attack / while waiting in
        // Approach -- a static attacker is easy to read. OR'd onto the attack flag.
        private Agent.MovementControlFlag _approachMove = Agent.MovementControlFlag.None;
        private Agent.MovementControlFlag _approachStrafe = Agent.MovementControlFlag.StrafeLeft; // diagonal approach side
        private bool _playerSwungSinceOurs;  // turn-taking: did the player get a swing in?
        private float _playerReadyElapsed;   // how long the player has been holding an attack ready
        private bool _defendAllowed;
        private bool _opportunistAllowed;
        private Agent.ActionStage _prevPlayerStage = Agent.ActionStage.None;

        // ---- T1/T2 defense + group-fight state (Session 19 design) ----
        // T1 (vs the player): PlayerCombatModel reads his direction+tempo
        // habits. Confident read -> COMMIT an early block at predictedHold -
        // latency (feints beat it organically); weak read -> wait for release
        // and race _reactionLatency against the measured time-to-hit -- a lost
        // race is an organic miss, not a dice roll.
        // T2 (vs AI): flat skill-check roll + cheaper reflex, no model.
        private float _reactionLatency = -1f;   // seeded once (Agent.Index must exist)
        private Agent _duelTarget;              // current FSM target (set per frame)
        private Agent _forcedDuelTarget;        // spar/group pin: overrides nearest-enemy pick
        private bool _sparThreats;              // threat scan accepts spar pair + player regardless of team
        private bool _useAssignedThreats;       // group mode: threats come from the engagement map, no field scan
        private bool _multiEnemy;               // group mode: 2+ live threats -> keep moving, halved feints
        private float _feintRateScale = 1f;     // 0.5 for outnumbered defenders
        private bool _chainDecided;             // defense decided for this swing chain (not per feint leg)
        private int _defCommitState;            // 0=none 1=armed(waiting commitAt) 2=holding the read
        private Agent.MovementControlFlag _defCommitFlag;
        private float _defCommitAt;             // ready-elapsed when the committed block starts
        private float _noAttackElapsed;         // time since target left all attack stages
        private float _releaseElapsed;          // how long their committed release has flown
        private bool _sawRelease;               // a Release stage happened in this chain
        private bool _blockedTheirSwing;        // we held a defend while their release resolved -> riposte
        private Agent.MovementControlFlag _lastAttackDir; // anti-pattern: penalize repeating the last line
        private readonly List<Agent> _attackersBuf = new List<Agent>(4);
        // ---- Session 64 group-fight rules ----
        // front-cone lock, 2-swing discipline, clearance-vetted slot steering,
        // N-threat side blocks, and a torso jab at adjacent non-locked enemies.
        private bool _groupDefender;            // outnumbered defender this tick -> side-roll + shield/side block rules
        private int _groupLockIdx = -1;         // lock reported by the last [GLOCK] line
        private int _gswingState;               // flanker 2-swing latch: 0 none, 1 front-free, 2 back-arc, 3 denied-front
        private Agent _jabTarget;               // group torso-jab target (non-locked enemy in front)
        private float _jabScanAt = -99f;        // throttle the attacker's jab-candidate field scan (~5Hz)
        private bool _duelHoldExtended;         // stagger hold already applied to the current swing
        private readonly List<Agent> _coAttackersBuf = new List<Agent>(4);  // attackers sharing OUR victim
        // Session 65 free-agent rule: a role-2+ attacker ON ME is the free
        // agent -- unbound from my view cone, works the back. The defender
        // partitions it out of the front-lock candidates and adds a keep-at-
        // bay repulsion to its free-space strafe; side-block scans unchanged.
        private readonly List<Agent> _freeAgentsBuf = new List<Agent>(2);   // role>=2 attackers on me
        private readonly List<Agent> _frontAttackersBuf = new List<Agent>(4); // role 0/1 attackers on me
        private readonly List<int> _freeIdxs = new List<int>(2);            // sorted indices of _freeAgentsBuf
        private readonly List<int> _lastFreeIdxs = new List<int>(2);        // last [GFREE]-reported set
        private static int _glockLogCount;
        private static int _gswingLogCount;
        private static int _gfreeLogCount;
        private const int MaxGlockLogs = 80;
        private const int MaxGswingLogs = 80;
        private const int MaxGfreeLogs = 60;
        private const float FlankerBackArcDeg = 120f;    // |bearing| off the victim's facing = "working the back"
        private const float FreeAgentBayMeters = 3f;     // keep-at-bay repulsion radius (outnumbered defender)
        private const float FrontLockTieDeg = 10f;      // bearing tie band for the front-lock tiebreak
        private const float PlayerBearingBonusDeg = 15f; // player stays in front on near-ties
        // ---- Session 66: pre-contact LINE ADVANCE (user spec: "each team
        // comes in line of 5 and then start fighting together"). While an
        // agent's assigned victim is beyond contact range it marches to a
        // perpendicular slot on a line standing just outside contact of the
        // enemy centroid -- no attack bits until its victim is in reach.
        private bool _groupAdvancing;               // inside the advance phase (for the [GROUP] advancing= count)
        private float _advRefreshAt = -99f;         // _brainClock of the last team/enemy scan (~0.3s cadence)
        private readonly List<Agent> _advTeam = new List<Agent>(8);   // live AI teammates (self incl.), sorted by Index
        private Vec2 _advEnemyCentroid;
        private int _advEnemyCount;
        private static int _gadvLogCount;
        private const int MaxGadvLogs = 60;
        private const float AdvRefreshSeconds = 0.3f;
        private const float AdvSlotSpacing = 1.2f;  // metres between neighbouring line slots
        private const float AdvAheadMeters = 2.5f;  // lead this far past the team mean -> hold the line
        public bool IsGroupAdvancing => _groupAdvancing;
        // Spar rig v2: attacker picks the nearer front slot (left OR right)
        // instead of always circling to the defender's left; defender rest
        // focus alternates between player and attacker.
        private int _sparSlotSide;              // 0 unset, +1 defender-left, -1 defender-right
        private float _slotPathAt = -99f;       // Session 63: slot-point path-legality throttle
        private bool _slotPathOk = true;
        private bool _slotPathDead;
        private float _jabTimer;                // spar defender: torso-jab window on the AI attacker
        private float _jabCooldown;             // ...between jab windows
        private bool _pureReactiveDefense;      // spar defender: react to releases only, no commit model

        // Session 52: the duel defence brain (S1/S2/S3 + FightScript) can be
        // ported to the spar DEFENDER via combatai.sparbrain. BrainVs(t) is
        // the defence-side "is this the player we think about" test; attack-
        // side/initiative sites keep the raw IsMainAgent/!_pureReactive check.
        // Session 62 B2: spar defender vs the PLAYER uses the duel initiative
        // gates (tempo/quiet/cancel-gap/grace/post-block kind) when enabled.
        private bool SparDuelGate => _pureReactiveDefense && SparBrainActive && CombatAiState.SparDuelInitiative;
        private bool SparBrainActive => CombatAiState.SparBrain && CombatAiState.SparMode && _pureReactiveDefense;
        private bool BrainVs(Agent t) => t != null && t.IsMainAgent && (!_pureReactiveDefense || SparBrainActive);
        // Spar defender leg pin: a FIXED captured frame (pos+facing), like
        // perfectblock's start pin -- legs literally cannot rotate when the
        // lock snaps to a side threat, so all block motion is torso-only.
        private bool _legPinOwned;              // we own the scripted frame -> suppress FSM sway/clear
        private bool _sparPinSet;
        // Session 56 torso-move experiment state (spar defender only).
        private CombatAiState.TorsoMoveModeT _tmMode = CombatAiState.TorsoMoveModeT.Pin;
        private bool _tmPinCaptured;
        private bool _tmFormationSet;
        private bool _tmLocoWindow;
        private int _tmStomps;                  // formation-frame stomps caught by the engine
        private int _tmBlocked, _tmClean;       // blows on the defender this mission
        private float _tmAng;                   // angleBetween the two attackers (ViewKeepPlan)
        // ---- FREE TARGET (Session 68, combatai.freetarget) ----
        // _freeTargetActive: this agent runs with the combat target RELEASED
        // (native slot -1) and the formation frame as the ONLY position/facing
        // writer. Set per tick in DrivePerFrameCore from mode+index gates;
        // requires NativeTargetGuard.ArmedLevel >= 2 (fail closed -> legacy).
        // IntendedTarget = "who I'm fighting" -- the managed intent the lock
        // stood in for (recorded in EnsureTargetAgent BEFORE the free policy
        // nulls the write). The native GetTargetAgent slot is diagnostics only.
        private Agent IntendedTarget;
        // ---- NATIVE STEER (combatai.steer, Option A) ----
        // _steerChan/_steerCtx: this agent's aiComp+0x15d8 heading channel /
        // +0x138c steering-ctx dir pointers, resolved once via
        // NativeSteerOverride.ResolveChannel. While steered, the combat lock
        // stays ON (the free policy never activates for us) and the heading-
        // command detour feeds our policy dir to the engine's own rate-limited
        // turn channel. Duel only.
        private UIntPtr _steerChan, _steerCtx;
        private bool _steerRegistered;
        // Spar-defender steer (Session: steer-spar): same hook, different
        // policy. _steerSparDef = "this agent is the steered spar defender
        // this tick" -- read by ComputeDesiredFlag (rig-path force) and
        // StartDuelAttack (side-matched swing). _steerLastA = last commanded
        // steer heading (rad) -- the >160deg HOLD case's memory, the steer
        // analog of the free path's _ftTargetA.
        private bool _steerSparDef;
        // Group pair fight: this agent is a steered group fighter -- the
        // lock sits on its lineup counterpart permanently and the steer
        // channel aims the body at it. Same attack-side machinery as the
        // steered spar defender (_attackDir is side-matched + live
        // re-evaluated), different heading policy (target bearing, not the
        // bisector).
        private bool _steerGroup;
        private float _steerLastA = -999f;
        // [SPARSTEER] bounded ~1s log (cap 200).
        private int _ssLogCount;
        private const int MaxSsLogs = 200;
        private float _ssLastLogAt = -99f;
        private bool _freeTargetActive;
        private bool _freeMarked;               // watchdog MarkReleased done for this free session
        private Vec2 _ftHeading;                // last commanded free-mode frame heading
        private int _ftLogCount;
        private float _ftLastLogAt = -99f;
        private bool _ftLookRtLogged;           // lookangle writer: one-time set->get round-trip logged
        private bool _ftMoveDirLogged;          // movedir writer: one-time first-write log
        private bool _ftReseed;                 // free heading re-seeded from current facing on next drive tick
        private bool _ftHandoffLock;            // last-seen handoff-lock state (transition logging)
        private int _ftHandoffLogCount;
        private const int MaxHandoffLogs = 40;
        private float _ftApproachStart = -1f;   // free-look approach drive clock (-1 = inactive)
        private float _ftSpread;                // sparfree: attacker spread deg this tick
        private Agent _ftPrimary;               // sparfree heading: current PRIMARY threat
        private bool _freeWasActive;            // engagement-log counter reset latch
        private int _sfAtkLogCount;             // [SPARFREE] attack gate/start-end lines
        private const int MaxSfAtkLogs = 80;
        private bool _sfAtkActive;              // defender attack window open (end log)
        // [SMOOTH] per-tick smoothness probe (ai30 "skipping frames"): does
        // native rewrite the heading between our writes, and is our tick even?
        private int _smTicks, _smJerk, _smOver, _smLogCount;
        private const int MaxSmLogs = 120;
        private float _smDtMin = 9f, _smDtMax, _smDtSum, _smLastAt = -99f;
        private float _smLastCmdDeg = -999f, _smLastYawDeg = -999f, _smLastStepDeg;
        private float _smMaxVel, _smMaxAcc;
        // ---- ai31: second-order heading smoothing ----
        private float _ftAngVel;                // commanded angular velocity (rad/s)
        private float _ftTargetA = -999f;       // rate-capped policy target angle (rad)
        private float _ftPrimSwitchAt = -99f;   // last primary switch (1.2s cooldown)
        private float _nearSince = -1f;         // proximity-switch latch
        private bool _faSw, _fpSw;
        private float _faSwAt, _fpSwAt;         // per-attacker swing start (>=0.15s rule)
        private int _blockedSupp;               // [SPARFREE] blocked-log suppression counter
        private float _lastBlockedAt = -99f;
        private float _smPrevVel;               // [SMOOTH] previous |angVel| for maxAcc
        // [TRACE] ring buffer (64 ticks): cmd/act/vel/lock per tick
        private readonly float[] _trT = new float[64], _trCmd = new float[64],
            _trAct = new float[64], _trVel = new float[64];
        private readonly int[] _trLock = new int[64];
        private int _trIdx, _trDumps, _trLockPrev;
        private const int MaxTrDumps = 20;
        private int _saLogCount;                // [SPARATK] bounded log budget
        private float _saLastLogAt = -99f;
        private const int MaxSaLogs = 200;
        private int _sfLogCount;
        private const int MaxSfLogs = 200;
        // [SPARVIEW] metric (sparfree defender, LOOKONLY-style)
        private int _svLogCount, _svSamples, _svViewL, _svViewB;
        private bool _svWasActive;
        private bool _ftEngaged;                // any threat within 8m (log gating, ai30)
        private const int MaxSvLogs = 300;
        private float _svLastLogAt = -99f;
        // ---- LOOK-ONLY duel diagnostic (combatai.lookonly) ----
        private int _loIntent;                  // 0 orbitL 1 orbitR 2 back 3 close 4 strafe 5 pause
        private float _loIntentUntil = -99f;
        private int _loStrafeSide = 1;
        private bool _loWasActive;
        private int _loLogCount;
        private const int MaxLoLogs = 150;
        private float _loLastLogAt = -99f;
        private int _loSamples, _loInView;
        private float _loSumErrB, _loSumErrL, _loMaxErr;
        // ---- BODY FEINT (Session 70, combatai.freetarget bodyfeint) ----
        // During the feint states with the lock released, the free driver
        // twists the body via SetMovementDirection (instant at target=-1).
        private bool _bfArmed;                  // body feint armed for this chain element
        private bool _bfPrevInFeint;            // transition detect for arm/end
        private int _bfSide = 1;                // twist side (+1/-1, rolled once per element)
        private float _bfElapsed;               // time in feint states (ramp clock)
        private float _bfOffsetDeg;             // current twist magnitude (deg, 0..55)
        private float _bfPeakErr;               // max legs-vs-commanded error seen while armed
        private float _bfLastAt = -99f;         // last body feint start (1.5s cooldown)
        private static int _bfLogCount;
        private const int MaxBfLogs = 60;

        // SINGLE source of truth for "does the combat lock hold right now" --
        // used by EnsureTargetAgent (positive write vs guarded release) AND
        // the free driver's skip-when-locked rule, so they can't disagree.
        private float _lcRelAt = -99f; private int _lcCycles; private static int _lcLogCount;
        private const float LockLingerSeconds = 0.40f;
        private float _lockLingerUntil = -99f;
        private bool LockWantedNow()
        {
            if (CombatAiState.FreeTarget != CombatAiState.FreeTargetModeT.AttackOnly)
                return false;
            // (sparfree defender: the AttackOnly rule now applies like the
            // duel -- SparDefAttackViewDeg caps the lock-acquisition turn at
            // ~30deg, so the attack-window lock can't snap the body; ai30)
            if (BodyFeintActiveNow())
                return false;
            // LOCK LINGER (ai36 NH log: 14 of 32 release->relock pairs were <0.3s, 8 <0.1s,
            // and 16 releases landed mid combat sub-state 6/7/8): every FSM hop into
            // Approach (block-cancel, feint cancel, chain gap) used to release the lock
            // (poke mode/goal) and re-acquire it a few ticks later -- the native combat
            // steering lost its target mid-hold, which reads as weapon shutter. The lock
            // now persists while any own attack stage is live and LockLingerSeconds after
            // the last wanted moment.
            Agent.ActionStage ownS = Agent.GetCurrentActionStage(1);
            bool wanted = _duelState != DuelState.Approach || _handOffToNative
                || ownS == Agent.ActionStage.AttackReady || ownS == Agent.ActionStage.AttackQuickReady
                || ownS == Agent.ActionStage.AttackRelease;
            if (wanted)
            {
                _lockLingerUntil = _brainClock + LockLingerSeconds;
                return true;
            }
            return _brainClock < _lockLingerUntil;
        }

        private bool FeintStatesActive => _duelState == DuelState.FeintCancel
            || _duelState == DuelState.FeintReady;

        // sparfree heading: is this agent mid-swing (channel-1 Attack* stage)?
        private static bool FtSwinging(Agent a)
        {
            if (!CombatLive(a)) return false;
            Agent.ActionStage s = a.GetCurrentActionStage(1);
            return s == Agent.ActionStage.AttackReady || s == Agent.ActionStage.AttackQuickReady
                || s == Agent.ActionStage.AttackRelease;
        }

        // Mid-swing for >=0.15s (ai31 primary rule): raw stage check latches a
        // start timestamp; the answer goes true once the swing has persisted.
        private bool FtSwing15(Agent a, ref bool latch, ref float swAt)
        {
            bool s = FtSwinging(a);
            if (s && !latch) swAt = _brainClock;
            latch = s;
            return s && _brainClock - swAt >= 0.15f;
        }

        // SPAR-DEFENDER heading policy (Session 62 / ai31 rules), extracted
        // from the free driver so the FREE-TARGET path and the NATIVE-STEER
        // path share the exact same sub-logic + constants -- the two cannot
        // drift. Bisector of the unit directions to the AI attacker (u1) and
        // the player (u2): spread <=90deg -> pure bisector; 90..160 -> blend
        // toward the sticky primary; >160 with nobody swinging -> HOLD
        // `holdA` (the caller's last commanded heading: _ftTargetA in free
        // mode, _steerLastA under steer -- the blend is never chased across
        // the seam). STICKY PRIMARY: a >=0.15s swing by exactly one attacker
        // wins (the 1.2s switch cooldown is bypassed only when our current
        // heading _ftHeading is already within 90deg of it); otherwise the
        // previous primary stands; proximity may switch only when the other
        // is >=1.5m nearer for >=1.0s. Single live threat -> its bearing.
        // Side effects: _ftSpread, _ftPrimary, _ftPrimSwitchAt, _nearSince,
        // _faSw/_faSwAt, _fpSw/_fpSwAt (kept live under steer so the primary
        // latch doesn't restart on a steer->free switch).
        // Returns -999 when no threat bearing exists (caller falls back).
        private float SparDefBisectorAngle(float holdA)
        {
            Agent fa = GetAgentByIndex(CombatAiState.SparAttackerIndex);
            Agent fp = Agent.Mission?.MainAgent;
            Vec2 u1 = Vec2.Zero, u2 = Vec2.Zero;
            if (CombatLive(fa)) u1 = FlatUnit(fa.Position.AsVec2 - Agent.Position.AsVec2);
            if (CombatLive(fp)) u2 = FlatUnit(fp.Position.AsVec2 - Agent.Position.AsVec2);
            if (u1.LengthSquared > 0.0001f && u2.LengthSquared > 0.0001f)
            {
                float spread = MathF.Abs(MathF.Atan2(u1.x * u2.y - u1.y * u2.x,
                    u1.x * u2.x + u1.y * u2.y)) * 57.2958f;
                _ftSpread = spread;
                bool a1sw = FtSwing15(fa, ref _faSw, ref _faSwAt);
                bool a2sw = FtSwing15(fp, ref _fpSw, ref _fpSwAt);
                Vec2 dfa = fa.Position.AsVec2 - Agent.Position.AsVec2;
                Vec2 dfp = fp.Position.AsVec2 - Agent.Position.AsVec2;
                if (a1sw != a2sw)
                {
                    Agent sw = a1sw ? fa : fp;
                    if (_ftPrimary != sw && CombatLive(sw))
                    {
                        Vec2 su = sw == fa ? u1 : u2;
                        float sErr = _ftHeading.LengthSquared > 0.0001f
                            ? MathF.Abs(MathF.Atan2(su.x * _ftHeading.y - su.y * _ftHeading.x,
                                su.x * _ftHeading.x + su.y * _ftHeading.y)) * 57.2958f : 0f;
                        if (_brainClock - _ftPrimSwitchAt >= 1.2f || sErr <= 90f)
                        {
                            _ftPrimary = sw;
                            _ftPrimSwitchAt = _brainClock;
                        }
                    }
                }
                else if (!a1sw && !a2sw && CombatLive(_ftPrimary)
                    && (fa != _ftPrimary || fp != _ftPrimary))
                {
                    Agent nearer = dfa.LengthSquared < dfp.LengthSquared ? fa : fp;
                    float dN = (nearer.Position.AsVec2 - Agent.Position.AsVec2).Length;
                    float dP = (_ftPrimary.Position.AsVec2 - Agent.Position.AsVec2).Length;
                    if (nearer != _ftPrimary && dP - dN >= 1.5f)
                    {
                        if (_nearSince < 0f) _nearSince = _brainClock;
                        if (_brainClock - _nearSince >= 1.0f
                            && _brainClock - _ftPrimSwitchAt >= 1.2f)
                        {
                            _ftPrimary = nearer;
                            _ftPrimSwitchAt = _brainClock;
                        }
                    }
                    else
                        _nearSince = -1f;
                }
                if (!CombatLive(_ftPrimary))
                    _ftPrimary = dfa.LengthSquared < dfp.LengthSquared ? fa : fp;
                Vec2 prim = _ftPrimary == fp ? u2 : u1;
                if (spread > 160f && !a1sw && !a2sw)
                {
                    // Nobody swinging and threats on opposite sides:
                    // HOLD -- don't chase the blend across the seam.
                    return holdA;
                }
                Vec2 bis = FlatUnit(u1 + u2);
                if (spread <= 90f)
                    return MathF.Atan2(bis.y, bis.x);
                float tB = spread > 160f ? 1f : (spread - 90f) / 70f;
                float aBis = MathF.Atan2(bis.y, bis.x);
                float aPri = MathF.Atan2(prim.y, prim.x);
                return aBis + NormalizeAngle(aPri - aBis) * tB;
            }
            if (u1.LengthSquared > 0.0001f)
                return MathF.Atan2(u1.y, u1.x);
            if (u2.LengthSquared > 0.0001f)
                return MathF.Atan2(u2.y, u2.x);
            return -999f;
        }

        private bool BodyFeintActiveNow()
            => CombatAiState.FreeBodyFeint && _freeTargetActive
               && CombatAiState.FreeFace == CombatAiState.FreeFaceT.MoveDir
               && FeintStatesActive && _bfArmed && CombatLive(_duelTarget)
               && !DodgeOwnsBody && !_handOffToNative;

        // ---- LOOK-ONLY (combatai.lookonly) ----
        // Position intents relative to a facing pinned on the player: orbit
        // (strafe arc), back off, close in, sidestep, brief pause -- re-rolled
        // every 2-4s. Ring radius ~reach*1.3..2.2 enforced by distance
        // overrides so the AI never drifts out or presses nose-to-nose.
        private static readonly string[] LookOnlyIntentNames = { "orbitL", "orbitR", "back", "close", "strafe", "pause" };
        private Agent.MovementControlFlag LookOnlyMoveFlag(float dt, float dist)
        {
            float reach = MeleeReach();
            if (dist > reach * 2.2f) { _loIntent = 3; _loIntentUntil = _brainClock + 0.6f; }
            else if (_brainClock >= _loIntentUntil || dist < reach * 1.05f)
            {
                double r = DuelRng.NextDouble();
                switch (CombatAiState.LookOnlyStyle)
                {
                    case CombatAiState.LookOnlyStyleT.Orbit:
                        _loIntent = r < 0.5 ? 0 : 1; break;
                    case CombatAiState.LookOnlyStyleT.Strafe:
                        _loIntent = r < 0.6 ? 4 : r < 0.85 ? 3 : 5; break;
                    default: // mixed
                        _loIntent = r < 0.25 ? 0 : r < 0.5 ? 1 : r < 0.65 ? 2 : r < 0.85 ? 4 : r < 0.95 ? 3 : 5;
                        break;
                }
                _loStrafeSide = DuelRng.NextDouble() < 0.5 ? -1 : 1;
                _loIntentUntil = _brainClock + 2f + (float)DuelRng.NextDouble() * 2f;
            }
            Agent.MovementControlFlag flag;
            switch (_loIntent)
            {
                case 0: flag = Agent.MovementControlFlag.StrafeLeft; break;
                case 1: flag = Agent.MovementControlFlag.StrafeRight; break;
                case 2: flag = dist < reach * 2.0f ? Agent.MovementControlFlag.Backward : Agent.MovementControlFlag.None; break;
                case 3: flag = Agent.MovementControlFlag.Forward; break;
                case 4: flag = _loStrafeSide < 0 ? Agent.MovementControlFlag.StrafeLeft : Agent.MovementControlFlag.StrafeRight; break;
                default: flag = Agent.MovementControlFlag.None; break;   // pause
            }
            // Ring push: orbit/strafe also edge in or out to hold the band.
            if ((_loIntent == 0 || _loIntent == 1 || _loIntent == 4) && dist > reach * 1.8f)
                flag |= Agent.MovementControlFlag.Forward;
            return flag;
        }

        // In-view metric: both body yaw (GetMovementDirection) and LookDirection
        // yaw vs the player bearing; inView90 counts |errLook| <= 45deg.
        private void LookOnlyTick(float dt, Agent target, float dist)
        {
            _loWasActive = true;
            Vec2 toT = target.Position.AsVec2 - Agent.Position.AsVec2;
            if (toT.LengthSquared < 0.0001f) return;
            float bearDeg = MathF.Atan2(toT.y, toT.x) * 57.2958f;
            Vec2 legs = Agent.GetMovementDirection();
            Vec3 look = Agent.LookDirection;
            float errB = legs.LengthSquared > 0.0001f
                ? MathF.Abs(NormalizeAngle((bearDeg - MathF.Atan2(legs.y, legs.x) * 57.2958f) * 0.0174533f)) * 57.2958f : -1f;
            float errL = look.LengthSquared > 0.0001f
                ? MathF.Abs(NormalizeAngle((bearDeg - MathF.Atan2(look.y, look.x) * 57.2958f) * 0.0174533f)) * 57.2958f : -1f;
            _loSamples++;
            if (errB >= 0f) { _loSumErrB += errB; if (errB > _loMaxErr) _loMaxErr = errB; }
            if (errL >= 0f) { _loSumErrL += errL; if (errL <= 45f) _loInView++; }
            if (_loLogCount < MaxLoLogs && _brainClock - _loLastLogAt >= 1.0f)
            {
                _loLastLogAt = _brainClock;
                _loLogCount++;
                float iv = _loSamples > 0 ? 100f * _loInView / _loSamples : 0f;
                float mB = _loSamples > 0 ? _loSumErrB / _loSamples : 0f;
                float mL = _loSamples > 0 ? _loSumErrL / _loSamples : 0f;
                Agent cur = Agent.GetTargetAgent();
                CombatAiState.LookOnlyInView90 = iv;
                CombatAiState.LookOnlyMeanErr = mB;
                CombatAiState.LookOnlyMaxErr = _loMaxErr;
                CombatAiLog.Log($"[LOOKONLY] Agent #{Agent.Index}: inView90={iv:F0}% meanErr={mB:F0} " +
                    $"errBody={errB:F0} errLook={errL:F0} maxErr={_loMaxErr:F0} " +
                    $"intent={LookOnlyIntentNames[_loIntent]} dist={dist:F2} " +
                    $"vel={Agent.GetCurrentVelocity().Length:F2} nativeTgt={(Live(cur) ? "#" + cur.Index : "-1")}");
            }
        }
        // ---- held-attack shutter tests (combatai.holdspin / lockthrash) ----
        private float _hsSpinAngle, _hsLastLogAt = -99f;
        private bool _hsInit;
        private float _thrPhaseAt = -99f;
        private bool _thrLocked;
        private int _hsLogCount, _thrLogCount;

        private void HoldTestTick(float dt, Agent player)
        {
            _brainClock += 0f;   // clock already advanced by the FSM call
            if (CombatAiState.HoldSpinMode)
            {
                // HOLD + BODY TURN: the combat lock stays on the player (the
                // engine needs it for the attack stage) while the MoveDir
                // writer rotates the body anyway -- the exact "attack + API
                // body turn" path. `nolock` = target=-1 variant (attack
                // probably can't hold without the lock -- that IS the test).
                if (CombatAiState.HoldSpinNoLock)
                    EnsureTargetAgent(null);
                if (!_hsInit)
                {
                    _hsInit = true;
                    Vec2 cur = Agent.GetMovementDirection();
                    Vec2 base0 = Agent.Position.AsVec2 - player.Position.AsVec2;
                    _hsSpinAngle = cur.LengthSquared > 0.0001f && base0.LengthSquared > 0.0001f
                        ? MathF.Atan2(cur.y, cur.x) - MathF.Atan2(base0.y, base0.x) : 0f;
                }
                _hsSpinAngle += CombatAiState.HoldSpinRateDeg * dt * 0.0174533f;
                Vec2 to = Agent.Position.AsVec2 - player.Position.AsVec2;
                float baseA = to.LengthSquared > 0.0001f ? MathF.Atan2(to.y, to.x) : 0f;
                _ftHeading = new Vec2(MathF.Cos(baseA + _hsSpinAngle), MathF.Sin(baseA + _hsSpinAngle));
                if (_hsLogCount < 300 && _brainClock - _hsLastLogAt >= 1f)
                {
                    _hsLastLogAt = _brainClock;
                    _hsLogCount++;
                    Vec2 legs = Agent.GetMovementDirection();
                    CombatAiLog.Log($"[HOLDTEST] Agent #{Agent.Index}: spin " +
                        $"stage={Agent.GetCurrentActionStage(1)} prog={Agent.GetCurrentActionProgress(1):F2} " +
                        $"head={(legs.LengthSquared > 0.0001f ? (MathF.Atan2(legs.y, legs.x) * 57.2958f).ToString("F0") : "na")} " +
                        $"tgt={(CombatLive(Agent.GetTargetAgent()) ? Agent.GetTargetAgent().Index : -1)}");
                }
            }
            else if (CombatAiState.LockThrashMode)
            {
                // Lock/release cycling while holding: reproduces the ai36
                // release->relock-in-100ms signature on demand.
                if (_thrPhaseAt < -50f) { _thrPhaseAt = _brainClock; _thrLocked = false; }
                float el = _brainClock - _thrPhaseAt;
                if (_thrLocked && el >= CombatAiState.ThrashOnSeconds)
                { _thrLocked = false; _thrPhaseAt = _brainClock; HoldStageLog("release"); }
                else if (!_thrLocked && el >= CombatAiState.ThrashOffSeconds)
                { _thrLocked = true; _thrPhaseAt = _brainClock; HoldStageLog("lock"); }
                // Coalesced writes (ai36 thrash caused log/memory-poke storm):
                // only touch the slot on a real state change.
                Agent curT = Agent.GetTargetAgent();
                if (_thrLocked)
                {
                    if (curT != player)
                    {
                        NativeTargetGuard.UnmarkReleased(Agent);
                        Agent.SetTargetAgent(player);
                    }
                }
                else if (curT != null)
                    NativeTargetGuard.ReleaseCombatTarget(Agent);
                // Release-window facing: toward the player (same as the bug case).
                Vec2 to = player.Position.AsVec2 - Agent.Position.AsVec2;
                if (to.LengthSquared > 0.0001f)
                    _ftHeading = FlatUnit(to);
            }
        }

        private void HoldStageLog(string evt)
        {
            if (_thrLogCount >= 200) return;
            _thrLogCount++;
            CombatAiLog.Log($"[HOLDTEST] Agent #{Agent.Index}: {evt} " +
                $"stage={Agent.GetCurrentActionStage(1)} prog={Agent.GetCurrentActionProgress(1):F2}");
        }
        private const int MaxFreeTgtLogs = 400;
        private int _bmLogCount;
        private float _bmLastLogAt = -99f;
        // Session 60 tactical planner state (spar defender).
        private TacticalMovement.TacticalState _tac;
        private TacticalMovement.Plan _tacPlan;
        private readonly List<(int key, Vec2 pos, bool preferred)> _tacEnemies = new List<(int, Vec2, bool)>(4);
        private static int _tacLogCount;
        private float _tacLastLogAt = -99f;
        private const int MaxTacLogs = 3000;
        private float _tsDistS, _tsFar, _tsClose, _tsDistN;
        private int _tmLogCount;
        private float _tmLastLogAt = -99f;
        private const int MaxTmLogs = 4000;
        // [TORSOSUM] accumulators (Session 58): 10s summaries per defender.
        private int _tsN, _tsBoth, _tsAng130, _tsBlockN, _tsFf;
        private float _tsSpd, _tsLastSumAt = -99f, _tsLastBlocked, _tsLastClean;
        private Vec3 _sparPinDir;               // fixed leg-facing; position anchor = current pos per frame
        private Vec2 _bmHeading = Vec2.Zero;    // Session 61b: duel blockmove heading, slewed toward the enemy (a live bearing makes the legs chase it in place)
        private Vec2 _tmInputVec = Vec2.Zero;   // Session 61c: blockmove=input vector mirrored into the OnAIInputSet ref channel

        // ---- FaceFrame experiment state (combatai.faceframe) ----
        // Single-designated-agent test: does a formation frame's DIRECTION
        // beat a held combat lock's facing? Reuses the formation-frame
        // channel (_tmFormationSet + TorsoMoveCleanup teardown), NOT the
        // scripted frame -- FrameInUseByActiveMode/the 5998 cleanup only
        // guard _targetFrameSet, same as torsomove/duelblockmove.
        private float _ffSpinAngle;             // spin mode: sweeping offset (deg, ~45/s)
        private float _ffLastLogAt = -99f;
        private bool _ffReleased;               // lock none: guarded release issued this session
        private float _ffRelockCheckAt = -99f;  // lock none: 0.5s re-lock sweep clock
        private float _ffMoveAngle;             // move=circle: rotating input angle
        private Vec2 _ffMoveVec;                // last commanded facing-relative input vec
        private bool _ffLookRtLogged;           // lookangle: one-time set->get round-trip logged
        private static int _ffLogCount;         // bounded [FACEFRAME] budget (single-agent mode)
        private const int MaxFfLogs = 120;
        private float _ffPrevLegsDeg = -999f;   // last log step's legs bearing -> dLegs drift delta
        private Agent.ActionCodeType _ffLastAct0 = Agent.ActionCodeType.Other;
        private Agent.ActionStage _ffLastStage0 = Agent.ActionStage.None;
        private bool _ffActSeen;                // _ffLast* initialized (first frame isn't a "change")
        private int _ffActLogCount;             // bounded [FACEFRAME] act= change budget
        private const int MaxFfActLogs = 40;

        // ---- Step dodge state ----
        private float _dodgeTime;               // >0 while the step is physically moving us
        // A rule-3 plain block hold also runs on _dodgeTime but moves nothing --
        // it must NOT claim the legs (it zeroed the input vector at every
        // release-onset block while pre-raised blocks kept walking).
        private bool DodgeOwnsBody => _dodgeTime > 0f && !_dodgeBlockOnly;
        private float _dodgeCooldown;           // anti-spam between dodges
        private bool _dodgeSpeedRaised;         // SetMaximumSpeedLimit was raised for the step -- must restore
        private bool _driveSpeedScaled;         // run-pace scale applied while we drive -- must restore
        private const float DriveSpeedScale = 2.0f;  // walk 1.7 m/s -> ~3.4 m/s combat run (matches logged run limit)
        private float _speedLogTimer;           // combatai.speedlog cadence gate
        private bool _dodgeIsAnim;              // current dodge is the MasterStrikes clip (not the locomotion step)
        private bool _dodgeOwnedByUs;           // channel-0 "dodge" clip was started BY US -- not foreign (Session 31)
        private Vec2 _dodgeDir;                 // world-space step direction (normalized)
        private bool _dodgeIsBackStep;           // this dodge resolved to a backward step -- combine a block with it
        private bool _dodgeBlockOnly;            // rule 3: non-dodgeable attack -- plain mirrored block, no step/clip
        private Agent.MovementControlFlag _dodgeDefendFlag; // mirrored block to combine on a back step only
        private bool _dodgedThisChain;          // at most one dodge per incoming swing chain
        private readonly HashSet<int> _evadeDodged = new HashSet<int>(); // evade mode: per-swing dedup

        // Mask for stripping all attack bits (spar attacker cone gate).
        private const Agent.MovementControlFlag AttackBits =
            Agent.MovementControlFlag.AttackLeft | Agent.MovementControlFlag.AttackRight
            | Agent.MovementControlFlag.AttackUp | Agent.MovementControlFlag.AttackDown;

        // The flag our per-frame drive currently wants. OnAIInputSet mirrors this
        // same field so the sparse (~1Hz) native callback can't stomp a different
        // value over what the per-frame write is holding.
        private Agent.MovementControlFlag _desiredMovementFlag = Agent.MovementControlFlag.None;
        private Agent.EventControlFlag _lastNativeEvent;            // eventFlag native passed into the last callback
        private Agent.MovementControlFlag _lastNativeMove;          // native's move bits at the last callback (MoveMask)
        private bool _nativeLocoWindow;                             // native owns locomotion+gait this frame (Session 48)
        private long _lastSeenTicks = -1;                           // TotalTicks seen by the previous DrivePerFrameCore
        private bool _drivingMovementFlags;

        // Counts how many ticks native tried to set a non-None flag before we nulled it,
        // i.e. how many times native actually wanted to act. Read by the periodic
        // summary logger in CombatAiMissionBehavior.
        public long InterceptedEventFlagTicks;
        public long InterceptedMovementFlagTicks;
        public long TotalTicks;

        public InjectPhase Phase { get; private set; } = InjectPhase.Idle;

        private float _phaseTimer;
        private bool _attackStageObserved;

        // PerfectBlockMode outcome tracking.
        private bool _watchingBlockOutcome;
        private float _blockOutcomeTimer;
        private Agent.UsageDirection _pendingIncomingDirection;

        // Resets the static detailed-log/heartbeat caps (combatai.clearlog calls this)
        // so a fresh test session gets the full quota of [PERFECTBLOCK]/[HEARTBEAT]
        // lines again instead of inheriting an already-exhausted budget.
        public static void ResetDiagnosticLogCounters()
        {
            _detailedBlockLogCount = 0;
            _heartbeatLogCount = 0;
            _duelLogCount = 0;
            _sideAttackLogCount = 0;
            _dodgeLogCount = 0;
            _defLogCount = 0;
            _decLogCount = 0;
            _adLogCount = 0;
            _initLogCount = 0;
            PlayerCombatModel.ResetLogCount();
            // Session 31 dodge/MS counters (the MasterStrikes* bools are
            // session facts and deliberately NOT reset here).
            CombatAiState.DodgeReleaseSeenCount = 0;
            CombatAiState.SpeedLogLines = 0;   // Session 59 per-mission [SPEEDLOG] budget
            _tacLogCount = 0;                  // Session 60 [TACTICAL] budget
            _rigMoveLogCount = 0;              // Session 62 rig budgets
            _rigWallLogCount = 0;
            _rigDefLogCount = 0;
            _rigPrimaryLogCount = 0;
            _rigStuckLogCount = 0;
            _rigEscLogCount = 0;
            _glockLogCount = 0;                // Session 64/65 group budgets
            _gswingLogCount = 0;
            _gfreeLogCount = 0;
            _gadvLogCount = 0;                 // Session 66: [GADV] + [GBLOCK]
            _gblockLogCount = 0;
            _ffLogCount = 0;                   // FaceFrame experiment [FACEFRAME] budget
            CombatAiMissionBehavior.ResetGroupLogBudget();
            AgentClearance.ResetLogBudgets();
            CombatAiState.DodgeOpportunityCount = 0;
            CombatAiState.OurDodgeStartCount = 0;
            CombatAiState.ForeignDodgeYieldCount = 0;
            CombatAiState.DodgeSuppressedMidSwing = 0;
        }

        // Public accessor for the mission behavior (AgentComponent.Agent is protected).
        public Agent TrackedAgent => Agent;

        public CombatAiOverrideComponent(Agent agent)
            : base(agent)
        {
        }

        // Called by CombatAiMissionBehavior when the inject hotkey is pressed.
        public void TriggerInjectSequence()
        {
            Phase = InjectPhase.ForceAttackRight;
            _phaseTimer = 0f;
            _attackStageObserved = false;
            CombatAiLog.Log($"[INJECT] Agent #{Agent.Index} ({Agent.Name}): sequence started -> ForceAttackRight.");
        }

        [HandleProcessCorruptedStateExceptions]
        public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag, ref Vec2 inputVector)
        {
            try
            {
                OnAIInputSetCore(ref eventFlag, ref movementFlag, ref inputVector);
            }
            catch (AccessViolationException ex)
            {
                LogAv("OnAIInputSet", ex);
            }
        }

        private void OnAIInputSetCore(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag, ref Vec2 inputVector)
        {
            _lastOp = "OnAIInputSet";
            TotalTicks++;

            if (eventFlag != Agent.EventControlFlag.None)
                InterceptedEventFlagTicks++;
            if (movementFlag != Agent.MovementControlFlag.None)
                InterceptedMovementFlagTicks++;

            // One-shot hotkey-triggered sequence takes top priority over the continuous
            // console-toggled test modes below.
            switch (Phase)
            {
                case InjectPhase.ForceAttackRight:
                    eventFlag = Agent.EventControlFlag.None;
                    movementFlag = Agent.MovementControlFlag.AttackRight;
                    inputVector = Vec2.Zero;
                    return;
                case InjectPhase.ForceDefendRight:
                    eventFlag = Agent.EventControlFlag.None;
                    movementFlag = Agent.MovementControlFlag.DefendRight;
                    inputVector = Vec2.Zero;
                    return;
            }

            // DuelMode/SparMode/GroupFightMode: mirror the per-frame brain's
            // current desired flag (the sparse callback can't stomp a different
            // value over it). Gated on _drivingMovementFlags so agents this
            // frame DOESN'T drive (mounted/ranged in group fight, out-of-range
            // in duel) aren't fed a stale None -- they fall through to the
            // normal override path instead.
            if ((CombatAiState.DuelMode || CombatAiState.SparMode || CombatAiState.GroupFightMode
                 || CombatAiState.EvadeMode || CombatAiState.AngleDodgeMode || CombatAiState.FaceFrameMode)
                && _drivingMovementFlags)
            {
                // Capture native's own outputs before overwriting (diagnostics
                // + the native-locomotion window below).
                _lastNativeEvent = eventFlag;
                _lastNativeMove = movementFlag & Agent.MovementControlFlag.MoveMask;
                // 4x translator: snapshot native's locomotion intent in
                // WORLD space so the group layer can re-express it in the
                // steered body frame. inputVector is LOCAL with the rig
                // writer's convention (x = right = (f.y,-f.x), y = forward
                // of the body heading); flags-only deliveries synthesize a
                // unit wish from the Forward/Backward/Strafe bits.
                if (_steerGroup && CombatAiState.GroupNativeTarget)
                {
                    Vec2 bf = Agent.GetMovementDirection();
                    if (bf.LengthSquared < 0.0001f) bf = Agent.Frame.rotation.f.AsVec2;
                    float bfl = bf.Length;
                    if (bfl > 0.0001f)
                    {
                        bf = new Vec2(bf.x / bfl, bf.y / bfl);
                        Vec2 wish;
                        if (inputVector.LengthSquared > 0.0001f)
                            wish = new Vec2(
                                inputVector.x * bf.y + inputVector.y * bf.x,
                                -inputVector.x * bf.x + inputVector.y * bf.y);
                        else
                        {
                            wish = Vec2.Zero;
                            if ((movementFlag & Agent.MovementControlFlag.Forward) != 0)
                                wish = new Vec2(wish.x + bf.x, wish.y + bf.y);
                            if ((movementFlag & Agent.MovementControlFlag.Backward) != 0)
                                wish = new Vec2(wish.x - bf.x, wish.y - bf.y);
                            if ((movementFlag & Agent.MovementControlFlag.StrafeRight) != 0)
                                wish = new Vec2(wish.x + bf.y, wish.y - bf.x);
                            if ((movementFlag & Agent.MovementControlFlag.StrafeLeft) != 0)
                                wish = new Vec2(wish.x - bf.y, wish.y + bf.x);
                        }
                        _nativeWorldMove = wish.LengthSquared > 0.0001f ? FlatUnit(wish) : Vec2.Zero;
                        _nativeWorldMoveAt = _brainClock;
                    }
                }
                if (_nativeLocoWindow)
                {
                    // Native locomotion window: native keeps its gait/move/
                    // input exactly as computed; we keep only our combat bits
                    // (Attack*/Defend*), so bare-defend returns no longer
                    // freeze the legs.
                    movementFlag = (_desiredMovementFlag & ~Agent.MovementControlFlag.MoveMask) | _lastNativeMove;
                    return;
                }
                // GaitFix: this callback's eventFlag=None was stomping the
                // per-frame EventControlFlags|=Run -- write Run through the
                // SAME channel the engine actually applies.
                eventFlag = (CombatAiState.GaitFix && Agent.WalkMode)
                    ? Agent.EventControlFlag.Run
                    : Agent.EventControlFlag.None;
                movementFlag = _desiredMovementFlag;
                // Mid-step: pass the dodge direction through to the native
                // input channel too so this ~1Hz callback can't cancel the
                // locomotion the per-frame writes are steering. Animated
                // dodges are root-motion driven -- zero input instead so
                // nothing fights the clip.
                inputVector = DodgeOwnsBody && !_dodgeIsAnim ? _dodgeDir
                    : _tmInputVec.LengthSquared > 0.0001f ? _tmInputVec : Vec2.Zero;
                return;
            }

            // Handoff (Session 35c): a driven mode is on but the brain chose
            // NOT to drive this frame (duel leash, non-melee group agent,
            // etc.) -- leave the refs exactly as NATIVE computed them so the
            // base game owns the body. Without this the nullifier below
            // froze agents between drive ticks (and mounted/ranged agents
            // in group mode were frozen instead of "native control").
            if ((CombatAiState.DuelMode || CombatAiState.SparMode || CombatAiState.GroupFightMode
                || CombatAiState.FaceFrameMode)
                && !_drivingMovementFlags)
            {
                // Session 46f: beyond the duel leash native owns the body, but
                // its Attack flags still leak through this pass-through -- a
                // native swing queued before our FSM takes over reads as an
                // instant attack. Strip them in hand-off only. Same for Defend
                // bits: a native guard raised out of range picks the line by
                // itself (often the wrong one) and survives the take-over as a
                // held pose -- our defense then inherits a wrong-line block
                // that eats the first player swing.
                if (_handOffToNative)
                    movementFlag &= ~(Agent.MovementControlFlag.AttackMask
                        | Agent.MovementControlFlag.DefendMask
                        | Agent.MovementControlFlag.DefendBlock);
                return;
            }

            // PerfectBlockMode: react only, never initiate anything else. Forces the
            // mirrored Defend flag if (and only if) the player is late in a swing;
            // otherwise nulls everything (no wandering/attacking) so this is a clean,
            // isolated reactive-block test.
            if (CombatAiState.PerfectBlockMode)
            {
                eventFlag = Agent.EventControlFlag.None;
                movementFlag = ComputeReactiveDefendFlag();
                inputVector = Vec2.Zero;
                return;
            }

            // ContinuousAttackMode: mirror whatever the per-frame attack-loop state
            // machine currently wants (Holding -> AttackRight, Releasing -> None).
            // Must mirror, not hardcode: if this ~1Hz callback wrote AttackRight
            // while the state machine was in its release gap, it would re-ready the
            // swing and the agent would just hold the attack forever (the exact bug
            // the first version of this mode had).
            if (CombatAiState.ContinuousAttackMode)
            {
                eventFlag = Agent.EventControlFlag.None;
                movementFlag = _desiredMovementFlag;
                inputVector = Vec2.Zero;
                return;
            }

            if (!CombatAiState.OverrideEnabled)
            {
                CavReleaseControl();   // no-op unless a speed cap/input is still held
                return;
            }

            // Default (idle) override: if a test mode is active but didn't
            // drive this frame, fall through to the freeze below. If NO test
            // mode is active at all, leave native in control -- combatai.enable
            // should mean "the mod is ready to take over when a mode is
            // toggled", not "freeze every agent until you pick a mode".
            if (!CombatAiState.DuelMode && !CombatAiState.SparMode && !CombatAiState.GroupFightMode
                && !CombatAiState.EvadeMode && !CombatAiState.AngleDodgeMode && !CombatAiState.PerfectBlockMode
                && !CombatAiState.ContinuousAttackMode && !CombatAiState.LookTestMode && !CombatAiState.FaceFrameMode)
            {
                return;
            }

            // A mode is active but didn't drive this frame (e.g., out of range
            // handoff) -- void native input so we don't fight our own leash.
            eventFlag = Agent.EventControlFlag.None;
            movementFlag = Agent.MovementControlFlag.None;
            inputVector = Vec2.Zero;
        }

        // =====================================================================
        // DUEL MODE -- the v1 combat brain (research doc section 10).
        // Runs per-frame from DrivePerFrame. Structure:
        //   Approach -> Windup (varied hold + optional feint + punish early block)
        //   -> Release -> Recover -> Approach
        // plus an always-on reactive-defense check that rolls once per player
        // swing: harder to block while we're mid-move (the counter window).
        // =====================================================================
        private Agent.MovementControlFlag ComputeDuelFlag(float dt)
        {
            _lastOp = "duel:enter";
            // Defense-only test mode: stay in approach, use the full defensive
            // brain, never progress into windup/release/recovery.
            if (CombatAiState.DefenseOnly)
                _duelState = DuelState.Approach;
            if (_reactionLatency < 0f)
                _reactionLatency = 0.16f + (Agent.Index % 9) * 0.028f; // 0.16-0.38s, deterministic per agent
            // Target: a mode-pinned focus (spar/group assignment) beats the
            // default nearest-enemy pick; the player is the last fallback.
            Agent target = _forcedDuelTarget ?? FindNearestEnemy() ?? Agent.Mission?.MainAgent;

            // Spar attacker: lock onto the designated defender so the two AIs
            // fight each other specifically. Re-asserted EVERY frame because
            // native re-targets to the closest/recent threat otherwise (observed:
            // attacker switched to the player).
            bool isSparAttacker = CombatAiState.SparMode && Agent.Index == CombatAiState.SparAttackerIndex;
            if (isSparAttacker)
            {
                Agent defender = GetAgentByIndex(CombatAiState.SparDefenderIndex);
                if (CombatLive(defender))
                {
                    target = defender;
                    EnsureTargetAgent(defender);
                }
            }
            _duelTarget = target; // helpers (PickAttackDir etc.) read this

            if (!CombatLive(target))
                return Agent.MovementControlFlag.None;

            // CRASH HARDENING (final): the combat target is NEVER null in duel
            // mode and native auto-target-selection is NEVER touched. Every
            // recorded crash landed during a target-null window (free-stance
            // idle, feint redirect) while the player was mid-swing -- an AI
            // agent in active melee with target=null is a state the engine
            // never produces on its own, and something in its worker-thread
            // code dereferences it (0xC0000005 with zero [AV] lines = engine-
            // internal, not our managed calls). Duel writes are now ONLY
            // MovementFlags + a permanent live SetTargetAgent lock + the
            // scripted pin below -- the frame-under-lock pattern is the same
            // one the spar defender runs for whole sessions without fault.
            // BODY FEINT arming (Session 70): entering the feint states rolls
            // once per chain element (1.5s cooldown per agent); the lock is
            // then released through the normal policy while the body twists.
            // End line logs the relock the FSM guarantees before AttackRelease.
            bool inFeint = FeintStatesActive;
            if (inFeint && !_bfPrevInFeint)
            {
                _bfArmed = false;
                if (CombatAiState.FreeBodyFeint && _freeTargetActive
                    && CombatAiState.FreeFace == CombatAiState.FreeFaceT.MoveDir
                    && CombatLive(target) && !DodgeOwnsBody && !_handOffToNative
                    && _brainClock - _bfLastAt >= 1.5f)
                {
                    _bfArmed = true;
                    _bfLastAt = _brainClock;
                    _bfSide = DuelRng.NextDouble() < 0.5 ? -1 : 1;
                    _bfElapsed = 0f;
                    _bfOffsetDeg = 0f;
                    _bfPeakErr = 0f;
                    if (_bfLogCount < MaxBfLogs)
                    {
                        _bfLogCount++;
                        CombatAiLog.Log($"[BODYFEINT] Agent #{Agent.Index}: start side={_bfSide:+1;-1} " +
                            $"offset=55 state={_duelState} lock=released");
                    }
                }
            }
            else if (!inFeint && _bfPrevInFeint)
            {
                if (_bfArmed)
                {
                    Agent cur = Agent.GetTargetAgent();
                    if (_bfLogCount < MaxBfLogs)
                    {
                        _bfLogCount++;
                        CombatAiLog.Log($"[BODYFEINT] Agent #{Agent.Index}: end state={_duelState} " +
                            $"peakErr={_bfPeakErr:F0} relocked={(Live(cur) ? "#" + cur.Index : "MISSING")} " +
                            $"nativeTgtAtRelease={(Live(cur) ? "#" + cur.Index : "-1")}");
                    }
                }
                _bfArmed = false;
                _bfOffsetDeg = 0f;
            }
            _bfPrevInFeint = inFeint;
            EnsureTargetAgent(target);
            // KICK TRACKING (Session 34): kicks/bashes never touch the
            // channel-1 combat stages -- they ride channel 0 as
            // ActionCodeType Kick..WeaponBash (MasterStrikes' detection
            // recipe) or a modded clip whose name says kick/bash. Rising
            // edge = one evade roll per kick; the latch updates BEFORE
            // the dodge early-return so a kick seen mid-step can't
            // retrigger when we come out of it.
            Agent.ActionCodeType tAct0 = target.GetCurrentActionType(0);
            bool kickNow = tAct0 >= Agent.ActionCodeType.Kick && tAct0 <= Agent.ActionCodeType.WeaponBash;
            if (!kickNow)
            {
                // Bash (block+kick) may surface on channel 1 or under a
                // modded clip name -- check both channels, enum AND name.
                Agent.ActionCodeType tAct1 = target.GetCurrentActionType(1);
                kickNow = tAct1 >= Agent.ActionCodeType.Kick && tAct1 <= Agent.ActionCodeType.WeaponBash;
                if (!kickNow)
                {
                    string a0name = target.GetCurrentAction(0).GetName();
                    string a1name = target.GetCurrentAction(1).GetName();
                    kickNow = (!string.IsNullOrEmpty(a0name) && (a0name.Contains("kick") || a0name.Contains("bash")))
                           || (!string.IsNullOrEmpty(a1name) && (a1name.Contains("kick") || a1name.Contains("bash")));
                }
            }
            bool kickEdge = kickNow && !_kickPrevActive;
            _kickPrevActive = kickNow;
            if (kickEdge && _dodgeLogCount < MaxDodgeLogs)
            {
                // Fires even when the evade gates reject it -- tells us
                // whether a missed dodge was detection or gating.
                _dodgeLogCount++;
                CombatAiLog.Log($"[DODGE] Agent #{Agent.Index} ({Agent.Name}): kick/bash edge on #{target.Index} act0={tAct0} dist={HorizontalDistanceTo(target):F2}.");
            }
            // Step dodge in progress: locomotion owns the body -- skip the FSM,
            // sway frames, everything, until the step finishes. The combat
            // lock stays (torso keeps tracking the threat mid-step).
            if (_dodgeTime > 0f)
                return TickDodgeStep(dt);
            // A MasterStrike/Clinch we triggered is playing on channel 0 --
            // never fight it with a forced flag (no log: it would spam).
            if (IsInMasterStrikeOrClinch())
                return Agent.MovementControlFlag.None;
            // MasterStrikes (or any other mod) is mid-dodge on channel 0 and we
            // didn't start it -- don't fight it with a forced Defend/Attack
            // flag this tick (see IsForeignDodgePlaying). Torso lock above
            // already keeps the combat target live.
            if (IsForeignDodgePlaying())
            {
                CombatAiState.ForeignDodgeYieldCount++;
                if (_duelLogCount < MaxDuelLogs)
                {
                    _duelLogCount++;
                    CombatAiLog.Log($"[DUEL] Agent #{Agent.Index} ({Agent.Name}): foreign dodge (channel 0) in progress -- yielding this tick instead of forcing Defend/Attack.");
                }
                return Agent.MovementControlFlag.None;
            }
            // Session 34 final: NO scripted pins or MovementInputVector
            // pressure in duel at all -- the dual-channel pressure caused
            // the circling/glitching/lost-tracking the user reported. All
            // movement now rides plain MovementControlFlag bits, the same
            // contract the base game uses. Any frame still installed is
            // stale (looktest leftovers) -> release once. The spar
            // DEFENDER's own leg pin (_legPinOwned) is still untouched.
            if (!_legPinOwned && _targetFrameSet)
                ReleaseFeintBodyTurn(target);

            // Body feint CLOSED: torso-only LookDirectionAsAngle writes under a
            // held lock were stomped by the lock too (user-tested, invisible) --
            // no channel rotates the body while the combat target is set, and
            // releasing it was the crash vector. Flag-level feints only.

            _duelTimer += dt;
            _brainClock += dt;
            if (_vulnerableTimer > 0f)
                _vulnerableTimer -= dt;
            if (_defendTargetHold > 0f)
                _defendTargetHold -= dt;
            if (_dodgeCooldown > 0f)
                _dodgeCooldown -= dt;

            Agent.ActionStage pStage = target.GetCurrentActionStage(1);
            // group fight: an enemy swinging/feinting at SOMEONE ELSE is not our business
            if (GroupUpDownOnly && !GroupAimedAtMe(target))
                pStage = Agent.ActionStage.None;
            bool pInAttack = pStage == Agent.ActionStage.AttackReady
                || pStage == Agent.ActionStage.AttackQuickReady
                || pStage == Agent.ActionStage.AttackRelease;
            bool pPrevInAttack = _prevPlayerStage == Agent.ActionStage.AttackReady
                || _prevPlayerStage == Agent.ActionStage.AttackQuickReady
                || _prevPlayerStage == Agent.ActionStage.AttackRelease;
            bool swingEdge = pInAttack && !pPrevInAttack;
            bool releaseEdge = pStage == Agent.ActionStage.AttackRelease
                && _prevPlayerStage != Agent.ActionStage.AttackRelease;
            // GROUP feint-fall proxy (Session 66 user spec): vs an AI target a
            // ready pose that drops to ANY non-attack stage without passing
            // through Release is a feint cancel (block-tap or a plain stage
            // drop) -- record it so THIS swing's release roll carries the -p
            // penalty (see NoteGroupCancelEdge / GroupReleaseBlockRoll).
            if (CombatAiState.GroupFightMode && !target.IsMainAgent
                && (_prevPlayerStage == Agent.ActionStage.AttackReady
                    || _prevPlayerStage == Agent.ActionStage.AttackQuickReady)
                && pStage != Agent.ActionStage.AttackReady
                && pStage != Agent.ActionStage.AttackQuickReady
                && pStage != Agent.ActionStage.AttackRelease)
                NoteGroupCancelEdge(target);
            _prevPlayerStage = pStage;
            // Track how long the TARGET has been holding (not releasing) an
            // attack. Session 34: the count now SURVIVES their feint-cancel
            // gaps (ready -> block-tap -> re-ready is one hold, not fresh
            // resets) -- total chain ready-time is what the 1-1.5s grace
            // measures, so feinting can't farm a fresh grace every leg.
            // Only a real release or a dead chain clears it.
            if (pStage == Agent.ActionStage.AttackReady || pStage == Agent.ActionStage.AttackQuickReady)
                _playerReadyElapsed += dt;
            else if (pStage == Agent.ActionStage.AttackRelease)
            {
                _playerReadyElapsed = 0f;
                _pushRolled = false;
                _pushUntil = 0f;
            }

            // Chain tracking: a feint chain is ready -> cancel gap -> re-ready
            // -> release. Defense is decided ONCE per chain (re-rolling on every
            // cancel edge would make us immune to feints). A chain counts dead
            // after 0.45s with no attack stage at all.
            if (pInAttack) _noAttackElapsed = 0f; else _noAttackElapsed += dt;
            if (pStage == Agent.ActionStage.AttackRelease)
            {
                // Combo-spam fix (Session 31): a release can be re-entered
                // with no observable non-attack frame between swings (release
                // -> ready -> release inside one "chain"). A second release
                // entry while _sawRelease is still set is a genuinely NEW
                // committed swing -- restore dodge eligibility for it.
                if (releaseEdge && _sawRelease)
                    _dodgedThisChain = false;
                _sawRelease = true;
                _releaseElapsed += dt;
            }
            else
                _releaseElapsed = 0f;
            bool chainDead = _noAttackElapsed > 0.45f;
            if (chainDead)
            {
                _chainDecided = false;
                _sawRelease = false;
                _dodgedThisChain = false;
                _stallPunished = false;
                _frozeThisRelease = false;
                _misreadDefendFlag = Agent.MovementControlFlag.None;
                _playerReadyElapsed = 0f;   // chain over: grace clock resets too
                _pushRolled = false;
                _pushUntil = 0f;
                _defCommittedReset();
            }

            float flatDist = HorizontalDistanceTo(target);

            // Combo-spam reset (Session 31): a NEW windup after the previous
            // chain completed a real release is a genuinely new swing, not a
            // feint re-ready (feints never reach release, so _sawRelease stays
            // false there and the once-per-chain latches correctly persist
            // through cancels). Without this the latches made dodge AND
            // defense re-rolls impossible during attack spam -- observed as
            // "90% chance that doesn't look like 90%".
            if (swingEdge && _sawRelease)
            {
                _chainDecided = false;
                _sawRelease = false;
                _dodgedThisChain = false;
                _stallPunished = false;
                _defCommittedReset();
            }

            if (swingEdge && !_chainDecided)
            {
                _chainDecided = true;
                _sawRelease = false;
                _playerSwungSinceOurs = true;
                // Session 34: grace window per chain (~1-1.5s) -- the only
                // caution the player holding/winding up buys them.
                _playerReadyGrace = PlayerReadyGraceMin
                    + (float)DuelRng.NextDouble() * (PlayerReadyGraceMax - PlayerReadyGraceMin);
                {
                    // Session 48: vs the player the grace is the brain's (much longer).
                    float g = RollHoldGrace(target);
                    if (g > 0f)
                        _playerReadyGrace = g;
                }
                DecideDefenseVs(target);

                // Opportunist roll: if we're free and in weapon reach, maybe
                // quick-attack into their windup before it releases.
                // Session 45b: scaled by the live read -- an AI that can't
                // predict the player doesn't stab confidently into windups.
                // Session 47: vs the PLAYER the blind stab-into-the-windup roll is
                // gone -- catching a hold off guard is now S1's COUNTER outcome,
                // which only fires when the forecast said the player would hold.
                _opportunistAllowed = !(target.IsMainAgent && !_pureReactiveDefense && CombatAiState.BrainS1)
                    && _duelState == DuelState.Approach
                    && flatDist < MeleeReach()
                    && DuelRng.NextDouble() < OpportunistChance
                        * (target.IsMainAgent ? 0.4f + 0.8f * PlayerCombatModel.Expectation : 1f);
            }

            Agent.ActionStage ownStage = Agent.GetCurrentActionStage(1);
            bool ownInAttack = ownStage == Agent.ActionStage.AttackReady
                || ownStage == Agent.ActionStage.AttackQuickReady
                || ownStage == Agent.ActionStage.AttackRelease;

            // DEFENSE BRAIN (Session 47): S1 hold-reaction + S2 feint-read run
            // every frame vs the player; they raise early guards through the
            // existing committed-block machinery below, or start a counter.
            if (BrainVs(target) && !CombatAiState.EvadeMode
                && BrainTick(target, dt, pStage, ownStage, flatDist))
                return _attackDir;
            // Spar defender with the lock on the AI attacker: consume feint
            // events instead of evaluating them later -- "busy with the other
            // attacker" is the natural distraction, a stale feint is never
            // fallen for late.
            else if (SparBrainActive)
                _s2Feints = PlayerCombatModel.FeintEventSerial;

            // OWN-ACTION VULNERABILITY (Session 45, expectation spec): while
            // WE hold an attack/feint our read of the player is suppressed --
            // the player can counter-read our moves the same way we read
            // theirs. Reuses _vulnerableTimer's existing machinery (commit
            // suppressed in DecideDefenseVs, +0.25s latency in the race). If
            // the player lets the window close without swinging (hesitated /
            // missed the counter), _recoverBoost shortens the next think
            // delay -- we recover and act first.
            if (ownInAttack || _duelState == DuelState.FeintCancel)
            {
                _vulnerableTimer = MathF.Max(_vulnerableTimer, OwnActionVulnerableSeconds * FightScript.WindowScale);
                _ownActionActive = true;
                if (pInAttack)
                    _playerSwungDuringOwn = true;
            }
            else if (_ownActionActive)
            {
                _ownActionActive = false;
                if (!_playerSwungDuringOwn)
                    _recoverBoost = RecoverBoostSeconds;
                _playerSwungDuringOwn = false;
            }
            if (_recoverBoost > 0f)
                _recoverBoost -= dt;

            // KICK EVADE (Session 34, 90%): a block does NOT stop a kick --
            // the answer is a hard LATERAL step (left/right, openness pick
            // coin-flipped so it can't be pre-tracked). Runs before the
            // melee dodge so an unblockable kick beats a blockable swing
            // edge for this tick. In range + aimed at us only; never while
            // our own swing is in flight. Session 44 (user spec): ENABLED
            // for the spar defender too -- a kick is unblockable, so evading
            // is its only real answer; direction is the deterministic outer
            // side via SparForcedDodgeSide (no coin flip in spar).
            if (kickEdge && CombatAiState.DodgeEnabled
                && ownStage != Agent.ActionStage.AttackRelease
                && flatDist <= KickEvadeRange && KickAimedAtUs(target)
                && DuelRng.NextDouble() < KickEvadeChance
                && TryStartKickDodge(target))
            {
                if (target.IsMainAgent)
                    PlayerCombatModel.MarkDefense(PlayerCombatModel.DefDodge);
                return DodgeMoveFlag() | _dodgeDefendFlag;
            }

            // STEP DODGE: a committed release in reach gets answered with a
            // hard step instead of a block some share of the time -- one roll
            // per swing chain. Outnumbered defenders dodge more: making space IS
            // their defense. Never mid-release of our own swing (can't step
            // and swing at once).
            // Session 46f/46h: latch the release read ONCE at the edge -- the
            // live read decays during flight (SecondsSinceReadySeen keeps
            // growing) and ~0.1s in every release would score as surprise.
            // For the player the rating fuses in the camera-aim term.
            if (releaseEdge)
            {
                if (BrainVs(target))
                {
                    float cam = CameraTerm(target);
                    _releaseRead = MathF.Max(0f, MathF.Min(1f,
                        (1f - CameraRatingWeight) * EffectiveReleaseRead() + CameraRatingWeight * cam));
                    _releaseCam = cam;
                }
                else
                    _releaseRead = EffectiveReleaseRead();
            }
            bool threatInReach = InThreatReach(target);
            if (releaseEdge && threatInReach)
            {
                CombatAiState.DodgeReleaseSeenCount++;
                // Mid-our-release is the dominant seen->roll suppressor
                // (can't step and swing at once) -- count it so the gap
                // reads as attack-overlap, not a mystery.
                if (ownStage == Agent.ActionStage.AttackRelease)
                    CombatAiState.DodgeSuppressedMidSwing++;
                // Session 46h (user design): NO fixed reaction window -- the
                // rating decides the CHANCE of defending at all. When the AI
                // defends it answers instantly at the release edge. A failed
                // roll latches _frozeThisRelease, which suppresses both the
                // dodge/block split and the 1b race for this release.
                if (BrainVs(target))
                {
                    bool defends;
                    if (CombatAiState.BrainS3)
                        defends = BrainReleaseBlock(target);   // Session 47: S3 on its own inputs + forecast modulation
                    else
                    {
                        float p = BlockChanceMin + (BlockChanceMax - BlockChanceMin)
                            * MathF.Max(0f, MathF.Min(1f, (_releaseRead - BlockRatingLo) / (BlockRatingHi - BlockRatingLo)));
                        defends = DuelRng.NextDouble() < p;
                        if (_defLogCount < MaxDefLogs)
                        {
                            _defLogCount++;
                            CombatAiLog.Log($"[DEF] Agent #{Agent.Index}: {(defends ? "DEFEND" : "FAILED")} " +
                                $"rating={_releaseRead:F2} (model={PlayerCombatModel.LastReleaseRead:F2} " +
                                $"pattern={PlayerCombatModel.LastPatternTerm:F2} cam={_releaseCam:F2}) p={p:F2} (legacy)");
                        }
                    }
                    _frozeThisRelease = !defends;
                    if (_frozeThisRelease)
                        PlayerCombatModel.MarkDefense(PlayerCombatModel.DefFroze);
                }
                // Session 52 spar brain: the AI ATTACKER's releases get a
                // probabilistic block too (cluster / distraction / script /
                // scripted miss gifts all apply) instead of always blocking.
                else if (SparBrainActive)
                {
                    bool defends = SparAttackerBlockRoll(target);
                    _frozeThisRelease = !defends;
                }
                // Session 66 (user spec): group-fight AI-vs-AI defenders roll
                // their block too -- the unconditional-defend else below is
                // what produced "the AI blocked EVERYTHING" in the 5v5.
                else if (CombatAiState.GroupFightMode && !target.IsMainAgent
                    && Agent.IsEnemyOf(target))
                {
                    bool defends = GroupReleaseBlockRoll(target, false);
                    _frozeThisRelease = !defends;
                    // Visible miss: latch the wrong-line guard this release
                    // will hold (emitted below, after the reaction race).
                    // Also covers a _defendAllowed=false chain -- that chain-
                    // level "never defends" roll produces the same statue the
                    // failed release roll did, so it gets the same misread.
                    _misreadDefendFlag = defends && _defendAllowed
                        ? Agent.MovementControlFlag.None
                        : GroupMisreadLine(BlockFlagVs(target, target.GetCurrentActionDirection(1)));
                }
                else
                {
                    _frozeThisRelease = false;
                    _misreadDefendFlag = Agent.MovementControlFlag.None;
                }
            }
            else if (pStage != Agent.ActionStage.AttackRelease)
            {
                _frozeThisRelease = false; // release over -> next swing re-rolls
                _misreadDefendFlag = Agent.MovementControlFlag.None;
            }
            // STEP DODGE trigger: release-edge only -- the same trigger for
            // every attack direction, the path that tested well for UP/DOWN.
            // (The earlier ready-hold "stabCommit" early trigger is removed:
            // dodging during the player's ready pose let them track and
            // re-aim the thrust into wherever the dodge landed -- every held
            // stab landed and it ate the chain's dodge latch besides.)
            // Duel defense split (Session 33, user spec): a committed release
            // in reach gets the evade-proven step dodge 40% of the time and a
            // plain mirrored block hold 60% of the time -- one roll per chain.
            // Session 37/38: re-enabled for the spar defender too (2-attacker
            // dodge), PLAYER-ONLY (user spec: AI tracking makes dodging the
            // AI attacker largely pointless -- keep it simple, always block
            // the AI attacker's releases, only ever dodge the player's, and
            // PickDodgeDirection forces the OUTER side (away from the AI
            // attacker) whenever it does. Kick-evade stays duel-only.
            // Session 46h: the instant answer is back (defense answers AT the
            // release edge) but an armed/fired early-commit block
            // (_defCommitState == 2) is still never overwritten by the split's
            // correct-line block -- that guard is what lets a feint bite a
            // committed block.
            bool commitHeld = BrainVs(target);
            if (releaseEdge && !_dodgedThisChain && ownStage != Agent.ActionStage.AttackRelease
                && CombatAiState.DodgeEnabled && threatInReach && !_frozeThisRelease
                && (!CombatAiState.SparMode || !_pureReactiveDefense || target.IsMainAgent)
                && !(commitHeld && _defCommitState == 2)
                && !_spamCounterArmed)   // Session 50: an armed spam counter needs the BLOCK to land -- never dodge it
            {
                _dodgedThisChain = true;
                CombatAiState.DodgeOpportunityCount++;
                float dodgeChance = DuelDodgeChance;
                if (DuelRng.NextDouble() < dodgeChance)
                {
                    if (TryStartDodge(target))
                    {
                        if (target.IsMainAgent)
                            PlayerCombatModel.MarkDefense(PlayerCombatModel.DefDodge);
                        // Session 48: a dodged (or rule-3 blocked) swing leaves the
                        // player committed and recovering -- that is OUR opening.
                        // Without this flag the riposte path only existed for the
                        // reactive-race blocks, so the 60% plain-block / 40% dodge
                        // split never counter-attacked (the "too passive" report).
                        _blockedTheirSwing = true;
                        return DodgeMoveFlag() | _dodgeDefendFlag;
                    }
                }
                else
                {
                    _blockedTheirSwing = true;   // plain block -> riposte window opens when the swing resolves
                    StartBlockOnly(target);
                    if (target.IsMainAgent)
                        PlayerCombatModel.MarkDefense(PlayerCombatModel.DefReactBlock);
                    if (_dodgeLogCount < MaxDodgeLogs)
                    {
                        _dodgeLogCount++;
                        CombatAiLog.Log($"[DODGE] Agent #{Agent.Index} ({Agent.Name}): vs #{target.Index} ({target.Name}) " +
                            $"atk={target.AttackDirection} act=block(60%) dir=(0,0) backstep=False block={_dodgeDefendFlag}.");
                    }
                    return DodgeMoveFlag() | _dodgeDefendFlag;
                }
            }

            // STALL PUNISH (Session 45, expectation spec): the player spam-
            // feinted or held the move far past its predicted time -- the
            // model says they're maximally readable, so instead of waiting
            // for the release we hit them mid-pose. Once per chain.
            if (target.IsMainAgent && (!_pureReactiveDefense || SparDuelGate) && PlayerCombatModel.ChainOverheld && !_stallPunished
                && _duelState == DuelState.Approach && flatDist < MeleeReach()
                && ownStage != Agent.ActionStage.AttackRelease && GroupCanStartAttack(target))
            {
                _stallPunished = true;
                if (DuelRng.NextDouble() < StallPunishChance)
                {
                    _attackReason = "stall"; StartDuelAttack(DuelAttackKind.Quick);
                    return _attackDir;
                }
            }

            // 1) DEFENSE -- committed read first, then the reaction race.
            //    The commit HOLDS through feint cancels and the release itself:
            //    if the player redirected onto another line, our block is
            //    already up on the wrong line -- that IS falling for a feint,
            //    produced by the model, not scripted.
            if (_defCommitState == 2)
            {
                if (ownStage == Agent.ActionStage.AttackRelease)
                { /* can't block mid-release; commit stays armed */ }
                else if (_sawRelease && !pInAttack)
                {
                    // Their committed swing resolved (hit or whiff) while our
                    // read-block was up -> chain over, counter window open.
                    _defCommittedReset();
                    // Session 46: the riposte chance scales with the release
                    // read -- a well-tracked swing gets punished, a surprising
                    // one leaves us cautious instead of instantly countering.
                    float riposteChance = target.IsMainAgent
                        ? RiposteChanceVsPlayer()
                        : 0.55f;
                    if (_blockedTheirSwing && flatDist < MeleeReach() && GroupCanStartAttack(target)
                        && DuelRng.NextDouble() < riposteChance)
                    {
                        _blockedTheirSwing = false;
                        _attackReason = "riposte-commit"; StartDuelAttack(PickPostBlockKind(target));
                        return _attackDir;
                    }
                    _blockedTheirSwing = false;
                }
                else if (chainDead)
                {
                    _defCommittedReset();
                }
                else
                {
                    _blockedTheirSwing |= _sawRelease;
                    // Session 47: a wrong-line guard that won its follow roll
                    // re-points to the real line after its sampled delay.
                    if (_repointFlag != Agent.MovementControlFlag.None && _brainClock >= _repointAt)
                    {
                        _defCommitFlag = _repointFlag;
                        _repointFlag = Agent.MovementControlFlag.None;
                    }
                    // Hold the committed line ONLY while they can actually
                    // reach us -- a read-block held against someone waving a
                    // sword from 5m away is just a turtle.
                    // Cav: release-only defense -- a brain-armed held guard
                    // is emitted only when the attack is actually coming
                    // (release edge or a live couch, which has no edge).
                    if (InThreatReach(target)
                        && (!_cavDriving || pStage == Agent.ActionStage.AttackRelease || target.IsDoingPassiveAttack))
                        return _defCommitFlag;
                }
            }
            else if (_defCommitState == 1 && !_cavDriving
                && ownStage != Agent.ActionStage.AttackRelease
                && InThreatReach(target))
            {
                bool pReady = pStage == Agent.ActionStage.AttackReady || pStage == Agent.ActionStage.AttackQuickReady;
                bool pRelease = pStage == Agent.ActionStage.AttackRelease;
                // Session 45: the commit fires only while the LIVE read is
                // still healthy -- a feint mid-ready drops Expectation and
                // defers the early block until the read rebuilds (that IS
                // "the rating changes while the move plays"). If the swing
                // releases while the read is still broken, the armed commit
                // is dropped so the normal reaction race below gets to run --
                // a surprise attack should be HARD to block, not impossible.
                bool liveReadOk = !target.IsMainAgent || PlayerCombatModel.Expectation >= ExpectCommitGate;
                if (liveReadOk && ((pReady && _playerReadyElapsed >= _defCommitAt) || pRelease))
                {
                    _defCommitState = 2;
                    _blockedTheirSwing = pRelease;
                    if (target.IsMainAgent)
                        PlayerCombatModel.MarkDefense(PlayerCombatModel.DefCommitBlock);
                    return _defCommitFlag;
                }
                if (pRelease && !liveReadOk)
                    _defCommittedReset();
            }

            // A swing that resolved while we were reactively blocking opens the
            // same riposte window a committed read does -- block -> counter is
            // the whole point of the defense, not a turtle.
            if (_blockedTheirSwing && _sawRelease && !pInAttack
                && ownStage != Agent.ActionStage.AttackRelease)
            {
                _blockedTheirSwing = false;
                _sawRelease = false;
                // Session 32 latch-leak fix: clearing _sawRelease here meant
                // the NEXT swing's releaseEdge arrived with _sawRelease=false
                // -> the `releaseEdge && _sawRelease` reset above couldn't
                // fire -> _dodgedThisChain stayed latched -> the dodge roll
                // was suppressed on every swing after a block+riposte.
                // Blocking literally consumed the next swing's dodge
                // ("AI only blocks and did not dodge"). A resolved swing
                // must not eat the next swing's dodge eligibility.
                _dodgedThisChain = false;
                if (_duelState == DuelState.Approach
                    && flatDist < MeleeReach()
                    && GroupCanStartAttack(target)
                    && DuelRng.NextDouble() < (target.IsMainAgent
                        ? RiposteChanceVsPlayer()
                        : 0.55f))
                {
                    _attackReason = "riposte-react"; StartDuelAttack(PickPostBlockKind(target));
                    return _attackDir;
                }
            }

            // 1b) The reaction race (uncommitted swings). Session 46h (user
            //     design): for the PLAYER there is no latency race at all --
            //     the release-edge block-chance roll already decided (the gate
            //     has !_frozeThisRelease), so the block answers instantly.
            //     Non-player targets keep the per-agent latency race.
            if (_defCommitState == 0 && _chainDecided && _defendAllowed
                && pStage == Agent.ActionStage.AttackRelease
                && ownStage != Agent.ActionStage.AttackRelease
                && InThreatReach(target)
                && !_frozeThisRelease)
            {
                float effLatency = _reactionLatency
                    + (_vulnerableTimer > 0f ? 0.25f : 0f);
                bool canReact = BrainVs(target)
                    || _releaseElapsed >= effLatency * 0.55f;
                if (canReact)
                {
                    Agent.MovementControlFlag def = BlockFlagVs(target, target.GetCurrentActionDirection(1));
                    if (def != Agent.MovementControlFlag.None)
                    {
                        EnsureTargetAgent(target);
                        _lastThreat = target;
                        _defendTargetHold = DefendTargetHoldSeconds;
                        _blockedTheirSwing = true;
                        if (target.IsMainAgent)
                            PlayerCombatModel.MarkDefense(PlayerCombatModel.DefReactBlock);
                        if (_duelState == DuelState.Windup)
                            EnterApproach(false);
                        return def;
                    }
                }
            }

            // 1b-miss) VISIBLE MISREAD (Session 67, group AI-vs-AI): the locked
            //     release-block roll failed -- _frozeThisRelease suppresses the
            //     dodge split AND the correct-line race above, but a zero
            //     reaction reads as "the miss was intentional". Hold the
            //     WRONG-LINE guard latched at the roll through the release
            //     instead: a readable misread, not a statue.
            if (_misreadDefendFlag != Agent.MovementControlFlag.None
                && ownStage != Agent.ActionStage.AttackRelease
                && pStage == Agent.ActionStage.AttackRelease
                && InThreatReach(target))
            {
                return _misreadDefendFlag;
            }

            // 1c) SIDE-ATTACKER defense (multi-agent): any OTHER threat releasing
            //     a swing at us gets blocked regardless of our current target.
            //     MirrorDefend stays valid within the ~+/-45deg torso arc
            //     (native re-faces the torso); beyond that it's a blindside
            //     (the realistic vulnerability). Spar mode accepts the
            //     designated pair + the player regardless of team.
            //     GROUP mode skips the full-field scan entirely -- the
            //     engagement map already knows who's attacking us, so a
            //     threat counts only if it's aimed AT us (lock or
            //     assignment), in reach, and releasing.
            Agent sideThreat = _useAssignedThreats
                ? FindAssignedReleaser(target)
                : (_sparThreats
                    ? FindSparThreat(target)
                    : FindThreateningAttackerExcluding(target));
            if (sideThreat != null && ownStage != Agent.ActionStage.AttackRelease)
            {
                // Side releases are the step's best case -- outside the block
                // arc anyway. Same once-per-chain roll as the main target.
                // Session 37/38: re-enabled for spar, PLAYER-ONLY (same spec
                // as the main-target split above) -- the AI attacker's side
                // releases always get the immediate MirrorDefend block right
                // below, never a dodge roll.
                if (CombatAiState.DodgeEnabled && !_dodgedThisChain
                    && (!CombatAiState.SparMode || !_pureReactiveDefense || sideThreat.IsMainAgent))
                {
                    _dodgedThisChain = true;
                    CombatAiState.DodgeOpportunityCount++;
                    if (DuelRng.NextDouble() < DodgeChanceSide && TryStartDodge(sideThreat))
                    {
                        if (sideThreat.IsMainAgent)
                            PlayerCombatModel.MarkDefense(PlayerCombatModel.DefDodge);
                        return DodgeMoveFlag() | _dodgeDefendFlag;
                    }
                }
                Agent.MovementControlFlag def = BlockFlagVs(sideThreat, sideThreat.GetCurrentActionDirection(1));
                // Session 52 spar brain: the AI attacker's side releases get the
                // same probabilistic roll -- ONCE per release (latched); a miss
                // never re-rolls and just lets this swing through.
                // Session 64: group outnumbered defenders get the identical
                // once-per-release side roll (per-attacker latch inside).
                // FREE TARGET: classify by bearing vs ACTUAL facing, not the
                // lock. |theta| <= 25deg = frontal -> the same roll the
                // locked-target path uses (spar brain / group brain arms;
                // the legacy non-brain path is unconditional). Wider =
                // the existing non-locked side roll (|theta|>90 reuses it --
                // it is already the worst-case odds).
                bool frontal = _freeTargetActive && ThreatBearingDeg(sideThreat) <= CombatAiState.FreeFrontalDeg;
                bool sideOk = def != Agent.MovementControlFlag.None
                    && (frontal
                        ? FreeFrontalRollOk(sideThreat)
                        : (!(SparBrainActive || _groupDefender) || SideReleaseRollOk(sideThreat)));
                if (_freeTargetActive && def != Agent.MovementControlFlag.None && _sfLogCount < MaxSfLogs
                    && (sideThreat.Position.AsVec2 - Agent.Position.AsVec2).Length < 8f)
                {
                    _sfLogCount++;
                    CombatAiLog.Log($"[SPARFREE] Agent #{Agent.Index}: block threat=#{sideThreat.Index} " +
                        $"bearing={ThreatBearingDeg(sideThreat):F0} class={(frontal ? "frontal" : "side")} " +
                        $"roll={(sideOk ? "BLOCK" : "MISS")}");
                }
                // Session 67: a FAILED group side roll also produces a
                // visible answer -- the wrong-line guard (misread), not a
                // statue. The latched roll returning false inside
                // SideReleaseRollOk is what got us here. 4z: the steered
                // pair path (_groupDefender=false) gets the same baited
                // look vs a HELD attack at HoldFallMisreadShare -- a held
                // swing the defender fell for should look like a bite.
                if (!sideOk && def != Agent.MovementControlFlag.None
                    && CombatAiState.GroupFightMode && !sideThreat.IsMainAgent
                    && (_groupDefender
                        || (GfMoveClass(sideThreat) == PlayerCombatModel.MvHold
                            && DuelRng.NextDouble() < HoldFallMisreadShare)))
                {
                    return GroupMisreadLine(def);
                }
                if (sideOk)
                {
                    // Torso follows the combat target -- retarget the side threat
                    // for the block so it visibly turns instead of a physics-only
                    // invisible-sword block. Session 62 rig: NEVER retarget for
                    // a block -- the primary keeps the lock/body.
                    if (!_rigDriving)
                    {
                        EnsureTargetAgent(sideThreat);
                        _lastThreat = sideThreat;
                        _defendTargetHold = DefendTargetHoldSeconds;
                    }
                    _freeStance = false;
                    if (sideThreat.IsMainAgent)
                        PlayerCombatModel.MarkDefense(PlayerCombatModel.DefReactBlock);
                    if (_duelState == DuelState.Windup)
                        EnterApproach(false);
                    if (_sideAttackLogCount < MaxDuelLogs
                        && (sideThreat.Index != _lastSideBlockIdx || _brainClock - _lastSideBlockAt >= 0.5f))
                    {
                        _sideAttackLogCount++;
                        _lastSideBlockIdx = sideThreat.Index;
                        _lastSideBlockAt = _brainClock;
                        CombatAiLog.Log($"[DUEL] Agent #{Agent.Index} ({Agent.Name}): side-attack blocked from #{sideThreat.Index} ({sideThreat.Name}).");
                    }
                    return def;
                }
            }

            // HANDOFF (Session 35c): flag-driven locomotion is COMBAT GAIT
            // only -- there is no flag that makes an AI sprint; Forward at
            // any WalkMode is a walk. Outside the leash the BASE GAME owns
            // the body entirely (native locomotion: real run speed,
            // target tracking, pathing -- "run to get in range, stand and
            // fight"). We take the fight back once they're inside reach
            // again. Hysteresis (0.8/0.3) stops boundary flicker. The
            // dodge/defense machinery ABOVE still ticks during handoff.
            float handoff = MeleeReach() + 0.8f;
            float resume = MeleeReach() + 0.3f;
            // ai27 free-target fix: with a look-based face writer the free
            // driver keeps the body out to FreeLeashMeters -- the native
            // hand-off's pacing (3-6s to close 0.5m) was the "AI is slow to
            // trace back" complaint. Beyond the leash native pathfinding
            // still owns the body (and keeps the lock).
            bool freeLookDrive = _freeTargetActive
                && CombatAiState.FreeFace != CombatAiState.FreeFaceT.Frame;
            if (freeLookDrive)
            {
                handoff = MathF.Max(handoff, CombatAiState.FreeLeashMeters);
                resume = handoff - 1.0f;
            }
            bool forceDrive = ArenaStallWatch(dt, flatDist > handoff, target, flatDist);
            // 2v1 attackers never hand the body back to native: in the arena its AI idles in
            // a Guard pose instead of closing in (log: standing still 3-6m away), and our
            // driven locomotion already runs at combat pace. They always steer to their slot.
            bool noLeash = CombatAiState.DuoAttackMode && ArenaDuoCoordinator.IsParticipant(Agent.Index);
            // Same for the arena spar pair: the native take-over at the
            // reach+0.8 border visibly stalls them (stops, then runs again) --
            // stay driven and let the slot steering do the approach.
            if (!noLeash && CombatAiState.SparMode
                && (Agent.Index == CombatAiState.SparAttackerIndex || Agent.Index == CombatAiState.SparDefenderIndex))
                noLeash = true;
            if (CombatAiState.LookOnlyMode)
                noLeash = true;   // look-only drives at every distance
            if (CombatAiState.GroupFightMode && CombatAiState.GroupFree)
                noLeash = true;   // stage-2 fighters never hand off to native
            if (!forceDrive && !noLeash && (_handOffToNative ? flatDist > resume : flatDist > handoff))
            {
                _handOffToNative = true;
                _duelState = DuelState.Approach;   // re-enter cleanly
                return Agent.MovementControlFlag.None;
            }
            _handOffToNative = false;

            // Free approach-drive timing (ai27): the band between the old
            // leash (reach+0.8) and FreeLeashMeters is now OURS -- log how
            // long closing actually takes.
            if (freeLookDrive)
            {
                if (_ftApproachStart < 0f && flatDist > MeleeReach() + 0.8f && flatDist <= handoff)
                {
                    _ftApproachStart = _brainClock;
                    if (_ftHandoffLogCount < MaxHandoffLogs)
                    {
                        _ftHandoffLogCount++;
                        CombatAiLog.Log($"[FREETGT] Agent #{Agent.Index}: approach drive dist={flatDist:F2} (leash={handoff:F2}).");
                    }
                }
                else if (_ftApproachStart >= 0f && flatDist <= MeleeReach() + 0.3f)
                {
                    float took = _brainClock - _ftApproachStart;
                    _ftApproachStart = -1f;
                    if (_ftHandoffLogCount < MaxHandoffLogs)
                    {
                        _ftHandoffLogCount++;
                        CombatAiLog.Log($"[FREETGT] Agent #{Agent.Index}: approach closed in {took:F1}s (dist={flatDist:F2}).");
                    }
                }
                else if (_ftApproachStart >= 0f && flatDist > handoff)
                    _ftApproachStart = -1f;   // drifted back out to the native leash
            }
            else
                _ftApproachStart = -1f;

            // 2) Attack plan.
            switch (_duelState)
            {
                case DuelState.Approach:
                    // Session 34: kick evasion means a head-on press is no
                    // longer suicide -- _approachGait is mostly straight
                    // Forward now (max pressure), sometimes diagonal,
                    // sometimes a strafe-in (rolled in EnterApproach).
                    // 4ab: native parks just outside our reach in the group
                    // fight -- a small slack lets the swing step-in close it.
                    float reachSlack = _steerGroup && CombatAiState.GroupNativeTarget
                        && CombatAiState.GfNativemove ? GroupReachSlack : 0f;
                    if (flatDist > MeleeReach() + reachSlack)
                        return _approachGait;

                    // Turn-taking: it's only really the PLAYER's turn once they've
                    // released the swing (AttackRelease = committed, hit in flight).
                    // A player just HOLDING a ready attack is NOT a turn -- waiting
                    // for that forever is the base-game passivity trap.
                    if (pStage == Agent.ActionStage.AttackRelease)
                        return ApproachMoveFor(flatDist);

                    if (pStage == Agent.ActionStage.AttackReady || pStage == Agent.ActionStage.AttackQuickReady)
                    {
                        // Opportunist quick-attack into their windup (rolled on the
                        // swing edge) fires immediately.
                        if (_opportunistAllowed && GroupCanStartAttack(target))
                        {
                            _opportunistAllowed = false;
                            _playerSwungSinceOurs = false;
                            _attackReason = "opportunist"; StartDuelAttack(DuelAttackKind.Quick);
                            return _attackDir;
                        }
                        // Session 34 spec: concede the ~1-1.5s grace but don't
                        // go passive -- press IN until contact; once touching,
                        // stop driving into them and hold the band.
                        if (_playerReadyElapsed < _playerReadyGrace)
                            return flatDist > 0.5f ? Agent.MovementControlFlag.Forward : ApproachMoveFor(flatDist);
                        // Grace spent and they're STILL holding: half the time
                        // keep shoving a beat longer (the push makes THEM
                        // commit -- block / release / back-pedal) instead of
                        // racing a release into theirs.
                        if (!_pushRolled)
                        {
                            _pushRolled = true;
                            if (DuelRng.NextDouble() < 0.5f)
                                _pushUntil = _playerReadyElapsed + 0.30f + (float)DuelRng.NextDouble() * 0.30f;
                        }
                        if (_playerReadyElapsed < _pushUntil)
                            return flatDist > 0.5f ? Agent.MovementControlFlag.Forward : ApproachMoveFor(flatDist);
                        if (!MayInitiate(target, true) || !GroupCanStartAttack(target))
                            return flatDist > 0.5f ? Agent.MovementControlFlag.Forward : ApproachMoveFor(flatDist);
                        _playerSwungSinceOurs = false;
                        _attackReason = "vs-held-grace"; StartDuelAttack(PickAttackKindVsHeld());
                        return _attackDir;
                    }

                    // Recover boost (Session 45): the player just let our
                    // vulnerable window close without countering -- we act
                    // before they do. Otherwise hesitation scales with the
                    // live read: an unreadable opponent makes the AI think
                    // longer (caution), a modeled one gets fast initiative.
                    // Session 48: vs the player the old "unreadable opponent ->
                    // think up to 2x longer" scaling is replaced by the brain's
                    // caution (the player-chain gates in MayInitiate already
                    // keep us from swinging into a windup) -- it made the AI
                    // passive for no reason the player could see.
                    float effThink = _recoverBoost > 0f ? _thinkDelay * 0.3f
                        : target.IsMainAgent
                            ? (CombatAiState.BrainInitiative
                                ? _thinkDelay * (0.75f + 0.5f * MathF.Max(0f, _caution))
                                : _thinkDelay * (2.0f - 1.5f * PlayerCombatModel.Expectation))
                        : _thinkDelay;
                    if (_duelTimer >= effThink && MayInitiate(target, false))
                    {
                        if (!GroupCanStartAttack(target))
                        {
                            if (_gfDefenderActive)
                            {
                                _gsumGapHold++;
                                if (_gfDefBlocked) _gsumDefGapHold++;
                            }
                            else
                                _gsumGapHoldND++;
                            return ApproachMoveFor(flatDist);
                        }
                        _playerSwungSinceOurs = false;
                        _recoverBoost = 0f;
                        _attackReason = "think"; StartDuelAttack(PickAttackKind());
                        return _attackDir;
                    }
                    return ApproachMoveFor(flatDist);

                case DuelState.Windup:
                    if (ownStage == Agent.ActionStage.AttackReady || ownStage == Agent.ActionStage.AttackQuickReady)
                    {
                        _dirSuspect &= ~_attackDir;   // proof this weapon CAN do it
                        // ai39 steered defender: the held dir is re-checked
                        // LIVE -- a target that crossed sides (or a held
                        // up/down with the target now clearly on a side)
                        // redirects through the normal feint path, or releases
                        // once the redirect budget is spent.
                        Agent.MovementControlFlag? steerRet = SteerLiveSideCheck(flatDist);
                        if (steerRet.HasValue)
                            return steerRet.Value;
                        // Punish early block -- but read WHICH line their guard
                        // covers first: blocking the wrong line = free hit
                        // (release now); blocking OUR line = they read the
                        // swing, so the feint redirect is the answer -- and an
                        // unplanned feint is allowed to trigger on the read.
                        // (The pending-feint exception stays: a planned feint
                        // is already the answer, don't pre-empt it.)
                        if ((pStage == Agent.ActionStage.Defend || pStage == Agent.ActionStage.DefendParry)
                            && !(_feintPlanned && !_feinted))
                        {
                            if (CoveredAttackDir(target) != _attackDir)
                            {
                                if (_duelLogCount < MaxDuelLogs)
                                {
                                    _duelLogCount++;
                                    CombatAiLog.Log($"[DUEL] Agent #{Agent.Index} ({Agent.Name}): early block on the WRONG line -> instant release into the open side.");
                                }
                                _duelState = DuelState.Release;
                                _duelTimer = 0f;
                                return Agent.MovementControlFlag.None;
                            }
                            // Guard covers our line: redirect into the read.
                            if (!GroupNoFeint && !_feinted && _feintChain < MaxFeintRedirects && _duelTimer > 0.12f)
                            {
                                if (_duelLogCount < MaxDuelLogs)
                                {
                                    _duelLogCount++;
                                    CombatAiLog.Log($"[DUEL] Agent #{Agent.Index} ({Agent.Name}): early block covers our line -> reactive FEINT redirect.");
                                }
                                _feintPlanned = false;
                                EnterFeintCancel(first: false);
                                return _attackDir | Agent.GetDefendMovementFlag();
                            }
                            // No redirect left: bait the held block a beat, then
                            // chip their posture anyway.
                            if (_duelTimer < _duelHoldSeconds + 0.30f)
                                return _attackDir | WindupMoveFor(flatDist);
                            if (_duelLogCount < MaxDuelLogs)
                            {
                                _duelLogCount++;
                                CombatAiLog.Log($"[DUEL] Agent #{Agent.Index} ({Agent.Name}): early block held -> release into it (posture chip).");
                            }
                            _duelState = DuelState.Release;
                            _duelTimer = 0f;
                            return Agent.MovementControlFlag.None;
                        }

                        // Feint: enter the redirect sequence -- the REAL mechanic
                        // is hold-attack -> TAP BLOCK (cancels the ready) -> hold
                        // attack in the new direction -> release. Not a bare flag
                        // switch. The ~0.3s right after committing to it is our
                        // vulnerable window (the trap the player punishes).
                        if (!GroupNoFeint && _feintPlanned && !_feinted && _duelTimer >= _feintAt)
                        {
                            EnterFeintCancel(first: true);
                            return _attackDir | Agent.GetDefendMovementFlag();
                        }

                        // Double-hold (Session 34 spec): THEIR ready is held
                        // while ours is -- releasing now races their release
                        // and their angle lands first. Hold a beat longer with
                        // the band step (Forward while out of reach = the
                        // shove-off-the-ready) so they have to commit first.
                        // A planned feint is exempt -- the redirect is already
                        // the answer.
                        // Session 48: the same hold applies in the player's feint
                        // block-tap GAP (they may re-ready any frame): loading up is
                        // free (we can cancel to a guard until OUR release),
                        // committing into it is not.
                        if (((pStage == Agent.ActionStage.AttackReady || pStage == Agent.ActionStage.AttackQuickReady)
                                || (CombatAiState.BrainInitiative && target.IsMainAgent && (!_pureReactiveDefense || SparDuelGate)
                                    && PlayerCombatModel.InCancelGap))
                            && !(_feintPlanned && !_feinted)
                            && _duelTimer >= _duelHoldSeconds
                            && _duelTimer < _duelHoldSeconds + DoubleHoldExtraSeconds)
                            return _attackDir | WindupMoveFor(flatDist);

                        if (_duelTimer >= _duelHoldSeconds)
                        {
                            _duelState = DuelState.Release;
                            _duelTimer = 0f;
                            return Agent.MovementControlFlag.None;
                        }
                        // Body flavor: drift while holding the ready so the hold
                        // doesn't telegraph statically (movement flag OR'd with
                        // the attack flag). Forward is suppressed once we're
                        // already inside reach -- otherwise the agent keeps
                        // shoving into the target during long holds.
                        return _attackDir | WindupMoveFor(flatDist);
                    }

                    if (!ownInAttack && _duelTimer > DuelWindupFailsafe)
                    {
                        // Weapon never readied (stunned / that direction is
                        // impossible for this weapon, e.g. can't thrust an axe)
                        // -> abandon the swing and retry with a fresh pick.
                        NoteDirectionFailed();
                        _duelState = DuelState.Recover;
                        _duelTimer = 0f;
                        return Agent.MovementControlFlag.None;
                    }
                    // ai39 steered defender: no ready has latched yet -- a
                    // crossed side fixes the held flag for free here.
                    if (!ownInAttack)
                        SteerPreReadySideSync();
                    return _attackDir | WindupMoveFor(flatDist);

                case DuelState.FeintCancel:
                    // The CRITICAL input detail (user-described player mechanic):
                    // the ATTACK button stays held THROUGHOUT the feint -- the
                    // block is only a TAP layered on top, so flags = attack | defend,
                    // never defend alone. Writing defend alone releases the attack
                    // entirely and the agent settles into a guard pose -- exactly
                    if (!ownInAttack)
                    {
                        // Ready dropped -- attack flag never left the input, so the
                        // new direction re-readies immediately = the fluid redirect.
                        _duelState = DuelState.FeintReady;
                        _duelTimer = 0f;
                        return _attackDir | WindupMoveFor(flatDist);
                    }
                    if (_duelTimer >= FeintCancelTimeout)
                    {
                        if (_duelLogCount < MaxDuelLogs)
                        {
                            _duelLogCount++;
                            CombatAiLog.Log($"[DUEL] Agent #{Agent.Index} ({Agent.Name}): feint cancel FAILED -- ready did not drop after {FeintCancelTimeout}s of defend input; releasing original swing.");
                        }
                        _duelState = DuelState.Release;
                        _duelTimer = 0f;
                        return WindupMoveFor(flatDist);
                    }
                    // ai39 steered defender: the ready is still held through
                    // the cancel, so a target crossing here leaves the just-
                    // picked dir stale before it ever readies -- re-check live.
                    Agent.MovementControlFlag? steerRetFc = SteerLiveSideCheck(flatDist);
                    if (steerRetFc.HasValue)
                        return steerRetFc.Value;
                    return _attackDir | Agent.GetDefendMovementFlag() | WindupMoveFor(flatDist);

                case DuelState.FeintReady:
                    if (ownStage == Agent.ActionStage.AttackReady || ownStage == Agent.ActionStage.AttackQuickReady)
                    {
                        _dirSuspect &= ~_attackDir;   // the redirect readied -- suspicion was wrong
                        if (_feintReadyAt < 0f)
                        {
                            _feintReadyAt = _duelTimer;
                            // Give the sword a visible beat in the new ready
                            // pose AND the body time to recenter on the target
                            // (the frame aims straight at it during settle) --
                            // releasing while still rotated = guaranteed whiff.
                            _feintSettleSeconds = MathF.Max(0.05f,
                                (0.16f + (float)DuelRng.NextDouble() * 0.14f) * FightScript.WindowScale);
                            if (!_feintLogged && _duelLogCount < MaxDuelLogs)
                            {
                                _feintLogged = true;
                                _duelLogCount++;
                                // AttackQuickReady here would CONFIRM the engine
                                // treats block-tap-in-ready as the quick redirect.
                                CombatAiLog.Log($"[DUEL] Agent #{Agent.Index} ({Agent.Name}): feint re-ready in stage={ownStage} actionDir={Agent.GetCurrentActionDirection(1)} (wanted {_attackDir}).");
                            }
                        }
                        if (_duelTimer >= _feintReadyAt + _feintSettleSeconds)
                        {
                            // Multi-feint chain: instead of releasing, block-tap
                            // again and redirect once more (up to 4 total). The
                            // hold -> feint -> maybe-feint-again -> finally release
                            // sequence is what makes the read genuinely hard.
                            if (!GroupNoFeint && _feintChain < MaxFeintRedirects && DuelRng.NextDouble() < FeintContinueChance * _feintRateScale)
                            {
                                EnterFeintCancel(first: false);
                                return _attackDir | Agent.GetDefendMovementFlag() | WindupMoveFor(flatDist);
                            }
                            _duelState = DuelState.Release;
                            _duelTimer = 0f;
                            // BODY FEINT relock guarantee: the state change
                            // makes LockWantedNow() true THIS tick, so this
                            // call writes the positive lock the same frame
                            // the FSM leaves FeintReady -> Release -- before
                            // the AttackRelease action can start on ch1.
                            EnsureTargetAgent(target);
                            return WindupMoveFor(flatDist);
                        }
                        // ai39 steered defender: the feinted ready is still
                        // held through its settle -- a crossing here would
                        // release at a stale side. Same live re-check.
                        Agent.MovementControlFlag? steerRetFr = SteerLiveSideCheck(flatDist);
                        if (steerRetFr.HasValue)
                            return steerRetFr.Value;
                        return _attackDir | WindupMoveFor(flatDist);
                    }
                    if (_duelTimer > FeintReReadyFailsafe)
                    {
                        // New direction never readied -- abandon and recover.
                        NoteDirectionFailed();
                        _duelState = DuelState.Recover;
                        _duelTimer = 0f;
                        return WindupMoveFor(flatDist);
                    }
                    return _attackDir | WindupMoveFor(flatDist);

                case DuelState.Release:
                    if ((!ownInAttack && _duelTimer >= 0.05f) || _duelTimer > 1.5f)
                    {
                        _duelState = DuelState.Recover;
                        _duelTimer = 0f;
                    }
                    return WindupMoveFor(flatDist);

                case DuelState.Recover:
                    if (_duelTimer >= DuelRecoverSeconds)
                    {
                        // Turn-taking: if the player got a swing in while we were
                        // attacking, answer back quickly (rally rhythm); if they
                        // never attacked, leave a longer beat before we press again.
                        EnterApproach(true);
                    }
                    return WindupMoveFor(flatDist);
            }

            return Agent.MovementControlFlag.None;
        }

        // Defense decision, made ONCE per swing chain (a feint's re-ready edge
        // doesn't re-decide -- that would make us immune to feints).
        //   vs the PLAYER (T1): PlayerCombatModel's read decides. Confident ->
        //   arm an early commit on the predicted line; weak -> stay
        //   uncommitted and let the release-edge reaction race decide.
        //   vs AI (T2): flat skill-check, no model -- cheap and the tempo of
        //   AI swings is slower anyway.
        private void DecideDefenseVs(Agent threat)
        {
            _defCommitState = 0;
            _defCommitFlag = Agent.MovementControlFlag.None;
            if (_pureReactiveDefense && !(SparBrainActive && threat != null && threat.IsMainAgent))
            {
                // Spar defender: perfectblock semantics -- always answer the
                // release edge, never arm the prediction-commit machinery.
                // (Session 52: skipped when the spar brain handles the player.)
                _defendAllowed = true;
                return;
            }
            if (threat != null && threat.IsMainAgent)
            {
                // The post-feint window is handled as reaction latency, not a
                // separate roll -- but while it's open the model's commit is
                // suppressed entirely (we're mid-redirect, can't pre-read).
                // Session 47: the old Predictability-gated early commit is gone
                // -- S1/S2 (BrainTick) own every early guard now. Only when the
                // brain's S1 is switched off does the legacy commit still arm.
                if (!CombatAiState.BrainS1 && _vulnerableTimer <= 0f
                    && PlayerCombatModel.TryGetPrediction(out Agent.UsageDirection pDir, out float pHold))
                {
                    _defCommitState = 1;
                    _defCommitFlag = BlockFlagVs(threat, pDir);
                    _defCommitAt = MathF.Max(0.05f, pHold - _reactionLatency - 0.06f);
                    if (_duelLogCount < MaxDuelLogs)
                    {
                        _duelLogCount++;
                        CombatAiLog.Log($"[DUEL] Agent #{Agent.Index}: READ commit -- predict {pDir} in ~{pHold:F2}s (block at {_defCommitAt:F2}s of ready, conf={PlayerCombatModel.Predictability:F2}).");
                    }
                }
                _defendAllowed = true; // the release edge runs the real race
            }
            else
            {
                float chance = _vulnerableTimer > 0f ? BlockChanceVulnerable : 0.72f;
                _defendAllowed = DuelRng.NextDouble() < chance;
            }
        }

        private void _defCommittedReset()
        {
            _defCommitState = 0;
            _defCommitFlag = Agent.MovementControlFlag.None;
            _wrongVisibleAt = -1f;
            _guardDropAt = 0f;
            _repointFlag = Agent.MovementControlFlag.None;
        }

        // One redirect step of a feint (also reused for chained feints): pick the
        // new direction, flick the body/look for the visual tell-cover, open our
        // vulnerable window, then let FeintCancel run the block tap.
        private void EnterFeintCancel(bool first)
        {
            // 4w gtnative group: no feints -- a call reaching us here is a
            // bug; count it for [GFSM-SUM] and refuse (never a state stall).
            if (GroupNoFeint)
            {
                _gsumFeints++;
                return;
            }
            Agent.MovementControlFlag oldDir = _attackDir;
            _attackDir = PickDifferentAttackDir();
            // Steered spar defender: a redirect must not swing AWAY from the
            // target's side (the body never rotates to it -- a wrong-side
            // swing is a guaranteed whiff). Re-apply the same side rule;
            // no-op when the pick already agrees.
            Agent.MovementControlFlag feintSide = SteerSideAttackDir();
            if (feintSide != Agent.MovementControlFlag.None)
                _attackDir = feintSide;
            _feinted = true;
            _feintReadyAt = -1f;
            _feintLogged = false;
            _feintChain++;
            _feintStreak++;   // Session 34: chains of feints arm the surprise quick
            _vulnerableTimer = PostFeintVulnerableSeconds * FightScript.WindowScale;
            _duelState = DuelState.FeintCancel;
            _duelTimer = 0f;
            if (_duelLogCount < MaxDuelLogs)
            {
                _duelLogCount++;
                CombatAiLog.Log($"[DUEL] Agent #{Agent.Index} ({Agent.Name}): FEINT {_feintChain} {oldDir} -> {_attackDir} (block-cancel -> re-ready).");
            }
        }

        private void StartDuelAttack(DuelAttackKind kind)
        {
            // Defense-only test mode: never transition into an attack.
            if (CombatAiState.DefenseOnly)
                return;
            if (_steerGroup && CombatAiState.GroupNativeTarget)
                _gsumAtk++;   // 4w [GFSM-SUM]
            // SPARFREE defender attack gate (ai30): a swing may START only
            // while the target is inside +/-SparDefAttackViewDeg of the
            // current body facing -- the attack-window lock acquisition then
            // can't snap the body (>30deg away it looked like teleporting the
            // camera). In-flight swings are never touched: this is the
            // single choke point for every duel-FSM attack start.
            // NATIVE-STEER variant (_steerSparDef): the lock never moves the
            // body, so there is no snap to cap -- the bound is "in view":
            // the bisector heading keeps both attackers in front and the
            // swing direction matches the target's side (SteerSideAttackDir),
            // so side swings still reach out to ~100deg. Behind-the-back
            // starts are refused the same way.
            if (CombatAiState.SparMode && CombatAiState.SparFree
                && (_freeTargetActive || _steerSparDef)
                && Agent.Index == CombatAiState.SparDefenderIndex && !Agent.HasMount)
            {
                Agent at = CombatLive(_forcedDuelTarget) ? _forcedDuelTarget
                    : GetAgentByIndex(CombatAiState.SparAttackerIndex);
                float bear = CombatLive(at) ? ThreatBearingDeg(at) : -1f;
                float viewLimit = _steerSparDef ? 100f : CombatAiState.SparDefAttackViewDeg;
                if (bear < 0f || bear > viewLimit)
                {
                    // ai31: a refused attack is rolled EVERY tick -- emit at
                    // most one line per 0.5s with an xN suppression suffix.
                    if (_brainClock - _lastBlockedAt >= 0.5f && _sfAtkLogCount < MaxSfAtkLogs)
                    {
                        _sfAtkLogCount++;
                        CombatAiLog.Log($"[SPARFREE] Agent #{Agent.Index}: defender attack start " +
                            $"target={(CombatLive(at) ? "#" + at.Index : "-")} bearing={bear:F0} " +
                            $"gate=blocked x{_blockedSupp + 1}");
                        _blockedSupp = 0;
                        _lastBlockedAt = _brainClock;
                    }
                    else
                        _blockedSupp++;
                    return;
                }
                if (_sfAtkLogCount < MaxSfAtkLogs)
                {
                    _sfAtkLogCount++;
                    CombatAiLog.Log($"[SPARFREE] Agent #{Agent.Index}: defender attack start " +
                        $"target=#{at.Index} bearing={bear:F0} gate=pass lock={(_steerSparDef ? "steer" : "on")}");
                    if (!_steerSparDef)
                        _sfAtkActive = true;   // steer: the lock never releases -- no end line to pair
                }
            }
            // STAGE-2 group fight: the engagement pointer is placed HERE, at
            // the single choke every attack start funnels through. A group
            // fighter may start only if its target exists and the engagement
            // claim succeeds (<= GroupMaxEngagers live claimers on the
            // victim). The release happens when the FSM drops back to
            // Approach (see the end-of-tick check in DrivePerFrameCore).
            if (CombatAiState.GroupFightMode && CombatAiState.GroupFree && _freeTargetActive
                && GroupLineup.Phase == GroupLineup.Ph.Fight && !Agent.HasMount)
            {
                Agent gv = CombatLive(_forcedDuelTarget) ? _forcedDuelTarget : _duelTarget;
                string deny = CombatLive(gv) ? null : "no-target";
                if (deny != null
                    || !GroupEngagement.TryClaim(Agent.Mission, Agent.Index, gv.Index,
                        GroupEngagement.KindT.Swing, _brainClock, out deny))
                {
                    if (_sfAtkLogCount < MaxSfAtkLogs)
                    {
                        _sfAtkLogCount++;
                        CombatAiLog.Log($"[GENG] Agent #{Agent.Index}: attack start DENIED " +
                            $"target={(CombatLive(gv) ? "#" + gv.Index : "-")} reason={deny ?? "no-target"}");
                    }
                    return;
                }
            }
            LogAttackStart(kind);
            NoteAttackStarted(); // tempo budget for self-started attacks
            // Only directions the weapon can actually perform (axes can't thrust).
            _attackDir = PickAttackDir(Agent.MovementControlFlag.None, AllyForbiddenAttackDir());
            // NATIVE-STEER spar defender (user spec): the body keeps the
            // bisector heading -- it does NOT rotate to face the target. The
            // swing direction instead matches the side the target sits on:
            // >20deg left -> AttackLeft, >20deg right -> AttackRight (skips
            // the covered-dir re-pick -- it always matches the side). Near
            // centre the normal pick stands; AttackUp/AttackDown remain
            // possible through it (the up/down-only rule is group-only).
            Agent.MovementControlFlag sideDir = SteerSideAttackDir();
            if (sideDir != Agent.MovementControlFlag.None)
                _attackDir = sideDir;
            // 4s (gtnative group, updown): crowded -> overhead/thrust only;
            // a side swing clips the allies packed beside us.
            if (GroupCrowded())
            {
                Agent.MovementControlFlag cd = GroupCrowdDir(_lastAttackDir);
                if (cd != Agent.MovementControlFlag.None)
                    _attackDir = cd;
            }
            _lastAttackDir = _attackDir;
            _feintPlanned = false;
            _feinted = false;
            _feintChain = 0;
            _feintAt = 0f;
            _duelHoldExtended = false;   // group 2-swing stagger: fresh swing may extend again
            _pressureSide = DuelRng.NextDouble() < 0.5 ? -1f : 1f; // sidestep side for the band
            if (kind != DuelAttackKind.Feint)
                _feintStreak = 0;   // a non-feint swing breaks the streak

            switch (kind)
            {
                case DuelAttackKind.Quick:
                    _duelHoldSeconds = MathF.Max(0.05f, 0.05f * FightScript.WindowScale);
                    break;
                case DuelAttackKind.Normal:
                    _duelHoldSeconds = MathF.Max(0.05f, (0.15f + (float)DuelRng.NextDouble() * 0.25f) * FightScript.WindowScale);
                    break;
                case DuelAttackKind.Held:
                    _duelHoldSeconds = MathF.Max(0.05f, (0.5f + (float)DuelRng.NextDouble() * 0.7f) * FightScript.WindowScale);
                    break;
                case DuelAttackKind.Feint:
                    _feintPlanned = true;
                    _feintAt = 0.08f + (float)DuelRng.NextDouble() * 0.10f;
                    _duelHoldSeconds = 999f; // released by the feint path's timer reset
                    break;
            }

            _duelState = DuelState.Windup;
            _duelTimer = 0f;
        }

        private DuelAttackKind PickAttackKind()
        {
            // 4t gtnative quick-hit: the excursion IS a jab window -- one
            // fast swing, then home.
            if (_gfForceQuick)
            {
                _gfForceQuick = false;
                return DuelAttackKind.Quick;
            }
            // 4w gtnative group: NO feints -- normal/held only (55/45).
            if (GroupNoFeint)
                return DuelRng.NextDouble() < 0.55 ? DuelAttackKind.Normal : DuelAttackKind.Held;
            // Spar defender jab window: the whole point of switching the lock
            // to the attacker is a QUICK hit, not a held windup.
            if (_jabTimer > 0f)
                return DuelAttackKind.Quick;
            // Feint share scales with _feintRateScale (halved for outnumbered
            // defenders -- a swarmed fighter who chains feints just eats hits).
            // Base 40%, same Held/Normal/Quick split of the remainder.
            // Session 34 spec: after a run of feints the player starts
            // expecting another feint or a held attack -- break the pattern
            // with a surprise quick sometimes (never ALWAYS quick = not spam).
            if (_feintStreak >= 2 && DuelRng.NextDouble() < 0.45f)
                return DuelAttackKind.Quick;
            double feintShare = 0.40 * _feintRateScale;
            double r = DuelRng.NextDouble();
            if (r >= 1.0 - feintShare)
                return DuelAttackKind.Feint;
            double x = r / (1.0 - feintShare);
            if (x < 0.42) return DuelAttackKind.Held;
            if (x < 0.75) return DuelAttackKind.Normal;
            return DuelAttackKind.Quick;
        }

        // Answering the player's HELD attack (Session 34 spec): never the
        // bare-quick answer -- simultaneous releases land THEIR angle first.
        // Mostly holds + feints that force THEIR commit (a raised attack
        // they have to respect), quicks only as the rare pattern-break.
        private DuelAttackKind PickAttackKindVsHeld()
        {
            if (GroupNoFeint)
                return DuelRng.NextDouble() < 0.55 ? DuelAttackKind.Normal : DuelAttackKind.Held;
            double feintShare = 0.40 * _feintRateScale;
            double r = DuelRng.NextDouble();
            if (r < feintShare) return DuelAttackKind.Feint;
            if (r < feintShare + 0.35) return DuelAttackKind.Held;
            if (r < feintShare + 0.55) return DuelAttackKind.Normal;
            return DuelAttackKind.Quick;
        }

        private Agent.MovementControlFlag PickDifferentAttackDir()
        {
            // Prefer the OPPOSITE side so the redirect swings the weapon across
            // the body -- a dramatic, readable feint. Falls back to any other
            // supported direction when the opposite isn't available (e.g. no
            // thrust on axes means Up->Down isn't possible).
            Agent.MovementControlFlag forbidden = AllyForbiddenAttackDir();
            if (GroupCrowded())   // 4s: crowded redirects stay on the up/down lines
            {
                Agent.MovementControlFlag cd = GroupCrowdDir(_attackDir);
                if (cd != Agent.MovementControlFlag.None && cd != _attackDir)
                    return cd;
            }
            Agent.MovementControlFlag opp = OppositeAttackDir(_attackDir);
            if (opp != Agent.MovementControlFlag.None && opp != forbidden && IsDirectionSupported(opp))
                return opp;
            return PickAttackDir(_attackDir, forbidden);
        }

        // Spar 2-attacker rule (Session 37, user spec): the attacker-relative
        // version -- "if attacker finds other attacker/friendly on his side
        // they don't swing from that side." No left/right told to us: we
        // compute it from the attacker's OWN facing vs the ally's bearing
        // (same cross-product convention as PlayerArcStrafe: cross>0 = ally
        // on our left). AttackLeft sweeps through our own left side
        // (CoveredAttackDir/MirrorDefend geometry), so that's what gets
        // forbidden when the ally is there; mirrored for the right.
        // SparAttacker-index gated -- never fires in duel or group.
        private Agent.MovementControlFlag AllyForbiddenAttackDir()
        {
            Agent ally;
            if (CombatAiState.DuoAttackMode && ArenaDuoCoordinator.IsParticipant(Agent.Index))
                ally = ArenaDuoCoordinator.GetPartnerAgent(Agent.Index, Agent.Mission); // 2v1: the other attacker
            else if (CombatAiState.SparMode && Agent.Index == CombatAiState.SparAttackerIndex)
                ally = FindSparAlly(null);
            else
                return Agent.MovementControlFlag.None;
            if (!CombatLive(ally))
                return Agent.MovementControlFlag.None;
            Vec3 f3 = Agent.LookDirection; f3.z = 0f;
            Vec3 to3 = ally.Position - Agent.Position; to3.z = 0f;
            if (f3.LengthSquared < 0.0001f || to3.LengthSquared < 0.0001f)
                return Agent.MovementControlFlag.None;
            Vec2 f = new Vec2(f3.x, f3.y); f.Normalize();
            Vec2 to = new Vec2(to3.x, to3.y); to.Normalize();
            float cross = f.x * to.y - f.y * to.x; // >0 = ally on OUR left
            const float deadzone = 0.15f; // ally roughly dead ahead/behind -> no restriction
            if (cross > deadzone) return Agent.MovementControlFlag.AttackLeft;
            if (cross < -deadzone) return Agent.MovementControlFlag.AttackRight;
            return Agent.MovementControlFlag.None;
        }

        private static Agent.MovementControlFlag OppositeAttackDir(Agent.MovementControlFlag dir)
        {
            switch (dir)
            {
                case Agent.MovementControlFlag.AttackLeft: return Agent.MovementControlFlag.AttackRight;
                case Agent.MovementControlFlag.AttackRight: return Agent.MovementControlFlag.AttackLeft;
                case Agent.MovementControlFlag.AttackUp: return Agent.MovementControlFlag.AttackDown;
                case Agent.MovementControlFlag.AttackDown: return Agent.MovementControlFlag.AttackUp;
                default: return Agent.MovementControlFlag.None;
            }
        }

        // Uniform pick among attack directions the wielded weapon can ACTUALLY
        // perform, excluding `exclude`. Required because weapons like axes and
        // the Vlandian billhook can't thrust -- picking AttackDown for them makes
        // the re-ready never come, and the agent just sits in the cancel-block
        // looking like it switched to blocking (observed in-game).
        //
        // Anti-pattern layer (T1): the line the defender is CURRENTLY holding a
        // block on is pointless to swing on, and the direction we used last
        // swing is the one a reader expects next -- both get excluded when an
        // alternative exists. Unpredictable by construction, not by dice.
        // `forbidden` (Session 37, spar 2-attacker rule): an extra direction
        // the caller physically must not use (e.g. it would swing toward a
        // nearby ally) -- tried with full priority first, then the "wide"
        // widen keeps respecting it, and only a true last-resort tier drops
        // it (ally-safety can't fully lock an attacker out of swinging at
        // all). When `forbidden` is None this is identical to the original
        // two-tier behavior -- no change for duel/group callers.
        private Agent.MovementControlFlag PickAttackDir(Agent.MovementControlFlag exclude, Agent.MovementControlFlag forbidden = Agent.MovementControlFlag.None)
        {
            Agent.MovementControlFlag covered = CoveredAttackDir(_duelTarget);

            Agent.MovementControlFlag TryPick(bool respectCovered, bool respectForbidden)
            {
                int options = 0;
                foreach (Agent.MovementControlFlag d in AttackDirs)
                    if (d != exclude && (!respectForbidden || d != forbidden)
                        && (!respectCovered || (d != covered && d != _lastAttackDir))
                        && IsDirectionSupported(d))
                        options++;
                if (options == 0)
                    return Agent.MovementControlFlag.None;
                int target = DuelRng.Next(options);
                foreach (Agent.MovementControlFlag d in AttackDirs)
                {
                    if (d == exclude || (respectForbidden && d == forbidden)
                        || (respectCovered && (d == covered || d == _lastAttackDir))
                        || !IsDirectionSupported(d))
                        continue;
                    if (target-- == 0)
                        return d;
                }
                return Agent.MovementControlFlag.None;
            }

            Agent.MovementControlFlag pick = TryPick(respectCovered: true, respectForbidden: true);
            if (pick == Agent.MovementControlFlag.None)
                pick = TryPick(respectCovered: false, respectForbidden: true);  // widen: drop covered/repeat
            if (pick == Agent.MovementControlFlag.None)
                pick = TryPick(respectCovered: false, respectForbidden: false); // last resort: drop forbidden too
            return pick != Agent.MovementControlFlag.None ? pick : (GroupUpDownOnly ? Agent.MovementControlFlag.AttackUp : Agent.MovementControlFlag.AttackLeft);
        }

        // NATIVE-STEER spar defender attack-side rule (user spec): the body
        // keeps the bisector heading -- it does NOT rotate to face the
        // target. The swing direction instead matches the side the target
        // sits on vs the CURRENT facing: >20deg left -> AttackLeft, >20deg
        // right -> AttackRight (cross>0 = target on OUR left, the same
        // convention as AllyForbiddenAttackDir). Inside the ~20deg dead zone
        // it returns None so the caller's normal pick stands (centre hits
        // keep full direction variety incl. Up/Down). Also None when this
        // isn't the steered defender, the target/facing isn't resolvable, or
        // the wielded weapon can't actually swing that line.
        private Agent.MovementControlFlag SteerSideAttackDir()
        {
            if (GroupCrowded())   // 4s: crowded gtnative -> up/down lines only
                return Agent.MovementControlFlag.None;
            float bear = SteerTargetBearingRad();
            // ai40: 15deg dead zone -- up/down picks inside 20deg that drifted
            // past the 25deg flip threshold were the visible leftover residue
            // (AttackDown chosen at bear~18-24deg, caught by the flip ticks
            // later). Now 15-25deg bearings start as side swings; up/down is
            // only ever picked for a truly centred target.
            if (float.IsNaN(bear) || MathF.Abs(bear) <= 0.2618f)   // ~15deg dead zone
                return Agent.MovementControlFlag.None;
            Agent.MovementControlFlag side = bear > 0f
                ? Agent.MovementControlFlag.AttackLeft
                : Agent.MovementControlFlag.AttackRight;
            return IsDirectionSupported(side) ? side : Agent.MovementControlFlag.None;
        }

        // Signed bearing (rad) of the steer target vs the CURRENT body facing
        // (GetMovementDirection, body-frame forward fallback): >0 = target on
        // OUR left -- the same cross convention as AllyForbiddenAttackDir and
        // ThreatBearingDeg. float.NaN when this isn't the steered defender or
        // the target/facing isn't resolvable. Recomputed every call -- this is
        // the LIVE reading the ai39 fix evaluates each tick.
        private float SteerTargetBearingRad()
        {
            if (!_steerSparDef && !_steerGroup)
                return float.NaN;
            Vec2 f = Agent.GetMovementDirection();
            if (f.LengthSquared < 0.0001f)
                f = Agent.Frame.rotation.f.AsVec2;
            Agent t = CombatLive(_duelTarget) ? _duelTarget
                : (CombatLive(_forcedDuelTarget) ? _forcedDuelTarget : null);
            if (f.LengthSquared < 0.0001f || !CombatLive(t))
                return float.NaN;
            Vec2 to = t.Position.AsVec2 - Agent.Position.AsVec2;
            if (to.LengthSquared < 0.0001f)
                return float.NaN;
            float cross = f.x * to.y - f.y * to.x;   // >0 = target on OUR left
            float dot = f.x * to.x + f.y * to.y;
            return MathF.Atan2(cross, dot);
        }

        // ai39: is the held _attackDir contradicted by the live target bearing?
        // A side swing held while the target is now >25deg on the OTHER side
        // is stale; a held Up/Down anywhere outside ~25deg of centre is stale
        // too (up/down is only honest for a near-centre target). The 5deg
        // hysteresis on top of the 20deg dead zone stops a target hovering at
        // the boundary from bouncing redirects every tick.
        private bool SteerHeldDirStale(float bearRad)
        {
            const float flip = 0.4363f;   // ~25deg
            if (_attackDir == Agent.MovementControlFlag.AttackLeft)
                return bearRad < -flip;
            if (_attackDir == Agent.MovementControlFlag.AttackRight)
                return bearRad > flip;
            if (_attackDir == Agent.MovementControlFlag.AttackUp
                || _attackDir == Agent.MovementControlFlag.AttackDown)
                return MathF.Abs(bearRad) > flip;
            return false;
        }

        // ai39 live side re-check for the steered spar defender: while a held
        // attack sits in a ready stage (Windup hold or mid-feint re-ready) the
        // target can cross to the other side -- the latched _attackDir would
        // then keep swinging at the OLD side forever (the ai39 bug). Called
        // every tick from the FSM's ready branches. Returns null when the held
        // dir still matches the live side (normal processing continues); a
        // flag value when the tick was handled: a side flip reuses the normal
        // feint path -- EnterFeintCancel re-picks, SteerSideAttackDir inside
        // it re-forces the live side, and FeintCancel/FeintReady run the
        // block-tap cancel -> re-ready (it costs one _feintChain redirect, the
        // same budget a chained feint spends). Once MaxFeintRedirects is spent
        // the swing is released instead: a wrong-side whiff beats holding a
        // wrong-side flag indefinitely.
        private Agent.MovementControlFlag? SteerLiveSideCheck(float flatDist)
        {
            if (!_steerSparDef && !_steerGroup)
                return null;
            if (GroupNoFeint)
                return null;   // 4w: no feint redirect -- the swing releases on the chosen dir
            float bear = SteerTargetBearingRad();
            if (float.IsNaN(bear) || !SteerHeldDirStale(bear))
                return null;
            if (SteerSideAttackDir() == Agent.MovementControlFlag.None)
                return null;   // the needed side swing isn't in this weapon's set -- nothing better to redirect to
            Agent.MovementControlFlag oldDir = _attackDir;
            if (_feintChain < MaxFeintRedirects)
            {
                _feintPlanned = false;   // a live redirect replaces any queued feint
                EnterFeintCancel(first: false);
                if (_ssLogCount < MaxSsLogs)
                {
                    _ssLogCount++;
                    CombatAiLog.Log($"[SPARSTEER] Agent #{Agent.Index}: live side flip " +
                        $"target={(CombatLive(_duelTarget) ? "#" + _duelTarget.Index : "-")} " +
                        $"bear={bear * 57.2958f:F0} dir={oldDir}->{_attackDir} chain={_feintChain}/{MaxFeintRedirects}.");
                }
                return _attackDir | Agent.GetDefendMovementFlag() | WindupMoveFor(flatDist);
            }
            _duelState = DuelState.Release;
            _duelTimer = 0f;
            if (_ssLogCount < MaxSsLogs)
            {
                _ssLogCount++;
                CombatAiLog.Log($"[SPARSTEER] Agent #{Agent.Index}: live side flip " +
                    $"target={(CombatLive(_duelTarget) ? "#" + _duelTarget.Index : "-")} " +
                    $"bear={bear * 57.2958f:F0} dir={oldDir} redirect budget spent -> forced release.");
            }
            return WindupMoveFor(flatDist);
        }

        // ai39 steered defender, pre-ready gap: while the attack flag is
        // asserted but no Ready stage has latched yet (early windup, or the
        // feint re-ready gap), the HELD flag is what decides which direction
        // the engine readies -- so a stale side can be swapped here for free
        // (no block-tap redirect needed, no _feintChain spent). Dead-zone
        // bearings (<=20deg) leave the pick alone; an unsupported side leaves
        // the old flag alone too (nothing better to offer).
        private void SteerPreReadySideSync()
        {
            if (!_steerSparDef && !_steerGroup)
                return;
            Agent.MovementControlFlag side = SteerSideAttackDir();
            if (side == Agent.MovementControlFlag.None || side == _attackDir)
                return;
            Agent.MovementControlFlag oldDir = _attackDir;
            _attackDir = side;
            _lastAttackDir = side;
            if (_ssLogCount < MaxSsLogs)
            {
                _ssLogCount++;
                CombatAiLog.Log($"[SPARSTEER] Agent #{Agent.Index}: pre-ready side sync {oldDir}->{side}.");
            }
        }

        // The attack direction the defender's LIVE guard currently covers:
        // MirrorDefend maps incoming AttackLeft -> defender's DefendRight, so a
        // defender holding DefendRight covers our AttackLeft line, etc. Reads
        // their real input flags (works for the player's held block and for AI
        // defenders we drive ourselves) -- the honest "guard coverage" channel.
        private static Agent.MovementControlFlag CoveredAttackDir(Agent defender)
        {
            if (!CombatLive(defender))
                return Agent.MovementControlFlag.None;
            Agent.MovementControlFlag f = defender.MovementFlags;
            if ((f & Agent.MovementControlFlag.DefendRight) != 0) return Agent.MovementControlFlag.AttackLeft;
            if ((f & Agent.MovementControlFlag.DefendLeft) != 0) return Agent.MovementControlFlag.AttackRight;
            if ((f & Agent.MovementControlFlag.DefendUp) != 0) return Agent.MovementControlFlag.AttackUp;
            if ((f & Agent.MovementControlFlag.DefendDown) != 0) return Agent.MovementControlFlag.AttackDown;
            return Agent.MovementControlFlag.None;
        }

        // A held flag that never reached a Ready stage is the ground truth for
        // "this weapon can't do this direction" -- but ONLY if the body was
        // free. Staggers (Strike*), kicks, weapon swaps and attack actions are
        // excluded; Guard/Other/Idle/Defend* = free body, flag ignored. The duo
        // view-cone strip is also not the weapon's fault (we suppress the flag
        // ourselves) -- checked at the call site via InViewCone.
        private bool BodyWasFreeToReady()
        {
            Agent.ActionCodeType t = Agent.GetCurrentActionType(0);
            return t == Agent.ActionCodeType.Other || t == Agent.ActionCodeType.Idle || t == Agent.ActionCodeType.Guard
                || (t >= Agent.ActionCodeType.DefendAllBegin && t <= Agent.ActionCodeType.DefendAllEnd);
        }

        private void NoteDirectionFailed()
        {
            if (!BodyWasFreeToReady())
                return;
            if (CombatAiState.DuoAttackMode && ArenaDuoCoordinator.IsParticipant(Agent.Index))
            {
                if (ArenaDuoCoordinator.IsWaiting(Agent.Index))
                    return;   // reserve fighter -- we strip its attack flags ourselves
                Agent p = Agent.Mission?.MainAgent;
                Vec2 v;
                if (CombatLive(p)
                    && !InViewCone(p, ArenaDuoCoordinator.TryGetViewFacing(out v) ? v : ViewFacingOf(p)))
                    return;   // we stripped the flag ourselves (outside the player's view cone)
            }
            if ((_dirSuspect & _attackDir) != 0)
            {
                _dirUnsupported |= _attackDir;
                _dirSuspect &= ~_attackDir;
                if (_duelLogCount < MaxDuelLogs)
                {
                    _duelLogCount++;
                    MissionWeapon w = Agent.WieldedWeapon;
                    CombatAiLog.Log($"[DUEL] Agent #{Agent.Index} ({Agent.Name}): {_attackDir} never readied twice " +
                        $"(weapon '{(w.IsEmpty ? "fists" : (w.Item != null ? w.Item.StringId : "?"))}' usage " +
                        $"'{(w.IsEmpty || w.CurrentUsageItem == null ? "-" : w.CurrentUsageItem.ItemUsage)}') -> direction blacklisted for this weapon.");
                }
            }
            else
                _dirSuspect |= _attackDir;
        }

        // Direction support comes from the weapon's usage stats: AttackDown is
        // the thrust -- weapons with no thrust (axes etc.) have ThrustSpeed == 0.
        // Swings need SwingSpeed > 0. On top of that, directions the engine was
        // OBSERVED to ignore (the failsafe-learned blacklist) count as
        // unsupported -- stats alone lie for some usages.
        private bool IsDirectionSupported(Agent.MovementControlFlag dir)
        {
            MissionWeapon w = Agent.WieldedWeapon;
            if (w.Item != _dirWeapon)
            {
                _dirWeapon = w.Item;   // weapon swapped: the learned marks belong to the old one
                _dirSuspect = Agent.MovementControlFlag.None;
                _dirUnsupported = Agent.MovementControlFlag.None;
            }
            if (GroupUpDownOnly && (dir == Agent.MovementControlFlag.AttackLeft || dir == Agent.MovementControlFlag.AttackRight))
                return false;
            if ((_dirUnsupported & dir) != 0)
                return false;
            if (w.IsEmpty || w.CurrentUsageItem == null)
                return dir != Agent.MovementControlFlag.AttackDown; // bare fists: no thrust
            WeaponComponentData u = w.CurrentUsageItem;
            if (dir == Agent.MovementControlFlag.AttackDown)
                return u.ThrustSpeed > 0;
            return u.SwingSpeed > 0;
        }

        private float HorizontalDistanceTo(Agent other)
        {
            Vec3 d = Agent.Position - other.Position;
            d.z = 0f;
            return d.Normalize();
        }

        // Distance at which our swings can actually land: the wielded weapon's
        // real length (cm -> m) plus a lunge/step-in margin. ~1.5m for a 1h
        // sword, more for polearms. Never commit swings from beyond this --
        // whiffed swings can't defend (no block mid-Release) = free hits.
        private float MeleeReach() => MeleeReachOf(Agent);

        // Same reach math for ANOTHER agent -- used to gate defense: a threat
        // that can't physically reach us doesn't deserve a block.
        private static float MeleeReachOf(Agent a)
        {
            MissionWeapon w = a.WieldedWeapon;
            if (!w.IsEmpty && w.CurrentUsageItem != null)
            {
                float reach = w.CurrentUsageItem.WeaponLength / 100f + ReachMarginMeters;
                reach = MathF.Max(MinEngageRange, MathF.Min(reach, MaxEngageRange));
                // Mounted: distances are rider-center to rider-center, but the
                // strike origin rides ~1.5m ahead on the horse's head. Without
                // the extent two lancers' centers never come "in range" (ai49
                // -- AI sat inside tip range and never swung). Native engages
                // speed-scaled (aiComp+0x229c) -- the constant extent is the
                // approximation; tune from [CAV] logs.
                if (a.HasMount)
                    reach += 1.5f;
                return reach;
            }
            return 1.6f;
        }

        // Can this threat's swing actually land on us right now? Generous
        // margin -- step-in during the release closes real distance.
        private bool InThreatReach(Agent threat)
            => CombatLive(threat) && HorizontalDistanceTo(threat) <= MeleeReachOf(threat) + 0.6f;

        // Session 46b: the effective read on the CURRENT release. A swing
        // that appeared with no ready pose in the last ~0.1s is a chamber --
        // an attack materializing where a defend was expected -- and is
        // nearly unreadable by definition. The clamp also covers the frame-
        // ordering race: a defender's input callback can see the release
        // before the model's tick has recorded it, so LastReleaseRead may
        // still hold the PREVIOUS swing's (possibly high) value.
        private static float EffectiveReleaseRead()
        {
            float read = PlayerCombatModel.LastReleaseRead;
            if (PlayerCombatModel.SecondsSinceReadySeen > 0.10f)
                read = MathF.Min(read, 0.12f);
            return read;
        }

        // Session 46h: the camera-aim rating term. aimOffset is the signed yaw
        // from the player->us bearing to their LookDirection (>0 = aiming to
        // THEIR left of us -- same convention as PickDodgeAction). An angle
        // swing paired with the camera pushed to its side is the consistent,
        // expected release; an angle swing out of a square-on camera is the
        // surprise (aim and release disagree). Up/down swings carry no camera
        // signal -> neutral.
        private float CameraTerm(Agent player)
        {
            if (!CombatLive(player))
                return 0.5f;
            Vec3 toUs3 = Agent.Position - player.Position;
            toUs3.z = 0f;
            Vec2 toUs = new Vec2(toUs3.x, toUs3.y);
            Vec3 look3 = player.LookDirection;
            look3.z = 0f;
            Vec2 aim = new Vec2(look3.x, look3.y);
            float aimOffset = 0f;
            if (toUs.LengthSquared > 0.01f && aim.LengthSquared > 0.01f)
            {
                toUs.Normalize();
                aim.Normalize();
                float sin = toUs.x * aim.y - toUs.y * aim.x;
                aimOffset = MathF.Asin(MathF.Max(-1f, MathF.Min(1f, sin)));
            }
            else
                return 0.5f;
            Agent.UsageDirection d = player.GetCurrentActionDirection(1);
            if (d == Agent.UsageDirection.AttackLeft || d == Agent.UsageDirection.AttackRight)
            {
                // Same pairing PickDodgeAction rule 2 uses: AttackLeft releases
                // with the camera on their RIGHT (negative offset), AttackRight
                // with it on their LEFT (positive offset).
                bool consistent = (d == Agent.UsageDirection.AttackLeft && aimOffset < -ExpectAimRad)
                    || (d == Agent.UsageDirection.AttackRight && aimOffset > ExpectAimRad);
                if (consistent)
                    return 0.9f;
                if (MathF.Abs(aimOffset) < SquareOnAimRad)
                    return 0.2f;
                return 0.5f;
            }
            return 0.5f;
        }

        // =====================================================================
        // STEP DODGE -- the no-animation-assets evade (see const block above).
        // TryStartDodge picks the open-space direction and arms the step;
        // the ComputeDuelFlag early-return then teleports the agent along it
        // for ~0.3s while the matching move flag plays the leg gait.
        // =====================================================================

        // Per-frame step driver: writes the dodge direction into the REAL
        // locomotion input channel (MovementInputVector) instead of teleporting
        // -- the engine then plays the strafe/backpedal animation at matching
        // speed, so legs actually move (the old teleport slid the body without
        // animating them = the "ice skating" look). The raised speed cap from
        // TryStartDodge is what makes the step cover real distance; it must be
        // restored when the window ends.
        private Agent.MovementControlFlag TickDodgeStep(float dt)
        {
            _dodgeTime -= dt;
            // Rule 3 block-hold: keep the mirrored guard raised for the
            // window -- no body displacement at all, just the Defend flag.
            if (_dodgeBlockOnly)
                return _dodgeDefendFlag;
            // Back step + block combo (Session 29): a mirrored Defend flag
            // layered on top of the step's own movement/root-motion output --
            // Defend drives the arm/weapon guard, a different channel from
            // the body-translation channel (locomotion or clip root motion),
            // so the two compose cleanly. None for non-back dodges.
            if (_dodgeIsAnim)
            {
                // Clip path: root motion owns the body -- nothing to write
                // for movement, but the block combo (if any) still applies.
                if (_dodgeTime <= 0f && _dodgeSpeedRaised)
                {
                    _lastOp = "restoreSpeed(dodge)";
                    Agent.SetMaximumSpeedLimit(-1f, isMultiplier: false);
                    _dodgeSpeedRaised = false;
                }
                return _dodgeDefendFlag;
            }
            _lastOp = "MovementInputVector(dodge)";
            Agent.MovementInputVector = _dodgeDir;
            if (_dodgeTime <= 0f)
            {
                Agent.MovementInputVector = Vec2.Zero;
                if (_dodgeSpeedRaised)
                {
                    _lastOp = "restoreSpeed(dodge)";
                    Agent.SetMaximumSpeedLimit(-1f, isMultiplier: false);
                    _dodgeSpeedRaised = false;
                }
            }
            return DodgeMoveFlag() | _dodgeDefendFlag;
        }

        // Inside the group fight dodging is off -- every roll resolves to the
        // plain mirrored block (TryStartDodge's non-dodgeable path).
        private bool GroupNoDodge => CombatAiState.GroupFightMode;

        // 4w mode predicate (nofeint toggle): gtnative group fight, any
        // AI agent -- NOT _steerGroup-bound (an agent whose steer isn't
        // armed yet still must not feint). Quick only via _gfForceQuick.
        private bool GroupNoFeint => CombatAiState.GroupFightMode
            && CombatAiState.GroupNativeTarget && !Agent.IsMainAgent && CombatAiState.GfNofeint;

        // 4w gap predicate (gap toggle): the steered gtnative pair fight
        // meters attacks -- mandatory breathe + in-reach/in-front starts.
        private bool GroupGap => _steerGroup && CombatAiState.GroupNativeTarget && CombatAiState.GfGap;

        // 4w attack-start gate (gtnative group only, gap toggle): a swing
        // may only START with the pair basically in reach and roughly in
        // front -- otherwise stay in Approach instead of whiffing at a
        // target behind us.
        private bool GroupCanStartAttack(Agent target)
        {
            _gfDefBlocked = false;
            if (!GroupGap)
                return true;
            if (!CombatLive(target))
                return false;
            // 4x defender bias: swing only in the lull -- while a real
            // attacker-on-me is mid-swing at me, defending wins.
            if (_gfDefenderActive)
            {
                float lim = 1.5f * MeleeReach();
                for (int i = 0; i < _gfAtk.Count; i++)
                {
                    Agent a = _gfAtk[i];
                    if (!CombatLive(a) || (a.Position.AsVec2 - Agent.Position.AsVec2).Length > lim)
                        continue;
                    Agent.ActionStage st = a.GetCurrentActionStage(1);
                    if (st == Agent.ActionStage.AttackReady || st == Agent.ActionStage.AttackQuickReady
                        || st == Agent.ActionStage.AttackRelease)
                    {
                        _gfDefBlocked = true;
                        return false;
                    }
                }
            }
            // 4z: the strict gate (1.05r / dot>=0.7) is defender-only; a
            // non-defender swings as soon as the pair is roughly reachable.
            float reachLim = _gfDefenderActive ? GroupStartReachR : GroupStartReachRND;
            float dotLim = _gfDefenderActive ? GroupStartDot : GroupStartDotND;
            Vec2 to = target.Position.AsVec2 - Agent.Position.AsVec2;
            float d = to.Length;
            if (d > reachLim * MeleeReach())
                return false;
            Vec2 face = Agent.GetMovementDirection();
            if (face.LengthSquared < 0.0001f) face = Agent.LookDirection.AsVec2;
            if (face.LengthSquared < 0.0001f || d < 0.0001f)
                return true;   // can't read facing -> don't block the start
            return Vec2.DotProduct(FlatUnit(face), to / d) >= dotLim;
        }

        private bool TryStartDodge(Agent threat)
        {
            if (Agent.HasMount)
                return false; // no dodging on horseback (MasterStrikes gates this too)
            _dodgeDir = PickDodgeDirection(threat);
            _dodgeCooldown = DodgeCooldownSeconds;
            _dodgeIsAnim = false;
            _dodgeBlockOnly = false; // was never reset: a stale block-hold latch made later real dodges return only the defend flag

            // ANIMATED dodge (the MasterStrikes clips): the animation itself
            // carries root motion -- AnimFlags=0, no teleport, no speed cap,
            // no input vector. The clips are real footwork so it looks like a
            // dodge, not a fast strafe. Action names are registered globally
            // by the MasterStrikes module; act_none -> locomotion fallback.
            ActionIndexCache dodgeAct = PickDodgeAction(threat, out bool dodgeable);
            if (GroupNoDodge)
                dodgeable = false;   // group fight answers with blocks only
            if (dodgeable && dodgeAct != ActionIndexCache.act_none)
            {
                // Dodgeable attack + clip -> pure sidestep, never blocks.
                _dodgeIsBackStep = false;
                _lastOp = "SetActionChannel(dodge)";
                Agent.SetActionChannel(0, dodgeAct, false, (AnimFlags)0, 0f, DodgeAnimSpeed, -0.2f, 0.4f, 0f, false, -0.2f, 0, true);
                _dodgeIsAnim = true;
                _dodgeTime = DodgeAnimSeconds;
            }
            else if (dodgeable)
            {
                // Dodgeable attack but no clip -> locomotion step fallback.
                Vec2 faceAtStart = Agent.GetMovementDirection();
                if (faceAtStart.LengthSquared < 0.0001f)
                    faceAtStart = new Vec2(Agent.LookDirection.x, Agent.LookDirection.y);
                faceAtStart.Normalize();
                _dodgeIsBackStep = Vec2.DotProduct(_dodgeDir, faceAtStart) < -0.35f;
                _dodgeTime = DodgeDurationSeconds;
                // Raise the speed cap for the step window: locomotion at 2.5x
                // covers ~1.5-2m in 0.32s while still playing the real gait.
                _lastOp = "SetMaxSpeed(dodge)";
                Agent.SetMaximumSpeedLimit(DodgeSpeedMultiplier, isMultiplier: true);
                _dodgeSpeedRaised = true;
            }
            else
            {
                // RULE 3 (user spec): not a dodgeable case -> plain mirrored
                // BLOCK, no step, no clip (shared helper -- same block the
                // duel 60% branch uses).
                StartBlockOnly(threat);
            }
            // Session 29 back-step+block survives ONLY on the locomotion
            // fallback (no clips -> geometric classify); rule-3 blocks mirror
            // the threat's swing direction. Animated side dodges never block.
            _dodgeDefendFlag = _dodgeBlockOnly || _dodgeIsBackStep
                ? (threat != null ? BlockFlagVs(threat, threat.GetCurrentActionDirection(1)) : Agent.MovementControlFlag.None)
                : Agent.MovementControlFlag.None;
            if (_dodgeBlockOnly && _dodgeDefendFlag == Agent.MovementControlFlag.None)
                _dodgeDefendFlag = Agent.MovementControlFlag.DefendUp; // guard something rather than nothing
            // Only the animated path puts a "dodge"-named action on channel 0
            // -- remember it's OURS so IsForeignDodgePlaying doesn't misread
            // the clip's tail (which can outlive _dodgeTime) as a foreign
            // dodge and yield mid-recovery.
            _dodgeOwnedByUs = _dodgeIsAnim;
            // Any installed scripted pin anchors the body -- it would fight
            // the teleport, so the step clears it (same cleanup as a mode
            // switch). _sparPinSet/_legPinOwned flags: left as-is -- the spar
            // path re-evaluates blockLive every frame and reinstalls when the
            // step is done.
            if (_targetFrameSet)
            {
                _lastOp = "ClearTargetFrame(dodge)";
                Agent.ClearTargetFrame();
                _lastOp = "DisableScriptedMovement(dodge)";
                Agent.DisableScriptedMovement();
                _targetFrameSet = false;
                _sparPinSet = false;
                TorsoMoveCleanup();
            }
            // Session 31: [DODGE] gets its OWN counter -- it previously shared
            // the static _duelLogCount cap with feint spam, which exhausted
            // the 150-line budget every session and silently hid every dodge
            // (the log file showed zero [DODGE] lines even while dodges ran).
            if (_dodgeLogCount < MaxDodgeLogs)
            {
                _dodgeLogCount++;
                string actName = _dodgeBlockOnly ? "block" : (_dodgeIsAnim ? dodgeAct.GetName() : "locomotion-step");
                CombatAiLog.Log($"[DODGE] Agent #{Agent.Index} ({Agent.Name}): vs #{threat.Index} ({threat.Name}) " +
                    $"atk={threat.AttackDirection} act={actName} dir=({_dodgeDir.x:F2},{_dodgeDir.y:F2}) backstep={_dodgeIsBackStep} block={_dodgeDefendFlag}.");
            }
            CombatAiState.OurDodgeStartCount++;
            return true;
        }

        // Plain mirrored BLOCK hold -- no step, no clip. Zeroes _dodgeDir so
        // DodgeMoveFlag() contributes None (legs must not move), raises the
        // mirrored guard for DodgeBlockHoldSeconds so the incoming hit lands
        // on the block. Shared by TryStartDodge's non-dodgeable branch and
        // the duel 60%-block path.
        private void StartBlockOnly(Agent threat)
        {
            _dodgeBlockOnly = true;
            _dodgeDir = Vec2.Zero;
            _dodgeIsBackStep = false;
            _dodgeIsAnim = false;
            _dodgeOwnedByUs = false;
            _dodgeTime = DodgeBlockHoldSeconds;
            // A block holds its ground: any leftover target frame from an
            // earlier mode is cleared so nothing anchors the feet.
            if (_targetFrameSet)
            {
                _lastOp = "ClearTargetFrame(block)";
                Agent.ClearTargetFrame();
                Agent.DisableScriptedMovement();
                _targetFrameSet = false;
                _sparPinSet = false;
                TorsoMoveCleanup();
            }
            _dodgeDefendFlag = threat != null
                ? BlockFlagVs(threat, threat.GetCurrentActionDirection(1))
                : Agent.MovementControlFlag.None;
            if (_dodgeDefendFlag == Agent.MovementControlFlag.None)
                _dodgeDefendFlag = Agent.MovementControlFlag.DefendUp; // guard something rather than nothing
        }

        // Open-space direction pick: straight back from the threat is the
        // baseline (always safe vs the swing itself); the side steps win when
        // they'd land in emptier space AND when "back" is already crowded by
        // other attackers -- i.e. surrounded -> sidestep toward the gap.
        // CHARGING-ATTACKER bias: if the threat is closing fast (running at
        // us), a pure back-step loses the footrace -- they sprint past the
        // step and still land the hit. The real answer is stepping OFF-LINE
        // laterally so their forward momentum overshoots. Closing speed
        // scales a bonus to directions perpendicular to their approach.
        private Vec2 PickDodgeDirection(Agent threat)
        {
            Vec3 away3 = Agent.Position - threat.Position;
            away3.z = 0f;
            Vec2 away = new Vec2(away3.x, away3.y);
            if (away.LengthSquared < 0.01f)
            {
                Vec2 f = Agent.GetMovementDirection();
                away = f.LengthSquared > 0.0001f ? new Vec2(-f.x, -f.y) : new Vec2(0f, -1f);
            }
            away.Normalize();
            Vec2 left = new Vec2(-away.y, away.x);   // threat's-left = our right? no: rotate +90
            Vec2 right = new Vec2(away.y, -away.x);

            Vec2 back = away;
            Vec2 backLeft = (away + left); backLeft.Normalize();
            Vec2 backRight = (away + right); backRight.Normalize();

            // Spar 2-attacker rule (Session 40, user spec, FINAL): frame of
            // reference is the OTHER AI (the designated spar attacker) --
            // dodge is ALWAYS opposite its side. Direct index lookup
            // (CombatAiState.SparAttackerIndex), NOT a radius-gated scan --
            // the earlier FindSparAlly-based version could silently fail to
            // find the attacker (out of its search radius/relation gates)
            // and fall through to the generic open-space scorer below,
            // which IS heuristic/non-deterministic -- that silent fallback,
            // not genuine instability, is what looked "random". Knowing
            // exactly who the attacker is, a direct lookup can't miss.
            // cross>0 = attacker on our LEFT -> dodge RIGHT (opposite);
            // cross<0 -> dodge LEFT. Gated to the defender specifically
            // (_pureReactiveDefense is only ever set by
            // ComputeSparDefenderFlag) so duel/group and the spar attacker's
            // own occasional dodge are untouched.
            if (CombatAiState.SparMode && _pureReactiveDefense)
            {
                Agent otherAi = GetAgentByIndex(CombatAiState.SparAttackerIndex);
                if (CombatLive(otherAi))
                {
                    Vec3 f3 = Agent.LookDirection; f3.z = 0f;
                    Vec3 to3 = otherAi.Position - Agent.Position; to3.z = 0f;
                    if (f3.LengthSquared > 0.0001f && to3.LengthSquared > 0.0001f)
                    {
                        Vec2 f = new Vec2(f3.x, f3.y);
                        f.Normalize();
                        Vec2 to = new Vec2(to3.x, to3.y);
                        to.Normalize();
                        float cross = f.x * to.y - f.y * to.x; // >0 = other AI on our left
                        Vec2 facingLeft = new Vec2(-f.y, f.x);
                        Vec2 facingRight = new Vec2(f.y, -f.x);
                        Vec2 outer = cross >= 0f ? (away + facingRight) : (away + facingLeft);
                        outer.Normalize();
                        return outer;
                    }
                }
            }

            // How fast is the threat closing on us? MovementVelocity is
            // world-space; project onto the threat->us bearing.
            Vec2 threatVel = threat.MovementVelocity;
            float closing = Vec2.DotProduct(threatVel, new Vec2(-away.x, -away.y));
            float lateralBias = MathF.Max(0f, (closing - DodgeClosingBiasSpeed)) * 0.35f;

            // Score a candidate: crowding + lateral bonus (perpendicularity
            // to `away` -- a pure back step gets zero bonus, a side step the
            // most). Lower is better.
            float Score(Vec2 c)
            {
                float align = Vec2.DotProduct(c, away);          // 1=pure back, 0=lateral
                float perp = 1f - MathF.Abs(align);
                return CrowdingScore(c) - lateralBias * perp;
            }

            Vec2 best = back;
            float bestScore = Score(back) + 0.25f; // bias toward the pure back step
            float s;
            if ((s = Score(backLeft)) < bestScore) { bestScore = s; best = backLeft; }
            if ((s = Score(backRight)) < bestScore) { bestScore = s; best = backRight; }
            if ((s = Score(left)) < bestScore) { bestScore = s; best = left; }
            if ((s = Score(right)) < bestScore) { bestScore = s; best = right; }
            return best;
        }

        // How occupied a direction is: each nearby enemy projects a cone of
        // "presence" along its bearing from us; stepping into a bearing that
        // already holds an enemy crowds the exit. Score = sum of enemy
        // proximity weighted by alignment with the candidate direction.
        private float CrowdingScore(Vec2 dir)
        {
            Mission m = Agent.Mission;
            if (m == null)
                return 0f;
            float score = 0f;
            float rSq = DodgeOpennessRadius * DodgeOpennessRadius;
            foreach (Agent a in m.Agents)
            {
                if (a == Agent || !CombatLive(a) || !a.IsHuman)
                    continue;
                Vec3 to3 = a.Position - Agent.Position;
                Vec2 to = new Vec2(to3.x, to3.y);
                float dSq = to.LengthSquared;
                if (dSq > rSq || dSq < 0.001f)
                    continue;
                float d = MathF.Sqrt(dSq);
                float align = Vec2.DotProduct(dir, to / d); // -1..1
                if (align <= 0f)
                    continue;
                score += align * (1.5f - d / DodgeOpennessRadius); // closer enemies crowd more
            }
            return score;
        }

        // MasterStrikes dodge clips: 3 weapon-class sets x {right, left, back}.
        // Registered globally by the MS module's action_types/action_sets XML;
        // resolved lazily here because module load order isn't guaranteed.
        private static ActionIndexCache[] _dodgeActs;
        private static bool _dodgeActsResolved;

        private static void EnsureDodgeActions()
        {
            if (_dodgeActsResolved)
                return;
            _dodgeActsResolved = true;
            _dodgeActs = new ActionIndexCache[9]
            {
                ActionIndexCache.Create("act_1h_right_dodge"),
                ActionIndexCache.Create("act_1h_left_dodge"),
                ActionIndexCache.Create("act_1h_back_dodge"),
                ActionIndexCache.Create("act_2h_right_dodge"),
                ActionIndexCache.Create("act_2h_left_dodge"),
                ActionIndexCache.Create("act_2h_back_dodge"),
                ActionIndexCache.Create("act_other_2h_right_dodge"),
                ActionIndexCache.Create("act_other_2h_left_dodge"),
                ActionIndexCache.Create("act_other_2h_back_dodge"),
            };
        }

        // Which dodge clip -- or act_none when the attack isn't dodgeable.
        // User spec (Session 32 re-spec -- all the diagonal/back machinery is
        // gone):
        //   rule 1: UP/DOWN attacks -> dodge left or right (aim side, or the
        //           emptier side when square-on). Unchanged -- worked well.
        //   rule 2: AttackLeft aimed to our RIGHT -> step right (dirIdx 0);
        //           AttackRight aimed to our LEFT -> step left (dirIdx 1).
        //           The aim must exceed DodgeAimAngleRad (~34deg, raised from
        //           ~20) -- only clearly-aimed attacks count.
        //   rule 3: EVERYTHING ELSE (square-on L/R, null threat) -> act_none,
        //           and TryStartDodge turns that into a plain mirrored BLOCK:
        //           no step, no clip, no diagonal anything.
        // aimOffset = signed yaw from the attacker->us bearing to their
        // LookDirection; >0 means aiming to THEIR left of us.
        // NOTE on clip naming: if the L/R clips visually step the wrong way
        // in testing, flip the two `dirIdx` assignments -- the clip-side
        // convention is unverifiable without seeing the animation.
        private ActionIndexCache PickDodgeAction(Agent threat, out bool dodgeable)
        {
            EnsureDodgeActions();
            dodgeable = false;
            int dirIdx = -1;
            if (threat != null)
            {
                Vec3 toUs3 = Agent.Position - threat.Position;
                toUs3.z = 0f;
                Vec2 toUs = new Vec2(toUs3.x, toUs3.y);
                Vec3 look3 = threat.LookDirection;
                look3.z = 0f;
                Vec2 aim = new Vec2(look3.x, look3.y);
                float aimOffset = 0f;
                if (toUs.LengthSquared > 0.01f && aim.LengthSquared > 0.01f)
                {
                    toUs.Normalize();
                    aim.Normalize();
                    float sin = toUs.x * aim.y - toUs.y * aim.x;
                    aimOffset = MathF.Asin(MathF.Max(-1f, MathF.Min(1f, sin)));
                }
                bool aimLeft = aimOffset > DodgeAimAngleRad;
                bool aimRight = aimOffset < -DodgeAimAngleRad;
                switch (threat.AttackDirection)
                {
                    case Agent.UsageDirection.AttackLeft:
                        dirIdx = aimRight ? 0 : -1;   // rule 2, else block
                        break;
                    case Agent.UsageDirection.AttackRight:
                        dirIdx = aimLeft ? 1 : -1;    // rule 2, else block
                        break;
                    default:
                        // rule 1: UP/DOWN -> dodge toward the aim side.
                        // Square-on: mostly the emptier side, but ~40% flips
                        // to the OTHER side -- a deterministic pick was
                        // always right (same spacing answer), readable and
                        // pre-trackable by the player.
                        if (aimRight)
                            dirIdx = 0;
                        else if (aimLeft)
                            dirIdx = 1;
                        else
                        {
                            int open = OpenDodgeSide(threat);
                            dirIdx = DuelRng.NextDouble() < DodgeSquareOnFlipChance ? 1 - open : open;
                        }
                        break;
                }
            }
            if (dirIdx < 0)
                return ActionIndexCache.act_none; // rule 3: caller blocks instead
            // Spar defender (Session 41 -- direction fix; Session 42 -- criteria
            // fix; Session 43 -- angle-side gating): the dodgeable determination
            // above is IDENTICAL to duel mode (aim-gated L/R, always UP/DOWN,
            // act_none for square-on angle attacks -> plain mirrored block).
            // For a dodgeable attack we override the SIDE: deterministic,
            // OPPOSITE the designated AI attacker's side (direct index lookup,
            // same reference as the PickDodgeDirection rule; bypasses the
            // aim-side pick and the DodgeSquareOnFlipChance 40% random flip).
            // Session 43 (user spec): in duel rules an angle attack has exactly
            // ONE natural dodge side -- AttackLeft only ever dodges RIGHT
            // (dirIdx 0), AttackRight only LEFT (dirIdx 1). In spar only the
            // angle attack whose natural side IS the forced outer side may
            // evade; the other would step INTO the AI attacker's space, so it
            // falls through to act_none -> plain block. This mirrors the
            // attacker's own Rule-1 restriction symmetrically: the attacker is
            // forbidden to swing toward the player's side, and the player's
            // angle swing on the attacker-facing side is likewise blocked, not
            // dodged. dirIdx: 0=right clip, 1=left clip.
            if (CombatAiState.SparMode && _pureReactiveDefense)
            {
                int forced = SparForcedDodgeSide(out float cross);
                if (forced >= 0)
                {
                    bool isAngle = threat != null &&
                        (threat.AttackDirection == Agent.UsageDirection.AttackLeft ||
                         threat.AttackDirection == Agent.UsageDirection.AttackRight);
                    if (isAngle && dirIdx != forced)
                        return ActionIndexCache.act_none; // angle swing on the attacker side -> block
                    dirIdx = forced;
                    if (_dodgeLogCount < MaxDodgeLogs)
                    {
                        _dodgeLogCount++;
                        CombatAiLog.Log($"[DODGE] Agent #{Agent.Index} ({Agent.Name}): spar-clip side={(dirIdx == 1 ? "L" : "R")} (attacker on {(cross >= 0f ? "LEFT" : "RIGHT")}, cross={cross:F2}) vs #{threat?.Index ?? -1} atk={threat?.AttackDirection}.");
                    }
                }
            }
            dodgeable = true;
            return _dodgeActs[DodgeSetBase() + dirIdx];
        }

        // Spar defender only: which lateral dodge side is OUTER (away from the
        // designated AI attacker)? Returns the clip-side index (0 = right,
        // 1 = left) or -1 when this isn't the spar defender or the attacker
        // can't be resolved. Shared by the swing dodge (PickDodgeAction) and
        // the kick/bash evade (TryStartKickDodge) so every dodge flavor picks
        // the same deterministic side.
        private int SparForcedDodgeSide(out float cross)
        {
            cross = 0f;
            if (!CombatAiState.SparMode || !_pureReactiveDefense)
                return -1;
            Agent otherAi = GetAgentByIndex(CombatAiState.SparAttackerIndex);
            if (!CombatLive(otherAi))
                return -1;
            Vec3 f3 = Agent.LookDirection; f3.z = 0f;
            Vec3 to3 = otherAi.Position - Agent.Position; to3.z = 0f;
            if (f3.LengthSquared <= 0.0001f || to3.LengthSquared <= 0.0001f)
                return -1;
            Vec2 f = new Vec2(f3.x, f3.y); f.Normalize();
            Vec2 to = new Vec2(to3.x, to3.y); to.Normalize();
            cross = f.x * to.y - f.y * to.x; // >0 = attacker on our left
            return cross >= 0f ? 0 : 1; // attacker left -> RIGHT dodge clip; else LEFT
        }

        // Weapon-class -> dodge clip set: 1h base 0, 2h swords base 3,
        // other 2h base 6 (extracted from PickDodgeAction so the kick-evade
        // path resolves the same sets).
        private int DodgeSetBase()
        {
            MissionWeapon w = Agent.WieldedWeapon;
            WeaponComponentData item = w.CurrentUsageItem;
            if (item != null && item.WeaponClass == WeaponClass.TwoHandedSword)
                return 3;
            if (item != null && item.IsTwoHanded)
                return 6;
            return 0;
        }

        // Square-on overhead/thrust: pick whichever lateral side has more
        // room (CrowdingScore on both side bearings, lower wins).
        private int OpenDodgeSide(Agent threat)
        {
            Vec3 away3 = Agent.Position - threat.Position;
            away3.z = 0f;
            Vec2 away = new Vec2(away3.x, away3.y);
            if (away.LengthSquared < 0.01f)
                return 0;
            away.Normalize();
            Vec2 left = new Vec2(-away.y, away.x);
            Vec2 right = new Vec2(away.y, -away.x);
            return CrowdingScore(left) < CrowdingScore(right) ? 1 : 0;
        }

        // MasterStrikes' "surrounded" test: >=3 enemies and <3 allies nearby
        // -> the dodge must create space, so only the back step is sane.
        private bool CrowdedForDodge()
        {
            Mission m = Agent.Mission;
            if (m == null)
                return false;
            float rSq = DodgeOpennessRadius * DodgeOpennessRadius;
            int enemies = 0, allies = 0;
            foreach (Agent a in m.Agents)
            {
                if (a == Agent || !CombatLive(a) || !a.IsHuman || a.HasMount)
                    continue;
                if (a.Position.DistanceSquared(Agent.Position) > rSq)
                    continue;
                if (Agent.IsEnemyOf(a)) enemies++; else allies++;
            }
            return enemies >= 3 && allies < 3;
        }

        // The flag that plays the matching leg gait for the step so it reads
        // as a real strafe/backpedal, not a slide: resolve _dodgeDir against
        // our CURRENT leg facing into Strafe/Backward bits.
        private Agent.MovementControlFlag DodgeMoveFlag()
        {
            Vec2 f = Agent.GetMovementDirection();
            if (f.LengthSquared < 0.0001f)
                return Agent.MovementControlFlag.Backward;
            f.Normalize();
            Vec2 right = new Vec2(f.y, -f.x);
            float fComp = Vec2.DotProduct(_dodgeDir, f);
            float rComp = Vec2.DotProduct(_dodgeDir, right);
            Agent.MovementControlFlag flag = Agent.MovementControlFlag.None;
            if (fComp < -0.35f) flag |= Agent.MovementControlFlag.Backward;
            else if (fComp > 0.35f) flag |= Agent.MovementControlFlag.Forward;
            if (rComp > 0.35f) flag |= Agent.MovementControlFlag.StrafeRight;
            else if (rComp < -0.35f) flag |= Agent.MovementControlFlag.StrafeLeft;
            return flag;
        }

        // =====================================================================
        // KICK EVADE + PRESSURE (Session 34)
        // =====================================================================

        // Kick/bash answer: a hard LATERAL step only -- kicks are frontal, so
        // left and right both clear it. Openness pick coin-flipped so the
        // player can't pre-track (same trick as the square-on swing flip).
        // Session 44: for the spar defender there is no flip -- the side is
        // the deterministic outer side (away from the designated AI attacker)
        // via SparForcedDodgeSide, same rule as the swing dodge.
        // NO block flag: a kick breaks/staggers through guard. Shares the
        // TickDodgeStep fields/driver with the swing dodge.
        private bool TryStartKickDodge(Agent threat)
        {
            if (Agent.HasMount || GroupNoDodge)
                return false;
            _dodgeCooldown = DodgeCooldownSeconds;
            _dodgeIsAnim = false;
            _dodgeBlockOnly = false;
            _dodgeIsBackStep = false;
            _dodgeDefendFlag = Agent.MovementControlFlag.None; // never block a kick

            int dirIdx;
            int forcedKick = SparForcedDodgeSide(out _);
            if (forcedKick >= 0)
                dirIdx = forcedKick;
            else
            {
                int open = OpenDodgeSide(threat);
                dirIdx = DuelRng.NextDouble() < 0.5 ? 1 - open : open;
            }

            Vec3 away3 = Agent.Position - threat.Position;
            away3.z = 0f;
            Vec2 away = new Vec2(away3.x, away3.y);
            if (away.LengthSquared < 0.01f)
                away = new Vec2(0f, -1f);
            away.Normalize();
            _dodgeDir = dirIdx == 1 ? new Vec2(-away.y, away.x) : new Vec2(away.y, -away.x);

            EnsureDodgeActions();
            ActionIndexCache act = _dodgeActs[DodgeSetBase() + dirIdx];
            if (act != ActionIndexCache.act_none)
            {
                _lastOp = "SetActionChannel(kickdodge)";
                Agent.SetActionChannel(0, act, false, (AnimFlags)0, 0f, DodgeAnimSpeed, -0.2f, 0.4f, 0f, false, -0.2f, 0, true);
                _dodgeIsAnim = true;
                _dodgeTime = DodgeAnimSeconds;
            }
            else
            {
                // No clip -> locomotion lateral step (same fallback as swings).
                _dodgeTime = DodgeDurationSeconds;
                _lastOp = "SetMaxSpeed(kickdodge)";
                Agent.SetMaximumSpeedLimit(DodgeSpeedMultiplier, isMultiplier: true);
                _dodgeSpeedRaised = true;
            }
            _dodgeOwnedByUs = _dodgeIsAnim;
            if (_targetFrameSet)
            {
                _lastOp = "ClearTargetFrame(kickdodge)";
                Agent.ClearTargetFrame();
                Agent.DisableScriptedMovement();
                _targetFrameSet = false;
                _sparPinSet = false;
                TorsoMoveCleanup();
            }
            if (_dodgeLogCount < MaxDodgeLogs)
            {
                _dodgeLogCount++;
                CombatAiLog.Log($"[DODGE] Agent #{Agent.Index} ({Agent.Name}): vs #{threat.Index} ({threat.Name}) " +
                    $"kick-evade act={(_dodgeIsAnim ? act.GetName() : "locomotion-step")} side={(dirIdx == 1 ? "L" : "R")} dir=({_dodgeDir.x:F2},{_dodgeDir.y:F2}).");
            }
            CombatAiState.OurDodgeStartCount++;
            return true;
        }

        // Is the kicker actually aimed at us? Cheap facing gate -- in group
        // fights a kick pointed somewhere else shouldn't burn the roll.
        private bool KickAimedAtUs(Agent threat)
        {
            Vec3 lk = threat.LookDirection;
            Vec3 us = Agent.Position - threat.Position;
            lk.z = 0f;
            us.z = 0f;
            float lkLen = lk.Length, usLen = us.Length;
            if (lkLen < 0.1f || usLen < 0.1f)
                return true; // can't tell -> assume aimed
            return (lk.x * us.x + lk.y * us.y) / (lkLen * usLen) > KickFacingDot;
        }

        // "A few feet" -- the blade-length band: closer than this and the
        // swing lands handle-first (too close to hurt properly). ~half
        // weapon reach, floored so short weapons don't stack us on top.
        private float CloseBandRange() => MathF.Max(0.75f, MeleeReach() * 0.5f);

        // Nearest active human enemy (or null). Multi-agent combat target.
        private Agent FindNearestEnemy()
        {
            _lastOp = "scan:nearest";
            Mission m = Agent.Mission;
            if (m == null)
                return null;
            Agent best = null;
            float bestDist = float.MaxValue;
            foreach (Agent a in m.Agents)
            {
                if (a == null || a == Agent || !CombatLive(a) || !a.IsHuman)
                    continue;
                if (!Agent.IsEnemyOf(a))
                    continue;
                float d = Agent.Position.DistanceSquared(a.Position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = a;
                }
            }
            return best;
        }

        // The arena spar ATTACKER fights on the player's side of the drill
        // (both attack the defender) -- the player's swings are a teammate's,
        // not threats. Without this the enemy check below admits him anyway
        // (the AI really IS an enemy on paper; only our mode made it friendly)
        // and the "teammate" blocks/dodges the player's practice swings.
        private bool IsSparPlayerAlly
            => CombatAiState.SparMode && Agent.Index == CombatAiState.SparAttackerIndex;

        // Nearest enemy (EXCLUDING `exclude`) whose swing is in AttackRelease
        // (committed, about to land). Used for side-attack defense: block whoever
        // is actually about to hit us, not just our current target.
        private Agent FindThreateningAttackerExcluding(Agent exclude)
        {
            _lastOp = "scan:threat";
            Mission m = Agent.Mission;
            if (m == null)
                return null;
            Agent best = null;
            float bestDist = float.MaxValue;
            foreach (Agent a in m.Agents)
            {
                if (a == null || a == Agent || a == exclude || !CombatLive(a) || !a.IsHuman)
                    continue;
                if (a.IsMainAgent && IsSparPlayerAlly)
                    continue;
                if (!Agent.IsEnemyOf(a))
                    continue;
                if (a.GetCurrentActionStage(1) != Agent.ActionStage.AttackRelease)
                    continue;
                // RANGE GATE (Session 19 bug): a releasing attacker that can't
                // physically reach us is not a threat -- without this every
                // agent on the field flinches at every swing (observed: whole
                // 40-agent field "blocking" at once in group fights).
                float d = Agent.Position.DistanceSquared(a.Position);
                float reach = MeleeReachOf(a) + 1.0f;
                if (d > reach * reach)
                    continue;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = a;
                }
            }
            return best;
        }

        // Counts active human enemies within a practical engagement radius
        // (including `primary`, our chosen FSM target) -- used to decide
        // whether to use the simple always-locked 1v1 behavior or the
        // group-fight free-stance model (2+ nearby enemies).
        private const float GroupFightRadiusMeters = 10f;
        private int CountNearbyActiveEnemies(Agent primary)
        {
            _lastOp = "scan:count";
            Mission m = Agent.Mission;
            if (m == null)
                return 0;
            int count = 0;
            float radiusSq = GroupFightRadiusMeters * GroupFightRadiusMeters;
            foreach (Agent a in m.Agents)
            {
                if (a == null || a == Agent || !CombatLive(a) || !a.IsHuman)
                    continue;
                if (!Agent.IsEnemyOf(a))
                    continue;
                if (a != primary && Agent.Position.DistanceSquared(a.Position) > radiusSq)
                    continue;
                count++;
            }
            return count;
        }

        // FREE-STANCE FACING: writes the direction-to-target into the movement
        // direction channel every frame (SetMovementDirection reads/writes the
        // same _movementDirectionPointer the engine uses for the player's
        // camera-relative facing). This pins the LEGS' facing on the enemy
        // while the combat target stays released -- the torso is free to
        // snap-block, the body can't turn its back, and movement flags still
        // work relative to it (Forward closes, Strafe orbits laterally).
        private void KeepFacingOn(Agent target)
        {
            Vec3 toT = target.Position - Agent.Position;
            toT.z = 0f;
            if (toT.LengthSquared < 0.01f)
                return;
            Vec2 d = new Vec2(toT.x, toT.y);
            d.Normalize();
            _lastOp = "SetMovementDirection";
            Agent.SetMovementDirection(d);
        }

        // REVERTED to the plain movement lever only (no scripted frame): bias
        // the strafe-side pick toward wherever more nearby enemies are, using
        // the agent's current movement direction as "front". 1v1 (fewer than
        // 2 nearby enemies) stays pure random, exactly as before -- this only
        // nudges group-fight movement to keep the cluster roughly in view.
        private Agent.MovementControlFlag PreferredStrafeSide()
        {
            _lastOp = "scan:strafe";
            Agent.MovementControlFlag randomSide = DuelRng.Next(2) == 0
                ? Agent.MovementControlFlag.StrafeLeft
                : Agent.MovementControlFlag.StrafeRight;

            Mission m = Agent.Mission;
            if (m == null || CountNearbyActiveEnemies(null) < 2)
                return randomSide;

            Vec2 facing = Agent.GetMovementDirection();
            if (facing.LengthSquared < 0.0001f)
                return randomSide;
            facing.Normalize();
            Vec2 right = new Vec2(facing.y, -facing.x); // agent's right-hand side

            float leftWeight = 0f, rightWeight = 0f;
            float radiusSq = GroupFightRadiusMeters * GroupFightRadiusMeters;
            foreach (Agent a in m.Agents)
            {
                if (a == null || a == Agent || !CombatLive(a) || !a.IsHuman)
                    continue;
                if (!Agent.IsEnemyOf(a))
                    continue;
                Vec2 d = a.Position.AsVec2 - Agent.Position.AsVec2;
                if (d.LengthSquared < 0.0001f || d.LengthSquared > radiusSq)
                    continue;
                d.Normalize();
                if (Vec2.DotProduct(d, right) >= 0f) rightWeight += 1f; else leftWeight += 1f;
            }
            if (Math.Abs(leftWeight - rightWeight) < 0.5f)
                return randomSide;
            return rightWeight > leftWeight ? Agent.MovementControlFlag.StrafeRight : Agent.MovementControlFlag.StrafeLeft;
        }

        private Agent GetAgentByIndex(int index)
        {
            _lastOp = "scan:idx";
            if (index < 0)
                return null;
            Mission m = Agent.Mission;
            if (m == null)
                return null;
            foreach (Agent a in m.Agents)
                if (Live(a) && a.Index == index)
                    return a;
            return null;
        }

        // =====================================================================
        // RIGGED SPAR (user design, 2v1 geometry):
        //   ATTACKER holds a fixed station on the DEFENDER'S LEFT-FRONT (~40deg
        //   off the defender's facing, at weapon reach) and fights from inside
        //   the defender's view cone -- never circling behind, never leaving
        //   its sight. Steering uses PLAIN movement flags (Forward/Backward/
        //   StrafeLeft/StrafeRight from MoveMask): the attacker is target-
        //   locked on the defender so Forward = closer and StrafeRight orbits
        //   toward the defender's left. Combos (Forward|Strafe*) give diagonal
        //   movement -- the normal lever, no scripted-frame weave.
        //   DEFENDER pins its legs facing the PLAYER (biased ~20deg toward the
        //   attacker side so the attacker sits inside the ~90deg torso arc,
        //   not on its edge), rests its combat target on the player, and
        //   snap-blocks whichever threat actually releases a swing.
        // =====================================================================
        private const float SparSlotAngleRadians = 0.52f;  // ~30deg each side of defender facing -- inside the view cone, not on its edge
        private const float SparSlotArriveMeters = 0.35f;  // close enough to slot = stop
        private const float SparSlotDirThreshold = 0.35f;  // component threshold -> flag
        private const float SparThreatRadiusMeters = 7f;

        // The duel FSM decides WHAT to do (attack/feint/block); this wrapper
        // decides WHERE to stand: locomotion bits are stripped from the FSM
        // output and replaced with steering toward the left-front slot. Combat
        // bits (attack/defend) pass straight through.
        private Agent.MovementControlFlag ComputeSparAttackerFlag(float dt)
        {
            _lastOp = "sparAtk:enter";
            Agent.MovementControlFlag fsm = ComputeDuelFlag(dt);
            Agent defender = GetAgentByIndex(CombatAiState.SparDefenderIndex);
            if (!CombatLive(defender) || _dodgeTime > 0f)
                return fsm;

            Agent.MovementControlFlag combat = fsm & ~Agent.MovementControlFlag.MoveMask;
            // Strike only from INSIDE the defender's front cone (~+/-80deg):
            // the defender rig is built on keeping both threats in view, so a
            // swing launched from outside the cone is unreadable cheese and
            // breaks the block-them-both exercise. Outside the cone we keep
            // steering for whichever front slot is closest instead.
            if (!InDefenderViewCone(defender))
                combat &= ~AttackBits;
            return combat | SparSlotMoveFlags(defender);
        }

        // Plain-flag steering toward the slot: slotDir = defender's facing
        // rotated SparSlotAngleRadians toward its LEFT, at ~weapon reach from
        // the defender. toSlot is then expressed in the attacker's local axes
        // (facing ~= toward the defender while target-locked) and thresholded
        // into Forward/Backward/StrafeLeft/StrafeRight.
        private Agent.MovementControlFlag SparSlotMoveFlags(Agent defender)
        {
            Vec3 toDef = defender.Position - Agent.Position;
            toDef.z = 0f;
            if (toDef.LengthSquared < 0.01f)
                return Agent.MovementControlFlag.None;
            toDef.Normalize();

            Vec2 defFacing = DefenderFacing(defender);
            if (defFacing.LengthSquared < 0.0001f)
                defFacing = new Vec2(-toDef.x, -toDef.y); // fallback: defender faces us
            defFacing.Normalize();

            // Dual-side slots (Session 19): left-front AND right-front mirror
            // slots at +/-SparSlotAngleRadians. Take whichever is closer, with
            // hysteresis so the choice doesn't oscillate at the dead-ahead
            // seam -- a dislodged attacker re-slots from the near side instead
            // of circling the whole way back to the defender's left.
            float dL = (SparSlotPos(defender, defFacing, 1) - Agent.Position.AsVec2).LengthSquared;
            float dR = (SparSlotPos(defender, defFacing, -1) - Agent.Position.AsVec2).LengthSquared;
            // Session 62: while a rigged 2v1 runs, slot to the side OPPOSITE
            // the player (bearing > 20deg, 1.0s hysteresis); dead-ahead or no
            // player -> existing nearest-slot logic.
            Agent pAgent = Agent.Mission?.MainAgent;
            bool rigOn = CombatAiState.SparRig && CombatLive(pAgent);
            // SPARFREE slot anchor (B3): the defender's facing is now the
            // MEDIAN of {attacker, player} -- anchoring the slot on it feeds
            // back (slot -> attacker pos -> median -> slot). Anchor on the
            // defender->PLAYER bearing rotated by side*SparSpreadDeg instead;
            // side = the attacker's current side of that line (>=1.0s
            // hysteresis, |bearing|<15deg keeps the previous side). Fallback:
            // no live player -> the old facing-anchored behavior.
            bool freeDef = CombatAiState.SparMode && CombatAiState.SparFree
                && !defender.HasMount;
            Vec2 slotPos;
            Vec2 slotAnchor = Vec2.Zero;   // defender->slot dir (ring math)
            float angFromFacing = -1f;
            float spreadToPlayer = -1f;
            bool saRing = false;
            if (freeDef && CombatLive(pAgent))
            {
                Vec2 toP = pAgent.Position.AsVec2 - defender.Position.AsVec2;
                Vec2 toA = Agent.Position.AsVec2 - defender.Position.AsVec2;
                if (toP.LengthSquared > 0.0001f && toA.LengthSquared > 0.0001f)
                {
                    Vec2 pDir = FlatUnit(toP);
                    Vec2 radial = FlatUnit(toA);
                    float cross = pDir.x * radial.y - pDir.y * radial.x;
                    float dot = pDir.x * radial.x + pDir.y * radial.y;
                    spreadToPlayer = MathF.Abs(MathF.Atan2(cross, dot)) * 57.2958f;
                    int wSide = spreadToPlayer >= 15f
                        ? (cross > 0f ? 1 : -1)
                        : (_sparSlotSide != 0 ? _sparSlotSide : 1);
                    if (wSide != _sparSlotSide && _brainClock - _slotSideSince >= 1.0f)
                    {
                        _sparSlotSide = wSide;
                        _slotSideSince = _brainClock;
                    }
                    else if (wSide == _sparSlotSide)
                        _slotSideSince = _brainClock;
                    if (_sparSlotSide == 0)
                        _sparSlotSide = wSide;
                    float sa = _sparSlotSide * CombatAiState.SparSpreadDeg * 0.0174533f;
                    float sc = MathF.Cos(sa), ss = MathF.Sin(sa);
                    slotAnchor = new Vec2(pDir.x * sc - pDir.y * ss, pDir.x * ss + pDir.y * sc);
                    float slotDist = Math.Max(1.30f, MeleeReach() * 0.85f);
                    slotPos = defender.Position.AsVec2 + slotAnchor * slotDist;
                    float fc = defFacing.x * radial.y - defFacing.y * radial.x;
                    float fd = defFacing.x * radial.x + defFacing.y * radial.y;
                    angFromFacing = MathF.Abs(MathF.Atan2(fc, fd)) * 57.2958f;
                }
                else
                    slotPos = SparSlotPos(defender, defFacing, _sparSlotSide != 0 ? _sparSlotSide : 1);
            }
            else
            {
                int pSide = 0;
                if (rigOn)
                {
                    Vec2 toP = pAgent.Position.AsVec2 - defender.Position.AsVec2;
                    float cross = defFacing.x * toP.y - defFacing.y * toP.x;
                    float dot = defFacing.x * toP.x + defFacing.y * toP.y;
                    float bear = MathF.Abs(MathF.Atan2(cross, dot)) * 57.2958f;
                    if (bear > 20f)
                        pSide = cross > 0f ? 1 : -1;   // player on the defender's LEFT => +1
                }
                // Stability clock FIRST: a side change only marks "first seen",
                // the slot flips once the side has been stable >= 1.0s.
                if (pSide != 0 && pSide != _lastPSide) { _lastPSide = pSide; _slotSideSince = _brainClock; }
                else if (pSide == 0) _lastPSide = 0;
                if (_sparSlotSide == 0)
                    _sparSlotSide = pSide != 0 ? -pSide : (dL <= dR ? 1 : -1);
                else if (pSide != 0 && _brainClock - _slotSideSince >= 1.0f && _sparSlotSide != -pSide)
                    _sparSlotSide = -pSide;
                else if (pSide == 0 && _sparSlotSide > 0 && dR + 0.25f < dL)
                    _sparSlotSide = -1;
                else if (pSide == 0 && _sparSlotSide < 0 && dL + 0.25f < dR)
                    _sparSlotSide = 1;

                slotPos = SparSlotPos(defender, defFacing, _sparSlotSide);
            }
            // Session 63: a slot parked inside a nav pocket (off-mesh corner,
            // obstacle shadow) permanently stalls the approach -- check the
            // slot POINT's path legality (GetPathDistanceToPoint, >=1e5 = no
            // path, throttled 0.2s) and pull the slot 30% toward the defender
            // before giving up on it.
            if (rigOn && !_slotPathDead)
            {
                if (_brainClock - _slotPathAt >= 0.2f)
                {
                    _slotPathAt = _brainClock;
                    try
                    {
                        Vec3 sp = new Vec3(slotPos.x, slotPos.y, Agent.Position.z);
                        float pd = Agent.GetPathDistanceToPoint(ref sp);
                        _slotPathOk = float.IsNaN(pd) || pd < 100000f;
                    }
                    catch (Exception e)
                    {
                        _slotPathDead = true;
                        _slotPathOk = true;
                        if (_rigWallLogCount < MaxRigWallLogs)
                        {
                            _rigWallLogCount++;
                            CombatAiLog.Log($"[RIGWALL] Agent #{Agent.Index}: slot path probe DISABLED ({e.GetType().Name})");
                        }
                    }
                }
                if (!_slotPathOk)
                    slotPos += (defender.Position.AsVec2 - slotPos) * 0.3f;
            }
            Vec2 toSlot = slotPos - Agent.Position.AsVec2;
            // SPARFREE ring steering (B4): a straight run at the slot that
            // sweeps behind the defender (radial vs slot-anchor > 75deg, i.e.
            // the chord passes within ~1.1m of the defender) is replaced by
            // the ring tangent that reduces the angle the SHORT way -- never
            // the arc that passes behind it. Behind already (>90deg from
            // facing): same tangent picks the shorter way around; the view
            // gate keeps it from attacking until back in front.
            if (freeDef && slotAnchor.LengthSquared > 0.0001f && toSlot.LengthSquared > 0.0001f)
            {
                Vec2 radial2 = FlatUnit(Agent.Position.AsVec2 - defender.Position.AsVec2);
                if (radial2.LengthSquared > 0.0001f)
                {
                    float rc = radial2.x * slotAnchor.y - radial2.y * slotAnchor.x;
                    float rd = radial2.x * slotAnchor.x + radial2.y * slotAnchor.y;
                    if (MathF.Abs(MathF.Atan2(rc, rd)) * 57.2958f > 75f)
                    {
                        saRing = true;
                        toSlot = rc > 0f ? new Vec2(-radial2.y, radial2.x)
                                         : new Vec2(radial2.y, -radial2.x);
                    }
                }
            }
            // Session 62 wall veto + round-4 clearance: a blocked slot dir ->
            // steer at the defender; then either perpendicular; else None.
            if (rigOn && toSlot.LengthSquared > 0.0001f)
            {
                Vec2 sDir = toSlot;
                sDir.Normalize();
                Vec2 dDir2 = defender.Position.AsVec2 - Agent.Position.AsVec2;
                if (dDir2.LengthSquared > 0.0001f)
                    dDir2.Normalize();
                if (AtkDirOk(sDir, toSlot.Length))
                {
                }
                else if (AtkDirOk(dDir2, dDir2.LengthSquared > 0.0001f ? (defender.Position.AsVec2 - Agent.Position.AsVec2).Length : 1f))
                    toSlot = dDir2;
                else
                {
                    Vec2 pL = new Vec2(-sDir.y, sDir.x), pR = new Vec2(sDir.y, -sDir.x);
                    bool lOk = AtkDirOk(pL, 1f), rOk = AtkDirOk(pR, 1f);
                    if (lOk && rOk)
                        toSlot = _atkClear.Free(Agent, pL, _brainClock) >= _atkClear.Free(Agent, pR, _brainClock) ? pL : pR;
                    else if (lOk) toSlot = pL;
                    else if (rOk) toSlot = pR;
                    else return Agent.MovementControlFlag.None;
                }
            }
            // Never park outside our own reach: the duel FSM only initiates
            // while flatDist < MeleeReach(), so a slot that lands just beyond
            // it is a permanent stall (the "AI watches until the player moves"
            // report). While we can't reach the defender, skip the arrive
            // radius and walk straight in.
            if ((defender.Position.AsVec2 - Agent.Position.AsVec2).Length > MeleeReach() - 0.10f)
                toSlot = defender.Position.AsVec2 - Agent.Position.AsVec2;
            else if (toSlot.LengthSquared < SparSlotArriveMeters * SparSlotArriveMeters)
                return Agent.MovementControlFlag.None;
            toSlot.Normalize();

            Vec2 fwd = new Vec2(toDef.x, toDef.y);       // attacker faces the defender
            Vec2 right = new Vec2(fwd.y, -fwd.x);        // attacker's right-hand side
            float fComp = Vec2.DotProduct(toSlot, fwd);
            float rComp = Vec2.DotProduct(toSlot, right);
            // Round-4 watchdog: feed the actual commanded dir each call.
            if (rigOn)
            {
                Vec2 cmd = toSlot.LengthSquared > 0.0001f ? toSlot : Vec2.Zero;
                Agent.ActionStage st0 = Agent.GetCurrentActionStage(1);
                bool ex = st0 == Agent.ActionStage.AttackRelease || _dodgeTime > 0f || st0 == Agent.ActionStage.DefendParry;
                _atkClear.Tick(Agent, _brainClock);
                _atkWatch.Note(Agent.Position.AsVec2, cmd, _brainClock, ex, out Vec2 _);
            }

            Agent.MovementControlFlag flags = Agent.MovementControlFlag.None;
            if (fComp > SparSlotDirThreshold) flags |= Agent.MovementControlFlag.Forward;
            else if (fComp < -SparSlotDirThreshold) flags |= Agent.MovementControlFlag.Backward;
            if (rComp > SparSlotDirThreshold) flags |= Agent.MovementControlFlag.StrafeRight;
            else if (rComp < -SparSlotDirThreshold) flags |= Agent.MovementControlFlag.StrafeLeft;

            if (freeDef && (defender.Position.AsVec2 - Agent.Position.AsVec2).Length < 8f
                && _saLogCount < MaxSaLogs && _brainClock - _saLastLogAt >= 0.5f)
            {
                _saLastLogAt = _brainClock;
                _saLogCount++;
                CombatAiLog.Log($"[SPARATK] Agent #{Agent.Index}: slotSide={_sparSlotSide} " +
                    $"angFromFacing={angFromFacing:F0} spreadToPlayer={spreadToPlayer:F0} " +
                    $"gate={(InDefenderViewCone(defender) ? "inView" : "outView")} " +
                    $"ring={(saRing ? "yes" : "no")} behind={(angFromFacing > 90f ? "yes" : "no")}");
            }
            return flags;
        }

        // Round-4: is a world dir free for the rigged attacker (learned blocks
        // + clearance probe)? The required margin scales with how far we want
        // to go -- a slot/defender parked inside ~1m ahead must not read as a
        // wall and stall the approach.
        private bool AtkDirOk(Vec2 dir, float goalDist)
        {
            if (dir.LengthSquared < 0.0001f || _atkWatch.IsBlocked(dir, _brainClock))
                return false;
            float need = MathF.Min(1.0f, MathF.Max(0.35f, goalDist * 0.8f));
            return _atkClear.Free(Agent, dir, _brainClock) >= need;
        }

        // Front-slot position: defender facing rotated +/-SparSlotAngleRadians
        // (side +1 = its left, -1 = its right), at ~weapon reach.
        private Vec2 SparSlotPos(Agent defender, Vec2 defFacing, int side)
        {
            float ang = SparSlotAngleRadians * side;
            float c = MathF.Cos(ang);
            float s = MathF.Sin(ang);
            Vec2 slotDir = new Vec2(defFacing.x * c - defFacing.y * s,
                                    defFacing.x * s + defFacing.y * c);
            // 0.85x reach + a 1.30 floor: a touch more stand-off than the old
            // max(1.15, 0.75x) while still INSIDE MeleeReach() even for short
            // 1h weapons -- a slot parked past its own swing range is a
            // permanent stall (no INIT path can ever fire).
            float slotDist = Math.Max(1.30f, MeleeReach() * 0.85f);
            return defender.Position.AsVec2 + slotDir * slotDist;
        }

        // The defender's body facing -- for a mounted defender that lives on
        // the horse, not the rider.
        internal static Vec2 DefenderFacing(Agent defender)
        {
            return (defender.HasMount && defender.MountAgent != null)
                ? defender.MountAgent.GetMovementDirection()
                : defender.GetMovementDirection();
        }

        // What the player is LOOKING at (camera yaw), not just which way the body is moving --
        // the 2v1 attackers stay inside this view. Falls back to the body facing when the
        // look vector is (near) vertical.
        internal static Vec2 ViewFacingOf(Agent a)
        {
            Vec3 l = a.LookDirection;
            Vec2 v = new Vec2(l.x, l.y);
            return v.LengthSquared < 0.0001f ? DefenderFacing(a) : v;
        }

        // Is our agent inside the defender's front cone (~+/-80deg)?
        private bool InDefenderViewCone(Agent defender)
        {
            Vec3 toMe = Agent.Position - defender.Position;
            toMe.z = 0f;
            if (toMe.LengthSquared < 0.01f)
                return true;
            toMe.Normalize();
            Vec2 f = DefenderFacing(defender);
            if (f.LengthSquared < 0.0001f)
                return true;
            f.Normalize();
            // Sparfree defender: the attack cone is a knob (default +/-55deg);
            // legacy callers/defenders keep the old ~80deg threshold.
            float need = (CombatAiState.SparMode && CombatAiState.SparFree && !defender.HasMount)
                ? MathF.Cos(CombatAiState.SparAttackViewDeg * 0.0174533f) : 0.17f;
            return Vec2.DotProduct(f, new Vec2(toMe.x, toMe.y)) > need;
        }

        // =====================================================================
        // ARENA DEFEND-2v1 (ArenaOverhaul menu -> DuoAttackMode):
        // BOTH attackers run the full duel FSM against the player (aggressive duo
        // tuning profile, pincer-timed starts) -- there is no waiting supporter.
        // Same wrapper shape as ComputeSparAttackerFlag: the FSM decides WHAT to do
        // (attack / feint / hold / block), its locomotion bits are stripped, and the
        // coordinator-assigned front slot (player's left or right, +/-40 degrees
        // off the player's view) decides WHERE to stand. Swings only start from
        // inside the player's view cone and never sweep toward the partner
        // (AllyForbiddenAttackDir).
        // =====================================================================
        private const float DuoSlotArriveMeters = 0.40f;   // close enough to the slot = stop
        private const float DuoSlotResumeMeters = 0.85f;  // ...and only walk again once pushed this far off (no jitter)
        private const float DuoSeparationMeters = 1.2f;   // keep the two attackers at least this far apart
        private bool _duoSteering;
        private float _drawRetryAt;

        // ARENA STALL WATCHDOG. Beyond the leash the base game owns the body, and
        // in the arena its pathing can simply give up (the fighter stands in a
        // Guard pose with no movement at all -- seen near the arena gate). While an
        // arena fight is armed: if the fighter is outside the leash yet moved less
        // than StallMoveMeters in StallWindowSeconds, drive the approach ourselves
        // for StallForceSeconds (plain Forward/strafe flags toward the locked
        // player) instead of handing the body back to native.
        private const float StallWindowSeconds = 1.5f;
        private const float StallMoveMeters = 0.35f;
        private const float StallForceSeconds = 3f;
        private const int MaxStallLogs = 80;
        private static int _stallLogCount;
        private bool _stallArmed;
        private float _stallT;
        private Vec2 _stallPos;
        private float _forceDriveUntil = -1f;

        private bool ArenaStallWatch(float dt, bool outsideLeash, Agent target, float flatDist)
        {
            if (!CombatAiState.ArenaFightActive || Agent.Mission == null)
                return false;
            float now = Agent.Mission.CurrentTime;
            if (now < _forceDriveUntil)
                return true;
            if (!outsideLeash)
            {
                _stallArmed = false;
                return false;
            }
            Vec2 p = Agent.Position.AsVec2;
            if (!_stallArmed)
            {
                _stallArmed = true;
                _stallT = 0f;
                _stallPos = p;
                return false;
            }
            _stallT += dt;
            if (_stallT < StallWindowSeconds)
                return false;
            float moved = (p - _stallPos).Length;
            _stallT = 0f;
            _stallPos = p;
            if (moved >= StallMoveMeters)
                return false;
            _forceDriveUntil = now + StallForceSeconds;
            if (_stallLogCount < MaxStallLogs)
            {
                _stallLogCount++;
                Vec2 tp = target != null ? target.Position.AsVec2 : Vec2.Zero;
                CombatAiLog.Log($"[STALL] Agent #{Agent.Index} ({Agent.Name}) moved {moved:F2}m in {StallWindowSeconds:F1}s outside the leash " +
                    $"(dist={flatDist:F1}m, pos=({p.x:F1},{p.y:F1}), target #{(target != null ? target.Index : -1)} at ({tp.x:F1},{tp.y:F1}), " +
                    $"act={Agent.GetCurrentActionType(0)}) -> native isn't moving us; driving the approach ourselves for {StallForceSeconds:F0}s.");
            }
            return true;
        }

        private Agent.MovementControlFlag ComputeDuoAttackerFlag(float dt)
        {
            _lastOp = "duo:enter";
            Agent.MovementControlFlag fsm = ComputeDuelFlag(dt);
            Agent player = Agent.Mission?.MainAgent;
            if (!CombatLive(player) || _dodgeTime > 0f)
                return fsm;

            Agent.MovementControlFlag combat = fsm & ~Agent.MovementControlFlag.MoveMask;
            // Queue discipline: AO keeps backfilling _participantAgents, so the
            // roster can outgrow the intended pair (6+ joined at once in a log).
            // Only the two slotted attackers fight -- waiters hold their spot
            // with guard up (defense bits pass through, attack bits stripped,
            // no slot to steer to) and step in when a side frees.
            if (ArenaDuoCoordinator.IsWaiting(Agent.Index))
                return combat & ~AttackBits;
            Vec2 view;
            if (!ArenaDuoCoordinator.TryGetViewFacing(out view))
                view = ViewFacingOf(player);
            if (!InViewCone(player, view))
                combat &= ~AttackBits;
            return combat | DuoSlotMoveFlags(player, view, dt);
        }

        private bool InViewCone(Agent defender, Vec2 facing)
        {
            Vec3 toMe = Agent.Position - defender.Position;
            toMe.z = 0f;
            if (toMe.LengthSquared < 0.01f || facing.LengthSquared < 0.0001f)
                return true;
            toMe.Normalize();
            facing.Normalize();
            return Vec2.DotProduct(facing, new Vec2(toMe.x, toMe.y)) > 0.17f;
        }

        // Arena fighters are spawned with weapons the native AI only draws once it
        // decides to fight (the vanilla controller wields initial weapons for the
        // player alone) -- a driven fighter that never unsheathes can't run the duel FSM.
        private void EnsureArenaWeaponDrawn()
        {
            if (!CombatAiState.ArenaFightActive || WieldsMeleeWeapon())
                return;
            float now = Agent.Mission != null ? Agent.Mission.CurrentTime : 0f;
            if (now < _drawRetryAt || Agent.Mission == null || !Agent.IsActive())
                return;
            _drawRetryAt = now + 1f;
            _lastOp = "arena:wield";
            Agent.WieldInitialWeapons(Agent.WeaponWieldActionType.WithAnimation, Equipment.InitialWeaponEquipPreference.MeleeForMainHand);
        }

        // Plain-flag steering toward this attacker's assigned slot (the coordinator
        // owns which side; the slot itself is the spar front slot built on the
        // player's smoothed view). Hysteresis on arrive/resume radii plus a
        // separation push from the partner keep the pair from overlapping or
        // twitching between directions. Same local-axes projection as
        // SparSlotMoveFlags -- facing is toward the player while target-locked.
        private bool _slotWatchArmed;
        private float _slotWatchT;
        private Vec2 _slotWatchPos;
        private float _slotRelaxUntil = -1f;

        private Agent.MovementControlFlag DuoSlotMoveFlags(Agent player, Vec2 view, float dt)
        {
            int side = ArenaDuoCoordinator.SideOf(Agent.Index);
            if (side == 0 || view.LengthSquared < 0.0001f)
                return Agent.MovementControlFlag.None;
            view.Normalize();

            Vec3 toPlayer = player.Position - Agent.Position;
            toPlayer.z = 0f;
            if (toPlayer.LengthSquared < 0.01f)
                return Agent.MovementControlFlag.None;
            toPlayer.Normalize();

            Vec2 me = Agent.Position.AsVec2;
            Vec2 slotPos = SparSlotPos(player, view, side);
            float nowT = Agent.Mission != null ? Agent.Mission.CurrentTime : 0f;
            if (nowT < _slotRelaxUntil)
                slotPos = player.Position.AsVec2 + (slotPos - player.Position.AsVec2) * 0.55f;
            Vec2 toSlot = slotPos - me;

            // Never park outside our own reach: every duel-FSM INIT path is
            // gated on flatDist < MeleeReach(), so a parked spot just beyond it
            // stalls the attacker until the player moves and drags the slot.
            // While the player is out of reach, skip the deadband and head
            // straight in -- this is also what removes the visible
            // stop/start "border" where the parked circle ended past reach.
            bool outOfReach = (player.Position.AsVec2 - me).Length > MeleeReach() - 0.10f;
            if (outOfReach)
            {
                _duoSteering = true;
                toSlot = player.Position.AsVec2 - me;
            }
            else
            {
                Agent ally = ArenaDuoCoordinator.GetPartnerAgent(Agent.Index, Agent.Mission);
                if (CombatLive(ally))
                {
                    Vec2 away = me - ally.Position.AsVec2;
                    float d = away.Length;
                    if (d > 0.01f && d < DuoSeparationMeters)
                        toSlot += away * ((DuoSeparationMeters - d) * 2f / d);
                }
            }

            float dist = toSlot.Length;
            if (!outOfReach && (_duoSteering ? dist < DuoSlotArriveMeters : dist > DuoSlotResumeMeters))
                _duoSteering = !_duoSteering;
            if (!_duoSteering)
            {
                _slotWatchArmed = false;
                return Agent.MovementControlFlag.None;
            }

            // Walking at the slot but going nowhere (slot beyond a gate/boundary the
            // agent can't cross): pull the slot toward the player for a few seconds.
            if (!_slotWatchArmed)
            {
                _slotWatchArmed = true;
                _slotWatchT = 0f;
                _slotWatchPos = me;
            }
            else if ((_slotWatchT += dt) >= StallWindowSeconds && nowT >= _slotRelaxUntil)
            {
                float moved = (me - _slotWatchPos).Length;
                _slotWatchT = 0f;
                _slotWatchPos = me;
                if (moved < 0.2f && dist > 0.9f)
                {
                    _slotRelaxUntil = nowT + StallForceSeconds;
                    if (_stallLogCount < MaxStallLogs)
                    {
                        _stallLogCount++;
                        Vec2 pp = player.Position.AsVec2;
                        CombatAiLog.Log($"[STALL-SLOT] Agent #{Agent.Index} side={(side > 0 ? "L" : "R")} moved {moved:F2}m toward its slot " +
                            $"(slot dist={dist:F1}m, pos=({me.x:F1},{me.y:F1}), slot=({slotPos.x:F1},{slotPos.y:F1}), player=({pp.x:F1},{pp.y:F1})) -> slot pulled in for {StallForceSeconds:F0}s.");
                    }
                }
            }
            toSlot.Normalize();

            Vec2 fwd = new Vec2(toPlayer.x, toPlayer.y);   // attacker faces the player
            Vec2 right = new Vec2(fwd.y, -fwd.x);
            float fComp = Vec2.DotProduct(toSlot, fwd);
            float rComp = Vec2.DotProduct(toSlot, right);

            Agent.MovementControlFlag flags = Agent.MovementControlFlag.None;
            if (fComp > SparSlotDirThreshold) flags |= Agent.MovementControlFlag.Forward;
            else if (fComp < -SparSlotDirThreshold) flags |= Agent.MovementControlFlag.Backward;
            if (rComp > SparSlotDirThreshold) flags |= Agent.MovementControlFlag.StrafeRight;
            else if (rComp < -SparSlotDirThreshold) flags |= Agent.MovementControlFlag.StrafeLeft;
            return flags;
        }

        // GROUP-MODE threat detection (user spec): the engagement map already
        // knows who's attacking whom, so instead of scanning the whole field
        // we check ONLY our assigned attackers. A threat counts when all
        // three hold: it's within its own weapon reach of us, it's in
        // AttackRelease, AND the swing is actually aimed at us -- its combat
        // lock is on us, or the map still says we're its victim (the lock can
        // flicker mid-swing; the assignment is the stable intent). The player
        // is never map-assigned, so he's checked separately via the
        // PlayerThreatens marking.
        private Agent FindAssignedReleaser(Agent exclude)
        {
            Mission m = Agent.Mission;
            if (m == null)
                return null;
            _attackersBuf.Clear();
            EngagementMap.AttackersOf(Agent.Index, _attackersBuf, m);
            Agent best = null;
            float bestD = float.MaxValue;
            foreach (Agent a in _attackersBuf)
            {
                if (a == exclude || !CombatLive(a))
                    continue;
                if (a.GetCurrentActionStage(1) != Agent.ActionStage.AttackRelease)
                    continue;
                if (!InThreatReach(a))
                    continue;
                Agent t = a.GetTargetAgent();
                bool aimedAtUs = t == Agent;
                if (!aimedAtUs
                    && EngagementMap.TryGetAssignment(a.Index, out EngagementMap.Assignment asg)
                    && asg.VictimIndex == Agent.Index)
                    aimedAtUs = true;
                if (!aimedAtUs)
                    continue;
                float d = a.Position.DistanceSquared(Agent.Position);
                if (d < bestD) { bestD = d; best = a; }
            }

            Agent p = m.MainAgent;
            if (p != exclude && CombatLive(p) && Agent.IsEnemyOf(p)
                && EngagementMap.PlayerThreatens(Agent.Index)
                && p.GetCurrentActionStage(1) == Agent.ActionStage.AttackRelease
                && InThreatReach(p))
            {
                float d = p.Position.DistanceSquared(Agent.Position);
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        // Nearest committed (AttackRelease) threat within SparThreatRadiusMeters.
        // Unlike FindThreateningAttackerExcluding this ALSO accepts the player
        // and the designated attacker regardless of team relations -- the spar
        // rig must still block when e.g. the defender is on the player's team.
        private Agent FindSparThreat(Agent exclude = null)
        {
            _lastOp = "scan:spar";
            Mission m = Agent.Mission;
            if (m == null)
                return null;
            if (GroupLockDefense)
            {
                Agent gb = null; float gbd = float.MaxValue;
                foreach (Agent a in m.Agents)
                {
                    if (a == null || a == Agent || a == exclude || !CombatLive(a) || !a.IsHuman || !Agent.IsEnemyOf(a))
                        continue;
                    if (a.GetCurrentActionStage(1) != Agent.ActionStage.AttackRelease)
                        continue;
                    // Pair fight (user spec): the attacker's own combat lock
                    // IS the engagement pointer -- "target info is enough",
                    // no registry. Only a release whose lock is ON ME counts
                    // as incoming. The free-fallback keeps the claim check
                    // (a free attacker's lock sits at -1 mid-chain).
                    bool aimed = a.GetTargetAgent() == Agent
                        || (GroupUpDownOnly && GroupAimedAtMe(a));
                    if (!aimed)
                        continue;
                    float gd = Agent.Position.DistanceSquared(a.Position);
                    float gr = MeleeReachOf(a) + 1.2f;
                    if (gd > gr * gr || gd >= gbd) continue;
                    gbd = gd; gb = a;
                }
                return gb;
            }
            Agent best = null;
            float bestDist = float.MaxValue;
            float radiusSq = SparThreatRadiusMeters * SparThreatRadiusMeters;
            foreach (Agent a in m.Agents)
            {
                if (a == null || a == Agent || a == exclude || !CombatLive(a) || !a.IsHuman)
                    continue;
                if (a.Index != CombatAiState.SparAttackerIndex && !a.IsMainAgent && !Agent.IsEnemyOf(a))
                    continue;
                if (a.GetCurrentActionStage(1) != Agent.ActionStage.AttackRelease)
                    continue;
                // Same range gate as the enemy scan -- reach + margin.
                float d = Agent.Position.DistanceSquared(a.Position);
                float reach = MeleeReachOf(a) + 1.2f;
                if (d > reach * reach || d > radiusSq || d >= bestDist)
                    continue;
                bestDist = d;
                best = a;
            }
            return best;
        }

        // The OTHER live designated spar participant (attacker or player),
        // not `exclude` -- position-based, NOT gated on current action
        // stage (unlike FindSparThreat, which only finds who's mid-release).
        // Session 37 (user spec): used so an attacker can avoid swinging
        // toward a nearby ally and a dodging defender can always step to
        // the OUTER side, away from whichever participant it isn't
        // currently reacting to.
        private Agent FindSparAlly(Agent exclude)
        {
            Mission m = Agent.Mission;
            if (m == null)
                return null;
            float radiusSq = SparThreatRadiusMeters * SparThreatRadiusMeters;
            Agent best = null;
            float bestDist = float.MaxValue;
            foreach (Agent a in m.Agents)
            {
                if (a == null || a == Agent || a == exclude || !CombatLive(a) || !a.IsHuman)
                    continue;
                if (a.Index != CombatAiState.SparAttackerIndex && !a.IsMainAgent)
                    continue; // only the two designated spar participants count
                float d = Agent.Position.DistanceSquared(a.Position);
                if (d > radiusSq || d >= bestDist)
                    continue;
                bestDist = d;
                best = a;
            }
            return best;
        }

        // Spar defender v2 -- FIGHTS BACK now (user spec): not a blocking
        // dummy anymore, it runs the full duel FSM against BOTH attackers
        // (the designated AI attacker AND the player, who may be a teammate
        // -- _sparThreats makes the threat scan accept both regardless of
        // side). Focus switches: whoever is mid-swing is the threat; idle
        // time rests the lock on the player so he never gets the back for
        // free. Feints run at HALF rate -- a defender facing two isn't a
        // fancy duelist. Legs stay pinned toward the player (the verified
        // legs/torso decoupling) so the torso can cover the attacker without
        // turning the body away.
        private Agent.MovementControlFlag ComputeSparDefenderFlag(float dt)
        {
            _lastOp = "sparDef:enter";
            if (_defendTargetHold > 0f)
                _defendTargetHold -= dt;
            if (_jabTimer > 0f) _jabTimer -= dt;
            if (_jabCooldown > 0f) _jabCooldown -= dt;

            Agent attacker = GetAgentByIndex(CombatAiState.SparAttackerIndex);
            Agent player = Agent.Mission?.MainAgent;
            bool atkLive = CombatLive(attacker);
            bool playerLive = CombatLive(player);
            _feintRateScale = 0.5f;
            _sparThreats = true;
            _pureReactiveDefense = true;
            _legPinOwned = true;
            _multiEnemy = atkLive && playerLive;

            // SIMPLE rig (user spec, v4): legs + combat lock stay on the
            // PLAYER the whole time -- the attacker is rigged to hold a
            // front-side slot, so the defender just stands, blocks whoever
            // RELEASES (the side-threat path snaps the lock to the releaser
            // for the block, like perfectblock), and occasionally throws a
            // TORSO JAB at the attacker: a plain attack flag whose swing arc
            // physically reaches the side slot WITHOUT the lock or legs ever
            // leaving the player. No retargeting = player tracking is never
            // lost.
            Agent threat = FindSparThreat();

            // Jab gating -- the defender should be AWARE of where the player
            // is before spending a swing on the AI: only jab while the
            // player is actually engaged in front of us. If he's drifted off
            // or behind, fencing the attacker is pointless -- tracking the
            // player IS the job.
            bool playerEngaged = playerLive
                && HorizontalDistanceTo(player) < MeleeReach() * 1.6f;
            if (threat == null && _jabCooldown <= 0f && atkLive && playerEngaged
                && HorizontalDistanceTo(attacker) < MeleeReach() * 1.3f
                && !OwnInReadyStage())
            {
                _jabCooldown = 5.0f;
                _jabTimer = 1.4f;
            }

            // Torso jab: hold the attack flag ~0.5s (enough to ready), then
            // drop it so the swing releases. Abort instantly if a real threat
            // appears -- blocking beats jabbing.
            if (_jabTimer > 0f && threat == null && atkLive)
            {
                if (_jabTimer > 0.9f)
                {
                    Agent.MovementControlFlag jd = JabDirToward(attacker);
                    if (jd != Agent.MovementControlFlag.None)
                        return jd;
                }
                _jabTimer = 0f;
                return Agent.MovementControlFlag.None; // flag drops -> release
            }
            if (_jabTimer <= 0f)
                _jabTimer = 0f;

            Agent lockOn = threat
                ?? (_defendTargetHold > 0f && CombatLive(_lastThreat) ? _lastThreat : null)
                ?? SparLockDefault(player, attacker, playerLive);
            _forcedDuelTarget = lockOn;

            // Frame policy (user spec, v5): the scripted frame exists ONLY
            // while a block is live -- a threat releasing or the post-block
            // hold. Then the legs are frozen at whatever they were doing so
            // the lock->releaser snap reads as a pure TORSO turn. Outside the
            // block window the frame is released and the defender is a normal
            // flag-driven agent (distance-keeping + crab-steps, facing just
            // follows the combat lock like any fighter). Never during a dodge
            // step -- a pin would anchor the teleport.
            bool blockLive = _dodgeTime <= 0f && (threat != null
                || (_defendTargetHold > 0f && CombatLive(_lastThreat)));
            // Session 56/57 experiment state: which lever is actually installed.
            CombatAiState.TorsoMoveModeT tmMode = CombatAiState.TorsoMoveMode;
            ViewKeepPlan(attacker, player, dt, out Vec2 vkH, out Vec2 vkM, out _tmAng);
            // Session 60: the shared tactical planner (spar defender path).
            if (tmMode == CombatAiState.TorsoMoveModeT.Tactical)
            {
                _tacEnemies.Clear();
                if (CombatLive(player))
                    _tacEnemies.Add((player.Index, player.Position.AsVec2, ReferenceEquals(player, lockOn)));
                if (CombatLive(attacker))
                    _tacEnemies.Add((attacker.Index, attacker.Position.AsVec2, ReferenceEquals(attacker, lockOn)));
                _tacPlan = TacticalMovement.Compute(Agent.Position.AsVec2, _tacEnemies, null,
                    MeleeReach(), _brainClock, dt, ref _tac, DuelRng);
            }
            else
                _tac = default(TacticalMovement.TacticalState);
            _tmLocoWindow = blockLive && _dodgeTime <= 0f
                && ((CombatAiState.TorsoMoveNativeLoco && tmMode != CombatAiState.TorsoMoveModeT.Pin)
                    || (tmMode == CombatAiState.TorsoMoveModeT.Free && CombatAiState.TorsoBlockMove == CombatAiState.TorsoBlockMoveT.Loco));
            if (!Agent.HasMount)
            {
                if (tmMode != _tmMode)   // switched mid-fight: strip the old lever first
                {
                    TorsoMoveCleanup();
                    _tmMode = tmMode;
                }
                if (blockLive)
                {
                    if (tmMode == CombatAiState.TorsoMoveModeT.Pin)
                    {
                        // PIN (default, unchanged): scripted own-position frame
                        // freezes the legs; the lock->releaser snap is a torso turn.
                        if (!_sparPinSet)
                        {
                            Vec2 f = Agent.GetMovementDirection();
                            _sparPinDir = f.LengthSquared > 0.0001f
                                ? new Vec3(f.x, f.y, 0f)
                                : DirToFlat(playerLive ? player : attacker);
                            _sparPinSet = true;
                        }
                        _lastOp = "SetTargetPosAndDir(sparPin)";
                        Agent.SetTargetPositionAndDirection(Agent.Position.AsVec2, _sparPinDir);
                        _targetFrameSet = true;
                        _lastPinDir = _sparPinDir;
                    }
                    else
                    {
                        // Free-leg modes: no scripted frame. Strip any leftover
                        // frame once, capture the block heading exactly as the
                        // pin does, then re-write the lever each frame.
                        if (_sparPinSet)
                        {
                            _lastOp = "ClearTargetFrame(tm)";
                            Agent.ClearTargetFrame();
                            Agent.DisableScriptedMovement();
                            _targetFrameSet = false;
                            _sparPinSet = false;
                            TorsoMoveCleanup();
                        }
                        if (!_tmPinCaptured)
                        {
                            Vec2 f = Agent.GetMovementDirection();
                            _sparPinDir = f.LengthSquared > 0.0001f
                                ? new Vec3(f.x, f.y, 0f)
                                : DirToFlat(playerLive ? player : attacker);
                            _tmPinCaptured = true;
                        }
                        if (CombatAiState.TorsoBlockMove == CombatAiState.TorsoBlockMoveT.Input)
                        {
                            // Session 61c: the WASD channel. MovementInputVector
                            // is world-space (the dodge proves writes work) and
                            // stays live inside block actions -- it is how a
                            // PLAYER strafes while holding block. No facing
                            // enforcement at all: torso stays on the lock via
                            // target switching, legs walk the vector.
                            // inputVector is FACING-RELATIVE (x=strafe right,
                            // y=forward) -- MissionMainAgentController writes
                            // raw axes. Convert the world-space goal into the
                            // agent's body frame: f = forward, right = f-90deg.
                            Vec2 g = BlockMoveGoal(lockOn, player, attacker);
                            Vec2 f2 = Agent.Frame.rotation.f.AsVec2;
                            float fl = f2.Length;
                            float gl = g.Length;
                            _tmInputVec = Vec2.Zero;
                            if (fl > 0.0001f && gl > 0.05f)
                            {
                                f2 = new Vec2(f2.x / fl, f2.y / fl);
                                if (gl > 1f)
                                    g = new Vec2(g.x / gl, g.y / gl);
                                _tmInputVec = new Vec2(
                                    g.x * f2.y - g.y * f2.x,      // strafe: dot(g, right) where right=(f.y,-f.x)
                                    g.x * f2.x + g.y * f2.y);     // fwd:    dot(g, forward)
                            }
                            _lastOp = "MovementInputVector(blockmove)";
                            Agent.MovementInputVector = _tmInputVec;
                        }
                        else
                        {
                            _tmInputVec = Vec2.Zero;
                            if (tmMode == CombatAiState.TorsoMoveModeT.MoveDir)
                            {
                                _lastOp = "SetMovementDirection(torso)";
                                Agent.SetMovementDirection(new Vec2(_sparPinDir.x, _sparPinDir.y));
                            }
                            else if (tmMode == CombatAiState.TorsoMoveModeT.Tactical)
                            {
                            if (_tmFormationSet && !Agent.IsFormationFrameEnabled)
                                _tmStomps++;
                            // Session 61b: the enforced heading must NOT track
                            // the live lock bearing -- the attacker orbits us,
                            // the bearing moves every tick and the legs spend
                            // the block turning in place (ai5: spd~0.17). Slew
                            // the frozen block heading toward the lock at
                            // ~60deg/s; the goal still centres on the lock.
                            Vec2 h = new Vec2(_sparPinDir.x, _sparPinDir.y);
                            Vec2 g = BlockMoveGoal(lockOn, player, attacker);
                            if (lockOn != null && CombatLive(lockOn))
                            {
                                Vec2 toL = lockOn.Position.AsVec2 - Agent.Position.AsVec2;
                                float dL = toL.Length;
                                if (dL > 0.05f)
                                {
                                    float curA = MathF.Atan2(_sparPinDir.y, _sparPinDir.x);
                                    float wantA = MathF.Atan2(toL.y, toL.x);
                                    float dA = wantA - curA;
                                    while (dA > 3.14159f) dA -= 6.28319f;
                                    while (dA < -3.14159f) dA += 6.28319f;
                                    float step = MathF.Max(-1.05f * dt, MathF.Min(1.05f * dt, dA));
                                    float newA = curA + step;
                                    _sparPinDir = new Vec3(MathF.Cos(newA), MathF.Sin(newA), 0f);
                                    h = new Vec2(_sparPinDir.x, _sparPinDir.y);
                                }
                            }
                            Vec2 goal2 = Agent.Position.AsVec2 + g;
                            Vec2 vel = Agent.GetCurrentVelocity();
                            _lastOp = "FormationFrame(tactical)";
                            Agent.SetFormationFrameEnabled(
                                new TaleWorlds.Engine.WorldPosition(Mission.Current.Scene, UIntPtr.Zero, new Vec3(goal2.x, goal2.y, Agent.Position.z), false),
                                h, vel, CombatAiState.TorsoFormationFactor);
                            _tmFormationSet = true;
                        }
                        else // Formation/Free-blockmove: native formation frame
                        {
                            if (_tmFormationSet && !Agent.IsFormationFrameEnabled)
                                _tmStomps++;   // the engine stomped our frame since last tick
                            bool formationOn = tmMode == CombatAiState.TorsoMoveModeT.Formation
                                || (tmMode == CombatAiState.TorsoMoveModeT.Free
                                    && (!CombatAiState.TorsoViewKeep || CombatAiState.TorsoBlockMove == CombatAiState.TorsoBlockMoveT.Formation));
                            if (formationOn)
                            {
                                Vec2 h, goal2;
                                if (CombatAiState.TorsoViewKeep)
                                {
                                    // Session 57/58: heading = the both-attackers
                                    // bisector, goal = D + the view-keep intent.
                                    h = vkH.LengthSquared > 0.0001f ? vkH : new Vec2(_sparPinDir.x, _sparPinDir.y);
                                    goal2 = Agent.Position.AsVec2 + vkM;
                                }
                                else
                                {
                                    // Session 59 default = the log-proven
                                    // Session-56 behaviour: frozen block-start
                                    // heading + "1m along current motion".
                                    h = new Vec2(_sparPinDir.x, _sparPinDir.y);
                                    Vec2 intent = Agent.GetMovementDirection();
                                    goal2 = intent.LengthSquared > 0.01f
                                        ? Agent.Position.AsVec2 + intent * (1f / MathF.Sqrt(intent.LengthSquared)) * 1.0f
                                        : Agent.Position.AsVec2;
                                }
                                Vec2 vel = Agent.GetCurrentVelocity();
                                _lastOp = "FormationFrame(spar)";
                                Agent.SetFormationFrameEnabled(
                                    new TaleWorlds.Engine.WorldPosition(Mission.Current.Scene, UIntPtr.Zero, new Vec3(goal2.x, goal2.y, Agent.Position.z), false),
                                    h, vel, CombatAiState.TorsoFormationFactor);
                                _tmFormationSet = true;
                            }
                        }
                        }
                    }
                }
                else
                {
                    if (_sparPinSet)
                    {
                        _lastOp = "ClearTargetFrame(spar)";
                        Agent.ClearTargetFrame();
                        _lastOp = "DisableScriptedMovement(spar)";
                        Agent.DisableScriptedMovement();
                        _targetFrameSet = false;
                        _sparPinSet = false;
                        TorsoMoveCleanup();
                    }
                    TorsoMoveCleanup();
                    _tmPinCaptured = false;
                }
            }

            // Session 52: cluster bookkeeping for the spar brain -- record each
            // attacker's release edges so SparClusterLogit can trim the block
            // chance when two attacks land close together.
            if (SparBrainActive)
                TrackSparReleaseEdges(player, attacker);

            Agent.MovementControlFlag fsm = ComputeDuelFlag(dt);

            // Dodge step in progress: the teleport+ gait flag inside fsm owns
            // the body -- don't let slot/keep-distance flags override it.
            if (_dodgeTime > 0f)
                return fsm;

            // Normal flag locomotion OUTSIDE block windows only: distance-
            // keeping toward the lock target + a crab-step toward the
            // player's side if he drifts off the front arc. While a block is
            // live the pin owns the legs -- flag writes would just fight it.
            // Front-view flags (|bearing vs legs heading| < 80deg) feed both the
            // per-frame line and the 10s summary, so they're computed once here.
            Vec2 legsDir = Agent.GetMovementDirection();
            bool inViewA = false, inViewP = false;
            if (legsDir.LengthSquared > 0.0001f)
            {
                if (CombatLive(attacker))
                {
                    Vec2 tA = attacker.Position.AsVec2 - Agent.Position.AsVec2;
                    if (tA.LengthSquared > 0.0001f)
                        inViewA = Vec2.DotProduct(legsDir, tA) / (legsDir.Length * tA.Length) > 0.1736f;
                }
                if (CombatLive(player))
                {
                    Vec2 tP = player.Position.AsVec2 - Agent.Position.AsVec2;
                    if (tP.LengthSquared > 0.0001f)
                        inViewP = Vec2.DotProduct(legsDir, tP) / (legsDir.Length * tP.Length) > 0.1736f;
                }
            }

            // [TORSOMOVE] Session 56 diagnostics: ~0.4s per defender while a
            // non-pin mode runs or the user asked for it.
            if ((tmMode != CombatAiState.TorsoMoveModeT.Pin || CombatAiState.TorsoMoveLog)
                && _tmLogCount < MaxTmLogs && _brainClock - _tmLastLogAt >= 0.4f)
            {
                _tmLastLogAt = _brainClock;
                _tmLogCount++;
                Vec2 legs = legsDir;
                Vec2 lk = Agent.LookDirection.AsVec2;
                Vec2 toLock = lockOn != null ? lockOn.Position.AsVec2 - Agent.Position.AsVec2 : Vec2.Zero;
                float legsDeg = legs.LengthSquared > 0.0001f ? MathF.Atan2(legs.y, legs.x) * 57.2958f : -999f;
                float lookDeg = lk.LengthSquared > 0.0001f ? MathF.Atan2(lk.y, lk.x) * 57.2958f : -999f;
                float bearDeg = toLock.LengthSquared > 0.0001f ? MathF.Atan2(toLock.y, toLock.x) * 57.2958f : -999f;
                float relDeg = (legsDeg > -900f && bearDeg > -900f)
                    ? (((bearDeg - legsDeg + 540f) % 360f) - 180f) : 0f;
                float pinDeg = _sparPinDir.LengthSquared > 0.0001f ? MathF.Atan2(_sparPinDir.y, _sparPinDir.x) * 57.2958f : -999f;
                CombatAiLog.Log($"[TORSOMOVE] Agent #{Agent.Index}: mode={tmMode} loco={(CombatAiState.TorsoMoveNativeLoco ? "on" : "off")} blockLive={blockLive} " +
                    $"lock={(lockOn != null ? lockOn.Index : -1)} spd={Agent.GetCurrentVelocity().Length:F2} " +
                    $"ffEnabled={Agent.IsFormationFrameEnabled} stomps={_tmStomps} " +
                    $"legs={legsDeg:F0} look={lookDeg:F0} bear={bearDeg:F0} rel={relDeg:F0} pin={pinDeg:F0} " +
                    $"ang={_tmAng:F0} inViewA={inViewA} inViewP={inViewP} viewkeep={CombatAiState.TorsoViewKeep} blockmove={CombatAiState.TorsoBlockMove} factor={CombatAiState.TorsoFormationFactor:F2} goal={vkM.Length:F2} " +
                    $"iv=({_tmInputVec.x:F2},{_tmInputVec.y:F2}) blocked={_tmBlocked}/hit={_tmClean}");
            }

            // [TORSOSUM] Session 58: 10s accumulated summary while a non-pin
            // mode runs or logging is on (keeps signal after the line budget).
            if (tmMode != CombatAiState.TorsoMoveModeT.Pin || CombatAiState.TorsoMoveLog)
            {
                _tsN++;
                if (inViewA && inViewP) _tsBoth++;
                if (_tmAng > 130f) _tsAng130++;
                if (blockLive) { _tsBlockN++; _tsSpd += Agent.GetCurrentVelocity().Length; }
                if (tmMode == CombatAiState.TorsoMoveModeT.Tactical && _tacPlan.DistSecondary > 0f)
                {
                    _tsDistN++;
                    _tsDistS += _tacPlan.DistSecondary;
                    float reach = MeleeReach();
                    if (_tacPlan.DistSecondary > reach) _tsFar++;
                    if (_tacPlan.DistPrimary < reach * 1.3f) _tsClose++;
                }
                if (Agent.IsFormationFrameEnabled) _tsFf++;
                if (_brainClock - _tsLastSumAt >= 10f && _tsN > 0)
                {
                    CombatAiLog.Log($"[TORSOSUM] Agent #{Agent.Index}: mode={tmMode} viewkeep={CombatAiState.TorsoViewKeep} factor={CombatAiState.TorsoFormationFactor:F2} " +
                        $"both={100f * _tsBoth / _tsN:F0}% ang>130={100f * _tsAng130 / _tsN:F0}% " +
                        $"blockSpd={(_tsBlockN > 0 ? _tsSpd / _tsBlockN : 0f):F2} ff={100f * _tsFf / _tsN:F0}% stomps={_tmStomps} " +
                        $"blocked+={_tmBlocked - _tsLastBlocked} hit+={_tmClean - _tsLastClean}" +
                        $" distS={(_tsDistN > 0 ? _tsDistS / _tsDistN : 0f):F2} far={(_tsDistN > 0 ? 100f * _tsFar / _tsDistN : 0f):F0}% close={(_tsDistN > 0 ? 100f * _tsClose / _tsDistN : 0f):F0}%");
                    _tsLastSumAt = _brainClock;
                    _tsN = _tsBoth = _tsAng130 = _tsBlockN = _tsFf = 0;
                    _tsSpd = 0f;
                    _tsDistS = _tsFar = _tsClose = _tsDistN = 0f;
                    _tsLastBlocked = _tmBlocked;
                    _tsLastClean = _tmClean;
                }
            }

            // [TACTICAL] Session 60: the shared planner's raw output, ~0.4s.
            if (tmMode == CombatAiState.TorsoMoveModeT.Tactical
                && _tacLogCount < MaxTacLogs && _brainClock - _tacLastLogAt >= 0.4f)
            {
                _tacLastLogAt = _brainClock;
                _tacLogCount++;
                CombatAiLog.Log($"[TACTICAL] Agent #{Agent.Index}: primary=#{_tacPlan.PrimaryKey} " +
                    $"distP={_tacPlan.DistPrimary:F2} distS={_tacPlan.DistSecondary:F2} goal={_tacPlan.Goal.Length:F2} " +
                    $"orbit={_tac.OrbitSign:F0} spd={Agent.GetCurrentVelocity().Length:F2} " +
                    $"ffEnabled={Agent.IsFormationFrameEnabled} stomps={_tmStomps} blockLive={blockLive}");
            }

            Agent.MovementControlFlag move = Agent.MovementControlFlag.None;
            if (tmMode == CombatAiState.TorsoMoveModeT.Free && CombatAiState.TorsoViewKeep)
            {
                // Free+viewkeep: locomotion is the view-keep intent, only
                // OUTSIDE the block window (inside it flags can't move; the
                // blockmove lever or loco window carries the legs).
                if (!blockLive)
                    move = MoveFlagsForIntent(vkM, vkH);
            }
            else if (tmMode == CombatAiState.TorsoMoveModeT.Tactical)
            {
                // Tactical: planner intent -> flags outside block windows
                // (inside them the formation frame owns the legs alone).
                if (!blockLive)
                    move = MoveFlagsForIntent(_tacPlan.Goal, Agent.GetMovementDirection());
            }
            else if (!blockLive
                || (tmMode != CombatAiState.TorsoMoveModeT.Pin && !CombatAiState.TorsoMoveNativeLoco))
                move = SparDefenderMoveFlags(lockOn) | PlayerArcStrafe(player);   // free-leg modes let the legs travel during blocks (loco on => none, native owns it)
            return (fsm & ~Agent.MovementControlFlag.MoveMask) | move;
        }

        // Session 61 (user direction): the block-window movement goal centred
        // on the LOCKED enemy -- close to the reach band on them, slide to the
        // side the other attacker is not on so the lock stays between us and
        // them, and shove off when they press into reach. Shared by the
        // formation-frame and input-vector block-move levers.
        private Vec2 BlockMoveGoal(Agent lockOn, Agent player, Agent attacker)
        {
            Vec2 g = Vec2.Zero;
            if (lockOn == null || !CombatLive(lockOn))
                return g;
            Vec2 toL = lockOn.Position.AsVec2 - Agent.Position.AsVec2;
            float dL = toL.Length;
            if (dL <= 0.05f)
                return g;
            Vec2 h = new Vec2(toL.x / dL, toL.y / dL);
            float reach = MeleeReach();
            Agent other = ReferenceEquals(lockOn, player) ? attacker : player;
            Vec2 toO = CombatLive(other) ? other.Position.AsVec2 - Agent.Position.AsVec2 : Vec2.Zero;
            float dO = toO.Length;
            if (dL > reach * 1.05f)
                g = h * MathF.Min(1.4f, dL - reach * 0.9f);
            else if (dL < reach * 0.55f)
                g = h * -MathF.Min(1.0f, reach * 0.55f - dL + 0.3f);
            else
            {
                float sign = _tac.OrbitSign != 0f ? _tac.OrbitSign : 1f;
                if (dO > 0.05f)
                    sign = toL.x * toO.y - toL.y * toO.x >= 0f ? -1f : 1f;
                g = new Vec2(-h.y * sign, h.x * sign) * 0.8f;
            }
            if (dO > 0.05f && dO < reach * 1.25f)
                g += new Vec2(-toO.x / dO, -toO.y / dO) * ((reach * 1.25f - dO) / (reach * 1.25f));
            return g;
        }

        // Session 61 (user direction): in Tactical the lock defaults to the
        // NEARER live enemy -- the one we're actually maneuvering on -- so
        // lock, torso and legs all point the same way. Other modes keep the
        // player-sticky default.
        private Agent SparLockDefault(Agent player, Agent attacker, bool playerLive)
        {
            if (CombatAiState.TorsoMoveMode != CombatAiState.TorsoMoveModeT.Tactical)
                return playerLive ? player : attacker;
            if (CombatLive(attacker)
                && (!playerLive
                    || (attacker.Position.AsVec2 - Agent.Position.AsVec2).LengthSquared
                        <= (player.Position.AsVec2 - Agent.Position.AsVec2).LengthSquared))
                return attacker;
            return playerLive ? player : attacker;
        }

        // Session 56: strip whatever the torso-move experiment installed.
        private void TorsoMoveCleanup()
        {
            if (_tmFormationSet)
            {
                _tmFormationSet = false;
                try
                {
                    Agent.SetFormationFrameDisabled();
                }
                catch { }
            }
            _bmHeading = Vec2.Zero;
            if (_tmInputVec.LengthSquared > 0.0001f)
            {
                _tmInputVec = Vec2.Zero;
                try
                {
                    Agent.MovementInputVector = Vec2.Zero;
                }
                catch { }
            }
            _tmLocoWindow = false;
        }

        // Session 57: keeps BOTH attackers inside the defender's front cone.
        // heading = bisector of D->attacker / D->player (or the perpendicular
        // toward the defender's own side when they're nearly opposite);
        // stepDir = a side step perpendicular to the A-P line that shrinks
        // the angle (with _vkSide hysteresis so it doesn't flip at the seam).
        private float _vkSide = 1f;
        private Vec2 _vkH = Vec2.Zero;   // smoothed heading across ticks
        private void ViewKeepPlan(Agent attacker, Agent player, float dt, out Vec2 heading, out Vec2 stepDir, out float angleBetweenDeg)
        {
            heading = Vec2.Zero;
            stepDir = Vec2.Zero;
            angleBetweenDeg = 180f;
            Vec2 pa = Agent.Position.AsVec2;
            bool aL = CombatLive(attacker), pL = CombatLive(player);
            if (!aL && !pL)
            {
                _vkH = Vec2.Zero;
                return;
            }
            Vec2 hNew;
            Vec2 m = Vec2.Zero;
            if (!aL || !pL)
            {
                Agent t = aL ? attacker : player;
                Vec2 u = t.Position.AsVec2 - pa;
                hNew = u.LengthSquared > 0.001f ? u : pa;
                if (hNew.LengthSquared > 0.0001f)
                    hNew.Normalize();
            }
            else
            {
                Vec2 uA = attacker.Position.AsVec2 - pa;
                Vec2 uP = player.Position.AsVec2 - pa;
                if (uA.LengthSquared < 0.0025f || uP.LengthSquared < 0.0025f)
                    return;
                uA.Normalize();
                uP.Normalize();
                angleBetweenDeg = MathF.Acos(MathF.Max(-1f, MathF.Min(1f, Vec2.DotProduct(uA, uP)))) * 57.2958f;
                Vec2 ap = uP - uA;                      // attacker -> player line dir
                Vec2 perp = new Vec2(-ap.y, ap.x);      // normal of that line
                if (perp.LengthSquared > 0.0001f)
                {
                    perp.Normalize();
                    Vec2 mid = (attacker.Position.AsVec2 + player.Position.AsVec2) * 0.5f;
                    float side = Vec2.DotProduct(perp, pa - mid);
                    if (MathF.Abs(side) > 0.05f)
                        _vkSide = MathF.Sign(side);      // hysteresis: only flip when clearly on the other side
                    perp *= _vkSide;
                }
                Vec2 sum = uA + uP;
                hNew = angleBetweenDeg < 150f && sum.LengthSquared > 0.0001f ? sum : perp;
                if (hNew.LengthSquared > 0.0001f)
                    hNew.Normalize();
                // Session 58: earlier+stronger view-keep. w ramps in at ~100deg
                // (was a hard >130 step); retreating from the A-P midpoint
                // shrinks the subtended angle fastest, the side step second.
                float w = MathF.Max(0f, MathF.Min(1f, (angleBetweenDeg - 100f) / 60f));
                Vec2 keepComp = Vec2.Zero;
                Agent lockT = _forcedDuelTarget;
                if (CombatLive(lockT))
                {
                    Vec2 toLock = lockT.Position.AsVec2 - pa;
                    float d = toLock.Length;
                    if (d > 0.05f)
                    {
                        toLock.Normalize();
                        float keep = MathF.Max(-0.4f, MathF.Min(0.4f, (d - 1.5f) * 0.5f));
                        keepComp = toLock * keep;
                    }
                }
                Vec2 midV = (attacker.Position.AsVec2 + player.Position.AsVec2) * 0.5f;
                Vec2 awayMid = pa - midV;
                if (awayMid.LengthSquared > 0.0001f)
                    awayMid.Normalize();
                m = keepComp * (1f - 0.7f * w) + awayMid * (1.5f * w) + perp * (1.0f * w);
                if (m.LengthSquared > 4f)
                {
                    m.Normalize();
                    m *= 2f;
                }
            }
            // Smooth the heading across ticks so it doesn't jitter (8/s lerp).
            float s = MathF.Max(0f, MathF.Min(1f, 8f * MathF.Min(dt, 0.05f)));
            _vkH = _vkH.LengthSquared > 0.0001f ? _vkH + (hNew - _vkH) * s : hNew;
            if (_vkH.LengthSquared > 0.0001f)
            {
                Vec2 n = _vkH;
                n.Normalize();
                heading = n;
            }
            stepDir = m;
        }

        // Local-axes projection of a world intent vector into movement flags
        // (same scheme SparSlotMoveFlags uses): facing ~= H.
        private Agent.MovementControlFlag MoveFlagsForIntent(Vec2 intent, Vec2 facing)
        {
            if (intent.LengthSquared < 0.0025f)
                return Agent.MovementControlFlag.None;
            if (facing.LengthSquared < 0.0001f)
                return Agent.MovementControlFlag.None;
            Vec2 i = intent;
            i.Normalize();
            Vec2 f = facing;
            f.Normalize();
            Vec2 right = new Vec2(f.y, -f.x);
            float fComp = Vec2.DotProduct(i, f), rComp = Vec2.DotProduct(i, right);
            Agent.MovementControlFlag flags = Agent.MovementControlFlag.None;
            if (fComp > 0.25f) flags |= Agent.MovementControlFlag.Forward;
            else if (fComp < -0.25f) flags |= Agent.MovementControlFlag.Backward;
            if (rComp > 0.25f) flags |= Agent.MovementControlFlag.StrafeRight;
            else if (rComp < -0.25f) flags |= Agent.MovementControlFlag.StrafeLeft;
            return flags;
        }

        // Blow bookkeeping for the [TORSOMOVE] line (spar defender only).
        public void NoteTorsoHit(CombatCollisionResult result)
        {
            if (Agent.Index != CombatAiState.SparDefenderIndex)
                return;
            if (result == CombatCollisionResult.Blocked
                || result == CombatCollisionResult.Parried
                || result == CombatCollisionResult.ChamberBlocked)
                _tmBlocked++;
            else
                _tmClean++;
        }

        // Distance-keeping: step in DIAGONALLY when out of reach (Forward |
        // strafe, the same angled close the duel approach uses -- a straight
        // walk-in is trivially kickable), back-step when pressed.
        private Agent.MovementControlFlag SparDefenderMoveFlags(Agent focus)
        {
            if (!CombatLive(focus))
                return Agent.MovementControlFlag.None;
            float d = HorizontalDistanceTo(focus);
            float reach = MeleeReach();
            if (d > reach * 1.05f) return Agent.MovementControlFlag.Forward | _approachStrafe;
            if (d < reach * 0.45f) return Agent.MovementControlFlag.Backward;
            return Agent.MovementControlFlag.None;
        }

        // If the player drifts ~24deg+ off the defender's current facing,
        // crab-step toward his side to re-center the line. This only runs
        // OUTSIDE block windows where facing follows the lock normally.
        private Agent.MovementControlFlag PlayerArcStrafe(Agent player)
        {
            if (!CombatLive(player))
                return Agent.MovementControlFlag.None;
            Vec3 to = DirToFlat(player);
            if (to.LengthSquared < 0.01f)
                return Agent.MovementControlFlag.None;
            Vec2 f = Agent.GetMovementDirection();
            if (f.LengthSquared < 0.0001f)
                return Agent.MovementControlFlag.None;
            f.Normalize();
            float cross = f.x * to.y - f.y * to.x; // >0 = player on our LEFT
            if (cross > 0.42f) return Agent.MovementControlFlag.StrafeLeft;
            if (cross < -0.42f) return Agent.MovementControlFlag.StrafeRight;
            return Agent.MovementControlFlag.None;
        }

        // Torso jab direction: which horizontal attack flag's swing arc
        // actually reaches `a` given our pinned facing? The blade sweeps the
        // side it starts on (an AttackLeft covers OUR left-front region --
        // same geometry as MirrorDefend: enemy AttackLeft lands our right,
        // i.e. it sweeps HIS left). Bearing ~15deg+ off center picks a side;
        // dead-ahead takes the thrust/overhead line.
        private Agent.MovementControlFlag JabDirToward(Agent a)
        {
            Vec3 to = DirToFlat(a);
            if (to.LengthSquared < 0.01f)
                return Agent.MovementControlFlag.None;
            Vec2 f = Agent.GetMovementDirection();
            if (f.LengthSquared < 0.0001f)
                return Agent.MovementControlFlag.AttackUp;
            f.Normalize();
            float cross = f.x * to.y - f.y * to.x; // >0 = target sits on our LEFT
            if (cross > 0.25f)
                return Agent.MovementControlFlag.AttackLeft;
            if (cross < -0.25f)
                return Agent.MovementControlFlag.AttackRight;
            return IsDirectionSupported(Agent.MovementControlFlag.AttackDown)
                ? Agent.MovementControlFlag.AttackDown
                : Agent.MovementControlFlag.AttackUp;
        }

        private static bool IsWindingOrReleasing(Agent a)
        {
            if (!CombatLive(a))
                return false;
            Agent.ActionStage s = a.GetCurrentActionStage(1);
            return s == Agent.ActionStage.AttackReady
                || s == Agent.ActionStage.AttackQuickReady
                || s == Agent.ActionStage.AttackRelease;
        }

        private Vec3 DirToFlat(Agent t)
        {
            Vec3 v = t.Position - Agent.Position;
            v.z = 0f;
            if (v.LengthSquared < 0.01f) return Vec3.Zero;
            v.Normalize();
            return v;
        }

        // =====================================================================
        // GROUP FIGHT (combatai.group) -- whole-field combat AI for testing.
        // EngagementMap pairs every AI with an untargeted enemy first (mutual
        // 1v1s), stacks extras onto the least-covered victim when outnumbering,
        // and marks the 3rd+ attacker a FLANKER who steers for the victim's
        // back. Session 65 "free agent" rule: stacking is capped at 2 per
        // victim until every enemy has 2, and the 3rd attacker is a FREE
        // AGENT -- unbound from the victim's view cone, it works the back and
        // may swing through the 2-swing lockout once it's genuinely behind
        // (|bearing| > ~120deg off the victim's facing). The outnumbered
        // defender knows who the free agent is: never targets/locks it, and
        // keeps it at bay while fighting the front two. Agents facing 2+
        // attackers (or 1 + the player) become OUTNUMBERED DEFENDERS: they
        // keep free-space movement, switch focus between threats, never give
        // the player their back, and feint at half rate. Agents the player
        // engages get the T1 brain; pure AI-vs-AI runs T2 inside the same FSM.
        // =====================================================================
        private Agent.MovementControlFlag ComputeGroupFlag(float dt)
        {
            // GROUP LINEUP (stage 1): while the lines form/advance/wait the
            // lineup drives each group agent directly -- no victim lock, no
            // formation advance, no slot steering. Phase=Fight falls through
            // to the legacy brain below.
            if (CombatAiState.GroupFree && GroupLineup.Phase != GroupLineup.Ph.Fight)
                return ComputeGroupLineFlag(dt);
            // STAGE 2: the registry-driven free fight replaces the legacy
            // lock-based group brain entirely (pairing/roles/advance unused).
            if (CombatAiState.GroupFree && GroupLineup.Phase == GroupLineup.Ph.Fight)
                return ComputeGroupFreeFightFlag(dt);

            _lastOp = "group:enter";
            // Group torso-jab timers (Session 64): same lever as the spar jab.
            if (_jabTimer > 0f) _jabTimer -= dt;
            if (_jabCooldown > 0f) _jabCooldown -= dt;

            Agent victim = null;
            int role = EngagementMap.RolePrimary;
            if (EngagementMap.TryGetAssignment(Agent.Index, out EngagementMap.Assignment asg))
            {
                victim = GetAgentByIndex(asg.VictimIndex);
                role = asg.Role;
            }
            if (!CombatLive(victim))
                victim = FindNearestEnemy();
            if (!CombatLive(victim))
            {
                _groupDefender = false;
                _groupAdvancing = false;
                return Agent.MovementControlFlag.None;
            }

            Mission m = Agent.Mission;
            Agent gPlayer = m?.MainAgent;
            // ENEMIES ONLY: the map already gates this, but belt-and-braces --
            // a teammate flagged "player threatens me" would lock onto and
            // swing at the PLAYER (observed: own team fencing the player).
            bool playerOnMe = EngagementMap.PlayerThreatens(Agent.Index)
                && CombatLive(gPlayer) && Agent.IsEnemyOf(gPlayer);
            int attackersOnMe = EngagementMap.AttackersOn(Agent.Index);
            bool outnumbered = attackersOnMe >= 2 || (attackersOnMe >= 1 && playerOnMe);
            _multiEnemy = outnumbered || attackersOnMe >= 1 || CombatAiState.GroupFightMode;
            _feintRateScale = outnumbered ? 0.5f : 1f;
            _groupDefender = outnumbered;
            // Shared clearance ring feeds every group steer/strafe (Session
            // 64): teammate bodies are the dominant obstacle in line fights --
            // the ring's agent channel (GetNearbyAgents, unfiltered) clamps
            // them the same way it clamps walls.
            _atkClear.Tick(Agent, _brainClock);

            // PRE-CONTACT LINE ADVANCE (Session 66 user spec): until OUR
            // victim is inside contact range the whole team marches in as a
            // LINE -- each agent steers to a perpendicular slot on a line
            // standing just outside contact of the enemy centroid, and no
            // attack bits are emitted. Once the victim is in reach the normal
            // group brain below (slot steering + 2-swing discipline) takes
            // over -- "the 5 arrive and then start fighting together".
            float victimDist = HorizontalDistanceTo(victim);
            if (victimDist > GroupContactRange())
            {
                _groupAdvancing = true;
                _brainClock += dt;             // the FSM's clock never ticks on this path
                _forcedDuelTarget = victim;
                _sparThreats = false;
                // Session 67: REAL formation orders own the march -- hand the
                // whole body to native (None + _handOffToNative, the duel-
                // leash suppression). The formation frame pulls each unit to
                // its line slot: native pathfinding, no wall-push, no
                // stragglers, and the handoff strips leaked combat bits so
                // nobody swings mid-march. Falls back to the flag-push march
                // when the formation path isn't armed/enabled for this agent.
                if (GroupFormationAdvance.Suppress(Agent))
                {
                    _handOffToNative = true;
                    return Agent.MovementControlFlag.None;
                }
                _handOffToNative = false;      // we drive the march ourselves -- a stale handoff would starve the flags
                EnsureTargetAgent(victim);     // face/lock the enemy line while marching
                RefreshGroupAdvance();
                return GroupAdvanceFlag(victim);
            }
            if (_groupAdvancing)
            {
                _groupAdvancing = false;
                if (_gadvLogCount < MaxGadvLogs)
                {
                    _gadvLogCount++;
                    CombatAiLog.Log($"[GADV] Agent #{Agent.Index}: contact on victim #{victim.Index} ({victimDist:F2}m) -- line advance over, combat brain.");
                }
            }

            if (outnumbered)
                return ComputeOutnumberedFlag(dt, victim, playerOnMe, m);

            _forcedDuelTarget = victim;
            _sparThreats = false;

            // Torso jab (Session 64): a NON-LOCKED enemy inside ~45deg of
            // facing and in reach gets jabbed from the torso arc instead of a
            // relock -- same gating/intervals as the spar defender's jab.
            if (TryGroupJab(victim, null, null, out Agent.MovementControlFlag jab))
                return jab;

            // 2-swing discipline (user spec): who else is on OUR victim, and
            // is a front (role 0/1) attacker committed right now.
            _coAttackersBuf.Clear();
            EngagementMap.AttackersOf(victim.Index, _coAttackersBuf, m);
            bool frontBusy = false, otherLineReleasing = false;
            foreach (Agent co in _coAttackersBuf)
            {
                if (co == Agent || !CombatLive(co))
                    continue;
                if (!EngagementMap.TryGetAssignment(co.Index, out EngagementMap.Assignment ca)
                    || ca.Role >= EngagementMap.RoleFlanker)
                    continue;
                if (IsWindingOrReleasing(co))
                    frontBusy = true;
                if (co.GetCurrentActionStage(1) == Agent.ActionStage.AttackRelease)
                    otherLineReleasing = true;
            }

            // Line-attacker stagger: the OTHER front attacker's swing is in
            // flight -> prefer holding our ready a beat (the double-hold
            // lever) so two swings don't land as one unblockable blob.
            if (role <= EngagementMap.RoleSupport && otherLineReleasing && !_duelHoldExtended)
            {
                _duelHoldSeconds += 0.15f + (float)DuelRng.NextDouble() * 0.15f;
                _duelHoldExtended = true;
                if (_gswingLogCount < MaxGswingLogs)
                {
                    _gswingLogCount++;
                    CombatAiLog.Log($"[GSWING] Agent #{Agent.Index}: stagger hold on victim #{victim.Index} (partner releasing, hold now {_duelHoldSeconds:F2}s).");
                }
            }

            // Free-agent back arc (Session 65 user spec): a role-2+ attacker
            // BEHIND the victim (|bearing| > ~120deg off its facing) is the
            // free agent working the back -- it swings through the front-busy
            // lockout. Still inside the front cone it's a redundant attacker
            // and keeps the 2-swing discipline (may target, may not attack).
            float flankerBear = 0f;
            bool behindVictim = false;
            if (role >= EngagementMap.RoleFlanker)
            {
                flankerBear = BearingOffVictimFacingDeg(victim);
                behindVictim = flankerBear > FlankerBackArcDeg;
            }
            bool flankerDenied = role >= EngagementMap.RoleFlanker && frontBusy && !behindVictim;

            if (role >= EngagementMap.RoleFlanker)
            {
                int state = flankerDenied ? 3 : (frontBusy ? 2 : 1);
                if (state != _gswingState && _gswingLogCount < MaxGswingLogs)
                {
                    _gswingLogCount++;
                    string reason = state == 3 ? "denied-front" : (state == 2 ? "back-arc" : "front-free");
                    CombatAiLog.Log($"[GSWING] Agent #{Agent.Index}: flanker swing {(flankerDenied ? "DENIED" : "allowed")} reason={reason} -- front slot {(frontBusy ? "committed" : "free")}, bearing={flankerBear:F0} on victim #{victim.Index}.");
                }
                _gswingState = state;
            }
            else
                _gswingState = 0;

            Agent.MovementControlFlag fsm = ComputeDuelFlag(dt);
            if (_dodgeTime > 0f)
                return fsm; // dodge step owns the body -- no slot steering mid-step
            Agent.MovementControlFlag combat = fsm & ~Agent.MovementControlFlag.MoveMask;
            // 2-swing rule: a flanker may only swing while NO front attacker
            // of the same victim is winding/releasing -- strip its attack
            // bits but let it keep steering (it may swing "if it wants" only
            // when a front slot is free -- OR it's the free agent already
            // behind the victim, see above).
            if (flankerDenied)
                combat &= ~AttackBits;
            // No flags while our own release is in flight.
            if (Agent.GetCurrentActionStage(1) == Agent.ActionStage.AttackRelease)
                return combat;
            return combat | GroupSlotMoveFlags(victim, role, _coAttackersBuf);
        }

        // Contact distance for the advance gate: the line stands just OUTSIDE
        // real melee reach of the enemy centroid. Farther than this from our
        // own victim = still marching, not fighting.
        private float GroupContactRange() => MeleeReach() * 2.5f + 1f;

        // Refresh the advance inputs (~0.3s cadence -- no per-tick field
        // scans): the live AI teammates (self included, Index-sorted for
        // stable slot ranks) and the live-enemy centroid.
        private void RefreshGroupAdvance()
        {
            if (_brainClock - _advRefreshAt < AdvRefreshSeconds)
                return;
            _advRefreshAt = _brainClock;
            _advTeam.Clear();
            Vec2 sum = Vec2.Zero;
            int n = 0;
            Mission m = Agent.Mission;
            if (m != null)
            {
                foreach (Agent a in m.Agents)
                {
                    if (a == null || !CombatLive(a) || !a.IsHuman)
                        continue;
                    if (a == Agent)
                    {
                        _advTeam.Add(a);   // self counts in the rank/spacing math
                        continue;
                    }
                    if (a.IsAIControlled && a.Team == Agent.Team)
                        _advTeam.Add(a);
                    else if (Agent.IsEnemyOf(a))
                    {
                        sum += a.Position.AsVec2;
                        n++;
                    }
                }
            }
            _advEnemyCount = n;
            if (n > 0)
                _advEnemyCentroid = sum * (1f / n);
            _advTeam.Sort((x, y) => x.Index.CompareTo(y.Index));
        }

        // Steering during the line advance. Slot = the line anchor (contact
        // distance in front of the enemy centroid) + our perpendicular rank
        // offset, so the team dresses into a rank of five facing the enemy.
        // A member more than ~AdvAheadMeters ahead of the team's mean
        // projection waits -- only its lateral slot error is driven -- so the
        // line arrives together. Single-axis flags only (no diagonals); the
        // goal dir is vetted through the shared clearance ring + the stuck
        // watchdog (teammate bodies ARE the main obstacle in a line).
        private Agent.MovementControlFlag GroupAdvanceFlag(Agent victim)
        {
            if (_advEnemyCount <= 0)
                return Agent.MovementControlFlag.None;   // nobody left -- hold position
            Vec2 me = Agent.Position.AsVec2;
            Vec2 toE = _advEnemyCentroid - me;
            if (toE.LengthSquared < 0.01f)
                return Agent.MovementControlFlag.None;
            toE.Normalize();
            Vec2 dir = toE;
            Vec2 perp = new Vec2(-dir.y, dir.x);     // left of the advance axis

            int rank = 0;
            for (int i = 0; i < _advTeam.Count; i++)
                if (_advTeam[i].Index < Agent.Index)
                    rank++;
            float offset = (rank - (_advTeam.Count - 1) * 0.5f) * AdvSlotSpacing;
            Vec2 anchor = _advEnemyCentroid - dir * GroupContactRange();
            Vec2 slot = anchor + perp * offset;

            // Hold-the-line: how far ahead of the team's mean projection on
            // `dir` we are. A runaway leader strips the forward drive and only
            // fixes its lateral slot error until the line catches up.
            float projMe = Vec2.DotProduct(me - anchor, dir);
            float projSum = 0f;
            int projN = 0;
            for (int i = 0; i < _advTeam.Count; i++)
            {
                if (!CombatLive(_advTeam[i]))
                    continue;
                projSum += Vec2.DotProduct(_advTeam[i].Position.AsVec2 - anchor, dir);
                projN++;
            }
            float teamProj = projN > 0 ? projSum / projN : projMe;
            Vec2 goal = slot;
            if (projMe - teamProj > AdvAheadMeters)
                goal = me + perp * Vec2.DotProduct(slot - me, perp);

            Vec2 toGoal = goal - me;
            float dGoal = toGoal.Length;
            if (dGoal < 0.40f)
                return Agent.MovementControlFlag.None;   // on slot -- stand in line
            float inv = 1f / dGoal;
            Vec2 gdir = new Vec2(toGoal.x * inv, toGoal.y * inv);
            // Feed the watchdog so a commanded march dir that produced no real
            // displacement gets learned as blocked (same lever as the slot code).
            _atkWatch.Note(me, gdir, _brainClock, false, out Vec2 _);
            if (!AtkDirOk(gdir, dGoal))
            {
                // Slot dir blocked: sidestep along the line toward the more
                // open side; fully walled -> hold in place.
                Vec2 pR = new Vec2(-perp.x, -perp.y);
                bool lOk = AtkDirOk(perp, 1f), rOk = AtkDirOk(pR, 1f);
                if (lOk && (!rOk || _atkClear.Free(Agent, perp, _brainClock) >= _atkClear.Free(Agent, pR, _brainClock)))
                    gdir = perp;
                else if (rOk)
                    gdir = pR;
                else
                    return Agent.MovementControlFlag.None;
            }

            // Facing-axis flags. The combat lock sits on the victim, so
            // Forward = toward it; use that as the march frame's forward axis
            // (dir-to-centroid when the victim has no flat dir).
            Vec3 toV3 = DirToFlat(victim);
            Vec2 fwd = toV3.LengthSquared < 0.01f ? dir : new Vec2(toV3.x, toV3.y);
            Vec2 right = new Vec2(fwd.y, -fwd.x);
            float fComp = Vec2.DotProduct(gdir, fwd);
            float rComp = Vec2.DotProduct(gdir, right);
            if (MathF.Abs(fComp) >= MathF.Abs(rComp))
            {
                if (fComp > SparSlotDirThreshold) return Agent.MovementControlFlag.Forward;
                if (fComp < -SparSlotDirThreshold) return Agent.MovementControlFlag.Backward;
                return Agent.MovementControlFlag.None;
            }
            if (rComp > SparSlotDirThreshold) return Agent.MovementControlFlag.StrafeRight;
            if (rComp < -SparSlotDirThreshold) return Agent.MovementControlFlag.StrafeLeft;
            return Agent.MovementControlFlag.None;
        }

        // Outnumbered defender (2+ attackers, or 1 + the player engaging):
        // "grace" per spec -- NOT zombie-tracked: movement stays free flags,
        // and the lock goes to the most-FRONT attacker (Session 64 spec -- a
        // side lock leaves the free side attacker undefended). The player
        // keeps a bearing bonus so he stays in front when he's one of the
        // threats. Feints already halved via _feintRateScale.
        private Agent.MovementControlFlag ComputeOutnumberedFlag(float dt, Agent fallback, bool playerOnMe, Mission m)
        {
            _attackersBuf.Clear();
            EngagementMap.AttackersOf(Agent.Index, _attackersBuf, m);
            Agent player = m?.MainAgent;
            bool playerLive = playerOnMe && CombatLive(player);

            // FREE-AGENT partition (Session 65 user spec): a role-2+ attacker
            // ON ME is the free agent -- it works my back and is unbound from
            // my view cone. It is EXCLUDED from the front-lock candidates
            // (chasing/locking a back-stabber is exactly how the front gets
            // exposed) but stays in _attackersBuf, so the N-threat side-block
            // evaluation and the threatActive scan still see its swings.
            _freeAgentsBuf.Clear();
            _frontAttackersBuf.Clear();
            _freeIdxs.Clear();
            foreach (Agent atk in _attackersBuf)
            {
                if (EngagementMap.TryGetAssignment(atk.Index, out EngagementMap.Assignment aa)
                    && aa.VictimIndex == Agent.Index
                    && aa.Role >= EngagementMap.RoleFlanker)
                {
                    _freeAgentsBuf.Add(atk);
                    _freeIdxs.Add(atk.Index);
                }
                else
                    _frontAttackersBuf.Add(atk);
            }
            _freeIdxs.Sort();
            if (!SameIdxSet(_freeIdxs, _lastFreeIdxs))
            {
                if (_gfreeLogCount < MaxGfreeLogs)
                {
                    _gfreeLogCount++;
                    string freeList = _freeIdxs.Count > 0 ? string.Join(",", _freeIdxs) : "none";
                    CombatAiLog.Log(_attackersBuf.Count >= 3
                        ? $"[GFREE] Agent #{Agent.Index}: {_attackersBuf.Count} attackers, free=#{freeList}."
                        : $"[GFREE] Agent #{Agent.Index}: free set -> {freeList} (attackers={_attackersBuf.Count}).");
                }
                _lastFreeIdxs.Clear();
                _lastFreeIdxs.AddRange(_freeIdxs);
            }

            Agent focus = PickFrontLock(_frontAttackersBuf, playerLive ? player : null,
                out Vec2 threatCentroid, out float lockBearing, out int threatN);
            _forcedDuelTarget = focus ?? fallback;
            _sparThreats = playerLive; // accept the player as a threat even if the team check would skip him
            int lockIdx = focus != null ? focus.Index : -1;
            if (lockIdx != _groupLockIdx && _glockLogCount < MaxGlockLogs)
            {
                _glockLogCount++;
                CombatAiLog.Log($"[GLOCK] Agent #{Agent.Index}: lock #{_groupLockIdx} -> #{lockIdx} bearing={lockBearing:F0} threats={threatN}.");
            }
            _groupLockIdx = lockIdx;

            // Torso jab at a NON-LOCKED attacker in front (spar-jab lever):
            // a quick hit on the adjacent enemy without giving up the lock.
            if (TryGroupJab(_forcedDuelTarget, _attackersBuf, playerLive ? player : null,
                    out Agent.MovementControlFlag jab))
                return jab;

            Agent.MovementControlFlag fsm = ComputeDuelFlag(dt);
            if (_dodgeTime > 0f)
                return fsm; // dodge step owns the body -- no free-space strafe mid-step
            // Free space: never stand still against a swarm -- if the FSM gave
            // no move bits, add a strafe so the defender keeps making room.
            // The direction now comes from the clearance ring (more free
            // space / away from the threat centroid) instead of a coin flip.
            // NOT while a threat is winding/releasing, though -- strafing
            // mid-swing is exactly how the back gets exposed (Session 19).
            bool threatActive = false;
            foreach (Agent atk in _attackersBuf)
                if (IsWindingOrReleasing(atk)) { threatActive = true; break; }
            if (!threatActive && IsWindingOrReleasing(player))
                threatActive = true;
            // Keep-at-bay (Session 65): the nearest free agent inside ~3m adds
            // its own repulsion to the free-space pick -- the defender drifts
            // away from the back-stabber while the two fronts stay in view.
            Vec2 awayFree = Vec2.Zero;
            Agent nearestFree = null;
            float freeD2 = float.MaxValue;
            foreach (Agent atk in _freeAgentsBuf)
            {
                if (!CombatLive(atk))
                    continue;
                float d2 = atk.Position.DistanceSquared(Agent.Position);
                if (d2 < freeD2) { freeD2 = d2; nearestFree = atk; }
            }
            if (nearestFree != null && freeD2 < FreeAgentBayMeters * FreeAgentBayMeters)
                awayFree = FlatUnit(Agent.Position.AsVec2 - nearestFree.Position.AsVec2);
            if (!threatActive && (fsm & Agent.MovementControlFlag.MoveMask) == 0)
                fsm |= GroupFreeSpaceFlag(threatCentroid, awayFree);
            return fsm;
        }

        // Front-cone lock (Session 64 user spec): the attacker with the
        // smallest |bearing from our facing| wins; bearings inside
        // FrontLockTieDeg tie-break on the most threatening stage (Release >
        // Ready > other) then nearest. The player gets a bearing bonus so a
        // flanking AI can't pull the lock off him on a near-tie. Also emits
        // the threat centroid for the free-space strafe.
        private Agent PickFrontLock(List<Agent> attackers, Agent player,
            out Vec2 centroid, out float lockBearing, out int threatN)
        {
            centroid = Vec2.Zero;
            lockBearing = 0f;
            threatN = 0;
            Vec2 f = Agent.Frame.rotation.f.AsVec2;
            if (f.LengthSquared < 0.0001f)
                f = Agent.GetMovementDirection();
            if (f.LengthSquared < 0.0001f)
                return null;
            f.Normalize();

            Agent best = null;
            float bestBear = float.MaxValue, bestD2 = float.MaxValue;
            int bestStage = -1;
            int n = 0;
            Vec2 sum = Vec2.Zero;   // local: `centroid` is an out param -- can't be captured by Consider
            Vec2 me = Agent.Position.AsVec2;
            void Consider(Agent atk)
            {
                if (!CombatLive(atk))
                    return;
                n++;
                sum += atk.Position.AsVec2;
                Vec2 to = atk.Position.AsVec2 - me;
                float d2 = to.LengthSquared;
                if (d2 < 0.0001f)
                    return;
                float cross = f.x * to.y - f.y * to.x;
                float dot = f.x * to.x + f.y * to.y;
                float bear = MathF.Abs(MathF.Atan2(cross, dot)) * 57.2958f;
                if (atk.IsMainAgent)
                    bear -= PlayerBearingBonusDeg;
                Agent.ActionStage s = atk.GetCurrentActionStage(1);
                int st = s == Agent.ActionStage.AttackRelease ? 2
                    : (s == Agent.ActionStage.AttackReady || s == Agent.ActionStage.AttackQuickReady) ? 1 : 0;
                bool better = best == null
                    || bear < bestBear - FrontLockTieDeg
                    || (bear <= bestBear + FrontLockTieDeg
                        && (st > bestStage || (st == bestStage && d2 < bestD2)));
                if (better)
                {
                    best = atk;
                    bestBear = bear;
                    bestStage = st;
                    bestD2 = d2;
                }
            }
            if (attackers != null)
                foreach (Agent a in attackers)
                    Consider(a);
            if (CombatLive(player) && (attackers == null || !attackers.Contains(player)))
                Consider(player);
            threatN = n;
            if (n > 0)
                centroid = sum * (1f / n);
            if (best != null)
                lockBearing = bestBear;
            return best;
        }

        // Free-space step for the outnumbered defender: score each facing-
        // axis escape dir as ring-free-space + a pull away from the threat
        // centroid; the best one wins (Backward included -- a straight
        // retreat is sometimes the only real gap). Session 65: when a free
        // agent is inside the bay radius, `awayFromFree` adds a second
        // repulsion term (weight 1.0) so the defender drifts off the
        // back-stabber while keeping the front attackers in view.
        private Agent.MovementControlFlag GroupFreeSpaceFlag(Vec2 threatCentroid, Vec2 awayFromFree)
        {
            Vec2 f = Agent.Frame.rotation.f.AsVec2;
            if (f.LengthSquared < 0.0001f)
                f = Agent.GetMovementDirection();
            if (f.LengthSquared < 0.0001f)
                return DuelRng.Next(2) == 0
                    ? Agent.MovementControlFlag.StrafeLeft
                    : Agent.MovementControlFlag.StrafeRight;   // old coin-flip fallback
            f.Normalize();
            Vec2 away = FlatUnit(Agent.Position.AsVec2 - threatCentroid);
            Vec2 left = new Vec2(-f.y, f.x), right = new Vec2(f.y, -f.x), back = new Vec2(-f.x, -f.y);
            float sL = StrafeScore(left, away);
            float sR = StrafeScore(right, away);
            float sB = StrafeScore(back, away);
            Agent.MovementControlFlag pick = BestFreeDir(sL, sR, sB);
            if (awayFromFree.LengthSquared < 0.0001f)
                return pick;   // no free agent inside the bay radius
            sL += left.x * awayFromFree.x + left.y * awayFromFree.y;    // +1.0 * dot
            sR += right.x * awayFromFree.x + right.y * awayFromFree.y;
            sB += back.x * awayFromFree.x + back.y * awayFromFree.y;
            Agent.MovementControlFlag bayPick = BestFreeDir(sL, sR, sB);
            if (bayPick != pick && _gfreeLogCount < MaxGfreeLogs)
            {
                _gfreeLogCount++;
                CombatAiLog.Log($"[GFREE] Agent #{Agent.Index}: bay move veto {pick} -> {bayPick} (free agent inside {FreeAgentBayMeters:F0}m).");
            }
            return bayPick;
        }

        private static Agent.MovementControlFlag BestFreeDir(float sL, float sR, float sB)
        {
            if (sL >= sR && sL >= sB)
                return Agent.MovementControlFlag.StrafeLeft;
            return sR >= sB ? Agent.MovementControlFlag.StrafeRight : Agent.MovementControlFlag.Backward;
        }

        private float StrafeScore(Vec2 dir, Vec2 away)
            => _atkClear.Free(Agent, dir, _brainClock)
               + 0.7f * (dir.x * away.x + dir.y * away.y);

        // |bearing| from `victim`'s facing to OUR position: 0 = dead ahead of
        // it, 180 = square behind. Same atan2(cross,dot) pattern as the slot
        // code; used by the flanker free-agent back-arc rule.
        private float BearingOffVictimFacingDeg(Agent victim)
        {
            Vec2 f = DefenderFacing(victim);
            Vec2 toMe = Agent.Position.AsVec2 - victim.Position.AsVec2;
            if (f.LengthSquared < 0.0001f || toMe.LengthSquared < 0.0001f)
                return 0f;
            f.Normalize();
            toMe.Normalize();
            float cross = f.x * toMe.y - f.y * toMe.x;
            float dot = f.x * toMe.x + f.y * toMe.y;
            return MathF.Abs(MathF.Atan2(cross, dot)) * 57.2958f;
        }

        private static bool SameIdxSet(List<int> a, List<int> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        // Group torso-jab (Session 64, same lever/gating as the spar jab): a
        // NON-LOCKED enemy inside ~45deg of facing and within reach gets a
        // held attack flag (~0.5s) from the torso arc instead of a relock.
        // Aborts instantly when a real threat releases at us -- blocking
        // beats jabbing. Returns true when it owns this tick's output.
        private bool TryGroupJab(Agent locked, List<Agent> prefer, Agent extra,
            out Agent.MovementControlFlag flag)
        {
            flag = Agent.MovementControlFlag.None;
            if (_jabTimer > 0f)
            {
                if (CombatLive(_jabTarget) && FindAssignedReleaser(null) == null)
                {
                    if (_jabTimer > 0.9f)
                    {
                        Agent.MovementControlFlag jd = JabDirToward(_jabTarget);
                        if (jd != Agent.MovementControlFlag.None)
                        {
                            flag = jd;
                            return true;
                        }
                    }
                    _jabTimer = 0f;
                    _jabTarget = null;
                    return true;   // flag drops -> release
                }
                _jabTimer = 0f;
                _jabTarget = null;
            }
            // Candidate scan throttled to ~5Hz: the attacker path walks
            // Mission.Agents, and a group agent with no adjacent enemy would
            // otherwise rescan every tick while the cooldown sits open.
            if (_jabCooldown <= 0f && !OwnInReadyStage()
                && _brainClock - _jabScanAt >= 0.2f
                && FindAssignedReleaser(null) == null)
            {
                _jabScanAt = _brainClock;
                Agent t = GroupJabTarget(locked, prefer, extra);
                if (t != null)
                {
                    _jabTarget = t;
                    _jabCooldown = 5.0f;
                    _jabTimer = 1.4f;
                }
            }
            return false;
        }

        // Jab candidate: nearest live enemy (not our lock) inside the ~45deg
        // front cone and jab reach. `prefer` = the outnumbered defender's
        // attacker list (cheap); null = the attacker's field scan (runs only
        // while the 5s jab cooldown is open -- not per tick).
        private Agent GroupJabTarget(Agent locked, List<Agent> prefer, Agent extra)
        {
            Mission m = Agent.Mission;
            if (m == null)
                return null;
            Vec2 f = Agent.Frame.rotation.f.AsVec2;
            if (f.LengthSquared < 0.0001f)
                f = Agent.GetMovementDirection();
            if (f.LengthSquared < 0.0001f)
                return null;
            f.Normalize();
            float reach = MeleeReach() * 1.3f;
            float reachSq = reach * reach;
            Agent best = null;
            float bestD = float.MaxValue;
            if (prefer != null)
            {
                foreach (Agent a in prefer)
                    GroupJabCandidate(a, locked, f, reachSq, ref best, ref bestD);
                GroupJabCandidate(extra, locked, f, reachSq, ref best, ref bestD);
            }
            else
            {
                foreach (Agent a in m.Agents)
                {
                    if (a == null || !Agent.IsEnemyOf(a))
                        continue;
                    GroupJabCandidate(a, locked, f, reachSq, ref best, ref bestD);
                }
            }
            return best;
        }

        private void GroupJabCandidate(Agent a, Agent locked, Vec2 f, float reachSq,
            ref Agent best, ref float bestD)
        {
            if (a == null || a == Agent || a == locked || !CombatLive(a) || !a.IsHuman)
                return;
            Vec2 to = a.Position.AsVec2 - Agent.Position.AsVec2;
            float d2 = to.LengthSquared;
            if (d2 > reachSq || d2 >= bestD || d2 < 0.0001f)
                return;
            float d = (float)Math.Sqrt(d2);
            if (to.x * f.x + to.y * f.y < 0.7071f * d)   // outside the ~45deg front cone
                return;
            best = a;
            bestD = d2;
        }

        // Positioning slots (Session 64 user spec): role 0 steers dead ahead
        // of the victim's facing, role 1 takes the ~30deg off-angle on the
        // side OPPOSITE the role-0 attacker (left when role-0's side is
        // ambiguous), role 2+ keeps the back-arc slot. Both line attackers
        // stay inside the victim's front cone so it can see them. Steering
        // goes through the shared clearance ring: a blocked slot dir ->
        // steer at the victim -> perpendicular -> hold (same ladder as the
        // spar slot; teammate bodies clamp via the agent channel).
        private Agent.MovementControlFlag GroupSlotMoveFlags(Agent victim, int role, List<Agent> coAttackers)
        {
            Vec3 toV = victim.Position - Agent.Position;
            toV.z = 0f;
            if (toV.LengthSquared < 0.01f)
                return Agent.MovementControlFlag.None;
            toV.Normalize();

            Vec2 vFacing = DefenderFacing(victim);
            if (vFacing.LengthSquared < 0.0001f)
                vFacing = new Vec2(toV.x, toV.y); // fallback: they face us -> behind = our side, circle anyway
            vFacing.Normalize();

            Vec2 slotPos;
            if (role >= EngagementMap.RoleFlanker)
                slotPos = victim.Position.AsVec2 - vFacing * Math.Max(1.2f, MeleeReach() * 0.8f);
            else if (role == EngagementMap.RoleSupport)
                slotPos = SparSlotPos(victim, vFacing, GroupSupportSide(victim, vFacing, coAttackers));
            else
                slotPos = SparSlotPos(victim, vFacing, 0);   // dead ahead, ~reach*0.85

            Vec2 toSlot = slotPos - Agent.Position.AsVec2;
            float distToVictim = (victim.Position.AsVec2 - Agent.Position.AsVec2).Length;
            if (toSlot.LengthSquared > 0.0001f)
            {
                Vec2 sDir = toSlot;
                sDir.Normalize();
                Vec2 dDir = victim.Position.AsVec2 - Agent.Position.AsVec2;
                if (dDir.LengthSquared > 0.0001f)
                    dDir.Normalize();
                if (!AtkDirOk(sDir, toSlot.Length))
                {
                    if (AtkDirOk(dDir, distToVictim))
                        toSlot = dDir;
                    else
                    {
                        Vec2 pL = new Vec2(-sDir.y, sDir.x), pR = new Vec2(sDir.y, -sDir.x);
                        bool lOk = AtkDirOk(pL, 1f), rOk = AtkDirOk(pR, 1f);
                        if (lOk && rOk)
                            toSlot = _atkClear.Free(Agent, pL, _brainClock) >= _atkClear.Free(Agent, pR, _brainClock) ? pL : pR;
                        else if (lOk) toSlot = pL;
                        else if (rOk) toSlot = pR;
                        else return Agent.MovementControlFlag.None;
                    }
                }
            }
            // Never park outside our own reach: the duel FSM only initiates
            // inside it, so a slot beyond swing range is a permanent stall --
            // walk straight at the victim instead (same fix as the spar slot).
            if (distToVictim > MeleeReach() - 0.10f)
                toSlot = victim.Position.AsVec2 - Agent.Position.AsVec2;
            else if (toSlot.LengthSquared < SparSlotArriveMeters * SparSlotArriveMeters)
                return Agent.MovementControlFlag.None;
            toSlot.Normalize();

            Vec2 fwd = new Vec2(toV.x, toV.y);        // we face the victim while locked
            Vec2 right = new Vec2(fwd.y, -fwd.x);
            float fComp = Vec2.DotProduct(toSlot, fwd);
            float rComp = Vec2.DotProduct(toSlot, right);

            // Watchdog: learn blocked dirs from real displacement (exempt
            // mid-release / mid-dodge, same exemptions as the spar attacker).
            Agent.ActionStage st0 = Agent.GetCurrentActionStage(1);
            bool ex = st0 == Agent.ActionStage.AttackRelease || _dodgeTime > 0f
                || st0 == Agent.ActionStage.DefendParry;
            _atkWatch.Note(Agent.Position.AsVec2, toSlot, _brainClock, ex, out Vec2 _);

            Agent.MovementControlFlag flags = Agent.MovementControlFlag.None;
            if (fComp > SparSlotDirThreshold) flags |= Agent.MovementControlFlag.Forward;
            else if (fComp < -SparSlotDirThreshold) flags |= Agent.MovementControlFlag.Backward;
            if (rComp > SparSlotDirThreshold) flags |= Agent.MovementControlFlag.StrafeRight;
            else if (rComp < -SparSlotDirThreshold) flags |= Agent.MovementControlFlag.StrafeLeft;
            return flags;
        }

        // Which side of the victim's facing the ROLE-0 attacker occupies
        // (+1 = its left, -1 = right): the support attacker takes the
        // opposite side so both sit inside the front cone. Role-0 dead ahead
        // (or missing) -> ambiguous -> LEFT per spec.
        private int GroupSupportSide(Agent victim, Vec2 vFacing, List<Agent> coAttackers)
        {
            if (coAttackers != null)
            {
                foreach (Agent co in coAttackers)
                {
                    if (co == Agent || !CombatLive(co))
                        continue;
                    if (!EngagementMap.TryGetAssignment(co.Index, out EngagementMap.Assignment ca)
                        || ca.Role != EngagementMap.RolePrimary)
                        continue;
                    Vec2 to0 = co.Position.AsVec2 - victim.Position.AsVec2;
                    float l = to0.Length;
                    if (l < 0.01f)
                        break;
                    float cross = (vFacing.x * to0.y - vFacing.y * to0.x) / l;   // sin of bearing
                    if (cross > 0.35f)
                        return -1;   // role-0 on victim's left -> we go right
                    if (cross < -0.35f)
                        return 1;    // role-0 on its right -> we go left
                    break;           // dead ahead -> ambiguous
                }
            }
            return 1;   // ambiguous -> left
        }

        // PERFECTBLOCK EXPERIMENT (user design): pin the legs to the agent's
        // STARTING position+facing via a scripted frame (own position = frozen
        // legs is WANTED here), while the combat target lock aims the torso at
        // the player. Hypothesis: legs stay at start facing, the torso rotates
        // within its arc to block the player's swings -- the "legs elsewhere,
        // torso works" state, engineered deliberately instead of by accident.
        //
        // TARGET LOCK: rests on the player permanently now. The original
        // "release to null while idle" design produced exactly the target-null
        // agent state that correlates with every recorded crash -- removed.
        // The experiment already proved its point (real reactive parries),
        // so the lock simply stays on the player the whole time.
        private Agent.MovementControlFlag ComputePerfectBlockFlag(float dt)
        {
            _lastOp = "perfectblock:enter";
            Agent player = Agent.Mission?.MainAgent;
            if (CombatLive(player))
            {
                if (!_pbFrameCaptured)
                {
                    _pbFrameCaptured = true;
                    _pbPos = Agent.Position.AsVec2;
                    Vec2 md = Agent.GetMovementDirection();
                    _pbFacing = new Vec3(md.x, md.y, 0f);
                    CombatAiLog.Log($"[PERFECTBLOCK] Agent #{Agent.Index}: pinned start frame facing=({_pbFacing.x:F2},{_pbFacing.y:F2}).");
                }
                // The scripted frame persists until cleared -- one write
                // installs it; re-writing every frame is pure MBAPI churn.
                if (!_targetFrameSet)
                {
                    _lastOp = "SetTargetPosAndDir(pb)";
                    Agent.SetTargetPositionAndDirection(_pbPos, _pbFacing);
                    _targetFrameSet = true;
                }
                SetAutoTargeting(false); // don't let native re-pick a target while we're releasing ours

                if (_pbTargetHold > 0f)
                    _pbTargetHold -= dt;

                if (player.GetCurrentActionStage(1) == Agent.ActionStage.AttackRelease)
                {
                    EnsureTargetAgent(player); // snap torso to block THIS swing
                    _pbTargetHold = DefendTargetHoldSeconds;
                }
                else if (_pbTargetHold <= 0f)
                {
                    EnsureTargetAgent(player); // rest locked -- never null
                }
            }
            return ComputeReactiveDefendFlag();
        }

        // EVADE-ONLY mode (combatai.evade): the pure-dodge counterpart of
        // PerfectBlockMode. Every committed release that can reach us gets
        // answered with the step dodge -- no blocks, no attacks, no idle
        // locomotion. The dodge cooldown is deliberately NOT gated here (the
        // point is 100% evade); dedup is per swing-episode per threat so a
        // single release can't retrigger us mid-step and a second attacker
        // still gets stepped away from.
        private Agent.MovementControlFlag ComputeEvadeFlag(float dt)
        {
            _lastOp = "evade:enter";
            // Step in progress: locomotion owns the body.
            if (_dodgeTime > 0f)
                return TickDodgeStep(dt);
            if (_dodgeCooldown > 0f)
                _dodgeCooldown -= dt;
            // Same foreign-dodge guard as duel mode (missing here until
            // Session 31): if channel 0 is mid-dodge from something we didn't
            // start, don't fight it with our own SetActionChannel.
            if (IsForeignDodgePlaying())
            {
                CombatAiState.ForeignDodgeYieldCount++;
                return Agent.MovementControlFlag.None;
            }

            Agent threat = FindEvadeReleaser();
            if (threat == null)
            {
                if (_evadeDodged.Count > 0)
                    _evadeDodged.Clear();
                // Rest locked on a live opponent -- target-null is the crash
                // vector, and the torso keeps tracking them between swings.
                // No keep-away movement (user-tested: artificial spacing the
                // AI could never hold in a real fight -- evade is the answer,
                // not distance management).
                Agent rest = FindNearestEnemy();
                if (!CombatLive(rest))
                    rest = Agent.Mission?.MainAgent;
                if (CombatLive(rest))
                    EnsureTargetAgent(rest);
                return Agent.MovementControlFlag.None;
            }

            EnsureTargetAgent(threat); // torso tracks whoever is swinging
            if (!_evadeDodged.Contains(threat.Index))
            {
                _evadeDodged.Add(threat.Index);
                CombatAiState.DodgeOpportunityCount++;
                if (TryStartDodge(threat))
                    return DodgeMoveFlag() | _dodgeDefendFlag;
            }
            return Agent.MovementControlFlag.None;
        }

        // Evade-mode threat scan: the nearest agent (enemy OR the player)
        // in AttackRelease whose weapon can reach us right now. Release-
        // stage only, same trigger as the duel path -- the earlier held-
        // ready stab read let the player track and re-aim into the dodge.
        private Agent FindEvadeReleaser()
        {
            _lastOp = "scan:evade";
            Mission m = Agent.Mission;
            if (m == null)
                return null;
            Agent best = null;
            float bestDist = float.MaxValue;
            foreach (Agent a in m.Agents)
            {
                if (a == null || a == Agent || !CombatLive(a) || !a.IsHuman)
                    continue;
                if (a.IsMainAgent && IsSparPlayerAlly)
                    continue;
                if (!a.IsMainAgent && !Agent.IsEnemyOf(a))
                    continue;
                Agent.ActionStage s = a.GetCurrentActionStage(1);
                if (s != Agent.ActionStage.AttackRelease)
                    continue;
                float d = Agent.Position.DistanceSquared(a.Position);
                float reach = MeleeReachOf(a) + 0.8f;
                if (d > reach * reach || d >= bestDist)
                    continue;
                bestDist = d;
                best = a;
            }
            return best;
        }

        // Look-test v3: use SetTargetPositionAndDirection (the lever that DID move
        // the body) but with a REAL movement target (the player's position, not our
        // own) so the agent keeps moving instead of freezing. It decouples:
        //   targetPosition -> where the LEGS move (chase player)
        //   targetDirection -> where the BODY faces (oscillates +/-angle)
        // Verifies: (a) body turns to the offset facing, (b) no freeze (agent still
        // moves toward the player), (c) native recenters on release.
        private Agent.MovementControlFlag ComputeLookTestFlag(float dt)
        {
            _lastOp = "looktest:enter";
            Agent player = Agent.Mission?.MainAgent;
            if (!CombatLive(player))
                return Agent.MovementControlFlag.None;

            _lookTestTimer += dt;
            float cycle = LookTestHoldSeconds + LookTestReleaseSeconds;
            float t = _lookTestTimer % cycle;
            bool holding = t < LookTestHoldSeconds;

            if (holding && !_lookTestHoldActive)
            {
                _lookTestHoldActive = true;
                _lookTestDir = -_lookTestDir; // flip side each hold
                _lookTestBaseAngle = Agent.LookDirectionAsAngle; // self-consistent offset base

                // Read the ACTUAL arc boundary instead of guessing it from
                // trial and error (apidoc 1.4.8: Agent.GetBodyRotationConstraint
                // -- Vec2(min,max) radians, vanilla compares it against
                // LookDirection-angle minus MovementDirection-angle).
                Vec2 constraint = Agent.GetBodyRotationConstraint();
                CombatAiLog.Log($"[LOOKTEST] Agent #{Agent.Index}: bodyRotationConstraint=({constraint.x * 57.2958f:F1}deg,{constraint.y * 57.2958f:F1}deg) variant={CombatAiState.LookTestVariant}.");
            }
            if (!holding)
                _lookTestHoldActive = false;

            Vec3 toPlayer = player.Position - Agent.Position;
            toPlayer.z = 0f;
            if (toPlayer.LengthSquared < 0.01f)
                return Agent.MovementControlFlag.None;
            toPlayer.Normalize();

            float angle = holding ? _lookTestDir * LookTestAngleRadians : 0f;
            float c = (float)Math.Cos(angle);
            float s = (float)Math.Sin(angle);
            Vec3 facing = new Vec3(toPlayer.x * c - toPlayer.y * s, toPlayer.x * s + toPlayer.y * c, 0f);

            // CRITICAL DIFFERENCE from every earlier look test: the combat
            // target is RELEASED (auto-select off + target null). All previous
            // "this function does nothing" results ran while a locked target
            // pinned the whole facing system -- now nothing fights the write.
            // Exception: variant 4 tests the target lock ITSELF (snap/release).
            SetAutoTargeting(false);
            if (CombatAiState.LookTestVariant == 4)
            {
                EnsureTargetAgent(holding ? player : null);
                return Agent.MovementControlFlag.None;
            }
            EnsureTargetAgent(null);

            // Switching off the frame variant: release any scripted frame it
            // installed so it can't linger under the other variants.
            if (CombatAiState.LookTestVariant != 0 && _targetFrameSet)
                ReleaseFeintBodyTurn(null);

            switch (CombatAiState.LookTestVariant)
            {
                case 1: // LookDirectionAsAngle -- absolute yaw, wrapped to +-pi.
                    // BUG FIX (user: "unpredictable"): the base MUST be the
                    // LIVE angle to the player every frame (same as `facing`
                    // below for frame/poi/movedir), not a one-time snapshot
                    // from Agent.LookDirectionAsAngle captured at hold-start.
                    // The old base never updated, so during release it kept
                    // rewriting a STALE angle forever instead of tracking the
                    // player -- the oscillation point drifted away from
                    // wherever the agent/player actually were.
                    float playerAngle = (float)Math.Atan2(toPlayer.y, toPlayer.x);
                    Agent.LookDirectionAsAngle = NormalizeAngle(playerAngle + angle);
                    break;

                case 2: // SetLookToPointOfInterest -- point in rotated direction
                    if (holding)
                    {
                        Agent.SetLookToPointOfInterest(Agent.Position + facing * 5f);
                        _poiActive = true;
                    }
                    else if (_poiActive)
                    {
                        Agent.DisableLookToPointOfInterest();
                        _poiActive = false;
                    }
                    break;

                case 3: // SetMovementDirection -- the siege-pilot facing setter
                    _lastOp = "looktest:SetMoveDir";
                    Agent.SetMovementDirection(facing.AsVec2);
                    break;

                case 5: // torso feint test: frame pins LEGS (start pos+facing),
                        // look-angle aims the TORSO +/-15deg. If the channels
                        // are independent -> feet planted, shoulders swinging.
                    if (!_lookTestTorsoCaptured)
                    {
                        _lookTestTorsoCaptured = true;
                        _torsoPinPos = Agent.Position.AsVec2;
                        Vec2 md = Agent.GetMovementDirection();
                        _torsoPinFacing = new Vec3(md.x, md.y, 0f);
                        CombatAiLog.Log($"[LOOKTEST] Agent #{Agent.Index}: torso variant -- legs pinned, angle base={_lookTestBaseAngle:F2}.");
                    }
                    _lastOp = "looktest:SetTargetPosDir(torso)";
                    Agent.SetTargetPositionAndDirection(_torsoPinPos, _torsoPinFacing);
                    _targetFrameSet = true;
                    Agent.LookDirectionAsAngle = NormalizeAngle(_lookTestBaseAngle + angle);
                    break;

                default: // 0 = frame: stationary in-place rotation (proven)
                    _lastOp = "looktest:SetTargetPosDir";
                    Agent.SetTargetPositionAndDirection(Agent.Position.AsVec2, facing);
                    _targetFrameSet = true;
                    break;
            }

            return Agent.MovementControlFlag.None;
        }

        // FaceFrame experiment (combatai.faceframe): while the combat lock
        // stays held (EnsureTargetAgent EVERY frame -- the target is never
        // null, which is exactly what makes this crash-safe), a formation
        // frame asks for a facing FaceFrameAngleDeg off the agent->player
        // bearing. Frame position = own position ("arrived" every tick -> no
        // drag, stationary test), velocity zero. Reuses the
        // torsomove/duelblockmove formation-frame channel (_tmFormationSet,
        // TorsoMoveCleanup teardown, IsFormationFrameEnabled stomp count) --
        // the 5998 FrameInUseByActiveMode cleanup guards only the SCRIPTED
        // frame (_targetFrameSet), so it needs no faceframe entry, same as
        // those modes.
        // `lock self` (FaceFrameLockSelf, the DEFAULT) is the Option-Z
        // experiment: the agent locks onto ITSELF -- a self-lock has no
        // valid facing direction to pull toward, so the formation frame's
        // direction may finally own facing. `lock player` = the control
        // group (legs known to pin to the lock bearing). The frame command
        // is unchanged in both: a facing off the agent->player bearing.
        // `lock none` (FaceFrameLockNone): NO lock writes AT ALL -- neither
        // EnsureTargetAgent nor SetTargetAgent runs, so the combat target
        // slot stays exactly as-is (combatai.unlock's poked release leaves
        // it at -1). SetAutoTargeting(false) still runs every frame: a
        // native re-lock would poison the test. Same frame command, same
        // [FACEFRAME] log with lock=none -- legs/look converging to `frame`
        // = frame owns facing with the lock truly absent.
        // [FACEFRAME] logs ~0.5s: off/factor, agent->player bearing `bear`
        // (kept in self/none mode -- if legs still track it, facing isn't
        // free), the commanded `frame` bearing, ground-truth
        // GetMovementDirection (legs) + `dLegs` drift-per-log-step +
        // LookDirectionAsAngle (torso) bearings, |error vs frame|, and
        // whether the lock took (lock=self / lock=#idx / lock=? /
        // lock=none / lock=none#idx = stale slot under none). Verdict:
        // legs/look converge to `frame` = frame owns facing (FREE FACING
        // ACHIEVED); legs drifts or frozen = lock's pull gone but frame
        // doesn't own facing; legs still track `bear` = self-lock doesn't
        // release facing (option Z dead -> hook route). [FACEFRAME] act=...
        // logs channel-0 action/stage CHANGES (bounded 40) -- self-lock may
        // put the body in weird states (no-fight, jitter). angle 180 = face
        // directly away from the player bearing.
        private Agent.MovementControlFlag ComputeFaceFrameFlag(float dt)
        {
            _lastOp = "faceframe:enter";
            bool noneLock = CombatAiState.FaceFrameLockNone;
            bool selfLock = CombatAiState.FaceFrameLockSelf && !noneLock;
            Agent player = Agent.Mission?.MainAgent;
            // Self-lock targets OURSELVES: the driven agent must be combat-
            // live or EnsureTargetAgent would try to lock a dying body.
            // Turn the mode off rather than drive it (hard invariant: NEVER
            // a null/dying target write).
            if (!CombatLive(Agent))
            {
                CombatAiState.FaceFrameMode = false;
                CombatAiState.FaceFrameAgentIndex = -1;
                CombatAiLog.Log($"[FACEFRAME] Agent #{Agent.Index}: self dying/gone -- mode OFF.");
                return Agent.MovementControlFlag.None;
            }
            if (!CombatLive(player))
            {
                // Lock target (player mode) / bearing reference (self mode)
                // died or left -- the experiment is over. Turn the mode off
                // rather than drive a null target (hard invariant: NEVER a
                // null target in melee).
                CombatAiState.FaceFrameMode = false;
                CombatAiState.FaceFrameAgentIndex = -1;
                CombatAiLog.Log($"[FACEFRAME] Agent #{Agent.Index}: player gone -- mode OFF.");
                return Agent.MovementControlFlag.None;
            }

            _brainClock += dt;       // this path never runs the duel FSM clock
            SetAutoTargeting(false); // deterministic lock: native can't re-pick
            // THE lock under test -- held every frame. `lock none` now owns
            // the release itself: EnsureArmed + ReleaseCombatTarget once on
            // engage, then a 0.5s poll re-releases any re-lock through the
            // guarded path (never a raw SetTargetAgent(null)). If the guard
            // can't arm we write NOTHING -- the slot stays as-is and the
            // [FACEFRAME] line's lock=none#idx says so.
            if (!noneLock)
                EnsureTargetAgent(selfLock ? Agent : player);
            else
            {
                if (!_ffReleased)
                {
                    _ffReleased = true;
                    if (NativeTargetGuard.EnsureArmed())
                    {
                        _lastOp = "faceframe:ReleaseCombatTarget";
                        bool ok = NativeTargetGuard.ReleaseCombatTarget(Agent);
                        CombatAiLog.Log($"[FACEFRAME] Agent #{Agent.Index}: lock none -- ReleaseCombatTarget -> {(ok ? "OK" : "FAILED")} (see [NH]).");
                    }
                    else
                        CombatAiLog.Log($"[FACEFRAME] Agent #{Agent.Index}: lock none but guard UNARMED -- target left as-is (legacy).");
                }
                else if (_brainClock - _ffRelockCheckAt >= 0.5f)
                {
                    _ffRelockCheckAt = _brainClock;
                    Agent cur2 = Agent.GetTargetAgent();
                    if (Live(cur2) && NativeTargetGuard.GuardedRelease)
                    {
                        _lastOp = "faceframe:re-release";
                        NativeTargetGuard.ReleaseCombatTarget(Agent);
                        CombatAiLog.Log($"[FACEFRAME] Agent #{Agent.Index}: RELOCKED nativeTgt=#{cur2.Index} -> re-released.");
                    }
                }
            }

            if (_tmFormationSet && !Agent.IsFormationFrameEnabled)
                _tmStomps++;         // engine stomped our frame since last tick

            Vec2 toP = player.Position.AsVec2 - Agent.Position.AsVec2;
            if (toP.LengthSquared < 0.0001f)
                return Agent.MovementControlFlag.None;   // on top of the player: no bearing to offset
            float bear = MathF.Atan2(toP.y, toP.x);
            if (CombatAiState.FaceFrameSpin)
            {
                _ffSpinAngle += 45f * dt;
                if (_ffSpinAngle >= 360f)
                    _ffSpinAngle -= 360f;
            }
            float offDeg = CombatAiState.FaceFrameSpin ? _ffSpinAngle : CombatAiState.FaceFrameAngleDeg;
            float fa = bear + offDeg * 0.0174533f;
            Vec2 frameDir = new Vec2(MathF.Cos(fa), MathF.Sin(fa));

            // WRITER (Session 69): who owns the commanded facing this tick.
            // frame    = SetFormationFrameEnabled (the proven baseline).
            // lookdir  = Agent.LookDirection (set_look_direction: writes ONLY
            //            the +0x474 dir + derived +0x484 angle -- never the
            //            +0x4a4 movement slot, native-verified).
            // lookangle= Agent.LookDirectionAsAngle (+0x484 = atan2f(-x,y):
            //            angle 0 faces +Y, sign verified in FUN_1806f2550 /
            //            FUN_1806f4e40; a one-time set->get round-trip logs it).
            // lookdir/lookangle never touch the formation frame; a leftover
            // frame from `writer frame` is disabled exactly once.
            CombatAiState.FaceFrameWriterT writer = CombatAiState.FaceFrameWriter;
            if (writer == CombatAiState.FaceFrameWriterT.Frame)
            {
                _lastOp = "FormationFrame(face)";
                Agent.SetFormationFrameEnabled(
                    new TaleWorlds.Engine.WorldPosition(Mission.Current.Scene, UIntPtr.Zero, Agent.Position, false),
                    frameDir, Vec2.Zero, CombatAiState.FaceFrameFactor);
                _tmFormationSet = true;
            }
            else
            {
                if (_tmFormationSet)
                {
                    _lastOp = "SetFormationFrameDisabled(ff)";
                    Agent.SetFormationFrameDisabled();
                    _tmFormationSet = false;
                }
                if (writer == CombatAiState.FaceFrameWriterT.LookDir)
                {
                    _lastOp = "LookDirection(ff)";
                    float z = Agent.LookDirection.z;
                    Agent.LookDirection = new Vec3(frameDir.x, frameDir.y, z);
                }
                else // LookAngle
                {
                    // Native: +0x484 = atan2f(-dir.x, dir.y). Write that
                    // convention, then read the managed getter back once.
                    float wantA = MathF.Atan2(-frameDir.x, frameDir.y);
                    _lastOp = "LookDirectionAsAngle(ff)";
                    Agent.LookDirectionAsAngle = NormalizeAngle(wantA);
                    if (!_ffLookRtLogged)
                    {
                        _ffLookRtLogged = true;
                        float gotA = Agent.LookDirectionAsAngle;
                        Vec3 got = Agent.LookDirection;
                        float back = MathF.Atan2(got.y, got.x) * 57.2958f;
                        CombatAiLog.Log($"[FACEFRAME] Agent #{Agent.Index}: lookangle round-trip " +
                            $"set={wantA * 57.2958f:F1} get={gotA * 57.2958f:F1} " +
                            $"dir=({got.x:F2},{got.y:F2},{got.z:F2}) worldBear={back:F0} " +
                            $"(want dir=({frameDir.x:F2},{frameDir.y:F2}) bear={fa * 57.2958f:F0}).");
                    }
                }
            }

            // MOVE (Session 69): facing-relative MovementInputVector (x=strafe
            // right, y=forward) -- the +0x4a4 slot the frame/lever modes never
            // shared. _tmInputVec mirrors it into the OnAIInputSet ref channel
            // so the ~1Hz callback can't stomp the write (same pattern as
            // duelblockmove=input). No frame / SetMovementDirection / scripted
            // frame anywhere in this path.
            Vec2 mv = Vec2.Zero;
            switch (CombatAiState.FaceFrameMove)
            {
                case CombatAiState.FaceFrameMoveT.Strafe: mv = new Vec2(0.8f, 0f); break;
                case CombatAiState.FaceFrameMoveT.Fwd:    mv = new Vec2(0f, 1f); break;
                case CombatAiState.FaceFrameMoveT.Back:   mv = new Vec2(0f, -0.6f); break;
                case CombatAiState.FaceFrameMoveT.Circle:
                    _ffMoveAngle += 1.5708f * dt;   // ~4s period
                    mv = new Vec2(MathF.Sin(_ffMoveAngle), MathF.Cos(_ffMoveAngle));
                    break;
            }
            if (mv.LengthSquared > 0.0001f || _ffMoveVec.LengthSquared > 0.0001f)
            {
                _lastOp = "MovementInputVector(ff)";
                _tmInputVec = mv;
                _ffMoveVec = mv;
                Agent.MovementInputVector = mv;
            }

            // Action-change diagnostic (bounded 40): self-lock may put the
            // body in weird states (no-fight idle, jitter) -- log only on
            // CHANGE so a steady state isn't spammed.
            Agent.ActionCodeType act0 = Agent.GetCurrentActionType(0);
            Agent.ActionStage stage0 = Agent.GetCurrentActionStage(0);
            if (_ffActSeen && (act0 != _ffLastAct0 || stage0 != _ffLastStage0)
                && _ffActLogCount < MaxFfActLogs)
            {
                _ffActLogCount++;
                CombatAiLog.Log($"[FACEFRAME] Agent #{Agent.Index}: act={_ffLastAct0}->{act0} " +
                    $"stage={_ffLastStage0}->{stage0} lock={(noneLock ? "none" : selfLock ? "self" : "player")}.");
            }
            _ffLastAct0 = act0;
            _ffLastStage0 = stage0;
            _ffActSeen = true;

            if (_ffLogCount < MaxFfLogs && _brainClock - _ffLastLogAt >= 0.5f)
            {
                _ffLastLogAt = _brainClock;
                _ffLogCount++;
                Vec2 legs = Agent.GetMovementDirection();
                float legsDeg = legs.LengthSquared > 0.0001f ? MathF.Atan2(legs.y, legs.x) * 57.2958f : -999f;
                float lookDeg = Agent.LookDirectionAsAngle * 57.2958f;
                float bearDeg = bear * 57.2958f;
                float frameDeg = fa * 57.2958f;
                float errLegs = legsDeg > -900f
                    ? MathF.Abs(NormalizeAngle((frameDeg - legsDeg) * 0.0174533f)) * 57.2958f : -1f;
                float errLook = MathF.Abs(NormalizeAngle((frameDeg - lookDeg) * 0.0174533f)) * 57.2958f;
                // dLegs: facing drift since the last log step -- following
                // the frame = steady drift toward frameDeg; free-but-idle =
                // ~0; still lock-pulled = drift toward `bear` instead.
                bool dLegsOk = legsDeg > -900f && _ffPrevLegsDeg > -900f;
                float dLegs = dLegsOk
                    ? NormalizeAngle((legsDeg - _ffPrevLegsDeg) * 0.0174533f) * 57.2958f : 0f;
                _ffPrevLegsDeg = legsDeg;
                Agent cur = Agent.GetTargetAgent();   // read-only probe -- even under `lock none` it catches a stale slot
                string lockStr = noneLock
                    ? (Live(cur) ? $"none#{cur.Index}" : "none")  // none#idx = slot still holds a target (unlock it)
                    : !Live(cur) ? "?" : cur.Index == Agent.Index ? "self" : "#" + cur.Index;
                // vel rel: measured velocity direction relative to legs yaw
                // (strafe cmd should read ~+/-90, fwd ~0, back ~180).
                Vec2 vel = Agent.GetCurrentVelocity();
                float velSpd = vel.Length;
                float velRel = velSpd > 0.05f && legsDeg > -900f
                    ? NormalizeAngle((MathF.Atan2(vel.y, vel.x) * 57.2958f - legsDeg) * 0.0174533f) * 57.2958f : -999f;
                string extra = $"writer={CombatAiState.FaceFrameWriter} cmdDir={frameDeg:F0} " +
                    $"mv=({_tmInputVec.x:F2},{_tmInputVec.y:F2}) vel={velSpd:F2} velRel={(velRel > -900f ? velRel.ToString("F0") : "na")} " +
                    $"nativeTgt={(Live(cur) ? "#" + cur.Index : "-1")}";
                if (selfLock || noneLock)
                {
                    CombatAiLog.Log($"[FACEFRAME] Agent #{Agent.Index}: off={offDeg:F0} factor={CombatAiState.FaceFrameFactor:F2} " +
                        $"bear={bearDeg:F0} frame={frameDeg:F0} legs={legsDeg:F0} dLegs={(dLegsOk ? dLegs.ToString("F0") : "na")} " +
                        $"look={lookDeg:F0} errLegs={errLegs:F0} errLook={errLook:F0} ffOn={Agent.IsFormationFrameEnabled} " +
                        $"stomps={_tmStomps} lock={lockStr} {extra}");
                }
                else
                {
                    CombatAiLog.Log($"[FACEFRAME] Agent #{Agent.Index}: off={offDeg:F0} factor={CombatAiState.FaceFrameFactor:F2} " +
                        $"bear={bearDeg:F0} frame={frameDeg:F0} legs={legsDeg:F0} look={lookDeg:F0} " +
                        $"errLegs={errLegs:F0} errLook={errLook:F0} ffOn={Agent.IsFormationFrameEnabled} " +
                        $"stomps={_tmStomps} lock={lockStr} {extra}");
                }
            }
            return Agent.MovementControlFlag.None;   // no move flags: the frame owns the legs, standing test
        }

        // Rotate the whole body by `angle` radians IN PLACE: movement target =
        // own position (arrived -> no stepping, and movement is suppressed
        // inside combat actions anyway), so only the stationary FACING path
        // runs -- the body rotates without walking. angle=0 recenters exactly
        // on the target (used in FeintReady so the release lands).
        // Rate-limited SetTargetPositionAndDirection for the own-position pin:
        // the scripted frame PERSISTS between writes, so re-writing a facing
        // that moved <~1deg is pure MBAPI churn for zero visual change. Part of
        // the per-frame native-call reduction (crash hardening).
        private Vec3 _lastPinDir;
        private void PinFacing(Vec3 dir)
        {
            if (dir.LengthSquared < 0.01f)
                return;
            if (!_targetFrameSet || (dir - _lastPinDir).LengthSquared > 0.0004f)
            {
                _lastOp = "SetTargetPosAndDir(pin)";
                Agent.SetTargetPositionAndDirection(Agent.Position.AsVec2, dir);
                _targetFrameSet = true;
                _lastPinDir = dir;
            }
        }

        private bool OwnInReadyStage()
        {
            Agent.ActionStage s = Agent.GetCurrentActionStage(1);
            return s == Agent.ActionStage.AttackReady || s == Agent.ActionStage.AttackQuickReady;
        }

        private static float NormalizeAngle(float a)
        {
            const float Pi = 3.1415927f;
            while (a > Pi) a -= 2f * Pi;
            while (a < -Pi) a += 2f * Pi;
            return a;
        }

        // Target direction rotated by `angle` radians about the agent.
        private Vec3 RotatedDirTo(Agent target, float angle)
        {
            Vec3 toTarget = target.Position - Agent.Position;
            toTarget.z = 0f;
            if (toTarget.LengthSquared < 0.01f)
                return Vec3.Zero;
            toTarget.Normalize();
            float c = (float)Math.Cos(angle);
            float s = (float)Math.Sin(angle);
            return new Vec3(toTarget.x * c - toTarget.y * s, toTarget.x * s + toTarget.y * c, 0f);
        }

        // Feint done/failed: release the scripted frame and re-affirm the
        // combat lock so the agent isn't left steering to a stale point.
        private void ReleaseFeintBodyTurn(Agent target)
        {
            _lastOp = "ClearTargetFrame";
            Agent.ClearTargetFrame();
            _lastOp = "DisableScriptedMovement";
            Agent.DisableScriptedMovement();
            _targetFrameSet = false;
            if (CombatLive(target))
                EnsureTargetAgent(target);
        }

        // In-reach approach movement with the distance band (Session 34
        // correction): nose-to-nose = handle range -> ease back out; in the
        // band -> the normal strafe/drift pick. Running INTO the player was
        // the bug -- never a bare Forward here.
        private Agent.MovementControlFlag ApproachMoveFor(float flatDist)
        {
            if (flatDist < CloseBandRange())
                return Agent.MovementControlFlag.Backward;
            return _approachMove;
        }

        // The base-game movement contract, as plain flags only (Session 34
        // final). The MovementInputVector + drift-pin pressure caused the
        // circling/glitching/lost-tracking the user reported; flags are what
        // native AI drives. Distance band on weapon reach:
        //   beyond reach  -> Forward (run in, follow the player)
        //   inside close  -> Backward (out of handle range)
        //   in the band   -> slow sidestep on the rolled side (keeps feet
        //                    moving through holds/feints without crowding)
        private Agent.MovementControlFlag WindupMoveFor(float flatDist)
        {
            if (flatDist > MeleeReach())
                return Agent.MovementControlFlag.Forward;
            if (flatDist < CloseBandRange())
                return Agent.MovementControlFlag.Backward;
            return _pressureSide > 0f ? Agent.MovementControlFlag.StrafeRight : Agent.MovementControlFlag.StrafeLeft;
        }

        private void EnterApproach(bool afterOurSwing)
        {
            _duelState = DuelState.Approach;
            _duelTimer = 0f;
            _thinkDelay = afterOurSwing
                ? (_playerSwungSinceOurs
                    ? 0.05f + (float)DuelRng.NextDouble() * 0.15f
                    : AfterAttackThinkMin + (float)DuelRng.NextDouble() * (AfterAttackThinkMax - AfterAttackThinkMin))
                : DuelThinkMin + (float)DuelRng.NextDouble() * (DuelThinkMax - DuelThinkMin);
            // 4w gtnative group (gap): a mandatory breathe between swings.
            // 4z: only the DEFENDER really waits (U(2.0,3.8)); everyone else
            // gets a token gap (U(0,0.3)) so the whole line stays aggressive.
            if (afterOurSwing && GroupGap)
            {
                float gMin = _gfDefenderActive ? DefenderGapMin : GroupAttackGapMin;
                float gMax = _gfDefenderActive ? DefenderGapMax : GroupAttackGapMax;
                _thinkDelay = MathF.Max(_thinkDelay,
                    gMin + (float)DuelRng.NextDouble() * (gMax - gMin));
            }
            // Keep MOVING while thinking -- a static attacker eats kicks.
            // Mostly strafe (angles off the player's front), occasional drift.
            // Also re-pick the diagonal-approach side so the push varies --
            // biased toward wherever more nearby enemies are (plain flag
            // lever only, no scripted frame) so the group tends to stay
            // roughly in view during ordinary movement.
            _approachStrafe = PreferredStrafeSide();
            double r = DuelRng.NextDouble();
            _approachMove = r < 0.38 ? Agent.MovementControlFlag.StrafeLeft
                          : r < 0.76 ? Agent.MovementControlFlag.StrafeRight
                          : Agent.MovementControlFlag.None;
            // Session 35/63: the close ALWAYS carries a straight Forward -- a
            // bare strafe beyond reach is a lateral drift that never closes
            // distance (the "walks in circles far away" symptom), and the old
            // Forward|strafe diagonal pick was the walk/run speed flicker
            // (diagonal combat gait is slower still). Sideways variety stays
            // in the in-band step (ApproachMoveFor/WindupMoveFor via
            // _approachMove).
            _approachGait = Agent.MovementControlFlag.Forward;
        }

        // The mirrored defend flag for a committed incoming direction
        // (MissionMainAgentController.cs mapping, research doc section 6a).
        private static Agent.MovementControlFlag MirrorDefend(Agent.UsageDirection incoming)
        {
            switch (incoming)
            {
                case Agent.UsageDirection.AttackLeft: return Agent.MovementControlFlag.DefendRight;
                case Agent.UsageDirection.AttackRight: return Agent.MovementControlFlag.DefendLeft;
                case Agent.UsageDirection.AttackUp: return Agent.MovementControlFlag.DefendUp;
                case Agent.UsageDirection.AttackDown: return Agent.MovementControlFlag.DefendDown;
                default: return Agent.MovementControlFlag.None;
            }
        }

        // Session 67 "visible miss" (group AI-vs-AI): a failed block roll
        // still produces a guard -- on a fixed WRONG line so the miss reads
        // as a misread, never a statue. Fixed readable mapping:
        //   DefendLeft  <-> DefendRight   (guessed the wrong side)
        //   DefendUp    ->  DefendLeft    (raised a side guard vs an overhead)
        //   DefendDown  ->  DefendRight
        //   anything else -> DefendUp     (guard something rather than nothing)
        private static Agent.MovementControlFlag GroupMisreadLine(Agent.MovementControlFlag correct)
        {
            switch (correct)
            {
                case Agent.MovementControlFlag.DefendLeft: return Agent.MovementControlFlag.DefendRight;
                case Agent.MovementControlFlag.DefendRight: return Agent.MovementControlFlag.DefendLeft;
                case Agent.MovementControlFlag.DefendUp: return Agent.MovementControlFlag.DefendLeft;
                case Agent.MovementControlFlag.DefendDown: return Agent.MovementControlFlag.DefendRight;
                default: return Agent.MovementControlFlag.DefendUp;
            }
        }

        private const int MaxDuelLogs = 400;
        private static int _duelLogCount;
        private const int MaxDodgeLogs = 80;   // dedicated [DODGE] budget -- feint spam can't starve it
        private static int _dodgeLogCount;
        private static int _defLogCount;     // [DEF] race diagnostics budget
        private const int MaxDefLogs = 200;
        private const int MaxTakeoverLogs = 30; // [TAKEOVER] speed-state diagnostics
        private static int _takeoverLogCount;
        private static int _lastSideBlockIdx = -99;   // Session 62: dedupe the side-attack [DUEL] line
        private static float _lastSideBlockAt = -99f;
        private static int _sideAttackLogCount;

        // Watches Agent.Mission.MainAgent (the player) specifically. Returns the
        // mirrored Defend flag only once the player's swing is late enough to count as
        // a deliberate, last-moment reaction; otherwise None. Mirroring per
        // combat_ai_research.txt section 6a (MissionMainAgentController.cs):
        //   incoming AttackLeft  -> DefendRight
        //   incoming AttackRight -> DefendLeft
        //   incoming AttackUp    -> DefendUp
        //   incoming AttackDown  -> DefendDown
        // Called EVERY FRAME from CombatAiMissionBehavior.OnMissionTick (main
        // thread). This is the real-time control path: writes the mode's desired
        // flag straight into Agent.MovementFlags, the same mechanism
        // MissionMainAgentController uses for the player (mainAgent.MovementFlags
        // |= flag every frame a key is held). OnAIInputSet is only a ~1Hz decision
        // callback -- too slow for this -- but still mirrors _desiredMovementFlag
        // so it can't stomp the per-frame value.
        // [HandleProcessCorruptedStateExceptions]: lets the catch below actually
        // observe an AccessViolationException raised inside our MBAPI calls
        // (.NET Framework swallows CSEs without it). The next crash then writes
        // an [AV] line naming the exact call -- or doesn't, which would prove
        // the fault is engine-side (e.g. a dangling native target slot).
        [HandleProcessCorruptedStateExceptions]
        public void DrivePerFrame(float dt)
        {
            try
            {
                DrivePerFrameCore(dt);
            }
            catch (AccessViolationException ex)
            {
                LogAv("DrivePerFrame", ex);
            }
        }

        private void DrivePerFrameCore(float dt)
        {
            // NEVER write native agent state on a dying/torn-down agent: during
            // the death window the agent can still pass the eligibility checks
            // (position/enemy flags still read valid) while the engine frees
            // its internals on worker threads -- writing MovementFlags /
            // SetTargetAgent / scripted frames there is a real AV candidate
            // (observed: 0xC0000005 on thread 10, a worker thread).
            // Pure-managed staleness gate FIRST (see Live/CombatLive above):
            // after Agent.Clear() every call dereferences a zeroed pointer.
            // || short-circuits, so Live() must lead the condition.
            _lastOp = "live-gate";
            if (!Live(Agent))
            {
                _drivingMovementFlags = false;
                _nativeLocoWindow = false;
                _targetFrameSet = false;
                // Bookkeeping only -- Agent is gone, no native call is safe
                // here. These just stop a later live agent (if the index is
                // ever reused) from inheriting a stale "raised" belief.
                _driveSpeedScaled = false;
                _dodgeSpeedRaised = false;
                if (_steerRegistered)   // managed-memory write only, always safe
                {
                    _steerRegistered = false;
                    NativeSteerOverride.Unregister(_steerChan);
                }
                return;
            }
            if (_dbgIndex < 0) { _dbgIndex = Agent.Index; _dbgName = Agent.Name; }
            if (_driveDisabledByAv)
            {
                // AV-disabled component never drives again -- its registered
                // dir must not keep steering the agent with a stale heading.
                if (_steerRegistered)
                {
                    _steerRegistered = false;
                    NativeSteerOverride.Unregister(_steerChan);
                }
                return;
            }
            _lastOp = "alive-check";
            if (!Agent.IsActive() || Agent.Health <= 0f)
            {
                // Dying/cleared agent: drop bookkeeping only -- even
                // ClearTargetFrame/DisableScriptedMovement are native calls
                // on a torn-down agent here.
                _drivingMovementFlags = false;
                _nativeLocoWindow = false;
                _targetFrameSet = false;
                _driveSpeedScaled = false;
                _dodgeSpeedRaised = false;
                if (_steerRegistered)
                {
                    _steerRegistered = false;
                    NativeSteerOverride.Unregister(_steerChan);
                }
                return;
            }

            // Stranded-cap restore: if a dodge step ended (or a mode switched
            // off mid-step), put the speed limit and locomotion input back --
            // a stale raised cap would leave the agent permanently fast.
            if (_dodgeSpeedRaised && _dodgeTime <= 0f)
            {
                _lastOp = "restoreSpeed(stray)";
                Agent.SetMaximumSpeedLimit(-1f, isMultiplier: false);
                Agent.MovementInputVector = Vec2.Zero;
                _dodgeSpeedRaised = false;
            }

            // FREE TARGET gate (combatai.freetarget): this agent drives with
            // the combat target released and the formation frame owning
            // facing/legs. Scope: default duel (incl. defense-only) + the spar
            // defender. FAIL CLOSED -- free mode may only run while the native
            // crash guard is armed (ArmedLevel >= 2); EnsureArmed auto-arms it
            // the first time a mode needs it, and any failure leaves the
            // agent on the byte-for-byte legacy locked path.
            // NATIVE STEER (combatai.steer, Option A): the combat lock stays ON
            // the whole drive -- no free-target release. Our detour on the
            // heading-command funnel (rva 0x61ac60, verified on the installed
            // 1.4.8 dll) swaps the steering-computed desired dir for ours; the
            // engine's own integrator still rate-limits the turn. FAIL CLOSED:
            // only while the hook is armed -- EnsureArmed auto-arms it on the
            // first steerWanted tick; any failure leaves duelFree/free-target
            // exactly as it was. Excludes every test mode that manipulates
            // facing/lock itself (faceframe, looktest, holdspin, lockthrash,
            // lookonly) and mounted agents.
            // Scope: (a) plain duel -- heading = face the target;
            //        (b) the spar DEFENDER under SparFree -- heading = the
            //            shared bisector policy (SparDefBisectorAngle), so it
            //            faces BETWEEN the two attackers while the lock stays
            //            on the rig primary. The spar attacker and every other
            //            mode keep their existing behavior.
            bool steerWanted = CombatAiState.NativeSteer
                && !CombatAiState.LookTestMode && !CombatAiState.LookOnlyMode
                && !CombatAiState.HoldSpinMode && !CombatAiState.LockThrashMode
                && !(CombatAiState.FaceFrameMode && Agent.Index == CombatAiState.FaceFrameAgentIndex)
                && ((CombatAiState.DuelMode
                        && !CombatAiState.SparMode && !CombatAiState.GroupFightMode
                        && !(CombatAiState.DuoAttackMode && ArenaDuoCoordinator.IsParticipant(Agent.Index)))
                    || (CombatAiState.SparMode && CombatAiState.SparFree
                        && Agent.Index == CombatAiState.SparDefenderIndex)
                    || (CombatAiState.GroupFightMode && CombatAiState.GroupFree
                        && GroupLineup.Phase == GroupLineup.Ph.Fight))
                && !Agent.HasMount && IsEligibleForTestModes();
            if (steerWanted && !NativeSteerOverride.Armed)
                NativeSteerOverride.EnsureArmed();
            bool steerActive = steerWanted && NativeSteerOverride.Armed;
            // Field (not local): ComputeDesiredFlag forces the rig path for a
            // steered defender and StartDuelAttack side-matches its swings.
            _steerSparDef = steerActive && CombatAiState.SparMode
                && Agent.Index == CombatAiState.SparDefenderIndex;
            _steerGroup = steerActive && CombatAiState.GroupFightMode && CombatAiState.GroupFree
                && GroupLineup.Phase == GroupLineup.Ph.Fight;
            if (!steerActive)
                _steerLastA = -999f;   // fresh hold memory on the next steer session

            bool sparDefFree = CombatAiState.SparMode && Agent.Index == CombatAiState.SparDefenderIndex
                && CombatAiState.SparFree   // combatai.sparfree off = legacy locked defender
                && !steerActive         // steered defender keeps the lock -- never runs free
                && !Agent.HasMount;   // mounted defender can't run the rig path -- keep it legacy-locked
            bool duelFree = CombatAiState.DuelMode && !CombatAiState.SparMode
                && !CombatAiState.GroupFightMode
                && !(CombatAiState.DuoAttackMode && ArenaDuoCoordinator.IsParticipant(Agent.Index))
                && !steerActive;   // steered agents keep the lock -- never run free
            // GROUP LINEUP stage 1: while the lines form/advance/wait the
            // group agents are also free-driven (never locked, facing = team
            // line facing); at Phase=Fight this flips off and the legacy
            // locked group brain resumes.
            bool groupLineFree = CombatAiState.GroupFightMode && CombatAiState.GroupFree
                && GroupLineup.Phase != GroupLineup.Ph.Fight
                && !Agent.HasMount;
            bool groupFightFree = CombatAiState.GroupFightMode && CombatAiState.GroupFree
                && GroupLineup.Phase == GroupLineup.Ph.Fight
                && !steerActive        // steered pair fighters keep the lock -- never run free
                && !Agent.HasMount;
            bool freeWanted = CombatAiState.FreeTarget != CombatAiState.FreeTargetModeT.Off
                && (sparDefFree || duelFree || groupLineFree || groupFightFree) && IsEligibleForTestModes()
                && !(CombatAiState.FaceFrameMode && Agent.Index == CombatAiState.FaceFrameAgentIndex)
                && !CombatAiState.CavDuelMode;   // cav keeps the lock -- the free policy must not release it
            if (freeWanted && NativeTargetGuard.ArmedLevel < 2)
                NativeTargetGuard.EnsureArmed();
            _freeTargetActive = freeWanted && NativeTargetGuard.ArmedLevel >= 2;
            if (_freeTargetActive)
            {
                // Deterministic release: native auto-select must never re-lock.
                SetAutoTargeting(false);
                if (!_freeMarked)
                {
                    _freeMarked = true;
                    // Mark even when the slot was already -1 -- the watchdog
                    // (WATCH/RELOCK-STACK) then prints the culprit stack if
                    // anything writes a positive target on this agent.
                    NativeTargetGuard.MarkReleased(Agent);
                }
            }
            else
                _freeMarked = false;

            _lastOp = "compute";
            _cavDriving = false;   // re-armed inside ComputeDesiredFlag when the cav dispatch wins
            Agent.MovementControlFlag desired = ComputeDesiredFlag(dt, out bool driving);

            // NATIVE STEER registration (needs _duelTarget, set by the FSM
            // inside ComputeDesiredFlag). The channel key resolves once per
            // agent and is cached; the dir refreshes every driven tick so the
            // detour feeds the current policy heading. Duel: face the target.
            // Spar defender: the SHARED bisector policy (SparDefBisectorAngle,
            // the exact code the free driver runs) -- face BETWEEN the two
            // attackers while the lock stays on the rig primary. Any window we
            // shouldn't steer (not driving, dodge step, native hand-off) drops
            // the membership so native steering resumes the same tick.
            bool steerDrive = steerActive && driving && !DodgeOwnsBody && !_handOffToNative
                && (!_steerGroup || _gfLayerActive);   // 4x: free camera lives only inside the layer
            bool steerWrote = false;
            if (steerDrive)
            {
                Vec2 stDir = Vec2.Zero;
                if (_steerSparDef)
                {
                    // holdA = our last commanded angle: the >160deg spread HOLD
                    // keeps steering toward it instead of snapping to the seam.
                    float sA = SparDefBisectorAngle(_steerLastA);
                    if (sA > -900f)
                        stDir = new Vec2(MathF.Cos(sA), MathF.Sin(sA));
                    else
                    {
                        // No threat bearings at all: fall back to the player.
                        Agent sp = Agent.Mission?.MainAgent;
                        if (CombatLive(sp))
                        {
                            Vec2 toP = sp.Position.AsVec2 - Agent.Position.AsVec2;
                            if (toP.LengthSquared > 0.0001f)
                                stDir = FlatUnit(toP);
                        }
                    }
                    if (stDir.LengthSquared > 0.0001f)
                    {
                        _steerLastA = MathF.Atan2(stDir.y, stDir.x);
                        // _ftHeading = "our last commanded heading" in BOTH
                        // paths -- the bisector helper's sErr check reads it,
                        // and a later steer->free switch slews on from it.
                        _ftHeading = stDir;
                    }
                }
                else
                {
                    Agent st = Live(_duelTarget) ? _duelTarget
                        : (CombatLive(IntendedTarget) ? IntendedTarget : Agent.Mission?.MainAgent);
                    if (CombatLive(st))
                    {
                        Vec2 toT = st.Position.AsVec2 - Agent.Position.AsVec2;
                        if (toT.LengthSquared > 0.0001f)
                            stDir = FlatUnit(toT);
                    }
                    // Group pair fight free look: the threat-median heading
                    // replaces face-the-lock whenever GroupComputeLook armed
                    // it (never armed while attacking).
                    if (_steerGroup && _gfLookValid)
                        stDir = _gfLookDir;
                }
                if (stDir.LengthSquared > 0.0001f)
                {
                    if (_steerChan == UIntPtr.Zero)
                        NativeSteerOverride.ResolveChannel(Agent, out _steerChan, out _steerCtx);
                    if (_steerChan != UIntPtr.Zero)
                    {
                        NativeSteerOverride.Register(_steerChan, _steerCtx, Agent.Index, stDir.x, stDir.y);
                        _steerRegistered = true;
                        steerWrote = true;
                    }
                }
                // [SPARSTEER] ~1s bounded line (cap 200): verifies "faces
                // BETWEEN the two attackers" -- commanded heading, spread,
                // sticky primary, both threat bearings RELATIVE to the
                // heading (both in the front cone = bisector working), own
                // swing dir while attacking, stage, held lock.
                if (_steerSparDef && steerWrote && _ssLogCount < MaxSsLogs
                    && _brainClock - _ssLastLogAt >= 1f)
                {
                    _ssLastLogAt = _brainClock;
                    _ssLogCount++;
                    float headDeg = _steerLastA * 57.2958f;
                    float r1 = -999f, r2 = -999f;
                    Agent sa = GetAgentByIndex(CombatAiState.SparAttackerIndex);
                    Agent sp2 = Agent.Mission?.MainAgent;
                    if (CombatLive(sa))
                    {
                        Vec2 t = sa.Position.AsVec2 - Agent.Position.AsVec2;
                        r1 = NormalizeAngle((MathF.Atan2(t.y, t.x) * 57.2958f - headDeg) * 0.0174533f) * 57.2958f;
                    }
                    if (CombatLive(sp2))
                    {
                        Vec2 t = sp2.Position.AsVec2 - Agent.Position.AsVec2;
                        r2 = NormalizeAngle((MathF.Atan2(t.y, t.x) * 57.2958f - headDeg) * 0.0174533f) * 57.2958f;
                    }
                    Agent.ActionStage ssStage = Agent.GetCurrentActionStage(1);
                    bool ssSwing = ssStage == Agent.ActionStage.AttackReady
                        || ssStage == Agent.ActionStage.AttackQuickReady
                        || ssStage == Agent.ActionStage.AttackRelease;
                    Agent cur = Agent.GetTargetAgent();
                    CombatAiLog.Log($"[SPARSTEER] Agent #{Agent.Index}: head={headDeg:F0} " +
                        $"spread={_ftSpread:F0} prim={(CombatLive(_ftPrimary) ? "#" + _ftPrimary.Index : "-")} " +
                        $"atkRel={(r1 > -900f ? r1.ToString("F0") : "na")} playerRel={(r2 > -900f ? r2.ToString("F0") : "na")} " +
                        $"dir={(ssSwing ? _attackDir.ToString() : "-")} stage={ssStage} " +
                        $"lock={(Live(cur) ? "#" + cur.Index : "-1")} state={_duelState}");
                }
            }
            if (_steerRegistered && !steerWrote)
            {
                // Any tick we didn't write (not driving, dodge, hand-off, dead
                // target): drop membership -- a stale dir must never steer.
                _steerRegistered = false;
                _steerLastA = -999f;
                NativeSteerOverride.Unregister(_steerChan);
            }

            // Free-target handoff-lock transitions (ai26): the AttackOnly
            // policy holds the combat lock while _handOffToNative so native
            // can chase, and releases it on resume. One bounded line each way.
            // LOOK-ONLY locomotion (Session 71): approach/orbit intents vs
            // the player through the existing flag channel, attacks already
            // masked by DefenseOnly (set at enable). Always moves relative
            // to a facing that points at the player (MoveDir writer).
            bool lookOnly = CombatAiState.LookOnlyMode && !CombatAiState.SparMode && !CombatAiState.GroupFightMode;
            Agent loPlayer = lookOnly ? Agent.Mission?.MainAgent : null;
            if (lookOnly && CombatLive(loPlayer))
            {
                float loDist = HorizontalDistanceTo(loPlayer);
                desired = (desired & ~Agent.MovementControlFlag.MoveMask) | LookOnlyMoveFlag(dt, loDist);
                LookOnlyTick(dt, loPlayer, loDist);
            }
            else if (_loWasActive)
            {
                // Mode switched off (or target lost) -- one-line summary.
                _loWasActive = false;
                float mB = _loSamples > 0 ? _loSumErrB / _loSamples : 0f;
                CombatAiLog.Log($"[LOOKONLY] Agent #{Agent.Index}: OFF inView90={CombatAiState.LookOnlyInView90:F0}% " +
                    $"meanErr={mB:F0} maxErr={_loMaxErr:F0}");
                _loIntentUntil = -99f;
            }

            // HELD-ATTACK shutter tests (holdspin / lockthrash): a persistent
            // AttackUp flag holds the weapon ready while the test tick drives
            // body-turn or lock writes. Forward only to get into range.
            bool testHold = (CombatAiState.HoldSpinMode || CombatAiState.LockThrashMode)
                && CombatAiState.DuelMode && !CombatAiState.SparMode && !CombatAiState.GroupFightMode;
            Agent htPlayer = testHold ? Agent.Mission?.MainAgent : null;
            if (testHold && CombatLive(htPlayer))
            {
                desired = (desired & ~Agent.MovementControlFlag.MoveMask
                    & ~Agent.MovementControlFlag.DefendMask)
                    | Agent.MovementControlFlag.AttackUp;
                if (HorizontalDistanceTo(htPlayer) > MeleeReach() * 1.1f)
                    desired |= Agent.MovementControlFlag.Forward;
                HoldTestTick(dt, htPlayer);
            }

            // Group-fight claim lifecycle: released when the FSM is back in
            // Approach (end of our attack chain), refreshed while attacking.
            if (CombatAiState.GroupFightMode && CombatAiState.GroupFree
                && GroupLineup.Phase == GroupLineup.Ph.Fight)
            {
                if (_duelState == DuelState.Approach)
                    GroupEngagement.Release(Agent.Mission, Agent.Index, _brainClock, "approach");
                else
                    GroupEngagement.Refresh(Agent.Mission, Agent.Index, _brainClock);
            }

            // SPARFREE defender attack window end: back in Approach = the
            // guarded release path owns the slot again.
            if (_sfAtkActive && _duelState == DuelState.Approach)
            {
                _sfAtkActive = false;
                if (_sfAtkLogCount < MaxSfAtkLogs)
                {
                    _sfAtkLogCount++;
                    Agent curA = Agent.GetTargetAgent();
                    CombatAiLog.Log($"[SPARFREE] Agent #{Agent.Index}: defender attack end " +
                        $"lock=released nativeTgt={(Live(curA) ? "#" + curA.Index : "-1")}");
                }
            }

            bool ftHandoff = _freeTargetActive && _handOffToNative;
            if (ftHandoff != _ftHandoffLock)
            {
                _ftHandoffLock = ftHandoff;
                if (_ftHandoffLogCount < MaxHandoffLogs)
                {
                    _ftHandoffLogCount++;
                    float hd = Live(_duelTarget) ? HorizontalDistanceTo(_duelTarget) : -1f;
                    CombatAiLog.Log($"[FREETGT] Agent #{Agent.Index}: handoff lock {(ftHandoff ? "ON" : "OFF")} (dist={hd:F2}).");
                }
            }

            // FaceFrame experiment (combatai.faceframe): the designated
            // agent's facing is owned EXCLUSIVELY by its formation frame +
            // the held player lock -- every locomotion/body lever below must
            // stay out of it (single-writer rule): the Session-49 native loco
            // window, duel block-move (formation AND input), the spar rig
            // input channel, and the raw inputtest probe.
            bool faceFrame = CombatAiState.FaceFrameMode
                && Agent.Index == CombatAiState.FaceFrameAgentIndex;

            // Session 48 native-locomotion window (plain duel only): during a
            // guard hold or attack-ready the bare defend/ready flag we return
            // has no move bits, so the agent stands still while native would
            // keep its feet going. In those windows we write only our combat
            // bits and let native's own move bits + gait through (the callback
            // reinstates native's MoveMask/eventFlag/inputVector). Never
            // during our own AttackRelease -- a mid-swing native write could
            // redirect the swing.
            bool callbackFired = TotalTicks != _lastSeenTicks;
            _lastSeenTicks = TotalTicks;
            Agent.ActionStage locoStage = driving ? Agent.GetCurrentActionStage(1) : Agent.ActionStage.None;
            _nativeLocoWindow = ((CombatAiState.NativeLocoWindows || CombatAiState.DuelBlockMove == CombatAiState.DuelBlockMoveT.Loco) && driving
                && !CombatAiState.SparMode && !CombatAiState.GroupFightMode
                && !(CombatAiState.DuoAttackMode && ArenaDuoCoordinator.IsParticipant(Agent.Index))
                && !DodgeOwnsBody && !_handOffToNative && callbackFired
                && (locoStage == Agent.ActionStage.Defend
                    || locoStage == Agent.ActionStage.DefendParry
                    || locoStage == Agent.ActionStage.AttackReady
                    || locoStage == Agent.ActionStage.AttackQuickReady
                    || (desired & (Agent.MovementControlFlag.DefendMask
                        | Agent.MovementControlFlag.DefendBlock)) != 0)
                && locoStage != Agent.ActionStage.AttackRelease)
                // Session 56: the torso-move experiment's own loco window for
                // the spar defender -- native owns locomotion during blocks.
                || (driving && _tmLocoWindow && callbackFired
                    && !_handOffToNative && locoStage != Agent.ActionStage.AttackRelease);
            if (faceFrame)
                _nativeLocoWindow = false;   // one locomotion writer -- never native's window

            // 4x group native-move: a steered pair fighter's legs belong to
            // the engine -- keep the native window armed so MoveMask/gait/
            // input pass through untouched. The translator case (layer
            // steering our facing) is the ONE exception: it replays native's
            // captured intent through the rig writer, so it must NOT take
            // the window.
            bool gfTranslate = driving && _steerGroup && CombatAiState.GroupNativeTarget
                && CombatAiState.GfNativemove && _gfLayerActive && !DodgeOwnsBody;
            bool gfRig = driving && _steerGroup && CombatAiState.GroupNativeTarget
                && CombatAiState.GfNativemove && _gfRigged && !DodgeOwnsBody;   // 4aa rigged 2v1
            bool groupNativeMove = driving && _steerGroup && CombatAiState.GroupNativeTarget
                && CombatAiState.GfNativemove && !gfTranslate && !gfRig && !DodgeOwnsBody;
            if (groupNativeMove)
            {
                _nativeLocoWindow = true;
                // One-time writer release when the translator drops: clear
                // the last MovementInputVector exactly once so a stale
                // translated vector can't linger -- native's move bits keep
                // arriving through the window so there is no dead frame.
                if (_tmInputVec.LengthSquared > 0.0001f)
                {
                    Agent.MovementInputVector = Vec2.Zero;
                    _tmInputVec = Vec2.Zero;
                }
            }

            // Session 58 DUEL block-move (opt-in): the same formation lever the
            // spar defender uses, in exactly the windows the Session-49 window
            // applies to -- native AI keeps moving while holding a block, so
            // do we. Intent: close/reopen toward ~reach; none in the sweet band.
            // freeFramePolicy: only the formation-frame free writer conflicts
            // with these locomotion channels -- the look writers
            // (set_look_direction: +0x474/+0x484 only, never +0x4a4) let the
            // proven legacy paths run unchanged.
            bool freeFramePolicy = _freeTargetActive && CombatAiState.FreeFace == CombatAiState.FreeFaceT.Frame;
            bool duelBm = CombatAiState.DuelBlockMove == CombatAiState.DuelBlockMoveT.Formation && driving
                && !freeFramePolicy   // free mode: the free-frame block below is the one writer
                && !faceFrame && !_cavDriving && !CombatAiState.SparMode && !CombatAiState.GroupFightMode
                && !(CombatAiState.DuoAttackMode && ArenaDuoCoordinator.IsParticipant(Agent.Index))
                && !DodgeOwnsBody && !_handOffToNative && locoStage != Agent.ActionStage.AttackRelease
                && (locoStage == Agent.ActionStage.Defend || locoStage == Agent.ActionStage.DefendParry
                    || locoStage == Agent.ActionStage.AttackReady || locoStage == Agent.ActionStage.AttackQuickReady
                    || (desired & (Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock)) != 0);
            // Session 61c DUEL blockmove=input (user request) + Session 63
            // fixes: y of the facing-relative input vector (zero strafe, no
            // diagonal) in two cases sharing the ONE write block below:
            //  (a) defend/ready windows: forward ONLY when the player is
            //      backpedaling (vel dot toUs < -0.35 -- toUs points
            //      player->us, so negative = moving away; the old in-band
            //      y=0 left us planted at the first block) or is
            //      out of reach; back when he presses inside 0.6*reach.
            //  (b) outside defend windows, DuelState.Approach with the player
            //      beyond 1.1*reach: forward -- the flag-channel Forward is
            //      combat-gait walk only and native runs outside the leash,
            //      so our takeover read as a visible slowdown.
            bool duelInputMode = CombatAiState.DuelBlockMove == CombatAiState.DuelBlockMoveT.Input && driving
                && !freeFramePolicy   // frame writer: no MovementInputVector writes
                && !faceFrame && !_cavDriving && !CombatAiState.SparMode && !CombatAiState.GroupFightMode
                && !(CombatAiState.DuoAttackMode && ArenaDuoCoordinator.IsParticipant(Agent.Index))
                && !DodgeOwnsBody && !_handOffToNative && locoStage != Agent.ActionStage.AttackRelease;
            bool duelBmInput = duelInputMode
                && (locoStage == Agent.ActionStage.Defend || locoStage == Agent.ActionStage.DefendParry
                    || locoStage == Agent.ActionStage.AttackReady || locoStage == Agent.ActionStage.AttackQuickReady
                    || (desired & (Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock)) != 0);
            bool duelApproachInput = false;
            if (duelInputMode && !duelBmInput && _duelState == DuelState.Approach)
            {
                Agent appT = Agent.Mission?.MainAgent;
                duelApproachInput = CombatLive(appT)
                    && (appT.Position.AsVec2 - Agent.Position.AsVec2).Length > MeleeReach() * 1.1f;
            }
            bool duelInputActive = duelBmInput || duelApproachInput;
            // Session 62: spar rig -- the defender's legs travel through the
            // WASD input channel (planner world dir -> facing-relative vector).
            bool sparRigInput = driving && _rigDriving && !faceFrame && !freeFramePolicy
                && !groupNativeMove   // 4x: native owns the legs -- no input writer
                && !DodgeOwnsBody && !_handOffToNative && locoStage != Agent.ActionStage.AttackRelease;
            Vec2 bmGoal = Vec2.Zero;
            if (duelInputActive)
            {
                _nativeLocoWindow = false;   // one locomotion writer
                float y = 1f;                // approach case: always closing (gated on d > 1.1*reach above)
                if (duelBmInput)
                {
                    y = 0f;
                    Agent dbTarget = Agent.Mission?.MainAgent;
                    if (CombatLive(dbTarget))
                    {
                        Vec2 toUs = Agent.Position.AsVec2 - dbTarget.Position.AsVec2;
                        float d = toUs.Length;
                        float reach = MeleeReach();
                        // vel along the player->us line < -0.35 = player
                        // backpedaling away from us -- follow forward.
                        bool separating = d > 0.0001f
                            && Vec2.DotProduct(dbTarget.GetCurrentVelocity(),
                                               new Vec2(toUs.x / d, toUs.y / d)) < -0.35f;
                        y = separating || d > reach * 1.1f ? 1f : d < reach * 0.6f ? -1f : 0f;
                    }
                }
                _tmInputVec = new Vec2(0f, y);
                _lastOp = "MovementInputVector(duel)";
                Agent.MovementInputVector = _tmInputVec;
                desired &= ~Agent.MovementControlFlag.MoveMask;
                bmGoal = _tmInputVec;
            }
            else if (sparRigInput)
            {
                _nativeLocoWindow = false;
                if (gfTranslate && !gfRig) _gsumXl++;   // 4x: translated native-intent frames
                Vec2 g = _rigGoal;
                // Facing basis = the BODY heading (the slot SetMovementDirection
                // owns under sparfree), Frame.rotation.f as fallback -- a stale
                // frame basis skews strafe/forward while the body is mid-turn.
                Vec2 f2 = Agent.GetMovementDirection();
                if (f2.LengthSquared < 0.0001f)
                    f2 = Agent.Frame.rotation.f.AsVec2;
                float fl = f2.Length;
                _tmInputVec = Vec2.Zero;
                if (fl > 0.0001f && g.LengthSquared > 0.0001f)
                {
                    f2 = new Vec2(f2.x / fl, f2.y / fl);
                    _tmInputVec = new Vec2(
                        g.x * f2.y - g.y * f2.x,      // strafe: dot(g, right)
                        g.x * f2.x + g.y * f2.y);     // fwd:    dot(g, forward)
                }
                _lastOp = "MovementInputVector(sparrig)";
                Agent.MovementInputVector = _tmInputVec;
                desired &= ~Agent.MovementControlFlag.MoveMask;
                if (_rigMoveLogCount < MaxRigMoveLogs && _brainClock - _rigLastLogAt >= 0.4f)
                {
                    _rigLastLogAt = _brainClock;
                    _rigMoveLogCount++;
                    CombatAiLog.Log($"[RIGMOVE] Agent #{Agent.Index}: primary=#{_rigPlan.PrimaryKey} kind={_rigPlan.Kind} " +
                        $"dP={_rigPlan.DistPrimary:F2} spread={_rigPlan.SpreadDeg:F0} secBearing={_rigPlan.SecondaryBearingDeg:F0} " +
                        $"free={_rigLastFree:F2} theta'={_rigPlan.ThetaAfterTop:F0} spd={Agent.GetCurrentVelocity().Length:F2} iv=({_tmInputVec.x:F2},{_tmInputVec.y:F2})");
                }
            }
            else if (_cavDriving && driving && !DodgeOwnsBody && !_handOffToNative
                && locoStage != Agent.ActionStage.AttackRelease
                && CombatAiState.CavSteer != CombatAiState.CavSteerT.Off)
            {
                // CAV DUEL locomotion: the mounted planner's facing-relative
                // input vector (+ optional Turn flags) is the ONLY locomotion
                // writer -- one-writer rule, same as the spar rig.
                _nativeLocoWindow = false;
                _tmInputVec = _cavInputVec;
                _lastOp = "MovementInputVector(cav)";
                Agent.MovementInputVector = _tmInputVec;
                // MoveMask CONTAINS the Turn/Forward/Backward flags: strip
                // first, then re-add the planner's own move bits so they
                // survive.
                desired = (desired & ~Agent.MovementControlFlag.MoveMask) | _cavMoveFlag;
            }
            else if (_tmInputVec.LengthSquared > 0.0001f)
            {
                Agent.MovementInputVector = Vec2.Zero;
                _tmInputVec = Vec2.Zero;
            }
            // Session 61c: raw input-channel probe (combatai.inputtest) --
            // dominates the block-movement levers: continuously writes the
            // requested facing-relative vector so we can isolate whether
            // MovementInputVector moves an agent at all vs stage/lock issues.
            if (CombatAiState.InputTestVec.LengthSquared > 0.0001f && driving
                && !faceFrame && !DodgeOwnsBody && !_handOffToNative && !freeFramePolicy)
            {
                _nativeLocoWindow = false;
                _tmInputVec = CombatAiState.InputTestVec;
                Agent.MovementInputVector = _tmInputVec;
                desired &= ~Agent.MovementControlFlag.MoveMask;
            }
            // Session 60: with the formation lever active the native window is
            // a second locomotion writer -- force it off (single writer rule),
            // and the goal now always comes from the tactical planner.
            if (duelBm)
            {
                _nativeLocoWindow = false;
                Agent dbTarget = Agent.Mission?.MainAgent;
                if (CombatLive(dbTarget))
                {
                    if (_tmFormationSet && !Agent.IsFormationFrameEnabled)
                        _tmStomps++;
                    _tacEnemies.Clear();
                    _tacEnemies.Add((dbTarget.Index, dbTarget.Position.AsVec2, true));
                    _tacPlan = TacticalMovement.Compute(Agent.Position.AsVec2, _tacEnemies, null,
                        MeleeReach(), _brainClock, dt, ref _tac, DuelRng);
                    if (_tacPlan.HasGoal)
                    {
                        bmGoal = _tacPlan.Goal;
                        Vec2 gp = Agent.Position.AsVec2 + bmGoal;
                        Vec2 vel2 = Agent.GetCurrentVelocity();
                        // Session 61b: same turn-in-place fix as the spar side --
                        // slew the frame heading toward the enemy at ~60deg/s
                        // instead of tracking the live bearing.
                        if (_tacPlan.Heading.LengthSquared > 0.0001f)
                        {
                            if (_bmHeading.LengthSquared < 0.0001f)
                                _bmHeading = _tacPlan.Heading;
                            float curA = MathF.Atan2(_bmHeading.y, _bmHeading.x);
                            float wantA = MathF.Atan2(_tacPlan.Heading.y, _tacPlan.Heading.x);
                            float dA = wantA - curA;
                            while (dA > 3.14159f) dA -= 6.28319f;
                            while (dA < -3.14159f) dA += 6.28319f;
                            float step = MathF.Max(-1.05f * dt, MathF.Min(1.05f * dt, dA));
                            float newA = curA + step;
                            _bmHeading = new Vec2(MathF.Cos(newA), MathF.Sin(newA));
                        }
                        _lastOp = "FormationFrame(duel)";
                        Agent.SetFormationFrameEnabled(
                            new TaleWorlds.Engine.WorldPosition(Mission.Current.Scene, UIntPtr.Zero, new Vec3(gp.x, gp.y, Agent.Position.z), false),
                            _bmHeading, vel2, CombatAiState.DuelBlockFactor);
                        _tmFormationSet = true;
                    }
                }
            }
            // FREE TARGET driver (combatai.freetarget): with the combat target
            // released, the formation frame is the ONLY position/facing writer
            // (ai21-verified: target=-1 -> the frame owns facing; legs follow
            // `frame`). Duel: pos = the same TacticalMovement band/orbit goal
            // duelblockmove=formation computes, heading = bearing to the
            // player (60deg/s slew, the Session-61b turn-in-place fix). Spar
            // defender: pos = the rig planner's world goal (hold position when
            // none), heading = bisector of the unit directions to the two
            // attackers (single attacker: that attacker). Frame skipped while
            // a dodge step or our own release owns the body -- it persists
            // engine-side and is re-asserted next tick.
            bool freeDrive = _freeTargetActive && driving && !faceFrame
                && !DodgeOwnsBody && !_handOffToNative
                && locoStage != Agent.ActionStage.AttackRelease;
            // ai28 fix: the LOOK writers keep the commanded heading written
            // in EVERY state without a held lock or native hand-off -- dodge
            // steps and non-driven ticks included (the >30deg gaps were the
            // body drifting to its last goal while nothing owned facing).
            bool lookDrive = _freeTargetActive && !faceFrame && !_handOffToNative
                && CombatAiState.FreeFace != CombatAiState.FreeFaceT.Frame
                && !LockWantedNow()
                && locoStage != Agent.ActionStage.AttackRelease;
            bool freeWrite = freeDrive || lookDrive;
            // Reseed the commanded heading from the agent's actual facing
            // whenever the free driver resumes (hand-off end, attack window,
            // dodge step) -- the stale pre-pause heading would slew in from
            // the wrong side. Snap straight to the player bearing when the
            // jump is <90deg (no visible wrong-side sweep).
            if (!freeWrite && _freeTargetActive)
            {
                _ftReseed = true;
                _ftAngVel = 0f;
                _ftTargetA = -999f;
            }
            // Engagement gate for the free logs (ai30: 8m+ standoff burned the
            // whole bounded budget before the fight started) + mode-start
            // counter reset.
            if (_freeTargetActive && !_freeWasActive)
            {
                _ftLogCount = 0; _svLogCount = 0; _svSamples = 0; _svViewL = 0; _svViewB = 0;
                _saLogCount = 0; _sfLogCount = 0; _sfAtkLogCount = 0; _smLogCount = 0;
            }
            _freeWasActive = _freeTargetActive;
            _ftEngaged = false;
            {
                Agent e1 = sparDefFree ? GetAgentByIndex(CombatAiState.SparAttackerIndex)
                    : (Live(_duelTarget) ? _duelTarget : null);
                Agent e2 = Agent.Mission?.MainAgent;
                if (CombatLive(e1) && (e1.Position.AsVec2 - Agent.Position.AsVec2).Length < 8f) _ftEngaged = true;
                if (CombatLive(e2) && (e2.Position.AsVec2 - Agent.Position.AsVec2).Length < 8f) _ftEngaged = true;
            }
            if (freeWrite)
            {
                if (_ftReseed)
                {
                    _ftReseed = false;
                    _ftAngVel = 0f;
                    _ftTargetA = -999f;
                    Vec2 seed = Agent.GetMovementDirection();
                    if (seed.LengthSquared > 0.0001f)
                        _ftHeading = FlatUnit(seed);
                    // (ai30: the <90deg snap-to-bearing shortcut is gone -- the
                    // smoothed slew eases from the ACTUAL facing to the target
                    // bearing, no hard turn.)
                }
                _nativeLocoWindow = false;   // one locomotion writer
                bool lookWriter = CombatAiState.FreeFace != CombatAiState.FreeFaceT.Frame;
                Vec2 goal = Vec2.Zero;
                Vec2 heading = Vec2.Zero;
                float tgtA = -999f;   // policy target angle this tick (rad)
                if (sparDefFree)
                {
                    goal = _rigGoal;   // zero = hold position (frame writer only)
                    // Shared with the native-steer defender: SparDefBisectorAngle
                    // owns the spread rules + sticky primary (extracted so the
                    // two facing paths can't drift). holdA = _ftTargetA, the
                    // rate-capped smoothed target = this path's "last heading".
                    float bA = SparDefBisectorAngle(_ftTargetA);
                    if (bA > -900f)
                        tgtA = bA;
                }
                else if (groupLineFree)
                {
                    // GROUP LINEUP: face the enemy line -- the team facing
                    // dir computed by GroupLineup for this agent's slot.
                    Vec2 lf = GroupLineup.FacingDir(Agent.Index);
                    if (lf.LengthSquared > 0.0001f)
                        tgtA = MathF.Atan2(lf.y, lf.x);
                }
                else if (groupFightFree)
                {
                    // STAGE-2 fight: weighted bisector of my threat set.
                    float g = GroupHeadingTarget(Agent.Mission);
                    if (g > -900f)
                        tgtA = g;
                }
                else
                {
                    Agent ft = Live(_duelTarget) ? _duelTarget : Agent.Mission?.MainAgent;
                    if (CombatLive(ft))
                    {
                        if (!lookWriter)
                        {
                            _tacEnemies.Clear();
                            _tacEnemies.Add((ft.Index, ft.Position.AsVec2, true));
                            _tacPlan = TacticalMovement.Compute(Agent.Position.AsVec2, _tacEnemies, null,
                                MeleeReach(), _brainClock, dt, ref _tac, DuelRng);
                            if (_tacPlan.HasGoal)
                                goal = _tacPlan.Goal;
                        }
                        Vec2 toT = ft.Position.AsVec2 - Agent.Position.AsVec2;
                        if (toT.LengthSquared > 0.0001f)
                            tgtA = MathF.Atan2(toT.y, toT.x);
                        else
                            tgtA = MathF.Atan2(_ftHeading.y, _ftHeading.x);
                    }
                }
                if (tgtA < -900f)
                {
                    // No live attacker / hold case: aim at whatever we were
                    // already commanded (first tick: current facing).
                    if (_ftHeading.LengthSquared > 0.0001f)
                        tgtA = MathF.Atan2(_ftHeading.y, _ftHeading.x);
                    else
                    {
                        Vec2 f0 = Agent.GetMovementDirection();
                        tgtA = f0.LengthSquared > 0.0001f
                            ? MathF.Atan2(f0.y, f0.x)
                            : MathF.Atan2(Agent.Frame.rotation.f.y, Agent.Frame.rotation.f.x);
                    }
                }
                // TARGET-jump cap (ai31): the policy target itself moves at
                // most 120deg/s, so even a primary switch is a gradual
                // retarget -- the smoother then only has to follow it.
                if (_ftTargetA < -900f)
                    _ftTargetA = tgtA;
                else
                {
                    float tJump = NormalizeAngle(tgtA - _ftTargetA);
                    float tMax = 2.0944f * dt;   // 120deg/s
                    _ftTargetA += MathF.Max(-tMax, MathF.Min(tMax, tJump));
                }
                // SECOND-ORDER slew (ai31): velocity state + acceleration
                // limit -- onset ramps, no instant jump to the rate cap.
                //   wantVel = clamp(K*err, -rate, +rate)
                //   angVel += clamp(wantVel - angVel, -acc*dt, +acc*dt)
                //   heading += angVel*dt   (snap+zero on overshoot)
                if (_ftHeading.LengthSquared < 0.0001f)
                    _ftHeading = new Vec2(MathF.Cos(tgtA), MathF.Sin(tgtA));
                float curA = MathF.Atan2(_ftHeading.y, _ftHeading.x);
                float hErr = NormalizeAngle(_ftTargetA - curA);
                float rate = CombatAiState.FreeTurnRateDegPerSec * 0.0174533f;
                float wantVel = MathF.Max(-rate, MathF.Min(rate, CombatAiState.FreeSmoothK * hErr));
                float acc = CombatAiState.FreeTurnAccelDegPerSec2 * 0.0174533f * dt;
                _ftAngVel += MathF.Max(-acc, MathF.Min(acc, wantVel - _ftAngVel));
                float step = _ftAngVel * dt;
                float newA;
                if (MathF.Abs(step) >= MathF.Abs(hErr) && MathF.Abs(hErr) > 0.00005f)
                {
                    newA = curA + hErr;   // would overshoot: arrive and stop
                    _ftAngVel = 0f;
                }
                else
                    newA = curA + step;
                _ftHeading = new Vec2(MathF.Cos(newA), MathF.Sin(newA));
                heading = _ftHeading;
                if (lookWriter)
                {
                    // Look writer: the frame must NOT own the legs. Strip a
                    // leftover frame once (switched writers mid-run) and leave
                    // MoveMask + _tmInputVec to the legacy locomotion paths.
                    if (_tmFormationSet)
                    {
                        _lastOp = "SetFormationFrameDisabled(free)";
                        Agent.SetFormationFrameDisabled();
                        _tmFormationSet = false;
                    }
                    // Write the heading only while the native slot is NULL --
                    // in attack windows the held lock owns facing. The rule
                    // is shared with EnsureTargetAgent (LockWantedNow) so
                    // they cannot disagree -- a body feint is an active
                    // WRITE window, not a locked one.
                    bool lockHeld = LockWantedNow();
                    // [TRACE] ring sample every freeWrite tick (cmd/act/vel/lock).
                    {
                        int li = lockHeld ? 1 : 0;
                        float cA = _ftHeading.LengthSquared > 0.0001f
                            ? MathF.Atan2(_ftHeading.y, _ftHeading.x) * 57.2958f : -999f;
                        _trT[_trIdx] = _brainClock;
                        _trCmd[_trIdx] = cA;
                        Vec2 aY = Agent.GetMovementDirection();
                        _trAct[_trIdx] = aY.LengthSquared > 0.0001f
                            ? MathF.Atan2(aY.y, aY.x) * 57.2958f : -999f;
                        _trVel[_trIdx] = _ftAngVel * 57.2958f;
                        _trLock[_trIdx] = li;
                        // Dump triggers: lock transition, or >60deg commanded
                        // jump inside the last 0.3s.
                        string trWhy = li != _trLockPrev ? (li == 1 ? "lockOn" : "lockOff") : null;
                        if (trWhy == null)
                        {
                            for (int k = 0; k < 64; k++)
                            {
                                int j = (_trIdx + 64 - 1 - k) % 64;
                                if (_trT[j] <= 0f || _brainClock - _trT[j] > 0.3f) break;
                                if (_trCmd[j] > -900f && cA > -900f
                                    && MathF.Abs(NormalizeAngle((cA - _trCmd[j]) * 0.0174533f)) * 57.2958f > 60f)
                                { trWhy = "jump"; break; }
                            }
                        }
                        _trLockPrev = li;
                        _trIdx = (_trIdx + 1) % 64;
                        if (trWhy != null && _trDumps < MaxTrDumps)
                        {
                            _trDumps++;
                            var sb = new System.Text.StringBuilder(256);
                            sb.Append($"[TRACE] Agent #{Agent.Index} reason={trWhy} t0={_brainClock:F2} samples:");
                            int shown = 0;
                            for (int k = 63; k >= 0 && shown < 16; k -= 4)
                            {
                                int j = (_trIdx + 64 - 1 - k) % 64;
                                if (_trT[j] <= 0f) continue;
                                sb.Append($" {_trT[j]:F2}:{_trCmd[j]:F0}/{_trAct[j]:F0}/{_trVel[j]:F0}/{_trLock[j]}");
                                shown++;
                            }
                            CombatAiLog.Log(sb.ToString());
                        }
                    }
                    if (lockHeld)
                        _ftAngVel = 0f;   // lock owns facing: kill our velocity state
                    if (!lockHeld || CombatAiState.HoldSpinMode)   // holdspin: writer ON while locked (shutter test)
                    {
                        if (CombatAiState.FreeFace == CombatAiState.FreeFaceT.LookDir)
                        {
                            _lastOp = "LookDirection(free)";
                            float z = Agent.LookDirection.z;
                            Agent.LookDirection = new Vec3(_ftHeading.x, _ftHeading.y, z);
                        }
                        else if (CombatAiState.FreeFace == CombatAiState.FreeFaceT.MoveDir)
                        {
                            // FUN_1806f3500: writes the +0x474 channel (dir,
                            // angle, order slot) + dynBody+0x34 heading + the
                            // aiComp+0x15d8.. mirror -- with target=-1 nothing
                            // recomputes it, so the write may stick as the
                            // BODY heading. MUST run before re-asserting the
                            // input vector (it clobbers the +0x4a4 slot).
                            // BODY FEINT: while armed the written heading is
                            // _ftHeading rotated by _bfSide * _bfOffsetDeg
                            // (ramp 0->55deg over ~0.18s, decays back ~0.12s
                            // after the feint states end).
                            if (_bfArmed)
                            {
                                _bfElapsed += dt;
                                float want = MathF.Min(55f, _bfElapsed * 55f / 0.18f);
                                _bfOffsetDeg = want;
                            }
                            else if (_bfOffsetDeg > 0f)
                                _bfOffsetDeg = MathF.Max(0f, _bfOffsetDeg - 55f * dt / 0.12f);
                            Vec2 wdir = _ftHeading;
                            if (_bfOffsetDeg != 0f)
                            {
                                float ra = _bfSide * _bfOffsetDeg * 0.0174533f;
                                float c = MathF.Cos(ra), s = MathF.Sin(ra);
                                wdir = new Vec2(_ftHeading.x * c - _ftHeading.y * s,
                                                _ftHeading.x * s + _ftHeading.y * c);
                            }
                            // [SMOOTH] probe (ai30): pre-write body yaw vs
                            // last-tick's commanded heading -> overwrites;
                            // consecutive yaw delta vs commanded step -> jerk.
                            Vec2 legsBefore = Agent.GetMovementDirection();
                            float yawNow = legsBefore.LengthSquared > 0.0001f
                                ? MathF.Atan2(legsBefore.y, legsBefore.x) * 57.2958f : -999f;
                            if (yawNow > -900f && _smLastCmdDeg > -900f)
                            {
                                if (MathF.Abs(NormalizeAngle((yawNow - _smLastCmdDeg) * 0.0174533f)) * 57.2958f > 3f)
                                    _smOver++;
                                if (_smLastYawDeg > -900f)
                                {
                                    float dYaw = NormalizeAngle((yawNow - _smLastYawDeg) * 0.0174533f) * 57.2958f;
                                    if (_smLastStepDeg != 0f
                                        && (MathF.Abs(dYaw) > 2.5f * MathF.Abs(_smLastStepDeg)
                                            || (MathF.Sign(dYaw) != MathF.Sign(_smLastStepDeg) && MathF.Abs(dYaw) > 2f)))
                                        _smJerk++;
                                }
                            }
                            _smTicks++;
                            if (dt < _smDtMin) _smDtMin = dt;
                            if (dt > _smDtMax) _smDtMax = dt;
                            _smDtSum += dt;
                            float vNow = MathF.Abs(_ftAngVel) * 57.2958f;
                            if (vNow > _smMaxVel) _smMaxVel = vNow;
                            if (dt > 0.0001f)
                            {
                                float aNow = MathF.Abs(vNow - _smPrevVel) / dt;
                                if (aNow > _smMaxAcc) _smMaxAcc = aNow;
                            }
                            _smPrevVel = vNow;
                            _lastOp = "SetMovementDirection(free)";
                            float wDeg = MathF.Atan2(wdir.y, wdir.x) * 57.2958f;
                            Agent.SetMovementDirection(wdir);
                            _smLastCmdDeg = wDeg;
                            _smLastStepDeg = yawNow > -900f
                                ? NormalizeAngle((wDeg - yawNow) * 0.0174533f) * 57.2958f : 0f;
                            _smLastYawDeg = yawNow;
                            if (_smLogCount < MaxSmLogs && _brainClock - _smLastAt >= 2f && _smTicks > 0)
                            {
                                _smLastAt = _brainClock;
                                _smLogCount++;
                                CombatAiLog.Log($"[SMOOTH] Agent #{Agent.Index}: ticks={_smTicks} " +
                                    $"dtMin={_smDtMin:F3} dtMax={_smDtMax:F3} dtMean={_smDtSum / _smTicks:F3} " +
                                    $"maxVel={_smMaxVel:F0} maxAcc={_smMaxAcc:F0} " +
                                    $"jerk={_smJerk} overwrites={_smOver}");
                                _smTicks = 0; _smJerk = 0; _smOver = 0;
                                _smMaxVel = 0f; _smMaxAcc = 0f;
                                _smDtMin = 9f; _smDtMax = 0f; _smDtSum = 0f;
                            }
                            // Head/aim agree with the body heading.
                            float mz = Agent.LookDirection.z;
                            Agent.LookDirection = new Vec3(wdir.x, wdir.y, mz);
                            if (_bfArmed && legsBefore.LengthSquared > 0.0001f)
                            {
                                float wdeg = MathF.Atan2(wdir.y, wdir.x) * 57.2958f;
                                float ldeg = MathF.Atan2(legsBefore.y, legsBefore.x) * 57.2958f;
                                float e = MathF.Abs(NormalizeAngle((wdeg - ldeg) * 0.0174533f)) * 57.2958f;
                                if (e > _bfPeakErr) _bfPeakErr = e;
                            }
                            if (!_ftMoveDirLogged)
                            {
                                _ftMoveDirLogged = true;
                                Vec2 legsAfter = Agent.GetMovementDirection();
                                float setDeg = MathF.Atan2(_ftHeading.y, _ftHeading.x) * 57.2958f;
                                CombatAiLog.Log($"[FREETGT] Agent #{Agent.Index}: movedir first write " +
                                    $"set={setDeg:F0} legsBefore={(legsBefore.LengthSquared > 0.0001f ? (MathF.Atan2(legsBefore.y, legsBefore.x) * 57.2958f).ToString("F0") : "na")} " +
                                    $"legsAfter={(legsAfter.LengthSquared > 0.0001f ? (MathF.Atan2(legsAfter.y, legsAfter.x) * 57.2958f).ToString("F0") : "na")}");
                            }
                            // +0x4a4 clobbered by SetMovementDirection: re-assert
                            // any vector the earlier locomotion writers set
                            // (a live dodge step's dir, else the mirrored vec).
                            Vec2 reassert = DodgeOwnsBody && !_dodgeIsAnim ? _dodgeDir : _tmInputVec;
                            if (reassert.LengthSquared > 0.0001f)
                            {
                                _lastOp = "MovementInputVector(free-mdir)";
                                Agent.MovementInputVector = reassert;
                            }
                        }
                        else
                        {
                            float wantA = MathF.Atan2(-_ftHeading.x, _ftHeading.y);
                            _lastOp = "LookDirectionAsAngle(free)";
                            Agent.LookDirectionAsAngle = NormalizeAngle(wantA);
                            if (!_ftLookRtLogged)
                            {
                                _ftLookRtLogged = true;
                                float gotA = Agent.LookDirectionAsAngle;
                                Vec3 got = Agent.LookDirection;
                                CombatAiLog.Log($"[FREETGT] Agent #{Agent.Index}: lookangle round-trip " +
                                    $"set={wantA * 57.2958f:F1} get={gotA * 57.2958f:F1} " +
                                    $"dir=({got.x:F2},{got.y:F2},{got.z:F2}) worldBear={MathF.Atan2(got.y, got.x) * 57.2958f:F0}.");
                            }
                        }
                    }
                }
                else
                {
                    if (_tmFormationSet && !Agent.IsFormationFrameEnabled)
                        _tmStomps++;   // engine stomped our frame since last tick
                    Vec2 gp = Agent.Position.AsVec2 + goal;
                    Vec2 fvel = Agent.GetCurrentVelocity();
                    _lastOp = "FormationFrame(free)";
                    Agent.SetFormationFrameEnabled(
                        new TaleWorlds.Engine.WorldPosition(Mission.Current.Scene, UIntPtr.Zero, new Vec3(gp.x, gp.y, Agent.Position.z), false),
                        _ftHeading, fvel, 1.0f);
                    _tmFormationSet = true;
                    desired &= ~Agent.MovementControlFlag.MoveMask;   // frame owns the legs
                }
                if (_ftLogCount < MaxFreeTgtLogs && _ftEngaged && _brainClock - _ftLastLogAt >= 0.5f)
                {
                    _ftLastLogAt = _brainClock;
                    _ftLogCount++;
                    Vec2 legs = Agent.GetMovementDirection();
                    float legsDeg = legs.LengthSquared > 0.0001f ? MathF.Atan2(legs.y, legs.x) * 57.2958f : -999f;
                    float headDeg = MathF.Atan2(_ftHeading.y, _ftHeading.x) * 57.2958f;
                    float err = legsDeg > -900f
                        ? MathF.Abs(NormalizeAngle((headDeg - legsDeg) * 0.0174533f)) * 57.2958f : -1f;
                    Vec3 look3 = Agent.LookDirection;
                    float lookDeg = look3.LengthSquared > 0.0001f ? MathF.Atan2(look3.y, look3.x) * 57.2958f : -999f;
                    float errLook = lookDeg > -900f
                        ? MathF.Abs(NormalizeAngle((headDeg - lookDeg) * 0.0174533f)) * 57.2958f : -1f;
                    Vec2 vel = Agent.GetCurrentVelocity();
                    float velSpd = vel.Length;
                    float velRel = velSpd > 0.05f && legsDeg > -900f
                        ? NormalizeAngle((MathF.Atan2(vel.y, vel.x) * 57.2958f - legsDeg) * 0.0174533f) * 57.2958f : -999f;
                    Agent cur = Agent.GetTargetAgent();
                    CombatAiLog.Log($"[FREETGT] Agent #{Agent.Index}: mode={(sparDefFree ? "sparDef" : "duel")} " +
                        $"face={CombatAiState.FreeFace} spread={_ftSpread:F0} head={headDeg:F0} legs={legsDeg:F0} err={err:F0} " +
                        $"lookYaw={lookDeg:F0} errLook={errLook:F0} vel={velSpd:F2} " +
                        $"velRel={(velRel > -900f ? velRel.ToString("F0") : "na")} " +
                        $"turn={CombatAiState.FreeTurnRateDegPerSec:F0} nativeTgt={(Live(cur) ? "#" + cur.Index : "-1")} " +
                        $"intendedTgt={(Live(IntendedTarget) ? "#" + IntendedTarget.Index : "-1")} " +
                        $"ffOn={Agent.IsFormationFrameEnabled} stomps={_tmStomps} armed={NativeTargetGuard.ArmedLevel}");
                }
            }
            // [SPARVIEW] metric for the free spar defender (1.0s, cap 150):
            // % of window frames BOTH attackers are inside +/-45deg of the
            // look yaw AND the body yaw, plus bearings/dists/vel/nativeTgt.
            bool svActive = freeWrite && sparDefFree && _ftEngaged;
            if (svActive || _svWasActive)
            {
                Agent sva = GetAgentByIndex(CombatAiState.SparAttackerIndex);
                Agent svp = Agent.Mission?.MainAgent;
                _svWasActive = svActive && (CombatLive(sva) || CombatLive(svp));
                if (svActive && (CombatLive(sva) || CombatLive(svp)))
                {
                    Vec2 lgs = Agent.GetMovementDirection();
                    Vec3 lk = Agent.LookDirection;
                    float lgDeg = lgs.LengthSquared > 0.0001f ? MathF.Atan2(lgs.y, lgs.x) * 57.2958f : -999f;
                    float lkDeg = lk.LengthSquared > 0.0001f ? MathF.Atan2(lk.y, lk.x) * 57.2958f : -999f;
                    float hdDeg = MathF.Atan2(_ftHeading.y, _ftHeading.x) * 57.2958f;
                    float errB = lgDeg > -900f ? MathF.Abs(NormalizeAngle((hdDeg - lgDeg) * 0.0174533f)) * 57.2958f : -1f;
                    float errL = lkDeg > -900f ? MathF.Abs(NormalizeAngle((hdDeg - lkDeg) * 0.0174533f)) * 57.2958f : -1f;
                    float b1 = -999f, b2 = -999f, d1 = -1f, d2 = -1f;
                    if (CombatLive(sva)) { Vec2 t = sva.Position.AsVec2 - Agent.Position.AsVec2; b1 = MathF.Atan2(t.y, t.x) * 57.2958f; d1 = t.Length; }
                    if (CombatLive(svp)) { Vec2 t = svp.Position.AsVec2 - Agent.Position.AsVec2; b2 = MathF.Atan2(t.y, t.x) * 57.2958f; d2 = t.Length; }
                    _svSamples++;
                    bool inL = lkDeg > -900f
                        && (b1 < -900f || MathF.Abs(NormalizeAngle((b1 - lkDeg) * 0.0174533f)) * 57.2958f <= 45f)
                        && (b2 < -900f || MathF.Abs(NormalizeAngle((b2 - lkDeg) * 0.0174533f)) * 57.2958f <= 45f);
                    bool inB = lgDeg > -900f
                        && (b1 < -900f || MathF.Abs(NormalizeAngle((b1 - lgDeg) * 0.0174533f)) * 57.2958f <= 45f)
                        && (b2 < -900f || MathF.Abs(NormalizeAngle((b2 - lgDeg) * 0.0174533f)) * 57.2958f <= 45f);
                    if (inL) _svViewL++;
                    if (inB) _svViewB++;
                    if (_svLogCount < MaxSvLogs && _brainClock - _svLastLogAt >= 1.0f)
                    {
                        _svLastLogAt = _brainClock;
                        _svLogCount++;
                        Agent cur = Agent.GetTargetAgent();
                        CombatAiLog.Log($"[SPARVIEW] Agent #{Agent.Index}: spread={_ftSpread:F0} " +
                            $"inView90={(100f * _svViewL / _svSamples):F0}% inViewBody={(100f * _svViewB / _svSamples):F0}% " +
                            $"errBody={errB:F0} errLook={errL:F0} a1Bearing={b1:F0} a2Bearing={b2:F0} " +
                            $"dist1={d1:F2} dist2={d2:F2} vel={Agent.GetCurrentVelocity().Length:F2} " +
                            $"nativeTgt={(Live(cur) ? "#" + cur.Index : "-1")}");
                    }
                }
                else
                    CombatAiLog.Log($"[SPARVIEW] Agent #{Agent.Index}: OFF inView90={( _svSamples > 0 ? 100f * _svViewL / _svSamples : 0f):F0}% " +
                        $"inViewBody={(_svSamples > 0 ? 100f * _svViewB / _svSamples : 0f):F0}% samples={_svSamples}");
            }

            // One locomotion writer rule: only the frame writer strips
            // MoveMask -- the look writers leave legs to legacy paths.
            bool freeFrameDrive = freeDrive && CombatAiState.FreeFace == CombatAiState.FreeFaceT.Frame;
            if ((!duelBm || bmGoal.LengthSquared < 0.0025f) && !duelInputActive && !freeFrameDrive)
            {
                // Window over / no intent: drop our frame (but not the spar
                // defender's -- this code never runs there anyway; and not
                // the faceframe experiment's -- its frame is re-installed
                // every tick inside ComputeDesiredFlag). Skip while the input
                // writer is live or TorsoMoveCleanup zeroes our vector.
                if (_tmFormationSet && !CombatAiState.SparMode && !faceFrame)
                    TorsoMoveCleanup();
            }
            // One locomotion writer: while the frame owns the legs, strip move bits.
            if (_tmFormationSet && (duelBm || freeFrameDrive))
                desired &= ~Agent.MovementControlFlag.MoveMask;
            if ((duelBm || duelInputActive) && _bmLogCount < 2000 && _brainClock - _bmLastLogAt >= 0.4f)
            {
                _bmLastLogAt = _brainClock;
                _bmLogCount++;
                Agent bmT = Agent.Mission?.MainAgent;
                float bmD = CombatLive(bmT) ? (bmT.Position.AsVec2 - Agent.Position.AsVec2).Length : -1f;
                CombatAiLog.Log($"[BLOCKMOVE] Agent #{Agent.Index}: mode={CombatAiState.DuelBlockMove} d={bmD:F2} " +
                    $"intent=({bmGoal.x:F2},{bmGoal.y:F2}) spd={Agent.GetCurrentVelocity().Length:F2} " +
                    $"ffEnabled={Agent.IsFormationFrameEnabled} stomps={_tmStomps} win={_nativeLocoWindow} factor={CombatAiState.DuelBlockFactor:F2} " +
                    $"plan={_tacPlan.Goal.Length:F2} orbit={_tac.OrbitSign:F0} primary=#{_tacPlan.PrimaryKey}");
            }

            if (driving)
            {
                // Per-tick speed enforcement (Session 35e/f). The [TAKEOVER]
                // log answered it: max cap is already -1 (uncapped) and the
                // CURRENT limit tracks gait -- ~1.7 m/s walk, ~3.4 m/s run.
                // So the "cap" with isMultiplier:true is a SPEED SCALER, not
                // a ceiling (the 2.5x dodge cap produced visibly fast steps).
                // We scale flag locomotion to combat-run pace while driving.
                // Plus the two other possible governors stay enforced:
                // WalkMode (anim gait -- engine's own Run flip, Agent.cs:1229)
                // and scripted-frame forced-walk bits (DoNotRun/Crouch).
                bool preWalk = false;
                float preCur = 0f, preMax = -1f;
                if (!_drivingMovementFlags)
                {
                    preWalk = Agent.WalkMode;
                    preCur = Agent.GetCurrentSpeedLimit();
                    preMax = Agent.GetMaximumSpeedLimit();
                }
                Agent.AIScriptedFrameFlags scrFlags = Agent.GetScriptedFlags();
                if (Agent.WalkMode)
                {
                    _lastOp = "EventControlFlags(Run)";
                    Agent.EventControlFlags |= Agent.EventControlFlag.Run;
                }
                if ((scrFlags & (Agent.AIScriptedFrameFlags.DoNotRun | Agent.AIScriptedFrameFlags.Crouch)) != 0)
                {
                    _lastOp = "ScriptedFlags(clear)";
                    Agent.SetScriptedFlags(scrFlags & ~(Agent.AIScriptedFrameFlags.DoNotRun | Agent.AIScriptedFrameFlags.Crouch));
                }
                // Assert the run-speed scale; the dodge owns the cap while
                // its step is raised, and foreign writers (formation
                // AdjustSpeedLimit, per parallel tick) get re-asserted.
                if (!_dodgeSpeedRaised && !_cavDriving && Agent.GetMaximumSpeedLimit() != DriveSpeedScale)
                {
                    _lastOp = "SetMaxSpeed(drive)";
                    Agent.SetMaximumSpeedLimit(DriveSpeedScale, isMultiplier: true);
                    _driveSpeedScaled = true;
                }
                if (!_drivingMovementFlags && _takeoverLogCount < MaxTakeoverLogs)
                {
                    _takeoverLogCount++;
                    CombatAiLog.Log($"[TAKEOVER] Agent #{Agent.Index} ({Agent.Name}): walk={preWalk} scrFlags={scrFlags} speedLimit={preCur:F2}/{preMax:F2}.");
                }
                _lastOp = "MovementFlags=desired";
                Agent.MovementFlags = _nativeLocoWindow
                    ? desired & ~Agent.MovementControlFlag.MoveMask
                    : desired;
                _drivingMovementFlags = true;
            }
            else if (_drivingMovementFlags)
            {
                // A mode just switched off (or we walked out of range) while we were
                // holding a flag -- release it once so the agent isn't left frozen
                // mid-pose holding a stale flag. Restore the speed scale too --
                // the handed-off/native side expects its own uncapped base.
                _lastOp = "MovementFlags=none";
                Agent.MovementFlags = Agent.MovementControlFlag.None;
                _drivingMovementFlags = false;
                _nativeLocoWindow = false;
                if (_driveSpeedScaled)
                {
                    _lastOp = "SetMaxSpeed(release)";
                    Agent.SetMaximumSpeedLimit(-1f, isMultiplier: false);
                    _driveSpeedScaled = false;
                }
            }
            else if (_handOffToNative)
            {
                // Same void as the OnAIInputSet handoff strip, per-frame: native
                // owns locomotion here, but its combat flags must not linger
                // between its ~1Hz input callbacks. A defend flag it raised out
                // of range survives the take-over as a held wrong-line pose.
                // Movement bits pass through untouched.
                Agent.MovementFlags &= ~(Agent.MovementControlFlag.AttackMask
                    | Agent.MovementControlFlag.DefendMask
                    | Agent.MovementControlFlag.DefendBlock);
            }

            // Ground-truth speed diagnostic (Session 35g): logs the ACTUAL
            // measured velocity (not a setting we wrote) alongside every
            // relevant governor, every ~0.4s. This settles the run/walk
            // question with real numbers instead of another inferred fix.
            // Session 59: only DRIVEN fight participants may log, and only up
            // to the per-mission global budget (arena = ~180 tracked agents).
            bool speedParticipant = driving
                && ((CombatAiState.DuelMode && !CombatAiState.SparMode)
                    || (CombatAiState.SparMode
                        && (Agent.Index == CombatAiState.SparDefenderIndex || Agent.Index == CombatAiState.SparAttackerIndex)));
            if (CombatAiState.SpeedLogMode && speedParticipant && CombatAiState.SpeedLogLines < CombatAiState.MaxSpeedLogLines)
            {
                _speedLogTimer += dt;
                if (_speedLogTimer >= 0.4f)
                {
                    _speedLogTimer = 0f;
                    CombatAiState.SpeedLogLines++;
                    float actualSpeed = Agent.GetCurrentVelocity().Length;
                    CombatAiLog.Log($"[SPEEDLOG] Agent #{Agent.Index} ({Agent.Name}): driving={driving} handoff={_handOffToNative} " +
                        $"actualVel={actualSpeed:F2} walk={Agent.WalkMode} speedLimit={Agent.GetCurrentSpeedLimit():F2}/{Agent.GetMaximumSpeedLimit():F2} " +
                        $"flags={Agent.MovementFlags} scrFlags={Agent.GetScriptedFlags()} ownStage={locoStage} " +
                        $"win={_nativeLocoWindow} nEvt={_lastNativeEvent} nMove={_lastNativeMove} gaitFix={CombatAiState.GaitFix} " +
                        $"dodgeT={_dodgeTime:F2} blockOnly={_dodgeBlockOnly} iv=({_tmInputVec.x:F2},{_tmInputVec.y:F2}).");
                }
            }
        }

        private Agent.MovementControlFlag ComputeDesiredFlag(float dt, out bool driving)
        {
            bool eligible = IsEligibleForTestModes();

            // Mode-transition cleanup: looktest installs a scripted target frame
            // via SetTargetPositionAndDirection. If nothing clears it when the
            // mode switches off, the agent keeps a stale movement target --
            // legs freeze, only the torso still works (observed: duel broke
            // after toggling modes). ClearTargetFrame is the vanilla counterpart
            // (standing points/ladders call it on exit); DisableScriptedMovement
            // is the belt-and-suspenders reset HumanAIComponent uses.
            if (_targetFrameSet && !FrameInUseByActiveMode(eligible))
            {
                _lastOp = "ClearTargetFrame(mode)";
                Agent.ClearTargetFrame();
                _lastOp = "DisableScriptedMovement(mode)";
                Agent.DisableScriptedMovement();
                _targetFrameSet = false;
                // NOTE: no EnsureTargetAgent(null) here -- a target-null window
                // in active melee is the crash vector; native auto-select
                // picks a fresh target on its own once restored below.
                // (No log: hold-sway install/clear cycles made this fire
                // every ~0.3s per agent -- pure spam.)
            }

            // Stale locomotion input (Session 34): duel mode no longer writes
            // MovementInputVector at all (flags-only movement), but the dodge
            // locomotion step can -- zero any leftover when every driven
            // combat mode is off.
            if (!(CombatAiState.DuelMode || CombatAiState.SparMode || CombatAiState.GroupFightMode)
                && Agent.MovementInputVector.LengthSquared > 0.0001f)
            {
                _lastOp = "MovementInputVector(mode)";
                Agent.MovementInputVector = Vec2.Zero;
            }

            // Same rule for target selection: if we disabled native
            // auto-retargeting (perfectblock's on-demand lock, looktest),
            // restore it whenever that state ends so targeting isn't stuck
            // off. Duel no longer disables auto-selection at all (target-null
            // states were the crash vector) -- it is NOT exempt here, so a
            // leftover disabled state gets restored when switching into duel.
            bool isSparMember = CombatAiState.SparMode && eligible
                && (Agent.Index == CombatAiState.SparDefenderIndex || Agent.Index == CombatAiState.SparAttackerIndex);
            bool perfectBlockActive = CombatAiState.PerfectBlockMode && eligible;
            if (_autoTargetDisabled && !isSparMember && !perfectBlockActive && !_freeTargetActive
                && !_steerGroup   // pair fighters keep auto-select off for the whole fight
                && !(CombatAiState.LookTestMode && eligible)
                && !(CombatAiState.FaceFrameMode && Agent.Index == CombatAiState.FaceFrameAgentIndex))
            {
                SetAutoTargeting(true);
                _freeStance = false;
            }

            // A pending look-POI survives a mode switch too -- release it.
            if (_poiActive && !(CombatAiState.LookTestMode && eligible))
            {
                _lastOp = "DisableLookToPOI(mode)";
                Agent.DisableLookToPointOfInterest();
                _poiActive = false;
            }

            // PerfectBlock captures its start frame once; reset on mode exit.
            if (!CombatAiState.PerfectBlockMode)
            {
                _pbFrameCaptured = false;
                _pbTargetHold = 0f;
            }
            if (!CombatAiState.LookTestMode)
                _lookTestTorsoCaptured = false;
            if (!CombatAiState.FaceFrameMode)
            {
                _ffSpinAngle = 0f;   // fresh sweep on each enable
                _ffPrevLegsDeg = -999f;   // fresh dLegs baseline
                _ffActSeen = false;       // fresh action-change baseline
                _ffActLogCount = 0;
                _ffReleased = false;      // fresh lock-none release
                _ffRelockCheckAt = -99f;
                _ffMoveAngle = 0f;
                _ffMoveVec = Vec2.Zero;
                _ffLookRtLogged = false;
                if (_tmInputVec.LengthSquared > 0.0001f)
                {
                    _tmInputVec = Vec2.Zero;
                    Agent.MovementInputVector = Vec2.Zero;
                }
            }

            // FaceFrame experiment (combatai.faceframe): ONE designated agent
            // holds a combat lock on the player while a formation frame asks
            // for a facing FaceFrameAngleDeg off the agent->player bearing --
            // does the frame's direction beat the lock's facing? Highest-
            // priority standalone test mode; the command exclusivizes anyway.
            // Non-designated agents fall through to their own modes/native.
            if (CombatAiState.FaceFrameMode && eligible
                && Agent.Index == CombatAiState.FaceFrameAgentIndex)
            {
                driving = true;
                _desiredMovementFlag = ComputeFaceFrameFlag(dt);
                return _desiredMovementFlag;
            }

            // Look-test: stand still facing the player, oscillate look +/-15deg
            // with native recenter between holds (upper-body control experiment).
            if (CombatAiState.LookTestMode && eligible)
            {
                driving = true;
                _desiredMovementFlag = ComputeLookTestFlag(dt);
                return _desiredMovementFlag;
            }

            // Spar mode ISOLATES the designated pair: only the defender and
            // attacker are driven; every other ELIGIBLE tracked agent stands
            // down (MovementFlags=None) so the experiment isn't drowned out by
            // unrelated fighters. Designated agents reach this branch via the
            // eligibility bypass above even on the player's own team.
            // INDEPENDENT of DuelMode -- `combatai.duel` is no longer required
            // (the attacker reuses the duel FSM internally, that's code reuse,
            // not the mode toggle).
            if (CombatAiState.SparMode && eligible)
            {
                driving = true;
                if (Agent.Index == CombatAiState.SparDefenderIndex || Agent.Index == CombatAiState.SparAttackerIndex)
                    EnsureArenaWeaponDrawn();
                if (Agent.Index == CombatAiState.SparDefenderIndex)
                {
                    // Disable native retargeting: our SetTargetAgent becomes
                    // deterministic -- the torso coverage follows OUR threat
                    // choice, native never fights it back to the closest enemy.
                    SetAutoTargeting(false);
                    _useAssignedThreats = false;
                    _groupDefender = false;
                    // Session 62: the rig path (no pin/frame; WASD-channel legs).
                    // Legacy path runs whenever the rig toggle or mount fails.
                    // FREE TARGET: the frame is the only writer, so the free
                    // defender always runs the rig path (TorsoMove levers and
                    // the scripted pin all depend on the lock).
                    // NATIVE STEER: same rule -- a steered defender must never
                    // run the legacy scripted-pin facing API (it would fight
                    // the steer channel), so the rig path is forced too.
                    if ((CombatAiState.SparRig || _freeTargetActive || _steerSparDef) && !Agent.HasMount)
                        _desiredMovementFlag = ComputeSparRigDefenderFlag(dt);
                    else
                    {
                        _rigDriving = false;
                        _desiredMovementFlag = ComputeSparDefenderFlag(dt);
                    }
                }
                else if (Agent.Index == CombatAiState.SparAttackerIndex)
                {
                    SetAutoTargeting(false);
                    // Attacker fights only the defender: full feint rate, no
                    // group threat widening, no forced focus (the isSparAttacker
                    // block inside ComputeDuelFlag pins the defender).
                    _forcedDuelTarget = null;
                    _sparThreats = false;
                    _useAssignedThreats = false;
                    _groupDefender = false;
                    _pureReactiveDefense = false;
                    _legPinOwned = false;
                    _sparPinSet = false;
                    TorsoMoveCleanup();
                    _feintRateScale = 1f;
                    _multiEnemy = false;
                    _desiredMovementFlag = ComputeSparAttackerFlag(dt);
                    if (_handOffToNative)
                        driving = false;
                }
                else
                    _desiredMovementFlag = Agent.MovementControlFlag.None;
                return _desiredMovementFlag;
            }

            // GROUP FIGHT: whole-field combat AI -- EngagementMap assigns each
            // AI an untargeted enemy (mutual 1v1s), extras gang up, 3rd+
            // attacker flanks for the back. Agents engaging the player (or
            // assigned to him) get the T1 brain; pure AI-vs-AI pairs run T2.
            // Infantry only: mounted/ranged fall through to native control.
            if (CombatAiState.GroupFightMode && eligible)
            {
                // Arena-armed group fight: pit spawns carry sheathed weapons --
                // no-op unless ArenaFightActive (see EnsureArenaWeaponDrawn).
                EnsureArenaWeaponDrawn();
                if (WieldsMeleeWeapon() && !Agent.HasMount)
                {
                    _useAssignedThreats = true;
                    _pureReactiveDefense = false;
                    _legPinOwned = false;
                    _sparPinSet = false;
                    TorsoMoveCleanup();
                    driving = true;
                    _desiredMovementFlag = ComputeGroupFlag(dt);
                    if (_handOffToNative)
                        driving = false;
                    return _desiredMovementFlag;
                }
            }

            // Arena DEFEND-2v1 (menu-armed, DuoAttackMode): every participant
            // attacks the PLAYER with the full duel FSM (aggressive duo tuning),
            // each holding its own front slot (player's left / right) inside the
            // player's view, swinging only away from the partner.
            if (CombatAiState.DuoAttackMode && eligible && ArenaDuoCoordinator.IsParticipant(Agent.Index))
                EnsureArenaWeaponDrawn();
            if (CombatAiState.DuoAttackMode && eligible)
            {
                if (WieldsMeleeWeapon() && ArenaDuoCoordinator.IsParticipant(Agent.Index))
                {
                    Agent duoPlayer = Agent.Mission?.MainAgent;
                    _forcedDuelTarget = CombatLive(duoPlayer) ? duoPlayer : null;
                    _sparThreats = false;
                    _useAssignedThreats = false;
                    _groupDefender = false;
                    _pureReactiveDefense = false;
                    _legPinOwned = false;
                    _sparPinSet = false;
                    TorsoMoveCleanup();
                    _feintRateScale = 1f;
                    _multiEnemy = false;
                    driving = true;
                    _desiredMovementFlag = ComputeDuoAttackerFlag(dt);
                    if (_handOffToNative)
                        driving = false;
                    return _desiredMovementFlag;
                }
            }

            // CAV DUEL (Session 74, mounted 1v1): the shared duel FSM keeps
            // attack/defend/lock ownership UNCHANGED; the cav planner
            // (CombatAiOverrideComponent.Cav.cs) owns locomotion -- charge/
            // pass/wheel on the input channel. Mounted agents only; no
            // scripted pin / frame writer / TorsoMoveCleanup on a horse.
            if (CombatAiState.CavDuelMode && eligible && Agent.HasMount && WieldsMeleeWeapon())
            {
                _forcedDuelTarget = null;
                _sparThreats = false;
                _useAssignedThreats = false;
                _groupDefender = false;
                _pureReactiveDefense = false;
                _legPinOwned = false;
                _sparPinSet = false;
                EnsureArenaWeaponDrawn();
                _feintRateScale = 1f;
                _multiEnemy = false;
                driving = true;
                _cavDriving = true;
                _desiredMovementFlag = ComputeCavFlag(dt);
                if (_handOffToNative)
                    driving = false;
                return _desiredMovementFlag;
            }
            if (_cavDriving)
                CavReleaseControl();   // mode off mid-ride: uncap mount+rider, free input

            // Duel mode = the real v1 brain; highest priority over the isolated
            // test modes. Melee-only: ranged wielders get nothing from a melee FSM.
            if (CombatAiState.DuelMode && eligible)
            {
                if (WieldsMeleeWeapon())
                {
                    // Standalone duel: no group/spar pinning -- normalize the
                    // mode fields (they're caller-set per frame elsewhere).
                    _forcedDuelTarget = null;
                    _sparThreats = false;
                    _useAssignedThreats = false;
                    _groupDefender = false;
                    _pureReactiveDefense = false;
                    _legPinOwned = false;
                    _sparPinSet = false;
                    TorsoMoveCleanup();
                    _feintRateScale = 1f;
                    _multiEnemy = false;
                    driving = true;
                    _desiredMovementFlag = ComputeDuelFlag(dt);
                    // Defense-only test mode: safety-mask any leaked attack
                    // flag. The FSM is forced to Approach and StartDuelAttack
                    // is a no-op, but a direct attack-flag return in some edge
                    // branch would still fire; strip it here so the mode is
                    // truly no-attack.
                    if (CombatAiState.DefenseOnly)
                    {
                        _desiredMovementFlag &= ~(Agent.MovementControlFlag.AttackLeft
                            | Agent.MovementControlFlag.AttackRight
                            | Agent.MovementControlFlag.AttackUp
                            | Agent.MovementControlFlag.AttackDown);
                    }
                    // Leash handoff: outside our range the base game drives
                    // the body (see ComputeDuelFlag) -- release the flag word
                    // so native locomotion isn't fighting a held write.
                    if (_handOffToNative)
                        driving = false;
                    return _desiredMovementFlag;
                }
            }

            if (CombatAiState.PerfectBlockMode && eligible)
            {
                driving = true;
                _desiredMovementFlag = ComputePerfectBlockFlag(dt);
                return _desiredMovementFlag;
            }

            // Evade-only mode: 100% dodge vs every committed release in reach,
            // no attacks/blocks. The pure-dodge counterpart of PerfectBlockMode.
            if (CombatAiState.EvadeMode && eligible)
            {
                driving = true;
                _desiredMovementFlag = ComputeEvadeFlag(dt);
                return _desiredMovementFlag;
            }

            // Angle-dodge mode: slight-angle Left/Right swing -> strafe until the
            // existing dodge condition is true, then the normal dodge at release.
            if (CombatAiState.AngleDodgeMode && eligible)
            {
                driving = true;
                _desiredMovementFlag = ComputeAngleDodgeFlag(dt);
                return _desiredMovementFlag;
            }

            // AttackLoop additionally requires a MELEE weapon in hand: forcing an
            // attack flag on an agent wielding a bow/crossbow makes them shoot --
            // observed in-game as town guards firing at the player from outside
            // the arena when attackloop was toggled on.
            if (CombatAiState.ContinuousAttackMode && eligible && WieldsMeleeWeapon())
            {
                driving = true;
                _desiredMovementFlag = ComputeAttackLoopFlag(dt);
                return _desiredMovementFlag;
            }

            driving = false;
            _desiredMovementFlag = Agent.MovementControlFlag.None;
            _loopState = AttackLoopState.Inactive;
            _duelState = DuelState.Approach;
            _handOffToNative = false;
            _opportunistAllowed = false;
            _forcedDuelTarget = null;
            _sparThreats = false;
            _useAssignedThreats = false;
            _groupDefender = false;
            _pureReactiveDefense = false;
            _legPinOwned = false;
            _sparPinSet = false;
            TorsoMoveCleanup();
            _feintRateScale = 1f;
            _multiEnemy = false;
            _chainDecided = false;
            _sawRelease = false;
            _dodgedThisChain = false;   // full chain-state reset -- a stale
                                        // latch would suppress the first
                                        // dodge roll of the next session
            _blockedTheirSwing = false;
            _defCommittedReset();
            _feintStreak = 0;
            _pushRolled = false;
            _pushUntil = 0f;
            _kickPrevActive = false;
            _ownActionActive = false;
            _playerSwungDuringOwn = false;
            _recoverBoost = 0f;
            _stallPunished = false;
            _frozeThisRelease = false;
            _misreadDefendFlag = Agent.MovementControlFlag.None;
            _groupAdvancing = false;
            return Agent.MovementControlFlag.None;
        }

        private bool InTestRange()
        {
            Agent main = Agent.Mission?.MainAgent;
            return CombatLive(main)
                && Agent.Position.DistanceSquared(main.Position) < TestRangeMeters * TestRangeMeters;
        }

        // Only agents that are actually hostile to (or already fighting) the
        // player participate in test modes. Without this, every tracked human
        // within range -- civilians, uninvolved guards -- gets driven into
        // combat by our flag writes, which produced stray swings and crossbow
        // fire from outside the arena during attackloop tests.
        private bool IsEligibleForTestModes()
        {
            _lastOp = "eligibility";
            // Spar-designated agents bypass the checks below ENTIRELY: a
            // defender placed on the player's own team is never hostile to
            // the player, so the normal gate can never pass for it (the
            // reason spar teammates did nothing in custom battles). Range is
            // bypassed too -- the pair may fight outside the 15m test bubble.
            if (CombatAiState.SparMode
                && (Agent.Index == CombatAiState.SparDefenderIndex
                    || Agent.Index == CombatAiState.SparAttackerIndex))
                return true;

            // FaceFrame experiment: the single designated agent is driven
            // regardless of team/range/hostility -- the test needs the lock
            // held on the player from ANY state (idle, teammate, enemy),
            // same "explicit designation wins" rule as the spar pair.
            if (CombatAiState.FaceFrameMode && Agent.Index == CombatAiState.FaceFrameAgentIndex)
                return true;

            // Arena-armed fights drive ONLY the spawned fighters (the practice
            // controller's _participantAgents roster, kept live by the mission
            // behavior). Without this every hostile NPC in range -- the arena
            // master, wandered-in guards -- gets the override and freezes
            // mid-pit holding a null flag word (seen on the Tournament Master).
            // Ahead of the GroupFightMode bypass so the arena-armed 5v5 doesn't
            // drive non-participant AI either (same freeze); harmless outside
            // the arena since ArenaFightActive is only set by a menu arm.
            if (CombatAiState.ArenaFightActive && !CombatAiState.ArenaRoster.Contains(Agent.Index))
                return false;

            // Group fight drives ALL AI humans on the field regardless of the
            // player-bubble range or hostility checks -- the whole point is a
            // 20v20 field test, not the 15m sandbox bubble.
            if (CombatAiState.GroupFightMode)
                return true;

            // Arena 2v1 participants are driven regardless of the test-range
            // bubble -- the pit is bigger than it and the pair must stay
            // engaged even when the player drags one of them to the ropes.
            if (CombatAiState.DuoAttackMode && ArenaDuoCoordinator.IsParticipant(Agent.Index))
                return true;

            if (!InTestRange())
                return false;

            Agent main = Agent.Mission?.MainAgent;
            if (Agent.IsEnemyOf(main))
                return true;
            if (Agent.ImmediateEnemy == main)
                return true;
            // "Who I'm fighting": IntendedTarget is the managed intent (the
            // native slot reads -1 in free-target mode by design); the native
            // read stays as the legacy fallback before any drive ran.
            Agent t = IntendedTarget ?? Agent.GetTargetAgent();
            return t != null && t == main;
        }

        // ---- MasterStrikes compatibility (Session 26, REVERSED in Session 27) -
        // Session 26's fix was "yield to MasterStrikes' own AIDodgeComponent
        // when present". The user instead wants the opposite: import ONLY the
        // dodge animation clips (act_1h/2h/other_2h_*_dodge, resolved via
        // ActionIndexCache -- module-level assets, loaded regardless of
        // whether MasterStrikes' own C# logic runs) and have CombatAI own all
        // dodge triggering outright. MasterStrikes' own AI dodge, Master
        // Strike (parry/riposte), and Clinch systems are now voided at the
        // source via MasterStrikesCompat.VoidMasterStrikesFeaturesOnce() (its
        // own MCM settings -- EnableDodges/EnableMasterStrikes/EnableClinch --
        // flipped off in memory for this session). With those voided,
        // MasterStrikes' AIDodgeComponent becomes a permanent no-op (its own
        // OnTick bails on EnableDodges) and channel 0 is exclusively ours, so
        // the old yield-to-MasterStrikes gates on the two TryStartDodge call
        // sites were removed (HasForeignDodgeComponent deleted).
        // This check remains as a pure belt-and-braces safety net in case the
        // settings override didn't apply (MCM/MasterStrikes not loaded the way
        // expected, version mismatch, etc.) -- if channel 0 is still mid-dodge
        // from something we didn't start, don't fight it with a forced flag.
        //
        // Session 31: ownership tracking via _dodgeOwnedByUs. The clip a dodge
        // plays can outlive _dodgeTime (the tick window is a fixed 0.45s while
        // clip length varies by weapon class); without the ownership flag the
        // tail of OUR OWN dodge read as "foreign" and made us yield every
        // dodge's recovery window -- plus inflated the yield counter.
        private bool IsForeignDodgePlaying()
        {
            ActionIndexCache act = Agent.GetCurrentAction(0);
            string name = act.GetName();
            bool isDodge = !string.IsNullOrEmpty(name) && name.Contains("dodge");
            if (!isDodge)
            {
                _dodgeOwnedByUs = false; // channel 0 clean again -- forget ownership
                return false;
            }
            return !_dodgeOwnedByUs;
        }

        private bool WieldsMeleeWeapon()
        {
            WeaponInfo w = Agent.GetWieldedWeaponInfo(Agent.HandIndex.MainHand);
            return w.IsValid && w.IsMeleeWeapon;
        }

        // The press/release state machine for ContinuousAttackMode:
        //   Holding   -> write AttackRight. When our own action reaches a Ready
        //                stage and a short hold has passed, clear the flag.
        //   Releasing -> write None ("let go of the button") until the swing has
        //                left all Attack* stages, plus a small rearm gap, then
        //                press again. If the attack never reaches Ready
        //                (stunned/no weapon/etc.) a failsafe retries anyway.
        private Agent.MovementControlFlag ComputeAttackLoopFlag(float dt)
        {
            _lastOp = "attackloop:enter";
            _loopTimer += dt;

            Agent.ActionStage stage = Agent.GetCurrentActionStage(1);
            bool inAttackStage = stage == Agent.ActionStage.AttackReady
                || stage == Agent.ActionStage.AttackQuickReady
                || stage == Agent.ActionStage.AttackRelease;

            switch (_loopState)
            {
                case AttackLoopState.Inactive:
                    _loopState = AttackLoopState.Holding;
                    _loopTimer = 0f;
                    return Agent.MovementControlFlag.AttackRight;

                case AttackLoopState.Holding:
                    if (inAttackStage && _loopTimer >= LoopReadyHoldSeconds)
                    {
                        _loopState = AttackLoopState.Releasing;
                        _loopTimer = 0f;
                        return Agent.MovementControlFlag.None;
                    }
                    if (!inAttackStage && _loopTimer >= LoopFailsafeSeconds)
                    {
                        _loopState = AttackLoopState.Releasing;
                        _loopTimer = 0f;
                        return Agent.MovementControlFlag.None;
                    }
                    return Agent.MovementControlFlag.AttackRight;

                case AttackLoopState.Releasing:
                    if ((!inAttackStage && _loopTimer >= LoopRearmSeconds)
                        || _loopTimer >= LoopFailsafeSeconds)
                    {
                        _loopState = AttackLoopState.Holding;
                        _loopTimer = 0f;
                        return Agent.MovementControlFlag.AttackRight;
                    }
                    return Agent.MovementControlFlag.None;
            }

            return Agent.MovementControlFlag.None;
        }

        private Agent.MovementControlFlag ComputeReactiveDefendFlag()
        {
            _lastOp = "reactiveDefend:enter";
            Agent target = Agent.Mission?.MainAgent;
            if (!CombatLive(target))
                return Agent.MovementControlFlag.None;

            // Only react if the player is close enough to actually reach us AND
            // roughly facing us. Without this, every enemy near the fight flinches
            // at every swing (the log showed Tournament Master + cavalry + spearman
            // all "reacting" to swings aimed at someone else) and block outcomes
            // become meaningless.
            Vec3 toUs = Agent.Position - target.Position;
            Vec3 toUsFlat = new Vec3(toUs.x, toUs.y, 0f);
            float dist = toUsFlat.Normalize();
            if (dist > BlockRangeMeters)
                return Agent.MovementControlFlag.None;

            Vec3 lookFlat = target.LookDirection;
            lookFlat.z = 0f;
            lookFlat.Normalize();
            if (Vec3.DotProduct(toUsFlat, lookFlat) < MinFacingDot)
                return Agent.MovementControlFlag.None;

            Agent.ActionStage stage = target.GetCurrentActionStage(1);
            if (stage != Agent.ActionStage.AttackRelease)
                return Agent.MovementControlFlag.None;

            // React the instant AttackRelease begins (not partway through it). At close
            // range/certain angles, the hit can register very early into the release,
            // so waiting for e.g. 55% progress risks missing the actual collision --
            // this is NOT "hold block early" (we still never react during
            // AttackReady/AttackQuickReady, before the player has committed), it's
            // "react as soon as they've committed, not partway into the swing."
            float progress = target.GetCurrentActionProgress(1);

            // Use the direction COMMITTED to the action playing on channel 1, NOT
            // GetAttackDirection() -- the latter tracks the player's LIVE aim
            // (cursor/stick) and can change after the swing is already released.
            // Observed in-game: attack from the left + aim flicked right mid-swing
            // made us force the block on the right while the real hit still came
            // from the left. Once AttackRelease has begun, the action's own
            // direction is locked -- it's the ground truth for where the hit lands.
            Agent.UsageDirection incoming = target.GetCurrentActionDirection(1);
            Agent.MovementControlFlag defend = MirrorDefend(incoming);
            if (defend == Agent.MovementControlFlag.None)
                return Agent.MovementControlFlag.None;

            if (!_watchingBlockOutcome)
            {
                _watchingBlockOutcome = true;
                _blockOutcomeTimer = 0f;
                _pendingIncomingDirection = incoming;

                if (_detailedBlockLogCount < MaxDetailedBlockLogs)
                {
                    _detailedBlockLogCount++;
                    CombatAiLog.Log(
                        $"[PERFECTBLOCK] Agent #{Agent.Index} ({Agent.Name}): reacting to incoming {incoming} " +
                        $"on release (progress={progress:F2}) -> forcing {defend}.");
                }
            }

            return defend;
        }

        [HandleProcessCorruptedStateExceptions]
        public override void OnTick(float dt)
        {
            try
            {
                OnTickCore(dt);
            }
            catch (AccessViolationException ex)
            {
                LogAv("OnTick", ex);
            }
        }

        private void OnTickCore(float dt)
        {
            _lastOp = "OnTick";
            base.OnTick(dt);

            if (Phase != InjectPhase.Idle)
            {
                _phaseTimer += dt;

                if (Phase == InjectPhase.ForceAttackRight)
                {
                    Agent.ActionStage stage = Agent.GetCurrentActionStage(1);
                    bool inAttackStage = stage == Agent.ActionStage.AttackReady
                        || stage == Agent.ActionStage.AttackQuickReady
                        || stage == Agent.ActionStage.AttackRelease;

                    if (inAttackStage)
                        _attackStageObserved = true;

                    bool attackFinished = _attackStageObserved && !inAttackStage;
                    bool timedOut = _phaseTimer > MaxAttackPhaseDuration;

                    if (attackFinished || timedOut)
                    {
                        CombatAiLog.Log($"[INJECT] Agent #{Agent.Index} ({Agent.Name}): attack phase done (finished={attackFinished}, timedOut={timedOut}) -> ForceDefendRight.");
                        Phase = InjectPhase.ForceDefendRight;
                        _phaseTimer = 0f;
                    }
                }
                else if (Phase == InjectPhase.ForceDefendRight)
                {
                    if (_phaseTimer > DefendHoldDuration)
                    {
                        CombatAiLog.Log($"[INJECT] Agent #{Agent.Index} ({Agent.Name}): sequence complete -> Idle.");
                        Phase = InjectPhase.Idle;
                        _phaseTimer = 0f;
                    }
                }
            }

            // PerfectBlockMode outcome check: did the forced late-block actually
            // resolve as a real parry (ParriedMelee) or just a plain block
            // (BlockedMelee)? Runs independently of Phase.
            if (_watchingBlockOutcome)
            {
                _blockOutcomeTimer += dt;
                Agent.ActionCodeType actionType = Agent.GetCurrentActionType(1);
                Agent.ActionStage ownStageNow = Agent.GetCurrentActionStage(1);

                // Parry evidence shows up either as the ParriedMelee/BlockedMelee
                // action type OR as stage=DefendParry on the directional defend
                // action (observed: a real parry logged DefendRightStaff +
                // DefendParry + a 0-damage ShrugOff blow in OnHit).
                bool resolved = actionType == Agent.ActionCodeType.ParriedMelee
                    || actionType == Agent.ActionCodeType.BlockedMelee
                    || ownStageNow == Agent.ActionStage.DefendParry;

                if (resolved)
                {
                    if (_detailedBlockLogCount < MaxDetailedBlockLogs)
                    {
                        _detailedBlockLogCount++;
                        CombatAiLog.Log(
                            $"[PERFECTBLOCK] Agent #{Agent.Index} ({Agent.Name}): outcome for incoming " +
                            $"{_pendingIncomingDirection} = actionType:{actionType} stage:{ownStageNow} " +
                            $"({(actionType == Agent.ActionCodeType.ParriedMelee || ownStageNow == Agent.ActionStage.DefendParry ? "REAL PARRY" : "plain block, not a parry")}).");
                    }
                    _watchingBlockOutcome = false;
                }
                else if (_blockOutcomeTimer > BlockOutcomeWatchDuration)
                {
                    if (_detailedBlockLogCount < MaxDetailedBlockLogs)
                    {
                        _detailedBlockLogCount++;
                        CombatAiLog.Log(
                            $"[PERFECTBLOCK] Agent #{Agent.Index} ({Agent.Name}): outcome watch timed out " +
                            $"(no Parried/Blocked action type observed; ownStage now={Agent.GetCurrentActionStage(1)}).");
                    }
                    _watchingBlockOutcome = false;
                }
            }

            // Diagnostic heartbeat -- runs regardless of whether anything triggered.
            // Gated on distance to the player: without this the shared log cap was
            // eaten by ~200 town NPCs logging TotalTicks=0 while the actual fighters
            // never got a heartbeat slot. Spar-designated agents skip the range
            // gate (they may fight far from the player and are exactly who we
            // need diagnostics for when a teammate "does nothing").
            bool sparMember = CombatAiState.SparMode
                && (Agent.Index == CombatAiState.SparDefenderIndex
                    || Agent.Index == CombatAiState.SparAttackerIndex);
            if ((CombatAiState.ContinuousAttackMode || CombatAiState.PerfectBlockMode || CombatAiState.DuelMode || CombatAiState.GroupFightMode || sparMember)
                && (InTestRange() || sparMember || CombatAiState.GroupFightMode) && _heartbeatLogCount < MaxHeartbeatLogs)
            {
                _heartbeatTimer += dt;
                if (_heartbeatTimer >= HeartbeatInterval)
                {
                    _heartbeatTimer = 0f;
                    _heartbeatLogCount++;

                    Agent.ActionStage ownStage = Agent.GetCurrentActionStage(1);
                    Agent.ActionCodeType ownType = Agent.GetCurrentActionType(1);
                    Agent.MovementControlFlag ownAppliedFlags = Agent.MovementFlags;

                    if (sparMember)
                    {
                        Agent cur = Agent.GetTargetAgent();
                        CombatAiLog.Log(
                            $"[HEARTBEAT-SPAR] Agent #{Agent.Index} ({Agent.Name}) " +
                            $"role={(Agent.Index == CombatAiState.SparDefenderIndex ? "DEFENDER" : "ATTACKER")} " +
                            $"eligible={IsEligibleForTestModes()} driving={_drivingMovementFlags} " +
                            $"desired={_desiredMovementFlag} applied={ownAppliedFlags} " +
                            $"ownStage={ownStage} ownActionType={ownType} " +
                            $"target={(Live(cur) ? "#" + cur.Index : "null")} " +
                            $"frameSet={_targetFrameSet} autoTargetOff={_autoTargetDisabled} " +
                            $"duelState={_duelState} aiControlled={Agent.IsAIControlled} TotalTicks={TotalTicks}");
                    }

                    if (CombatAiState.ContinuousAttackMode)
                    {
                        CombatAiLog.Log(
                            $"[HEARTBEAT-ATTACKLOOP] Agent #{Agent.Index} ({Agent.Name}): loopState={_loopState} " +
                            $"desired={_desiredMovementFlag} ownStage={ownStage} " +
                            $"ownActionType={ownType} ownAppliedMovementFlags={ownAppliedFlags} TotalTicks={TotalTicks}");
                    }

                    if (CombatAiState.DuelMode && _drivingMovementFlags)   // Session 59: driven participants only (was every eligible agent in range)
                    {
                        Agent target = Agent.Mission?.MainAgent;
                        Agent curLock = Agent.GetTargetAgent();
                        CombatAiLog.Log(
                            $"[HEARTBEAT-DUEL] Agent #{Agent.Index} ({Agent.Name}): duelState={_duelState} " +
                            $"attackDir={_attackDir} defendAllowed={_defendAllowed} freeStance={_freeStance} " +
                            $"lockedOn={(Live(curLock) ? "#" + curLock.Index : "null")} " +
                            $"targetStage={(CombatLive(target) ? target.GetCurrentActionStage(1).ToString() : "n/a")} " +
                            $"ownStage={ownStage} ownActionType={ownType} ownAppliedMovementFlags={ownAppliedFlags} " +
                            $"posture={PlayerPosture.Value:F0} TotalTicks={TotalTicks}");
                    }

                    if (CombatAiState.GroupFightMode)
                    {
                        Agent cur = Agent.GetTargetAgent();
                        string asgDesc = EngagementMap.TryGetAssignment(Agent.Index, out EngagementMap.Assignment ga)
                            ? $"victim=#{ga.VictimIndex} role={(ga.Role == EngagementMap.RolePrimary ? "primary" : ga.Role == EngagementMap.RoleSupport ? "support" : "flanker")}"
                            : "unassigned";
                        CombatAiLog.Log(
                            $"[HEARTBEAT-GROUP] Agent #{Agent.Index} ({Agent.Name}): {asgDesc} " +
                            $"attackersOnMe={EngagementMap.AttackersOn(Agent.Index)} playerThreatens={EngagementMap.PlayerThreatens(Agent.Index)} " +
                            $"lockedOn={(Live(cur) ? "#" + cur.Index : "null")} duelState={_duelState} " +
                            $"ownStage={ownStage} appliedFlags={ownAppliedFlags} TotalTicks={TotalTicks}");
                    }

                    if (CombatAiState.PerfectBlockMode)
                    {
                        Agent target = Agent.Mission?.MainAgent;
                        if (CombatLive(target))
                        {
                            Agent.ActionStage targetStage = target.GetCurrentActionStage(1);
                            Agent.UsageDirection targetAimDir = target.GetAttackDirection();
                            Agent.UsageDirection targetActionDir = target.GetCurrentActionDirection(1);
                            float targetProgress = target.GetCurrentActionProgress(1);
                            CombatAiLog.Log(
                                $"[HEARTBEAT-PERFECTBLOCK] Agent #{Agent.Index} ({Agent.Name}): " +
                                $"targetStage={targetStage} targetAimDir={targetAimDir} targetActionDir={targetActionDir} targetProgress={targetProgress:F2} " +
                                $"ownStage={ownStage} ownActionType={ownType} ownAppliedMovementFlags={ownAppliedFlags} TotalTicks={TotalTicks}");
                        }
                        else
                        {
                            CombatAiLog.Log($"[HEARTBEAT-PERFECTBLOCK] Agent #{Agent.Index} ({Agent.Name}): target (MainAgent) is null or inactive.");
                        }
                    }
                }
            }
        }

        // Ground truth: fires when a blow lands on THIS agent. If we "blocked" a
        // player swing but this logs real damage, the block failed. If it fires
        // with ~0 damage or doesn't fire while our action type shows ParriedMelee/
        // BlockedMelee, the deflection worked.
        [HandleProcessCorruptedStateExceptions]
        public override void OnHit(Agent affectorAgent, int damage, in MissionWeapon affectorWeapon, in Blow b, in AttackCollisionData collisionData)
        {
            try
            {
                if (affectorAgent != null && affectorAgent.IsMainAgent)
                    BrainOnHit(damage);
                if (affectorAgent != null && affectorAgent.IsMainAgent
                    && _detailedBlockLogCount < MaxDetailedBlockLogs)
                {
                    _detailedBlockLogCount++;
                    CombatAiLog.Log(
                        $"[HIT-US] Agent #{Agent.Index} ({Agent.Name}) took {damage} dmg from the player: " +
                        $"ourActionType={Agent.GetCurrentActionType(1)} ourStage={Agent.GetCurrentActionStage(1)} " +
                        $"blowFlags={b.BlowFlag} strikeType={b.StrikeType} defenderStun={b.DefenderStunPeriod:F2}s " +
                        $"shieldBlock={collisionData.AttackBlockedWithShield} correctSide={collisionData.CorrectSideShieldBlock}.");
                }
            }
            catch (AccessViolationException ex)
            {
                LogAv("OnHit", ex);
            }
        }
    }
}
