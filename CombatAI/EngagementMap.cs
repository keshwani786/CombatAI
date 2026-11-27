using System;
using System.Collections.Generic;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =======================================================================
    // ENGAGEMENT MAP -- the group-fight pairing brain.
    //
    // Rebuilt ~3x/sec by CombatAiMissionBehavior while GroupFightMode is on.
    // One O(n^2) pass per rebuild (n = agents on the field) -- cheap at this
    // cadence, and it replaces every agent scanning Mission.Agents itself.
    //
    // Pairing rules (user spec):
    //   * every AI picks an UNTARGETED enemy first -- a victim with zero
    //     assigned attackers beats everything else
    //   * pairs are MUTUAL: if A locks onto an unassigned enemy B, B locks
    //     back onto A -- equal numbers collapse into clean 1v1 duels
    //     instead of a cluttered melee blob
    //   * when a side outnumbers, free agents stack onto the least-covered
    //     enemy: 2nd attacker = SUPPORT (front off-angle), 3rd+ = FLANKER
    //     (steers around behind the victim)
    //   * stacking prefers 2 per victim: a driver joins a victim already
    //     covered by 2 only when NO enemy has cover < 2 -- that's the "a
    //     free agent is actually needed" case. The 3rd attacker is the
    //     FREE AGENT: unbound from the victim's view cone, works the back.
    //   * the PLAYER is a valid victim (enemies can pick him) but is never
    //     assigned an attacker of his own -- we don't drive the player
    //
    // Also answers, per agent:
    //   * AttackersOn(idx)      -- how many assigned attackers it has (the
    //                              outnumbered-defender test: >=2)
    //   * AttackersOf(idx, buf) -- WHO is attacking it (focus switching)
    //   * PlayerThreatens(idx)  -- the player is engaging this agent (near +
    //                              facing + recent hits either way)
    //   * PlayerIsVictimOf(idx) -- this agent's assigned victim IS the player
    // =======================================================================
    public static class EngagementMap
    {
        public const int RolePrimary = 0;  // first (or only) attacker on a victim
        public const int RoleSupport = 1;  // second attacker -- front off-angle
        public const int RoleFlanker = 2;  // third+ attacker -- go for the back

        public struct Assignment
        {
            public int VictimIndex;
            public int Role;
        }

        private const float RebuildSeconds = 0.35f;
        private const int MaxAttackersPerVictim = 3;   // coverage cap (user spec)
        private const float PlayerThreatDistMeters = 5f;
        private const float PlayerThreatDot = 0.20f;
        private const float PlayerHitStickinessSeconds = 4f;
        // Session 67 "teammates fight together" retarget pass: a driver whose
        // assigned victim is still beyond exchange range may re-pick a NEARER
        // enemy (uncovered, or already a teammate's victim in contact -- a
        // valid 2-stack beside that teammate). Per-driver cooldown stops
        // re-aim churn.
        private const float RetargetVictimFarMeters = 2.5f; // victim beyond this = not mid-exchange
        private const float RetargetBesideMeters = 1.5f;    // teammate victim this close to its teammate = "fighting beside us"
        private const float RetargetCooldownSeconds = 1.5f;
        private static readonly Dictionary<int, float> _lastRetargetAt = new Dictionary<int, float>();
        private static int _gretargetLogCount;
        private const int MaxGretargetLogs = 40;

        private static readonly Dictionary<int, Assignment> _assign = new Dictionary<int, Assignment>();
        private static readonly Dictionary<int, List<int>> _attackersByVictim = new Dictionary<int, List<int>>();
        private static readonly Dictionary<int, float> _playerHitUntil = new Dictionary<int, float>();
        private static readonly HashSet<int> _playerThreatens = new HashSet<int>();
        private static readonly List<Agent> _fighters = new List<Agent>();
        private static readonly List<Agent> _drivers = new List<Agent>();

        private static float _clock;
        private static float _rebuildTimer;
        private static Mission _mission;

        // The map is keyed on live mission agents; these are the same Live /
        // CombatLive semantics the component uses (a cleared wrapper fails
        // Mission != null before any native call can deref a zeroed pointer).
        private static bool Live(Agent a) => a != null && a.Mission != null;
        private static bool CombatLive(Agent a) => Live(a) && a.IsActive() && a.Health > 0f;

        public static void Reset()
        {
            _assign.Clear();
            _attackersByVictim.Clear();
            _playerHitUntil.Clear();
            _playerThreatens.Clear();
            _fighters.Clear();
            _drivers.Clear();
            _lastRetargetAt.Clear();
            _mission = null;
            _rebuildTimer = 0f;
            _clock = 0f;
        }

        public static void ResetRetargetLogBudget()
        {
            _gretargetLogCount = 0;
        }

        public static bool TryGetAssignment(int agentIndex, out Assignment a)
            => _assign.TryGetValue(agentIndex, out a);

        public static int AttackersOn(int victimIndex)
        {
            return _attackersByVictim.TryGetValue(victimIndex, out List<int> l) ? l.Count : 0;
        }

        // Fills `buffer` with the live attacker agents assigned to this victim.
        public static void AttackersOf(int victimIndex, List<Agent> buffer, Mission m)
        {
            buffer.Clear();
            if (m == null || !_attackersByVictim.TryGetValue(victimIndex, out List<int> ids))
                return;
            foreach (int id in ids)
            {
                Agent a = FindAgent(m, id);
                if (CombatLive(a))
                    buffer.Add(a);
            }
        }

        public static bool PlayerThreatens(int agentIndex) => _playerThreatens.Contains(agentIndex);

        public static bool PlayerIsVictimOf(int agentIndex, Mission m)
        {
            Agent p = m?.MainAgent;
            return Live(p) && _assign.TryGetValue(agentIndex, out Assignment a) && a.VictimIndex == p.Index;
        }

        // Sticky engagement: a blow between the player and this agent (either
        // direction) marks them engaged for a few seconds even if the player's
        // facing momentarily drifts off.
        public static void OnPlayerAgentHit(int aiAgentIndex)
        {
            _playerHitUntil[aiAgentIndex] = _clock + PlayerHitStickinessSeconds;
        }

        public static void Tick(float dt, Mission m)
        {
            _clock += dt;
            if (m != _mission)
            {
                Reset();
                _mission = m;
                _clock = dt;
            }
            if (m == null)
                return;
            _rebuildTimer -= dt;
            if (_rebuildTimer > 0f)
                return;
            _rebuildTimer = RebuildSeconds;
            Rebuild(m);
        }

        private static void Rebuild(Mission m)
        {
            // Snapshot the old map BEFORE clearing -- valid assignments are
            // carried forward so established pairs stay stable between rebuilds
            // (Session 19 bug: rebuilding from scratch every 0.35s re-rolled
            // victims as coverage counts shifted -> constant re-aim churn).
            var oldAssign = new Dictionary<int, Assignment>(_assign);
            _assign.Clear();
            _attackersByVictim.Clear();
            _playerThreatens.Clear();
            _fighters.Clear();
            _drivers.Clear();

            Agent player = m.MainAgent;
            bool playerLive = CombatLive(player);

            // Pools: every live human is a potential victim; only AI agents get
            // assigned as attackers (we never drive the player).
            foreach (Agent a in m.Agents)
            {
                if (!CombatLive(a) || !a.IsHuman)
                    continue;
                _fighters.Add(a);
                if (a.IsAIControlled)
                    _drivers.Add(a);
            }
            _drivers.Sort((x, y) => x.Index.CompareTo(y.Index));

            // Player-engagement snapshot: near + in the player's front arc, or
            // a recent hit between them (stickiness survives facing flicks).
            // ENEMIES ONLY (Session 19 bug): teammates standing next to the
            // player at spawn were getting flagged "threatened by the player"
            // -> entered the outnumbered-defender path -> locked and swung AT
            // the player.
            if (playerLive)
            {
                Vec3 look = player.LookDirection;
                look.z = 0f;
                float rl = look.Length;
                if (rl > 0.01f) look = look / rl;
                float dMax = PlayerThreatDistMeters * PlayerThreatDistMeters;
                foreach (Agent a in _drivers)
                {
                    if (!a.IsEnemyOf(player))
                        continue;
                    Vec3 to = a.Position - player.Position;
                    to.z = 0f;
                    float d2 = to.LengthSquared;
                    bool engaged = false;
                    if (d2 < dMax)
                    {
                        float tl = (float)Math.Sqrt(d2);
                        if (tl > 0.01f)
                        {
                            Vec3 t = to / tl;
                            engaged = Vec3.DotProduct(t, look) > PlayerThreatDot;
                        }
                    }
                    if (!engaged
                        && _playerHitUntil.TryGetValue(a.Index, out float until)
                        && _clock < until)
                        engaged = true;
                    if (engaged)
                        _playerThreatens.Add(a.Index);
                }
            }

            // Pass 1: carry forward still-valid assignments (both live, still
            // enemies). Victim-side attacker counts rebuild from these.
            foreach (KeyValuePair<int, Assignment> kv in oldAssign)
            {
                Agent driver = FindAgent(m, kv.Key);
                Agent victim = FindAgent(m, kv.Value.VictimIndex);
                if (driver == null || victim == null || !driver.IsEnemyOf(victim))
                    continue;
                _assign[kv.Key] = kv.Value;
                AddAttacker(kv.Value.VictimIndex, kv.Key);
            }

            // Pass 2: greedy mutual pairing for unassigned drivers,
            // deterministic order (index asc).
            foreach (Agent a in _drivers)
            {
                if (_assign.ContainsKey(a.Index))
                    continue;
                Agent victim = PickVictim(a, m, player);
                if (victim == null)
                    continue;
                int role = AttackersOn(victim.Index); // 0->primary 1->support 2+->flanker
                _assign[a.Index] = new Assignment { VictimIndex = victim.Index, Role = role };
                AddAttacker(victim.Index, a.Index);

                // Mutual lock: an unassigned AI victim turns back on its
                // attacker -> clean 1v1 pairs when the sides are even.
                if (victim.IsAIControlled && role == RolePrimary && !_assign.ContainsKey(victim.Index))
                {
                    _assign[victim.Index] = new Assignment { VictimIndex = a.Index, Role = RolePrimary };
                    AddAttacker(a.Index, victim.Index);
                }
            }

            // Pass 3 (Session 67 user spec): teammates fight TOGETHER -- a
            // driver whose victim is still far may retarget a nearer enemy
            // that's uncovered or already in contact with a teammate.
            RetargetPass(m);

            // Role-order every attacker list: carried-forward assignments
            // re-add in (unspecified) dictionary order, so sort by the STORED
            // role to keep "the 3rd attacker" -- the free agent -- a stable
            // last element for consumers walking AttackersOf in order.
            foreach (List<int> l in _attackersByVictim.Values)
                l.Sort(CompareByAssignedRole);
        }

        // Retarget pass (Session 67): a driver may retarget its victim when
        // ALL hold:
        //   * the current victim is beyond ~2.5m -- never retarget while a
        //     victim is in reach (that's mid-exchange),
        //   * a NEARER enemy exists that is either (a) uncovered (cover 0) or
        //     (b) already a teammate's assigned victim within ~1.5m of that
        //     teammate -- fighting beside a teammate is a valid 2-stack per
        //     the 2-per-victim rule,
        //   * the candidate's coverage stays under the 3 cap,
        //   * the driver hasn't retargeted in the last ~1.5s.
        private static void RetargetPass(Mission m)
        {
            float farSq = RetargetVictimFarMeters * RetargetVictimFarMeters;
            foreach (Agent a in _drivers)
            {
                if (!_assign.TryGetValue(a.Index, out Assignment cur))
                    continue;
                Agent victim = FindAgent(m, cur.VictimIndex);
                if (victim == null)
                    continue;
                float curD2 = a.Position.DistanceSquared(victim.Position);
                if (curD2 <= farSq)
                    continue;   // mid-exchange -- hold the assignment
                if (_lastRetargetAt.TryGetValue(a.Index, out float last)
                    && _clock - last < RetargetCooldownSeconds)
                    continue;

                Agent best = null;
                float bestD2 = curD2;          // strictly nearer than the current victim
                int bestReason = 0;            // 1 = uncovered, 2 = beside-teammate
                foreach (Agent c in _fighters)
                {
                    if (c == a || !a.IsEnemyOf(c))
                        continue;
                    int cover = AttackersOn(c.Index);
                    if (cover >= MaxAttackersPerVictim)
                        continue;
                    float d2 = a.Position.DistanceSquared(c.Position);
                    if (d2 >= bestD2)
                        continue;
                    int reason = cover == 0 ? 1 : (IsTeammateVictimInContact(a, c) ? 2 : 0);
                    if (reason == 0)
                        continue;
                    best = c;
                    bestD2 = d2;
                    bestReason = reason;
                }
                if (best == null)
                    continue;

                RemoveAttacker(cur.VictimIndex, a.Index);
                int role = AttackersOn(best.Index);
                _assign[a.Index] = new Assignment { VictimIndex = best.Index, Role = role };
                AddAttacker(best.Index, a.Index);
                _lastRetargetAt[a.Index] = _clock;
                if (_gretargetLogCount < MaxGretargetLogs)
                {
                    _gretargetLogCount++;
                    CombatAiLog.Log($"[GRETARGET] Agent #{a.Index}: victim #{cur.VictimIndex} -> #{best.Index} " +
                        $"reason={(bestReason == 1 ? "uncovered" : "beside-teammate")} role={role} " +
                        $"dist={(float)Math.Sqrt(bestD2):F1}m (was {(float)Math.Sqrt(curD2):F1}m).");
                }
            }
        }

        // Is enemy `c` already the assigned victim of one of `a`'s teammates
        // AND in contact (within RetargetBesideMeters) with that teammate?
        private static bool IsTeammateVictimInContact(Agent a, Agent c)
        {
            float besideSq = RetargetBesideMeters * RetargetBesideMeters;
            foreach (Agent t in _drivers)
            {
                if (t == a || t.Team != a.Team)
                    continue;
                if (_assign.TryGetValue(t.Index, out Assignment ta)
                    && ta.VictimIndex == c.Index
                    && t.Position.DistanceSquared(c.Position) < besideSq)
                    return true;
            }
            return false;
        }

        private static void RemoveAttacker(int victimIndex, int attackerIndex)
        {
            if (_attackersByVictim.TryGetValue(victimIndex, out List<int> l))
                l.Remove(attackerIndex);
        }

        private static int CompareByAssignedRole(int x, int y)
        {
            int rx = _assign.TryGetValue(x, out Assignment ax) ? ax.Role : int.MaxValue;
            int ry = _assign.TryGetValue(y, out Assignment ay) ? ay.Role : int.MaxValue;
            return rx.CompareTo(ry);
        }

        // TWO-TIER coverage preference (Session 65 user spec): a victim gets
        // at most TWO attackers while any enemy still has room -- a driver
        // joins a victim already covered by 2 only when NO enemy has cover
        // < 2 (that's the "a 3rd / free agent is actually needed" case).
        //   Pass A: least-covered enemy with cover < 2 (untargeted first,
        //           distance breaks ties -- same rule as before).
        //   Pass B (only when pass A finds nobody): least-covered enemy with
        //           cover < MaxAttackersPerVictim -- i.e. the cover-2 pool;
        //           the cap stays 3.
        //   Fallback: every enemy is capped -> least-covered overall.
        private static Agent PickVictim(Agent a, Mission m, Agent player)
        {
            Agent best = null;          // pass A: cover < 2
            int bestCover = int.MaxValue;
            float bestDist = float.MaxValue;
            Agent mid = null;           // pass B: cover < cap (the 3rd stack)
            int midCover = int.MaxValue;
            float midDist = float.MaxValue;
            Agent fallback = null;      // all-capped: least-covered overall
            int fbCover = int.MaxValue;
            float fbDist = float.MaxValue;
            foreach (Agent v in _fighters)
            {
                if (v == a || !a.IsEnemyOf(v))
                    continue;
                int cover = AttackersOn(v.Index);
                float d = a.Position.DistanceSquared(v.Position);
                if (cover < fbCover || (cover == fbCover && d < fbDist))
                {
                    fbCover = cover;
                    fbDist = d;
                    fallback = v;
                }
                if (cover < MaxAttackersPerVictim
                    && (cover < midCover || (cover == midCover && d < midDist)))
                {
                    midCover = cover;
                    midDist = d;
                    mid = v;
                }
                if (cover >= 2)
                    continue;   // 3rd-stack territory -- pass B / fallback only
                if (cover < bestCover || (cover == bestCover && d < bestDist))
                {
                    bestCover = cover;
                    bestDist = d;
                    best = v;
                }
            }
            return best ?? mid ?? fallback;
        }

        // Coverage histogram for the [GROUP] line: how many victims currently
        // have exactly 1 / 2 / 3+ assigned attackers.
        public static void VictimCoverage(out int one, out int two, out int threePlus)
        {
            one = two = threePlus = 0;
            foreach (List<int> l in _attackersByVictim.Values)
            {
                if (l.Count >= MaxAttackersPerVictim) threePlus++;
                else if (l.Count == 2) two++;
                else if (l.Count == 1) one++;
            }
        }

        private static void AddAttacker(int victimIndex, int attackerIndex)
        {
            if (!_attackersByVictim.TryGetValue(victimIndex, out List<int> l))
            {
                l = new List<int>(3);
                _attackersByVictim[victimIndex] = l;
            }
            l.Add(attackerIndex);
        }

        private static Agent FindAgent(Mission m, int index)
        {
            // _fighters is rebuilt every pass -- linear scan over the pool
            // instead of Mission.Agents.
            for (int i = 0; i < _fighters.Count; i++)
                if (_fighters[i].Index == index)
                    return _fighters[i];
            return null;
        }
    }
}
