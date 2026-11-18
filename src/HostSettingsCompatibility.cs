using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BannerlordTogetherCrashGuard
{
    internal static class HostSettingsCompatibility
    {
        private sealed class Binding
        {
            internal string Id;
            internal Func<object> GetInstance;
        }

        private static readonly Binding[] Bindings =
        {
            new Binding { Id = "CampaignTweaks", GetInstance = () => StaticProperty("OffTheClock.CampaignTweaks.SubModule", "Current") },
            new Binding { Id = "ModularSmithing2", GetInstance = () => StaticProperty("ModularSmithing.Crafting.MSmithConfig", "Settings") },
            new Binding { Id = "BetterTime", GetInstance = () => McmSettings("BetterTime.Settings") },
            new Binding { Id = "XorberaxLegacy", GetInstance = () => McmSettings("XorberaxLegacy.Settings.Settings") },
            new Binding { Id = "Diplomacy", GetInstance = () => McmSettings("Diplomacy.Settings") },
            new Binding { Id = "DismembermentPlus", GetInstance = () => McmSettings("DismembermentPlus.Settings.Settings") },
            new Binding { Id = "ImprovedGarrisons", GetInstance = () => InstanceProperty("ImprovedGarrisons.SaveSystem.Configuration.ConfigManager", "Instance", "Config") }
        };

        private static readonly Dictionary<string, Tuple<object, MemberInfo, object>> Originals =
            new Dictionary<string, Tuple<object, MemberInfo, object>>(StringComparer.Ordinal);
        private static readonly HashSet<string> Failures = new HashSet<string>();

        private static void Report(string binding, Exception exception)
        {
            if (Failures.Add(binding + exception.GetType().FullName + exception.Message))
                BattleLog.Write("[HOST-SETTINGS] skipped unavailable binding " + binding + " " + exception.Message);
        }

        internal static object StaticProperty(string typeName, string propertyName)
        {
            Type type = AccessTools.TypeByName(typeName);
            return type == null ? null : AccessTools.Property(type, propertyName)?.GetValue(null);
        }

        private static object InstanceProperty(string typeName, string singleton, string propertyName)
        {
            object instance = StaticProperty(typeName, singleton);
            return instance == null ? null : AccessTools.Property(instance.GetType(), propertyName)?.GetValue(instance);
        }

        private static object McmSettings(string typeName)
        {
            Type type = AccessTools.TypeByName(typeName);
            if (type == null)
                return null;
            return type.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)?.GetValue(null);
        }

        private static bool Supported(Type type)
        {
            return type == typeof(bool) || type == typeof(int) || type == typeof(float) ||
                type == typeof(double) || type == typeof(string) || type.IsEnum;
        }

        private static IEnumerable<MemberInfo> Members(object instance)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in instance.GetType().GetFields(flags))
                if (!field.IsInitOnly && !field.Name.Contains("<") && Supported(field.FieldType))
                    yield return field;
            foreach (PropertyInfo property in instance.GetType().GetProperties(flags))
                if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0 &&
                    Supported(property.PropertyType))
                    yield return property;
        }

        private static object Read(object instance, MemberInfo member)
        {
            return member is FieldInfo field ? field.GetValue(instance) : ((PropertyInfo)member).GetValue(instance);
        }

        private static void Write(object instance, MemberInfo member, object value)
        {
            if (member is FieldInfo field)
                field.SetValue(instance, value);
            else
                ((PropertyInfo)member).SetValue(instance, value);
        }

        internal static void Capture(Dictionary<string, string> values)
        {
            foreach (Binding binding in Bindings)
            {
                try
                {
                object instance = binding.GetInstance();
                if (instance == null)
                    continue;
                foreach (MemberInfo member in Members(instance))
                {
                    object value = Read(instance, member);
                    string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (text != null && text.Length <= 4096)
                        values["setting/" + binding.Id + "/" + member.Name] = text;
                }
                }
                catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
                { Report(binding.Id, exception); }
            }
        }

        internal static void Apply(Dictionary<string, string> values)
        {
            foreach (Binding binding in Bindings)
            {
                try
                {
                object instance = binding.GetInstance();
                if (instance == null)
                    continue;
                foreach (MemberInfo member in Members(instance))
                {
                    string key = "setting/" + binding.Id + "/" + member.Name;
                    if (!values.TryGetValue(key, out string text))
                        continue;
                    Type type = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
                    object value = type.IsEnum ? Enum.Parse(type, text, false)
                        : Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
                    object current = Read(instance, member);
                    if (Equals(current, value))
                        continue;
                    if (!Originals.TryGetValue(key, out var original) || !ReferenceEquals(original.Item1, instance))
                    {
                        Originals.Remove(key);
                        Originals.Add(key, Tuple.Create(instance, member, current));
                    }
                    Write(instance, member, value);
                    BattleLog.Write("[HOST-SETTINGS] applied " + key + "=" + text);
                }
                }
                catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
                { Report(binding.Id, exception); }
            }
        }

        internal static void Restore()
        {
            foreach (var original in Originals.Values)
            {
                try { Write(original.Item1, original.Item2, original.Item3); }
                catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
                { BattleLog.Write("[HOST-SETTINGS] local setting restore failed " + original.Item2.Name + " " + exception.Message); }
            }
            Originals.Clear();
            Failures.Clear();
        }
    }
}
