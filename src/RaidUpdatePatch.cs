using System.Runtime.CompilerServices;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    [HarmonyPatch(typeof(MapEvent), "Update")]
    internal static class RaidUpdatePatch
    {
        private sealed class UpdateLog
        {
            internal bool MapReported;
            internal bool MissionReported;
            internal bool ClosingReported;
        }

        private static readonly ConditionalWeakTable<MapEvent, UpdateLog> Reported =
            new ConditionalWeakTable<MapEvent, UpdateLog>();

        internal static bool DiagnosticsOnly;

        internal static bool IsQuestRaid(MapEvent mapEvent)
        {
            if (mapEvent?.IsRaid != true || mapEvent.MapEventSettlement?.IsVillage != true)
                return false;

            string attackerId = mapEvent.AttackerSide.LeaderParty?.MobileParty?.StringId;
            return attackerId == "ebdi_deserters_party_1_new" || attackerId == "ebdi_deserters_party_1";
        }

        private static bool IsPlayerEncounter(MapEvent mapEvent)
        {
            return ReferenceEquals(mapEvent, MapEvent.PlayerMapEvent)
                && (ReferenceEquals(mapEvent, PlayerEncounter.Battle)
                    || ReferenceEquals(mapEvent.AttackerSide.LeaderParty, PlayerEncounter.EncounteredParty));
        }

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(MapEvent __instance, out string __state)
        {
            __state = null;
            if (!CoopSession.IsGameplayHost || __instance.IsFinalized)
                return true;

            Mission mission = Mission.Current;
            if (mission != null)
            {
                if (mission.CurrentState == Mission.State.Over
                    || !DeploymentDiagnostics.IsTrackedQuestMission(mission, out MapEvent missionEvent)
                    || !ReferenceEquals(__instance, missionEvent))
                    return true;
            }
            else if (!IsQuestRaid(__instance) || !IsPlayerEncounter(__instance)
                || __instance.HasWinner || __instance.PlayerSide != BattleSideEnum.Defender
                || PlayerEncounter.CurrentBattleSimulation != null)
            {
                return true;
            }

            UpdateLog reported = Reported.GetValue(__instance, _ => new UpdateLog());
            if (mission == null && !reported.MapReported)
            {
                reported.MapReported = true;
                Report(__instance, "encounter");
            }
            else if (mission != null && (mission.MissionEnded || __instance.HasWinner)
                && !reported.ClosingReported)
            {
                reported.ClosingReported = true;
                Report(__instance, "mission-ending");
            }
            else if (mission != null && !reported.MissionReported)
            {
                reported.MissionReported = true;
                Report(__instance, "mission");
            }

            if (DiagnosticsOnly)
                __state = BattleLog.Event(__instance);

            // MapEventManager continues calling Update for raids involving the main party.
            // The mission still uses the raid roster while it removes its agents after victory.
            return DiagnosticsOnly;
        }

        private static void Report(MapEvent mapEvent, string phase)
        {
            BattleLog.Write($"Raid update {(DiagnosticsOnly ? "observed" : "held")} " +
                $"phase={phase} {BattleLog.Event(mapEvent)}");
        }

        private static void Postfix(MapEvent __instance, string __state)
        {
            if (__state == null)
                return;

            string after = BattleLog.Event(__instance);
            if (after != __state)
                BattleLog.Write($"Raid update changed state before=[{__state}] after=[{after}]");
        }
    }
}
