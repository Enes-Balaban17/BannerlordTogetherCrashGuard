using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.ObjectSystem;

namespace BannerlordTogetherCrashGuard
{
    internal static class GhostEncyclopediaCompatibility
    {
        internal static readonly HashSet<string> Retired = new HashSet<string>(StringComparer.Ordinal);

        internal static void Install(Harmony harmony)
        {
            var method = AccessTools.Method(typeof(GhostPartyController), "Despawn");
            if (Harmony.GetPatchInfo(method)?.Owners.Contains(SubModule.Owner) == true)
                return;
            harmony.Patch(method, new HarmonyMethod(typeof(GhostEncyclopediaCompatibility), nameof(BeforeDespawn)),
                new HarmonyMethod(typeof(GhostEncyclopediaCompatibility), nameof(AfterDespawn)));
            harmony.Patch(AccessTools.Method(typeof(GhostPartyController), "Spawn"),
                postfix: new HarmonyMethod(typeof(GhostEncyclopediaCompatibility), nameof(AfterSpawn)));
        }

        private static void AfterSpawn(GhostPartyController __instance)
        {
            Hero hero = __instance.GhostHero;
            if (hero != null && hero != Hero.MainHero)
                Retired.Add(hero.StringId);
        }

        private static void BeforeDespawn(GhostPartyController __instance, out Hero __state)
        {
            __state = __instance.GhostHero;
        }

        private static void AfterDespawn(Hero __state)
        {
            if (__state == null || __state == Hero.MainHero || !__state.IsDead)
                return;
            Retired.Add(__state.StringId);
            Hide(__state);
        }

        private static void Hide(Hero hero)
        {
            if (!hero.HiddenInEncyclopedia)
            {
                hero.HiddenInEncyclopedia = true;
                BattleLog.Write("[ENCYCLOPEDIA] hid retired co-op projection " + hero.StringId);
            }
        }

        internal static void RestoreHiddenEntries()
        {
            foreach (string id in Retired)
            {
                Hero hero = MBObjectManager.Instance.GetObject<Hero>(id);
                if (hero != null && hero != Hero.MainHero && hero.IsDead)
                    Hide(hero);
            }
        }

        internal static void ImportLegacyLogEvidence()
        {
            string folder = Environment.GetEnvironmentVariable("BT_LOG_DIR");
            if (string.IsNullOrWhiteSpace(folder))
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "BannerlordTogether", "logs");
            if (!Directory.Exists(folder))
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BannerlordTogether", "logs");
            if (!Directory.Exists(folder))
                return;
            var expression = new Regex(@"\[Ghost\] Despawn start party=bt_ghost_\S+ leader=(\S+)");
            foreach (string file in Directory.EnumerateFiles(folder, "bt-sync-*.txt")
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path)).Take(4))
            {
                // Logs identify the generated hero directly. Personal names never select heroes.
                try
                {
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        bool partialLine = stream.Length > 4 * 1024 * 1024;
                        stream.Position = Math.Max(0, stream.Length - 4 * 1024 * 1024);
                        using (var reader = new StreamReader(stream))
                        {
                            if (partialLine)
                                reader.ReadLine();
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                Match match = expression.Match(line);
                                if (match.Success)
                                    Retired.Add(match.Groups[1].Value);
                            }
                        }
                    }
                }
                catch (IOException exception) { BattleLog.Write("[ENCYCLOPEDIA] legacy log unavailable " + exception.Message); }
                catch (UnauthorizedAccessException exception) { BattleLog.Write("[ENCYCLOPEDIA] legacy log unavailable " + exception.Message); }
            }
            RestoreHiddenEntries();
        }
    }
}
