using System;
using System.Collections.Generic;
using System.Text;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // GROUP PAIR FIGHT (steered path) -- "distributed target + threat
    // free-look" model. Terms (user spec):
    //   TARGET = the real combat lock. Always held while fighting; the agent
    //            marches to it until in melee range; need NOT be unique; may
    //            switch to another enemy in range and may return to the
    //            original (anchor) after a hit.
    //   ANCHOR = the order-preserving counterpart GroupTargeting assigns
    //            across the enemy line ("the original target").
    //   THREAT = any enemy inside ThreatRange of me, aimed at me or not.
    //            While not attacking the steer heading holds the median
    //            threat bearing (up to 3, clamped) instead of the lock
    //            bearing -- the free look. Attacking dissolves it: the
    //            release always goes out facing the lock it lands on.
    // Movement stays MovementInputVector via PairMarchGoal/_rigGoal; every
    // lock write goes through EnsureTargetAgent.
    public partial class CombatAiOverrideComponent
    {
        private const float SwitchRangeMul = 1.3f;        // switchRange = MeleeReach * this
        private const float SwitchMargin = 0.4f;          // nearest must be this much closer than active (m)
        private const float SwitchDwell = 0.8f;           // seconds between switches
        private const float LookClampDeg = 60f;           // free-look median clamp vs target bearing
        private const float LookSlewDegPerSec = 240f;     // free-look slew rate

        // ===== 4x tunables (native movement + layer targeting rework) =====
        private const float QuickRangeR = 1.5f;           // quick-switch/cover candidate range (x reach)
        private const float QuickArcDot = 0.5f;           // quick candidate min dot vs body facing
        private const float QuickCooldown = 4.0f;         // s between quick excursions
        private const float QuickMaxSeconds = 3.0f;       // max excursion length
        private const float QuickFireChance = 0.65f;      // per-eval fire chance once a trigger is true
        private const float GuardHoldSeconds = 0.25f;     // H guarding continuously this long arms T1
        private const float HomeFreeSeconds = 1.0f;       // excursion must run this long before home-free return
        private const float DefenderGraceSeconds = 0.4f;  // defender mode off-debounce (deliberate debounce)
        private const float DefenderEvalSeconds = 0.25f;  // defender attacker re-pick interval
        private const float DefenderSwitchMargin = 1.3f;  // attacker switch needs this score multiple (jitter guard)
        private const float DefenderGapMin = 2.0f;        // defender post-swing gap min s (attack less)
        private const float DefenderGapMax = 3.8f;        // defender post-swing gap max s
        private const float GroupStartReachRND = 1.25f;   // non-defender start gate range (x reach)
        private const float GroupStartDotND = 0.5f;       // non-defender start gate front dot
        private const float DefSpreadDeg = 110f;          // defender step-out arms above this A1-A2 spread
        private const float DefSpreadEndDeg = 80f;        // correction stops once spread drops below this
        private const float DefFixBlend = 0.6f;           // step-out share in the corrected goal
        private const float DefFixMag = 0.8f;             // corrected-goal magnitude
        private const float DefFixDamp = 0.5f;            // tangential-intent damp ("runs one side")
        private const float DefFixSideLatch = 0.6f;       // s the picked step-out side holds
        private const float HoldReadySeconds = 0.5f;      // ready stage >= this before Release = a HOLD
        private const float HoldFallMisreadShare = 0.70f; // failed roll vs held -> misread guard share
        private const float PickNoiseMargin = 0f;         // 4aa: disabled (fluid picks)
        private const float NativeAnchorMul = 1.1f;       // native pick bonus (was 1.3)
        private const float CoveredStayPenalty = 0.65f;   // cur pair shared while an uncovered enemy is near
        private const float SwapRangeM = 3.5f;            // coordinated swap partner max dist (m)
        private const float SwapGain = 1.2f;              // both sides must score this x better post-swap
        private const float SwapReachR = 2.0f;            // each new target must be within this x reach
        private const float SwapCooldown = 3.0f;          // per-agent s between swap initiations
        private const float SwapEvalSeconds = 0.25f;      // swap eval throttle
        private const float SwapAssignTtl = 2.0f;         // GfAssign entry lifetime s
        private const float NewcomerSeconds = 1.0f;       // "entered near me" window for quick-newcomer
        private const float SurpriseSeconds = 1.5f;       // strike-surprise window after a switch
        private const float SurpriseDelta = 0.25f;        // victim block-p penalty vs a surprise swing
        private const float DefenderBlockBonus = 0.10f;   // defender-layer block bonus
        private const float RigViewDeg = 80f;             // rigged 2v1: keep attackers inside +-this
        private const float RigMag = 0.7f;                // rigged orbit/step magnitude
        private const float AssistMaxR = 3.0f;            // closing-assist max range (x reach)
        private const float AssistSpeed = 0.5f;           // planar speed below which we count as stalled
        private const float AssistStallSeconds = 0.35f;   // low-speed dwell before assist may arm
        private const float AssistTimerSeconds = 3.0f;    // Approach timer that arms assist outright
        private const float AssistReleaseR = 0.9f;        // release hysteresis (arm at >0.95x)
        private const float GroupReachSlack = 0.20f;      // extra Approach reach margin (m)
        private const int MaxGfStallLogs = 200;        // [STALL] cap (4ab)
        private const float OnMeRangeR = 1.6f;            // an attacker-on-me inside this x reach counts
        private const float OnMeFactor = 2.5f;            // score bonus for an enemy targeting me
        private const float OnMeFrontFloor = 1.2f;        // attacker behind us keeps this front floor
        private const float TwoVOnePenalty = 0.5f;        // ally-occupied non-attacker while threatNear
        private const float CapRangeR = 3.0f;             // 2-attacker cap applies inside this x reach
        private const float IntentExpireSeconds = 1.5f;   // native world-intent stale timeout (translator)
        private const float GroupAttackGapMin = 0.0f;     // non-defender post-swing gap min s
        private const float GroupAttackGapMax = 0.30f;    // non-defender post-swing gap max s
        private const float GroupStartReachR = 1.05f;     // attack may only start inside this x reach
        private const float GroupStartDot = 0.7f;         // attack may only start with pair ~in front
        private const int MaxGfsmLogs = 1500;             // [GFSM] event budget per component
        private static float ThreatRange => CombatAiState.GroupThreatRangeMeters;
        private static float LocalLeash => CombatAiState.GroupThreatRangeMeters;
        private static int MaxThreats => Math.Min(3, CombatAiState.GroupMaxThreats);
        private static int CoverCap => CombatAiState.GroupMaxEngagers;

        private Agent _gfAnchor;              // distributed counterpart (GroupTargeting)
        private bool _gfSwitched;             // active != anchor right now
        private int _gfRelSinceSwitch;        // releases completed since the switch
        private DuelState _gfPrevState;
        private float _gfSwitchAt = -99f;
        private bool _gfFought;               // an enemy has been inside melee*1.2 once
        private float _gfScanAt = -99f;
        private Agent _gfNearest;             // throttled scan: nearest live enemy
        private float _gfNearestDist = float.MaxValue;
        private readonly List<Agent> _gfLook = new List<Agent>(4);       // close threats, dist-sorted
        private readonly List<float> _gfLookAng = new List<float>(4);
        private readonly List<(Agent e, float d)> _gfLookScan = new List<(Agent e, float d)>(4);
        private Vec2 _gfLookDir;              // free-look heading consumed by the steer block
        private bool _gfLookValid;            // false = face the lock (attacking / no threats)
        private float _gfLookA;               // slewed absolute look angle (rad)
        private bool _gfLookInit;
        private float _gfWPickAt = -99f;      // weighted-pick re-eval throttle (4r)
        private Agent _gfWPick;
        private readonly List<Agent> _gfWCand = new List<Agent>(6);
        private int _gtwLogCount;
        private float _gtwLastAt = -99f;
        private int _gtargetLogCount, _gswitchLogCount, _glookLogCount;
        private float _glookLastAt = -99f;
        private const int MaxGtargetLogs = 120, MaxGswitchLogs = 150, MaxGlookLogs = 120;

        // ---- 4x layer targeting / crowd fields ----
        private float _gfCrowdAt = -99f;
        private bool _gfCrowded;
        private bool _gfLayerActive;                   // layer owns target+camera+free attack
        private bool _gfDefenderActive;                // layer-on because >=2 attackers target me
        private bool _gfQuickOn;                       // layer-on because a quick excursion runs
        private bool _gfTranslating;                   // native intent replay armed ([GTN] xl=)
        private string _gfLayerReason = "";            // layer-on reason ([GTN] reason=)
        private Agent _gfFsmHome;                      // H: the 1v1 target an excursion returns to
        private Agent _gfQuickTarget;
        private bool _gfQuickReleased;
        private string _gfQuickReason = "?";           // quick-guard / quick-easy / quick-cant-hit
        private float _gfQuickAt = -99f, _gfQuickCdAt = -99f;
        private float _gfHGuardAt = -99f;              // H guarding continuously since
        private float _gfDefenderUntil = -99f;         // defender debounce deadline
        private float _gfDefEvalAt = -99f;             // attacker re-pick throttle
        private int _gfAttOnMe;                        // enemies whose lock is me (GfEnemyOn, 6m)
        private bool _gfDefBlocked;                    // last start-gate refusal was a readying attacker
        private bool _gfThreatNear;                    // an enemy targeting me sits inside OnMeRangeR
        private bool _gfUncoveredNear;                 // an enemy nobody fights sits inside 2*reach
        private bool _gfDefFixOn;                      // spread hysteresis latch for the step-out fix
        private int _gfDefFixSide = 1;                 // picked step-out side (perp(A1->A2) * side)
        private float _gfDefFixSideUntil = -99f;       // side re-pick not before this time
        private readonly int[] _gfrIdx = { -1, -1, -1, -1, -1, -1, -1, -1 };   // attacker ready-stage stamps
        private readonly float[] _gfrAt = new float[8];
        private bool _gfQuickPerm;                       // excursion is permanent (H stays covered)
        private bool _gfRigged;                          // rigged 2v1 role drives _rigGoal this tick
        private int _gfRigRole;                          // 0 none / 1 defender / 2 attacker (rig-on/off logs)
        private bool _gfAssist;                          // closing-assist owns _rigGoal this tick
        private float _gfStallSince = -1f;               // first tick speed < AssistSpeed in Approach
        private float _gfOutReachSince = -1f;            // first tick Approach-with-pd-in-(reach,3r]
        private float _gfStallLogAt = -99f;
        private float _gfSwapCdAt = -99f;                // swap initiation cooldown
        private float _gfSwapEvalAt = -99f;              // swap eval throttle
        private readonly Dictionary<int, float> _gfNearSince = new Dictionary<int, float>(8);
        private readonly List<int> _gfNearKeys = new List<int>(8);
        private bool _gfForceQuick;                    // next PickAttackKind answers Quick
        private Vec2 _nativeWorldMove;                 // native locomotion intent, WORLD frame
        private float _nativeWorldMoveAt = -99f;
        private readonly List<Agent> _gfAtk = new List<Agent>(4);   // scan enemies targeting me, dist-sorted
        private int _gfsmLogCount;
        private float _gfsmLastAt = -99f;
        // The ACTIVE combat target for the steered pair path (_gfPair).
        // Mid-chain commits keep their target so the FSM can finish the
        // swing -- only Approach or a dead active may re-pick (same gate as
        // the old counterpart block). Returns null only when no live enemy
        // exists.
        // Always-run threat scan (extracted from GroupPickTarget, 4q): the free-look
        // set must refresh even when native owns the pick (gtnative) -- the
        // old placement skipped the scan entirely while using=native, leaving
        // stale threats and no free-look.
        private void GroupThreatScan(Mission m)
        {
            if (_brainClock - _gfScanAt < 0.15f)
                return;
            _gfScanAt = _brainClock;
            _gfNearest = null;
            _gfNearestDist = float.MaxValue;
            _gfLookScan.Clear();
            foreach (Agent e in m.Agents)
            {
                if (!CombatLive(e) || e == Agent || e.IsMainAgent || !Agent.IsEnemyOf(e))
                    continue;
                float d = (e.Position.AsVec2 - Agent.Position.AsVec2).Length;
                if (d < _gfNearestDist) { _gfNearestDist = d; _gfNearest = e; }
                if (d <= ThreatRange)
                    _gfLookScan.Add((e, d));
            }
            _gfLookScan.Sort((x, y) => x.d.CompareTo(y.d));
            _gfLook.Clear();
            for (int i = 0; i < _gfLookScan.Count && i < MaxThreats; i++)
                _gfLook.Add(_gfLookScan[i].e);
        }

        private Agent GroupPickTarget(Mission m)
        {
            // "Fought" arms the local leash once any enemy has been inside
            // melee*1.2 -- before that the lines are still closing and
            // marching across the field to the anchor is correct.
            if (!_gfFought && _gfNearest != null && _gfNearestDist <= MeleeReach() * 1.2f)
                _gfFought = true;
            // A completed release while switched = "after a hit": it unlocks
            // the anchor return below. Counted every tick, mid-chain too.
            if (_duelState == DuelState.Release && _gfPrevState != DuelState.Release && _gfSwitched)
                _gfRelSinceSwitch++;
            _gfPrevState = _duelState;
            if (!CombatLive(_gfAnchor))
            {
                _gfAnchor = GroupTargeting.Assign(Agent, m,
                    out int ri, out int na, out int rj, out int nm, out int cv);
                if (_gtargetLogCount < MaxGtargetLogs)
                {
                    _gtargetLogCount++;
                    CombatAiLog.Log($"[GTARGET] Agent #{Agent.Index}: " +
                        $"anchor={(CombatLive(_gfAnchor) ? "#" + _gfAnchor.Index : "-")} " +
                        $"(rank {ri}/{na} -> {rj}/{nm} cov={cv})");
                }
            }

            Agent active = _gfPair;
            // Switching only in Approach (or when the active died): mid-chain
            // commits keep the target so the release lands on the lock.
            if (_duelState != DuelState.Approach && CombatLive(active))
                return active;
            if (!CombatLive(active))
            {
                active = CombatLive(_gfAnchor) ? _gfAnchor : ResolveGroupPair(m);
                if (CombatLive(active))
                {
                    _gfSwitched = false;
                    _gfRelSinceSwitch = 0;
                    if (active != _gfAnchor && _gtargetLogCount < MaxGtargetLogs)
                    {
                        _gtargetLogCount++;
                        CombatAiLog.Log($"[GTARGET] Agent #{Agent.Index}: " +
                            $"active=#{active.Index} (nearest-enemy fallback, anchor dead)");
                    }
                }
            }
            if (!CombatLive(active))
                return active;

            float dAct = (active.Position.AsVec2 - Agent.Position.AsVec2).Length;
            if (_gfSwitched)
            {
                if (_gfRelSinceSwitch >= 1)
                {
                    // A swing on the switched target landed ("after a hit"):
                    // back to the anchor when it's still near, else adopt the
                    // current target as the new anchor.
                    if (CombatLive(_gfAnchor)
                        && (_gfAnchor.Position.AsVec2 - Agent.Position.AsVec2).Length <= LocalLeash)
                    {
                        LogGSwitch("return", active, _gfAnchor, dAct);
                        active = _gfAnchor;
                    }
                    else
                    {
                        LogGSwitch("adopt", _gfAnchor, active, dAct);
                        _gfAnchor = active;
                    }
                    _gfSwitched = false;
                    _gfRelSinceSwitch = 0;
                }
            }
            else if (CombatLive(_gfNearest) && _gfNearest != active
                && _gfNearestDist <= MeleeReach() * SwitchRangeMul
                && _gfNearestDist + SwitchMargin < dAct
                && _brainClock - _gfSwitchAt >= SwitchDwell
                && AlliesLockedOn(m, _gfNearest) < CoverCap)
            {
                LogGSwitch("switch", active, _gfNearest, _gfNearestDist);
                active = _gfNearest;
                _gfSwitched = true;
                _gfRelSinceSwitch = 0;
                _gfSwitchAt = _brainClock;
            }
            // LOCAL LEASH (anti-drift, armed once we've fought): never trail
            // an active target outside the threat bubble while a nearer
            // enemy stands inside it -- changes WHO we fight, never a goal.
            if (_gfFought && CombatLive(active)
                && (active.Position.AsVec2 - Agent.Position.AsVec2).Length > LocalLeash
                && CombatLive(_gfNearest) && _gfNearestDist <= LocalLeash)
            {
                LogGSwitch("leash", active, _gfNearest, _gfNearestDist);
                active = _gfNearest;
                _gfAnchor = _gfNearest;
                _gfSwitched = false;
                _gfRelSinceSwitch = 0;
            }
            return active;
        }

        // Cover check for the switch rule: how many allies currently hold
        // their combat lock on `t` (the lock IS the group fight's engagement
        // pointer -- FindSparThreat's group branch reads it the same way).
        // 4s perf: reads the once-per-frame target-count map
        // (GfBuildTargetCounts, run by the mission behavior while the
        // gtnative group fight is on) instead of a full m.Agents scan per
        // call -- O(n) per candidate x ~4 candidates per agent was the 20v20
        // cost. The map counts EVERY live human's lock incl. the player's;
        // the old per-call loop stays as the fallback for frames where the
        // map wasn't built.
        private static readonly Dictionary<int, int> GfTargetCounts = new Dictionary<int, int>(64);
        private static float _gfCountMapAt = -99f;
        // The same single frame pass also builds GfEnemyOn[t] = hostile
        // humans locked on t within 6m (the defender-mode signal) and prunes
        // the excursion switch bookkeeping.
        private static readonly Dictionary<int, int> GfEnemyOn = new Dictionary<int, int>(64);
        private static readonly List<int> _gfPrune = new List<int>(16);
        // 4aa coordination: swap/cover target assignments (agent -> enemy,
        // ttl), excursion/swap occupants, and the surprise-strike window.
        // Cleared by GfStaticReset at every GroupTargeting.Reset call site.
        private static readonly Dictionary<int, (int e, float until)> GfAssign =
            new Dictionary<int, (int e, float until)>(32);
        private static readonly Dictionary<int, float> GfBusyUntil = new Dictionary<int, float>(32);
        private static readonly Dictionary<int, float> GfSurprise = new Dictionary<int, float>(32);

        internal static void GfStaticReset()
        {
            GfAssign.Clear();
            GfBusyUntil.Clear();
            GfSurprise.Clear();
        }

        internal static void GfBuildTargetCounts(Mission m)
        {
            GfTargetCounts.Clear();
            GfEnemyOn.Clear();
            _gfCountMapAt = m != null ? m.CurrentTime : -99f;
            if (m == null)
                return;
            float now = m.CurrentTime;
            foreach (Agent a in m.Agents)
            {
                if (a == null || !a.IsActive() || !a.IsHuman)
                    continue;
                Agent t = null;
                try { t = a.GetTargetAgent(); }
                catch { }
                if (t == null)
                    continue;
                GfTargetCounts[t.Index] = GfTargetCounts.TryGetValue(t.Index, out int c) ? c + 1 : 1;
                if (a.IsEnemyOf(t)
                    && (t.Position.AsVec2 - a.Position.AsVec2).Length <= 6f)
                    GfEnemyOn[t.Index] = GfEnemyOn.TryGetValue(t.Index, out int e) ? e + 1 : 1;
            }
        }

        // Same-frame book for the cap: an excursion start moves our count
        // onto E NOW, so agents evaluated later this frame already see it.
        private static void GfCountMove(Agent from, Agent to)
        {
            if (from != null && GfTargetCounts.TryGetValue(from.Index, out int oc) && oc > 0)
                GfTargetCounts[from.Index] = oc - 1;
            if (to != null)
                GfTargetCounts[to.Index] = GfTargetCounts.TryGetValue(to.Index, out int ec) ? ec + 1 : 1;
        }

        private int AlliesLockedOn(Mission m, Agent t)
        {
            if (m != null && m.CurrentTime == _gfCountMapAt)
            {
                int n = GfTargetCounts.TryGetValue(t.Index, out int c) ? c : 0;
                Agent self = null;
                try { self = Agent.GetTargetAgent(); }
                catch { }
                if (self == t)
                    n--;
                return n < 0 ? 0 : n;
            }
            int count = 0;
            foreach (Agent a in m.Agents)
            {
                if (!CombatLive(a) || a == Agent || a.IsMainAgent || Agent.IsEnemyOf(a))
                    continue;
                if (a.GetTargetAgent() == t)
                    count++;
            }
            return count;
        }

        // 4r gtnative weighted pick: native auto-select stays ON but WE own
        // the lock -- native's pick is just one candidate. Close enemies and
        // enemies already on us outscore a far native pick; max 2 attackers
        // per enemy (a candidate targeting me is exempt from the cap), and
        // the current pair keeps hysteresis so the lock can't flap.
        // 4x baseline pick (weights toggle): native auto-select stays ON
        // but WE own the lock -- native's own pick stands in for its scoring
        // as the x1.3 anchor candidate (no native hook). Candidates: the
        // native pick, the current pair, scan enemies inside max(2*reach,3m).
        // Score = close x in-reach x FrontFactor x UniqueFactor x native.
        // The 2-attacker cap only bites while an uncapped candidate remains
        // inside CapRangeR*reach. No dwell, no hysteresis, no blackout --
        // only the PickNoiseMargin flicker guard.
        private Agent GroupPickWeighted(Mission m, Agent nativePick)
        {
            if (CombatLive(_gfPair) && CombatLive(_gfWPick) && _brainClock - _gfWPickAt < 0.15f)
                return _gfWPick;
            _gfWPickAt = _brainClock;
            Vec2 pos = Agent.Position.AsVec2;
            float reach = MeleeReach();
            bool nativeOk = CombatLive(nativePick) && Agent.IsEnemyOf(nativePick);
            _gfWCand.Clear();
            if (nativeOk)
                _gfWCand.Add(nativePick);
            if (CombatLive(_gfPair) && !_gfWCand.Contains(_gfPair))
                _gfWCand.Add(_gfPair);
            float closeLim = MathF.Max(2f * reach, 3f);
            for (int i = 0; i < _gfLookScan.Count; i++)
            {
                Agent e = _gfLookScan[i].e;
                if (_gfLookScan[i].d <= closeLim && CombatLive(e) && !_gfWCand.Contains(e))
                    _gfWCand.Add(e);
            }
            // Cap rule: count uncapped candidates inside CapRangeR*reach
            // first -- the cap only applies while a legal pick exists.
            float capLim = CapRangeR * reach;
            int avail = 0;
            foreach (Agent c in _gfWCand)
            {
                if (!Agent.IsEnemyOf(c))
                    continue;
                float dC = (c.Position.AsVec2 - pos).Length;
                if (dC <= capLim && AlliesLockedOn(m, c) < 2)
                    avail++;
            }
            // 4y onMe: an enemy whose lock is ME inside OnMeRangeR*reach
            // (under the 2-attacker load) both earns the attacker bonus and
            // arms the greedy-2v1 penalty on ally-occupied non-attackers.
            bool threatNear = false;
            foreach (Agent c in _gfWCand)
            {
                if (!Agent.IsEnemyOf(c))
                    continue;
                float dT = (c.Position.AsVec2 - pos).Length;
                if (dT > OnMeRangeR * reach)
                    continue;
                bool tm = false;
                try { tm = c.GetTargetAgent() == Agent; } catch { }
                if (tm && AlliesLockedOn(m, c) < 2)
                    threatNear = true;
            }
            _gfThreatNear = threatNear;
            // covered-stay needs an uncovered enemy inside 2*reach in the
            // front arc -- one pass over the same candidates.
            _gfUncoveredNear = false;
            {
                Vec2 face3 = Agent.GetMovementDirection();
                if (face3.LengthSquared < 0.0001f) face3 = Agent.LookDirection.AsVec2;
                foreach (Agent c in _gfWCand)
                {
                    if (!Agent.IsEnemyOf(c))
                        continue;
                    Vec2 toC = c.Position.AsVec2 - pos;
                    float dC = toC.Length;
                    if (dC > 2f * reach || AlliesLockedOn(m, c) != 0)
                        continue;
                    if (face3.LengthSquared > 0.0001f && dC > 0.0001f
                        && Vec2.DotProduct(FlatUnit(face3), toC / dC) < 0.5f)
                        continue;
                    _gfUncoveredNear = true;
                    break;
                }
            }
            Agent best = null;
            float bestScore = 0f, curScore = 0f;
            foreach (Agent c in _gfWCand)
            {
                if (!Agent.IsEnemyOf(c))
                    continue;
                Vec2 toC = c.Position.AsVec2 - pos;
                float d = toC.Length;
                int n = AlliesLockedOn(m, c);
                if (n >= 2 && d <= capLim && avail > 0)
                    continue;   // 2 attackers max while an uncapped pick exists
                bool onMe = false;
                try { onMe = c.GetTargetAgent() == Agent; } catch { }
                float s = 1f / (1f + 0.15f * d);
                if (d <= 1.3f * reach) s *= 1.5f;
                float fr = FrontFactor(c, pos);
                // 4y (a): attacker-on-me in range, not already 2v1'd ->
                // bonus + a front floor so one behind us is not penalised.
                // 4y (b): n>=2 never earns the bonus.
                if (CombatAiState.GfOnMe && onMe && n < 2 && d <= OnMeRangeR * reach)
                {
                    s *= OnMeFactor;
                    if (fr < OnMeFrontFloor) fr = OnMeFrontFloor;
                }
                s *= fr;
                if (n == 0) s *= 1.6f;                  // unique targets first
                if (c == nativePick) s *= NativeAnchorMul;
                // 4aa covered-stay: don't sit on a pair an ally already
                // fights while an uncovered enemy is in reach.
                if (c == _gfPair && n >= 2 && _gfUncoveredNear)
                    s *= CoveredStayPenalty;
                // 4y (c) greedy-2v1: don't mob an ally's enemy while a real
                // attacker-on-me is unaddressed.
                if (CombatAiState.GfOnMe && threatNear && !onMe && n >= 1)
                    s *= TwoVOnePenalty;
                if (s > bestScore) { bestScore = s; best = c; }
                if (c == _gfPair) curScore = s;
            }
            Agent chosen = best != null ? best : (nativeOk ? nativePick : GroupPickTarget(m));
            bool chosenOnMe = false;
            try { chosenOnMe = CombatLive(chosen) && chosen.GetTargetAgent() == Agent; } catch { }
            if (chosen != _gfPair && chosenOnMe)
                _gsumOnMePicks++;
            string reason = best == null ? "cap" : "weights";
            if (best != null && best != _gfPair && CombatLive(_gfPair)
                && bestScore < PickNoiseMargin * curScore)
            {
                chosen = _gfPair;
                reason = "noise";
            }
            _gfWPick = chosen;
            if (chosenOnMe && chosen != _gfPair)
                reason += "+onMe";
            if (chosen != nativePick && _gtwLogCount < 200 && _brainClock - _gtwLastAt >= 1f)
            {
                _gtwLastAt = _brainClock;
                _gtwLogCount++;
                CombatAiLog.Log($"[GTW] Agent #{Agent.Index}: native=#{(nativeOk ? nativePick.Index.ToString() : "-")} " +
                    $"chosen=#{(CombatLive(chosen) ? chosen.Index.ToString() : "-")} " +
                    $"d={(CombatLive(chosen) ? (chosen.Position.AsVec2 - pos).Length : -1f):F2} " +
                    $"reason={reason} n={(CombatLive(chosen) ? AlliesLockedOn(m, chosen) : -1)}");
            }
            return chosen;
        }

        // Front factor shared by the baseline and defender picks (body
        // facing; LookDirection fallback; 1.0 when facing unreadable).
        private float FrontFactor(Agent e, Vec2 pos)
        {
            Vec2 face = Agent.GetMovementDirection();
            if (face.LengthSquared < 0.0001f) face = Agent.LookDirection.AsVec2;
            Vec2 to = e.Position.AsVec2 - pos;
            float d = to.Length;
            if (face.LengthSquared < 0.0001f || d < 0.0001f)
                return 1f;
            float dot = Vec2.DotProduct(FlatUnit(face), to / d);
            if (dot >= 0.85f) return 1.5f;
            if (dot >= 0.5f) return 1f + (dot - 0.5f) / 0.35f * 0.5f;
            if (dot >= 0f) return 0.35f + dot / 0.5f * 0.65f;
            return 0.15f;
        }

        // 4s crowd check (0.15s cache, gtnative steer path only, updown
        // toggle): true while (a) an ally already locks our duel target, or
        // (b) a live friendly human -- allies incl. the player -- stands
        // inside max(1.6*reach, 2.2m) within the +/-120deg cone of our
        // facing: the band a side swing would sweep through. While crowded
        // only Up/Down attacks are honest, so every side-pick call site
        // defers to this.
        private bool GroupCrowded()
        {
            if (!(_steerGroup && CombatAiState.GroupNativeTarget && CombatAiState.GfUpdown))
                return false;
            if (_brainClock - _gfCrowdAt < 0.15f)
                return _gfCrowded;
            _gfCrowdAt = _brainClock;
            Mission m = Agent.Mission;
            if (m == null)
                return _gfCrowded = false;
            if (CombatLive(_duelTarget) && AlliesLockedOn(m, _duelTarget) >= 1)
                return _gfCrowded = true;
            float lim = MathF.Max(1.6f * MeleeReach(), 2.2f);
            Vec2 face = Agent.LookDirection.AsVec2;
            bool haveFace = face.LengthSquared > 0.0001f;
            if (haveFace)
                face.Normalize();
            Vec2 pos = Agent.Position.AsVec2;
            foreach (Agent a in m.Agents)
            {
                if (!CombatLive(a) || a == Agent || !a.IsHuman || Agent.IsEnemyOf(a))
                    continue;
                Vec2 to = a.Position.AsVec2 - pos;
                float d = to.Length;
                if (d > lim || d < 0.0001f)
                    continue;
                if (haveFace)
                {
                    float bear = MathF.Abs(MathF.Atan2(face.x * to.y - face.y * to.x,
                        face.x * to.x + face.y * to.y));
                    if (bear > 2.0944f)   // 120 deg
                        continue;
                }
                return _gfCrowded = true;
            }
            return _gfCrowded = false;
        }

        // 4s crowded pick: random among the up/down lines the weapon supports,
        // preferring not to repeat `avoid`; None when neither is usable so the
        // caller can fall back to its normal pick.
        private Agent.MovementControlFlag GroupCrowdDir(Agent.MovementControlFlag avoid)
        {
            bool up = IsDirectionSupported(Agent.MovementControlFlag.AttackUp);
            bool dn = IsDirectionSupported(Agent.MovementControlFlag.AttackDown);
            if (up && dn && avoid == Agent.MovementControlFlag.AttackUp) up = false;
            else if (up && dn && avoid == Agent.MovementControlFlag.AttackDown) dn = false;
            if (up && dn)
                return DuelRng.NextDouble() < 0.5
                    ? Agent.MovementControlFlag.AttackUp : Agent.MovementControlFlag.AttackDown;
            if (up) return Agent.MovementControlFlag.AttackUp;
            if (dn) return Agent.MovementControlFlag.AttackDown;
            return Agent.MovementControlFlag.None;
        }

        // =====================================================================
        // 4x LAYER TARGETING: the layer is NOT sticky -- it is active only
        // (a) while this agent is a DEFENDER (GfEnemyOn >= 2 enemies lock on
        // it, with a deliberate 0.4s off-debounce) or (b) during a QUICK
        // excursion out of a clean 1v1. Layer ON = free camera + free
        // attack + the target picks below. Layer OFF = the baseline
        // weights/native pick and native-facing.
        // =====================================================================
        private Agent GfTargetTick(Mission m, Agent nativePick)
        {
            Agent cur = _gfPair;
            float reach = MeleeReach();
            Vec2 pos = Agent.Position.AsVec2;
            // Attackers on me: scan enemies whose combat lock IS me
            // (dist-sorted already). The defender pick + defender camera
            // read this set; activation keys off GfEnemyOn (6m map count).
            _gfAtk.Clear();
            for (int i = 0; i < _gfLookScan.Count && _gfAtk.Count < 4; i++)
            {
                Agent e = _gfLookScan[i].e;
                if (CombatLive(e) && e.GetTargetAgent() == Agent)
                    _gfAtk.Add(e);
            }
            // 4aa newcomer watch: first tick each scan enemy sat inside
            // 1.5*reach -- the quick-newcomer trigger reads the stamp.
            _gfNearKeys.Clear();
            for (int i = 0; i < _gfLookScan.Count; i++)
            {
                Agent e0 = _gfLookScan[i].e;
                if (!CombatLive(e0)) continue;
                if (_gfLookScan[i].d <= 1.5f * reach)
                {
                    _gfNearKeys.Add(e0.Index);
                    if (!_gfNearSince.ContainsKey(e0.Index))
                        _gfNearSince[e0.Index] = _brainClock;
                }
            }
            if (_gfNearSince.Count > 0)
            {
                _gfPrune.Clear();
                foreach (var kv in _gfNearSince)
                    if (!_gfNearKeys.Contains(kv.Key))
                        _gfPrune.Add(kv.Key);
                for (int i = 0; i < _gfPrune.Count; i++)
                    _gfNearSince.Remove(_gfPrune[i]);
            }
            int att = GfEnemyOn.TryGetValue(Agent.Index, out int an) ? an : 0;
            _gfAttOnMe = att;
            bool defNow = CombatAiState.GfDefender && att >= 2;
            if (defNow)
                _gfDefenderUntil = _brainClock + DefenderGraceSeconds;
            _gfDefenderActive = _brainClock < _gfDefenderUntil;
            bool wasLayer = _gfLayerActive;

            // H guarding bookkeeping (T1 trigger) -- measured on the current
            // 1v1 target before any quick check fires.
            if (CombatLive(cur) && cur.GetCurrentActionStage(1) != Agent.ActionStage.AttackRelease
                && CoveredAttackDir(cur) != Agent.MovementControlFlag.None)
            {
                if (_gfHGuardAt < -90f)
                    _gfHGuardAt = _brainClock;
            }
            else
                _gfHGuardAt = -99f;

            // ---- excursion bookkeeping: first Release completes the errand ----
            if (_gfQuickOn)
            {
                Agent e = _gfQuickTarget;
                if (cur == e && _duelState == DuelState.Release)
                    _gfQuickReleased = true;
                bool releasedLeft = _gfQuickReleased
                    && _duelState != DuelState.Release;
                bool homeFree = CombatLive(_gfFsmHome)
                    && (_gfFsmHome.Position.AsVec2 - pos).Length <= 4f * reach
                    && AlliesLockedOn(m, _gfFsmHome) == 0
                    && _brainClock - _gfQuickAt >= HomeFreeSeconds;
                bool done = !CombatLive(e)
                    || (e.Position.AsVec2 - pos).Length > 2f * reach
                    || _brainClock - _gfQuickAt > QuickMaxSeconds
                    || FriendInLane(e, pos)
                    || releasedLeft || homeFree;
                if (!done && !_gfDefenderActive)
                {
                    _gfLayerActive = true;
                    return cur;   // mid-excursion: stay on E
                }
                // Return: defender preempts (defender pick wins); a perm
                // switch keeps E (it is the new home), else home when free,
                // else the baseline pick.
                Agent back = _gfDefenderActive ? GfDefenderPick(m, cur, pos, reach)
                    : (_gfQuickPerm && CombatLive(e) ? e
                        : (homeFree && CombatLive(_gfFsmHome)
                            ? _gfFsmHome : GfBaselinePick(m, nativePick)));
                if (!CombatLive(back))
                    back = GfBaselinePick(m, nativePick);
                bool wasPerm = _gfQuickPerm;
                _gfQuickOn = false;
                _gfQuickPerm = false;
                _gfQuickTarget = null;
                _gfQuickCdAt = _brainClock;
                _gfForceQuick = false;
                GfBusyUntil.Remove(Agent.Index);
                if (!wasPerm) _gsumQuickRet++;
                _gfLayerActive = _gfDefenderActive;
                if (!_gfLayerActive)
                {
                    _gfLayerReason = "";
                    GFsmLog(m, homeFree ? "layer-off home-free" : "layer-off", cur, back);
                }
                else
                    _gfLayerReason = "defender";
                if (back != cur)
                {
                    _gsumPairChg++;
                    GFsmLog(m, homeFree ? "return-home-free" : "pick-change", cur, back);
                }
                return back;
            }

            // ---- defender: target only enemies that are targeting me ----
            if (_gfDefenderActive)
            {
                _gfLayerActive = true;
                Agent pick = GfDefenderPick(m, cur, pos, reach);
                if (!CombatLive(pick))
                    pick = GfBaselinePick(m, nativePick);
                if (!wasLayer)
                {
                    _gfLayerReason = "defender";
                    GFsmLog(m, "layer-on defender", cur, pick);
                }
                if (pick != cur)
                {
                    _gsumPairChg++;
                    GFsmLog(m, "pick-change defender", cur, pick);
                }
                return pick;
            }

            // ---- baseline: weights pick + assignment/swap/cover/quick ----
            Agent baseline = GfBaselinePick(m, nativePick);
            bool safeTick = _duelState == DuelState.Approach || _duelState == DuelState.Recover;
            bool contact = CombatLive(cur) && (cur.Position.AsVec2 - pos).Length <= 1.5f * reach;

            // S1a: honour a live swap/cover assignment at a safe tick.
            if (safeTick && GfAssign.TryGetValue(Agent.Index, out (int e, float until) asg))
            {
                Agent at = GetAgentByIndex(asg.e);
                if (CombatLive(at) && Agent.IsEnemyOf(at) && _brainClock <= asg.until)
                {
                    GfAssign.Remove(Agent.Index);
                    if (at != cur)
                        GFsmLog(m, "swap-take", cur, at);
                    return at;
                }
                GfAssign.Remove(Agent.Index);
            }

            // S1 coordinated swap: I take F's enemy, F takes my H -- only
            // when BOTH sides come out clearly ahead (straighter/closer).
            if (CombatAiState.GfSwap && contact && safeTick
                && _brainClock - _gfSwapCdAt >= SwapCooldown
                && _brainClock - _gfSwapEvalAt >= SwapEvalSeconds
                && !GfAssign.ContainsKey(Agent.Index))
            {
                _gfSwapEvalAt = _brainClock;
                Agent fSwap = null, tF = null;
                foreach (Agent f in m.Agents)
                {
                    if (!CombatLive(f) || f == Agent || !f.IsHuman || f.IsMainAgent || Agent.IsEnemyOf(f))
                        continue;
                    if (GfEnemyOn.TryGetValue(f.Index, out int fe) && fe >= 2)
                        continue;                       // defender: no swapper role
                    if (GfBusyUntil.TryGetValue(f.Index, out float bu) && _brainClock < bu)
                        continue;                       // excursion or swap cooldown
                    if (GfAssign.ContainsKey(f.Index))
                        continue;                       // pending assignment
                    if ((f.Position.AsVec2 - pos).Length > SwapRangeM)
                        continue;
                    Agent tc = null;
                    try { tc = f.GetTargetAgent(); } catch { }
                    if (!CombatLive(tc) || !Agent.IsEnemyOf(tc) || tc == cur)
                        continue;
                    if ((tc.Position.AsVec2 - f.Position.AsVec2).Length > 1.5f * reach)
                        continue;                       // not in contact-ish
                    if (GfScoreFor(Agent, tc) < SwapGain * GfScoreFor(Agent, cur)
                        || GfScoreFor(f, cur) < SwapGain * GfScoreFor(f, tc))
                        continue;
                    if ((tc.Position.AsVec2 - pos).Length > SwapReachR * reach
                        || (cur.Position.AsVec2 - f.Position.AsVec2).Length > SwapReachR * reach)
                        continue;
                    if (FriendInLane(tc, pos) || FriendInLaneFrom(f.Position.AsVec2, f, cur))
                        continue;
                    // cap: post-swap effective allies-on must stay < 2
                    if (AlliesLockedOn(m, tc) >= 2 || AlliesLockedOn(m, cur) >= 2)
                        continue;
                    fSwap = f; tF = tc;
                    break;
                }
                if (fSwap != null)
                {
                    _gfSwapCdAt = _brainClock;
                    float exp = _brainClock + SwapAssignTtl;
                    GfAssign[Agent.Index] = (tF.Index, exp);
                    GfAssign[fSwap.Index] = (cur.Index, exp);
                    GfBusyUntil[fSwap.Index] = _brainClock + SwapCooldown;
                    _gsumSwaps++;
                    _gsumPairChg++;
                    GFsmLog(m, $"swap M#{Agent.Index}<->F#{fSwap.Index}", cur, tF);
                    return tF;
                }
            }

            // S1b cover handoff: leave H for a close uncovered newcomer only
            // while H stays covered (ally already on it, or a free nearby
            // friend takes the assignment).
            if (CombatAiState.GfSwap && contact && safeTick && !GfAssign.ContainsKey(Agent.Index))
            {
                Agent eCov = FindCoverTarget(m, cur, pos, reach);
                if (eCov != null && GfCoverOk(m, cur, pos))
                {
                    _gsumCovers++;
                    _gsumPairChg++;
                    GFsmLog(m, "cover-handoff", cur, eCov);
                    return eCov;
                }
            }

            // S2 quick switch: triggers T1/T2/T4/T5(newcomer); H covered ->
            // permanent, else the one-hit return trip.
            if (CombatAiState.GfQuick && contact
                && _brainClock - _gfQuickCdAt >= QuickCooldown)
            {
                Agent e = FindQuickTarget(m, cur, pos, reach);
                if (e != null)
                {
                    bool coverOk = CombatAiState.GfSwap && GfCoverOk(m, cur, pos);
                    _gfQuickOn = true;
                    _gfQuickPerm = coverOk;
                    _gfQuickTarget = e;
                    _gfQuickAt = _brainClock;
                    _gfQuickReleased = false;
                    _gfForceQuick = true;
                    _gfFsmHome = coverOk ? e : cur;
                    GfCountMove(cur, e);
                    GfBusyUntil[Agent.Index] = _brainClock + QuickMaxSeconds + 1f;
                    GfSurprise[Agent.Index] = _brainClock + SurpriseSeconds;
                    _gfLayerActive = true;
                    _gfLayerReason = _gfQuickReason;
                    _gsumQuickStart++;
                    if (coverOk) _gsumQuickPerm++;
                    _gsumPairChg++;
                    GFsmLog(m, coverOk ? "layer-on quick-perm " + _gfQuickReason
                        : "layer-on quick-return " + _gfQuickReason, cur, e);
                    return e;
                }
            }
            _gfLayerActive = false;
            _gfLayerReason = "";
            if (wasLayer)
                GFsmLog(m, "layer-off", cur, baseline);
            else if (baseline != cur)
                GFsmLog(m, "pick-change weights", cur, baseline);
            if (baseline != cur)
                _gsumPairChg++;
            return baseline;
        }

        // weights toggle OFF = native's pick untouched (we only supply the
        // lock when native has none).
        private Agent GfBaselinePick(Mission m, Agent nativePick)
        {
            if (!CombatAiState.GfWeights)
                return CombatLive(nativePick) ? nativePick : GroupPickTarget(m);
            return GroupPickWeighted(m, nativePick);
        }

        // Defender pick: best enemy that is targeting ME (front/close
        // weights, no cap, no unique bonus). 0.25s re-eval; a challenger
        // must beat the current attacker by DefenderSwitchMargin.
        private Agent GfDefenderPick(Mission m, Agent cur, Vec2 pos, float reach)
        {
            bool curAtk = CombatLive(cur) && cur.GetTargetAgent() == Agent;
            if (_brainClock - _gfDefEvalAt < DefenderEvalSeconds && curAtk)
                return cur;
            _gfDefEvalAt = _brainClock;
            Agent best = null;
            float bestS = 0f, curS = 0f;
            for (int i = 0; i < _gfAtk.Count; i++)
            {
                Agent e = _gfAtk[i];
                if (!CombatLive(e))
                    continue;
                Vec2 to = e.Position.AsVec2 - pos;
                float d = to.Length;
                float sc = 1f / (1f + 0.15f * d);
                if (d <= 1.3f * reach) sc *= 1.5f;
                sc *= FrontFactor(e, pos);
                if (sc > bestS) { bestS = sc; best = e; }
                if (e == cur) curS = sc;
            }
            if (best == null)
                return curAtk ? cur : null;
            if (curAtk && best != cur && bestS < DefenderSwitchMargin * curS)
                return cur;
            return best;
        }

        // Quick switch (the only layer takeover of a 1v1): E must be inside
        // 1.3*reach, not H, in our front arc, lane clear of friends, fewer
        // than 2 allies already on it -- plus a reason: T1 H has been
        // guarding, T2 E is an easier hit, T4 H is not hittable right now.
        private Agent FindQuickTarget(Mission m, Agent cur, Vec2 pos, float reach)
        {
            bool guardOk = _gfHGuardAt > -90f
                && _brainClock - _gfHGuardAt >= GuardHoldSeconds;
            Vec2 face = Agent.GetMovementDirection();
            if (face.LengthSquared < 0.0001f) face = Agent.LookDirection.AsVec2;
            Vec2 toH = cur.Position.AsVec2 - pos;
            float dH = toH.Length;
            bool hUnhit = dH > 1.15f * reach
                || (face.LengthSquared > 0.0001f && dH > 0.0001f
                    && Vec2.DotProduct(FlatUnit(face), toH / dH) < 0.5f);
            bool hFacesMe = CombatLive(cur) && FacesMe(cur, Agent, 0f);
            for (int i = 0; i < _gfLookScan.Count; i++)
            {
                Agent e = _gfLookScan[i].e;
                float d = _gfLookScan[i].d;
                if (d > QuickRangeR * reach || !CombatLive(e) || e == cur)
                    continue;
                if (face.LengthSquared > 0.0001f && d > 0.0001f
                    && Vec2.DotProduct(FlatUnit(face), (e.Position.AsVec2 - pos) / d) < QuickArcDot)
                    continue;                       // behind us -- never
                if (FriendInLane(e, pos))
                    continue;
                int nE = AlliesLockedOn(m, e);
                if (CombatAiState.GfOnMe)
                {
                    bool eOnMe = false;
                    try { eOnMe = e.GetTargetAgent() == Agent; } catch { }
                    if (!(nE == 0 || eOnMe))
                        continue;   // no 2v1 greed unless it is attacking me
                }
                else if (nE >= 2)
                    continue;
                bool eRelease = e.GetCurrentActionStage(1) == Agent.ActionStage.AttackRelease;
                bool t2 = eRelease || (hFacesMe && !FacesMe(e, Agent, 0.2f));
                bool t5 = _gfNearSince.TryGetValue(e.Index, out float ns)
                    && _brainClock - ns <= NewcomerSeconds;
                bool t4 = hUnhit && d <= 1.0f * reach
                    && face.LengthSquared > 0.0001f
                    && Vec2.DotProduct(FlatUnit(face), (e.Position.AsVec2 - pos) / d) >= 0.7f;
                if (!(guardOk || t2 || t4 || t5))
                    continue;
                if (DuelRng.NextDouble() >= QuickFireChance)
                    continue;   // 4z: a chance, not a certainty
                _gfQuickReason = guardOk ? "quick-guard" : t2 ? "quick-easy"
                    : t4 ? "quick-cant-hit" : "quick-newcomer";
                return e;
            }
            return null;
        }

        // Does `e` face `me` (body facing, LookDirection fallback)?
        private static bool FacesMe(Agent e, Agent me, float minDot)
        {
            if (!CombatLive(e)) return false;
            Vec2 f = e.GetMovementDirection();
            if (f.LengthSquared < 0.0001f) f = e.LookDirection.AsVec2;
            Vec2 toMe = me.Position.AsVec2 - e.Position.AsVec2;
            return f.LengthSquared > 0.0001f && toMe.LengthSquared > 0.0001f
                && Vec2.DotProduct(FlatUnit(f), FlatUnit(toMe)) > minDot;
        }

        // Is a FRIEND standing on the me->t segment (within 0.7m of the
        // line)? A quick/help side-switch must never cut through an ally.
        private bool FriendInLane(Agent t, Vec2 pos)
        {
            if (!CombatLive(t)) return false;
            Vec2 to = t.Position.AsVec2 - pos;
            float d = to.Length;
            if (d < 0.0001f) return false;
            Mission m = Agent.Mission;
            if (m == null) return false;
            foreach (Agent a in m.Agents)
            {
                if (!CombatLive(a) || a == Agent || !a.IsHuman || Agent.IsEnemyOf(a))
                    continue;
                Vec2 ap = a.Position.AsVec2 - pos;
                float along = Vec2.DotProduct(ap, to) / d;
                if (along < 0.3f || along > d - 0.3f)
                    continue;
                float off = MathF.Abs(to.x * ap.y - to.y * ap.x) / d;
                if (off < 0.7f)
                    return true;
            }
            return false;
        }

        // 4z held-attack classifier: remember when each nearby enemy
        // entered AttackReady/QuickReady so the block roll can tell a held
        // swing (>=0.5s of ready stage before Release) from a normal one.
        private void GfNoteReadyStage(Agent e)
        {
            if (!CombatLive(e)) return;
            Agent.ActionStage st = e.GetCurrentActionStage(1);
            int idx = e.Index;
            if (st == Agent.ActionStage.AttackReady || st == Agent.ActionStage.AttackQuickReady)
            {
                int free = -1, oldest = 0;
                for (int i = 0; i < _gfrIdx.Length; i++)
                {
                    if (_gfrIdx[i] == idx) return;
                    if (_gfrIdx[i] < 0 && free < 0) free = i;
                    if (_gfrAt[i] < _gfrAt[oldest]) oldest = i;
                }
                int slot = free >= 0 ? free : oldest;
                _gfrIdx[slot] = idx;
                _gfrAt[slot] = _brainClock;
            }
            else if (st != Agent.ActionStage.AttackRelease)
            {
                // The stamp must survive through Release (the roll reads it
                // there) -- clear only once the swing fully ended.
                for (int i = 0; i < _gfrIdx.Length; i++)
                    if (_gfrIdx[i] == idx) { _gfrIdx[i] = -1; return; }
            }
        }
        // Observed move class of an enemy's swing: HELD if it sat in a
        // ready stage >=HoldReadySeconds before Release, else NORMAL
        // (a quick's <0.15s ready counts as normal per spec).
        private int GfMoveClass(Agent releaser)
        {
            if (releaser == null) return PlayerCombatModel.MvNormal;
            float ready = -1f;
            for (int i = 0; i < _gfrIdx.Length; i++)
                if (_gfrIdx[i] == releaser.Index) ready = _brainClock - _gfrAt[i];
            return ready >= HoldReadySeconds ? PlayerCombatModel.MvHold : PlayerCombatModel.MvNormal;
        }
        private float GfMoveDelta(Agent releaser)
            => CombatAiTune.BlockDelta(GfMoveClass(releaser));

        // 4aa swap scoring from an ARBITRARY agent's position/facing (the
        // symmetric mutual-benefit test): close x in-reach x front -- no
        // unique/native/onMe terms.
        private float GfScoreFor(Agent who, Agent enemy)
        {
            if (!CombatLive(who) || !CombatLive(enemy)) return 0f;
            Vec2 wp = who.Position.AsVec2;
            Vec2 to = enemy.Position.AsVec2 - wp;
            float d = to.Length;
            float sc = 1f / (1f + 0.15f * d);
            if (d <= 1.3f * MeleeReach()) sc *= 1.5f;
            Vec2 face = who.GetMovementDirection();
            if (face.LengthSquared < 0.0001f) face = who.LookDirection.AsVec2;
            if (face.LengthSquared > 0.0001f && d > 0.0001f)
            {
                float dot = Vec2.DotProduct(FlatUnit(face), to / d);
                sc *= dot >= 0.85f ? 1.5f
                    : dot >= 0.5f ? 1f + (dot - 0.5f) / 0.35f * 0.5f
                    : dot >= 0f ? 0.35f + dot / 0.5f * 0.65f : 0.15f;
            }
            return sc;
        }

        // FriendInLane from an ARBITRARY friendly's position (the partner's
        // half of the swap test) -- me and the partner don't count.
        private bool FriendInLaneFrom(Vec2 fromPos, Agent fromAgent, Agent t)
        {
            if (!CombatLive(t)) return false;
            Vec2 to = t.Position.AsVec2 - fromPos;
            float d = to.Length;
            if (d < 0.0001f) return false;
            Mission m = Agent.Mission;
            if (m == null) return false;
            foreach (Agent a in m.Agents)
            {
                if (!CombatLive(a) || a == fromAgent || a == Agent || !a.IsHuman
                    || fromAgent.IsEnemyOf(a))
                    continue;
                Vec2 ap = a.Position.AsVec2 - fromPos;
                float along = Vec2.DotProduct(ap, to) / d;
                if (along < 0.3f || along > d - 0.3f)
                    continue;
                float off = MathF.Abs(to.x * ap.y - to.y * ap.x) / d;
                if (off < 0.7f)
                    return true;
            }
            return false;
        }

        // Cover: is H still fought by our side after I leave? An ally is
        // already on it, or a free nearby friend takes the assignment.
        private bool GfCoverOk(Mission m, Agent h, Vec2 pos)
        {
            if (!CombatLive(h)) return false;
            int n = AlliesLockedOn(m, h);
            bool iLock = false;
            try { iLock = Agent.GetTargetAgent() == h; } catch { }
            if (n - (iLock ? 1 : 0) >= 1)
                return true;
            foreach (Agent f in m.Agents)
            {
                if (!CombatLive(f) || f == Agent || !f.IsHuman || f.IsMainAgent || Agent.IsEnemyOf(f))
                    continue;
                if (GfEnemyOn.TryGetValue(f.Index, out int fe) && fe >= 2)
                    continue;                       // defender can't cover
                if (GfAssign.ContainsKey(f.Index))
                    continue;
                if (GfBusyUntil.TryGetValue(f.Index, out float bu) && _brainClock < bu)
                    continue;
                if ((f.Position.AsVec2 - h.Position.AsVec2).Length > 3f)
                    continue;
                Agent ft = null;
                try { ft = f.GetTargetAgent(); } catch { }
                if (CombatLive(ft) && (ft.Position.AsVec2 - f.Position.AsVec2).Length <= 2f * MeleeReach())
                    continue;                       // in contact -- can't cover
                GfAssign[f.Index] = (h.Index, _brainClock + SwapAssignTtl);
                return true;
            }
            return false;
        }

        // Cover-handoff newcomer legality: inside QuickRangeR*reach, in the
        // front arc, lane clear, uncovered or already attacking me.
        private Agent FindCoverTarget(Mission m, Agent cur, Vec2 pos, float reach)
        {
            Vec2 face = Agent.GetMovementDirection();
            if (face.LengthSquared < 0.0001f) face = Agent.LookDirection.AsVec2;
            for (int i = 0; i < _gfLookScan.Count; i++)
            {
                Agent e = _gfLookScan[i].e;
                float d = _gfLookScan[i].d;
                if (d > QuickRangeR * reach || !CombatLive(e) || e == cur)
                    continue;
                if (face.LengthSquared > 0.0001f && d > 0.0001f
                    && Vec2.DotProduct(FlatUnit(face), (e.Position.AsVec2 - pos) / d) < QuickArcDot)
                    continue;
                if (FriendInLane(e, pos))
                    continue;
                bool eOnMe = false;
                try { eOnMe = e.GetTargetAgent() == Agent; } catch { }
                if (!(AlliesLockedOn(m, e) == 0 || eOnMe))
                    continue;
                return e;
            }
            return null;
        }

        // 4aa/4z shared step-side pick (deffix + rigged defender): free
        // lane wins, else the side the intent prefers, else the ally side.
        private int GfPickStepSide(Vec2 pp, Vec2 ppN, Vec2 prefer)
        {
            if (_brainClock < _gfDefFixSideUntil)
                return _gfDefFixSide;
            float fp = _clear.NearestAgentInDir(Agent, pp, nonEnemiesOnly: true);
            float fn = _clear.NearestAgentInDir(Agent, ppN, nonEnemiesOnly: true);
            bool posOk = (fp > 1f || fp <= 0f) && _clear.WallFree(Agent, pp, _brainClock) >= 0.8f;
            bool negOk = (fn > 1f || fn <= 0f) && _clear.WallFree(Agent, ppN, _brainClock) >= 0.8f;
            int side;
            if (posOk != negOk)
                side = posOk ? 1 : -1;
            else if (prefer.LengthSquared > 0.0001f)
                side = Vec2.DotProduct(prefer, pp) >= 0f ? 1 : -1;
            else
            {
                Vec2 ad = Vec2.Zero;
                Mission m2 = Agent.Mission;
                if (m2 != null)
                    foreach (Agent al in m2.Agents)
                    {
                        if (!CombatLive(al) || al == Agent || !al.IsHuman || Agent.IsEnemyOf(al))
                            continue;
                        Vec2 rel = al.Position.AsVec2 - Agent.Position.AsVec2;
                        ad = new Vec2(ad.x + rel.x, ad.y + rel.y);
                    }
                side = ad.LengthSquared > 0.0001f
                    ? (Vec2.DotProduct(FlatUnit(ad), pp) >= 0f ? 1 : -1) : _gfDefFixSide;
            }
            _gfDefFixSide = side;
            _gfDefFixSideUntil = _brainClock + DefFixSideLatch;
            return side;
        }

        // Rigged-goal legality (the GfGoalCheck shape): friendly body under
        // 1m, wall probe, blocked-watch veto -- opposite dir as fallback.
        private Vec2 GfRigCheck(Vec2 g)
        {
            if (g.LengthSquared < 0.0001f) return Vec2.Zero;
            Vec2 d = FlatUnit(g);
            float fb = _clear.NearestAgentInDir(Agent, d, nonEnemiesOnly: true);
            bool bad = (fb > 0f && fb < 1f)
                || _clear.WallFree(Agent, d, _brainClock) < 0.8f
                || _clear.Watch.IsBlocked(d, _brainClock);
            if (!bad) return g;
            Vec2 o = new Vec2(-g.x, -g.y);
            Vec2 od = FlatUnit(o);
            float fb2 = _clear.NearestAgentInDir(Agent, od, nonEnemiesOnly: true);
            bool bad2 = (fb2 > 0f && fb2 < 1f)
                || _clear.WallFree(Agent, od, _brainClock) < 0.8f
                || _clear.Watch.IsBlocked(od, _brainClock);
            return bad2 ? Vec2.Zero : o;
        }

        private void GFsmLog(Mission m, string reason, Agent from, Agent to)
        {
            if (_gfsmLogCount >= MaxGfsmLogs || _brainClock - _gfsmLastAt < 2f)
                return;
            _gfsmLastAt = _brainClock;
            _gfsmLogCount++;
            float fd = CombatLive(from) ? (from.Position.AsVec2 - Agent.Position.AsVec2).Length : -1f;
            float td = CombatLive(to) ? (to.Position.AsVec2 - Agent.Position.AsVec2).Length : -1f;
            CombatAiLog.Log($"[GFSM] Agent #{Agent.Index} reason={reason} " +
                $"from=#{(CombatLive(from) ? from.Index.ToString() : "-")}({fd:F2}) " +
                $"to=#{(CombatLive(to) ? to.Index.ToString() : "-")}({td:F2}) " +
                $"att={_gfAttOnMe} state={_duelState}");
        }

        private static int _gsumTicks, _gsumDefender, _gsumPairChg;
        private static int _gsumQuickStart, _gsumQuickRet;
        private static int _gsumAtk, _gsumFeints, _gsumGapHold, _gsumDefGapHold, _gsumXl;
        private static int _gsumOnMePicks, _gsumGreedy2v1;
        private static int _gsumSpread110, _gsumDefFix, _gsumGapHoldND, _gsumBlkNormal, _gsumBlkHold;
        private static int _gsumSwaps, _gsumCovers, _gsumQuickPerm, _gsumSurpriseRolls;
        private static int _gsumAssist, _gsumStalled, _gsumStallLogs;
        private static int _gsumRigDef, _gsumRigAtk;
        private static float _gsumLastAt = -99f;
        internal static void GfSumTick(Mission m)
        {
            if (!CombatAiState.GroupFightMode || m == null) return;
            float now = m.CurrentTime;
            if (_gsumLastAt < -90f) { _gsumLastAt = now; return; }
            if (now - _gsumLastAt < 10f)
                return;
            CombatAiLog.Log($"[GFSM-SUM] t={now:F0} " +
                $"defender={(_gsumTicks > 0 ? 100f * _gsumDefender / _gsumTicks : 0f):F0}% " +
                $"quick={_gsumQuickStart}/{_gsumQuickRet} pairChg={_gsumPairChg} " +
                $"atk={_gsumAtk} feints={_gsumFeints} gapHold={_gsumGapHold} " +
                $"defGapHold={_gsumDefGapHold} xl={_gsumXl} " +
                $"onMePicks={_gsumOnMePicks} greedy2v1={_gsumGreedy2v1} " +
                $"spread110={_gsumSpread110} defFix={_gsumDefFix} gapHoldND={_gsumGapHoldND} " +
                $"blkNormD={_gsumBlkNormal} blkHoldD={_gsumBlkHold} " +
                $"swaps={_gsumSwaps} covers={_gsumCovers} quickPerm={_gsumQuickPerm} " +
                $"surpriseRolls={_gsumSurpriseRolls} rigDef={_gsumRigDef} rigAtk={_gsumRigAtk} " +
                $"assist={_gsumAssist} stalled={_gsumStalled} " +
                $"nativemove={(CombatAiState.GfNativemove ? 1 : 0)} samples={_gsumTicks}");
            _gsumTicks = _gsumDefender = _gsumPairChg = 0;
            _gsumQuickStart = _gsumQuickRet = 0;
            _gsumAtk = _gsumFeints = _gsumGapHold = _gsumDefGapHold = _gsumXl = 0;
            _gsumOnMePicks = _gsumGreedy2v1 = 0;
            _gsumSpread110 = _gsumDefFix = _gsumGapHoldND = _gsumBlkNormal = _gsumBlkHold = 0;
            _gsumSwaps = _gsumCovers = _gsumQuickPerm = _gsumSurpriseRolls = _gsumRigDef = _gsumRigAtk = 0;
            _gsumAssist = _gsumStalled = _gsumStallLogs = 0;
            _gsumLastAt = now;
        }

        // Free-look heading for the steer channel (4x): armed ONLY while
        // the targeting layer is active (defender or quick excursion) --
        // layer off = no free camera at all. Median signed bearing of the
        // threat set RELATIVE to the active target's bearing, clamped to
        // +/-LookClampDeg, slewed at LookSlewDegPerSec. Defender median =
        // the enemies targeting me (<=3 nearest); excursion median = the
        // close-threat set. _gfLookValid=false (=> the steer block keeps
        // face-the-lock) while an Up/Down attack is in flight -- a side
        // swing lands without facing the lock so the look runs through it.
        private void GroupComputeLook(Mission m, float dt)
        {
            _gfLookValid = false;
            bool attacking = _duelState == DuelState.Windup || _duelState == DuelState.FeintReady
                || _duelState == DuelState.FeintCancel || _duelState == DuelState.Release;
            bool layer = CombatAiState.GroupNativeTarget && _gfLayerActive;
            bool needLock = attacking && !(layer
                && _attackDir != Agent.MovementControlFlag.AttackUp
                && _attackDir != Agent.MovementControlFlag.AttackDown);
            if (!layer || needLock || !CombatLive(_gfPair))
            {
                _gfLookInit = false;   // re-seed the slew at the bearings next valid tick
                return;
            }
            Vec2 pos = Agent.Position.AsVec2;
            Vec2 toT = _gfPair.Position.AsVec2 - pos;
            if (toT.LengthSquared < 0.0001f)
            {
                _gfLookInit = false;
                return;
            }
            float refA = MathF.Atan2(toT.y, toT.x);
            _gfLookAng.Clear();
            int cnt = _gfDefenderActive ? MathF.Min(_gfAtk.Count, 3) : _gfLook.Count;
            for (int i = 0; i < cnt; i++)
            {
                Agent t = _gfDefenderActive ? _gfAtk[i] : _gfLook[i];
                if (!CombatLive(t)) continue;
                Vec2 to = t.Position.AsVec2 - pos;
                if (to.LengthSquared < 0.0001f) continue;
                _gfLookAng.Add(NormalizeAngle(MathF.Atan2(to.y, to.x) - refA));
            }
            if (_gfLookAng.Count == 0)
            {
                _gfLookInit = false;
                return;
            }
            _gfLookAng.Sort();
            float med = _gfLookAng[_gfLookAng.Count / 2];
            if ((_gfLookAng.Count & 1) == 0)
                med = (_gfLookAng[_gfLookAng.Count / 2 - 1] + med) * 0.5f;
            float medRaw = med;
            float clamp = LookClampDeg * 0.0174533f;
            if (med > clamp) med = clamp; else if (med < -clamp) med = -clamp;
            float desired = refA + med;
            if (!_gfLookInit)
            {
                _gfLookA = desired;
                _gfLookInit = true;
            }
            else
            {
                float d = NormalizeAngle(desired - _gfLookA);
                float step = LookSlewDegPerSec * 0.0174533f * MathF.Max(0f, dt);
                if (d > step) d = step; else if (d < -step) d = -step;
                _gfLookA = NormalizeAngle(_gfLookA + d);
            }
            _gfLookDir = new Vec2(MathF.Cos(_gfLookA), MathF.Sin(_gfLookA));
            _gfLookValid = true;
            if (_glookLogCount < MaxGlookLogs && _brainClock - _glookLastAt >= 1f)
            {
                _glookLastAt = _brainClock;
                _glookLogCount++;
                var sb = new StringBuilder(96);
                sb.Append($"[GLOOK] Agent #{Agent.Index}: threats=[");
                for (int i = 0; i < cnt; i++)
                {
                    if (i > 0) sb.Append(',');
                    Agent t = _gfDefenderActive ? _gfAtk[i] : _gfLook[i];
                    sb.Append('#').Append(t != null ? t.Index : -1);
                }
                sb.Append($"] median={medRaw * 57.2958f:F0} clamped={med * 57.2958f:F0} " +
                    $"attacking=0 lock=#{_gfPair.Index} state={_duelState}");
                CombatAiLog.Log(sb.ToString());
            }
        }

        private void LogGSwitch(string reason, Agent from, Agent to, float dist)
        {
            if (_gswitchLogCount >= MaxGswitchLogs) return;
            _gswitchLogCount++;
            CombatAiLog.Log($"[GSWITCH] Agent #{Agent.Index}: reason={reason} " +
                $"from={(CombatLive(from) ? "#" + from.Index : "-")} " +
                $"to={(CombatLive(to) ? "#" + to.Index : "-")} " +
                $"dist={dist:F2} state={_duelState}");
        }
    }
}
