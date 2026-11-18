using System;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.Localization;

namespace BannerlordTogetherCrashGuard
{
    internal static class ClanIncomeCompatibility
    {
        internal static float HostIncome;
        internal static int PlayerCount = 1;

        internal static bool SharedMode()
        {
            object behavior = ClanModeSyncBehavior.Instance;
            Type helper = AccessTools.TypeByName("BannerlordTogether.Patches.CoopFinanceHelper");
            if (behavior == null || helper == null)
                return false;
            object mode = AccessTools.Property(behavior.GetType(), "CurrentMode").GetValue(behavior);
            return (bool)AccessTools.Method(helper, "ShouldApplyCoopEconomySplit").Invoke(null, new[] { mode });
        }

        private static bool Enabled => CoopSession.IsActive && PlayerCount > 1 && SharedMode() &&
            (CoopSession.IsHost || CoopCompatibilityBehavior.HasHostState);

        internal static void Install(Harmony harmony)
        {
            Type existing = AccessTools.TypeByName("BannerlordTogether.Patches.CoopFinancePatches");
            var originalPostfix = existing == null ? null : AccessTools.Method(existing, "Postfix");
            if (originalPostfix != null && Harmony.GetPatchInfo(originalPostfix)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(originalPostfix, new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(OriginalSplitPrefix)));
            var calculation = AccessTools.Method(typeof(DefaultClanFinanceModel), "CalculateClanGoldChange");
            if (Harmony.GetPatchInfo(calculation)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(calculation, postfix: new HarmonyMethod(typeof(ClanIncomeCompatibility), nameof(ShareIncome)));
        }

        private static bool OriginalSplitPrefix() => !Enabled;

        private static void ShareIncome(Clan clan, ref ExplainedNumber __result)
        {
            if (!Enabled || clan != Clan.PlayerClan)
                return;
            float localIncome = Campaign.Current.Models.ClanFinanceModel.CalculateClanIncome(clan, false, false, false).ResultNumber;
            float total = CoopSession.IsHost ? localIncome : HostIncome;
            __result.Add(total / PlayerCount - localIncome,
                new TextObject("Crash Guard shared clan income (" + PlayerCount + " players)"));
        }
    }
}
