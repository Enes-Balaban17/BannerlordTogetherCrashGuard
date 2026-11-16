using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using BannerlordTogether;
using HarmonyLib;
using SandBox;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    internal static class DeploymentDiagnostics
    {
        private static readonly ConditionalWeakTable<Mission, MapEvent> Missions =
            new ConditionalWeakTable<Mission, MapEvent>();

        internal static bool IsTrackedQuestMission(Mission mission, out MapEvent mapEvent)
        {
            return Missions.TryGetValue(mission, out mapEvent) && mission.SceneLevels == "land_raid";
        }

        [HarmonyPatch(typeof(SandBoxMissions), nameof(SandBoxMissions.OpenBattleMission),
            typeof(MissionInitializerRecord))]
        private static class MissionOpen
        {
            private static void Prefix(MissionInitializerRecord rec, out MapEvent __state)
            {
                __state = null;
                MapEvent mapEvent = MapEvent.PlayerMapEvent ?? PlayerEncounter.Battle;
                if (!RaidUpdatePatch.IsQuestRaid(mapEvent))
                    return;

                __state = mapEvent;
                BattleLog.Write($"Opening quest battle levels='{rec.SceneLevels}' host={CoopSession.IsHost} " +
                    $"client={CoopSession.IsClient} synchronizedBattle={CoopSession.InSpNativeBattle} " +
                    BattleLog.Event(mapEvent));
            }

            private static void Postfix(Mission __result, MapEvent __state)
            {
                if (__state != null && __result != null)
                    Missions.GetValue(__result, _ => __state);
            }
        }

        [HarmonyPatch(typeof(MapEvent), "FinalizeEventAux")]
        private static class FinalizeRaid
        {
            private static void Prefix(MapEvent __instance)
            {
                if (!RaidUpdatePatch.IsQuestRaid(__instance) || __instance.IsFinalized)
                    return;

                var caller = new StackTrace(1, false).ToString().Trim();
                BattleLog.Write($"Quest raid finalizing mission={Mission.Current != null} " +
                    $"{BattleLog.Event(__instance)}{Environment.NewLine}{caller}");
            }
        }

        [HarmonyPatch(typeof(DeploymentMissionController), "SetupTeams")]
        private static class SetupTeams
        {
            private static void Prefix(DeploymentMissionController __instance)
            {
                Mission mission = __instance.Mission;
                if (!Missions.TryGetValue(mission, out MapEvent mapEvent))
                    return;

                var tick = AccessTools.Method(typeof(DeploymentMissionController),
                    nameof(DeploymentMissionController.OnMissionTick));
                var owners = Harmony.GetPatchInfo(tick)?.Owners;
                BattleLog.Write($"SetupTeams begin tickOwners=[{(owners == null ? "" : string.Join(",", owners))}] " +
                    $"{BattleLog.Event(mapEvent)} {BattleLog.Deployment(mission)}");
            }

            private static void Postfix(DeploymentMissionController __instance, bool __runOriginal)
            {
                WriteDeployment($"SetupTeams returned originalRan={__runOriginal}", __instance.Mission);
            }

            [HarmonyPriority(Priority.First)]
            private static void Finalizer(DeploymentMissionController __instance, Exception __exception)
            {
                if (__exception != null)
                    WriteDeployment($"SetupTeams threw {__exception.GetType().Name}", __instance.Mission);
            }
        }

        [HarmonyPatch(typeof(BattleDeploymentMissionController), "OnSetupTeamsOfSide")]
        private static class SetupSide
        {
            private static void Postfix(BattleDeploymentMissionController __instance, BattleSideEnum __0,
                bool __runOriginal)
            {
                WriteDeployment($"Side setup returned side={__0} originalRan={__runOriginal}", __instance.Mission);
            }
        }

        [HarmonyPatch(typeof(DeploymentMissionController), nameof(DeploymentMissionController.FinishDeployment))]
        private static class FinishDeployment
        {
            private static void Postfix(DeploymentMissionController __instance, bool __runOriginal)
            {
                WriteDeployment($"FinishDeployment originalRan={__runOriginal}", __instance.Mission);
            }
        }

        private static void WriteDeployment(string stage, Mission mission)
        {
            if (mission != null && Missions.TryGetValue(mission, out MapEvent mapEvent))
                BattleLog.Write($"{stage} {BattleLog.Event(mapEvent)} {BattleLog.Deployment(mission)}");
        }
    }
}
