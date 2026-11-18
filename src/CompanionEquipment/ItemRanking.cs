// Adapted from CompanionManagementSuite, Copyright (c) 2026 Elias Elsner, MIT. See licenses/CompanionManagementSuite-MIT.txt.
namespace BannerlordTogetherCrashGuard.CompanionEquipment
{
    /// <summary>
    /// The single rule for "is this item the better pick". Shared by the armour and weapon selectors so
    /// the two can never rank the same pair differently.
    /// </summary>
    public static class ItemRanking
    {
        /// <summary>
        /// Effectiveness decides, tier breaks a tie, and the item id breaks the remainder - the last
        /// step exists only so repeated passes over the same inventory always reach the same answer.
        /// </summary>
        public static bool IsBetter(ItemView candidate, ItemView current)
        {
            if (candidate.Effectiveness != current.Effectiveness)
            {
                return candidate.Effectiveness > current.Effectiveness;
            }

            if (candidate.Tier != current.Tier)
            {
                return candidate.Tier > current.Tier;
            }

            return string.CompareOrdinal(candidate.ItemId, current.ItemId) < 0;
        }
    }
}
