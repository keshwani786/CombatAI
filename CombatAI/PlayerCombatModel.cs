using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =======================================================================
    // PLAYER COMBAT MODEL -- the T1 brain's "read" on the human player.
    // (Design: research doc Session 18+, user spec.)
    //
    // One shared model per mission, ticked ONCE per frame by
    // CombatAiMissionBehavior and fed impact timing by OnAgentHit. Every agent
    // fighting the player reads the same model -- the model is about the
    // PLAYER'S habits, not about any one duelist.
    //
    // Two predictors + measured timing:
    //   DIRECTION -- order-1 Markov over committed attack directions
    //     P(next | previous), blended with the raw direction histogram when
    //     the transition table is thin. Confidence = empirical probability of
    //     the argmax, discounted by total sample count.
    //   TEMPO -- mean + stddev of hold time (ready-enter -> release-enter; a
    //     feint chain counts its WHOLE span, because a feint IS extra hold
    //     time). Tight spread = rhythmical spammer = predictable.
    //   TIME-TO-HIT -- measured release -> blow-resolution average for the
    //     player's current weapon (measured, never stat-read). Defaults to a
    //     typical swing until enough samples land.
    //
    // How a defender uses it (CombatAiOverrideComponent):
    //   - Predictability high -> COMMIT an early mirrored block at
    //     (predictedHold - reactionLatency). This is the "read" -- and it is
    //     exactly what makes feints work: a committed block direction is
    //     already wrong when the redirected swing lands. Feint-falling is
    //     emergent, not scripted.
    //   - Predictability low -> wait for AttackRelease, then race the agent's
    //     per-agent reaction latency against the measured time-to-hit window.
    //     A lost race = the hit lands. Information deficit, not dice.
    // =======================================================================
    public static class PlayerCombatModel
    {
        private const int MaxHistory = 20;   // Session 45: short-term window = last 20 swings (user spec)
        private const int TempoWindow = 20;
        private const int MinSamples = 6;
        private const float ChainGapSeconds = 0.55f;   // ready-gap longer than this = swing abandoned
        private const float DefaultTimeToHit = 0.24f;  // typical melee release->impact
        private const float MinTimeToHit = 0.10f;
        private const float MaxTimeToHit = 0.60f;
        // Commit threshold: combined confidence needed before a defender trusts
        // the read enough to block early.
        public const float CommitThreshold = 0.40f;

        private struct Swing
        {
            public float HoldSeconds;
            public Agent.UsageDirection Dir;
            public int PrevDir;        // DirIndex of the previous committed dir (-1 none)
            public bool Feinted;
            public byte Cancels;       // feint-cancel count inside this chain
            public float Read;         // LastReleaseRead at the moment it committed
            public byte AIDefense;     // how the AI answered (Def* consts, 0=none)
            public byte Outcome;       // Out* consts, 0=pending
            public float TimeToHit; // <0 = never observed an impact
            public float Interval;  // seconds since the previous chain started (<0 none)
            public sbyte InitIdx;   // DirIndex of the chain's FIRST held dir (-1 none)
            // Session 48: ready-segment durations of the chain. Seg k = how long
            // the ready lasted after k feints, until the next cancel (k < Cancels)
            // or the release (k == Cancels). Feeds the state-conditioned forecast.
            public float Seg0, Seg1, Seg2, Seg3;
        }

        // How the AI answered a committed swing (AIDefense codes).
        public const int DefNone = 0;
        public const int DefCommitBlock = 1;  // early predicted-line commit
        public const int DefReactBlock = 2;   // release-edge reaction block
        public const int DefDodge = 3;        // step/clip dodge
        public const int DefFroze = 4;        // surprise freeze -- no answer
        // Swing outcomes (Outcome codes).
        public const int OutPending = 0;
        public const int OutBlocked = 1;
        public const int OutParried = 2;
        public const int OutLanded = 3;       // clean hit on the AI
        public const int OutNoConnect = 4;    // whiffed / dodged / out of reach

        private static readonly List<Swing> _history = new List<Swing>(MaxHistory);
        private static readonly int[] _dirCounts = new int[8];
        private static readonly int[,] _transCounts = new int[8, 8];
        // Held->released pattern table (windowed like _history): [dir first
        // HELD at chain start, dir actually RELEASED]. Feints preserve the
        // original held dir, so a repeated hold-up-release-left habit is
        // learned directly.
        private static readonly int[,] _patCounts = new int[4, 4];
        private static Agent.UsageDirection _lastCommittedDir = Agent.UsageDirection.None;

        private static float _clock;
        private static bool _inChain;
        private static float _chainStart;
        private static int _chainCancels;
        private static Agent.UsageDirection _chainInitDir; // first held dir of the current chain
        private static float _releaseStart = -1f;
        private static bool _wasReleasing;
        private static float _gapElapsed;
        private static float _prevChainStart = -1f;
        private static Mission _mission;

        // ---- live outputs (recomputed each time a swing records) ----
        public static bool PlayerInReady { get; private set; }
        // Seconds since the player last held ANY attack-ready pose. Defenders
        // use it at the release edge to spot no-ready attacks (chambers) even
        // when their input callback runs before this model's tick.
        public static float SecondsSinceReadySeen { get; private set; } = 999f;
        private static float _lastReadyTick = -999f;
        public static float ReadyElapsed { get; private set; }
        public static int SwingsObserved { get; private set; }
        public static float Predictability { get; private set; }
        public static float DirConfidence { get; private set; }
        public static float TempoConfidence { get; private set; }
        public static Agent.UsageDirection PredictedDir { get; private set; } = Agent.UsageDirection.None;
        public static float PredictedHold { get; private set; }
        // Session 46: predicted inter-attack cadence (chain-start to chain-
        // start). This is the "arrives on the beat" signal -- a metronome
        // player's next attack comes when expected; an off-beat release is
        // the surprise. Confidence rides on interval spread.
        public static float PredictedInterval { get; private set; }
        public static float IntervalConfidence { get; private set; }
        public static float TimeToHitEstimate { get; private set; } = DefaultTimeToHit;
        public static float FeintRate { get; private set; }

        // ---- EXPECTATION RATING (Session 45, user spec) ----
        // The rating is LIVE: it moves every tick while the player's move
        // unfolds, not just at release. Mechanics:
        //   - a fresh chain seeds it from prior knowledge (Predictability);
        //   - as the held move matures toward its predicted release point the
        //     cap rises -- a move reaching its end is easier to track;
        //   - each feint-cancel and each mid-ready direction switch (camera /
        //     body feint) destroys part of the current read, which then has
        //     to rebuild at the ramp rate;
        //   - a chain held far past its predicted time, or spam-feinted,
        //     saturates to ~0.85 -- stalling is itself readable, and the
        //     ChainOverheld flag tells defenders to stop waiting and attack;
        //   - at release, LastReleaseRead fuses the live rating with how
        //     well the actual hold time matched the predicted tempo --
        //     on-beat releases were tracked the whole way = easy blocks.
        //   - idle: the rating decays back toward a resting level set by
        //     long-term predictability.
        public static float Expectation { get; private set; }
        public static float LastReleaseRead { get; private set; }
        // Held->released pattern term of the most recent release (0.5 = no
        // data / non-attack dirs; <0.5 unexpected, up to 0.85 well-learned).
        public static float LastPatternTerm { get; private set; }
        public static bool ChainOverheld { get; private set; }
        private static float _expect;
        private static Agent.UsageDirection _liveDir = Agent.UsageDirection.None;

        // ---- FORECAST (Session 47, user's revised architecture; defense design doc section 14) ----
        // The expectation system as a PREDICTOR + MODULATOR, not a gate. Before
        // the player acts it forecasts the NEXT move TYPE from short-term
        // context (the 20-swing window, order-2 -> order-1 -> order-0 backoff)
        // and long-term memory (save-persistent unigram + order-1 tables as
        // the prior). When the move unfolds, the model scores the forecast at
        // the first observable event (hold confirmed / feint / release) and the
        // defender systems (S1 hold-reaction, S2 feint-read, S3 release-block)
        // scale THEIR OWN odds by how well it matched. It never decides a
        // defense by itself.
        public const int MvQuick = 0, MvNormal = 1, MvHold = 2, MvFeint = 3, MvCount = 4;
        private const float QuickMaxHold = 0.30f;     // ready->release shorter than this = quick attack
        private const float HoldMinHold = 0.60f;      // ready held at least this long = a real hold
        private const float ObserveMin = 0.30f;       // the AI "sees" a hold confirmed after 0.30-0.40s of ready
        private const float ObserveMax = 0.40f;
        private const float FcPriorStrength = 4f;     // weight of the long-term prior vs the window
        private const float FcCtxStrength = 1.5f;     // small = context sharpens FAST (high-cap fast learning)
        private static readonly Random _fcRng = new Random();
        private static readonly int[] _ltMvCounts = new int[MvCount];
        private static readonly int[,] _ltMvTrans = new int[MvCount, MvCount];
        private static int _ltLastMv = -1;
        // Live pre-forecast for the NEXT chain; ChainForecast is the snapshot
        // taken when the current chain began (what the AI believed BEFORE the
        // player acted -- the thing that gets scored).
        public static readonly float[] Forecast = new float[MvCount];
        public static readonly float[] ChainForecast = new float[MvCount];
        public static float ForecastReliability { get; private set; }
        // How CONFIDENT the pre-forecast was when this chain began (0 = flat /
        // unsure, 1 = one move clearly favoured). A forecast that was not sure
        // can neither embarrass nor reward the AI -- only a confident one does.
        public static float ChainSharpness { get; private set; }
        public static int ChainSerial { get; private set; }          // ++ per fresh chain
        public static int FeintEventSerial { get; private set; }     // ++ per cancel edge / in-ready direction switch
        public static int ChainCancelCount => _chainCancels;
        // The player cancelled a ready (feint block-tap) and may re-ready any frame:
        // NOT a moment to start our own attack. (After a RELEASE the player is
        // committed and recovering -- that is our opening, not a gap.)
        public static bool InCancelGap => !_inChain && _chainCancels > 0 && _gapElapsed <= ChainGapSeconds;
        public static bool HoldConfirmed { get; private set; }       // ready held past the observe time, no feint yet
        public static float HoldExpect { get; private set; }         // forecast mass that said "hold" at that moment
        public static float LastFeintExpect { get; private set; }    // forecast mass that said "feint" at the last cancel
        public static int LastFeintK { get; private set; }           // which feint of the chain (1 = first)
        public static Agent.UsageDirection LastFeintFrom { get; private set; } // the line the cancelled ready was held on
        public static int ReleaseSerial { get; private set; }        // ++ per committed release
        public static int LastReleaseMove { get; private set; }
        public static float LastReleaseHold { get; private set; }
        public static float LastReleaseExpect { get; private set; }  // forecast mass for what the release turned out to be
        public static int LastReleaseCancels { get; private set; }
        public static bool LastReleaseNoReady { get; private set; }  // release with no observed ready pose
        private static float _observeAt = 0.35f;

        public static int ClassifyMove(float hold, int cancels)
            => cancels > 0 ? MvFeint : hold < QuickMaxHold ? MvQuick : hold < HoldMinHold ? MvNormal : MvHold;

        public static string MoveName(int mv)
            => mv == MvQuick ? "quick" : mv == MvNormal ? "normal" : mv == MvHold ? "hold" : mv == MvFeint ? "feint" : "?";

        private static int MoveOf(Swing s) => ClassifyMove(s.HoldSeconds, s.Cancels);

        // ---- Session 48: LIVE, STATE-CONDITIONED forecast (user spec) ----
        // The forecast is re-run from the player's CURRENT state, not once per
        // chain. A CHECKPOINT is made when a ready begins (fresh, or after a
        // feint) and again every CheckpointWindow seconds the ready goes on. It
        // answers, for the next CheckpointWindow seconds and from the state
        // (k feints so far, t seconds into this ready): will he RELEASE, FEINT
        // (again) or keep HOLDING? Evidence:
        //   (a) competing-risk counts over the recent window of swings -- how long
        //       ready segments lasted and how they ended, so it is conditioned on
        //       how long he has already held and how many feints he already did
        //       (short context);
        //   (b) smoothed toward the pre-forecast over move types (order-2
        //       context + long-term memory + the pattern-break transform) and the
        //       feint-continuation habit.
        // Events are scored against the active checkpoint: hold-confirm against
        // its H mass, a feint against F, a release against R.
        private const float CheckpointWindow = 0.45f;
        public static float CpR { get; private set; }
        public static float CpF { get; private set; }
        public static float CpH { get; private set; }
        public static float CpSharp { get; private set; }   // how sure the checkpoint was (0 = flat)
        public static float CpT { get; private set; }        // seconds into the ready it was made
        public static int CpK { get; private set; }          // feints so far when it was made
        private static float _cpClock = -999f;
        private static bool _cpOpen;
        private static float _segStart;
        private static readonly float[] _segDur = new float[4];
        // Base rate of each checkpoint outcome for THIS player (EMA at checkpoint
        // resolution). An event's score is (forecast mass - base rate): only a
        // forecast that knows more than the base rate moves anyone's odds.
        private static float _baseR = 0.50f, _baseF = 0.25f, _baseH = 0.25f;
        public static float HoldBase => _baseH;
        public static float LastFeintBase { get; private set; }
        public static float LastReleaseBase { get; private set; }

        // Pattern break (user spec: "spot it or rate it").
        public static float BreakRisk { get; private set; }   // P(the current run of one move type ends with the next move)
        public static int RunLength { get; private set; }
        public static int BreakSpottedSerial { get; private set; }
        public static string LastBreakNote { get; private set; } = "";
        private static float _breakAware;   // 0..1 after a spotted break: flatten the forecast + relearn fast
        private static float _dirBreak;     // 0..1 after a broken held->released habit
        private const int MaxFcLogs = 300;
        private static int _fcLogCount;

        // ---- Session 50: ready-state FEINT CHAIN tracker ----
        // A feint chain is every consecutive ready state: a block-tap cancel
        // into a re-ready continues it, a direction switch INSIDE a ready is a
        // feint event for the AI, and it ends on release, on ~1.25s with no
        // new ready, or when the AI reads it / hits the max (EndFeintChain).
        // Separate from the _chainCancels machinery (that feeds the forecast
        // and the no-attack veto; untouched).
        public const float FeintChainGapSeconds = 1.25f;
        private const float FeintDirStableSeconds = 0.05f;
        public static bool FeintChainAlive { get; private set; }
        public static int FeintChainLen { get; private set; }     // confirmed feints in this chain
        public static int FeintChainSerial { get; private set; }  // ++ when a chain starts
        private static Agent.UsageDirection _fcConfirmedDir = Agent.UsageDirection.None;
        private static Agent.UsageDirection _fcCandDir = Agent.UsageDirection.None;
        private static float _fcCandSince;
        private static float _fcLeftAt = -999f;
        private static bool _fcPendingCancel;
        // Session 61: block-tap spam guard. Holding attack while tapping block
        // cycles ready->defend->ready in ~0.1s -- the stage says AttackReady
        // but no windup ever shows on screen. A cancel edge only counts as a
        // feint when the killed ready was visibly committed (FeintReadyDwell)
        // or a chain already confirmed a real feint (len >= 1), and rapid
        // edges with no direction change between them stop counting at all.
        private const int FeintSpamCancels = 3;
        private const float FeintSpamWindowSeconds = 1.5f;
        private static float _cancelWindowStart = -999f;
        private static int _cancelWindowCount;
        private static bool _fcHadUnconfirmed;  // a cancel edge fired but no new-direction re-ready confirmed it yet

        public static void EndFeintChain(string reason)
        {
            if (FeintChainAlive)
                FcLog($"[CHAIN] over: {reason} (len was {FeintChainLen})");
            FeintChainAlive = false;
            FeintChainLen = 0;
            _fcConfirmedDir = Agent.UsageDirection.None;
            _fcCandDir = Agent.UsageDirection.None;
            _fcPendingCancel = false;
            _fcHadUnconfirmed = false;
        }

        // A direction held stably (~50ms) inside a ready confirms it -- the
        // first frames of a re-ready still report the PREVIOUS direction.
        private static void FcConfirmDir(Agent.UsageDirection newDir)
        {
            if (!FeintChainAlive)
            {
                FeintChainAlive = true;
                FeintChainLen = 0;
                FeintChainSerial++;
                _fcConfirmedDir = newDir;
                _fcPendingCancel = false;
                _fcHadUnconfirmed = false;
                FcLog($"[CHAIN] start dir={newDir} (serial {FeintChainSerial})");
                return;
            }
            if (newDir != _fcConfirmedDir)
            {
                if (_fcPendingCancel)
                {
                    // Cancel -> re-ready: the AI already evaluated this feint at
                    // the cancel edge -- just confirm the new line.
                    FeintChainLen++;
                    _cancelWindowCount = 0;   // a confirmed direction change clears spam suspicion
                    _fcConfirmedDir = newDir;
                    _fcPendingCancel = false;
                    _fcHadUnconfirmed = false;
                    FcLog($"[CHAIN] feint #{FeintChainLen} (cancel+re-ready)");
                }
                else if (FeintChainLen > 0 || _clock - _segStart >= CombatAiTune.FeintReadyDwell)
                {
                    // Direction switched INSIDE the ready, no cancel -- that IS
                    // a feint event for the AI.
                    FeintChainLen++;
                    _cancelWindowCount = 0;
                    _fcHadUnconfirmed = false;
                    LastFeintK = FeintChainLen;
                    LastFeintFrom = _fcConfirmedDir;
                    LastFeintExpect = CpF;
                    LastFeintBase = _baseF;
                    FeintEventSerial++;
                    FcLog($"[CHAIN] feint #{FeintChainLen} {_fcConfirmedDir}->{newDir} (in-ready direction switch)");
                    _fcConfirmedDir = newDir;
                    _fcPendingCancel = false;
                }
                else
                {
                    // A direction flick inside a barely-visible ready (hold-
                    // attack + tap-block spam) is not a feint event either --
                    // and it does not grow the chain length.
                    _fcConfirmedDir = newDir;
                    _fcPendingCancel = false;
                    FcLog($"[CHAIN] dir->{newDir} ignored (ready {_clock - _segStart:F2}s, below feint dwell)");
                }
            }
            else
                _fcPendingCancel = false;   // same-direction re-ready: chain stays alive, not counted
        }

        private static float SegOf(Swing s, int k) => k <= 0 ? s.Seg0 : k == 1 ? s.Seg1 : k == 2 ? s.Seg2 : s.Seg3;

        private static void WindowForecast(int k, float t, out float R, out float F, out float H)
        {
            int risk = 0, rel = 0, can = 0;
            for (int i = 0; i < _history.Count; i++)
            {
                Swing s = _history[i];
                if (s.Cancels < k)
                    continue;
                float d = SegOf(s, k);
                if (d <= t)
                    continue;            // that ready was already over by now
                risk++;
                if (d <= t + CheckpointWindow)
                {
                    if (s.Cancels == k) rel++; else can++;
                }
            }
            float pF, pR;
            if (k == 0)
            {
                float f = ChainForecast[MvFeint];
                float q = ChainForecast[MvQuick] + ChainForecast[MvNormal];
                float h = ChainForecast[MvHold];
                if (t > CheckpointWindow)
                    q *= 0.4f;           // quick/normal types are mostly over by now
                float sum = f + q + h;
                if (sum < 0.01f) sum = 1f;
                pF = f / sum;
                pR = q / sum;
            }
            else
            {
                float c = FeintContinueProb(k);
                pF = c * 0.8f;
                pR = (1f - c) * 0.6f;
            }
            const float A = 3f;
            R = (rel + A * pR) / (risk + A);
            F = (can + A * pF) / (risk + A);
            H = MathF.Max(0f, 1f - R - F);
        }

        private static void MakeCheckpoint(float tInSeg)
        {
            WindowForecast(_chainCancels, tInSeg, out float r, out float f, out float h);
            CpR = r; CpF = f; CpH = h;
            CpT = tInSeg;
            CpK = _chainCancels;
            _cpClock = _clock;
            _cpOpen = true;
            float mx = MathF.Max(r, MathF.Max(f, h));
            CpSharp = MathF.Max(0f, MathF.Min(1f, (mx - 0.45f) / 0.35f));
        }

        // outcome: 0 = released, 1 = feinted, 2 = still holding when the window ended.
        private static void ResolveCheckpoint(int outcome)
        {
            if (!_cpOpen)
                return;
            _cpOpen = false;
            const float a = 0.10f;
            _baseR += a * ((outcome == 0 ? 1f : 0f) - _baseR);
            _baseF += a * ((outcome == 1 ? 1f : 0f) - _baseF);
            _baseH += a * ((outcome == 2 ? 1f : 0f) - _baseH);
        }

        private static void FcLog(string msg)
        {
            if (_fcLogCount >= MaxFcLogs)
                return;
            _fcLogCount++;
            CombatAiLog.Log(msg);
        }

        // P(the player feints AGAIN | already feinted k times) from the window
        // history, smoothed toward the long-term feint rate.
        public static float FeintContinueProb(int k)
        {
            int a = 0, b = 0;
            for (int i = 0; i < _history.Count; i++)
            {
                if (_history[i].Cancels >= k) a++;
                if (_history[i].Cancels >= k + 1) b++;
            }
            float prior = _ltSwings > 0 ? MathF.Max(0.15f, MathF.Min(0.6f, _ltFeintSum / _ltSwings * 1.2f)) : 0.3f;
            return (b + 2f * prior) / (a + 2f);
        }

        // Held->released learned pattern: the release direction the player
        // habitually uses after holding `held` (i.e. their feint destination
        // habit). Returns false until enough repeats exist.
        public static bool PredictRelease(Agent.UsageDirection held, out Agent.UsageDirection predicted, out float conf)
        {
            predicted = held;
            conf = 0f;
            int hi = DirIndex(held);
            if (hi < 0)
                return false;
            int n = 0, best = -1, bc = 0;
            for (int f = 0; f < 4; f++)
            {
                n += _patCounts[hi, f];
                if (_patCounts[hi, f] > bc) { bc = _patCounts[hi, f]; best = f; }
            }
            if (n < 3 || best < 0)
                return false;
            conf = bc / (float)n * MathF.Min(1f, n / 4f);
            conf *= 1f - 0.6f * _dirBreak;   // the habit was just broken: trust it less for a while
            predicted = (Agent.UsageDirection)best;
            return true;
        }

        private static void RecomputeForecast()
        {
            int n = _history.Count;
            // After a spotted pattern break the old habit is stale: relearn from
            // only the most recent swings (fast adaptation) until it fades.
            int s0 = _breakAware > 0.3f ? Math.Max(0, n - 8) : 0;
            int ne = n - s0;
            float ltN = 0f;
            for (int j = 0; j < MvCount; j++) ltN += _ltMvCounts[j];
            float[] o0 = new float[MvCount];
            float[] o1 = new float[MvCount];
            int[] c0 = new int[MvCount];
            int[,] c1 = new int[MvCount, MvCount];
            int[,,] c2 = new int[MvCount, MvCount, MvCount];
            int prev = -1, prev2 = -1;
            for (int i = s0; i < n; i++)
            {
                int m = MoveOf(_history[i]);
                c0[m]++;
                if (prev >= 0) c1[prev, m]++;
                if (prev >= 0 && prev2 >= 0) c2[prev2, prev, m]++;
                prev2 = prev;
                prev = m;
            }
            if (n > 0 && prev < 0)
                prev = MoveOf(_history[n - 1]);
            for (int j = 0; j < MvCount; j++)
            {
                float plt = (_ltMvCounts[j] + 1f) / (ltN + MvCount);
                o0[j] = (c0[j] + FcPriorStrength * plt) / (ne + FcPriorStrength);
            }
            float ltRow = 0f;
            int n1 = 0, n2 = 0;
            if (prev >= 0)
                for (int j = 0; j < MvCount; j++) { ltRow += _ltMvTrans[prev, j]; n1 += c1[prev, j]; }
            if (prev >= 0 && prev2 >= 0)
                for (int j = 0; j < MvCount; j++) n2 += c2[prev2, prev, j];
            for (int j = 0; j < MvCount; j++)
            {
                if (prev < 0) { o1[j] = o0[j]; continue; }
                float prior = (_ltMvTrans[prev, j] + 2f * o0[j]) / (ltRow + 2f);
                o1[j] = (c1[prev, j] + FcCtxStrength * prior) / (n1 + FcCtxStrength);
            }
            for (int j = 0; j < MvCount; j++)
                Forecast[j] = (prev >= 0 && prev2 >= 0)
                    ? (c2[prev2, prev, j] + FcCtxStrength * o1[j]) / (n2 + FcCtxStrength)
                    : o1[j];

            // ---- PATTERN-BREAK EXPECTATION ----
            // A player who has repeated one move type r times in a row usually
            // breaks the run at some point. From this player's own completed runs
            // (window), P(the run ends at length r | it got to r) -- when it is
            // high the forecast moves mass OFF the run's move and onto the
            // alternatives, i.e. it expects the player to switch it up.
            RunLength = 0;
            BreakRisk = 0f;
            if (n >= 2)
            {
                int lastM = MoveOf(_history[n - 1]);
                int r = 0;
                for (int i = n - 1; i >= 0 && MoveOf(_history[i]) == lastM; i--) r++;
                RunLength = r;
                if (r >= 2)
                {
                    int ended = 0, atRisk = 0;
                    int runLen = 0, runMv = -1;
                    for (int i = 0; i < n - r; i++)
                    {
                        int m = MoveOf(_history[i]);
                        if (m == runMv) runLen++;
                        else
                        {
                            if (runMv >= 0) { if (runLen == r) ended++; if (runLen >= r) atRisk++; }
                            runMv = m;
                            runLen = 1;
                        }
                    }
                    if (runMv >= 0) { if (runLen == r) ended++; if (runLen >= r) atRisk++; }
                    BreakRisk = (ended + 0.3f * 2f) / (atRisk + 2f);
                    float b = BreakRisk * 0.8f;
                    float dm = Forecast[lastM] * b;
                    float others = 1f - Forecast[lastM];
                    Forecast[lastM] -= dm;
                    for (int j = 0; j < MvCount; j++)
                        if (j != lastM)
                            Forecast[j] += others > 0.001f ? dm * Forecast[j] / others : dm / (MvCount - 1);
                }
            }
            if (_breakAware > 0.01f)
            {
                float a = 0.35f * _breakAware;   // just got fooled: do not be so sure of ANY habit
                for (int j = 0; j < MvCount; j++)
                    Forecast[j] = (1f - a) * Forecast[j] + a * (1f / MvCount);
            }
            ForecastReliability = MathF.Min(1f, (n + 0.15f * (float)Math.Max(0, _ltSwings - n)) / 10f);
        }

        private static void ResetForecastLive()
        {
            for (int j = 0; j < MvCount; j++)
            {
                Forecast[j] = 1f / MvCount;
                ChainForecast[j] = 1f / MvCount;
            }
            ForecastReliability = 0f;
            ChainSharpness = 0f;
            ChainSerial = 0;
            FeintEventSerial = 0;
            HoldConfirmed = false;
            HoldExpect = 0f;
            LastFeintExpect = 0f;
            LastFeintK = 0;
            ReleaseSerial = 0;
            LastReleaseMove = MvQuick;
            LastReleaseHold = 0f;
            LastReleaseExpect = 0f;
            LastReleaseCancels = 0;
            LastReleaseNoReady = false;
            LastFeintBase = 0.25f;
            LastReleaseBase = 0.25f;
            _observeAt = 0.35f;
            CpR = CpF = CpH = 1f / 3f;
            CpSharp = 0f;
            CpT = 0f;
            CpK = 0;
            _cpClock = -999f;
            _cpOpen = false;
            _segStart = 0f;
            Array.Clear(_segDur, 0, _segDur.Length);
            _baseR = 0.50f;
            _baseF = 0.25f;
            _baseH = 0.25f;
            BreakRisk = 0f;
            RunLength = 0;
            BreakSpottedSerial = 0;
            LastBreakNote = "";
            _breakAware = 0f;
            _dirBreak = 0f;
        }

        private const float ExpectSeedBase = 0.30f;      // rating floor at chain start
        private const float ExpectSeedPrior = 0.40f;     // share of Predictability folded into the seed
        private const float ExpectRampPerSec = 1.4f;     // rebuild rate while a move is being tracked
        private const float ExpectFeintDrop = 0.35f;     // one cancel = the read is broken
        private const float ExpectDirSwitchDrop = 0.18f; // mid-ready direction change (body feint)
        // The first 1-3 frames of a ready report the PREVIOUS direction before the real one
        // (161 such corrections in one test, 158 of them within 0.03s): not a re-aim.
        private const float StaleDirSeconds = 0.08f;
        private const float ExpectStallSaturation = 0.85f;
        private const float StallHoldFactor = 1.8f;      // chain > predicted hold * 1.8 = over-held
        private const float StallMinSeconds = 1.2f;      // never flag stalls before this absolute time
        private const int StallFeintCount = 3;           // this many cancels = spam
        private const float ExpectRestScale = 0.5f;      // idle rating = Predictability * this
        private const float ExpectRestApproach = 1.5f;   // 1/s decay toward resting level
        private const float MaxIntervalSeconds = 3.0f;   // longer gap = an exchange break, not rhythm
        private const float IntervalStdFull = 0.45f;     // this much interval spread = no rhythm read

        // ---- LONG-TERM MEMORY (Session 45, user spec) ----
        // Never cleared by Reset() -- missions come and go; this is the
        // "this player, this save" layer. Persisted inside the campaign save
        // by CombatAiMemoryBehavior (IDataStore), and dumped to the log for
        // analysis via combatai.memory. The short-term 20-swing window is
        // the current-fight read; these tables answer when the window is
        // thin, and grow sharper with every duel in the save.
        private static readonly int[] _ltDirCounts = new int[8];
        private static readonly int[,] _ltTransCounts = new int[8, 8];
        private static float _ltHoldSum;
        private static float _ltFeintSum;
        private static float _ltIntervalSum;
        private static int _ltIntervalN;
        private static int _ltSwings;
        private static Agent.UsageDirection _ltLastDir = Agent.UsageDirection.None;
        public static int LongTermSwings => _ltSwings;
        // Long-term outcome/defense context (user spec: memory must carry the
        // gist of the FIGHT -- what the player did AND how the AI answered).
        private static readonly int[,] _ltOutcomeByDir = new int[8, 5]; // [dir][Out*]
        private static readonly int[] _ltDefense = new int[5];          // [Def*]
        private static int _ltDefCountedKind;                            // last kind counted this swing (once per swing/kind)

        // Back-stamp support: the swing records at release-commit, but the
        // AI's answer (block/dodge/freeze) resolves during the release that
        // follows -- MarkDefense() stamps the freshest swing within this
        // window. _pendingDefense carries a defense declared mid-ready (an
        // armed/fired commit) forward to the record.
        private static int _pendingDefense;
        private static float _lastRecordTime = -999f;
        private const float DefenseStampWindow = 1.5f;

        public static void Reset()
        {
            _history.Clear();
            Array.Clear(_dirCounts, 0, _dirCounts.Length);
            Array.Clear(_transCounts, 0, _transCounts.Length);
            Array.Clear(_patCounts, 0, _patCounts.Length);
            _lastCommittedDir = Agent.UsageDirection.None;
            _inChain = false;
            _chainCancels = 0;
            _chainInitDir = Agent.UsageDirection.None;
            _releaseStart = -1f;
            _wasReleasing = false;
            _gapElapsed = 0f;
            _prevChainStart = -1f;
            _clock = 0f;
            _mission = null;
            PlayerInReady = false;
            _lastReadyTick = -999f;
            SecondsSinceReadySeen = 999f;
            ReadyElapsed = 0f;
            SwingsObserved = 0;
            Predictability = 0f;
            DirConfidence = 0f;
            TempoConfidence = 0f;
            PredictedDir = Agent.UsageDirection.None;
            PredictedHold = 0f;
            PredictedInterval = 0f;
            IntervalConfidence = 0f;
            TimeToHitEstimate = DefaultTimeToHit;
            FeintRate = 0f;
            // Live expectation resets with the mission (it's about the CURRENT
            // unfolding move); the _lt* long-term tables deliberately do NOT
            // reset -- they persist across missions and into the save file.
            _expect = 0f;
            Expectation = 0f;
            LastReleaseRead = 0f;
            ChainOverheld = false;
            _liveDir = Agent.UsageDirection.None;
            _pendingDefense = DefNone;
            _ltDefCountedKind = DefNone;
            _lastRecordTime = -999f;
            LastPatternTerm = 0.5f;
            FeintChainAlive = false;
            FeintChainLen = 0;
            FeintChainSerial = 0;
            _fcConfirmedDir = Agent.UsageDirection.None;
            _fcCandDir = Agent.UsageDirection.None;
            _fcCandSince = 0f;
            _fcLeftAt = -999f;
            _fcPendingCancel = false;
            _fcHadUnconfirmed = false;
            _cancelWindowStart = -999f;
            _cancelWindowCount = 0;
            ResetForecastLive();
            RecomputeForecast(); // restored long-term memory informs the very first forecast
        }

        // Called once per OnMissionTick by CombatAiMissionBehavior.
        public static void Tick(float dt, Agent player)
        {
            _clock += dt;
            Mission m = player?.Mission;
            if (m != _mission)
            {
                // New mission (or first tick): wipe everything -- habits are
                // per-fight and stale cross-mission reads poison the model.
                Reset();
                _mission = m;
                _clock = dt;
            }
            if (m == null || !player.IsActive())
                return;

            Agent.ActionStage stage = player.GetCurrentActionStage(1);
            bool ready = stage == Agent.ActionStage.AttackReady
                || stage == Agent.ActionStage.AttackQuickReady;
            bool releasing = stage == Agent.ActionStage.AttackRelease;

            // [PSTAGE] trace: every change of (stage, action type, direction) while a
            // chain is alive -- the ground truth for what a windup / feint looks like
            // to the engine (short-feint detection depends on it).
            if (_traceCount < MaxTrace && (_inChain || _chainCancels > 0 || stage != Agent.ActionStage.None))
            {
                Agent.ActionCodeType trType = player.GetCurrentActionType(1);
                Agent.UsageDirection trDir = player.GetCurrentActionDirection(1);
                if (stage != _trStage || trType != _trType || trDir != _trDir)
                {
                    _trStage = stage;
                    _trType = trType;
                    _trDir = trDir;
                    _traceCount++;
                    CombatAiLog.Log($"[PSTAGE] t={_clock:F2} stage={stage} type={trType} dir={trDir} prog={player.GetCurrentActionProgress(1):F2} " +
                        $"inChain={(_inChain ? 1 : 0)} cancels={_chainCancels} gap={_gapElapsed:F2}");
                }
            }

            PlayerInReady = ready;
            if (ready)
                _lastReadyTick = _clock;
            SecondsSinceReadySeen = _clock - _lastReadyTick;
            ReadyElapsed = _inChain ? _clock - _chainStart : 0f;

            if (ready)
            {
                // Session 46 feint-chain fix: a re-ready inside the cancel-gap
                // window CONTINUES the same chain -- the feint count, original
                // chain start and (damaged) expectation all carry over, because
                // a feint is part of the move, not a new move. MUST be decided
                // before _gapElapsed is zeroed below.
                bool continuing = !_inChain && _chainCancels > 0 && _gapElapsed <= ChainGapSeconds;
                _gapElapsed = 0f;
                Agent.UsageDirection held = player.GetCurrentActionDirection(1);
                // Session 50 feint-chain tracker: a held direction only counts
                // once it is stable (~50ms) -- the first frames of a re-ready
                // still report the PREVIOUS direction (see the [PSTAGE] trace).
                if (DirIndex(held) >= 0)
                {
                    if (held != _fcCandDir)
                    {
                        _fcCandDir = held;
                        _fcCandSince = _clock;
                    }
                    else if (_clock - _fcCandSince >= FeintDirStableSeconds && held != _fcConfirmedDir)
                        FcConfirmDir(held);
                }
                if (!_inChain)
                {
                    _inChain = true;
                    if (!continuing)
                    {
                        _chainStart = _clock;
                        _chainCancels = 0;
                        // A fresh move starts partly unreadable; prior
                        // knowledge seeds the floor (a heavily-modeled player
                        // starts read). A feint-heavy player's seed is capped:
                        // every commit from them is inherently less trusted.
                        float seed = ExpectSeedBase + ExpectSeedPrior * Predictability;
                        _expect = MathF.Min(seed, 1f - FeintRate * 0.6f);
                        ChainOverheld = false;
                        _chainInitDir = held;
                        // Session 47: snapshot what we BELIEVED before this move
                        // began -- that is the thing the events below score.
                        ChainSerial++;
                        Array.Copy(Forecast, ChainForecast, MvCount);
                        {
                            float fmax = 0f;
                            for (int fi2 = 0; fi2 < MvCount; fi2++)
                                if (ChainForecast[fi2] > fmax) fmax = ChainForecast[fi2];
                            ChainSharpness = MathF.Max(0f, MathF.Min(1f, (fmax - 0.30f) / 0.40f));
                        }
                        HoldConfirmed = false;
                        _observeAt = ObserveMin + (float)_fcRng.NextDouble() * (ObserveMax - ObserveMin);
                        Array.Clear(_segDur, 0, _segDur.Length);
                    }
                    _liveDir = held;
                    // Session 48: a ready just began (fresh, or re-ready after a
                    // feint) -> run the forecast from THIS state.
                    _segStart = _clock;
                    MakeCheckpoint(0f);
                }
                else
                {
                    // Mid-ready direction switch = a body feint / camera re-aim:
                    // the direction part of the read just broke and must rebuild.
                    if (_liveDir != Agent.UsageDirection.None
                        && held != Agent.UsageDirection.None
                        && held != _liveDir && _clock - _segStart > StaleDirSeconds)
                        _expect -= ExpectDirSwitchDrop;
                    if (held != Agent.UsageDirection.None)
                        _liveDir = held;
                }
                // The first frame of a ready can report no attack direction yet;
                // capture the chain's FIRST real held direction lazily so the
                // held->released pattern table never silently stays empty.
                if (DirIndex(_chainInitDir) < 0 && DirIndex(held) >= 0)
                    _chainInitDir = held;
                // The read builds as the move matures -- cap rises toward 1 as
                // chain time approaches the predicted hold ("as the move
                // reaches its end the expectation is higher").
                float ph = PredictedHold > 0.05f ? PredictedHold : 0.6f;
                float cap = 0.55f + 0.45f * MathF.Min(1.3f, ReadyElapsed / ph);
                _expect = MathF.Min(cap, _expect + ExpectRampPerSec * dt);
                // Stall saturation: spam-feinting or holding a move way past
                // its time makes the player MORE readable, not less -- the
                // defender stops waiting and punishes (ChainOverheld).
                if (_chainCancels >= StallFeintCount
                    || (PredictedHold > 0.05f && ReadyElapsed > PredictedHold * StallHoldFactor
                        && ReadyElapsed > StallMinSeconds))
                {
                    ChainOverheld = true;
                    _expect = MathF.Max(_expect, ExpectStallSaturation);
                }
                // Session 47: the AI "sees" a HOLD once the ready outlives the
                // observe time (~0.30-0.40s) with no feint yet. The forecast mass
                // that said "hold" is what S1 scales its odds by.
                if (!HoldConfirmed && _chainCancels == 0 && _clock - _chainStart >= _observeAt)
                {
                    HoldConfirmed = true;
                    HoldExpect = CpH;            // what the live checkpoint said about "keeps holding"
                    ChainSharpness = CpSharp;
                }
                // The window ran out with the ready still up: the checkpoint's
                // answer was "holding" -- resolve it and forecast the next window
                // from the new state (he has now held even longer).
                if (_clock - _cpClock >= CheckpointWindow)
                {
                    ResolveCheckpoint(2);
                    MakeCheckpoint(_clock - _segStart);
                }
            }
            else if (releasing)
            {
                _gapElapsed = 0f;
                EndFeintChain("released");
                if (_inChain)
                {
                    float hold = _clock - _chainStart;
                    // Session 46: the tempo signal the spec actually asked for
                    // is the INTERVAL between attacks -- does this swing arrive
                    // on the beat the player has been attacking on? Hold-time
                    // matching was self-fulfilling (any consistent player
                    // trivially "matches" their own mean) and is kept only as
                    // a minor term.
                    float interval = _prevChainStart >= 0f ? _chainStart - _prevChainStart : -1f;
                    if (interval > MaxIntervalSeconds)
                        interval = -1f; // a pause between exchanges is not rhythm data
                    float holdMatch = PredictedHold > 0.05f
                        ? 1f - MathF.Min(1f, MathF.Abs(hold - PredictedHold) / PredictedHold)
                        : 0.5f;
                    float intMatch = (interval > 0f && PredictedInterval > 0.05f)
                        ? 1f - MathF.Min(1f, MathF.Abs(interval - PredictedInterval) / PredictedInterval)
                        : 0.5f;
                    float match = holdMatch * 0.45f + intMatch * 0.55f;
                    // Record: total chain time is the hold (feint cancels
                    // inflate it), direction is the COMMITTED action
                    // direction (ground truth, not live aim).
                    Agent.UsageDirection dir = player.GetCurrentActionDirection(1);
                    // Session 46h: held->released PATTERN term -- does this
                    // chain's first held direction predict this release from
                    // the windowed table? Fast learning, high cap: ~3 repeats
                    // of a pattern and it reads as expected (cap 0.85).
                    int ii = DirIndex(_chainInitDir);
                    int fi = DirIndex(dir);
                    float pattern = 0.5f;
                    if (ii >= 0 && fi >= 0)
                    {
                        int n = 0;
                        for (int f = 0; f < 4; f++)
                            n += _patCounts[ii, f];
                        if (n >= 2)
                        {
                            float pf = _patCounts[ii, fi] / (float)n;
                            pattern = MathF.Max(0.05f, MathF.Min(0.85f,
                                0.5f + (pf - 0.5f) * MathF.Min(1f, n / 3f)));
                        }
                    }
                    LastPatternTerm = pattern;
                    // Session 47: classify + score the release against the
                    // forecast snapshot (BEFORE RecordSwing refreshes it).
                    {
                        int mv = ClassifyMove(hold, _chainCancels);
                        LastReleaseMove = mv;
                        LastReleaseHold = hold;
                        LastReleaseCancels = _chainCancels;
                        LastReleaseNoReady = false;
                        // Scored against the LIVE checkpoint (made from the state
                        // just before this release): did it say "release"?
                        LastReleaseExpect = CpR;
                        LastReleaseBase = _baseR;
                        ChainSharpness = CpSharp;
                        ResolveCheckpoint(0);
                        _segDur[Math.Min(_chainCancels, 3)] = _clock - _segStart;
                        ReleaseSerial++;
                        HoldConfirmed = false;
                    }
                    // The swing committed. Fuse the live read, the tempo match
                    // and the held->released pattern: a release landing on the
                    // predicted beat through a learned pattern was tracked the
                    // whole way (easy block); an off-pattern release is the
                    // surprise.
                    LastReleaseRead = MathF.Max(0f, MathF.Min(1f,
                        0.45f * _expect + 0.20f * match + 0.35f * pattern));
                    RecordSwing(hold, dir, _chainCancels, interval, _chainInitDir);
                    _inChain = false;
                    _chainCancels = 0; // spent -- must not leak into the next chain
                    _releaseStart = _clock;
                    _liveDir = Agent.UsageDirection.None;
                    ChainOverheld = false;
                }
                else if (_releaseStart < 0f && !_wasReleasing)
                {
                    // Session 46b: release with NO observed ready = a CHAMBER
                    // attack (attack launched from the block pose) -- or a
                    // mid-swing spawn edge. Either way it is the most
                    // surprising move in the game: the blow materializes
                    // where a defend was expected. Record it as a real swing
                    // (it is one) with a resting-level read.
                    _chainStart = _clock;
                    float interval = _prevChainStart >= 0f ? _clock - _prevChainStart : -1f;
                    if (interval > MaxIntervalSeconds)
                        interval = -1f;
                    float intMatch = (interval > 0f && PredictedInterval > 0.05f)
                        ? 1f - MathF.Min(1f, MathF.Abs(interval - PredictedInterval) / PredictedInterval)
                        : 0.5f;
                    LastReleaseRead = MathF.Max(0f, MathF.Min(1f, _expect * 0.7f + intMatch * 0.3f));
                    LastPatternTerm = 0.5f;
                    LastReleaseMove = MvQuick;
                    LastReleaseHold = 0f;
                    LastReleaseCancels = 0;
                    LastReleaseNoReady = true;
                    LastReleaseExpect = _baseR;
                    LastReleaseBase = _baseR;
                    ChainSharpness = 0f;          // no ready was ever seen: the forecast has no say
                    Array.Clear(_segDur, 0, _segDur.Length);
                    ReleaseSerial++;
                    HoldConfirmed = false;
                    Agent.UsageDirection dir = player.GetCurrentActionDirection(1);
                    RecordSwing(0f, dir, 0, interval, Agent.UsageDirection.None);
                    _releaseStart = _clock;
                    _liveDir = Agent.UsageDirection.None;
                    ChainOverheld = false;
                }
            }
            else
            {
                // Neither ready nor releasing: cancel gap or full abandon.
                if (_inChain)
                {
                    _gapElapsed += dt;
                    _inChain = false;
                    float dwell = _clock - _segStart;
                    _segDur[Math.Min(_chainCancels, 3)] = dwell;
                    _chainCancels++;
                    _expect -= ExpectFeintDrop; // the read just broke
                    // Session 61: a cancel edge is only a FEINT if the killed
                    // ready was visibly committed (FeintReadyDwell, ~0.3s --
                    // the user's own "time in windup" rule) or a chain already
                    // confirmed a real feint (len >= 1). Holding attack while
                    // tapping block cycles ready->defend->ready in ~0.1s: the
                    // stage says AttackReady but no windup ever appears on
                    // screen, and every tap used to open a feint chain + an
                    // S2 decision the AI then fell for.
                    if (_clock - _cancelWindowStart > FeintSpamWindowSeconds)
                    {
                        _cancelWindowStart = _clock;
                        _cancelWindowCount = 0;
                    }
                    _cancelWindowCount++;
                    bool feintLike;
                    if (FeintChainAlive && FeintChainLen > 0)
                        feintLike = true;                                   // a real chain is in progress
                    else if (FeintChainAlive && _fcHadUnconfirmed)
                        feintLike = false;                                  // an earlier cancel never confirmed a new line (same-dir re-ready spam)
                    else
                        feintLike = dwell >= CombatAiTune.FeintReadyDwell && _cancelWindowCount < FeintSpamCancels;
                    if (feintLike)
                    {
                        // Session 50 feint chain: the ready just left. If no chain
                        // is alive yet (the confirm never fired, or the AI ended it
                        // a moment ago) start one on the last live direction first.
                        if (FeintChainAlive)
                        {
                            _fcPendingCancel = true;
                            _fcLeftAt = _clock;
                        }
                        else if (DirIndex(_liveDir) >= 0)
                        {
                            FeintChainAlive = true;
                            FeintChainLen = 0;
                            FeintChainSerial++;
                            _fcConfirmedDir = _liveDir;
                            _fcPendingCancel = true;
                            _fcLeftAt = _clock;
                            FcLog($"[CHAIN] start dir={_liveDir} (serial {FeintChainSerial}, from cancel edge) " +
                                $"dwell={dwell:F2} wt={player.GetActionChannelWeight(1):F2} awt={player.GetActionChannelCurrentActionWeight(1):F2} prog={player.GetCurrentActionProgress(1):F2}");
                        }
                        // A cancel edge = a feint event, scored against the LIVE
                        // checkpoint made from the state just before it (how long he
                        // had held, how many feints already): did it say "feint"?
                        // (Session 54: event timing moved BACK to the cancel edge --
                        // confirmed-direction timing was too late to answer.)
                        FeintEventSerial++;
                        LastFeintK = FeintChainLen + 1;
                        LastFeintFrom = _liveDir;
                        LastFeintExpect = CpF;
                        LastFeintBase = _baseF;
                        ChainSharpness = CpSharp;
                        ResolveCheckpoint(1);
                        _fcHadUnconfirmed = true;   // until a new-direction re-ready confirms it
                    }
                    else
                    {
                        // Not a feint: a block-tap cancel (or spam). A len-0
                        // chain built only of these is a block chain -- close
                        // it so it can't hang pending. wt/prog are logged to
                        // test whether the action channel weight/progress
                        // separates a "blink" ready from a committed one.
                        if (FeintChainAlive)
                            EndFeintChain("shallow cancel");
                        else
                            FcLog($"[CHAIN] cancel ignored: ready dwell {dwell:F2}s, cancels {_cancelWindowCount} " +
                                $"wt={player.GetActionChannelWeight(1):F2} awt={player.GetActionChannelCurrentActionWeight(1):F2} prog={player.GetCurrentActionProgress(1):F2} (block-tap, not a feint)");
                    }
                    HoldConfirmed = false;
                    _liveDir = Agent.UsageDirection.None;
                }
                else
                {
                    _gapElapsed += dt;
                    if (_chainCancels > 0 && _gapElapsed > ChainGapSeconds)
                    {
                            // Chain died without a swing: the player feinted and
                            // walked away -- still useful data (feint rate), but no
                            // committed direction exists to record.
                            _chainCancels = 0;
                            ChainOverheld = false;
                    }
                }
                // Session 50: a chain left hanging with no new ready dies after
                // FeintChainGapSeconds (~1.25s per the user spec).
                if (FeintChainAlive && _fcPendingCancel && _clock - _fcLeftAt > FeintChainGapSeconds)
                    EndFeintChain("no new ready within 1.25s");
                if (_releaseStart >= 0f && stage != Agent.ActionStage.AttackRelease)
                {
                    _releaseStart = -1f; // swing ended without an observed impact
                    // No blow ever landed: whiffed, dodged, or out of reach --
                    // all mean the swing didn't connect, which is its own
                    // information (a dodge that worked looks exactly like this).
                    if (_history.Count > 0)
                    {
                        Swing last = _history[_history.Count - 1];
                        if (last.Outcome == OutPending)
                        {
                            last.Outcome = OutNoConnect;
                            _history[_history.Count - 1] = last;
                            StampOutcome(last);
                        }
                    }
                }

                // Idle: the rating drifts back toward a resting level set by
                // long-term predictability of this player.
                float rest = Predictability * ExpectRestScale;
                _expect += (rest - _expect) * MathF.Min(1f, ExpectRestApproach * dt);
            }

            _wasReleasing = releasing;
            Expectation = MathF.Max(0f, MathF.Min(1f, _expect));
        }

        // Called by CombatAiMissionBehavior.OnAgentHit for every blow where the
        // PLAYER is the affector (blocked or clean -- impact timing is the same
        // event either way).
        // Called by CombatAiMissionBehavior.OnAgentHit for every blow where the
        // PLAYER is the affector -- collisionResult is the engine's own verdict
        // (Blocked / Parried / ChamberBlocked / clean) so the swing's outcome
        // and the AI's answer are recorded, not just the timing.
        public static void OnPlayerBlowLanded(CombatCollisionResult result)
        {
            if (_releaseStart < 0f)
                return;
            float t = _clock - _releaseStart;
            _releaseStart = -1f;
            if (t < 0.01f || t > 1.5f)
                return;
            if (_history.Count > 0)
            {
                // Stamp the timing AND the outcome onto the most recent swing.
                Swing last = _history[_history.Count - 1];
                if (last.TimeToHit < 0f)
                    last.TimeToHit = t;
                if (last.Outcome == OutPending)
                {
                    last.Outcome = (byte)(result == CombatCollisionResult.Blocked ? OutBlocked
                        : (result == CombatCollisionResult.Parried || result == CombatCollisionResult.ChamberBlocked) ? OutParried
                        : OutLanded);
                    StampOutcome(last);
                }
                _history[_history.Count - 1] = last;
            }
            RecomputeStats();
        }

        // A defending component reports how it answered the current player
        // swing -- commit-block, reactive block, dodge, or a panic freeze.
        // Called mid-ready (armed commit) or during release (race/dodge);
        // back-stamps the freshest recorded swing, or is held as pending for
        // the swing that hasn't committed yet.
        public static void MarkDefense(int kind)
        {
            _pendingDefense = kind;
            // Session 46h: count at most once per swing per kind -- the race
            // and commit paths called this every frame (5426 "defenses" for
            // 1375 swings).
            if (kind > DefNone && kind < 5 && kind != _ltDefCountedKind)
            {
                _ltDefense[kind]++;
                _ltDefCountedKind = kind;
            }
            if (_history.Count > 0 && _clock - _lastRecordTime < DefenseStampWindow)
            {
                Swing last = _history[_history.Count - 1];
                if (last.AIDefense == DefNone)
                {
                    last.AIDefense = (byte)kind;
                    _history[_history.Count - 1] = last;
                }
            }
        }

        private static void StampOutcome(Swing s)
        {
            int d = DirIndex(s.Dir);
            if (d >= 0 && s.Outcome > OutPending && s.Outcome < 5)
                _ltOutcomeByDir[d, s.Outcome]++;
        }

        private static void RecordSwing(float holdSeconds, Agent.UsageDirection dir, int cancels, float interval,
            Agent.UsageDirection initDir)
        {
            bool feinted = cancels > 0;
            int d = DirIndex(dir);
            int ii = DirIndex(initDir);

            // ---- PATTERN-BREAK SPOTTING (user spec), BEFORE this swing joins the
            // tables/history so the forecast it is judged against is intact ----
            _breakAware *= 0.6f;
            _dirBreak *= 0.6f;
            if (!LastReleaseNoReady)
            {
                int mvNew = ClassifyMove(holdSeconds, cancels);
                int am = -1;
                float amx = 0f;
                for (int j = 0; j < MvCount; j++)
                    if (ChainForecast[j] > amx) { amx = ChainForecast[j]; am = j; }
                int hn = _history.Count;
                int lastMv = hn > 0 ? MoveOf(_history[hn - 1]) : -1;
                int runBefore = 0;
                for (int i = hn - 1; lastMv >= 0 && i >= 0 && MoveOf(_history[i]) == lastMv; i--) runBefore++;
                if (amx >= 0.5f && ForecastReliability >= 0.5f && mvNew != am)
                {
                    _breakAware = 1f;
                    BreakSpottedSerial++;
                    LastBreakNote = $"expected {MoveName(am)}@{amx:F2}, got {MoveName(mvNew)}";
                    FcLog($"[FC] PATTERN BREAK spotted: {LastBreakNote} (run before={runBefore} of {MoveName(lastMv)}) -> forecast flattens + relearns from the last 8");
                }
                else if (runBefore >= 3 && mvNew != lastMv)
                {
                    _breakAware = MathF.Max(_breakAware, 0.7f);
                    BreakSpottedSerial++;
                    LastBreakNote = $"{runBefore}x {MoveName(lastMv)} then {MoveName(mvNew)}";
                    FcLog($"[FC] PATTERN BREAK spotted: run of {LastBreakNote} (breakRisk was {BreakRisk:F2})");
                }
                if (ii >= 0 && d >= 0 && PredictRelease(initDir, out Agent.UsageDirection hp, out float hc)
                    && hc >= 0.45f && DirIndex(hp) != d)
                {
                    _dirBreak = 1f;
                    BreakSpottedSerial++;
                    LastBreakNote = $"held {initDir} usually released {hp}@{hc:F2}, got {dir}";
                    FcLog($"[FC] DIRECTION HABIT BREAK spotted: {LastBreakNote}");
                }
            }

            int prev = -1;
            if (d >= 0)
            {
                _dirCounts[d]++;
                prev = DirIndex(_lastCommittedDir);
                if (prev >= 0)
                    _transCounts[prev, d]++;
                _lastCommittedDir = dir;
                if (ii >= 0)
                    _patCounts[ii, d]++;
            }

            // Session 45: the direction/transition tables are WINDOWED to the
            // same 20 swings as _history -- evicting a swing decrements its
            // contribution, so old habits decay out of the short-term read.
            _history.Add(new Swing
            {
                HoldSeconds = holdSeconds,
                Dir = dir,
                PrevDir = prev,
                Feinted = feinted,
                Cancels = (byte)cancels,
                Read = LastReleaseRead,
                AIDefense = (byte)_pendingDefense,
                Outcome = OutPending,
                TimeToHit = -1f,
                Interval = interval,
                InitIdx = (sbyte)ii,
                Seg0 = _segDur[0],
                Seg1 = _segDur[1],
                Seg2 = _segDur[2],
                Seg3 = _segDur[3]
            });
            _pendingDefense = DefNone;
            _ltDefCountedKind = DefNone;
            _lastRecordTime = _clock;
            _prevChainStart = _chainStart;
            if (_history.Count > MaxHistory)
            {
                Swing evicted = _history[0];
                _history.RemoveAt(0);
                int ed = DirIndex(evicted.Dir);
                if (ed >= 0)
                {
                    _dirCounts[ed]--;
                    if (evicted.PrevDir >= 0)
                        _transCounts[evicted.PrevDir, ed]--;
                    if (evicted.InitIdx >= 0)
                        _patCounts[evicted.InitIdx, ed]--;
                }
            }

            // Long-term accumulation (never windowed, survives Reset()).
            if (d >= 0)
            {
                _ltDirCounts[d]++;
                int lp = DirIndex(_ltLastDir);
                if (lp >= 0)
                    _ltTransCounts[lp, d]++;
                _ltLastDir = dir;
            }
            {
                int mv = ClassifyMove(holdSeconds, cancels);
                _ltMvCounts[mv]++;
                if (_ltLastMv >= 0)
                    _ltMvTrans[_ltLastMv, mv]++;
                _ltLastMv = mv;
            }
            _ltHoldSum += holdSeconds;
            _ltFeintSum += feinted ? 1f : 0f;
            if (interval > 0f) { _ltIntervalSum += interval; _ltIntervalN++; }
            _ltSwings++;

            SwingsObserved++;
            RecomputeStats();

            if (_logCount < MaxLogs)
            {
                _logCount++;
                CombatAiLog.Log($"[PMODEL] swing recorded: dir={dir} hold={holdSeconds:F2}s intv={interval:F2}s feints={cancels} " +
                    $"| predict: dir={PredictedDir}@{DirConfidence:F2} hold~{PredictedHold:F2}s@{TempoConfidence:F2} " +
                    $"intv~{PredictedInterval:F2}s@{IntervalConfidence:F2} tth={TimeToHitEstimate:F2}s " +
                    $"feintRate={FeintRate:F2} read={LastReleaseRead:F2} n={_history.Count}");
            }
        }

        // Predictability = how confident the NEXT swing's direction AND timing
        // both are. A committed early block needs both right.
        private static void RecomputeStats()
        {
            int n = _history.Count;
            float sampleScale = MathF.Min(1f, n / (float)(MinSamples * 2));

            // Direction: order-1 Markov from the last committed direction,
            // falling back to the unigram histogram when that transition row
            // is thin.
            PredictedDir = Agent.UsageDirection.None;
            DirConfidence = 0f;
            int p = DirIndex(_lastCommittedDir);
            if (p >= 0)
            {
                int rowTotal = 0;
                for (int i = 0; i < 8; i++) rowTotal += _transCounts[p, i];
                if (rowTotal >= 3)
                {
                    int best = -1, bestCount = 0;
                    for (int i = 0; i < 8; i++)
                        if (_transCounts[p, i] > bestCount) { bestCount = _transCounts[p, i]; best = i; }
                    if (best >= 0)
                    {
                        PredictedDir = (Agent.UsageDirection)best;
                        DirConfidence = bestCount / (float)rowTotal;
                    }
                }
            }
            if (DirConfidence <= 0f && n > 0)
            {
                int best = -1, bestCount = 0;
                for (int i = 0; i < 8; i++)
                    if (_dirCounts[i] > bestCount) { bestCount = _dirCounts[i]; best = i; }
                if (best >= 0)
                {
                    PredictedDir = (Agent.UsageDirection)best;
                    DirConfidence = bestCount / (float)n;
                }
            }
            DirConfidence *= sampleScale;

            // Long-term layer (Session 45): the save-persistent tables only
            // override the short-term read when their transition row is
            // genuinely MORE confident -- fresh habits keep winning, old
            // habits answer when the 20-swing window is thin.
            if (_ltSwings >= 8)
            {
                int lp = DirIndex(_ltLastDir);
                if (lp >= 0)
                {
                    int rowTotal = 0;
                    for (int i = 0; i < 8; i++) rowTotal += _ltTransCounts[lp, i];
                    if (rowTotal >= 4)
                    {
                        int best = -1, bestCount = 0;
                        for (int i = 0; i < 8; i++)
                            if (_ltTransCounts[lp, i] > bestCount) { bestCount = _ltTransCounts[lp, i]; best = i; }
                        if (best >= 0)
                        {
                            float ltConf = bestCount / (float)rowTotal * MathF.Min(1f, _ltSwings / 40f) * 0.85f;
                            if (ltConf > DirConfidence)
                            {
                                PredictedDir = (Agent.UsageDirection)best;
                                DirConfidence = ltConf;
                            }
                        }
                    }
                }
            }

            // Tempo: mean + stddev over the recent window. Low spread = the
            // player attacks on a rhythm we can pre-commit to.
            int m = Math.Min(n, TempoWindow);
            if (m > 0)
            {
                float sum = 0f;
                int feints = 0;
                float hitSum = 0f;
                int hitN = 0;
                for (int i = n - m; i < n; i++)
                {
                    sum += _history[i].HoldSeconds;
                    if (_history[i].Feinted) feints++;
                    if (_history[i].TimeToHit >= 0f) { hitSum += _history[i].TimeToHit; hitN++; }
                }
                PredictedHold = sum / m;
                float var = 0f;
                for (int i = n - m; i < n; i++)
                {
                    float d = _history[i].HoldSeconds - PredictedHold;
                    var += d * d;
                }
                float stddev = MathF.Sqrt(var / m);
                // Session 46c: relative spread matters, not absolute. A player
                // who holds around 0.6s with +/-0.15s is rhythmical; a player
                // with +/-0.15s around 0.2s is not.
                TempoConfidence = MathF.Max(0f, 1f - stddev / MathF.Max(PredictedHold, 0.25f)) * sampleScale;
                FeintRate = feints / (float)m;
                if (hitN > 0)
                    TimeToHitEstimate = MathF.Max(MinTimeToHit, MathF.Min(MaxTimeToHit, hitSum / hitN));

                // Session 46: attack cadence -- mean + spread of chain-start
                // intervals. This is the "attacks on a rhythm" predictor.
                float iSum = 0f;
                int iN = 0;
                for (int i = n - m; i < n; i++)
                    if (_history[i].Interval > 0f) { iSum += _history[i].Interval; iN++; }
                if (iN >= 2)
                {
                    PredictedInterval = iSum / iN;
                    float iVar = 0f;
                    for (int i = n - m; i < n; i++)
                        if (_history[i].Interval > 0f)
                        {
                            float dd = _history[i].Interval - PredictedInterval;
                            iVar += dd * dd;
                        }
                    float iStd = MathF.Sqrt(iVar / iN);
                    IntervalConfidence = MathF.Max(0f, 1f - iStd / MathF.Max(PredictedInterval, 0.40f))
                        * MathF.Min(1f, iN / (float)MinSamples);
                }
                else
                {
                    PredictedInterval = 0f;
                    IntervalConfidence = 0f;
                }
            }

            Predictability = DirConfidence * TempoConfidence;
            RecomputeForecast();
        }

        // The read a defender acts on: predicted direction + predicted hold
        // time, valid only when the combined confidence clears the commit bar.
        public static bool TryGetPrediction(out Agent.UsageDirection dir, out float holdSeconds)
        {
            dir = PredictedDir;
            holdSeconds = PredictedHold;
            return Predictability >= CommitThreshold
                && PredictedDir != Agent.UsageDirection.None
                && PredictedHold > 0.05f;
        }

        // UsageDirection: AttackUp=0 AttackDown=1 AttackLeft=2 AttackRight=3
        // (Defend* = 4-8). The int IS the table index -- only attack dirs count.
        private static int DirIndex(Agent.UsageDirection d)
        {
            int i = (int)d;
            return (i >= 0 && i < 4) ? i : -1;
        }

        // =====================================================================
        // LONG-TERM MEMORY -- save sync + analysis dump (Session 45)
        // =====================================================================

        // Compact serialization of the long-term tables into one string.
        // v3 format: v3;swings;holdSum;feintSum;lastDir;d0..d7;t0..t63;
        //            out0..39 (dir x outcome);def0..4;intSum;intN
        //            (v2 blobs load fine -- the cadence tail just stays zero.)
        // (single-line CSV -- survives IDataStore.SyncData<string> intact.
        //  v1 blobs from older saves still load, just without outcomes.)
        public static string ExportMemory()
        {
            StringBuilder sb = new StringBuilder(400);
            sb.Append("v4;");
            sb.Append(_ltSwings).Append(';');
            sb.Append(_ltHoldSum.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(_ltFeintSum.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            sb.Append((int)_ltLastDir).Append(';');
            for (int i = 0; i < 8; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_ltDirCounts[i]);
            }
            sb.Append(';');
            for (int i = 0; i < 64; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_ltTransCounts[i / 8, i % 8]);
            }
            sb.Append(';');
            for (int i = 0; i < 40; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_ltOutcomeByDir[i / 5, i % 5]);
            }
            sb.Append(';');
            for (int i = 0; i < 5; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_ltDefense[i]);
            }
            // v3 tail: attack-cadence accumulators (interval mean over the
            // whole save). Appended last so v2 parses stay index-aligned.
            sb.Append(';');
            sb.Append(_ltIntervalSum.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            sb.Append(_ltIntervalN);
            // v4 tail (Session 47): move-TYPE tables feeding the forecast --
            // unigram(4), order-1 transitions(16), last move.
            sb.Append(';');
            for (int i = 0; i < MvCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_ltMvCounts[i]);
            }
            sb.Append(';');
            for (int i = 0; i < MvCount * MvCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_ltMvTrans[i / MvCount, i % MvCount]);
            }
            sb.Append(';');
            sb.Append(_ltLastMv);
            return sb.ToString();
        }

        public static void ImportMemory(string s)
        {
            if (string.IsNullOrEmpty(s))
                return;
            try
            {
                string[] parts = s.Split(';');
                if (parts.Length < 7 || (parts[0] != "v1" && parts[0] != "v2" && parts[0] != "v3" && parts[0] != "v4"))
                    return;
                _ltSwings = int.Parse(parts[1], CultureInfo.InvariantCulture);
                _ltHoldSum = float.Parse(parts[2], CultureInfo.InvariantCulture);
                _ltFeintSum = float.Parse(parts[3], CultureInfo.InvariantCulture);
                _ltLastDir = (Agent.UsageDirection)int.Parse(parts[4], CultureInfo.InvariantCulture);
                string[] dc = parts[5].Split(',');
                for (int i = 0; i < 8 && i < dc.Length; i++)
                    _ltDirCounts[i] = int.Parse(dc[i], CultureInfo.InvariantCulture);
                string[] tc = parts[6].Split(',');
                for (int i = 0; i < 64 && i < tc.Length; i++)
                    _ltTransCounts[i / 8, i % 8] = int.Parse(tc[i], CultureInfo.InvariantCulture);
                if (parts[0] != "v1" && parts.Length >= 9)
                {
                    string[] oc = parts[7].Split(',');
                    for (int i = 0; i < 40 && i < oc.Length; i++)
                        _ltOutcomeByDir[i / 5, i % 5] = int.Parse(oc[i], CultureInfo.InvariantCulture);
                    string[] df = parts[8].Split(',');
                    for (int i = 0; i < 5 && i < df.Length; i++)
                        _ltDefense[i] = int.Parse(df[i], CultureInfo.InvariantCulture);
                }
                if ((parts[0] == "v3" || parts[0] == "v4") && parts.Length >= 11)
                {
                    _ltIntervalSum = float.Parse(parts[9], CultureInfo.InvariantCulture);
                    _ltIntervalN = int.Parse(parts[10], CultureInfo.InvariantCulture);
                }
                if (parts[0] == "v4" && parts.Length >= 14)
                {
                    string[] mc = parts[11].Split(',');
                    for (int i = 0; i < MvCount && i < mc.Length; i++)
                        _ltMvCounts[i] = int.Parse(mc[i], CultureInfo.InvariantCulture);
                    string[] mt = parts[12].Split(',');
                    for (int i = 0; i < MvCount * MvCount && i < mt.Length; i++)
                        _ltMvTrans[i / MvCount, i % MvCount] = int.Parse(mt[i], CultureInfo.InvariantCulture);
                    _ltLastMv = int.Parse(parts[13], CultureInfo.InvariantCulture);
                }
                RecomputeStats(); // let the restored memory inform the very next read
            }
            catch (Exception)
            {
                // Corrupt/old-format save data -- start the memory fresh rather
                // than poison the model with half-parsed tables.
                Array.Clear(_ltDirCounts, 0, _ltDirCounts.Length);
                Array.Clear(_ltTransCounts, 0, _ltTransCounts.Length);
                Array.Clear(_ltOutcomeByDir, 0, _ltOutcomeByDir.Length);
                Array.Clear(_ltDefense, 0, _ltDefense.Length);
                Array.Clear(_ltMvCounts, 0, _ltMvCounts.Length);
                Array.Clear(_ltMvTrans, 0, _ltMvTrans.Length);
                _ltLastMv = -1;
                _ltHoldSum = 0f;
                _ltFeintSum = 0f;
                _ltIntervalSum = 0f;
                _ltIntervalN = 0;
                _ltSwings = 0;
                _ltLastDir = Agent.UsageDirection.None;
            }
        }

        // Human-readable analysis dump -> combat_ai.log (combatai.memory).
        public static void DumpMemoryToLog()
        {
            CombatAiLog.Log($"[PMEM] long-term player memory: swings={_ltSwings} " +
                $"avgHold={(_ltSwings > 0 ? _ltHoldSum / _ltSwings : 0f):F2}s " +
                $"avgInt={(_ltIntervalN > 0 ? _ltIntervalSum / _ltIntervalN : 0f):F2}s " +
                $"feintRate={(_ltSwings > 0 ? _ltFeintSum / _ltSwings : 0f):F2} lastDir={_ltLastDir}");
            for (int i = 0; i < 4; i++)
            {
                int rowTotal = 0;
                for (int j = 0; j < 8; j++) rowTotal += _ltTransCounts[i, j];
                if (rowTotal <= 0)
                    continue;
                StringBuilder sb = new StringBuilder();
                sb.Append($"[PMEM]   after {(Agent.UsageDirection)i} ({rowTotal}x) ->");
                for (int j = 0; j < 4; j++)
                    if (_ltTransCounts[i, j] > 0)
                        sb.Append($" {(Agent.UsageDirection)j}:{_ltTransCounts[i, j] * 100 / rowTotal}%");
                CombatAiLog.Log(sb.ToString());
            }
            // Outcome context per direction -- how each player attack line has
            // historically resolved against our AI (blocked/parried/landed/
            // no-connect), plus which defenses the AI leaned on.
            string[] outNames = { "pending", "blocked", "parried", "landed", "noconn" };
            for (int i = 0; i < 4; i++)
            {
                int total = 0;
                for (int j = 1; j < 5; j++) total += _ltOutcomeByDir[i, j];
                if (total <= 0)
                    continue;
                StringBuilder sb = new StringBuilder();
                sb.Append($"[PMEM]   {(Agent.UsageDirection)i} outcomes ({total}x):");
                for (int j = 1; j < 5; j++)
                    if (_ltOutcomeByDir[i, j] > 0)
                        sb.Append($" {outNames[j]}:{_ltOutcomeByDir[i, j] * 100 / total}%");
                CombatAiLog.Log(sb.ToString());
            }
            {
                string[] defNames = { "none", "commit-block", "react-block", "dodge", "froze" };
                int defTotal = 0;
                for (int j = 1; j < 5; j++) defTotal += _ltDefense[j];
                StringBuilder sb = new StringBuilder();
                sb.Append($"[PMEM] AI defenses used ({defTotal}x):");
                for (int j = 1; j < 5; j++)
                    if (_ltDefense[j] > 0)
                        sb.Append($" {defNames[j]}:{_ltDefense[j] * 100 / defTotal}%");
                CombatAiLog.Log(sb.ToString());
            }
            // Held->released pattern table (short-term window): what the chain's
            // FIRST held direction predicted about the released direction.
            CombatAiLog.Log("[PMEM] held->released patterns (short-term window):");
            for (int i = 0; i < 4; i++)
            {
                int rowTotal = 0;
                for (int j = 0; j < 4; j++) rowTotal += _patCounts[i, j];
                if (rowTotal <= 0)
                    continue;
                StringBuilder sb = new StringBuilder();
                sb.Append($"[PMEM]   held {(Agent.UsageDirection)i} ({rowTotal}x) -> released:");
                for (int j = 0; j < 4; j++)
                    if (_patCounts[i, j] > 0)
                        sb.Append($" {(Agent.UsageDirection)j}:{_patCounts[i, j] * 100 / rowTotal}%");
                CombatAiLog.Log(sb.ToString());
            }
            // The fight gist: the short-term window as an ordered sequence --
            // each swing's direction, hold time, feint count, our read, the
            // AI's answer, and the outcome.
            CombatAiLog.Log("[PMEM] short-term window (last " + _history.Count + " swings, oldest first):");
            string[] defTag = { "-", "COMMIT", "react", "DODGE", "FROZE" };
            string[] outTag = { "...", "blocked", "parried", "LANDED", "miss" };
            for (int i = 0; i < _history.Count; i++)
            {
                Swing s = _history[i];
                CombatAiLog.Log($"[PMEM]   #{i + 1} {s.Dir} held={(s.InitIdx >= 0 ? ((Agent.UsageDirection)s.InitIdx).ToString() : "-")} " +
                    $"hold={s.HoldSeconds:F2}s intv={(s.Interval >= 0f ? s.Interval.ToString("F2") : "-")}s feints={s.Cancels} " +
                    $"read={s.Read:F2} -> ai={defTag[s.AIDefense < 5 ? s.AIDefense : 0]} outcome={outTag[s.Outcome < 5 ? s.Outcome : 0]}" +
                    (s.TimeToHit >= 0f ? $" tth={s.TimeToHit:F2}s" : ""));
            }
            CombatAiLog.Log("[PMEM] forecast (next move): " +
                $"quick={Forecast[MvQuick]:P0} normal={Forecast[MvNormal]:P0} hold={Forecast[MvHold]:P0} feint={Forecast[MvFeint]:P0} " +
                $"reliability={ForecastReliability:F2} ltMoves=[{_ltMvCounts[0]},{_ltMvCounts[1]},{_ltMvCounts[2]},{_ltMvCounts[3]}]");
            CombatAiLog.Log("[PMEM] live checkpoint: " +
                $"release={CpR:P0} feint={CpF:P0} hold={CpH:P0} sharp={CpSharp:F2} (k={CpK} feints, t={CpT:F2}s) " +
                $"base[R/F/H]={_baseR:P0}/{_baseF:P0}/{_baseH:P0} | pattern: run={RunLength} breakRisk={BreakRisk:F2} " +
                $"breakAware={_breakAware:F2} dirBreak={_dirBreak:F2} breaksSpotted={BreakSpottedSerial} last=\"{LastBreakNote}\"");
            CombatAiLog.Log("[PMEM] model state: " +
                $"predict={PredictedDir}@{DirConfidence:F2} " +
                $"hold={PredictedHold:F2}s@{TempoConfidence:F2} liveExpect={Expectation:F2}");
        }

        private const int MaxLogs = 60;
        private static int _logCount;

        private const int MaxTrace = 800;
        private static int _traceCount;
        private static Agent.ActionStage _trStage = Agent.ActionStage.None;
        private static Agent.ActionCodeType _trType;
        private static Agent.UsageDirection _trDir = Agent.UsageDirection.None;

        public static void ResetLogCount()
        {
            _logCount = 0;
            _fcLogCount = 0;
            _traceCount = 0;
        }
    }
}
