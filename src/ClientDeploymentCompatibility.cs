using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    internal static class ClientDeploymentCompatibility
    {
        private sealed class PendingPlayer : Exception { }
        private sealed class State
        {
            internal int Sides;
            internal bool Waiting;
            internal long Since;
            internal bool Abort;
            internal bool Ended;
        }
        private static readonly ConditionalWeakTable<DeploymentMissionController, State> States =
            new ConditionalWeakTable<DeploymentMissionController, State>();
        private static bool Scoped(DeploymentMissionController controller) => CoopSession.IsClient && CoopSession.InSpNativeBattle &&
            !CoopSession.IsSpectator && controller?.Mission != null && ReferenceEquals(controller.Mission, Mission.Current);
        internal static bool Expired(double seconds) => seconds >= 30;
        private static Agent Player(Mission mission)
        {
            var character = Hero.MainHero?.CharacterObject;
            Agent agent = mission.InitialPlayerAgent;
            if (agent?.IsActive() == true && character != null && agent.Character == character) return agent;
            agent = mission.MainAgent;
            if (agent?.IsActive() != true || character == null || agent.Character != character) return null;
            // Repair only an existing local human body; never create a substitute or use a remote lord.
            AccessTools.Field(typeof(Mission), "_initialPlayerAgent").SetValue(mission, agent);
            return agent;
        }
        private static Agent RequiredPlayer(Mission mission)
        {
            if (!CoopSession.IsClient || !CoopSession.InSpNativeBattle || CoopSession.IsSpectator ||
                !ReferenceEquals(mission, Mission.Current)) return mission.InitialPlayerAgent;
            Agent agent = Player(mission);
            if (agent == null) throw new PendingPlayer();
            return agent;
        }
        private static void Abort(State state, Mission mission, string reason)
        {
            if (state.Abort) return;
            state.Abort = true;
            BattleLog.Write("[CLIENT-DEPLOY] BROKEN_START_ABORT scene=" + mission.SceneName + " reason=" + reason);
            InformationManager.DisplayMessage(new InformationMessage("[Crash Guard] Client deployment failed. Leaving this battle safely; see battle.log."));
        }
        [HarmonyPatch(typeof(DeploymentMissionController), "SetupTeams")]
        private static class Setup
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo getter = AccessTools.PropertyGetter(typeof(Mission), nameof(Mission.InitialPlayerAgent));
                MethodInfo replacement = AccessTools.Method(typeof(ClientDeploymentCompatibility), nameof(RequiredPlayer));
                int count = 0;
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.Calls(getter)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; count++; }
                    yield return instruction;
                }
                if (count != 1) throw new InvalidOperationException("Expected one deployment InitialPlayerAgent read, found " + count);
            }
            [HarmonyPriority(Priority.Last)]
            private static Exception Finalizer(DeploymentMissionController __instance, Exception __exception)
            {
                if (!Scoped(__instance) || !(__exception is NullReferenceException)) return __exception;
                State state = States.GetValue(__instance, _ => new State());
                Abort(state, __instance.Mission, "SetupTeams null reference: " + __exception);
                // Abort the outer tick too, so TeamSetupOver is never set after failed setup.
                return new PendingPlayer();
            }
        }
        [HarmonyPatch(typeof(DeploymentMissionController), nameof(DeploymentMissionController.OnMissionTick))]
        private static class Tick
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(DeploymentMissionController __instance)
            {
                if (!Scoped(__instance) || !States.TryGetValue(__instance, out State state)) return true;
                if (state.Waiting && !state.Abort)
                {
                    if (Player(__instance.Mission) != null)
                    {
                        state.Waiting = false;
                        BattleLog.Write("[CLIENT-DEPLOY] local player arrived; resuming deployment without repeating completed sides");
                    }
                    else if (Expired((Stopwatch.GetTimestamp() - state.Since) / (double)Stopwatch.Frequency))
                        Abort(state, __instance.Mission, "local player did not arrive within 30 seconds");
                    else return false;
                }
                if (!state.Abort) return true;
                if (!state.Ended)
                {
                    state.Ended = true;
                    try { __instance.Mission.EndMission(); }
                    catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error.GetBaseException()))
                    { BattleLog.Write("[CLIENT-DEPLOY] native battle exit failed " + error); }
                }
                return false;
            }
            private static Exception Finalizer(DeploymentMissionController __instance, Exception __exception)
            {
                if (!Scoped(__instance) || !(__exception is PendingPlayer)) return __exception;
                State state = States.GetValue(__instance, _ => new State());
                if (!state.Waiting && !state.Abort)
                {
                    state.Waiting = true; state.Since = Stopwatch.GetTimestamp();
                    BattleLog.Write("[CLIENT-DEPLOY] waiting for authoritative local player; native setup remains incomplete scene=" + __instance.Mission.SceneName);
                }
                return null;
            }
        }
        [HarmonyPatch]
        private static class Side
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(BattleDeploymentMissionController), "OnSetupTeamsOfSide");
                yield return AccessTools.Method(typeof(SiegeDeploymentMissionController), "OnSetupTeamsOfSide");
            }
            private static bool Prefix(DeploymentMissionController __instance, BattleSideEnum __0)
            {
                if (!Scoped(__instance)) return true;
                State state = States.GetValue(__instance, _ => new State());
                return (state.Sides & (1 << (int)__0)) == 0;
            }
            private static void Postfix(DeploymentMissionController __instance, BattleSideEnum __0, bool __runOriginal)
            {
                if (Scoped(__instance) && __runOriginal) States.GetValue(__instance, _ => new State()).Sides |= 1 << (int)__0;
            }
        }
        [HarmonyPatch(typeof(DeploymentMissionController), nameof(DeploymentMissionController.FinishDeployment))]
        private static class Finish
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(DeploymentMissionController __instance)
            {
                if (!Scoped(__instance) || Player(__instance.Mission) != null) return true;
                State state = States.GetValue(__instance, _ => new State());
                if (!state.Waiting) { state.Waiting = true; state.Since = Stopwatch.GetTimestamp(); }
                return false;
            }
            [HarmonyPriority(Priority.Last)]
            private static Exception Finalizer(DeploymentMissionController __instance, Exception __exception)
            {
                if (!Scoped(__instance) || !(__exception is NullReferenceException)) return __exception;
                Abort(States.GetValue(__instance, _ => new State()), __instance.Mission, "FinishDeployment null reference: " + __exception);
                return null;
            }
        }
    }
}
