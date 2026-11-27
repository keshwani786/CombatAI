using System;
using System.Collections.Generic;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // Distributed group-fight ANCHOR assignment (steered pair path). Each
    // agent gets one "anchor" enemy: the order-preserving counterpart across
    // the enemy line, computed by projecting BOTH teams onto the same
    // lateral axis (perpendicular to the ally-centroid -> enemy-centroid
    // direction) and matching ranks -- left faces left = directly across,
    // so approach paths don't cross and there is no "nearest" requirement.
    // The candidates for a rank are the enemies at ranks j-1..j+1; the one
    // carrying the FEWEST already-assigned anchors wins (coverage), ties
    // break to closest rank then nearest distance. The registry only feeds
    // this initial pick -- the per-agent switch/leash/adopt rules live in
    // CombatAiOverrideComponent.GroupTarget.cs.
    public static class GroupTargeting
    {
        // agentIdx -> enemyIdx: who this agent was anchored to.
        private static readonly Dictionary<int, int> _anchorOf = new Dictionary<int, int>(16);
        private static readonly HashSet<int> _live = new HashSet<int>();
        private static readonly List<int> _prune = new List<int>(8);

        public static void Reset()
        {
            _anchorOf.Clear();
        }

        public static Agent Assign(Agent self, Mission m)
            => Assign(self, m, out _, out _, out _, out _, out _);

        // The anchor for `self`, recorded in the registry for coverage.
        // Returns null when there are no live enemies. The out params are
        // diagnostics for the [GTARGET] line: self's lateral rank i of n
        // allies, the picked enemy's rank j of m enemies, cov = anchors
        // already on the pick (-1 when unassigned).
        public static Agent Assign(Agent self, Mission m,
            out int rankSelf, out int nAllies, out int rankPick, out int nEnemies, out int cov)
        {
            rankSelf = rankPick = cov = -1;
            nAllies = nEnemies = 0;
            if (self == null || m == null)
                return null;

            // Prune registry entries whose owner died (or left) since the
            // last call -- the mission scan below is the live set.
            _live.Clear();
            foreach (Agent a in m.Agents)
                if (a != null && a.IsActive() && a.IsHuman && !a.IsMainAgent && a.Health > 0f)
                    _live.Add(a.Index);
            _prune.Clear();
            foreach (KeyValuePair<int, int> kv in _anchorOf)
                if (!_live.Contains(kv.Key))
                    _prune.Add(kv.Key);
            for (int k = 0; k < _prune.Count; k++)
                _anchorOf.Remove(_prune[k]);

            var allies = new List<Agent>(8);    // includes self
            var enemies = new List<Agent>(8);
            Vec2 cAlly = Vec2.Zero, cEnemy = Vec2.Zero;
            foreach (Agent a in m.Agents)
            {
                if (a == null || !a.IsActive() || !a.IsHuman || a.IsMainAgent || a.Health <= 0f)
                    continue;
                if (self.IsEnemyOf(a))
                {
                    enemies.Add(a);
                    cEnemy += a.Position.AsVec2;
                }
                else
                {
                    allies.Add(a);
                    cAlly += a.Position.AsVec2;
                }
            }
            nAllies = allies.Count;
            nEnemies = enemies.Count;
            if (nEnemies == 0)
                return null;
            cEnemy = new Vec2(cEnemy.x / nEnemies, cEnemy.y / nEnemies);
            cAlly = nAllies > 0 ? new Vec2(cAlly.x / nAllies, cAlly.y / nAllies) : self.Position.AsVec2;

            Vec2 fwd = cEnemy - cAlly;
            if (fwd.LengthSquared < 0.0001f)
            {
                // Degenerate centroids: aim at self's nearest enemy instead.
                float bd = float.MaxValue;
                foreach (Agent e in enemies)
                {
                    float d = (e.Position.AsVec2 - self.Position.AsVec2).LengthSquared;
                    if (d < bd) { bd = d; fwd = e.Position.AsVec2 - self.Position.AsVec2; }
                }
                if (fwd.LengthSquared < 0.0001f)
                    fwd = new Vec2(1f, 0f);
            }
            float fl = fwd.Length;
            fwd = new Vec2(fwd.x / fl, fwd.y / fl);
            Vec2 lat = new Vec2(-fwd.y, fwd.x);

            // Sort BOTH teams ascending on the same lateral axis so rank i
            // faces rank round(r*(m-1)) = directly across.
            var aProj = new List<(Agent a, float p)>(nAllies);
            var eProj = new List<(Agent a, float p)>(nEnemies);
            for (int k = 0; k < nAllies; k++)
            {
                Vec2 p = allies[k].Position.AsVec2;
                aProj.Add((allies[k], p.x * lat.x + p.y * lat.y));
            }
            for (int k = 0; k < nEnemies; k++)
            {
                Vec2 p = enemies[k].Position.AsVec2;
                eProj.Add((enemies[k], p.x * lat.x + p.y * lat.y));
            }
            aProj.Sort((x, y) => x.p.CompareTo(y.p));
            eProj.Sort((x, y) => x.p.CompareTo(y.p));

            int i = -1;
            for (int k = 0; k < nAllies; k++)
                if (aProj[k].a.Index == self.Index) { i = k; break; }
            if (i < 0)
                return null;   // self missing from its own ally set
            rankSelf = i;
            float r = nAllies > 1 ? (float)i / (nAllies - 1) : 0.5f;
            int j = (int)MathF.Round(r * (nEnemies - 1));
            if (j < 0) j = 0; else if (j > nEnemies - 1) j = nEnemies - 1;

            Agent best = null;
            int bestCov = int.MaxValue, bestRankD = int.MaxValue;
            float bestD = float.MaxValue;
            int lo = Math.Max(0, j - 1), hi = Math.Min(nEnemies - 1, j + 1);
            for (int k = lo; k <= hi; k++)
            {
                Agent e = eProj[k].a;
                int c = 0;
                foreach (KeyValuePair<int, int> kv in _anchorOf)
                    if (kv.Value == e.Index && kv.Key != self.Index)
                        c++;
                int rd = Math.Abs(k - j);
                float d = (e.Position.AsVec2 - self.Position.AsVec2).LengthSquared;
                if (c < bestCov || (c == bestCov && (rd < bestRankD || (rd == bestRankD && d < bestD))))
                {
                    best = e; bestCov = c; bestRankD = rd; bestD = d;
                    rankPick = k;
                }
            }
            if (best == null)
                return null;
            cov = bestCov;
            _anchorOf[self.Index] = best.Index;
            return best;
        }
    }
}
