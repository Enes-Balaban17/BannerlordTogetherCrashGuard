using System;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    internal static class MessengerCompatibility
    {
        internal static void Install(Harmony harmony)
        {
            Type type = AccessTools.TypeByName("Diplomacy.Messengers.MessengerManager");
            if (type == null)
                return;
            var method = AccessTools.DeclaredMethod(type, "StartDialogue");
            if (method != null && Harmony.GetPatchInfo(method)?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(method, new HarmonyMethod(typeof(MessengerCompatibility), nameof(StartDialogue)));
        }

        private static bool StartDialogue(object __instance, Hero targetHero)
        {
            if (!CoopSession.IsActive)
                return true;
            if (targetHero == null || targetHero.IsDead || targetHero.IsPrisoner || Hero.MainHero == null ||
                PartyBase.MainParty == null || Mission.Current != null || PlayerEncounter.Current != null ||
                CoopSession.InSpNativeBattle || CoopSession.BattleLinkPending)
            {
                BattleLog.Write("[MESSENGER] deferred conversation because player or target is unavailable");
                return false;
            }
            // Remote conversations use a field conversation scene. Settlement location entries
            // may be absent on a co-op client even though the hero has a CurrentSettlement.
            PartyBase targetParty = targetHero.PartyBelongedTo?.Party ?? targetHero.CurrentSettlement?.Party
                ?? targetHero.BornSettlement?.Party;
            if (targetParty == null || targetParty == PartyBase.MainParty)
                return false;
            bool started = false;
            try
            {
                PlayerEncounter.Start();
                started = true;
                PlayerEncounter.Current.SetupFields(PartyBase.MainParty, targetParty);
                Campaign.Current.CurrentConversationContext = ConversationContext.Default;
                var mission = Campaign.Current.CampaignMissionManager.OpenConversationMission(
                    new ConversationCharacterData(Hero.MainHero.CharacterObject, PartyBase.MainParty, true, false, false, false, false, false),
                    new ConversationCharacterData(targetHero.CharacterObject, targetParty, true, false, false, false, false, false), "", "", false) as Mission;
                if (mission == null)
                {
                    PlayerEncounter.Finish(true);
                    BattleLog.Write("[MESSENGER] conversation open was declined by game or co-op guard");
                    return false;
                }
                AccessTools.Field(__instance.GetType(), "_position").SetValue(__instance, CampaignVec2.Invalid);
                AccessTools.Field(__instance.GetType(), "_currentMission").SetValue(__instance, mission);
                mission.AddListener((IMissionListener)__instance);
                BattleLog.Write("[MESSENGER] opened remote conversation hero=" + targetHero.StringId);
            }
            catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
            {
                if (started && Mission.Current == null)
                    PlayerEncounter.Finish(true);
                BattleLog.Write("[MESSENGER] conversation failed hero=" + targetHero.StringId + " " + exception);
            }
            return false;
        }
    }
}
