// Adapted from CompanionManagementSuite, Copyright (c) 2026 Elias Elsner, MIT. See licenses/CompanionManagementSuite-MIT.txt.
namespace BannerlordTogetherCrashGuard.CompanionEquipment
{
    /// <summary>
    /// Game-free descriptor of a candidate item from the party inventory. The integration layer builds
    /// these from vanilla ItemObject; the selectors reason purely over this so they stay unit-testable.
    /// </summary>
    public sealed class ItemView
    {
        public ItemView(
            string itemId,
            ItemCategory category,
            float effectiveness,
            int tier,
            string requiredSkillId,
            int difficulty)
        {
            ItemId = itemId;
            Category = category;
            Effectiveness = effectiveness;
            Tier = tier;
            RequiredSkillId = requiredSkillId;
            Difficulty = difficulty;
        }

        /// <summary>Stable identifier of the underlying item (vanilla ItemObject.StringId).</summary>
        public string ItemId { get; }

        public ItemCategory Category { get; }

        /// <summary>Vanilla ItemObject.Effectiveness - the universal "how good" score used for ranking.</summary>
        public float Effectiveness { get; }

        /// <summary>Item tier (0-5), used only as a tie-break behind <see cref="Effectiveness"/>.</summary>
        public int Tier { get; }

        /// <summary>
        /// Skill StringId the wielder needs, or null/empty if none. Matches <c>IHeroStats.GetSkill</c> keys.
        /// </summary>
        public string RequiredSkillId { get; }

        /// <summary>Minimum skill value required to use the item (vanilla ItemObject.Difficulty).</summary>
        public int Difficulty { get; }
    }
}
