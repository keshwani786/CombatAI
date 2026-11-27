using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // Session 28: direct Harmony patch replacing the AIDodgeChanceMultiplier
    // reflection trick (MasterStrikesCompat) for voiding MasterStrikes' own
    // AI dodge -- the user asked for the more robust option now that Harmony
    // is an acceptable dependency. This guarantees MasterStrikes.
    // AIDodgeComponent.OnTick never runs at all, regardless of its settings
    // state (immune to a future MasterStrikes version renaming/removing the
    // AIDodgeChanceMultiplier property) -- CombatAI's own TryStartDodge
    // becomes the sole source of AI dodging, with channel 0 fully ours.
    //
    // Zero risk to the PLAYER's own manual (gamepad-bound) dodge:
    // AIDodgeComponent.OnTick already early-returns on `Agent.IsMainAgent`
    // before doing anything (decompiled source) -- it never drove the
    // player's dodge in the first place, so skipping the whole method has no
    // observable effect on it. The player's dodge is a separate code path in
    // MasterStrikesBehavior triggered directly by input, untouched here.
    //
    // Session 50 note: our spam counter (MasterStrikesBridge) calls the
    // MasterStrikeLogic/ClinchLogic constructors directly -- unaffected by
    // these patches by design.
    //
    // Session 32 extension (settings void proved insufficient -- the user saw
    // an AI agent master-strike with EnableMasterStrikes=false asserted, i.e.
    // the in-memory write can be overwritten by an MCM reload at read time):
    // the same direct-patch approach now also covers AIClinchComponent.OnTick
    // (AI clinches -- previously never patched at all) and
    // MasterStrikesBehavior.OnAgentHit (the AI master-strike trigger; the
    // prefix skips only when the performer `affectedAgent` isn't the main
    // agent, so the player's own keyed master strike keeps working -- same
    // asymmetry as the dodge). The settings void stays on as a fallback layer.
    //
    // Deliberately keeps every HarmonyLib-typed value LOCAL to this method
    // (no class-level fields of type Harmony/HarmonyMethod/etc.) so that if
    // 0Harmony.dll isn't loaded (MasterStrikes/Bannerlord.Harmony not
    // installed), resolving those types only fails inside this try/catch --
    // it can never prevent the rest of CombatAI.dll from loading or crash
    // SubModule initialization. The caller wraps this call in its own
    // try/catch too, as a second layer of the same safety.
    internal static class MasterStrikesHarmonyPatches
    {
        private static bool _attempted;

        // One patch target per AI system we void: the dodge component (channel-0
        // contention), the clinch component (was never patched before -- AI
        // clinches stayed live), and MasterStrikesBehavior.OnAgentHit for AI
        // master strikes (its performer is `affectedAgent`; IsMainAgent there
        // is the player's own keyed path -- skipping only AI performers keeps
        // the player's master strike working, same asymmetry as the dodge).
        public static void ApplyOnce()
        {
            if (_attempted)
                return;
            try
            {
                Type dodgeType = null, clinchType = null, behaviorType = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (dodgeType == null)
                        dodgeType = SafeGetType(asm, "MasterStrikes.AIDodgeComponent");
                    if (clinchType == null)
                        clinchType = SafeGetType(asm, "MasterStrikes.AIClinchComponent");
                    if (behaviorType == null)
                        behaviorType = SafeGetType(asm, "MasterStrikes.MasterStrikesBehavior");
                    if (dodgeType != null && clinchType != null && behaviorType != null)
                        break;
                }
                if (dodgeType == null && clinchType == null && behaviorType == null)
                    return; // MasterStrikes not loaded yet -- do NOT latch,
                            // retry on the next mission init (the old code
                            // latched _attempted here, so one early call with
                            // MasterStrikes not yet loaded meant we never
                            // patched it all session).
                // From here on MasterStrikes IS loaded -- one attempt is the
                // right count (a Harmony patch persists process-wide, and a
                // failure would recur identically every mission).
                _attempted = true;
                CombatAiState.MasterStrikesDetected = true;

                Harmony harmony = new Harmony("CombatAI.VoidMasterStrikesAI");
                MethodInfo skipOnTick = typeof(MasterStrikesHarmonyPatches).GetMethod(
                    nameof(SkipOnTick), BindingFlags.NonPublic | BindingFlags.Static);

                int applied = 0;
                if (PatchOnTick(harmony, dodgeType, skipOnTick, "AIDodgeComponent"))
                {
                    applied++;
                    CombatAiState.MasterStrikesHarmonyApplied = true;
                }
                if (PatchOnTick(harmony, clinchType, skipOnTick, "AIClinchComponent"))
                    applied++;
                if (PatchAgentHit(harmony, behaviorType))
                    applied++;
                CombatAiState.MasterStrikesPatchesApplied = applied;

                CombatAiLog.Log($"[MASTERSTRIKES] Harmony patches applied {applied}/3 -- AI dodge/clinch/master-strike " +
                    "triggers are now no-ops for AI agents (player's manual dodge + keyed master strike + clinch " +
                    "all untouched). CombatAI owns AI combat triggering.");
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[MASTERSTRIKES] Harmony patch failed (non-fatal -- AI voiding falls back to " +
                    $"the settings-based multiplier overrides if that ran): {ex.Message}");
            }
        }

        private static bool PatchOnTick(Harmony harmony, Type componentType, MethodInfo skipOnTick, string label)
        {
            try
            {
                if (componentType == null)
                {
                    CombatAiLog.Log($"[MASTERSTRIKES] {label} type not found -- skipping its patch.");
                    return false;
                }
                MethodInfo original = componentType.GetMethod("OnTick", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(float) }, null);
                if (original == null)
                {
                    CombatAiLog.Log($"[MASTERSTRIKES] {label}.OnTick(float) not found -- skipping its patch.");
                    return false;
                }
                harmony.Patch(original, prefix: new HarmonyMethod(skipOnTick));
                return true;
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[MASTERSTrikes] {label} patch failed (non-fatal): {ex.Message}");
                return false;
            }
        }

        private static bool PatchAgentHit(Harmony harmony, Type behaviorType)
        {
            try
            {
                if (behaviorType == null)
                {
                    CombatAiLog.Log("[MASTERSTRIKES] MasterStrikesBehavior type not found -- skipping the AI master-strike patch.");
                    return false;
                }
                MethodInfo original = behaviorType.GetMethod("OnAgentHit",
                    BindingFlags.Public | BindingFlags.Instance);
                if (original == null)
                {
                    CombatAiLog.Log("[MASTERSTRIKES] MasterStrikesBehavior.OnAgentHit not found -- skipping its patch.");
                    return false;
                }
                MethodInfo prefix = typeof(MasterStrikesHarmonyPatches).GetMethod(
                    nameof(SkipOnAgentHitForAI), BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                return true;
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[MASTERSTRIKES] OnAgentHit patch failed (non-fatal): {ex.Message}");
                return false;
            }
        }

        private static Type SafeGetType(Assembly asm, string fullName)
        {
            try { return asm.GetType(fullName); }
            catch { return null; }
        }

        // Harmony prefix convention: any bool-returning prefix controls
        // whether the original method body runs -- false skips it entirely.
        // __instance is untyped (object) since we never reference
        // MasterStrikes.AIDodgeComponent at compile time.
        private static bool SkipOnTick(object __instance, float dt)
        {
            return false;
        }

        // MasterStrikesBehavior.OnAgentHit master-strike trigger: `affectedAgent`
        // is the agent PERFORMING the master strike (the defender reacting to a
        // landed blow), and the IsMainAgent branch inside is the player's own
        // keyed master strike. Skipping whenever the performer is AI voids all
        // AI master strikes -- vs the player AND vs other AI -- while leaving
        // the player's own keyed path intact. Signature note: Harmony injects
        // `affectedAgent` by name; the `in` params we don't declare are simply
        // ignored.
        private static bool SkipOnAgentHitForAI(Agent affectedAgent)
        {
            return affectedAgent == null || affectedAgent.IsMainAgent;
        }
    }
}
