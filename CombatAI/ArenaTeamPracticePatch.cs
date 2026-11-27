using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CombatAI
{
    // =======================================================================
    // TEAM PRACTICE ALLY-SPAWN BLOCK: Harmony PREFIX on AO's internal
    // TeamPracticeController.SpwanAllyAgent -- the single funnel every
    // companion-ally spawn goes through (initial spawn AND wave respawns).
    // AO's Team practice fills the player's team with party companions; our
    // GroupFight spec wants the player ALONE on a neutral third team, so the
    // prefix returns false (skip original) only while our fight is armed or
    // pending. Normal AO team practice is untouched. Lazily installed once
    // via EnsureApplied() from BeginPractice(GroupFight5v5). MasterStrikes
    // pattern: no AO/Harmony types on the class surface, try/catch
    // everywhere, idempotent, never throws into the game.
    // =======================================================================
    internal static class ArenaTeamPracticePatch
    {
        private static bool _attempted;

        public static void EnsureApplied()
        {
            if (_attempted)
                return;
            _attempted = true;
            try
            {
                // TeamPracticeController is internal -- locate it through the
                // loaded assembly list so the exact AO assembly name/version
                // never appears in our code.
                Type ctrl = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    ctrl = asm.GetType("ArenaOverhaul.Managers.TeamPracticeController") ?? asm.GetType("ArenaOverhaul.ArenaPractice.TeamPracticeController");
                    if (ctrl != null)
                        break;
                }
                MethodInfo target = ctrl?.GetMethod("SpwanAllyAgent",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null)
                {
                    CombatAiLog.Log("[ARENA] team-practice patch NOT applied: SpwanAllyAgent not found (AO internals changed?).");
                    return;
                }
                ApplyPatch(target);
            }
            catch (Exception e)
            {
                CombatAiLog.Log($"[ARENA] team-practice patch NOT applied: {e.GetType().Name}: {e.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyPatch(MethodInfo target)
        {
            try
            {
                HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("combatai.arena.noallies");
                MethodInfo prefixMethod = typeof(ArenaTeamPracticePatch).GetMethod(nameof(AllySpawnPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(target, prefix: new HarmonyLib.HarmonyMethod(prefixMethod));
                CombatAiLog.Log("[ARENA] team-practice patch applied: SpwanAllyAgent prefix armed.");
            }
            catch (Exception e)
            {
                CombatAiLog.Log($"[ARENA] team-practice patch NOT applied: {e.GetType().Name}: {e.Message}");
            }
        }

        // false = skip the original -> the companion ally never spawns.
        // PendingArenaFight covers the spawn waves that run BEFORE our arm
        // tick; GroupFightMode covers anything after it.
        private static bool AllySpawnPrefix()
        {
            return CombatAiState.PendingArenaFight != CombatAiArenaKind.GroupFight5v5
                && !CombatAiState.GroupFightMode;
        }
    }
}
