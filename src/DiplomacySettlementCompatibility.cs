using System;
using System.Linq;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordTogetherCrashGuard
{
    internal static class DiplomacySettlementCompatibility
    {
        private static bool _reported;

        internal static void Install(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(DefaultSettlementValueModel), "GeographicalAdvantageForFaction"),
                nameof(GeographyPrefix), null);
            Type type = AccessTools.TypeByName("Diplomacy.CampaignBehaviors.MaintainInfluenceBehavior");
            if (type == null)
                return;
            Patch(harmony, AccessTools.Method(type, "GetGoldValueForFief"), nameof(ValuationPrefix), null);
            Patch(harmony, AccessTools.Method(type, "GetBestFiefBarter"), null, nameof(BarterPostfix));
            Patch(harmony, AccessTools.Method(type, "ReduceCorruption"), nameof(ReduceCorruptionPrefix), null);
        }

        private static void Patch(Harmony harmony, System.Reflection.MethodInfo method, string prefix, string postfix)
        {
            if (method != null && Harmony.GetPatchInfo(method)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(method, prefix == null ? null : new HarmonyMethod(typeof(DiplomacySettlementCompatibility), prefix),
                    postfix == null ? null : new HarmonyMethod(typeof(DiplomacySettlementCompatibility), postfix));
        }

        private static bool GeographyPrefix(Settlement settlement, IFaction faction, ref float __result)
        {
            if (!CoopSession.IsActive || RaidUpdatePatch.DiagnosticsOnly ||
                (settlement != null && faction?.FactionMidSettlement != null))
                return true;
            // Geography contributes a multiplier. Missing faction geography has no bonus,
            // rather than dereferencing a missing midpoint or inventing a new settlement.
            __result = 1f;
            if (!_reported)
            {
                _reported = true;
                BattleLog.Write("[DIPLOMACY-FIEF] missing faction midpoint; used neutral geography multiplier");
            }
            return false;
        }

        private static bool ValidBuyer(Clan clan)
        {
            return clan != null && !clan.IsEliminated && clan.Leader != null &&
                clan.Culture != null && clan.FactionMidSettlement != null;
        }

        private static bool ValuationPrefix(Clan buyerClan, Settlement settlement, ref int __result)
        {
            if (!CoopSession.IsActive || RaidUpdatePatch.DiagnosticsOnly ||
                (ValidBuyer(buyerClan) && settlement != null && (!settlement.IsVillage || settlement.Village.Bound != null)))
                return true;
            // Sort unavailable buyers last. BarterPostfix also rejects an unavailable winner,
            // so this sentinel can never turn a missing valuation into a free fief transfer.
            __result = -1;
            return false;
        }

        private static void BarterPostfix(ref Clan otherClan, ref FiefBarterable __result)
        {
            if (CoopSession.IsActive && !RaidUpdatePatch.DiagnosticsOnly && !ValidBuyer(otherClan))
            {
                otherClan = null;
                __result = null;
            }
        }

        private static bool ReduceCorruptionPrefix(Clan clan)
        {
            if (!CoopSession.IsActive || RaidUpdatePatch.DiagnosticsOnly)
                return true;
            return !(CoopSession.IsClient && CoopCompatibilityBehavior.HasHostState) &&
                clan?.Leader != null && clan.MapFaction != null;
        }
    }
}
