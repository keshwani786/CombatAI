using System;
using System.Collections.Generic;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =====================================================================
    // GROUP LINEUP (stage 1 of the group-fight rebuild, combatai.groupfree)
    // =====================================================================
    // Two hostile AI lines walk up facing each other, equal spacing, stop at
    // ~gap metres, wait `wait` seconds with no attacks, then the fight starts
    // (legacy group brain, stage 2 replaces it). NOT the game's formation
    // system: slots are computed here, per-agent flags + MovementInputVector
    // in ComputeGroupLineFlag (CombatAiOverrideComponent.GroupLine.cs), and
    // facing comes from the shared free driver (groupLineFree).
    //
    // Phases: Idle -> Form (walk to staging slots 5m back) -> Advance
    // (lockstep line approach) -> Wait (stand facing counterpart) -> Fight.
    // Static per mission; stage 2 reads Phase/FightStarted.
    public static class GroupLineup
    {
        public enum Ph { Idle, Form, Advance, Wait, Fight }
        public static Ph Phase = Ph.Idle;
        public static bool FightStarted;
        public static bool Armed;

        private const float StageBackMeters = 5f;

        // Per-slot table (parallel arrays, index into _agents of team t).
        private sealed class Line
        {
            public Team Team;
            public List<int> Agents = new List<int>(6);      // index-sorted lateral order
            public readonly List<Vec2> Slots = new List<Vec2>(6);  // final positions
            public Vec2 Facing;                              // unit dir toward enemy line
            public Vec2 Centre;
            public float Adv;                                // lockstep progress 0..StageBack
            public float PausedFor;                          // seconds the line has been waiting on a straggler
            public int CounterOf(int i) => OppSlot(i);
            public Line Opp;
            public int OppSlot(int i)                        // mirrored lateral index
            {
                if (Opp == null || Opp.Agents.Count == 0) return -1;
                int j = Opp.Agents.Count - 1 - i;
                if (j < 0) j = 0;
                if (j >= Opp.Agents.Count) j = Opp.Agents.Count - 1;
                return Opp.Agents[j];
            }
        }
        private static Line _a, _b;
        private static float _phaseAt, _waitStart = -1f;
        private static float _lastTickClock = -99f;
        private static int _logCount;
        private const int MaxGlineLogs = 300;
        private static bool _slotsLogged;
        private static readonly Dictionary<int, Vec2> _goalOf = new Dictionary<int, Vec2>(12);
        private static readonly Dictionary<int, Vec2> _faceOf = new Dictionary<int, Vec2>(12);

        public static void Reset()
        {
            Phase = Ph.Idle;
            FightStarted = false;
            Armed = false;
            _a = _b = null;
            _waitStart = -1f;
            _lastTickClock = -99f;
            _lastMetricsAt = -99f;
            _advDoneAt = -1f;
            _slotsLogged = false;
            _goalOf.Clear();
            _faceOf.Clear();
        }

        // Per-agent lookups for ComputeGroupLineFlag + the free driver.
        public static bool GoalOf(int idx, out Vec2 g) => _goalOf.TryGetValue(idx, out g);
        public static bool FacingOf(int idx, out Vec2 f) => _faceOf.TryGetValue(idx, out f);
        public static Vec2 FacingDir(int idx) => _faceOf.TryGetValue(idx, out Vec2 f) ? f : Vec2.Zero;

        // Stage-2 pair fight (user spec): the agent's unique opposite-line
        // target = the mirrored counterpart for its slot. -1 when the agent
        // isn't in a line.
        public static int CounterpartOf(int agentIdx)
        {
            foreach (Line l in new[] { _a, _b })
            {
                if (l == null) continue;
                int k = l.Agents.IndexOf(agentIdx);
                if (k >= 0)
                    return l.CounterOf(k);
            }
            return -1;
        }

        private static void Log(string s)
        {
            if (_logCount < MaxGlineLogs) { _logCount++; CombatAiLog.Log(s); }
        }

        // Ticked once per frame from any group-line agent's flag fn (guarded
        // by _lastTickClock -- every driven agent runs the same _brainClock).
        public static void Tick(float dt, float brainClock, Mission m)
        {
            if (!CombatAiState.GroupFightMode || !CombatAiState.GroupFree || m == null)
            {
                if (Phase != Ph.Idle && Phase != Ph.Fight)
                    Reset();
                return;
            }
            // Mission-global clock: per-agent _brainClock values differ (each
            // component accumulates from its own start), so they can neither
            // dedupe the per-frame tick nor drive the phase timers.
            float now = m.CurrentTime;
            if (MathF.Abs(now - _lastTickClock) < 0.00001f)
                return;   // another line agent already ticked this frame
            dt = now > _lastTickClock && _lastTickClock > -50f ? MathF.Min(0.1f, now - _lastTickClock) : 0f;
            _lastTickClock = now;
            brainClock = now;

            if (!Armed)
            {
                if (TryArm(m))
                    Armed = true;
                else
                    return;
            }
            switch (Phase)
            {
                case Ph.Idle:
                    Phase = Ph.Form;
                    _phaseAt = brainClock;
                    Log($"[GLINE] phase=Form t={brainClock:F1}");
                    break;
                case Ph.Form:
                    if (AllStaged(0.6f) || brainClock - _phaseAt > 20f)
                    {
                        if (brainClock - _phaseAt > 20f)
                            LogLate();
                        Phase = Ph.Advance;
                        _phaseAt = brainClock;
                        _a.Adv = _b.Adv = 0f;
                        _a.PausedFor = _b.PausedFor = 0f;
                        _advDoneAt = -1f;
                        Log($"[GLINE] phase=Advance t={brainClock:F1}");
                    }
                    break;
                case Ph.Advance:
                    TickAdvance(dt, brainClock);
                    break;
                case Ph.Wait:
                    if (_waitStart < 0f) _waitStart = brainClock;
                    {
                        float left = CombatAiState.GroupWaitSeconds - (brainClock - _waitStart);
                        int c = (int)Math.Ceiling(left);
                        if (c == 10 || c == 5 || c <= 0)
                            Log($"[GLINE] wait t-{Math.Max(0, left):F0}");
                    }
                    if (brainClock - _waitStart >= CombatAiState.GroupWaitSeconds)
                    {
                        Phase = Ph.Fight;
                        FightStarted = true;
                        _phaseAt = brainClock;
                        GroupEngagement.Reset();   // fresh claim registry for the fight
                        Log($"[GLINE] FIGHT START t={brainClock:F1}");
                    }
                    break;
            }
            RebuildGoals();
            PerSecMetrics(brainClock);
        }

        // ---- ARM: discover the two teams (same ArenaRoster rule as
        // GroupFormationAdvance.Arm, fallback: two largest hostile AI teams)
        // and lay out the slot geometry.
        private static bool TryArm(Mission m)
        {
            var byTeam = new Dictionary<Team, List<Agent>>(4);
            bool rostered = CombatAiState.ArenaRoster != null && CombatAiState.ArenaRoster.Count > 0;
            foreach (Agent a in m.Agents)
            {
                if (!a.IsActive() || !a.IsHuman || a.IsMainAgent || a.Team == null)
                    continue;
                if (rostered && !CombatAiState.ArenaRoster.Contains(a.Index))
                    continue;
                if (!byTeam.TryGetValue(a.Team, out List<Agent> l))
                {
                    l = new List<Agent>(6);
                    byTeam[a.Team] = l;
                }
                l.Add(a);
            }
            if (byTeam.Count < 2)
                return false;
            var teams = new List<KeyValuePair<Team, List<Agent>>>(byTeam);
            teams.Sort((x, y) => y.Value.Count.CompareTo(x.Value.Count));
            List<Agent> ta = teams[0].Value, tb = teams[1].Value;
            Vec2 ca = Centroid(ta), cb = Centroid(tb);
            Vec2 axis = cb - ca;
            float al = axis.Length;
            if (al < 0.1f) return false;
            axis = new Vec2(axis.x / al, axis.y / al);
            Vec2 perp = new Vec2(-axis.y, axis.x);
            Vec2 c = (ca + cb) * 0.5f;
            float gap = CombatAiState.GroupGapMeters;
            float sp = CombatAiState.GroupSpacingMeters;
            _a = BuildLine(teams[0].Key, ta, c - axis * (gap * 0.5f), axis, perp, sp);
            _b = BuildLine(teams[1].Key, tb, c + axis * (gap * 0.5f), new Vec2(-axis.x, -axis.y), perp, sp);
            _a.Opp = _b;
            _b.Opp = _a;
            Phase = Ph.Idle;
            _phaseAt = 0f;
            return true;
        }

        // Sort a team's agents by lateral coordinate and assign slots with no
        // crossings; slots sit at lateral (k-(n-1)/2)*spacing around centre.
        private static Line BuildLine(Team t, List<Agent> agents, Vec2 centre, Vec2 facing, Vec2 perp, float sp)
        {
            var line = new Line { Team = t, Facing = facing, Centre = centre };
            var sorted = new List<Agent>(agents);
            sorted.Sort((x, y) =>
                Vec2.DotProduct(x.Position.AsVec2 - centre, perp)
                    .CompareTo(Vec2.DotProduct(y.Position.AsVec2 - centre, perp)));
            int n = sorted.Count;
            for (int k = 0; k < n; k++)
            {
                line.Agents.Add(sorted[k].Index);
                line.Slots.Add(centre + perp * ((k - (n - 1) * 0.5f) * sp));
            }
            return line;
        }

        private static Vec2 Centroid(List<Agent> l)
        {
            Vec2 s = Vec2.Zero;
            foreach (Agent a in l) s += a.Position.AsVec2;
            return l.Count > 0 ? s * (1f / l.Count) : Vec2.Zero;
        }

        // Staging slot = final slot pushed 5m back along -facing.
        private static Vec2 Staging(Line l, int k) => l.Slots[k] - l.Facing * StageBackMeters;

        private static bool AllStaged(float tol)
        {
            return LineStaged(_a, tol) && LineStaged(_b, tol);
        }

        private static bool LineStaged(Line l, float tol)
        {
            for (int k = 0; k < l.Agents.Count; k++)
            {
                Agent a = AgentOf(l.Agents[k]);
                if (a == null || !a.IsActive()) continue;   // dead: slot freed
                if ((a.Position.AsVec2 - Staging(l, k)).Length > tol)
                    return false;
            }
            return true;
        }

        private static void LogLate()
        {
            foreach (Line l in new[] { _a, _b })
                for (int k = 0; k < l.Agents.Count; k++)
                {
                    Agent a = AgentOf(l.Agents[k]);
                    if (a == null || !a.IsActive()) continue;
                    Vec2 st = Staging(l, k);
                    if ((a.Position.AsVec2 - st).Length > 0.6f)
                        Log($"[GLINE] LATE Agent #{a.Index} team={(l == _a ? "A" : "B")} pos=({a.Position.AsVec2.x:F1},{a.Position.AsVec2.y:F1}) goal=({st.x:F1},{st.y:F1})");
                }
        }

        // Lockstep: the line pauses while its worst straggler is >0.6m behind
        // its moving goal so nobody is left behind or stretched.
        private static void TickAdvance(float dt, float brainClock)
        {
            TickAdvanceLine(_a, dt);
            TickAdvanceLine(_b, dt);
            bool advDone = _a.Adv >= StageBackMeters && _b.Adv >= StageBackMeters;
            if (advDone && _advDoneAt < 0f)
                _advDoneAt = brainClock;
            // Agents decelerate to a crawl near the slot and can park ~0.4-0.6 m
            // short (input-vector deadzone), so 0.35 m never arrives. Accept 0.7 m,
            // and never wait on stragglers longer than 6 s (or 45 s total).
            bool arrived = advDone && AllArrived(0.7f);
            bool graceOver = advDone && brainClock - _advDoneAt > 6f;
            bool advTimeout = brainClock - _phaseAt > 45f;
            if (arrived || graceOver || advTimeout)
            {
                if (!arrived)
                    LogLate();
                Phase = Ph.Wait;
                _phaseAt = brainClock;
                _waitStart = -1f;
                Log($"[GLINE] phase=Wait t={brainClock:F1}{(arrived ? "" : " (forced: stragglers/timeout)")}");
            }
        }
        private static float _advDoneAt = -1f;

        private static void TickAdvanceLine(Line l, float dt)
        {
            float maxLag = MaxLag(l);
            // A straggler pauses the line, but never longer than 3 s in a row
            // (a stuck agent must not freeze both lines for the whole fight).
            if (maxLag < 0.6f) l.PausedFor = 0f; else l.PausedFor += dt;
            if ((maxLag < 0.6f || l.PausedFor > 3f) && l.Adv < StageBackMeters)
                l.Adv = MathF.Min(StageBackMeters, l.Adv + CombatAiState.GroupSpeedMeters * dt);
        }

        private static float MaxLag(Line l)
        {
            float worst = 0f;
            for (int k = 0; k < l.Agents.Count; k++)
            {
                Agent a = AgentOf(l.Agents[k]);
                if (a == null || !a.IsActive()) continue;
                Vec2 g = l.Slots[k] - l.Facing * (StageBackMeters - l.Adv);
                float d = (a.Position.AsVec2 - g).Length;
                if (d > worst) worst = d;
            }
            return worst;
        }

        private static bool AllArrived(float tol)
        {
            foreach (Line l in new[] { _a, _b })
                for (int k = 0; k < l.Agents.Count; k++)
                {
                    Agent a = AgentOf(l.Agents[k]);
                    if (a == null || !a.IsActive()) continue;
                    if ((a.Position.AsVec2 - l.Slots[k]).Length > tol)
                        return false;
                }
            return true;
        }

        // Per-agent moving goals + facing for the flag fn (rebuilt per tick;
        // cheap, <=10 agents).
        private static void RebuildGoals()
        {
            _goalOf.Clear();
            _faceOf.Clear();
            foreach (Line l in new[] { _a, _b })
            {
                if (l == null) continue;
                for (int k = 0; k < l.Agents.Count; k++)
                {
                    Vec2 g;
                    switch (Phase)
                    {
                        case Ph.Form:    g = Staging(l, k); break;
                        case Ph.Advance: g = l.Slots[k] - l.Facing * (StageBackMeters - l.Adv); break;
                        default:         g = l.Slots[k]; break;   // Wait: stand on slot
                    }
                    _goalOf[l.Agents[k]] = g;
                    _faceOf[l.Agents[k]] = l.Facing;
                }
            }
            if (!_slotsLogged)
            {
                _slotsLogged = true;
                foreach (Line l in new[] { _a, _b })
                    for (int k = 0; k < l.Agents.Count; k++)
                    {
                        int cp = l.CounterOf(k);
                        Log($"[GLINE] slot Agent #{l.Agents[k]} team={(l == _a ? "A" : "B")} k={k} " +
                            $"pos=({l.Slots[k].x:F1},{l.Slots[k].y:F1}) counterpart=#{(cp >= 0 ? cp.ToString() : "-")}");
                    }
            }
        }

        // 1.0s metrics while engaged: line straightness / spacing / facing.
        private static float _lastMetricsAt = -99f;
        private static void PerSecMetrics(float brainClock)
        {
            if (Phase != Ph.Advance && Phase != Ph.Wait) return;
            if (brainClock - _lastMetricsAt < 1.0f) return;
            _lastMetricsAt = brainClock;
            foreach (Line l in new[] { _a, _b })
            {
                if (l == null) continue;
                float maxDev = 0f, spErr = 0f, faceErr = 0f; int nLive = 0, nPairs = 0;
                Vec2 prevSlot = Vec2.Zero; bool havePrev = false;
                for (int k = 0; k < l.Agents.Count; k++)
                {
                    Agent a = AgentOf(l.Agents[k]);
                    if (a == null || !a.IsActive()) continue;
                    nLive++;
                    Vec2 g = Phase == Ph.Advance
                        ? l.Slots[k] - l.Facing * (StageBackMeters - l.Adv) : l.Slots[k];
                    Vec2 rel = a.Position.AsVec2 - g;
                    // Lateral deviation = |cross(facing, rel)|
                    float dev = MathF.Abs(l.Facing.x * rel.y - l.Facing.y * rel.x);
                    if (dev > maxDev) maxDev = dev;
                    if (havePrev)
                        spErr += MathF.Abs((l.Slots[k] - prevSlot).Length - CombatAiState.GroupSpacingMeters);
                    prevSlot = l.Slots[k];
                    havePrev = true;
                    int cp = l.CounterOf(k);
                    Agent cpa = AgentOf(cp);
                    if (cpa != null && cpa.IsActive())
                    {
                        Vec2 toCp = cpa.Position.AsVec2 - a.Position.AsVec2;
                        Vec2 fd = a.GetMovementDirection();
                        if (toCp.LengthSquared > 0.0001f && fd.LengthSquared > 0.0001f)
                        {
                            faceErr += MathF.Abs(MathF.Atan2(fd.x * toCp.y - fd.y * toCp.x,
                                fd.x * toCp.x + fd.y * toCp.y)) * 57.2958f;
                            nPairs++;
                        }
                    }
                }
                Log($"[GLINE] team={(l == _a ? "A" : "B")} lineStraight={maxDev:F2} " +
                    $"spacingErr={(nLive > 1 ? spErr / (nLive - 1) : 0f):F2} " +
                    $"counterpartFaceErr={(nPairs > 0 ? faceErr / nPairs : -1f):F0} " +
                    $"lag={MaxLag(l):F2} adv={l.Adv:F1}/{StageBackMeters:F0}");
            }
        }

        private static Agent AgentOf(int idx)
        {
            Mission m = Mission.Current;
            if (idx < 0 || m == null) return null;
            foreach (Agent a in m.Agents)
                if (a.Index == idx)
                    return a;
            return null;
        }
    }
}
