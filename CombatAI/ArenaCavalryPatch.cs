using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace CombatAI
{
    // =======================================================================
    // ARENA CAVALRY PATCH (cav duel carrier): while CombatAiState.
    // ArenaForceMounts is on, every practice fighter -- the player AND the
    // AI -- spawns mounted with a couch lance. Three small Harmony hooks,
    // verified against the vanilla + AO decompiles:
    //
    //   1. POSTFIX ArenaPracticeFightMissionController.AddRandomWeapons
    //      (Equipment,int): lance in Weapon0, Weapon1..3 empty, Horse +
    //      HorseHarness filled. Covers BOTH spawn paths -- vanilla
    //      SpawnArenaAgent calls it directly, AO's TeamPracticeController.
    //      SpawnArenaAgentInternal calls it through deAddRandomWeapons.
    //      AddRandomClothes afterwards only writes slots 5..9 (it skips
    //      Horse(10)/HorseHarness(11)), so these writes survive.
    //   2. PREFIX AgentBuildData.NoHorses(bool): forces the arg false ONLY
    //      while _insideFighterSpawn > 0 -- the bracket set by our prefix on
    //      SpawnArenaAgent (vanilla AND AO's public SpawnArenaAgent, which
    //      also calls NoHorses inside the same window). Postfix+finalizer
    //      close the bracket even on exceptions. The tournament master,
    //      guards, townsmen and the pre-practice player spawn through other
    //      funnels -- never inside the bracket, so untouched.
    //   3. POSTFIX SpawnArenaAgent: [ARENA] cav spawn log with the live
    //      result (mounted/horse id) -- the runtime proof the hooks landed.
    //
    // Lazily installed once via EnsureApplied() from BeginPractice(CavDuel).
    // MasterStrikes pattern: HarmonyLib types local, try/catch everywhere,
    // idempotent, never throws into the game, fail-closed logging.
    // =======================================================================
    internal static class ArenaCavalryPatch
    {
        // Picked from SandBoxCore/ModuleData/items/: t3_khuzait_horse is the
        // best maneuver+speed regular war-horse (maneuver=77, speed=60,
        // Type="Horse"); chain_barding a plain war harness; vlandia_lance_1_t3
        // _blunt = "Sparring Lance" (arena-blunted CraftedItem built on
        // spear_handle_23, the Light Lance handle -- the couch-lance family;
        // its generated item_usage is logged per spawn to verify).
        private const string HorseId = "t3_khuzait_horse";
        private const string HarnessId = "chain_barding";
        private const string LanceId = "vlandia_lance_1_t3_blunt";
        private const string LanceFallbackId = "empire_lance_1_t3_blunt";

        private static bool _attempted;
        private static int _insideFighterSpawn;    // bracket counter for the NoHorses gate
        private static ItemObject _horse, _harness, _lance;
        private static int _equipLogCount, _spawnLogCount, _noHorseLogCount;
        private const int MaxEquipLogs = 12, MaxSpawnLogs = 40, MaxNoHorseLogs = 12;

        public static void EnsureApplied()
        {
            if (_attempted)
                return;
            _attempted = true;
            try
            {
                Type controller = typeof(SandBox.Missions.MissionLogics.Arena.ArenaPracticeFightMissionController);
                MethodInfo weapons = controller.GetMethod("AddRandomWeapons",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(Equipment), typeof(int) }, null);
                MethodInfo spawn = controller.GetMethod("SpawnArenaAgent",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(Team), typeof(MatrixFrame) }, null);
                MethodInfo noHorses = typeof(AgentBuildData).GetMethod("NoHorses",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(bool) }, null);
                // 1.5.7 added two optional params (Equipment, ItemObject); 1.4.x had
                // only (AgentBuildData, bool). GetMethod with an exact param list needs
                // the FULL list, so try the 1.5.7 shape first, fall back to legacy.
                MethodInfo spawnAgent = typeof(Mission).GetMethod("SpawnAgent",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(AgentBuildData), typeof(bool), typeof(Equipment), typeof(ItemObject) }, null)
                    ?? typeof(Mission).GetMethod("SpawnAgent",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(AgentBuildData), typeof(bool) }, null);
                MethodInfo aoSpawn = FindAoSpawn();
                if (weapons == null || spawn == null || noHorses == null || spawnAgent == null)
                {
                    CombatAiLog.Log($"[ARENA] cavalry patch NOT applied: missing targets " +
                        $"weapons={(weapons != null)} spawn={(spawn != null)} noHorses={(noHorses != null)} spawnAgent={(spawnAgent != null)}.");
                    return;
                }
                ApplyPatches(weapons, spawn, noHorses, spawnAgent, aoSpawn);
            }
            catch (Exception e)
            {
                CombatAiLog.Log($"[ARENA] cavalry patch NOT applied: {e.GetType().Name}: {e.Message}");
            }
        }

        // AO's TeamPracticeController.SpawnArenaAgent -- the TEAM carrier's
        // fighter funnel (public static, returns bool, out Agent). CavDuel
        // runs on the Parry carrier so the vanilla bracket already covers it;
        // this is belt&braces so the mounts also work on the team path.
        // Reflection keeps the internal AO type name out of our signature.
        private static MethodInfo FindAoSpawn()
        {
            try
            {
                Type ctrl = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    ctrl = asm.GetType("ArenaOverhaul.Managers.TeamPracticeController") ?? asm.GetType("ArenaOverhaul.ArenaPractice.TeamPracticeController");
                    if (ctrl != null)
                        break;
                }
                return ctrl?.GetMethod("SpawnArenaAgent",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[]
                    {
                        typeof(SandBox.Missions.MissionLogics.Arena.ArenaPracticeFightMissionController),
                        typeof(Team), typeof(MatrixFrame), typeof(Agent).MakeByRefType()
                    }, null);
            }
            catch { return null; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyPatches(MethodInfo weapons, MethodInfo spawn, MethodInfo noHorses, MethodInfo spawnAgent, MethodInfo aoSpawn)
        {
            try
            {
                HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("combatai.arena.cavalry");
                Type self = typeof(ArenaCavalryPatch);
                MethodInfo Fix(string name) => self.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(weapons,
                    postfix: new HarmonyLib.HarmonyMethod(Fix(nameof(CavWeaponsPostfix))));
                harmony.Patch(spawn,
                    prefix: new HarmonyLib.HarmonyMethod(Fix(nameof(SpawnBracketPrefix))),
                    postfix: new HarmonyLib.HarmonyMethod(Fix(nameof(SpawnBracketPostfix))),
                    finalizer: new HarmonyLib.HarmonyMethod(Fix(nameof(SpawnBracketFinalizer))));
                harmony.Patch(noHorses,
                    prefix: new HarmonyLib.HarmonyMethod(Fix(nameof(NoHorsesPrefix))));
                // ai46: the NoHorses prefix never fired -- the tiny method is
                // JIT-inlined at the build-data call sites. Mutate the build
                // data ourselves at the Mission.SpawnAgent funnel instead:
                // every arena fighter spawn passes through it inside the
                // bracket, and NoHorses(false) is a plain call on the arg.
                harmony.Patch(spawnAgent,
                    prefix: new HarmonyLib.HarmonyMethod(Fix(nameof(SpawnAgentMountPrefix))));
                CombatAiLog.Log("[ARENA] cavalry patch applied: AddRandomWeapons postfix + SpawnArenaAgent bracket + NoHorses prefix armed.");
                if (aoSpawn != null)
                {
                    harmony.Patch(aoSpawn,
                        prefix: new HarmonyLib.HarmonyMethod(Fix(nameof(SpawnBracketPrefix))),
                        postfix: new HarmonyLib.HarmonyMethod(Fix(nameof(AoSpawnBracketPostfix))),
                        finalizer: new HarmonyLib.HarmonyMethod(Fix(nameof(SpawnBracketFinalizer))));
                    CombatAiLog.Log("[ARENA] cavalry patch: AO TeamPracticeController.SpawnArenaAgent bracket armed.");
                }
                else
                {
                    CombatAiLog.Log("[ARENA] cavalry patch: AO TeamPracticeController.SpawnArenaAgent not found -- vanilla bracket only.");
                }
            }
            catch (Exception e)
            {
                CombatAiLog.Log($"[ARENA] cavalry patch NOT applied: {e.GetType().Name}: {e.Message}");
            }
        }

        // ---- equipment hook ----
        private static void CavWeaponsPostfix(Equipment equipment, int spawnIndex)
        {
            try
            {
                if (!CombatAiState.ArenaForceMounts || equipment == null)
                    return;
                ResolveItems();
                if (_lance == null || _horse == null)
                {
                    if (_equipLogCount < MaxEquipLogs)
                    {
                        _equipLogCount++;
                        CombatAiLog.Log($"[ARENA] cavalry loadout SKIPPED spawnIndex={spawnIndex}: item ids unresolved " +
                            $"horse=({HorseId} {(_horse != null ? "ok" : "MISSING")}) lance=({LanceId} {(_lance != null ? "ok" : "MISSING")}).");
                    }
                    return;
                }
                equipment[EquipmentIndex.Weapon0] = new EquipmentElement(_lance);
                equipment[EquipmentIndex.Weapon1] = EquipmentElement.Invalid;
                equipment[EquipmentIndex.Weapon2] = EquipmentElement.Invalid;
                equipment[EquipmentIndex.Weapon3] = EquipmentElement.Invalid;
                equipment[EquipmentIndex.Horse] = new EquipmentElement(_horse);
                if (_harness != null)
                    equipment[EquipmentIndex.HorseHarness] = new EquipmentElement(_harness);
                if (_equipLogCount < MaxEquipLogs)
                {
                    _equipLogCount++;
                    CombatAiLog.Log($"[ARENA] cavalry loadout spawnIndex={spawnIndex} " +
                        $"lance={_lance.StringId}(usage={(_lance.PrimaryWeapon != null ? _lance.PrimaryWeapon.ItemUsage : "?")}) " +
                        $"horse={_horse.StringId} harness={(_harness != null ? _harness.StringId : "-")}.");
                }
            }
            catch (Exception e)
            {
                CombatAiLog.Log($"[ARENA] cavalry weapons postfix error: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void ResolveItems()
        {
            try
            {
                if (_horse == null)
                    _horse = MBObjectManager.Instance.GetObject<ItemObject>(HorseId);
                if (_harness == null)
                    _harness = MBObjectManager.Instance.GetObject<ItemObject>(HarnessId);
                if (_lance == null)
                {
                    _lance = MBObjectManager.Instance.GetObject<ItemObject>(LanceId);
                    if (_lance == null)
                        _lance = MBObjectManager.Instance.GetObject<ItemObject>(LanceFallbackId);
                }
            }
            catch { /* leave unresolved -> fail-closed log above */ }
        }

        // ---- spawn bracket (vanilla + AO share the handlers) ----
        private static void SpawnBracketPrefix()
        {
            _insideFighterSpawn++;
        }

        private static void SpawnBracketPostfix(Agent __result)
        {
            if (_insideFighterSpawn > 0)
                _insideFighterSpawn--;
            LogSpawn(__result);
        }

        // AO's method returns bool with `out Agent agent` -- bind the out
        // param by name for the same log line.
        private static void AoSpawnBracketPostfix(Agent agent)
        {
            if (_insideFighterSpawn > 0)
                _insideFighterSpawn--;
            LogSpawn(agent);
        }

        private static Exception SpawnBracketFinalizer(Exception __exception)
        {
            if (_insideFighterSpawn > 0)
                _insideFighterSpawn--;
            return __exception;   // never swallow -- only close the bracket
        }

        private static void LogSpawn(Agent a)
        {
            try
            {
                if (!CombatAiState.ArenaForceMounts || a == null || _spawnLogCount >= MaxSpawnLogs)
                    return;
                _spawnLogCount++;
                CombatAiLog.Log($"[ARENA] cav spawn: #{a.Index} horse={HorseId} weapon={LanceId} " +
                    $"mounted={a.HasMount} ai={(a.IsAIControlled ? 1 : 0)}");
            }
            catch { /* logging must never fault the spawn */ }
        }

        // ---- Mission.SpawnAgent funnel: flip the build data's horse flag ----
        private static void SpawnAgentMountPrefix(AgentBuildData agentBuildData)
        {
            try
            {
                if (!CombatAiState.ArenaForceMounts || _insideFighterSpawn <= 0 || agentBuildData == null)
                    return;
                agentBuildData.NoHorses(false);
                if (_noHorseLogCount < MaxNoHorseLogs)
                {
                    _noHorseLogCount++;
                    CombatAiLog.Log("[ARENA] cavalry: build data NoHorses -> false at Mission.SpawnAgent.");
                }
            }
            catch { /* a missed flip just means an infantry fighter */ }
        }

        // ---- NoHorses gate ----
        private static void NoHorsesPrefix(ref bool noHorses)
        {
            try
            {
                if (!CombatAiState.ArenaForceMounts || _insideFighterSpawn <= 0 || !noHorses)
                    return;
                noHorses = false;
                if (_noHorseLogCount < MaxNoHorseLogs)
                {
                    _noHorseLogCount++;
                    CombatAiLog.Log("[ARENA] cavalry: NoHorses(true) -> false inside fighter spawn.");
                }
            }
            catch { /* a missed flip just means an infantry fighter */ }
        }
    }
}
