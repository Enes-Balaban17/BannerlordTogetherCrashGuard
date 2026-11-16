using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    internal static class DeploymentCrashGuard
    {
        [Flags]
        private enum FailureStage
        {
            SetupTeams = 1,
            FinishDeployment = 2
        }

        private sealed class GuardState
        {
            internal FailureStage Reported;
            internal FailureStage Pending;
            internal long RecheckAt;
        }

        private static readonly ConditionalWeakTable<Mission, GuardState> Missions =
            new ConditionalWeakTable<Mission, GuardState>();

        [HarmonyPatch(typeof(DeploymentMissionController), "SetupTeams")]
        private static class SetupTeams
        {
            [HarmonyPriority(Priority.Last)]
            private static Exception Finalizer(DeploymentMissionController __instance, Exception __exception)
            {
                return Handle(__instance, __exception, FailureStage.SetupTeams);
            }
        }

        [HarmonyPatch(typeof(DeploymentMissionController), nameof(DeploymentMissionController.FinishDeployment))]
        private static class FinishDeployment
        {
            [HarmonyPriority(Priority.Last)]
            private static Exception Finalizer(DeploymentMissionController __instance, Exception __exception)
            {
                return Handle(__instance, __exception, FailureStage.FinishDeployment);
            }
        }

        private static Exception Handle(DeploymentMissionController controller, Exception exception, FailureStage stage)
        {
            if (exception == null || RaidUpdatePatch.DiagnosticsOnly || !CoopSession.IsGameplayHost
                || !CanSuppress(exception))
                return exception;

            Mission mission = controller?.Mission;
            if (mission == null || !ReferenceEquals(mission, Mission.Current))
                return exception;

            try
            {
                GuardState state = Missions.GetValue(mission, _ => new GuardState());
                if ((state.Reported & stage) == 0)
                {
                    state.Reported |= stage;
                    state.Pending |= stage;
                    state.RecheckAt = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
                    BattleLog.Write($"[DEPLOY-GUARD] suppressed stage={stage} scene={mission.SceneName} " +
                        $"levels={mission.SceneLevels} synchronizedBattle={CoopSession.InSpNativeBattle} " +
                        exception);
                    ReportOutcome(mission, stage, "immediate");
                }
            }
            catch (Exception loggingFailure) when (CanSuppress(loggingFailure))
            {
                BattleLog.Write($"[DEPLOY-GUARD] snapshot unavailable stage={stage} " +
                    $"error={loggingFailure.GetType().Name}");
            }

            return null;
        }

        [HarmonyPatch(typeof(Mission), nameof(Mission.OnTick),
            typeof(float), typeof(float), typeof(bool), typeof(bool))]
        private static class MissionTick
        {
            private static void Postfix(Mission __instance)
            {
                if (!Missions.TryGetValue(__instance, out GuardState state) || state.Pending == 0
                    || Stopwatch.GetTimestamp() < state.RecheckAt)
                    return;

                FailureStage stage = state.Pending;
                state.Pending = 0;
                try
                {
                    ReportOutcome(__instance, stage, "follow-up");
                }
                catch (Exception loggingFailure) when (CanSuppress(loggingFailure))
                {
                    BattleLog.Write($"[DEPLOY-GUARD] follow-up unavailable stage={stage} " +
                        $"error={loggingFailure.GetType().Name}");
                }
            }
        }

        private static void ReportOutcome(Mission mission, FailureStage stage, string sample)
        {
            string details = BattleLog.Deployment(mission, out int defenders, out int attackers);
            bool deploying = mission.Mode == MissionMode.Deployment;
            bool finishFailed = (stage & FailureStage.FinishDeployment) != 0;
            Agent main = mission.MainAgent;
            bool playerAvailable = deploying && !finishFailed
                ? mission.InitialPlayerAgent?.IsActive() == true || main?.IsActive() == true
                : main?.IsActive() == true && main.Controller == AgentControllerType.Player;

            BattleLog.Write($"[DEPLOY-GUARD] {Classify(defenders, attackers, playerAvailable, deploying, finishFailed)} " +
                $"stage={stage} sample={sample} {details}");
        }

        internal static string Classify(int defenders, int attackers, bool playerAvailable, bool deploying,
            bool finishFailed)
        {
            if (defenders == 0 && attackers == 0)
                return "BROKEN_START_EMPTY_BATTLE";
            if (defenders == 0 || attackers == 0)
                return "BROKEN_START_MISSING_SIDE";
            if (!playerAvailable)
                return "BROKEN_START_NO_PLAYER";
            if (deploying && finishFailed)
                return "BROKEN_START_DEPLOYMENT_STALLED";
            return "SPAWN_CHECK_PASSED";
        }

        internal static bool CanSuppress(Exception exception)
        {
            return exception != null
                && !(exception is OutOfMemoryException)
                && !(exception is StackOverflowException)
                && !(exception is ThreadAbortException)
                && !(exception is AccessViolationException)
                && !(exception is SEHException);
        }
    }
}
