using System;
using System.Collections;
using System.Reflection;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CombatAI
{
    // =======================================================================
    // MasterStrikes BRIDGE (Session 50) -- reflection-only access to the
    // MasterStrikes mod's MasterStrikeLogic / ClinchLogic so our spam counter
    // can start a master strike or clinch DIRECTLY (bypassing MasterStrikes'
    // own AI triggers, which our compat layer voids anyway).
    //
    // Everything is a silent no-op when MasterStrikes is absent: handles are
    // resolved lazily once; any missing type/member latches _failed after one
    // [SPAMCOUNTER] WARN and is never retried.
    //
    // Verified against the reference source: MasterStrikesBehavior.Instance,
    // .GetMasterstrikeAgents / .GetClinchAgents (Dictionary<Agent,Agent>,
    // affector=performer -> affected=victim), MasterStrikeLogic / ClinchLogic
    // public ctor(performer, victim) -- the ctor itself removes the performer
    // from the dictionary when the weapon pair has no valid animation.
    // =======================================================================
    internal static class MasterStrikesBridge
    {
        private static bool _resolved;
        private static bool _failed;
        private static PropertyInfo _instanceProp;
        private static PropertyInfo _msAgentsProp, _clinchAgentsProp;
        private static Type _msLogicType, _clinchLogicType;
        private static MethodInfo _isBusy, _isStaggered, _isAgentCorrect, _isInAnyMsAction;

        private static Type SafeGetType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType(name);
                if (t != null)
                    return t;
            }
            return null;
        }

        private static void Fail(string why)
        {
            _failed = true;
            CombatAiLog.Log($"[SPAMCOUNTER] WARN: MasterStrikes bridge disabled ({why}).");
        }

        private static bool Ensure()
        {
            if (_failed)
                return false;
            if (_resolved)
                return _instanceProp != null;
            _resolved = true;
            try
            {
                Type beh = SafeGetType("MasterStrikes.MasterStrikesBehavior");
                _msLogicType = SafeGetType("MasterStrikes.MasterStrikeLogic");
                _clinchLogicType = SafeGetType("MasterStrikes.ClinchLogic");
                Type ext = SafeGetType("MasterStrikes.AgentExtensions");
                if (beh == null || _msLogicType == null || _clinchLogicType == null || ext == null)
                {
                    Fail("MasterStrikes types not found -- mod absent?");
                    return false;
                }
                _instanceProp = beh.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _msAgentsProp = beh.GetProperty("GetMasterstrikeAgents", BindingFlags.Public | BindingFlags.Instance);
                _clinchAgentsProp = beh.GetProperty("GetClinchAgents", BindingFlags.Public | BindingFlags.Instance);
                _isBusy = ext.GetMethod("IsBusy", BindingFlags.Public | BindingFlags.Static);
                _isStaggered = ext.GetMethod("IsStaggered", BindingFlags.Public | BindingFlags.Static);
                _isAgentCorrect = ext.GetMethod("IsAgentCorrect", BindingFlags.Public | BindingFlags.Static);
                _isInAnyMsAction = ext.GetMethod("IsInAnyMSAction", BindingFlags.Public | BindingFlags.Static);
                if (_instanceProp == null || _msAgentsProp == null || _clinchAgentsProp == null
                    || _isBusy == null || _isStaggered == null || _isAgentCorrect == null || _isInAnyMsAction == null)
                {
                    Fail("MasterStrikes member(s) missing (version drift?)");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Fail(ex.GetType().Name + " during reflection");
                return false;
            }
        }

        private static bool Ext(MethodInfo m, Agent a)
            => (bool)m.Invoke(null, new object[] { a });

        // Try to start a MasterStrike (preferred) or Clinch (fallback), or
        // vice versa. Mirrors MasterStrikesBehavior.OnAgentHit's gates minus
        // the skill/setting gates (our own chance roll covers that).
        public static bool TryStart(Agent ai, Agent player, bool preferMasterStrike, out string what)
        {
            what = "none (no valid animation for this weapon pair)";
            try
            {
                if (!Ensure())
                    return false;
                object inst = _instanceProp.GetValue(null);
                if (inst == null)
                    return false;
                if (ai == null || player == null)
                    return false;
                if (!Ext(_isAgentCorrect, ai) || !Ext(_isAgentCorrect, player))
                    return false;
                if (Ext(_isBusy, ai) || Ext(_isStaggered, ai) || Ext(_isInAnyMsAction, ai)
                    || Ext(_isBusy, player) || Ext(_isStaggered, player) || Ext(_isInAnyMsAction, player))
                    return false;
                if (ai.MountAgent != null || player.MountAgent != null)
                    return false;
                if (!ai.IsEnemyOf(player))
                    return false;
                if (ai.WieldedWeapon.IsEmpty || player.WieldedWeapon.IsEmpty)
                    return false;
                Vec2 d = new Vec2(ai.Position.x - player.Position.x, ai.Position.y - player.Position.y);
                if (d.Length > 2.1f)
                    return false;

                for (int i = 0; i < 2; i++)
                {
                    bool wantMs = (i == 0) == preferMasterStrike;
                    IDictionary dict = (IDictionary)(wantMs ? _msAgentsProp : _clinchAgentsProp).GetValue(inst);
                    if (dict == null || dict.Contains(ai))
                        continue;
                    Type logic = wantMs ? _msLogicType : _clinchLogicType;
                    try
                    {
                        dict[ai] = player;
                        Activator.CreateInstance(logic, ai, player);
                    }
                    catch
                    {
                        dict.Remove(ai);
                        continue;
                    }
                    if (dict.Contains(ai))
                    {
                        what = wantMs ? "MasterStrike" : "Clinch";
                        return true;
                    }
                    // ctor removed the entry itself: no valid animation for this pair.
                }
                return false;
            }
            catch (Exception ex)
            {
                CombatAiLog.Log($"[SPAMCOUNTER] WARN: TryStart threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
