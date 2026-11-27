using System;
using System.Collections.Generic;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // Defend-2v1 coordinator. Both attackers are active and aggressive (each runs the
    // full duel FSM with the duo tuning profile); this class only keeps them out of
    // each other's way:
    //   - SIDES: one attacker owns the player's LEFT front slot, the other the RIGHT
    //     (same +/-40 degree slots the spar rig uses, so both always sit inside the
    //     player's view). The assignment is sticky: it only swaps when the pair has
    //     really crossed over (held for SwapHoldSeconds), so nobody pendulums between
    //     slots. A lone fighter (partner dead, replacement not yet spawned) just takes
    //     whichever side it is closest to, with hysteresis.
    //   - VIEW: the player's view direction, low-pass filtered, so the slots glide
    //     with the camera instead of snapping on every flick.
    //   - PINCER: an attacker that sees its partner committed to a swing may start its
    //     own right away (the component skips its tempo breather) so the two releases
    //     land close together.
    public static class ArenaDuoCoordinator
    {
        private const float RefreshInterval = 0.5f;
        private const float ViewSmoothSeconds = 0.30f;
        private const float SwapMarginMeters = 1.0f;       // pair counts as crossed over beyond this lateral gap
        private const float SwapHoldSeconds = 0.5f;
        private const float SingleSideMarginMeters = 0.8f;  // lone fighter flips side only past this lateral offset
        private const float PincerWindowSeconds = 0.45f;    // partner counts as committed this long after its last attack stage

        private static readonly List<int> _fighters = new List<int>(4);
        private static readonly List<int> _scratch = new List<int>(4);
        private static readonly Dictionary<int, int> _sides = new Dictionary<int, int>();
        private static readonly Dictionary<int, float> _lastAttackAt = new Dictionary<int, float>();
        private static float _refreshTimer;
        private static float _swapTimer;
        private static float _now;
        private static Vec2 _view;
        private static bool _viewValid;

        public static void SetParticipants(IEnumerable<int> indices)
        {
            Reset();
            foreach (int i in indices)
                _fighters.Add(i);
            CombatAiLog.Log($"[DUO] participants set: [{string.Join(",", _fighters)}].");
        }

        public static void Reset()
        {
            _fighters.Clear();
            _sides.Clear();
            _lastAttackAt.Clear();
            _refreshTimer = 0f;
            _swapTimer = 0f;
            _viewValid = false;
        }

        public static bool IsParticipant(int index)
        {
            return index >= 0 && _fighters.Contains(index);
        }

        // A roster member beyond the slotted pair: driven (so it promotes the
        // moment a side frees) but held in reserve -- no slot, no attacks.
        public static bool IsWaiting(int index)
        {
            return IsParticipant(index) && !_sides.ContainsKey(index);
        }

        // +1 = the player's left front slot, -1 = the right one, 0 = not assigned yet.
        public static int SideOf(int index)
        {
            int s;
            return _sides.TryGetValue(index, out s) ? s : 0;
        }

        // Smoothed player view direction (unit vector); false until the first tick.
        public static bool TryGetViewFacing(out Vec2 facing)
        {
            facing = _view;
            return _viewValid;
        }

        // The other live AI fighter (the "ally" an attacker must not swing toward).
        // Never the player -- the defender is the target, not a partner.
        public static Agent GetPartnerAgent(int index, Mission mission)
        {
            Agent main = mission != null ? mission.MainAgent : null;
            for (int i = 0; i < _fighters.Count; i++)
            {
                if (_fighters[i] == index)
                    continue;
                Agent a = LiveAgent(mission, _fighters[i]);
                if (a != null && (main == null || a.Index != main.Index))
                    return a;
            }
            return null;
        }

        // True while the partner is mid-attack (or just was): the caller may open its own
        // attack without waiting out its tempo breather.
        public static bool PartnerCommitting(int index)
        {
            foreach (KeyValuePair<int, float> kv in _lastAttackAt)
                if (kv.Key != index && _now - kv.Value < PincerWindowSeconds)
                    return true;
            return false;
        }

        // Ticked from CombatAiMissionBehavior while DuoAttackMode is on.
        public static void Tick(float dt, Mission mission)
        {
            if (mission == null)
                return;
            _now = mission.CurrentTime;

            _refreshTimer += dt;
            if (_refreshTimer >= RefreshInterval)
            {
                _refreshTimer = 0f;
                // Replacement participants (AO keeps spawning up to the total) join
                // the rotation automatically. The player (MainAgent) is the
                // defender, never an attacker -- keep him out of the roster or he
                // occupies one of the two side slots and the 2nd AI stands.
                Agent main = mission.MainAgent;
                int mainIdx = main != null ? main.Index : -1;
                if (ArenaOverhaulBridge.TryGetArenaParticipantIndices(mission, _scratch))
                {
                    for (int i = 0; i < _scratch.Count; i++)
                    {
                        if (_scratch[i] == mainIdx)
                            continue;
                        if (!_fighters.Contains(_scratch[i]))
                        {
                            _fighters.Add(_scratch[i]);
                            CombatAiLog.Log($"[DUO] replacement fighter joined: #{_scratch[i]}.");
                        }
                    }
                }
            }

            PruneDead(mission);

            Agent player = mission.MainAgent;
            if (player == null || !player.IsActive())
                return;

            UpdateView(dt, player);
            TrackAttacks(mission);
            AssignSides(dt, mission, player);
        }

        private static void UpdateView(float dt, Agent player)
        {
            Vec2 raw = CombatAiOverrideComponent.ViewFacingOf(player);
            if (raw.LengthSquared < 0.0001f)
                return;
            raw.Normalize();
            if (!_viewValid)
            {
                _view = raw;
                _viewValid = true;
                return;
            }
            float a = 1f - (float)Math.Exp(-dt / ViewSmoothSeconds);
            Vec2 v = _view + (raw - _view) * a;
            if (v.LengthSquared < 0.0001f)
                v = raw;
            v.Normalize();
            _view = v;
        }

        private static void TrackAttacks(Mission mission)
        {
            for (int i = 0; i < _fighters.Count; i++)
            {
                Agent a = LiveAgent(mission, _fighters[i]);
                if (a == null)
                    continue;
                Agent.ActionStage st = a.GetCurrentActionStage(1);
                if (st == Agent.ActionStage.AttackReady || st == Agent.ActionStage.AttackQuickReady
                    || st == Agent.ActionStage.AttackRelease)
                    _lastAttackAt[a.Index] = _now;
            }
        }

        // Signed lateral offset of an agent from the player's view axis, in metres:
        // > 0 = on the player's LEFT (same cross convention as AllyForbiddenAttackDir).
        private static float Lateral(Agent a, Agent player)
        {
            Vec2 d = a.Position.AsVec2 - player.Position.AsVec2;
            return _view.x * d.y - _view.y * d.x;
        }

        private static void AssignSides(float dt, Mission mission, Agent player)
        {
            if (!_viewValid)
                return;

            Agent a = null, b = null;
            for (int i = 0; i < _fighters.Count; i++)
            {
                Agent f = LiveAgent(mission, _fighters[i]);
                if (f == null || f.Index == player.Index)
                    continue;
                if (a == null) a = f;
                else if (b == null) b = f;
                // 3rd+ live fighter: stays in _fighters (auto-promotes when a
                // slot frees) but gets NO side -- it waits in reserve instead
                // of crowding in as a 3rd+ attacker.
            }
            if (a == null)
                return;

            if (b == null)
            {
                _swapTimer = 0f;
                AssignLone(a, player);
                return;
            }

            int sa = SideOf(a.Index), sb = SideOf(b.Index);
            float la = Lateral(a, player), lb = Lateral(b, player);
            if (sa == 0 && sb == 0 || sa == sb)
            {
                // Fresh pair (or a corrupted one): the more-left fighter takes the left slot.
                bool aLeft = la >= lb;
                _sides[a.Index] = aLeft ? 1 : -1;
                _sides[b.Index] = aLeft ? -1 : 1;
                _swapTimer = 0f;
                return;
            }
            if (sa == 0 || sb == 0)
            {
                // A replacement joined: it takes the side its partner is not using.
                if (sa == 0) _sides[a.Index] = -sb;
                else _sides[b.Index] = -sa;
                _swapTimer = 0f;
                return;
            }

            // Both assigned and distinct: swap only when the pair has genuinely crossed over.
            float leftLat = sa > 0 ? la : lb;
            float rightLat = sa > 0 ? lb : la;
            if (leftLat < rightLat - SwapMarginMeters)
            {
                _swapTimer += dt;
                if (_swapTimer >= SwapHoldSeconds)
                {
                    _sides[a.Index] = -sa;
                    _sides[b.Index] = -sb;
                    _swapTimer = 0f;
                    CombatAiLog.Log($"[DUO] sides swapped (pair crossed over): #{a.Index}->{(-sa > 0 ? "L" : "R")} #{b.Index}->{(-sb > 0 ? "L" : "R")}.");
                }
            }
            else
                _swapTimer = 0f;
        }

        private static void AssignLone(Agent a, Agent player)
        {
            float lat = Lateral(a, player);
            int s = SideOf(a.Index);
            if (s == 0)
                _sides[a.Index] = lat >= 0f ? 1 : -1;
            else if (s > 0 && lat < -SingleSideMarginMeters)
                _sides[a.Index] = -1;
            else if (s < 0 && lat > SingleSideMarginMeters)
                _sides[a.Index] = 1;
        }

        private static void PruneDead(Mission mission)
        {
            for (int i = _fighters.Count - 1; i >= 0; i--)
            {
                if (LiveAgent(mission, _fighters[i]) == null)
                {
                    _sides.Remove(_fighters[i]);
                    _lastAttackAt.Remove(_fighters[i]);
                    _fighters.RemoveAt(i);
                }
            }
        }

        private static Agent LiveAgent(Mission mission, int index)
        {
            if (mission == null || index < 0)
                return null;
            Agent a = mission.FindAgentWithIndex(index);
            if (a == null || !a.IsActive() || a.Health <= 0f)
                return null;
            return a;
        }
    }
}
