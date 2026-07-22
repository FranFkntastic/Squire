using System.Text.Json;
using System.Text.Json.Serialization;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class CrafterOracleChallengeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void FrozenChallengeBookMatchesIndependentExpectedOutcomes()
    {
        var book = LoadBook();
        var failures = new List<string>();
        foreach (var challenge in book.Cases)
        {
            var profile = new CrafterUtilityProfile(
                challenge.Context,
                challenge.Baseline,
                challenge.ClassJobId,
                challenge.CharacterLevel);
            var candidate = profile.Evaluate(challenge.Candidate);
            var authority = profile.AssessAuthority(
                candidate,
                challenge.AdditionalCostGil,
                challenge.EvidenceComplete,
                challenge.PatchMatches,
                challenge.HasUnmodeledRelevantEffect);

            if (candidate.Assessment != challenge.ExpectedAssessment)
                failures.Add($"{challenge.CaseId}: assessment {candidate.Assessment}, expected {challenge.ExpectedAssessment}");
            if (authority.AdvisorMayConsider != challenge.ExpectedAdvisorMayConsider)
                failures.Add($"{challenge.CaseId}: authority {authority.AdvisorMayConsider}, expected {challenge.ExpectedAdvisorMayConsider}");
            if (!authority.GainedCapabilityIds.SequenceEqual(challenge.ExpectedGainedCapabilityIds, StringComparer.Ordinal))
                failures.Add($"{challenge.CaseId}: gained [{string.Join(',', authority.GainedCapabilityIds)}], expected [{string.Join(',', challenge.ExpectedGainedCapabilityIds)}]");
            if (challenge.MaximumUtilityDelta is { } maximum &&
                candidate.UtilityScore - profile.BaselineEvaluation.UtilityScore > maximum)
            {
                failures.Add($"{challenge.CaseId}: utility delta leaked above {maximum}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static ChallengeBook LoadBook()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Squire", "OutfitterUtility", "crafter-7.51-challenge-book.json");
        return JsonSerializer.Deserialize<ChallengeBook>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("Crafter challenge book was empty.");
    }

    private sealed record ChallengeBook(IReadOnlyList<ChallengeCase> Cases);

    private sealed record ChallengeCase(
        string CaseId,
        CrafterUtilityContextKind Context,
        uint ClassJobId,
        CrafterUtilityStats Baseline,
        CrafterUtilityStats Candidate,
        ulong AdditionalCostGil,
        UpgradeAssessment ExpectedAssessment,
        bool ExpectedAdvisorMayConsider,
        IReadOnlyList<string> ExpectedGainedCapabilityIds,
        uint CharacterLevel = 100,
        bool EvidenceComplete = true,
        bool PatchMatches = true,
        bool HasUnmodeledRelevantEffect = false,
        double? MaximumUtilityDelta = null);
}
