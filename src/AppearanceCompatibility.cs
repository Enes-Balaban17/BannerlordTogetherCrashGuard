using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Extensions;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace BannerlordTogetherCrashGuard
{
    internal static class AppearanceCompatibility
    {
        public sealed class HeroAppearance
        {
            public string Body { get; set; }
            public string Name { get; set; }
            public string FirstName { get; set; }
            public string Culture { get; set; }
            public bool Female { get; set; }
            public int Race { get; set; }
            public int Level { get; set; }
            public Dictionary<string, int> Skills { get; set; }
            public Dictionary<string, int> Attributes { get; set; }
        }

        private static readonly Dictionary<string, string> Published = new Dictionary<string, string>();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static string _localAppearance;
        private static readonly HashSet<string> FailedHeroes = new HashSet<string>();
        private static readonly Dictionary<GhostPartyController, Tuple<PlayerHeroDataPacket, bool>> GhostData =
            new Dictionary<GhostPartyController, Tuple<PlayerHeroDataPacket, bool>>();

        internal static void Reset()
        {
            Published.Clear();
            _localAppearance = null;
            FailedHeroes.Clear();
            GhostData.Clear();
        }

        internal static void ObserveLocalHero()
        {
            Hero hero = Hero.MainHero;
            if (hero?.CharacterObject == null || CoopCampaignBehavior.Instance == null)
                return;
            string value = Serialize(hero);
            if (_localAppearance == value)
                return;
            _localAppearance = value;
            AccessTools.Field(typeof(CoopCampaignBehavior), "_heroDataSent").SetValue(CoopCampaignBehavior.Instance, false);
            BattleLog.Write("[CHARACTER-RELOAD] scheduled native player appearance rebroadcast");
        }

        private static bool Eligible(Hero hero)
        {
            return hero != null && hero != Hero.MainHero && !hero.IsDead && !hero.IsTemplate &&
                hero.CharacterObject != null && hero.StringId != "main_hero" &&
                !GhostEncyclopediaCompatibility.Retired.Contains(hero.StringId) &&
                (hero.PartyBelongedTo == null || !CoopSession.GhostPartyIds.Contains(hero.PartyBelongedTo.StringId));
        }

        private static string Serialize(Hero hero)
        {
            return Json.Serialize(new HeroAppearance
            {
                Body = hero.CharacterObject.GetBodyProperties(hero.CharacterObject.Equipment).ToString(),
                Name = hero.Name?.ToString() ?? hero.StringId, FirstName = hero.FirstName?.ToString() ?? hero.Name?.ToString() ?? hero.StringId,
                Culture = hero.Culture?.StringId ?? "", Female = hero.IsFemale, Race = hero.CharacterObject.Race, Level = hero.Level,
                Skills = Skills.All.ToDictionary(skill => skill.StringId, skill => hero.GetSkillValue(skill)),
                Attributes = Attributes.All.ToDictionary(attribute => attribute.StringId, attribute => hero.GetAttributeValue(attribute))
            });
        }

        internal static void Publish(Hero hero)
        {
            if (hero == Hero.MainHero)
            {
                ObserveLocalHero();
                return;
            }
            if (!Eligible(hero))
                return;
            CoopCompatibilityBehavior.Instance?.PublishChanges(new Dictionary<string, string>
                { ["hero/" + hero.StringId] = Serialize(hero) });
        }

        internal static void CaptureChanges(Dictionary<string, string> state)
        {
            foreach (Hero hero in Hero.AllAliveHeroes)
            {
                if (!Eligible(hero))
                    continue;
                string value = TrySerialize(hero);
                if (value == null)
                    continue;
                if (Published.TryGetValue(hero.StringId, out string old) && old == value)
                    continue;
                state["hero/" + hero.StringId] = value;
                Published[hero.StringId] = value;
            }
        }

        internal static void SendFullState(int peer)
        {
            var state = new Dictionary<string, string>();
            foreach (Hero hero in Hero.AllAliveHeroes)
                if (Eligible(hero))
                {
                    string value = TrySerialize(hero);
                    if (value != null)
                        state["hero/" + hero.StringId] = value;
                }
            CoopCompatibilityBehavior.Instance.SendState(peer, state);
        }

        private static string TrySerialize(Hero hero)
        {
            try { return Serialize(hero); }
            catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
            {
                if (FailedHeroes.Add(hero.StringId))
                    BattleLog.Write("[CHARACTER-RELOAD] skipped invalid hero " + hero.StringId + " " + exception.Message);
                return null;
            }
        }

        internal static void TrackGhostData(GhostPartyController controller, PlayerHeroDataPacket data, bool shared)
        {
            GhostData[controller] = Tuple.Create(data, shared);
        }

        internal static void RefreshGhostEquipment()
        {
            foreach (var item in GhostData.ToArray())
                if (item.Key.IsSpawned)
                    item.Key.ApplyHeroData(item.Value.Item1, item.Value.Item2);
        }

        internal static void AcceptClientChanges(Dictionary<string, string> state)
        {
            // The explicit Character Reload edit path is relayed by the host after validation.
            var accepted = new Dictionary<string, string>();
            foreach (var item in state)
                if (item.Key.StartsWith("hero/", StringComparison.Ordinal) && ApplyHero(item.Key.Substring(5), item.Value))
                    accepted[item.Key] = item.Value;
            if (accepted.Count > 0)
                CoopCompatibilityBehavior.Instance.PublishChanges(accepted);
        }

        internal static void Apply(Dictionary<string, string> state)
        {
            foreach (var item in state)
                if (item.Key.StartsWith("hero/", StringComparison.Ordinal))
                    ApplyHero(item.Key.Substring(5), item.Value);
        }

        private static bool ApplyHero(string id, string text)
        {
            Hero hero = MBObjectManager.Instance.GetObject<Hero>(id);
            if (!Eligible(hero))
                return false;
            HeroAppearance data = Json.Deserialize<HeroAppearance>(text);
            if (data == null || data.Name == null || data.Name.Length > 256 || data.FirstName == null ||
                data.FirstName.Length > 256 || data.Race < 0 || data.Race >= FaceGen.GetRaceCount() || data.Level < 0 || data.Level > 10000)
                return false;
            BodyProperties body;
            if (string.IsNullOrEmpty(data.Body) || !BodyProperties.FromString(data.Body, out body) ||
                body.Age < 0 || body.Age > 150 || body.Weight < 0 || body.Weight > 1 || body.Build < 0 || body.Build > 1)
                return false;
            hero.StaticBodyProperties = body.StaticProperties;
            hero.Weight = body.Weight;
            hero.Build = body.Build;
            hero.IsFemale = data.Female;
            hero.CharacterObject.Race = data.Race;
            hero.Level = data.Level;
            if (Math.Abs(hero.Age - body.Age) > 0.01f)
                hero.SetBirthDay(CampaignTime.YearsFromNow(-body.Age));
            hero.SetName(new TextObject(data.Name), new TextObject(data.FirstName));
            CultureObject culture = MBObjectManager.Instance.GetObject<CultureObject>(data.Culture);
            if (culture != null)
                hero.Culture = culture;
            hero.PartyBelongedTo?.Party.SetVisualAsDirty();
            if (data.Skills != null)
                foreach (SkillObject skill in Skills.All)
                    if (data.Skills.TryGetValue(skill.StringId, out int value) && value >= 0 && value <= 10000)
                        hero.SetSkillValue(skill, value);
            if (data.Attributes != null)
                foreach (CharacterAttribute attribute in Attributes.All)
                    if (data.Attributes.TryGetValue(attribute.StringId, out int value) && value >= 0 && value <= 10000)
                        AccessTools.Method(typeof(Hero), "SetAttributeValueInternal").Invoke(hero, new object[] { attribute, value });
            Type utils = AccessTools.TypeByName("CharacterReload.Utils.HeroUtils");
            if (utils != null)
                AccessTools.Property(utils, "refreshEncyclopedia")?.SetValue(null, true);
            return true;
        }
    }
}
