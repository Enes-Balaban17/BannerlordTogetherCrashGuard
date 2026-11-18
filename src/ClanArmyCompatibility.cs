using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTogether;
using BannerlordTogether.Network;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    internal sealed class ClanArmyCompatibility : CampaignBehaviorBase
    {
        private static readonly HashSet<string> Leaders = new HashSet<string>();
        private static bool _open;
        private static Type Sync => typeof(ArmySyncBehavior);
        public ClanArmyCompatibility() { Leaders.Clear(); _open = false; }
        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, _ =>
            {
                foreach (Army army in Campaign.Current.MobileParties.Where(party => party.Army != null && Leaders.Contains(party.StringId))
                    .Select(party => party.Army).Distinct())
                {
                    if (AccessTools.Field(typeof(Army), "_tickEvent").GetValue(army) == null)
                        AccessTools.Method(typeof(Army), "OnAfterLoad").Invoke(army, null);
                    if (CoopSession.IsClient) AccessTools.Method(Sync, "DisableArmyPeriodicTicks").Invoke(null, new object[] { army });
                }
            });
        }
        public override void SyncData(IDataStore store)
        {
            string ids = store.IsSaving ? string.Join("\n", Leaders) : "";
            store.SyncData("btcg_clan_army_leaders", ref ids);
            if (store.IsLoading) Leaders.UnionWith((ids ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }
        internal static void Install(Harmony harmony)
        {
            Patch(harmony, Sync, "TryEnsurePlayerArmyForLeader", nameof(Ensure));
            Patch(harmony, Sync, "CreateClientArmy", nameof(ClientArmy));
            Patch(harmony, typeof(Army), "HourlyTick", nameof(Hourly));
            Patch(harmony, typeof(Army), "FindBestGatheringSettlementAndMoveTheLeader", nameof(Gather));
            harmony.Patch(AccessTools.Method(Sync, "GetNativeArmyUiRiskReason"),
                postfix: new HarmonyMethod(typeof(ClanArmyCompatibility), nameof(UiRisk)));
            harmony.Patch(AccessTools.Method(Sync, "HandlePlayerArmyResult"),
                postfix: new HarmonyMethod(typeof(ClanArmyCompatibility), nameof(Result)));
        }
        private static void Patch(Harmony harmony, Type type, string target, string prefix) =>
            harmony.Patch(AccessTools.Method(type, target) ?? throw new MissingMethodException(type.FullName, target),
                new HarmonyMethod(typeof(ClanArmyCompatibility), prefix));
        internal static bool Independent(Army army) => army?.Kingdom == null && army?.LeaderParty != null &&
            Leaders.Contains(army.LeaderParty.StringId) && army.LeaderParty.LeaderHero?.Clan?.Kingdom == null;
        private static bool Human(MobileParty party) => party != null && (party == MobileParty.MainParty ||
            CoopSession.GhostPartyIds.Contains(party.StringId));
        internal static bool Available(MobileParty party) => party != null && party.IsActive && !party.IsDisbanding &&
            party.LeaderHero != null && !party.LeaderHero.IsDead && !party.LeaderHero.IsPrisoner &&
            party.MapEvent == null && party.SiegeEvent == null && !party.IsCurrentlyAtSea && !party.IsInRaftState;
        private static Army Create(MobileParty leader, Army.ArmyTypes type)
        {
            Leaders.Add(leader.StringId);
            Army army = (Army)AccessTools.Method(Sync, "CreateArmyAllowingRemotePlayerProxyForDebug")
                .Invoke(null, new object[] { null, leader, type, "Crash Guard independent clan army" });
            if (army == null) throw new InvalidOperationException("Native BT clan army creation failed; see BT log");
            if (leader == MobileParty.MainParty && Game.Current.GameStateManager.ActiveState is MapState map)
                AccessTools.Method(typeof(MapState), "OnArmyCreated").Invoke(map, new object[] { leader });
            return army;
        }
        private static bool Ensure(MobileParty leader, ref Army army, ref string reason, ref bool __result)
        {
            if (!CoopSession.IsActive || leader?.LeaderHero?.Clan == null || leader.LeaderHero.Clan.Kingdom != null ||
                leader.Army != null || !Human(leader)) return true;
            if (!Available(leader)) { army = null; reason = "clan-army-leader-unavailable"; __result = false; return false; }
            try { army = Create(leader, Army.ArmyTypes.Defender); reason = ""; __result = true; }
            catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error))
            { army = null; reason = "clan-army-creation-failed"; __result = false; BattleLog.Write("[CLAN-ARMY] " + error); }
            return false;
        }
        private static bool ClientArmy(MobileParty leaderParty, Army.ArmyTypes armyType, string kingdomId, ref Army __result)
        {
            if (!CoopSession.IsClient || !string.IsNullOrEmpty(kingdomId) || leaderParty?.LeaderHero?.Clan == null ||
                leaderParty.LeaderHero.Clan.Kingdom != null || !Human(leaderParty)) return true;
            try
            {
                Leaders.Add(leaderParty.StringId);
                __result = new Army(null, leaderParty, armyType);
                AccessTools.Method(Sync, "DisableArmyPeriodicTicks").Invoke(null, new object[] { __result });
            }
            catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error))
            { __result = null; BattleLog.Write("[CLAN-ARMY] client creation failed " + error); }
            return false;
        }
        private static bool Hourly(Army __instance)
        {
            if (__instance.Kingdom == null && __instance.LeaderParty != null && Leaders.Contains(__instance.LeaderParty.StringId) &&
                __instance.LeaderParty.LeaderHero?.Clan?.Kingdom != null)
                __instance.Kingdom = __instance.LeaderParty.LeaderHero.Clan.Kingdom;
            if (!Independent(__instance)) return true;
            if (!Available(__instance.LeaderParty))
            {
                // Native captivity/death/battle lifecycle owns teardown during encounters.
                if (!__instance.LeaderParty.IsActive || __instance.LeaderParty.LeaderHero == null ||
                    __instance.LeaderParty.LeaderHero.IsDead || __instance.LeaderParty.LeaderHero.IsPrisoner)
                    DisbandArmyAction.ApplyByUnknownReason(__instance);
                return false;
            }
            // A same-clan human-led army has no kingdom influence/cohesion economy.
            // Keep native proximity merging (Army.Tick), food consumption and battles.
            __instance.Cohesion = 100;
            AccessTools.Method(typeof(Army), "RecalculateArmyMorale").Invoke(__instance, null);
            return false;
        }
        private static bool Gather(Army __instance) => !Independent(__instance);
        private static void UiRisk(Army army, ref string __result)
        {
            if (Independent(army)) __result = string.Join(",", (__result ?? "").Split(',').Where(risk => risk != "kingdom-null"));
        }
        private static void Result(PlayerArmyResultPacket result)
        {
            if (!CoopSession.IsClient) return;
            InformationManager.DisplayMessage(new InformationMessage("[Crash Guard] Army: " + (result.Accepted ? "membership updated" : result.Reason)));
        }
        internal static void Tick()
        {
            if (Input.IsKeyDown(InputKey.LeftAlt) && Input.IsKeyPressed(InputKey.A)) Show();
        }
        private static bool MapReady() => CoopSession.IsActive && CoopSession.IsSessionReady && Mission.Current == null &&
            Game.Current?.GameStateManager.ActiveState is MapState && Campaign.Current?.CurrentMenuContext == null && Available(MobileParty.MainParty);
        internal static void Show()
        {
            if (_open || !MapReady()) return;
            var options = new List<InquiryElement>();
            MobileParty local = MobileParty.MainParty;
            if (local.Army == null || local.Army.LeaderParty == local)
                options.Add(new InquiryElement("clan", "Call my clan parties", null));
            if (local.Army != null)
            {
                options.Add(new InquiryElement("leave", local.Army.LeaderParty == local ? "Disband my army" : "Leave army", null));
                if (local.Army.LeaderParty != local)
                    options.Add(new InquiryElement(local.Army.LeaderParty, "Merge with my army leader (when nearby)", null));
            }
            if (local.Army == null)
                foreach (MobileParty leader in Campaign.Current.MobileParties.Where(party => party != local && Human(party) && Available(party) &&
                    party.CurrentSettlement == null && (party.Army == null || party.Army.LeaderParty == party) &&
                    !FactionManager.IsAtWarAgainstFaction(local.MapFaction, party.MapFaction)))
                    options.Add(new InquiryElement(leader, "Join " + leader.LeaderHero.Name + "'s army", null));
            if (options.Count == 0) { Info("No available army actions. Leave the settlement/battle first."); return; }
            Pick("Clan and co-op armies", "Independent clan armies need no kingdom. Players choose whether to join. Nearby joining players merge; distant parties travel normally.", options, selected =>
            {
                if (!MapReady()) return;
                if (selected is MobileParty leader) Intent(leader, PlayerArmyActionKind.Join);
                else if ((string)selected == "clan") CallClan();
                else if (local.Army?.LeaderParty == local && CoopSession.IsHost)
                { DisbandArmyAction.ApplyByUnknownReason(local.Army); Dirty(); }
                else if (local.Army?.LeaderParty == local)
                    CoopCompatibilityBehavior.Instance.PublishChanges(new Dictionary<string, string> { ["army/disband"] = "1" });
                else if (local.Army != null) Intent(local.Army.LeaderParty, PlayerArmyActionKind.Leave);
            });
        }
        private static void CallClan()
        {
            MobileParty local = MobileParty.MainParty;
            var options = Campaign.Current.MobileParties.Where(party => EligibleMember(local, party))
                .Select(party => new InquiryElement(party, party.Name.ToString(), null)).ToList();
            if (options.Count == 0) { Info("No available clan parties. Create a companion/family party in the Clan screen first."); return; }
            Pick("Call a clan party", "Only your clan's available NPC war parties are listed. Repeat to call another party. Human players join through their own army menu.", options, selected =>
            {
                var party = selected as MobileParty;
                if (!MapReady() || !EligibleMember(local, party)) return;
                if (CoopSession.IsClient)
                {
                    CoopCompatibilityBehavior.Instance.PublishChanges(new Dictionary<string, string> { ["army/call"] = party.StringId });
                    return;
                }
                Call(local, party);
            });
        }
        private static void Call(MobileParty leader, MobileParty party)
        {
            Army army = leader.Army;
            if (army != null && army.LeaderParty != leader) throw new InvalidOperationException("Only the army leader can call parties");
            if (army == null)
            {
                Kingdom kingdom = leader.LeaderHero.Clan.Kingdom;
                army = kingdom == null ? Create(leader, Army.ArmyTypes.Defender) :
                    (Army)AccessTools.Method(Sync, "CreateArmyAllowingRemotePlayerProxyForDebug").Invoke(null,
                        new object[] { kingdom, leader, Army.ArmyTypes.Defender, "Crash Guard clan party call" });
                if (army == null) throw new InvalidOperationException("Could not create clan army");
            }
            party.Army = army;
            party.SetMoveEscortParty(leader, party.NavigationCapability, false);
            Dirty(); Info("Called " + party.Name + ". The party will travel to the army leader.");
        }
        internal static void Receive(int peer, Dictionary<string, string> values)
        {
            if (!values.ContainsKey("army/call") && !values.ContainsKey("army/disband")) return;
            MobileParty leader = CoopPeerIdentity.HeroForPeer(peer)?.PartyBelongedTo;
            if (!Available(leader) || !Human(leader) || leader.CurrentSettlement != null ||
                (leader.Army != null && leader.Army.LeaderParty != leader)) return;
            if (values.TryGetValue("army/call", out string id))
            {
                MobileParty party = Campaign.Current.MobileParties.FirstOrDefault(candidate => candidate.StringId == id);
                if (EligibleMember(leader, party)) Call(leader, party);
            }
            else if (leader.Army != null)
            {
                DisbandArmyAction.ApplyByUnknownReason(leader.Army); Dirty();
            }
        }
        internal static bool EligibleMember(MobileParty leader, MobileParty party) => Available(leader) && Available(party) &&
            party != leader && !Human(party) && party.IsLordParty && party.Army == null &&
            party.LeaderHero.Clan == leader.LeaderHero.Clan && party.CurrentSettlement?.SiegeEvent == null &&
            !leader.LeaderHero.Clan.IsUnderMercenaryService;
        private static void Intent(MobileParty leader, PlayerArmyActionKind action)
        {
            MobileParty member = MobileParty.MainParty;
            if (ArmySyncBehavior.Instance == null || leader == member || !Available(member) || !Available(leader)) return;
            var sequence = AccessTools.Field(Sync, "_playerArmyIntentId");
            uint id = (uint)sequence.GetValue(ArmySyncBehavior.Instance) + 1;
            sequence.SetValue(ArmySyncBehavior.Instance, id);
            string Source(MobileParty party) => (string)AccessTools.Method(Sync, "ResolveGameplaySourceKey").Invoke(null, new object[] { party });
            float range = leader.IsCurrentlyAtSea ? 0 : Campaign.Current.Models.EncounterModel.MaximumAllowedLandDistanceForEncounteringMobilePartyInArmy;
            var packet = new PlayerArmyIntentPacket { IntentId = id, ActionKind = action,
                LeaderSourceKey = Source(leader), MemberSourceKey = Source(member),
                Merge = action == PlayerArmyActionKind.Join && (leader.Position - member.Position).LengthSquared < range,
                Context = "Crash Guard army menu" };
            if (action == PlayerArmyActionKind.Join && member.Army?.LeaderParty == leader && !packet.Merge)
            { Info("Move near the army leader, then choose Merge again. No teleport is performed."); return; }
            if (CoopSession.IsHost) ArmySyncBehavior.Instance.HandlePlayerArmyIntent(packet);
            else CoopSession.Client?.SendRaw(PacketSerializer.SerializePlayerArmyIntent(packet));
        }
        private static void Dirty()
        {
            if (ArmySyncBehavior.Instance != null) AccessTools.Method(Sync, "MarkNativeArmySyncDirty").Invoke(ArmySyncBehavior.Instance, new object[] { "Crash Guard clan army action" });
        }
        private static void Pick(string title, string text, List<InquiryElement> options, Action<object> action)
        {
            _open = true;
            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(title, text, options, true, 1, 1, "Select", "Cancel",
                selected => { _open = false; try { if (selected.Count > 0) action(selected[0].Identifier); }
                    catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error)) { BattleLog.Write("[CLAN-ARMY] " + error); Info("Army action failed. See battle.log."); } },
                _ => { _open = false; }));
        }
        private static void Info(string text) => InformationManager.DisplayMessage(new InformationMessage("[Crash Guard] " + text));
    }
    public static class ClanArmyCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("army", "btcg")]
        public static string Menu(List<string> args) { ClanArmyCompatibility.Show(); return "Army menu: Alt+A on the campaign map."; }
    }
}
