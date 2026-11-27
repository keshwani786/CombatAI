using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =======================================================================
    // AGENT CLEARANCE (Session 62 round 4 / Session 63): the shared
    // surroundings layer for duel/spar/group. Wraps the wall probe + an 8-dir
    // clearance ring + the StuckWatch (blocked-direction learning from actual
    // displacement). Session 63 adds the two channels the navmesh probe is
    // blind to: nearby AGENTS via Mission.GetNearbyAgents (boxer-ins were
    // invisible -- [STUCK] free(cmd)=1.50 blocker=none while fenced by
    // bodies), and the mission BOUNDARY via GetClosestBoundaryPosition (the
    // arena fence caps ring dirs and biases Escape). No spar-specific state:
    // duel/spar/group components each own one instance.
    // API: Tick(agent,now), Free(agent,dir,now), Ring/MinRing, Escape(prefer),
    // Watch (StuckWatch: Note/IsBlocked/StuckCount), Blocker (raycast diag),
    // NearestAgentInDir, BoundaryDist/BoundaryAwayDir.
    // =======================================================================
    internal sealed class AgentClearance
    {
        private const float ProbeDist = 1.5f;
        private const float ProbeCacheSeconds = 0.1f;
        private const float RingRefreshSeconds = 0.3f;
        private const int RingDirsPerTick = 2;
        private const int MaxWallProbeLogs = 60;
        private const int MaxBlockerLogs = 40;
        // Session 63: agent channel + boundary anchor.
        private const float NearbyRadius = 2.0f;
        private const float NearbyRefreshSeconds = 0.15f;
        private const float AgentBodyMargin = 0.45f;   // ~0.37 capsule radius + margin
        private const float AgentConeDot = 0.87f;      // ~30 deg off the commanded dir
        private const float BoundaryNearDist = 1.5f;   // Escape bias kicks in inside this
        // Session 63: path-legality channel. FindLongestDirectMoveToPosition
        // smooths over obstacles a real nav path can't cross (wall pockets) --
        // validate the 1.2m goal with GetPathDistanceToPoint (>=1e5 = no path,
        // >2.5x straight dist = obstacle detour). 8-sector cache at 0.2s --
        // path queries are A*-adjacent, never per-tick.
        private const float PathProbeDist = 1.2f;
        private const float PathCacheSeconds = 0.2f;
        private const float PathFailDist = 100000f;
        private const float PathRatioMax = 2.5f;
        private const int MaxPathLogs = 20;
        // Blocker raycast exclude mask: the default CommonFocus mask plus the
        // volumes that are pure noise for a ground blocker diagnostic (agent
        // bodies -- handled by the agent channel now, raycast-only helpers,
        // stealth boxes, flora). Dynamic/Moveable props (stones) and dropped
        // items are NOT excluded -- those were the invisible blockers.
        private const BodyFlags BlockerExcludeFlags =
            BodyFlags.CommonFocusRayCastExcludeFlags
            | BodyFlags.AgentOnly
            | BodyFlags.OnlyCollideWithRaycast
            | BodyFlags.StealthBox
            | BodyFlags.BodyOwnerFlora;

        public readonly float[] Ring = new float[8];
        public readonly StuckWatch Watch = new StuckWatch();

        // Session 63 outputs: mission-boundary anchor (BoundaryDist ==
        // float.MaxValue / BoundaryAwayDir == Zero when unknown).
        public float BoundaryDist = float.MaxValue;
        public Vec2 BoundaryAwayDir = Vec2.Zero;

        private bool _dead;                     // one exception disables probing for this instance
        private bool _metaDead;                 // nearby-agents/boundary channel disabled independently
        private bool _pathDead;                 // path-legality channel disabled independently
        private readonly float[] _pAt = new float[8] { -99f, -99f, -99f, -99f, -99f, -99f, -99f, -99f };
        private readonly bool[] _pOk = new bool[8];
        private int _ringNext;
        private float _ringRefreshAt = -99f;
        private float _metaRefreshAt = -99f;
        private readonly MBList<Agent> _nearby = new MBList<Agent>();
        private readonly Vec2[] _cDirs = new Vec2[4];
        private readonly float[] _cFree = new float[4];
        private readonly float[] _cAt = new float[4] { -99f, -99f, -99f, -99f };
        private int _cNext;
        private static int _wallProbeCount;
        private static int _blockerLogCount;
        private static int _pathLogCount;

        public static void ResetLogBudgets()
        {
            _wallProbeCount = 0;
            _blockerLogCount = 0;
            _pathLogCount = 0;
        }

        public float MinRing()
        {
            float m = ProbeDist;
            for (int i = 0; i < 8; i++)
                if (Ring[i] < m) m = Ring[i];
            return m;
        }

        public Vec2 Escape(Vec2 prefer)
        {
            Vec2 p = prefer;
            // Cornered by the fence: bias the open-dir pick along the
            // boundary-away direction (ring dirs toward it are already capped
            // at BoundaryDist by ClampFree).
            if (BoundaryDist < BoundaryNearDist && BoundaryAwayDir.LengthSquared > 0.0001f)
                p += BoundaryAwayDir * 1.5f;
            return ClearanceMath.BestOpenDir(Ring, p, 1.0f);
        }

        // Refresh two ring directions per call, round-robin; the agent/
        // boundary channel refreshes on its own ~0.15s clock.
        public void Tick(Agent a, float now)
        {
            if (_dead)
                return;
            if (!_metaDead && now - _metaRefreshAt >= NearbyRefreshSeconds)
            {
                _metaRefreshAt = now;
                RefreshMeta(a);
            }
            if (now - _ringRefreshAt < RingRefreshSeconds)
                return;
            _ringRefreshAt = now;
            for (int i = 0; i < RingDirsPerTick; i++)
            {
                int k = _ringNext;
                _ringNext = (_ringNext + 1) % 8;
                Ring[k] = ProbeDir(a, ClearanceMath.RingDir(k));
            }
        }

        // Single-direction probe, cached 0.1 s per direction (4 slots). The
        // cache holds the RAW fit-probe result; Free() applies the agent-body
        // + boundary clamps, WallFree() applies only the boundary clamp (the
        // pair-fight march needs to tell "wall blocks me" from "a body blocks
        // me" -- bodies get sidesteps, walls get vetoes).
        public float Free(Agent a, Vec2 dir, float now)
        {
            if (_dead)
                return ProbeDist;
            return ClampFree(a, dir, ProbeCached(a, dir, now));
        }

        public float WallFree(Agent a, Vec2 dir, float now)
        {
            if (_dead)
                return ProbeDist;
            return ClampBoundary(a, dir, ProbeCached(a, dir, now));
        }

        private float ProbeCached(Agent a, Vec2 dir, float now)
        {
            for (int i = 0; i < 4; i++)
            {
                if (now - _cAt[i] < ProbeCacheSeconds
                    && dir.x * _cDirs[i].x + dir.y * _cDirs[i].y > 0.999f)
                    return _cFree[i];
            }
            int slot = _cNext;
            _cNext = (_cNext + 1) % 4;
            _cAt[slot] = now;
            _cDirs[slot] = dir;
            _cFree[slot] = ProbeDir(a, dir);
            return _cFree[slot];
        }

        private float ProbeDir(Agent a, Vec2 dir)
        {
            try
            {
                Vec2 pos = a.Position.AsVec2;
                Vec2 target = pos + dir * ProbeDist;
                Vec2 result = a.FindLongestDirectMoveToPosition(target, true, true, out bool _);
                Vec2 delta = result - pos;
                if (float.IsNaN(delta.x) || float.IsNaN(delta.y)
                    || float.IsInfinity(delta.x) || float.IsInfinity(delta.y)
                    || (result.x == 0f && result.y == 0f))
                    return ProbeDist;   // unknown -> don't veto
                float nav = delta.Length;
                if (_wallProbeCount < MaxWallProbeLogs)
                {
                    _wallProbeCount++;
                    CombatAiLog.Log($"[RIGWALL] Agent #{a.Index}: dir=({dir.x:F2},{dir.y:F2}) target=({target.x:F1},{target.y:F1}) " +
                        $"returned=({result.x:F1},{result.y:F1}) free={nav:F2}");
                }
                return nav;
            }
            catch (Exception e)
            {
                _dead = true;
                CombatAiLog.Log($"[RIGWALL] Agent #{a.Index}: probe DISABLED after exception ({e.GetType().Name}) -- vetoes off");
                return ProbeDist;
            }
        }

        // Session 63: the two channels the navmesh probe is blind to.
        // (a) nearby AGENTS: a live agent inside ~30deg of the commanded dir
        //     caps free at (distance - body margin) -- boxer-ins read as walls.
        // (b) mission BOUNDARY: a dir pointing mostly toward the fence is
        //     capped at the boundary distance.
        private float ClampFree(Agent a, Vec2 dir, float free)
        {
            if (_metaDead)
                return free;
            try
            {
                Vec2 pos = a.Position.AsVec2;
                for (int i = 0; i < _nearby.Count; i++)
                {
                    Agent o = _nearby[i];
                    if (o == null || o.Index == a.Index || !o.IsActive())
                        continue;
                    Vec2 toA = o.Position.AsVec2 - pos;
                    float d = toA.Length;
                    if (d < 0.0001f)
                    {
                        free = 0f;   // stacked on top of us
                        continue;
                    }
                    // dot(unit(toA), dir) > AgentConeDot without a sqrt.
                    if (toA.x * dir.x + toA.y * dir.y > AgentConeDot * d)
                    {
                        float lim = d - AgentBodyMargin;
                        if (lim < free)
                            free = lim < 0f ? 0f : lim;
                    }
                }
                if (BoundaryDist < float.MaxValue && BoundaryAwayDir.LengthSquared > 0.0001f)
                {
                    // toward-boundary == -BoundaryAwayDir
                    float dot = -(dir.x * BoundaryAwayDir.x + dir.y * BoundaryAwayDir.y);
                    if (dot > 0.5f && free > BoundaryDist)
                        free = BoundaryDist;
                }
                return free;
            }
            catch (Exception)
            {
                _metaDead = true;
                return free;   // stale agent ref: drop the clamp, keep the probe
            }
        }

        // Boundary-only clamp for WallFree: the agent-body loop is skipped so
        // the caller can distinguish "wall in the way" from "body in the way".
        private float ClampBoundary(Agent a, Vec2 dir, float free)
        {
            if (_metaDead)
                return free;
            try
            {
                if (BoundaryDist < float.MaxValue && BoundaryAwayDir.LengthSquared > 0.0001f)
                {
                    float dot = -(dir.x * BoundaryAwayDir.x + dir.y * BoundaryAwayDir.y);
                    if (dot > 0.5f && free > BoundaryDist)
                        free = BoundaryDist;
                }
            }
            catch (Exception) { }
            return free;
        }

        // Session 63: refresh the nearby-agent list + the boundary anchor.
        // Separate fault domain from the navmesh probe (_metaDead, not _dead).
        private void RefreshMeta(Agent a)
        {
            try
            {
                Mission m = a.Mission != null ? a.Mission : Mission.Current;
                if (m == null)
                {
                    _nearby.Clear();
                    BoundaryDist = float.MaxValue;
                    BoundaryAwayDir = Vec2.Zero;
                    return;
                }
                m.GetNearbyAgents(a.Position.AsVec2, NearbyRadius, _nearby);
                Vec2 cp = m.GetClosestBoundaryPosition(a.Position.AsVec2);
                Vec2 away = a.Position.AsVec2 - cp;
                float l = away.Length;
                if (float.IsNaN(l) || float.IsInfinity(l))
                {
                    BoundaryDist = float.MaxValue;
                    BoundaryAwayDir = Vec2.Zero;
                }
                else
                {
                    BoundaryDist = l;
                    BoundaryAwayDir = l > 0.0001f ? new Vec2(away.x / l, away.y / l) : Vec2.Zero;
                }
            }
            catch (Exception e)
            {
                _metaDead = true;
                _nearby.Clear();
                BoundaryDist = float.MaxValue;
                BoundaryAwayDir = Vec2.Zero;
                if (_wallProbeCount < MaxWallProbeLogs)
                {
                    _wallProbeCount++;
                    CombatAiLog.Log($"[RIGWALL] Agent #{a.Index}: nearby/boundary DISABLED after exception ({e.GetType().Name})");
                }
            }
        }

        // Session 63: is the point 1.2m along dir nav-REACHABLE for this
        // agent? false = no path or a >2.5x detour (an obstacle the direct-
        // move probe slides along). Unknown/NaN -> true (don't veto blind),
        // one exception disables the channel.
        public bool PathOk(Agent a, Vec2 dir, float now)
        {
            if (_pathDead)
                return true;
            int k = ClearanceMath.RingSector(dir);
            if (k < 0)
                return true;
            if (now - _pAt[k] < PathCacheSeconds)
                return _pOk[k];
            _pAt[k] = now;
            try
            {
                Vec3 goal = a.Position + new Vec3(dir.x * PathProbeDist, dir.y * PathProbeDist, 0f);
                float pd = a.GetPathDistanceToPoint(ref goal);
                _pOk[k] = float.IsNaN(pd)
                    || (pd < PathFailDist && pd <= PathProbeDist * PathRatioMax);
                if (!_pOk[k] && _pathLogCount < MaxPathLogs)
                {
                    _pathLogCount++;
                    CombatAiLog.Log($"[RIGPATH] Agent #{a.Index}: dir=({dir.x:F2},{dir.y:F2}) pathDist={pd:F1} -> blocked");
                }
                return _pOk[k];
            }
            catch (Exception e)
            {
                _pathDead = true;
                if (_pathLogCount < MaxPathLogs)
                {
                    _pathLogCount++;
                    CombatAiLog.Log($"[RIGPATH] Agent #{a.Index}: path probe DISABLED after exception ({e.GetType().Name})");
                }
                return true;
            }
        }

        // Closest nearby agent inside the ~30deg cone on dir (-1 when none) --
        // for the [STUCK] line's agent-vs-fence disambiguation.
        public float NearestAgentInDir(Agent a, Vec2 dir)
            => NearestAgentInDir(a, dir, false);

        // nonEnemiesOnly: mirror the native fit-probe occupancy rule
        // (research 13 sec.4 -- FUN_1805e6bf0 counts only NON-hostile bodies;
        // enemies never veto a goal point). The pair march sidesteps
        // friendlies but pushes straight through enemy bodies.
        public float NearestAgentInDir(Agent a, Vec2 dir, bool nonEnemiesOnly)
        {
            float best = -1f;
            if (_metaDead)
                return best;
            try
            {
                Vec2 pos = a.Position.AsVec2;
                for (int i = 0; i < _nearby.Count; i++)
                {
                    Agent o = _nearby[i];
                    if (o == null || o.Index == a.Index || !o.IsActive())
                        continue;
                    if (nonEnemiesOnly && a.IsEnemyOf(o))
                        continue;
                    Vec2 toA = o.Position.AsVec2 - pos;
                    float d = toA.Length;
                    if (d < 0.0001f)
                    {
                        best = 0f;
                        continue;
                    }
                    if (toA.x * dir.x + toA.y * dir.y > AgentConeDot * d
                        && (best < 0f || d < best))
                        best = d;
                }
            }
            catch (Exception) { }
            return best;
        }

        // Raycast diagnostic for the [STUCK] log only: what object is between
        // us and the command point. Session 63: cast at SHIN height (the old
        // 0.9m chest ray sailed over knee-high stones/props) with an exclude
        // mask that keeps Moveable/Dynamic props hittable.
        public string Blocker(Agent a, Vec2 dir)
        {
            if (_blockerLogCount >= MaxBlockerLogs)
                return "(budget)";
            try
            {
                Vec3 from = a.Position + new Vec3(0f, 0f, 0.15f);
                Vec3 to = a.Position + new Vec3(dir.x * ProbeDist, dir.y * ProbeDist, 0.15f);
                _blockerLogCount++;
                bool hit = Mission.Current.Scene.RayCastForClosestEntityOrTerrain(
                    from, to, out float dist, out WeakGameEntity ent, 0.05f, BlockerExcludeFlags);
                if (!hit)
                    return "none";
                try { return $"entity={(ent.IsValid ? ent.Name : "?")} dist={dist:F2}"; }
                catch { return $"terrain dist={dist:F2}"; }
            }
            catch (Exception e)
            {
                return $"err:{e.GetType().Name}";
            }
        }
    }
}
