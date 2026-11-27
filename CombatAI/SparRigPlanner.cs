using System.Collections.Generic;
using TaleWorlds.Library;

namespace CombatAI
{
    // =======================================================================
    // SPAR RIG PLANNER (Session 62 round 3): pure movement+primary planner
    // for the 2v1 spar defender. Movement is GEOMETRY-LOOKAHEAD: candidate
    // moves are scored by the spread theta they leave (movement is the only
    // thing that can shrink a >85deg spread -- switching primary cannot, so
    // switches are now view-safe and proximity-driven). Vec2 math only, no
    // Agent/native calls; the caller owns the ranked[] buffer (no allocs).
    // =======================================================================
    internal static class SparRigPlanner
    {
        internal struct RigState
        {
            public int PrimaryKey;        // agent index of the primary (faced) enemy
            public float PrimarySince;    // when that primary was chosen
            public float FlankSince;      // (retained for state layout; flank switch removed)
            public float LateralSign;     // +1/-1
            public bool SpreadActive;     // spread hysteresis latch
            public int MoveKind;          // stored top candidate kind (stickiness)
            public float MoveSign;        // stored lateral sign
            public float MoveHoldUntil;   // stickiness hold
            public float IdleSince;       // start of the current None-top streak (-1 = moving)
        }

        internal enum RigMoveKind { None = 0, Forward = 1, Back = 2, Strafe = 3 }

        internal struct RigCand
        {
            public RigMoveKind Kind;
            public Vec2 Dir;
            public float Cost;
            public float ThetaAfter;
            public float Speed;           // goal magnitude scale (drift = slow); 1 when unset
        }

        internal struct RigPlan
        {
            public int PrimaryKey;
            public RigMoveKind Kind;
            public Vec2 Dir;              // unit world dir, zero when Kind==None
            public float SpreadDeg;
            public float SecondaryBearingDeg;
            public float DistPrimary;
            public float DistSecondary;
            public int SwitchReason;      // 0 none, 1 switch to non-player, 2 return to player
            public bool SwitchBlocked;    // a proximity switch was denied: target not incoming
            public float ThetaAfterTop;   // theta' of ranked[0] (theta when None)
        }

        private const float PrimaryDwellSeconds = 1.5f;
        private const float PlayerScoreBias = 2.5f;   // the AI attacker only becomes primary when ~3.5m nearer
        private const float SwitchScoreMargin = 1.0f;
        private const float ViewSafeTheta = 85f;
        private const float SpreadEnterDeg = 85f;
        private const float SpreadLeaveDeg = 70f;
        private const float MoveHoldSeconds = 0.4f;
        private const float ImproveMargin = 4f;
        // Anti-idle drift (Session 63): when calm (theta small, primary in the
        // comfort band) a >~1.2s idle emits a slow theta-reducing strafe held
        // for MoveHoldSeconds instead of freezing in place.
        private const float DriftThetaMax = 70f;
        private const float DriftIdleSeconds = 1.2f;
        private const float DriftSpeed = 0.55f;

        private static Vec2 Norm(Vec2 v)
        {
            if (v.LengthSquared < 0.0001f)
                return Vec2.Zero;
            float l = v.Length;
            return new Vec2(v.x / l, v.y / l);
        }

        private static float ThetaDeg(Vec2 toP, Vec2 toS)
        {
            if (toP.LengthSquared < 0.0001f || toS.LengthSquared < 0.0001f)
                return 0f;
            return MathF.Acos(MathF.Max(-1f, MathF.Min(1f, toP.x * toS.x + toP.y * toS.y))) * 57.2958f;
        }

        private static float BearingDeg(Vec2 facing, Vec2 to)
        {
            if (facing.LengthSquared < 0.0001f || to.LengthSquared < 0.0001f)
                return 0f;
            float c = facing.x * to.y - facing.y * to.x;
            float d = facing.x * to.x + facing.y * to.y;
            return MathF.Abs(MathF.Atan2(c, d) * 57.2958f);
        }

        private static float BandPen(float d, float reach)
        {
            if (d < reach * 0.55f) return 80f;
            if (d < reach * 0.75f) return 10f;
            if (d > reach * 1.4f) return 25f;
            return 0f;
        }

        private static float ClosePenS(float d, float reach)
            => d >= 0f && d < reach * 0.4f ? 60f : 0f;

        private static float MovePenOf(RigMoveKind k)
            => k == RigMoveKind.Back ? 2f : k == RigMoveKind.Forward ? 6f : 0f;

        public static RigPlan Compute(Vec2 self, Vec2 facing,
            IList<(int key, Vec2 pos, bool isPlayer, bool incoming)> enemies, float reach, float now,
            ref RigState st, RigCand[] ranked, out int rankedCount)
        {
            RigPlan pl = new RigPlan();
            rankedCount = 0;
            if (enemies == null || enemies.Count == 0)
            {
                st.PrimaryKey = 0;
                ranked[rankedCount++] = new RigCand { Kind = RigMoveKind.None, Dir = Vec2.Zero };
                pl.ThetaAfterTop = 0f;
                return pl;
            }

            // (a) PRIMARY: state-based -- keep st.PrimaryKey while present,
            // else the player if present, else enemies[0].
            int pi = -1, playerIdx = -1;
            for (int i = 0; i < enemies.Count; i++)
            {
                if (enemies[i].key == st.PrimaryKey)
                    pi = i;
                if (enemies[i].isPlayer)
                    playerIdx = i;
            }
            if (pi < 0)
                pi = playerIdx >= 0 ? playerIdx : 0;

            int si = -1;
            float theta = 0f;
            Vec2 P = enemies[pi].pos, S = Vec2.Zero;
            float dP = (P - self).Length, dS = -1f;
            Vec2 toP = Norm(P - self), toS = Vec2.Zero;
            if (enemies.Count > 1)
            {
                si = pi == 0 ? 1 : 0;
                S = enemies[si].pos;
                dS = (S - self).Length;
                toS = Norm(S - self);
                theta = ThetaDeg(toP, toS);

                // View-safe proximity switch: the OTHER enemy must be clearly
                // closer after the player bias, the current primary held 1.5s,
                // and the spread must be small enough that turning keeps the
                // old primary inside the front cone (switch alone can never
                // fix a wide spread -- only movement can). Session 63: a
                // non-player target must also be ACTUALLY INCOMING (targets
                // us / closing / closing over the last ~1s -- caller-supplied)
                // so we never relock to a passive attacker.
                if (now - st.PrimarySince >= PrimaryDwellSeconds && theta <= ViewSafeTheta)
                {
                    float scCur = dP - (enemies[pi].isPlayer ? PlayerScoreBias : 0f);
                    float scOth = dS - (enemies[si].isPlayer ? PlayerScoreBias : 0f);
                    if (scOth + SwitchScoreMargin < scCur)
                    {
                        if (enemies[si].isPlayer || enemies[si].incoming)
                        {
                        int tmp = pi; pi = si; si = tmp;
                        var P2 = enemies[pi].pos;
                        P = P2;
                        dP = (P - self).Length;
                        toP = Norm(P - self);
                        S = enemies[si].pos;
                        dS = (S - self).Length;
                        toS = Norm(S - self);
                        theta = ThetaDeg(toP, toS);
                        pl.SwitchReason = enemies[pi].isPlayer ? 2 : 1;
                        st.PrimarySince = now;
                        st.FlankSince = 0f;
                        }
                        else
                            pl.SwitchBlocked = true;   // closer but passive -- don't chase
                    }
                }
            }

            var pk = enemies[pi];
            pl.PrimaryKey = pk.key;
            pl.DistPrimary = dP;
            pl.DistSecondary = dS;
            pl.SpreadDeg = theta;
            pl.SecondaryBearingDeg = si >= 0 ? BearingDeg(facing, enemies[si].pos - self) : 0f;
            if (st.PrimaryKey != pk.key)
            {
                st.PrimaryKey = pk.key;
                st.PrimarySince = now;
            }
            if (st.LateralSign == 0f)
                st.LateralSign = 1f;

            // (b) MOVEMENT with geometric lookahead
            if (si < 0)
            {
                // 1 enemy: band only, plus a gentle orbit drift after a
                // long calm idle (anti-idle, Session 63).
                if (dP < reach * 0.55f)
                    AddCand(ranked, ref rankedCount, RigMoveKind.Back, new Vec2(-toP.x, -toP.y), 0f, 0f);
                else if (dP > reach * 1.05f)
                    AddCand(ranked, ref rankedCount, RigMoveKind.Forward, toP, 0f, 0f);
                else
                    AddDriftOrNone(ranked, ref rankedCount, self, toP, P, Vec2.Zero, false, 0f, ref st, now);
                pl.ThetaAfterTop = 0f;
            }
            else
            {
                if (!st.SpreadActive && theta > SpreadEnterDeg)
                    st.SpreadActive = true;
                else if (st.SpreadActive && theta < SpreadLeaveDeg)
                    st.SpreadActive = false;

                // Pressure rule (regression fix): a primary inside 0.55*reach
                // always gets Back first -- ranked first when not spread, and
                // forced ahead of the scored candidates when spread.
                bool pressed = dP < reach * 0.55f;
                if (pressed && !st.SpreadActive)
                {
                    AddCand(ranked, ref rankedCount, RigMoveKind.Back, new Vec2(-toP.x, -toP.y), 0f, theta);
                    AddCand(ranked, ref rankedCount, RigMoveKind.None, Vec2.Zero, 0f, theta);
                    pl.ThetaAfterTop = theta;
                    pl.Kind = ranked[0].Kind;
                    pl.Dir = ranked[0].Dir;
                    st.IdleSince = -1f;
                    return pl;
                }

                if (st.SpreadActive)
                {
                    float noneCost = theta + BandPen(dP, reach) + ClosePenS(dS, reach);
                    Vec2 perp = new Vec2(-toP.y, toP.x);
                    ScoreCandidate(ranked, ref rankedCount, RigMoveKind.Back, new Vec2(-toP.x, -toP.y), self, P, S, dS, reach, noneCost);
                    ScoreCandidate(ranked, ref rankedCount, RigMoveKind.Strafe, perp, self, P, S, dS, reach, noneCost);
                    ScoreCandidate(ranked, ref rankedCount, RigMoveKind.Strafe, new Vec2(-perp.x, -perp.y), self, P, S, dS, reach, noneCost);
                    ScoreCandidate(ranked, ref rankedCount, RigMoveKind.Forward, toP, self, P, S, dS, reach, noneCost);
                    // None is appended last as a candidate too (caller walks the list).
                    AddCand(ranked, ref rankedCount, RigMoveKind.None, Vec2.Zero, noneCost, theta);
                    // sort ascending by cost, None stays last
                    Sort(ranked, rankedCount - 1);
                    // While pressed, Back(-toP) is forced to the front (wall
                    // veto keeps the rest behind it as fallbacks); stickiness
                    // is skipped so it cannot reorder the back-off away.
                    if (pressed)
                    {
                        Vec2 backDir = new Vec2(-toP.x, -toP.y);
                        Vec2 sp = self + backDir * 1.0f;
                        float backTheta = ThetaDeg(Norm(P - sp), Norm(S - sp));
                        int bi = -1;
                        for (int i = 0; i < rankedCount; i++)
                            if (ranked[i].Kind == RigMoveKind.Back) { bi = i; break; }
                        if (bi < 0)
                        {
                            AddCand(ranked, ref rankedCount, RigMoveKind.Back, backDir, theta + BandPen(dP - 1f, reach), backTheta);
                            bi = rankedCount - 1;
                        }
                        RigCand bt = ranked[bi];
                        bt.ThetaAfter = backTheta;
                        for (int j = bi; j > 0; j--) ranked[j] = ranked[j - 1];
                        ranked[0] = bt;
                        // None stays last: if it floated up mid-list, push it back.
                        for (int i = 0; i < rankedCount - 1; i++)
                        {
                            if (ranked[i].Kind == RigMoveKind.None)
                            {
                                RigCand nc = ranked[i];
                                for (int j = i; j < rankedCount - 1; j++) ranked[j] = ranked[j + 1];
                                ranked[rankedCount - 1] = nc;
                                break;
                            }
                        }
                        st.MoveKind = (int)ranked[0].Kind;
                        st.MoveSign = 0f;
                        st.MoveHoldUntil = now + MoveHoldSeconds;
                    }
                    else
                    {
                    // STICKINESS: keep the stored candidate if still listed and held.
                    if (st.MoveKind != 0 && now < st.MoveHoldUntil)
                    {
                        for (int i = 0; i < rankedCount - 1; i++)
                        {
                            if ((int)ranked[i].Kind == st.MoveKind && (st.MoveKind != (int)RigMoveKind.Strafe || MathF.Sign(ranked[i].Dir.x * perp.x + ranked[i].Dir.y * perp.y) == MathF.Sign(st.MoveSign)))
                            {
                                RigCand t = ranked[i];
                                for (int j = i; j > 0; j--) ranked[j] = ranked[j - 1];
                                ranked[0] = t;
                                break;
                            }
                        }
                    }
                    if ((int)ranked[0].Kind != st.MoveKind
                        || (st.MoveKind == (int)RigMoveKind.Strafe && MathF.Sign(ranked[0].Dir.x * perp.x + ranked[0].Dir.y * perp.y) != MathF.Sign(st.MoveSign)))
                    {
                        st.MoveKind = (int)ranked[0].Kind;
                        st.MoveSign = ranked[0].Kind == RigMoveKind.Strafe ? MathF.Sign(ranked[0].Dir.x * perp.x + ranked[0].Dir.y * perp.y) : 0f;
                        st.MoveHoldUntil = now + MoveHoldSeconds;
                    }
                    }
                }
                else
                {
                    if (dP > reach * 1.05f)
                    {
                        AddCand(ranked, ref rankedCount, RigMoveKind.Forward, toP, 0f, 0f);
                        st.MoveKind = 0;
                        st.MoveSign = 0f;
                    }
                    else if (theta <= DriftThetaMax)
                    {
                        // Calm band: a short idle is fine; past ~1.2s emit a
                        // slow theta-reducing orbit strafe (anti-idle).
                        AddDriftOrNone(ranked, ref rankedCount, self, toP, P, S, true, theta, ref st, now);
                    }
                    else
                    {
                        AddCand(ranked, ref rankedCount, RigMoveKind.None, Vec2.Zero, 0f, 0f);
                        st.MoveKind = 0;
                        st.MoveSign = 0f;
                    }
                }
                pl.ThetaAfterTop = rankedCount > 0 ? ranked[0].ThetaAfter : theta;
            }

            pl.Kind = rankedCount > 0 ? ranked[0].Kind : RigMoveKind.None;
            pl.Dir = rankedCount > 0 ? ranked[0].Dir : Vec2.Zero;
            // Idle streak: None-top ticks accumulate; any move resets.
            if (pl.Kind == RigMoveKind.None)
            {
                if (st.IdleSince < 0f)
                    st.IdleSince = now;
            }
            else
                st.IdleSince = -1f;
            return pl;
        }

        // Anti-idle drift (Session 63): in calm geometry emit a slow orbit
        // strafe (held for MoveHoldSeconds) toward the side that shrinks
        // theta, with None as the caller's fallback. Only reached in the
        // comfort band -- never pressed, never spread.
        private static void AddDriftOrNone(RigCand[] buf, ref int n, Vec2 self, Vec2 toP,
            Vec2 P, Vec2 S, bool hasS, float theta, ref RigState st, float now)
        {
            bool held = st.MoveKind == (int)RigMoveKind.Strafe && now < st.MoveHoldUntil;
            bool idleLong = st.IdleSince >= 0f && now - st.IdleSince >= DriftIdleSeconds;
            if (held || idleLong)
            {
                float sign = held ? st.MoveSign : 0f;
                Vec2 perp = new Vec2(-toP.y, toP.x);
                if (sign == 0f)
                {
                    if (hasS)
                    {
                        Vec2 lp = self + perp, rp = self - perp;
                        float thL = ThetaDeg(Norm(P - lp), Norm(S - lp));
                        float thR = ThetaDeg(Norm(P - rp), Norm(S - rp));
                        sign = thL <= thR ? 1f : -1f;
                    }
                    else
                        sign = st.LateralSign != 0f ? st.LateralSign : 1f;
                }
                Vec2 dd = sign >= 0f ? perp : new Vec2(-perp.x, -perp.y);
                Vec2 gp = self + dd * 1.0f;
                float thAfter = hasS ? ThetaDeg(Norm(P - gp), Norm(S - gp)) : theta;
                AddCand(buf, ref n, RigMoveKind.Strafe, dd, 0f, thAfter, DriftSpeed);
                AddCand(buf, ref n, RigMoveKind.None, Vec2.Zero, 0f, theta);
                st.MoveKind = (int)RigMoveKind.Strafe;
                st.MoveSign = sign;
                if (!held)
                    st.MoveHoldUntil = now + MoveHoldSeconds;
            }
            else
            {
                AddCand(buf, ref n, RigMoveKind.None, Vec2.Zero, 0f, theta);
                st.MoveKind = 0;
                st.MoveSign = 0f;
            }
        }

        private static void AddCand(RigCand[] buf, ref int n, RigMoveKind k, Vec2 dir, float cost, float thetaAfter, float speed = 1f)
        {
            buf[n] = new RigCand { Kind = k, Dir = dir, Cost = cost, ThetaAfter = thetaAfter, Speed = speed };
            n++;
        }

        // Lookahead: self' = self + dir*1m, score the resulting geometry.
        private static void ScoreCandidate(RigCand[] buf, ref int n, RigMoveKind k, Vec2 dir,
            Vec2 self, Vec2 P, Vec2 S, float dS, float reach, float noneCost)
        {
            Vec2 sp = self + dir * 1.0f;
            float dPp = (P - sp).Length;
            float dSp = (S - sp).Length;
            float thetaP = ThetaDeg(Norm(P - sp), Norm(S - sp));
            float cost = thetaP + BandPen(dPp, reach) + ClosePenS(dSp, reach) + MovePenOf(k);
            if (cost < noneCost - ImproveMargin)   // must really improve
                AddCand(buf, ref n, k, dir, cost, thetaP);
        }

        private static void Sort(RigCand[] a, int n)
        {
            for (int i = 1; i < n; i++)
            {
                RigCand t = a[i];
                int j = i - 1;
                while (j >= 0 && a[j].Cost > t.Cost) { a[j + 1] = a[j]; j--; }
                a[j + 1] = t;
            }
        }
    }
}
