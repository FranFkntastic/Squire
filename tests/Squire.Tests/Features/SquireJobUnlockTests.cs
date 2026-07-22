using MarketMafioso.Squire.Observation;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireJobUnlockTests
{
    [Fact]
    public void HighQualityScalarBonus_IsAddedToBaseInsteadOfReplacingIt()
    {
        Assert.Equal(535, DalamudCharacterEquipmentSnapshotSource.ResolveScalar(481, 54, true, true, false));
        Assert.Equal(481, DalamudCharacterEquipmentSnapshotSource.ResolveScalar(481, 481, false, false, true));
    }

    [Theory]
    [InlineData(1, EquipmentRarity.Normal)]
    [InlineData(2, EquipmentRarity.Uncommon)]
    [InlineData(3, EquipmentRarity.Rare)]
    [InlineData(4, EquipmentRarity.Relic)]
    [InlineData(5, EquipmentRarity.Unknown)]
    public void RarityMapping_IsExplicit(byte raw, EquipmentRarity expected) =>
        Assert.Equal(expected, DalamudCharacterEquipmentSnapshotSource.MapRarity(raw));

    [Theory]
    [InlineData(1, ExpertDeliveryEligibility.Ineligible)]
    [InlineData(2, ExpertDeliveryEligibility.Eligible)]
    [InlineData(3, ExpertDeliveryEligibility.Eligible)]
    [InlineData(4, ExpertDeliveryEligibility.Eligible)]
    [InlineData(5, ExpertDeliveryEligibility.Unknown)]
    public void ExpertDeliveryEligibility_FollowsEquipmentRarityRule(byte raw, ExpertDeliveryEligibility expected) =>
        Assert.Equal(expected, DalamudCharacterEquipmentSnapshotSource.MapExpertDeliveryEligibility(raw));

    [Theory]
    [InlineData(1, EquipmentStatSemantic.Strength)]
    [InlineData(2, EquipmentStatSemantic.Dexterity)]
    [InlineData(3, EquipmentStatSemantic.Vitality)]
    [InlineData(4, EquipmentStatSemantic.Intelligence)]
    [InlineData(5, EquipmentStatSemantic.Mind)]
    [InlineData(6, EquipmentStatSemantic.Piety)]
    [InlineData(10, EquipmentStatSemantic.GatheringPoints)]
    [InlineData(11, EquipmentStatSemantic.CraftingPoints)]
    [InlineData(12, EquipmentStatSemantic.PhysicalDamage)]
    [InlineData(13, EquipmentStatSemantic.MagicalDamage)]
    [InlineData(19, EquipmentStatSemantic.Tenacity)]
    [InlineData(21, EquipmentStatSemantic.PhysicalDefense)]
    [InlineData(22, EquipmentStatSemantic.DirectHit)]
    [InlineData(24, EquipmentStatSemantic.MagicalDefense)]
    [InlineData(27, EquipmentStatSemantic.CriticalHit)]
    [InlineData(30, EquipmentStatSemantic.PiercingResistance)]
    [InlineData(44, EquipmentStatSemantic.Determination)]
    [InlineData(45, EquipmentStatSemantic.SkillSpeed)]
    [InlineData(46, EquipmentStatSemantic.SpellSpeed)]
    [InlineData(70, EquipmentStatSemantic.Craftsmanship)]
    [InlineData(71, EquipmentStatSemantic.Control)]
    [InlineData(72, EquipmentStatSemantic.Gathering)]
    [InlineData(73, EquipmentStatSemantic.Perception)]
    public void BaseParameterMapping_UsesStableRowIdsRegardlessOfLocalizedName(
        uint rowId,
        EquipmentStatSemantic expected)
    {
        Assert.Equal(expected, DalamudCharacterEquipmentSnapshotSource.MapStatSemantic(rowId, "Not the English name"));
        Assert.Equal(expected, DalamudCharacterEquipmentSnapshotSource.MapStatSemantic(rowId, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public void BaseParameterMapping_FailsClosedForUnsupportedRows(uint rowId) =>
        Assert.Equal(
            EquipmentStatSemantic.Unknown,
            DalamudCharacterEquipmentSnapshotSource.MapStatSemantic(rowId, "Strength"));

    [Theory]
    [InlineData(3, EquipmentStatSemantic.Dexterity, "Physical Ranged DPS")]
    [InlineData(3, EquipmentStatSemantic.Intelligence, "Magical Ranged DPS")]
    [InlineData(3, EquipmentStatSemantic.Unknown, "Ranged DPS")]
    public void AmbiguousRangedRole_UsesPrimaryStat(byte rawRole, EquipmentStatSemantic primaryStat, string expected) =>
        Assert.Equal(expected, DalamudCharacterEquipmentSnapshotSource.FormatRole(rawRole, primaryStat));
    [Fact]
    public void UpgradedJobRequiresItsSoulCrystalEvenWhenClassLevelIsShared()
    {
        Assert.False(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(50, 21, 3, 4549, new HashSet<uint>()));
        Assert.True(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(50, 21, 3, 4549, new HashSet<uint> { 4549 }));
    }

    [Fact]
    public void BaseClassUsesObservedLevel()
    {
        Assert.False(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(0, 3, 3, 0, new HashSet<uint>()));
        Assert.True(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(1, 3, 3, 0, new HashSet<uint>()));
    }

    [Fact]
    public void CraftingClassUsesLevelEvenWhenSheetHasSpecialistCrystal()
    {
        Assert.True(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(49, 11, 11, 9999, new HashSet<uint>()));
    }

    [Theory]
    [InlineData(2, EquipmentSlot.OffHand)]
    [InlineData(3, EquipmentSlot.Head)]
    [InlineData(4, EquipmentSlot.Body)]
    [InlineData(5, EquipmentSlot.Hands)]
    [InlineData(6, EquipmentSlot.Unknown)]
    [InlineData(7, EquipmentSlot.Legs)]
    public void EquipSlotCategoryMapping_MatchesGameSheet(uint rowId, EquipmentSlot expected)
    {
        Assert.Equal(expected, DalamudCharacterEquipmentSnapshotSource.MapEquipSlot(rowId));
    }
}
