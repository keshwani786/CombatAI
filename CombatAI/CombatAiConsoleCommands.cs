using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // Console commands (Bannerlord's in-game console, opened with the tilde/backtick key):
    //   combatai.enable        - arm the override (no mode needed first; turn modes on directly)
    //   combatai.disable       - STOP everything: OverrideEnabled off + all test modes off
    //   combatai.attackloop [on|off]   - agents keep forcing AttackRight forever (does it actually connect/damage?)
    //   combatai.perfectblock [on|off] - agents react the instant the player's swing releases with the mirrored Defend (parry test)
    //   combatai.godmode [on|off]      - pin player + all tracked agents at 100k HealthLimit/Health every tick
    //                                    (overkill-proof: a >100dmg hit can't one-shot; limits restore on off)
    //   combatai.evade [on|off]        - evade-only mode: 100% step dodge vs every committed release (never blocks)
    //   combatai.duel [on|off]         - the v1 combat brain: attack variety, feints,
    //                                  punish early blocks, chance-based defense
    //   combatai.posture               - print current player posture
    //   combatai.status   - print current mode states and where the log file is
    //   combatai.clearlog - erase combat_ai.log contents (and reset the diagnostic
    //                       log caps) so each build/test run starts fresh
    //   combatai.openlog  - open combat_ai.log in Notepad
    //   combatai.tail [n] - print the last N lines of combat_ai.log to the message log
    //   combatai.speedlog [on|off] - ground-truth speed diagnostic: logs actual
    //                       measured velocity + WalkMode + speedLimit + handoff
    //                       state every ~0.4s per driven duel agent
    //   combatai.movefix [gait|window|all] [on|off] - Session 48 A/B toggles:
    //                       gait = Run via the callback eventFlag channel;
    //                       window = native locomotion during guard/ready
    //                       windows (plain duel only)
    //   combatai.memory    - append the long-term player-move memory analysis
    //                       (persisted inside the campaign save) to combat_ai.log
    //   combatai.faceframe [off|angle <deg>|spin] [factor <0..1>] [agent <idx>] [lock self|player]
    //                       - facing-priority experiment: does a formation frame's
    //                         direction override a held combat target lock? One
    //                         agent stays LOCKED (on itself = default Option-Z;
    //                         on player = control) while a formation frame asks
    //                         for a different facing ([FACEFRAME] logs)
    //   combatai.nativehook [scan|hook|guard|status|off]
    //                       - native-hook infra + target-release crash guard
    //                         ([NH] log). Default OFF; nothing hooks until armed.
    //   combatai.unlock [agent <idx>]
    //                       - release the combat target lock on one agent via the
    //                         poked path (needs nativehook hook|guard armed)
    //   combatai.freetarget [off|on|attack|status]
    //                       - duel + spar defender never hold a combat target;
    //                         a formation frame owns facing/legs instead
    //                         (auto-arms the native guard; off = legacy)
    public static class CombatAiConsoleCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("enable", "combatai")]
        public static string Enable(List<string> args)
        {
            CombatAiState.OverrideEnabled = true;
            CombatAiLog.Log("[CONSOLE] combatai.enable -> override ENABLED (armed).");
            return "CombatAI armed. Nothing needs enabling first: turn modes on directly " +
                   "(combatai.duel on, combatai.defense on, ...). combatai.disable = stop everything.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("disable", "combatai")]
        public static string Disable(List<string> args)
        {
            CombatAiState.OverrideEnabled = false;
            CombatAiState.DisableAllModes();
            CombatAiLog.Log("[CONSOLE] combatai.disable -> override DISABLED, all test modes off.");
            return "CombatAI STOPPED: all test modes off, base game AI has full control.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("attackloop", "combatai")]
        public static string AttackLoop(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.ContinuousAttackMode) : !CombatAiState.ContinuousAttackMode;
            CombatAiState.ContinuousAttackMode = enable;
            string state = enable ? "ON" : "OFF";
            CombatAiLog.Log($"[CONSOLE] combatai.attackloop -> {state}.");
            return $"Continuous attack loop (forces AttackRight every tick, forever): {state}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("perfectblock", "combatai")]
        public static string PerfectBlock(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.PerfectBlockMode) : !CombatAiState.PerfectBlockMode;
            CombatAiState.PerfectBlockMode = enable;
            string state = enable ? "ON" : "OFF";
            CombatAiLog.Log($"[CONSOLE] combatai.perfectblock -> {state}.");
            return $"Perfect reactive block (watches player, forces mirrored Defend the instant their swing releases, checks Parried vs Blocked outcome): {state}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("godmode", "combatai")]
        public static string GodMode(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.GodMode) : !CombatAiState.GodMode;
            CombatAiState.GodMode = enable;
            string state = enable ? "ON" : "OFF";
            CombatAiLog.Log($"[CONSOLE] combatai.godmode -> {state}.");
            return $"God mode (player + tracked agents healed to full every tick): {state}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("duel", "combatai")]
        public static string Duel(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.DuelMode) : !CombatAiState.DuelMode;
            CombatAiState.DuelMode = enable;
            string state = enable ? "ON" : "OFF";
            CombatAiLog.Log($"[CONSOLE] combatai.duel -> {state}.");
            return $"Duel brain (attack variety + feints + early-block punish + chance defense): {state}.";
        }

        // combatai.cav [on|off|status] -- CAVALRY DUEL brain (Phase 1): mounted
        // 1v1 -- the shared duel FSM keeps attack/defend/lock; the cav planner
        // (charge -> pass -> wheel) owns locomotion on the input channel. The
        // agent must be MOUNTED (arena 'Cavalry Duel (mounted 1v1)' mounts both
        // fighters via ArenaCavalryPatch). DuelMode turns on with it, like
        // combatai.defense on.
        [CommandLineFunctionality.CommandLineArgumentFunction("cav", "combatai")]
        public static string Cav(List<string> args)
        {
            if (args.Count > 0 && args[0].Trim().ToLowerInvariant() == "status")
                return $"cav={(CombatAiState.CavDuelMode ? "on" : "off")} steer={CombatAiState.CavSteer} duel={CombatAiState.DuelMode}.";
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.CavDuelMode) : !CombatAiState.CavDuelMode;
            CombatAiState.CavDuelMode = enable;
            if (enable)
                CombatAiState.DuelMode = true;
            string state = enable ? "ON" : "OFF";
            CombatAiLog.Log($"[CONSOLE] combatai.cav -> {state}.");
            return $"Cavalry duel brain (mounted 1v1: charge/pass/wheel planner + shared duel FSM; steer={CombatAiState.CavSteer}): {state}.";
        }

        // combatai.cavsteer [off|vec|turn|both|status] -- which channel steers
        // the horse: vec = facing-relative MovementInputVector (default),
        // turn = TurnLeft/TurnRight flags + forward input, both, off = no cav
        // locomotion writes (native baseline).
        [CommandLineFunctionality.CommandLineArgumentFunction("cavsteer", "combatai")]
        public static string CavSteer(List<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                switch (a)
                {
                    case "off":  CombatAiState.CavSteer = CombatAiState.CavSteerT.Off; break;
                    case "vec":  CombatAiState.CavSteer = CombatAiState.CavSteerT.Vec; break;
                    case "turn": CombatAiState.CavSteer = CombatAiState.CavSteerT.Turn; break;
                    case "both": CombatAiState.CavSteer = CombatAiState.CavSteerT.Both; break;
                    case "status": break;
                    default:
                        return "Usage: combatai.cavsteer [off|vec|turn|both|status]";
                }
            }
            string line = $"cavsteer={CombatAiState.CavSteer} (cav={CombatAiState.CavDuelMode}).";
            CombatAiLog.Log("[CONSOLE] combatai.cavsteer " + line);
            return line;
        }

        // combatai.cavhold [on|off|status] -- hold-to-release attack gate for
        // the cav brain: windup starts early and the attack flag stays
        // asserted through the charge so the release lands the instant the
        // tip arrives; off restores the old strip-out-of-range gate.
        [CommandLineFunctionality.CommandLineArgumentFunction("cavhold", "combatai")]
        public static string CavHold(List<string> args)
        {
            if (args.Count > 0 && args[0].Trim().ToLowerInvariant() == "status")
                return $"cavhold={(CombatAiState.CavHold ? "on" : "off")} (cav={CombatAiState.CavDuelMode}).";
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.CavHold) : !CombatAiState.CavHold;
            CombatAiState.CavHold = enable;
            string line = $"cavhold={enable} (attack readied early and held through the charge; default on).";
            CombatAiLog.Log("[CONSOLE] combatai.cavhold " + line);
            return line;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("group", "combatai")]
        public static string Group(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.GroupFightMode) : !CombatAiState.GroupFightMode;
            CombatAiState.GroupFightMode = enable;
            if (!enable)
                EngagementMap.Reset();
            GroupTargeting.Reset();   // fresh anchor registry on every toggle
            string state = enable ? "ON" : "OFF";
            CombatAiLog.Log($"[CONSOLE] combatai.group -> {state}.");
            return $"Group fight (infantry field AI: mutual pairing, gang-up flankers, outnumbered-defender grace, T1 vs player / T2 AI-vs-AI): {state}. Meant for custom battles with melee infantry only.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("dodge", "combatai")]
        public static string Dodge(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.DodgeEnabled) : !CombatAiState.DodgeEnabled;
            CombatAiState.DodgeEnabled = enable;
            string state = enable ? "ON" : "OFF";
            CombatAiLog.Log($"[CONSOLE] combatai.dodge -> {state}.");
            return $"Step dodge (side/back step instead of block on some releases; outnumbered AI dodges more): {state}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("evade", "combatai")]
        public static string Evade(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.EvadeMode) : !CombatAiState.EvadeMode;
            CombatAiState.EvadeMode = enable;
            string state = enable ? "ON" : "OFF";
            CombatAiLog.Log($"[CONSOLE] combatai.evade -> {state}.");
            return $"Evade-only mode (every eligible agent step-dodges EVERY committed release in reach, 100% -- never blocks, never attacks): {state}.";
        }

        // combatai.angledodge [on|off] -- like perfectblock, but with the DODGE: when
        // the player holds a Left/Right swing from only a slight camera angle (the
        // existing dodge rule would block it), the AI strafes until the dodge
        // condition is true, then does the normal dodge at the release. No attacks;
        // dodge logic is not modified.
        [CommandLineFunctionality.CommandLineArgumentFunction("angledodge", "combatai")]
        public static string AngleDodge(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.AngleDodgeMode) : !CombatAiState.AngleDodgeMode;
            CombatAiState.AngleDodgeMode = enable;
            string note = "";
            if (enable)
            {
                // Higher-priority modes would silently win over this one: switch them off.
                bool other = CombatAiState.DuelMode || CombatAiState.DefenseOnly || CombatAiState.SparMode
                    || CombatAiState.GroupFightMode || CombatAiState.PerfectBlockMode || CombatAiState.EvadeMode
                    || CombatAiState.ContinuousAttackMode || CombatAiState.LookTestMode;
                if (other)
                {
                    CombatAiState.DuelMode = false;
                    CombatAiState.DefenseOnly = false;
                    CombatAiState.SparMode = false;
                    CombatAiState.SparDefenderIndex = -1;
                    CombatAiState.SparAttackerIndex = -1;
                    CombatAiState.GroupFightMode = false;
                    CombatAiState.PerfectBlockMode = false;
                    CombatAiState.EvadeMode = false;
                    CombatAiState.ContinuousAttackMode = false;
                    CombatAiState.LookTestMode = false;
                    note = " (other combat modes were switched off so this one is the only driver)";
                }
                CombatAiState.DodgeEnabled = true;
            }
            string state = enable ? "ON" : "OFF";
            CombatAiLog.Log($"[CONSOLE] combatai.angledodge -> {state}.");
            return $"Angle-dodge test mode (hold a Left/Right swing from a slight camera angle: the AI strafes until the dodge condition is true, then does the normal dodge; never attacks): {state}.{note}";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("defense", "combatai")]
        public static string Defense(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.DefenseOnly) : !CombatAiState.DefenseOnly;
            CombatAiState.DefenseOnly = enable;
            // Defense-only is a duel sub-mode -- without duel on the agent
            // falls all the way through to native and "defense on" did
            // nothing (observed: fully native AI).
            if (enable)
                CombatAiState.DuelMode = true;
            string state = enable ? "ON" : "OFF";
            CombatAiLog.Log($"[CONSOLE] combatai.defense -> {state}.");
            return $"Defense-only test mode (AI approaches, faces and defends with the expectation system but NEVER attacks -- use this to see feint overcommits / panic freezes / read-scaled reaction): {state}." +
                   (enable ? " (duel mode turned on too -- defense-only is a duel sub-mode.)" : "");
        }

        // combatai.brain                 -> state dump (forecast, reliability, panic, caution, initiative)
        // combatai.brain s1|s2|s3|init|all [on|off]
        // Each defense-brain system can be switched off on its own to isolate it
        // (off falls back to the legacy behaviour for that part).
        [CommandLineFunctionality.CommandLineArgumentFunction("brain", "combatai")]
        public static string Brain(List<string> args)
        {
            if (args.Count >= 1)
            {
                string which = args[0].ToLowerInvariant();
                bool Cur()
                    => which == "s1" ? CombatAiState.BrainS1
                     : which == "s2" ? CombatAiState.BrainS2
                     : which == "s3" ? CombatAiState.BrainS3
                     : CombatAiState.BrainInitiative;
                if (which == "s1" || which == "s2" || which == "s3" || which == "init" || which == "all")
                {
                    bool enable = args.Count > 1 ? ParseOnOff(args[1], Cur()) : !Cur();
                    if (which == "s1" || which == "all") CombatAiState.BrainS1 = enable;
                    if (which == "s2" || which == "all") CombatAiState.BrainS2 = enable;
                    if (which == "s3" || which == "all") CombatAiState.BrainS3 = enable;
                    if (which == "init" || which == "all") CombatAiState.BrainInitiative = enable;
                    CombatAiLog.Log($"[CONSOLE] combatai.brain {which} -> {(enable ? "ON" : "OFF")}.");
                }
                else
                    return "Usage: combatai.brain [s1|s2|s3|init|all] [on|off]  (no args = state dump)";
            }
            float[] f = PlayerCombatModel.Forecast;
            string line = $"Brain: S1={CombatAiState.BrainS1} S2={CombatAiState.BrainS2} S3={CombatAiState.BrainS3} init={CombatAiState.BrainInitiative} | " +
                $"next-move prior: quick={f[0]:P0} normal={f[1]:P0} hold={f[2]:P0} feint={f[3]:P0} reliability={PlayerCombatModel.ForecastReliability:F2} | " +
                $"live (next {0.45f:F2}s, k={PlayerCombatModel.CpK} feints, t={PlayerCombatModel.CpT:F2}s): release={PlayerCombatModel.CpR:P0} feint={PlayerCombatModel.CpF:P0} hold={PlayerCombatModel.CpH:P0} | " +
                $"pattern: run={PlayerCombatModel.RunLength} breakRisk={PlayerCombatModel.BreakRisk:F2} breaksSpotted={PlayerCombatModel.BreakSpottedSerial} last=\"{PlayerCombatModel.LastBreakNote}\" | " +
                $"panic={CombatAiOverrideComponent.LastBrainPanic:F2} caution={CombatAiOverrideComponent.LastBrainCaution:F2} " +
                $"initiative={CombatAiOverrideComponent.LastBrainInitiative:+0;-0;0} spamSideHits={CombatAiOverrideComponent.LastBrainSpamSideHits} counterArmed={CombatAiOverrideComponent.LastBrainCounterArmed}";
            CombatAiLog.Log("[BRAIN] " + line);
            return line;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("torsomove", "combatai")]
        public static string TorsoMove(List<string> args)
        {
            // combatai.torsomove [pin|movedir|formation] [loco on|off] [log on|off]
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                if (a == "loco" || a == "log")
                {
                    bool val = i + 1 < args.Count ? ParseOnOff(args[++i], a == "loco" ? CombatAiState.TorsoMoveNativeLoco : CombatAiState.TorsoMoveLog)
                        : !(a == "loco" ? CombatAiState.TorsoMoveNativeLoco : CombatAiState.TorsoMoveLog);
                    if (a == "loco") CombatAiState.TorsoMoveNativeLoco = val;
                    else CombatAiState.TorsoMoveLog = val;
                }
                else if (a == "viewkeep" && i + 1 < args.Count)
                {
                    CombatAiState.TorsoViewKeep = ParseOnOff(args[++i], CombatAiState.TorsoViewKeep);
                }
                else if (a == "blockmove" && i + 1 < args.Count)
                {
                    string b = args[++i].Trim().ToLowerInvariant();
                    if (b == "formation") CombatAiState.TorsoBlockMove = CombatAiState.TorsoBlockMoveT.Formation;
                    else if (b == "loco") CombatAiState.TorsoBlockMove = CombatAiState.TorsoBlockMoveT.Loco;
                    else if (b == "input") CombatAiState.TorsoBlockMove = CombatAiState.TorsoBlockMoveT.Input;
                    else if (b == "off") CombatAiState.TorsoBlockMove = CombatAiState.TorsoBlockMoveT.Off;
                }
                else if (a == "factor" && i + 1 < args.Count)
                {
                    if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                    {
                        CombatAiState.TorsoFormationFactor = MathF.Max(0f, MathF.Min(1f, f));
                        CombatAiState.TorsoFactorSet = true;
                    }
                }
                else if (a == "pin" || a == "movedir" || a == "formation" || a == "free" || a == "tactical")
                {
                    CombatAiState.TorsoMoveMode = a == "pin" ? CombatAiState.TorsoMoveModeT.Pin
                        : a == "movedir" ? CombatAiState.TorsoMoveModeT.MoveDir
                        : a == "formation" ? CombatAiState.TorsoMoveModeT.Formation
                        : a == "tactical" ? CombatAiState.TorsoMoveModeT.Tactical
                        : CombatAiState.TorsoMoveModeT.Free;
                    if (!CombatAiState.TorsoFactorSet)   // per-mode factor default until the user overrides
                        CombatAiState.TorsoFormationFactor = 1f;   // Session 58: factor scales whether the frame is honoured; 1 for all modes
                }
                else return "Usage: combatai.torsomove [pin|movedir|formation|free|tactical] [loco on|off] [log on|off] [blockmove formation|loco|off] [factor 0..1] [viewkeep on|off]  (no args = state)";
            }
            string line = $"torsomove mode={CombatAiState.TorsoMoveMode} blockmove={CombatAiState.TorsoBlockMove} factor={CombatAiState.TorsoFormationFactor:F2} loco={CombatAiState.TorsoMoveNativeLoco} log={CombatAiState.TorsoMoveLog} viewkeep={CombatAiState.TorsoViewKeep} (spar defender only; pin = default).";
            CombatAiLog.Log("[CONSOLE] combatai.torsomove " + line);
            return line;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("duelblockmove", "combatai")]
        public static string DuelBlockMove(List<string> args)
        {
            // combatai.duelblockmove [off|loco|formation] [factor 0..1]
            string a = args.Count > 0 ? args[0].Trim().ToLowerInvariant() : "";
            if (a == "loco") CombatAiState.DuelBlockMove = CombatAiState.DuelBlockMoveT.Loco;
            else if (a == "formation") CombatAiState.DuelBlockMove = CombatAiState.DuelBlockMoveT.Formation;
            else if (a == "input") CombatAiState.DuelBlockMove = CombatAiState.DuelBlockMoveT.Input;
            else if (a == "off" || a == "") CombatAiState.DuelBlockMove = a == "" ? CombatAiState.DuelBlockMove : CombatAiState.DuelBlockMoveT.Off;
            else return "Usage: combatai.duelblockmove [input|off|loco|formation] [factor 0..1]  (no args = state; default input)";
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i].Trim().ToLowerInvariant() == "factor" && i + 1 < args.Count
                    && float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                    CombatAiState.DuelBlockFactor = MathF.Max(0f, MathF.Min(1f, f));
            }
            string line = $"duelblockmove={CombatAiState.DuelBlockMove} factor={CombatAiState.DuelBlockFactor:F2} (plain duel only).";
            CombatAiLog.Log("[CONSOLE] combatai.duelblockmove " + line);
            return line;
        }

        // combatai.faceframe [off|angle <deg>|spin] [factor <0..1>] [agent <idx>] [lock self|player|none]
        // Facing-priority experiment: ONE AI agent (nearest live AI human to
        // the player, or `agent <idx>`) holds a combat lock the WHOLE test --
        // EnsureTargetAgent every frame, never released/null -- while a
        // formation frame asks for a facing `angle` deg off the
        // agent->player bearing (frame pos = own pos, vel = 0: stationary).
        //   lock self   = Option-Z (DEFAULT): the agent locks onto ITSELF --
        //                 no valid facing direction to pull toward, so the
        //                 frame's direction may finally own facing
        //   lock player = control group: legs pin to the lock bearing
        //   lock none   = NO target writes at all: the slot stays as-is (run
        //                 combatai.unlock first for a true -1). Auto-select
        //                 still forced off so native can't re-lock. Is the
        //                 frame enough to own facing with no lock at all?
        //   angle 180   = face directly away from the player bearing (strongest)
        //   spin        = the offset sweeps ~45deg/s so body tracking is visible
        // [FACEFRAME] logs bearings every ~0.5s (cap 120): legs/look
        // converging to the commanded frame bearing = frame wins; legs
        // tracking `bear` = lock still owns facing (self mode: option Z
        // dead); legs drifting = lock released but frame doesn't own it.
        [CommandLineFunctionality.CommandLineArgumentFunction("faceframe", "combatai")]
        public static string FaceFrame(List<string> args)
        {
            bool wantOn = CombatAiState.FaceFrameMode;
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                if (a == "off")
                {
                    CombatAiState.FaceFrameMode = false;
                    CombatAiState.FaceFrameAgentIndex = -1;
                    wantOn = false;
                }
                else if (a == "spin")
                {
                    CombatAiState.FaceFrameSpin = true;
                    wantOn = true;
                }
                else if (a == "angle" && i + 1 < args.Count)
                {
                    if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float deg))
                    {
                        CombatAiState.FaceFrameAngleDeg = deg;
                        CombatAiState.FaceFrameSpin = false;
                    }
                    wantOn = true;
                }
                else if (a == "factor" && i + 1 < args.Count)
                {
                    if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                        CombatAiState.FaceFrameFactor = MathF.Max(0f, MathF.Min(1f, f));
                }
                else if (a == "agent" && i + 1 < args.Count)
                {
                    if (int.TryParse(args[++i], out int idx))
                        CombatAiState.FaceFrameAgentIndex = idx;
                }
                else if (a == "lock" && i + 1 < args.Count)
                {
                    string v = args[++i].Trim().ToLowerInvariant();
                    if (v == "self")
                    {
                        CombatAiState.FaceFrameLockSelf = true;
                        CombatAiState.FaceFrameLockNone = false;
                    }
                    else if (v == "player")
                    {
                        CombatAiState.FaceFrameLockSelf = false;
                        CombatAiState.FaceFrameLockNone = false;
                    }
                    else if (v == "none")
                    {
                        CombatAiState.FaceFrameLockNone = true;
                        CombatAiState.FaceFrameLockSelf = false;
                    }
                    else
                        return "Usage: combatai.faceframe [off|on|angle <deg>|spin] [factor <0..1>] [agent <idx>] [lock self|player|none] [writer frame|lookdir|lookangle] [move off|strafe|fwd|back|circle]  (no args = state; defaults angle=90 factor=1.0 lock=self writer=frame)";
                }
                else if (a == "writer" && i + 1 < args.Count)
                {
                    string v = args[++i].Trim().ToLowerInvariant();
                    if (v == "frame") CombatAiState.FaceFrameWriter = CombatAiState.FaceFrameWriterT.Frame;
                    else if (v == "lookdir") CombatAiState.FaceFrameWriter = CombatAiState.FaceFrameWriterT.LookDir;
                    else if (v == "lookangle") CombatAiState.FaceFrameWriter = CombatAiState.FaceFrameWriterT.LookAngle;
                    else
                        return "Usage: ... writer frame|lookdir|lookangle";
                    // Look-writers need the lock released: default `lock none`
                    // unless the user explicitly passes `lock` later.
                    if (CombatAiState.FaceFrameWriter != CombatAiState.FaceFrameWriterT.Frame)
                    {
                        CombatAiState.FaceFrameLockNone = true;
                        CombatAiState.FaceFrameLockSelf = false;
                    }
                    wantOn = true;
                }
                else if (a == "move" && i + 1 < args.Count)
                {
                    string v = args[++i].Trim().ToLowerInvariant();
                    if (v == "off") CombatAiState.FaceFrameMove = CombatAiState.FaceFrameMoveT.Off;
                    else if (v == "strafe") CombatAiState.FaceFrameMove = CombatAiState.FaceFrameMoveT.Strafe;
                    else if (v == "fwd") CombatAiState.FaceFrameMove = CombatAiState.FaceFrameMoveT.Fwd;
                    else if (v == "back") CombatAiState.FaceFrameMove = CombatAiState.FaceFrameMoveT.Back;
                    else if (v == "circle") CombatAiState.FaceFrameMove = CombatAiState.FaceFrameMoveT.Circle;
                    else
                        return "Usage: ... move off|strafe|fwd|back|circle";
                    wantOn = true;
                }
                else if (a == "on")
                {
                    wantOn = true;
                }
                else
                    return "Usage: combatai.faceframe [off|on|angle <deg>|spin] [factor <0..1>] [agent <idx>] [lock self|player|none]  (no args = state; defaults angle=90 factor=1.0 lock=self)";
            }

            if (wantOn && !CombatAiState.FaceFrameMode)
            {
                // Single-agent experiment: `agent <idx>` wins, else auto-pick
                // the nearest live AI-controlled human to the player.
                if (CombatAiState.FaceFrameAgentIndex < 0)
                {
                    Agent pick = FindNearestAiAgentForFaceFrame();
                    if (pick == null)
                        return "faceframe: no live AI-controlled human agent found (be in a mission with the player + AI).";
                    CombatAiState.FaceFrameAgentIndex = pick.Index;
                }
                // Higher-priority modes would silently win the drive: switch
                // them off (same exclusivize pattern as angledodge) so this
                // is the only driver on the designated agent.
                CombatAiState.DuelMode = false;
                CombatAiState.DefenseOnly = false;
                CombatAiState.SparMode = false;
                CombatAiState.SparDefenderIndex = -1;
                CombatAiState.SparAttackerIndex = -1;
                CombatAiState.GroupFightMode = false;
                CombatAiState.PerfectBlockMode = false;
                CombatAiState.EvadeMode = false;
                CombatAiState.AngleDodgeMode = false;
                CombatAiState.ContinuousAttackMode = false;
                CombatAiState.LookTestMode = false;
                CombatAiState.DuoAttackMode = false;
                CombatAiState.FaceFrameMode = true;
            }

            string modeDesc = !CombatAiState.FaceFrameMode ? "off"
                : CombatAiState.FaceFrameSpin ? "spin" : $"angle {CombatAiState.FaceFrameAngleDeg:F0}";
            string line = $"faceframe={modeDesc} factor={CombatAiState.FaceFrameFactor:F2} agent={DescribeAgentForSpar(CombatAiState.FaceFrameAgentIndex)} " +
                $"lock={(CombatAiState.FaceFrameLockNone ? "none" : CombatAiState.FaceFrameLockSelf ? "self" : "player")} " +
                $"writer={CombatAiState.FaceFrameWriter} move={CombatAiState.FaceFrameMove}. " +
                (CombatAiState.FaceFrameLockNone
                    ? "NO lock writes (slot left as-is -- combatai.unlock first for a true -1; auto-select still off)."
                    : CombatAiState.FaceFrameLockSelf
                        ? "Lock held on SELF (Option-Z: no facing pull -> frame may own facing)."
                        : "Lock held on player.") +
                " Frame pos = own pos -> stationary. [FACEFRAME] logs bearings ~0.5s.";
            CombatAiLog.Log("[CONSOLE] combatai.faceframe " + line);
            return line;
        }

        // Nearest live AI-controlled human to the player -- the faceframe
        // single-agent pick (`agent <idx>` overrides). Same mission scan the
        // spar describe helper uses.
        private static Agent FindNearestAiAgentForFaceFrame()
        {
            Mission m = Mission.Current;
            Agent main = m?.MainAgent;
            if (m == null || main == null)
                return null;
            Agent best = null;
            float bestD2 = float.MaxValue;
            foreach (Agent a in m.Agents)
            {
                if (a == null || a == main || !a.IsAIControlled || !a.IsHuman || !a.IsActive())
                    continue;
                float d2 = a.Position.DistanceSquared(main.Position);
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = a;
                }
            }
            return best;
        }

        // Session 61c: raw facing-relative input probe -- continuously writes
        // the vector to MovementInputVector regardless of stage, to isolate
        // whether the channel works at all (idle / block-no-attack / block-
        // under-attack) vs a target-lock or defend-stage conflict.
        [CommandLineFunctionality.CommandLineArgumentFunction("inputtest", "combatai")]
        public static string InputTest(List<string> args)
        {
            string a = args.Count > 0 ? args[0].Trim().ToLowerInvariant() : "";
            if (a == "fwd") CombatAiState.InputTestVec = new Vec2(0f, 1f);
            else if (a == "back") CombatAiState.InputTestVec = new Vec2(0f, -1f);
            else if (a == "left") CombatAiState.InputTestVec = new Vec2(-1f, 0f);
            else if (a == "right") CombatAiState.InputTestVec = new Vec2(1f, 0f);
            else if (a == "off" || a == "") CombatAiState.InputTestVec = a == "" ? CombatAiState.InputTestVec : Vec2.Zero;
            else return "Usage: combatai.inputtest [fwd|back|left|right|off]  (no args = state)";
            string line = $"inputtest=({CombatAiState.InputTestVec.x:F2},{CombatAiState.InputTestVec.y:F2}) (facing-relative, fwd=toward look dir; resets with combatai.disable).";
            CombatAiLog.Log("[CONSOLE] combatai.inputtest " + line);
            return line;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("sparbrain", "combatai")]
        public static string SparBrain(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.SparBrain) : !CombatAiState.SparBrain;
            CombatAiState.SparBrain = enable;
            CombatAiLog.Log($"[CONSOLE] combatai.sparbrain -> {(enable ? "ON" : "OFF")}.");
            return $"Spar-defender defence brain (S1/S2/S3 + script on the 2v1 defender): {(enable ? "ON" : "OFF")}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("sparrig", "combatai")]
        public static string SparRig(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.SparRig) : !CombatAiState.SparRig;
            CombatAiState.SparRig = enable;
            CombatAiLog.Log($"[CONSOLE] combatai.sparrig -> {(enable ? "ON" : "OFF")}.");
            return $"Spar-rig defender (no pin, WASD-channel movement, shield/side blocks): {(enable ? "ON" : "OFF")}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("sparinit", "combatai")]
        public static string SparInit(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.SparDuelInitiative) : !CombatAiState.SparDuelInitiative;
            CombatAiState.SparDuelInitiative = enable;
            CombatAiLog.Log($"[CONSOLE] combatai.sparinit -> {(enable ? "ON" : "OFF")}.");
            return $"Spar-defender duel initiative gating vs the player: {(enable ? "ON" : "OFF")}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("cbtarget", "combatai")]
        public static string CbTarget(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.CbTargetMode) : !CombatAiState.CbTargetMode;
            CombatAiState.CbTargetMode = enable;
            CombatAiState.CbTargetUserDisabled = !enable;   // "off" sticks across missions (custom-battle auto-arm honours it)
            if (enable)
                CombatAiOverrideComponent.CbClearHits();
            CombatAiLog.Log($"[CONSOLE] combatai.cbtarget -> {(enable ? "ON" : "OFF")}.");
            return $"Custom-battle native-target replica (log-only, parallel, no override): {(enable ? "ON" : "OFF")}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("gtnative", "combatai")]
        public static string GtNative(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.GroupNativeTarget) : !CombatAiState.GroupNativeTarget;
            CombatAiState.GroupNativeTarget = enable;
            CombatAiLog.Log($"[CONSOLE] combatai.gtnative -> {(enable ? "ON" : "OFF")}.");
            return $"Group-fight native-first targeting (native picks, free-look keeps threats in view; fallback anchor when native has none): {(enable ? "ON" : "OFF")}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("gf", "combatai")]
        public static string GfFeature(List<string> args)
        {
            string names = "weights defender quick nativemove updown nofeint gap onme spice defmove swap rig2v1 assist";
            if (args.Count == 0)
                return "gtnative group toggles: "
                    + $"weights={B(CombatAiState.GfWeights)} defender={B(CombatAiState.GfDefender)} "
                    + $"quick={B(CombatAiState.GfQuick)} nativemove={B(CombatAiState.GfNativemove)} "
                    + $"updown={B(CombatAiState.GfUpdown)} "
                    + $"nofeint={B(CombatAiState.GfNofeint)} gap={B(CombatAiState.GfGap)} "
                    + $"onme={B(CombatAiState.GfOnMe)} "
                    + $"spice={B(CombatAiState.GfSpice)} defmove={B(CombatAiState.GfDefmove)} "
                    + $"swap={B(CombatAiState.GfSwap)} rig2v1={B(CombatAiState.GfRig2v1)} "
                    + $"assist={B(CombatAiState.GfAssist)} "
                    + "(combatai.gf <name> [on|off])";
            string key = args[0].Trim().ToLowerInvariant();
            bool cur;
            switch (key)
            {
                case "weights": cur = CombatAiState.GfWeights; break;
                case "defender": cur = CombatAiState.GfDefender; break;
                case "quick": cur = CombatAiState.GfQuick; break;
                case "nativemove": cur = CombatAiState.GfNativemove; break;
                case "updown": cur = CombatAiState.GfUpdown; break;
                case "nofeint": cur = CombatAiState.GfNofeint; break;
                case "gap": cur = CombatAiState.GfGap; break;
                case "onme": cur = CombatAiState.GfOnMe; break;
                case "spice": cur = CombatAiState.GfSpice; break;
                case "defmove": cur = CombatAiState.GfDefmove; break;
                case "swap": cur = CombatAiState.GfSwap; break;
                case "rig2v1": cur = CombatAiState.GfRig2v1; break;
                case "assist": cur = CombatAiState.GfAssist; break;
                default: return $"unknown gtnative feature '{args[0]}' ({names})";
            }
            bool enable = args.Count > 1 ? ParseOnOff(args[1], cur) : !cur;
            switch (key)
            {
                case "weights": CombatAiState.GfWeights = enable; break;
                case "defender": CombatAiState.GfDefender = enable; break;
                case "quick": CombatAiState.GfQuick = enable; break;
                case "nativemove": CombatAiState.GfNativemove = enable; break;
                case "updown": CombatAiState.GfUpdown = enable; break;
                case "nofeint": CombatAiState.GfNofeint = enable; break;
                case "gap": CombatAiState.GfGap = enable; break;
                case "onme": CombatAiState.GfOnMe = enable; break;
                case "spice": CombatAiState.GfSpice = enable; break;
                case "defmove": CombatAiState.GfDefmove = enable; break;
                case "swap": CombatAiState.GfSwap = enable; break;
                case "rig2v1": CombatAiState.GfRig2v1 = enable; break;
                case "assist": CombatAiState.GfAssist = enable; break;
            }
            CombatAiLog.Log($"[CONSOLE] combatai.gf {key} -> {(enable ? "ON" : "OFF")}.");
            return $"gtnative group feature '{key}': {(enable ? "ON" : "OFF")}.";
        }

        private static string B(bool v) => v ? "ON" : "OFF";

        [CommandLineFunctionality.CommandLineArgumentFunction("groupsize", "combatai")]
        public static string GroupSize(List<string> args)
        {
            int size = CombatAiState.GroupFightTeamSize;
            if (args.Count > 0 && int.TryParse(args[0], out int v))
                size = Math.Max(2, Math.Min(20, v));
            CombatAiState.GroupFightTeamSize = size;
            CombatAiLog.Log($"[CONSOLE] combatai.groupsize -> {size}v{size}.");
            return $"Group-fight team size (agents per AI team, 2..20; the 5v5 menu entry reads it): {size}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("script", "combatai")]
        public static string Script(List<string> args)
        {
            if (args.Count > 0 && args[0].Trim().ToLowerInvariant() != "status")
            {
                bool enable = ParseOnOff(args[0], CombatAiTune.ScriptOn);
                CombatAiTune.ScriptOn = enable;
                CombatAiLog.Log($"[CONSOLE] combatai.script -> {(enable ? "ON" : "OFF")}.");
                return $"Fight script (momentum director): {(enable ? "ON" : "OFF")}.";
            }
            string line = FightScript.StatusLine() + $" sparBrain={CombatAiState.SparBrain}";
            CombatAiLog.Log("[SCRIPT] " + line);
            return line;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("pmodel", "combatai")]
        public static string PModel(List<string> args)
        {
            return $"PlayerModel: swings={PlayerCombatModel.SwingsObserved} predict={PlayerCombatModel.Predictability:F2} " +
                   $"dir={PlayerCombatModel.PredictedDir}@{PlayerCombatModel.DirConfidence:F2} " +
                   $"hold={PlayerCombatModel.PredictedHold:F2}s@{PlayerCombatModel.TempoConfidence:F2} " +
                   $"intv={PlayerCombatModel.PredictedInterval:F2}s@{PlayerCombatModel.IntervalConfidence:F2} " +
                   $"tth={PlayerCombatModel.TimeToHitEstimate:F2}s feintRate={PlayerCombatModel.FeintRate:F2} " +
                   $"inReady={PlayerCombatModel.PlayerInReady} readyElapsed={PlayerCombatModel.ReadyElapsed:F2}s " +
                   $"expect={PlayerCombatModel.Expectation:F2} lastRead={PlayerCombatModel.LastReleaseRead:F2} " +
                   $"overheld={PlayerCombatModel.ChainOverheld} ltSwings={PlayerCombatModel.LongTermSwings}";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("memory", "combatai")]
        public static string Memory(List<string> args)
        {
            PlayerCombatModel.DumpMemoryToLog();
            return $"Long-term player memory appended to combat_ai.log ({PlayerCombatModel.LongTermSwings} lifetime swings recorded). See [PMEM] lines.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("spar", "combatai")]
        public static string Spar(List<string> args)
        {
            if (args.Count >= 2
                && int.TryParse(args[0], out int defender)
                && int.TryParse(args[1], out int attacker))
            {
                CombatAiState.SparDefenderIndex = defender;
                CombatAiState.SparAttackerIndex = attacker;
                CombatAiState.SparMode = true;
                // Resolve the indices against live agents so a mistyped index is
                // caught NOW instead of the rig silently doing nothing.
                string defDesc = DescribeAgentForSpar(defender);
                string atkDesc = DescribeAgentForSpar(attacker);
                CombatAiLog.Log($"[CONSOLE] combatai.spar -> defender {defDesc}, attacker {atkDesc}.");
                return $"Spar: defender {defDesc}, attacker {atkDesc}.";
            }

            // no/other args -> off
            CombatAiState.SparMode = false;
            CombatAiState.SparDefenderIndex = -1;
            CombatAiState.SparAttackerIndex = -1;
            CombatAiLog.Log("[CONSOLE] combatai.spar -> OFF.");
            return "Spar off. Usage: combatai.spar <defenderIndex> <attackerIndex>";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("looktest", "combatai")]
        public static string LookTest(List<string> args)
        {
            if (args.Count > 0)
            {
                switch (args[0].Trim().ToLowerInvariant())
                {
                    case "frame":   CombatAiState.LookTestVariant = 0; CombatAiState.LookTestMode = true; break;
                    case "angle":   CombatAiState.LookTestVariant = 1; CombatAiState.LookTestMode = true; break;
                    case "poi":     CombatAiState.LookTestVariant = 2; CombatAiState.LookTestMode = true; break;
                    case "movedir": CombatAiState.LookTestVariant = 3; CombatAiState.LookTestMode = true; break;
                    case "target":  CombatAiState.LookTestVariant = 4; CombatAiState.LookTestMode = true; break;
                    case "torso":   CombatAiState.LookTestVariant = 5; CombatAiState.LookTestMode = true; break;
                    default:
                        CombatAiState.LookTestMode = ParseOnOff(args[0], CombatAiState.LookTestMode);
                        break;
                }
            }
            else
                CombatAiState.LookTestMode = !CombatAiState.LookTestMode;

            string state = CombatAiState.LookTestMode ? "ON" : "OFF";
            string variant = CombatAiState.LookTestVariant == 1 ? "angle"
                           : CombatAiState.LookTestVariant == 2 ? "poi"
                           : CombatAiState.LookTestVariant == 3 ? "movedir"
                           : CombatAiState.LookTestVariant == 4 ? "target"
                           : CombatAiState.LookTestVariant == 5 ? "torso" : "frame";
            CombatAiLog.Log($"[CONSOLE] combatai.looktest -> {state} (variant={variant}).");
            return $"Look/facing test ({variant}, +/-15deg, target RELEASED): {state}. Usage: combatai.looktest frame|angle|poi|movedir|target|torso|off";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("movefix", "combatai")]
        public static string MoveFix(List<string> args)
        {
            // Session 48 A/B: gait = Run through the callback eventFlag channel;
            // window = native locomotion during guard/ready windows (duel only).
            string which = args.Count > 0 ? args[0].Trim().ToLowerInvariant() : "all";
            bool? set = args.Count > 1 ? (bool?)ParseOnOff(args[1], true) : null;
            switch (which)
            {
                case "gait":
                    CombatAiState.GaitFix = set ?? !CombatAiState.GaitFix;
                    break;
                case "window":
                    CombatAiState.NativeLocoWindows = set ?? !CombatAiState.NativeLocoWindows;
                    break;
                default:
                    CombatAiState.GaitFix = set ?? !CombatAiState.GaitFix;
                    CombatAiState.NativeLocoWindows = CombatAiState.GaitFix;
                    break;
            }
            string state = $"gait={CombatAiState.GaitFix} window={CombatAiState.NativeLocoWindows}";
            CombatAiLog.Log($"[CONSOLE] combatai.movefix {which} -> {state}.");
            return $"Movement fixes: {state}. (gait = Run via callback channel; window = native locomotion in guard/ready windows, plain duel only)";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("posture", "combatai")]
        public static string Posture(List<string> args)
        {
            return $"Player posture: {PlayerPosture.Value:F0}/{PlayerPosture.Max:F0}";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("status", "combatai")]
        public static string Status(List<string> args)
        {
            return $"CombatAI override_enabled={CombatAiState.OverrideEnabled} attackloop={CombatAiState.ContinuousAttackMode} " +
                   $"perfectblock={CombatAiState.PerfectBlockMode} defense={CombatAiState.DefenseOnly} duel={CombatAiState.DuelMode} group={CombatAiState.GroupFightMode} godmode={CombatAiState.GodMode} dodge={CombatAiState.DodgeEnabled} evade={CombatAiState.EvadeMode} angledodge={CombatAiState.AngleDodgeMode} " +
                   $"posture={PlayerPosture.Value:F0}/{PlayerPosture.Max:F0} " +
                   $"gaitfix={CombatAiState.GaitFix} locowin={CombatAiState.NativeLocoWindows} speedlog={CombatAiState.SpeedLogMode} " +
                   $"sparbrain={CombatAiState.SparBrain} sparrig={CombatAiState.SparRig} sparinit={CombatAiState.SparDuelInitiative} script={CombatAiTune.ScriptOn} favor={FightScript.Favor:F2} " +
                   $"freetarget={CombatAiState.FreeTarget}/armed{NativeTargetGuard.ArmedLevel} " +
                   $"steer={CombatAiState.NativeSteer}/armed{NativeSteerOverride.Armed}. " +
                   $"MS: detected={CombatAiState.MasterStrikesDetected} settings_voided={CombatAiState.MasterStrikesSettingsVoided} " +
                   $"patches={CombatAiState.MasterStrikesPatchesApplied}/3. " +
                   $"Dodge: releases_seen={CombatAiState.DodgeReleaseSeenCount} rolls={CombatAiState.DodgeOpportunityCount} " +
                   $"started={CombatAiState.OurDodgeStartCount} suppressed_own_swing={CombatAiState.DodgeSuppressedMidSwing} foreign_yields={CombatAiState.ForeignDodgeYieldCount}. " +
                   $"Log: {CombatAiLog.LogFilePath}";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("speedlog", "combatai")]
        public static string SpeedLog(List<string> args)
        {
            bool enable = args.Count > 0 ? ParseOnOff(args[0], CombatAiState.SpeedLogMode) : !CombatAiState.SpeedLogMode;
            CombatAiState.SpeedLogMode = enable;
            string state = enable ? "ON" : "OFF";
            CombatAiLog.Log($"[CONSOLE] combatai.speedlog -> {state}.");
            return $"Speed ground-truth log (actual velocity + WalkMode + speedLimit cur/max + handoff state, ~0.4s per driven duel agent): {state}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("clearlog", "combatai")]
        public static string ClearLog(List<string> args)
        {
            try
            {
                string path = CombatAiLog.LogFilePath;
                CombatAiLog.Clear();
                CombatAiOverrideComponent.ResetDiagnosticLogCounters();
                CombatAiLog.Log("[CONSOLE] combatai.clearlog -> log cleared, diagnostic counters reset.");
                return $"Cleared: {path} (detailed/heartbeat log caps reset too).";
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
        }

        // combatai.nativehook [scan|hook|guard|status|off] — managed native-hook
        // infrastructure + detection-first crash guard for releasing the combat
        // target lock (research/12_nulltarget_shim.md, Option H2). Default OFF:
        // nothing hooks until `hook`/`guard`. `scan` only locates + reports
        // addresses to the [NH] log (delegate-extracted binding addrs +
        // signature-scan results). `guard` adds the mode-leaf detour that
        // forces pathObj+0x1c=0 whenever the effective combat target is < 0.
        [CommandLineFunctionality.CommandLineArgumentFunction("nativehook", "combatai")]
        public static string NativeHook(List<string> args)
        {
            string r = NativeTargetGuard.HandleConsole(args);
            CombatAiLog.Log("[CONSOLE] combatai.nativehook " + string.Join(" ", args.ToArray()) + " -> " + r);
            return r;
        }

        // combatai.steer [on|off|scan|status] -- NATIVE STEER (Option A), the
        // plain-duel default: the combat lock stays ON the whole drive while a
        // detour on the native heading-command funnel (rva 0x61ac60 on the
        // installed 1.4.8 dll) swaps the steering-computed desired dir for our
        // policy heading at the aiComp+0x15d8 channel input. FAIL CLOSED: the
        // agent runs the current freetarget/legacy behavior whenever the hook
        // isn't armed. `scan` reports the resolved address only; `off`
        // disposes the hook and clears the channel registry.
        [CommandLineFunctionality.CommandLineArgumentFunction("steer", "combatai")]
        public static string Steer(List<string> args)
        {
            string r = NativeSteerOverride.HandleConsole(args);
            CombatAiLog.Log("[CONSOLE] combatai.steer " + string.Join(" ", args.ToArray()) + " -> " + r);
            return r;
        }

        // combatai.unlock [agent <idx>] — explicit target-lock RELEASE test via
        // the poked path (ReleaseCombatTarget: aiComp+0x19c=0 +0x1304=-1 then
        // SetTargetAgent(null) + auto-select off). Requires the nativehook
        // subsystem armed (hook|guard) — refuse otherwise. Designated agent =
        // `agent <idx>`, else the faceframe agent if set, else the nearest live
        // AI human to the player (same pick the faceframe command makes).
        [CommandLineFunctionality.CommandLineArgumentFunction("unlock", "combatai")]
        public static string Unlock(List<string> args)
        {
            if (!NativeTargetGuard.GuardedRelease)
                return "unlock: arm the native hook first (combatai.nativehook hook|guard) — the poked release path is off.";
            int idx = -1;
            for (int i = 0; i + 1 < args.Count; i++)
                if (args[i].Trim().ToLowerInvariant() == "agent" && int.TryParse(args[i + 1], out int v))
                    idx = v;
            Mission m = Mission.Current;
            if (m == null)
                return "unlock: no mission.";
            Agent target = null;
            if (idx >= 0)
            {
                foreach (Agent a in m.Agents)
                    if (a != null && a.Index == idx) { target = a; break; }
                if (target == null)
                    return $"unlock: agent #{idx} not found in mission.";
            }
            else if (CombatAiState.FaceFrameAgentIndex >= 0)
            {
                foreach (Agent a in m.Agents)
                    if (a != null && a.Index == CombatAiState.FaceFrameAgentIndex) { target = a; break; }
            }
            if (target == null)
                target = FindNearestAiAgentForFaceFrame();
            if (target == null)
                return "unlock: no live AI-controlled human agent found.";
            try
            {
                string desc = DescribeAgentForSpar(target.Index);
                CombatAiLog.Log($"[CONSOLE] combatai.unlock -> ReleaseCombatTarget({desc})");
                bool ok = NativeTargetGuard.ReleaseCombatTarget(target);
                return $"unlock: ReleaseCombatTarget({desc}) -> {(ok ? "OK — see [NH] release-pre/poke lines" : "FAILED — see [NH] log")}.";
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[CONSOLE] combatai.unlock failed: {ex.GetType().Name} {ex.Message}");
                return "unlock: failed — see [NH]/console log.";
            }
        }

        // combatai.freetarget [off|on|attack|status] -- FREE TARGET policy for
        // the default duel and the spar defender: the driven agent NEVER holds
        // a combat target (aiComp+0x1504 = -1); the formation frame is the
        // only position/facing writer (heading = player bearing / attacker
        // bisector; position = tactical / rig-planner goal). Block decisions
        // branch on the threat's bearing vs ACTUAL facing. FAIL CLOSED: only
        // active while NativeTargetGuard is armed (auto-arms on mode start);
        // `off` restores the legacy locked behavior byte-for-byte.
        //   attack = keep the lock only while we hold an attack
        //            (Windup..Recover), release after recovery
        [CommandLineFunctionality.CommandLineArgumentFunction("freetarget", "combatai")]
        public static string FreeTarget(List<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                switch (a)
                {
                    case "off":    CombatAiState.FreeTarget = CombatAiState.FreeTargetModeT.Off; break;
                    case "on":     CombatAiState.FreeTarget = CombatAiState.FreeTargetModeT.On; break;
                    case "attack": CombatAiState.FreeTarget = CombatAiState.FreeTargetModeT.AttackOnly; break;
                    case "status": break;
                    case "face" when i + 1 < args.Count:
                    {
                        string v = args[++i].Trim().ToLowerInvariant();
                        if (v == "frame") CombatAiState.FreeFace = CombatAiState.FreeFaceT.Frame;
                        else if (v == "lookdir") CombatAiState.FreeFace = CombatAiState.FreeFaceT.LookDir;
                        else if (v == "lookangle") CombatAiState.FreeFace = CombatAiState.FreeFaceT.LookAngle;
                        else if (v == "movedir") CombatAiState.FreeFace = CombatAiState.FreeFaceT.MoveDir;
                        else return "Usage: ... face frame|lookdir|lookangle|movedir  (lookangle is dead on this build -- engine ignores the write, ai25)";
                        break;
                    }
                    case "turnrate" when i + 1 < args.Count:
                    {
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float t))
                            CombatAiState.FreeTurnRateDegPerSec = MathF.Max(30f, MathF.Min(720f, t));
                        break;
                    }
                    case "leash" when i + 1 < args.Count:
                    {
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float l))
                            CombatAiState.FreeLeashMeters = MathF.Max(3f, MathF.Min(30f, l));
                        break;
                    }
                    case "smooth" when i + 1 < args.Count:
                    {
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float k))
                            CombatAiState.FreeSmoothK = MathF.Max(2f, MathF.Min(40f, k));
                        break;
                    }
                    case "accel" when i + 1 < args.Count:
                    {
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float ac))
                            CombatAiState.FreeTurnAccelDegPerSec2 = MathF.Max(100f, MathF.Min(3000f, ac));
                        break;
                    }
                    case "bodyfeint":
                    {
                        CombatAiState.FreeBodyFeint = i + 1 < args.Count
                            ? ParseOnOff(args[++i], CombatAiState.FreeBodyFeint)
                            : !CombatAiState.FreeBodyFeint;
                        break;
                    }
                    default:
                        return "Usage: combatai.freetarget [off|on|attack|status] [face frame|lookdir|lookangle|movedir] [turnrate <deg/s>] [leash <m>] [bodyfeint on|off]  " +
                               "(on = never lock, attack = lock only during our swings; " +
                               "duel + spar defender only; needs the native guard, auto-armed)";
                }
            }
            string line = $"freetarget={CombatAiState.FreeTarget} face={CombatAiState.FreeFace} " +
                $"turnrate={CombatAiState.FreeTurnRateDegPerSec:F0} smooth={CombatAiState.FreeSmoothK:F0} " +
                $"accel={CombatAiState.FreeTurnAccelDegPerSec2:F0} leash={CombatAiState.FreeLeashMeters:F1} " +
                $"bodyfeint={CombatAiState.FreeBodyFeint} guardArmed={NativeTargetGuard.ArmedLevel} " +
                (CombatAiState.FreeTarget == CombatAiState.FreeTargetModeT.Off
                    ? "(legacy locked behavior)."
                    : NativeTargetGuard.ArmedLevel >= 2
                        ? "(active: target=-1 + formation frame)."
                        : "(armed check FAILED -> runs LEGACY locked until the guard arms)");
            CombatAiLog.Log("[CONSOLE] combatai.freetarget " + line);
            return line;
        }

        private static string DescribeAgentForSpar(int index)
        {
            Mission m = Mission.Current;
            if (m == null)
                return $"#{index} (no mission)";
            foreach (Agent a in m.Agents)
            {
                if (a != null && a.Index == index)
                {
                    string side = a.IsMainAgent ? "PLAYER" : (a.IsAIControlled ? "AI" : "not-ai");
                    string human = a.IsHuman ? "human" : "NON-HUMAN";
                    return $"#{index} {a.Name} ({side}, {human}, team={a.Team?.Side.ToString() ?? "?"}, active={a.IsActive()})";
                }
            }
            return $"#{index} NOT FOUND in mission ({m.Agents.Count} agents)";
        }

        private static bool ParseOnOff(string arg, bool current)
        {
            switch (arg.Trim().ToLowerInvariant())
            {
                case "on": case "enable": case "true": case "1": return true;
                case "off": case "disable": case "false": case "0": return false;
                default: return !current;
            }
        }

        // combatai.lookonly [on|off] [orbit|strafe|mixed] -- LOOK-ONLY duel
        // (Session 71): approaches then keeps repositioning around the player
        // (no attacks; DefenseOnly mask) while the free-target MoveDir writer
        // keeps the player in its 90deg view. Saves/restores the duel/
        // defense/freetarget/face state around the mode. Metric: [LOOKONLY]
        // 1.0s lines in combat_ai.log (inView90, errBody/errLook, intent,
        // dist, vel, nativeTgt -- must read -1).
        private static bool _loSavedDuel, _loSavedDefense;
        private static CombatAiState.FreeTargetModeT _loSavedFt;
        private static CombatAiState.FreeFaceT _loSavedFace;
        private static bool _loHaveSaved;
        [CommandLineFunctionality.CommandLineArgumentFunction("lookonly", "combatai")]
        public static string LookOnly(List<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                if (a == "orbit") CombatAiState.LookOnlyStyle = CombatAiState.LookOnlyStyleT.Orbit;
                else if (a == "strafe") CombatAiState.LookOnlyStyle = CombatAiState.LookOnlyStyleT.Strafe;
                else if (a == "mixed") CombatAiState.LookOnlyStyle = CombatAiState.LookOnlyStyleT.Mixed;
                else if (a == "on" || a == "off")
                {
                    bool enable = a == "on";
                    if (enable && !CombatAiState.LookOnlyMode)
                    {
                        _loSavedDuel = CombatAiState.DuelMode;
                        _loSavedDefense = CombatAiState.DefenseOnly;
                        _loSavedFt = CombatAiState.FreeTarget;
                        _loSavedFace = CombatAiState.FreeFace;
                        _loHaveSaved = true;
                        CombatAiState.DuelMode = true;
                        CombatAiState.DefenseOnly = true;   // FSM pinned to Approach, attacks masked
                        CombatAiState.FreeTarget = CombatAiState.FreeTargetModeT.AttackOnly;
                        CombatAiState.FreeFace = CombatAiState.FreeFaceT.MoveDir;
                        CombatAiState.LookOnlyMode = true;
                    }
                    else if (!enable && CombatAiState.LookOnlyMode)
                    {
                        CombatAiState.LookOnlyMode = false;
                        if (_loHaveSaved)
                        {
                            _loHaveSaved = false;
                            CombatAiState.DuelMode = _loSavedDuel;
                            CombatAiState.DefenseOnly = _loSavedDefense;
                            CombatAiState.FreeTarget = _loSavedFt;
                            CombatAiState.FreeFace = _loSavedFace;
                        }
                    }
                }
                else
                    return "Usage: combatai.lookonly [on|off] [orbit|strafe|mixed]  " +
                           "(approach + orbit/strafe/back/close/pause intents, no attacks; " +
                           "metric: [LOOKONLY] lines in combat_ai.log)";
            }
            string line = $"lookonly={(CombatAiState.LookOnlyMode ? "on" : "off")} style={CombatAiState.LookOnlyStyle} " +
                $"last: inView90={CombatAiState.LookOnlyInView90:F0}% meanErr={CombatAiState.LookOnlyMeanErr:F0} maxErr={CombatAiState.LookOnlyMaxErr:F0} " +
                $"(duel={CombatAiState.DuelMode} defenseOnly={CombatAiState.DefenseOnly} free={CombatAiState.FreeTarget}/{CombatAiState.FreeFace}).";
            CombatAiLog.Log("[CONSOLE] combatai.lookonly " + line);
            return line;
        }

        // combatai.sparfree [on|off|status] -- spar DEFENDER free-target path
        // (default ON): no combat lock ever, median heading between the two
        // attackers via the MoveDir writer, rig planner locomotion kept.
        // off = the pre-free legacy locked defender.
        [CommandLineFunctionality.CommandLineArgumentFunction("sparfree", "combatai")]
        public static string SparFree(List<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                switch (a)
                {
                    case "on":  CombatAiState.SparFree = true; break;
                    case "off": CombatAiState.SparFree = false; break;
                    case "status": break;
                    case "frontal" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float fd))
                            CombatAiState.FreeFrontalDeg = MathF.Max(5f, MathF.Min(45f, fd));
                        break;
                    case "view" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float vd))
                            CombatAiState.SparAttackViewDeg = MathF.Max(30f, MathF.Min(90f, vd));
                        break;
                    case "spread" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float sd))
                            CombatAiState.SparSpreadDeg = MathF.Max(40f, MathF.Min(110f, sd));
                        break;
                    case "attackview" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float av))
                            CombatAiState.SparDefAttackViewDeg = MathF.Max(10f, MathF.Min(90f, av));
                        break;
                    default:
                        return "Usage: combatai.sparfree [on|off|status] [frontal <deg 5..45>] [view <deg 30..90>] [spread <deg 40..110>]";
                }
            }
            string line = $"sparfree={CombatAiState.SparFree} frontal={CombatAiState.FreeFrontalDeg:F0} " +
                $"view={CombatAiState.SparAttackViewDeg:F0} spread={CombatAiState.SparSpreadDeg:F0} " +
                $"attackview={CombatAiState.SparDefAttackViewDeg:F0} " +
                $"(sparMode={CombatAiState.SparMode} def={CombatAiState.SparDefenderIndex} face={CombatAiState.FreeFace}).";
            CombatAiLog.Log("[CONSOLE] combatai.sparfree " + line);
            return line;
        }

        // combatai.groupfree [on|off|status] [gap <m>] [spacing <m>] [wait <s>]
        // [speed <m/s>] -- GROUP LINEUP stage 1: two AI lines face off
        // (equal spacing, no attacks), wait, then the legacy fight. Default ON.
        [CommandLineFunctionality.CommandLineArgumentFunction("groupfree", "combatai")]
        public static string GroupFree(List<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                switch (a)
                {
                    case "on":  CombatAiState.GroupFree = true; break;
                    case "off": CombatAiState.GroupFree = false; GroupLineup.Reset(); GroupEngagement.Reset(); GroupTargeting.Reset(); break;
                    case "status": break;
                    case "gap" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float g))
                            CombatAiState.GroupGapMeters = MathF.Max(0.8f, MathF.Min(4f, g));
                        break;
                    case "spacing" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float s))
                            CombatAiState.GroupSpacingMeters = MathF.Max(0.6f, MathF.Min(3f, s));
                        break;
                    case "wait" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float w))
                            CombatAiState.GroupWaitSeconds = MathF.Max(0f, MathF.Min(60f, w));
                        break;
                    case "speed" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float v))
                            CombatAiState.GroupSpeedMeters = MathF.Max(0.8f, MathF.Min(3.5f, v));
                        break;
                    case "fightgap" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float fg))
                            CombatAiState.GroupFightGapMeters = MathF.Max(0.8f, MathF.Min(4f, fg));
                        break;
                    case "leash" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float lh))
                            CombatAiState.GroupLeashMeters = MathF.Max(0.3f, MathF.Min(4f, lh));
                        break;
                    case "threat" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float tr))
                            CombatAiState.GroupThreatRangeMeters = MathF.Max(1.5f, MathF.Min(8f, tr));
                        break;
                    case "maxthreats" when i + 1 < args.Count:
                        if (int.TryParse(args[++i], out int mt))
                            CombatAiState.GroupMaxThreats = Math.Max(1, Math.Min(4, mt));
                        break;
                    case "engagers" when i + 1 < args.Count:
                        if (int.TryParse(args[++i], out int eg))
                            CombatAiState.GroupMaxEngagers = Math.Max(1, Math.Min(4, eg));
                        break;
                    case "busy" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float bu))
                            CombatAiState.GroupBusyIgnoreSeconds = MathF.Max(0f, MathF.Min(8f, bu));
                        break;
                    case "view" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float vw))
                            CombatAiState.GroupAttackViewDeg = MathF.Max(30f, MathF.Min(90f, vw));
                        break;
                    case "aim" when i + 1 < args.Count:
                        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float am))
                            CombatAiState.GroupAttackAimDeg = MathF.Max(10f, MathF.Min(90f, am));
                        break;
                    default:
                        return "Usage: combatai.groupfree [on|off|status] [gap <m>] [spacing <m>] [wait <s>] [speed <m/s>]";
                }
            }
            string line = $"groupfree={CombatAiState.GroupFree} gap={CombatAiState.GroupGapMeters:F1} " +
                $"spacing={CombatAiState.GroupSpacingMeters:F1} wait={CombatAiState.GroupWaitSeconds:F0} " +
                $"speed={CombatAiState.GroupSpeedMeters:F1} fightgap={CombatAiState.GroupFightGapMeters:F1} leash={CombatAiState.GroupLeashMeters:F1} phase={GroupLineup.Phase} (group={CombatAiState.GroupFightMode}).";
            CombatAiLog.Log("[CONSOLE] combatai.groupfree " + line);
            return line;
        }

        // combatai.holdspin [on|off] [rate <deg/s>] -- hold AttackUp while the
        // free MoveDir writer rotates the body on its axis, target=-1.
        // combatai.lockthrash [on|off] [on <s>] [off <s>] -- hold AttackUp
        // while the combat lock is written/released on a timed cycle.
        private static bool _thSavedDuel, _thSavedDefense, _thHaveSaved;
        private static CombatAiState.FreeTargetModeT _thSavedFt;
        private static CombatAiState.FreeFaceT _thSavedFace;
        private static void TestHoldEnable(bool spin)
        {
            if (!_thHaveSaved)
            {
                _thSavedDuel = CombatAiState.DuelMode;
                _thSavedDefense = CombatAiState.DefenseOnly;
                _thSavedFt = CombatAiState.FreeTarget;
                _thSavedFace = CombatAiState.FreeFace;
                _thHaveSaved = true;
            }
            CombatAiState.DuelMode = true;
            CombatAiState.DefenseOnly = true;
            CombatAiState.FreeTarget = CombatAiState.FreeTargetModeT.AttackOnly;
            CombatAiState.FreeFace = CombatAiState.FreeFaceT.MoveDir;
            CombatAiState.HoldSpinMode = spin;
            CombatAiState.LockThrashMode = !spin;
            NativeTargetGuard.Quiet = !spin;   // thrash would spam NH ~16x/s
        }
        private static void TestHoldDisable()
        {
            CombatAiState.HoldSpinMode = false;
            CombatAiState.LockThrashMode = false;
            CombatAiState.HoldSpinNoLock = false;
            NativeTargetGuard.Quiet = false;
            if (_thHaveSaved)
            {
                _thHaveSaved = false;
                CombatAiState.DuelMode = _thSavedDuel;
                CombatAiState.DefenseOnly = _thSavedDefense;
                CombatAiState.FreeTarget = _thSavedFt;
                CombatAiState.FreeFace = _thSavedFace;
            }
        }
        [CommandLineFunctionality.CommandLineArgumentFunction("holdspin", "combatai")]
        public static string HoldSpin(List<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                if (a == "on") TestHoldEnable(true);
                else if (a == "off") TestHoldDisable();
                else if (a == "nolock") CombatAiState.HoldSpinNoLock = true;
                else if (a == "rate" && i + 1 < args.Count
                    && float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r))
                    CombatAiState.HoldSpinRateDeg = MathF.Max(5f, MathF.Min(360f, r));
                else
                    return "Usage: combatai.holdspin [on|off] [rate <deg/s>]";
            }
            string line = $"holdspin={(CombatAiState.HoldSpinMode ? "on" : "off")} rate={CombatAiState.HoldSpinRateDeg:F0} nolock={CombatAiState.HoldSpinNoLock}";
            CombatAiLog.Log("[CONSOLE] combatai.holdspin " + line);
            return line;
        }
        [CommandLineFunctionality.CommandLineArgumentFunction("lockthrash", "combatai")]
        public static string LockThrash(List<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                if (a == "on") TestHoldEnable(false);
                else if (a == "off") TestHoldDisable();
                else if (a == "onsec" && i + 1 < args.Count
                    && float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float o))
                    CombatAiState.ThrashOnSeconds = MathF.Max(0.02f, MathF.Min(3f, o));
                else if (a == "offsec" && i + 1 < args.Count
                    && float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                    CombatAiState.ThrashOffSeconds = MathF.Max(0.02f, MathF.Min(3f, f));
                else
                    return "Usage: combatai.lockthrash [on|off] [onsec <s>] [offsec <s>]";
            }
            string line = $"lockthrash={(CombatAiState.LockThrashMode ? "on" : "off")} " +
                $"on={CombatAiState.ThrashOnSeconds:F2}s off={CombatAiState.ThrashOffSeconds:F2}s";
            CombatAiLog.Log("[CONSOLE] combatai.lockthrash " + line);
            return line;
        }
        [CommandLineFunctionality.CommandLineArgumentFunction("openlog", "combatai")]
        public static string OpenLog(List<string> args)
        {
            try
            {
                string path = CombatAiLog.LogFilePath;
                if (!File.Exists(path))
                    return $"Log file does not exist yet: {path}";
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\""));
                return "Opened: " + path;
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("tail", "combatai")]
        public static string Tail(List<string> args)
        {
            try
            {
                string path = CombatAiLog.LogFilePath;
                if (!File.Exists(path))
                    return $"Log file does not exist yet: {path}";

                int lineCount = args.Count > 0 && int.TryParse(args[0], out int n) ? n : 30;
                string[] lines = File.ReadAllLines(path);
                int start = Math.Max(0, lines.Length - lineCount);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"=== Last {Math.Min(lineCount, lines.Length)} lines of {path} ===");
                for (int i = start; i < lines.Length; i++)
                    sb.AppendLine(lines[i]);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
        }
    }
}
