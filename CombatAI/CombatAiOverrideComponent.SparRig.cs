using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =======================================================================
    // SPAR RIG (Session 62): replacement for ComputeSparDefenderFlag behind
    // CombatAiState.SparRig (default ON). Differences from the legacy path:
    //   - no scripted pin / formation frame / SetMovementDirection EVER;
    //   - legs travel through the MovementInputVector channel (the player WASD
    //     channel -- stays live inside block actions);
    //   - one PRIMARY target the defender faces (planner-picked, default the
    //     player); blocks never retarget (no lock-snap/body turns);
    //   - block direction uses shield + attacker side (BlockFlagVs).
    // Inert when SparRig is off / not the spar defender / mounted.
    // =======================================================================
    public sealed partial class CombatAiOverrideComponent
    {
        private const float RigVetoSeconds = 0.5f;
        private const float RigProbeDist = 1.5f;
        private const float RigFreeMin = 1.0f;
        private const int MaxRigMoveLogs = 1500;
        private const int MaxRigWallLogs = 150;
        private const int MaxRigWallProbeLogs = 60;
        private const int MaxRigDefLogs = 600;

        private bool _rigDriving;                 // ComputeSparRigDefenderFlag ran this tick
        private Agent _rigPrimary;                // the agent the defender faces
        private Vec2 _rigGoal;                    // world dir (zero = no move); may be <1 for slow drift
        private SparRigPlanner.RigState _rigState;
        private SparRigPlanner.RigPlan _rigPlan;
        private readonly System.Collections.Generic.List<(int key, Vec2 pos, bool isPlayer, bool incoming)> _rigEnemies
            = new System.Collections.Generic.List<(int, Vec2, bool, bool)>(4);
        private static int _rigMoveLogCount;
        private float _rigLastLogAt = -99f;
        // wall probe
        private static int _rigWallLogCount;
        private static int _rigStuckLogCount;
        private static int _rigEscLogCount;
        private Vec2 _rigVetoDir;
        private float _rigVetoUntil = -99f;
        private static int _rigDefLogCount;
        private static int _rigPrimaryLogCount;
        private float _rigLastFree = -1f;       // free distance used by the last wall step (A6)
        // Round 3: ranked candidate buffer; Round 4: shared clearance layer.
        private readonly SparRigPlanner.RigCand[] _rigRanked = new SparRigPlanner.RigCand[5];
        private int _rigRankedCount;
        private readonly AgentClearance _clear = new AgentClearance();
        private readonly AgentClearance _atkClear = new AgentClearance();
        private readonly StuckWatch _atkWatch = new StuckWatch();
        private float _rigEscapeUntil = -99f;
        private Vec2 _rigEscapeDir = Vec2.Zero;
        private float _rigWallZeroSince = -99f;
        private int _rigStuckCount, _rigEscapes;
        // Round-3 instrumentation counters (the roll latch itself moved to
        // per-attacker slots in Brain.cs, Session 64).
        private int _rigSideReleases, _rigSideMisses;
        private int _rigSumTicks, _rigSumT85, _rigSumT120, _rigSumSwitches, _rigSumMoveNone, _rigSumVetoes;
        private int _rigSumPrim, _rigSumLoaded;
        private int _rigS2Falls, _rigS2Busy, _rigS2Reads, _rigS2Buffer, _rigS2Ignored, _rigS1Press, _rigS1Bites;
        private float _rigSumLastAt = -99f;
        private int _rigLastDefThreat = -99, _rigLastDefSide = -99;
        private Agent.MovementControlFlag _rigLastDefFlag = Agent.MovementControlFlag.None;
        private float _rigLastDefAt = -99f;
        private float _slotSideSince = -99f;   // attacker slot-opposite-side hysteresis clock
        private int _lastPSide;               // last player side (-1/0/+1) seen by SparSlotMoveFlags
        // Session 63: incoming-test support (per-enemy distance history for
        // the "shrank >0.3m over ~1s" clause), [RIGLOCK] budget, and the
        // one-time arena boundary synthesis flag.
        private readonly int[] _rigDistKey = new int[2] { -1, -1 };
        private readonly float[] _rigDistPrev = new float[2];
        private readonly float[] _rigDistAt = new float[2] { -99f, -99f };
        private bool _rigAtkTargetsMe;
        private static int _rigLockLogCount;
        private const int MaxRigLockLogs = 80;
        private bool _rigBoundaryDone;
        private static int _rigBoundaryLogCount;
        private const int MaxRigBoundaryLogs = 8;

        private void RigResetState()
        {
            _rigDriving = false;
            _rigPrimary = null;
            _rigGoal = Vec2.Zero;
            _rigState = default(SparRigPlanner.RigState);
            _rigPlan = default(SparRigPlanner.RigPlan);
            _rigRankedCount = 0;
            _rigSideReleases = _rigSideMisses = 0;
            _rigSumTicks = _rigSumT85 = _rigSumT120 = _rigSumSwitches = _rigSumMoveNone = _rigSumVetoes = 0;
            _rigSumLastAt = -99f;
            _rigSumPrim = _rigSumLoaded = 0;
            _rigS2Falls = _rigS2Busy = _rigS2Reads = _rigS2Buffer = _rigS2Ignored = _rigS1Press = _rigS1Bites = 0;
            _rigStuckCount = _rigEscapes = 0;
            _rigWallZeroSince = -99f;
            _rigEscapeUntil = -99f;
            _rigDistKey[0] = _rigDistKey[1] = -1;
            _rigDistAt[0] = _rigDistAt[1] = -99f;
            _rigAtkTargetsMe = false;
        }

        // ------------------------------------------------------------------
        // (1) THE RIG DEFENDER -- replaces ComputeSparDefenderFlag when
        // CombatAiState.SparRig && SparMode && defender && !HasMount.
        // ------------------------------------------------------------------
        private Agent.MovementControlFlag ComputeSparRigDefenderFlag(float dt)
        {
            _lastOp = "rigDef:enter";
            _rigDriving = true;
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
            _legPinOwned = false;
            _multiEnemy = atkLive && playerLive;

            // Rig mode owns NO frames/levers: strip anything a previous mode
            // installed once (same calls the legacy cleanup branch uses).
            // Exception: in free-target mode the formation frame IS ours --
            // DrivePerFrameCore re-writes it every tick; stripping it here
            // would churn enable/disable every frame.
            if (_sparPinSet || _targetFrameSet || (_tmFormationSet && !_freeTargetActive))
            {
                _lastOp = "ClearTargetFrame(rig)";
                Agent.ClearTargetFrame();
                Agent.DisableScriptedMovement();
                _targetFrameSet = false;
                _sparPinSet = false;
                TorsoMoveCleanup();
                _tmInputVec = Vec2.Zero;
            }

            Agent threat = FindSparThreat();

            // Torso jab at the rigged attacker (identical gating to legacy).
            bool playerEngaged = playerLive
                && HorizontalDistanceTo(player) < MeleeReach() * 1.6f;
            if (threat == null && _jabCooldown <= 0f && atkLive && playerEngaged
                && HorizontalDistanceTo(attacker) < MeleeReach() * 1.3f
                && !OwnInReadyStage())
            {
                _jabCooldown = 5.0f;
                _jabTimer = 1.4f;
            }
            if (_jabTimer > 0f && threat == null && atkLive)
            {
                _rigGoal = Vec2.Zero;   // FIX3: no stale goal driving the legs during jab holds
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

            // PRIMARY: planner picks it (player by default; flank switch when
            // the secondary stays off-face long enough). _forcedDuelTarget
            // and _rigPrimary are ONLY ever the primary -- never a releaser.
            _rigEnemies.Clear();
            _rigAtkTargetsMe = false;
            if (playerLive)
                _rigEnemies.Add((player.Index, player.Position.AsVec2, true,
                    RigIncoming(player, out bool _)));
            if (atkLive)
            {
                bool atkInc = RigIncoming(attacker, out bool atkTm);
                _rigAtkTargetsMe = atkTm;
                _rigEnemies.Add((attacker.Index, attacker.Position.AsVec2, false, atkInc));
            }
            Vec2 facing2 = Agent.Frame.rotation.f.AsVec2;
            _rigPlan = SparRigPlanner.Compute(Agent.Position.AsVec2, facing2,
                _rigEnemies, MeleeReach(), _brainClock, ref _rigState, _rigRanked, out _rigRankedCount);
            if (_rigPlan.SwitchReason != 0)
                _rigSumSwitches++;

            Agent primary = null;
            int prevPrimaryKey = _rigPrimary != null ? _rigPrimary.Index : -1;
            for (int i = 0; i < _rigEnemies.Count; i++)
            {
                if (_rigEnemies[i].key == _rigPlan.PrimaryKey)
                {
                    primary = _rigEnemies[i].isPlayer ? player : attacker;   // A1: no boxing
                    break;
                }
            }
            // Fallback to the live player / attacker when the key resolution failed.
            if (primary == null)
                primary = playerLive ? player : attacker;
            _rigPrimary = primary;
            _forcedDuelTarget = primary;
            // [RIGLOCK]: every primary change attempt -- real switches and
            // denied ones (closer but passive attacker, Session 63).
            if (_rigLockLogCount < MaxRigLockLogs && primary != null
                && (_rigPlan.SwitchReason != 0 || _rigPlan.SwitchBlocked))
            {
                _rigLockLogCount++;
                CombatAiLog.Log($"[RIGLOCK] Agent #{Agent.Index}: primary #{prevPrimaryKey} -> #{primary.Index} " +
                    $"reason={(_rigPlan.SwitchBlocked ? "denied-passive" : _rigPlan.SwitchReason == 2 ? "return" : "near+incoming")} " +
                    $"targetsMe={_rigAtkTargetsMe} theta={_rigPlan.SpreadDeg:F0} " +
                    $"dP={_rigPlan.DistPrimary:F2} dS={_rigPlan.DistSecondary:F2}");
            }
            if (_rigPrimaryLogCount < 100 && primary != null && prevPrimaryKey >= 0 && primary.Index != prevPrimaryKey)
            {
                _rigPrimaryLogCount++;
                CombatAiLog.Log($"[RIGPRIMARY] Agent #{Agent.Index}: primary #{prevPrimaryKey} -> #{primary.Index} " +
                    $"(reason={(_rigPlan.SwitchReason == 2 ? "return" : "near")}) theta={_rigPlan.SpreadDeg:F0} " +
                    $"dP={_rigPlan.DistPrimary:F2} dS={_rigPlan.DistSecondary:F2} secBearing={_rigPlan.SecondaryBearingDeg:F0}");
            }

            // Session 52: cluster bookkeeping for the spar brain.
            if (SparBrainActive)
                TrackSparReleaseEdges(player, attacker);

            // (4) wall/terrain probe + clearance ring over the ranked
            // candidates -> _rigGoal, then the stuck watchdog feeds from the
            // command we actually gave the legs.
            _clear.Tick(Agent, _brainClock);
            // Session 63: the arena pit is not a mission boundary -- if the
            // clearance layer still sees no boundary after its first refresh,
            // synthesize the pit polygon once (then BoundaryDist/BoundaryAwayDir
            // and the fence cap + escape bias work automatically).
            if (!_rigBoundaryDone && _clear.BoundaryDist > 1e30f)
                RigSynthesizeBoundary();
            Vec2 topDir = _rigRankedCount > 0 ? _rigRanked[0].Dir : Vec2.Zero;
            _rigGoal = RigApplyWall();

            // Watchdog: a commanded direction producing <0.15m/0.6s is BLOCKED
            // (learned block, 2s) -- catches stones/props the probe misses.
            Agent.ActionStage ownStage2 = Agent.GetCurrentActionStage(1);
            bool stuckExempt = ownStage2 == Agent.ActionStage.AttackRelease
                || DodgeOwnsBody || ownStage2 == Agent.ActionStage.DefendParry || _handOffToNative;
            if (_clear.Watch.Note(Agent.Position.AsVec2, FlatUnit(_rigGoal), _brainClock, stuckExempt, out Vec2 stuckDir))
            {
                _rigStuckCount++;
                if (_rigStuckLogCount < 40)
                {
                    _rigStuckLogCount++;
                    System.Text.StringBuilder ring = new System.Text.StringBuilder();
                    for (int k = 0; k < 8; k++)
                    {
                        if (k > 0) ring.Append(',');
                        ring.Append(_clear.Ring[k].ToString("F1"));
                    }
                    float bDist = _clear.BoundaryDist;
                    CombatAiLog.Log($"[STUCK] Agent #{Agent.Index}: pos=({Agent.Position.x:F1},{Agent.Position.y:F1}) " +
                        $"cmd=({stuckDir.x:F2},{stuckDir.y:F2}) ring=[{ring}] " +
                        $"free(cmd)={_clear.Free(Agent, stuckDir, _brainClock):F2} blocker={_clear.Blocker(Agent, stuckDir)} " +
                        $"nearestAgent={_clear.NearestAgentInDir(Agent, stuckDir):F2} " +
                        $"boundary={(bDist > 1e30f ? "-1" : bDist.ToString("F2"))}");
                }
            }

            // ESCAPE: every ranked move vetoed/blocked while the planner wanted
            // one for >=1.0s -> 0.8s burst toward the clearance ring's best dir.
            if (_rigGoal.LengthSquared < 0.0001f && topDir.LengthSquared > 0.0001f
                && (_rigRankedCount > 0 && _rigRanked[0].Kind != SparRigPlanner.RigMoveKind.None))
            {
                if (_rigWallZeroSince < 0f)
                    _rigWallZeroSince = _brainClock;
                else if (_brainClock - _rigWallZeroSince >= 1.0f && _brainClock >= _rigEscapeUntil)
                {
                    Vec2 esc = _clear.Escape(primary != null && CombatLive(primary)
                        ? FlatUnit(Agent.Position.AsVec2 - primary.Position.AsVec2) : topDir);
                    if (esc.LengthSquared > 0.0001f)
                    {
                        _rigEscapeUntil = _brainClock + 0.8f;
                        _rigEscapeDir = esc;
                        _rigEscapes++;
                        if (_rigEscLogCount < 60)
                        {
                            _rigEscLogCount++;
                            CombatAiLog.Log($"[RIGESC] Agent #{Agent.Index}: dir=({esc.x:F2},{esc.y:F2}) minRing={_clear.MinRing():F2}");
                        }
                    }
                    _rigWallZeroSince = _brainClock;   // don't re-arm instantly
                }
            }
            else if (_rigGoal.LengthSquared > 0.0001f)
                _rigWallZeroSince = -99f;
            if (_brainClock < _rigEscapeUntil
                && _rigEscapeDir.LengthSquared > 0.0001f
                && !_clear.Watch.IsBlocked(_rigEscapeDir, _brainClock))
                _rigGoal = _rigEscapeDir;

            // [RIGSUM] instrumentation: theta accumulation every rig tick
            // (two live enemies only), plus the 10s summary line.
            _rigSumTicks++;
            if (_rigEnemies.Count > 1)
            {
                if (_rigPlan.SpreadDeg > 85f) _rigSumT85++;
                if (_rigPlan.SpreadDeg > 120f) _rigSumT120++;
            }
            if (_rigPrimary != null && _rigPrimary.IsMainAgent) _rigSumPrim++;
            if (ownStage2 == Agent.ActionStage.AttackReady || ownStage2 == Agent.ActionStage.AttackQuickReady
                || ownStage2 == Agent.ActionStage.AttackRelease) _rigSumLoaded++;
            if (_brainClock - _rigSumLastAt >= 10f)
            {
                _rigSumLastAt = _brainClock;
                CombatAiLog.Log($"[RIGSUM] Agent #{Agent.Index}: ticks={_rigSumTicks} " +
                    $"theta>85={(_rigSumTicks > 0 ? 100f * _rigSumT85 / _rigSumTicks : 0f):F0}% " +
                    $"theta>120={(_rigSumTicks > 0 ? 100f * _rigSumT120 / _rigSumTicks : 0f):F0}% " +
                    $"switches={_rigSumSwitches} sideReleases={_rigSideReleases} sideMisses={_rigSideMisses} " +
                    $"moveNone={(_rigSumTicks > 0 ? 100f * _rigSumMoveNone / _rigSumTicks : 0f):F0}% wallVetoes={_rigSumVetoes} " +
                    $"stuck={_rigStuckCount} escapes={_rigEscapes} minClear={_clear.MinRing():F2} " +
                    $"primaryPlayer={(_rigSumTicks > 0 ? 100f * _rigSumPrim / _rigSumTicks : 0f):F0}% " +
                    $"loaded={(_rigSumTicks > 0 ? 100f * _rigSumLoaded / _rigSumTicks : 0f):F0}% " +
                    $"s2[fall={_rigS2Falls} busy={_rigS2Busy} read={_rigS2Reads} buf={_rigS2Buffer} ign={_rigS2Ignored}] " +
                    $"s1[press={_rigS1Press} bite={_rigS1Bites}]");
                _rigSumTicks = _rigSumT85 = _rigSumT120 = _rigSumSwitches = _rigSumMoveNone = _rigSumVetoes = 0;
                _rigSideReleases = _rigSideMisses = 0;
                _rigStuckCount = _rigEscapes = 0;
                _rigSumPrim = _rigSumLoaded = 0;
                _rigS2Falls = _rigS2Busy = _rigS2Reads = _rigS2Buffer = _rigS2Ignored = 0;
                _rigS1Press = _rigS1Bites = 0;
            }

            Agent.MovementControlFlag fsm = ComputeDuelFlag(dt);
            if (_dodgeTime > 0f)
                return fsm;   // a real dodge step (or block-only hold) owns the return
            return fsm & ~Agent.MovementControlFlag.MoveMask;   // all locomotion is input-channel
        }

        // MissionBehavior -> component accessor for [HIT-ATK] logging.
        public int RigPrimaryIndex => _rigPrimary != null ? _rigPrimary.Index : -1;

        private static Vec2 FlatUnit(Vec2 v)
        {
            if (v.LengthSquared < 0.0001f)
                return Vec2.Zero;
            float l = v.Length;
            return new Vec2(v.x / l, v.y / l);
        }

        // Threat bearing vs the agent's ACTUAL facing: the legs/movement yaw
        // (Agent.GetMovementDirection, the `legs` channel the FACEFRAME log
        // uses), falling back to the body frame forward. Degrees 0..180.
        private float ThreatBearingDeg(Agent threat)
        {
            if (threat == null)
                return 180f;
            Vec2 f = Agent.GetMovementDirection();
            if (f.LengthSquared < 0.0001f)
                f = Agent.Frame.rotation.f.AsVec2;
            Vec2 to = threat.Position.AsVec2 - Agent.Position.AsVec2;
            float cross = f.x * to.y - f.y * to.x;
            float dot = f.x * to.x + f.y * to.y;
            return MathF.Abs(MathF.Atan2(cross, dot)) * 57.2958f;
        }

        // FREE TARGET: the release-block roll for a threat inside the 25deg
        // frontal cone that is NOT the FSM target -- the same functions the
        // locked-target release-edge roll calls (spar brain / group brain
        // arms); the legacy non-brain path is the unconditional block.
        private bool FreeFrontalRollOk(Agent t)
        {
            if (SparBrainActive)
                return SparAttackerBlockRoll(t);
            if (_groupDefender && CombatAiState.GroupFightMode)
                return GroupReleaseBlockRoll(t, false);
            return true;
        }

        // ------------------------------------------------------------------
        // (4) WALL/EDGE PROBE -- FindLongestDirectMoveToPosition, cached 0.1s.
        // ------------------------------------------------------------------
        private float RigFreeDistance(Vec2 dir)
        {
            return _clear.Free(Agent, dir, _brainClock);
        }

        // Walk the planner's ranked candidates; first non-vetoed direction
        // with enough free space wins. None (Dir=Zero) always ends the loop.
        private Vec2 RigApplyWall()
        {
            _rigLastFree = -1f;
            for (int i = 0; i < _rigRankedCount; i++)
            {
                ref SparRigPlanner.RigCand c = ref _rigRanked[i];
                if (c.Kind == SparRigPlanner.RigMoveKind.None || c.Dir.LengthSquared < 0.0001f)
                {
                    _rigSumMoveNone++;
                    return Vec2.Zero;
                }
                if (RigVetoed(c.Dir) || _clear.Watch.IsBlocked(c.Dir, _brainClock))
                {
                    _rigSumVetoes++;
                    continue;
                }
                float free = RigFreeDistance(c.Dir);
                // Forward's required margin scales with the actual gap left to
                // close (the primary parked ~0.6m ahead must not stall the
                // approach); Back/Strafe keep the full 1.0m margin.
                float need = c.Kind == SparRigPlanner.RigMoveKind.Forward
                    ? MathF.Min(RigFreeMin, MathF.Max(0.35f, (_rigPlan.DistPrimary - 0.6f) * 0.8f))
                    : RigFreeMin;
                if (free < need)
                {
                    _rigSumVetoes++;
                    RigVeto(c.Dir, Vec2.Zero);
                    continue;
                }
                // Session 63: nav-path legality -- the direct-move probe
                // smooths along obstacles a real path can't cross, so a goal
                // that only reaches via a >2.5x detour (wall pocket) reads as
                // blocked here. 8-sector 0.2s cache inside AgentClearance.
                if (!_clear.PathOk(Agent, c.Dir, _brainClock))
                {
                    _rigSumVetoes++;
                    RigVeto(c.Dir, Vec2.Zero);
                    continue;
                }
                _rigLastFree = free;
                // Candidate Speed scales the input magnitude (drift = slow).
                float sp = c.Speed > 0.0001f ? MathF.Min(c.Speed, 1f) : 1f;
                return sp >= 0.999f ? c.Dir : new Vec2(c.Dir.x * sp, c.Dir.y * sp);
            }
            return Vec2.Zero;
        }

        private bool RigVetoed(Vec2 dir)
            => _brainClock < _rigVetoUntil
               && dir.x * _rigVetoDir.x + dir.y * _rigVetoDir.y > 0.995f;

        private Vec2 RigVeto(Vec2 dir, Vec2 alt)
        {
            _rigVetoDir = dir;
            _rigVetoUntil = _brainClock + RigVetoSeconds;
            if (_rigWallLogCount < MaxRigWallLogs)
            {
                _rigWallLogCount++;
                CombatAiLog.Log($"[RIGWALL] Agent #{Agent.Index}: VETO dir=({dir.x:F2},{dir.y:F2}) -> ({alt.x:F2},{alt.y:F2})");
            }
            return alt;
        }

        // ------------------------------------------------------------------
        // INCOMING TEST (Session 63) -- the "actually incoming" requirement
        // for the planner's non-player primary switch: the enemy targets us,
        // is closing at >0.3 m/s, or its distance shrank >0.3m over ~1s.
        // GetTargetAgent() reads another agent's target lock (Agent.cs:2873).
        // ------------------------------------------------------------------
        private bool RigIncoming(Agent e, out bool targetsMe)
        {
            targetsMe = false;
            try
            {
                targetsMe = e.GetTargetAgent() == Agent;
                if (targetsMe)
                    return true;
                Vec2 toUs = Agent.Position.AsVec2 - e.Position.AsVec2;
                float d = toUs.Length;
                Vec2 dirToUs = d > 0.0001f ? new Vec2(toUs.x / d, toUs.y / d) : Vec2.Zero;
                return ClearanceMath.Incoming(e.GetCurrentVelocity(), dirToUs,
                    RigDistDelta(e.Index, d));
            }
            catch { return false; }
        }

        // Per-enemy distance sampler (~1s window, 2 slots): returns how much
        // the distance shrank since the last sample once the window is old
        // enough, then refreshes the sample.
        private float RigDistDelta(int key, float d)
        {
            for (int i = 0; i < 2; i++)
            {
                if (_rigDistKey[i] != key)
                    continue;
                float age = _brainClock - _rigDistAt[i];
                float delta = _rigDistPrev[i] - d;
                if (age >= 1.0f)
                {
                    _rigDistPrev[i] = d;
                    _rigDistAt[i] = _brainClock;
                    return delta;
                }
                return age >= 0.8f ? delta : 0f;
            }
            int slot = _rigDistKey[0] == -1 ? 0
                : _rigDistKey[1] == -1 ? 1
                : (_rigDistAt[0] <= _rigDistAt[1] ? 0 : 1);
            _rigDistKey[slot] = key;
            _rigDistPrev[slot] = d;
            _rigDistAt[slot] = _brainClock;
            return 0f;
        }

        // ------------------------------------------------------------------
        // ARENA BOUNDARY SYNTHESIS (Session 63) -- the arena pit is not a
        // mission boundary, so GetClosestBoundaryPosition returns invalid and
        // the clearance boundary channel is blind. Build the pit polygon
        // once per mission: the scene's hard-boundary polyline when present
        // (Scene.GetHardBoundaryVertexCount/GetHardBoundaryVertex, Scene.cs:
        // 1519-1531), else the XY convex hull of the sp_arena /
        // sp_arena_respawn spawn frames inflated 1m outward. Boundaries.Add
        // (name, points, isAllowanceInside:true -- decompiled Mission.cs:193)
        // marks the interior as allowed space; it throws on a duplicate name
        // so ContainsKey is checked first.
        // ------------------------------------------------------------------
        private void RigSynthesizeBoundary()
        {
            _rigBoundaryDone = true;
            string source = "none";
            int verts = 0;
            float sanity = -1f;
            try
            {
                Mission m = Agent.Mission != null ? Agent.Mission : Mission.Current;
                if (m != null && m.Scene != null)
                {
                    if (m.Boundaries.ContainsKey("combatai_pit"))
                        source = "exists";
                    else
                    {
                        List<Vec2> poly = null;
                        int hc = m.Scene.GetHardBoundaryVertexCount();
                        if (hc >= 3)
                        {
                            poly = new List<Vec2>(hc);
                            for (int i = 0; i < hc; i++)
                                poly.Add(m.Scene.GetHardBoundaryVertex(i));
                            source = "hardverts";
                        }
                        else
                        {
                            List<Vec2> seeds = new List<Vec2>(16);
                            foreach (GameEntity e in m.Scene.FindEntitiesWithTag("sp_arena"))
                                seeds.Add(e.GlobalPosition.AsVec2);
                            foreach (GameEntity e in m.Scene.FindEntitiesWithTag("sp_arena_respawn"))
                                seeds.Add(e.GlobalPosition.AsVec2);
                            if (seeds.Count >= 3)
                            {
                                List<Vec2> hull = ClearanceMath.ConvexHull(seeds);
                                if (hull != null && hull.Count >= 3)
                                {
                                    poly = ClearanceMath.InflateHull(hull, 1.0f);
                                    source = "spawnhull";
                                }
                            }
                        }
                        if (poly != null && poly.Count >= 3)
                        {
                            m.Boundaries.Add("combatai_pit", poly, isAllowanceInside: true);
                            verts = poly.Count;
                        }
                    }
                    Vec2 cp = m.GetClosestBoundaryPosition(Agent.Position.AsVec2);
                    Vec2 dv = Agent.Position.AsVec2 - cp;
                    sanity = dv.Length;
                }
            }
            catch (Exception e)
            {
                source = "err:" + e.GetType().Name;
            }
            if (_rigBoundaryLogCount < MaxRigBoundaryLogs)
            {
                _rigBoundaryLogCount++;
                CombatAiLog.Log($"[BOUNDARY] Agent #{Agent.Index}: source={source} verts={verts} " +
                    $"closestDist={(sanity < 0f ? "nan" : sanity.ToString("F2"))}");
            }
        }

        // ------------------------------------------------------------------
        // (6) BLOCK DIRECTION RULES -- shield + attacker side aware.
        // ------------------------------------------------------------------
        private bool RigShield()
        {
            try
            {
                EquipmentIndex idx = Agent.GetOffhandWieldedItemIndex();
                return idx != EquipmentIndex.None && Agent.Equipment[(int)idx].IsShield();
            }
            catch { return false; }
        }

        private Agent.MovementControlFlag BlockFlagVs(Agent threat, Agent.UsageDirection dir)
        {
            bool rig = CombatAiState.SparRig && CombatAiState.SparMode
                && Agent.Index == CombatAiState.SparDefenderIndex && !Agent.HasMount
                && threat != null;
            // Session 64: the same shield + attacker-side rules generalized to
            // GROUP-mode defenders (N threats). "Primary" there is the front
            // lock (_duelTarget); non-locked side attackers get shield->side /
            // no-shield->mirror-by-line rules, the locked target keeps plain
            // MirrorDefend (unchanged mirror rules).
            bool group = !rig && CombatAiState.GroupFightMode && _useAssignedThreats && threat != null;
            if (!rig && !group)
                return MirrorDefend(dir);
            if (dir == Agent.UsageDirection.None || dir == Agent.UsageDirection.AttackEnd)
            {
                if (rig)
                    LogRigDef(threat, false, 0, dir, Agent.MovementControlFlag.None);
                return Agent.MovementControlFlag.None;
            }
            bool shield = RigShield();
            Vec2 f = Agent.Frame.rotation.f.AsVec2;
            Vec2 to = threat.Position.AsVec2 - Agent.Position.AsVec2;
            float cross = f.x * to.y - f.y * to.x;
            float dot = f.x * to.x + f.y * to.y;
            float bear = MathF.Abs(MathF.Atan2(cross, dot)) * 57.2958f;
            int side = bear < 20f ? 0 : cross > 0f ? 1 : -1;   // +1 = target on our LEFT
            // FREE TARGET: there is no lock to compare against -- "primary" is
            // whoever sits inside the 25-degree frontal cone of our ACTUAL
            // facing (legs/movement yaw). Wider bearings keep the existing
            // side rules (the |theta|>90 case deliberately reuses them -- the
            // side-threat roll is already the worst-case path).
            bool isPrimary = _freeTargetActive
                ? ThreatBearingDeg(threat) <= CombatAiState.FreeFrontalDeg
                : rig
                    ? (ReferenceEquals(threat, _rigPrimary) || _rigPrimary == null)
                    : (ReferenceEquals(threat, _duelTarget) || _duelTarget == null);
            Agent.MovementControlFlag flag;
            if (isPrimary || side == 0)
            {
                if (rig && shield)
                {
                    flag = dir == Agent.UsageDirection.AttackUp ? Agent.MovementControlFlag.DefendUp
                        : dir == Agent.UsageDirection.AttackDown ? Agent.MovementControlFlag.DefendDown
                        : Agent.MovementControlFlag.DefendDown;   // big shield covers lateral swings low
                }
                else
                    flag = MirrorDefend(dir);   // locked target: unchanged mirror rules
            }
            else
            {
                if (shield)
                    flag = side > 0 ? Agent.MovementControlFlag.DefendLeft : Agent.MovementControlFlag.DefendRight;
                else
                    flag = dir == Agent.UsageDirection.AttackUp ? Agent.MovementControlFlag.DefendUp
                        : dir == Agent.UsageDirection.AttackDown ? Agent.MovementControlFlag.DefendDown
                        : (side > 0 ? Agent.MovementControlFlag.DefendLeft : Agent.MovementControlFlag.DefendRight);
            }
            if (rig)
                LogRigDef(threat, shield, side, dir, flag);
            return flag;
        }

        private void LogRigDef(Agent threat, bool shield, int side, Agent.UsageDirection dir, Agent.MovementControlFlag flag)
        {
            if (_rigDefLogCount >= MaxRigDefLogs)
                return;
            // Dedupe: a release logs every tick -- same (threat,side,flag)
            // within 0.5s is one block decision, not 20 lines.
            int key = threat != null ? threat.Index : -99;
            if (key == _rigLastDefThreat && side == _rigLastDefSide && flag == _rigLastDefFlag
                && _brainClock - _rigLastDefAt < 0.5f)
                return;
            _rigLastDefThreat = key;
            _rigLastDefSide = side;
            _rigLastDefFlag = flag;
            _rigLastDefAt = _brainClock;
            _rigDefLogCount++;
            CombatAiLog.Log($"[RIGDEF] Agent #{Agent.Index}: threat=#{(threat != null ? threat.Index : -1)} " +
                $"role={(ReferenceEquals(threat, _rigPrimary) || _rigPrimary == null ? "primary" : "secondary")} " +
                $"shield={shield} side={side} dir={dir} -> {flag}");
        }
    }
}
