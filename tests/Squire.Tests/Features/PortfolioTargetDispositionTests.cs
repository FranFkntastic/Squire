using MarketMafioso.Squire.Outfitter.Portfolio;

namespace MarketMafioso.Tests.Squire;

public sealed class PortfolioTargetDispositionTests
{
    [Fact]
    public void MissingHigherPriorityEvidence_BlocksOtherwiseExecutableLowerPriorityPlan()
    {
        var lower = Candidate("lower", "lower-upgrade");
        var raw = OutfitterPortfolioPlanner.Plan(
            [
                new("higher", "Higher", 100, PortfolioProgressionHorizon.Immediate),
                new("lower", "Lower", 10, PortfolioProgressionHorizon.NearTerm),
            ],
            [lower],
            [new(lower.Demands.Single().Allocation, 1)]);

        var composed = PortfolioTargetDispositionResolver.Apply(
            raw,
            new Dictionary<string, (string, string?, PortfolioTargetDispositionKind)>
            {
                ["lower"] = ("lower-evidence-7", null, PortfolioTargetDispositionKind.TerminalNoUpgrade),
            },
            new Dictionary<string, string>
            {
                ["higher"] = "Saved gearset evidence is not ready.",
            });

        Assert.Equal("lower-upgrade", Assert.Single(composed.SelectedCandidates).CandidateKey);
        Assert.False(composed.IsComplete);
        Assert.False(composed.HasExecutableTargetDispositions);
        var higher = composed.DispositionFor("higher");
        Assert.Equal(PortfolioTargetDispositionKind.Incomplete, higher!.Kind);
        Assert.Contains("not ready", higher.Reason, StringComparison.OrdinalIgnoreCase);
        var error = Assert.Throws<InvalidOperationException>(() =>
            PortfolioAuthorityEnvelopeFactory.Create(Lineage(), composed));
        Assert.Contains("Incomplete portfolio", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactTerminalNoUpgrade_PermitsCompletePortfolioAndIsFingerprinted()
    {
        var lower = Candidate("lower", "lower-upgrade");
        var raw = OutfitterPortfolioPlanner.Plan(
            [
                new("higher", "Higher", 100, PortfolioProgressionHorizon.Immediate),
                new("lower", "Lower", 10, PortfolioProgressionHorizon.NearTerm),
            ],
            [lower],
            [new(lower.Demands.Single().Allocation, 1)]);
        var composed = PortfolioTargetDispositionResolver.Apply(
            raw,
            new Dictionary<string, (string, string?, PortfolioTargetDispositionKind)>
            {
                ["higher"] = ("higher-evidence-4", "No supported upgrade improves this target.", PortfolioTargetDispositionKind.TerminalNoUpgrade),
                ["lower"] = ("lower-evidence-7", null, PortfolioTargetDispositionKind.TerminalNoUpgrade),
            },
            new Dictionary<string, string>());

        var authority = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), composed);

        Assert.True(composed.IsComplete);
        Assert.True(composed.HasExecutableTargetDispositions);
        var higher = authority.Targets.Single(value => value.TargetKey == "higher");
        Assert.Equal(PortfolioTargetDispositionKind.TerminalNoUpgrade, higher.Disposition);
        Assert.Equal("higher-evidence-4", higher.EvidenceGeneration);
        Assert.Null(higher.SelectedCandidateKey);
        Assert.True(authority.HasValidFingerprint());
    }

    [Fact]
    public void ExactTerminalAbstention_IsDistinctAndEvidenceOrReasonChangesFingerprint()
    {
        var raw = new OutfitterPortfolioPlan(
            [new("fisher", "Fisher", 100, PortfolioProgressionHorizon.LongTerm)],
            [],
            []);
        var first = Terminal(raw, "fisher-evidence-1", "Fisher is outside modeled scope.");
        var second = Terminal(raw, "fisher-evidence-2", "Fisher is outside modeled scope.");
        var third = Terminal(raw, "fisher-evidence-1", "No calibrated Fisher objective exists.");

        var firstAuthority = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), first);
        var secondAuthority = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), second);
        var thirdAuthority = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), third);

        Assert.Equal(PortfolioTargetDispositionKind.TerminalAbstention, Assert.Single(firstAuthority.Targets).Disposition);
        Assert.NotEqual(firstAuthority.Fingerprint, secondAuthority.Fingerprint);
        Assert.NotEqual(firstAuthority.Fingerprint, thirdAuthority.Fingerprint);
    }

    [Fact]
    public void MissingDispositionCannotBeSmuggledIntoAuthorityByCompletePlanFlag()
    {
        var candidate = Candidate("target", "upgrade");
        var raw = OutfitterPortfolioPlanner.Plan(
            [new("target", "Target", 1, PortfolioProgressionHorizon.Immediate)],
            [candidate],
            [new(candidate.Demands.Single().Allocation, 1)]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            PortfolioAuthorityEnvelopeFactory.Create(Lineage(), raw));

        Assert.Contains("disposition", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static OutfitterPortfolioPlan Terminal(OutfitterPortfolioPlan raw, string generation, string reason) =>
        PortfolioTargetDispositionResolver.Apply(
            raw,
            new Dictionary<string, (string, string?, PortfolioTargetDispositionKind)>
            {
                ["fisher"] = (generation, reason, PortfolioTargetDispositionKind.TerminalAbstention),
            },
            new Dictionary<string, string>());

    private static PortfolioCandidate Candidate(string target, string key)
    {
        var allocation = new PortfolioAllocationKey(
            PortfolioAllocationSourceKind.OwnedInstance,
            $"owned:{target}",
            100,
            false,
            $"instance:{target}");
        return new(target, key, 10, [new(allocation, 1)], [], []);
    }

    private static PortfolioAuthorityLineage Lineage() =>
        new("owner", "owner-evidence", "inventory-evidence", "listing-evidence", []);
}
