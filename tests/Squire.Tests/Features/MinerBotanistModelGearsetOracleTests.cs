using System.Text.Json;
using System.Text.Json.Serialization;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class MinerBotanistModelGearsetOracleTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void PublishedModelFamilyAndAdversarialVariantsMatchEveryExpectedDecision()
    {
        var oracle = LoadOracle();
        var loadouts = oracle.Loadouts.ToDictionary(loadout => loadout.LoadoutId, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var comparison in oracle.Comparisons)
        {
            var baseline = loadouts[comparison.BaselineLoadoutId];
            var candidateLoadout = loadouts[comparison.CandidateLoadoutId];
            var profile = new MinerBotanistUtilityProfile(
                comparison.Context,
                baseline.Stats,
                MinerBotanistUtilityProfile.MinerClassJobId);
            var candidate = profile.Evaluate(candidateLoadout.Stats);
            var authority = profile.AssessAuthority(
                candidate,
                comparison.CandidateHasAdditionalAcquisitionCost ? 1UL : 0UL,
                hasUnmodeledRelevantEffect: comparison.HasUnmodeledRelevantEffect);

            if (candidate.Assessment != comparison.ExpectedAssessment)
                failures.Add($"{comparison.CaseId}: assessment {candidate.Assessment}, expected {comparison.ExpectedAssessment}");
            if (authority.AdvisorMayConsider != comparison.ExpectedAdvisorMayConsider)
                failures.Add($"{comparison.CaseId}: authority {authority.AdvisorMayConsider}, expected {comparison.ExpectedAdvisorMayConsider}");
            if (!authority.GainedCapabilityIds.SequenceEqual(comparison.ExpectedGainedCapabilityIds, StringComparer.Ordinal))
            {
                failures.Add(
                    $"{comparison.CaseId}: gained [{string.Join(',', authority.GainedCapabilityIds)}], " +
                    $"expected [{string.Join(',', comparison.ExpectedGainedCapabilityIds)}]");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void PublishedCostUtilityNeighborhoodSurvivesWhileDerivedNoiseIsDominated()
    {
        var oracle = LoadOracle();
        var profile = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
            new(0, 0, 0),
            MinerBotanistUtilityProfile.MinerClassJobId);
        var evaluated = oracle.Loadouts
            .Select(loadout => new EvaluatedLoadout(
                loadout.LoadoutId,
                loadout.AcquisitionBurdenRank,
                profile.Evaluate(loadout.Stats).UtilityScore))
            .ToArray();

        var frontier = evaluated
            .Where(candidate => !evaluated.Any(other =>
                other.LoadoutId != candidate.LoadoutId &&
                other.AcquisitionBurdenRank <= candidate.AcquisitionBurdenRank &&
                other.UtilityScore >= candidate.UtilityScore &&
                (other.AcquisitionBurdenRank < candidate.AcquisitionBurdenRank ||
                    other.UtilityScore > candidate.UtilityScore)))
            .Select(candidate => candidate.LoadoutId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(oracle.ExpectedLegendaryFrontierLoadoutIds.Order(StringComparer.Ordinal), frontier);
        Assert.DoesNotContain("derived-high-regression", frontier);
        Assert.DoesNotContain("derived-high-cost-only", frontier);
    }

    private static ModelGearsetOracle LoadOracle()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Squire",
            "OutfitterUtility",
            "min-btn-7.51-model-gearset-oracle.json");
        return JsonSerializer.Deserialize<ModelGearsetOracle>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("MIN/BTN model-gearset oracle was empty.");
    }

    private sealed record ModelGearsetOracle(
        IReadOnlyList<OracleLoadout> Loadouts,
        IReadOnlyList<OracleComparison> Comparisons,
        IReadOnlyList<string> ExpectedLegendaryFrontierLoadoutIds);

    private sealed record OracleLoadout(
        string LoadoutId,
        string Label,
        string Origin,
        int AcquisitionBurdenRank,
        MinerBotanistUtilityStats Stats,
        IReadOnlyList<string> Assumptions,
        string? SourceId = null,
        string? DerivedFromLoadoutId = null);

    private sealed record OracleComparison(
        string CaseId,
        MinerBotanistUtilityContextKind Context,
        string BaselineLoadoutId,
        string CandidateLoadoutId,
        bool CandidateHasAdditionalAcquisitionCost,
        UpgradeAssessment ExpectedAssessment,
        bool ExpectedAdvisorMayConsider,
        IReadOnlyList<string> ExpectedGainedCapabilityIds,
        bool HasUnmodeledRelevantEffect = false);

    private sealed record EvaluatedLoadout(
        string LoadoutId,
        int AcquisitionBurdenRank,
        double UtilityScore);
}
