using System;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =======================================================================
    // FIGHT SCRIPT (Sessions 51->55: FIFA-style "scripting", reward-based).
    // One per-fight director that modulates the EXISTING defensive systems'
    // probabilities/timings -- never fakes damage, speed or hit results.
    //
    // Session 55 redesign (user verdict: "tone alone can't produce visible,
    // short fights"): a TURN-BASED HAZARD director on top of the Favor tone.
    //   Calm counts resolved exchanges (blocked/parried/chambered blows) since
    //   the last decisive hit. After a GRACE of calm exchanges, Hazard ramps
    //   toward 1 over _cycleTarget further exchanges; the Brain pulls each
    //   block/fall/bite probability toward an ERRING goal by Hazard*Power via
    //   Mix(), so openings appear on a rhythm, not at random. A clean hit
    //   anywhere = decisive: the cycle restarts. Offering 3 un-taken errors
    //   pulls Calm back (no endless gifting).
    //
    //   Favor in [-1,+1] stays as the "tone": + favours the player, - the AI.
    // =======================================================================
    internal static class FightScript
    {
        public enum MixKind { Block, Fall, Bite }

        // ---- tunables (CombatAiTune, MCM "10. Script") ----
        private static bool On => CombatAiTune.ScriptOn;
        private static float Strength => CombatAiTune.ScriptPower;
        private static float Reward => CombatAiTune.ScriptReward;
        private static float TargetBlock => CombatAiTune.ScriptTargetBlock;
        private static int TurnsMin => CombatAiTune.ScriptTurnsMin;
        private static int TurnsMax => CombatAiTune.ScriptTurnsMax;

        private const float ScoreAlpha = 0.25f;
        private const float FavorSlewPerSec = 0.25f;
        private const int GraceAttacksDuel = 5;
        private const int GraceAttacksSpar = 6;
        private const float StartBiasDuel = -0.25f;
        private const float StartBiasSpar = -0.7f;
        private const int MaxScriptLogs = 600;
        private const int GraceDuel = 2;          // calm exchanges before Hazard starts ramping
        private const int GraceSpar = 3;
        private const int SparCycleBonus = 2;     // defender-favoured start: longer cycles
        private const int SparFirstCycleBonus = 2;
        private const int MaxOffersPerCycle = 3;  // ignored openings pull Calm back
        private const float OfferLogHazard = 0.25f;

        private static readonly Random _rng = new Random();
        private static Mission _mission;
        private static int _logCount;
        private static float _lastScriptLogAt = -99f;
        private static float _clock;

        public static float Favor { get; private set; }
        public static float PlayerDefScore { get; private set; } = 0.5f;
        public static float AiBlockScore { get; private set; } = 0.62f;
        public static int DefStreak { get; private set; }
        public static int AttacksSeen { get; private set; }
        public static int Calm { get; private set; }          // resolved exchanges since the last decisive hit
        public static int CycleTarget => _cycleTarget;

        private static int Grace => CombatAiState.SparMode ? GraceSpar : GraceDuel;
        private static int _cycleTarget = 4;
        private static int _offered;                  // errors already offered this cycle
        private static int _cyclesDone;
        private static float _lastAiAttackAt = -99f;
        private static int _lastBand;
        private static float _lastTickLogAt = -99f;

        public static void Reset()
        {
            Favor = 0f;
            PlayerDefScore = 0.5f;
            AiBlockScore = 0.62f;
            DefStreak = 0;
            AttacksSeen = 0;
            Calm = 0;
            _offered = 0;
            _cyclesDone = 0;
            _cycleTarget = SampleTarget();
            _lastAiAttackAt = 0f;
            _lastBand = 0;
            _lastScriptLogAt = -99f;   // Session 53: a stale stamp (previous mission's clock) stalled every [SCRIPT] log on the new clock
            _lastTickLogAt = -99f;
            _clock = 0f;
        }

        // ------------------------------------------------------------------
        // HAZARD -- the turn-based director
        // ------------------------------------------------------------------

        // How many calm exchanges this cycle should take: a base draw minus
        // a favour bonus (a player who defends well earns openings sooner),
        // always at least grace+1; spar cycles run longer and the very first
        // longer still so the 2v1 defender lasts its start.
        private static int SampleTarget()
        {
            int span = MathF.Max(0, TurnsMax - TurnsMin);
            int t = TurnsMin + (span > 0 ? _rng.Next(span + 1) : 0);
            t -= (int)MathF.Round(1.5f * Favor);
            if (CombatAiState.SparMode)
            {
                t += SparCycleBonus;
                if (_cyclesDone == 0)
                    t += SparFirstCycleBonus;
            }
            return MathF.Max(Grace + 1, t);
        }

        // 0 inside the grace, ramps to 1 at _cycleTarget calm exchanges.
        public static float Hazard
        {
            get
            {
                if (!On)
                    return 0f;
                float h = (Calm - Grace) / (float)MathF.Max(1, _cycleTarget - Grace);
                return h < 0f ? 0f : h > 1f ? 1f : h;
            }
        }

        // Pull a probability toward its "err" goal by hazard * power.
        // busy: while the AI is itself loaded the pull is weaker (a loaded AI
        // should not suddenly turn incompetent).
        public static float Mix(float p, MixKind kind, bool busy = false)
        {
            if (!On)
                return p;
            float w = Hazard * Strength * (busy ? 0.25f : 1f);
            if (w <= 0f)
                return p;
            float goal = kind == MixKind.Block ? 0.10f : kind == MixKind.Fall ? 0.80f : 0.70f;
            return p + (goal - p) * Clamp01(w);
        }

        // The Brain calls this when a hazard-driven error was actually offered
        // (S3 miss, S2 fall, S1 bite). Too many un-taken openings pull Calm
        // back so the player can't be gifted forever.
        public static void NoteErrorOffered()
        {
            if (!On)
                return;
            _offered++;
            ScriptLog($"[SCRIPT] opening offered (hazard {Hazard:F2}, calm {Calm}/{_cycleTarget})");
            if (_offered >= MaxOffersPerCycle)
            {
                Calm = Math.Max(0, Calm - 2);
                _offered = 0;
            }
        }

        // A fight-pair blow resolved: blocked/parried/chambered = a calm
        // exchange; a clean hit = decisive (cycle restarts). Session 59:
        // a clean hit resets only when the PLAYER landed it (on a tracked
        // AI -- duel, or on the spar defender); the godmode test had the AI
        // landing clean hits on the player all the time, which starved Calm
        // at 0 forever -- those now just pull Calm back by 1 (duel only).
        private static void NoteExchange(bool blocked, bool playerLanded)
        {
            if (blocked)
                Calm++;
            else if (playerLanded)
            {
                Calm = 0;
                _offered = 0;
                _cycleTarget = SampleTarget();
                _cyclesDone++;
            }
            else if (!CombatAiState.SparMode)
                Calm = Math.Max(0, Calm - 1);
        }

        // ------------------------------------------------------------------
        // INPUTS
        // ------------------------------------------------------------------

        // Every blow in the mission. result->score: 1 = the defender answered
        // (Blocked/Parried/ChamberBlocked), 0 = the blow landed clean.
        public static void OnHit(Agent affected, Agent affector, CombatCollisionResult result, int damage)
        {
            try
            {
                if (affected == null || affector == null || !On)
                    return;
                bool blocked = result == CombatCollisionResult.Blocked
                    || result == CombatCollisionResult.Parried
                    || result == CombatCollisionResult.ChamberBlocked;

                // Player defending vs an AI attacker. (Session 53: NOT in
                // spar -- the spar defender's own jabs hitting the player are
                // not "AI attacks the player defended", and they were pinning
                // PlayerDefScore at the favourable end all session.)
                if (affected.IsMainAgent && !affector.IsMainAgent)
                {
                    if (!CombatAiState.SparMode)
                    {
                        PlayerDefScore += ScoreAlpha * ((blocked ? 1f : 0f) - PlayerDefScore);
                        NoteExchange(blocked, false);          // duel: AI->player blow (clean hits only dent Calm by 1)
                    }
                    else if (affector.Index == CombatAiState.SparDefenderIndex)
                        NoteExchange(blocked, false);          // spar: defender->player jab
                    return;
                }

                // An AI defending vs the player's swing -- the duel defender,
                // or the spar defender (only blows ON it count as exchanges).
                if (affector.IsMainAgent && !affected.IsMainAgent
                    && (!CombatAiState.SparMode || affected.Index == CombatAiState.SparDefenderIndex))
                {
                    AttacksSeen++;
                    AiBlockScore += ScoreAlpha * ((blocked ? 1f : 0f) - AiBlockScore);
                    NoteExchange(blocked, true);               // player->AI blow (a clean hit is decisive)
                    return;
                }

                // Spar defender AI hit by the spar attacker AI.
                if (CombatAiState.SparMode && affected.Index == CombatAiState.SparDefenderIndex && !affector.IsMainAgent)
                {
                    AttacksSeen++;
                    AiBlockScore += ScoreAlpha * ((blocked ? 1f : 0f) - AiBlockScore);
                    NoteExchange(blocked, true);               // attacker->defender blow (spar: decisive too)
                }
            }
            catch { /* script must never break combat */ }
        }

        // The AI just answered a player offensive event defensively (S3
        // decision, S1 bite, S2 fall) -- counts toward the streak stat.
        public static void NoteAiDefensiveEvent()
        {
            if (!On)
                return;
            DefStreak++;
        }

        // The AI started an attack -- its defensive streak is over.
        public static void NoteAiAttackStarted()
        {
            if (!On)
                return;
            DefStreak = 0;
            _lastAiAttackAt = _clock;
        }

        // ------------------------------------------------------------------
        // TICK -- the favor controller
        // ------------------------------------------------------------------

        public static void Tick(float dt)
        {
            _clock += dt;
            Mission m = Mission.Current;
            if (m != _mission)
            {
                Reset();
                _mission = m;
                _clock = dt;
            }
            if (!On)
            {
                Favor = 0f;
                return;
            }

            bool spar = CombatAiState.SparMode;
            // Session 53: in spar there is no "player defends vs AI attacks"
            // signal (the defender's jabs are not that) -- reward term off.
            float reward = spar ? 0f : (PlayerDefScore - 0.5f) * 2f * Reward;   // player defends well -> AI more gullible
            float balance = (AiBlockScore - TargetBlock) / 0.5f * 0.6f;   // AI blocking above target -> open up; below -> tighten
            float bias = spar ? StartBiasSpar : StartBiasDuel;
            int grace = spar ? GraceAttacksSpar : GraceAttacksDuel;
            float start = bias * MathF.Max(0f, 1f - AttacksSeen / (float)grace);
            float target = Clamp(-1f, 1f, reward + balance + start) * Strength;
            Favor += Clamp(-FavorSlewPerSec * dt, FavorSlewPerSec * dt, target - Favor);

            // [SCRIPT] band crossing (+-0.25), throttled to 1/s.
            int band = Favor > 0.25f ? 1 : Favor < -0.25f ? -1 : 0;
            if (band != _lastBand)
            {
                _lastBand = band;
                ScriptLog($"[SCRIPT] favor crossed {(band > 0 ? "+0.25 (favours the player)" : band < 0 ? "-0.25 (favours the AI)" : "neutral")}: {Favor:F2} " +
                    $"(defScore={PlayerDefScore:F2} aiBlock={AiBlockScore:F2} streak={DefStreak} graceSeen={AttacksSeen})");
            }

            // Periodic state dump so the director is visible in the log:
            // every 5s while anything is actually moving.
            if (_clock - _lastTickLogAt >= 5f
                && (MathF.Abs(Favor) > 0.05f || Hazard > 0f))
            {
                _lastTickLogAt = _clock;
                ScriptLog($"[SCRIPT] tick favor={Favor:F2} playerDef={PlayerDefScore:F2} aiBlock={AiBlockScore:F2} " +
                    $"streak={DefStreak} calm={Calm} target={_cycleTarget} hazard={Hazard:F2}");
            }
        }

        // ------------------------------------------------------------------
        // MODIFIERS -- neutral at Favor=0 (Session 55: softened -- Mix now
        // carries the drama; these are only the "tone")
        // ------------------------------------------------------------------
        public static float BlockLogitShift => On ? -1.2f * Favor : 0f;
        public static float FallScale => On ? Clamp(0.5f, 1.8f, 1f + 0.8f * Favor) : 1f;
        public static float BiteScale => On ? Clamp(0.5f, 1.6f, 1f + 0.6f * Favor) : 1f;
        public static float FollowLogitShift => On ? -1.4f * Favor : 0f;
        public static float FollowDelayScale => On ? Clamp(0.5f, 1.8f, 1f + 0.8f * Favor) : 1f;
        public static float WindowScale => On ? Clamp(0.7f, 1.45f, 1f + 0.45f * Favor) : 1f;
        public static float TempoScale => On ? Clamp(0.65f, 1.4f, 1f + 0.4f * Favor) : 1f;

        private static float Clamp(float lo, float hi, float v)
            => v < lo ? lo : v > hi ? hi : v;

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        private static void ScriptLog(string msg)
        {
            if (_logCount >= MaxScriptLogs || _clock - _lastScriptLogAt < 1f)
                return;
            _lastScriptLogAt = _clock;
            _logCount++;
            CombatAiLog.Log(msg);
        }

        // combatai.script status dump.
        public static string StatusLine()
            => $"script={On} favor={Favor:F2} playerDef={PlayerDefScore:F2} aiBlock={AiBlockScore:F2} " +
               $"defStreak={DefStreak} attacksSeen={AttacksSeen} calm={Calm} target={_cycleTarget} hazard={Hazard:F2} | " +
               $"blockShift={BlockLogitShift:F2} fallX={FallScale:F2} biteX={BiteScale:F2} " +
               $"followShift={FollowLogitShift:F2} followDelayX={FollowDelayScale:F2} windowX={WindowScale:F2} tempoX={TempoScale:F2}";
    }
}
