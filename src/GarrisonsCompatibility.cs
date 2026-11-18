using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace BannerlordTogetherCrashGuard
{
    internal static class GarrisonsCompatibility
    {
        public sealed class TroopStack
        {
            public string Id { get; set; }
            public int Count { get; set; }
            public int Wounded { get; set; }
        }

        public sealed class PatrolState
        {
            public string Name { get; set; }
            public string Home { get; set; }
            public string Owner { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public bool OnLand { get; set; }
            public List<TroopStack> Troops { get; set; }
            public List<TroopStack> Prisoners { get; set; }
        }

        public sealed class TemplateState
        {
            public string Name { get; set; }
            public Dictionary<string, int> Troops { get; set; }
        }

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        internal static readonly HashSet<string> Projections = new HashSet<string>(StringComparer.Ordinal);

        private static object Manager => HostSettingsCompatibility.StaticProperty("ImprovedGarrisons.Main", "PartyManagement");

        internal static void Capture(Dictionary<string, string> state)
        {
            object manager = Manager;
            if (manager == null)
                return;
            var parties = AccessTools.Method(manager.GetType(), "GetAllImprovedGarrisonParties").Invoke(manager, null) as IEnumerable;
            if (parties == null)
                return;
            var ids = new List<string>();
            foreach (MobileParty party in parties)
            {
                if (party == null || !party.IsActive || party.HomeSettlement == null)
                    continue;
                ids.Add(party.StringId);
                state["ig/party/" + party.StringId] = Json.Serialize(new PatrolState
                {
                    Name = party.Name.ToString(), Home = party.HomeSettlement.StringId,
                    Owner = party.Owner?.StringId ?? party.HomeSettlement.Owner?.StringId,
                    X = party.Position.X, Y = party.Position.Y, OnLand = party.Position.IsOnLand,
                    Troops = CaptureRoster(party.MemberRoster), Prisoners = CaptureRoster(party.PrisonRoster)
                });
            }
            state["ig/manifest"] = Json.Serialize(ids);
            object save = HostSettingsCompatibility.StaticProperty("ImprovedGarrisons.SaveSystem.SaveData.IGSaveData", "Instance");
            var settings = save == null ? null : AccessTools.Property(save.GetType(), "SettlementSettingsData").GetValue(save) as IDictionary;
            if (settings == null)
                return;
            foreach (DictionaryEntry item in settings)
            {
                var values = new Dictionary<string, string>();
                foreach (PropertyInfo property in item.Value.GetType().GetProperties())
                    if (property.CanWrite && property.CanRead && (property.PropertyType.IsPrimitive || property.PropertyType == typeof(string)))
                        values[property.Name] = Convert.ToString(property.GetValue(item.Value), CultureInfo.InvariantCulture);
                object template = AccessTools.Property(item.Value.GetType(), "Template").GetValue(item.Value);
                if (template != null)
                {
                    var troops = AccessTools.Field(template.GetType(), "troopList").GetValue(template) as Dictionary<string, int>;
                    values["__template"] = Json.Serialize(new TemplateState
                    {
                        Name = (string)AccessTools.Property(template.GetType(), "Name").GetValue(template), Troops = troops
                    });
                }
                values["__upgrades"] = Json.Serialize(AccessTools.Property(item.Value.GetType(), "TroopsToUpgradeTo").GetValue(item.Value));
                var initial = AccessTools.Property(item.Value.GetType(), "InitialTroopRoster").GetValue(item.Value) as IEnumerable<Tuple<string, int>>;
                values["__initial"] = Json.Serialize(initial?.Select(row => new TroopStack { Id = row.Item1, Count = row.Item2 }).ToList());
                state["ig/settings/" + item.Key] = Json.Serialize(values);
            }
        }

        private static List<TroopStack> CaptureRoster(TroopRoster roster)
        {
            return roster.GetTroopRoster().Where(row => row.Character != null && !row.Character.IsHero)
                .Select(row => new TroopStack { Id = row.Character.StringId, Count = row.Number, Wounded = row.WoundedNumber }).ToList();
        }

        internal static void Apply(Dictionary<string, string> state)
        {
            if (Manager == null)
                return;
            foreach (var item in state)
            {
                if (item.Key.StartsWith("ig/party/", StringComparison.Ordinal))
                    ApplyPatrol(item.Key.Substring(9), Json.Deserialize<PatrolState>(item.Value));
                else if (item.Key.StartsWith("ig/settings/", StringComparison.Ordinal))
                    ApplySettings(item.Key.Substring(12), Json.Deserialize<Dictionary<string, string>>(item.Value));
            }
            if (state.TryGetValue("ig/manifest", out string manifest))
            {
                var ids = new HashSet<string>(Json.Deserialize<string[]>(manifest), StringComparer.Ordinal);
                foreach (string stale in Projections.Except(ids).ToArray())
                {
                    MobileParty party = MBObjectManager.Instance.GetObject<MobileParty>(stale);
                    if (party?.MapEvent != null)
                        continue;
                    if (party != null && party.IsActive)
                    {
                        UnregisterPatrol(party);
                        DestroyPartyAction.Apply(null, party);
                    }
                    Projections.Remove(stale);
                }
            }
        }

        private static void ApplySettings(string key, Dictionary<string, string> values)
        {
            object save = HostSettingsCompatibility.StaticProperty("ImprovedGarrisons.SaveSystem.SaveData.IGSaveData", "Instance");
            var settings = AccessTools.Property(save.GetType(), "SettlementSettingsData").GetValue(save) as IDictionary;
            if (settings == null)
                return;
            object target = settings[key];
            if (target == null)
            {
                Type valueType = settings.GetType().GetGenericArguments()[1];
                target = Activator.CreateInstance(valueType);
                settings[key] = target;
            }
            foreach (PropertyInfo property in target.GetType().GetProperties())
                if (property.CanWrite && (property.PropertyType.IsPrimitive || property.PropertyType == typeof(string)) &&
                    values.TryGetValue(property.Name, out string value))
                    property.SetValue(target, Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture));
            if (values.TryGetValue("__upgrades", out string upgrades))
                AccessTools.Property(target.GetType(), "TroopsToUpgradeTo").SetValue(target, Json.Deserialize<bool[]>(upgrades));
            if (values.TryGetValue("__initial", out string initial))
            {
                PropertyInfo property = AccessTools.Property(target.GetType(), "InitialTroopRoster");
                var rows = Json.Deserialize<List<TroopStack>>(initial);
                property.SetValue(target, rows?.Select(row => Tuple.Create(row.Id, row.Count)).ToList());
            }
            if (values.TryGetValue("__template", out string text))
            {
                TemplateState data = Json.Deserialize<TemplateState>(text);
                PropertyInfo property = AccessTools.Property(target.GetType(), "Template");
                object template = Activator.CreateInstance(property.PropertyType, new object[] { data.Name });
                foreach (var row in data.Troops ?? new Dictionary<string, int>())
                {
                    CharacterObject character = MBObjectManager.Instance.GetObject<CharacterObject>(row.Key);
                    if (character != null && !character.IsHero && row.Value >= 0)
                        AccessTools.Method(template.GetType(), "AddOrUpdateCharacter").Invoke(template, new object[] { character, row.Value });
                }
                property.SetValue(target, template);
            }
        }

        private static bool ValidRoster(List<TroopStack> rows)
        {
            return rows != null && rows.Count <= 512 && rows.All(row => row != null && row.Count >= 0 &&
                row.Count <= 100000 && row.Wounded >= 0 && row.Wounded <= row.Count &&
                MBObjectManager.Instance.GetObject<CharacterObject>(row.Id) is CharacterObject character && !character.IsHero);
        }

        private static void SetRoster(TroopRoster target, List<TroopStack> rows)
        {
            target.Clear();
            foreach (TroopStack row in rows)
                target.AddToCounts(MBObjectManager.Instance.GetObject<CharacterObject>(row.Id), row.Count,
                    false, row.Wounded);
        }

        private static void ApplyPatrol(string id, PatrolState state)
        {
            if (state == null || !ValidRoster(state.Troops) || !ValidRoster(state.Prisoners) ||
                float.IsNaN(state.X) || float.IsInfinity(state.X) || float.IsNaN(state.Y) || float.IsInfinity(state.Y))
                return;
            Settlement home = Settlement.Find(state.Home);
            Hero owner = MBObjectManager.Instance.GetObject<Hero>(state.Owner);
            if (home == null || owner == null)
                return;
            MobileParty party = MBObjectManager.Instance.GetObject<MobileParty>(id);
            if (party == MobileParty.MainParty || party?.MapEvent != null)
                return;
            var position = new CampaignVec2(new Vec2(state.X, state.Y), state.OnLand);
            if (party == null)
            {
                party = MobileParty.CreateParty(id, null);
                party.InitializeMobilePartyAroundPosition(TroopRoster.CreateDummyTroopRoster(),
                    TroopRoster.CreateDummyTroopRoster(), position, 0f, 0f, !state.OnLand);
                BattleLog.Write("[GARRISONS] created host patrol projection " + id);
            }
            Projections.Add(id);
            party.Party.SetCustomName(new TextObject(state.Name));
            party.SetCustomHomeSettlement(home);
            party.Party.SetCustomOwner(owner);
            party.ActualClan = owner.Clan;
            party.Ai.DisableAi();
            party.IgnoreByOtherPartiesTill(CampaignTime.HoursFromNow(99999f));
            party.Position = position;
            SetRoster(party.MemberRoster, state.Troops);
            SetRoster(party.PrisonRoster, state.Prisoners);
            party.Party.SetVisualAsDirty();
            RegisterPatrol(party, home);
        }

        private static IDictionary PatrolRegistry()
        {
            object manager = Manager;
            object patrols = manager == null ? null : AccessTools.Field(manager.GetType(), "mobileGarrisonManagement").GetValue(manager);
            return patrols == null ? null : AccessTools.Property(patrols.GetType(), "MobileGarrisons").GetValue(patrols) as IDictionary;
        }

        private static void RegisterPatrol(MobileParty party, Settlement home)
        {
            if (!party.StringId.StartsWith("mobilegarrison_", StringComparison.Ordinal))
                return;
            IDictionary registry = PatrolRegistry();
            if (registry == null)
                return;
            object current = registry[home.StringId];
            if (current != null && ReferenceEquals(AccessTools.Field(current.GetType(), "mobileParty").GetValue(current), party))
                return;
            Type type = AccessTools.TypeByName("ImprovedGarrisons.AI.AITypes.MobileGarrison");
            registry[home.StringId] = Activator.CreateInstance(type, new object[] { party, home, false });
        }

        private static void UnregisterPatrol(MobileParty party)
        {
            IDictionary registry = PatrolRegistry();
            if (registry == null)
                return;
            var keys = new List<object>();
            foreach (DictionaryEntry entry in registry)
                if (ReferenceEquals(AccessTools.Field(entry.Value.GetType(), "mobileParty").GetValue(entry.Value), party))
                    keys.Add(entry.Key);
            foreach (object key in keys)
                registry.Remove(key);
        }
    }
}
