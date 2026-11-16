using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    internal static class BattleLog
    {
        internal static readonly string Directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BannerlordTogetherCrashGuard");
        private static readonly object Gate = new object();

        internal static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    File.AppendAllText(Path.Combine(Directory, "battle.log"),
                        $"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static string Event(MapEvent mapEvent)
        {
            if (mapEvent == null)
                return "event=none";

            return $"event={RuntimeHelpers.GetHashCode(mapEvent):x8} state={mapEvent.State} " +
                $"battleState={mapEvent.BattleState} finalized={mapEvent.IsFinalized} " +
                $"playerEvent={ReferenceEquals(mapEvent, MapEvent.PlayerMapEvent)} " +
                $"encounterEvent={ReferenceEquals(mapEvent, PlayerEncounter.Battle)} " +
                $"side={mapEvent.PlayerSide} defenderRoster={mapEvent.DefenderSide.TroopCount} " +
                $"attackerRoster={mapEvent.AttackerSide.TroopCount}";
        }

        internal static string Deployment(Mission mission)
        {
            return Deployment(mission, out _, out _);
        }

        internal static string Deployment(Mission mission, out int defenders, out int attackers)
        {
            defenders = 0;
            attackers = 0;
            foreach (Agent agent in mission.Agents)
            {
                if (!agent.IsHuman)
                    continue;

                if (agent.Team?.Side == BattleSideEnum.Defender)
                    defenders++;
                else if (agent.Team?.Side == BattleSideEnum.Attacker)
                    attackers++;
            }

            var spawn = mission.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
            return $"mode={mission.Mode} t={mission.CurrentTime:F2} " +
                $"defenderTeam={mission.DefenderTeam != null} attackerTeam={mission.AttackerTeam != null} " +
                $"defenderAgents={defenders} attackerAgents={attackers} " +
                $"initialPlayer={Describe(mission.InitialPlayerAgent)} mainAgent={Describe(mission.MainAgent)} " +
                $"spawnLogic={spawn != null} defenderPending={spawn?.DefenderActivePhase?.InitialSpawnNumber} " +
                $"attackerPending={spawn?.AttackerActivePhase?.InitialSpawnNumber}";
        }

        private static string Describe(Agent agent)
        {
            return agent == null ? "absent" : $"{agent.Index}/{agent.Controller}/{agent.State}";
        }
    }
}
