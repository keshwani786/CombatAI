using System;
using System.Collections.Generic;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =======================================================================
    // GROUP FORMATION ADVANCE (Session 67) -- REAL formation orders for the
    // 5v5 line advance, replacing the per-agent flag-push (GroupAdvanceFlag)
    // whose exit gate deadlocked at the contact rim (agents stood ~2min at
    // the edge because the march goal sat OUTSIDE the contact bound).
    //
    // ARM (arena GroupFight5v5 arm only): every live AI participant on each
    // AI team is moved into the team's Infantry formation (Team auto-creates
    // all 8 -- Team.GetFormation(FormationClass)); the formation gets a
    // single-rank custom-width line (FormOrderCustom sets LineFormation.
    // FlankWidth), FacingOrderLookAtEnemy, and a Move order whose anchor sits
    // MarchStandoffMeters in front of the ENEMY centroid. The order is
    // re-issued every ~0.5s with a fresh anchor so it tracks the marching
    // enemy (and recovers if anything ever stomps the order -- nothing does
    // in a no-TeamAI mission: Formation.Tick only calls AI.Tick under
    // Team.HasTeamAi, and Move orders are always IsApplicable).
    //
    // SUPPRESS: while the march is live a driven agent in its advance phase
    // hands the whole body to native (None + _handOffToNative -- the same
    // suppression used by the duel leash). The engine's formation frame
    // (HumanAIComponent.ParallelUpdateFormationMovement -> GetFormationFrame
    // -> TrySetFormationFrame) then pulls each unit to its line slot:
    // native pathfinding = no wall-push, no stragglers.
    //
    // RELEASE = GLOBAL, not per-victim: the moment the two lines meet --
    // whichever reports contact first, the formation's cached
    // median-to-median distance (Formation.CachedClosestEnemyFormationDistance
    // Squared, refreshed ~1.5s) OR a direct min cross-team pair distance --
    // BOTH formations are stopped and every participant is DETACHED.
    //   WHY DETACH, not stop-and-keep: GetFormationFrame returns result=true
    //   under MovementStateEnum.StandGround (the Stop order), so
    //   TrySetFormationFrame would keep pinning units to their line slots --
    //   the rim-freeze again. formation == null returns result=false ->
    //   SetFormationFrameDisabled + AdjustSpeedLimit(-1), so our per-agent
    //   flag writes fully own movement after release.
    //
    // FAILSAFE: any exception arming/ticking -> the flag
    // CombatAiState.FormationMarch is switched off and the old flag-push
    // advance (GroupAdvanceFlag) takes over -- the old code path is kept
    // verbatim as the fallback.
    // =======================================================================
    public static class GroupFormationAdvance
    {
        private const float TickSeconds = 0.5f;
        private const float MarchStandoffMeters = 2.5f;   // anchor sits this far in front of the enemy centroid
        private const float ReleaseDistanceMeters = 2.5f; // ~reach*1.5+0.5 -- min cross-team pair distance
        private const float MarchTimeoutSeconds = 30f;    // dead-flag: never let a stuck march lock the fight
        private const float LineWidthMeters = 6f;         // ~1.2m per unit -> single rank for a 5-man line

        private static Mission _mission;
        private static bool _marching;
        private static float _tickTimer;
        private static float _marchElapsed;
        private static int _marchLogTick;
        private static readonly List<Formation> _formations = new List<Formation>(2);
        private static readonly HashSet<Team> _teams = new HashSet<Team>();
        private static readonly List<Agent> _teamUnitsBuf = new List<Agent>(8);
        private static int _coordLogCount;
        private const int MaxCoordLogs = 80;

        public static bool Marching => _marching;

        public static void ResetLogBudget()
        {
            _coordLogCount = 0;
        }

        public static void Reset()
        {
            _marching = false;
            _formations.Clear();
            _teams.Clear();
            _tickTimer = 0f;
            _marchElapsed = 0f;
            _marchLogTick = 0;
            _mission = null;
        }

        // Called once from TryArmPendingArenaFight (GroupFight5v5) after the
        // roster snapshot exists. Assigns every live AI participant into its
        // team's Infantry formation and orders the rank/form/facing once.
        public static void Arm(Mission m)
        {
            Reset();
            _mission = m;
            if (!CombatAiState.FormationMarch || m == null)
                return;
            try
            {
                var byTeam = new Dictionary<Team, List<Agent>>(4);
                foreach (Agent a in m.Agents)
                {
                    if (a == null || !a.IsActive() || a.Health <= 0f || !a.IsHuman
                        || !a.IsAIControlled || a.HasMount)
                        continue;
                    if (!CombatAiState.ArenaRoster.Contains(a.Index))
                        continue;   // spawned fight participants only
                    Team t = a.Team;
                    if (t == null)
                        continue;
                    if (!byTeam.TryGetValue(t, out List<Agent> l))
                    {
                        l = new List<Agent>(6);
                        byTeam[t] = l;
                    }
                    l.Add(a);
                }
                int assigned = 0;
                foreach (KeyValuePair<Team, List<Agent>> kv in byTeam)
                {
                    Formation f = kv.Key.GetFormation(TaleWorlds.Core.FormationClass.Infantry);
                    if (f == null)
                        continue;
                    foreach (Agent a in kv.Value)
                    {
                        if (a.Formation != f)
                        {
                            a.Formation = f;   // setter calls AddUnit/DetachUnit -- verified Agent.cs:1098
                            assigned++;
                        }
                    }
                    // Single rank for a 5-man line: Custom sets LineFormation.
                    // FlankWidth directly (FormOrder.cs:189). LookAtEnemy is
                    // already the ResetAux default -- set anyway (explicit).
                    f.SetFormOrder(FormOrder.FormOrderCustom(LineWidthMeters));
                    f.SetFacingOrder(FacingOrder.FacingOrderLookAtEnemy);
                    _formations.Add(f);
                    _teams.Add(kv.Key);
                }
                if (_formations.Count == 0)
                {
                    CombatAiLog.Log("[GADV] formation march: no formations armed (no AI participants?) -- flag-push fallback.");
                    CombatAiState.FormationMarch = false;
                    return;
                }
                _marching = true;
                CombatAiLog.Log($"[GADV] formation march ARMED: formations={_formations.Count} moved={assigned} " +
                    $"width={LineWidthMeters:F1}m standoff={MarchStandoffMeters:F1}m releaseDist={ReleaseDistanceMeters:F1}m.");
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[GADV] formation march arm threw {ex.GetType().Name}: {ex.Message} -- flag-push fallback.");
                CombatAiState.FormationMarch = false;
                Reset();
            }
        }

        // ~0.5s cadence from CombatAiMissionBehavior.OnMissionTick while
        // GroupFightMode is on. Re-asserts membership (respawns), re-issues
        // the march order with a fresh enemy-centroid anchor, and fires the
        // global release the moment the lines meet.
        public static void Tick(float dt, Mission m)
        {
            if (m != _mission)
            {
                Reset();
                _mission = m;
                return;
            }
            if (!_marching || m == null)
                return;
            _tickTimer -= dt;
            _marchElapsed += dt;
            if (_tickTimer > 0f)
                return;
            _tickTimer = TickSeconds;
            try
            {
                TickMarch(m);
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[GADV] formation march tick threw {ex.GetType().Name}: {ex.Message} -- flag-push fallback.");
                CombatAiState.FormationMarch = false;
                Reset();
            }
        }

        private static void TickMarch(Mission m)
        {
            // Re-assert formation membership every tick: respawned/late
            // participants join their team's line mid-march, and a unit that
            // somehow left the formation re-enters it.
            _teamUnitsBuf.Clear();
            int live = 0;
            foreach (Agent a in m.Agents)
            {
                if (a == null || !a.IsActive() || a.Health <= 0f || !a.IsHuman
                    || !a.IsAIControlled || a.HasMount)
                    continue;
                Team t = a.Team;
                if (t == null || !_teams.Contains(t))
                    continue;
                if (!CombatAiState.ArenaRoster.Contains(a.Index))
                    continue;
                Formation want = t.GetFormation(TaleWorlds.Core.FormationClass.Infantry);
                if (want != null && a.Formation != want)
                    a.Formation = want;
                _teamUnitsBuf.Add(a);
                live++;
            }

            // Min cross-team pair distance (25 pairs at 5v5 -- trivial) and
            // a centroid of each formation's enemies, in one sweep.
            float minPairSq = float.MaxValue;
            var centroids = new Dictionary<Formation, Vec2>(_formations.Count);
            var enemyCounts = new Dictionary<Formation, int>(_formations.Count);
            foreach (Formation f in _formations)
            {
                centroids[f] = Vec2.Zero;
                enemyCounts[f] = 0;
            }
            for (int i = 0; i < _teamUnitsBuf.Count; i++)
            {
                Agent a = _teamUnitsBuf[i];
                foreach (Formation f in _formations)
                {
                    if (f.Team == a.Team || !f.Team.IsEnemyOf(a.Team))
                        continue;
                    centroids[f] += a.Position.AsVec2;
                    enemyCounts[f]++;
                }
                for (int j = i + 1; j < _teamUnitsBuf.Count; j++)
                {
                    Agent b = _teamUnitsBuf[j];
                    if (a.Team == b.Team || !a.IsEnemyOf(b))
                        continue;
                    float d2 = a.Position.DistanceSquared(b.Position);
                    if (d2 < minPairSq)
                        minPairSq = d2;
                }
            }

            // Re-issue the march order: anchor = enemy centroid pulled back
            // along our approach dir by the standoff -- the line halts just
            // inside real melee reach of the enemy mass (the OLD bug anchored
            // the goal OUTSIDE the exit gate's reach).
            foreach (Formation f in _formations)
            {
                int n = enemyCounts[f];
                if (n <= 0)
                    continue;
                Vec2 enemyC = centroids[f] * (1f / n);
                Vec2 dir = enemyC - f.CachedAveragePosition;
                dir = dir.LengthSquared < 0.01f ? f.Direction : dir.Normalized();
                Vec2 anchor = enemyC - dir * MarchStandoffMeters;
                WorldPosition wp = new WorldPosition(m.Scene, UIntPtr.Zero, new Vec3(anchor.x, anchor.y, 0f), false);
                wp.SetVec2(anchor);
                f.SetMovementOrder(MovementOrder.MovementOrderMove(wp));
                if (_marchLogTick % 4 == 0 && _coordLogCount < MaxCoordLogs)
                {
                    _coordLogCount++;
                    CombatAiLog.Log($"[GADV] march: team={f.Team.Side} enemies={n} anchor=({anchor.x:F1},{anchor.y:F1}) " +
                        $"elapsed={_marchElapsed:F1}s.");
                }
            }
            _marchLogTick++;

            // RELEASE = GLOBAL: whichever signal reports contact first -- the
            // cached median-to-median distance (finite only while an enemy
            // formation with units exists) or the direct pair distance.
            float releaseSq = ReleaseDistanceMeters * ReleaseDistanceMeters;
            float cachedSq = float.MaxValue;
            foreach (Formation f in _formations)
                if (f.CachedClosestEnemyFormationDistanceSquared < cachedSq)
                    cachedSq = f.CachedClosestEnemyFormationDistanceSquared;
            float trigger = MathF.Min(cachedSq, minPairSq);
            if (trigger < releaseSq || _marchElapsed > MarchTimeoutSeconds || live == 0)
                Release(m, trigger, cachedSq, minPairSq, live);
        }

        private static void Release(Mission m, float trigger, float cachedSq, float pairSq, int live)
        {
            _marching = false;
            int detached = 0;
            try
            {
                foreach (Formation f in _formations)
                    f.SetMovementOrder(MovementOrder.MovementOrderStop);
                // DETACH every participant on a marching team: a stopped
                // formation keeps StandGround's formation frame alive and
                // would pin the units to their line slots. nulling Formation
                // disables the frame + any speed limit outright.
                foreach (Agent a in m.Agents)
                {
                    if (a == null || !a.IsActive() || !a.IsHuman || !a.IsAIControlled)
                        continue;
                    if (a.Formation == null || a.Team == null || !_teams.Contains(a.Team))
                        continue;
                    a.Formation = null;
                    detached++;
                }
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[GADV] release detach threw {ex.GetType().Name}: {ex.Message} (continuing).");
            }
            bool timeout = _marchElapsed > MarchTimeoutSeconds;
            CombatAiLog.Log($"[GADV] RELEASE{(timeout ? " (timeout)" : "")}: trigger={trigger:F2}m^2 " +
                $"(cached={cachedSq:F2} pair={pairSq:F2} bound={ReleaseDistanceMeters * ReleaseDistanceMeters:F2}) " +
                $"elapsed={_marchElapsed:F1}s detached={detached} live={live} -- combat brain owns all units.");
            _formations.Clear();
            _teams.Clear();
        }

        // Component gate (ComputeGroupFlag advance branch): TRUE while the
        // formation march should own this agent's body -- march live, flag
        // enabled, and the agent actually sits in one of the marching
        // formations. A participant not yet assigned (respawn awaiting the
        // next re-assert sweep) falls back to the flag-push advance that
        // window instead of freezing.
        public static bool Suppress(Agent a)
        {
            if (!_marching || !CombatAiState.FormationMarch || a == null)
                return false;
            Formation f = a.Formation;
            return f != null && _formations.Contains(f);
        }
    }
}
