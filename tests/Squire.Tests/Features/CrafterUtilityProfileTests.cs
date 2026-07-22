using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class CrafterUtilityProfileTests
{
    [Fact]
    public void OrdinaryDominanceCapabilityRequiresBothMainStatsWithoutCpLoss()
    {
        var profile = Ordinary(new(4_000, 4_000, 600));

        var bothImproved = profile.Evaluate(new CrafterUtilityStats(4_001, 4_001, 600));
        var craftsmanshipOnly = profile.Evaluate(new CrafterUtilityStats(4_001, 4_000, 600));
        var cpSacrifice = profile.Evaluate(new CrafterUtilityStats(4_001, 4_001, 599));

        Assert.Contains(bothImproved.Thresholds, threshold => threshold.ThresholdId == "ordinary-balanced-stat-dominance" && threshold.Satisfied);
        Assert.DoesNotContain(craftsmanshipOnly.Thresholds, threshold => threshold.ThresholdId == "ordinary-balanced-stat-dominance" && threshold.Satisfied);
        Assert.DoesNotContain(cpSacrifice.Thresholds, threshold => threshold.ThresholdId == "ordinary-balanced-stat-dominance" && threshold.Satisfied);
    }

    [Fact]
    public void HigherCraftingStatsScoreHigherOnTheMonotonicEnvelope()
    {
        var profile = Ordinary(new(4_000, 4_000, 600));

        var better = profile.Evaluate(new CrafterUtilityStats(4_100, 4_100, 610));

        Assert.True(better.UtilityScore > profile.BaselineEvaluation.UtilityScore);
        Assert.Equal(UpgradeAssessment.ClearImprovement, better.Assessment);
    }

    [Fact]
    public void ConflictingCraftsmanshipAndControlTradeIsContextDependent()
    {
        var profile = Ordinary(new(4_000, 4_100, 600));

        var trade = profile.Evaluate(new CrafterUtilityStats(4_100, 4_000, 600));

        Assert.Equal(UpgradeAssessment.ContextDependent, trade.Assessment);
    }

    [Fact]
    public void AllEightCrafterJobsAreSupportedButGatherersAreNot()
    {
        foreach (var classJobId in CrafterUtilityProfile.CrafterClassJobIds)
        {
            var profile = new CrafterUtilityProfile(
                CrafterUtilityContextKind.OrdinaryCraftBenchmark,
                new(4_000, 4_000, 600),
                classJobId,
                85);
            Assert.DoesNotContain(profile.Definition.SupportedClassJobIds, id => id == classJobId && profile.Evaluate(new CrafterUtilityStats(4_001, 4_001, 600)).Assessment == UpgradeAssessment.Unsupported);
        }

        var gatherer = new CrafterUtilityProfile(
            CrafterUtilityContextKind.OrdinaryCraftBenchmark,
            new(4_000, 4_000, 600),
            16,
            85);
        Assert.Contains(gatherer.Definition.SupportedClassJobIds, id => id != 16);
        Assert.Equal(UpgradeAssessment.Unsupported, gatherer.Evaluate(new CrafterUtilityStats(4_001, 4_001, 600)).Assessment);
    }

    private static CrafterUtilityProfile Ordinary(CrafterUtilityStats baseline) =>
        new(CrafterUtilityContextKind.OrdinaryCraftBenchmark, baseline, CrafterUtilityProfile.BlacksmithClassJobId, 100);
}
