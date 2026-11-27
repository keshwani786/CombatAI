using System;
using System.Collections.Generic;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =====================================================================
    // GROUP ENGAGEMENT registry (stage 2 free-target group fight)
    // =====================================================================
    // The "engagement pointer" the user spec asks for: an attacker declares
    // attack intent on a victim BEFORE the windup starts; the victim's
    // defense sees it instantly (no native target read needed -- the target
    // slot stays -1 under the free policy). Managed only, O(10) queries.
    // At most GroupMaxEngagers live claims per victim. Expiry 3.5s after the
    // last refresh is pure cleanup safety.
    public static class GroupEngagement
    {
        public enum KindT { Swing, Feint, Hold }
        public struct Intent
        {
            public int Attacker, Victim;
            public KindT Kind;
            public float Since, RefreshAt, ExpireAt;
        }

        private static readonly List<Intent> _claims = new List<Intent>(16);
        private static int _logCount;
        private const int MaxGengLogs = 300;

        public static void Reset()
        {
            _claims.Clear();
            _gcAt = -99f;
            _logCount = 0;
        }

        private static void Log(string s)
        {
            if (_logCount < MaxGengLogs) { _logCount++; CombatAiLog.Log(s); }
        }

        private static bool Alive(Mission m, int idx)
        {
            Agent a = AgentOf(m, idx);
            return a != null && a.IsActive() && a.Health > 0f;
        }

        public static Agent AgentOf(Mission m, int idx)
        {
            if (idx < 0 || m == null) return null;
            foreach (Agent a in m.Agents)
                if (a.Index == idx) return a;
            return null;
        }

        public static int LiveClaimersOn(Mission m, int victimIdx, float now)
        {
            if (m != null) now = m.CurrentTime;
            int n = 0;
            foreach (Intent c in _claims)
                if (c.Victim == victimIdx && c.ExpireAt > now && Alive(m, c.Attacker))
                    n++;
            return n;
        }

        // Place or refresh the attack-intent pointer. Fails when the victim
        // already has GroupMaxEngagers LIVE claimers (the attacker should
        // pick another threat instead).
        public static bool TryClaim(Mission m, int attackerIdx, int victimIdx, KindT kind, float now, out string deny)
        {
            if (m != null) now = m.CurrentTime;
            deny = null;
            Agent v = AgentOf(m, victimIdx), a = AgentOf(m, attackerIdx);
            if (v == null || !v.IsActive() || v.Health <= 0f) { deny = "victim-dead"; return false; }
            if (a == null || !a.IsEnemyOf(v)) { deny = "not-enemy"; return false; }
            for (int i = 0; i < _claims.Count; i++)
            {
                Intent c = _claims[i];
                if (c.Attacker == attackerIdx)
                {
                    if (c.Victim == victimIdx)
                    {
                        c.Kind = kind;
                        c.RefreshAt = now;
                        c.ExpireAt = now + 3.5f;
                        _claims[i] = c;
                        return true;
                    }
                    deny = "already-claimed"; return false;
                }
            }
            if (LiveClaimersOn(m, victimIdx, now) >= CombatAiState.GroupMaxEngagers)
            {
                deny = "saturated";
                Log($"[GENG] DENY #{attackerIdx} -> #{victimIdx} ({deny}, {LiveClaimersOn(m, victimIdx, now)}/{CombatAiState.GroupMaxEngagers})");
                return false;
            }
            _claims.Add(new Intent
            {
                Attacker = attackerIdx, Victim = victimIdx, Kind = kind,
                Since = now, RefreshAt = now, ExpireAt = now + 3.5f
            });
            Log($"[GENG] claim #{attackerIdx} -> #{victimIdx} kind={kind} cov={LiveClaimersOn(m, victimIdx, now)}");
            return true;
        }

        public static void Refresh(Mission m, int attackerIdx, float now)
        {
            if (m != null) now = m.CurrentTime;
            for (int i = 0; i < _claims.Count; i++)
            {
                Intent c = _claims[i];
                if (c.Attacker == attackerIdx)
                {
                    c.RefreshAt = now;
                    c.ExpireAt = now + 3.5f;
                    _claims[i] = c;
                    return;
                }
            }
        }

        public static void Release(Mission m, int attackerIdx, float now, string why)
        {
            if (m != null) now = m.CurrentTime;
            for (int i = _claims.Count - 1; i >= 0; i--)
            {
                if (_claims[i].Attacker == attackerIdx)
                {
                    Log($"[GENG] release #{attackerIdx} -> #{_claims[i].Victim} ({why})");
                    _claims.RemoveAt(i);
                    return;
                }
            }
        }

        public static bool ClaimOf(Mission m, int attackerIdx, float now, out Intent it)
        {
            if (m != null) now = m.CurrentTime;
            foreach (Intent c in _claims)
                if (c.Attacker == attackerIdx && c.ExpireAt > now)
                { it = c; return true; }
            it = default(Intent);
            return false;
        }

        // Every live intent directed at victimIdx, freshest first.
        public static int IncomingOn(Mission m, int victimIdx, float now, List<Intent> buf)
        {
            if (m != null) now = m.CurrentTime;
            buf.Clear();
            for (int i = _claims.Count - 1; i >= 0; i--)
            {
                Intent c = _claims[i];
                if (c.Victim == victimIdx && c.ExpireAt > now && Alive(m, c.Attacker))
                    buf.Add(c);
            }
            return buf.Count;
        }

        // Lazy GC of dead/expired claims; call once per tick from any agent.
        // NOTE: every public method overrides `now` with Mission.CurrentTime --
        // per-agent _brainClock values differ and must never be compared.
        private static float _gcAt = -99f;
        public static void Housekeep(Mission m, float now)
        {
            if (m != null) now = m.CurrentTime;
            if (now - _gcAt < 1.0f) return;
            _gcAt = now;
            for (int i = _claims.Count - 1; i >= 0; i--)
            {
                Intent c = _claims[i];
                if (c.ExpireAt <= now || !Alive(m, c.Attacker))
                {
                    Log($"[GENG] release #{c.Attacker} -> #{c.Victim} (expired/dead)");
                    _claims.RemoveAt(i);
                }
            }
        }

        public static int Count => _claims.Count;
    }
}
