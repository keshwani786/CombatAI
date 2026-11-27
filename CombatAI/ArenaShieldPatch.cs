using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace CombatAI
{
    // =======================================================================
    // ARENA SHIELD PATCH (Session 62 Part B): Harmony POSTFIX on
    // ArenaPracticeFightMissionController.AddRandomWeapons -- forces the
    // 'Spar (all shields)' loadout: Weapon0 1H melee, Weapon1 shield,
    // Weapon2/Weapon3 empty. Lazily installed once via EnsureApplied() from
    // BeginPractice(SparShield); only edits the `equipment` param, only when
    // CombatAiState.ArenaForceShields is on. MasterStrikes-pattern: types as
    // locals, try/catch everywhere, idempotent, never throws into the game.
    // =======================================================================
    internal static class ArenaShieldPatch
    {
        private static bool _attempted;
        private static int _spawnLogCount;
        private const int MaxSpawnLogs = 40;

        public static void EnsureApplied()
        {
            if (_attempted)
                return;
            _attempted = true;
            try
            {
                Type controller = typeof(SandBox.Missions.MissionLogics.Arena.ArenaPracticeFightMissionController);
                MethodInfo target = controller.GetMethod("AddRandomWeapons",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(Equipment), typeof(int) }, null);
                if (target == null)
                {
                    CombatAiLog.Log("[ARENA] shield patch NOT applied: AddRandomWeapons(Equipment,int) not found.");
                    return;
                }
                ApplyPatch(target);
            }
            catch (Exception e)
            {
                CombatAiLog.Log($"[ARENA] shield patch NOT applied: {e.GetType().Name}: {e.Message}");
            }
        }

        // Keeps every HarmonyLib-typed value LOCAL so a missing 0Harmony.dll
        // faults here only (MasterStrikes pattern). Harmony 2.4.2: Patch has
        // exactly five params.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyPatch(MethodInfo target)
        {
            try
            {
                HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("combatai.arena.shields");
                MethodInfo postfixMethod = typeof(ArenaShieldPatch).GetMethod(nameof(ShieldPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(target, postfix: new HarmonyLib.HarmonyMethod(postfixMethod));
                CombatAiLog.Log("[ARENA] shield patch applied: AddRandomWeapons postfix armed.");
            }
            catch (Exception e)
            {
                CombatAiLog.Log($"[ARENA] shield patch NOT applied: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void ShieldPostfix(Equipment equipment, int spawnIndex)
        {
            try
            {
                if (!CombatAiState.ArenaForceShields || equipment == null)
                    return;
                ItemObject melee = null, shield = null;
                bool keptMelee = false, keptShield = false;
                for (int s = 0; s < 4; s++)
                {
                    EquipmentElement el = equipment[(EquipmentIndex)s];
                    ItemObject it = el.Item;
                    if (it == null)
                        continue;
                    if (melee == null && it.Type == ItemObject.ItemTypeEnum.OneHandedWeapon)
                    {
                        melee = it;
                        keptMelee = true;
                    }
                    if (shield == null && it.Type == ItemObject.ItemTypeEnum.Shield)
                    {
                        shield = it;
                        keptShield = true;
                    }
                }
                if (melee == null)
                    melee = PickItem(ItemObject.ItemTypeEnum.OneHandedWeapon);
                if (shield == null)
                    shield = PickItem(ItemObject.ItemTypeEnum.Shield);
                if (melee == null || shield == null)
                {
                    CombatAiLog.Log($"[ARENA] shield loadout SKIPPED spawnIndex={spawnIndex}: item list empty (melee={(melee != null)} shield={(shield != null)}).");
                    return;
                }
                equipment[EquipmentIndex.Weapon0] = new EquipmentElement(melee);
                equipment[EquipmentIndex.Weapon1] = new EquipmentElement(shield);
                equipment[EquipmentIndex.Weapon2] = EquipmentElement.Invalid;
                equipment[EquipmentIndex.Weapon3] = EquipmentElement.Invalid;
                if (_spawnLogCount < MaxSpawnLogs)
                {
                    _spawnLogCount++;
                    CombatAiLog.Log($"[ARENA] shield loadout spawnIndex={spawnIndex} weapon={melee.StringId} shield={shield.StringId} " +
                        $"({(keptMelee ? "kept" : "picked")} {(keptShield ? "kept" : "picked")})");
                }
            }
            catch (Exception e)
            {
                CombatAiLog.Log($"[ARENA] shield postfix error: {e.GetType().Name}: {e.Message}");
            }
        }

        private static ItemObject PickItem(ItemObject.ItemTypeEnum type)
        {
            try
            {
                string culture = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement?.Culture?.StringId;
                List<ItemObject> pref = new List<ItemObject>();
                List<ItemObject> any = new List<ItemObject>();
                foreach (ItemObject it in MBObjectManager.Instance.GetObjectTypeList<ItemObject>())
                {
                    if (it == null || it.Type != type || it.Tierf > 3.5f || it.NotMerchandise)
                        continue;
                    (culture != null && it.Culture?.StringId == culture ? pref : any).Add(it);
                }
                List<ItemObject> pool = pref.Count > 0 ? pref : any;
                return pool.Count > 0 ? pool[MBRandom.RandomInt(pool.Count)] : null;
            }
            catch { return null; }
        }
    }
}
