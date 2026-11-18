using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTogether;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using BannerlordTogetherCrashGuard.CompanionEquipment;
using GearCategory = BannerlordTogetherCrashGuard.CompanionEquipment.ItemCategory;

namespace BannerlordTogetherCrashGuard
{
    internal sealed class CompanionEquipmentBehavior : CampaignBehaviorBase
    {
        internal static CompanionEquipmentBehavior Instance;
        private sealed class Candidate
        {
            internal EquipmentElement Element;
            internal bool Market;
        }
        private sealed class Job
        {
            internal Hero Hero;
            internal MobileParty Party;
            internal Settlement Town;
            internal Candidate[] Candidates;
            internal int Slot;
            internal int Scan;
            internal Candidate Best;
            internal ItemView BestView;
            internal int Changed;
            internal int Budget;
            internal int Purchases;
        }
        private readonly Queue<Hero> _pending = new Queue<Hero>();
        private readonly HashSet<Hero> _queued = new HashSet<Hero>();
        private readonly Dictionary<string, int> _attempts = new Dictionary<string, int>();
        private Job _job;
        internal bool Enabled = true;

        public CompanionEquipmentBehavior() { Instance = this; }
        public override void SyncData(IDataStore store)
        {
            store.SyncData("btcg_companion_equipment_enabled", ref Enabled);
            string attempts = store.IsSaving ? string.Join("\n", _attempts.Select(pair => pair.Key + "|" + pair.Value)) : "";
            store.SyncData("btcg_companion_equipment_attempts", ref attempts);
            if (store.IsLoading)
            {
                _attempts.Clear();
                foreach (string row in (attempts ?? "").Split('\n'))
                {
                    string[] parts = row.Split('|');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int day))
                        _attempts[parts[0]] = day;
                }
            }
        }
        public override void RegisterEvents()
        {
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, (party, settlement, hero) => Enqueue(party?.LeaderHero));
            CampaignEvents.DailyTickHeroEvent.AddNonSerializedListener(this, Enqueue);
        }

        internal static int PurchaseBudget(int gold) => Math.Max(0, Math.Min(20000, Math.Min(gold / 10, gold - 1000)));

        private static bool Eligible(Hero hero)
        {
            if (!CoopSession.IsGameplayHost || !CoopSession.IsSessionReady || hero == null || hero == Hero.MainHero ||
                hero.IsDead || hero.IsPrisoner || hero.IsChild || !hero.IsWanderer || hero.CompanionOf == null)
                return false;
            MobileParty party = hero.PartyBelongedTo;
            if (party == null || party == MobileParty.MainParty || party.LeaderHero != hero ||
                CoopSession.GhostPartyIds.Contains(party.StringId) || party.MapEvent != null)
                return false;
            if (hero.Clan == Clan.PlayerClan)
                return true;
            var compat = CoopCompatibilityBehavior.Instance;
            return compat != null && compat.CompatiblePeers().Any(peer => CoopPeerIdentity.HeroForPeer(peer)?.Clan == hero.Clan);
        }

        private void Enqueue(Hero hero)
        {
            if (!Enabled || !Eligible(hero) || _queued.Contains(hero) || _pending.Count >= 64)
                return;
            int day = (int)CampaignTime.Now.ToDays;
            if (_attempts.TryGetValue(hero.StringId, out int previous) && previous == day)
                return;
            _attempts[hero.StringId] = day;
            _queued.Add(hero);
            _pending.Enqueue(hero);
        }

        internal void Tick()
        {
            if (!Enabled || !CoopSession.IsGameplayHost || !CoopSession.IsSessionReady || CoopSession.IsPaused ||
                CoopCompatibilityBehavior.Applying || TaleWorlds.MountAndBlade.Mission.Current != null ||
                TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.Current != null ||
                !(Game.Current?.GameStateManager.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState))
                return;
            try { Step(); }
            catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error))
            {
                BattleLog.Write("[COMPANION-EQUIP] failed " + error);
                Finish();
            }
        }

        private void Step()
        {
            if (_job == null)
            {
                if (_pending.Count == 0)
                    return;
                Hero hero = _pending.Dequeue();
                _queued.Remove(hero);
                if (!Eligible(hero))
                    return;
                var party = hero.PartyBelongedTo;
                Settlement town = party.CurrentSettlement?.IsTown == true ? party.CurrentSettlement : null;
                var candidates = party.ItemRoster.Where(row => row.Amount > 0 && row.EquipmentElement.Item != null)
                    .Select(row => new Candidate { Element = row.EquipmentElement }).ToList();
                if (town != null && PurchaseBudget(hero.Gold) > 0)
                    candidates.AddRange(town.ItemRoster.Where(row => row.Amount > 0 && row.EquipmentElement.Item != null)
                        .Select(row => new Candidate { Element = row.EquipmentElement, Market = true }));
                _job = new Job { Hero = hero, Party = party, Town = town, Candidates = candidates.ToArray(), Budget = PurchaseBudget(hero.Gold) };
            }
            Job job = _job;
            if (!Eligible(job.Hero) || job.Hero.PartyBelongedTo != job.Party || job.Slot >= 12)
            {
                Finish();
                return;
            }
            if (job.Slot == (int)EquipmentIndex.ExtraWeaponSlot)
            {
                job.Slot++;
                job.Scan = 0;
                return;
            }
            var current = job.Hero.BattleEquipment[(EquipmentIndex)job.Slot];
            if (job.Scan == 0)
            {
                job.Best = null;
                job.BestView = View(job.Hero, current);
            }
            int stop = Math.Min(job.Candidates.Length, job.Scan + 32);
            for (; job.Scan < stop; job.Scan++)
            {
                Candidate candidate = job.Candidates[job.Scan];
                if (!Fits(job.Hero, job.Slot, current.Item, candidate.Element.Item))
                    continue;
                if (candidate.Market && (job.Town == null || job.Party.CurrentSettlement != job.Town || job.Purchases >= 2 ||
                    job.Town.Town.GetItemPrice(candidate.Element, job.Party, false) > job.Budget))
                    continue;
                ItemView view = View(job.Hero, candidate.Element);
                if (job.BestView == null || ItemRanking.IsBetter(view, job.BestView))
                {
                    job.Best = candidate;
                    job.BestView = view;
                }
            }
            if (job.Scan < job.Candidates.Length)
                return;
            if (job.Best != null)
                Swap(job, current, job.Best);
            job.Slot++;
            job.Scan = 0;
        }

        private static ItemView View(Hero hero, EquipmentElement element)
        {
            ItemObject item = element.Item;
            if (item == null)
                return null;
            float culture = item.Culture != null && item.Culture == hero.Culture ? 1.03f : 1f;
            float modifier = Math.Max(0.1f, element.ItemModifier?.PriceMultiplier ?? 1f);
            return new ItemView(item.StringId, GearCategory.Other, item.Effectiveness * culture * (float)Math.Sqrt(modifier),
                (int)item.Tier, item.RelevantSkill?.StringId, item.Difficulty);
        }

        private static bool Fits(Hero hero, int slot, ItemObject worn, ItemObject candidate)
        {
            if (candidate == null || (candidate.RelevantSkill != null && hero.GetSkillValue(candidate.RelevantSkill) < candidate.Difficulty))
                return false;
            if ((hero.IsFemale && candidate.ItemFlags.HasFlag(ItemFlags.NotUsableByFemale)) ||
                (!hero.IsFemale && candidate.ItemFlags.HasFlag(ItemFlags.NotUsableByMale)))
                return false;
            // Preserve weapon/ammunition and mount families. This upgrades an existing
            // loadout rather than converting an archer into an unusable mixed loadout.
            if (slot == 10)
                return worn?.HorseComponent != null && candidate.HorseComponent != null &&
                    candidate.HorseComponent.Monster.FamilyType == worn.HorseComponent.Monster.FamilyType;
            if (slot == 11)
                return worn?.ArmorComponent != null && candidate.ArmorComponent != null &&
                    candidate.ArmorComponent.FamilyType == worn.ArmorComponent.FamilyType;
            if (slot < 5)
                return worn != null && candidate.ItemType == worn.ItemType &&
                    candidate.PrimaryWeapon?.WeaponClass == worn.PrimaryWeapon?.WeaponClass;
            switch ((EquipmentIndex)slot)
            {
                case EquipmentIndex.Head: return candidate.ItemType == ItemObject.ItemTypeEnum.HeadArmor;
                case EquipmentIndex.Body: return candidate.ItemType == ItemObject.ItemTypeEnum.BodyArmor || candidate.ItemType == ItemObject.ItemTypeEnum.ChestArmor;
                case EquipmentIndex.Leg: return candidate.ItemType == ItemObject.ItemTypeEnum.LegArmor;
                case EquipmentIndex.Gloves: return candidate.ItemType == ItemObject.ItemTypeEnum.HandArmor;
                case EquipmentIndex.Cape: return candidate.ItemType == ItemObject.ItemTypeEnum.Cape;
                default: return false;
            }
        }

        private static void Swap(Job job, EquipmentElement old, Candidate candidate)
        {
            ItemRoster source = candidate.Market ? job.Town.ItemRoster : job.Party.ItemRoster;
            int stock = source.FindIndexOfElement(candidate.Element);
            if (stock < 0 || source.GetElementNumber(stock) <= 0 || old.IsEqualTo(candidate.Element))
                return;
            int price = candidate.Market ? job.Town.Town.GetItemPrice(candidate.Element, job.Party, false) : 0;
            if (price < 0 || price > job.Budget || price > Math.Max(0, job.Hero.Gold - 1000))
                return;
            bool removed = false, returned = false;
            int oldGold = job.Hero.Gold;
            int oldMerchantGold = candidate.Market ? job.Town.SettlementComponent.Gold : 0;
            try
            {
                source.AddToCounts(candidate.Element, -1);
                removed = true;
                if (old.Item != null)
                {
                    job.Party.ItemRoster.AddToCounts(old, 1);
                    returned = true;
                }
                job.Hero.BattleEquipment.AddEquipmentToSlotWithoutAgent((EquipmentIndex)job.Slot, candidate.Element);
                if (price > 0)
                    GiveGoldAction.ApplyForCharacterToSettlement(job.Hero, job.Town, price, true);
            }
            catch
            {
                if (price > 0)
                {
                    job.Hero.ChangeHeroGold(oldGold - job.Hero.Gold);
                    job.Town.SettlementComponent.ChangeGold(oldMerchantGold - job.Town.SettlementComponent.Gold);
                }
                job.Hero.BattleEquipment.AddEquipmentToSlotWithoutAgent((EquipmentIndex)job.Slot, old);
                if (returned) job.Party.ItemRoster.AddToCounts(old, -1);
                if (removed) source.AddToCounts(candidate.Element, 1);
                throw;
            }
            job.Budget -= price;
            if (candidate.Market) job.Purchases++;
            job.Changed++;
            job.Party.Party.SetVisualAsDirty();
            BattleLog.Write("[COMPANION-EQUIP] hero=" + job.Hero.StringId + " slot=" + job.Slot +
                " item=" + candidate.Element.Item.StringId + " paid=" + price);
        }

        private void Finish()
        {
            Job job = _job;
            _job = null;
            if (job != null && job.Changed > 0)
                try { AppearanceCompatibility.Publish(job.Hero); }
                catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error))
                { BattleLog.Write("[COMPANION-EQUIP] appearance publish failed " + error.Message); }
        }

        internal void SetEnabled(bool enabled)
        {
            if (!enabled)
            {
                Finish();
                _pending.Clear();
                _queued.Clear();
            }
            Enabled = enabled;
        }
    }

    public static class CompanionEquipmentCommands
    {
        [TaleWorlds.Library.CommandLineFunctionality.CommandLineArgumentFunction("companion_equipment", "btcg")]
        public static string SetAutomation(List<string> arguments)
        {
            if (!CoopSession.IsHost || CompanionEquipmentBehavior.Instance == null)
                return "Only the co-op host can change companion equipment automation.";
            if (arguments == null || arguments.Count != 1 || (arguments[0] != "on" && arguments[0] != "off"))
                return "Usage: btcg.companion_equipment on|off";
            CompanionEquipmentBehavior.Instance.SetEnabled(arguments[0] == "on");
            return "Companion equipment automation: " + arguments[0];
        }
    }
}
