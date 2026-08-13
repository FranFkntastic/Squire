using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using Franthropy.Filtering.Completion;
using MarketMafioso.Squire;
using MarketMafioso.Windows.Squire;
using Newtonsoft.Json;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireCandidateFilterTests
{
    [Fact]
    public void BareTextAndNegationSearchItemLocationAndReasonAcrossMultipleMatches()
    {
        var filter = new SquireCandidateFilter();
        var rows = new[]
        {
            CreateCandidate("Darksteel Barbut", "ArmoryHead", true, true, 90),
            CreateCandidate("Darksteel Gauntlets", "ArmoryHands", false, false, 80),
            CreateCandidate("Mythril Barbut", "Inventory1", false, false, 70),
        };

        Assert.Equal(2, filter.Apply(rows, "darksteel").Length);
        Assert.Single(filter.Apply(rows, "not darksteel"));
        Assert.Equal(2, filter.Apply(rows, "armoury").Length);
        Assert.Equal(3, filter.Apply(rows, "trusted baseline").Length);
    }

    [Theory]
    [InlineData("location:armoury", 2)]
    [InlineData("quality:hq", 1)]
    [InlineData("is:hq", 1)]
    [InlineData("is:equipped", 1)]
    [InlineData("itemlevel>=80", 2)]
    [InlineData("route:desynth", 3)]
    public void StructuredFiltersBindToCandidateEvidence(string expression, int expected)
    {
        var filter = new SquireCandidateFilter();
        var rows = new[]
        {
            CreateCandidate("Darksteel Barbut", "ArmoryHead", true, true, 90),
            CreateCandidate("Darksteel Gauntlets", "ArmoryHands", false, false, 80),
            CreateCandidate("Mythril Barbut", "Inventory1", false, false, 70),
        };

        Assert.Equal(expected, filter.Apply(rows, expression).Length);
    }

    [Fact]
    public void InvalidEditKeepsTheLastValidResultSet()
    {
        var filter = new SquireCandidateFilter();
        var rows = new[]
        {
            CreateCandidate("Darksteel Barbut", "ArmoryHead", true, true, 90),
            CreateCandidate("Mythril Barbut", "Inventory1", false, false, 70),
        };

        Assert.Single(filter.Apply(rows, "quality:hq"));
        Assert.Single(filter.Apply(rows, "quality:"));
        Assert.Equal("quality:", filter.Expression);
        Assert.False(filter.IsValid);
        Assert.NotNull(filter.Error);

        Assert.Single(filter.Apply(rows, "quality:nq"));
        Assert.True(filter.IsValid);
        Assert.Null(filter.Error);
    }

    [Fact]
    public void CompletionOffersPredicateValuesAtTheCaret()
    {
        var completion = FilterCompletionService.Complete(
            SquireCandidateFilter.Context,
            new FilterCompletionRequest("squire-candidates", "is:h", 4));

        Assert.Contains(completion.Items, item => item.InsertionText.Equals("hq", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StructuredExpressionRoundTripsThroughCanonicalConfiguration()
    {
        var configuration = new SquireConfiguration { Search = "quality:hq itemlevel>=80" };

        var restored = JsonConvert.DeserializeObject<SquireConfiguration>(JsonConvert.SerializeObject(configuration));

        Assert.NotNull(restored);
        Assert.Equal(configuration.Search, restored.Search);
        var filter = new SquireCandidateFilter();
        Assert.Single(filter.Apply([CreateCandidate("Darksteel Barbut", "ArmoryHead", true, true, 90)], restored.Search));
        Assert.True(filter.IsValid);
    }

    private static SquireCandidate CreateCandidate(
        string name,
        string container,
        bool highQuality,
        bool equipped,
        uint itemLevel)
    {
        var scope = new CharacterScope(7, "Squire", 21);
        var itemId = (uint)(Math.Abs(HashCode.Combine(name, container)) + 1);
        var fingerprint = new EquipmentInstanceFingerprint(
            scope,
            container,
            4,
            itemId,
            highQuality,
            1,
            15000,
            0,
            null,
            [],
            null,
            []);
        var definition = new EquipmentItemDefinition(
            itemId,
            name,
            50,
            itemLevel,
            EquipmentSlot.Head,
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
            new EquipmentInstanceSnapshot(fingerprint, DateTimeOffset.UtcNow, equipped),
            definition,
            SquireAssessment.Candidate,
            SquireDisposition.Desynthesize,
            new HashSet<SquireDisposition> { SquireDisposition.Desynthesize },
            [new SquireReason("StrictlyWorseForAllUnlockedJobs", "A trusted baseline dominates this item.", SquireReasonSeverity.Information)],
            null,
            new SquireDuplicateStatus(1, 0, 0));
    }
}
