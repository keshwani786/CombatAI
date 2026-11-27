using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyHook;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =========================================================================
    // NativeTargetGuard — managed native-hook infrastructure + detection-first
    // crash guard for releasing the combat target lock (target = -1) safely.
    //
    // Research basis: research/12_nulltarget_shim.md (Option H2),
    // research/11_native_ai_movement.md, research/10_native_target_lock.md.
    // Working-recipe basis: the NativeHook module's NativeHookSubModule.cs
    // (EasyHook LocalHook.Create on scanned native addresses, unsafe-ptr reads,
    // original-delegate captured BEFORE LocalHook.Create,
    // ThreadACL.SetExclusiveACL(new int[0])).
    //
    // CRASH-SURFACE SUMMARY (verified on the deployed 1.4.8 binary — see the
    // dev notes at the bottom): EVERY unguarded AV site on the -1 path sits
    // behind ONE gate, pathObj+0x1c (== aiComp+0x19c, "direct/combat-move"
    // mode). Native sets it to 1 iff:
    //     goal(+0x1184) >= 0  OR  (foot && target>=0 && combatState in {7,8})
    // The guard = ONE detour on the leaf that computes that flag: call the
    // original, then force +0x1c = 0 when the effective combat target < 0.
    // Plus a one-time field poke at release (mode=0, goal=-1) which closes
    // the <=1-tick stale window before the next AI recompute.
    //
    // VERIFIED 1.4.8 OFFSETS (confirmed by disassembling the deployed
    // TaleWorlds.Native.dll — the leaf at rva 0x6c2d50 is a byte-for-byte
    // analog of 1.4.5's FUN_1806bfdd0):
    //   agent +0x888   -> aiComp (AI component; null for non-AI)
    //   aiComp +0x00   -> owning agent record (read by the leaf for +0x55c)
    //   agent +0x55c   -> mount agent index (-1 = foot)
    //   agent +0x504   -> command-block copy of the target index
    //   aiComp +0x180  == pathObj base (pathObj+0x28 stores the aiComp back-ptr)
    //   pathObj+0x1c   == aiComp+0x19c   mode flag (THE crash gate)
    //   pathObj+0x1184 == aiComp+0x1304  goal-agent override index (-1 none)
    //   aiComp +0x1504 -> committed combat target index (-1 none)
    //   aiComp +0x2258 -> pending-target flag byte; +0x225c pending index
    //   aiComp +0x1804 -> combat-behavior state (mode-1 needs {7,8})
    //
    // CONSOLE:  combatai.nativehook [scan|hook|guard|status|off]
    //   scan   - locate + report all addresses to the [NH] log, hook nothing
    //   hook   - arm Hook 1 (set_target_agent detection dump) ONLY
    //   guard  - arm Hook 1 + Hook 2 (mode-leaf crash guard)
    //   off    - dispose all hooks, guarded release off
    //   status - current state (default with no args)
    // Default state is OFF: nothing hooks until `hook`/`guard` is run.
    //
    // SAFETY INVARIANTS:
    //  * SetTargetAgent(null) goes ONLY through ReleaseCombatTarget (the poked
    //    path) once this subsystem is armed — never anywhere else. Enforced by
    //    the routing inside CombatAiOverrideComponent.EnsureTargetAgent.
    //  * The mode-leaf detour ONLY zeroes the mode when the effective combat
    //    target is < 0 — a no-op whenever a target is held. The detection
    //    detour does zero mutation.
    //  * If ANY address can't be located the command logs and does nothing —
    //    no fuzzy patching.
    //
    // RE-LOCK DIAGNOSTICS (research/14_relock_paths.md):
    //  * The set_target_agent detour logs EVERY call (-1 releases AND real-idx
    //    re-locks) — catches managed-side re-locks the old -1-only filter hid.
    //  * ReleaseCombatTarget read-backs aiComp+0x2114 bit 0x800 after
    //    SetAutomaticTargetSelection(false) (auto800=on|MISSING) — verifies the
    //    folded-shim hypothesis on the deployed build.
    //  * The released-agent watchdog (MarkReleased -> Poll, driven from
    //    CombatAiMissionBehavior.OnMissionTick) diffs the native target fields
    //    per tick; a WATCH line names the re-lock channel: pend flip = native
    //    scorer, goal flip = +0x1304 override, tgt flip = pending commit.
    //  * A positive set_target write on a watched agent is tagged RELOCK and
    //    the first ~4 per agent also emit a bounded RELOCK-STACK managed
    //    stack — names the managed caller that re-locks a released agent.
    // =========================================================================
    public static class NativeTargetGuard
    {
        // ---- native field offsets (verified on the deployed 1.4.8 dll) ----
        private const int AgentOffAiComp        = 0x888;
        private const int AgentOffIndex         = 0x10;
        private const int AgentOffMountIdx      = 0x55c;
        private const int AgentOffCmdBlockTgt   = 0x504;
        private const int AiCompOffOwnerAgent   = 0x00;
        private const int AiCompOffTarget       = 0x1504;
        private const int AiCompOffPendFlag     = 0x2258;
        private const int AiCompOffPendTarget   = 0x225c;
        private const int AiCompOffGoal         = 0x1304;
        private const int AiCompOffMode         = 0x19c;
        private const int AiCompOffCombatState  = 0x1804;
        private const int AiCompOffFlags        = 0x2114;  // flag word; bit 0x800 = auto-select OFF (research 14 sec.4)
        private const int PathObjOffMode        = 0x1c;
        private const int PathObjOffAiComp      = 0x28;
        private const int PathObjOffGoal        = 0x1184;
        private const int PathObjToAiCompDelta  = 0x180;   // pathObj = aiComp + 0x180

        // ---- armed state ----
        // 0 = off (default), 1 = Hook1 only (set_target_agent detection),
        // 2 = Hook1 + Hook2 (mode-leaf guard). Set only via the console cmd.
        public static int ArmedLevel { get; private set; }
        // Once armed (level>=1) every null-target release MUST route through
        // ReleaseCombatTarget — see EnsureTargetAgent.
        public static bool GuardedRelease => ArmedLevel >= 1;

        public static IntPtr NativeBase { get; private set; }
        private static int _nativeSize;
        private static int _textRva, _textSize;
        private static int _pdataRva, _pdataSize;
        private static byte[] _textBuf;
        private static bool _initialized;
        private static bool _scanned;

        // resolved addresses (IntPtr.Zero = unresolved)
        private static IntPtr _addrSetTarget, _addrGetTarget, _addrInvalidate, _addrAutoSelect;
        private static IntPtr _addrModeLeaf, _addrGoalSetter;
        private static bool _guardOnFallback;

        // hooks + original-call delegates (kept alive for the session).
        // IList of LocalHook — the EasyHook type stays OUT of field/member
        // signatures so this class loads and GuardedRelease is readable even
        // if EasyHook.dll can't resolve; only Arm() (JIT-gated, try/caught)
        // actually requires the assembly.
        private static System.Collections.IList _hooks;
        private static NativeSetTargetAgentDelegate _callSetTarget;
        private static NativeModeLeafDelegate _callModeLeaf;
        private static NativeGoalSetterDelegate _callGoalSetter;
        // strong refs to the detour delegates themselves
        private static NativeSetTargetAgentDelegate _detourSetTarget;
        private static NativeModeLeafDelegate _detourModeLeaf;
        private static NativeGoalSetterDelegate _detourGoalSetter;

        // reflection handles
        private static MethodInfo _agentGetPtr;

        // bounded [NH] logging
        private static int _nhDetectLines;
        private static int _nhGuardLines;
        private const int MaxNhDetectLines = 80;
        private const int MaxNhGuardLines = 80;
        private static string _lastOp = "init";

        // RELOCK-STACK budget: stack capture in the set_target detour is
        // bounded twice — per watched agent (StackDumps) and globally.
        private static int _nhStackLines;
        private const int MaxNhStackLines = 160;
        private const int MaxRelockStacksPerAgent = 4;
        private const int MaxStackFrames = 12;

        // ---- released-agent watchdog (research 14: who re-locks +0x1504?) ----
        // MarkReleased() snapshots a released agent's native target fields;
        // Poll() (driven from CombatAiMissionBehavior.OnMissionTick, cheap
        // early-out when nothing is watched) diffs them per tick and logs the
        // exact transition — tgt flip = pending-commit, pend flip = native
        // scorer queued a re-lock, goal flip = +0x1304 goal-override.
        private sealed class WatchEntry
        {
            public Agent Agent;        // kept for the IsActive() liveness check
            public UIntPtr AgentPtr;   // native agent record
            public int AgentIndex;
            public int Tgt, PendF, Pend, Goal, Flags;   // last-seen snapshot
            public int Lines;          // per-agent log budget
            public int StackDumps;     // RELOCK-STACK dumps emitted (capped)
            public bool HasSnapshot;
        }
        private static readonly List<WatchEntry> _watch = new List<WatchEntry>(8);
        private const int MaxWatch = 8;
        private const int MaxWatchLinesPerAgent = 40;

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, SetLastError = true)]
        private delegate void NativeSetTargetAgentDelegate(UIntPtr agentPtr, int targetIdx);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall, SetLastError = true)]
        private delegate void NativeModeLeafDelegate(UIntPtr pathObj);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall, SetLastError = true)]
        private delegate void NativeGoalSetterDelegate(UIntPtr pathObj, int goalIdx);

        // ------------------------------------------------------------------
        // Init: locate TaleWorlds.Native.dll in this process, parse its PE
        // sections (in-memory image: section data sits at base + VirtualAddress),
        // snapshot .text for signature scanning.
        // ------------------------------------------------------------------
        // internal (was private): NativeSteerOverride shares this init path.
        internal static bool EnsureInitialized()
        {
            if (_initialized)
                return NativeBase != IntPtr.Zero;
            _initialized = true;
            try
            {
                foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                {
                    if (m.ModuleName == "TaleWorlds.Native.dll")
                    {
                        NativeBase = m.BaseAddress;
                        _nativeSize = m.ModuleMemorySize;
                        break;
                    }
                }
                if (NativeBase == IntPtr.Zero)
                {
                    NHLog("[NH] ERROR: TaleWorlds.Native.dll not found in process modules.");
                    return false;
                }
                NHLog($"[NH] TaleWorlds.Native.dll base=0x{NativeBase.ToInt64():x} size=0x{_nativeSize:x}");
                ParseSections();
                NHLog($"[NH] .text rva=0x{_textRva:x} size=0x{_textSize:x} | .pdata rva=0x{_pdataRva:x} size=0x{_pdataSize:x} ({_pdataSize / 12} funcs)");
                _agentGetPtr = AccessTools.Method(typeof(Agent), "GetPtr");
                if (_agentGetPtr == null)
                    NHLog("[NH] WARN: Agent.GetPtr method not found — ReleaseCombatTarget will be unavailable.");
                return true;
            }
            catch (Exception ex)
            {
                NHLog("[NH] ERROR in EnsureInitialized: " + ex);
                return false;
            }
        }

        private static unsafe void ParseSections()
        {
            byte* p = (byte*)NativeBase;
            int peOff = *(int*)(p + 0x3c);
            byte* nt = p + peOff;
            ushort nsec = *(ushort*)(nt + 4 + 2);
            ushort optSize = *(ushort*)(nt + 4 + 16);
            byte* sec = nt + 4 + 20 + optSize;
            for (int i = 0; i < nsec; i++)
            {
                byte* s = sec + i * 40;
                var nameChars = new char[8];
                int n = 0;
                for (int k = 0; k < 8 && s[k] != 0; k++) nameChars[n++] = (char)s[k];
                string name = new string(nameChars, 0, n);
                int vsize = *(int*)(s + 8), vaddr = *(int*)(s + 12), rsize = *(int*)(s + 16);
                if (name == ".text") { _textRva = vaddr; _textSize = Math.Min(vsize, rsize); }
                else if (name == ".pdata") { _pdataRva = vaddr; _pdataSize = rsize; }
            }
        }

        // ------------------------------------------------------------------
        // Scan: resolve every address we need. Logs each hit/miss to [NH].
        // Returns a human-readable summary for the console.
        // ------------------------------------------------------------------
        private static string Scan()
        {
            if (!EnsureInitialized())
                return "nativehook: init failed — see [NH] log.";
            try
            {
                _scanned = true;
                // Managed copy of .text (same pattern as NativeHookSubModule —
                // bounded, one-time, and safe against unmapped page edges).
                _textBuf = GetMemoryBuffer(NativeBase + _textRva, _textSize);

                // 1) EngineMethod binding addresses — NO signature needed.
                //    The generated delegates in ManagedCallbacks.
                //    ScriptingInterfaceOfIMBAgent wrap the raw native entry
                //    points (Marshal.GetDelegateForFunctionPointer); recover
                //    the pointer back out of the delegate object.
                Type siAgent = FindType("ManagedCallbacks.ScriptingInterfaceOfIMBAgent");
                if (siAgent == null)
                    NHLog("[NH] ERROR: type ManagedCallbacks.ScriptingInterfaceOfIMBAgent not found in any loaded assembly.");
                else
                {
                    _addrSetTarget   = ResolveBindingAddress(siAgent, "call_SetTargetAgentDelegate", "set_target_agent");
                    _addrGetTarget   = ResolveBindingAddress(siAgent, "call_GetTargetAgentDelegate", "get_target_agent");
                    _addrInvalidate  = ResolveBindingAddress(siAgent, "call_InvalidateTargetAgentDelegate", "invalidate_target_agent");
                    _addrAutoSelect  = ResolveBindingAddress(siAgent, "call_SetAutomaticTargetSelectionDelegate", "set_automatic_target_selection");
                }

                // 2) The mode leaf (1.4.8-analog of FUN_1806bfdd0 — the ONLY
                //    writer of the +0x1c mode flag inside the goal setter).
                //    Primary: direct body signature over .text.
                _addrModeLeaf = ScanForFirstResult(_textBuf,
                    // cmp dword [r?+0x1184],0 ; mov r?,[r?+0x28] ; mov r?,[r?]
                    // mov dword [r?+0x1c],0  ; jge rel8
                    // cmp dword [r?+0x55c],0 ; jge rel8
                    // cmp byte  [r?+0x2258],0
                    "83 ?? 84 11 00 00 00 48 8b ?? 28 48 8b ?? c7 ?? 1c 00 00 00 00 " +
                    "?? ?? 83 ?? 5c 05 00 00 00 ?? ?? 80 ?? 58 22 00 00 00");
                if (_addrModeLeaf != IntPtr.Zero)
                {
                    bool tail = ConfirmLeafTail(_addrModeLeaf);
                    NHLog($"[NH] mode-leaf signature hit @0x{_addrModeLeaf.ToInt64():x} (rva 0x{(_addrModeLeaf.ToInt64() - NativeBase.ToInt64()):x}) tail-confirm={(tail ? "OK" : "FAILED")}");
                    if (!tail)
                        _addrModeLeaf = IntPtr.Zero;   // don't trust a head-only match
                }

                // 3) Caller chain (task's anchor method): the goal setter
                //    (1.4.8-analog of FUN_1806be8d0) is the function containing
                //    the mgr+0x3238 compares fed by an imul-by-0xc60. Its LAST
                //    `call rel32` targets the leaf.
                _addrGoalSetter = FindGoalSetter();
                if (_addrGoalSetter != IntPtr.Zero)
                {
                    NHLog($"[NH] goal-setter (FUN_1806be8d0-analog) @0x{_addrGoalSetter.ToInt64():x} (rva 0x{(_addrGoalSetter.ToInt64() - NativeBase.ToInt64()):x})");
                    if (_addrModeLeaf == IntPtr.Zero)
                    {
                        IntPtr leaf = FindLeafInFunction(_addrGoalSetter);
                        if (leaf != IntPtr.Zero)
                        {
                            _addrModeLeaf = leaf;
                            NHLog($"[NH] mode-leaf resolved via caller chain @0x{leaf.ToInt64():x} (rva 0x{(leaf.ToInt64() - NativeBase.ToInt64()):x})");
                        }
                    }
                }
                else
                    NHLog("[NH] goal-setter not found (+0x3238/0xc60 anchor scan) — leaf fallback unavailable.");

                _guardOnFallback = false;
                return StatusLine();
            }
            catch (Exception ex)
            {
                NHLog("[NH] ERROR in Scan: " + ex);
                return "nativehook: scan failed — see [NH] log.";
            }
        }

        // ------------------------------------------------------------------
        // Resolve an EngineMethod binding's native address out of the static
        // call_*Delegate field. Tries Marshal.GetFunctionPointerForDelegate
        // first; falls back to the delegate's _methodPtr/_methodPtrAux fields.
        // Logs every candidate with an in-module flag — the right one is the
        // pointer inside [NativeBase, NativeBase+size).
        // ------------------------------------------------------------------
        private static IntPtr ResolveBindingAddress(Type holder, string fieldName, string label)
        {
            try
            {
                FieldInfo f = AccessTools.Field(holder, fieldName);
                if (f == null)
                {
                    NHLog($"[NH] {label}: field {fieldName} not found on {holder.Name}.");
                    return IntPtr.Zero;
                }
                if (!(f.GetValue(null) is Delegate d))
                {
                    NHLog($"[NH] {label}: {fieldName} is null/not a delegate (bindings not installed yet?).");
                    return IntPtr.Zero;
                }
                var cands = new List<IntPtr>();
                try { cands.Add(Marshal.GetFunctionPointerForDelegate(d)); }
                catch (Exception ex) { NHLog($"[NH] {label}: GetFunctionPointerForDelegate threw {ex.GetType().Name}: {ex.Message}"); }
                foreach (string fn in new[] { "_methodPtr", "_methodPtrAux", "_methodPtrAuxiliary" })
                {
                    try
                    {
                        FieldInfo mf = typeof(Delegate).GetField(fn, BindingFlags.Instance | BindingFlags.NonPublic);
                        if (mf != null && mf.GetValue(d) is IntPtr ip && ip != IntPtr.Zero && !cands.Contains(ip))
                            cands.Add(ip);
                    }
                    catch { }
                }
                IntPtr best = IntPtr.Zero;
                var parts = new List<string>();
                foreach (IntPtr c in cands)
                {
                    bool inMod = InModule(c);
                    if (inMod && best == IntPtr.Zero) best = c;
                    parts.Add($"0x{c.ToInt64():x}{(inMod ? "[IN]" : "[out]")}");
                }
                NHLog($"[NH] {label}: candidates={string.Join(" ", parts.ToArray())} -> using 0x{best.ToInt64():x}");
                if (best == IntPtr.Zero && cands.Count > 0)
                    NHLog($"[NH] {label}: WARN no candidate inside the native module — refusing to guess.");
                return best;
            }
            catch (Exception ex)
            {
                NHLog($"[NH] {label}: resolve failed — {ex}");
                return IntPtr.Zero;
            }
        }

        private static bool InModule(IntPtr p)
        {
            long v = p.ToInt64(), b = NativeBase.ToInt64();
            return v >= b && v < b + _nativeSize;
        }

        // internal (was private): shared by NativeSteerOverride's caller-chain scan.
        internal static bool InText(long rva) => rva >= _textRva && rva < _textRva + _textSize;

        // Read-only views of the parsed image for sibling native-hook files
        // (NativeSteerOverride). EnsureTextBuffer fills the shared .text
        // snapshot lazily — Scan() fills it too, whichever runs first.
        internal static byte[] TextBuffer => _textBuf;
        internal static int TextRva => _textRva;
        internal static int TextSize => _textSize;
        internal static void EnsureTextBuffer()
        {
            if (_textBuf == null && _textSize > 0 && NativeBase != IntPtr.Zero)
                _textBuf = GetMemoryBuffer(NativeBase + _textRva, _textSize);
        }

        // ------------------------------------------------------------------
        // Caller-chain location of the goal setter: find .text sites that
        // reference the mgr+0x3238 record field within +-0x300 bytes of an
        // imul-by-0xc60, then use .pdata (the RUNTIME_FUNCTION table — exact
        // bounds, better than walking back byte patterns for a prologue) to
        // get the containing function. The setter also writes [r?+0x1184]
        // (the goal idx store before the leaf call) which we require inside
        // the candidate function to reject sibling +0x3238 readers.
        // ------------------------------------------------------------------
        private static IntPtr FindGoalSetter()
        {
            if (_textBuf == null || _pdataRva == 0) return IntPtr.Zero;
            var sig3238 = ParseSignatureString("38 32 00 00");
            var sigc60  = ParseSignatureString("60 0c 00 00");
            var sig1184 = ParseSignatureString("84 11 00 00");
            var seen = new HashSet<long>();
            for (int i = 0; i + 4 <= _textBuf.Length; i++)
            {
                if (!MatchAt(_textBuf, i, sig3238)) continue;
                // require an imul imm32 0xc60 nearby
                bool near = false;
                int lo = Math.Max(0, i - 0x300), hi = Math.Min(_textBuf.Length - 4, i + 0x300);
                for (int j = lo; j <= hi; j++)
                    if (MatchAt(_textBuf, j, sigc60)) { near = true; break; }
                if (!near) continue;
                int rva = _textRva + i;
                if (!PdataFindFunc(rva, out int fb, out int fe)) continue;
                if (!seen.Add(fb)) continue;              // same function, dedupe
                // the goal setter stores the new goal via a [reg+0x1184] write
                bool has1184 = false;
                for (int j = fb - _textRva; j <= fe - _textRva - 4 && j >= 0 && j + 4 <= _textBuf.Length; j++)
                    if (MatchAt(_textBuf, j, sig1184)) { has1184 = true; break; }
                NHLog($"[NH] +0x3238 site rva=0x{rva:x} -> func [0x{fb:x},0x{fe:x}) has1184write={has1184}");
                if (has1184) return NativeBase + fb;
            }
            return IntPtr.Zero;
        }

        // .pdata RUNTIME_FUNCTION lookup: entries are sorted {begin,end,unwind}
        // RVAs, 12 bytes each. Returns the function containing `rva`.
        // internal (was private): shared by NativeSteerOverride's caller-chain scan.
        internal static unsafe bool PdataFindFunc(int rva, out int begin, out int end)
        {
            begin = end = 0;
            if (_pdataRva == 0) return false;
            byte* pd = (byte*)NativeBase + _pdataRva;
            int count = _pdataSize / 12;
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int b = *(int*)(pd + mid * 12), e = *(int*)(pd + mid * 12 + 4);
                if (rva < b) hi = mid - 1;
                else if (rva >= e) lo = mid + 1;
                else { begin = b; end = e; return true; }
            }
            return false;
        }

        // Scan a function's bytes for `e8 <rel32>` call sites; a candidate is
        // the leaf iff the call target passes LooksLikeModeLeaf. Returns the
        // LAST verified target (the leaf call is the last call in the setter).
        private static unsafe IntPtr FindLeafInFunction(IntPtr funcStart)
        {
            long fb = funcStart.ToInt64() - NativeBase.ToInt64();
            if (!PdataFindFunc((int)fb, out int b, out int e)) return IntPtr.Zero;
            IntPtr last = IntPtr.Zero;
            for (int i = b - _textRva; i <= e - _textRva - 5; i++)
            {
                if (_textBuf[i] != 0xE8) continue;
                int rel = BitConverter.ToInt32(_textBuf, i + 1);
                long targetRva = _textRva + i + 5 + (long)rel;
                if (!InText(targetRva)) continue;
                if (LooksLikeModeLeaf(NativeBase + (int)targetRva))
                    last = NativeBase + (int)targetRva;
            }
            return last;
        }

        // Cheap body-shape verification for the mode leaf: starts with
        // `83 ?? 84 11 00 00 00` (cmp dword [reg+0x1184],0), and within the
        // next ~0x50 bytes contains `c7 ?? 1c 01 00 00 00` (mov [reg+0x1c],1)
        // followed shortly by a `c3` ret — the exact tail of FUN_1806bfdd0.
        private static unsafe bool LooksLikeModeLeaf(IntPtr addr)
        {
            byte* p = (byte*)addr;
            if (!(p[0] == 0x83 && p[2] == 0x84 && p[3] == 0x11 && p[4] == 0x00 && p[5] == 0x00 && p[6] == 0x00))
                return false;
            for (int i = 0x10; i < 0x50; i++)
            {
                if (p[i] == 0xC7 && p[i + 2] == 0x1c && p[i + 3] == 0x01 && p[i + 4] == 0x00 && p[i + 5] == 0x00 && p[i + 6] == 0x00)
                {
                    for (int j = i + 7; j < i + 0x20; j++)
                        if (p[j] == 0xC3) return true;
                    return false;
                }
            }
            return false;
        }

        // After a head-signature hit, confirm the tail: within the next 0x40
        // bytes we expect the committed-target read `8b ?? 04 15 00 00`
        // (mov reg,[reg+0x1504]), the `sub reg,7 ; cmp reg,1` state check
        // (`83 ?? 07 83 ?? 01`), the mode=1 write `c7 ?? 1c 01 00 00 00`,
        // and a `c3` ret.
        private static unsafe bool ConfirmLeafTail(IntPtr addr)
        {
            byte* p = (byte*)addr;
            bool has1504 = false, hasStateCheck = false, hasMode1 = false, hasRet = false;
            for (int i = 0x27; i < 0x60; i++)
            {
                if (p[i] == 0x8B && p[i + 2] == 0x04 && p[i + 3] == 0x15 && p[i + 4] == 0x00 && p[i + 5] == 0x00) has1504 = true;
                if (p[i] == 0x83 && p[i + 2] == 0x07 && p[i + 3] == 0x83 && p[i + 5] == 0x01) hasStateCheck = true;
                if (p[i] == 0xC7 && p[i + 2] == 0x1C && p[i + 3] == 0x01 && p[i + 4] == 0x00 && p[i + 5] == 0x00 && p[i + 6] == 0x00) hasMode1 = true;
                if (p[i] == 0xC3) hasRet = true;
            }
            if (!(has1504 && hasStateCheck && hasMode1 && hasRet))
                NHLog($"[NH] leaf tail check: 1504read={has1504} state={hasStateCheck} mode1write={hasMode1} ret={hasRet}");
            return has1504 && hasStateCheck && hasMode1 && hasRet;
        }

        // ------------------------------------------------------------------
        // NativeHookSubModule's scan helpers (unchanged recipe): memory buffer
        // copy + hex-string signatures with `??` wildcards.
        // ------------------------------------------------------------------
        private static unsafe byte[] GetMemoryBuffer(IntPtr startAddress, int size)
        {
            if (startAddress == IntPtr.Zero || size <= 0) return new byte[1];
            var buf = new byte[size];
            byte* p = (byte*)startAddress;
            for (long i = 0; i < buf.LongLength; i++)
                buf[i] = p[i];
            return buf;
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

        // internal (was private): shared by NativeSteerOverride's scan.
        internal static IntPtr ScanForFirstResult(byte[] buffer, string signatureString)
        {
            int[] sig = ParseSignatureString(signatureString);
            for (int i = 0; i + sig.Length <= buffer.Length; i++)
                if (MatchAt(buffer, i, sig))
                    return NativeBase + _textRva + i;
            return IntPtr.Zero;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = a.GetType(fullName); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Arming. level 1 = detection hook only; level 2 = + mode-leaf guard.
        // NoInlining isolates the EasyHook/JIT surface so a missing assembly
        // can only fault this frame, caught by the caller.
        // ------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Arm(int level)
        {
            try
            {
                if (!_scanned)
                {
                    string r = Scan();
                    if (!_scanned) return r;
                }
                _hooks = _hooks ?? new List<LocalHook>();
                string msg = "";

                // Hook 1 — set_target_agent (DETECTION only, zero mutation):
                // every managed->native target write passes through here.
                // When targetIdx == -1 we dump the native target fields to
                // prove the detour fires + validate the 1.4.8 offsets.
                if (_addrSetTarget != IntPtr.Zero)
                {
                    if (_callSetTarget == null)
                    {
                        try
                        {
                            _detourSetTarget = new NativeSetTargetAgentDelegate(DetourSetTargetAgent);
                            // Recipe: capture the ORIGINAL-call delegate BEFORE
                            // LocalHook.Create patches the prologue.
                            _callSetTarget = (NativeSetTargetAgentDelegate)Marshal.GetDelegateForFunctionPointer(
                                _addrSetTarget, typeof(NativeSetTargetAgentDelegate));
                            LocalHook h = LocalHook.Create(_addrSetTarget, _detourSetTarget, null);
                            h.ThreadACL.SetExclusiveACL(new int[0]);
                            _hooks.Add(h);
                            msg += $"set_target_agent hooked @0x{_addrSetTarget.ToInt64():x}. ";
                            NHLog($"[NH] hook1 armed: set_target_agent @0x{_addrSetTarget.ToInt64():x}");
                        }
                        catch (Exception ex)
                        {
                            _callSetTarget = null;
                            _detourSetTarget = null;
                            msg += "set_target_agent hook FAILED — see [NH]. ";
                            NHLog($"[NH] hook1 create failed @0x{_addrSetTarget.ToInt64():x}: {ex}");
                        }
                    }
                    else msg += "set_target_agent already hooked. ";
                }
                else msg += "set_target_agent addr unresolved — hook1 SKIPPED. ";

                if (level >= 2)
                {
                    // Hook 2 — the mode leaf (or the goal setter as fallback).
                    // Detour: call original, then force pathObj+0x1c = 0 when
                    // the effective combat target is < 0 (H2 crash guard).
                    if (_addrModeLeaf != IntPtr.Zero)
                    {
                        if (_callModeLeaf == null)
                        {
                            try
                            {
                                _detourModeLeaf = new NativeModeLeafDelegate(DetourModeLeaf);
                                _callModeLeaf = (NativeModeLeafDelegate)Marshal.GetDelegateForFunctionPointer(
                                    _addrModeLeaf, typeof(NativeModeLeafDelegate));
                                LocalHook h = LocalHook.Create(_addrModeLeaf, _detourModeLeaf, null);
                                h.ThreadACL.SetExclusiveACL(new int[0]);
                                _hooks.Add(h);
                                _guardOnFallback = false;
                                msg += $"mode-leaf guard armed @0x{_addrModeLeaf.ToInt64():x}. ";
                                NHLog($"[NH] hook2 armed: mode leaf @0x{_addrModeLeaf.ToInt64():x} (primary)");
                            }
                            catch (Exception ex)
                            {
                                _callModeLeaf = null;
                                _detourModeLeaf = null;
                                msg += "mode-leaf hook FAILED — see [NH]. ";
                                NHLog($"[NH] hook2 create failed @0x{_addrModeLeaf.ToInt64():x}: {ex}");
                            }
                        }
                        else msg += "mode-leaf already hooked. ";
                    }
                    else if (_addrGoalSetter != IntPtr.Zero)
                    {
                        if (_callGoalSetter == null)
                        {
                            try
                            {
                                // Fallback: hook the containing goal setter itself
                                // (bigger surface — every goal change — but the
                                // post-call guard is the same).
                                _detourGoalSetter = new NativeGoalSetterDelegate(DetourGoalSetter);
                                _callGoalSetter = (NativeGoalSetterDelegate)Marshal.GetDelegateForFunctionPointer(
                                    _addrGoalSetter, typeof(NativeGoalSetterDelegate));
                                LocalHook h = LocalHook.Create(_addrGoalSetter, _detourGoalSetter, null);
                                h.ThreadACL.SetExclusiveACL(new int[0]);
                                _hooks.Add(h);
                                _guardOnFallback = true;
                                msg += $"mode guard armed on GOAL-SETTER fallback @0x{_addrGoalSetter.ToInt64():x}. ";
                                NHLog($"[NH] hook2 armed: goal setter @0x{_addrGoalSetter.ToInt64():x} (FALLBACK)");
                            }
                            catch (Exception ex)
                            {
                                _callGoalSetter = null;
                                _detourGoalSetter = null;
                                msg += "goal-setter hook FAILED — see [NH]. ";
                                NHLog($"[NH] hook2-fallback create failed @0x{_addrGoalSetter.ToInt64():x}: {ex}");
                            }
                        }
                        else msg += "goal-setter already hooked. ";
                    }
                    else msg += "mode leaf + goal setter BOTH unresolved — guard SKIPPED (detection only). ";
                }

                ArmedLevel = Math.Max(ArmedLevel,
                    (_callModeLeaf != null || _callGoalSetter != null) ? 2
                    : _callSetTarget != null ? 1 : 0);
                NHLog($"[NH] armed level -> {ArmedLevel}");
                return msg + StatusLine();
            }
            catch (Exception ex)
            {
                NHLog("[NH] ERROR in Arm: " + ex);
                return "nativehook: arm failed — see [NH] log. " + StatusLine();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Disarm()
        {
            try
            {
                if (_hooks != null)
                {
                    foreach (object h in _hooks)
                    {
                        try { ((IDisposable)h).Dispose(); } catch { }
                    }
                    _hooks.Clear();
                }
                _hooks = null;
                _callSetTarget = null;
                _callModeLeaf = null;
                _callGoalSetter = null;
                _detourSetTarget = null;
                _detourModeLeaf = null;
                _detourGoalSetter = null;
                _watch.Clear();
                ArmedLevel = 0;
                _autoArmSuppressed = true;   // manual off: no silent re-arm by free-target modes
                NHLog("[NH] all hooks disposed — level 0.");
                return "nativehook: OFF (all hooks disposed).";
            }
            catch (Exception ex)
            {
                NHLog("[NH] ERROR in Disarm: " + ex);
                return "nativehook: disarm failed — see [NH] log.";
            }
        }

        public static void Shutdown()
        {
            if (ArmedLevel > 0 || (_hooks != null && _hooks.Count > 0))
                Disarm();
        }

        private static string StatusLine()
        {
            return $"nativehook: level={ArmedLevel} scanned={_scanned} " +
                   $"set_target=0x{_addrSetTarget.ToInt64():x} get_target=0x{_addrGetTarget.ToInt64():x} " +
                   $"invalidate=0x{_addrInvalidate.ToInt64():x} autosel=0x{_addrAutoSelect.ToInt64():x} " +
                   $"modeLeaf=0x{_addrModeLeaf.ToInt64():x} goalSetter=0x{_addrGoalSetter.ToInt64():x} " +
                   $"guardFallback={_guardOnFallback} " +
                   $"guardedRelease={GuardedRelease} detectLines={_nhDetectLines}/{MaxNhDetectLines} " +
                   $"guardLines={_nhGuardLines}/{MaxNhGuardLines} watch={_watch.Count} lastOp={_lastOp}";
        }

        public static string HandleConsole(List<string> args)
        {
            // Outer try: a missing/unloadable EasyHook.dll would throw at JIT
            // time of Arm/Disarm (type refs in their bodies) — i.e. at THIS
            // call site, not inside their own try blocks.
            try
            {
                string a = args.Count > 0 ? args[0].Trim().ToLowerInvariant() : "status";
                if (a == "hook" || a == "guard")
                    _autoArmSuppressed = false;   // explicit arm re-enables EnsureArmed
                switch (a)
                {
                    case "scan":   return Scan();
                    case "hook":   return Arm(1);
                    case "guard":  return Arm(2);
                    case "off":    return Disarm();
                    case "status": return StatusLine();
                    default:
                        return "Usage: combatai.nativehook [scan|hook|guard|status|off]  " +
                               "(scan = locate+report only; hook = set_target_agent detection; " +
                               "guard = + mode-leaf crash guard; off = dispose all)";
                }
            }
            catch (Exception ex)
            {
                NHLog("[NH] ERROR handling nativehook command (EasyHook load failure?): " + ex);
                return "nativehook: failed — see [NH] log. " + StatusLine();
            }
        }

        // Auto-arm for free-target modes: idempotent -- scans + hooks + arms
        // the guard the first time a duel/spar mode needs it, so the user no
        // longer types nativehook commands. FAIL CLOSED: returns false when
        // anything can't resolve (the caller then runs the legacy locked
        // behavior). A manual `nativehook off` suppresses further auto-arms
        // until a mode explicitly arms again via the console commands.
        private static bool _autoArmFailed;
        private static bool _autoArmSuppressed;
        public static bool EnsureArmed()
        {
            try
            {
                if (ArmedLevel >= 2)
                    return true;
                if (_autoArmFailed || _autoArmSuppressed)
                    return false;
                Arm(2);
                if (ArmedLevel >= 2)
                    return true;
                _autoArmFailed = true;
                NHLog("[NH] EnsureArmed: guard could not arm (unresolved addresses or hook failure) -- free-target will run LEGACY locked.");
                return false;
            }
            catch (Exception ex)
            {
                _autoArmFailed = true;
                NHLog("[NH] ERROR in EnsureArmed (free-target stays LEGACY locked): " + ex);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Release-time poke + the ONLY sanctioned null-target write path.
        // Order (per research 12 / H2): poke {aiComp+0x19c = 0 (mode),
        // aiComp+0x1304 = -1 (goal override)} to close the stale-mode window
        // immediately, THEN queue the pending release via SetTargetAgent(null)
        // (legit pending->commit path, housekeeping included) and disable
        // native auto-retarget so it stays released.
        // ------------------------------------------------------------------
        public static bool ReleaseCombatTarget(Agent a)
        {
            _lastOp = "release-gate";
            try
            {
                if (!GuardedRelease)
                {
                    NHLog("[NH] ReleaseCombatTarget refused: nativehook not armed (combatai.nativehook hook|guard first).");
                    return false;
                }
                if (a == null || a.Mission == null || !a.IsActive() || a.Health <= 0f)
                    return false;
                _lastOp = "release-getptr";
                UIntPtr agentPtr = GetAgentPtr(a);
                if (agentPtr == UIntPtr.Zero)
                {
                    NHLog($"[NH] ReleaseCombatTarget: agent #{SafeIndex(a)} has null native ptr.");
                    return false;
                }
                _lastOp = "release-read";
                unsafe
                {
                    byte* aiComp = *(byte**)(agentPtr + AgentOffAiComp).ToPointer();
                    if (aiComp == null)
                    {
                        NHLog($"[NH] ReleaseCombatTarget: agent #{SafeIndex(a)} aiComp=NULL (not AI-controlled?).");
                        return false;
                    }
                    NHLog("[NH] release-pre  " + DumpFields(agentPtr, aiComp));
                    // THE POKE: kill direct/combat-move mode NOW (crash gate)
                    // and clear any stale goal-agent override.
                    *(int*)(aiComp + AiCompOffMode) = 0;
                    *(int*)(aiComp + AiCompOffGoal) = -1;
                    _lastOp = "release-poke";
                    NHLog("[NH] release-poke " + DumpFields(agentPtr, aiComp));
                }
                _lastOp = "release-SetTargetAgent";
                a.SetTargetAgent(null);
                _lastOp = "release-SetAutoTarget";
                a.SetAutomaticTargetSelection(false);
                _lastOp = "release-flags-readback";
                unsafe
                {
                    // Folded-shim verification (research 14 sec.7 candidate 2):
                    // set_automatic_target_selection(false) should latch
                    // aiComp+0x2114 bit 0x800 ("auto-select OFF"). If the bit
                    // is MISSING on the deployed build, the native scorer can
                    // still re-pick a target -> explains re-locks after combat
                    // contact (hit flag -> scorer repick, all 0x800-gated).
                    byte* aiComp2 = *(byte**)(agentPtr + AgentOffAiComp).ToPointer();
                    if (aiComp2 != null)
                    {
                        int fl = *(int*)(aiComp2 + AiCompOffFlags);
                        NHLog($"[NH] release-auto800 agent #{SafeIndex(a)} flags=0x{fl:x8} " +
                              $"auto800={(((uint)fl & 0x800u) != 0u ? "on" : "MISSING")}");
                    }
                }
                _lastOp = "release-mark";
                MarkReleased(a);
                NHLog($"[NH] ReleaseCombatTarget DONE agent #{SafeIndex(a)} (poked mode=0 goal=-1, SetTargetAgent(null), auto-select off).");
                return true;
            }
            catch (Exception ex)
            {
                NHLog($"[NH] ERROR in ReleaseCombatTarget at {_lastOp}: {ex}");
                return false;
            }
        }

        // internal (was private): shared by NativeSteerOverride's channel resolve.
        internal static UIntPtr GetAgentPtr(Agent a)
        {
            try
            {
                if (_agentGetPtr == null)
                    _agentGetPtr = AccessTools.Method(typeof(Agent), "GetPtr");
                if (_agentGetPtr == null) return UIntPtr.Zero;
                object r = _agentGetPtr.Invoke(a, null);
                return r is UIntPtr p ? p : UIntPtr.Zero;
            }
            catch { return UIntPtr.Zero; }
        }

        private static int SafeIndex(Agent a)
        {
            try { return a != null && a.Mission != null ? a.Index : -1; }
            catch { return -1; }
        }

        // Field dump shared by the detection detour and the release path —
        // this is what validates the offsets on the live build.
        private static unsafe string DumpFields(UIntPtr agentPtr, byte* aiComp)
        {
            int idx      = *(int*)(agentPtr + AgentOffIndex).ToPointer();
            int mount    = *(int*)(agentPtr + AgentOffMountIdx).ToPointer();
            int cmdTgt   = *(int*)(agentPtr + AgentOffCmdBlockTgt).ToPointer();
            int tgt      = *(int*)(aiComp + AiCompOffTarget);
            int pendF    = *(byte*)(aiComp + AiCompOffPendFlag);
            int pend     = *(int*)(aiComp + AiCompOffPendTarget);
            int goal     = *(int*)(aiComp + AiCompOffGoal);
            int mode     = *(int*)(aiComp + AiCompOffMode);
            int state    = *(int*)(aiComp + AiCompOffCombatState);
            byte* owner  = *(byte**)(aiComp + AiCompOffOwnerAgent);
            return $"agent#{idx} ptr=0x{agentPtr.ToUInt64():x} aiComp=0x{(long)aiComp:x} owner=0x{(long)owner:x} " +
                   $"tgt={tgt} pend={pendF}:{pend} goal={goal} mode={mode} combatState={state} mount={mount} cmd504={cmdTgt}";
        }

        // ------------------------------------------------------------------
        // Detour 1 — set_target_agent. Calls the original, then dumps the
        // native fields for EVERY call (bounded) — -1 releases AND real-idx
        // re-locks. ZERO mutation. The idx>=0 lines are what catch a managed-
        // side re-lock (ours via EnsureTargetAgent, another mod, or a vanilla
        // mission controller) that the old -1-only filter hid (research 14).
        //
        // Watched agents (released via ReleaseCombatTarget -> MarkReleased)
        // get extra attribution: every positive write is tagged RELOCK, and
        // the first MaxRelockStacksPerAgent writes also dump a bounded
        // managed stack (RELOCK-STACK) naming the managed caller. Runs on a
        // worker thread — Environment.StackTrace allocates, but this path is
        // strictly gated to watched agents and capped, so it stays rare.
        // ------------------------------------------------------------------
        private unsafe static void DetourSetTargetAgent(UIntPtr agentPtr, int targetIdx)
        {
            _callSetTarget(agentPtr, targetIdx);
            if (targetIdx >= 0)
            {
                WatchEntry w = FindWatch(agentPtr);
                if (w != null)
                {
                    try
                    {
                        byte* aiCompR = *(byte**)(agentPtr + AgentOffAiComp).ToPointer();
                        if (aiCompR == null)
                            NHLog($"[NH] set_target(#{targetIdx}) RELOCK ptr=0x{agentPtr.ToUInt64():x} aiComp=NULL");
                        else
                            NHLog($"[NH] set_target(#{targetIdx}) RELOCK " + DumpFields(agentPtr, aiCompR));
                        if (w.StackDumps < MaxRelockStacksPerAgent && _nhStackLines < MaxNhStackLines)
                        {
                            w.StackDumps++;
                            LogRelockStack(w.AgentIndex, targetIdx);
                        }
                    }
                    catch (Exception ex)
                    {
                        NHLog($"[NH] RELOCK detour read failed: {ex.GetType().Name} {ex.Message}");
                    }
                    return;
                }
            }
            if (_nhDetectLines >= MaxNhDetectLines)
                return;
            try
            {
                _nhDetectLines++;
                string tag = targetIdx < 0 ? "-1" : "#" + targetIdx;
                byte* aiComp = *(byte**)(agentPtr + AgentOffAiComp).ToPointer();
                if (aiComp == null)
                    NHLog($"[NH] set_target({tag}) ptr=0x{agentPtr.ToUInt64():x} aiComp=NULL");
                else
                    NHLog($"[NH] set_target({tag}) " + DumpFields(agentPtr, aiComp));
            }
            catch (Exception ex)
            {
                NHLog($"[NH] set_target detour read failed: {ex.GetType().Name} {ex.Message}");
            }
        }

        // Watch-set lookup by native agent record ptr. Runs off the main
        // thread while Poll/MarkReleased can mutate _watch — the iteration is
        // intentionally lock-free and failure-tolerant (a torn read just
        // misses a dump; this is a diagnostic path, and the callers' writes
        // are single-slot appends/removals).
        private static WatchEntry FindWatch(UIntPtr agentPtr)
        {
            try
            {
                for (int i = 0; i < _watch.Count; i++)
                    if (_watch[i].AgentPtr == agentPtr)
                        return _watch[i];
            }
            catch { }
            return null;
        }

        // Bounded RELOCK-STACK block: header + up to MaxStackFrames filtered
        // frames of the managed caller chain. Frame lines count against the
        // global _nhStackLines budget.
        private static void LogRelockStack(int agentIndex, int targetIdx)
        {
            NHLog($"[NH] RELOCK-STACK agent#{agentIndex} -> set_target(#{targetIdx}) via:");
            string raw = null;
            try { raw = Environment.StackTrace; }
            catch { }
            if (string.IsNullOrEmpty(raw))
            {
                NHLog("[NH]   <stack unavailable>");
                return;
            }
            string[] lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            int emitted = 0;
            for (int i = 0; i < lines.Length && emitted < MaxStackFrames && _nhStackLines < MaxNhStackLines; i++)
            {
                string s = lines[i].Trim();
                if (s.Length == 0 || IsStackNoise(s))
                    continue;
                _nhStackLines++;
                emitted++;
                NHLog("[NH]   " + s);
            }
            if (emitted == 0)
                NHLog("[NH]   <all frames filtered>");
        }

        // CLR-infrastructure + our-own frames carry no attribution value.
        private static bool IsStackNoise(string frame)
        {
            return frame.IndexOf("System.Environment", StringComparison.Ordinal) >= 0
                || frame.IndexOf("NativeTargetGuard", StringComparison.Ordinal) >= 0
                || frame.IndexOf("EasyHook", StringComparison.Ordinal) >= 0
                || frame.IndexOf("DomainHelper", StringComparison.Ordinal) >= 0
                || frame.IndexOf("RuntimeMethodHandle", StringComparison.Ordinal) >= 0
                || frame.IndexOf("RuntimeMethodInfo", StringComparison.Ordinal) >= 0
                || frame.IndexOf("System.Threading", StringComparison.Ordinal) >= 0
                || frame.IndexOf("ThreadStart", StringComparison.Ordinal) >= 0;
        }

        // ------------------------------------------------------------------
        // Detour 2 — the mode leaf (pathObj arg). Call original (it writes
        // pathObj+0x1c per the rule), then, if it set mode != 0 while the
        // effective combat target is < 0, force it back to 0. That is the
        // WHOLE crash guard: every AV site is gated on this flag.
        // ------------------------------------------------------------------
        private unsafe static void DetourModeLeaf(UIntPtr pathObj)
        {
            _callModeLeaf(pathObj);
            PostCallModeFix(pathObj, "leaf");
        }

        // Fallback detour — same post-call fixup on the goal setter itself
        // (pathObj, goalIdx args; the setter's tail call runs the leaf inline).
        private unsafe static void DetourGoalSetter(UIntPtr pathObj, int goalIdx)
        {
            _callGoalSetter(pathObj, goalIdx);
            PostCallModeFix(pathObj, "goalsetter");
        }

        private unsafe static void PostCallModeFix(UIntPtr pathObj, string from)
        {
            try
            {
                if (*(int*)(pathObj + PathObjOffMode).ToPointer() == 0)
                    return;                                  // already safe
                byte* aiComp = *(byte**)(pathObj + PathObjOffAiComp).ToPointer();
                if (aiComp == null)
                    return;
                int tgt = (*(byte*)(aiComp + AiCompOffPendFlag) != 0)
                    ? *(int*)(aiComp + AiCompOffPendTarget)
                    : *(int*)(aiComp + AiCompOffTarget);
                if (tgt >= 0)
                    return;                                  // valid target — leave native alone
                *(int*)(pathObj + PathObjOffMode).ToPointer() = 0;
                if (_nhGuardLines < MaxNhGuardLines)
                {
                    _nhGuardLines++;
                    int goal = *(int*)(pathObj + PathObjOffGoal).ToPointer();
                    int state = *(int*)(aiComp + AiCompOffCombatState);
                    // one-time self-check: pathObj+0x28 should equal pathObj-0x180
                    bool layoutOk = aiComp == (byte*)pathObj.ToPointer() - PathObjToAiCompDelta;
                    NHLog($"[NH] guard({from}) forced mode 0: tgt={tgt} goal={goal} state={state} " +
                          $"pathObj=0x{pathObj.ToUInt64():x} aiComp(+0x28)=0x{(long)aiComp:x} layoutOk={layoutOk}");
                }
            }
            catch (Exception ex)
            {
                if (_nhGuardLines < MaxNhGuardLines)
                {
                    _nhGuardLines++;
                    NHLog($"[NH] guard({from}) fixup failed: {ex.GetType().Name} {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------
        // Released-agent watchdog (research 14 sec.7 mitigations). Cheap,
        // zero hooks beyond what arm already installed: Poll() diffs the
        // native fields each tick; a transition names WHO re-locked when
        // cross-read against the set_target detour lines —
        //   set_target(#N) detour line          -> a MANAGED write (us/mod)
        //   pend flip with no detour line       -> native scorer (FUN_1806b69a0)
        //   goal flip with tgt still -1         -> +0x1304 goal-override (chase
        //                                          without lock — cosmetic)
        //   flags 0x800 clearing                -> auto-select re-enabled.
        // ------------------------------------------------------------------
        public static void MarkReleased(Agent a)
        {
            _lastOp = "watch-mark";
            try
            {
                if (a == null)
                    return;
                UIntPtr agentPtr = GetAgentPtr(a);
                if (agentPtr == UIntPtr.Zero)
                    return;
                for (int i = 0; i < _watch.Count; i++)
                {
                    if (_watch[i].AgentPtr != agentPtr)
                        continue;
                    // re-release: re-baseline so the fresh release cycle logs
                    _watch[i].Agent = a;
                    _watch[i].AgentIndex = SafeIndex(a);
                    _watch[i].HasSnapshot = false;
                    _watch[i].Lines = 0;
                    _watch[i].StackDumps = 0;
                    return;
                }
                if (_watch.Count >= MaxWatch)
                    _watch.RemoveAt(0);          // bounded set — drop oldest
                var e = new WatchEntry { Agent = a, AgentPtr = agentPtr, AgentIndex = SafeIndex(a) };
                unsafe
                {
                    byte* aiComp = *(byte**)(agentPtr + AgentOffAiComp).ToPointer();
                    if (aiComp != null)
                    {
                        e.Tgt   = *(int*)(aiComp + AiCompOffTarget);
                        e.PendF = *(byte*)(aiComp + AiCompOffPendFlag);
                        e.Pend  = *(int*)(aiComp + AiCompOffPendTarget);
                        e.Goal  = *(int*)(aiComp + AiCompOffGoal);
                        e.Flags = *(int*)(aiComp + AiCompOffFlags);
                        e.HasSnapshot = true;
                    }
                }
                _watch.Add(e);
                NHLog($"[NH] WATCH +agent#{e.AgentIndex} (set={_watch.Count}/{MaxWatch})");
            }
            catch (Exception ex)
            {
                NHLog($"[NH] ERROR in MarkReleased: {ex.GetType().Name} {ex.Message}");
            }
        }

        // Drop an agent from the watch set — called when we legitimately
        // re-lock it (EnsureTargetAgent non-null write) or it leaves the
        // mission. No-op when the agent isn't watched.
        public static void UnmarkReleased(Agent a)
        {
            try
            {
                if (a == null || _watch.Count == 0)
                    return;
                int idx = SafeIndex(a);
                for (int i = _watch.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_watch[i].Agent, a) || (idx >= 0 && _watch[i].AgentIndex == idx))
                    {
                        _watch.RemoveAt(i);
                        return;
                    }
                }
            }
            catch { }
        }

        // Mission end / DisableAllModes / Disarm: the release no longer needs
        // to hold — drop the whole set.
        public static void ClearWatch()
        {
            try { _watch.Clear(); } catch { }
        }

        // Per-tick poll, wired from CombatAiMissionBehavior.OnMissionTick.
        // Early-out is two field reads — safe to call every frame even when
        // the subsystem is off.
        public static void Poll()
        {
            if (ArmedLevel <= 0 || _watch.Count == 0)
                return;
            _lastOp = "watch-poll";
            try
            {
                for (int i = _watch.Count - 1; i >= 0; i--)
                {
                    WatchEntry e = _watch[i];
                    // Liveness check on the MANAGED side first — never touch
                    // the native record of a dead/removed agent.
                    Agent a = e.Agent;
                    if (a == null || a.Mission == null || !a.IsActive())
                    {
                        _watch.RemoveAt(i);
                        continue;
                    }
                    unsafe
                    {
                        byte* aiComp = *(byte**)(e.AgentPtr + AgentOffAiComp).ToPointer();
                        if (aiComp == null)
                        {
                            _watch.RemoveAt(i);
                            continue;
                        }
                        int tgt   = *(int*)(aiComp + AiCompOffTarget);
                        int pendF = *(byte*)(aiComp + AiCompOffPendFlag);
                        int pend  = *(int*)(aiComp + AiCompOffPendTarget);
                        int goal  = *(int*)(aiComp + AiCompOffGoal);
                        int flags = *(int*)(aiComp + AiCompOffFlags);
                        if (!e.HasSnapshot)
                        {
                            e.Tgt = tgt; e.PendF = pendF; e.Pend = pend; e.Goal = goal; e.Flags = flags;
                            e.HasSnapshot = true;
                            continue;
                        }
                        if (tgt != e.Tgt || pendF != e.PendF || pend != e.Pend || goal != e.Goal || flags != e.Flags)
                        {
                            if (e.Lines < MaxWatchLinesPerAgent)
                            {
                                e.Lines++;
                                NHLog($"[NH] WATCH agent#{e.AgentIndex} tgt:{e.Tgt}->{tgt} " +
                                      $"pend:{e.PendF}:{e.Pend}->{pendF}:{pend} goal:{e.Goal}->{goal} " +
                                      $"flags:0x{e.Flags:x}->0x{flags:x}");
                            }
                            e.Tgt = tgt; e.PendF = pendF; e.Pend = pend; e.Goal = goal; e.Flags = flags;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                NHLog($"[NH] ERROR in watch Poll: {ex.GetType().Name} {ex.Message}");
            }
        }

        // QUIET (lockthrash test): suppress the per-write NH spam when a test
        // cycles the slot dozens of times a second -- the flood itself drops
        // frames and buries the signal. Set/reset by the test mode.
        public static bool Quiet;
        private static void NHLog(string msg) { if (!Quiet) CombatAiLog.Log(msg); }
    }

    // =========================================================================
    // DEV NOTES — 1.4.8 verification (deployed TaleWorlds.Native.dll,
    // imagebase 0x180000000, .text rva 0x1000 size 0xa2b5dc):
    //
    //  * The mode leaf lives at RVA 0x6c2d50 and is 0x51 bytes — a byte-exact
    //    analog of 1.4.5's FUN_1806bfdd0. Its disassembly:
    //      83 b9 84 11 00 00 00   cmp dword [rcx+0x1184], 0     (goal)
    //      48 8b 41 28            mov rax, [rcx+0x28]           (aiComp)
    //      48 8b 10               mov rdx, [rax]                (owner agent)
    //      c7 41 1c 00 00 00 00   mov dword [rcx+0x1c], 0       (mode=0)
    //      7d 32                  jge -> mode=1                 (goal>=0)
    //      83 ba 5c 05 00 00 00   cmp dword [rdx+0x55c], 0      (mount idx)
    //      7d 30                  jge -> ret                    (mounted)
    //      80 b8 58 22 00 00 00   cmp byte  [rax+0x2258], 0     (pending flag)
    //      74 08 / 8b 90 5c 22 00 00 / eb 06 / 8b 90 04 15 00 00
    //                             select pending(0x225c) or committed(0x1504)
    //      85 d2 78 15            test edx,edx ; js -> ret      (target<0)
    //      8b 80 04 18 00 00      mov eax, [rax+0x1804]         (combat state)
    //      83 e8 07 83 f8 01 77 07   state-7 >1 unsigned -> ret (state!={7,8})
    //      c7 41 1c 01 00 00 00   mov dword [rcx+0x1c], 1       (mode=1)
    //      c3                     ret
    //    => every offset used here is CONFIRMED on the live build.
    //
    //  * The leaf is NOT in .pdata (leaf functions need no unwind info) —
    //    which is why the caller-chain works backward from the SETTER (which
    //    IS in .pdata: [0x6c1870,0x6c1a1d) contains both mgr+0x3238 compares
    //    at 0x6c189e/0x6c1946, the imul-by-0xc60 at 0x6c188d, the [r?+0x1184]
    //    store at 0x6c19f1, and its LAST `call rel32` at 0x6c19f8 -> 0x6c2d50).
    //
    //  * Offset assumptions still needing a runtime glance at [NH] output:
    //      - agent+0x10 = public index (log shows it next to cmd504 — sanity)
    //      - agent+0x504 = the command-block target copy (should track tgt)
    //      - pathObj+0x28 == pathObj-0x180 (logged as layoutOk on guard fires)
    // =========================================================================
}
