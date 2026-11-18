using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace BannerlordTogetherCrashGuard
{
    internal static class ArmyInterfaceCompatibility
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static DateTime _nextContact;
        internal static void Install(Harmony harmony)
        {
            const string typeName = "TaleWorlds.CampaignSystem.ViewModelCollection.ArmyManagement.ArmyManagementVM";
            Type vm = AccessTools.TypeByName(typeName) ?? Type.GetType(typeName + ", TaleWorlds.CampaignSystem.ViewModelCollection", false);
            if (vm == null) throw new TypeLoadException("Army management view model");
            Patch(harmony, AccessTools.Method(vm, "ExecuteDone"), nameof(Done));
            Patch(harmony, AccessTools.Method(vm, "ExecuteDisbandArmy"), nameof(Disband));
            var create = AccessTools.Method(typeof(TaleWorlds.CampaignSystem.GameComponents.DefaultArmyManagementCalculationModel), "CanPlayerCreateArmy");
            if (Harmony.GetPatchInfo(create)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(create, postfix: new HarmonyMethod(typeof(ArmyInterfaceCompatibility), nameof(CanCreate)));
            Patch(harmony, AccessTools.Method(typeof(EncounterManager), "StartPartyEncounter"), nameof(Contact));
        }
        private static void Patch(Harmony harmony, System.Reflection.MethodInfo method, string prefix)
        {
            if (method == null) throw new MissingMethodException(prefix);
            if (Harmony.GetPatchInfo(method)?.Owners.Contains(SubModule.Owner) == true) return;
            harmony.Patch(method, new HarmonyMethod(typeof(ArmyInterfaceCompatibility), prefix) { priority = Priority.First });
        }
        private static void CanCreate(ref bool __result, ref TextObject disabledReason)
        {
            if (!CoopSession.IsActive || !CoopSession.IsSessionReady || Clan.PlayerClan == null || Clan.PlayerClan.Kingdom != null ||
                Clan.PlayerClan.IsUnderMercenaryService || !ClanArmyCompatibility.Available(MobileParty.MainParty) ||
                MobileParty.MainParty.CurrentSettlement != null || MobileParty.MainParty.Army != null ||
                TaleWorlds.MountAndBlade.Mission.Current != null) return;
            __result = true; disabledReason = TextObject.GetEmpty();
        }
        private static bool Done(object __instance)
        {
            if (!CoopSession.IsActive || (!CoopSession.IsClient && Clan.PlayerClan?.Kingdom != null)) return true;
            try
            {
                MobileParty leader = MobileParty.MainParty;
                if (!CoopSession.IsSessionReady || !ClanArmyCompatibility.Available(leader) ||
                    (leader.Army != null && leader.Army.LeaderParty != leader))
                { ClanArmyCompatibility.Info("Only an available army leader can manage clan parties."); return false; }
                var cart = (IEnumerable)AccessTools.Property(__instance.GetType(), "PartiesInCart").GetValue(__instance);
                var selected = cart.Cast<object>().Select(row => (MobileParty)AccessTools.Property(row.GetType(), "Party").GetValue(row))
                    .Where(party => party != leader && !ClanArmyCompatibility.Human(party)).ToArray();
                string[] ids = selected.Select(party => party.StringId).Distinct().ToArray();
                if (CoopSession.IsClient)
                {
                    if (!CoopCompatibilityBehavior.HasHostState) { ClanArmyCompatibility.Info("Wait for host synchronization."); return false; }
                    CoopCompatibilityBehavior.Instance.PublishChanges(new Dictionary<string, string> { ["army/manage"] = Json.Serialize(ids) });
                    ClanArmyCompatibility.Info("Army selection sent to host for confirmation.");
                }
                else Apply(leader, ids);
                ((Action)AccessTools.Field(__instance.GetType(), "_onClose").GetValue(__instance))();
                CampaignEventDispatcher.Instance.OnArmyOverlaySetDirty();
            }
            catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error.GetBaseException()))
            { BattleLog.Write("[CLAN-ARMY] native selection failed " + error); ClanArmyCompatibility.Info("Army selection failed; see battle.log."); }
            return false;
        }
        internal static void Accept(int peer, string json)
        {
            string message;
            try
            {
                MobileParty leader = CoopPeerIdentity.HeroForPeer(peer)?.PartyBelongedTo;
                if (!ClanArmyCompatibility.Human(leader)) throw new InvalidOperationException("Player identity not ready.");
                string[] ids = Json.Deserialize<string[]>(json);
                Apply(leader, ids);
                message = "Host confirmed clan army selection (" + ids.Length + " NPC parties).";
            }
            catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error.GetBaseException()))
            { message = "Army selection rejected: " + error.GetBaseException().Message; BattleLog.Write("[CLAN-ARMY] " + message); }
            CoopCompatibilityBehavior.Instance.SendState(peer, new Dictionary<string, string> {
                ["army/result"] = Guid.NewGuid().ToString("N") + "|" + message });
        }
        private static void Apply(MobileParty leader, string[] ids)
        {
            if (!CoopSession.IsHost || !CoopSession.IsSessionReady || !ClanArmyCompatibility.Available(leader) ||
                leader.CurrentSettlement != null || (leader.Army != null && leader.Army.LeaderParty != leader))
                throw new InvalidOperationException("Leader is busy or does not lead this army.");
            if (!ValidSelection(ids)) throw new InvalidOperationException("Invalid party selection.");
            Clan owner = ClanArmyCompatibility.OwnerClan(leader);
            if (owner == null || owner.IsUnderMercenaryService) throw new InvalidOperationException("Clan cannot call armies.");
            var byId = Campaign.Current.MobileParties.GroupBy(party => party.StringId)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var parties = ids.Select(id => byId.TryGetValue(id, out MobileParty party) ? party : null).ToArray();
            if (parties.Any(party => !ClanArmyCompatibility.Available(party) || ClanArmyCompatibility.Human(party) ||
                !party.IsLordParty || party.LeaderHero.Clan != owner || party.CurrentSettlement?.SiegeEvent != null ||
                (party.Army != null && party.Army != leader.Army)))
                throw new InvalidOperationException("A selected clan party is unavailable or belongs to another army/clan.");
            // Validate the complete selection before changing any membership. Humans remain voluntary members.
            foreach (MobileParty party in parties) ClanArmyCompatibility.Call(leader, party, false);
            if (leader.Army != null)
                foreach (MobileParty party in leader.Army.Parties.Where(party => party != leader && !ClanArmyCompatibility.Human(party) &&
                    party.LeaderHero?.Clan == owner && !ids.Contains(party.StringId)).ToArray()) party.Army = null;
            ClanArmyCompatibility.Dirty();
            BattleLog.Write("[CLAN-ARMY] committed native selection leader=" + leader.StringId + " count=" + ids.Length);
        }
        internal static bool ValidSelection(string[] ids) => ids != null && ids.Length <= 512 &&
            ids.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 128) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;
        internal static void ApplyResult(Dictionary<string, string> values)
        {
            if (values.TryGetValue("army/result", out string result))
                ClanArmyCompatibility.Info(result.Substring(result.IndexOf('|') + 1));
        }
        private static bool Disband()
        {
            if (!CoopSession.IsClient || !CoopSession.IsActive) return true;
            Army army = MobileParty.MainParty?.Army;
            if (army?.LeaderParty == MobileParty.MainParty)
                CoopCompatibilityBehavior.Instance?.PublishChanges(new Dictionary<string, string> { ["army/disband"] = "1" });
            else if (army != null) ClanArmyCompatibility.Intent(army.LeaderParty, PlayerArmyActionKind.Leave);
            return false;
        }
        private static bool Contact(PartyBase attackerParty, PartyBase defenderParty)
        {
            if (!CoopSession.IsClient || !CoopSession.IsSessionReady || !CoopCompatibilityBehavior.HasHostState ||
                TaleWorlds.MountAndBlade.Mission.Current != null) return true;
            MobileParty local = MobileParty.MainParty;
            MobileParty target = attackerParty?.MobileParty == local ? defenderParty?.MobileParty :
                defenderParty?.MobileParty == local ? attackerParty?.MobileParty : null;
            MobileParty leader = target?.Army?.LeaderParty;
            if (!ClanArmyCompatibility.Human(leader) || leader == local || !ClanArmyCompatibility.Available(leader) ||
                !ClanArmyCompatibility.Available(local) || (local.Army != null && local.Army != leader.Army) ||
                local.MapFaction == null || leader.MapFaction == null || FactionManager.IsAtWarAgainstFaction(local.MapFaction, leader.MapFaction)) return true;
            if (local.Army == leader.Army && local.AttachedTo == leader) return false;
            if (DateTime.UtcNow < _nextContact) return false;
            _nextContact = DateTime.UtcNow.AddSeconds(0.75);
            // Contact already occurs at native encounter distance. Reuse authenticated membership intent.
            ClanArmyCompatibility.Intent(leader, PlayerArmyActionKind.Join);
            BattleLog.Write("[CLAN-ARMY] map contact requested player join leader=" + leader.StringId);
            return false;
        }
    }
}
