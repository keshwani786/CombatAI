using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;

namespace CombatAI
{
    // ArenaOverhaul integration: adds CombatAI's own options to the town_arena
    // menu, next to AO's Expansive/Parry/Team practice entries. Each option
    // drives AO's practice pipeline (ArenaPracticeMode.Parry as the carrier,
    // participant counts bumped in memory) and arms a CombatAiArenaKind that
    // CombatAiMissionBehavior consumes once the fight actually starts.
    //
    //   Combat AI: Duel           -> 1 opponent, DuelMode (no console needed)
    //   Combat AI: Spar           -> 2 opponents, auto-assigned spar pair
    //   Combat AI: Defend (2v1)   -> 2 coordinated attackers vs player defender
    //   Combat AI: Spar (all shields)       -> spar pair, everyone with 1H + shield
    //   Combat AI: Group Fight 5v5 (team practice) -> 10 shielded AI on two
    //     colour-coded mutually-hostile AI teams (AO's Team carrier); the
    //     player is alone on a third team, neutralized, free to observe
    //
    // Non-participants (arena master, guards, townsmen) are excluded by
    // reading the controller's _participantAgents list at mission time.
    public class CombatAiArenaBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener((object)this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Stateless: PendingArenaFight is a session flag, nothing to save.
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            starter.AddGameMenuOption("town_arena", "town_arena_combatai_duel",
                "{=caiDuel01}Combat AI: Duel",
                (GameMenuOption.OnConditionDelegate)MenuCondition,
                (GameMenuOption.OnConsequenceDelegate)(args => ArenaOverhaulBridge.BeginPractice(CombatAiArenaKind.Duel)),
                false, 5, false, (object)null);
            starter.AddGameMenuOption("town_arena", "town_arena_combatai_spar",
                "{=caiSpar01}Combat AI: Spar",
                (GameMenuOption.OnConditionDelegate)MenuCondition,
                (GameMenuOption.OnConsequenceDelegate)(args => ArenaOverhaulBridge.BeginPractice(CombatAiArenaKind.Spar)),
                false, 6, false, (object)null);
            starter.AddGameMenuOption("town_arena", "town_arena_combatai_2v1",
                "{=caiDuo01}Combat AI: Defend against two",
                (GameMenuOption.OnConditionDelegate)MenuCondition,
                (GameMenuOption.OnConsequenceDelegate)(args => ArenaOverhaulBridge.BeginPractice(CombatAiArenaKind.Defend2v1)),
                false, 7, false, (object)null);
            starter.AddGameMenuOption("town_arena", "town_arena_combatai_spar_shield",
                "{=caiSparSh01}Combat AI: Spar (all shields)",
                (GameMenuOption.OnConditionDelegate)MenuCondition,
                (GameMenuOption.OnConsequenceDelegate)(args => ArenaOverhaulBridge.BeginPractice(CombatAiArenaKind.SparShield)),
                false, 8, false, (object)null);
            starter.AddGameMenuOption("town_arena", "town_arena_combatai_group5v5",
                "{=caiGrp01}Combat AI: Group Fight 5v5 (team practice)",
                (GameMenuOption.OnConditionDelegate)MenuCondition,
                (GameMenuOption.OnConsequenceDelegate)(args => ArenaOverhaulBridge.BeginPractice(CombatAiArenaKind.GroupFight5v5)),
                false, 9, false, (object)null);
            starter.AddGameMenuOption("town_arena", "town_arena_combatai_cavduel",
                "{=caiCav01}Combat AI: Cavalry Duel (mounted 1v1)",
                (GameMenuOption.OnConditionDelegate)MenuCondition,
                (GameMenuOption.OnConsequenceDelegate)(args => ArenaOverhaulBridge.BeginPractice(CombatAiArenaKind.CavDuel)),
                false, 10, false, (object)null);
        }

        private static bool MenuCondition(MenuCallbackArgs args)
        {
            if (!ArenaOverhaulBridge.IsAoAvailable())
                return false;
            args.IsEnabled = true;
            return true;
        }
    }
}
