using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordTogetherCrashGuard
{
    [HarmonyPatch(typeof(MobileParty), "TeleportPartyToOutSideOfEncounterRadius")]
    internal static class CaptivityReleasePatch
    {
        private static readonly MethodInfo MapFaction = AccessTools.PropertyGetter(
            typeof(MobileParty), nameof(MobileParty.MapFaction));
        private static readonly MethodInfo AtWar = AccessTools.Method(
            typeof(IFaction), nameof(IFaction.IsAtWarWith), new[] { typeof(IFaction) });
        private static readonly MethodInfo CheckHostility = AccessTools.Method(
            typeof(CaptivityReleasePatch), nameof(IsNearbyPartyHostile));
        private static int _reportedMissingNearbyFaction;
        private static int _reportedMissingPlayerFaction;
        private static int _reportedRelocationFailure;

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var matches = new List<int>();
            for (int index = 0; index <= code.Count - 4; index++)
            {
                if (code[index].Calls(MapFaction) && code[index + 1].opcode == OpCodes.Ldarg_0
                    && code[index + 2].Calls(MapFaction) && code[index + 3].Calls(AtWar))
                    matches.Add(index);
            }

            if (matches.Count != 1 || code.Skip(matches[0]).Take(4)
                .Any(instruction => instruction.labels.Count != 0 || instruction.blocks.Count != 0))
            {
                BattleLog.Write("[CAPTIVITY-EXIT] Nearby faction check not patched: incompatible IL");
                return code;
            }

            // Consume the same two parties and leave the same bool for the vanilla position search.
            int start = matches[0];
            code[start] = new CodeInstruction(OpCodes.Ldarg_0);
            code[start + 1] = new CodeInstruction(OpCodes.Call, CheckHostility);
            code.RemoveRange(start + 2, 2);
            return code;
        }

        private static bool IsNearbyPartyHostile(MobileParty nearby, MobileParty released)
        {
            if (!IsCoopCaptivityRelease(released))
                return nearby.MapFaction.IsAtWarWith(released.MapFaction);

            IFaction releasedFaction = released.MapFaction ?? Hero.MainHero?.MapFaction;
            if (releasedFaction == null)
            {
                if (Interlocked.Exchange(ref _reportedMissingPlayerFaction, 1) == 0)
                    BattleLog.Write("[CAPTIVITY-EXIT] Rejected relocation candidate without a player faction");
                return true;
            }

            IFaction nearbyFaction = nearby.MapFaction;
            if (nearbyFaction != null)
                return nearbyFaction.IsAtWarWith(releasedFaction);

            // A map projection without a faction cannot determine whether a tile is hostile.
            // For a real active party with unknown allegiance, try a different tile.
            bool blocksRelocation = nearby.IsActive && !CoopSession.GhostPartyIds.Contains(nearby.StringId);
            if (Interlocked.Exchange(ref _reportedMissingNearbyFaction, 1) == 0)
                BattleLog.Write($"[CAPTIVITY-EXIT] Nearby party has no faction candidateRejected={blocksRelocation}");
            return blocksRelocation;
        }

        private static Exception Finalizer(MobileParty __instance, Exception __exception)
        {
            if (!(__exception is NullReferenceException) || !IsCoopCaptivityRelease(__instance))
                return __exception;

            // Release still needs to reactivate the party and clear the captor after relocation.
            if (Interlocked.Exchange(ref _reportedRelocationFailure, 1) == 0)
                BattleLog.Write("[CAPTIVITY-EXIT] Relocation failed; continuing native release: " + __exception);
            return null;
        }

        private static bool IsCoopCaptivityRelease(MobileParty party)
        {
            return !RaidUpdatePatch.DiagnosticsOnly && CoopSession.IsActive
                && ReferenceEquals(party, MobileParty.MainParty) && PlayerCaptivity.CaptorParty != null;
        }
    }
}
