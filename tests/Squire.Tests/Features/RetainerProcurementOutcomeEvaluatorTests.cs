using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RetainerProcurementOutcomeEvaluatorTests
{
    [Fact]
    public void Battle_profile_uses_item_level_and_ignores_hq_stat_increases()
    {
        var objective = Objective(RetainerProcurementProfileKind.Battle, eligibility: 690, (690, 10), (715, 15));
        var nq = new RetainerProcurementStats(700, Gathering: 0, Perception: 0, GatheringPoints: 0);
        var hq = nq with { Gathering = 5000, Perception = 5000, GatheringPoints = 999 };

        var result = RetainerProcurementOutcomeEvaluator.CompareQuality(objective, nq, hq);

        Assert.False(result.HighQualityChangesOutcome);
        Assert.Equal(10, result.NormalQuality.Quantity);
        Assert.Equal(result.NormalQuality, result.HighQuality);
    }

    [Fact]
    public void Gathering_profile_uses_gathering_for_eligibility()
    {
        var objective = Objective(RetainerProcurementProfileKind.Gathering, eligibility: 4300, (0, 10), (4900, 20));

        var result = RetainerProcurementOutcomeEvaluator.Evaluate(
            objective,
            new RetainerProcurementStats(AverageItemLevel: 999, Gathering: 4299, Perception: 6000, GatheringPoints: 999));

        Assert.Equal(RetainerProcurementOutcomeStatus.Ineligible, result.Status);
        Assert.False(result.IsEligible);
        Assert.Equal(0, result.Quantity);
    }

    [Fact]
    public void Gathering_hq_matters_only_when_it_crosses_a_supported_yield_threshold()
    {
        var objective = Objective(RetainerProcurementProfileKind.Gathering, eligibility: 4300, (0, 10), (4900, 20), (5200, 30));
        var nq = new RetainerProcurementStats(0, Gathering: 4500, Perception: 4890, GatheringPoints: 600);
        var hq = nq with { Perception = 4910 };

        var result = RetainerProcurementOutcomeEvaluator.CompareQuality(objective, nq, hq);

        Assert.True(result.HighQualityChangesOutcome);
        Assert.Equal(10, result.NormalQuality.Quantity);
        Assert.Equal(20, result.HighQuality.Quantity);
    }

    [Fact]
    public void Gathering_hq_does_not_matter_when_both_sets_remain_in_the_same_tier()
    {
        var objective = Objective(RetainerProcurementProfileKind.Gathering, eligibility: 4300, (0, 10), (4900, 20));
        var nq = new RetainerProcurementStats(0, Gathering: 4500, Perception: 4600, GatheringPoints: 600);
        var hq = nq with { Gathering = 4700, Perception = 4800, GatheringPoints = 700 };

        var result = RetainerProcurementOutcomeEvaluator.CompareQuality(objective, nq, hq);

        Assert.False(result.HighQualityChangesOutcome);
        Assert.Equal(10, result.NormalQuality.Quantity);
        Assert.Equal(10, result.HighQuality.Quantity);
    }

    [Fact]
    public void Gathering_points_never_change_a_supported_procurement_outcome()
    {
        var objective = Objective(RetainerProcurementProfileKind.Gathering, eligibility: 4300, (0, 10), (4900, 20));
        var lowGp = new RetainerProcurementStats(0, Gathering: 4500, Perception: 4950, GatheringPoints: 0);
        var highGp = lowGp with { GatheringPoints = 9999 };

        var result = RetainerProcurementOutcomeEvaluator.CompareQuality(objective, lowGp, highGp);

        Assert.False(result.HighQualityChangesOutcome);
        Assert.Equal(result.NormalQuality, result.HighQuality);
    }

    [Fact]
    public void Evaluator_abstains_from_inconsistent_threshold_evidence()
    {
        var objective = Objective(RetainerProcurementProfileKind.Gathering, eligibility: 1, (100, 10), (100, 20));

        var result = RetainerProcurementOutcomeEvaluator.Evaluate(objective, new(0, 100, 100, 0));

        Assert.Equal(RetainerProcurementOutcomeStatus.InvalidEvidence, result.Status);
    }

    [Fact]
    public void Evaluator_refuses_thresholds_not_completed_by_rendered_ui()
    {
        var objective = Objective(RetainerProcurementProfileKind.Battle, eligibility: 1, (0, 10)) with
        {
            IsRenderedUiComplete = false,
        };

        var result = RetainerProcurementOutcomeEvaluator.Evaluate(objective, new(999, 999, 999, 999));

        Assert.Equal(RetainerProcurementOutcomeStatus.InvalidEvidence, result.Status);
    }

    private static RetainerProcurementObjective Objective(
        RetainerProcurementProfileKind profile,
        int eligibility,
        params (int Required, int Quantity)[] tiers) =>
        new(
            "synthetic-targeted-venture",
            profile,
            eligibility,
            tiers.Select(value => new RetainerYieldThreshold(value.Required, value.Quantity)).ToArray(),
            Guid.Parse("105cdb55-d6e4-4fa8-967d-5d99b637ec71"),
            new DateTimeOffset(2026, 7, 18, 5, 0, 0, TimeSpan.Zero),
            IsRenderedUiComplete: true);
}
