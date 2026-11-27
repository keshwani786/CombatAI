using System;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =======================================================================
    // ANGLE-DODGE TEST MODE (combatai.angledodge) -- the dodge counterpart of
    // PerfectBlock/Evade, built around the EXISTING dodge rule.
    //
    // The dodge rule (PickDodgeAction rule 2, UNCHANGED): an AttackLeft swing
    // is only dodged (to the right clip) when the attacker's camera is aimed
    // clearly to one side (|aim offset| > DodgeAimAngleRad, ~34 deg, correct
    // sign); AttackRight likewise to the other side. A swing from a slight
    // angle fails that test and is answered with a plain block (rule 3).
    //
    // Session 50: the spam-repositioning strafe was removed (user spec) --
    // the mode now just stands and answers each release with the normal
    // TryStartDodge / rule-3 block. UP/DOWN swings are always dodgeable
    // (rule 1).
    //
    // No attacks, no blocks of its own (a non-dodgeable swing still falls to
    // the existing rule-3 block, exactly like evade mode).
    // =======================================================================
    public partial class CombatAiOverrideComponent
    {
        private const int MaxAdLogs = 200;
        private static int _adLogCount;

        private int _adDodgedFor = -1;                // threat index already answered for THIS release

        private static void AdLog(string msg)
        {
            if (_adLogCount >= MaxAdLogs)
                return;
            _adLogCount++;
            CombatAiLog.Log(msg);
        }

        // Same geometry as PickDodgeAction: signed yaw from the attacker->us
        // bearing to their LookDirection, sign-flipped for AttackLeft so that a
        // BIGGER result is always closer to "the dodge rule is satisfied"
        // (satisfied when > DodgeAimAngleRad). myPos lets us probe a step.
        private float AdAimNeed(Agent threat, Vec2 myPos, Agent.UsageDirection dir)
        {
            Vec3 pp3 = threat.Position;
            Vec2 toUs = myPos - new Vec2(pp3.x, pp3.y);
            Vec3 look3 = threat.LookDirection;
            Vec2 aim = new Vec2(look3.x, look3.y);
            if (toUs.LengthSquared < 0.01f || aim.LengthSquared < 0.01f)
                return 0f;
            toUs.Normalize();
            aim.Normalize();
            float sin = toUs.x * aim.y - toUs.y * aim.x;
            float off = MathF.Asin(MathF.Max(-1f, MathF.Min(1f, sin)));
            return dir == Agent.UsageDirection.AttackLeft ? -off : off;
        }

        // Nearest hostile (or the player) in an attack stage within reach.
        // A releasing swing beats a merely-readied one.
        private Agent FindAngleDodgeThreat(out bool releasing)
        {
            _lastOp = "scan:angledodge";
            releasing = false;
            Mission m = Agent.Mission;
            if (m == null)
                return null;
            Agent bestRel = null, bestReady = null;
            float relDist = float.MaxValue, readyDist = float.MaxValue;
            foreach (Agent a in m.Agents)
            {
                if (a == null || a == Agent || !CombatLive(a) || !a.IsHuman)
                    continue;
                if (a.IsMainAgent && IsSparPlayerAlly)
                    continue;
                if (!a.IsMainAgent && !Agent.IsEnemyOf(a))
                    continue;
                Agent.ActionStage s = a.GetCurrentActionStage(1);
                bool rel = s == Agent.ActionStage.AttackRelease;
                bool rdy = s == Agent.ActionStage.AttackReady || s == Agent.ActionStage.AttackQuickReady;
                if (!rel && !rdy)
                    continue;
                float d = Agent.Position.DistanceSquared(a.Position);
                float reach = MeleeReachOf(a) + 0.8f;
                if (d > reach * reach)
                    continue;
                if (rel && d < relDist) { relDist = d; bestRel = a; }
                else if (rdy && d < readyDist) { readyDist = d; bestReady = a; }
            }
            if (bestRel != null)
            {
                releasing = true;
                return bestRel;
            }
            return bestReady;
        }


        private float _adClock;

        private Agent.MovementControlFlag ComputeAngleDodgeFlag(float dt)
        {
            _lastOp = "angledodge:enter";
            _adClock += dt;
            if (_dodgeTime > 0f)
                return TickDodgeStep(dt);
            if (_dodgeCooldown > 0f)
                _dodgeCooldown -= dt;
            if (IsForeignDodgePlaying())
            {
                CombatAiState.ForeignDodgeYieldCount++;
                return Agent.MovementControlFlag.None;
            }

            Agent threat = FindAngleDodgeThreat(out bool releasing);
            if (threat == null)
            {
                _adDodgedFor = -1;
                // Rest locked on a live opponent (target-null is the crash vector).
                Agent rest = FindNearestEnemy();
                if (!CombatLive(rest))
                    rest = Agent.Mission?.MainAgent;
                if (CombatLive(rest))
                    EnsureTargetAgent(rest);
                return Agent.MovementControlFlag.None;
            }

            EnsureTargetAgent(threat);
            Agent.UsageDirection dir = threat.AttackDirection;
            bool sideSwing = dir == Agent.UsageDirection.AttackLeft || dir == Agent.UsageDirection.AttackRight;

            if (releasing)
            {
                if (_adDodgedFor != threat.Index)
                {
                    _adDodgedFor = threat.Index;
                    CombatAiState.DodgeOpportunityCount++;
                    float need = sideSwing ? AdAimNeed(threat, new Vec2(Agent.Position.x, Agent.Position.y), dir) : 999f;
                    bool started = TryStartDodge(threat);   // the EXISTING dodge, untouched
                    AdLog($"[ANGLEDODGE] Agent #{Agent.Index}: release {dir} aimNeed={(sideSwing ? need.ToString("F2") : "n/a")} " +
                        $"(rule needs >{DodgeAimAngleRad:F2}) -> {(started ? (_dodgeBlockOnly ? "plain block (rule 3: condition not met)" : "DODGE") : "no dodge")}.");
                    if (started)
                        return DodgeMoveFlag() | _dodgeDefendFlag;
                }
                return Agent.MovementControlFlag.None;
            }

            // Ready / hold phase: a new release is coming, re-arm the dodge.
            _adDodgedFor = -1;
            return Agent.MovementControlFlag.None;
        }
    }
}
