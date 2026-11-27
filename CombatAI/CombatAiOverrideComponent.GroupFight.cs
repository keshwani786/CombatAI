using System;
using System.Collections.Generic;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // Stage-2 free-target group fight: per-agent flag fn. Threats = up to 3
    // close enemies scored by intent/proximity/facing; defense reads the
    // GroupEngagement registry instantly (never the native target slot);
    // attacks go through the duel FSM with the StartDuelAttack gate.
    public partial class CombatAiOverrideComponent
    {
        private readonly List<Agent> _gfThreats = new List<Agent>(4);
        private readonly List<float> _gfThreatW = new List<float>(4);
        private readonly List<GroupEngagement.Intent> _gfIncoming = new List<GroupEngagement.Intent>(4);
        private float _gfThreatAt = -99f, _gfMemberAt = -99f;
        private int _gthreatLogCount, _gviewLogCount, _gfightLogCount;
        private float _gthreatLastAt = -99f, _gviewLastAt = -99f, _gfightLastAt = -99f;
        private const int MaxGthreatLogs = 400, MaxGviewLogs = 100, MaxGfightLogs = 60;
        private bool _gfReturning, _gfHomeSet;
        private Vec2 _gfHome;
        private Agent _gfPair;                       // pair fight: the ACTIVE target (anchor unless switched)
        private float _gfInViewSum;
        private int _gfInViewN;

        // Group-fight phase flag shared by both stage-2 paths: the pair
        // fight's defense reads the ATTACKER's combat lock ("target info is
        // enough", no engagement registry); the free-fallback keeps the
        // claim check as the OR inside FindSparThreat's group branch.
        private bool GroupLockDefense => CombatAiState.GroupFightMode && CombatAiState.GroupFree
            && GroupLineup.Phase == GroupLineup.Ph.Fight;

        // Group fighter = the SPAR RIG stack with N threats: shared duel FSM
        // for attack/defense (spar side-threat finder + frontal/side rolls),
        // SparRigPlanner for movement (top-2 threats), the same clearance /
        // stuck / escape layer, and the lineup slot as a leash on the goal.
        private Agent.MovementControlFlag ComputeGroupFreeFightFlag(float dt)
        {
            _brainClock += dt;
            Mission m = Agent.Mission;
            if (m == null) return Agent.MovementControlFlag.None;
            // Steered pair fight (user spec): permanent counterpart lock +
            // native-steer facing. The free registry path below stays as the
            // fail-closed fallback for when the steer hook can't arm.
            if (_steerGroup)
                return ComputeGroupPairFlag(dt, m);
            GroupEngagement.Housekeep(m, _brainClock);
            GroupEngagement.IncomingOn(m, Agent.Index, _brainClock, _gfIncoming);
            if (_brainClock - _gfThreatAt >= 0.2f)
            {
                _gfThreatAt = _brainClock;
                UpdateThreats(m);
            }

            // Spar-rig mode flags (the caller set _useAssignedThreats=true for
            // the legacy brain -- override: defense goes through FindSparThreat).
            _rigDriving = true;
            _sparThreats = true;
            _useAssignedThreats = false;
            _groupDefender = true;
            _pureReactiveDefense = false;
            _legPinOwned = false;
            GroupTrackReleaseEdges(m);
            int liveIncoming = _gfIncoming.Count;
            _multiEnemy = _gfThreats.Count > 1 || liveIncoming > 1;
            _feintRateScale = liveIncoming > 1 ? 0.5f : 1f;

            // ---------- home slot / leash ----------
            Vec2 pos = Agent.Position.AsVec2;
            Vec2 home = _gfHomeSet ? _gfHome : pos;
            if (GroupLineup.GoalOf(Agent.Index, out Vec2 hg))
            {
                Vec2 fd = GroupLineup.FacingDir(Agent.Index);
                float shift = MathF.Max(0f, (CombatAiState.GroupGapMeters - CombatAiState.GroupFightGapMeters) * 0.5f);
                home = fd.LengthSquared > 0.0001f ? hg + FlatUnit(fd) * shift : hg;
                _gfHome = home; _gfHomeSet = true;
            }
            else if (!_gfHomeSet) { _gfHome = pos; _gfHomeSet = true; home = pos; }
            float leash = CombatAiState.GroupLeashMeters;
            float dHome = (pos - home).Length;
            if (dHome > leash) _gfReturning = true;
            else if (dHome < 0.35f) _gfReturning = false;

            // ---------- planner (top-2 threats) ----------
            _rigEnemies.Clear();
            for (int i = 0; i < _gfThreats.Count && _rigEnemies.Count < 2; i++)
            {
                Agent t = _gfThreats[i];
                if (!CombatLive(t)) continue;
                bool inc = GroupAimedAtMe(t) || RigIncoming(t, out bool _);
                _rigEnemies.Add((t.Index, t.Position.AsVec2, false, inc));
            }
            Vec2 face2 = Agent.GetMovementDirection();
            if (face2.LengthSquared < 0.0001f) face2 = Agent.Frame.rotation.f.AsVec2;
            _rigPlan = SparRigPlanner.Compute(pos, face2, _rigEnemies, MeleeReach(), _brainClock,
                ref _rigState, _rigRanked, out _rigRankedCount);
            Agent primary = null;
            for (int i = 0; i < _gfThreats.Count; i++)
                if (_gfThreats[i].Index == _rigPlan.PrimaryKey && CombatLive(_gfThreats[i]))
                { primary = _gfThreats[i]; break; }
            _rigPrimary = primary;

            // ---------- attack target (any threat in reach, 90deg cone, lane clear) ----------
            if (_duelState != DuelState.Approach && CombatLive(_forcedDuelTarget))
            { }   // mid-chain: keep the committed target so the FSM can finish
            else
                _forcedDuelTarget = _gfReturning ? null : PickAttackTarget();
            if (_forcedDuelTarget == null && _duelState == DuelState.Approach)
                EnsureTargetAgent(null);   // nothing to swing at: drop any stale lock

            // ---------- surroundings (clearance ring, wall veto, stuck, escape) ----------
            GroupSurroundings(primary);

            // ---------- line hold on top of the rig goal ----------
            if (_gfReturning)
            {
                Vec2 back = home - pos;
                _rigGoal = back.LengthSquared > 0.0001f
                    ? FlatUnit(back) * MathF.Min(1f, back.Length / 1.0f) : Vec2.Zero;
            }
            else if (_gfThreats.Count == 0)
                _rigGoal = dHome > 0.35f ? FlatUnit(home - pos) * MathF.Min(1f, dHome) : Vec2.Zero;
            else if (_rigGoal.LengthSquared > 0.0001f)
            {
                Vec2 next = pos + _rigGoal * 0.5f;
                if ((next - home).Length > leash && (next - home).Length > dHome)
                    _rigGoal = Vec2.Zero;
            }

            GThreatLog(m);
            GViewSample(m);
            GFightLog(m);
            GRigLog(dHome);

            Agent.MovementControlFlag fsm = _forcedDuelTarget != null
                ? ComputeDuelFlag(dt)
                : Agent.MovementControlFlag.None;
            if (_dodgeTime > 0f)
                return fsm;
            return fsm & ~Agent.MovementControlFlag.MoveMask;   // all locomotion is input-channel (_rigGoal)
        }

        // =====================================================================
        // PAIR FIGHT (user spec, steer path): distributed target + threat
        // free-look. Each agent keeps ONE combat lock -- the ACTIVE target:
        // its GroupTargeting anchor (order-preserving counterpart across
        // the enemy line) unless a closer enemy earns a switch or the local
        // leash pulls it back; it may return to the anchor after a hit.
        // Facing comes from the native steer channel aimed at _duelTarget
        // (the registration block in DrivePerFrameCore) -- face-the-lock
        // while attacking, else the median bearing of the close-threat set
        // (GroupComputeLook -> _gfLookDir). Attack/block go through the
        // standard duel FSM with the steer side-match + live side re-eval
        // the spar defender already uses. Legs: single-enemy rig plan +
        // the same clearance/stuck/escape layer, all on the input channel.
        // =====================================================================
        private Agent.MovementControlFlag ComputeGroupPairFlag(float dt, Mission m)
        {
            _rigDriving = true;
            _sparThreats = true;   // side-threat scan -> FindSparThreat (lock-aim group branch)
            _useAssignedThreats = false;
            _groupDefender = false;
            _pureReactiveDefense = false;
            _legPinOwned = false;
            _multiEnemy = false;
            _feintRateScale = 1f;
            // gtnative: native keeps its own auto-selection on (its pick feeds
            // the weighted pick); anchor mode disables it (the lock is ours).
            if (!CombatAiState.GroupNativeTarget)
                SetAutoTargeting(false);

            // ---------- the active target: weighted pick + threat look ----------
            // gtnative (4r): WE own the lock -- GroupPickWeighted scores the
            // native pick against the close-threat set (proximity /
            // targeting-me / 2-attacker cap) and chooses; native auto-select
            // stays ON so its pick is only a candidate, anchor pick is the
            // fallback. GroupComputeLook arms the free-look heading for the
            // steer block (dissolved while attacking, aimed at the worst
            // threat while exposed).
            if (CombatAiState.GroupNativeTarget)
            {
                GroupThreatScan(m);   // scan first: the weighted pick reads _gfLookScan/_gfNearest
                Agent nativePick = null;
                try { nativePick = Agent.GetTargetAgent(); }
                catch { }
                _gfPair = GfTargetTick(m, nativePick);   // 4x layer pick (defender/quick) or baseline weights
            }
            else
                _gfPair = GroupPickTarget(m);
            GroupComputeLook(m, dt);
            // Mid-chain commits keep their target so the FSM can finish the
            // swing; a dead target always re-resolves.
            if (_duelState == DuelState.Approach || !CombatLive(_forcedDuelTarget))
                _forcedDuelTarget = _gfPair;
            _duelTarget = _forcedDuelTarget;   // steer heading reads this before the FSM does
            // Permanent lock -- asserted EVERY tick, and never a null write
            // here: a dead lock self-heals on the next positive write while a
            // null write is the active-melee crash window (research 12).
            if (_gfPair != null)
                EnsureTargetAgent(_gfPair);

            // ---------- legs: MARCH TO THE PAIR, nothing else ----------
            // Native model (research 11): walls/props hard-veto via the fit
            // probe, but agent BODIES are never walls -- native sidesteps
            // around them (sidestep sampling + capsule slide). Our ClampFree
            // treated every nearby body as a hard cap, which is why 5v5
            // pairs never reached each other (ai40 log: kind=Forward
            // goal=(0,0) for 50s). March = straight at the pair; a body in
            // the way -> latched sidestep on the freer side; a wall pocket
            // -> Zero (surroundings escape takes it); inside the melee band
            // -> Zero (the FSM owns the fight distance).
            Vec2 pos = Agent.Position.AsVec2;
            Vec2 toP = CombatLive(_gfPair) ? _gfPair.Position.AsVec2 - pos : Vec2.Zero;
            float pd = toP.Length;
            float reach = MeleeReach();
            _rigEnemies.Clear();
            if (CombatLive(_gfPair))
                _rigEnemies.Add((_gfPair.Index, _gfPair.Position.AsVec2, false, RigIncoming(_gfPair, out bool _)));
            // gtnative: also feed the close threats into the rig so its band
            // movement reduces the threat spread (keeps them in view while
            // fighting), not just the single pair. _gfPair can be null here
            // (native pick absent + anchor fallback found nobody -- crash fix).
            if (CombatAiState.GroupNativeTarget && CombatLive(_gfPair))
            {
                for (int ti = 0; ti < _gfLook.Count && _rigEnemies.Count < 2; ti++)
                {
                    Agent t = _gfLook[ti];
                    if (t != null && t.Index != _gfPair.Index && CombatLive(t))
                        _rigEnemies.Add((t.Index, t.Position.AsVec2, false, RigIncoming(t, out bool _)));
                }
            }
            Vec2 face2 = Agent.GetMovementDirection();
            if (face2.LengthSquared < 0.0001f) face2 = Agent.Frame.rotation.f.AsVec2;
            _rigPlan = SparRigPlanner.Compute(pos, face2, _rigEnemies, reach, _brainClock,
                ref _rigState, _rigRanked, out _rigRankedCount);
            _rigPrimary = CombatLive(_gfPair) ? _gfPair : null;
            if (CombatAiState.GroupNativeTarget && CombatAiState.GfNativemove)
            {
                // 4x native-move: legs belong to the engine. While the layer
                // steers our facing we replay native's last locomotion
                // INTENT (world frame, captured in OnAIInputSetCore) through
                // the rig writer so the MovementInputVector is expressed in
                // the CURRENT body frame -- otherwise the detoured body
                // would walk native's stale local vector. Layer off: no
                // _rigGoal, no writer, pure native window.
                // 4aa rigged 2v1 (rig2v1): exactly-two-attackers case. The
                // DEFENDER keeps both attackers inside +-RigViewDeg of its
                // body facing (else steps back/out of the line); each
                // ATTACKER orbits to stay in the defender's view. Both
                // REPLACE native movement -- not blended -- through the same
                // rig writer, released exactly like the translator.
                bool attacking = _duelState == DuelState.Windup
                    || _duelState == DuelState.FeintReady
                    || _duelState == DuelState.FeintCancel
                    || _duelState == DuelState.Release;
                bool rigDef = CombatAiState.GfRig2v1 && _gfDefenderActive
                    && _gfAttOnMe == 2 && _gfAtk.Count >= 2
                    && CombatLive(_gfAtk[0]) && CombatLive(_gfAtk[1]);
                Agent rigD = null, rigOther = null;
                if (!rigDef && CombatAiState.GfRig2v1 && !_gfLayerActive
                    && CombatLive(_gfPair) && !GfAssign.ContainsKey(Agent.Index)
                    && GfEnemyOn.TryGetValue(_gfPair.Index, out int te2) && te2 == 2)
                {
                    foreach (Agent a2v in m.Agents)
                    {
                        if (!CombatLive(a2v) || a2v == Agent || !a2v.IsHuman
                            || Agent.IsEnemyOf(a2v))
                            continue;
                        Agent lt = null;
                        try { lt = a2v.GetTargetAgent(); } catch { }
                        if (lt == _gfPair) { rigOther = a2v; rigD = _gfPair; break; }
                    }
                }
                bool rigAtk = rigOther != null;
                _gfRigged = (rigDef || rigAtk) && !attacking;
                int rigRoleNow = rigDef ? 1 : rigAtk ? 2 : 0;
                if (rigRoleNow != _gfRigRole)
                {
                    _gfRigRole = rigRoleNow;
                    GFsmLog(m, rigRoleNow == 0 ? "rig-off"
                        : rigRoleNow == 1 ? "rig-on defender" : "rig-on attacker",
                        _gfPair, rigDef && _gfAtk.Count > 0 ? _gfAtk[0] : rigD);
                }
                _gfTranslating = _gfLayerActive && !_gfRigged;
                Vec2 intent = _brainClock - _nativeWorldMoveAt <= IntentExpireSeconds
                    ? _nativeWorldMove : Vec2.Zero;
                _rigGoal = _gfTranslating ? intent : Vec2.Zero;
                if (_gfRigged && rigDef)
                {
                    _gsumRigDef++;
                    _clear.Tick(Agent, _brainClock);
                    Vec2 fu = FlatUnit(face2);
                    Vec2 w1 = _gfAtk[0].Position.AsVec2 - pos;
                    Vec2 w2 = _gfAtk[1].Position.AsVec2 - pos;
                    float l1 = w1.Length, l2 = w2.Length;
                    if (fu.LengthSquared > 0.0001f && l1 > 0.0001f && l2 > 0.0001f)
                    {
                        Vec2 a1 = new Vec2(w1.x / l1, w1.y / l1);
                        Vec2 a2 = new Vec2(w2.x / l2, w2.y / l2);
                        float b1 = MathF.Acos(MathF.Max(-1f, MathF.Min(1f, Vec2.DotProduct(fu, a1)))) * 57.2958f;
                        float b2 = MathF.Acos(MathF.Max(-1f, MathF.Min(1f, Vec2.DotProduct(fu, a2)))) * 57.2958f;
                        if (b1 <= RigViewDeg && b2 <= RigViewDeg)
                        {
                            // both in view: hold ground; re-close when the
                            // nearest attacker drifts beyond the melee band
                            Vec2 near = l1 <= l2 ? a1 : a2;
                            _rigGoal = MathF.Min(l1, l2) > 1.0f * reach
                                ? near * 0.6f : Vec2.Zero;
                        }
                        else
                        {
                            Vec2 away = FlatUnit(new Vec2(-(a1.x + a2.x), -(a1.y + a2.y)));
                            float spr = MathF.Acos(MathF.Max(-1f, MathF.Min(1f, Vec2.DotProduct(a1, a2)))) * 57.2958f;
                            if (spr > DefSpreadDeg)
                            {
                                Vec2 line = _gfAtk[1].Position.AsVec2 - _gfAtk[0].Position.AsVec2;
                                if (line.LengthSquared > 0.0001f)
                                {
                                    Vec2 lu = FlatUnit(line);
                                    Vec2 pp = new Vec2(-lu.y, lu.x);
                                    Vec2 ppN = new Vec2(lu.y, -lu.x);
                                    int sd = GfPickStepSide(pp, ppN, intent);
                                    Vec2 step = new Vec2(pp.x * sd, pp.y * sd);
                                    Vec2 mix = new Vec2(away.x * 0.5f + step.x * 0.5f,
                                                        away.y * 0.5f + step.y * 0.5f);
                                    _rigGoal = mix.LengthSquared > 0.0001f
                                        ? FlatUnit(mix) * 0.8f : Vec2.Zero;
                                }
                                else
                                    _rigGoal = away * 0.8f;
                            }
                            else
                                _rigGoal = away * 0.8f;
                        }
                        _rigGoal = GfRigCheck(_rigGoal);
                    }
                }
                else if (_gfRigged && rigAtk)
                {
                    _gsumRigAtk++;
                    _clear.Tick(Agent, _brainClock);
                    Vec2 df = rigD.GetMovementDirection();
                    if (df.LengthSquared < 0.0001f) df = rigD.Frame.rotation.f.AsVec2;
                    Vec2 dm = pos - rigD.Position.AsVec2;
                    float dd = dm.Length;
                    Vec2 dov = rigOther.Position.AsVec2 - rigD.Position.AsVec2;
                    float odv = dov.Length;
                    if (df.LengthSquared > 0.0001f && dd > 0.0001f)
                    {
                        Vec2 fu = FlatUnit(df);
                        Vec2 mu = new Vec2(dm.x / dd, dm.y / dd);
                        float bi = MathF.Atan2(fu.x * mu.y - fu.y * mu.x,
                            fu.x * mu.x + fu.y * mu.y) * 57.2958f;
                        float bo = 0f, sep = 180f;
                        if (odv > 0.0001f)
                        {
                            Vec2 ou = new Vec2(dov.x / odv, dov.y / odv);
                            bo = MathF.Atan2(fu.x * ou.y - fu.y * ou.x,
                                fu.x * ou.x + fu.y * ou.y) * 57.2958f;
                            sep = MathF.Acos(MathF.Max(-1f, MathF.Min(1f,
                                Vec2.DotProduct(mu, ou)))) * 57.2958f;
                        }
                        Vec2 goal;
                        if (MathF.Abs(bi) > RigViewDeg)
                            goal = (bi > 0f ? new Vec2(mu.y, -mu.x)
                                : new Vec2(-mu.y, mu.x)) * RigMag;   // orbit toward D's front
                        else if (sep < 20f && MathF.Abs(bi) > MathF.Abs(bo))
                            goal = (bi > 0f ? new Vec2(-mu.y, mu.x)
                                : new Vec2(mu.y, -mu.x)) * RigMag;   // open ~35deg of separation
                        else if (dd > 0.95f * reach)
                            goal = PairMarchGoal(toP, pd, reach);
                        else
                            goal = Vec2.Zero;
                        _rigGoal = GfRigCheck(goal);
                    }
                }
                // 4ab closing assist (assist): native parks at ITS engagement
                // distance (~1.4-1.7m) while the FSM refuses to swing beyond
                // reach -- a stalled Approach gets the march goal through the
                // same writer. Arms at >0.95*reach, releases below 0.9*reach
                // (or out of Approach) so the edge never oscillates.
                float spd = Agent.GetCurrentVelocity().Length;
                if (_duelState != DuelState.Approach || spd >= AssistSpeed * 2f)
                    _gfStallSince = -1f;
                else if (spd < AssistSpeed && _gfStallSince < 0f)
                    _gfStallSince = _brainClock;
                if (CombatAiState.GfAssist && !_gfRigged && !_gfDefFixOn
                    && _duelState == DuelState.Approach && CombatLive(_gfPair)
                    && pd > 0.95f * reach && pd <= AssistMaxR * reach
                    && ((_gfStallSince >= 0f && _brainClock - _gfStallSince >= AssistStallSeconds)
                        || _duelTimer > AssistTimerSeconds))
                    _gfAssist = true;
                if (_gfAssist)
                {
                    if (_duelState == DuelState.Approach && CombatLive(_gfPair)
                        && pd > AssistReleaseR * reach)
                    {
                        _gfRigged = true;
                        _gfTranslating = false;
                        _clear.Tick(Agent, _brainClock);
                        _rigGoal = PairMarchGoal(toP, pd, reach);
                        _gsumAssist++;
                    }
                    else
                        _gfAssist = false;
                }
                // stalled metric + [STALL] diagnostic (the bug this fixes)
                bool outReach = _duelState == DuelState.Approach && CombatLive(_gfPair)
                    && pd > reach && pd <= AssistMaxR * reach;
                if (outReach && spd < AssistSpeed)
                    _gsumStalled++;
                if (outReach)
                {
                    if (_gfOutReachSince < 0f)
                        _gfOutReachSince = _brainClock;
                    if (_brainClock - _gfOutReachSince > 4f && _gsumStallLogs < MaxGfStallLogs
                        && _brainClock - _gfStallLogAt >= 5f)
                    {
                        _gfStallLogAt = _brainClock;
                        _gsumStallLogs++;
                        Agent nl = null;
                        try { nl = Agent.GetTargetAgent(); } catch { }
                        CombatAiLog.Log($"[STALL] Agent #{Agent.Index}: pair=#{_gfPair.Index} " +
                            $"pd={pd:F2} reach={reach:F2} speed={spd:F2} timer={_duelTimer:F1} " +
                            $"assist={(_gfAssist ? 1 : 0)} lock=#{(nl != null ? nl.Index : -1)} " +
                            $"steer={(_steerGroup ? 1 : 0)} layer={(_gfLayerActive ? 1 : 0)}");
                    }
                }
                else
                    _gfOutReachSince = -1f;

                // 4z defender step-out (defmove): caught BETWEEN two
                // attackers (spread > 110deg) -> slide out of their line.
                // Hysteresis: keep correcting until the spread closes under
                // 80deg; the step side latches 0.6s. Quick excursions and
                // narrow spreads keep the pure translated intent.
                if (CombatAiState.GfDefmove && _gfDefenderActive && !_gfQuickOn && !rigDef
                    && _gfAtk.Count >= 2 && CombatLive(_gfAtk[0]) && CombatLive(_gfAtk[1]))
                {
                    Vec2 w1 = _gfAtk[0].Position.AsVec2 - pos;
                    Vec2 w2 = _gfAtk[1].Position.AsVec2 - pos;
                    float l1 = w1.Length, l2 = w2.Length;
                    if (l1 > 0.0001f && l2 > 0.0001f)
                    {
                        Vec2 a1 = new Vec2(w1.x / l1, w1.y / l1);
                        Vec2 a2 = new Vec2(w2.x / l2, w2.y / l2);
                        float cs = Vec2.DotProduct(a1, a2);
                        if (cs > 1f) cs = 1f; else if (cs < -1f) cs = -1f;
                        float spreadDeg = MathF.Acos(cs) * 57.2958f;
                        if (spreadDeg > DefSpreadDeg)
                        {
                            _gsumSpread110++;
                            _gfDefFixOn = true;
                        }
                        else if (spreadDeg < DefSpreadEndDeg)
                            _gfDefFixOn = false;
                        if (_gfDefFixOn)
                        {
                            // "Runs along just one side": intent mostly
                            // tangential to the attackers' centroid dir gets
                            // damped before blending.
                            Vec2 cen = a1 + a2;
                            if (spreadDeg > 90f && intent.LengthSquared > 0.0001f
                                && cen.LengthSquared > 0.0001f
                                && MathF.Abs(Vec2.DotProduct(intent, FlatUnit(cen))) < 0.3f)
                                intent = new Vec2(intent.x * DefFixDamp, intent.y * DefFixDamp);
                            Vec2 line = _gfAtk[1].Position.AsVec2 - _gfAtk[0].Position.AsVec2;
                            if (line.LengthSquared > 0.0001f)
                            {
                                Vec2 lu = FlatUnit(line);
                                Vec2 pp = new Vec2(-lu.y, lu.x);   // perp to the A1->A2 line
                                Vec2 ppN = new Vec2(lu.y, -lu.x);
                                if (_brainClock >= _gfDefFixSideUntil)
                                {
                                    float fp = _clear.NearestAgentInDir(Agent, pp, nonEnemiesOnly: true);
                                    float fn = _clear.NearestAgentInDir(Agent, ppN, nonEnemiesOnly: true);
                                    bool posOk = (fp > 1f || fp <= 0f)
                                        && _clear.WallFree(Agent, pp, _brainClock) >= 0.8f;
                                    bool negOk = (fn > 1f || fn <= 0f)
                                        && _clear.WallFree(Agent, ppN, _brainClock) >= 0.8f;
                                    int side;
                                    if (posOk != negOk)
                                        side = posOk ? 1 : -1;
                                    else if (intent.LengthSquared > 0.0001f)
                                        side = Vec2.DotProduct(intent, pp) >= 0f ? 1 : -1;
                                    else
                                    {
                                        Vec2 ad = Vec2.Zero;
                                        foreach (Agent al in m.Agents)
                                        {
                                            if (!CombatLive(al) || al == Agent || !al.IsHuman
                                                || Agent.IsEnemyOf(al))
                                                continue;
                                            Vec2 rel = al.Position.AsVec2 - pos;
                                            ad = new Vec2(ad.x + rel.x, ad.y + rel.y);
                                        }
                                        side = ad.LengthSquared > 0.0001f
                                            ? (Vec2.DotProduct(FlatUnit(ad), pp) >= 0f ? 1 : -1)
                                            : _gfDefFixSide;
                                    }
                                    _gfDefFixSide = side;
                                    _gfDefFixSideUntil = _brainClock + DefFixSideLatch;
                                }
                                Vec2 step = new Vec2(pp.x * _gfDefFixSide, pp.y * _gfDefFixSide);
                                Vec2 mix = new Vec2(step.x * DefFixBlend + intent.x * 0.4f,
                                                    step.y * DefFixBlend + intent.y * 0.4f);
                                _rigGoal = mix.LengthSquared > 0.0001f
                                    ? FlatUnit(mix) * DefFixMag : Vec2.Zero;
                                _gsumDefFix++;
                            }
                        }
                    }
                }
                _pairWantsMove = false;
            }
            else
            {
                // nativemove OFF fallback + the anchor (non-gtnative) path:
                // the a069e7f march, nothing else.
                _gfTranslating = false;
                _rigGoal = PairMarchGoal(toP, pd, reach);
                GroupSurroundings(_gfPair, false);
            }
            for (int i = 0; i < _gfLookScan.Count; i++)
                GfNoteReadyStage(_gfLookScan[i].e);   // 4z held-attack classifier feed
            GfNoteReadyStage(_gfPair);
            if (CombatAiState.GroupNativeTarget)
            {
                _gsumTicks++;
                if (_gfDefenderActive) _gsumDefender++;
                // 4y greedy-2v1 metric: in contact, mobbing an enemy an ally
                // already fights while an attacker-on-me is unaddressed.
                if (_gfThreatNear && CombatLive(_gfPair) && pd <= 1.5f * reach
                    && AlliesLockedOn(m, _gfPair) >= 1)
                    _gsumGreedy2v1++;
                GTnLog(nativePickSeen: _gfPair != null && _gfLookValid);
            }

            GPairLog();

            Agent.MovementControlFlag fsm = CombatLive(_gfPair)
                ? ComputeDuelFlag(dt)
                : Agent.MovementControlFlag.None;
            if (_dodgeTime > 0f)
                return fsm;
            return fsm & ~Agent.MovementControlFlag.MoveMask;   // all locomotion is input-channel (_rigGoal)
        }

        // Fallback pick for when the anchor registry has nobody for us
        // (Assign returned null, e.g. mid-melee churn): the nearest live
        // enemy. The lineup counterpart is gone -- GroupTargeting owns the
        // distributed assignment now.
        private Agent ResolveGroupPair(Mission m)
        {
            Agent best = null;
            float bd = float.MaxValue;
            foreach (Agent a in m.Agents)
            {
                if (!CombatLive(a) || a == Agent || !a.IsHuman || !Agent.IsEnemyOf(a))
                    continue;
                float d = Agent.Position.DistanceSquared(a.Position);
                if (d < bd) { bd = d; best = a; }
            }
            return best;
        }

        // Pair march (research 11 avoidance model): dir = at the pair; the
        // wall probe (WallFree = fit-probe + boundary, NO body cap) still
        // vetoes real walls; a BODY inside the ~30deg command cone produces
        // a latched ~55deg sidestep on the freer side instead of a veto, and
        // the pair itself never counts as a blocker (bodyD < pd - 0.35).
        private const float MarchSideAngle = 0.96f;   // ~55 deg
        private int _gpairSide;
        private float _rigStuckLastAt = -99f;       // 4w [STUCK] per-agent rate limit
        private bool _pairWantsMove;
        private float _gpairSideUntil = -99f, _gpairWall = -1f, _gpairBody = -1f;

        private Vec2 PairMarchGoal(Vec2 toP, float pd, float reach)
        {
            _gpairWall = -1f; _gpairBody = -1f;
            // _pairWantsMove: is the march TRYING to move (vs the in-band
            // stop)? The escape layer keys on it -- an intentional band
            // Zero must not look like a wall pocket.
            _pairWantsMove = false;
            if (pd < 0.0001f) { _gpairSide = 0; return Vec2.Zero; }
            Vec2 dir = new Vec2(toP.x / pd, toP.y / pd);
            if (pd <= reach * 0.95f) { _gpairSide = 0; return Vec2.Zero; }
            _pairWantsMove = true;
            float mag = pd < reach * 1.6f ? 0.6f : 1f;   // taper the last stretch
            _gpairWall = _clear.WallFree(Agent, dir, _brainClock);
            if (_gpairWall < 0.30f) { _gpairSide = 0; return Vec2.Zero; }
            // Native occupancy rule (research 13 sec.4): enemy bodies never
            // veto a goal -- only NON-hostile bodies do. Friendlies get
            // sidesteps; enemies are pushed through (it's a melee).
            float bodyD = _clear.NearestAgentInDir(Agent, dir, nonEnemiesOnly: true);
            _gpairBody = bodyD;
            bool bodyAhead = bodyD > 0f && bodyD < pd - 0.35f;
            bool stuckBlocked = _clear.Watch.IsBlocked(dir, _brainClock);
            if (!bodyAhead && !stuckBlocked)
            {
                _gpairSide = 0;
                return new Vec2(dir.x * mag, dir.y * mag);
            }
            if (_gpairSide == 0 || _brainClock >= _gpairSideUntil)
                _gpairSide = PickMarchSide(dir);
            _gpairSideUntil = _brainClock + 0.7f;
            Vec2 esc = RotDir(dir, _gpairSide * MarchSideAngle);
            if (_clear.WallFree(Agent, esc, _brainClock) >= 0.30f)
                return new Vec2(esc.x * mag, esc.y * mag);
            esc = RotDir(dir, -_gpairSide * MarchSideAngle);
            if (_clear.WallFree(Agent, esc, _brainClock) >= 0.30f)
            {
                _gpairSide = -_gpairSide;
                return new Vec2(esc.x * mag, esc.y * mag);
            }
            return Vec2.Zero;
        }

        private int PickMarchSide(Vec2 dir)
        {
            float fl = _clear.Free(Agent, RotDir(dir, MarchSideAngle), _brainClock);
            float fr = _clear.Free(Agent, RotDir(dir, -MarchSideAngle), _brainClock);
            return fl > fr + 0.05f ? 1 : fr > fl + 0.05f ? -1 : (Agent.Index & 1) == 0 ? 1 : -1;
        }

        private static Vec2 RotDir(Vec2 d, float ang)
        {
            float c = MathF.Cos(ang), s = MathF.Sin(ang);
            return new Vec2(d.x * c - d.y * s, d.x * s + d.y * c);
        }

        private int _gpairLogCount;
        private float _gpairLastAt = -99f;
        private void GPairLog()
        {
            if (_gpairLogCount >= 300 || _brainClock - _gpairLastAt < 1f) return;
            _gpairLastAt = _brainClock;
            _gpairLogCount++;
            Agent cur = Agent.GetTargetAgent();
            float d = CombatLive(_gfPair) ? (_gfPair.Position.AsVec2 - Agent.Position.AsVec2).Length : -1f;
            CombatAiLog.Log($"[GPAIR] Agent #{Agent.Index}: pair=#{(CombatLive(_gfPair) ? _gfPair.Index.ToString() : "-")} " +
                $"dist={d:F2} kind={_rigPlan.Kind} goal=({_rigGoal.x:F2},{_rigGoal.y:F2}) " +
                $"wall={_gpairWall:F2} body={_gpairBody:F2} side={_gpairSide} " +
                $"lock={(Live(cur) ? "#" + cur.Index : "-")} steer={(_steerRegistered ? 1 : 0)} " +
                $"state={_duelState} timer={_duelTimer:F2}");
        }

        private int _gtnLogCount;
        private float _gtnLastAt = -99f;
        // gtnative diagnostic: is NATIVE the one picking (vs our anchor
        // fallback), and how much the free-look bent the approach.
        private void GTnLog(bool nativePickSeen)
        {
            if (_gtnLogCount >= 400 || _brainClock - _gtnLastAt < 1f) return;
            _gtnLastAt = _brainClock;
            _gtnLogCount++;
            Agent nativePick = null;
            try { nativePick = Agent.GetTargetAgent(); }
            catch { }
            bool nativeLive = CombatLive(nativePick) && Agent.IsEnemyOf(nativePick);
            float d = CombatLive(_gfPair) ? (_gfPair.Position.AsVec2 - Agent.Position.AsVec2).Length : -1f;
            Mission mm = Agent.Mission;
            int n = mm != null && CombatLive(_gfPair) ? AlliesLockedOn(mm, _gfPair) : -1;
            CombatAiLog.Log($"[GTN] Agent #{Agent.Index}: native=#{(nativeLive ? nativePick.Index.ToString() : "-")} " +
                $"using={(nativeLive && _gfPair == nativePick ? "native" : "weight")} " +
                $"pair=#{(CombatLive(_gfPair) ? _gfPair.Index.ToString() : "-")}(d={d:F2}) " +
                $"lookValid={(_gfLookValid ? 1 : 0)} threats={_gfLook.Count} goal=({_rigGoal.x:F2},{_rigGoal.y:F2}) " +
                $"n={n} att={_gfAttOnMe} layer={(_gfLayerActive ? 1 : 0)} " +
                $"reason={_gfLayerReason} xl={(_gfTranslating ? 1 : 0)} state={_duelState}");
        }

        // Copy of the spar rig's surroundings block (SparRig.cs "(4)" section)
        // so the spar defender stays untouched: clearance ring + boundary,
        // wall/path veto over the planner's ranked moves, stuck watchdog,
        // escape burst. applyWallVeto=false for the pair path: PairMarchGoal
        // already owns _rigGoal -- the RigApplyWall candidate scan would
        // re-veto it on the bodies the march is allowed to sidestep around.
        private void GroupSurroundings(Agent primary, bool applyWallVeto = true)
        {
            _clear.Tick(Agent, _brainClock);
            if (!_rigBoundaryDone && _clear.BoundaryDist > 1e30f)
                RigSynthesizeBoundary();
            Vec2 topDir = _rigRankedCount > 0 ? _rigRanked[0].Dir : Vec2.Zero;
            if (applyWallVeto)
                _rigGoal = RigApplyWall();

            Agent.ActionStage os = Agent.GetCurrentActionStage(1);
            bool stuckExempt = os == Agent.ActionStage.AttackRelease
                || DodgeOwnsBody || os == Agent.ActionStage.DefendParry || _handOffToNative;
            if (_clear.Watch.Note(Agent.Position.AsVec2, FlatUnit(_rigGoal), _brainClock, stuckExempt, out Vec2 stuckDir))
            {
                _rigStuckCount++;
                // 4w: cap raised (40->300, the old one hid the real rate) +
                // a 2s per-agent rate limit + the actual blocker evidence:
                // body distances both ways, the clearance boundary, and the
                // raycast diag. Diagnostics only -- no escape changes.
                if (_rigStuckLogCount < 300 && _brainClock - _rigStuckLastAt >= 2f)
                {
                    _rigStuckLastAt = _brainClock;
                    _rigStuckLogCount++;
                    float fb = _clear.NearestAgentInDir(Agent, stuckDir, nonEnemiesOnly: true);
                    float eb = _clear.NearestAgentInDir(Agent, stuckDir, nonEnemiesOnly: false);
                    CombatAiLog.Log($"[STUCK] Agent #{Agent.Index}: (group) pos=({Agent.Position.x:F1},{Agent.Position.y:F1}) " +
                        $"cmd=({stuckDir.x:F2},{stuckDir.y:F2}) minRing={_clear.MinRing():F2} " +
                        $"friend={fb:F2} body={eb:F2} bnd={_clear.BoundaryDist:F1} blk={_clear.Blocker(Agent, stuckDir)}");
                }
            }

            if (_rigGoal.LengthSquared < 0.0001f && topDir.LengthSquared > 0.0001f
                && _rigRankedCount > 0 && _rigRanked[0].Kind != SparRigPlanner.RigMoveKind.None
                && (applyWallVeto || _pairWantsMove))
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
                    }
                    _rigWallZeroSince = _brainClock;
                }
            }
            else if (_rigGoal.LengthSquared > 0.0001f)
                _rigWallZeroSince = -99f;
            if (_brainClock < _rigEscapeUntil
                && _rigEscapeDir.LengthSquared > 0.0001f
                && !_clear.Watch.IsBlocked(_rigEscapeDir, _brainClock))
                _rigGoal = _rigEscapeDir;
        }

        // ---- group versions of the spar brain's two multi-attacker terms ----
        // CLUSTER: another attacker aimed at me is mid-release, or released
        // within SparClusterWindow -> this block is a little less likely.
        // DISTRACTION: another attacker aimed at me is HOLDING a ready attack
        // or feinting (cancel edges in the window) -> my attention is on it,
        // this release lands at lower block chance.
        private readonly Dictionary<int, float> _gfRelEdgeAt = new Dictionary<int, float>(4);
        private readonly HashSet<int> _gfWasRel = new HashSet<int>();

        private void GroupTrackReleaseEdges(Mission m)
        {
            foreach (GroupEngagement.Intent it in _gfIncoming)
            {
                Agent a = GroupEngagement.AgentOf(m, it.Attacker);
                bool rel = CombatLive(a) && a.GetCurrentActionStage(1) == Agent.ActionStage.AttackRelease;
                bool was = _gfWasRel.Contains(it.Attacker);
                if (rel && !was) _gfRelEdgeAt[it.Attacker] = m.CurrentTime;
                if (rel) _gfWasRel.Add(it.Attacker); else _gfWasRel.Remove(it.Attacker);
            }
        }

        private float GroupClusterLogit(Agent releaser)
        {
            if (!GroupUpDownOnly || releaser == null || Agent.Mission == null) return 0f;
            float now = Agent.Mission.CurrentTime;
            foreach (GroupEngagement.Intent it in _gfIncoming)
            {
                if (it.Attacker == releaser.Index) continue;
                Agent a = GroupEngagement.AgentOf(Agent.Mission, it.Attacker);
                if (!CombatLive(a)) continue;
                if (a.GetCurrentActionStage(1) == Agent.ActionStage.AttackRelease
                    || (_gfRelEdgeAt.TryGetValue(it.Attacker, out float at) && now - at <= SparClusterWindow))
                    return SparClusterLogitValue;
            }
            return 0f;
        }

        private float GroupDistractLogit(Agent releaser)
        {
            if (!GroupUpDownOnly || releaser == null || Agent.Mission == null) return 0f;
            foreach (GroupEngagement.Intent it in _gfIncoming)
            {
                if (it.Attacker == releaser.Index) continue;
                Agent a = GroupEngagement.AgentOf(Agent.Mission, it.Attacker);
                if (!CombatLive(a)) continue;
                Agent.ActionStage st = a.GetCurrentActionStage(1);
                if (st == Agent.ActionStage.AttackReady || st == Agent.ActionStage.AttackQuickReady
                    || GroupFeintCancels(a) > 0)
                    return SparDistractLogitValue;
            }
            return 0f;
        }
        private int _grigLogCount;
        private float _grigLastAt = -99f;
        private void GRigLog(float dHome)
        {
            if (_grigLogCount >= 200 || _gfThreats.Count == 0 || _brainClock - _grigLastAt < 1f) return;
            _grigLastAt = _brainClock;
            _grigLogCount++;
            CombatAiLog.Log($"[GRIG] Agent #{Agent.Index}: primary=#{_rigPlan.PrimaryKey} kind={_rigPlan.Kind} " +
                $"spread={_rigPlan.SpreadDeg:F0} goal=({_rigGoal.x:F2},{_rigGoal.y:F2}) homeDist={dHome:F2} " +
                $"ret={(_gfReturning ? 1 : 0)} incoming={_gfIncoming.Count} aim={(CombatLive(_forcedDuelTarget) ? "#" + _forcedDuelTarget.Index : "-")}");
        }
        // Threat set: candidates = live enemies within threat range (player
        // excluded). Score: +100 intent-on-me, +30 swinging, +range-dist,
        // +10 inside facing +/-60. Enemies intent on SOMEONE ELSE are
        // excluded while their intent is younger than GroupBusyIgnoreSeconds
        // (unless that leaves me with zero threats). Membership changes at
        // most every 0.25s, a new intent-on-me forces it now.
        private void UpdateThreats(Mission m)
        {
            Vec2 fw = Agent.GetMovementDirection();
            bool forced = false;
            foreach (GroupEngagement.Intent it in _gfIncoming)
                if (!_gfThreats.Exists(t => t.Index == it.Attacker))
                    forced = true;
            if (!forced && _brainClock - _gfMemberAt < 0.25f && _gfThreats.Count > 0)
            {
                _gfThreats.RemoveAll(t => !CombatLive(t));
                return;
            }
            _gfMemberAt = _brainClock;
            _gfThreats.Clear();
            _gfThreatW.Clear();
            var cand = new List<(Agent a, float s, float d)>(8);
            var busy = new List<(Agent a, float s, float d)>(8);
            foreach (Agent e in m.Agents)
            {
                if (!CombatLive(e) || e == Agent || e.IsMainAgent || !Agent.IsEnemyOf(e))
                    continue;
                float d = (e.Position.AsVec2 - Agent.Position.AsVec2).Length;
                if (d > CombatAiState.GroupThreatRangeMeters) continue;
                float s = CombatAiState.GroupThreatRangeMeters - d;
                bool onMe = _gfIncoming.Exists(it => it.Attacker == e.Index);
                if (onMe) s += 100f;
                Agent.ActionStage st = e.GetCurrentActionStage(1);
                if (st == Agent.ActionStage.AttackReady || st == Agent.ActionStage.AttackQuickReady
                    || st == Agent.ActionStage.AttackRelease)
                    s += 30f;
                if (fw.LengthSquared > 0.0001f)
                {
                    Vec2 to = e.Position.AsVec2 - Agent.Position.AsVec2;
                    float bear = MathF.Abs(MathF.Atan2(fw.x * to.y - fw.y * to.x,
                        fw.x * to.x + fw.y * to.y)) * 57.2958f;
                    if (bear <= 60f) s += 10f;
                }
                // Busy-elsewhere exclusion.
                bool busyElse = false;
                if (GroupEngagement.ClaimOf(m, e.Index, _brainClock, out GroupEngagement.Intent ec)
                    && ec.Victim != Agent.Index && m.CurrentTime - ec.Since < CombatAiState.GroupBusyIgnoreSeconds)
                    busyElse = true;
                if (busyElse) busy.Add((e, s, d)); else cand.Add((e, s, d));
            }
            if (cand.Count == 0) { cand.AddRange(busy); }
            cand.Sort((x, y) => y.s.CompareTo(x.s));
            for (int i = 0; i < cand.Count && i < CombatAiState.GroupMaxThreats; i++)
            {
                _gfThreats.Add(cand[i].a);
                _gfThreatW.Add(cand[i].s);
            }
        }

        // Attack target = any threat that is IN REACH, inside the +/-GroupAttackAimDeg
        // cone of my facing (90 deg default), able to see me, with a CLEAR lane
        // (no friendly or other enemy standing on the line to it). Intent-on-me
        // first, then nearest; a small stickiness stops flip-flopping.
        private Agent PickAttackTarget()
        {
            Vec2 fw = Agent.GetMovementDirection();
            float reach = MeleeReach();
            Agent best = null; float bs = float.NegativeInfinity;
            foreach (Agent t in _gfThreats)
            {
                if (!CombatLive(t)) continue;
                Vec2 to = t.Position.AsVec2 - Agent.Position.AsVec2;
                float d = to.Length;
                if (d > reach) continue;
                float bear = fw.LengthSquared > 0.0001f
                    ? MathF.Abs(MathF.Atan2(fw.x * to.y - fw.y * to.x, fw.x * to.x + fw.y * to.y)) * 57.2958f
                    : 0f;
                if (bear > CombatAiState.GroupAttackAimDeg) continue;
                Vec2 tf = DefenderFacing(t);
                Vec2 back = Agent.Position.AsVec2 - t.Position.AsVec2;
                if (tf.LengthSquared > 0.0001f && back.LengthSquared > 0.0001f)
                {
                    float vb = MathF.Abs(MathF.Atan2(tf.x * back.y - tf.y * back.x,
                        tf.x * back.x + tf.y * back.y)) * 57.2958f;
                    if (vb > CombatAiState.GroupAttackViewDeg) continue;
                }
                if (!LaneClear(t)) continue;
                float s = -bear * 0.3f - d * 8f
                    + (_gfIncoming.Exists(it => it.Attacker == t.Index) ? 50f : 0f)
                    + (t == _forcedDuelTarget ? 10f : 0f);
                if (s > bs) { bs = s; best = t; }
            }
            return best;
        }

        // No other human (friend OR foe) on the straight line to the target.
        private bool LaneClear(Agent t)
        {
            Vec2 a = Agent.Position.AsVec2, ab = t.Position.AsVec2 - a;
            float len = ab.Length;
            if (len < 0.01f) return true;
            Vec2 u = new Vec2(ab.x / len, ab.y / len);
            foreach (Agent e in Agent.Mission.Agents)
            {
                if (e == Agent || e == t || !e.IsHuman || !CombatLive(e)) continue;
                Vec2 ae = e.Position.AsVec2 - a;
                float proj = ae.x * u.x + ae.y * u.y;
                if (proj < 0.2f || proj > len + 0.1f) continue;
                float perp = MathF.Abs(ae.x * u.y - ae.y * u.x);
                if (perp < (Agent.IsEnemyOf(e) ? 0.4f : 0.55f)) return false;
            }
            return true;
        }

        // Group fighters only swing overhead / thrust (left/right swings clip
        // the shoulder-to-shoulder neighbours).
        private bool GroupAimedAtMe(Agent t)
            => CombatLive(t) && GroupEngagement.ClaimOf(Agent.Mission, t.Index, 0f, out GroupEngagement.Intent ci) && ci.Victim == Agent.Index;

        private bool GroupUpDownOnly => CombatAiState.GroupFightMode && CombatAiState.GroupFree
            && GroupLineup.Phase == GroupLineup.Ph.Fight && _freeTargetActive;
        // Heading for the free driver: weighted bisector of threat dirs with
        // the spar spread rules; primary = highest weight, sticky via the
        // existing _ftPrimary/_ftPrimSwitchAt.
        private float GroupHeadingTarget(Mission m)
        {
            _gfThreats.RemoveAll(t => !CombatLive(t));
            if (_gfThreats.Count == 0)
            {
                Agent near = null; float nd = 64f;
                foreach (Agent e in m.Agents)
                {
                    if (!CombatLive(e) || e == Agent || e.IsMainAgent || !Agent.IsEnemyOf(e)) continue;
                    float d = (e.Position.AsVec2 - Agent.Position.AsVec2).LengthSquared;
                    if (d < nd) { nd = d; near = e; }
                }
                if (near != null)
                {
                    Vec2 to = near.Position.AsVec2 - Agent.Position.AsVec2;
                    return MathF.Atan2(to.y, to.x);
                }
                return _ftHeading.LengthSquared > 0.0001f ? MathF.Atan2(_ftHeading.y, _ftHeading.x) : -999f;
            }
            if (_gfThreats.Count == 1)
            {
                Vec2 to = _gfThreats[0].Position.AsVec2 - Agent.Position.AsVec2;
                return MathF.Atan2(to.y, to.x);
            }
            Vec2 bis = Vec2.Zero; float sw = 0f; Agent top = _gfThreats[0]; float topW = -1f;
            float maxA = -9f;
            var angs = new List<float>(_gfThreats.Count);
            for (int i = 0; i < _gfThreats.Count; i++)
            {
                Agent t = _gfThreats[i];
                Vec2 to = t.Position.AsVec2 - Agent.Position.AsVec2;
                float w = _gfThreatW[i] > 100f ? 2f : _gfThreatW[i] > 30f ? 1.5f : 1f;
                w *= 1f + MathF.Max(0f, 3.2f - to.Length) * 0.05f;
                Vec2 u = FlatUnit(to);
                bis += u * w;
                sw += w;
                angs.Add(MathF.Atan2(u.y, u.x));
                if (w > topW) { topW = w; top = t; }
            }
            for (int i = 0; i < angs.Count; i++)
                for (int j = i + 1; j < angs.Count; j++)
                {
                    float d = MathF.Abs(NormalizeAngle(angs[i] - angs[j])) * 57.2958f;
                    if (d > maxA) maxA = d;
                }
            float spread = maxA > -1f ? maxA : 0f;
            _ftSpread = spread;
            // Sticky primary: cooldown 1.2s between switches.
            if (top != _ftPrimary && _brainClock - _ftPrimSwitchAt >= 1.2f)
            {
                _ftPrimary = top;
                _ftPrimSwitchAt = _brainClock;
            }
            if (!CombatLive(_ftPrimary)) _ftPrimary = top;
            Vec2 pu = FlatUnit(_ftPrimary.Position.AsVec2 - Agent.Position.AsVec2);
            if (spread > 160f) return MathF.Atan2(pu.y, pu.x);
            if (bis.LengthSquared < 0.0001f) return MathF.Atan2(pu.y, pu.x);
            float aBis = MathF.Atan2(bis.y, bis.x);
            if (spread <= 90f) return aBis;
            float aPri = MathF.Atan2(pu.y, pu.x);
            return aBis + NormalizeAngle(aPri - aBis) * ((spread - 90f) / 70f);
        }

        private void GThreatLog(Mission m)
        {
            bool engaged = _gfThreats.Count > 0 || _gfIncoming.Count > 0;
            if (!engaged) return;
            if (_gthreatLogCount < MaxGthreatLogs && _brainClock - _gthreatLastAt >= 1.0f)
            {
                _gthreatLastAt = _brainClock;
                _gthreatLogCount++;
                var sb = new System.Text.StringBuilder(160);
                sb.Append($"[GTHREAT] Agent #{Agent.Index} threats=[");
                for (int i = 0; i < _gfThreats.Count; i++)
                {
                    Agent t = _gfThreats[i];
                    Vec2 to = t.Position.AsVec2 - Agent.Position.AsVec2;
                    bool onMe = _gfIncoming.Exists(it => it.Attacker == t.Index);
                    sb.Append($"{(i > 0 ? "," : "")}#{t.Index}(d={to.Length:F1},i={(onMe ? "Y" : "N")})");
                }
                sb.Append($"] primary=#{(Live(_ftPrimary) ? _ftPrimary.Index : -1)} " +
                    $"spread={_ftSpread:F0} intentOnMe={_gfIncoming.Count} " +
                    $"ret={(_gfReturning ? 1 : 0)} reach={MeleeReach():F2} aim={(CombatLive(_forcedDuelTarget) ? "#" + _forcedDuelTarget.Index : "-")} " +
                    $"state={_duelState} think={_thinkDelay:F2} timer={_duelTimer:F2} lock={Agent.GetTargetAgent()?.Index ?? -1}");
                CombatAiLog.Log(sb.ToString());
            }
        }

        private void GViewSample(Mission m)
        {
            Vec3 lk = Agent.LookDirection;
            if (lk.LengthSquared > 0.0001f)
            {
                float lkDeg = MathF.Atan2(lk.y, lk.x) * 57.2958f;
                foreach (Agent t in _gfThreats)
                {
                    Vec2 to = t.Position.AsVec2 - Agent.Position.AsVec2;
                    if (to.LengthSquared < 0.0001f) continue;
                    float b = MathF.Abs(NormalizeAngle(
                        (MathF.Atan2(to.y, to.x) * 57.2958f - lkDeg) * 0.0174533f)) * 57.2958f;
                    _gfInViewSum += b <= 45f ? 1f : 0f;
                    _gfInViewN++;
                }
            }
            if (_gviewLogCount < MaxGviewLogs && _brainClock - _gviewLastAt >= 2.0f)
            {
                _gviewLastAt = _brainClock;
                _gviewLogCount++;
                CombatAiLog.Log($"[GVIEW] Agent #{Agent.Index}: inView45={(_gfInViewN > 0 ? 100f * _gfInViewSum / _gfInViewN : -1f):F0}% " +
                    $"threats={_gfThreats.Count} claims={GroupEngagement.Count}");
                _gfInViewSum = 0f; _gfInViewN = 0;
            }
        }

        private void GFightLog(Mission m)
        {
            if (_gfightLogCount >= MaxGfightLogs || _brainClock - _gfightLastAt < 5f)
                return;
            _gfightLastAt = _brainClock;
            _gfightLogCount++;
            int liveA = 0, liveB = 0, cov0 = 0, cov1 = 0, cov2 = 0, cov3 = 0;
            foreach (Agent a in m.Agents)
            {
                if (!CombatLive(a) || a.IsMainAgent || !a.IsHuman) continue;
                if (Agent.IsEnemyOf(a)) liveB++; else liveA++;
                int c = GroupEngagement.LiveClaimersOn(m, a.Index, _brainClock);
                if (c == 0) cov0++; else if (c == 1) cov1++; else if (c == 2) cov2++; else cov3++;
            }
            CombatAiLog.Log($"[GFIGHT] t={_brainClock:F0} live={liveA}v{liveB} claims={GroupEngagement.Count} " +
                $"coverage 0/1/2/3={cov0}/{cov1}/{cov2}/{cov3}");
        }
    }
}
