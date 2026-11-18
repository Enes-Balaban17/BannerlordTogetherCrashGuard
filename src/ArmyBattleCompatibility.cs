using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace BannerlordTogetherCrashGuard
{
    internal static class ArmyBattleCompatibility
    {
        private static readonly Dictionary<string, string> WireLeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        internal static void Reset() => WireLeaders.Clear();
        internal static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(ArmySyncBehavior), "ApplyBatch", null, nameof(ArmySnapshotApplied));
            Patch(harmony, typeof(BattleSyncBehavior), "TryTriggerArmyBattle", nameof(MatchLeader), null);
            Patch(harmony, typeof(BattleSyncBehavior), "SendLocalBattleStart", nameof(IncludeArmyPeers), null);
        }
        private static void Patch(Harmony harmony, Type type, string method, string prefix, string postfix)
        {
            var target = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.FullName, method);
            if (Harmony.GetPatchInfo(target)?.Owners.Contains(SubModule.Owner) == true) return;
            harmony.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(ArmyBattleCompatibility), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(ArmyBattleCompatibility), postfix));
        }
        private static void ArmySnapshotApplied(ArmySnapshot snap)
        {
            if (!CoopSession.IsClient || Campaign.Current == null) return;
            foreach (ArmyEntry entry in snap.Armies ?? Array.Empty<ArmyEntry>())
            {
                string source = entry.LeaderPartyRef.GameplaySourceKey;
                if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(entry.LeaderPartyId)) continue;
                object[] args = { source, null, null };
                if (!(bool)AccessTools.Method(typeof(ArmySyncBehavior), "TryResolveGameplaySourceParty").Invoke(null, args)) continue;
                var leader = args[1] as MobileParty;
                if (leader != null && ClanArmyCompatibility.Human(leader)) WireLeaders[leader.StringId] = entry.LeaderPartyId;
            }
        }
        private static void MatchLeader(ref BattleStartPacket packet)
        {
            if (!CoopSession.IsClient || !packet.IsHostAuthority) return;
            MobileParty leader = MobileParty.MainParty?.Army?.LeaderParty;
            if (leader == null || !WireLeaders.TryGetValue(leader.StringId, out string wire)) return;
            // The native helper compares raw host IDs to a client's local ghost ID.
            // Change only this private call's copied packet, never the actual manifest.
            packet.AttackerParties = Remap(packet.AttackerParties, wire, leader.StringId);
            packet.DefenderParties = Remap(packet.DefenderParties, wire, leader.StringId);
        }
        internal static BattlePartyRef[] Remap(BattlePartyRef[] rows, string wire, string local)
        {
            if (rows == null || string.IsNullOrEmpty(wire) || string.IsNullOrEmpty(local) || wire == local) return rows;
            BattlePartyRef[] copy = (BattlePartyRef[])rows.Clone();
            for (int index = 0; index < copy.Length; index++)
                if (copy[index].PartyId == wire) copy[index].PartyId = local;
            return copy;
        }
        private static void IncludeArmyPeers(BattleSyncBehavior __instance, MapEvent mapEvent, uint battleId)
        {
            if (!CoopSession.IsGameplayHost || mapEvent == null || mapEvent.IsFinalized || Campaign.Current == null) return;
            MobileParty local = MobileParty.MainParty;
            Army army = local?.Army;
            if (army == null) return;
            MapEventSide side = new[] { mapEvent.AttackerSide, mapEvent.DefenderSide }.FirstOrDefault(candidate =>
                candidate != null && candidate.Parties.Any(party => party.Party == local.Party));
            if (side == null) return;
            var compat = CoopCompatibilityBehavior.Instance;
            if (compat == null) return;
            foreach (int peer in compat.CompatiblePeers())
            {
                MobileParty ghost = CoopPeerIdentity.HeroForPeer(peer)?.PartyBelongedTo;
                if (ghost == null || ghost.Army != army || !ghost.IsActive || ghost.LeaderHero?.IsPrisoner != false ||
                    ghost.CurrentSettlement?.SiegeEvent != null && ghost.CurrentSettlement != mapEvent.MapEventSettlement ||
                    (ghost.Party.MapEventSide != null && ghost.Party.MapEventSide.MapEvent != mapEvent)) continue;
                float range = Campaign.Current.Models.EncounterModel.MaximumAllowedLandDistanceForEncounteringMobilePartyInArmy;
                if (ghost.AttachedTo != army.LeaderParty && (ghost.Position - army.LeaderParty.Position).LengthSquared >= range) continue;
                try
                {
                    if (ghost.Party.MapEventSide?.MapEvent == mapEvent)
                    {
                        // Native helper registers ownership even when the party is already present.
                        AccessTools.Method(typeof(BattleSyncBehavior), "TryAddExactGhostToMapEvent").Invoke(__instance,
                            new object[] { mapEvent, battleId, ghost, null, side.MissionSide });
                    }
                    else
                    {
                        bool added = (bool)AccessTools.Method(typeof(BattleSyncBehavior), "TryAddExactGhostToMapEvent").Invoke(__instance,
                            new object[] { mapEvent, battleId, ghost, null, side.MissionSide });
                        BattleLog.Write("[CLAN-ARMY] battle manifest peer=" + peer + " battle=" + battleId + " side=" + side.MissionSide + " added=" + added);
                    }
                }
                catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error.GetBaseException()))
                { BattleLog.Write("[CLAN-ARMY] battle inclusion failed battle=" + battleId + " peer=" + peer + " " + error); }
            }
        }
    }
}
