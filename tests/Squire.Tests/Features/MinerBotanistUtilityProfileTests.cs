using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class MinerBotanistUtilityProfileTests
{
    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(10u, 1u)]
    [InlineData(85u, 75u)]
    [InlineData(100u, 90u)]
    public void PlayerCatalogUsesBoundedTenLevelHorizon(uint characterLevel, uint expectedMinimum)
    {
        Assert.Equal(expectedMinimum, MinerBotanistAdvisorCatalog.MinimumEquipLevel(characterLevel));
    }

    [Fact]
    public void HqCrossingLegendaryYieldThresholdEarnsCapabilityAuthority()
    {
        var profile = Legendary(new(5_399, 5_200, 950));

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_200, 950));
        var authority = profile.AssessAuthority(candidate, additionalCostGil: 25_000);

        Assert.Equal(UpgradeAssessment.ClearImprovement, candidate.Assessment);
        Assert.True(authority.AdvisorMayConsider);
        Assert.Contains("node-yield-plus-one", authority.GainedCapabilityIds);
    }

    [Fact]
    public void PaidNonCrossingImprovementRemainsVisibleButCannotNominateItself()
    {
        var profile = Legendary(new(5_401, 5_200, 950));

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_500, 5_200, 950));
        var authority = profile.AssessAuthority(candidate, additionalCostGil: 25_000);

        Assert.True(candidate.UtilityScore > profile.BaselineEvaluation.UtilityScore);
        Assert.Equal(UpgradeAssessment.ClearImprovement, candidate.Assessment);
        Assert.False(authority.AdvisorMayConsider);
        Assert.Contains(authority.Reasons, reason => reason.Contains("no supported capability step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FreeMonotonicImprovementMayBeConsideredWithoutThresholdGain()
    {
        var profile = Legendary(new(5_401, 5_200, 950));

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_500, 5_200, 950));
        var authority = profile.AssessAuthority(candidate, additionalCostGil: 0);

        Assert.True(authority.AdvisorMayConsider);
        Assert.Empty(authority.GainedCapabilityIds);
    }

    [Fact]
    public void ConflictingGatheringAndPerceptionTradeAbstains()
    {
        var profile = Legendary(new(5_399, 5_600, 950));

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_599, 950));
        var authority = profile.AssessAuthority(candidate, additionalCostGil: 0);

        Assert.Equal(UpgradeAssessment.ContextDependent, candidate.Assessment);
        Assert.False(authority.AdvisorMayConsider);
    }

    [Fact]
    public void CollectableCompositeCapabilityRequiresBothStats()
    {
        var profile = Collectable(new(5_172, 5_172, 950));

        var gatheringOnly = profile.Evaluate(new MinerBotanistUtilityStats(5_173, 5_172, 950));
        var both = profile.Evaluate(new MinerBotanistUtilityStats(5_173, 5_173, 950));

        Assert.DoesNotContain(gatheringOnly.Thresholds, threshold => threshold.ThresholdId == "collectable-actions-i730" && threshold.Satisfied);
        Assert.Contains(both.Thresholds, threshold => threshold.ThresholdId == "collectable-actions-i730" && threshold.Satisfied);
        Assert.True(both.UtilityScore - gatheringOnly.UtilityScore > 25);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void EvidencePatchAndUnmodeledEffectsIndependentlyBlockAuthority(
        bool evidenceComplete,
        bool patchMatches,
        bool hasUnmodeledEffect)
    {
        var profile = Legendary(new(5_399, 5_200, 950));
        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_200, 950));

        var authority = profile.AssessAuthority(
            candidate,
            0,
            evidenceComplete,
            patchMatches,
            hasUnmodeledEffect);

        Assert.False(authority.AdvisorMayConsider);
    }

    [Fact]
    public void OrdinaryResourceContextDoesNotLeakLegendaryThresholdStep()
    {
        var profile = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
            new(5_399, 5_200, 950),
            MinerBotanistUtilityProfile.MinerClassJobId);

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_200, 950));
        var authority = profile.AssessAuthority(candidate, 0);

        Assert.InRange(candidate.UtilityScore - profile.BaselineEvaluation.UtilityScore, 0.001, 1);
        Assert.DoesNotContain(candidate.Thresholds, threshold => threshold.ThresholdId == "node-yield-plus-one");
        Assert.DoesNotContain(candidate.Thresholds, threshold => threshold.ThresholdId == "ordinary-balanced-stat-dominance" && threshold.Satisfied);
        Assert.Equal(UpgradeAssessment.ClearImprovement, candidate.Assessment);
        Assert.True(authority.AdvisorMayConsider);
    }

    [Fact]
    public void Level85BalancedOrdinaryUpgradeMayBeConsideredWithoutPretendingAtNodeBreakpoint()
    {
        var profile = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
            new(2_577, 2_577, 764),
            MinerBotanistUtilityProfile.MinerClassJobId,
            characterLevel: 85);

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(2_600, 2_600, 764));
        var authority = profile.AssessAuthority(candidate, 25_000);

        Assert.Equal(UpgradeAssessment.ClearImprovement, candidate.Assessment);
        Assert.True(authority.AdvisorMayConsider);
        Assert.Equal(["ordinary-balanced-stat-dominance"], authority.GainedCapabilityIds);
        Assert.Contains(candidate.Thresholds, threshold =>
            threshold.ThresholdId == "ordinary-balanced-stat-dominance" &&
            threshold.Rationale.Contains("not a claim", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TaskSpecificCapabilityCountsShareAStablePlotScale()
    {
        var stats = new MinerBotanistUtilityStats(5_700, 5_504, 995);
        var ordinary = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
            stats,
            MinerBotanistUtilityProfile.MinerClassJobId).Evaluate(stats);
        var legendary = Legendary(stats).Evaluate(stats);
        var collectable = Collectable(stats).Evaluate(stats);

        Assert.All([ordinary, legendary, collectable], evaluation => Assert.InRange(evaluation.UtilityScore, 0d, 100d));
        Assert.True(legendary.UtilityScore < ordinary.UtilityScore * 2d);
        Assert.True(collectable.UtilityScore < ordinary.UtilityScore * 2d);
    }

    [Fact]
    public void FixedNonOfferStatsParticipateInWholeLoadoutThresholds()
    {
        var profile = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
            new(399, 200, 50),
            MinerBotanistUtilityProfile.MinerClassJobId,
            fixedStats: new(5_000, 5_000, 900));

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(400, 200, 50));

        Assert.Contains(candidate.Thresholds, threshold => threshold.ThresholdId == "node-yield-plus-one" && threshold.Satisfied);
        Assert.Equal(5_400, candidate.RawStats.Single(stat => stat.Semantic == EquipmentStatSemantic.Gathering).Value);
    }

    [Fact]
    public void SupportedProfileCanGrantRuntimeAuthorityAfterEvidenceAndCapabilityChecksPass()
    {
        var profile = Legendary(new(5_399, 5_200, 950));
        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_200, 950));

        var authority = profile.AssessAuthority(candidate, additionalCostGil: 0);

        Assert.Equal(AdvisorProfileCalibrationState.Supported, MinerBotanistUtilityProfile.CalibrationState);
        Assert.True(authority.AdvisorMayConsider);
        Assert.Empty(authority.Reasons);
    }

    [Fact]
    public void CollectableContextIsExplicitlyBoundedToI730Mechanics()
    {
        var profile = Collectable(new(5_172, 5_172, 950));

        Assert.Equal("collectable-i730-efficiency", profile.BaselineEvaluation.Context.ContextId);
        Assert.Contains("i730", profile.BaselineEvaluation.Context.Scenario, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            profile.BaselineEvaluation.Diagnostics,
            diagnostic => diagnostic.Contains("1000-GP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FisherCannotUseSharedMinerBotanistProfile()
    {
        var profile = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
            new(5_399, 5_200, 950),
            classJobId: 18);

        var evaluation = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_200, 950));

        Assert.Equal(UpgradeAssessment.Unsupported, evaluation.Assessment);
        Assert.Contains(
            "Fisher is permanently unsupported and out of scope for Squire Outfitter.",
            evaluation.Diagnostics);
    }

    private static MinerBotanistUtilityProfile Legendary(MinerBotanistUtilityStats baseline) => new(
        MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
        baseline,
        MinerBotanistUtilityProfile.MinerClassJobId);

    private static MinerBotanistUtilityProfile Collectable(MinerBotanistUtilityStats baseline) => new(
        MinerBotanistUtilityContextKind.CollectableEfficiency,
        baseline,
        MinerBotanistUtilityProfile.BotanistClassJobId);
}
