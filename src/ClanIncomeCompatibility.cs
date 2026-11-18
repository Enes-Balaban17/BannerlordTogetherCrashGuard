using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.Localization;

namespace BannerlordTogetherCrashGuard
{
    internal static class ClanIncomeCompatibility
    {
        internal static float HostIncome;
        internal static float HostGarrisonExpense;
        internal static int PlayerCount = 1;
        internal static int ShareIndex;
        [ThreadStatic] private static bool _rawPreview;
        private static bool Pooling => CoopSession.IsHost && PlayerCount > 1 && SharedMode();
        private sealed class IncomeCall
        {
            internal float Before;
            internal bool ClientHandled;
        }
        [ThreadStatic] private static ExpenseCall _expense;
        private sealed class ExpenseCall
        {
            internal ExpenseCall Previous;
            internal float Garrison;
        }

        internal static bool SharedMode()
        {
            object behavior = ClanModeSyncBehavior.Instance;
            Type helper = AccessTools.TypeByName("BannerlordTogether.Patches.CoopFinanceHelper");
            if (behavior == null || helper == null)
                return false;
            object mode = AccessTools.Property(behavior.GetType(), "CurrentMode").GetValue(behavior);
            return (bool)AccessTools.Method(helper, "ShouldApplyCoopEconomySplit").Invoke(null, new[] { mode });
        }

        private static bool Enabled => !_rawPreview && CoopSession.IsActive && PlayerCount > 1 && SharedMode() &&
            (CoopSession.IsHost || CoopCompatibilityBehavior.HasHostState);

        internal static void Install(Harmony harmony)
        {
            Type existing = AccessTools.TypeByName("BannerlordTogether.Patches.CoopFinancePatches");
            var originalPostfix = existing == null ? null : AccessTools.Method(existing, "Postfix");
            if (originalPostfix != null && Harmony.GetPatchInfo(originalPostfix)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(originalPostfix, new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(OriginalSplitPrefix)));
            var calculation = AccessTools.Method(typeof(DefaultClanFinanceModel), "CalculateClanIncomeInternal");
            if (Harmony.GetPatchInfo(calculation)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(calculation, new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(IncomePrefix)),
                    new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(IncomePostfix)));
            var expense = AccessTools.Method(typeof(DefaultClanFinanceModel), "AddPartyExpense");
            if (Harmony.GetPatchInfo(expense)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(expense, new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(GarrisonExpensePrefix)),
                    new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(GarrisonExpensePostfix)));
            var expenses = AccessTools.Method(typeof(DefaultClanFinanceModel), "CalculateClanExpensesInternal");
            if (Harmony.GetPatchInfo(expenses)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(expenses, new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(ExpensesPrefix)),
                    new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(ExpensesPostfix)),
                    finalizer: new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(ExpensesFinalizer)));
            var daily = AccessTools.Method(typeof(ClanVariablesCampaignBehavior), "DailyTickClan");
            if (Harmony.GetPatchInfo(daily)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(daily, transpiler: new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(RouteDailyPayment)));
            var partyIncome = AccessTools.Method(typeof(DefaultClanFinanceModel), "AddIncomeFromParty");
            if (Harmony.GetPatchInfo(partyIncome)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(partyIncome, new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(PooledCaravanIncomePrefix)));
            var fallback = AccessTools.Method(typeof(ClientCaravanCreationBehavior), "HasDailyFinanceDeltaSent");
            if (Harmony.GetPatchInfo(fallback)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(fallback, new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(CaravanFallbackPrefix)));
        }

        private static bool OriginalSplitPrefix() => !Enabled;

        internal static int GrossShare(float total, int count, int index)
        {
            if (float.IsNaN(total) || float.IsInfinity(total) || count < 1 || index < 0 || index >= count)
                throw new ArgumentOutOfRangeException();
            int rounded = checked((int)Math.Round(total, MidpointRounding.AwayFromZero));
            int remainder = rounded % count;
            return rounded / count + (index < Math.Abs(remainder) ? Math.Sign(remainder) : 0);
        }

        internal static void RefreshPreview()
        {
            if (Clan.PlayerClan == null || Campaign.Current == null)
                return;
            try
            {
                _rawPreview = true;
                SharedAssetIncome.Prepare();
                HostIncome = Campaign.Current.Models.ClanFinanceModel.CalculateClanIncome(Clan.PlayerClan, false, false, false).ResultNumber;
                HostIncome += SharedAssetIncome.CollectAdditional(false);
                HostGarrisonExpense = 0;
                object model = Campaign.Current.Models.ClanFinanceModel;
                MethodInfo expense = AccessTools.Method(typeof(DefaultClanFinanceModel), "AddPartyExpense");
                foreach (var fief in Clan.PlayerClan.Fiefs)
                    if (fief.GarrisonParty?.IsActive == true)
                    {
                        object[] args = { fief.GarrisonParty, Clan.PlayerClan, new ExplainedNumber(HostIncome), false };
                        HostGarrisonExpense += Math.Max(0, -(int)expense.Invoke(model, args));
                    }
            }
            finally { _rawPreview = false; }
        }

        private static bool IncomePrefix(Clan clan, ref ExplainedNumber goldChange, bool applyWithdrawals, out IncomeCall __state)
        {
            __state = null;
            if (!Enabled || clan != Clan.PlayerClan)
                return true;
            __state = new IncomeCall { Before = goldChange.ResultNumber, ClientHandled = CoopSession.IsClient };
            if (!__state.ClientHandled)
            {
                SharedAssetIncome.Prepare();
                return true;
            }
            goldChange.Add(GrossShare(HostIncome, PlayerCount, Math.Min(ShareIndex, PlayerCount - 1)),
                new TextObject("Crash Guard shared clan income (" + PlayerCount + " players)"));
            if (applyWithdrawals)
                BattleLog.Write("[CLAN-INCOME] client gross=" + HostIncome + " share=" +
                    GrossShare(HostIncome, PlayerCount, Math.Min(ShareIndex, PlayerCount - 1)) + " players=" + PlayerCount);
            return false;
        }

        private static void IncomePostfix(Clan clan, ref ExplainedNumber goldChange, bool applyWithdrawals, IncomeCall __state)
        {
            if (__state == null || __state.ClientHandled)
                return;
            float extra = SharedAssetIncome.CollectAdditional(applyWithdrawals);
            goldChange.Add(extra, new TextObject("Co-op player workshop and caravan income"));
            float actualIncome = goldChange.ResultNumber - __state.Before;
            HostIncome = actualIncome;
            int share = GrossShare(actualIncome, PlayerCount, 0);
            goldChange.Add(share - actualIncome, new TextObject("Crash Guard income distributed to co-op players"));
            if (applyWithdrawals)
            {
                BattleLog.Write("[CLAN-INCOME] daily gross=" + actualIncome + " hostShare=" + share +
                    " players=" + PlayerCount + " paidTo=" + clan.Leader?.StringId + " localHero=" + Hero.MainHero?.StringId);
                CoopCompatibilityBehavior.Instance?.PublishChanges(new System.Collections.Generic.Dictionary<string, string>
                    { ["income"] = actualIncome.ToString("R", System.Globalization.CultureInfo.InvariantCulture) });
            }
        }

        private static void GarrisonExpensePostfix(TaleWorlds.CampaignSystem.Party.MobileParty party, Clan clan, ref int __result)
        {
            if (!_rawPreview && _expense != null && clan == Clan.PlayerClan && party?.IsGarrison == true)
            {
                _expense.Garrison += Math.Max(0, -__result);
                __result = 0;
            }
        }

        private static bool GarrisonExpensePrefix(TaleWorlds.CampaignSystem.Party.MobileParty party, Clan clan, ref int __result)
        {
            if (_expense == null || !CoopSession.IsClient || clan != Clan.PlayerClan || party?.IsGarrison != true)
                return true;
            __result = 0;
            return false;
        }

        private static void ExpensesPrefix(Clan clan, out ExpenseCall __state)
        {
            __state = null;
            if (Enabled && clan == Clan.PlayerClan)
                _expense = __state = new ExpenseCall { Previous = _expense };
        }

        private static void ExpensesPostfix(ref ExplainedNumber goldChange, bool applyWithdrawals, ExpenseCall __state)
        {
            if (__state == null)
                return;
            if (CoopSession.IsHost)
                HostGarrisonExpense = __state.Garrison;
            int share = GrossShare(HostGarrisonExpense, PlayerCount, CoopSession.IsHost ? 0 : Math.Min(ShareIndex, PlayerCount - 1));
            goldChange.Add(-share, new TextObject("Crash Guard shared garrison expenses (" + PlayerCount + " players)"));
            if (applyWithdrawals && CoopSession.IsHost)
                CoopCompatibilityBehavior.Instance?.PublishChanges(new Dictionary<string, string>
                    { ["garrison-expense"] = HostGarrisonExpense.ToString("R", System.Globalization.CultureInfo.InvariantCulture) });
        }

        private static void ExpensesFinalizer(ExpenseCall __state)
        {
            if (__state != null)
                _expense = __state.Previous;
        }

        private static bool PooledCaravanIncomePrefix(TaleWorlds.CampaignSystem.Party.MobileParty party, ref int __result)
        {
            if (!Pooling || !SharedAssetIncome.Contains(party))
                return true;
            __result = 0;
            return false;
        }

        private static bool CaravanFallbackPrefix(ulong syncId, string kind, ref bool __result)
        {
            if (!Pooling || kind != "income" || !SharedAssetIncome.Contains(syncId))
                return true;
            __result = true;
            return false;
        }

        private static IEnumerable<CodeInstruction> RouteDailyPayment(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo original = AccessTools.Method(typeof(GiveGoldAction), "ApplyBetweenCharacters",
                new[] { typeof(Hero), typeof(Hero), typeof(int), typeof(bool) });
            MethodInfo replacement = AccessTools.Method(typeof(ClanIncomeCompatibility), nameof(PayDailyGold));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(original))
                {
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1)
                throw new InvalidOperationException("Expected one daily clan payment; found " + replaced);
        }

        private static void PayDailyGold(Hero giverHero, Hero recipientHero, int amount, bool disableNotification)
        {
            if (Enabled && CoopSession.IsClient && giverHero == null && Hero.MainHero?.Clan == Clan.PlayerClan &&
                recipientHero == Clan.PlayerClan?.Leader)
                recipientHero = Hero.MainHero;
            GiveGoldAction.ApplyBetweenCharacters(giverHero, recipientHero, amount, disableNotification);
            if (Enabled && recipientHero == Hero.MainHero)
                BattleLog.Write("[CLAN-INCOME] local daily net=" + amount + " hero=" + recipientHero.StringId);
        }
    }
}
