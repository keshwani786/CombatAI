using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SandBox.Missions.MissionLogics.Arena;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Settlements.Locations;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // Which CombatAI fight the arena menu should arm for the next practice
    // mission. Written by the menu consequence (campaign map), consumed by
    // CombatAiMissionBehavior once the arena practice participants exist.
    public enum CombatAiArenaKind
    {
        None,
        Duel,        // 1v1 -- the duel FSM drives the opponent (combatai.duel without the console)
        Spar,        // 2 AI -- existing spar rig, roles auto-assigned (no combatai.spar <ids>)
        Defend2v1,   // 2 AI attackers vs player defender -- coordinated via ArenaDuoCoordinator
        SparShield,  // Spar with every AI participant spawned with 1H weapon + shield
        GroupFight5v5, // 10 shielded AI on two mutually-hostile AI teams via the TEAM-practice
                      // carrier; the player is alone on a third team, neutralized at arm
        CavDuel      // mounted 1v1: BOTH fighters spawn with horse + sparring lance
                      // (ArenaCavalryPatch), the cav planner drives the AI
    }

    // ArenaOverhaul bridge. The Redux fork obfuscates class names and makes its
    // settings internal (ModSettings/MCMSettings are internal classes), so every
    // AO touch goes through reflection resolved by NAME at runtime. When AO is not
    // installed/loaded, IsAoAvailable() returns false and every feature fails
    // closed with a log line -- nothing faults the module load. The arena *menu*
    // entries are gated on IsAoAvailable() by CombatAiArenaBehavior, so without AO
    // the mod simply doesn't offer arena practice modes.
    public static class ArenaOverhaulBridge
    {
        private static FieldInfo _fiEnteredFromMenu;
        private static PropertyInfo _piPrePractice;
        private static MethodInfo _miPrePracticeSetter;
        private static FieldInfo _fiParticipantAgents;
        private static Type _mgrType;
        private static Type _mcmSettingsType;
        private static object _mcmInstance;
        private static bool _reflectionFailed;

        // MCM snapshot so the 2v1 modes can bump participant counts without
        // permanently rewriting the user's ArenaOverhaul settings (in-memory
        // only -- same trick as MasterStrikesCompat; AO's saved config on disk
        // is never touched).
        private static bool _snapshotTaken;
        private static int _saveTotal, _saveActive, _saveMin, _saveInitial, _saveTeams;
        // Team-practice snapshot (GroupFight5v5 carrier) -- kept separate so
        // RestoreSettings only writes back the group that was actually saved.
        private static bool _teamSnapshotTaken;
        private static int _saveTeamTotal, _saveTeamActive, _saveTeamMin, _saveTeamInitial, _saveTeamTeams;
        private static bool _saveTeamEnabled, _saveTeamLoadoutChoice;

        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        private static Type ManagerType
        {
            get
            {
                if (_mgrType == null && !_reflectionFailed)
                {
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (!asm.FullName.StartsWith("ArenaOverhaul", StringComparison.OrdinalIgnoreCase))
                            continue;
                        foreach (Type t in SafeGetTypes(asm))
                        {
                            if (t.Name == "AOArenaBehaviorManager" || t.Name == "_AOArenaBehaviorManager")
                            {
                                _mgrType = t;
                                break;
                            }
                        }
                        if (_mgrType != null)
                            break;
                    }
                    if (_mgrType == null)
                        _reflectionFailed = true;
                }
                return _mgrType;
            }
        }

        private static object ResolveManager()
        {
            Type t = ManagerType;
            if (t == null)
                return null;
            try
            {
                PropertyInfo inst = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                return inst?.GetValue(null);
            }
            catch { return null; }
        }

        // ArenaOverhaulRedux stores settings in internal MCMSettings (MCM
        // AttributeGlobalSettings). Resolve GlobalSettings<MCMSettings>.Instance
        // via reflection and keep the cached instance + its Type for property
        // get/set by name (property names are unchanged vs classic AO).
        private static object ResolveSettings()
        {
            if (_mcmInstance != null)
                return _mcmInstance;
            if (_reflectionFailed)
                return null;
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.FullName.StartsWith("ArenaOverhaul", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Type mcm = SafeGetTypes(asm).FirstOrDefault(t => t.Name == "MCMSettings");
                    if (mcm == null)
                        continue;
                    _mcmSettingsType = mcm;
                    Type global = typeof(MCM.Abstractions.Base.Global.GlobalSettings<>).MakeGenericType(mcm);
                    PropertyInfo inst = global.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    _mcmInstance = inst?.GetValue(null);
                    if (_mcmInstance != null)
                        return _mcmInstance;
                }
            }
            catch { }
            _reflectionFailed = true;
            return null;
        }

        private static object GetSetting(string name)
        {
            object s = ResolveSettings();
            if (s == null || _mcmSettingsType == null) return null;
            PropertyInfo p = _mcmSettingsType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p?.GetValue(s);
        }

        private static void SetSetting(string name, object value)
        {
            object s = ResolveSettings();
            if (s == null || _mcmSettingsType == null) return;
            PropertyInfo p = _mcmSettingsType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            p?.SetValue(s, value);
        }

        public static bool IsAoAvailable()
        {
            try { return IsAoAvailableImpl(); }
            catch { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsAoAvailableImpl()
        {
            return ResolveManager() != null;
        }

        // Called from the town_arena menu consequence. Mirrors what AO's own
        // StartPlayerPracticeFromMenu does (AOArenaBehavior.cs): set the
        // practice mode, flag the menu-entry state, open the arena mission.
        // AO's own AfterMissionStarted then pays, sets the mission mode and
        // calls StartPlayerPractice() for us.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void BeginPractice(CombatAiArenaKind kind)
        {
            try
            {
                object mgr = ResolveManager();
                if (mgr == null)
                {
                    CombatAiLog.Log("[ARENA] BeginPractice aborted: ArenaOverhaul not available.");
                    return;
                }
                bool sparKind = kind == CombatAiArenaKind.Spar || kind == CombatAiArenaKind.SparShield;
                int opponents = (kind == CombatAiArenaKind.Duel || kind == CombatAiArenaKind.CavDuel) ? 1 : (kind == CombatAiArenaKind.GroupFight5v5 ? 2 * CombatAiState.GroupFightTeamSize : 2);
                int aiTeams = (sparKind || kind == CombatAiArenaKind.GroupFight5v5) ? 2 : 1;
                if (kind == CombatAiArenaKind.GroupFight5v5)
                {
                    SnapshotAndApplyTeamCounts(opponents, aiTeams);
                    ArenaTeamPracticePatch.EnsureApplied();
                }
                else
                {
                    SnapshotAndApplyCounts(opponents, aiTeams, false);
                }
                CombatAiState.ArenaForceShields = kind == CombatAiArenaKind.SparShield || kind == CombatAiArenaKind.GroupFight5v5;
                if (CombatAiState.ArenaForceShields)
                    ArenaShieldPatch.EnsureApplied();
                CombatAiState.ArenaForceMounts = kind == CombatAiArenaKind.CavDuel;
                if (CombatAiState.ArenaForceMounts)
                    ArenaCavalryPatch.EnsureApplied();
                SetPracticeMode(mgr, kind == CombatAiArenaKind.GroupFight5v5 ? "Team" : "Parry");
                if (!SetPrePracticeFlag(mgr, true) || !SetEnteredFromMenu(mgr, true))
                    CombatAiLog.Log("[ARENA] WARN: AO internal flag write failed -- practice may not auto-start (AO internals changed?).");
                CombatAiState.PendingArenaFight = kind;
                CombatAiState.OverrideEnabled = true;
                CombatAiLog.Log($"[ARENA] menu -> {kind} practice (Parry carrier, opponents={opponents}, aiTeams={aiTeams}, forceShields={CombatAiState.ArenaForceShields}).");
                PlayerEncounter.LocationEncounter.CreateAndOpenMissionController(
                    LocationComplex.Current.GetLocationWithId("arena"), (Location)null, (CharacterObject)null, (string)null);
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[ARENA] BeginPractice failed: {ex}");
            }
        }

        private static void SetPracticeMode(object mgr, string modeName)
        {
            try
            {
                Type t = mgr.GetType();
                Type enumType = SafeGetTypes(t.Assembly).FirstOrDefault(x => x.Name == "ArenaPracticeMode");
                object value = null;
                if (enumType != null)
                {
                    FieldInfo f = enumType.GetField(modeName);
                    value = f?.GetValue(null);
                }
                MethodInfo m = t.GetMethod("SetPracticeMode");
                if (m != null && value != null)
                    m.Invoke(mgr, new[] { value });
            }
            catch { }
        }

        // Parry practice is the carrier for all our modes: a single AI team of
        // opponents hostile to the player, no prizes. Only the participant
        // counts change per mode; everything else honours the user's AO
        // settings (cost, loadouts, weapons).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SnapshotAndApplyCounts(int activeOpponents, int aiTeams, bool pinRoster)
        {
            if (ResolveSettings() == null)
            {
                CombatAiLog.Log("[ARENA] AO MCM settings instance is null -- participant counts left as user configured.");
                return;
            }
            if (!_snapshotTaken)
            {
                _snapshotTaken = true;
                _saveTotal = Convert.ToInt32(GetSetting("ParryPracticeTotalParticipants"));
                _saveActive = Convert.ToInt32(GetSetting("ParryPracticeActiveParticipants"));
                _saveMin = Convert.ToInt32(GetSetting("ParryPracticeActiveParticipantsMinimum"));
                _saveInitial = Convert.ToInt32(GetSetting("ParryPracticeInitialParticipants"));
                _saveTeams = Convert.ToInt32(GetSetting("ParryPracticeAITeamsCount"));
            }
            SetSetting("ParryPracticeActiveParticipants", activeOpponents);
            SetSetting("ParryPracticeActiveParticipantsMinimum", activeOpponents);
            SetSetting("ParryPracticeInitialParticipants", activeOpponents);
            SetSetting("ParryPracticeAITeamsCount", aiTeams);
            int total = Convert.ToInt32(GetSetting("ParryPracticeTotalParticipants"));
            if (pinRoster || total < activeOpponents)
                SetSetting("ParryPracticeTotalParticipants", activeOpponents);
        }

        // Team-practice variant for the GroupFight5v5 carrier. Team practice
        // reads its own TeamPractice* MCM group (GetTotalParticipantsCount/
        // GetActiveOpponentCount/GetAITeamsCount all switch on PracticeMode).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SnapshotAndApplyTeamCounts(int activeOpponents, int aiTeams)
        {
            if (ResolveSettings() == null)
            {
                CombatAiLog.Log("[ARENA] AO MCM settings instance is null -- team practice counts left as user configured.");
                return;
            }
            if (!_teamSnapshotTaken)
            {
                _teamSnapshotTaken = true;
                _saveTeamTotal = Convert.ToInt32(GetSetting("TeamPracticeTotalParticipants"));
                _saveTeamActive = Convert.ToInt32(GetSetting("TeamPracticeActiveParticipants"));
                _saveTeamMin = Convert.ToInt32(GetSetting("TeamPracticeActiveParticipantsMinimum"));
                _saveTeamInitial = Convert.ToInt32(GetSetting("TeamPracticeInitialParticipants"));
                _saveTeamTeams = Convert.ToInt32(GetSetting("TeamPracticeAITeamsCount"));
                _saveTeamEnabled = Convert.ToBoolean(GetSetting("EnableTeamPractice"));
                _saveTeamLoadoutChoice = Convert.ToBoolean(GetSetting("TeamPracticeEnableLoadoutChoice"));
            }
            SetSetting("EnableTeamPractice", true);
            SetSetting("TeamPracticeActiveParticipants", activeOpponents);
            SetSetting("TeamPracticeActiveParticipantsMinimum", activeOpponents);
            SetSetting("TeamPracticeInitialParticipants", activeOpponents);
            SetSetting("TeamPracticeAITeamsCount", aiTeams);
            SetSetting("TeamPracticeEnableLoadoutChoice", false);
            SetSetting("TeamPracticeTotalParticipants", activeOpponents);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RestoreSettings()
        {
            if (!_snapshotTaken && !_teamSnapshotTaken)
                return;
            try
            {
                if (ResolveSettings() != null)
                {
                    if (_snapshotTaken)
                    {
                        SetSetting("ParryPracticeTotalParticipants", _saveTotal);
                        SetSetting("ParryPracticeActiveParticipants", _saveActive);
                        SetSetting("ParryPracticeActiveParticipantsMinimum", _saveMin);
                        SetSetting("ParryPracticeInitialParticipants", _saveInitial);
                        SetSetting("ParryPracticeAITeamsCount", _saveTeams);
                    }
                    if (_teamSnapshotTaken)
                    {
                        SetSetting("TeamPracticeTotalParticipants", _saveTeamTotal);
                        SetSetting("TeamPracticeActiveParticipants", _saveTeamActive);
                        SetSetting("TeamPracticeActiveParticipantsMinimum", _saveTeamMin);
                        SetSetting("TeamPracticeInitialParticipants", _saveTeamInitial);
                        SetSetting("TeamPracticeAITeamsCount", _saveTeamTeams);
                        SetSetting("EnableTeamPractice", _saveTeamEnabled);
                        SetSetting("TeamPracticeEnableLoadoutChoice", _saveTeamLoadoutChoice);
                    }
                }
                _snapshotTaken = false;
                _teamSnapshotTaken = false;
                CombatAiLog.Log("[ARENA] AO practice settings restored.");
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[ARENA] RestoreSettings failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SetPrePracticeFlag(object mgr, bool value)
        {
            if (_reflectionFailed)
                return false;
            try
            {
                if (_piPrePractice == null)
                    _piPrePractice = ManagerType?.GetProperty("IsPlayerPrePractice");
                if (_miPrePracticeSetter == null)
                    _miPrePracticeSetter = _piPrePractice?.GetSetMethod(true);
                if (_miPrePracticeSetter == null)
                {
                    _reflectionFailed = true;
                    return false;
                }
                _miPrePracticeSetter.Invoke(mgr, new object[] { value });
                return true;
            }
            catch
            {
                _reflectionFailed = true;
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SetEnteredFromMenu(object mgr, bool value)
        {
            if (_reflectionFailed)
                return false;
            try
            {
                if (_fiEnteredFromMenu == null)
                {
                    // ArenaOverhaulRedux turned the private field into an internal
                    // property (EnteredPracticeFightFromMenu); classic AO kept it as
                    // a private field (_enteredPracticeFightFromMenu). Try both.
                    PropertyInfo prop = ManagerType?.GetProperty("EnteredPracticeFightFromMenu",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop != null)
                    {
                        prop.SetValue(mgr, value);
                        return true;
                    }
                    _fiEnteredFromMenu = AccessTools.Field(ManagerType, "_enteredPracticeFightFromMenu");
                }
                if (_fiEnteredFromMenu == null)
                {
                    _reflectionFailed = true;
                    return false;
                }
                _fiEnteredFromMenu.SetValue(mgr, value);
                return true;
            }
            catch
            {
                _reflectionFailed = true;
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsArenaPracticeMission(Mission mission)
        {
            return mission != null && mission.GetMissionBehavior<ArenaPracticeFightMissionController>() != null;
        }

        // The mission's real fighters: the controller's _participantAgents
        // list is exactly who spawns into the pit -- the arena master, guards
        // and townsmen are never in it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryGetArenaParticipantIndices(Mission mission, List<int> indices)
        {
            indices.Clear();
            try
            {
                ArenaPracticeFightMissionController controller =
                    mission != null ? mission.GetMissionBehavior<ArenaPracticeFightMissionController>() : null;
                if (controller == null)
                    return false;
                if (_fiParticipantAgents == null)
                    _fiParticipantAgents = AccessTools.Field(typeof(ArenaPracticeFightMissionController), "_participantAgents");
                List<Agent> participants = _fiParticipantAgents?.GetValue(controller) as List<Agent>;
                if (participants == null)
                    return false;
                foreach (Agent a in participants)
                {
                    if (a != null && a.IsActive() && a.Index >= 0)
                        indices.Add(a.Index);
                }
                return indices.Count > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}