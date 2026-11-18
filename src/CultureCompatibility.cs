using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;

namespace BannerlordTogetherCrashGuard
{
    internal static class CultureCompatibility
    {
        public sealed class Profile
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string PositiveFeatId1 { get; set; }
            public string PositiveFeatId2 { get; set; }
            public string NegativeFeatId { get; set; }
            public string SpawnRegionCultureId { get; set; }
            public string Source { get; set; }
        }

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly Dictionary<string, Profile> Profiles = new Dictionary<string, Profile>(StringComparer.Ordinal);
        internal static readonly HashSet<string> LoadoutSlots = new HashSet<string>(StringComparer.Ordinal);
        internal static string LocalCharacterId;
        private static bool _applying;

        internal static void BeginCampaign()
        {
            Profiles.Clear();
            LoadoutSlots.Clear();
            LocalCharacterId = null;
        }

        private static object Behavior => HostSettingsCompatibility.StaticProperty("BannerlordCustomCultures.CustomCultureBehavior", "Instance");

        internal static string LocalProfile()
        {
            object behavior = Behavior;
            object profile = behavior == null ? null : AccessTools.Property(behavior.GetType(), "PlayerProfile").GetValue(behavior);
            return profile == null ? "" : Json.Serialize(profile);
        }

        internal static void Install(Harmony harmony)
        {
            Type type = AccessTools.TypeByName("BannerlordCustomCultures.CustomCultureBehavior");
            if (type != null)
            {
                var feat = AccessTools.Method(type, "TryGetFeatOverrideResult");
                if (Harmony.GetPatchInfo(feat)?.Owners.Contains(SubModule.Owner) != true)
                    harmony.Patch(feat,
                    new HarmonyMethod(typeof(CultureCompatibility), nameof(FeatPrefix)));
                var set = AccessTools.Method(type, "SetPlayerProfile");
                if (Harmony.GetPatchInfo(set)?.Owners.Contains(SubModule.Owner) != true)
                    harmony.Patch(set,
                    postfix: new HarmonyMethod(typeof(CultureCompatibility), nameof(ProfileChanged)));
            }
            Type loadout = AccessTools.TypeByName("CustomStartingLoadout.StartingLoadoutBehavior");
            if (loadout != null && Harmony.GetPatchInfo(AccessTools.Method(loadout, "OnCharacterCreationIsOver"))?.Owners.Contains(SubModule.Owner) != true)
                harmony.Patch(AccessTools.Method(loadout, "OnCharacterCreationIsOver"),
                    new HarmonyMethod(typeof(CultureCompatibility), nameof(BeforeLoadout)),
                    new HarmonyMethod(typeof(CultureCompatibility), nameof(AfterLoadout)));
        }

        private static void BeforeLoadout(object __instance)
        {
            if (!CoopSession.IsClient)
                return;
            string slot = CoopPeerIdentity.LocalSlot();
            if (!string.IsNullOrEmpty(slot))
                AccessTools.Field(__instance.GetType(), "_alreadyShown").SetValue(__instance, LoadoutSlots.Contains(slot));
        }

        private static void AfterLoadout(object __instance)
        {
            if (!CoopSession.IsClient || !(bool)AccessTools.Field(__instance.GetType(), "_alreadyShown").GetValue(__instance))
                return;
            string slot = CoopPeerIdentity.LocalSlot();
            if (!string.IsNullOrEmpty(slot))
                LoadoutSlots.Add(slot);
        }

        private static void ProfileChanged()
        {
            if (_applying || !CoopSession.IsActive)
                return;
            if (CoopSession.IsHost)
                Profiles["host"] = Parse(LocalProfile());
            else if (CoopCompatibilityBehavior.HasHostState)
                CoopCompatibilityBehavior.Instance.PublishChanges(new Dictionary<string, string> { ["culture-edit"] = LocalProfile() });
        }

        private static Profile Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            Profile profile = Json.Deserialize<Profile>(text);
            if (profile == null || (profile.Name?.Length ?? 0) > 256 || (profile.Description?.Length ?? 0) > 4096)
                return null;
            return profile;
        }

        internal static void Accept(int peer, string text, bool replace)
        {
            string id = CoopPeerIdentity.CharacterId(peer);
            if (string.IsNullOrEmpty(id) || (!replace && Profiles.ContainsKey(id)))
                return;
            Profile profile = Parse(text);
            if (profile == null)
                return;
            profile.Source = CoopPeerIdentity.SourceKey(peer);
            Profiles[id] = profile;
        }

        internal static void Capture(Dictionary<string, string> values, IEnumerable<int> peers)
        {
            Profile own = Parse(LocalProfile());
            if (own != null)
            {
                own.Source = "host";
                Profiles["host"] = own;
            }
            foreach (int peer in peers)
            {
                string id = CoopPeerIdentity.CharacterId(peer);
                if (Profiles.TryGetValue(id, out Profile profile) && profile != null)
                    profile.Source = CoopPeerIdentity.SourceKey(peer);
            }
            foreach (var item in Profiles)
                if (item.Value != null)
                    values["culture/" + item.Key] = Json.Serialize(item.Value);
        }

        internal static void Apply(Dictionary<string, string> values)
        {
            foreach (var item in values)
            {
                if (!item.Key.StartsWith("culture/", StringComparison.Ordinal))
                    continue;
                string id = item.Key.Substring(8);
                Profile profile = Parse(item.Value);
                if (profile == null)
                    continue;
                Profiles[id] = profile;
                if (id == LocalCharacterId && Behavior != null)
                {
                    object behavior = Behavior;
                    Type profileType = AccessTools.Property(behavior.GetType(), "PlayerProfile").PropertyType;
                    object native = Json.Deserialize(item.Value, profileType);
                    try
                    {
                        _applying = true;
                        AccessTools.Method(behavior.GetType(), "SetPlayerProfile").Invoke(behavior, new[] { native });
                    }
                    finally { _applying = false; }
                }
            }
        }

        private static bool FeatPrefix(Hero hero, FeatObject feat, ref bool result, ref bool __result)
        {
            if (!CoopSession.IsActive || hero == null || hero == Hero.MainHero || feat == null)
                return true;
            foreach (Profile profile in Profiles.Values)
            {
                if (profile == null || CoopPeerIdentity.HeroForSource(profile.Source) != hero)
                    continue;
                result = feat.StringId == profile.PositiveFeatId1 || feat.StringId == profile.PositiveFeatId2 || feat.StringId == profile.NegativeFeatId;
                __result = true;
                return false;
            }
            return true;
        }

        internal static void SyncData(IDataStore store)
        {
            string text = store.IsSaving ? Json.Serialize(Profiles) : "";
            string slots = store.IsSaving ? string.Join("\n", LoadoutSlots) : "";
            store.SyncData("btcg_cultures_v1", ref text);
            store.SyncData("btcg_loadout_slots_v1", ref slots);
            if (store.IsLoading)
            {
                Profiles.Clear();
                if (!string.IsNullOrEmpty(text))
                    foreach (var item in Json.Deserialize<Dictionary<string, Profile>>(text))
                        Profiles[item.Key] = item.Value;
                LoadoutSlots.Clear();
                LoadoutSlots.UnionWith((slots ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }
    }
}
