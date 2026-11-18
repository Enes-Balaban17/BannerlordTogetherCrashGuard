using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Xml;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace BannerlordTogetherCrashGuard
{
    internal static class SmithingCompatibility
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly Dictionary<string, string> Applied = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Sent = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> OriginalFlags = new Dictionary<string, bool>();
        private static bool _importing;

        internal static void PublishRecord(object record)
        {
            if (record == null || _importing || CoopCompatibilityBehavior.Applying ||
                !BannerlordTogether.CoopSession.IsActive || CoopCompatibilityBehavior.Instance == null)
                return;
            try
            {
                string id = (string)AccessTools.Field(record.GetType(), "StringId").GetValue(record);
                string value = Json.Serialize(record);
                if (value.Length <= 32000)
                    CoopCompatibilityBehavior.Instance.PublishChanges(new Dictionary<string, string> { ["smithing/" + id] = value });
            }
            catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
            { BattleLog.Write("[SMITHING] immediate publish failed " + exception.Message); }
        }

        internal static void Capture(Dictionary<string, string> state)
        {
            Type service = AccessTools.TypeByName("ModularSmithing.Crafting.SmithingCraftService");
            if (service != null)
                foreach (string flag in new[] { "FreeCrafting", "AllowExperimentalWeapons" })
                    state["smithflag/" + flag] = AccessTools.Field(service, flag).GetValue(null).ToString();
            object behavior = HostSettingsCompatibility.StaticProperty("ModularSmithing.Crafting.SmithingPersistenceBehavior", "Current");
            if (behavior == null)
                return;
            var records = AccessTools.Property(behavior.GetType(), "Records").GetValue(behavior) as IEnumerable;
            foreach (object record in records)
            {
                string id = (string)AccessTools.Field(record.GetType(), "StringId").GetValue(record);
                string value = Json.Serialize(record);
                if (value.Length <= 32000)
                    state["smithing/" + id] = value;
            }
        }

        internal static void Apply(Dictionary<string, string> state)
        {
            Type type = AccessTools.TypeByName("ModularSmithing.Crafting.CraftedItemRecord");
            Type factory = AccessTools.TypeByName("ModularSmithing.Crafting.CraftingItemFactory");
            object behavior = HostSettingsCompatibility.StaticProperty("ModularSmithing.Crafting.SmithingPersistenceBehavior", "Current");
            if (type == null || factory == null || behavior == null)
                return;
            Type service = AccessTools.TypeByName("ModularSmithing.Crafting.SmithingCraftService");
            foreach (string flag in new[] { "FreeCrafting", "AllowExperimentalWeapons" })
                if (service != null && state.TryGetValue("smithflag/" + flag, out string text) && bool.TryParse(text, out bool value))
                {
                    var field = AccessTools.Field(service, flag);
                    if (!OriginalFlags.ContainsKey(flag))
                        OriginalFlags.Add(flag, (bool)field.GetValue(null));
                    field.SetValue(null, value);
                }
            foreach (var item in state)
            {
                if (!item.Key.StartsWith("smithing/", StringComparison.Ordinal) ||
                    (Applied.TryGetValue(item.Key, out string previous) && previous == item.Value))
                    continue;
                object record = Json.Deserialize(item.Value, type);
                string id = (string)AccessTools.Field(type, "StringId").GetValue(record);
                string donor = (string)AccessTools.Field(type, "DonorItemId").GetValue(record);
                string xml = (string)AccessTools.Field(type, "ItemXml").GetValue(record);
                if (item.Key != "smithing/" + id || string.IsNullOrEmpty(xml) ||
                    (!string.IsNullOrEmpty(donor) && MBObjectManager.Instance.GetObject<ItemObject>(donor) == null))
                    continue;
                var document = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(new System.IO.StringReader(xml),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    document.Load(reader);
                if (document.DocumentElement?.GetAttribute("id") != id)
                    continue;
                ItemObject existing = MBObjectManager.Instance.GetObject<ItemObject>(id);
                bool registered = (bool)AccessTools.Method(behavior.GetType(), "Contains").Invoke(behavior, new object[] { id });
                if (existing != null && !registered)
                {
                    BattleLog.Write("[SMITHING] rejected crafted definition colliding with installed item " + id);
                    continue;
                }
                MethodInfo method = AccessTools.Method(factory, existing == null ? "CreateNew" : "Rehydrate");
                if (method.Invoke(null, new[] { record }) == null)
                    continue;
                _importing = true;
                try { AccessTools.Method(behavior.GetType(), "Register").Invoke(behavior, new[] { record }); }
                finally { _importing = false; }
                Applied[item.Key] = item.Value;
                AppearanceCompatibility.RefreshGhostEquipment();
                BattleLog.Write("[SMITHING] synchronized crafted definition " + id + " donor=" + donor);
            }
        }

        internal static void PublishClientRecords()
        {
            if (!CoopCompatibilityBehavior.HasHostState)
                return;
            var state = new Dictionary<string, string>();
            Capture(state);
            foreach (var item in state)
            {
                if (!item.Key.StartsWith("smithing/", StringComparison.Ordinal) ||
                    (Sent.TryGetValue(item.Key, out string old) && old == item.Value))
                    continue;
                CoopCompatibilityBehavior.Instance.PublishChanges(new Dictionary<string, string> { [item.Key] = item.Value });
                Sent[item.Key] = item.Value;
            }
        }

        internal static void AcceptClientChanges(Dictionary<string, string> state)
        {
            var records = new Dictionary<string, string>();
            foreach (var item in state)
                if (item.Key.StartsWith("smithing/", StringComparison.Ordinal))
                    records.Add(item.Key, item.Value);
            Apply(records);
            var accepted = new Dictionary<string, string>();
            foreach (var record in records)
                if (Applied.TryGetValue(record.Key, out string applied) && applied == record.Value)
                    accepted.Add(record.Key, record.Value);
            if (accepted.Count > 0)
                CoopCompatibilityBehavior.Instance.PublishChanges(accepted);
        }

        internal static void Reset()
        {
            Type service = AccessTools.TypeByName("ModularSmithing.Crafting.SmithingCraftService");
            if (service != null)
                foreach (var flag in OriginalFlags)
                    AccessTools.Field(service, flag.Key)?.SetValue(null, flag.Value);
            OriginalFlags.Clear();
            Applied.Clear();
            Sent.Clear();
        }
    }
}
