using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Portfolio;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterPortfolioPlannerTests
{
    [Fact]
    public void SharedCraftMaterialListing_IsAllocatedToHigherPriorityTargetBeforeAuthority()
    {
        var sharedMaterial = new PortfolioAllocationKey(
            PortfolioAllocationSourceKind.MarketListing,
            "craft-material-market:Siren:listing-1:revision-1",
            400,
            false);
        var targets = new[]
        {
            new PortfolioTargetPriority("high", "High", 100, PortfolioProgressionHorizon.Immediate),
            new PortfolioTargetPriority("low", "Low", 50, PortfolioProgressionHorizon.NearTerm),
        };
        var candidates = new[]
        {
            Candidate("high", "high-craft", 10, sharedMaterial),
            Candidate("low", "low-craft", 50, sharedMaterial),
        };

        var plan = OutfitterPortfolioPlanner.Plan(
            targets,
            candidates,
            [new PortfolioAllocationCapacity(sharedMaterial, 1)]);

        Assert.Equal("high-craft", Assert.Single(plan.SelectedCandidates).CandidateKey);
        var consequence = Assert.Single(plan.AllocationConsequences);
        Assert.Equal("low", consequence.LosingTargetKey);
        Assert.Equal(sharedMaterial, consequence.ScarceAllocation);
        Assert.Equal(["high"], consequence.HigherPriorityWinnerTargetKeys);
    }
    [Fact]
    public void Plan_AllocatesScarceInstanceToHigherPriorityAndExplainsLowerPriorityLoss()
    {
        var scarce = Owned("instance-a", 100, highQuality: true);
        var plan = OutfitterPortfolioPlanner.Plan(
            [
                Target("player", priority: 100, PortfolioProgressionHorizon.Immediate),
                Target("retainer", priority: 40, PortfolioProgressionHorizon.NearTerm),
            ],
            [
                Candidate("player", "player-best", 10, scarce),
                Candidate("retainer", "retainer-best", 50, scarce),
            ],
            [new(scarce, 1)]);

        Assert.Equal("player-best", Assert.Single(plan.SelectedCandidates).CandidateKey);
        var consequence = Assert.Single(plan.AllocationConsequences);
        Assert.Equal("retainer", consequence.LosingTargetKey);
        Assert.Equal("retainer-best", consequence.LosingCandidateKey);
        Assert.Equal(scarce, consequence.ScarceAllocation);
        Assert.Equal((uint)1, consequence.AvailableQuantity);
        Assert.Equal((uint)2, consequence.RequiredQuantity);
        Assert.Equal(["player"], consequence.HigherPriorityWinnerTargetKeys);
        Assert.Contains("higher-priority", consequence.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_ChoosesAtMostOneCandidatePerTargetAndNeverExceedsListingCapacity()
    {
        var listing = Listing("listing-1", 200, highQuality: false);
        var plan = OutfitterPortfolioPlanner.Plan(
            [
                Target("a", 100, PortfolioProgressionHorizon.Immediate),
                Target("b", 90, PortfolioProgressionHorizon.Immediate),
            ],
            [
                Candidate("a", "a-expensive", 20, listing, quantity: 2),
                Candidate("a", "a-efficient", 10, listing),
                Candidate("b", "b-one", 10, listing),
            ],
            [new(listing, 2)]);

        var selected = Assert.Single(plan.SelectedCandidates);
        Assert.Equal("a-expensive", selected.CandidateKey);
        Assert.Equal((uint)2, selected.Demands.Single().Quantity);
        Assert.Null(plan.SelectionFor("b"));
        Assert.Equal(plan.SelectedCandidates.Count, plan.SelectedCandidates.Select(candidate => candidate.TargetKey).Distinct().Count());
    }

    [Fact]
    public void Plan_UsesProgressionHorizonAsExplicitPriorityTieBreaker()
    {
        var scarce = Owned("instance-a", 300, highQuality: false);
        var plan = OutfitterPortfolioPlanner.Plan(
            [
                Target("long", 50, PortfolioProgressionHorizon.LongTerm),
                Target("now", 50, PortfolioProgressionHorizon.Immediate),
            ],
            [
                Candidate("long", "long-best", 999, scarce),
                Candidate("now", "now-best", 1, scarce),
            ],
            [new(scarce, 1)]);

        Assert.Equal(["now", "long"], plan.OrderedTargets.Select(target => target.TargetKey));
        Assert.Equal("now-best", Assert.Single(plan.SelectedCandidates).CandidateKey);
    }

    [Fact]
    public void Plan_RejectsHandMeDownWithoutSelectedExplicitReleaseEvidence()
    {
        var handMeDown = HandMeDown("released-hat", 400, highQuality: true);
        var plan = OutfitterPortfolioPlanner.Plan(
            [Target("retainer", 10, PortfolioProgressionHorizon.NearTerm)],
            [Candidate("retainer", "use-hand-me-down", 10, handMeDown)],
            []);

        Assert.Empty(plan.SelectedCandidates);
    }

    [Fact]
    public void Plan_AllowsExactHandMeDownOnlyWhenHigherPriorityReplacementReleasesIt()
    {
        var released = Item(400, highQuality: true, "released-hat");
        var replacement = Item(401, highQuality: false, "new-hat");
        var newHatAllocation = Owned("new-hat", 401, highQuality: false);
        var handMeDown = HandMeDown("released-hat", 400, highQuality: true);
        var upstream = new PortfolioCandidate(
            "player",
            "replace-player-hat",
            10,
            [new(newHatAllocation, 1)],
            [new(EquipmentLoadoutPosition.Head, replacement)],
            [new("gear-evidence-7", EquipmentLoadoutPosition.Head, released, replacement, handMeDown)]);
        var downstream = Candidate("retainer", "wear-player-hat", 5, handMeDown);

        var plan = OutfitterPortfolioPlanner.Plan(
            [
                Target("player", 100, PortfolioProgressionHorizon.Immediate),
                Target("retainer", 10, PortfolioProgressionHorizon.NearTerm),
            ],
            [upstream, downstream],
            [new(newHatAllocation, 1)]);

        Assert.Equal(["replace-player-hat", "wear-player-hat"], plan.SelectedCandidates.Select(candidate => candidate.CandidateKey));
        Assert.Empty(plan.AllocationConsequences);
    }

    [Fact]
    public void Plan_RejectsHandMeDownWhoseQualityDoesNotMatchReleasedInstance()
    {
        var released = Item(400, highQuality: true, "released-hat");
        var replacement = Item(401, highQuality: false, "new-hat");
        var mismatched = HandMeDown("released-hat", 400, highQuality: false);
        var candidate = new PortfolioCandidate(
            "player",
            "bad-release",
            10,
            [],
            [new(EquipmentLoadoutPosition.Head, replacement)],
            [new("evidence", EquipmentLoadoutPosition.Head, released, replacement, mismatched)]);

        var error = Assert.Throws<ArgumentException>(() => OutfitterPortfolioPlanner.Plan(
            [Target("player", 100, PortfolioProgressionHorizon.Immediate)],
            [candidate],
            []));

        Assert.Contains("exact released item", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_FailsClosedWhenExactSearchExceedsDeclaredStateBudget()
    {
        var choices = Enumerable.Range(1, 12)
            .Select(index => new PortfolioCandidate("player", $"choice-{index}", 10, [], [], []))
            .ToArray();

        var plan = OutfitterPortfolioPlanner.Plan(
            [Target("player", 100, PortfolioProgressionHorizon.Immediate)],
            choices,
            [],
            maximumExploredStates: 3);

        Assert.False(plan.IsComplete);
        Assert.Empty(plan.SelectedCandidates);
        Assert.Contains("stopped", plan.Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    private static PortfolioTargetPriority Target(string key, int priority, PortfolioProgressionHorizon horizon) =>
        new(key, key, priority, horizon);

    private static PortfolioCandidate Candidate(
        string target,
        string candidate,
        long utility,
        PortfolioAllocationKey allocation,
        uint quantity = 1) =>
        new(target, candidate, utility, [new(allocation, quantity)], [], []);

    private static PortfolioAllocationKey Owned(string instance, uint itemId, bool highQuality) =>
        new(PortfolioAllocationSourceKind.OwnedInstance, $"owned:{instance}", itemId, highQuality, instance);

    private static PortfolioAllocationKey Listing(string listing, uint itemId, bool highQuality) =>
        new(PortfolioAllocationSourceKind.MarketListing, $"listing:{listing}", itemId, highQuality);

    private static PortfolioAllocationKey HandMeDown(string instance, uint itemId, bool highQuality) =>
        new(PortfolioAllocationSourceKind.HandMeDown, $"hand-me-down:{instance}", itemId, highQuality, instance);

    private static PortfolioExactItem Item(uint itemId, bool highQuality, string instance) =>
        new(itemId, highQuality, instance);
}
