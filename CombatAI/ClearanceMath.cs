using System;
using System.Collections.Generic;
using TaleWorlds.Library;

namespace CombatAI
{
    // =======================================================================
    // CLEARANCE MATH (Session 62 round 4): pure helpers shared by duel/spar/
    // group -- a stuck watchdog that LEARNS blocked directions from actual
    // lack of displacement (the wall probe alone misses stones/props), and a
    // clearance-ring direction pick. Vec2 math only, no Agent/native calls.
    // =======================================================================
    internal static class ClearanceMath
    {
        // The 8 ring directions: world angles k*45deg, k=0 is +x, CCW.
        public static Vec2 RingDir(int k)
        {
            float a = k * (MathF.PI * 2f) / 8f;
            return new Vec2(MathF.Cos(a), MathF.Sin(a));
        }

        // Among ring dirs with free >= minFree pick max(free + 0.5*dot(dir,prefer)).
        // Vec2.Zero when none is open.
        public static Vec2 BestOpenDir(float[] ring, Vec2 prefer, float minFree)
        {
            float pl = prefer.Length;
            Vec2 pu = pl > 0.0001f ? new Vec2(prefer.x / pl, prefer.y / pl) : Vec2.Zero;
            float best = -1f;
            int bk = -1;
            for (int k = 0; k < 8; k++)
            {
                if (ring[k] < minFree)
                    continue;
                Vec2 d = RingDir(k);
                float score = ring[k] + 0.5f * (d.x * pu.x + d.y * pu.y);
                if (score > best)
                {
                    best = score;
                    bk = k;
                }
            }
            return bk < 0 ? Vec2.Zero : RingDir(bk);
        }

        // The ring sector a world dir falls in (sector k centered on
        // RingDir(k); -1 for ~zero dirs). Used to bucket per-sector caches.
        public static int RingSector(Vec2 dir)
        {
            if (dir.LengthSquared < 0.0001f)
                return -1;
            float a = MathF.Atan2(dir.y, dir.x);
            int k = (int)MathF.Floor((a + MathF.PI / 8f) / (MathF.PI / 4f));
            if (k < 0)
                k += 8;
            return k % 8;
        }

        // "Actually incoming" test for the target-switch gate: closing at
        // >0.3 m/s along the (unit) dir to us, or net approach of >0.3m over
        // the tracked window. The GetTargetAgent()==us half lives in the
        // caller (it needs Agent access).
        public static bool Incoming(Vec2 velWorld, Vec2 dirToUs, float distDelta)
        {
            return Vec2.DotProduct(velWorld, dirToUs) > 0.3f || distDelta > 0.3f;
        }

        // Andrew's monotone chain, XY. Returns the hull CCW (no duplicated
        // closing vertex), or null when there aren't 3 distinct points.
        public static List<Vec2> ConvexHull(List<Vec2> pts)
        {
            if (pts == null || pts.Count < 3)
                return null;
            Vec2[] a = pts.ToArray();
            Array.Sort(a, delegate (Vec2 p, Vec2 q)
            {
                return p.x != q.x ? p.x.CompareTo(q.x) : p.y.CompareTo(q.y);
            });
            List<Vec2> u = new List<Vec2>(a.Length);
            for (int i = 0; i < a.Length; i++)
                if (u.Count == 0 || a[i].x != u[u.Count - 1].x || a[i].y != u[u.Count - 1].y)
                    u.Add(a[i]);
            if (u.Count < 3)
                return null;
            List<Vec2> h = new List<Vec2>(u.Count + 1);
            for (int i = 0; i < u.Count; i++)
            {
                while (h.Count >= 2 && HullCross(h[h.Count - 2], h[h.Count - 1], u[i]) <= 0f)
                    h.RemoveAt(h.Count - 1);
                h.Add(u[i]);
            }
            int lower = h.Count + 1;
            for (int i = u.Count - 2; i >= 0; i--)
            {
                Vec2 p = u[i];
                while (h.Count >= lower && HullCross(h[h.Count - 2], h[h.Count - 1], p) <= 0f)
                    h.RemoveAt(h.Count - 1);
                h.Add(p);
            }
            h.RemoveAt(h.Count - 1);   // last == first
            return h.Count >= 3 ? h : null;
        }

        private static float HullCross(Vec2 o, Vec2 a, Vec2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }

        // Push every hull vertex `amount` meters away from the centroid.
        public static List<Vec2> InflateHull(List<Vec2> hull, float amount)
        {
            if (hull == null || hull.Count == 0)
                return hull;
            Vec2 c = Vec2.Zero;
            for (int i = 0; i < hull.Count; i++)
                c += hull[i];
            c = c * (1f / hull.Count);
            List<Vec2> o = new List<Vec2>(hull.Count);
            for (int i = 0; i < hull.Count; i++)
            {
                Vec2 d = hull[i] - c;
                float l = d.Length;
                o.Add(l > 0.0001f ? hull[i] + d * (amount / l) : hull[i]);
            }
            return o;
        }
    }

    // Reactive layer: a commanded direction that produced ~no displacement for
    // >=0.6s is BLOCKED -- remember it for 2s (4 slots, round-robin).
    internal sealed class StuckWatch
    {
        private const float HoldSeconds = 0.6f;
        private const float MinDisplace = 0.15f;
        private const float BlockSeconds = 2.0f;

        private Vec2 _winPos;
        private Vec2 _winDir;
        private float _winAt = -99f;
        private readonly Vec2[] _blocks = new Vec2[4];
        private readonly float[] _blockUntil = new float[4] { -99f, -99f, -99f, -99f };
        private int _blockNext;
        public int StuckCount;

        public bool Note(Vec2 pos, Vec2 cmdDir, float now, bool exempt, out Vec2 blockedDir)
        {
            blockedDir = Vec2.Zero;
            if (exempt || cmdDir.LengthSquared < 0.0001f)
            {
                _winAt = -99f;   // not tracking: exempt or no move commanded
                return false;
            }
            if (_winAt < 0f || (_winDir.x * cmdDir.x + _winDir.y * cmdDir.y) < 0.9f)
            {
                _winPos = pos;
                _winDir = cmdDir;
                _winAt = now;
                return false;
            }
            if (now - _winAt < HoldSeconds)
                return false;
            Vec2 d = pos - _winPos;
            float along = d.x * _winDir.x + d.y * _winDir.y;
            _winAt = -99f;   // restart the window either way (fires once)
            if (along < MinDisplace)
            {
                _blocks[_blockNext] = _winDir;
                _blockUntil[_blockNext] = now + BlockSeconds;
                _blockNext = (_blockNext + 1) % 4;
                StuckCount++;
                blockedDir = _winDir;
                return true;
            }
            return false;
        }

        public bool IsBlocked(Vec2 dir, float now)
        {
            for (int i = 0; i < 4; i++)
            {
                if (now < _blockUntil[i]
                    && dir.x * _blocks[i].x + dir.y * _blocks[i].y > 0.8f)
                    return true;
            }
            return false;
        }
    }
}
