using System.Runtime.CompilerServices;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // Minimal entry point: no campaign behaviors, no UI. Just attaches the mission
    // behavior to every mission (battle, custom battle, arena, tournament, sparring)
    // so the proof-of-concept works anywhere agents fight, not only in campaign battles.
    // Session 45: a single campaign behavior joins in campaign games to sync the
    // long-term player combat memory into the save file (CombatAiMemoryBehavior).
    public class CombatAiSubModule : MBSubModuleBase
    {
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            CombatAiLog.Log("[SUBMODULE] CombatAI loaded.");
        }

        protected override void OnSubModuleUnloaded()
        {
            base.OnSubModuleUnloaded();
            // Dispose any armed EasyHook LocalHooks (no-op when nothing armed).
            // Try/catch: a missing EasyHook.dll would fault on JIT inside
            // Shutdown — never let cleanup crash the unload path.
            try { NativeTargetGuard.Shutdown(); } catch { }
            try { NativeSteerOverride.Shutdown(); } catch { }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarter)
        {
            base.OnGameStart(game, gameStarter);
            AddCampaignMemoryBehavior(game, gameStarter);
        }

        // NoInlining: isolates every CampaignSystem type resolution to this
        // frame so a non-campaign context can never fault on them.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddCampaignMemoryBehavior(Game game, IGameStarter starter)
        {
            if (game.GameType is TaleWorlds.CampaignSystem.Campaign)
            {
                ((TaleWorlds.CampaignSystem.CampaignGameStarter)starter).AddBehavior(new CombatAiMemoryBehavior());
                ((TaleWorlds.CampaignSystem.CampaignGameStarter)starter).AddBehavior(new CombatAiArenaBehavior());
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            mission.AddMissionBehavior(new CombatAiMissionBehavior());
        }
    }
}
