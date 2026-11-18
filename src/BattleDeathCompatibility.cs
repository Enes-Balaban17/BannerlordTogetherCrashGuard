using System;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    internal static class BattleDeathCompatibility
    {
        private static DateTime _lastReport;
        internal static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName("BannerlordTogether.SpNativeBattle.SpNativeBattleClientMissionBehavior");
            var method = AccessTools.Method(type, "ApplySyntheticFatalBlow");
            if (method == null) throw new MissingMethodException("BT synthetic death replay");
            if (Harmony.GetPatchInfo(method)?.Owners.Contains(SubModule.Owner) == true) return;
            harmony.Patch(method, new HarmonyMethod(typeof(BattleDeathCompatibility), nameof(Before)),
                finalizer: new HarmonyMethod(typeof(BattleDeathCompatibility), nameof(FinalizeReplay)));
        }

        private static bool Before(Agent agent, Agent affectorAgent)
        {
            if (!CoopSession.IsClient || !CoopSession.InSpNativeBattle) return true;
            // The caller already has BT's authoritative terminal-state/quarantine fallback.
            // Skipping only this synthetic hit leaves that fallback in control of the death.
            bool usable;
            try
            {
                usable = agent != null && agent.IsActive() && agent.Monster != null &&
                    agent.AgentVisuals != null && agent.AgentVisuals.IsValid() &&
                    affectorAgent != null && affectorAgent.IsActive() && affectorAgent.AgentVisuals != null && affectorAgent.AgentVisuals.IsValid();
            }
            catch (NullReferenceException) { usable = false; }
            if (!usable) Report("skipped invalid synthetic-hit visuals; native BT death fallback continues");
            return usable;
        }

        private static Exception FinalizeReplay(Exception __exception)
        {
            if (__exception is NullReferenceException && CoopSession.IsClient && CoopSession.InSpNativeBattle)
            {
                Report("caught synthetic-hit null reference; native BT death fallback continues");
                return null;
            }
            return __exception;
        }

        private static void Report(string message)
        {
            if ((DateTime.UtcNow - _lastReport).TotalSeconds < 10) return;
            _lastReport = DateTime.UtcNow;
            BattleLog.Write("[BATTLE-DEATH] " + message);
        }
    }
}
