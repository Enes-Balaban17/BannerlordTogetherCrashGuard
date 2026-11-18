using System;
using System.Collections;
using BannerlordTogether;
using BannerlordTogether.Network;
using BannerlordTogether.Identity;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BannerlordTogetherCrashGuard
{
    internal static class CoopPeerIdentity
    {
        // These members belong to the exact Bannerlord Together build checked at startup.
        internal static string CharacterId(int peer)
        {
            var peers = CoopSession.Server == null ? null :
                AccessTools.Field(typeof(CoopServer), "bNK").GetValue(CoopSession.Server) as IDictionary;
            object context = peers?[peer];
            if (context == null)
                return "";
            var character = (CharacterId)AccessTools.Method(context.GetType(), "I", Type.EmptyTypes).Invoke(context, null);
            return character.IsEmpty ? "" : character.ToString();
        }

        internal static string SourceKey(int peer)
        {
            var method = AccessTools.Method(typeof(CoopCampaignBehavior), "TryResolveGameplayGhostSourceKey",
                new[] { typeof(int), typeof(string).MakeByRefType(), typeof(string).MakeByRefType() });
            object[] parameters = { peer, null, null };
            return (bool)method.Invoke(null, parameters) ? (string)parameters[1] : "";
        }

        internal static Hero HeroForSource(string source)
        {
            if (CoopCampaignBehavior.Instance == null || string.IsNullOrEmpty(source))
                return null;
            return AccessTools.Method(typeof(CoopCampaignBehavior), "GetRemoteGhostHero", new[] { typeof(string) })
                .Invoke(CoopCampaignBehavior.Instance, new object[] { source }) as Hero;
        }

        internal static Hero HeroForPeer(int peer) => HeroForSource(SourceKey(peer));

        internal static string LocalSlot()
        {
            var behavior = Campaign.Current?.GetCampaignBehavior<CoopClientCharacterRosterBehavior>();
            return behavior == null ? "" : (string)AccessTools.Property(behavior.GetType(), "ActiveSlotId").GetValue(behavior);
        }
    }
}
