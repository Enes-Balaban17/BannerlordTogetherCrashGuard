// Adapted from CompanionManagementSuite, Copyright (c) 2026 Elias Elsner, MIT. See licenses/CompanionManagementSuite-MIT.txt.
namespace BannerlordTogetherCrashGuard.CompanionEquipment
{
    /// <summary>
    /// Game-free classification of an equippable item, mapped from vanilla ItemObject.ItemType by the
    /// integration layer. Drives which slot(s) an item can fill and which archetype wants it.
    /// </summary>
    public enum ItemCategory
    {
        Other = 0,
        HeadArmor,
        BodyArmor,
        LegArmor,
        HandArmor,
        Cape,
        Mount,
        MountHarness,
        OneHandedWeapon,
        TwoHandedWeapon,
        Polearm,
        Bow,
        Crossbow,
        Thrown,
        Shield,
        Arrows,
        Bolts,
    }
}
