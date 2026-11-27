using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =======================================================================
    // CUSTOM-BATTLE NATIVE-TARGET REPLICA (log-only, parallel, NO override).
    // A managed copy of native's per-candidate target scoring
    // (compute_basic_score, retail 0x69def0 / SJ 0x180c97230; terms a-p in
    // group_fight_decode.txt section 1) that runs BESIDE the native target
    // system in custom battles. We never call SetTargetAgent /
    // SetAutomaticTargetSelection here: native keeps full control, this only
    // logs [CBTGT] lines (our pick + native's pick + the factor breakdown)
    // so the replica can be validated/tuned against native's real choices.
    // Terms marked ~ are managed approximations of a native read we cannot
    // make exactly (documented per factor).
    // =======================================================================
    public partial class CombatAiOverrideComponent
    {
        // last_hitting_agent_index (Agent_ai+0x2398, x6 REVENGE term): tracked
        // from OnAgentHit while the replica is armed. Static so the mission
        // behavior can feed it.
        private static readonly Dictionary<int, int> _cbLastHitter = new Dictionary<int, int>(64);

        public static void CbRecordHit(Agent affected, Agent affector)
        {
            if (affected == null || affector == null)
                return;
            _cbLastHitter[affected.Index] = affector.Index;
        }

        public static void CbClearHits()
        {
            _cbLastHitter.Clear();
        }

        // Candidate record for the per-agent pick.
        private struct CbCand
        {
            public Agent A;
            public float Dist;
            public float Score;
            public float FacingF, SeenF, ReachF, LoyalF, RevF, PairBonus, CrowdF, InterceptF, ZF, MoveF;
        }

        private float _cbTickAt = -99f;
        private int _cbLogCount;
        private const int MaxCbLogs = 4000;           // fight-phase gating bounds real volume; keep the phase fully logged
        private const float CbThrottleSeconds = 1.0f;
        private const float CbScanRange = 45f;          // prefilter radius for the dist check
        private const float CbContactRange = 30f;       // native pick within this = "contact phase"
        private const float CbInterceptSeconds = 0.5f;  // sb_select_deadline floor
        // Stickiness gate (native score_target_switch): switch only when the
        // candidate is within 2.5 s of combined top speed (W_FARGATE_2P5) AND
        // its meet-time (d/closing) is < 3.0 s (DAT_18149fd14).
        private const float CbSwitchMaxMeters = 12.5f;
        private const float CbSwitchMeetSeconds = 3.0f;
        private Agent _cbPick;                          // held pick (sticky)
        private float _cbPickScore;
        private readonly List<CbCand> _cbCands = new List<CbCand>(16);

        // Phase-separated agree counters, logged every 5s by CbSummaryTick.
        private static int _cbSumNear, _cbSumNearAgree, _cbSumFar, _cbSumFarAgree, _cbSumNone;
        private static float _cbSumAt = -99f;
        private static int _cbSumLogCount;
        private const int MaxCbSumLogs = 60;
        private const float CbSumIntervalSeconds = 5f;

        // target.Index -> number of AI agents currently locked on it, rebuilt
        // ONCE per frame by CbBuildTargetCounts (the crowd/rank term). The old
        // per-candidate full-mission scan was O(n^2) with ~380 agents and was
        // a real per-tick cost -- the lag source in log 56.
        private static readonly Dictionary<int, int> CbTargetCounts = new Dictionary<int, int>(256);

        internal static void CbBuildTargetCounts(Mission m)
        {
            CbTargetCounts.Clear();
            if (m == null)
                return;
            foreach (Agent a in m.Agents)
            {
                if (a == null || !a.IsActive() || !a.IsAIControlled || !a.IsHuman)
                    continue;
                Agent t = null;
                try { t = a.GetTargetAgent(); }
                catch { }
                if (t != null)
                    CbTargetCounts[t.Index] = CbTargetCounts.TryGetValue(t.Index, out int c) ? c + 1 : 1;
            }
        }

        // Runs from CombatAiMissionBehavior.OnMissionTick while
        // CombatAiState.CbTargetMode is on (custom battles only).
        internal void CbTargetTick(float dt, Mission m)
        {
            if (m == null || !CombatLive(Agent) || !Agent.IsAIControlled)
                return;
            // FIGHT-PHASE GATE (user): only compare while OUR formation is
            // under a player-issued Charge/ChargeToTarget. During advance/
            // move native's alarm-driven acquisition dominates (low agree is
            // structural); the rout phase is trivial. This also cuts the log
            // volume and the per-tick work the approach phase caused.
            Formation f0 = Agent.Formation;
            if (f0 == null)
                return;
            MovementOrder.MovementOrderEnum oe;
            try { oe = f0.GetReadonlyMovementOrderReference().OrderEnum; }
            catch { return; }
            if (oe != MovementOrder.MovementOrderEnum.Charge
                && oe != MovementOrder.MovementOrderEnum.ChargeToTarget)
                return;

            // Throttle on MISSION time: _brainClock only advances inside driven
            // modes, which are never armed in a custom battle -- using it here
            // froze the gate at 0 and suppressed every [CBTGT] line (log 55).
            float now = m.CurrentTime;
            if (now < _cbTickAt)
                return;
            _cbTickAt = now + CbThrottleSeconds;

            Vec3 selfPos = Agent.Position;
            Vec2 selfPos2 = selfPos.AsVec2;
            float reach = MeleeReach();
            Vec2 look = Agent.LookDirection.AsVec2;
            if (look.LengthSquared < 0.0001f)
                look = Agent.GetMovementDirection();
            if (look.LengthSquared < 0.0001f)
                look = Vec2.Zero;

            // Native pick for comparison.
            Agent nativePick = Agent.GetTargetAgent();
            // tfi (target formation index): -1 under Move/Advance, >=0 under
            // ChargeToTarget -- feeds the formation-pair bonus term.
            int tfi = -1;
            try { tfi = Agent.GetTargetFormationIndex(); }
            catch { }

            // Prefilter by raw distance, then full score the near set.
            _cbCands.Clear();
            for (int i = 0; i < m.Agents.Count; i++)
            {
                Agent e = m.Agents[i];
                if (!CombatLive(e) || e == Agent || e.HasMount || !Agent.IsEnemyOf(e))
                    continue;
                float d = (e.Position.AsVec2 - selfPos2).Length;
                if (d > CbScanRange)
                    continue;
                _cbCands.Add(new CbCand { A = e, Dist = d });
            }
            if (_cbCands.Count == 0)
                return;

            // Cap the scored set: score the nearest 8 by raw dist first.
            _cbCands.Sort((x, y) => x.Dist.CompareTo(y.Dist));
            int scored = Math.Min(8, _cbCands.Count);
            Agent best = null;
            float bestScore = float.NegativeInfinity;
            float bestDist = float.MaxValue;
            for (int k = 0; k < scored; k++)
            {
                CbCand c = _cbCands[k];
                float score = CbScore(c.A, selfPos2, look, reach, nativePick, tfi, out float fac, out float seen,
                    out float rch, out float loy, out float rev, out float pair, out float crowd, out float itc,
                    out float zf, out float mvf);
                c.Score = score;
                c.FacingF = fac; c.SeenF = seen; c.ReachF = rch; c.LoyalF = loy; c.RevF = rev;
                c.PairBonus = pair; c.CrowdF = crowd; c.InterceptF = itc; c.ZF = zf; c.MoveF = mvf;
                _cbCands[k] = c;
                if (score > bestScore || (score == bestScore && c.Dist < bestDist))
                {
                    bestScore = score;
                    best = c.A;
                    bestDist = c.Dist;
                }
            }
            if (best == null)
                return;

            // STICKINESS (native score_target_switch): hold the last pick
            // unless the new best is genuinely switchable -- inside 2.5 s of
            // combined top speed (12.5 m) AND meet-time d/closing < 3.0 s.
            // Log 60's 0% endgame: enemies running at ~27 m fail the gate, so
            // native holds its target while we re-picked max every tick (and
            // rev x6 flipped us to a fresh enemy at identical distance).
            Agent held = _cbPick != null && CombatLive(_cbPick) && Agent.IsEnemyOf(_cbPick)
                ? _cbPick : null;
            if (held != null && best != held)
            {
                Vec2 toBest = best.Position.AsVec2 - selfPos2;
                float bd = toBest.Length;
                Vec2 relV = (best.Velocity - Agent.Velocity).AsVec2;
                toBest.Normalize();
                float closing = MathF.Max(0.5f, -Vec2.DotProduct(relV, toBest));
                bool canSwitch = bd <= CbSwitchMaxMeters && (bd / closing) < CbSwitchMeetSeconds;
                if (!canSwitch)
                {
                    best = held;
                    bestScore = _cbPickScore;
                    bestDist = (held.Position.AsVec2 - selfPos2).Length;
                }
            }
            _cbPick = best;
            _cbPickScore = bestScore;

            // Top-3 by score for the log.
            for (int k = 1; k < scored; k++)
                for (int j = k; j > 0 && _cbCands[j - 1].Score < _cbCands[j].Score; j--)
                {
                    CbCand t = _cbCands[j - 1]; _cbCands[j - 1] = _cbCands[j]; _cbCands[j] = t;
                }

            CbCand top = _cbCands[0];
            bool agree = nativePick != null && nativePick.Index == best.Index;
            // Phase-separated summary counters: near = native pick within
            // contact range, far = beyond (approach phase), none = native has
            // no target. Logged every 5s by CbSummaryTick as [CBTGT-SUM].
            float nativeD = nativePick != null ? (nativePick.Position.AsVec2 - selfPos2).Length : -1f;
            if (nativePick == null) { _cbSumNone++; }
            else if (nativeD <= CbContactRange) { _cbSumNear++; if (agree) _cbSumNearAgree++; }
            else { _cbSumFar++; if (agree) _cbSumFarAgree++; }

            if (_cbLogCount >= MaxCbLogs)
                return;
            _cbLogCount++;
            string top3 = "";
            for (int k = 0; k < Math.Min(3, scored); k++)
                top3 += $"#{_cbCands[k].A.Index}({_cbCands[k].Score:F1}) ";
            // Our formation + movement order + target-formation index, so the
            // log shows how the order set on our unit shapes targeting.
            string form = "-";
            string order = "-";
            Formation f = Agent.Formation;
            if (f != null)
            {
                form = f.Index + ":" + f.LogicalClass;
                try { order = f.GetReadonlyMovementOrderReference().OrderEnum.ToString(); }
                catch { }
            }
            CombatAiLog.Log($"[CBTGT] #{Agent.Index}: form={form}:{order} tfi={tfi} " +
                $"native=#{(nativePick != null ? nativePick.Index.ToString() : "-")}" +
                $"(d={nativeD:F2}) " +
                $"ours=#{best.Index}(d={bestDist:F2}) agree={(agree ? "Y" : "N")} score={bestScore:F1} " +
                $"top=[{top3.Trim()}] f=[fac={top.FacingF:F2} seen={top.SeenF:F2} reach={top.ReachF:F1} " +
                $"loy={top.LoyalF:F1} rev={top.RevF:F1} pair={top.PairBonus:F0} crowd={top.CrowdF:F2} " +
                $"itc={top.InterceptF:F1} z={top.ZF:F2} mv={top.MoveF:F1}]");
        }

        // Multiplicative replica of compute_basic_score's terms (a-p). Returns
        // the score and the per-factor breakdown for the log. ~ = approximate.
        private float CbScore(Agent e, Vec2 selfPos2, Vec2 look, float reach, Agent nativePick, int myTfi,
            out float fac, out float seen, out float rch, out float loy, out float rev, out float pair,
            out float crowd, out float itc, out float zf, out float mvf)
        {
            fac = seen = rch = loy = rev = pair = crowd = itc = zf = mvf = 1f;

            Vec3 ePos = e.Position;
            Vec2 toE = ePos.AsVec2 - selfPos2;
            float dist = toE.Length;
            if (dist < 0.0001f)
                return 0f;
            toE.Normalize();

            // (d) GEOMETRY base + Z quartic 200/(dz^4*0.198+200) -- steep height rejection.
            float dz = MathF.Abs(ePos.z - Agent.Position.z);
            zf = 200f / (dz * dz * dz * dz * 0.198f + 200f);
            // Base distance decay softened (log 56: the first 1/(1+0.1d) made
            // EVERY agent converge on one near outlier while native spreads
            // picks across the enemy line during approach).
            float baseScore = 1f / (1f + dist * 0.05f) * zf;

            // (b) NAV reachability -- native rejects outright; we veto like PathOk.
            // Skipped at close range (fight-phase candidates are at contact;
            // the native call is expensive and the check is an approach term).
            if (dist > 10.0f)
            {
                Vec3 pt = ePos;
                float pathD = Agent.GetPathDistanceToPoint(ref pt);
                if (pathD >= 100000f || pathD > dist * 2.5f)
                    return 0f;   // unreachable candidate: native would reject it
            }

            // (e) INTERCEPT: predicted separation at t=CbInterceptSeconds with both velocities.
            float t = CbInterceptSeconds;
            Vec3 pSelf = Agent.Position + Agent.Velocity * t;
            Vec3 pCand = ePos + e.Velocity * t;
            float predSep = (pCand - pSelf).Length;
            itc = predSep <= reach ? 2f : predSep <= reach * 1.5f ? 1.5f : 1f;   // ~ in-reach bonus tiers

            // (f) FACING piecewise curve on dot(look, dirToCand).
            float cosF = look.LengthSquared > 0.0001f ? Vec2.DotProduct(look.Normalized(), toE) : 0f;
            if (cosF > 0.966f) fac = 12.98f * cosF - 11.18f;
            else if (cosF > 0.875f) fac = 2.98f * cosF - 1.52f;
            else if (cosF > 0f) fac = 0.1f * cosF + 1f;
            else fac = 1f;

            // (h) REACH x2 when already inside our reach.
            rch = dist <= reach ? 2f : 1f;

            // (i) SEEN-BY-ENEMY: dot(-dirToUs, enemy look)^3 clamp [0.125,1].
            Vec2 eLook = e.LookDirection.AsVec2;
            if (eLook.LengthSquared < 0.0001f) eLook = e.GetMovementDirection();
            float seenDot = eLook.LengthSquared > 0.0001f
                ? Vec2.DotProduct(eLook.Normalized(), -toE) : 0f;
            float sd = MathF.Max(0f, seenDot);
            seen = MathF.Max(0.125f, MathF.Min(1f, sd * sd * sd));

            // (j) LOYALTY x1.2: candidate is our current native target (stickiness).
            loy = nativePick != null && nativePick.Index == e.Index ? 1.2f : 1f;

            // (k) REVENGE x6: candidate hit us last.
            int lastHit;
            rev = _cbLastHitter.TryGetValue(Agent.Index, out lastHit) && lastHit == e.Index ? 6f : 1f;

            // (l) FORMATION PAIR x2 + 65: enemies in the formation we target under
            // ChargeToTarget. Native compares enemy+0x4fc to our pair idx;
            // managed-visible approximation = enemy Formation.Index == our
            // GetTargetFormationIndex() (tfi). Move/Advance keep tfi=-1 ->
            // no bonus (log 56 confirmed tfi=-1 the whole fight).
            pair = 0f;
            float pairF = 1f;
            if (myTfi >= 0 && e.Formation != null && e.Formation.Index == myTfi)
            {
                pairF = 2f;
                pair = 65f;
            }

            // (n) MOVING-AWAY x0.25: the CANDIDATE's own velocity along the
            // line away from us (first version used relative velocity with the
            // sign inverted: mutual approach scored as "moving away" and
            // penalized every closing pair -- log 56 showed mv=0.3 on all).
            Vec2 eVel = e.Velocity.AsVec2;
            mvf = Vec2.DotProduct(eVel, toE) > 0.5f ? 0.25f : 1f;

            float score = baseScore * itc * fac * rch * seen * loy * rev * pairF * mvf + pair;

            // (2) CROWD (apply_crowd_adjustment): rank>4 -> score *= 0.84/(1+0.012*rank).
            // Rank from the once-per-frame map; self-excluded like native's
            // num_agents_targeting ("minus 1 if enemy is my current target").
            int rank = CbTargetCounts.TryGetValue(e.Index, out int r) ? r : 0;
            if (rank > 0 && nativePick != null && nativePick.Index == e.Index)
                rank--;
            crowd = rank > 4 ? 0.84f / (1f + 0.012f * rank) : 1f;
            score *= crowd;
            return score;
        }
    // Per-team formation/order summary ([CBFORM], throttled ~5s): the
        // order set on each of the (up to 8) team formation slots, so the log
        // shows how orders shape the targeting picture. Called from the
        // mission behavior alongside the per-agent replica tick.
        private static float _cbFormLastAt = -99f;
        private static int _cbFormLogCount;
        private const int MaxCbFormLogs = 120;
        private const float CbFormIntervalSeconds = 5f;

        internal static void CbFormationTick(float dt, Mission m)
        {
            if (m == null)
                return;
            float now = m.CurrentTime;
            if (now < _cbFormLastAt)
                return;
            _cbFormLastAt = now + CbFormIntervalSeconds;
            if (_cbFormLogCount >= MaxCbFormLogs)
                return;
            _cbFormLogCount++;

            var seen = new HashSet<Team>();
            foreach (Agent a in m.Agents)
            {
                if (a == null || !a.IsActive() || a.Team == null || a.Formation == null)
                    continue;
                if (!seen.Add(a.Team))
                    continue;
                // One summary per team: iterate its 8 formation slots.
                var sb = new System.Text.StringBuilder(96);
                sb.Append($"[CBFORM] team={(a.Team.Side == BattleSideEnum.Attacker ? "A" : a.Team.Side == BattleSideEnum.Defender ? "D" : "N")} slots=[");
                for (int i = 0; i < a.Team.FormationsIncludingEmpty.Count; i++)
                {
                    Formation f = a.Team.FormationsIncludingEmpty[i];
                    if (f == null || f.CountOfUnits <= 0)
                        continue;
                    string order = "-";
                    try { order = f.GetReadonlyMovementOrderReference().OrderEnum.ToString(); }
                    catch { }
                    sb.Append($"{f.Index}:{f.LogicalClass}={order}(n={f.CountOfUnits}) ");
                }
                sb.Append("]");
                CombatAiLog.Log(sb.ToString());
            }
        }

        // Phase-separated agree summary: near (native pick <= CbContactRange)
        // vs far (approach) vs none, every 5s -- the single number that tells
        // whether the replica matches native where it matters (at contact).
        internal static void CbSummaryTick(float dt, Mission m)
        {
            if (m == null)
                return;
            float now = m.CurrentTime;
            if (now < _cbSumAt)
                return;
            _cbSumAt = now + CbSumIntervalSeconds;
            if (_cbSumLogCount >= MaxCbSumLogs || (_cbSumNear + _cbSumFar + _cbSumNone) == 0)
                return;
            _cbSumLogCount++;
            float nearRate = _cbSumNear > 0 ? 100f * _cbSumNearAgree / _cbSumNear : -1f;
            float farRate = _cbSumFar > 0 ? 100f * _cbSumFarAgree / _cbSumFar : -1f;
            CombatAiLog.Log($"[CBTGT-SUM] near={_cbSumNear}(agree {nearRate:F0}%) " +
                $"far={_cbSumFar}(agree {farRate:F0}%) none={_cbSumNone}");
            _cbSumNear = _cbSumNearAgree = _cbSumFar = _cbSumFarAgree = _cbSumNone = 0;
        }
    }
}