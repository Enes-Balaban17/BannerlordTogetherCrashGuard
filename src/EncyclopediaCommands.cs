using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace BannerlordTogetherCrashGuard
{
    public static class EncyclopediaCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("find_dead_heroes", "btcg")]
        public static string FindDeadHeroes(List<string> args)
        {
            if (Campaign.Current == null || args == null || args.Count == 0)
                return "Usage: btcg.find_dead_heroes <character name>";
            string name = string.Join(" ", args);
            var heroes = Hero.DeadOrDisabledHeroes.Where(hero => hero.IsDead &&
                hero.Name.ToString().IndexOf(name, StringComparison.CurrentCultureIgnoreCase) >= 0);
            return string.Join(Environment.NewLine, heroes.Select(hero => hero.StringId + " | " + hero.Name));
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("hide_dead_heroes", "btcg")]
        public static string HideDeadHeroes(List<string> args)
        {
            if (Campaign.Current == null || args == null || args.Count == 0)
                return "Usage: btcg.hide_dead_heroes <exact hero id> [more ids]";
            int count = 0;
            foreach (string id in args)
            {
                Hero hero = MBObjectManager.Instance.GetObject<Hero>(id);
                if (hero == null || hero == Hero.MainHero || !hero.IsDead)
                    continue;
                GhostEncyclopediaCompatibility.Retired.Add(id);
                hero.HiddenInEncyclopedia = true;
                count++;
            }
            return "Hidden encyclopedia entries: " + count;
        }
    }
}
