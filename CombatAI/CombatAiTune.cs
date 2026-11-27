using System;
using System.Reflection;
using System.Text;

namespace CombatAI
{
    // Live tunable values for the defence brain / initiative. Plain statics with the shipped defaults,
    // so the AI code never touches MCM types. CombatAiSettings (MCM) overwrites them via Apply().
    // GENERATED from one table together with CombatAiSettings.cs -- keep the two in sync.
    public static class CombatAiTune
    {
        public static float BlockNeutral = 0.85f;
        public static float BlockMin = 0.05f;
        public static float BlockMax = 0.94f;
        public static float BlockDeltaNormal = 0f;
        public static float BlockDeltaQuick = 0f;
        public static float BlockDeltaFeint = 0f;
        public static float BlockDeltaHold = 0f;
        public static float CamGain = 2f;
        public static float PatGain = 2f;
        public static float FollowBase = 0.45f;
        public static float FollowDelayMin = 0.08f;
        public static float FollowDelayMax = 0.3f;
        public static float S1Base = 0.12f;
        public static float S1CautionGain = 0.25f;
        public static float S1BiteDelayMax = 0.45f;
        public static float S1PatienceMin = 0.9f;
        public static float S1PatienceMax = 1.6f;
        public static float S1Press = 0.45f;
        public static float S1CounterBase = 0.2f;
        public static float S1CounterGain = 0.45f;
        public static float FeintReadyDwell = 0.30f;
        public static float S2Base = 0.45f;
        public static float S2Decay = 0.60f;
        public static int S2MaxFalls = 4;
        public static float S2FollowGrowth = 0.6f;
        public static float S2Buffer = 1.5f;
        public static float S1Buffer = 1.5f;
        public static float BusyFall = 0.12f;
        public static float TurnWatch = 1.0f;
        public static float S2QuickFollow = 0.28f;
        public static float S2FollowDelayMin = 0.15f;
        public static float S2FollowDelayMax = 0.35f;
        public static float S2Initiative = 0.55f;
        public static float RiposteBase = 0.65f;
        public static float PostBlockGoodBlock = 0.6f;
        public static float PostBlockGoodParry = 0.85f;
        public static float PostBlockReleaseShare = 0.6f;
        public static float PostBlockFeint = 0.18f;
        public static float TempoMin = 0.35f;
        public static float TempoMax = 0.9f;
        public static float HoldGraceMin = 0.45f;
        public static float HoldGraceMax = 0.9f;
        public static float QuietSeconds = 0.15f;
        public static float DoubleHoldExtra = 1.2f;
        public static float ReviewWatchBase = 0.3f;
        public static float ReviewWatchGain = 0.3f;
        public static float ReviewRisk = 0.22f;
        public static float WatchMin = 0.6f;
        public static float WatchMax = 1.4f;
        public static float WatchAfterBlock = 0.6f;
        public static float WatchAfterBlockMin = 1f;
        public static float WatchAfterBlockMax = 2f;
        public static int BurnStreak = 2;
        public static float BurnBase = 0.3f;
        public static float BurnMax = 0.7f;
        public static float DuelDodge = 0.4f;
        public static float SpamCounter = 0.85f;
        public static int SpamCount = 3;
        public static bool SpamEitherSide = true;
        public static bool ScriptOn = true;
        public static float ScriptPower = 1.00f;
        public static float ScriptReward = 0.70f;
        public static float ScriptTargetBlock = 0.62f;
        public static int ScriptTurnsMin = 3;
        public static int ScriptTurnsMax = 5;
        public static float SpamWindow = 2.0f;
        public static float FeintContinue = 0.6f;
        public static bool S1On = true;
        public static bool S2On = true;
        public static bool S3On = true;
        public static bool InitOn = true;

        // ---- Arena "Defend against two" profile ----
        // The 2v1 drill is meant to be tough and quick: no watch time, short breathers and
        // holds, early risky releases. Applied over the live values only while the mode is
        // armed (snapshot/restore), so standalone duel keeps exactly its own tuning. The MCM
        // bridge re-applies the overrides after each refresh so a slider move can't undo them.
        public static bool DuoProfileActive;
        private static System.Collections.Generic.Dictionary<FieldInfo, object> _duoSaved;

        public static void EnterDuoProfile()
        {
            if (DuoProfileActive)
                return;
            _duoSaved = new System.Collections.Generic.Dictionary<FieldInfo, object>();
            foreach (FieldInfo f in typeof(CombatAiTune).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType != typeof(bool) || f.Name.EndsWith("On"))
                    _duoSaved[f] = f.GetValue(null);
            DuoProfileActive = true;
            ApplyDuoOverrides();
        }

        public static void ExitDuoProfile()
        {
            if (!DuoProfileActive)
                return;
            DuoProfileActive = false;
            if (_duoSaved != null)
                foreach (var kv in _duoSaved)
                    kv.Key.SetValue(null, kv.Value);
            _duoSaved = null;
        }

        public static void ApplyDuoOverrides()
        {
            TempoMin = 0.12f;            // gap between self-started attacks
            TempoMax = 0.40f;
            QuietSeconds = 0.04f;        // settle after the player's last attack stage
            HoldGraceMin = 0.12f;        // watching a held ready before attacking into it
            HoldGraceMax = 0.30f;
            DoubleHoldExtra = 0.25f;
            ReviewWatchBase = 0f;        // no cancel-to-watch when both are loaded
            ReviewWatchGain = 0f;
            ReviewRisk = 0.90f;          // release early to keep the initiative
            WatchMin = 0f;
            WatchMax = 0f;
            WatchAfterBlock = 0f;        // never hand the turn over after a block
            WatchAfterBlockMin = 0f;
            WatchAfterBlockMax = 0f;
            S1Press = 0.65f;             // load up pressure instead of waiting out a hold
            S2Initiative = 0.85f;        // a read feint becomes our attack
            PostBlockGoodBlock = 0.8f;
            PostBlockGoodParry = 0.95f;
            PostBlockReleaseShare = 0.8f;
            PostBlockFeint = 0.2f;
            RiposteBase = 0.85f;
        }

        // ---- Arena "Group fight" profile (4z spice) ----
        // The 5v5 wants aggression everywhere except the outnumbered defender
        // (whose 'attack less' stays per-agent in the FSM): almost no watch
        // time, short breathers, risky releases, held swings fallen for more. Same
        // snapshot/restore plumbing as the duo profile.
        public static bool GroupProfileActive;
        private static System.Collections.Generic.Dictionary<FieldInfo, object> _groupSaved;

        public static void EnterGroupProfile()
        {
            if (GroupProfileActive)
                return;
            _groupSaved = new System.Collections.Generic.Dictionary<FieldInfo, object>();
            foreach (FieldInfo f in typeof(CombatAiTune).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType != typeof(bool) || f.Name.EndsWith("On"))
                    _groupSaved[f] = f.GetValue(null);
            GroupProfileActive = true;
            ApplyGroupOverrides();
        }

        public static void ExitGroupProfile()
        {
            if (!GroupProfileActive)
                return;
            GroupProfileActive = false;
            if (_groupSaved != null)
                foreach (var kv in _groupSaved)
                    kv.Key.SetValue(null, kv.Value);
            _groupSaved = null;
        }

        public static void ApplyGroupOverrides()
        {
            TempoMin = 0.20f;            // gap between self-started attacks
            TempoMax = 0.60f;
            QuietSeconds = 0.05f;        // settle after the last attack stage
            HoldGraceMin = 0.20f;        // watching a held ready before attacking into it
            HoldGraceMax = 0.40f;
            ReviewWatchBase = 0f;        // no cancel-to-watch when both are loaded
            ReviewWatchGain = 0f;
            ReviewRisk = 0.70f;
            WatchMin = 0f;
            WatchMax = 0.30f;
            WatchAfterBlock = 0.15f;     // rarely hand the turn over after a block
            WatchAfterBlockMin = 0.2f;
            WatchAfterBlockMax = 0.6f;
            S1Press = 0.60f;
            S2Initiative = 0.70f;
            RiposteBase = 0.80f;
            PostBlockReleaseShare = 0.75f;
            BlockNeutral = 0.70f;        // 4aa base block 70% (snapshot restores)
            BlockDeltaNormal = -0.20f;   // normal swings get blocked less (spice)
            BlockDeltaHold = -0.25f;     // held swings are FALLEN FOR more (bait works)
        }

        // Block chance adjustment (fraction) for the player's released move type.
        public static float BlockDelta(int move)
        {
            switch (move)
            {
                case PlayerCombatModel.MvQuick: return BlockDeltaQuick;
                case PlayerCombatModel.MvNormal: return BlockDeltaNormal;
                case PlayerCombatModel.MvHold: return BlockDeltaHold;
                case PlayerCombatModel.MvFeint: return BlockDeltaFeint;
                default: return 0f;
            }
        }

        // One-line dump of every value, for the log (so a tuned session is reproducible).
        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            foreach (FieldInfo f in typeof(CombatAiTune).GetFields(BindingFlags.Public | BindingFlags.Static))
                sb.Append(f.Name).Append('=').Append(Convert.ToString(f.GetValue(null), System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }
}
