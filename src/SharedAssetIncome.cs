using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements.Workshops;

namespace BannerlordTogetherCrashGuard
{
    internal static class SharedAssetIncome
    {
        private static readonly Dictionary<string, MobileParty> Caravans = new Dictionary<string, MobileParty>(StringComparer.Ordinal);
        private static readonly HashSet<ulong> CaravanSyncIds = new HashSet<ulong>();
        private static readonly HashSet<Hero> Owners = new HashSet<Hero>();

        internal static void Reset()
        {
            Owners.Clear(); Caravans.Clear(); CaravanSyncIds.Clear();
        }

        internal static void Prepare()
        {
            Reset();
            if (!CoopSession.IsHost || Hero.MainHero == null)
                return;
            Owners.Add(Hero.MainHero);
            var behavior = CoopCompatibilityBehavior.Instance;
            if (behavior != null)
                foreach (int peer in behavior.CompatiblePeers())
                {
                    Hero hero = CoopPeerIdentity.HeroForPeer(peer);
                    if (hero != null)
                        Owners.Add(hero);
                }
            foreach (Hero owner in Owners)
                foreach (var caravan in owner.OwnedCaravans)
                {
                    MobileParty party = caravan.MobileParty;
                    if (party?.IsActive != true || !party.IsCaravan)
                        continue;
                    Caravans[party.StringId] = party;
                    object[] identity = { party, 0UL, null };
                    if ((bool)AccessTools.Method(typeof(WorldPartySyncBehavior), "TryGetWorldPartyIdentity").Invoke(null, identity))
                        CaravanSyncIds.Add((ulong)identity[1]);
                }
        }

        internal static bool Contains(MobileParty party) => party != null && Caravans.ContainsKey(party.StringId);
        internal static bool Contains(ulong syncId) => CaravanSyncIds.Contains(syncId);

        internal static int CaravanProfit(int tradeGold) => tradeGold > 10000 ? (tradeGold - 10000) / 10 : 0;

        internal static float CollectAdditional(bool withdraw)
        {
            if (Owners.Count == 0 || Campaign.Current == null)
                return 0;
            var model = Campaign.Current.Models.ClanFinanceModel;
            var ledger = new IncomeAssetLedger();
            // The base model already collects only the clan leader's workshops. Union all
            // connected human owners, retaining the base contribution exactly once.
            var seen = new HashSet<Workshop>(Clan.PlayerClan.Leader?.OwnedWorkshops ?? Enumerable.Empty<Workshop>());
            foreach (Hero owner in Owners)
                foreach (Workshop workshop in owner.OwnedWorkshops)
                {
                    if (workshop == null || !seen.Add(workshop))
                        continue;
                    string key = workshop.Settlement.StringId + "/" + Array.IndexOf(workshop.Settlement.Town.Workshops, workshop);
                    ledger.Add("workshop/" + key, take =>
                    {
                        int income = Math.Max(0, model.CalculateOwnerIncomeFromWorkshop(workshop));
                        if (take && income > 0)
                            workshop.ChangeGold(-income);
                        return income;
                    });
                }
            // Human-owned caravans are excluded from the native per-clan pass and from
            // BT's direct owner income forwarding, so every wallet is withdrawn once here.
            foreach (MobileParty caravan in Caravans.Values)
            {
                ledger.Add("caravan/" + caravan.StringId, take =>
                {
                    int income = CaravanProfit(caravan.PartyTradeGold);
                    if (take && income > 0)
                        caravan.PartyTradeGold -= income;
                    return income;
                });
            }
            int extra = ledger.Collect(withdraw);
            if (withdraw)
                BattleLog.Write("[CLAN-INCOME] pooled human owners=" + Owners.Count + " caravans=" + Caravans.Count + " additionalAssets=" + extra);
            return extra;
        }
    }
}
