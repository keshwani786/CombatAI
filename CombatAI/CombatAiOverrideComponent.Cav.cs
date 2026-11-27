using System;
using System.Collections.Generic;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =======================================================================
    // CAV DUEL BRAIN (mounted 1v1, research/16 spec): player vs one AI, both
    // mounted with lances (arena kind CavDuel / combatai.cav). The SHARED
    // duel FSM keeps ownership of attack start/release/feint and the combat
    // lock -- unchanged. The cav layer adds a mounted movement model and a
    // release-only block on top (post-processing only, no FSM edits).
    //
    // MOVEMENT (spec §3): speed-aware, player-input-faithful channels:
    //   steer  = TurnLeft/TurnRight flags (the mount's rotate-on-axis channel
    //            -- A/D keys, MMAC:619-629). Never strafe-steer at speed.
    //   lean   = inputVec.x clamp(err) -- the A+W circle trim (Both mode).
    //   throttle = inputVec.y (+ Forward/Backward flag parity, like the
    //            player's quadrant flags). Brake/reverse = y<0 + Backward.
    //   spur   = one-tick EventControlFlags |= DoubleTapToDirectionUp on a
    //            cooldown (player double-W -> native Dash burst).
    //   stop   = DoubleTapToDirectionDown pulse (player double-S).
    //   Readback: MountAgent.MovementVelocity.y = signed fwd speed (the
    //            sidestep/brake gate), .GetMovementDirection() = heading,
    //            .GetCurrentActionType(0) = Dash/QuickStop/Rear.
    //
    //   Charge: aim at predicted intercept, full throttle, spur pulses.
    //   Brawl:  arrived in reach -> fight up close ~2.2s/2 swings, then
    //           break away -- the "fight a bit, create space, charge
    //           again" cadence.
    //   Dodge/HaltTurn: rear-chase answers (|bear|>110 && closing>1.5,
    //           alternating pick, 3.5s cd) -- lateral escape burst or a
    //           sudden halt + pivot into a point-blank attack.
    //   Pass:   run it out to open the wheel room.
    //   Wheel:  aim directly AT the target -- Turn+Forward arcs the horse
    //           around (the old side-point aim pinned hdgErr at +-90).
    //   Escape: nose-blocked (mount-radius probe ahead < ~2m) or stalled
    //           (commanding forward but mount speed ~0, e.g. pinned by the
    //           enemy horse) -> brake, then back-step + turn toward the
    //           most open probed heading (~0.9s), then Charge again.
    //   Boundary guard unchanged: ~6m ahead outside combatai_pit -> centre.
    //
    //   Steering modes (combatai.cavsteer): Vec = legacy pure input-vec
    //   strafe steering; Turn = turn flags + forward only; Both (default)
    //   = turn flags + x-lean + throttle (the faithful player mapping).
    //
    // ATTACK (spec §5): swing-start gate. The FSM's reach model is infantry
    // -- hold-to-release (combatai.cavhold): the windup arms inside a
    //   closing-scaled hold window and the Attack* bit (level-triggered)
    //   stays asserted through the approach; clearing it at the strike
    //   gate releases the instant the tip arrives. Commits outside the
    //   window still roll back to Approach with the flag stripped (the
    //   windup failsafe / NoteDirectionFailed guard, unchanged); held
    //   readies die on FSM release, a dead swing (None >0.6s), or a 2s
    //   max-hold. CavHold off restores the strip gate. NOTE: a held
    //   lance-couch ALSO reports AttackReady for seconds (player couch seen
    //   in ai47) -- ownAct0/ownAct1 telemetry disambiguates.
    //
    // DEFENSE (spec §6): release-only "perfect block". The ai47 hold came
    //   from this layer accepting AttackReady/QuickReady (guard held the
    //   whole enemy ready, seconds). Now: block ONLY on AttackRelease, or
    //   on the couch equivalent (tgt.IsDoingPassiveAttack -- a live couch
    //   never produces an AttackRelease edge). Gate distance scales with
    //   closing: threatReach + closing*lead. Couch direction fallback =
    //   DefendDown (GetCurrentActionDirection likely None during couch).
    //   Brain early guards (_defCommitState holds) are suppressed while
    //   _cavDriving (RaiseEarlyGuard early-return + state-1/2 emit gates).
    //   The far-strip stays (no distant turtling).
    // =======================================================================
    public partial class CombatAiOverrideComponent
    {
        // Charge -> Brawl (fight ~2s/2 swings up close) -> Pass (break away)
        //        -> Wheel (turn back) -> Charge. Dodge/HaltTurn = rear-chase
        //        answers: lateral burst or brake+pivot into a point-blank hit.
        private enum CavPhase { Charge, Pass, Wheel, Escape, Brawl, Dodge, HaltTurn }

        private bool _cavDriving;                     // this tick's flag fn is the cav planner
        private Vec2 _cavInputVec;                    // facing-relative input vec for the write branch
        private Agent.MovementControlFlag _cavMoveFlag; // planner move-mask bits (Turn/Forward/Backward)
        private CavPhase _cavPhase = CavPhase.Charge;
        private float _cavPhaseAt = -99f;
        private bool _cavCameClose;
        // escape state
        private Vec2 _cavEscDir;
        private float _cavStallAt = -1f;
        private bool _cavMovedOnce;      // stall detection only arms after real motion (spawn grace)
        private float _cavNextProbeAt = -99f;
        private float _cavAheadDist = 99f;
        private bool _cavAheadByAgent;   // probe blocker was an agent, not a wall (ai50 escape-spam fix)
        private bool _cavEscLogged;
        // speed-law state (research/17 model): desired speed + turn-rate measure
        private float _cavDesired;
        private float _cavLastHdg = float.MinValue;
        private float _cavTurnRate;
        // spur pacing + speed-cap bookkeeping
        private float _cavSpurAt = -99f;
        private float _cavHaltAt = -99f;
        private float _cavMaxSpd = 9f;
        private bool _cavMaxSpdSet;
        private float _cavLastCap = -1f;
        // pit centre (cached once from the synthesized boundary)
        private Vec2 _cavPitCentre;
        private bool _cavPitCentreSet;
        // telemetry
        private float _cavPassMin = float.MaxValue;
        private Agent.ActionStage _cavPassStage = Agent.ActionStage.None;
        private bool _cavPassRel;
        private Agent.ActionStage _cavPrevStage = Agent.ActionStage.None;
        private bool _cavBlockHeld;
        private int _cavGateStrips;
        // attack-hold state (CavHold): windup armed inside the hold window,
        // _cavHoldDir re-asserted each tick until the strike gate clears it.
        private bool _cavHolding;
        private float _cavHoldAt = -99f;
        private Agent.MovementControlFlag _cavHoldDir;
        // brawl (up-close fight window) + rear-chase + dir-remap state.
        private float _cavBrawlUntil = -99f;
        private int _cavBrawlSwings;
        private Vec2 _cavDodgeDir;
        private bool _cavUnderPick;      // alternates Dodge vs HaltTurn picks
        private float _cavNextUnderAt = -99f;
        private bool _cavDirAlt;         // lateral swings -> alternating Up/Down
        private int _cavLogCount, _cavPassLogCount, _cavSwingLogCount, _cavBlkLogCount, _cavEscLogCount;
        private float _cavLastLogAt = -99f;
        private const int MaxCavLogs = 700, MaxCavPassLogs = 60, MaxCavSwingLogs = 80,
            MaxCavBlkLogs = 60, MaxCavEscLogs = 60;

        private Agent.MovementControlFlag ComputeCavFlag(float dt)
        {
            // (1) Attack/defend/lock: the shared duel FSM, REUSED unchanged.
            Agent.MovementControlFlag fsm = ComputeDuelFlag(dt);
            // The infantry leash inside ComputeDuelFlag hands the body to
            // native past reach+0.8: cav owns the body at every range.
            _handOffToNative = false;

            Mission m = Agent.Mission;
            Agent tgt = CombatLive(_duelTarget) ? _duelTarget : Agent.Mission?.MainAgent;
            _cavInputVec = Vec2.Zero;
            _cavMoveFlag = Agent.MovementControlFlag.None;
            if (m == null || !CombatLive(tgt))
                return fsm;
            if (!_rigBoundaryDone)
                RigSynthesizeBoundary();   // arena pit isn't a mission boundary -- synthesize once

            float now = m.CurrentTime;
            Agent mount = Agent.MountAgent;
            Agent tgtMount = tgt.MountAgent;
            Vec2 selfPos = (mount ?? Agent).Position.AsVec2;
            Vec2 tgtPos = (tgtMount ?? tgt).Position.AsVec2;

            // ---- readback (native Velocity -- no position differencing) ----
            Vec2 selfVelW = (mount ?? Agent).Velocity.AsVec2;
            Vec2 tgtVelW = (tgtMount ?? tgt).Velocity.AsVec2;
            float mountSpd = mount != null ? mount.MovementVelocity.y : Agent.MovementVelocity.y;
            Agent.ActionCodeType mountAct = mount != null
                ? mount.GetCurrentActionType(0) : Agent.ActionCodeType.Other;
            if (mount != null && !_cavMaxSpdSet)
            {
                _cavMaxSpdSet = true;
                try
                {
                    float ms = mount.GetMaximumForwardUnlimitedSpeed();
                    if (ms > 1f) _cavMaxSpd = ms;
                }
                catch { /* keep 9f default */ }
            }

            // ---- speed management (native +0x114 model): the mount's own
            // speed LIMIT is the lever -- cap ~ distance-to-goal so arrival
            // bleeds speed through the engine's smoother instead of our
            // throttle. Turn-radius guard on top: native has none (research
            // r2) -- big heading demand at speed is what smashes into walls.
            Vec2 toT = tgtPos - selfPos;
            float dist = toT.Length;
            Vec2 dirT = dist > 0.0001f ? new Vec2(toT.x / dist, toT.y / dist) : Vec2.Zero;
            Vec2 f = DefenderFacing(Agent);   // mounted -> the HORSE heading
            if (f.LengthSquared < 0.0001f)
                f = dirT.LengthSquared > 0.0001f ? dirT : new Vec2(1f, 0f);
            float fl = f.Length;
            if (fl > 0.0001f)
                f = new Vec2(f.x / fl, f.y / fl);
            // measured turn rate (deg/s) -- telemetry for the omega(v) law
            float hdgNow = MathF.Atan2(f.y, f.x);
            if (_cavLastHdg > float.MinValue && dt > 0f)
            {
                float dH = hdgNow - _cavLastHdg;
                while (dH > 3.14159f) dH -= 6.28318f;
                while (dH < -3.14159f) dH += 6.28318f;
                _cavTurnRate = MathF.Abs(dH) / dt * 57.2958f;
            }
            _cavLastHdg = hdgNow;
            float cross = f.x * dirT.y - f.y * dirT.x;   // >0 = target on our LEFT
            float dot = f.x * dirT.x + f.y * dirT.y;
            float bearDeg = MathF.Atan2(cross, dot) * 57.2958f;
            float closing = dirT.LengthSquared > 0.0001f
                ? MathF.Max(0f, (selfVelW.x - tgtVelW.x) * dirT.x + (selfVelW.y - tgtVelW.y) * dirT.y)
                : 0f;
            float selfSpd = selfVelW.Length;
            float reach = MeleeReach();
            float threatReach = MeleeReachOf(tgt);

            Agent.ActionStage ownStage = Agent.GetCurrentActionStage(1);
            Agent.ActionStage tgtStage = tgt.GetCurrentActionStage(1);
            Agent.ActionCodeType tgtAct0 = tgt.GetCurrentActionType(0);
            Agent.ActionCodeType tgtAct1 = tgt.GetCurrentActionType(1);
            float tgtProg = tgt.GetCurrentActionProgress(1);
            bool tgtCouch = false;
            try { tgtCouch = tgt.IsDoingPassiveAttack; } catch { }

            // ---- obstacle sense: how far can the horse run on this heading ----
            if (mount != null && now >= _cavNextProbeAt)
            {
                _cavNextProbeAt = now + 0.2f;
                try
                {
                    Vec2 res = mount.FindLongestDirectMoveToPosition(selfPos + f * 7f, true, true, out bool hitAgent);
                    _cavAheadDist = (res - selfPos).Length;
                    _cavAheadByAgent = hitAgent;
                }
                catch { _cavAheadDist = 99f; _cavAheadByAgent = false; }
            }

            // ---- phase machine ----
            // stall only counts when the phase actually wants forward pace;
            // Brawl/HaltTurn intentionally ride slow.
            bool commandingFwd = _cavPhase != CavPhase.Escape
                && _cavPhase != CavPhase.HaltTurn && _cavPhase != CavPhase.Brawl;
            if (commandingFwd && mountSpd < 0.4f)
            {
                if (_cavStallAt < 0f)
                    _cavStallAt = now;
            }
            else _cavStallAt = -1f;

            if (mountSpd > 1.5f)
                _cavMovedOnce = true;

            // hard brake: wall inside ~0.45s of travel at speed -> instant
            // halt pulse (verified: DoubleTapDown -> MountQuickStop action).
            if (mount != null && mountSpd > 5.5f && !_cavAheadByAgent
                && _cavAheadDist < mountSpd * 0.45f + 1.2f && now - _cavHaltAt > 0.8f)
            {
                _cavHaltAt = now;
                try { Agent.EventControlFlags |= Agent.EventControlFlag.DoubleTapToDirectionDown; } catch { }
                if (_cavEscLogCount < MaxCavEscLogs)
                {
                    _cavEscLogCount++;
                    CombatAiLog.Log($"[CAV] halt Agent #{Agent.Index}: spd={mountSpd:F1} ahead={_cavAheadDist:F1} dist={dist:F2}");
                }
            }

            if (_cavPhase != CavPhase.Escape)
            {
                // lookahead scales with speed: ~0.45s of travel, floor 2.2m.
                // Only a STATIC blocker while facing AWAY from the target --
                // a short "ahead" toward the target is the player's horse
                // (ai50: the AI fled its own duel target).
                bool noseBlocked = mount != null && dist > reach * 1.2f
                    && MathF.Abs(bearDeg) > 55f && !_cavAheadByAgent
                    && _cavAheadDist < MathF.Max(2.2f, mountSpd * 0.45f);
                // stalled = commanded forward but the horse isn't moving --
                // only counts once it has actually moved (spawn grace).
                bool stalled = _cavMovedOnce && _cavStallAt >= 0f && now - _cavStallAt > 0.5f;
                if (noseBlocked || stalled)
                {
                    _cavPhase = CavPhase.Escape;
                    _cavPhaseAt = now;
                    _cavEscLogged = false;
                    _cavEscDir = CavPickOpenDir(mount, selfPos, f);
                    if (mountSpd > 3f)   // double-S hard stop pulse
                    {
                        try { Agent.EventControlFlags |= Agent.EventControlFlag.DoubleTapToDirectionDown; } catch { }
                    }
                }
            }

            // ---- rear-chase answer (alternate): player on our tail and
            // closing -> (a) lateral dodge burst, (b) halt-and-turn into a
            // point-blank attack. 3.5s cooldown so it can't spam. ----
            if (now >= _cavNextUnderAt
                && _cavPhase != CavPhase.Escape && _cavPhase != CavPhase.Dodge
                && _cavPhase != CavPhase.HaltTurn
                && MathF.Abs(bearDeg) > 110f && dist < 9f && closing > 1.5f)
            {
                _cavNextUnderAt = now + 3.5f;
                _cavPhaseAt = now;
                _cavUnderPick = !_cavUnderPick;
                if (_cavUnderPick)
                {
                    _cavPhase = CavPhase.Dodge;
                    _cavDodgeDir = CavPickOpenDir(mount, selfPos, f);
                    if (_cavEscLogCount < MaxCavEscLogs)
                    {
                        _cavEscLogCount++;
                        CombatAiLog.Log($"[CAV] dodge Agent #{Agent.Index}: dist={dist:F2} bear={bearDeg:F0} " +
                            $"closing={closing:F1} dir=({_cavDodgeDir.x:F2},{_cavDodgeDir.y:F2})");
                    }
                }
                else
                {
                    _cavPhase = CavPhase.HaltTurn;
                    if (mountSpd > 3f)   // sudden stop, then pivot toward them
                    {
                        try { Agent.EventControlFlags |= Agent.EventControlFlag.DoubleTapToDirectionDown; } catch { }
                    }
                    if (_cavEscLogCount < MaxCavEscLogs)
                    {
                        _cavEscLogCount++;
                        CombatAiLog.Log($"[CAV] haltturn Agent #{Agent.Index}: dist={dist:F2} bear={bearDeg:F0} " +
                            $"closing={closing:F1} spd={mountSpd:F1}");
                    }
                }
            }

            switch (_cavPhase)
            {
                case CavPhase.Charge:
                    if (dist <= reach * 1.0f && dot > 0f)
                        _cavCameClose = true;
                    // arrived in reach facing them -> fight up close for a
                    // beat, THEN break away (the hit-and-run cadence).
                    if (dist <= reach * 1.15f && MathF.Abs(bearDeg) < 75f)
                    {
                        _cavPhase = CavPhase.Brawl;
                        _cavPhaseAt = now;
                        _cavBrawlUntil = now + 2.2f;
                        _cavBrawlSwings = 0;
                        if (_cavEscLogCount < MaxCavEscLogs)
                        {
                            _cavEscLogCount++;
                            CombatAiLog.Log($"[CAV] brawl-in Agent #{Agent.Index}: dist={dist:F2} spd={mountSpd:F1}");
                        }
                    }
                    else if (_cavCameClose && MathF.Abs(bearDeg) > 90f)
                    {
                        _cavPhase = CavPhase.Pass;
                        _cavPhaseAt = now;
                    }
                    break;
                case CavPhase.Brawl:
                    // count our releases; exit on time/swings, or the target
                    // slipping out of reach (-> pass, then chase again).
                    if (ownStage == Agent.ActionStage.AttackRelease
                        && _cavPrevStage != Agent.ActionStage.AttackRelease)
                        _cavBrawlSwings++;
                    if (now >= _cavBrawlUntil || _cavBrawlSwings >= 2 || dist > reach * 1.7f)
                    {
                        float brawlT = now - _cavPhaseAt;
                        _cavPhase = CavPhase.Pass;
                        _cavPhaseAt = now;
                        if (_cavEscLogCount < MaxCavEscLogs)
                        {
                            _cavEscLogCount++;
                            CombatAiLog.Log($"[CAV] brawl-out Agent #{Agent.Index}: swings={_cavBrawlSwings} " +
                                $"dist={dist:F2} t={brawlT:F1}");
                        }
                    }
                    break;
                case CavPhase.Dodge:
                    // ~0.65s lateral burst, then straight back to business.
                    if (now - _cavPhaseAt > 0.65f)
                    {
                        _cavPhase = dist <= reach * 1.3f ? CavPhase.Brawl : CavPhase.Charge;
                        _cavPhaseAt = now;
                        _cavBrawlUntil = now + 2.0f;
                        _cavBrawlSwings = 0;
                    }
                    break;
                case CavPhase.HaltTurn:
                    // pivoted to face them (or timed out) -> fight at point
                    // blank if they're still there, else re-charge.
                    if (MathF.Abs(bearDeg) < 55f || now - _cavPhaseAt > 1.0f)
                    {
                        _cavPhase = dist <= reach * 1.4f ? CavPhase.Brawl : CavPhase.Charge;
                        _cavPhaseAt = now;
                        _cavBrawlUntil = now + 2.0f;
                        _cavBrawlSwings = 0;
                    }
                    break;
                case CavPhase.Pass:
                    if (MathF.Abs(bearDeg) > 100f || now - _cavPhaseAt > 1.5f)
                    {
                        _cavPhase = CavPhase.Wheel;
                        _cavPhaseAt = now;
                        LogCavPass(reach);
                    }
                    break;
                case CavPhase.Wheel:
                    if (MathF.Abs(bearDeg) < 40f || now - _cavPhaseAt > 5f)
                    {
                        _cavPhase = CavPhase.Charge;
                        _cavPhaseAt = now;
                        _cavCameClose = false;
                        _cavPassMin = float.MaxValue;
                        _cavPassStage = Agent.ActionStage.None;
                        _cavPassRel = false;
                    }
                    break;
                case CavPhase.Escape:
                    if (!_cavEscLogged)
                    {
                        _cavEscLogged = true;
                        if (_cavEscLogCount < MaxCavEscLogs)
                        {
                            _cavEscLogCount++;
                            CombatAiLog.Log($"[CAV] escape Agent #{Agent.Index}: dist={dist:F2} spd={mountSpd:F1} " +
                                $"ahead={_cavAheadDist:F1} escDir=({ _cavEscDir.x:F2},{_cavEscDir.y:F2})");
                        }
                    }
                    if (now - _cavPhaseAt > 0.9f || _cavAheadDist > 5f)
                    {
                        _cavPhase = CavPhase.Charge;
                        _cavPhaseAt = now;
                        _cavStallAt = -1f;
                    }
                    break;
            }

            // pass telemetry (min dist + what we did at it) across Charge+Pass
            if (_cavPhase != CavPhase.Wheel && _cavPhase != CavPhase.Escape && dist < _cavPassMin)
            {
                _cavPassMin = dist;
                _cavPassStage = ownStage;
            }
            if (ownStage == Agent.ActionStage.AttackRelease && dist <= reach * 1.5f)
                _cavPassRel = true;

            // ---- aim point + desired-speed hint per phase ----
            bool pitKnown = m.Boundaries.ContainsKey("combatai_pit");
            Vec2 aim;
            float vPhase;
            switch (_cavPhase)
            {
                case CavPhase.Charge:
                    {
                        float t = MathF.Max(0f, MathF.Min(1.2f, dist / MathF.Max(selfSpd, 4f)));
                        aim = tgtPos + tgtVelW * t;
                        // pursuit needs real speed -- catch a fleeing player,
                        // charge a distant one (user: "need speed to catch").
                        vPhase = closing < -0.5f || dist > 11f ? _cavMaxSpd * 0.8f : 8.5f;
                        // spur: one-tick double-W pulse, ONLY when lined up
                        // and far -- galloping into a turn is the ai49 bug.
                        if (mount != null && mountAct != Agent.ActionCodeType.Dash
                            && dist > 9f && MathF.Abs(bearDeg) < 35f
                            && mountSpd > 2f && mountSpd < _cavDesired - 3f
                            && now - _cavSpurAt >= 2.2f)
                        {
                            _cavSpurAt = now;
                            try { Agent.EventControlFlags |= Agent.EventControlFlag.DoubleTapToDirectionUp; } catch { }
                            if (_cavEscLogCount < MaxCavEscLogs)
                            {
                                _cavEscLogCount++;
                                CombatAiLog.Log($"[CAV] spur Agent #{Agent.Index}: dist={dist:F2} spd={mountSpd:F1} des={_cavDesired:F1} max={_cavMaxSpd:F1}");
                            }
                        }
                        break;
                    }
                case CavPhase.Brawl:
                    // up-close fight: stay on them (mild lead keeps the
                    // press), modest pace so the horse doesn't ride off.
                    aim = tgtPos + tgtVelW * 0.35f;
                    vPhase = 4.5f;
                    break;
                case CavPhase.Dodge:
                    aim = selfPos + _cavDodgeDir * 9f;
                    vPhase = 8.5f;
                    break;
                case CavPhase.HaltTurn:
                    aim = tgtPos;      // aim AT them: the arc clamp pivots us
                    vPhase = 0f;       // near-stop while turning (the halt)
                    break;
                case CavPhase.Pass:
                    aim = selfPos + f * 10f;   // run it out to open the wheel room
                    vPhase = 9.5f;
                    break;
                case CavPhase.Wheel:
                    // Aim AT the target: Turn+Forward arcs the horse around
                    // (old +-90 side-point pinned hdgErr and crawled to a stop).
                    aim = tgtPos;
                    vPhase = 6.5f;
                    break;
                default: // Escape: brake/back-step along the open probe dir
                    aim = selfPos + _cavEscDir * 8f;
                    vPhase = 0f;
                    break;
            }
            // Boundary guard: ~6m ahead leaving the pit -> aim at the centre.
            if (pitKnown && _cavPhase != CavPhase.Escape && !m.IsPositionInsideBoundaries(selfPos + f * 6f))
                aim = CavPitCentre(m, selfPos, tgtPos);

            // ---- steering output ----
            Vec2 h = aim - selfPos;
            if (h.LengthSquared > 0.0001f)
                h = FlatUnit(h);
            else
                h = f;
            // signed angle from horse facing to desired heading: >0 = left.
            float err = MathF.Atan2(f.x * h.y - f.y * h.x, f.x * h.x + f.y * h.y);

            // Feasible-arc clamp (native-verified model): a horse turns on an
            // arc, not on the spot -- demanding >~75deg off the nose just
            // overshoots. Re-aim along the arc it CAN take; the aim sweeps
            // onto the real target as the horse rotates. Gives the wheel
            // circle for free (target behind -> aim sits ~50deg off).
            if ((_cavPhase == CavPhase.Charge || _cavPhase == CavPhase.Wheel
                || _cavPhase == CavPhase.HaltTurn)
                && MathF.Abs(err) > 1.31f)
            {
                float clampRad = MathF.Min(MathF.Abs(err) * 0.6f, 0.9f);
                aim = selfPos + RotDir(f, MathF.Sign(err) * clampRad) * MathF.Max(4f, dist);
                h = FlatUnit(aim - selfPos);
                err = MathF.Atan2(f.x * h.y - f.y * h.x, f.x * h.x + f.y * h.y);
            }

            // ---- speed law (native gait-demand model, research/17) ----
            // desired = min(phase, turn-feasible, obstacle-feasible). Native
            // couples turn to gait (vel·desiredDir < floor -> Walk); we use
            // tiered |err| caps + predictive early-brake: if we'd ARRIVE
            // while the heading change is still pending, brake BEFORE the
            // corner -- "evaluate in time when they can't turn".
            float absErrDeg = MathF.Abs(err * 57.2958f);
            float vTurn = absErrDeg > 100f ? 4.5f : absErrDeg > 60f ? 6.5f : absErrDeg > 30f ? 9.5f : _cavMaxSpd;
            float tArr = dist / MathF.Max(1.5f, closing);
            if (tArr < 1.2f && absErrDeg > 45f)
                vTurn = MathF.Min(vTurn, 4.5f);
            float vObs = _cavAheadByAgent ? _cavMaxSpd : (_cavAheadDist - 1.2f) / 0.45f;
            float desired = MathF.Max(
                _cavPhase == CavPhase.Escape || _cavPhase == CavPhase.HaltTurn ? 0f : 2.5f,
                MathF.Min(vPhase, MathF.Min(vTurn, vObs)));
            _cavDesired = desired;

            // +0x114 ceiling: bounds runaway a little above the demand; the
            // band below does the regulating, not the cap.
            float cap = MathF.Max(desired + 2f, 4f);
            if (mount != null && MathF.Abs(cap - _cavLastCap) > 0.4f)
            {
                _cavLastCap = cap;
                try { Agent.SetMaximumSpeedLimit(cap, false); mount.SetMaximumSpeedLimit(cap, false); } catch { }
            }

            // Band throttle: below -> W build; inside -> coast y=0 (the
            // smoother holds the pace -- research/17 verified); over -> S
            // tap; way over -> S+E scrub pulse (MountQuickStop is
            // PROGRESSIVE, not instant -- user-verified it scrubs speed at
            // high speed while keeping momentum).
            float throttle;
            if (_cavPhase == CavPhase.Escape)
            {
                throttle = 0f;
            }
            else if (_cavPhase == CavPhase.HaltTurn)
            {
                // the sudden halt: brake while rolling, ease once ~stopped.
                throttle = mountSpd > 0.5f ? -0.6f : -0.2f;
            }
            else if (mountSpd < desired - 0.8f)
            {
                throttle = 1f;
            }
            else if (mountSpd > desired + 3.5f)
            {
                throttle = -0.5f;
                if (now - _cavHaltAt > 1.4f)
                {
                    _cavHaltAt = now;
                    try { Agent.EventControlFlags |= Agent.EventControlFlag.DoubleTapToDirectionDown; } catch { }
                    if (_cavEscLogCount < MaxCavEscLogs)
                    {
                        _cavEscLogCount++;
                        CombatAiLog.Log($"[CAV] scrub Agent #{Agent.Index}: spd={mountSpd:F1} des={desired:F1} dist={dist:F2} err={absErrDeg:F0}");
                    }
                }
            }
            else if (mountSpd > desired + 1.2f)
            {
                throttle = -0.4f;
            }
            else
            {
                throttle = 0f;   // coast: pace held by the smoother, no held W
            }

            const float deadband = 8f * 0.0174533f;
            Agent.MovementControlFlag turn = err > deadband ? Agent.MovementControlFlag.TurnLeft
                : err < -deadband ? Agent.MovementControlFlag.TurnRight
                : Agent.MovementControlFlag.None;

            if (CombatAiState.CavSteer != CombatAiState.CavSteerT.Off)
            {
                if (_cavPhase == CavPhase.Escape)
                {
                    // S-key: brake while moving, reverse step once ~stopped.
                    _cavInputVec = new Vec2(0f, mountSpd > 0.6f ? -0.4f : -0.45f);
                    _cavMoveFlag = Agent.MovementControlFlag.Backward | turn;
                }
                else
                {
                    bool vecMode = CombatAiState.CavSteer == CombatAiState.CavSteerT.Vec;
                    // VERIFIED native semantics (mod_0068 decoder): on a mount
                    // inputVec.x is LATERAL velocity (side-pass), and only a
                    // turn-rate scaler when its turn flag is set. x=0 + flag
                    // -> full-rate rotate with NO drift. x=0 is the default:
                    // never steer a horse with x alone.
                    // strafe clamp: lateral demand capped at +-0.35 so the
                    // horse can't side-drift wide (the ai "strafes too much"
                    // complaint); Turn/Both already ride x=0.
                    float x = vecMode
                        ? MathF.Max(-0.35f, MathF.Min(0.35f, h.x * f.y - h.y * f.x))
                        : 0f;
                    _cavInputVec = new Vec2(x, throttle);
                    // fwdCmd = y + (Fwd -> +1) - (Back -> -1): the flag bits
                    // alone saturate demand, so Forward only at full pace and
                    // Backward only when actually braking.
                    _cavMoveFlag = vecMode
                        ? Agent.MovementControlFlag.None
                        : turn
                            | (throttle >= 0.95f ? Agent.MovementControlFlag.Forward : Agent.MovementControlFlag.None)
                            | (throttle <= -0.3f ? Agent.MovementControlFlag.Backward : Agent.MovementControlFlag.None);
                    // Native held-gait demand (research/17): the Run LEVEL,
                    // not held W, is what keeps a mount at pace.
                    try { Agent.EventControlFlags |= Agent.EventControlFlag.Run; } catch { }
                }
            }

            // ---- attack gate v3: no feints, no couch, up/down dirs only,
            // and the hold survives the whole windup (stage None IS the
            // windup -- the old 0.6s None-check was feint-cancelling real
            // swings, the "doesn't attack / delayed" bug). ----

            // (a) feint suppression: a feint state gets a real release or a
            // clean rollback -- never the fake-out.
            if (_duelState == DuelState.FeintCancel || _duelState == DuelState.FeintReady)
            {
                _duelState = DuelState.Approach;
                _duelTimer = 0f;
                _cavHolding = false;
                fsm &= ~Agent.MovementControlFlag.AttackMask;
            }
            // (b) simple up/down attacks: lateral starts remap to
            // alternating Up/Down (mounted swings only have these anyway).
            if ((fsm & (Agent.MovementControlFlag.AttackLeft
                      | Agent.MovementControlFlag.AttackRight)) != 0)
            {
                _cavDirAlt = !_cavDirAlt;
                fsm &= ~Agent.MovementControlFlag.AttackMask;
                fsm |= _cavDirAlt
                    ? Agent.MovementControlFlag.AttackUp
                    : Agent.MovementControlFlag.AttackDown;
            }

            bool midOwnAttack = ownStage == Agent.ActionStage.AttackReady
                || ownStage == Agent.ActionStage.AttackQuickReady
                || ownStage == Agent.ActionStage.AttackRelease;
            float strikeGate = reach + MathF.Min(4f, closing * 0.35f);
            float releaseGate = reach + MathF.Min(2.5f, closing * 0.3f);
            bool ownCouch = false;
            try { ownCouch = Agent.IsDoingPassiveAttack; } catch { }
            if (CombatAiState.CavHold && _cavHolding)
            {
                bool ready = ownStage == Agent.ActionStage.AttackReady
                    || ownStage == Agent.ActionStage.AttackQuickReady;
                // lead-aware release: fire at where they'll BE in ~0.22s
                // (release-to-hit), and tighten the bearing gate vs their
                // lateral crossing speed -- the "misses when I move" fix.
                float relDist = dist - MathF.Max(0f, closing) * 0.22f;
                float latSpd = MathF.Abs(
                    (tgtVelW.x - selfVelW.x) * -dirT.y + (tgtVelW.y - selfVelW.y) * dirT.x);
                float bearGate = MathF.Max(35f, 70f - latSpd * 6f);
                bool releaseNow = ready && relDist <= releaseGate && MathF.Abs(bearDeg) <= bearGate;
                // dead = the engine released it on its own (or couch fired
                // instead -- drop the bit so the couch ends).
                bool dead = ownStage == Agent.ActionStage.AttackRelease || ownCouch;
                bool expired = now - _cavHoldAt > 2.0f;
                // past = target gone before the windup ever readied; drop
                // it (feint-cancel if winding, harmless air release if ready).
                float holdGate = strikeGate + MathF.Max(2.5f, MathF.Min(6f, closing * 0.8f));
                bool past = !ready && (MathF.Abs(bearDeg) > 105f || dist > holdGate + 3f);
                if (releaseNow || dead || expired || past)
                {
                    _cavHolding = false;
                    fsm &= ~Agent.MovementControlFlag.AttackMask;
                    if (_cavSwingLogCount < MaxCavSwingLogs)
                    {
                        _cavSwingLogCount++;
                        CombatAiLog.Log($"[CAV] rel Agent #{Agent.Index}: " +
                            $"{(releaseNow ? "strike" : dead ? (ownCouch ? "couch" : "dead") : expired ? "expire" : "past")} " +
                            $"dist={dist:F2} bear={bearDeg:F0} lat={latSpd:F1} gate={bearGate:F0} " +
                            $"stage={ownStage} held={(now - _cavHoldAt):F2}s");
                    }
                }
                else
                {
                    // keep the button held: re-assert the armed direction so
                    // the engine stays in ready even if the FSM moved on.
                    fsm = (fsm & ~Agent.MovementControlFlag.AttackMask) | _cavHoldDir;
                }
            }
            else if (ownStage == Agent.ActionStage.None && _duelState == DuelState.Windup
                && (fsm & Agent.MovementControlFlag.AttackMask) != 0 && !ownCouch)
            {
                // hold window = strike gate + closing-scaled lead: commits
                // inside it are ARMED (held by the block above); outside it
                // roll the state back to Approach and strip the start flag
                // -- repeat-safe, dodges the windup failsafe.
                float holdGate = CombatAiState.CavHold
                    ? strikeGate + MathF.Max(2.5f, MathF.Min(6f, closing * 0.8f))
                    : strikeGate;
                if (dist <= holdGate && MathF.Abs(bearDeg) <= 80f)
                {
                    if (CombatAiState.CavHold)
                    {
                        _cavHolding = true;
                        _cavHoldAt = now;
                        _cavHoldDir = fsm & Agent.MovementControlFlag.AttackMask;
                        if (_cavSwingLogCount < MaxCavSwingLogs)
                        {
                            _cavSwingLogCount++;
                            CombatAiLog.Log($"[CAV] hold Agent #{Agent.Index}: dir={_cavHoldDir} " +
                                $"dist={dist:F2} closing={closing:F2} gate={holdGate:F1} spd={mountSpd:F1}");
                        }
                    }
                }
                else
                {
                    _duelState = DuelState.Approach;
                    _duelTimer = 0f;
                    fsm &= ~Agent.MovementControlFlag.AttackMask;
                    _cavGateStrips++;
                }
            }

            // ---- cav defense layer (spec §6): release-only ----
            bool earlyBlock = false, stripped = false;
            string blkSrc = "-";
            // (a) no standoff shield-holding: strip distant Defend* flags.
            float farGate = reach + MathF.Max(1f, MathF.Min(6f, closing * 0.5f)) + 1.0f;
            if (!midOwnAttack && dist > farGate
                && (fsm & (Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock)) != 0)
            {
                fsm &= ~(Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock);
                stripped = true;
            }
            // (b) perfect block: ONLY on the enemy's release edge (or a live
            // couch -- no AttackRelease edge exists for passive usage). Gate
            // scales with closing so the flag lands before the tip arrives.
            const float blockLead = 0.35f;
            float blockGate = threatReach + closing * blockLead;
            if (!midOwnAttack && dist <= blockGate
                && (tgtStage == Agent.ActionStage.AttackRelease || tgtCouch))
            {
                Agent.MovementControlFlag def = BlockFlagVs(tgt, tgt.GetCurrentActionDirection(1));
                if (def == Agent.MovementControlFlag.None && tgtCouch)
                    def = Agent.MovementControlFlag.DefendDown;   // couch = thrust-class fallback
                if (def != Agent.MovementControlFlag.None)
                {
                    fsm |= def;
                    earlyBlock = true;
                    blkSrc = tgtCouch ? "couch" : "release";
                }
            }
            if (earlyBlock && !_cavBlockHeld && _cavBlkLogCount < MaxCavBlkLogs)
            {
                _cavBlkLogCount++;
                CombatAiLog.Log($"[CAV] block Agent #{Agent.Index}: src={blkSrc} dist={dist:F2} " +
                    $"closing={closing:F2} tgtStage={tgtStage} tgtAct={tgtAct0}/{tgtAct1} prog={tgtProg:F2} gate={blockGate:F1}");
            }
            _cavBlockHeld = earlyBlock;

            // ---- telemetry ----
            bool ownReady = ownStage == Agent.ActionStage.AttackReady
                || ownStage == Agent.ActionStage.AttackQuickReady;
            bool prevReady = _cavPrevStage == Agent.ActionStage.AttackReady
                || _cavPrevStage == Agent.ActionStage.AttackQuickReady;
            if (_cavSwingLogCount < MaxCavSwingLogs
                && ((ownReady && !prevReady) || (ownStage == Agent.ActionStage.AttackRelease && _cavPrevStage != Agent.ActionStage.AttackRelease)))
            {
                _cavSwingLogCount++;
                CombatAiLog.Log($"[CAV] swing Agent #{Agent.Index}: {(ownReady ? "ready" : "release")} " +
                    $"dist={dist:F2} closing={closing:F2} bear={bearDeg:F0} stage={ownStage} " +
                    $"ownAct={Agent.GetCurrentActionType(0)}/{Agent.GetCurrentActionType(1)}");
            }
            _cavPrevStage = ownStage;
            if (_cavLogCount < MaxCavLogs && now - _cavLastLogAt >= 0.4f)
            {
                _cavLastLogAt = now;
                _cavLogCount++;
                CombatAiLog.Log($"[CAV] Agent #{Agent.Index} phase={_cavPhase} dist={dist:F2} " +
                    $"bearing={bearDeg:F0} closing={closing:F2} mV={mountSpd:F1} mAct={mountAct} cap={_cavLastCap:F1} " +
                    $"hdgErr={err * 57.2958f:F0} ahead={_cavAheadDist:F1} des={_cavDesired:F1} trn={_cavTurnRate:F0} " +
                    $"iv=({_cavInputVec.x:F2},{_cavInputVec.y:F2}) mv={_cavMoveFlag} steer={CombatAiState.CavSteer} " +
                    $"state={_duelState} stage={ownStage} ownAct={Agent.GetCurrentActionType(0)}/{Agent.GetCurrentActionType(1)} " +
                    $"tgtStage={tgtStage} tgtAct={tgtAct0}/{tgtAct1} prog={tgtProg:F2} couch={(tgtCouch ? 1 : 0)} " +
                    $"blk={blkSrc}/{(stripped ? 1 : 0)}strip gate={_cavGateStrips} " +
                    $"hold={(_cavHolding ? 1 : 0)}");
            }
            return fsm;
        }

        // Most-open escape heading: probe +-60 / +-120 deg cones from the
        // current heading with the mount's own fit-probe (mount radius) and
        // keep the longest clear run.
        private Vec2 CavPickOpenDir(Agent mount, Vec2 selfPos, Vec2 f)
        {
            if (mount == null)
                return RotDir(f, 1.5708f);
            float best = -1f;
            Vec2 bestD = f;
            for (int i = -2; i <= 2; i++)
            {
                if (i == 0)
                    continue;
                Vec2 d = RotDir(f, i * 0.9f);
                float dd;
                try
                {
                    Vec2 res = mount.FindLongestDirectMoveToPosition(selfPos + d * 6f, true, true, out _);
                    dd = (res - selfPos).Length;
                }
                catch { dd = 0f; }
                if (dd > best)
                {
                    best = dd;
                    bestD = d;
                }
            }
            return bestD;
        }

        // Hand the body back to native: uncap rider + mount speed and clear
        // the held input -- without this a disable leaves the mount capped at
        // ~1.5-4.5 m/s and walking in the last commanded direction (ai49:
        // "rider stops riding after combatai.disable"). Cheap no-op unless we
        // actually capped or drove.
        private void CavReleaseControl()
        {
            bool held = _cavLastCap >= 0f || _cavDriving;
            _cavLastCap = -1f;
            _cavInputVec = Vec2.Zero;
            _cavMoveFlag = Agent.MovementControlFlag.None;
            _cavDriving = false;
            _cavHolding = false;   // a disable mid-hold must not freeze the bit
            if (!held || !Live(Agent))
                return;
            try
            {
                Agent.SetMaximumSpeedLimit(-1f, isMultiplier: false);
                if (Agent.MovementInputVector.LengthSquared > 0.0001f)
                    Agent.MovementInputVector = Vec2.Zero;
                Agent mnt = Agent.MountAgent;
                if (mnt != null && CombatLive(mnt))
                    mnt.SetMaximumSpeedLimit(-1f, isMultiplier: false);
                CombatAiLog.Log($"[CAV] release Agent #{Agent.Index}: mount+rider speed caps cleared, input restored to native.");
            }
            catch { }
        }

        private void LogCavPass(float reach)
        {
            if (_cavPassLogCount >= MaxCavPassLogs)
                return;
            _cavPassLogCount++;
            CombatAiLog.Log($"[CAV] pass Agent #{Agent.Index}: minDist={(_cavPassMin < float.MaxValue ? _cavPassMin.ToString("F2") : "na")} " +
                $"stageAtMin={_cavPassStage} releasedWithinReach={(_cavPassRel ? 1 : 0)} reach={reach:F2}");
        }

        private Vec2 CavPitCentre(Mission m, Vec2 selfPos, Vec2 tgtPos)
        {
            if (!_cavPitCentreSet)
            {
                _cavPitCentreSet = true;
                try
                {
                    if (m.Boundaries.TryGetValue("combatai_pit", out ICollection<Vec2> pts)
                        && pts != null && pts.Count > 0)
                    {
                        Vec2 c = Vec2.Zero;
                        int n = 0;
                        foreach (Vec2 p in pts) { c += p; n++; }
                        if (n > 0)
                            _cavPitCentre = new Vec2(c.x / n, c.y / n);
                    }
                }
                catch { /* centre stays unset -> midpoint fallback */ }
            }
            if (_cavPitCentre.LengthSquared < 0.0001f)
                return (selfPos + tgtPos) * 0.5f;   // midpoint of both fighters
            return _cavPitCentre;
        }
    }
}
