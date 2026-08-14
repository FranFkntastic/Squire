using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Portfolio;

namespace MarketMafioso.Tests.Squire;

public sealed class PortfolioAllocationPresentationTests
{
    private const string OpaqueKey = "vendor-v2:ZXhhY3Qtb3BhcXVlLWJhc2U2NC1hdXRob3JpdHktZmluZ2VycHJpbnQ";

    [Fact]
    public void ZeroChanges_IsOneCompactTruthfulSummary()
    {
        var result = PortfolioAllocationPresentationResolver.Resolve(
            new("target", "candidate", 1, [], [], []),
            new Dictionary<EquipmentLoadoutPosition, PortfolioExactItem?>(),
            ItemName);

        Assert.Equal("No slot changes", result.ChangeSummary);
        Assert.Equal("Current gear retained", result.ItemPreview);
        Assert.Equal("No item allocation required", result.AllocationSummary);
        Assert.Null(result.HandMeDownSummary);
    }

    [Fact]
    public void OneChange_ShowsOneFriendlyItemWithoutAuthorityKeys()
    {
        var result = PortfolioAllocationPresentationResolver.Resolve(
            Candidate(1, [Demand(PortfolioAllocationSourceKind.MarketListing, 1)]),
            new Dictionary<EquipmentLoadoutPosition, PortfolioExactItem?>(),
            ItemName);

        Assert.Equal("1 slot changes", result.ChangeSummary);
        Assert.Contains("Item 100", result.ItemPreview);
        Assert.Equal("Market 1", result.AllocationSummary);
        Assert.DoesNotContain(OpaqueKey, Visible(result), StringComparison.Ordinal);
    }

    [Fact]
    public void TwelveChanges_AreBoundedToTwoItemPreviewsAndFriendlySourceCounts()
    {
        var handDown = new PortfolioAllocationKey(
            PortfolioAllocationSourceKind.HandMeDown, OpaqueKey, 500, false, "instance-secret");
        var candidate = Candidate(
            12,
            [
                Demand(PortfolioAllocationSourceKind.OwnedInstance, 5),
                Demand(PortfolioAllocationSourceKind.MarketListing, 2),
                Demand(PortfolioAllocationSourceKind.GilVendor, 1),
                Demand(PortfolioAllocationSourceKind.Craft, 3),
                new(handDown, 1),
            ]) with
        {
            HandMeDowns =
            [
                new("generation", EquipmentLoadoutPosition.Head,
                    new(500, false, "instance-secret"), new(100, false, "replacement"), handDown),
            ],
        };

        var result = PortfolioAllocationPresentationResolver.Resolve(
            candidate,
            new Dictionary<EquipmentLoadoutPosition, PortfolioExactItem?>(),
            ItemName,
            new Dictionary<PortfolioAllocationKey, IReadOnlyList<string>>
            {
                [handDown] = ["Gatherer retainer"],
            });
        var visible = Visible(result);

        Assert.Equal("12 slots change", result.ChangeSummary);
        Assert.Contains("+10 more", result.ItemPreview);
        Assert.Equal("Owned 5 / Hand-me-down 1 / Market 2 / Vendor 1 / Craft 3", result.AllocationSummary);
        Assert.Equal("1 hand-me-down -> Gatherer retainer", result.HandMeDownSummary);
        Assert.DoesNotContain(OpaqueKey, visible, StringComparison.Ordinal);
        Assert.DoesNotContain("instance-secret", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("vendor-v2", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void FullTwelveSlotLoadout_CountsOnlyOneExactChangedReplacement()
    {
        var candidate = Candidate(12, [Demand(PortfolioAllocationSourceKind.OwnedInstance, 1)]);
        var baseline = candidate.SelectedReplacements.ToDictionary(
            value => value.Position,
            value => (PortfolioExactItem?)value.Item);
        var changed = candidate.SelectedReplacements[7];
        baseline[changed.Position] = changed.Item with { InstanceId = "different-equipped-instance" };

        var result = PortfolioAllocationPresentationResolver.Resolve(candidate, baseline, ItemName);

        Assert.Equal("1 slot changes", result.ChangeSummary);
        Assert.Contains($"{changed.Position}: Item {changed.Item.ItemId}", result.ItemPreview);
        Assert.DoesNotContain("+10 more", result.ItemPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedConsequence_UsesFriendlyLabelsAndNeverOpaquePlannerLineage()
    {
        var consequence = new PortfolioAllocationConsequence(
            "loser-key",
            "candidate:" + OpaqueKey,
            new(PortfolioAllocationSourceKind.MarketListing, OpaqueKey, 100, false),
            1,
            2,
            ["winner-key"],
            "Allocation " + OpaqueKey + " is unavailable.");

        var result = PortfolioConsequencePresentationResolver.Resolve(
            consequence,
            new Dictionary<string, string>
            {
                ["loser-key"] = "Current equipped job",
                ["winner-key"] = "Gatherer retainer",
            },
            ItemName);
        var visible = $"{result.LosingTargetLabel}\n{result.ConflictSummary}\n{result.WinnerSummary}";

        Assert.Equal("Current equipped job", result.LosingTargetLabel);
        Assert.Equal("Market listing conflict / Item 100: needs 2, 1 available", result.ConflictSummary);
        Assert.Equal("Allocated first to Gatherer retainer", result.WinnerSummary);
        Assert.DoesNotContain(OpaqueKey, visible, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate:", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void Consequences_AreCollapsedByDefaultBehindStableReviewedDisclosure()
    {
        var collapsed = PortfolioConsequenceDisclosurePresentationResolver.Resolve(48, expanded: false);
        var expanded = PortfolioConsequenceDisclosurePresentationResolver.Resolve(48, expanded: true);

        Assert.Equal("squire.outfitter.portfolio.consequences.toggle", PortfolioPresentationReviewedControlIds.ConsequencesToggle);
        Assert.False(collapsed.DrawRows);
        Assert.Equal("48 lower-priority alternatives deferred by scarce allocations.", collapsed.Summary);
        Assert.Equal("Show allocation details", collapsed.ActionLabel);
        Assert.True(expanded.DrawRows);
        Assert.Equal("Hide allocation details", expanded.ActionLabel);
    }

    private static PortfolioCandidate Candidate(
        int changes,
        IReadOnlyList<PortfolioAllocationDemand> demands) =>
        new(
            "target",
            "candidate",
            1,
            demands,
            Enum.GetValues<EquipmentLoadoutPosition>().Take(changes)
                .Select((position, index) => new PortfolioSelectedReplacement(
                    position,
                    new((uint)(100 + index), index % 2 == 0, $"replacement-{index}")))
                .ToArray(),
            []);

    private static PortfolioAllocationDemand Demand(PortfolioAllocationSourceKind kind, uint quantity) =>
        new(new(kind, OpaqueKey, 100, false, kind == PortfolioAllocationSourceKind.OwnedInstance ? "instance-secret" : null), quantity);

    private static string ItemName(uint itemId) => $"Item {itemId}";

    private static string Visible(PortfolioAllocationPresentation value) =>
        $"{value.SelectedUpgradeText}\n{value.ExactAllocationText}";
}
