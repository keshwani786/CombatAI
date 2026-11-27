using System;
using System.Linq;
using System.Runtime.CompilerServices;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace CombatAI
{
    // MCM (Mod Configuration Menu) page for the combat AI. Probabilities are shown as percent.
    // Values are copied into CombatAiTune about once a second (and at mission start), so changes
    // made from the in-mission options screen take effect live.
    public class CombatAiSettings : AttributeGlobalSettings<CombatAiSettings>
    {
        public override string Id => "CombatAI_v1";
        public override string DisplayName => "Combat AI (BC-CR Bridge)";
        public override string FolderName => "CombatAI";
        public override string FormatType => "json2";

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Base block chance (%)", 30f, 99f, "0", Order = 0, RequireRestart = false, HintText = "Chance to block a released swing when nothing else is known. Camera angle, learned pattern, hold time and the forecast then move it up or down.")]
        public float BlockNeutral { get; set; } = 85f;

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Minimum block chance (%)", 0f, 60f, "0", Order = 1, RequireRestart = false, HintText = "Floor after all adjustments.")]
        public float BlockMin { get; set; } = 5f;

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Maximum block chance (%)", 50f, 100f, "0", Order = 2, RequireRestart = false, HintText = "Ceiling after all adjustments (the AI is never perfect below 100).")]
        public float BlockMax { get; set; } = 94f;

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Normal attack: block chance adjust (points)", -50f, 20f, "0", Order = 3, RequireRestart = false, HintText = "Added to the block chance when the player releases a NORMAL attack (hold between quick and long).")]
        public float BlockDeltaNormal { get; set; } = 0f;

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Quick attack: block chance adjust (points)", -50f, 20f, "0", Order = 4, RequireRestart = false, HintText = "Added to the block chance when the player releases a QUICK attack.")]
        public float BlockDeltaQuick { get; set; } = 0f;

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Attack after feint: block chance adjust (points)", -50f, 20f, "0", Order = 5, RequireRestart = false, HintText = "Added when the player releases after one or more feints.")]
        public float BlockDeltaFeint { get; set; } = 0f;

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Held attack: block chance adjust (points)", -50f, 20f, "0", Order = 6, RequireRestart = false, HintText = "Added when the player releases after a long hold.")]
        public float BlockDeltaHold { get; set; } = 0f;

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Camera-angle read strength", 0f, 4f, "0.00", Order = 7, RequireRestart = false, HintText = "How much the player camera tell (quick attacks) moves the block chance. 0 = ignore the camera.")]
        public float CamGain { get; set; } = 2f;

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Learned-pattern read strength", 0f, 4f, "0.00", Order = 8, RequireRestart = false, HintText = "How much the learned held-to-released habit moves the block chance. 0 = ignore patterns.")]
        public float PatGain { get; set; } = 2f;

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Wrong-line guard: re-point chance (%)", 0f, 100f, "0", Order = 9, RequireRestart = false, HintText = "When an early guard is on the wrong line at the release, the chance the AI still moves it to the right line in time.")]
        public float FollowBase { get; set; } = 45f;

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Wrong-line re-point delay min (s)", 0f, 1f, "0.00", Order = 10, RequireRestart = false, HintText = "Shortest delay before the guard re-points.")]
        public float FollowDelayMin { get; set; } = 0.08f;

        [SettingPropertyGroup("1. Release block (S3)", GroupOrder = 1)]
        [SettingPropertyFloatingInteger("Wrong-line re-point delay max (s)", 0f, 1f, "0.00", Order = 11, RequireRestart = false, HintText = "Longest delay before the guard re-points.")]
        public float FollowDelayMax { get; set; } = 0.3f;

        [SettingPropertyGroup("2. Hold reaction (S1)", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Bite chance on a held attack: base (%)", 0f, 90f, "0", Order = 12, RequireRestart = false, HintText = "Chance the AI reacts early (raises a guard) to a held attack. Added to the caution term below.")]
        public float S1Base { get; set; } = 12f;

        [SettingPropertyGroup("2. Hold reaction (S1)", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Bite chance: added by caution, up to (%)", 0f, 90f, "0", Order = 13, RequireRestart = false, HintText = "Each AI has a caution level (25-60%); this scales how much it adds to the bite chance.")]
        public float S1CautionGain { get; set; } = 25f;

        [SettingPropertyGroup("2. Hold reaction (S1)", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Bite delay max (s)", 0f, 1.5f, "0.00", Order = 14, RequireRestart = false, HintText = "A bite happens at a random time up to this long into the hold.")]
        public float S1BiteDelayMax { get; set; } = 0.45f;

        [SettingPropertyGroup("2. Hold reaction (S1)", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Early guard patience min (s)", 0.2f, 4f, "0.00", Order = 15, RequireRestart = false, HintText = "An early guard is dropped after at least this long (turtling a long hold is passive).")]
        public float S1PatienceMin { get; set; } = 0.9f;

        [SettingPropertyGroup("2. Hold reaction (S1)", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Early guard patience max (s)", 0.2f, 6f, "0.00", Order = 16, RequireRestart = false, HintText = "...and at most this long.")]
        public float S1PatienceMax { get; set; } = 1.6f;

        [SettingPropertyGroup("2. Hold reaction (S1)", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Press chance when it does not bite (%)", 0f, 100f, "0", Order = 17, RequireRestart = false, HintText = "If it neither bites nor counters, the chance it loads up its own cancellable hold or feint to put the holding player under pressure.")]
        public float S1Press { get; set; } = 45f;

        [SettingPropertyGroup("2. Hold reaction (S1)", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Counter-attack chance: base (%)", 0f, 100f, "0", Order = 18, RequireRestart = false, HintText = "If it does not bite, the chance to catch the holding player with a quick attack.")]
        public float S1CounterBase { get; set; } = 20f;

        [SettingPropertyGroup("2. Hold reaction (S1)", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Counter-attack chance: added by a confirmed forecast (%)", 0f, 100f, "0", Order = 19, RequireRestart = false, HintText = "Extra counter chance when the forecast said the player would keep holding.")]
        public float S1CounterGain { get; set; } = 45f;

        [SettingPropertyGroup("2. Hold reaction (S1)", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Buffer after a bite (s)", 0f, 4f, "0.00", Order = 102, RequireRestart = false, HintText = "After the AI bites a hold (raises an early guard on it) it will not bite another hold for this long and may counter/press instead.")]
        public float S1Buffer { get; set; } = 1.5f;

        [SettingPropertyGroup("3. Feint read (S2)", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Ready held before a block-tap counts as a feint (s)", 0f, 1f, "0.00", Order = 19, RequireRestart = false, HintText = "The windup must exist this long before cancelling it counts as a feint. Holding attack while tapping block cycles the ready in ~0.1s and is NOT a feint. Lower it if real fast feints get ignored.")]
        public float FeintReadyDwell { get; set; } = 0.30f;

        [SettingPropertyGroup("3. Feint read (S2)", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Fall for a feint: base chance (%)", 0f, 100f, "0", Order = 20, RequireRestart = false, HintText = "Chance the AI falls for the FIRST feint of a chain (guards the old line, then follows the new one).")]
        public float S2Base { get; set; } = 45f;

        [SettingPropertyGroup("3. Feint read (S2)", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Fall chance decay per feint after the 2nd (%)", 10f, 100f, "0", Order = 21, RequireRestart = false, HintText = "Chain shape: feint 2 is boosted (fell for the first), each feint after that multiplies the fall chance by this. 60 = feint 3 is 60% as likely as feint 2.")]
        public float S2Decay { get; set; } = 60f;

        [SettingPropertyGroup("3. Feint read (S2)", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Fall chance while the AI is holding/feinting itself (%)", 0f, 100f, "0", Order = 22, RequireRestart = false, HintText = "When the AI is itself holding or feinting an attack, the player's block-tap is more likely to be blocking/countering than a feint -- the fall/bite chance is multiplied by this.")]
        public float BusyFall { get; set; } = 12f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Turn watch after my swing resolves (s)", 0f, 2.5f, "0.00", Order = 107, RequireRestart = false, HintText = "Duel only: after our swing resolves (blocked, parried or clean) we hand the turn to the player for this long before attacking again (0 = off). Scaled up when the script favours the player.")]
        public float TurnWatch { get; set; } = 1.0f;

        [SettingPropertyGroup("3. Feint read (S2)", GroupOrder = 3)]
        [SettingPropertyInteger("Max feints fallen for in a row (within the window)", 1, 10, "0", Order = 22, RequireRestart = false, HintText = "After this many falls in a row the AI does not fall for the next feint and goes on the attack instead.")]
        public int S2MaxFalls { get; set; } = 4;

        [SettingPropertyGroup("3. Feint read (S2)", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Later falls follow faster (% per feint)", 0f, 100f, "0", Order = 100, RequireRestart = false, HintText = "Each further fall in a chain follows the new line FASTER: the follow delay shrinks by this share per earlier fall. 0 = no speed-up.")]
        public float S2FollowGrowth { get; set; } = 60f;

        [SettingPropertyGroup("3. Feint read (S2)", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Buffer after a read / chain max (s)", 0f, 4f, "0.00", Order = 101, RequireRestart = false, HintText = "After the AI reads a feint or a chain reaches its max, it does not fall for new feints for this long and may attack instead.")]
        public float S2Buffer { get; set; } = 1.5f;

        [SettingPropertyGroup("3. Feint read (S2)", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Near-instant follow chance (%)", 0f, 100f, "0", Order = 23, RequireRestart = false, HintText = "When it falls for a feint, the chance it follows the new line with almost no delay.")]
        public float S2QuickFollow { get; set; } = 28f;

        [SettingPropertyGroup("3. Feint read (S2)", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Follow delay min (s)", 0f, 1f, "0.00", Order = 24, RequireRestart = false, HintText = "Normal follow delay, counted from when the new line is visible.")]
        public float S2FollowDelayMin { get; set; } = 0.15f;

        [SettingPropertyGroup("3. Feint read (S2)", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Follow delay max (s)", 0f, 1f, "0.00", Order = 25, RequireRestart = false, HintText = "...")]
        public float S2FollowDelayMax { get; set; } = 0.35f;

        [SettingPropertyGroup("3. Feint read (S2)", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Take the ready after reading a feint (%)", 0f, 100f, "0", Order = 26, RequireRestart = false, HintText = "When it reads a feint and the forecast agreed, the chance to take the initiative with its own ready.")]
        public float S2Initiative { get; set; } = 55f;

        [SettingPropertyGroup("4. Initiative: after a block", GroupOrder = 4)]
        [SettingPropertyFloatingInteger("Follow-up chance after a block or dodge: base (%)", 0f, 100f, "0", Order = 27, RequireRestart = false, HintText = "Base chance to follow a block or dodge with an attack (then scaled by how well it read the swing, panic and caution).")]
        public float RiposteBase { get; set; } = 65f;

        [SettingPropertyGroup("4. Initiative: after a block", GroupOrder = 4)]
        [SettingPropertyFloatingInteger("Opening quality after a plain block or dodge (%)", 0f, 100f, "0", Order = 28, RequireRestart = false, HintText = "How good the chance to hit is after a normal block or dodge. Decides release-now vs load-up.")]
        public float PostBlockGoodBlock { get; set; } = 60f;

        [SettingPropertyGroup("4. Initiative: after a block", GroupOrder = 4)]
        [SettingPropertyFloatingInteger("Opening quality after a parry (%)", 0f, 100f, "0", Order = 29, RequireRestart = false, HintText = "Same after a real parry (the attacker is stunned longer).")]
        public float PostBlockGoodParry { get; set; } = 85f;

        [SettingPropertyGroup("4. Initiative: after a block", GroupOrder = 4)]
        [SettingPropertyFloatingInteger("Share of the opening used to release at once (%)", 0f, 100f, "0", Order = 30, RequireRestart = false, HintText = "Release-now chance = opening quality x this.")]
        public float PostBlockReleaseShare { get; set; } = 60f;

        [SettingPropertyGroup("4. Initiative: after a block", GroupOrder = 4)]
        [SettingPropertyFloatingInteger("Follow-up is a feint chance (%)", 0f, 100f, "0", Order = 31, RequireRestart = false, HintText = "Chance the follow-up is a feint to push the player onto the defensive. The rest loads a cancellable hold.")]
        public float PostBlockFeint { get; set; } = 18f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Breather after own attack min (s)", 0f, 3f, "0.00", Order = 32, RequireRestart = false, HintText = "Minimum pause between attacks the AI starts on its own.")]
        public float TempoMin { get; set; } = 0.35f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Breather after own attack max (s)", 0f, 5f, "0.00", Order = 33, RequireRestart = false, HintText = "Maximum pause.")]
        public float TempoMax { get; set; } = 0.9f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Watch a held ready min (s)", 0f, 4f, "0.00", Order = 34, RequireRestart = false, HintText = "How long the AI watches a held player ready before attacking into it.")]
        public float HoldGraceMin { get; set; } = 0.45f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Watch a held ready max (s)", 0f, 6f, "0.00", Order = 35, RequireRestart = false, HintText = "...")]
        public float HoldGraceMax { get; set; } = 0.9f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Settle time after your last attack (s)", 0f, 2f, "0.00", Order = 36, RequireRestart = false, HintText = "Tiny pause after the player attack stage ends before the AI starts attacking on its own.")]
        public float QuietSeconds { get; set; } = 0.15f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Double-hold extra wait (s)", 0f, 4f, "0.00", Order = 37, RequireRestart = false, HintText = "When both are loaded, how much longer the AI holds before releasing so the player has to commit first.")]
        public float DoubleHoldExtra { get; set; } = 1.2f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Both loaded: go to WATCH base (%)", 0f, 90f, "0", Order = 38, RequireRestart = false, HintText = "When the player gets loaded while the AI is loaded: chance to cancel and watch.")]
        public float ReviewWatchBase { get; set; } = 30f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Both loaded: WATCH added by caution (%)", 0f, 90f, "0", Order = 39, RequireRestart = false, HintText = "Scaled by the AI caution level.")]
        public float ReviewWatchGain { get; set; } = 30f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Both loaded: RISK early release (%)", 0f, 100f, "0", Order = 40, RequireRestart = false, HintText = "Chance to release early to keep the initiative (reduced by caution, never vs a quick ready).")]
        public float ReviewRisk { get; set; } = 22f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Watch duration min (s)", 0f, 4f, "0.00", Order = 41, RequireRestart = false, HintText = "How long the AI gives the player the turn after a review WATCH.")]
        public float WatchMin { get; set; } = 0.6f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Watch duration max (s)", 0f, 6f, "0.00", Order = 42, RequireRestart = false, HintText = "...")]
        public float WatchMax { get; set; } = 1.4f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("Watch after the player blocks our swing (%)", 0f, 100f, "0", Order = 43, RequireRestart = false, HintText = "Chance the AI gives the player a moment after its swing was blocked.")]
        public float WatchAfterBlock { get; set; } = 60f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("...duration min (s)", 0f, 5f, "0.00", Order = 44, RequireRestart = false, HintText = "...")]
        public float WatchAfterBlockMin { get; set; } = 1f;

        [SettingPropertyGroup("5. Initiative: tempo and watch", GroupOrder = 5)]
        [SettingPropertyFloatingInteger("...duration max (s)", 0f, 8f, "0.00", Order = 45, RequireRestart = false, HintText = "...")]
        public float WatchAfterBlockMax { get; set; } = 2f;

        [SettingPropertyGroup("6. Lapses (burn)", GroupOrder = 6)]
        [SettingPropertyInteger("Confident forecast misses in a row before a lapse can arm", 1, 6, "0", Order = 46, RequireRestart = false, HintText = "A rare one-shot lapse (over-trusting its forecast) only arms after this many confident misses in a row.")]
        public int BurnStreak { get; set; } = 2;

        [SettingPropertyGroup("6. Lapses (burn)", GroupOrder = 6)]
        [SettingPropertyFloatingInteger("Lapse arm chance: base (%)", 0f, 100f, "0", Order = 47, RequireRestart = false, HintText = "Chance to arm on that miss; it rises with further misses.")]
        public float BurnBase { get; set; } = 30f;

        [SettingPropertyGroup("6. Lapses (burn)", GroupOrder = 6)]
        [SettingPropertyFloatingInteger("Lapse arm chance: cap (%)", 0f, 100f, "0", Order = 48, RequireRestart = false, HintText = "Never more likely than this. Set both to 0 to disable lapses.")]
        public float BurnMax { get; set; } = 70f;

        [SettingPropertyGroup("7. Dodge", GroupOrder = 7)]
        [SettingPropertyFloatingInteger("Dodge share of released swings, duel (%)", 0f, 100f, "0", Order = 49, RequireRestart = false, HintText = "Of the swings the AI answers, this share is dodged (existing dodge logic) and the rest plain-blocked.")]
        public float DuelDodge { get; set; } = 40f;

        [SettingPropertyGroup("7. Dodge", GroupOrder = 7)]
        [SettingPropertyFloatingInteger("Same-side spam counter chance (%)", 0f, 100f, "0", Order = 50, RequireRestart = false, HintText = "Chance that the Nth left/right swing in a row is answered by a master strike or clinch (needs the MasterStrikes mod).")]
        public float SpamCounter { get; set; } = 85f;

        [SettingPropertyGroup("7. Dodge", GroupOrder = 7)]
        [SettingPropertyInteger("Same-side releases that count as spam", 2, 8, "0", Order = 51, RequireRestart = false, HintText = "The Nth left/right swing in a row can be answered by a master strike or clinch.")]
        public int SpamCount { get; set; } = 3;

        [SettingPropertyGroup("7. Dodge", GroupOrder = 7)]
        [SettingPropertyBool("Either side counts as spam", Order = 103, RequireRestart = false, HintText = "On: any mix of left and right swings counts; off: only the same side in a row.")]
        public bool SpamEitherSide { get; set; } = true;

        [SettingPropertyGroup("7. Dodge", GroupOrder = 7)]
        [SettingPropertyFloatingInteger("Max gap between spammed swings (s)", 0.5f, 6f, "0.0", Order = 51, RequireRestart = false, HintText = "Same-side swings only count as spam when each one follows the previous within this many seconds; a longer pause resets the count.")]
        public float SpamWindow { get; set; } = 2f;

        [SettingPropertyGroup("8. AI attacks", GroupOrder = 8)]
        [SettingPropertyFloatingInteger("AI feint chain: continue chance (%)", 0f, 100f, "0", Order = 52, RequireRestart = false, HintText = "Chance the AI chains another feint instead of releasing.")]
        public float FeintContinue { get; set; } = 60f;

        [SettingPropertyGroup("9. Systems on/off", GroupOrder = 9)]
        [SettingPropertyBool("Hold reaction (S1) enabled", Order = 53, RequireRestart = false, HintText = "Off = the AI never reacts early to held attacks (also needs combatai.brain on).")]
        public bool S1On { get; set; } = true;

        [SettingPropertyGroup("9. Systems on/off", GroupOrder = 9)]
        [SettingPropertyBool("Feint read (S2) enabled", Order = 54, RequireRestart = false, HintText = "Off = the AI never falls for feints.")]
        public bool S2On { get; set; } = true;

        [SettingPropertyGroup("9. Systems on/off", GroupOrder = 9)]
        [SettingPropertyBool("Release block (S3) enabled", Order = 55, RequireRestart = false, HintText = "Off = the old non-brain release answer.")]
        public bool S3On { get; set; } = true;

        [SettingPropertyGroup("9. Systems on/off", GroupOrder = 9)]
        [SettingPropertyBool("Initiative enabled", Order = 56, RequireRestart = false, HintText = "Off = no counters, press, watch or review (old behaviour).")]
        public bool InitOn { get; set; } = true;

        [SettingPropertyGroup("10. Script (momentum)", GroupOrder = 10)]
        [SettingPropertyBool("Fight script enabled", Order = 60, RequireRestart = false, HintText = "Momentum director: rewards the player's good defence with AI openings, then takes the favour back. Off = every modifier neutral.")]
        public bool ScriptOn { get; set; } = true;

        [SettingPropertyGroup("10. Script (momentum)", GroupOrder = 10)]
        [SettingPropertyFloatingInteger("Script power (%)", 0f, 100f, "0", Order = 61, RequireRestart = false, HintText = "Overall scale of the favour target (and therefore of every modifier).")]
        public float ScriptPower { get; set; } = 100f;

        [SettingPropertyGroup("10. Script (momentum)", GroupOrder = 10)]
        [SettingPropertyFloatingInteger("Reward for good player defence (%)", 0f, 100f, "0", Order = 62, RequireRestart = false, HintText = "How much defending well against AI attacks makes the AI more gullible.")]
        public float ScriptReward { get; set; } = 70f;

        [SettingPropertyGroup("10. Script (momentum)", GroupOrder = 10)]
        [SettingPropertyFloatingInteger("Target AI block ratio (%)", 30f, 90f, "0", Order = 63, RequireRestart = false, HintText = "The share of player swings the script aims for the AI to block: blocking above this opens the AI up, below this tightens it.")]
        public float ScriptTargetBlock { get; set; } = 62f;

        [SettingPropertyGroup("10. Script (momentum)", GroupOrder = 10)]
        [SettingPropertyInteger("Exchanges before an opening: min", 2, 10, "0", Order = 64, RequireRestart = false, HintText = "Lower end of the calm-exchange count after which the script starts offering openings.")]
        public int ScriptTurnsMin { get; set; } = 3;

        [SettingPropertyGroup("10. Script (momentum)", GroupOrder = 10)]
        [SettingPropertyInteger("Exchanges before an opening: max", 2, 10, "0", Order = 65, RequireRestart = false, HintText = "Upper end of the calm-exchange count after which the script starts offering openings.")]
        public int ScriptTurnsMax { get; set; } = 5;

        internal void Apply()
        {
            CombatAiTune.BlockNeutral = BlockNeutral / 100f;
            CombatAiTune.BlockMin = BlockMin / 100f;
            CombatAiTune.BlockMax = BlockMax / 100f;
            CombatAiTune.BlockDeltaNormal = BlockDeltaNormal / 100f;
            CombatAiTune.BlockDeltaQuick = BlockDeltaQuick / 100f;
            CombatAiTune.BlockDeltaFeint = BlockDeltaFeint / 100f;
            CombatAiTune.BlockDeltaHold = BlockDeltaHold / 100f;
            CombatAiTune.CamGain = CamGain;
            CombatAiTune.PatGain = PatGain;
            CombatAiTune.FollowBase = FollowBase / 100f;
            CombatAiTune.FollowDelayMin = FollowDelayMin;
            CombatAiTune.FollowDelayMax = FollowDelayMax;
            CombatAiTune.S1Base = S1Base / 100f;
            CombatAiTune.S1CautionGain = S1CautionGain / 100f;
            CombatAiTune.S1BiteDelayMax = S1BiteDelayMax;
            CombatAiTune.S1PatienceMin = S1PatienceMin;
            CombatAiTune.S1PatienceMax = S1PatienceMax;
            CombatAiTune.S1Press = S1Press / 100f;
            CombatAiTune.S1CounterBase = S1CounterBase / 100f;
            CombatAiTune.S1CounterGain = S1CounterGain / 100f;
            CombatAiTune.FeintReadyDwell = FeintReadyDwell;
            CombatAiTune.S2Base = S2Base / 100f;
            CombatAiTune.S2Decay = S2Decay / 100f;
            CombatAiTune.BusyFall = BusyFall / 100f;
            CombatAiTune.TurnWatch = TurnWatch;
            CombatAiTune.S1Buffer = S1Buffer;
            CombatAiTune.S2MaxFalls = S2MaxFalls;
            CombatAiTune.S2FollowGrowth = S2FollowGrowth / 100f;
            CombatAiTune.S2Buffer = S2Buffer;
            CombatAiTune.S2QuickFollow = S2QuickFollow / 100f;
            CombatAiTune.S2FollowDelayMin = S2FollowDelayMin;
            CombatAiTune.S2FollowDelayMax = S2FollowDelayMax;
            CombatAiTune.S2Initiative = S2Initiative / 100f;
            CombatAiTune.RiposteBase = RiposteBase / 100f;
            CombatAiTune.PostBlockGoodBlock = PostBlockGoodBlock / 100f;
            CombatAiTune.PostBlockGoodParry = PostBlockGoodParry / 100f;
            CombatAiTune.PostBlockReleaseShare = PostBlockReleaseShare / 100f;
            CombatAiTune.PostBlockFeint = PostBlockFeint / 100f;
            CombatAiTune.TempoMin = TempoMin;
            CombatAiTune.TempoMax = TempoMax;
            CombatAiTune.HoldGraceMin = HoldGraceMin;
            CombatAiTune.HoldGraceMax = HoldGraceMax;
            CombatAiTune.QuietSeconds = QuietSeconds;
            CombatAiTune.DoubleHoldExtra = DoubleHoldExtra;
            CombatAiTune.ReviewWatchBase = ReviewWatchBase / 100f;
            CombatAiTune.ReviewWatchGain = ReviewWatchGain / 100f;
            CombatAiTune.ReviewRisk = ReviewRisk / 100f;
            CombatAiTune.WatchMin = WatchMin;
            CombatAiTune.WatchMax = WatchMax;
            CombatAiTune.WatchAfterBlock = WatchAfterBlock / 100f;
            CombatAiTune.WatchAfterBlockMin = WatchAfterBlockMin;
            CombatAiTune.WatchAfterBlockMax = WatchAfterBlockMax;
            CombatAiTune.BurnStreak = BurnStreak;
            CombatAiTune.BurnBase = BurnBase / 100f;
            CombatAiTune.BurnMax = BurnMax / 100f;
            CombatAiTune.DuelDodge = DuelDodge / 100f;
            CombatAiTune.SpamCounter = SpamCounter / 100f;
            CombatAiTune.SpamCount = SpamCount;
            CombatAiTune.SpamEitherSide = SpamEitherSide;
            CombatAiTune.ScriptOn = ScriptOn;
            CombatAiTune.ScriptPower = ScriptPower / 100f;
            CombatAiTune.ScriptReward = ScriptReward / 100f;
            CombatAiTune.ScriptTargetBlock = ScriptTargetBlock / 100f;
            CombatAiTune.ScriptTurnsMin = ScriptTurnsMin;
            CombatAiTune.ScriptTurnsMax = ScriptTurnsMax;
            CombatAiTune.SpamWindow = SpamWindow;
            CombatAiTune.FeintContinue = FeintContinue / 100f;
            CombatAiTune.S1On = S1On;
            CombatAiTune.S2On = S2On;
            CombatAiTune.S3On = S3On;
            CombatAiTune.InitOn = InitOn;
        }
    }

    // The only place the rest of the mod touches MCM, guarded so a missing MCM = shipped defaults.
    public static class CombatAiMcmBridge
    {
        private static bool _present;
        private static float _acc;
        private static string _lastDump;
        private static bool _failLogged;

        private static bool McmPresent()
        {
            if (_present)
                return true;
            _present = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "MCMv5");
            return _present;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RefreshImpl()
        {
            CombatAiSettings s = CombatAiSettings.Instance;
            if (s != null)
                s.Apply();
        }

        public static void Refresh()
        {
            if (!McmPresent())
                return;
            try
            {
                RefreshImpl();
                if (CombatAiTune.DuoProfileActive)
                    CombatAiTune.ApplyDuoOverrides();
                if (CombatAiTune.GroupProfileActive)
                    CombatAiTune.ApplyGroupOverrides();
                string d = CombatAiTune.Dump();
                if (d != _lastDump)
                {
                    _lastDump = d;
                    CombatAiLog.Log("[MCM] tuning values: " + d);
                }
            }
            catch (Exception ex)
            {
                if (!_failLogged)
                {
                    _failLogged = true;
                    CombatAiLog.Log("[MCM] settings unavailable (using defaults): " + ex.Message);
                }
            }
        }

        public static void Tick(float dt)
        {
            _acc += dt;
            if (_acc < 1f)
                return;
            _acc = 0f;
            Refresh();
        }
    }
}
