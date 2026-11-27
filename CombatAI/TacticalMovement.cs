using System;
using System.Collections.Generic;
using TaleWorlds.Library;

namespace CombatAI
{
    // =======================================================================
    // TACTICAL MOVEMENT (Session 60): ONE movement planner for duel / spar /
    // group, built on the formation-frame lever (position-based steering is
    // the only thing that keeps legs moving inside block actions -- G1/F3).
    // PURE: Vec2 math only + a small caller-owned state struct, no Agent or
    // native calls, so any mode can reuse it.
    //
    //   One enemy  -> distance band + continuous orbit in the band.
    //   Two+       -> "line them up": hold the primary between us and the
    //                 secondary, orbit AWAY from the secondary, flip the
    //                 orbit side when the secondary cuts us off.
    //   Allies     -> drift toward the nearest ally when isolated.
    // =======================================================================
    internal static class TacticalMovement
    {
        internal struct TacticalState
        {
            public int PrimaryKey;        // enemy we committed to (hysteresis)
            public float PrimarySince;    // when that primary was chosen
            public float OrbitSign;       // +1 / -1: which side we circle on
            public float OrbitFlipAt;     // clock of the next idle flip
            public float LastFlipAt;      // last actual sign change (0.8s min between flips)
            public Vec2 SmoothGoal;       // smoothed world-space goal delta
        }

        internal struct Plan
        {
            public bool HasGoal;
            public Vec2 Goal;             // world delta from self, length <= 1.6
            public Vec2 Heading;          // unit, toward the primary
            public int PrimaryKey;
            public float DistPrimary;
            public float DistSecondary;
        }

        private const float MaxGoal = 1.6f;

        private static Vec2 Norm(Vec2 v)
        {
            if (v.LengthSquared < 0.0001f)
                return Vec2.Zero;
            float l = v.Length;
            return new Vec2(v.x / l, v.y / l);
        }

        private static Vec2 Perp(Vec2 v) => new Vec2(-v.y, v.x);

        // +1 when q lies on the counter-clockwise side of the a->b direction.
        private static float Cross(Vec2 a, Vec2 b) => a.x * b.y - a.y * b.x;

        public static Plan Compute(Vec2 self, IList<(int key, Vec2 pos, bool preferred)> enemies,
            IList<Vec2> allies, float reach, float now, float dt, ref TacticalState st, Random rng)
        {
            Plan pl = new Plan();
            if (enemies == null || enemies.Count == 0)
            {
                st.PrimaryKey = 0;
                st.SmoothGoal = Vec2.Zero;
                return pl;
            }

            // 1) primary: nearest, preferred counts as 0.8m closer; hysteresis
            // keeps the old primary unless beaten by >0.8m or dwell < 1s.
            int pi = 0, si = -1;
            float pd = float.MaxValue, sd = float.MaxValue;
            for (int i = 0; i < enemies.Count; i++)
            {
                float d = (enemies[i].pos - self).Length - (enemies[i].preferred ? 0.8f : 0f);
                if (d < pd) { sd = pd; si = pi; pd = d; pi = i; }
                else if (d < sd) { sd = d; si = i; }
            }
            if (st.PrimaryKey != 0 && st.PrimaryKey != enemies[pi].key && now - st.PrimarySince > 1.0f)
            {
                for (int i = 0; i < enemies.Count; i++)
                {
                    if (enemies[i].key == st.PrimaryKey)
                    {
                        float dKeep = (enemies[i].pos - self).Length - (enemies[i].preferred ? 0.8f : 0f);
                        if (dKeep <= pd + 0.8f) { pi = i; pd = dKeep; }
                        break;
                    }
                }
            }
            var P = enemies[pi];
            pl.PrimaryKey = P.key;
            Vec2 P2 = P.pos, S2 = si >= 0 ? enemies[si].pos : self;
            float dP = (P2 - self).Length, dS = si >= 0 ? (S2 - self).Length : -1f;
            pl.DistPrimary = dP;
            pl.DistSecondary = dS;
            if (st.PrimaryKey != P.key)
            {
                st.PrimaryKey = P.key;
                st.PrimarySince = now;
            }
            if (st.OrbitSign == 0f)
                st.OrbitSign = 1f;

            Vec2 toP = Norm(P2 - self);
            pl.Heading = toP;
            Vec2 goal = Vec2.Zero;

            if (si < 0)
            {
                // 2) ONE enemy (duel): band + orbit; feet never idle in the band.
                float lo = reach * 0.55f, hi = reach * 1.05f;
                if (now >= st.OrbitFlipAt)
                {
                    st.OrbitSign = -st.OrbitSign;
                    st.OrbitFlipAt = now + 1.2f + (float)(rng?.NextDouble() ?? 0.5) * 1.4f;
                    st.LastFlipAt = now;
                }
                if (dP > hi)
                    goal = toP * MathF.Min(1.4f, dP - reach * 0.9f) + Perp(toP) * (0.4f * st.OrbitSign);
                else if (dP < lo)
                    goal = toP * -MathF.Min(1.2f, reach * 0.8f - dP + 0.4f);
                else
                    goal = Perp(toP) * (0.8f * st.OrbitSign);
            }
            else
            {
                // 3) TWO+ enemies: line them up -- T keeps the primary between
                // us and the secondary; never run through the gap between them.
                Vec2 toS = Norm(S2 - self);
                Vec2 T = P2 + Norm(P2 - S2) * (0.9f * reach);
                Vec2 toT = T - self;

                // Orbit sign: circle on the side that keeps S farther away;
                // flip when S moves to cut off our current side (>=0.8s apart).
                float side = Cross(toP, toS);            // +1: S left of the self->P line
                float want = side >= 0f ? -1f : 1f;      // orbit the opposite way from S
                if (want != st.OrbitSign && now - st.LastFlipAt >= 0.8f)
                {
                    st.OrbitSign = want;
                    st.LastFlipAt = now;
                }

                if (toT.Length < 0.5f)
                    goal = Perp(toP) * (0.8f * st.OrbitSign);
                else
                {
                    // Does the straight run to T pass near S? Then go around P instead.
                    Vec2 seg = Norm(toT);
                    float proj = MathF.Max(0f, MathF.Min(toT.Length, Vec2.DotProduct(S2 - self, seg)));
                    Vec2 nearest = self + seg * proj;
                    bool throughS = proj > 0.1f && proj < toT.Length + 0.1f && (S2 - nearest).Length < 1.0f;
                    goal = throughS
                        ? Perp(toP) * (0.9f * st.OrbitSign) + toT * 0.3f
                        : Norm(toT) * MathF.Min(1.4f, toT.Length);
                }
                // Push away from S while it crowds into reach.
                if (dS < reach * 1.25f && dS > 0.05f)
                    goal += toS * -(1.0f * (reach * 1.25f - dS) / (reach * 1.25f));
            }

            // 4) allies: drift toward the nearest one when isolated / far.
            if (allies != null && allies.Count > 0)
            {
                float dA = float.MaxValue;
                Vec2 ally = Vec2.Zero;
                foreach (Vec2 a in allies)
                {
                    float d = (a - self).Length;
                    if (d < dA) { dA = d; ally = a; }
                }
                bool crowded = si >= 0 && dS <= 2.2f * reach || dP <= 2.2f * reach;
                float w = crowded ? MathF.Max(0f, MathF.Min(1f, (dA - 4.5f) / 4f)) * 0.9f
                    : MathF.Max(0f, MathF.Min(1f, (dA - 4.5f) / 4f));
                goal += Norm(ally - self) * w;
            }

            // 5) smooth + clamp.
            float s = MathF.Max(0f, MathF.Min(1f, 10f * MathF.Min(dt, 0.1f)));
            st.SmoothGoal += (goal - st.SmoothGoal) * s;
            Vec2 g = st.SmoothGoal;
            if (g.Length > MaxGoal)
                g = Norm(g) * MaxGoal;
            pl.Goal = g;
            pl.HasGoal = g.LengthSquared > 0.0025f;
            return pl;
        }
    }
}
