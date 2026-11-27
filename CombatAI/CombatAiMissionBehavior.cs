using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // Global on/off switch so the override can be tested without restarting the mission.
    // When disabled, the attached components still count what native wanted to do, but
    // pass it through unchanged (useful to A/B compare against vanilla behavior).
    //
    // Two additional console-controlled test modes (see CombatAiOverrideComponent):
    //   ContinuousAttackMode - every tracked agent keeps forcing AttackRight, over and
    //     over, never stopping. Tests whether forced attacks actually connect/damage
    //     the player, not just play an animation.
    //   PerfectBlockMode - every tracked agent watches the MAIN AGENT (player)
    //     specifically and, the instant the player's AttackRelease begins (never held
    //     early during AttackReady/AttackQuickReady, before they've committed), forces
    //     the mirrored Defend direction. Tests whether a purely-reactive block resolves
    //     as a real parry (ActionCodeType.ParriedMelee) instead of a plain block
    //     (BlockedMelee).
    public static class CombatAiState
    {
        public static bool OverrideEnabled = true;
        public static bool ContinuousAttackMode = false;
        public static bool PerfectBlockMode = false;

        // combatai.godmode -- every mission tick, heals the player and every
        // tracked agent back to full so test sessions aren't cut short by deaths.
        public static bool GodMode = false;

        // combatai.duel -- the real v1 combat brain: attack variety (quick/normal/
        // held/feint), punish early player blocks, chance-based reactive defense.
        public static bool DuelMode = false;

        // combatai.spar -- controlled two-AI experiment: one attacker (uses the
        // duel attack FSM) vs one defender (100% block, target locked on the
        // attacker, never attacks). The player can come in from the side to watch
        // how the defender turns/blocks.
        public static bool SparMode = false;
        public static int SparAttackerIndex = -1;
        public static int SparDefenderIndex = -1;

        // combatai.looktest <variant> -- 1v1 facing/look control experiment.
        // ALL variants run with the combat target RELEASED (auto-select off +
        // SetTargetAgent(null)) -- every earlier look test failed because a
        // locked target pins the whole facing system. Variants:
        //   frame   = SetTargetPositionAndDirection(ownPos, rotated facing)
        //   angle   = LookDirectionAsAngle base +/- 15deg
        //   poi     = SetLookToPointOfInterest(side point)
        //   movedir = SetMovementDirection(rotated)
        //   target  = SetTargetAgent(player) during holds, null on release
        //             (the duel/spar lever -- combat target -> torso aim)
        //   torso   = legs pinned by frame + torso aimed by LookDirectionAsAngle
        //             (two channels composed -> pure torso feint test)
        public static bool LookTestMode = false;
        public static int LookTestVariant = 0; // 0=frame,1=angle,2=poi,3=movedir,4=target,5=torso

        // combatai.group -- GROUP FIGHT (whole-field test for custom battles,
        // meant for infantry-vs-infantry setups like 20v20): EngagementMap
        // pairs every AI with an untargeted enemy first (mutual 1v1s -- A
        // targets B => B targets A), free agents on the outnumbering side
        // stack onto the least-covered victim (2nd = support, 3rd+ = flanker
        // steering for the victim's back). Defenders facing 2+ attackers get
        // the grace rules: free-space movement, rotating focus, half feints.
        // Agents actually engaging the player get the T1 brain
        // (PlayerCombatModel predictability + reaction); pure AI-vs-AI runs
        // the cheaper T2 path inside the same FSM.
        public static bool GroupFightMode = false;

        // Session 67: REAL formation orders drive the group-fight line
        // advance (GroupFormationAdvance): participants march inside their
        // team's Infantry formation under a Move order until the lines meet,
        // then a GLOBAL release detaches them into the combat brain. OFF =
        // the old per-agent flag-push advance (GroupAdvanceFlag) -- also the
        // automatic fallback if the formation APIs throw at runtime.
        public static bool FormationMarch = true;

        // combatai.dodge -- step dodge: some share of committed releases get
        // answered with a hard side/back STEP instead of a block (no custom
        // animation -- teleport displacement + gait flag). Outnumbered
        // defenders dodge more to make space.
        public static bool DodgeEnabled = true;

        // combatai.evade -- EVADE-ONLY test mode (100% dodge): every eligible
        // agent answers EVERY committed release with the step dodge -- never
        // blocks, never attacks. The pure-dodge counterpart of PerfectBlockMode.
        public static bool EvadeMode = false;

        // combatai.angledodge -- ANGLE-DODGE test mode: when the player holds a
        // Left/Right swing from only a slight camera angle (the existing dodge
        // rule would block), the AI strafes until the rule's condition is true
        // and then does the normal dodge at the release. Dodge logic untouched.
        public static bool AngleDodgeMode = false;

        // combatai.defense -- DEFENSE-ONLY test mode: the AI approaches,
        // faces, and defends (block/dodge/freeze) using the full expectation
        // rating, but NEVER attacks. This isolates the defensive brain so
        // feint overcommits, panic freezes and read-scaled reaction are visible
        // without the AI trading back and masking the behavior.
        public static bool DefenseOnly = false;

        // ArenaOverhaul integration (arena menu options, CombatAiArenaBehavior):
        // the consequence stores which fight to arm; the mission behavior
        // consumes it once _participantAgents populates. DuoAttackMode is the
        // live flag while the 2v1-defend fight runs.
        public static CombatAiArenaKind PendingArenaFight = CombatAiArenaKind.None;
        public static bool DuoAttackMode = false;
        public static bool ArenaFightActive = false;   // an arena-menu fight is armed: driven fighters get their weapon drawn
        // Live copy of the practice controller's _participantAgents indices,
        // refreshed ~2x/sec while ArenaFightActive (CombatAiMissionBehavior.
        // TickArenaRoster). The override component drives ONLY these agents in
        // an arena fight -- everyone else (arena master, guards, townsmen)
        // keeps native control.
        public static readonly HashSet<int> ArenaRoster = new HashSet<int>();

        // Session 4p custom-battle targeting replica: a managed copy of
        // native compute_basic_score that runs BESIDE native targeting in
        // custom battles, logging [CBTGT] lines. Log-only: never calls
        // SetTargetAgent / SetAutomaticTargetSelection -- native keeps full
        // control. CbTargetUserDisabled makes `combatai.cbtarget off` stick
        // across missions (the auto-arm in custom battles honours it).
        public static bool CbTargetMode = false;
        public static bool CbTargetUserDisabled = false;

        // Session 4q GROUP-FIGHT native-first targeting: let NATIVE pick the
        // fight target (we never SetTargetAgent it), approach it, and use the
        // spar free-look to keep close threats in view (bending movement, not
        // just facing). Fallback to the anchor when native has no pick (arena
        // has no order layer). Persistent preference (like SparRig), default
        // ON per user: `combatai.gtnative off` turns it off for later fights.
        public static bool GroupNativeTarget = true;

        // Session 4w gtnative group feature toggles (`combatai.gf <name>
        // [on|off]`): every feature OFF falls back to the a069e7f behaviour
        // for exactly that feature.
        public static bool GfWeights = true;    // baseline re-pick: front/close/unique + 2-attacker cap
        public static bool GfDefender = true;   // 2+ attackers on me -> layer on (attack less, defend)
        public static bool GfQuick = true;      // clean-1v1 one-swing side switches (layer excursion)
        public static bool GfNativemove = true; // group legs belong to native (off = a069e7f march)
        public static bool GfUpdown = true;     // crowded -> up/down only; free look through side swings
        public static bool GfNofeint = true;    // no feints in the gtnative group fight
        public static bool GfGap = true;        // post-swing think gap + in-reach/in-front attack starts
        public static bool GfOnMe = true;        // prefer attackers-on-me; penalise greedy 2v1 joins
        public static bool GfSpice = true;       // aggressive group tuning profile (armed at fight start)
        public static bool GfDefmove = true;     // defender 2v1 step-out correction via the translator
        public static bool GfSwap = true;        // coordinated target swaps + cover handoffs
        public static bool GfRig2v1 = true;      // rigged 2v1 control (defender keeps attackers in view)
        public static bool GfAssist = true;      // closing assist when native stalls outside our reach

        // GroupFight5v5 menu entry (`combatai.groupsize <2..20>`). 5 == the
        // stock 5v5; AO reads the team-practice counts we push at arm time,
        // so size flows through the same spawn path.
        public static int GroupFightTeamSize = 5;

        // Session 47 defense brain (combatai.brain): independent switches for
        // the three decision systems and the initiative/dodge-out layer.
        // Switching S1/S3 off falls back to the legacy behaviour for that part.
        private static bool _brainS1 = true;
        public static bool BrainS1 { get { return _brainS1 && CombatAiTune.S1On; } set { _brainS1 = value; } }          // hold-reaction (early guard / counter)
        private static bool _brainS2 = true;
        public static bool BrainS2 { get { return _brainS2 && CombatAiTune.S2On; } set { _brainS2 = value; } }          // feint-read
        private static bool _brainS3 = true;
        public static bool BrainS3 { get { return _brainS3 && CombatAiTune.S3On; } set { _brainS3 = value; } }          // release-block
        private static bool _brainInit = true;
        public static bool BrainInitiative { get { return _brainInit && CombatAiTune.InitOn; } set { _brainInit = value; } }  // counters, take-the-ready, same-line dodge-out

        // Session 31 diagnostics -- "dodge doesn't look like 90%" report:
        // these let combatai.status answer "is MasterStrikes actually
        // voided?" and "are we even reaching the dodge roll as often as
        // expected?" from hard counters instead of guessing. Reset by
        // combatai.clearlog (CombatAiOverrideComponent.ResetDiagnosticLogCounters).
        public static bool MasterStrikesDetected;        // the MasterStrikes assembly/type was found at all
        public static bool MasterStrikesSettingsVoided;  // EnableMasterStrikes/EnableClinch flip succeeded
        public static bool MasterStrikesHarmonyApplied;  // AIDodgeComponent.OnTick patch bound successfully
        public static int MasterStrikesPatchesApplied;   // AI-side patches bound (dodge + clinch OnTick + OnAgentHit master-strike gate; target 3)
        public static int DodgeReleaseSeenCount;         // committed enemy release in weapon reach (dodgable event)
        public static int DodgeOpportunityCount;         // release-edge gate passed, roll about to happen
        public static int OurDodgeStartCount;             // TryStartDodge actually returned true
        public static int ForeignDodgeYieldCount;          // we bailed because channel 0 was mid-dodge from something else
        public static int DodgeSuppressedMidSwing;         // seen release skipped because WE were mid-AttackRelease (can't step and swing)

        // combatai.speedlog -- Session 35g ground-truth diagnostic: logs the
        // REAL measured values (Agent.GetCurrentVelocity() length, WalkMode,
        // speed-limit cur/max, handoff state, desired flag) every ~0.4s per
        // driven duel agent. Settles the run/walk inconsistency with actual
        // numbers instead of another guess.
        public static bool SpeedLogMode = false;
        // Session 59: [SPEEDLOG] ran for EVERY tracked agent (~450 lines/s of
        // file-append under a global lock -> the game went choppy). Now only
        // driven fight participants log, under a per-mission global budget.
        public static int SpeedLogLines;
        public const int MaxSpeedLogLines = 4000;

        // Session 48 A/B toggles (combatai.movefix): GaitFix writes Run through
        // the OnAIInputSet callback's eventFlag channel while we drive a walking
        // agent (the per-frame EventControlFlags|=Run gets stomped by the
        // callback's None). NativeLocoWindows hands locomotion+gait back to
        // native during guard/ready windows in plain duel (we keep combat bits).
        public static bool GaitFix = true;
        public static bool NativeLocoWindows = true;

        // Session 52: port the duel defence brain (S1/S2/S3 + FightScript) to
        // the spar DEFENDER (2v1) -- toggle for in-game A/B (combatai.sparbrain).
        public static bool SparBrain = true;

        // Session 62: the rig spar-defender path (no pin/frame, WASD-channel
        // legs, primary-facing lock, shield/side-aware block flags). Keeps
        // out of DisableAllModes -- a persistent preference like SparBrain.
        public static bool SparRig = true;

        // combatai.freetarget -- FREE TARGET (Session 68 design): a driven
        // duel agent / spar defender NEVER holds a combat target
        // (aiComp+0x1504 = -1); body/position are owned by the ONE
        // SetFormationFrameEnabled channel (verified ai21: with target=-1
        // the frame owns facing). Block decisions branch on the threat's
        // bearing vs ACTUAL facing instead of on the lock. Scope this pass:
        // default DUEL (incl. defense-only) + SPAR defender only -- group,
        // duo, looktest, perfectblock, faceframe etc. always legacy-locked.
        // FAIL CLOSED: active only while NativeTargetGuard.ArmedLevel >= 2
        // (auto-arms via EnsureArmed when a mode starts); if arming fails the
        // agent runs the legacy locked behavior unchanged. `off` restores
        // byte-for-byte legacy behavior; `attack` keeps the lock only while
        // we hold an attack (Windup..Recover) and releases after recovery.
        // Persistent preference (like SparRig), not reset by DisableAllModes.
        public enum FreeTargetModeT { Off, On, AttackOnly }
        public static FreeTargetModeT FreeTarget = FreeTargetModeT.AttackOnly;

        // combatai.freetarget face -- WHO writes the free-mode commanded
        // heading (Session 69, native-verified writers):
        //   Frame    = formation frame (ai21-verified ownership, but legs
        //              follow at a native rate cap -- ai24: 68deg mean err)
        //   LookDir  = Agent.LookDirection (set_look_direction: +0x474 dir,
        //              derived +0x484 angle -- never the +0x4a4 input slot,
        //              so MovementInputVector/flag locomotion is unaffected)
        //   LookAngle= Agent.LookDirectionAsAngle (+0x484 = atan2f(-x,y))
        // With a look writer the frame does NOT own the legs: the proven
        // legacy locomotion paths (flag moves, duelblockmove input, spar-rig
        // MovementInputVector) run instead. Persistent preference, NOT reset
        // by DisableAllModes.
        //   MoveDir  = Agent.SetMovementDirection (FUN_1806f3500: writes the
        //              agent+0x474 channel {dir,+0x484 angle,+0x4a4 slot},
        //              dynBody+0x34 heading AND the aiComp+0x15d8.. mirror --
        //              with target=-1 nothing recomputes it, so this may own
        //              the BODY heading; clobbers +0x4a4 -> call before the
        //              MovementInputVector re-assert). LookAngle verified
        //              dead on the live build (round-trip ignored, ai25).
        public enum FreeFaceT { Frame, LookDir, LookAngle, MoveDir }
        public static FreeFaceT FreeFace = FreeFaceT.MoveDir;   // ai26-proven instant writer
        // combatai.freetarget turnrate <deg/s> -- slew cap on the commanded
        // free-mode heading (all three writers). 30..720, default 240
        // (replaces the hard-coded 60deg/s -- ai24 measured 68deg mean
        // facing error while the player circles).
        public static float FreeTurnRateDegPerSec = 240f;
        // combatai.freetarget smooth <k> -- heading update is now
        // step = dA*(1-exp(-dt*K)) clamped to turnrate (ai30: a plain 360deg/s
        // rate cap written into dynBody heading reads as a hard snap). K is
        // the exponential smoothing rate: higher = stiffer. Clamp 2..40,
        // default 9.
        public static float FreeSmoothK = 9f;
        // combatai.freetarget accel <deg/s2> -- the heading driver is second-
        // order now (velocity state + acceleration limit): onset ramps
        // instead of jumping straight to the rate cap (ai31). Clamp 100..3000.
        public static float FreeTurnAccelDegPerSec2 = 500f;
        // combatai.freetarget leash <m> -- with a look-based face writer
        // (MoveDir/LookDir) the free driver keeps the body at ANY distance
        // out to this leash instead of handing off to native (ai27: 26
        // hand-off cycles, each taking 3-6s for native to close ~0.5m).
        // Beyond it native pathfinding still owns the body (lock kept).
        // Clamp 3..30, default 6.0. Persistent preference.
        public static float FreeLeashMeters = 6.0f;
        // combatai.freetarget bodyfeint -- BODY FEINT (Session 70): during
        // the duel feint states (FeintCancel/FeintReady) the combat lock is
        // released and the free driver twists the BODY with
        // SetMovementDirection (proven instant at target=-1, ai26), relocked
        // the same tick the FSM leaves FeintReady -> Release. Only with
        // FreeTarget=AttackOnly + FreeFace=MoveDir. Persistent, default OFF.
        public static bool FreeBodyFeint = false;
        // combatai.sparfree -- spar DEFENDER free-target path (median heading
        // between the two attackers, rig locomotion kept, never a lock).
        // Default ON; off = the pre-free legacy locked defender.
        public static bool SparFree = true;

        // combatai.steer -- NATIVE STEER (Option A), the NEW duel default:
        // plain-duel agents keep the combat lock ON at all times while the
        // NativeSteerOverride detour on the native heading-command funnel
        // (rva 0x61ac60 on the installed 1.4.8 dll, FUN_180617d10 analog)
        // replaces the steering-computed desired dir with ours at the channel
        // input -- the engine's own rate-limited integrator still turns the
        // body, the lock keeps attack aim/relock internals, no release/relock
        // thrash, no -1 crash surface. Scope: plain duel + the SPAR DEFENDER
        // under SparFree (bisector heading between the two attackers, side-
        // matched swings; group/duo/spar-attacker untouched). FAIL CLOSED:
        // needs the hook armed (auto-arms on first
        // steer tick); if arming fails the agent falls back to the current
        // freetarget/legacy behavior unchanged. Persistent preference (like
        // FreeTarget), not reset by DisableAllModes.
        public static bool NativeSteer = true;

        // combatai.groupfree -- GROUP LINEUP (stage 1 of the group rebuild):
        // two AI lines form up facing each other, advance to ~gap, wait,
        // fight. Persistent, default ON; off = the legacy formation-advance
        // group fight byte-identical.
        public static bool GroupFree = true;
        public static float GroupGapMeters = 1.6f;      // line-to-line gap
        public static float GroupSpacingMeters = 0.9f;  // in-line spacing (shoulder to shoulder)
        public static float GroupFightGapMeters = 1.2f; // line gap once the fight starts (lines step in)
        public static float GroupLeashMeters = 0.9f;    // max drift from the line slot before returning
        public static float GroupWaitSeconds = 10f;     // standoff before the fight
        public static float GroupSpeedMeters = 1.6f;    // lockstep advance speed
        // ---- stage-2 free-target fight knobs (persistent) ----
        public static float GroupThreatRangeMeters = 3.2f;  // threat-candidate range (clamp 1.5..8)
        public static int GroupMaxThreats = 3;              // threats kept per agent (1..4)
        public static int GroupMaxEngagers = 3;             // max live engagement claims per victim (1..4)
        public static float GroupBusyIgnoreSeconds = 2.5f;  // an enemy engaging someone else is a non-threat this long (0..8)
        public static float GroupAttackViewDeg = 55f;       // attacker must stay in victim's +/- view (30..90)
        public static float GroupAttackAimDeg = 45f;        // attack target must be within this of facing (10..90); 45 so a bisector-facing agent can still swing at a +/-35 deg threat
        // Free-target block classification: threat bearing vs ACTUAL facing
        // below this is "frontal" (duel-style roll), above is side (spar
        // side roll). Default 15 -- a defender looking BETWEEN two attackers
        // treats both as non-target. Clamp 5..45.
        public static float FreeFrontalDeg = 15f;
        // Spar attacker view cone: it may only attack while within +/- this
        // of the free-driven defender's facing. Clamp 30..90, default 55.
        public static float SparAttackViewDeg = 55f;
        // Sparfree slot anchor: attacker slot sits on the PLAYER bearing as
        // seen from the defender, rotated by side*this deg (default 70,
        // clamp 40..110) -- anchors on the player, not on the defender's
        // median facing (which depends on the attacker's own position and
        // would feed back/spin).
        public static float SparSpreadDeg = 70f;
        // Sparfree defender attack gate: may only START a swing while the
        // target sits within +/- this deg of its current facing, so the lock
        // acquisition (attack-window lock) never snaps the body. Clamp 10..90.
        public static float SparDefAttackViewDeg = 30f;

        // combatai.lookonly -- LOOK-ONLY duel diagnostic (Session 71): the AI
        // approaches to combat range then keeps repositioning (orbit/strafe/
        // back/close/pause intents, 2-4s each) WITHOUT attacking while the
        // free-target MoveDir writer keeps the player in its 90deg view.
        // Reuses the duel path + DefenseOnly's no-attack mask; target lock is
        // never written (LockWantedNow stays false in Approach). Mode state
        // (DuelMode/DefenseOnly/FreeTarget/FreeFace) is saved at enable and
        // restored at off. Reset by DisableAllModes.
        // combatai.holdspin / combatai.lockthrash -- shutter-repro tests:
        // holdspin = hold AttackUp while the MoveDir body writer rotates the
        // agent on its axis with target=-1 (does the write itself shutter a
        // held weapon?); lockthrash = hold AttackUp while the combat lock is
        // written/released on a timed cycle (the ai36 thrash signature).
        public static bool HoldSpinMode = false;
        public static bool HoldSpinNoLock = false;   // holdspin nolock: target=-1 variant
        public static bool LockThrashMode = false;
        public static float HoldSpinRateDeg = 70f;
        public static float ThrashOnSeconds = 0.15f;
        public static float ThrashOffSeconds = 0.06f;

        public enum LookOnlyStyleT { Orbit, Strafe, Mixed }
        public static bool LookOnlyMode = false;
        public static LookOnlyStyleT LookOnlyStyle = LookOnlyStyleT.Mixed;
        public static float LookOnlyInView90, LookOnlyMeanErr, LookOnlyMaxErr;  // last-off summary

        // Session 62 Part B: 'Spar (all shields)' menu kind -- every arena AI
        // spawns with 1H weapon + shield (ArenaShieldPatch postfix). Armed in
        // BeginPractice, cleared where the arena state is torn down.
        public static bool ArenaForceShields;

        // Session 74: 'Cavalry Duel (mounted 1v1)' menu kind -- every practice
        // fighter (player AND AI) spawns with horse + harness + couch lance
        // (ArenaCavalryPatch). Armed in BeginPractice, cleared where the arena
        // state is torn down (same places as ArenaForceShields).
        public static bool ArenaForceMounts;
        // CavDuelMode = the live brain flag (TryArmPendingArenaFight /
        // combatai.cav): mounted roster fighters run the cav planner
        // (ComputeCavFlag) on top of the shared duel FSM.
        // CavSteer = horse-steering channel A/B (combatai.cavsteer):
        //   Vec  = MovementInputVector strafe+forward (legacy)
        //   Turn = TurnLeft/TurnRight MovementControlFlags + forward input
        //   Both = turn flags + x-lean + throttle -- the faithful player
        //          input mapping (default; research/16 verified channel)
        //   Off  = no cav locomotion writes (baseline)
        public enum CavSteerT { Off, Vec, Turn, Both }
        public static bool CavDuelMode = false;
        public static CavSteerT CavSteer = CavSteerT.Both;
        // CavHold = early attack-ready hold (combatai.cavhold, default ON):
        // a charging rider starts the windup inside a closing-scaled window
        // and HOLDS the attack flag through the approach -- clearing it at
        // the strike gate releases instantly instead of swinging late.
        public static bool CavHold = true;

        // Session 62 Part B: spar defender gets the duel initiative/tempo
        // gating vs the player (tempo breather, quiet, cancel-gap, hold
        // grace, post-block kind) instead of always attacking every tick.
        public static bool SparDuelInitiative = true;

        // Session 56 EXPERIMENT (combatai.torsomove): spar-defender block
        // window without frozen legs. Pin = today's behaviour (scripted frame
        // + lock). MoveDir = constant SetMovementDirection heading + lock.
        // Formation = native formation frame (SetFormationFrameEnabled, factor
        // 1) + lock. TorsoMoveNativeLoco adds the Session-49 native locomotion
        // window during blocks. TorsoMoveLog forces [TORSOMOVE] diagnostics.
        public enum TorsoMoveModeT { Pin, MoveDir, Formation, Free, Tactical }
        // Session 57: how Free mode moves the legs during a block window.
        public enum TorsoBlockMoveT { Formation, Loco, Input, Off }
        // Session 57 duel opt-in: block-window leg lever in plain duel.
        public enum DuelBlockMoveT { Off, Formation, Loco, Input }
        public static TorsoMoveModeT TorsoMoveMode = TorsoMoveModeT.Pin;
        public static TorsoBlockMoveT TorsoBlockMove = TorsoBlockMoveT.Formation;
        public static DuelBlockMoveT DuelBlockMove = DuelBlockMoveT.Input;

        // Session 61c: raw input-channel probe (combatai.inputtest). Nonzero =
        // continuously write this facing-relative vector to MovementInputVector
        // while we drive -- isolates whether the channel works at all vs
        // whether the conflict is block/lock-specific.
        public static Vec2 InputTestVec = Vec2.Zero;
        public static float TorsoFormationFactor = 1f;
        public static float DuelBlockFactor = 1f;   // session 58: duel block-move enforcement factor
        public static bool TorsoFactorSet;      // user passed `factor` explicitly
        public static bool TorsoMoveNativeLoco;
        public static bool TorsoMoveLog;
        // Session 59 user decision: view-keeping (bisector heading / retreat
        // step) is OFF by default -- "the only difference should be that the
        // AI can move while blocking". When off, Formation AND Free behave
        // exactly like the log-proven Session-56 formation frame.
        public static bool TorsoViewKeep;

        // combatai.faceframe -- FACING-PRIORITY experiment: ONE designated AI
        // agent holds a combat lock on the player the WHOLE test
        // (EnsureTargetAgent every frame -- never released, never null: the
        // lock is the crash-safe part) while a formation frame asks for a
        // facing FaceFrameAngleDeg off the agent->player bearing. Answers:
        // does a formation frame's DIRECTION override a held target lock's
        // facing? Stationary test -- frame position = own position (arrived
        // every frame, no drag), velocity zero, no move flags. angle 180 =
        // face directly away from the locked target (strongest test); spin
        // sweeps the offset ~45deg/s so body tracking is visible live.
        // [FACEFRAME] logs bearings ~0.5s (120-line cap): legs/look
        // converging to the frame bearing = frame wins; pinned on the player
        // bearing = lock wins.
        // `lock self` (Session 68 Option-Z, the DEFAULT): the agent locks onto
        // ITSELF -- a self-lock has no valid facing direction to pull toward,
        // so the formation frame's direction may finally own facing.
        // `lock player` = the Session-67 control group (legs pinned to the
        // lock bearing). The frame command is unchanged in both: a facing
        // FaceFrameAngleDeg off the agent->player bearing.
        // `lock none` (FaceFrameLockNone): NO lock writes at all -- neither
        // EnsureTargetAgent nor SetTargetAgent is ever called, so the combat
        // target slot stays exactly as-is (run combatai.unlock first to leave
        // it at -1). Auto-select is still forced off so native can't re-lock
        // and poison the test: does the frame own facing with the lock truly
        // absent? Logs `lock=none`.
        public static bool FaceFrameMode = false;
        public static int FaceFrameAgentIndex = -1;
        public static float FaceFrameAngleDeg = 90f;
        public static float FaceFrameFactor = 1f;
        public static bool FaceFrameSpin = false;
        public static bool FaceFrameLockSelf = true;
        public static bool FaceFrameLockNone = false;

        // Session 69 writer/move experiment (native decompile mod_0073.c):
        // set_movement_input_vector writes ONLY agent+0x4a4 (vec2);
        // set_look_direction / set_look_direction_as_angle write ONLY the
        // +0x474 dir / +0x484 angle (angle = atan2f(-dir.x, dir.y) -- the
        // "+Y = angle 0" convention, verified in FUN_1806f2550/1806f4e40).
        // So with the lock released, LookDirection can own facing while
        // MovementInputVector owns locomotion -- no frame, no conflict.
        //   writer frame|lookdir|lookangle  -- who writes the commanded facing
        //   move   off|strafe|fwd|back|circle -- facing-relative input vec
        public enum FaceFrameWriterT { Frame, LookDir, LookAngle }
        public enum FaceFrameMoveT { Off, Strafe, Fwd, Back, Circle }
        public static FaceFrameWriterT FaceFrameWriter = FaceFrameWriterT.Frame;
        public static FaceFrameMoveT FaceFrameMove = FaceFrameMoveT.Off;

        // combatai.disable / Ctrl+Shift+K off: full stop -- every test mode
        // off plus the same cleanup their own off-branches run (spar indices
        // back to -1, group EngagementMap.Reset()).
        public static void DisableAllModes()
        {
            DuelMode = false;
            DefenseOnly = false;
            SparMode = false;
            GroupFightMode = false;
            EvadeMode = false;
            AngleDodgeMode = false;
            PerfectBlockMode = false;
            ContinuousAttackMode = false;
            LookTestMode = false;
            SparDefenderIndex = -1;
            SparAttackerIndex = -1;
            PendingArenaFight = CombatAiArenaKind.None;
            DuoAttackMode = false;
            ArenaFightActive = false;
            ArenaRoster.Clear();
            CbTargetMode = false;
            CombatAiOverrideComponent.CbClearHits();
            CombatAiTune.ExitDuoProfile();
            CombatAiTune.ExitGroupProfile();
            ArenaDuoCoordinator.Reset();
            EngagementMap.Reset();
            GroupFormationAdvance.Reset();
            GroupLineup.Reset();
            GroupTargeting.Reset();
            CombatAiOverrideComponent.GfStaticReset();
            TorsoMoveMode = TorsoMoveModeT.Pin;
            TorsoBlockMove = TorsoBlockMoveT.Formation;
            DuelBlockMove = DuelBlockMoveT.Input;
            InputTestVec = Vec2.Zero;
            TorsoFormationFactor = 1f;
            DuelBlockFactor = 1f;
            TorsoFactorSet = false;
            TorsoMoveNativeLoco = false;
            TorsoMoveLog = false;
            TorsoViewKeep = false;
            ArenaForceShields = false;
            ArenaForceMounts = false;
            CavDuelMode = false;
            FaceFrameMode = false;
            FaceFrameAgentIndex = -1;
            FaceFrameAngleDeg = 90f;
            FaceFrameFactor = 1f;
            FaceFrameSpin = false;
            FaceFrameLockSelf = true;   // Option-Z default: self is the experiment
            FaceFrameLockNone = false;
            FaceFrameWriter = FaceFrameWriterT.Frame;
            FaceFrameMove = FaceFrameMoveT.Off;
            LookOnlyMode = false;
            HoldSpinMode = false;
            LockThrashMode = false;
            NativeTargetGuard.ClearWatch();   // full stop also ends release-hold watches
        }
    }

    // Player posture system (RBM-style, see research doc sec. 10): the player's
    // regular blocks chip posture; when a blocked hit depletes it, THAT blow
    // breaks the guard and crushes through as real damage (RegisterBlow path in
    // OnAgentHit) and posture resets to 75% -- RBM's POSTURE_RESET_MODIFIER.
    public static class PlayerPosture
    {
        public const float Max = 100f;
        public const float ChipPerBlock = 15f;
        private const float RegenPerSecond = 12f;
        private const float RegenDelaySeconds = 1.5f;
        private const float ResetModifier = 0.75f;

        public static float Value { get; private set; } = Max;
        private static float _sinceLastHit = 999f;

        public static void Tick(float dt)
        {
            _sinceLastHit += dt;
            if (_sinceLastHit > RegenDelaySeconds && Value < Max)
                Value = MathF.Min(Max, Value + RegenPerSecond * dt);
        }

        public static void Reset()
        {
            Value = Max;
            _sinceLastHit = 999f;
        }

        // A blocked hit lands on the player's guard. Returns the amount that
        // overflowed past 0 -- the "overkill" that becomes crush-through damage.
        public static float ApplyBlockHit(float postureDamage)
        {
            _sinceLastHit = 0f;
            float before = Value;
            Value = MathF.Max(0f, Value - postureDamage);
            return MathF.Max(0f, postureDamage - before);
        }

        public static void OnPlayerTookHit()
        {
            _sinceLastHit = 0f;
        }

        // Posture broke -> reset to a fraction of max so the next few blocks
        // are still vulnerable-ish rather than instantly full again.
        public static void ResetAfterBreak()
        {
            Value = Max * ResetModifier;
        }
    }

    // Attaches a CombatAiOverrideComponent to every AI-controlled human agent, the same
    // way the archived CombatNullifierMissionBehavior enabled its Harmony-based hook --
    // except here it's a plain, first-party AgentComponent, so no Harmony is involved at
    // all. Also logs a periodic summary so we can see, from the log file alone, whether
    // native is actually trying to act (and being overridden) every tick.
    public class CombatAiMissionBehavior : MissionBehavior
    {
        private const float LogIntervalSeconds = 5f;
        private const int MaxIndividualAttachLogLines = 20;

        private readonly Dictionary<int, CombatAiOverrideComponent> _components = new Dictionary<int, CombatAiOverrideComponent>();
        private float _logTimer;
        private int _postureBlowGuard; // >0 while registering our synthetic crush-through blow
        private int _agentsLoggedIndividually;
        private bool _swingLocked;
        private Vec3 _swingLockLook;
        private bool _missionInitRan;   // Session 31: see below -- OnBehaviorInitialize never fires for us
        private bool _msCompatDone;
        private int _msRetryTicks;
        // ~20-40s of mission frames: covers MCM lazy-init making the settings
        // instance null early, and MasterStrikes loading late -- without
        // rescanning assemblies forever on a MasterStrikes-free install.
        private const int MsRetryTickLimit = 1200;

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        // IMPORTANT (Session 31, verified in decompiled Mission.AfterStart):
        // the engine runs its OnBehaviorInitialize pass BEFORE calling our
        // submodule's OnMissionBehaviorInitialize -- which is where we
        // AddMissionBehavior -- and AddMissionBehavior itself only calls
        // OnCreated(). So THIS METHOD NEVER RUNS for our behavior (the log
        // file never showed a single [MISSION] line across every session).
        // All init logic lives in EnsureMissionInitialized, driven from the
        // first OnMissionTick -- a hook that provably runs.
        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            EnsureMissionInitialized();
        }

        private void EnsureMissionInitialized()
        {
            if (_missionInitRan)
                return;
            _missionInitRan = true;
            // Arena menu picked a fight but this mission isn't the arena
            // practice one (interrupted transition) -- don't carry the arming
            // into an unrelated mission where it would arm out of nowhere.
            if (CombatAiState.PendingArenaFight != CombatAiArenaKind.None && !ArenaOverhaulBridge.IsArenaPracticeMission(Mission))
            {
                CombatAiLog.Log($"[ARENA] pending fight {CombatAiState.PendingArenaFight} cleared: mission has no ArenaPracticeFightMissionController.");
                CombatAiState.PendingArenaFight = CombatAiArenaKind.None;
                ArenaOverhaulBridge.RestoreSettings();
            }
            CombatAiLog.Log($"[MISSION] CombatAiMissionBehavior initialized. OverrideEnabled={CombatAiState.OverrideEnabled}. Log path: {CombatAiLog.LogFilePath}");
            // Custom-battle targeting replica auto-arms in custom battles:
            // managed copy of native compute_basic_score running in PARALLEL,
            // log-only, native targeting untouched (combatai.cbtarget off
            // sticks via CbTargetUserDisabled).
            if (!CombatAiState.CbTargetUserDisabled
                && Mission.GetMissionBehavior<CustomBattleAgentLogic>() != null)
            {
                CombatAiState.CbTargetMode = true;
                CombatAiLog.Log("[CBTGT] armed: custom battle detected -- managed native-target replica running in PARALLEL (log only, no override).");
            }
            TryApplyMasterStrikesCompat();
            CombatAiMcmBridge.Refresh();
            // Each mission gets a fresh log quota (the per-process caps ran out mid-session once).
            CombatAiOverrideComponent.ResetDiagnosticLogCounters();
        }

        private void TryApplyMasterStrikesCompat()
        {
            MasterStrikesCompat.VoidMasterStrikesFeatures();
            // Second safety layer around the Harmony call specifically: it's
            // the only code path in this mod that touches a HarmonyLib type,
            // so if 0Harmony.dll isn't present this guards against even a
            // TypeLoadException on first touch of MasterStrikesHarmonyPatches
            // itself (on top of the try/catch already inside that method).
            try
            {
                MasterStrikesHarmonyPatches.ApplyOnce();
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[MASTERSTRIKES] Harmony patch call failed to even load (non-fatal): {ex.Message}");
            }
        }

        public override void OnAgentBuild(Agent agent, Banner banner)
        {
            TryAttach(agent);
        }

        public override void OnMissionTick(float dt)
        {
            EnsureMissionInitialized();
            CombatAiMcmBridge.Tick(dt);

            // Released-agent re-lock watchdog (research 14): two field-read
            // early-out unless nativehook is armed AND something was released.
            NativeTargetGuard.Poll();

            // MasterStrikes compat retry window: the first call can come back
            // empty-handed while MCM's GlobalSettings.Instance is still null
            // or the MasterStrikes assembly hasn't loaded yet -- keep trying
            // for a bounded number of frames until detection resolves one way
            // or the other, then stop rescanning assemblies every frame.
            if (!_msCompatDone)
            {
                TryApplyMasterStrikesCompat();
                bool fullyVoided = CombatAiState.MasterStrikesDetected
                    && (CombatAiState.MasterStrikesSettingsVoided || CombatAiState.MasterStrikesHarmonyApplied);
                if (fullyVoided || ++_msRetryTicks > MsRetryTickLimit)
                {
                    _msCompatDone = true;
                    if (!CombatAiState.MasterStrikesDetected)
                        CombatAiLog.Log("[MASTERSTRIKES] not detected -- compat inert (MasterStrikes not installed/enabled).");
                    else if (!fullyVoided)
                        CombatAiLog.Log($"[MASTERSTRIKES] detected but void incomplete (settings_voided={CombatAiState.MasterStrikesSettingsVoided} patches={CombatAiState.MasterStrikesPatchesApplied}/3) -- some MasterStrikes AI behavior may still be live.");
                }
            }

            // Catch late-spawning agents (reinforcements), same pattern as the archived POC.
            MBReadOnlyList<Agent> agents = Mission.Agents;
            for (int i = 0; i < agents.Count; i++)
            {
                TryAttach(agents[i]);
            }

            CheckToggleHotkey();

            // T1 player model: observes the player's stage machine every frame
            // (attack chains, feint cancels, release timing). Always ticked --
            // duel/spar/group defenders all read it.
            PlayerCombatModel.Tick(dt, Mission.MainAgent);
            // Session 51 fight script: runs only while the override is armed
            // and a fight mode is live -- neutral otherwise.
            if (CombatAiState.OverrideEnabled && (CombatAiState.DuelMode || CombatAiState.SparMode))
                FightScript.Tick(dt);
            if (CombatAiState.GroupFightMode)
            {
                EngagementMap.Tick(dt, Mission);
                GroupFormationAdvance.Tick(dt, Mission);
                if (CombatAiState.GroupNativeTarget)
                {
                    CombatAiOverrideComponent.GfBuildTargetCounts(Mission);   // once per frame: allies-on-target + enemies-on-me maps (4x)
                    CombatAiOverrideComponent.GfSumTick(Mission);             // [GFSM-SUM] 10s aggregate (4w)
                }
                TickGroupDiagnostics(dt);
            }
            else
                _groupWasOn = false;

            // ArenaOverhaul fight arming: poll for the spawned participants
            // (they appear a few frames after StartPlayerPractice).
            if (CombatAiState.PendingArenaFight != CombatAiArenaKind.None)
                TryArmPendingArenaFight();
            if (CombatAiState.ArenaFightActive)
                TickArenaRoster(dt);
            if (CombatAiState.DuoAttackMode)
                ArenaDuoCoordinator.Tick(dt, Mission);
            else if (_armedByArena && CombatAiState.SparMode)
                TickArenaSparMaintenance(dt);

            // Custom-battle targeting replica (log-only, parallel): each
            // driven component scores its own candidates and logs our pick vs
            // native's (GetTargetAgent) -- native targeting is never touched.
            // The formation summary line shows the order set on each team's
            // (up to 8) formation slots so order->targeting is visible.
            if (CombatAiState.CbTargetMode)
            {
                CombatAiOverrideComponent.CbBuildTargetCounts(Mission);   // once per frame: crowd/rank map (kills the O(n^2) scan)
                foreach (KeyValuePair<int, CombatAiOverrideComponent> kv in _components)
                    kv.Value.CbTargetTick(dt, Mission);
                CombatAiOverrideComponent.CbFormationTick(dt, Mission);
                CombatAiOverrideComponent.CbSummaryTick(dt, Mission);
            }

            // Per-frame drive for the test modes. CRITICAL: OnAIInputSet only
            // fires ~1/sec per agent (native AI decision tick, see log evidence
            // in combat_ai_research.txt section 9) -- far too slow for parry
            // timing or attack press/release cycling. Agent.MovementFlags is a
            // public settable property and is exactly what MissionMainAgent-
            // Controller writes EVERY FRAME for the player, so we drive our
            // desired flag into it here, every frame, on the main thread.
            foreach (KeyValuePair<int, CombatAiOverrideComponent> kv in _components)
            {
                kv.Value.DrivePerFrame(dt);
            }

            if (CombatAiState.GodMode)
            {
                ApplyGodMode();
                _godModeRestorePending = true;
            }
            else if (_godModeRestorePending)
            {
                // One shot on the off edge: put the pumped HealthLimits back
                // so nothing stays immortal after godmode is switched off.
                _godModeRestorePending = false;
                RestoreGodHealthLimits();
            }

            // A committed player swing stays committed -- no walking through
            // the dodge, no mid-release camera redirect. Originally evade-
            // mode-only; folded into DuelMode too (Session 27) now that dodge
            // is a real defense there, not just an isolated test behavior.
            if (CombatAiState.EvadeMode || CombatAiState.AngleDodgeMode || CombatAiState.DuelMode)
                TickPlayerSwingLock(dt);
            else if (_swingLocked)
                ReleaseSwingLock();

            PlayerPosture.Tick(dt);

            _logTimer += dt;
            if (_logTimer >= LogIntervalSeconds)
            {
                _logTimer = 0f;
                LogSummary();
            }
        }

        // Ctrl+Shift+K toggles the override on/off mid-mission, no console needed
        // (matches the Ctrl+Shift+L/J/B style already used by BCCrBridge's hotkeys).
        private void CheckToggleHotkey()
        {
            if (Input.IsKeyDown(InputKey.LeftControl) && Input.IsKeyDown(InputKey.LeftShift) &&
                Input.IsKeyPressed(InputKey.K))
            {
                CombatAiState.OverrideEnabled = !CombatAiState.OverrideEnabled;
                if (!CombatAiState.OverrideEnabled)
                    CombatAiState.DisableAllModes();
                string state = CombatAiState.OverrideEnabled ? "ENABLED" : "DISABLED";
                InformationManager.DisplayMessage(new InformationMessage($"[CombatAI] Override {state}."));
                CombatAiLog.Log($"[HOTKEY] Ctrl+Shift+K -> override {state}.");
            }

            // Ctrl+Shift+I: INJECT a scripted AttackRight -> DefendRight sequence into
            // every currently-tracked AI agent. Proves we can force specific actions,
            // not just void them (see CombatAiOverrideComponent.TriggerInjectSequence).
            if (Input.IsKeyDown(InputKey.LeftControl) && Input.IsKeyDown(InputKey.LeftShift) &&
                Input.IsKeyPressed(InputKey.I))
            {
                int count = 0;
                foreach (KeyValuePair<int, CombatAiOverrideComponent> kv in _components)
                {
                    kv.Value.TriggerInjectSequence();
                    count++;
                }
                InformationManager.DisplayMessage(new InformationMessage($"[CombatAI] Injecting AttackRight -> DefendRight into {count} agent(s)."));
                CombatAiLog.Log($"[HOTKEY] Ctrl+Shift+I -> injected sequence into {count} agent(s).");
            }
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            if (affectedAgent != null)
            {
                _components.Remove(affectedAgent.Index);
                NativeTargetGuard.UnmarkReleased(affectedAgent);
            }
        }

        // Session 25: lock the PLAYER's camera for the duration of a
        // committed swing (originally evade-mode testing only; folded into
        // DuelMode in Session 27). Without it, the swing's aim can get
        // dragged mid-release.
        //   Session 29 (teleport-based position hold) found that fighting
        //   the player's own position at all -- even via a collision-
        //   respecting MovementInputVector correction -- wasn't worth it:
        //   retested fine for the collision/shield-bypass bug, but the user
        //   decided camera lock alone is enough to make AI evade meaningful,
        //   and asked to stop messing with player movement entirely. So
        //   movement is no longer touched here at all -- the player can
        //   walk freely through a release; only LookDirection is pinned.
        //   Unlocks the instant the agent leaves AttackRelease (or is gone).
        private void TickPlayerSwingLock(float dt)
        {
            Agent p = Mission.MainAgent;
            if (p == null || p.Mission == null || !p.IsActive())
            {
                ReleaseSwingLock();
                return;
            }
            if (p.GetCurrentActionStage(1) == Agent.ActionStage.AttackRelease)
            {
                if (!_swingLocked)
                {
                    _swingLocked = true;
                    _swingLockLook = p.LookDirection;
                    p.IsLookDirectionLocked = true;
                    CombatAiLog.Log("[SWINGLOCK] player released -- look locked (movement untouched) until swing ends.");
                }
                p.LookDirection = _swingLockLook;
            }
            else if (_swingLocked)
            {
                ReleaseSwingLock();
            }
        }

        private void ReleaseSwingLock()
        {
            if (!_swingLocked)
                return;
            _swingLocked = false;
            Agent p = Mission != null ? Mission.MainAgent : null;
            if (p != null && p.Mission != null && p.IsActive())
            {
                p.IsLookDirectionLocked = false;
            }
        }

        public override void OnRemoveBehavior()
        {
            ReleaseSwingLock();
            NativeTargetGuard.ClearWatch();   // released-agent watchdog dies with the mission
            NativeSteerOverride.ClearRegistry();   // steer channel keys die with the mission
            // Arena fight teardown: put AO's MCM counts back and clear every
            // flag we armed -- a stale SparMode index would drive the wrong
            // agent in a later mission, and a lingering DuelMode surprises
            // the next fight.
            ArenaOverhaulBridge.RestoreSettings();
            CombatAiTune.ExitDuoProfile();
            CombatAiTune.ExitGroupProfile();
            CombatAiState.ArenaFightActive = false;
            CombatAiState.ArenaForceShields = false;   // Session 62: the arena is over
            CombatAiState.ArenaForceMounts = false;    // cav duel: same teardown
            CombatAiState.ArenaRoster.Clear();
            if (_armedByArena)
            {
                _armedByArena = false;
                CombatAiState.PendingArenaFight = CombatAiArenaKind.None;
                CombatAiState.DuoAttackMode = false;
                CombatAiState.DuelMode = false;
                CombatAiState.SparMode = false;
                CombatAiState.SparDefenderIndex = -1;
                CombatAiState.SparAttackerIndex = -1;
                CombatAiState.GroupFightMode = false;   // GROUP5v5 arm -- teams die with the mission
                CombatAiState.CavDuelMode = false;      // CAV arm -- dies with the mission too
                _group5v5PlayerNeutral = false;
                GroupFormationAdvance.Reset();
                GroupLineup.Reset();
                GroupEngagement.Reset();
                GroupTargeting.Reset();
            CombatAiOverrideComponent.GfStaticReset();
                ArenaDuoCoordinator.Reset();
                _hitAtkLogCount = 0;
            }
            else if (CombatAiState.PendingArenaFight != CombatAiArenaKind.None || CombatAiState.DuoAttackMode)
            {
                CombatAiState.PendingArenaFight = CombatAiArenaKind.None;
                CombatAiState.DuoAttackMode = false;
                ArenaDuoCoordinator.Reset();
            }
            base.OnRemoveBehavior();
        }

        // Polls the practice controller's participant list until the fighters
        // exist, then arms the mode the arena menu asked for. This is the
        // "detect who is fighting" half of the integration -- no combatai.spar
        // indices needed; guards/townsmen are never in _participantAgents.
        private readonly List<int> _arenaPartBuf = new List<int>(8);
        private bool _armedByArena;  // OnRemoveBehavior only resets modes we actually armed
        private static int _hitAtkLogCount;  // [HIT-ATK] budget (Session 62)

        // [GROUP] diagnostics (Session 64): one line on arm + every 10s while
        // the group fight runs -- live headcount of the two biggest AI teams
        // plus the EngagementMap coverage histogram (1-att/2-att/3+-att
        // victims). Bounded like the other log budgets.
        private float _groupLogTimer;
        private bool _groupWasOn;
        private static int _groupLogCount;
        private const int MaxGroupLogs = 80;
        private const float GroupLogIntervalSeconds = 10f;

        public static void ResetGroupLogBudget()
        {
            _groupLogCount = 0;
            GroupFormationAdvance.ResetLogBudget();
            EngagementMap.ResetRetargetLogBudget();
        }

        private void TickGroupDiagnostics(float dt)
        {
            if (!_groupWasOn)
            {
                _groupWasOn = true;
                _groupLogTimer = 0f;
                LogGroupLine("arm");
                return;
            }
            _groupLogTimer += dt;
            if (_groupLogTimer >= GroupLogIntervalSeconds)
            {
                _groupLogTimer = 0f;
                LogGroupLine("tick");
            }
        }

        private void LogGroupLine(string tag)
        {
            if (_groupLogCount >= MaxGroupLogs)
                return;
            _groupLogCount++;
            Team tA = null, tB = null;
            int cA = 0, cB = 0;
            var counts = new Dictionary<Team, int>(8);
            foreach (Agent a in Mission.Agents)
            {
                if (a == null || !a.IsActive() || a.Health <= 0f || !a.IsHuman || !a.IsAIControlled)
                    continue;
                Team t = a.Team;
                if (t == null)
                    continue;
                counts[t] = counts.TryGetValue(t, out int c) ? c + 1 : 1;
            }
            foreach (KeyValuePair<Team, int> kv in counts)
            {
                if (kv.Value > cA) { tB = tA; cB = cA; tA = kv.Key; cA = kv.Value; }
                else if (kv.Value > cB) { tB = kv.Key; cB = kv.Value; }
            }
            EngagementMap.VictimCoverage(out int one, out int two, out int threePlus);
            // Session 66 line advance: how many driven agents are still in the
            // pre-contact march phase (they drop out of it as their victim
            // reaches contact range).
            int advancing = 0;
            foreach (KeyValuePair<int, CombatAiOverrideComponent> kv in _components)
                if (kv.Value.IsGroupAdvancing)
                    advancing++;
            CombatAiLog.Log($"[GROUP] {tag}: teamA={cA} teamB={cB} victims[1att]={one} [2att]={two} [3+att]={threePlus} " +
                $"teams={counts.Count} advancing={advancing} marching={GroupFormationAdvance.Marching}.");
        }
        private void TryArmPendingArenaFight()
        {
            if (!ArenaOverhaulBridge.TryGetArenaParticipantIndices(Mission, _arenaPartBuf) || _arenaPartBuf.Count == 0)
                return;

            CombatAiArenaKind kind = CombatAiState.PendingArenaFight;
            CombatAiState.PendingArenaFight = CombatAiArenaKind.None;
            CombatAiState.OverrideEnabled = true;
            CombatAiState.ArenaFightActive = true;
            CombatAiState.ArenaRoster.Clear();
            foreach (int i in _arenaPartBuf)
                CombatAiState.ArenaRoster.Add(i);
            _armedByArena = true;

            switch (kind)
            {
                case CombatAiArenaKind.Duel:
                    CombatAiState.DuelMode = true;
                    CombatAiLog.Log($"[ARENA] armed DUEL: {_arenaPartBuf.Count} participant(s) detected: [{string.Join(",", _arenaPartBuf)}].");
                    break;
                case CombatAiArenaKind.Spar:
                case CombatAiArenaKind.SparShield:
                    if (_arenaPartBuf.Count >= 2)
                    {
                        _arenaPartBuf.Sort();
                        CombatAiState.SparDefenderIndex = _arenaPartBuf[0];
                        CombatAiState.SparAttackerIndex = _arenaPartBuf[1];
                        CombatAiState.SparMode = true;
                        CombatAiLog.Log($"[ARENA] armed SPAR{(kind == CombatAiArenaKind.SparShield ? " (shields)" : "")}: defender=#{_arenaPartBuf[0]} attacker=#{_arenaPartBuf[1]} (auto-assigned).");
                        if (kind == CombatAiArenaKind.SparShield)
                        {
                            foreach (int pi in _arenaPartBuf)
                            {
                                Agent pa = Mission.Agents.FirstOrDefault(a => a.Index == pi);
                                CombatAiLog.Log($"[ARENA]   #{pi} hasShield={(pa != null ? pa.HasShieldCached.ToString() : "n/a")}");
                            }
                        }
                    }
                    else
                    {
                        CombatAiState.DuelMode = true;
                        CombatAiLog.Log($"[ARENA] spar requested but only {_arenaPartBuf.Count} participant(s) -- falling back to DUEL.");
                    }
                    break;
                case CombatAiArenaKind.Defend2v1:
                    CombatAiState.DuelMode = true; // drives the flag machinery; the duo branch below gates per-agent
                    CombatAiState.DuoAttackMode = true;
                    CombatAiTune.EnterDuoProfile();
                    // AO's _participantAgents includes the PLAYER, but the duo
                    // coordinator may only ever manage the AI attackers: if the
                    // player holds one of the two side slots, the second AI
                    // attacker never gets one and waits in reserve forever
                    // (standing still at spawn -- log-verified in all three
                    // Defend2v1 runs of combat_ai53).
                    Agent duoMain = Mission.MainAgent;
                    if (duoMain != null)
                        _arenaPartBuf.RemoveAll(i => i == duoMain.Index);
                    ArenaDuoCoordinator.SetParticipants(_arenaPartBuf);
                    CombatAiLog.Log($"[ARENA] armed DEFEND-2v1: attackers=[{string.Join(",", _arenaPartBuf)}], player is the defender (aggressive duo tuning on).");
                    break;
                case CombatAiArenaKind.CavDuel:
                    CombatAiState.DuelMode = true;
                    CombatAiState.CavDuelMode = true;
                    CombatAiLog.Log($"[ARENA] armed CAV DUEL: {_arenaPartBuf.Count} participant(s) detected: [{string.Join(",", _arenaPartBuf)}] steer={CombatAiState.CavSteer}.");
                    // Post-spawn mount check: every roster agent + the player
                    // should be mounted (ArenaCavalryPatch) -- proves the
                    // NoHorses/equipment hooks landed on the live build.
                    foreach (int ci in _arenaPartBuf)
                    {
                        Agent ca = Mission.Agents.FirstOrDefault(x => x.Index == ci);
                        CombatAiLog.Log($"[ARENA] cav spawn: #{ci} mounted={(ca != null ? ca.HasMount.ToString() : "?")} " +
                            $"mount={(ca != null && ca.MountAgent != null ? ca.MountAgent.Index.ToString() : "-")} " +
                            $"wielding={(ca != null ? ca.WieldedWeapon.Item?.StringId ?? "-" : "-")}");
                    }
                    Agent cavPlayer = Mission.MainAgent;
                    CombatAiLog.Log($"[ARENA] cav spawn: player mounted={(cavPlayer != null ? cavPlayer.HasMount.ToString() : "?")} " +
                        $"mount={(cavPlayer != null && cavPlayer.MountAgent != null ? cavPlayer.MountAgent.Index.ToString() : "-")}");
                    break;
                case CombatAiArenaKind.GroupFight5v5:
                    CombatAiState.GroupFightMode = true; // EngagementMap self-pairs the two hostile AI teams
                    if (CombatAiState.GfSpice) CombatAiTune.EnterGroupProfile();   // 4z aggressive group tuning
                    GroupTargeting.Reset();   // fresh anchor assignment for this fight
                    CombatAiOverrideComponent.GfStaticReset();
                    if (!_group5v5PlayerNeutral)
                        _group5v5PlayerNeutral = NeutralizePlayerTeamForGroupFight();
                    // Session 67: REAL formation orders for the line advance
                    // (ArenaRoster was just populated above). Any failure
                    // inside Arm flips CombatAiState.FormationMarch off and
                    // the old flag-push advance keeps running.
                    if (!CombatAiState.GroupFree)
                        GroupFormationAdvance.Arm(Mission);   // groupfree ON: lineup owns the approach
                    CombatAiLog.Log($"[ARENA] armed GROUP5v5: {_arenaPartBuf.Count} participant(s) detected: [{string.Join(",", _arenaPartBuf)}]. playerNeutral={_group5v5PlayerNeutral} formationMarch={CombatAiState.FormationMarch}.");
                    break;
            }
        }

        // GROUP 5v5: the player is an OBSERVER -- neutralize enmity between his
        // team and every other mission team, BOTH directions. Vanilla's
        // ArrangePlayerTeamEnmity (decompiled ArenaPracticeFightMissionController:
        // aIParticipantTeam.SetIsEnemyOf(Mission.PlayerTeam, IsPlayerPracticing))
        // runs inside StartPractice -- i.e. BEFORE the participants we wait on
        // exist -- so this first arm tick is always after the relation was
        // written. Team.SetIsEnemyOf(other, v) is the FORWARD direction ("this
        // team treats other as an enemy" -- MBTeam.SetIsEnemyOf, Team.cs:618),
        // so both calls are needed: attacker-side teams may also have been made
        // hostile to the defender-side player team at creation. Returns the
        // post-fixup verification (rechecked IsEnemyOf over all teams): true =
        // the player's team is fully neutral. After the fixup EngagementMap's
        // a.IsEnemyOf(victim) victim-pool filter and PlayerThreatens naturally
        // exclude the player -- the two AI sides only see each other.
        private bool _group5v5PlayerNeutral;
        private bool NeutralizePlayerTeamForGroupFight()
        {
            Team playerTeam = Mission != null ? Mission.PlayerTeam : null;
            if (playerTeam == null)
                return false;
            foreach (Team t in Mission.Teams)
            {
                if (t == null || t == playerTeam)
                    continue;
                if (playerTeam.IsEnemyOf(t) || t.IsEnemyOf(playerTeam))
                {
                    playerTeam.SetIsEnemyOf(t, false);
                    t.SetIsEnemyOf(playerTeam, false);
                }
            }
            foreach (Team t in Mission.Teams)
            {
                if (t != null && t != playerTeam
                    && (playerTeam.IsEnemyOf(t) || t.IsEnemyOf(playerTeam)))
                    return false;
            }
            return true;
        }

        // AO's SpawnArenaAgents keeps refilling _participantAgents as fighters
        // drop (until the total-participant roster runs dry), but SparMode is
        // bound to two FIXED indices -- a dead defender or attacker index does
        // nothing. Re-pin the roles onto live participants ~2x/sec so the spar
        // survives kills. Survivors keep their role; only the dead slot is
        // refilled. (Duel needs nothing -- nearest-enemy picks up each new
        // spawn automatically; the duo coordinator self-refreshes.)
        // Live participant roster for IsEligibleForTestModes' arena gate:
        // refreshed ~2x/sec for every arena-armed kind (duel/spar/2v1), so
        // AO's backfill spawns become driven and removed/absent agents fall
        // back to native. Cheap: reflection read on the 0.5s tick, a HashSet
        // lookup per agent per frame the rest of the time.
        private float _arenaRosterT;
        private void TickArenaRoster(float dt)
        {
            _arenaRosterT += dt;
            if (_arenaRosterT < 0.5f)
                return;
            _arenaRosterT = 0f;
            if (!ArenaOverhaulBridge.TryGetArenaParticipantIndices(Mission, _arenaPartBuf))
                return;
            CombatAiState.ArenaRoster.Clear();
            foreach (int i in _arenaPartBuf)
                CombatAiState.ArenaRoster.Add(i);
        }

        private float _arenaSparMaintainT;
        private void TickArenaSparMaintenance(float dt)
        {
            _arenaSparMaintainT += dt;
            if (_arenaSparMaintainT < 0.5f)
                return;
            _arenaSparMaintainT = 0f;

            if (!ArenaOverhaulBridge.TryGetArenaParticipantIndices(Mission, _arenaPartBuf) || _arenaPartBuf.Count == 0)
                return;

            bool LiveIndex(int i)
            {
                Agent a = Mission.FindAgentWithIndex(i);
                return a != null && a.Mission != null && a.IsActive() && a.Health > 0f && !a.IsMainAgent;
            }

            _arenaPartBuf.Sort();
            int def = LiveIndex(CombatAiState.SparDefenderIndex) ? CombatAiState.SparDefenderIndex : -1;
            int atk = LiveIndex(CombatAiState.SparAttackerIndex) ? CombatAiState.SparAttackerIndex : -1;
            if (def >= 0 && atk >= 0 && def != atk)
                return; // both roles still on live fighters

            foreach (int i in _arenaPartBuf)
            {
                if (!LiveIndex(i))
                    continue;
                if (def < 0 && i != atk) { def = i; continue; }
                if (atk < 0 && i != def) { atk = i; }
            }

            if (def != CombatAiState.SparDefenderIndex || atk != CombatAiState.SparAttackerIndex)
            {
                CombatAiState.SparDefenderIndex = def;
                CombatAiState.SparAttackerIndex = atk;
                CombatAiLog.Log($"[ARENA] spar refill: defender=#{def} attacker=#{atk} live=[{string.Join(",", _arenaPartBuf.Where(LiveIndex))}].");
            }
        }

        // Ground truth for the attack-loop test AND the posture system: fires
        // for EVERY blow in the mission. Blocked/parried blows still fire this
        // with ~0 damage (proven by the [HIT-US] entries in test 4), so the
        // player's defense classification at impact tells us how each swing
        // resolved.
        public override void OnAgentHit(Agent affectedAgent, Agent affectorAgent, in MissionWeapon affectorWeapon, in Blow blow, in AttackCollisionData attackCollisionData)
        {
            // Targeting replica revenge channel (x6 last-hitter term): feed it
            // from every blow while armed (native writes Agent_ai+0x2398 on
            // blow processing; this is the managed-visible equivalent).
            if (CombatAiState.CbTargetMode && affectorAgent != null && affectedAgent != null)
                CombatAiOverrideComponent.CbRecordHit(affectedAgent, affectorAgent);

            // Feed the player model: release->impact timing AND the engine's
            // own outcome verdict (blocked/parried/clean) so the swing record
            // carries how the AI answered, not just when it landed.
            if (affectorAgent != null && affectorAgent.IsMainAgent)
                PlayerCombatModel.OnPlayerBlowLanded(attackCollisionData.CollisionResult);
            if (affectorAgent != null && affectedAgent != null)
            {
                if (affectorAgent.IsMainAgent && affectedAgent.IsAIControlled)
                    EngagementMap.OnPlayerAgentHit(affectedAgent.Index);
                else if (affectedAgent.IsMainAgent && affectorAgent.IsAIControlled)
                    EngagementMap.OnPlayerAgentHit(affectorAgent.Index);
            }

            // Fight script momentum input (Session 51): scores each side's
            // block/miss ratio before anything else reads the blow.
            FightScript.OnHit(affectedAgent, affectorAgent, attackCollisionData.CollisionResult, blow.InflictedDamage);

            // Turn watch (Session 55): a tracked duel AI's swing resolved on
            // the player -- the fight script hands the turn over for a while.
            if (affectorAgent != null && affectedAgent != null && affectedAgent.IsMainAgent
                && _components.TryGetValue(affectorAgent.Index, out CombatAiOverrideComponent swingComp))
            {
                swingComp.OnOwnSwingResolved(attackCollisionData.CollisionResult);
            }

            // Torso-move diag counters (Session 56): any blow ON a tracked agent.
            if (affectedAgent != null && _components.TryGetValue(affectedAgent.Index, out CombatAiOverrideComponent hitComp))
                hitComp.NoteTorsoHit(attackCollisionData.CollisionResult);

            // Session 62: rigged attacker -> defender blows, so the next log
            // proves whether side blocks land (shield flag + correct side).
            if (CombatAiState.SparMode
                && affectorAgent != null && affectorAgent.Index == CombatAiState.SparAttackerIndex
                && affectedAgent != null && affectedAgent.Index == CombatAiState.SparDefenderIndex
                && _hitAtkLogCount < 300)
            {
                _hitAtkLogCount++;
                int prim = _components.TryGetValue(affectedAgent.Index, out CombatAiOverrideComponent defComp)
                    ? defComp.RigPrimaryIndex : -1;
                CombatAiLog.Log($"[HIT-ATK] Agent #{affectedAgent.Index} took {blow.InflictedDamage} dmg from spar attacker: " +
                    $"result={attackCollisionData.CollisionResult} shieldBlock={attackCollisionData.AttackBlockedWithShield} " +
                    $"correctSide={attackCollisionData.CorrectSideShieldBlock} defStage={affectedAgent.GetCurrentActionStage(1)} primary=#{prim}");
            }

            // Spam counter: the PLAYER's blow landed on one of OUR driven agents --
            // a Blocked/Parried result may trigger the armed master strike / clinch.
            if (affectorAgent != null && affectorAgent.IsMainAgent
                && affectedAgent != null
                && _components.TryGetValue(affectedAgent.Index, out CombatAiOverrideComponent counterComp))
            {
                counterComp.NoteSpamSwingResult(attackCollisionData.CollisionResult);   // stamp the run first
                counterComp.TryFireSpamCounter(affectorAgent, attackCollisionData.CollisionResult);
            }

            if (affectedAgent != null && affectorAgent != null
                && affectedAgent.IsMainAgent
                && _components.TryGetValue(affectorAgent.Index, out _))
            {
                // Our own synthetic crush-through blow re-enters this callback --
                // don't let it chip posture again.
                if (_postureBlowGuard > 0)
                    return;

                // Exact outcome from the engine's own collision result (the RBM
                // recipe) -- far more reliable than reading the player's action
                // stage, which lagged and classified wrong.
                CombatCollisionResult result = attackCollisionData.CollisionResult;
                Agent.ActionStage vStage = affectedAgent.GetCurrentActionStage(1);
                string state = $"collision={result} shield={attackCollisionData.AttackBlockedWithShield} vStage={vStage}";

                if (result == CombatCollisionResult.Parried || result == CombatCollisionResult.ChamberBlocked)
                {
                    // Parry (or the player chambered our swing) -- no chip, and
                    // per the design parries are the REWARDED defense.
                    PlayerPosture.OnPlayerTookHit();
                    CombatAiLog.Log($"[HIT-PLAYER] Agent #{affectorAgent.Index} ({affectorAgent.Name}): player PARRIED ({blow.InflictedDamage} dmg, {state}). Posture={PlayerPosture.Value:F0}");
                }
                else if (result == CombatCollisionResult.Blocked)
                {
                    float overkill = PlayerPosture.ApplyBlockHit(PlayerPosture.ChipPerBlock);
                    if (PlayerPosture.Value <= 0f)
                    {
                        // POSTURE BREAK: the blow that emptied the guard crushes
                        // through it -- a synthetic second blow injected via the
                        // engine's own RegisterBlow so it gets real damage/stun/
                        // armor handling (RBM's makePostureCrashThroughBlow).
                        Blow crush = blow;
                        crush.InflictedDamage = (int)MathF.Max(10f, overkill);
                        crush.DefenderStunPeriod = blow.DefenderStunPeriod * 3f;
                        crush.BlowFlag = blow.BlowFlag | BlowFlags.CrushThrough;
                        _postureBlowGuard++;
                        try
                        {
                            affectedAgent.RegisterBlow(crush, in attackCollisionData);
                        }
                        finally
                        {
                            _postureBlowGuard--;
                        }
                        PlayerPosture.ResetAfterBreak();
                        CombatAiLog.Log($"[HIT-PLAYER] Agent #{affectorAgent.Index} ({affectorAgent.Name}): POSTURE BREAK -- {crush.InflictedDamage} dmg crushed through the guard ({state}). Posture reset to {PlayerPosture.Value:F0}");
                    }
                    else
                    {
                        CombatAiLog.Log($"[HIT-PLAYER] Agent #{affectorAgent.Index} ({affectorAgent.Name}): player blocked ({blow.InflictedDamage} dmg, {state}). Posture={PlayerPosture.Value:F0}");
                    }
                }
                else
                {
                    PlayerPosture.OnPlayerTookHit();
                    CombatAiLog.Log($"[HIT-PLAYER] Agent #{affectorAgent.Index} ({affectorAgent.Name}) hit the player for {blow.InflictedDamage} dmg CLEAN (part={blow.VictimBodyPart}, stun={blow.DefenderStunPeriod:F2}s, {state}). Posture={PlayerPosture.Value:F0}");
                }
            }
        }

        // GodMode pins BOTH far above any single blow: healing to HealthLimit
        // once a tick can't save an agent from a hit that deals more than its
        // whole bar (~100) in one frame -- the old clamp lost that race. The
        // Health setter never clamps to HealthLimit, and HealthLimit itself
        // is settable, so pushing both to 100k makes overkill hits impossible.
        private const float GodModeHealthLimit = 100000f;
        private bool _godModeRestorePending;

        private void ApplyGodMode()
        {
            Agent main = Mission.MainAgent;
            if (main != null && main.Mission != null && main.IsActive())
                PinGodHealth(main);

            foreach (KeyValuePair<int, CombatAiOverrideComponent> kv in _components)
            {
                Agent a = kv.Value.TrackedAgent;
                // a.Mission != null first: Agent.Clear() zeroes all cached
                // native pointers on removal -- IsActive()/Health would then
                // dereference UIntPtr.Zero (0xC0000005).
                if (a != null && a.Mission != null && a.IsActive())
                    PinGodHealth(a);
            }
        }

        private static void PinGodHealth(Agent a)
        {
            if (a.HealthLimit < GodModeHealthLimit)
                a.HealthLimit = GodModeHealthLimit;
            if (a.Health < a.HealthLimit)
                a.Health = a.HealthLimit;
        }

        private void RestoreGodHealthLimits()
        {
            Agent main = Mission.MainAgent;
            if (main != null && main.Mission != null && main.IsActive())
            {
                main.HealthLimit = main.BaseHealthLimit;
                if (main.Health > main.HealthLimit)
                    main.Health = main.HealthLimit;
            }
            foreach (KeyValuePair<int, CombatAiOverrideComponent> kv in _components)
            {
                Agent a = kv.Value.TrackedAgent;
                if (a != null && a.Mission != null && a.IsActive())
                {
                    a.HealthLimit = a.BaseHealthLimit;
                    if (a.Health > a.HealthLimit)
                        a.Health = a.HealthLimit;
                }
            }
        }

        private void TryAttach(Agent agent)
        {
            if (agent == null || !agent.IsActive()) return;
            if (!agent.IsHuman) return;
            if (!agent.IsAIControlled) return;
            if (_components.ContainsKey(agent.Index)) return;
            if (agent.Components.Any(c => c is CombatAiOverrideComponent)) return;

            var component = new CombatAiOverrideComponent(agent);
            agent.AddComponent(component);

            // CRITICAL: native only calls Agent.OnAIInputSet (and therefore fans out to
            // AgentComponent.OnAIInputSet) for a given agent if this has been enabled for
            // it first. Without this, our override above is a no-op -- native never
            // invokes the callback at all, so nothing gets intercepted. Same requirement
            // the archived Harmony-based POC had (SetHasOnAiInputSetCallback), it's just
            // not automatic for AgentComponent either.
            agent.SetHasOnAiInputSetCallback(true);

            _components[agent.Index] = component;

            if (_agentsLoggedIndividually < MaxIndividualAttachLogLines)
            {
                _agentsLoggedIndividually++;
                bool callbackConfirmed = agent.GetHasOnAiInputSetCallback();
                CombatAiLog.Log($"[ATTACH] CombatAiOverrideComponent attached to agent #{agent.Index} ({agent.Name}). HasOnAiInputSetCallback={callbackConfirmed}");
            }
        }

        private void LogSummary()
        {
            long totalTicks = 0;
            long eventFlagTicks = 0;
            long movementFlagTicks = 0;
            int activeAgents = 0;

            foreach (KeyValuePair<int, CombatAiOverrideComponent> kv in _components)
            {
                CombatAiOverrideComponent c = kv.Value;
                totalTicks += c.TotalTicks;
                eventFlagTicks += c.InterceptedEventFlagTicks;
                movementFlagTicks += c.InterceptedMovementFlagTicks;
                activeAgents++;
            }

            CombatAiLog.Log(
                $"[SUMMARY] override_enabled={CombatAiState.OverrideEnabled} agents_tracked={activeAgents} " +
                $"total_ticks={totalTicks} native_wanted_event_flag={eventFlagTicks} native_wanted_movement_flag={movementFlagTicks} " +
                $"dodge[seen={CombatAiState.DodgeReleaseSeenCount} roll={CombatAiState.DodgeOpportunityCount} " +
                $"start={CombatAiState.OurDodgeStartCount} own={CombatAiState.DodgeSuppressedMidSwing} yield={CombatAiState.ForeignDodgeYieldCount}] " +
                $"ms[det={CombatAiState.MasterStrikesDetected} void={CombatAiState.MasterStrikesSettingsVoided} " +
                $"patch={CombatAiState.MasterStrikesPatchesApplied}/3]");
        }
    }
}
