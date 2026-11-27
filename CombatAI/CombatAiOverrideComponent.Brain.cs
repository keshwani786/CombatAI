using System;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =======================================================================
    // DEFENSE BRAIN (Session 47) -- the user's revised architecture, see
    // combat_ai_defense_design.txt section 14.
    //
    // Three INDEPENDENT decision systems, each with its OWN probability from
    // its OWN inputs, triggered by DIFFERENT events:
    //   S1 HOLD-REACTION  player holds a ready past ~0.35s   -> bite (raise an
    //                     early guard) or watch / counter
    //   S2 FEINT-READ     player cancels + redirects (feint)  -> fall for it
    //                     (guard follows the new line after a delay) or read it
    //   S3 RELEASE-BLOCK  player releases                     -> block or miss
    // The EXPECTATION forecast (PlayerCombatModel.Forecast*, made BEFORE the
    // player acts) is a MODULATOR: when the move unfolds as forecast the
    // matching system is boosted (falls less / blocks more); when it does not,
    // it is penalised for that swing. Repeated forecast failure builds PANIC,
    // which makes the AI believe the forecast too hard and bite earlier.
    //
    // Execution is UNCHANGED: an early guard is the existing committed-block
    // machinery (_defCommitState 2), the release answer is the existing
    // block/dodge split. Hold, feint, attack, dodge and targeting recipes are
    // not touched.
    // =======================================================================
    internal static class BrainMath
    {
        // Log-odds swing of a fully confirmed forecast: x3 odds for the AI
        // (a 30% fall-for chance becomes ~10%).
        public const float G = 1.1f;
        // A contradicted forecast hurts more than a confirmed one helps.
        public const float SurpriseGain = 1.0f;
        // Panic makes the AI believe the forecast too hard (amplifies both ways).
        public const float PanicBelief = 0.6f;

        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        public static float Logit(float p)
        {
            p = p < 0.001f ? 0.001f : p > 0.999f ? 0.999f : p;
            return (float)Math.Log(p / (1f - p));
        }
        public static float Sigmoid(float l) => 1f / (1f + (float)Math.Exp(-l));

        // >0 forecast confirmed, <0 surprised; scaled by how informed the
        // forecast is (a thin history changes nothing).
        public static float ConfirmX(float pExpected, float baseline)
        {
            float x = (pExpected - baseline) / 0.5f;
            x = x < -0.5f ? -0.5f : x > 1f ? 1f : x;
            return x * PlayerCombatModel.ForecastReliability * PlayerCombatModel.ChainSharpness;
        }

        // Log-odds shift in the AI's favour. ONLY a confirmed forecast helps;
        // a contradicted one does NOT penalise every swing -- repeated wrong
        // forecasts instead arm a rare one-shot "burn" (see ScoreRelease).
        public static float Shift(float x, float panic) => x > 0f ? x * G : 0f;
    }

    public partial class CombatAiOverrideComponent
    {
        // ---- S1 hold-reaction ----
        private static float S1BaseChance => CombatAiTune.S1Base;       // base odds of biting a hold (with caution 0.4 ~ 30%)
        private static float S1CautionGain => CombatAiTune.S1CautionGain;
        private const float S1PanicGain = 0f;           // panic no longer feeds the odds (user: punish/bite is a rare one-shot burn)
        private static float S1BiteDelayMax => CombatAiTune.S1BiteDelayMax;     // sampled bite time inside the hold (no fixed window)
        private static float S1PatienceMin => CombatAiTune.S1PatienceMin;       // early guard is dropped after this long (turtling a hold is passive)
        private static float S1PatienceMax => CombatAiTune.S1PatienceMax;
        private static float S1PressChance => CombatAiTune.S1Press;      // watch outcome: load up a cancellable hold/feint to put the holder under pressure
        private static float S1CounterBase => CombatAiTune.S1CounterBase;      // forecast confirmed + no bite -> catch them off guard
        private static float S1CounterGain => CombatAiTune.S1CounterGain;
        private static float S1Buffer => CombatAiTune.S1Buffer;                // post-bite: no new bites, may counter/press
        // ---- S2 feint-read ----
        private static float S2BaseChance => CombatAiTune.S2Base;       // first feint of a chain is followed ~half the time
        private static float S2Decay => CombatAiTune.S2Decay;           // fall-chance decay per feint after the 2nd
        private const float S2SecondBoost = 1.4f;                        // fell for feint 1 -> feint 2 is MORE likely
        private const float S2PanicGain = 0f;
        private static float S2FollowDelayMin => CombatAiTune.S2FollowDelayMin;
        private static float S2FollowDelayMax => CombatAiTune.S2FollowDelayMax;
        private static float S2InitiativeChance => CombatAiTune.S2Initiative; // read the feint + forecast right -> take the ready (initiative)
        private static float S2QuickFollowChance => CombatAiTune.S2QuickFollow; // falls AND follows the new line with (almost) no delay
        private const float S2QuickFollowMax = 0.06f;
        private static float S2FollowGrowth => CombatAiTune.S2FollowGrowth;
        private static float S2Buffer => CombatAiTune.S2Buffer;                 // after a read / chain max: our window, no new falls
        private static int S2MaxFalls => CombatAiTune.S2MaxFalls;               // never fall for more than 4 feints in a row: the 5th is answered by attacking

        // ---- the three flow states: ATTACK (front foot) / WATCH / DEFENSE ----
        // Attack = our windup / hold / feint. Defense = an early guard or block (the
        // player has the initiative). Watch = neither: we give them the turn.
        // The AI can leave ATTACK for WATCH or DEFENSE without releasing (it can
        // cancel any ready/feint into a guard), and return to ATTACK from either
        // unless a defence system holds it back.
        private static float ReviewWatchBase => CombatAiTune.ReviewWatchBase;    // the player got loaded while we were: P(cancel to WATCH) = base + gain*caution
        private static float ReviewWatchGain => CombatAiTune.ReviewWatchGain;
        private const float ReviewQuickReadyWatch = 0.25f; // their quick ready means a release is imminent: lean to watching
        private static float ReviewRiskBase => CombatAiTune.ReviewRisk;     // P(RISK: release early to keep the initiative) = base*(1-caution)
        private static float WatchMin => CombatAiTune.WatchMin;
        private static float WatchMax => CombatAiTune.WatchMax;
        private static float WatchAfterBlockChance => CombatAiTune.WatchAfterBlock; // their block of OUR swing: 60% we give them the turn
        private static float WatchAfterBlockMin => CombatAiTune.WatchAfterBlockMin;
        private static float WatchAfterBlockMax => CombatAiTune.WatchAfterBlockMax;
        // ---- S3 release-block ----
        private static float S3BaseLogit => BrainMath.Logit(CombatAiTune.BlockNeutral);        // ~85% with every term neutral
        private static float S3CamGain => CombatAiTune.CamGain;           // camera tell: +-0.8 logit at the extremes
        private static float S3PatGain => CombatAiTune.PatGain;           // learned held->released pattern
        private const float S3VulnLogit = -0.5f;        // we are mid own action
        private const float S3PanicLogit = 0f;       // a panicked AI whiffs defences
        private static float S3Min => CombatAiTune.BlockMin;
        private static float S3Max => CombatAiTune.BlockMax;
        private static float S3FollowBase => CombatAiTune.FollowBase;       // wrong-line early guard: chance to re-point to the real line
        private static float S3FollowDelayMin => CombatAiTune.FollowDelayMin;
        private static float S3FollowDelayMax => CombatAiTune.FollowDelayMax;
        // ---- panic ----
        private const float PanicDecayPerSec = 0.20f;
        private const float PanicOnMiss = 0.10f;        // forecast badly wrong
        private const float PanicOnConfirm = -0.06f;    // forecast right
        private const float PanicOnHit = 0.12f;         // we took damage
        private const float PanicOnStreak = 0.08f;      // ...twice within 6s
        private const float PanicOnBlock = -0.15f;      // calm exchange: our block engaged
        // ---- spam counter: Nth same-side swing may be answered by a
        // master strike / clinch (MasterStrikes, reflection-only) ----
        private static int SpamCount => CombatAiTune.SpamCount;
        private static float SpamWindowSeconds => CombatAiTune.SpamWindow;
        private static float SpamCounterChance => CombatAiTune.SpamCounter;
        private const float SpamCounterArmSeconds = 1.5f;

        // ---- the BURN: rare, one-shot "believed its forecast too hard" ----
        // Only when the forecast has been CONFIDENTLY wrong several times in a
        // row does the AI get a CHANCE (never a certainty) to over-trust it on
        // ONE later decision: bite early (S1), fall for the feint (S2) or
        // whiff the block (S3). Then the streak resets. Not applied by
        // default and never on every swing.
        private const float BurnFailX = 0.25f;          // a confident miss is x <= -this
        private const float BurnSuccessX = 0.25f;       // a confident hit clears the streak
        private static int BurnStreak => CombatAiTune.BurnStreak;               // misses in a row before a burn can arm
        private static float BurnBaseChance => CombatAiTune.BurnBase;     // chance to arm on that miss
        private const float BurnStep = 0.15f;           // +chance per further miss in the run
        private static float BurnMaxChance => CombatAiTune.BurnMax;
        private const float BurnS3Logit = -1.8f;        // the burned release: likely, not certain, a miss

        private const int MaxDecLogs = 8000;
        private static int _decLogCount;

        // Last values, for combatai.brain.
        public static float LastBrainPanic, LastBrainCaution, LastBrainInitiative;
        public static int LastBrainSpamSideHits;
        public static bool LastBrainCounterArmed;

        private float _brainClock;
        private float _panic;
        private int _failStreak;
        private bool _burnArmed;
        private float _caution = -1f;
        private float _initiative;
        private int _s1Chain = -1;
        private bool _s1BitePending;
        private float _s1BiteAt;
        private const float S1AfterFeintGap = 1.0f;   // no hold-fall this soon after a feint event
        private float _lastS2EventAt = -999f;         // clock of the last feint event the S2 brain consumed
        private float _s1BufferUntil = -1f;   // post-bite: no new bites, may counter/press instead
        private int _s2Feints;
        private bool _s2Pending;
        private float _s2Delay;
        private float _s2SeenReadyAt = -1f;
        private Agent.UsageDirection _s2Dir = Agent.UsageDirection.None;
        private float _s2DirSince;
        private const float S2DirStableSeconds = 0.05f;
        private const float PostReleaseIgnoreSeconds = 0.50f;   // a block-tap right after our swing is a reaction, not a feint
        private const float BusyGraceSeconds = 0.6f;            // attack-activity recency that counts as "busy"
        private float _ownReleaseEndAt = -99f;
        private bool _ownWasReleasing;
        private float _ownAttackActivityAt = -99f;  // last tick ANY attack state of ours was live
        private bool AiBusyNow => _brainClock - _ownAttackActivityAt < BusyGraceSeconds;
        private float _s2BufferUntil = -1f;
        private int _s2LastFallK;             // k of the last FALL in this chain (slims the wrong-line follow)
        private float _wrongVisibleAt = -1f;  // when the correct new line first became visible (human-react window)
        private float _turnWatchUntil = -1f;  // turn handed to the player after our swing resolved
        private float _playerIdleSince = -99f; // last time the player was doing something (idle lifts the turn watch)
        private float _watchUntil = -1f;
        private bool _prevPReady;
        private Agent.ActionCodeType _prevPCode;
        private float _s2ExpireAt;
        private float _guardDropAt;
        private Agent.MovementControlFlag _repointFlag;
        private float _repointAt;
        private float _lastHitAt = -99f;
        private Agent.ActionCodeType _prevOwnCode;
        private Agent.UsageDirection _spamSideDir = Agent.UsageDirection.None;
        private int _spamSideHits;
        private float _spamSideAt = -99f;
        private bool _spamPrevBlocked;        // the last counted spam swing was confirmed blocked/parried
        private bool _spamCounterArmed;
        private float _spamCounterUntil;

        private static void DecLog(string msg)
        {
            if (_decLogCount >= MaxDecLogs)
                return;
            _decLogCount++;
            CombatAiLog.Log(msg);
        }

        private float Rand(float lo, float hi) => lo + (float)DuelRng.NextDouble() * (hi - lo);

        // Called every duel frame vs the PLAYER (never the spar defender).
        // Returns true when it started an attack this frame (caller then
        // returns _attackDir).
        private bool BrainTick(Agent target, float dt, Agent.ActionStage pStage, Agent.ActionStage ownStage, float flatDist)
        {
            if (_caution < 0f)
                _caution = 0.25f + 0.35f * (float)DuelRng.NextDouble();
            if (_panic > 0f)
                _panic = MathF.Max(0f, _panic - PanicDecayPerSec * dt);

            bool pReady = pStage == Agent.ActionStage.AttackReady || pStage == Agent.ActionStage.AttackQuickReady;
            bool pLoaded = pReady || pStage == Agent.ActionStage.AttackRelease;
            bool ownReady = ownStage == Agent.ActionStage.AttackReady || ownStage == Agent.ActionStage.AttackQuickReady;
            bool ownReleasing = ownStage == Agent.ActionStage.AttackRelease;
            // Session 54: stamp when OUR swing ends (stage edge OR the FSM
            // leaving Release) -- feint events within 0.30s of it are ignored.
            bool ownReleasingNow = ownReleasing || _duelState == DuelState.Release;
            if (!ownReleasingNow && _ownWasReleasing)
                _ownReleaseEndAt = _brainClock;
            _ownWasReleasing = ownReleasingNow;
            // Session 58: ANY of our attack states (incl. the Approach/None
            // transition frames the old stage test missed) keeps the "busy"
            // flag warm for 0.6s.
            if (_duelState == DuelState.Windup || _duelState == DuelState.FeintCancel || _duelState == DuelState.FeintReady
                || _duelState == DuelState.Release || _duelState == DuelState.Recover
                || ownReady || ownReleasing)
                _ownAttackActivityAt = _brainClock;

            // Session 55 turn watch: track whether the player is DOING
            // something. A handed turn expires early when the player just
            // idles through it -- a passive player is still attacked.
            if (pReady || pStage == Agent.ActionStage.AttackRelease
                || pStage == Agent.ActionStage.Defend || pStage == Agent.ActionStage.DefendParry)
                _playerIdleSince = _brainClock;
            if (_turnWatchUntil > _brainClock && _brainClock - _playerIdleSince >= 1.2f)
            {
                _turnWatchUntil = _brainClock;
                _watchUntil = MathF.Min(_watchUntil, _brainClock);
                _nextInitiateAt = MathF.Min(_nextInitiateAt, _brainClock);
            }
            bool aiLoaded = ownReady || _duelState == DuelState.Windup || _duelState == DuelState.FeintReady;
            _initiative = aiLoaded && !pLoaded ? 1f : pLoaded && !aiLoaded ? -1f : 0f;
            LastBrainPanic = _panic;
            LastBrainCaution = _caution;
            LastBrainInitiative = _initiative;
            LastBrainSpamSideHits = _spamSideHits;
            LastBrainCounterArmed = _spamCounterArmed;

            // A block that engaged is a calm exchange: panic eases.
            Agent.ActionCodeType own = Agent.GetCurrentActionType(1);
            if (own != _prevOwnCode)
            {
                if (own == Agent.ActionCodeType.BlockedMelee || own == Agent.ActionCodeType.ParriedMelee)
                {
                    _panic = BrainMath.Clamp01(_panic + PanicOnBlock);
                    if (own == Agent.ActionCodeType.ParriedMelee)
                        _lastParryAt = _brainClock;
                }
                _prevOwnCode = own;
            }

            // An armed spam counter expires shortly after the release edge if
            // no blocked/parried blow fired it (a clean hit just lets it lapse).
            if (_spamCounterArmed && _brainClock > _spamCounterUntil)
            {
                _spamCounterArmed = false;
                DecLog($"[DEC] Agent #{Agent.Index}: spam counter expired unspent.");
            }

            bool reach = InThreatReach(target);
            bool started = false;
            int chain = PlayerCombatModel.ChainSerial;

            // ---- S1: hold confirmed -> bite or watch/counter ----
            if (CombatAiState.BrainS1 && PlayerCombatModel.HoldConfirmed && _s1Chain != chain)
            {
                _s1Chain = chain;
                // Session 61 (user rule): no hold-fall right after a feint --
                // the hold that follows a feint IS the real swing winding;
                // biting it defeats the whole point of feinting.
                if (_brainClock - _lastS2EventAt < S1AfterFeintGap)
                    DecLog($"[DEC] Agent #{Agent.Index}: S1 hold SUPPRESSED ({_brainClock - _lastS2EventAt:F2}s after a feint event)");
                else
                    started = EvaluateS1(target, flatDist, ownStage, reach);
            }
            if (_s1BitePending)
            {
                if (!pReady || chain != _s1Chain)
                    _s1BitePending = false;                       // swing released / cancelled before the bite
                else if (_brainClock >= _s1BiteAt && !ownReleasing && reach)
                {
                    RaiseEarlyGuard(target, "S1");
                    _s1BitePending = false;
                }
            }

            // ---- S2: feint event -> fall for it or read it ----
            if (PlayerCombatModel.FeintEventSerial < _s2Feints)
                _s2Feints = PlayerCombatModel.FeintEventSerial;   // model was reset (new mission)
            if (CombatAiState.BrainS2 && PlayerCombatModel.FeintEventSerial != _s2Feints)
            {
                _s2Feints = PlayerCombatModel.FeintEventSerial;
                _lastS2EventAt = _brainClock;
                started |= EvaluateS2(target, flatDist, ownStage, reach);
            }
            if (_s2Pending)
            {
                if (_brainClock > _s2ExpireAt)
                    _s2Pending = false;
                else if (pReady && !ownReleasing && reach)
                {
                    // The follow delay runs from the moment the NEW line is visible
                    // (their re-ready), not from the cancel -- during it the guard
                    // is still on the OLD line and they can land the new one.
                    if (_s2SeenReadyAt < 0f)
                    {
                        _s2SeenReadyAt = _brainClock;
                        _wrongVisibleAt = _brainClock;   // the new line is now visible -- the human-react window runs from here
                    }
                    // The first 1-3 frames of a ready report the PREVIOUS direction
                    // (seen in the [PSTAGE] trace): follow only a direction that has
                    // held for a few frames, and follow exactly that visible line --
                    // the learned-pattern gamble was wrong 91% of the time here.
                    Agent.UsageDirection hd = target.GetCurrentActionDirection(1);
                    if (hd != _s2Dir)
                    {
                        _s2Dir = hd;
                        _s2DirSince = _brainClock;
                    }
                    if (_brainClock >= _s2SeenReadyAt + _s2Delay && _brainClock - _s2DirSince >= S2DirStableSeconds
                        && hd != Agent.UsageDirection.None)
                    {
                        RaiseEarlyGuard(target, "S2", hd);
                        _s2Pending = false;
                    }
                }
            }

            // ---- feint chain MAX: the AI never falls for more than S2MaxFalls
            // feints in a row -- the chain is closed and a buffer window opens
            // so we get a turn instead of falling again.
            if (PlayerCombatModel.FeintChainAlive && PlayerCombatModel.FeintChainLen >= S2MaxFalls)
            {
                PlayerCombatModel.EndFeintChain("max");
                _s2BufferUntil = _brainClock + S2Buffer;
                _s1BufferUntil = MathF.Max(_s1BufferUntil, _s2BufferUntil);
                _nextInitiateAt = MathF.Max(_brainClock, _turnWatchUntil);   // never cancel an active turn watch
                _watchUntil = MathF.Max(_watchUntil, _turnWatchUntil);
                DecLog($"[DEC] Agent #{Agent.Index}: feint chain max ({S2MaxFalls}) reached -> BUFFER {S2Buffer:F2}s (our turn)");
            }

            // ---- ATTACK-STATE REVIEW: the player got loaded while we were ----
            bool pReadyEdge = pReady && !_prevPReady;
            _prevPReady = pReady;
            if (pReadyEdge && CombatAiState.BrainInitiative && !CombatAiState.DefenseOnly && ownReady && reach
                && (_duelState == DuelState.Windup || _duelState == DuelState.FeintReady)
                && !(_feintPlanned && !_feinted) && _defCommitState != 2)
                started |= ReviewAttackState(target, pStage);

            // ---- WATCH after the player blocks OUR swing: give them the turn ----
            Agent.ActionCodeType pCode = target.GetCurrentActionType(1);
            if (pCode != _prevPCode)
            {
                if ((pCode == Agent.ActionCodeType.BlockedMelee || pCode == Agent.ActionCodeType.ParriedMelee)
                    && CombatAiState.BrainInitiative
                    && (ownReleasing || _duelState == DuelState.Release || _duelState == DuelState.Recover)
                    && DuelRng.NextDouble() < WatchAfterBlockChance)
                {
                    float w = Rand(WatchAfterBlockMin, WatchAfterBlockMax);
                    _watchUntil = _brainClock + w;
                    DecLog($"[DEC] Agent #{Agent.Index}: WATCH -- the player blocked our swing; we give them the turn for {w:F1}s");
                }
                _prevPCode = pCode;
            }

            // An early guard against a hold that goes on and on is turtling:
            // drop it (the grace/attack logic then punishes the long hold).
            // NOT while an S2 follow is pending: the old-line guard of a fallen-for feint
            // must stay up until the follow fires or expires -- dropping it would turn
            // the release into a plain 85% neutral roll and the wrong-line re-point
            // chance would never get its turn (a long follow delay made this common).
            if (_defCommitState == 2 && _guardDropAt > 0f && _brainClock >= _guardDropAt && pReady && !_s2Pending)
            {
                _defCommittedReset();
                DecLog($"[DEC] Agent #{Agent.Index}: guard dropped (patience spent on a long hold) panic={_panic:F2}");
            }
            return started;
        }

        // Scored ONCE per release with x = BrainMath.ConfirmX (already gated by
        // the forecast's reliability and sharpness, so only a forecast that was
        // really sure counts as "proved wrong"). A run of confident misses can
        // arm ONE burn; a confident hit clears the run.
        private void ScoreRelease(float x)
        {
            if (x <= -BurnFailX)
            {
                _failStreak++;
                if (_failStreak >= BurnStreak && !_burnArmed)
                {
                    int run = _failStreak;
                    float chance = MathF.Min(BurnMaxChance, BurnBaseChance + BurnStep * (run - BurnStreak));
                    bool arm = DuelRng.NextDouble() < chance;
                    if (arm)
                    {
                        _burnArmed = true;
                        _failStreak = 0;
                    }
                    DecLog($"[DEC] Agent #{Agent.Index}: forecast confidently wrong x{run} in a row (chance {chance:F2}) -> " +
                        (arm ? "BURN ARMED (one decision will over-trust the forecast)" : "no burn yet"));
                }
            }
            else if (x >= BurnSuccessX)
                _failStreak = 0;
        }

        // The armed burn is spent by the FIRST eligible decision, once.
        private bool ConsumeBurn()
        {
            if (!_burnArmed)
                return false;
            _burnArmed = false;
            _failStreak = 0;
            return true;
        }

        // S1: the player's ready outlived the observe time.
        private bool EvaluateS1(Agent target, float flatDist, Agent.ActionStage ownStage, bool reach)
        {
            float pe = PlayerCombatModel.HoldExpect;
            float x = BrainMath.ConfirmX(pe, PlayerCombatModel.HoldBase);
            float pBase = MathF.Clamp(S1BaseChance + S1CautionGain * _caution + S1PanicGain * _panic, 0.02f, 0.9f);
            // Session 53: while WE are loaded/feinting the player's "hold" may
            // just be them readying a counter -- bites are scaled down the same
            // way falls are (a swing already in flight can't bite anyway).
            // Session 58: any recent attack activity of ours counts as busy
            // (0.6s grace covers the transition frames the old stage test
            // missed); below hazard 0.5 a busy bite is not even rolled.
            bool aiBusy = AiBusyNow;
            bool busyGate = aiBusy && FightScript.Hazard < 0.5f;
            float p = MathF.Clamp(BrainMath.Sigmoid(BrainMath.Logit(pBase) - BrainMath.Shift(x, _panic)), 0.02f, 0.85f)
                * FightScript.BiteScale;
            p = MathF.Clamp(p * (aiBusy ? CombatAiTune.BusyFall : 1f), 0.02f, 0.85f);
            p = FightScript.Mix(p, FightScript.MixKind.Bite, aiBusy);   // Session 55 hazard pull (after all clamps)
            // Post-bite buffer: after falling for a hold the AI will not bite
            // another one for S1Buffer seconds -- counter/press are its turn.
            bool s1Buffer = _brainClock < _s1BufferUntil;
            bool canBite = !busyGate && !s1Buffer && reach && ownStage != Agent.ActionStage.AttackRelease;
            bool burn = canBite && ConsumeBurn();
            bool bite = canBite && (burn || DuelRng.NextDouble() < p);
            float delay = 0f;
            if (bite)
            {
                if (SparBrainActive) _rigS1Bites++;
                delay = Rand(0f, S1BiteDelayMax) * (1f - 0.5f * _panic);
                _s1BitePending = true;
                _s1BiteAt = _brainClock + delay;
                _s1BufferUntil = _brainClock + S1Buffer;
                FightScript.NoteAiDefensiveEvent();
                if (FightScript.Hazard > 0.25f)
                    FightScript.NoteErrorOffered();
            }
            bool counter = false;
            if (!bite && x > -0.2f && _brainClock >= _nextInitiateAt && !CombatAiState.DefenseOnly && CombatAiState.BrainInitiative
                && _duelState == DuelState.Approach && flatDist < MeleeReach()
                && ownStage != Agent.ActionStage.AttackRelease)
            {
                float pc = MathF.Clamp(S1CounterBase + S1CounterGain * x, 0f, 0.7f);
                if (DuelRng.NextDouble() < pc)
                {
                    counter = true;
                    _attackReason = "s1-counter";
                    StartDuelAttack(DuelAttackKind.Quick);
                }
            }
            // No bite, no counter: still not passive. The AI can cancel any ready
            // or feint back into a guard until its own release, so it LOADS UP
            // (a cancellable hold or a feint) to put the holding player under
            // pressure instead of just waiting out the grace.
            bool press = false;
            if (!bite && !counter && CombatAiState.BrainInitiative && !CombatAiState.DefenseOnly
                && _duelState == DuelState.Approach && flatDist < MeleeReach()
                && ownStage != Agent.ActionStage.AttackRelease && _brainClock >= _nextInitiateAt
                && DuelRng.NextDouble() < S1PressChance)
            {
                if (SparBrainActive) _rigS1Press++;
                press = true;
                counter = true;
                _attackReason = "s1-press";
                StartDuelAttack(!GroupNoFeint && DuelRng.NextDouble() < 0.4 ? DuelAttackKind.Feint : DuelAttackKind.Held);
            }
            DecLog($"[DEC] Agent #{Agent.Index}: S1 hold-confirmed expect={pe:F2} x={x:F2} pBase={pBase:F2} p={p:F2} " +
                $"-> {(bite ? $"BITE in {delay:F2}s{(burn ? " (BURN: over-trusted its forecast)" : "")}" : press ? "PRESS (loads up a cancellable hold/feint)" : counter ? "COUNTER (caught them off guard)" : s1Buffer ? $"BUFFER (post-bite, {_s1BufferUntil - _brainClock:F2}s left)" : busyGate ? "BUSY: not falling while our own attack is alive" : "watch")} " +
                $"busy={(aiBusy ? 1 : 0)} own={_duelState}/{ownStage} act={(_brainClock - _ownAttackActivityAt):F2} " +
                $"caution={_caution:F2} type-prior=[q{PlayerCombatModel.ChainForecast[0]:F2} n{PlayerCombatModel.ChainForecast[1]:F2} h{PlayerCombatModel.ChainForecast[2]:F2} f{PlayerCombatModel.ChainForecast[3]:F2}] " +
                $"live[R{PlayerCombatModel.CpR:F2} F{PlayerCombatModel.CpF:F2} H{PlayerCombatModel.CpH:F2} sharp{PlayerCombatModel.CpSharp:F2}] runBreakRisk={PlayerCombatModel.BreakRisk:F2}");
            return counter;
        }

        // The player got loaded (ready) while WE were already loaded and had not
        // released: initiative is now uncertain. Three honest outcomes, all
        // possible because we can cancel into a guard until our own release:
        //   WATCH  cancel our ready (a brief guard on their line) and give them the turn
        //   RISK   release early / quick to keep the initiative before they commit
        //   HOLD   stay loaded (the double-hold: whoever commits first loses the race)
        private bool ReviewAttackState(Agent target, Agent.ActionStage pStage)
        {
            bool quickReady = pStage == Agent.ActionStage.AttackQuickReady;
            float pWatch = MathF.Clamp(ReviewWatchBase + ReviewWatchGain * _caution + (quickReady ? ReviewQuickReadyWatch : 0f), 0f, 0.85f);
            float pRisk = quickReady ? 0f : ReviewRiskBase * (1f - _caution);
            double r = DuelRng.NextDouble();
            if (r < pWatch)
            {
                float w = Rand(WatchMin, WatchMax);
                _watchUntil = _brainClock + w;
                RaiseEarlyGuard(target, "WATCH", target.GetCurrentActionDirection(1));
                _guardDropAt = _brainClock + Rand(0.2f, 0.4f);   // a cancel, not a turtle: back to neutral shortly
                DecLog($"[DEC] Agent #{Agent.Index}: REVIEW they loaded while we were loaded -> WATCH for {w:F1}s (cancel to a guard, give them the turn) pWatch={pWatch:F2} caution={_caution:F2}");
                return false;
            }
            if (r < pWatch + pRisk)
            {
                _duelState = DuelState.Release;
                _duelTimer = 0f;
                DecLog($"[DEC] Agent #{Agent.Index}: REVIEW they loaded while we were loaded -> RISK (release early to keep the initiative) pWatch={pWatch:F2} pRisk={pRisk:F2}");
                return false;
            }
            DecLog($"[DEC] Agent #{Agent.Index}: REVIEW they loaded while we were loaded -> HOLD ON (stay loaded, they must commit first) pWatch={pWatch:F2} pRisk={pRisk:F2}");
            return false;
        }

        // S2: the player cancelled a ready (a feint event).
        private bool EvaluateS2(Agent target, float flatDist, Agent.ActionStage ownStage, bool reach)
        {
            int k = PlayerCombatModel.LastFeintK;
            float pe = PlayerCombatModel.LastFeintExpect;
            // Session 53 (user spec): while OUR swing is in flight the player's
            // block-tap was probably just blocking it -- a release-edge cancel
            // like that is not a feint at all, ignore the event wholesale (no
            // fall, no read, no buffer). While WE are loaded/holding a player's
            // ready cancel can also be a counter-block, not a feint -- fall
            // chance is scaled down and no buffer may open from a read.
            bool aiReleasing = ownStage == Agent.ActionStage.AttackRelease || _duelState == DuelState.Release;
            // Session 58: any recent attack activity of ours counts as busy
            // (the 0.6s grace covers the transition frames the old stage test
            // missed); below hazard 0.5 a busy fall is not even rolled.
            bool aiBusy = AiBusyNow;
            bool busyGate = aiBusy && FightScript.Hazard < 0.5f;
            if (aiReleasing)
            {
                if (SparBrainActive) _rigS2Ignored++;
            DecLog($"[DEC] Agent #{Agent.Index}: S2 feint#{k} IGNORED (our swing is in flight: a block, not a feint)");
                return false;
            }
            // Session 54: a block-tap within 0.30s of OUR swing ending is the
            // player reacting to it, not a feint -- ignore the same way.
            if (_brainClock - _ownReleaseEndAt < PostReleaseIgnoreSeconds)
            {
                if (SparBrainActive) _rigS2Ignored++;
            DecLog($"[DEC] Agent #{Agent.Index}: S2 feint#{k} IGNORED ({_brainClock - _ownReleaseEndAt:F2}s after our release ended: a reaction, not a feint)");
                return false;
            }
            float x = BrainMath.ConfirmX(pe, PlayerCombatModel.LastFeintBase);
            // Session 51 chain shape (user spec, always applies): feint 1 ->
            // 1.0; feint 2 -> S2SecondBoost (fell for the first -> MORE likely);
            // feint 3+ -> shape(k-1) * S2Decay (each later feint doubted more).
            // Spar defender (Session 52 user spec): monotone DEcreasing --
            // each feint in the chain is less likely to be fallen for than the
            // previous one, no second-feint boost.
            float shape = SparBrainActive
                ? MathF.Pow(S2Decay, (float)Math.Max(0, k - 1))
                : k <= 1 ? 1.0f : k == 2 ? S2SecondBoost : S2SecondBoost * MathF.Pow(S2Decay, (float)(k - 2));
            float pBase = S2BaseChance * shape
                * (1f - 0.25f * PlayerCombatModel.FeintRate) + S2PanicGain * _panic;
            pBase = MathF.Clamp(pBase, 0.03f, 0.9f);
            float p = MathF.Clamp(BrainMath.Sigmoid(BrainMath.Logit(pBase) - BrainMath.Shift(x, _panic)), 0.02f, 0.9f)
                * FightScript.FallScale;
            p = MathF.Clamp(p * (aiBusy ? CombatAiTune.BusyFall : 1f), 0.02f, 0.9f);
            p = FightScript.Mix(p, FightScript.MixKind.Fall, aiBusy);   // Session 55 hazard pull (after all clamps)
            // Session 50: the streak is the ready-state feint CHAIN (the model's
            // FeintChainLen), not a time window. A read or the chain max opens a
            // BUFFER window: no new falls and the initiative vetoes lift so the
            // AI can attack instead of falling forever.
            int recent = Math.Max(0, k - 1);                    // falls earlier in this chain
            bool capped = k > S2MaxFalls;
            bool inBuffer = _brainClock < _s2BufferUntil;
            bool burn = !capped && !inBuffer && reach && ConsumeBurn();
            // FallNoFix gift: the script hands the player this feint -- the AI
            // falls AND its later wrong-line re-point is forced to fail.
            bool falls = !busyGate && !capped && !inBuffer && reach && (burn || DuelRng.NextDouble() < p);
            // A pending early bite is moot once the player has cancelled.
            _s1BitePending = false;
            bool started = false;
            string outcome;
            bool canAttack = !CombatAiState.DefenseOnly && CombatAiState.BrainInitiative
                && _duelState == DuelState.Approach && flatDist < MeleeReach()
                && ownStage != Agent.ActionStage.AttackRelease;
            if (inBuffer)
            {
                // Post-read/post-cap window: we do not fall for new feints --
                // a Held attack makes the buffer OUR turn.
                _s2Pending = false;
                _s2LastFallK = 0;
                if (_defCommitState == 2)
                    _defCommittedReset();
                outcome = $"BUFFER: not falling (our turn, {_s2BufferUntil - _brainClock:F2}s left)";
                if (SparBrainActive) _rigS2Buffer++;
                if (canAttack && DuelRng.NextDouble() < MathF.Max(S2InitiativeChance, 0.5f))
                {
                    _attackReason = "s2-buffer";
                    StartDuelAttack(DuelAttackKind.Held);
                    started = true;
                    outcome += " -> ATTACKS into the feint";
                }
            }
            else if (falls)
            {
                // Only the FIRST fall of a chain can be near-instant -- and
                // only while hazard is low (Session 55: a scripted opening
                // must look slow enough to be taken, not teleported).
                bool quick = recent == 0 && FightScript.Hazard < 0.3f && DuelRng.NextDouble() < S2QuickFollowChance;
                float stretch = MathF.Max(0.4f, 1f - S2FollowGrowth * recent);
                _s2Delay = (quick ? Rand(0f, S2QuickFollowMax)
                    : Rand(S2FollowDelayMin, S2FollowDelayMax) * (1f - 0.4f * _panic) * stretch)
                    * FightScript.FollowDelayScale;
                // Human reaction floor: a follow can never beat ~0.3-0.65s of
                // the new line being visible, and hazard slows it further.
                float followFloor = PlayerCombatModel.LastReleaseNoReady ? 0.30f
                    : MathF.Max(0.30f, MathF.Min(0.65f, 0.25f + 0.35f * PlayerCombatModel.LastReleaseHold));
                _s2Delay = MathF.Max(_s2Delay, followFloor) * (1f + 0.5f * FightScript.Hazard);
                _s2SeenReadyAt = -1f;
                _s2Dir = Agent.UsageDirection.None;
                _s2Pending = true;
                _s2LastFallK = k;
                FightScript.NoteAiDefensiveEvent();
                _s2ExpireAt = _brainClock + 1.2f + _s2Delay;     // re-ready wait + the follow delay + slack
                Agent.UsageDirection from = PlayerCombatModel.LastFeintFrom;
                // Falling for it = we were covering the OLD line; the guard goes
                // up there first (if not already) and only then follows the new one.
                if (_defCommitState != 2 && from != Agent.UsageDirection.None)
                    RaiseEarlyGuard(target, "S2-old-line", from);
                if (FightScript.Hazard > 0.25f)
                    FightScript.NoteErrorOffered();
                if (SparBrainActive) _rigS2Falls++;
                outcome = $"FALLS{(burn ? " (BURN: over-trusted its forecast)" : "")} (feint #{k} of the chain; guards the old line {from}, " +
                    $"follows the new one {(quick ? $"almost at once ({_s2Delay:F2}s)" : $"~{_s2Delay:F2}s after the re-ready, x{stretch:F2} from {recent} earlier falls")})";
            }
            else
            {
                _s2Pending = false;
                if (_defCommitState == 2)
                    _defCommittedReset();                         // read through it: drop the guard, wait for the release
                if (SparBrainActive)
                {
                    if (busyGate) _rigS2Busy++;
                    else _rigS2Reads++;
                }
                outcome = busyGate ? "BUSY: not falling while our own attack is alive"
                    : capped ? $"CAP: not falling for feint #{k} (fell for {S2MaxFalls} in a row)" : "reads it";
                // A real read (we could have fallen: in reach) or a cap closes
                // the chain; a feint seen from out of reach is neither. A busy
                // non-fall keeps the read's chain bookkeeping but may open no buffer.
                if (reach || capped)
                {
                    PlayerCombatModel.EndFeintChain(capped ? "cap" : "read");
                    _s2LastFallK = 0;
                    // Session 53: the buffer opens only when the AI had actually
                    // FALLEN in this chain (k >= 2) or hit the cap -- a read on
                    // the first feint (and every read while we are loaded)
                    // opens nothing.
                    bool openBuffer = !aiBusy && (capped || k >= 2);
                    if (openBuffer)
                    {
                        _s2BufferUntil = _brainClock + S2Buffer;
                        // One-way coupling: a feint buffer blocks hold bites too;
                        // the S1 buffer never opens the S2 buffer.
                        _s1BufferUntil = MathF.Max(_s1BufferUntil, _s2BufferUntil);
                        _nextInitiateAt = MathF.Max(_brainClock, _turnWatchUntil);   // never cancel an active turn watch
                        _watchUntil = MathF.Max(_watchUntil, _turnWatchUntil);
                    }
                }
                if (canAttack && capped)
                {
                    // 5th in a row: stop living in defence -- go to the ATTACK state,
                    // sometimes taking the risk of an early quick release.
                    double r = DuelRng.NextDouble();
                    DuelAttackKind kind = r < 0.55 || GroupNoFeint ? DuelAttackKind.Held : r < 0.80 ? DuelAttackKind.Quick : DuelAttackKind.Feint;
                    _attackReason = "s2-cap";
                    StartDuelAttack(kind);
                    started = true;
                    outcome += $" -> goes on the ATTACK ({kind})";
                }
                else if (canAttack && _brainClock >= _nextInitiateAt && x > 0.3f && DuelRng.NextDouble() < S2InitiativeChance * x)
                {
                    _attackReason = "s2-take-ready";
                    StartDuelAttack(DuelAttackKind.Held);         // into a cancellable ready: take the initiative
                    started = true;
                    outcome = "reads it -> TAKES THE READY (initiative)";
                }
            }
            DecLog($"[DEC] Agent #{Agent.Index}: S2 feint#{k} chainLen={PlayerCombatModel.FeintChainLen} " +
                $"buffer={(inBuffer ? $"{_s2BufferUntil - _brainClock:F2}s" : "-")} expect={pe:F2} x={x:F2} pBase={pBase:F2} p={p:F2} -> {outcome} " +
                $"busy={(aiBusy ? 1 : 0)} own={_duelState}/{ownStage} act={(_brainClock - _ownAttackActivityAt):F2} " +
                $"panic={_panic:F2} feintRate={PlayerCombatModel.FeintRate:F2}");
            return started;
        }

        // Raises the early guard on the player's held line. A learned
        // held->released habit (their feint destination) can move the guard
        // to where they usually end up -- a read, and a gamble.
        private void RaiseEarlyGuard(Agent target, string tag, Agent.UsageDirection forcedSide = Agent.UsageDirection.None)
        {
            // Cav release-only defense: brain early guards are the held-block
            // model we're replacing -- never arm them on the mounted path.
            if (_cavDriving)
                return;
            Agent.UsageDirection held = forcedSide != Agent.UsageDirection.None ? forcedSide : target.GetCurrentActionDirection(1);
            Agent.UsageDirection side = held;
            string note = forcedSide != Agent.UsageDirection.None ? "given line" : "held";
            if (forcedSide == Agent.UsageDirection.None
                && PlayerCombatModel.PredictRelease(held, out Agent.UsageDirection pred, out float conf)
                && pred != held && DuelRng.NextDouble() < conf)
            {
                side = pred;
                note = $"learned {held}->{pred}@{conf:F2}";
            }
            Agent.MovementControlFlag flag = BlockFlagVs(target, side);
            if (flag == Agent.MovementControlFlag.None)
                return;
            if (_duelState == DuelState.Windup || _duelState == DuelState.FeintCancel || _duelState == DuelState.FeintReady)
                EnterApproach(false);
            _defCommitState = 2;
            _defCommitFlag = flag;
            _repointFlag = Agent.MovementControlFlag.None;
            _blockedTheirSwing = false;
            _guardDropAt = _brainClock + Rand(S1PatienceMin, S1PatienceMax) * (0.8f + 0.6f * _caution);
            EnsureTargetAgent(target);
            _lastThreat = target;
            _defendTargetHold = DefendTargetHoldSeconds;
            PlayerCombatModel.MarkDefense(PlayerCombatModel.DefCommitBlock);
            DecLog($"[DEC] Agent #{Agent.Index}: {tag} early guard UP on {side} ({note}) flag={flag}");
        }

        // S3 (and the wrong-line follow): answered at the release edge.
        // Returns whether the AI defends this release at all.
        private bool BrainReleaseBlock(Agent target)
        {
            float cam = CameraTerm(target);
            _releaseCam = cam;
            float pe = PlayerCombatModel.LastReleaseExpect;
            float x = BrainMath.ConfirmX(pe, PlayerCombatModel.LastReleaseBase);
            float shift = BrainMath.Shift(x, _panic);
            string mv = PlayerCombatModel.MoveName(PlayerCombatModel.LastReleaseMove);
            FightScript.NoteAiDefensiveEvent();   // a defensive answer this release (for the script streak)
            TrackSpamSide(target);
            // A spam counter was armed for this release: the block MUST be on
            // the right line so the counter can land -- drop a wrong-line
            // commit so the instant release answer below blocks correctly.
            if (_spamCounterArmed)
            {
                Agent.MovementControlFlag needed = BlockFlagVs(target, target.GetCurrentActionDirection(1));
                if (_defCommitState == 2 && _defCommitFlag != needed)
                {
                    _defCommittedReset();
                    DecLog($"[DEC] Agent #{Agent.Index}: spam counter armed -- dropped wrong-line commit {_defCommitFlag} (need {needed}).");
                }
                DecLog($"[DEC] Agent #{Agent.Index}: spam counter armed -- forcing the block.");
                return true;
            }

            // An early guard is already up: right line = answered; wrong line
            // (S1 learned-pattern gamble / S2 fall that the player re-feinted)
            // = a follow roll with a delay, which can still lose the race.
            if (_defCommitState == 2)
            {
                Agent.MovementControlFlag actual = BlockFlagVs(target, target.GetCurrentActionDirection(1));
                if (actual == Agent.MovementControlFlag.None || actual == _defCommitFlag)
                {
                    ScoreRelease(x);
                    DecLog($"[DEC] Agent #{Agent.Index}: S3 release({mv}) guard already on the right line -> BLOCK (expect={pe:F2})");
                    return true;
                }
                // Session 51: each fall deeper into the chain slims the
                // wrong-line re-point (+0.55 logit per fall past the first).
                // Session 55: hazard also bleeds the follow off (an offered
                // opening stays open), and a HUMAN reaction window scales it:
                // the new line needs ~0.22s of visibility before the AI can
                // even start answering, full chance only after ~0.62s.
                float pf = MathF.Clamp(BrainMath.Sigmoid(BrainMath.Logit(S3FollowBase) + shift
                        + 0.55f * Math.Max(0, _s2LastFallK - 1) + FightScript.FollowLogitShift), 0.05f, 0.9f)
                        * (1f - 0.7f * FightScript.Hazard);
                float react = _wrongVisibleAt > 0f
                    ? MathF.Max(0f, MathF.Min(1f, (_brainClock - _wrongVisibleAt - 0.22f) / 0.40f))
                    : 0f;
                pf *= react;
                bool burn = ConsumeBurn();
                bool follows = !burn && DuelRng.NextDouble() < pf;
                float delay = Rand(S3FollowDelayMin, S3FollowDelayMax) * (1f - 0.4f * _panic)
                    * FightScript.FollowDelayScale;
                if (follows)
                {
                    _repointFlag = actual;
                    _repointAt = _brainClock + delay;
                }
                ScoreRelease(x);
                DecLog($"[DEC] Agent #{Agent.Index}: S3 release({mv}) guard on the WRONG line ({_defCommitFlag} vs {actual}) " +
                    $"pFollow={pf:F2} react={react:F2} -> {(follows ? $"re-points in {delay:F2}s" : burn ? "stays wrong (BURN)" : "stays wrong")} expect={pe:F2} panic={_panic:F2}");
                return follows;
            }

            float pat = PlayerCombatModel.LastPatternTerm;
            float hold = PlayerCombatModel.LastReleaseHold;
            float speed = PlayerCombatModel.LastReleaseNoReady ? -1.0f
                : hold < 0.22f ? -0.2f
                : hold < 0.30f ? -0.10f
                : hold > 0.8f ? 0.2f : 0f;
            float logit = S3BaseLogit + S3CamGain * (cam - 0.5f) + S3PatGain * (pat - 0.5f) + speed + shift
                + (_vulnerableTimer > 0f ? S3VulnLogit : 0f) + S3PanicLogit * _panic + FightScript.BlockLogitShift
                + SparClusterLogit(target);   // Session 52: attacks close together -> block a little less
            bool burned = ConsumeBurn();
            if (burned)
                logit += BurnS3Logit;
            float p = MathF.Clamp(BrainMath.Sigmoid(logit) + CombatAiTune.BlockDelta(PlayerCombatModel.LastReleaseMove), S3Min, S3Max);
            p = FightScript.Mix(p, FightScript.MixKind.Block);   // Session 55 hazard pull toward missing
            bool defends = DuelRng.NextDouble() < p;
            // Session 55: a hazard-driven miss does not just stand open -- half
            // the time it visibly guards the WRONG line (a misread the player
            // can punish); never re-pointed afterwards.
            bool misread = false;
            if (!defends && FightScript.Hazard > 0.25f)
            {
                FightScript.NoteErrorOffered();
                if (DuelRng.NextDouble() < 0.5)
                {
                    Agent.UsageDirection real = target.GetCurrentActionDirection(1);
                    Agent.UsageDirection wrong = WrongGuardDir(real);
                    if (wrong != Agent.UsageDirection.None)
                    {
                        RaiseEarlyGuard(target, "S3-misread", wrong);
                        misread = true;
                    }
                }
            }
            ScoreRelease(x);
            DecLog($"[DEC] Agent #{Agent.Index}: S3 release({mv}, hold={hold:F2}s) {(defends ? "BLOCK" : "MISS")}{(burned ? " (BURN: over-trusted its forecast)" : misread ? " (misread: guards the wrong line)" : "")} p={p:F2} " +
                $"[cam={cam:F2} pat={pat:F2} speed={speed:+0.00;-0.00} expect={pe:F2} x={x:F2} shift={shift:+0.00;-0.00} " +
                $"vuln={(_vulnerableTimer > 0f ? 1 : 0)} streak={_failStreak} burnArmed={(_burnArmed ? 1 : 0)}]");
            return defends;
        }

        // Session 55 turn watch (duel only): every swing of ours that resolves
        // hands the turn to the player for a while -- blocked/parried swings
        // longer than clean hits. The Brain's initiate/watch vetoes are
        // pushed forward with Max() so defence keeps running normally.
        public void OnOwnSwingResolved(CombatCollisionResult result)
        {
            if (_pureReactiveDefense || !CombatAiState.DuelMode || CombatAiState.SparMode || CombatAiState.DuoAttackMode)
                return;
            if (CombatAiTune.TurnWatch <= 0f)
                return;
            bool blocked = result == CombatCollisionResult.Blocked || result == CombatCollisionResult.Parried
                || result == CombatCollisionResult.ChamberBlocked;
            float w = Rand(CombatAiTune.TurnWatch, CombatAiTune.TurnWatch * 1.8f)
                * FightScript.WindowScale * (1f + 0.3f * FightScript.Hazard) * (blocked ? 1f : 0.6f);
            _turnWatchUntil = _brainClock + w;
            _watchUntil = MathF.Max(_watchUntil, _turnWatchUntil);
            _nextInitiateAt = MathF.Max(_nextInitiateAt, _turnWatchUntil);
            DecLog($"[WATCH] Agent #{Agent.Index}: turn handed to the player for {w:F2}s ({result})");
        }

        // A plausibly-wrong line for an S3 misread: a side swing is answered
        // with the opposite side or a high guard; Up/Down get a plausible
        // wrong pick. None when no direction to misread exists.
        private Agent.UsageDirection WrongGuardDir(Agent.UsageDirection real)
        {
            switch (real)
            {
                case Agent.UsageDirection.AttackLeft: return DuelRng.NextDouble() < 0.5 ? Agent.UsageDirection.AttackRight : Agent.UsageDirection.AttackUp;
                case Agent.UsageDirection.AttackRight: return DuelRng.NextDouble() < 0.5 ? Agent.UsageDirection.AttackLeft : Agent.UsageDirection.AttackUp;
                case Agent.UsageDirection.AttackUp: return DuelRng.NextDouble() < 0.5 ? Agent.UsageDirection.AttackLeft : Agent.UsageDirection.AttackRight;
                case Agent.UsageDirection.AttackDown: return Agent.UsageDirection.AttackUp;
                default: return Agent.UsageDirection.None;
            }
        }

        // Same-side spam (Session 50 user spec): LEFT or RIGHT releases repeated
        // in a row arm a counter -- on the Nth (SpamCount, default 3) swing the
        // block is forced and a master strike / clinch may fire on the impact
        // (TryFireSpamCounter). Any other direction (or a gap) resets the run.
        private void TrackSpamSide(Agent target)
        {
            Agent.UsageDirection d = target.GetCurrentActionDirection(1);
            // Session 53 (user spec): a spam run only counts if EVERY previous
            // counted swing was confirmed BLOCKED/PARRIED (NoteSpamSwingResult
            // stamps it when the blow lands). A run where swing 2 hit the AI
            // starts over.
            if (_spamSideHits > 0 && !_spamPrevBlocked)
            {
                _spamSideHits = 0;
                DecLog($"[SPAMCOUNTER] Agent #{Agent.Index}: run broken: previous swing not blocked");
            }
            if (d == Agent.UsageDirection.AttackLeft || d == Agent.UsageDirection.AttackRight)
            {
                // Session 51: SpamEitherSide counts ANY left/right mix as spam
                // (Left,Right,Left is a run); off = only the same side in a row.
                bool counts = CombatAiTune.SpamEitherSide || d == _spamSideDir;
                if (counts && _brainClock - _spamSideAt < SpamWindowSeconds)
                    _spamSideHits++;
                else
                    _spamSideHits = 1;
                _spamSideDir = d;
                _spamSideAt = _brainClock;
                _spamPrevBlocked = false;   // this swing's outcome not yet seen
            }
            else
            {
                _spamSideHits = 0;
                _spamSideDir = Agent.UsageDirection.None;
            }
            if (_spamSideHits >= SpamCount && CombatAiState.BrainInitiative)
            {
                _spamSideHits = 0;  // a new cycle needs two more spammed swings
                bool arm = DuelRng.NextDouble() < SpamCounterChance;
                if (arm)
                {
                    _spamCounterArmed = true;
                    _spamCounterUntil = _brainClock + SpamCounterArmSeconds;
                }
                DecLog($"[SPAMCOUNTER] Agent #{Agent.Index}: spam {_spamSideDir} x{SpamCount} " +
                    $"-> counter {(arm ? $"ARMED for {SpamCounterArmSeconds:F1}s" : $"not armed (roll vs {SpamCounterChance:F2})")}");
            }
        }

        // Called from OnAgentHit for every player blow on this agent, BEFORE
        // TryFireSpamCounter: stamps whether the last counted spam swing was
        // blocked (the run requirement) -- anything else breaks the run and
        // disarms the counter.
        public void NoteSpamSwingResult(CombatCollisionResult result)
        {
            if (result == CombatCollisionResult.Blocked || result == CombatCollisionResult.Parried)
                _spamPrevBlocked = true;
            else
            {
                _spamPrevBlocked = false;
                _spamSideHits = 0;
                _spamCounterArmed = false;
            }
        }

        // Called from OnAgentHit for every blow the PLAYER lands on this agent.
        // Only a Blocked/Parried result fires the armed counter -- a clean hit
        // or chamber just lets the arm expire.
        public void TryFireSpamCounter(Agent player, CombatCollisionResult result)
        {
            if (!_spamCounterArmed || _brainClock > _spamCounterUntil)
                return;
            if (result != CombatCollisionResult.Blocked && result != CombatCollisionResult.Parried)
                return;
            _spamCounterArmed = false;
            bool ms = DuelRng.NextDouble() < 0.5;
            bool started = MasterStrikesBridge.TryStart(Agent, player, ms, out string what);
            DecLog($"[SPAMCOUNTER] Agent #{Agent.Index}: player spammed {_spamSideDir} x{SpamCount} -> {what}");
            if (started)
            {
                _defCommittedReset();
                BrainResetState();
                _duelState = DuelState.Approach;
            }
        }

        // ------------------------------------------------------------------
        // SPAR BRAIN (Session 52): the spar defender faces two attackers; its
        // S1/S2/S3 only evaluate vs the player (BrainVs), while the AI
        // attacker's releases are answered by SparAttackerBlockRoll with two
        // extra logits: cluster (attacks landing close together) and
        // distraction (committed to the player while the other swings).
        // ------------------------------------------------------------------
        private const float SparClusterLogitValue = -0.6f;
        private const float SparClusterWindow = 0.7f;
        private const float SparDistractLogitValue = -0.9f;

        private readonly float[] _sparRelEdgeAt = { -99f, -99f };  // 0 = player, 1 = spar attacker
        private readonly bool[] _sparWasRel = { false, false };
        // Session 64: the side-release roll latch is now KEYED BY ATTACKER
        // INDEX (4 slots) so the spar pair AND group N-threat defenders share
        // it -- a miss on attacker A stays latched across A's whole release
        // even while B's release interleaves (the old single-field latch
        // re-rolled a miss whenever the threat index flipped between
        // attackers). Same 0.2s-gap re-arm as the Session 62 stale-latch fix,
        // per attacker.
        private readonly int[] _sideRollIdx = { -1, -1, -1, -1 };
        private readonly float[] _sideRollAt = { -99f, -99f, -99f, -99f };
        private readonly bool[] _sideRollOk = new bool[4];

        private void TrackSparReleaseEdges(Agent player, Agent attacker)
        {
            NoteSparEdge(player, 0);
            NoteSparEdge(attacker, 1);
        }

        private void NoteSparEdge(Agent a, int i)
        {
            bool rel = CombatLive(a) && a.GetCurrentActionStage(1) == Agent.ActionStage.AttackRelease;
            if (rel && !_sparWasRel[i])
                _sparRelEdgeAt[i] = _brainClock;
            _sparWasRel[i] = rel;
        }

        // "Attacks close to each other -> blocking chance a little less": the
        // OTHER attacker's release edge came inside 0.7s before this one, or
        // the other is mid-release right now.
        private float SparClusterLogit(Agent releaser)
        {
            if (!SparBrainActive || releaser == null)
                return 0f;
            int other = releaser.IsMainAgent ? 1 : 0;
            Agent otherAgent = other == 0 ? Agent.Mission?.MainAgent : GetAgentByIndex(CombatAiState.SparAttackerIndex);
            bool otherReleasing = CombatLive(otherAgent)
                && otherAgent.GetCurrentActionStage(1) == Agent.ActionStage.AttackRelease;
            if (otherReleasing || _brainClock - _sparRelEdgeAt[other] <= SparClusterWindow)
                return SparClusterLogitValue;
            return 0f;
        }

        // "Fell for one attacker's feint / busy with their hold -> the other's
        // attack lands at lower block chance." The distraction model only
        // exists for the player, so the exposed releaser is the AI attacker.
        private float SparDistractLogit(Agent releaser)
        {
            if (!SparBrainActive || releaser == null || releaser.IsMainAgent)
                return 0f;
            if (_s2Pending || _s1BitePending || _defCommitState == 2)
                return SparDistractLogitValue;
            return 0f;
        }

        // The dice + bookkeeping tail every release-block roll shares
        // (Session 66): fight-script hazard pull toward missing, [0,1] clamp,
        // the roll itself, and the script's defensive-event/error counters.
        // `p` is updated in place so the caller logs the POST-mix probability.
        private bool RollReleaseBlockOutcome(ref float p)
        {
            p = FightScript.Mix(p, FightScript.MixKind.Block);   // Session 55 hazard pull toward missing
            if (p < 0f) p = 0f;
            else if (p > 1f) p = 1f;
            bool defends = DuelRng.NextDouble() < p;
            FightScript.NoteAiDefensiveEvent();
            if (!defends && FightScript.Hazard > 0.25f)
                FightScript.NoteErrorOffered();
            return defends;
        }

        // Probabilistic block answer for the spar ATTACKER's release (and its
        // side-threat releases). Same neutral/base/clamp as S3, plus cluster,
        // distraction and the script; a scripted MissBlock gift forces a miss.
        private bool SparAttackerBlockRoll(Agent releaser)
        {
            float cluster = SparClusterLogit(releaser);
            float distract = SparDistractLogit(releaser);
            float script = FightScript.BlockLogitShift;
            float logit = BrainMath.Logit(CombatAiTune.BlockNeutral) + script + cluster + distract;
            float p = MathF.Clamp(BrainMath.Sigmoid(logit), S3Min, S3Max);
            bool defends = RollReleaseBlockOutcome(ref p);
            DecLog($"[SPARBRAIN] Agent #{Agent.Index}: attacker #{(releaser != null ? releaser.Index : -1)} release p={p:F2} " +
                $"(cluster={cluster:F2} distract={distract:F2} script={script:F2}) -> {(defends ? "BLOCK" : "MISS")}");
            return defends;
        }

        // ------------------------------------------------------------------
        // GROUP AI-vs-AI DEFENSE (Session 66 user spec): AI-vs-AI defenders
        // must actually MISS -- before this the release-edge chain ended in an
        // unconditional "_frozeThisRelease = false", i.e. a 100% block on
        // every locked release. Same S3 base/script/clamp as the spar roll,
        // plus two group-specific p-space terms:
        //   * feint-fall proxy -- the LOCKED target's ready->cancel edges
        //     inside a rolling ~1.5s window debuff the roll (-0.12p per
        //     cancel, capped at -0.36p), so feints work vs AI without needing
        //     a fall animation;
        //   * side factor -- a NON-LOCKED attacker's release (the
        //     SideReleaseRollOk path) scales the pass p by 0.60: attacks from
        //     the sides land more often (user: "side attacks should miss more").
        // [GBLOCK] logs every roll (agent, attacker, p, terms, result) so the
        // miss rate is measurable.
        // ------------------------------------------------------------------
        private const float GroupFeintWindowSeconds = 1.5f;   // rolling window the cancel count covers
        private const float GroupFeintCancelPenalty = 0.12f;  // -p per observed cancel inside the window
        private const float GroupFeintCancelPenaltyMax = 0.36f;
        private const float GroupSideBlockFactor = 0.60f;     // non-locked attacker's release: pass p x0.60
        private const int MaxGblockLogs = 400;
        private static int _gblockLogCount;
        // Feint-cancel counts keyed on attacker index -- lock switches keep
        // each attacker's own count. 4 slots, oldest evicted (same shape as
        // _sideRollIdx below).
        private readonly int[] _fcIdx = { -1, -1, -1, -1 };
        private readonly int[] _fcCount = new int[4];
        private readonly float[] _fcAt = { -99f, -99f, -99f, -99f };

        // A ready->cancel edge on an AI attacker (called from the pStage edge
        // detection in ComputeDuelFlag, group mode only).
        private void NoteGroupCancelEdge(Agent t)
        {
            if (t == null || t.IsMainAgent)
                return;
            int idx = t.Index;
            int free = -1, oldest = 0;
            for (int i = 0; i < _fcIdx.Length; i++)
            {
                if (_fcIdx[i] == idx)
                {
                    // Consecutive cancels inside the window accumulate; a gap
                    // longer than the window starts a fresh count.
                    _fcCount[i] = _brainClock - _fcAt[i] <= GroupFeintWindowSeconds
                        ? _fcCount[i] + 1 : 1;
                    _fcAt[i] = _brainClock;
                    return;
                }
                if (_fcIdx[i] < 0 && free < 0)
                    free = i;
                if (_fcAt[i] < _fcAt[oldest])
                    oldest = i;
            }
            int slot = free >= 0 ? free : oldest;
            _fcIdx[slot] = idx;
            _fcCount[slot] = 1;
            _fcAt[slot] = _brainClock;
        }

        // How many of `t`'s cancels are still live inside the rolling window.
        private int GroupFeintCancels(Agent t)
        {
            if (t == null)
                return 0;
            for (int i = 0; i < _fcIdx.Length; i++)
                if (_fcIdx[i] == t.Index)
                    return _brainClock - _fcAt[i] <= GroupFeintWindowSeconds ? _fcCount[i] : 0;
            return 0;
        }

        // The group release-block roll. `side` = the NON-LOCKED attacker path
        // (SideReleaseRollOk) and carries the extra side-attack miss factor.
        private bool GroupReleaseBlockRoll(Agent releaser, bool side)
        {
            float script = FightScript.BlockLogitShift;
            float cluster = GroupClusterLogit(releaser);
            float distract = GroupDistractLogit(releaser);
            float logit = BrainMath.Logit(CombatAiTune.BlockNeutral) + script + cluster + distract;
            float p = MathF.Clamp(BrainMath.Sigmoid(logit), S3Min, S3Max);
            int cancels = GroupFeintCancels(releaser);
            float feintPen = MathF.Min(cancels * GroupFeintCancelPenalty, GroupFeintCancelPenaltyMax);
            float sideF = side ? GroupSideBlockFactor : 1f;
            p = p * sideF - feintPen;
            // 4z spice: the attacker's observed move type tunes the roll --
            // normals blocked less, held swings fallen for MORE (the group
            // profile owns the deltas; outside it they are 0 = no-op).
            int mv = GfMoveClass(releaser);
            float mvDelta = CombatAiTune.BlockDelta(mv);
            p = MathF.Clamp(p + mvDelta, CombatAiTune.BlockMin, CombatAiTune.BlockMax);
            if (mvDelta > 0.0001f || mvDelta < -0.0001f)
            {
                if (mv == PlayerCombatModel.MvHold) _gsumBlkHold++;
                else _gsumBlkNormal++;
            }
            // 4aa surprise: a strike inside SurpriseSeconds of a coordinated
            // switch is harder to read -- consumed at the first release roll.
            if (releaser != null && GfSurprise.TryGetValue(releaser.Index, out float su))
            {
                if (_brainClock <= su)
                {
                    p -= SurpriseDelta;
                    _gsumSurpriseRolls++;
                }
                GfSurprise.Remove(releaser.Index);
            }
            // 4aa defender bonus: the layer owns the block line.
            if (_gfDefenderActive)
                p += DefenderBlockBonus;
            p = MathF.Clamp(p, CombatAiTune.BlockMin, CombatAiTune.BlockMax);
            bool defends = RollReleaseBlockOutcome(ref p);
            if (_gblockLogCount < MaxGblockLogs)
            {
                _gblockLogCount++;
                string sideTxt = side ? $"x{GroupSideBlockFactor:F2}" : "-";
                CombatAiLog.Log($"[GBLOCK] Agent #{Agent.Index}: attacker #{(releaser != null ? releaser.Index : -1)} " +
                    $"release p={p:F2} (script={script:F2} cluster={cluster:F2} distract={distract:F2} side={sideTxt} " +
                    $"cancels={cancels} pen={feintPen:F2} mvD={mvDelta:F2}) -> {(defends ? "BLOCK" : "MISS miss=wrongline")}");
            }
            return defends;
        }

        // Which release roll a NON-LOCKED side threat gets (Session 66): group
        // defenders run the group roll with the side-attack miss factor; the
        // spar defender keeps its own roll.
        private bool SideRollFor(Agent sideThreat)
            => CombatAiState.GroupFightMode && !SparBrainActive
                ? GroupReleaseBlockRoll(sideThreat, true)
                : SparAttackerBlockRoll(sideThreat);

        // The side-threat path needs the roll once per (attacker, release):
        // latch the result while that agent is inside its AttackRelease so a
        // failed roll never re-rolls every frame (and never latches stuck --
        // leaving the stage resets the latch for the next swing). Keyed by
        // attacker index (Session 64) so several threats can interleave
        // releases without erasing each other's latches.
        private bool SideReleaseRollOk(Agent sideThreat)
        {
            if (sideThreat == null
                || sideThreat.GetCurrentActionStage(1) != Agent.ActionStage.AttackRelease)
                return false;   // not in release: never a stale value
            int idx = sideThreat.Index;
            int free = -1, oldest = 0;
            for (int i = 0; i < _sideRollIdx.Length; i++)
            {
                if (_sideRollIdx[i] == idx)
                {
                    // Same attacker still inside the same release -> latched
                    // value; a >0.2s call gap means the previous release
                    // ended -> fresh roll (per-attacker stale-latch fix).
                    if (_brainClock - _sideRollAt[i] > 0.2f)
                    {
                        _rigSideReleases++;
                        _sideRollOk[i] = SideRollFor(sideThreat);
                        if (!_sideRollOk[i])
                            _rigSideMisses++;
                    }
                    _sideRollAt[i] = _brainClock;
                    return _sideRollOk[i];
                }
                if (_sideRollIdx[i] < 0 && free < 0)
                    free = i;
                if (_sideRollAt[i] < _sideRollAt[oldest])
                    oldest = i;
            }
            int slot = free >= 0 ? free : oldest;
            _rigSideReleases++;
            _sideRollOk[slot] = SideRollFor(sideThreat);
            if (!_sideRollOk[slot])
                _rigSideMisses++;
            _sideRollIdx[slot] = idx;
            _sideRollAt[slot] = _brainClock;
            return _sideRollOk[slot];
        }

        // Channel-0 action-name check (same pattern as IsForeignDodgePlaying):
        // while a MasterStrike/Clinch clip plays we must not write combat flags.
        private bool IsInMasterStrikeOrClinch()
        {
            string n = Agent.GetCurrentAction(0).GetName();
            return !string.IsNullOrEmpty(n)
                && (n.IndexOf("masterstrike", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("clinch", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ---- initiative: tempo budget + "their move is not over" gate ----
        private static float InitiateQuietSeconds => CombatAiTune.QuietSeconds;   // tiny settle after their last attack stage (a feint GAP is handled separately)
        private static float InitiateGapMin => CombatAiTune.TempoMin;          // min seconds between our self-started attacks
        private static float InitiateGapMax => CombatAiTune.TempoMax;
        private static float HoldGraceMin => CombatAiTune.HoldGraceMin;            // how long we watch a held ready before attacking into it
        private static float HoldGraceMax => CombatAiTune.HoldGraceMax;
        private float _nextInitiateAt;
        private string _attackReason = "?";
        private static int _initLogCount;
        private const int MaxInitLogs = 2000;

        // May the AI START an attack on its own initiative vs the player?
        // (Ripostes, S1 counters, S2 take-the-ready and stall punishes do not
        // ask -- they are reactions.) Fixes "attacks at nearly every possible
        // time, even while the player is winding up":
        //   - their chain is still alive (ready, the block-tap gap of a feint,
        //     the swing in flight) -> not our turn, they may re-ready any frame
        //   - tempo budget: a self-started attack is followed by a breather
        private bool MayInitiate(Agent target, bool vsHeldReady)
        {
            if (!CombatAiState.BrainInitiative || !target.IsMainAgent
                || (_pureReactiveDefense && !SparDuelGate))
                return true;
            if (_brainClock < _nextInitiateAt || _brainClock < _watchUntil)
            {
                // 2v1 pincer: the partner is committed to a swing -> go in right
                // after it instead of waiting out our own breather / watch.
                if (!(CombatAiState.DuoAttackMode && ArenaDuoCoordinator.PartnerCommitting(Agent.Index)))
                    return false;   // tempo breather / WATCH (we gave them the turn)
            }
            if (!vsHeldReady && _brainClock >= MathF.Max(_s1BufferUntil, _s2BufferUntil))
            {
                // (Inside the post-read/max BUFFER the cancel-gap and quiet
                // vetoes are exactly what kept the AI passive during feint
                // spam -- they are lifted so it can actually attack.)
                if (PlayerCombatModel.InCancelGap)
                    return false;   // feint block-tap gap: they may re-ready any frame
                if (_noAttackElapsed < InitiateQuietSeconds)
                    return false;
            }
            return true;
        }

        private float _lastAttackStartAt = -99f;
        private float _lastParryAt = -99f;

        // AFTER A BLOCK / PARRY / DODGE (user guidance): the AI can cancel any
        // ready, hold or feint back into a guard until its OWN release, so there
        // is no reason to be passive. Choose the follow-up by how good the chance
        // is: RELEASE now (the blocked attacker is stunned -- more on a parry --
        // or whiffed past a dodge), LOAD UP a cancellable hold to keep pressure,
        // or FEINT to push the player onto the defensive.
        private DuelAttackKind PickPostBlockKind(Agent target)
        {
            if (GroupNoFeint)
                return DuelRng.NextDouble() < 0.55 ? DuelAttackKind.Normal : DuelAttackKind.Held;
            if (!CombatAiState.BrainInitiative || !target.IsMainAgent
                || (_pureReactiveDefense && !SparDuelGate))
                return DuelAttackKind.Quick;
            bool parried = _brainClock - _lastParryAt < 0.8f;
            float good = parried ? CombatAiTune.PostBlockGoodParry : CombatAiTune.PostBlockGoodBlock;
            double r = DuelRng.NextDouble();
            if (r < good * CombatAiTune.PostBlockReleaseShare)
                return DuelAttackKind.Quick;
            if (r < good * CombatAiTune.PostBlockReleaseShare + CombatAiTune.PostBlockFeint)
                return DuelAttackKind.Feint;
            return DuelAttackKind.Held;
        }

        // [INIT] one line per attack we start: why, and what the player was
        // doing -- the data for tuning initiative vs aggression.
        private void LogAttackStart(DuelAttackKind kind)
        {
            if (_initLogCount < MaxInitLogs)
            {
                _initLogCount++;
                Agent tgt = _duelTarget;
                string ps = CombatLive(tgt) ? tgt.GetCurrentActionStage(1).ToString() : "-";
                CombatAiLog.Log($"[INIT] Agent #{Agent.Index}: attack {kind} reason={_attackReason} playerStage={ps} " +
                    $"quiet={_noAttackElapsed:F2}s cancelGap={PlayerCombatModel.InCancelGap} sinceOwnAttack={_brainClock - _lastAttackStartAt:F2}s " +
                    $"initiative={_initiative:+0;-0;0} caution={_caution:F2}");
            }
            _attackReason = "?";
        }

        // Called from StartDuelAttack for every attack we start.
        private void NoteAttackStarted()
        {
            _lastAttackStartAt = _brainClock;
            FightScript.NoteAiAttackStarted();
            // Script tempo: an AI-favoured fight attacks sooner.
            _nextInitiateAt = _brainClock + Rand(InitiateGapMin, InitiateGapMax)
                * (1f + 0.3f * MathF.Max(0f, _caution)) * FightScript.TempoScale;
        }

        // Grace for a held ready: long enough that the player's windup is a
        // real chance to attack, not a 1s countdown before we swing into it.
        private float RollHoldGrace(Agent target)
            => CombatAiState.BrainInitiative && target.IsMainAgent
                && (!_pureReactiveDefense || SparDuelGate)
                ? Rand(HoldGraceMin, HoldGraceMax) * (0.85f + 0.3f * MathF.Max(0f, _caution))
                : -1f;

        // Riposte after a block: scaled by how well the swing was read (legacy
        // read), eased by panic (a rattled AI does not counter) and by caution
        // (a careful AI re-sets instead of swinging back every time).
        private float RiposteChanceVsPlayer()
            => CombatAiTune.RiposteBase * (0.45f + 0.9f * PlayerCombatModel.LastReleaseRead)
               * (1f - 0.6f * _panic) * (1f - 0.25f * MathF.Max(0f, _caution));

        private void BrainOnHit(int damage)
        {
            if (damage <= 0)
                return;
            float add = PanicOnHit + (_brainClock - _lastHitAt < 6f ? PanicOnStreak : 0f);
            _panic = BrainMath.Clamp01(_panic + add);
            _lastHitAt = _brainClock;
            DecLog($"[DEC] Agent #{Agent.Index}: took {damage} dmg -> panic={_panic:F2}");
        }

        private void BrainResetState()
        {
            _s1BitePending = false;
            _s1BufferUntil = -1f;
            _lastS2EventAt = -999f;
            _s2Pending = false;
            _s2BufferUntil = -1f;
            _ownReleaseEndAt = -99f;
            _ownWasReleasing = false;
            _s2LastFallK = 0;
            _wrongVisibleAt = -1f;
            _turnWatchUntil = -1f;
            RigResetState();                     // Session 62 rig state
            _spamCounterArmed = false;
            _guardDropAt = 0f;
            _repointFlag = Agent.MovementControlFlag.None;
        }
    }
}
