using System;
using System.Reflection;

namespace CombatAI
{
    // Session 27: the user wants to import ONLY MasterStrikes' dodge
    // animation clips (act_1h/2h/other_2h_*_dodge, resolved at runtime via
    // ActionIndexCache.Create in CombatAiOverrideComponent -- module-level
    // assets, loaded regardless of whether MasterStrikes' own C# logic runs)
    // while CombatAI owns ALL dodge triggering, and MasterStrikes' own Master
    // Strike (parry/riposte) and Clinch systems are voided entirely so they
    // can't fight our combat FSM for control of an agent mid-duel.
    // Session 50: our spam counter calls MasterStrikeLogic/ClinchLogic
    // DIRECTLY (MasterStrikesBridge, reflection) -- these voids only stop
    // MasterStrikes' own AI-initiated triggers, not ours.
    //
    // MasterStrikes exposes exactly the toggles we need on its own MCM
    // settings object (MasterStrikes.ModSettings, decompiled):
    //   EnableMasterStrikes, EnableClinch -- public bool properties voided
    //   outright (both AI and player lose these two features). No asymmetry
    //   to work around here (user is fine voiding both for AI and player),
    //   so the settings route stays primary for these two -- a Harmony patch
    //   would add risk for no benefit (the AI trigger for Master Strike is
    //   inline inside a larger hit-reaction method in MasterStrikesBehavior,
    //   not a clean isolated entry point).
    //   Dodge is different: "EnableDodges" is a SHARED flag ("neither AI nor
    //   player can dodge" per its own hint text), so it can't kill only the
    //   AI's dodge without also killing the player's manual (gamepad-bound)
    //   one, which the user wants to keep. Session 27 used
    //   "AIDodgeChanceMultiplier" (AI-only -- the player's manual dodge is a
    //   direct button press, no chance roll) = 0 to zero the roll instead.
    //   Session 28: a direct Harmony prefix on AIDodgeComponent.OnTick
    //   (MasterStrikesHarmonyPatches.cs) is now the PRIMARY mechanism for
    //   voiding AI dodge -- guaranteed regardless of any settings value,
    //   immune to a future property rename. The AIDodgeChanceMultiplier=0
    //   override below is KEPT as a fallback layer: if the Harmony patch
    //   ever fails to bind (e.g. a MasterStrikes update changes OnTick's
    //   signature), this still neutralizes almost all AI dodging on its own.
    //   Either way channel 0 stays exclusively ours for AI agents -- the
    //   Session 26 "yield to MasterStrikes" workaround remains unnecessary
    //   and stays removed from CombatAiOverrideComponent.
    //
    // No project reference to MasterStrikes or MCM.Abstractions is added --
    // both types are located purely via reflection over already-loaded
    // assemblies, so this is a no-op (not an error) if MasterStrikes isn't
    // installed. The override is applied to the IN-MEMORY singleton only --
    // MasterStrikes' own saved MCM config file on disk is never touched, so
    // if CombatAI is ever removed, MasterStrikes returns to the user's own
    // settings next launch without any manual re-enabling.
    internal static class MasterStrikesCompat
    {
        private static bool _announced;

        // Called every mission init (OnBehaviorInitialize). Deliberately NOT
        // latched to a single attempt: MCM's GlobalSettings<T>.Instance is
        // lazily initialized and can return null at the first mission (the
        // old _attempted latch meant the void silently NEVER ran in that
        // case -- the "had to disable MasterStrikes manually" report), and a
        // later MCM settings reload can overwrite our in-memory void.
        // Re-asserting each mission heals both; the setters only report a
        // real change so unchanged-value rewrites don't spam the log.
        public static void VoidMasterStrikesFeatures()
        {
            try
            {
                Type settingsType = null;
                Type globalSettingsOpenType = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (settingsType == null)
                        settingsType = SafeGetType(asm, "MasterStrikes.ModSettings");
                    if (globalSettingsOpenType == null)
                        globalSettingsOpenType = SafeGetType(asm, "MCM.Abstractions.Base.Global.GlobalSettings`1");
                    if (settingsType != null && globalSettingsOpenType != null)
                        break;
                }
                if (settingsType != null)
                    CombatAiState.MasterStrikesDetected = true;
                if (settingsType == null || globalSettingsOpenType == null)
                    return; // MasterStrikes (or MCM) not loaded -- nothing to void

                Type closedGlobalSettings = globalSettingsOpenType.MakeGenericType(settingsType);
                PropertyInfo instanceProp = closedGlobalSettings.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                object instance = instanceProp?.GetValue(null);
                if (instance == null)
                    return; // MCM settings not initialized yet -- retry next mission

                bool changed = false;
                changed |= SetBoolIfPresent(instance, settingsType, "EnableMasterStrikes", false);
                changed |= SetBoolIfPresent(instance, settingsType, "EnableClinch", false);
                // EnableDodges deliberately left untouched -- it's shared with
                // the player's own manual (gamepad-bound) dodge, which the
                // user wants to keep working.
                changed |= SetFloatIfPresent(instance, settingsType, "AIDodgeChanceMultiplier", 0f);
                changed |= SetFloatIfPresent(instance, settingsType, "AIMasterStrikeChanceMultiplier", 0f);
                changed |= SetFloatIfPresent(instance, settingsType, "AIClinchChanceMultiplier", 0f);

                // Reached the live settings object and asserted our values --
                // report voided even when `changed` is false (the user's own
                // MCM config may already have them off; the state is voided
                // either way).
                CombatAiState.MasterStrikesSettingsVoided = true;

                if (changed)
                {
                    CombatAiLog.Log(_announced
                        ? "[MASTERSTRIKES] Settings values drifted back (MCM reload?) -- re-asserted " +
                          "EnableMasterStrikes/EnableClinch=false and AI chance multipliers=0."
                        : "[MASTERSTRIKES] Detected -- voided EnableMasterStrikes/EnableClinch and zeroed the three " +
                          "AI chance multipliers for this session (in-memory only, MasterStrikes' own saved MCM config " +
                          "untouched). CombatAI now owns all AI combat triggering; MasterStrikes' dodge clip assets remain " +
                          "usable via ActionIndexCache; the player's own manual dodge (EnableDodges) is left alone.");
                    _announced = true;
                }
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[MASTERSTRIKES] Compat override failed (non-fatal, MasterStrikes features may remain active): {ex.Message}");
            }
        }

        private static Type SafeGetType(Assembly asm, string fullName)
        {
            try { return asm.GetType(fullName); }
            catch { return null; }
        }

        // Returns true only when the property existed AND its value actually
        // changed -- re-assert passes over already-correct values report false
        // so the caller's `changed` flag (and log) reflects real work.
        private static bool SetBoolIfPresent(object instance, Type type, string propName, bool value)
        {
            PropertyInfo p = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanRead && p.CanWrite && p.PropertyType == typeof(bool)
                && (bool)p.GetValue(instance) != value)
            {
                p.SetValue(instance, value);
                return true;
            }
            return false;
        }

        private static bool SetFloatIfPresent(object instance, Type type, string propName, float value)
        {
            PropertyInfo p = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanRead && p.CanWrite && p.PropertyType == typeof(float)
                && (float)p.GetValue(instance) != value)
            {
                p.SetValue(instance, value);
                return true;
            }
            return false;
        }
    }
}
