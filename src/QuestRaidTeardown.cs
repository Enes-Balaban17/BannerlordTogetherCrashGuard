using System;
using System.Collections.Generic;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.AgentOrigins;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    internal static class QuestRaidTeardown
    {
        private sealed class Cleanup
        {
            internal readonly Dictionary<PartyGroupAgentOrigin, CharacterObject> Characters =
                new Dictionary<PartyGroupAgentOrigin, CharacterObject>();
            internal Cleanup Previous;
            internal int RecoveredHeroes;
            internal int SkippedCallbacks;
        }

        [ThreadStatic]
        private static Cleanup _cleanup;

        [HarmonyPatch(typeof(Mission), "EndMissionInternal")]
        private static class EndMission
        {
            private static void Prefix(Mission __instance, out Cleanup __state)
            {
                __state = null;
                if (!CoopSession.IsGameplayHost
                    || !DeploymentDiagnostics.IsTrackedQuestMission(__instance, out MapEvent mapEvent))
                    return;

                BattleLog.Write($"[QUEST-RAID-EXIT] begin {BattleLog.Event(mapEvent)}");
                if (RaidUpdatePatch.DiagnosticsOnly)
                    return;

                var cleanup = new Cleanup { Previous = _cleanup };
                foreach (Agent agent in __instance.Agents)
                {
                    if (agent.Origin is PartyGroupAgentOrigin origin)
                        cleanup.Characters[origin] = agent.Character as CharacterObject;
                }

                __state = cleanup;
                _cleanup = cleanup;
            }

            private static void Finalizer(Cleanup __state, Exception __exception)
            {
                if (__state == null)
                    return;

                _cleanup = __state.Previous;
                BattleLog.Write($"[QUEST-RAID-EXIT] {(__exception == null ? "completed" : "failed")} " +
                    $"heroHealthRecovered={__state.RecoveredHeroes} callbacksSkipped={__state.SkippedCallbacks}" +
                    (__exception == null ? "" : $" error={__exception.GetType().Name}"));
            }
        }

        [HarmonyPatch(typeof(PartyGroupAgentOrigin), nameof(PartyGroupAgentOrigin.OnAgentRemoved))]
        private static class RemoveAgent
        {
            private static bool Prefix(PartyGroupAgentOrigin __instance, float agentHealth)
            {
                Cleanup cleanup = _cleanup;
                if (cleanup == null || !cleanup.Characters.TryGetValue(__instance, out CharacterObject character))
                    return true;

                CharacterObject campaignTroop = __instance.Troop;
                if (campaignTroop != null && (!campaignTroop.IsHero || campaignTroop.HeroObject != null))
                    return true;

                // The roster entry may already be gone. Use the actual mission character
                // for the same hero health write that the native removal callback performs.
                Hero hero = character?.HeroObject;
                if (hero != null && !hero.IsDead)
                {
                    hero.HitPoints = TaleWorlds.Library.MathF.Max(1, TaleWorlds.Library.MathF.Round(agentHealth));
                    cleanup.RecoveredHeroes++;
                }
                else
                {
                    cleanup.SkippedCallbacks++;
                }

                return false;
            }
        }
    }
}
