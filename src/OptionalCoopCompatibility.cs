using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordTogetherCrashGuard
{
    internal static class OptionalCoopCompatibility
    {
        internal static void Apply(Harmony harmony)
        {
            Hook(harmony, "XorberaxLegacy.Logic.BanksofCalradiaCampaignBehavior", "Withdraw", null, nameof(BankTransactionPostfix));
            Hook(harmony, "XorberaxLegacy.Logic.BanksofCalradiaCampaignBehavior", "CloseBankAccountAtSettlement", null, nameof(BankTransactionPostfix));
            Hook(harmony, "XorberaxLegacy.Logic.BanksofCalradiaCampaignBehavior", "UpdateBankData", nameof(BankTickPrefix), null);
            Hook(harmony, "OffTheClock.CampaignTweaks.TweakClanTierModel", "GetCompanionLimit", null, nameof(CompanionLimitPostfix));
            Hook(harmony, "OffTheClock.CampaignTweaks.TweakWorkshopModel", "get_MaximumWorkshopsPlayerCanHave", null, nameof(WorkshopLimitPostfix));
            Hook(harmony, "TaleWorlds.CampaignSystem.GameComponents.DefaultClanTierModel", "GetCompanionLimit", null, nameof(CompanionLimitPostfix));
            Hook(harmony, "TaleWorlds.CampaignSystem.GameComponents.DefaultWorkshopModel", "get_MaximumWorkshopsPlayerCanHave", null, nameof(WorkshopLimitPostfix));
            Hook(harmony, "CharacterReload.Utils.HeroUtils", "UpdateHeroCharacterBodyProperties", null, nameof(HeroBodyPostfix));
            Hook(harmony, "CharacterReload.VM.HeroAdmin.HereAdminDashBoardVM", "ExecuteDone", null, nameof(HeroAdminSaved));
            Hook(harmony, "TaleWorlds.MountAndBlade.BodyGenerator", "SaveCurrentCharacter", null, nameof(BodyGeneratorSaved));
            Hook(harmony, "BannerlordTogether.GhostPartyController", "ApplyHeroData", null, nameof(GhostDataApplied));
            Hook(harmony, "ModularSmithing.Crafting.SmithingPersistenceBehavior", "Register", null, nameof(SmithingRegistered));
            Hook(harmony, "BannerlordTogether.PlayerTradeBehavior", "BeginSaveResync", null, nameof(SaveResyncStarted));
            Hook(harmony, "BannerlordTogether.CoopCampaignBehavior", "PrepareGameplayPeerForIsolatedResync", null, nameof(PeerResyncPrepared));
            Hook(harmony, "OffTheClock.CampaignTweaks.SubModule", "LoadSettings", null, nameof(SettingsChanged));
            Hook(harmony, "ModularSmithing.Crafting.MSmithConfig", "Save", null, nameof(SettingsChanged));
            Hook(harmony, "ModularSmithing.Crafting.MSmithConfig", "Reload", null, nameof(SettingsChanged));
            Hook(harmony, "ModularSmithing.Crafting.MSmithCraftCommands", "FreeCraft", null, nameof(SettingsChanged));
            Hook(harmony, "ModularSmithing.Crafting.MSmithCraftCommands", "EnableWeapons", null, nameof(SettingsChanged));
            Hook(harmony, "TaleWorlds.CampaignSystem.Settlements.Workshops.Workshop", "ChangeOwnerOfWorkshop", null, nameof(FinanceChanged));
            Hook(harmony, "TaleWorlds.CampaignSystem.Actions.EndCaptivityAction", "ApplyInternal", null, nameof(PrisonerReleased));
            Hook(harmony, "Diplomacy.CampaignBehaviors.CivilWarBehavior", "DailyTick", nameof(AuthorityPrefix), null);
            Hook(harmony, "Diplomacy.CampaignBehaviors.CivilWarBehavior", "DailyTickClan", nameof(AuthorityPrefix), null);
            Hook(harmony, "Diplomacy.CampaignBehaviors.DiplomaticAgreementBehavior", "UpdateDiplomaticAgreements", nameof(AuthorityPrefix), null);
            Hook(harmony, "Diplomacy.CampaignBehaviors.WarExhaustionBehavior", "OnDailyTick", nameof(AuthorityPrefix), null);
            Hook(harmony, "ImprovedGarrisons.SaveSystem.GarrisonDailyBehavior", "DailyBehavior", nameof(AuthorityPrefix), null);
            Hook(harmony, "ImprovedGarrisons.SaveSystem.GarrisonBehavior", "HourlyEvent", nameof(AuthorityPrefix), null);
            Hook(harmony, "ImprovedGarrisons.SaveSystem.GarrisonBehavior", "DailyEvent", nameof(AuthorityPrefix), null);
            foreach (string name in new[] { "PartyPartialHourlyAi", "PartyHourlyAi", "PartyDailyAi" })
                Hook(harmony, "ImprovedGarrisons.Behaviours.GarrisonPartyBehavior", name, nameof(AuthorityPrefix), null);
            Install("Diplomacy messengers", () => MessengerCompatibility.Install(harmony));
            Install("Diplomacy fief valuation", () => DiplomacySettlementCompatibility.Install(harmony));
            Install("Encyclopedia", () => GhostEncyclopediaCompatibility.Install(harmony));
            Install("Clan income", () => ClanIncomeCompatibility.Install(harmony));
            Install("Settings change events", () => HostSettingsCompatibility.InstallChangeHooks(harmony));
            Install("Clan lifecycle", () => ClanLifecycleCompatibility.Install(harmony));
            Install("Dismemberment", () => DismembermentCompatibility.Install(harmony));
            Install("Custom cultures and loadout", () => CultureCompatibility.Install(harmony));
            Install("Battle rewards", () => RewardShareCompatibility.Install(harmony));
            Hook(harmony, "TogetherLordsCore.PeerSync.PacketDispatchPostfix", "Postfix", nameof(IgnoreOurExtensionInLordsPrefix), null);
        }

        private static void Install(string feature, Action apply)
        {
            try { apply(); }
            catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
            { BattleLog.Write("[COOP-COMPAT] optional feature unavailable " + feature + " " + exception); }
        }

        internal static void Hook(Harmony harmony, string typeName, string methodName, string prefix, string postfix)
        {
            Type type = AccessTools.TypeByName(typeName);
            if (type == null)
                return;
            MethodInfo[] methods = AccessTools.GetDeclaredMethods(type).Where(method => method.Name == methodName).ToArray();
            if (methods.Length == 0)
            {
                BattleLog.Write("[COOP-COMPAT] optional method unavailable " + typeName + "." + methodName);
                return;
            }
            try
            {
                foreach (MethodInfo method in methods)
                {
                    if (Harmony.GetPatchInfo(method)?.Owners.Contains(SubModule.Owner) == true)
                        continue;
                    harmony.Patch(method,
                        prefix == null ? null : new HarmonyMethod(typeof(OptionalCoopCompatibility), prefix),
                        postfix == null ? null : new HarmonyMethod(typeof(OptionalCoopCompatibility), postfix));
                }
                BattleLog.Write("[COOP-COMPAT] optional hook active " + typeName + "." + methodName);
            }
            catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
            { BattleLog.Write("[COOP-COMPAT] optional hook failed " + typeName + "." + methodName + " " + exception.Message); }
        }

        private static bool AuthorityPrefix()
        {
            return !CoopSession.IsClient || !CoopCompatibilityBehavior.HasHostState;
        }

        private static bool IgnoreOurExtensionInLordsPrefix(byte[] data) => !CompatibilityWire.IsOurs(data);

        internal static bool SameHostClan()
        {
            return CoopSession.IsClient && CoopCompatibilityBehavior.HasHostState && Clan.PlayerClan != null &&
                ClanIncomeCompatibility.SharedMode();
        }

        private static void CompanionLimitPostfix(Clan clan, ref int __result)
        {
            if (clan == Clan.PlayerClan && SameHostClan())
                __result = CoopCompatibilityBehavior.HostCompanions;
        }

        private static void WorkshopLimitPostfix(ref int __result)
        {
            if (SameHostClan())
                __result = CoopCompatibilityBehavior.HostWorkshops;
        }

        private static void BankTransactionPostfix(object __instance, Settlement settlement)
        {
            if (!CoopSession.IsActive)
                return;
            var getBank = AccessTools.Method(__instance.GetType(), "GetBankDataAtSettlement");
            NormalizeEmptyBank(getBank.Invoke(__instance, new object[] { settlement }));
        }

        private static void BankTickPrefix(object __instance)
        {
            if (!CoopSession.IsActive)
                return;
            var banks = AccessTools.Field(__instance.GetType(), "_settlementBankDataBySettlementId").GetValue(__instance) as IDictionary;
            if (banks == null)
                return;
            foreach (object bank in banks.Values)
                NormalizeEmptyBank(bank);
        }

        internal static void NormalizeEmptyBank(object bank)
        {
            if (bank == null)
                return;
            Type type = bank.GetType();
            int balance = (int)AccessTools.Property(type, "Balance").GetValue(bank);
            if (balance > 0)
                return;
            PropertyInfo interest = AccessTools.Property(type, "InterestRate");
            if ((float)interest.GetValue(bank) == 0f)
                return;
            interest.SetValue(bank, 0f);
            BattleLog.Write("[BANK] cleared stale interest after account was emptied");
        }

        private static void HeroBodyPostfix(CharacterObject character)
        {
            if (!CoopCompatibilityBehavior.Applying && CoopSession.IsActive)
                AppearanceCompatibility.Publish(character?.HeroObject);
        }

        private static void BodyGeneratorSaved(object __instance)
        {
            if (CoopCompatibilityBehavior.Applying || !CoopSession.IsActive)
                return;
            object character = AccessTools.Property(__instance.GetType(), "Character")?.GetValue(__instance);
            AppearanceCompatibility.Publish((character as CharacterObject)?.HeroObject);
        }

        private static void HeroAdminSaved(object __instance)
        {
            if (!CoopCompatibilityBehavior.Applying && CoopSession.IsActive)
                AppearanceCompatibility.Publish(AccessTools.Field(__instance.GetType(), "_hero").GetValue(__instance) as Hero);
        }

        private static void GhostDataApplied(GhostPartyController __instance, PlayerHeroDataPacket __0, bool __1)
        {
            AppearanceCompatibility.TrackGhostData(__instance, __0, __1);
        }

        private static void SmithingRegistered(object __0)
        {
            SmithingCompatibility.PublishRecord(__0);
        }

        private static void SaveResyncStarted(bool __result)
        {
            if (__result)
                CoopCompatibilityBehavior.Instance?.RequestFullSync();
        }

        private static void PeerResyncPrepared(int peerId, bool __result)
        {
            if (__result)
                CoopCompatibilityBehavior.Instance?.RequestFullSync(peerId);
        }

        private static void SettingsChanged()
        {
            CoopCompatibilityBehavior.Instance?.MarkSettingsChanged();
        }

        private static void FinanceChanged()
        {
            CoopCompatibilityBehavior.Instance?.MarkFinanceChanged();
        }

        private static void PrisonerReleased(Hero __0)
        {
            RewardShareCompatibility.PrisonerReleased(__0);
        }
    }
}
