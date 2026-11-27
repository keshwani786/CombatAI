using TaleWorlds.CampaignSystem;

namespace CombatAI
{
    // Campaign-side save sync for the player's LONG-TERM combat memory
    // (PlayerCombatModel's _lt* tables -- Session 45, user spec).
    //
    // The mission-side model observes and resets its short-term window every
    // fight; this behavior carries the accumulated long-term tables inside
    // the campaign save file itself via IDataStore.SyncData, so the AI's
    // read on the player's habits survives save/load and improves across the
    // whole campaign. In non-campaign game types (custom battles, arena) the
    // behavior never registers and the memory simply lives for the session.
    public class CombatAiMemoryBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            // No campaign events needed -- we exist purely to serialize.
        }

        public override void SyncData(IDataStore dataStore)
        {
            string s = null;
            if (dataStore.IsSaving)
                s = PlayerCombatModel.ExportMemory();
            if (!dataStore.SyncData("cai_pmem_v1", ref s))
                return;
            if (dataStore.IsLoading && !string.IsNullOrEmpty(s))
            {
                PlayerCombatModel.ImportMemory(s);
                CombatAiLog.Log("[PMEM] long-term player memory loaded from save.");
            }
        }
    }
}
