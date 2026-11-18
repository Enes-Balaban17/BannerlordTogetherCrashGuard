using System;
using System.Linq;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.LogEntries;

namespace BannerlordTogetherCrashGuard
{
    internal static class ClanLifecycleCompatibility
    {
        internal static bool IsProjection(string id) => !string.IsNullOrEmpty(id) && id.StartsWith("bt_remote_", StringComparison.Ordinal);

        internal static void Install(Harmony harmony)
        {
            var destroy = AccessTools.Method(typeof(DestroyClanAction), "ApplyInternal");
            if (Harmony.GetPatchInfo(destroy)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(destroy, new HarmonyMethod(typeof(ClanLifecycleCompatibility), nameof(BeforeDestroy)));
            var visible = AccessTools.PropertyGetter(typeof(ClanDestroyedLogEntry), "IsVisibleNotification");
            if (Harmony.GetPatchInfo(visible)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(visible, postfix: new HarmonyMethod(typeof(ClanLifecycleCompatibility), nameof(NotificationPostfix)));
        }

        private static bool Protected(Clan clan)
        {
            if (clan == null)
                return false;
            if (IsProjection(clan.StringId))
                return true;
            if (Campaign.Current == null)
                return false;
            if (clan == Clan.PlayerClan && Hero.MainHero?.IsAlive == true)
                return true;
            var behavior = CoopCompatibilityBehavior.Instance;
            return CoopSession.IsHost && behavior != null && behavior.CompatiblePeers().Any(peer =>
            {
                Hero hero = CoopPeerIdentity.HeroForPeer(peer);
                return hero?.IsAlive == true && hero.Clan == clan;
            });
        }

        private static bool BeforeDestroy(Clan destroyedClan)
        {
            if (!CoopSession.IsActive || RaidUpdatePatch.DiagnosticsOnly || !Protected(destroyedClan))
                return true;
            BattleLog.Write("[CLAN-LIFECYCLE] prevented destruction of living player/projection clan=" + destroyedClan.StringId);
            return false;
        }

        private static void NotificationPostfix(Clan ____clan, ref bool __result)
        {
            if (CoopSession.IsActive && !RaidUpdatePatch.DiagnosticsOnly && Protected(____clan))
                __result = false;
        }
    }
}
