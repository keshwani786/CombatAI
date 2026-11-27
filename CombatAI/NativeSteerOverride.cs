using System;
using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyHook;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =========================================================================
    // NativeSteerOverride — "Option A" native steering override for plain duel
    // and the spar defender (bisector heading between the two attackers).
    //
    // Instead of driving body heading via Agent.SetMovementDirection (the
    // free-target MoveDir writer), duel agents keep the native combat lock ON
    // at all times and we override what the engine's own heading channel turns
    // toward: the detour sits on the heading-command leaf — the funnel every
    // AI steering heading write goes through — and swaps the desired dir for
    // ours. The engine still rate-limits and integrates the turn, so the lock
    // keeps aim/relock internals while our managed policy chooses facing.
    //
    // VERIFIED on the deployed 1.4.8 TaleWorlds.Native.dll (image 0x180000000,
    // .text rva 0x1000) — see DEV NOTES at the bottom:
    //   headCmd RVA 0x61ac60 = 1.4.5 FUN_180617d10 analog:
    //       void f(uint* channelObj /*rcx*/, uint* dirVec3 /*rdx*/)
    //       angle=atan2f(-dir.x,dir.y); folds residual into [obj+0x14]; writes
    //       [obj+0x10]=angle, [obj+0..8)=sincos dir (x negated), [obj+8]=dir.z,
    //       [obj+0x18]=zDelta; tail-jmps the dirty-flag helper.
    //   AI heading channel object = aiComp+0x15d8 (NOT +0x1578 — corrected):
    //       aiTick non-decision:  lea rdx,[r15+0x138c]; lea rcx,[r15+0x15d8]; call
    //       aiTick stack-order:   lea rdx,[r15+0x138c]; lea rcx,[rsp+0x60]; call
    //       decision dispatch tail: movups [rsp+0x30],[rdi+0x138c];
    //                          lea rcx,[rdi+0x15d8]; lea rdx,[rsp+0x30]; call
    //   => THREE call sites. rcx==aiComp+0x15d8 on two; rdx==aiComp+0x138c on
    //   two (the dispatcher tail's rdx is a stack copy). Membership test in the
    //   detour covers BOTH keys so all three sites are intercepted.
    //   Channel layout (0x3c bytes, bulk-copied aiComp+0x15d8 -> agent+0x474 by
    //   the apply pass @ rva 0x5f4770): {desiredDir v2 @0, desiredZ @8,
    //   currentAngle @0x10, residualAngle @0x14, zDelta @0x18, progress @0x1c/
    //   0x20, flags/slot @0x28..0x38}. The rate-limited integrator (0x61aac0 =
    //   FUN_180617b70 analog, called on r15+0x15d8 @0x6b97c4 and on agent+0x474
    //   in the per-agent tick) turns the body toward it.
    //   Steering ctx output dir = aiComp+0x138c (the value our detour replaces).
    //
    // DETOUR SEMANTICS: allocation-free — reads a fixed registry of steered
    // channel/context pointers; on a match it calls the original with a
    // stack-built {x,y,z} where x,y are OUR registered policy dir and z is the
    // caller's own desiredZ passthrough. Non-members pass through untouched.
    // FAIL CLOSED everywhere: no verified address -> no hook -> duel falls back
    // to the existing free-target/locked paths.
    //
    // CONSOLE:  combatai.steer [on|off|scan|status]
    //   on     - CombatAiState.NativeSteer=true + arm the hook now (default ON)
    //   off    - NativeSteer=false, registry cleared, hook disposed
    //   scan   - locate + report the headCmd address only (hook nothing)
    //   status - armed state, resolved rva, registered channels, detour hits
    // =========================================================================
    public static class NativeSteerOverride
    {
        // ---- verified 1.4.8 offsets ----
        private const int AgentOffAiComp   = 0x888;    // agent -> aiComp (null for non-AI)
        private const int AiCompOffChannel = 0x15d8;   // AI movement-order/heading channel
        private const int AiCompOffCtxDir  = 0x138c;   // combat-steering desired-dir output
        private const int MaxReg = 16;

        public static bool Armed { get; private set; }
        private static bool _scanned;
        private static IntPtr _addrHeadCmd;
        private static bool _sigFallback;            // resolved via caller-chain, not the body sig

        // hook + original-call delegate (kept alive for the session).
        // IList of LocalHook keeps the EasyHook type out of member signatures
        // so the class loads even if EasyHook.dll can't resolve — same pattern
        // as NativeTargetGuard.
        private static IList _hooks;
        private static NativeHeadCmdDelegate _callHeadCmd;
        private static NativeHeadCmdDelegate _detourHeadCmd;

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, SetLastError = true)]
        private delegate void NativeHeadCmdDelegate(UIntPtr channelObj, UIntPtr dirPtr);

        // ---- steered-channel registry ------------------------------------
        // Fixed-size struct array scanned lock-free inside the detour (the AI
        // tick may run on parallel workers). Register/Unregister run on the
        // main thread only; a torn read in the detour can at worst steer one
        // frame with a half-updated dir — bounded, harmless.
        private struct SteerReg
        {
            public UIntPtr Chan;      // aiComp+0x15d8 — membership key A
            public UIntPtr CtxDir;    // aiComp+0x138c — membership key B
            public int AgentIndex;    // diagnostics only
            public ulong PackedDir;   // {fY<<32|fX} — single 8-byte atomic slot
            public int Enabled;       // 0/1 — the detour's only gate
            public int HitLogged;     // first-hit [NSTEER] latch
        }
        private static readonly SteerReg[] _reg = new SteerReg[MaxReg];

        // ---- bounded [NSTEER] logging ----
        private static int _nsLines;
        private const int MaxNsLines = 160;
        private static long _hits, _misses;
        private static int _firstHits;

        private static void NsLog(string msg) { if (!NativeTargetGuard.Quiet) CombatAiLog.Log(msg); }

        private static ulong PackDir(float x, float y)
        {
            uint ux, uy;
            unsafe { ux = *(uint*)&x; uy = *(uint*)&y; }
            return ((ulong)uy << 32) | ux;
        }

        // ------------------------------------------------------------------
        // Resolve an agent's AI heading-channel + steering-ctx dir pointers.
        // aiComp is read straight off the agent record — same unsafe-read
        // pattern as NativeTargetGuard's DumpFields.
        // ------------------------------------------------------------------
        public static unsafe bool ResolveChannel(Agent a, out UIntPtr chan, out UIntPtr ctxDir)
        {
            chan = ctxDir = UIntPtr.Zero;
            try
            {
                if (a == null || a.Mission == null || !a.IsActive())
                    return false;
                UIntPtr agentPtr = NativeTargetGuard.GetAgentPtr(a);
                if (agentPtr == UIntPtr.Zero)
                    return false;
                byte* aiComp = *(byte**)(agentPtr + AgentOffAiComp).ToPointer();
                if (aiComp == null)
                    return false;
                chan = (UIntPtr)(aiComp + AiCompOffChannel);
                ctxDir = (UIntPtr)(aiComp + AiCompOffCtxDir);
                return true;
            }
            catch { return false; }
        }

        // Register/refresh an agent's channel with the desired dir for this
        // tick. Main thread only (DrivePerFrameCore).
        public static void Register(UIntPtr chan, UIntPtr ctxDir, int agentIndex, float dirX, float dirY)
        {
            if (chan == UIntPtr.Zero)
                return;
            try
            {
                int slot = -1;
                for (int i = 0; i < MaxReg; i++)
                {
                    if (_reg[i].Chan == chan) { slot = i; break; }
                    if (_reg[i].Enabled == 0 && slot < 0) slot = i;
                }
                if (slot < 0)
                {
                    if (_nsLines < MaxNsLines)
                    {
                        _nsLines++;
                        NsLog($"[NSTEER] registry FULL ({MaxReg}) — agent#{agentIndex} not steered.");
                    }
                    return;
                }
                bool fresh = _reg[slot].Chan != chan;
                _reg[slot].CtxDir = ctxDir;
                _reg[slot].AgentIndex = agentIndex;
                _reg[slot].PackedDir = PackDir(dirX, dirY);
                _reg[slot].Chan = chan;           // write the match key last
                _reg[slot].Enabled = 1;
                if (fresh && _reg[slot].HitLogged == 0)
                {
                    _reg[slot].HitLogged = 2;     // armed latch; detour uses 1 for its own first-hit
                    NsLog($"[NSTEER] registered agent#{agentIndex} chan=0x{chan.ToUInt64():x} ctx=0x{ctxDir.ToUInt64():x}");
                }
            }
            catch { }
        }

        // Drop an agent's membership — native steering resumes the same tick.
        public static void Unregister(UIntPtr chan)
        {
            if (chan == UIntPtr.Zero)
                return;
            try
            {
                for (int i = 0; i < MaxReg; i++)
                {
                    if (_reg[i].Chan == chan && _reg[i].Enabled != 0)
                    {
                        _reg[i].Enabled = 0;
                        if (_nsLines < MaxNsLines)
                        {
                            _nsLines++;
                            NsLog($"[NSTEER] unregistered agent#{_reg[i].AgentIndex} chan=0x{chan.ToUInt64():x} hits={_hits}");
                        }
                        return;
                    }
                }
            }
            catch { }
        }

        // Mission end / full stop: nothing may steer a freed aiComp slot.
        public static void ClearRegistry()
        {
            try
            {
                for (int i = 0; i < MaxReg; i++)
                {
                    _reg[i].Enabled = 0;
                    _reg[i].Chan = UIntPtr.Zero;
                    _reg[i].CtxDir = UIntPtr.Zero;
                    _reg[i].HitLogged = 0;
                }
            }
            catch { }
        }

        public static int RegisteredCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < MaxReg; i++)
                    if (_reg[i].Enabled != 0) n++;
                return n;
            }
        }

        // ------------------------------------------------------------------
        // Scan: resolve the 1.4.8 heading-command leaf.
        //   Primary: direct body signature over .text (verified unique on the
        //            installed 1.4.8 dll — one hit at rva 0x61ac60).
        //   Fallback: caller chain — find `mov dword [r?+0x1808],5` sites (the
        //            dispatcher's "no combat -> movement state 5" write), get
        //            the containing function via .pdata, take its LAST call
        //            rel32, verify the callee head looks like the leaf.
        // ------------------------------------------------------------------
        // mov [rsp+8],rbx ; push rdi ; sub rsp,0x20 ; movss xmm0,[rdx] ;
        // mov rdi,rdx ; xorps xmm0,[rip+X] ; mov rbx,rcx ; movss xmm1,[rdx+4] ;
        // call atan2f ; movss xmm1,[rbx+0x10] ; subss xmm1,xmm0 ;
        // addss xmm1,[rbx+0x14] ; comiss xmm1,[rip+X] ; movss [rbx+0x14],xmm1
        private const string HeadCmdSig =
            "48 89 5c 24 08 57 48 83 ec 20 f3 0f 10 02 48 8b fa 0f 57 05 ?? ?? ?? ?? " +
            "48 8b d9 f3 0f 10 4a 04 e8 ?? ?? ?? ?? f3 0f 10 4b 10 f3 0f 5c c8 " +
            "f3 0f 58 4b 14 0f 2f 0d ?? ?? ?? ?? f3 0f 11 4b 14";

        private static string Scan()
        {
            if (!NativeTargetGuard.EnsureInitialized())
                return "steer: native init failed — see [NH] log.";
            try
            {
                _scanned = true;
                _sigFallback = false;
                NativeTargetGuard.EnsureTextBuffer();
                byte[] buf = NativeTargetGuard.TextBuffer;
                if (buf == null || buf.Length < 0x100)
                {
                    NsLog("[NSTEER] ERROR: .text buffer unavailable.");
                    return StatusLine();
                }
                _addrHeadCmd = NativeTargetGuard.ScanForFirstResult(buf, HeadCmdSig);
                if (_addrHeadCmd != IntPtr.Zero)
                {
                    bool tail = ConfirmHeadCmdTail(_addrHeadCmd);
                    NsLog($"[NSTEER] headCmd signature hit @0x{_addrHeadCmd.ToInt64():x} " +
                          $"(rva 0x{(_addrHeadCmd.ToInt64() - NativeTargetGuard.NativeBase.ToInt64()):x}) tail-confirm={(tail ? "OK" : "FAILED")}");
                    if (!tail)
                        _addrHeadCmd = IntPtr.Zero;
                }
                if (_addrHeadCmd == IntPtr.Zero)
                {
                    _addrHeadCmd = FindHeadCmdViaDispatcher(buf);
                    if (_addrHeadCmd != IntPtr.Zero)
                    {
                        _sigFallback = true;
                        NsLog($"[NSTEER] headCmd resolved via dispatcher caller-chain @0x{_addrHeadCmd.ToInt64():x} " +
                              $"(rva 0x{(_addrHeadCmd.ToInt64() - NativeTargetGuard.NativeBase.ToInt64()):x})");
                    }
                }
                if (_addrHeadCmd == IntPtr.Zero)
                    NsLog("[NSTEER] headCmd NOT FOUND — steer override unavailable (fail closed).");
                return StatusLine();
            }
            catch (Exception ex)
            {
                NsLog("[NSTEER] ERROR in Scan: " + ex);
                return "steer: scan failed — see [NSTEER] log.";
            }
        }

        // Tail check after a head-signature hit: within the next ~0x50 bytes
        // expect the z-delta block `movss xmm0,[rbx+8]` / `subss xmm0,[rdi+8]` /
        // `addss xmm0,[rbx+0x18]` / `movss [rbx+0x18],xmm0`, the `mov eax,[rdi+8]`
        // / `mov [rbx+8],eax` desiredZ store, and an `e9` tail jmp.
        private static unsafe bool ConfirmHeadCmdTail(IntPtr addr)
        {
            byte* p = (byte*)addr;
            bool hasZRead = false, hasZDelta = false, hasZStore = false, hasJmp = false;
            for (int i = 0x3d; i < 0x80; i++)
            {
                if (p[i] == 0xF3 && p[i + 1] == 0x0F && p[i + 2] == 0x10 && p[i + 3] == 0x43 && p[i + 4] == 0x08) hasZRead = true;
                if (p[i] == 0xF3 && p[i + 1] == 0x0F && p[i + 2] == 0x58 && p[i + 3] == 0x43 && p[i + 4] == 0x18) hasZDelta = true;
                if (p[i] == 0x8B && p[i + 1] == 0x47 && p[i + 2] == 0x08) hasZStore = true;
                if (p[i] == 0xE9) hasJmp = true;
            }
            if (!(hasZRead && hasZDelta && hasZStore && hasJmp))
                NsLog($"[NSTEER] tail check: zRead={hasZRead} zDelta={hasZDelta} zStore={hasZStore} jmp={hasJmp}");
            return hasZRead && hasZDelta && hasZStore && hasJmp;
        }

        // Head-shape verification for a caller-chain candidate: the leaf starts
        // `48 89 5c 24 08 57 48 83 ec 20` and contains `movss xmm0,[rdx]`,
        // `xorps xmm0,[rip]`, `movss xmm1,[rdx+4]`, then a `call rel32`.
        private static unsafe bool LooksLikeHeadCmd(IntPtr addr)
        {
            byte* p = (byte*)addr;
            if (!(p[0] == 0x48 && p[1] == 0x89 && p[2] == 0x5C && p[3] == 0x24 && p[4] == 0x08
                  && p[5] == 0x57 && p[6] == 0x48 && p[7] == 0x83 && p[8] == 0xEC && p[9] == 0x20))
                return false;
            bool hasDirX = false, hasXor = false, hasDirY = false, hasCall = false;
            for (int i = 0x0a; i < 0x40; i++)
            {
                if (p[i] == 0xF3 && p[i + 1] == 0x0F && p[i + 2] == 0x10 && p[i + 3] == 0x02) hasDirX = true;
                if (p[i] == 0x0F && p[i + 1] == 0x57 && p[i + 2] == 0x05) hasXor = true;
                if (p[i] == 0xF3 && p[i + 1] == 0x0F && p[i + 2] == 0x10 && p[i + 3] == 0x4A && p[i + 4] == 0x04) hasDirY = true;
                if (p[i] == 0xE8) hasCall = true;
            }
            return hasDirX && hasXor && hasDirY && hasCall;
        }

        // Caller-chain fallback: the decision dispatcher is the function whose
        // "no combat" branch writes `mov dword [r?+0x1808],5`; its LAST call is
        // the heading-command write into aiComp+0x15d8.
        private static IntPtr FindHeadCmdViaDispatcher(byte[] buf)
        {
            int[] sig = ParseSignatureString("?? ?? c7 ?? 08 18 00 00 05 00 00 00");
            int textRva = NativeTargetGuard.TextRva;
            var seen = new System.Collections.Generic.HashSet<long>();
            for (int i = 4; i + sig.Length <= buf.Length; i++)
            {
                if (!MatchAt(buf, i, sig))
                    continue;
                int siteRva = textRva + i;
                if (!NativeTargetGuard.PdataFindFunc(siteRva, out int fb, out int fe))
                    continue;
                if (!seen.Add(fb))
                    continue;
                NsLog($"[NSTEER] +0x1808=5 site rva=0x{siteRva:x} -> func [0x{fb:x},0x{fe:x})");
                // small functions only — the dispatcher is ~0x2d3; the aiTick
                // embeds the same write inside a ~0x9da body whose last call is
                // NOT the leaf. Take the last in-text call target that passes
                // the leaf head check.
                IntPtr last = IntPtr.Zero;
                for (int j = fb - textRva; j <= fe - textRva - 5 && j >= 0 && j + 5 <= buf.Length; j++)
                {
                    if (buf[j] != 0xE8)
                        continue;
                    int rel = BitConverter.ToInt32(buf, j + 1);
                    long tgtRva = textRva + j + 5 + (long)rel;
                    if (!NativeTargetGuard.InText(tgtRva))
                        continue;
                    if (LooksLikeHeadCmd(NativeTargetGuard.NativeBase + (int)tgtRva))
                        last = NativeTargetGuard.NativeBase + (int)tgtRva;
                }
                if (last != IntPtr.Zero)
                    return last;
            }
            return IntPtr.Zero;
        }

        private static int[] ParseSignatureString(string signature)
        {
            string[] parts = signature.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var sig = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                sig[i] = int.TryParse(parts[i], NumberStyles.HexNumber, null, out int v) ? v : -1;
            return sig;
        }

        private static bool MatchAt(byte[] buffer, int i, int[] sig)
        {
            for (int j = 0; j < sig.Length; j++)
                if (sig[j] != -1 && sig[j] != buffer[i + j]) return false;
            return true;
        }

        // ------------------------------------------------------------------
        // Arm/disarm. NoInlining isolates the EasyHook/JIT surface so a missing
        // assembly can only fault this frame — caught by the caller.
        // ------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Arm()
        {
            try
            {
                if (!_scanned)
                {
                    string r = Scan();
                    if (!_scanned) return r;
                }
                if (_addrHeadCmd == IntPtr.Zero)
                    return "steer: headCmd unresolved — cannot arm (fail closed). " + StatusLine();
                if (_callHeadCmd != null)
                    return "steer: already armed. " + StatusLine();
                _hooks = _hooks ?? new System.Collections.Generic.List<LocalHook>();
                try
                {
                    _detourHeadCmd = new NativeHeadCmdDelegate(DetourHeadCmd);
                    // Recipe (NativeTargetGuard): capture the ORIGINAL-call
                    // delegate BEFORE LocalHook.Create patches the prologue.
                    _callHeadCmd = (NativeHeadCmdDelegate)Marshal.GetDelegateForFunctionPointer(
                        _addrHeadCmd, typeof(NativeHeadCmdDelegate));
                    LocalHook h = LocalHook.Create(_addrHeadCmd, _detourHeadCmd, null);
                    h.ThreadACL.SetExclusiveACL(new int[0]);
                    _hooks.Add(h);
                    Armed = true;
                    NsLog($"[NSTEER] armed: heading-command detour @0x{_addrHeadCmd.ToInt64():x} " +
                          $"(rva 0x{(_addrHeadCmd.ToInt64() - NativeTargetGuard.NativeBase.ToInt64()):x}{(_sigFallback ? ", caller-chain" : "")})");
                }
                catch (Exception ex)
                {
                    _callHeadCmd = null;
                    _detourHeadCmd = null;
                    NsLog($"[NSTEER] hook create failed @0x{_addrHeadCmd.ToInt64():x}: {ex}");
                    return "steer: hook create FAILED — see [NSTEER]. " + StatusLine();
                }
                return "steer: armed. " + StatusLine();
            }
            catch (Exception ex)
            {
                NsLog("[NSTEER] ERROR in Arm: " + ex);
                return "steer: arm failed — see [NSTEER] log. " + StatusLine();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Disarm()
        {
            try
            {
                ClearRegistry();
                if (_hooks != null)
                {
                    foreach (object h in _hooks)
                    {
                        try { ((IDisposable)h).Dispose(); } catch { }
                    }
                    _hooks.Clear();
                }
                _hooks = null;
                _callHeadCmd = null;
                _detourHeadCmd = null;
                Armed = false;
                _autoArmSuppressed = true;   // manual off: no silent re-arm
                NsLog("[NSTEER] disarmed — hook disposed, registry cleared.");
                return "steer: OFF (hook disposed, registry cleared).";
            }
            catch (Exception ex)
            {
                NsLog("[NSTEER] ERROR in Disarm: " + ex);
                return "steer: disarm failed — see [NSTEER] log.";
            }
        }

        public static void Shutdown()
        {
            if (Armed || (_hooks != null && _hooks.Count > 0))
                Disarm();
        }

        // Auto-arm for duel steer: idempotent — scans + hooks on the first
        // steerWanted tick. FAIL CLOSED: false leaves the duel on the current
        // free-target/legacy paths. A manual `steer off` suppresses re-arms.
        private static bool _autoArmFailed;
        private static bool _autoArmSuppressed;
        public static bool EnsureArmed()
        {
            try
            {
                if (Armed)
                    return true;
                if (_autoArmFailed || _autoArmSuppressed)
                    return false;
                Arm();
                if (Armed)
                    return true;
                _autoArmFailed = true;
                NsLog("[NSTEER] EnsureArmed: could not arm — duel runs the CURRENT free-target/legacy behavior.");
                return false;
            }
            catch (Exception ex)
            {
                _autoArmFailed = true;
                NsLog("[NSTEER] ERROR in EnsureArmed (duel stays current behavior): " + ex);
                return false;
            }
        }

        private static string StatusLine()
        {
            long rva = _addrHeadCmd != IntPtr.Zero && NativeTargetGuard.NativeBase != IntPtr.Zero
                ? _addrHeadCmd.ToInt64() - NativeTargetGuard.NativeBase.ToInt64() : -1;
            return $"steer: flag={(CombatAiState.NativeSteer ? "on" : "off")} armed={Armed} scanned={_scanned} " +
                   $"headCmd=0x{_addrHeadCmd.ToInt64():x} (rva 0x{rva:x}{(_sigFallback ? " fallback" : "")}) " +
                   $"registered={RegisteredCount}/{MaxReg} hits={_hits} misses={_misses} firstHits={_firstHits} " +
                   $"logLines={_nsLines}/{MaxNsLines}";
        }

        public static string HandleConsole(System.Collections.Generic.List<string> args)
        {
            // Outer try: a missing/unloadable EasyHook.dll throws at JIT of
            // Arm/Disarm — i.e. at THIS call site (same as nativehook).
            try
            {
                string a = args.Count > 0 ? args[0].Trim().ToLowerInvariant() : "status";
                switch (a)
                {
                    case "on":
                        CombatAiState.NativeSteer = true;
                        _autoArmSuppressed = false;
                        _autoArmFailed = false;
                        return "steer: ON for plain duel + spar defender. " + EnsureArmed() + " " + StatusLine();
                    case "off":
                        CombatAiState.NativeSteer = false;
                        return Disarm();
                    case "scan":
                        return Scan();
                    case "status":
                        return StatusLine();
                    default:
                        return "Usage: combatai.steer [on|off|scan|status]  " +
                               "(plain duel + spar defender: keep the combat lock ON, " +
                               "detour the native heading-command funnel at aiComp+0x15d8; " +
                               "off = current free-target/legacy behavior)";
                }
            }
            catch (Exception ex)
            {
                NsLog("[NSTEER] ERROR handling steer command (EasyHook load failure?): " + ex);
                return "steer: failed — see [NSTEER] log. " + StatusLine();
            }
        }

        // ------------------------------------------------------------------
        // Detour — THE funnel: void f(uint* channelObj, uint* dirVec3).
        // Runs on whatever thread the AI tick uses (possibly parallel workers):
        // pure pointer math + a fixed-array scan, zero allocation, never throws.
        // Membership = channelObj == registered aiComp+0x15d8  OR
        //              dirPtr     == registered aiComp+0x138c.
        // Member -> call original with a stack-built {ourX, ourY, callerZ}.
        // ------------------------------------------------------------------
        private unsafe static void DetourHeadCmd(UIntPtr channelObj, UIntPtr dirPtr)
        {
            NativeHeadCmdDelegate orig = _callHeadCmd;
            if (orig == null)
                return;
            try
            {
                for (int i = 0; i < MaxReg; i++)
                {
                    if (_reg[i].Enabled == 0)
                        continue;
                    bool chanHit = _reg[i].Chan == channelObj;
                    if (!chanHit && _reg[i].CtxDir != dirPtr)
                        continue;
                    ulong packed = _reg[i].PackedDir;
                    float* d = stackalloc float[4];
                    uint ux = (uint)(packed & 0xffffffffu), uy = (uint)(packed >> 32);
                    d[0] = *(float*)&ux;
                    d[1] = *(float*)&uy;
                    d[2] = *(float*)((byte*)dirPtr.ToPointer() + 8);   // caller's desiredZ passthrough
                    d[3] = 0f;
                    _hits++;
                    if (_reg[i].HitLogged < 3 && _nsLines < MaxNsLines)
                    {
                        _reg[i].HitLogged = 3;
                        _firstHits++;
                        _nsLines++;
                        NsLog($"[NSTEER] first override agent#{_reg[i].AgentIndex} via {(chanHit ? "chan" : "ctx")} " +
                              $"chan=0x{channelObj.ToUInt64():x} dir=0x{dirPtr.ToUInt64():x} " +
                              $"want=({d[0]:F2},{d[1]:F2}) z={d[2]:F2}");
                    }
                    orig(channelObj, (UIntPtr)(void*)d);
                    return;
                }
                _misses++;
            }
            catch { }
            orig(channelObj, dirPtr);
        }
    }

    // =========================================================================
    // DEV NOTES — 1.4.8 verification (installed TaleWorlds.Native.dll,
    // capstone disasm of proj/TaleWorlds.Native.1.4.8.dll):
    //
    //  * headCmd leaf @ rva 0x61ac60 (.pdata func [0x61ac60,0x61ad18)):
    //      48 89 5c 24 08      mov [rsp+8],rbx
    //      57                  push rdi
    //      48 83 ec 20         sub rsp,0x20
    //      f3 0f 10 02         movss xmm0,[rdx]          dir.x
    //      48 8b fa            mov rdi,rdx
    //      0f 57 05 ..         xorps xmm0,[rip]          -dir.x (sign mask)
    //      48 8b d9            mov rbx,rcx               channel obj
    //      f3 0f 10 4a 04      movss xmm1,[rdx+4]        dir.y
    //      e8 ..               call atan2f -> angle
    //      movss [rbx+0x14]=curAng+resid fold into [-pi,pi] via rip consts
    //      movss [rbx+0x10]=angle ; call sincos-pack ;
    //      movss [rbx]=x^mask ; shufps ; movss [rbx+4]=y
    //      movss xmm0,[rbx+8]; subss xmm0,[rdi+8]; addss xmm0,[rbx+0x18];
    //      movss [rbx+0x18],xmm0 ; mov eax,[rdi+8]; mov [rbx+8],eax
    //      mov rbx,[rsp+0x30]; add rsp,0x20; pop rdi; jmp dirty-flag helper
    //    => byte-exact analog of 1.4.5 FUN_180617d10.
    //
    //  * Call sites (3, byte-scanned — the ONLY e8 calls to it):
    //      0x6b92a3  aiTick non-decision: rcx=r15+0x15d8, rdx=r15+0x138c
    //                (right after call 0x712560 = FUN_18070f5e0 steering)
    //      0x6b96b9  aiTick posted stack order: rcx=rsp+0x60, rdx=r15+0x138c
    //      0x6ba3d1  decision-dispatcher tail: rcx=rdi+0x15d8, rdx=rsp+0x30
    //                (stack copy of [rdi+0x138c..0x139c])
    //
    //  * aiComp+0x15d8 -> agent+0x474 bulk copy (0x3c bytes) verified @ rva
    //    0x5f4770: movups xmm1,[rdx+0x15d8] ; movups [rdi+0x474],xmm1
    //
    //  * Integrator (FUN_180617b70 analog) @ rva 0x61aac0 — residual/zDelta
    //    rate-limiter, called on r15+0x15d8 inside aiTick (@0x6b97c4).
    //    Angle variant (FUN_180617c90 analog) @ rva 0x61abe0 — feeds agent+0x474
    //    in heading-only mode; NOT hooked (managed SetMovementDirection mirrors
    //    into aiComp+0x15d8 itself @ rva 0x603e80 — separate path, unused by
    //    steer mode).
    //
    //  * The +0x1808=5 write appears twice: dispatcher [0x6ba110,0x6ba3e3)
    //    (the fallback's target — last call -> 0x61ac60) and inside the bigger
    //    aiTick [0x69e8a0,0x69f27a) (AI-component init/reset — its +0x15d8
    //    writes are spawn-time init, harmless; detour re-overrides next tick).
    //
    //  * Signature above verified UNIQUE over the installed .text (1 hit).
    // =========================================================================
}
