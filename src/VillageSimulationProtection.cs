using System.Linq;
using System.Runtime.CompilerServices;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    [HarmonyPatch(typeof(MapEvent), "SimulateBattleSessionForMapEvent")]
    internal static class VillageSimulationProtection
    {
        private sealed class Marker { }
        private static readonly ConditionalWeakTable<MapEvent, Marker> Reported = new ConditionalWeakTable<MapEvent, Marker>();

        private static bool Prefix(MapEvent __instance)
        {
            if (!CoopSession.IsHost || __instance.MapEventSettlement?.IsVillage != true ||
                PlayerEncounter.CurrentBattleSimulation != null ||
                !__instance.InvolvedParties.Any(party => party?.MobileParty != null &&
                    CoopSession.GhostPartyIds.Contains(party.MobileParty.StringId)))
                return true;
            bool awaitingPlayerBattle = CoopSession.BattleLinkPending ||
                (BattleSyncBehavior.Instance?.HasPendingGhostMerge == true &&
                    ReferenceEquals(PlayerEncounter.Battle, __instance));
            bool inMission = Mission.Current != null &&
                DeploymentDiagnostics.IsTrackedQuestMission(Mission.Current, out MapEvent tracked) &&
                ReferenceEquals(tracked, __instance);
            if (!awaitingPlayerBattle && !inMission)
                return true;
            if (!Reported.TryGetValue(__instance, out _))
            {
                Reported.Add(__instance, new Marker());
                BattleLog.Write("[VILLAGE-SIMULATION] held campaign casualties while co-op mission is pending " + BattleLog.Event(__instance));
            }
            return RaidUpdatePatch.DiagnosticsOnly;
        }
    }
}
