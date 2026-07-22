using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;
using MarketMafioso.Windows.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireCandidateTableProjectionTests
{
    [Theory]
    [InlineData(EquipmentRarity.Normal, "Common (white)")]
    [InlineData(EquipmentRarity.Uncommon, "Uncommon (green)")]
    [InlineData(EquipmentRarity.Rare, "Rare (blue)")]
    [InlineData(EquipmentRarity.Relic, "Relic (purple)")]
    [InlineData(EquipmentRarity.Unknown, "Unknown")]
    public void FormatRarity_UsesPlayerFacingLabels(EquipmentRarity rarity, string expected) =>
        Assert.Equal(expected, SquireCandidateTableProjection.FormatRarity(rarity));

    [Fact]
    public void Filter_MapsOptionalEquipmentFactsToTheirColumns()
    {
        var candidate = CreateCandidate();
        var filters = new string[SquireCandidateTableProjection.ColumnCount];
        filters[4] = "blue";
        filters[5] = "HQ";
        filters[6] = "3 owned";
        filters[7] = "2";
        filters[8] = "50%";
        filters[14] = "12345";

        Assert.Single(SquireCandidateTableProjection.Filter([candidate], filters, _ => "Cleanup batch"));

        filters[4] = "purple";
        Assert.Empty(SquireCandidateTableProjection.Filter([candidate], filters, _ => "Cleanup batch"));
    }

    private static SquireCandidate CreateCandidate()
    {
        var scope = new CharacterScope(7, "Squire", 21);
        var fingerprint = new EquipmentInstanceFingerprint(
            scope,
            "Inventory1",
            4,
            12345,
            true,
            1,
            15000,
            0,
            null,
            [100, 101],
            null,
            []);
        var definition = new EquipmentItemDefinition(
            12345,
            "Test Casting Ring",
            50,
            130,
            EquipmentSlot.Ring,
            new HashSet<uint> { 25 },
            3,
            true,
            false,
            true,
            true,
            1,
            true,
            false,
            true,
            false,
            NormalizedRarity: EquipmentRarity.Rare,
            ClassJobCategoryName: "All Classes");
        return new SquireCandidate(
            new EquipmentInstanceSnapshot(fingerprint, DateTimeOffset.UtcNow, false),
            definition,
            SquireAssessment.Candidate,
            SquireDisposition.Desynthesize,
            new HashSet<SquireDisposition> { SquireDisposition.Desynthesize },
            [new SquireReason("StrictlyWorseForAllUnlockedJobs", "A trusted baseline dominates this item.", SquireReasonSeverity.Information)],
            null,
            new SquireDuplicateStatus(3, 2, 1));
    }
}
