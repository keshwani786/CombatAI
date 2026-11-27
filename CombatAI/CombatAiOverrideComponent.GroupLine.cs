using System;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // Group lineup per-agent flag (stage 1, combatai.groupfree): locomotion
    // only -- MovementInputVector in the spar-rig facing-relative convention
    // (basis = the real body heading GetMovementDirection), magnitude scaled
    // by distance to the moving goal and capped to the stage speed. No
    // attack/defend bits ever. Facing comes from the shared free driver
    // (groupLineFree -> MoveDir writer, heading = team facing from
    // GroupLineup). Teammate/wall awareness via the shared clearance ring.
    public partial class CombatAiOverrideComponent
    {
        private int _glineStuckLogged;   // per-agent "never arrives" log latch
        private float _glineFarSince = -1f;  // >0.8m-from-goal clock for the stuck log

        private Agent.MovementControlFlag ComputeGroupLineFlag(float dt)
        {
            GroupLineup.Tick(dt, _brainClock, Agent.Mission);
            _brainClock += dt;
            // Pair fight state resets while we're still in the lineup phases
            // -- a new Fight re-pairs fresh (this fn only runs Phase!=Fight).
            _gfPair = null;
            _gfHomeSet = false;
            _gfReturning = false;

            if (!GroupLineup.GoalOf(Agent.Index, out Vec2 goal))
                return Agent.MovementControlFlag.None;   // not in a line (player etc.)

            // Path-legality veto (spar slot rule): a goal in a nav pocket
            // permanently stalls the line -- pull it 30% toward the line
            // centre once and log.
            if (_brainClock - _slotPathAt >= 0.2f && !_slotPathDead)
            {
                _slotPathAt = _brainClock;
                try
                {
                    Vec3 gp = new Vec3(goal.x, goal.y, Agent.Position.z);
                    float pd = Agent.GetPathDistanceToPoint(ref gp);
                    if (!float.IsNaN(pd) && pd >= 100000f)
                    {
                        Vec2 c = GroupLineup.FacingDir(Agent.Index);
                        goal += (Agent.Position.AsVec2 - goal) * 0.3f;
                        if (_glineStuckLogged < 3)
                        {
                            _glineStuckLogged++;
                            CombatAiLog.Log($"[GLINE] Agent #{Agent.Index}: goal path-blocked, pulled 30% (pd={pd:F0})");
                        }
                    }
                }
                catch (Exception e)
                {
                    _slotPathDead = true;
                    CombatAiLog.Log($"[GLINE] Agent #{Agent.Index}: path probe DISABLED ({e.GetType().Name})");
                }
            }

            Vec2 g = goal - Agent.Position.AsVec2;
            float dist = g.Length;
            // Smooth-arrival magnitude: ~1 at >1.5m, ramps to 0 at the slot,
            // capped to the stage speed (walk during Advance/Wait, run ok in
            // Form). MovementInputVector is a ~0..1 fraction of speed cap.
            float cap = GroupLineup.Phase == GroupLineup.Ph.Form ? 1f
                : MathF.Min(1f, CombatAiState.GroupSpeedMeters / 3.4f);
            float mag = MathF.Min(cap, dist / 1.5f);
            Vec2 f = Agent.GetMovementDirection();
            if (f.LengthSquared < 0.0001f)
                f = Agent.Frame.rotation.f.AsVec2;
            float fl = f.Length;
            Vec2 iv = Vec2.Zero;
            if (fl > 0.0001f && dist > 0.05f)
            {
                f = new Vec2(f.x / fl, f.y / fl);
                g = new Vec2(g.x / dist, g.y / dist);
                iv = new Vec2(
                    (g.x * f.y - g.y * f.x) * mag,    // strafe: dot(g,right)
                    (g.x * f.x + g.y * f.y) * mag);   // fwd:    dot(g,forward)
            }
            _lastOp = "MovementInputVector(gline)";
            _tmInputVec = iv;
            Agent.MovementInputVector = iv;

            // Stuck diagnostic: a live agent >0.8m from its goal for >8s
            // without progressing gets a once-per-session [GLINE] stuck line.
            if (dist > 0.8f)
            {
                if (_glineFarSince < 0f) _glineFarSince = _brainClock;
            }
            else
                _glineFarSince = -1f;
            if (dist > 0.8f && _glineStuckLogged < 3 && _glineFarSince >= 0f
                && _brainClock - _glineFarSince > 8f)
            {
                _glineStuckLogged++;
                CombatAiLog.Log($"[GLINE] Agent #{Agent.Index}: STUCK pos=({Agent.Position.AsVec2.x:F1},{Agent.Position.AsVec2.y:F1}) " +
                    $"goal=({goal.x:F1},{goal.y:F1}) dist={dist:F2} blocked-watch state logged.");
            }

            // Return the matching move bits so `driving` stays true (the
            // OnAIInputSet mirror channel) -- same convention as the rig:
            // bits echo the commanded vector, combat bits never set.
            Agent.MovementControlFlag flags = Agent.MovementControlFlag.None;
            if (iv.y > 0.3f) flags |= Agent.MovementControlFlag.Forward;
            else if (iv.y < -0.3f) flags |= Agent.MovementControlFlag.Backward;
            if (iv.x > 0.3f) flags |= Agent.MovementControlFlag.StrafeRight;
            else if (iv.x < -0.3f) flags |= Agent.MovementControlFlag.StrafeLeft;
            return flags;
        }
    }
}
