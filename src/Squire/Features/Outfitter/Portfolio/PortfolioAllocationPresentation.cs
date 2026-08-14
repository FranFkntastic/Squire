using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Portfolio;

public static class PortfolioPresentationReviewedControlIds
{
    public const string ConsequencesToggle = "squire.outfitter.portfolio.consequences.toggle";
}

public sealed record PortfolioAllocationPresentation(
    string ChangeSummary,
    string ItemPreview,
    string AllocationSummary,
    string? HandMeDownSummary)
{
    public string SelectedUpgradeText => $"{ChangeSummary}\n{ItemPreview}";
    public string ExactAllocationText => HandMeDownSummary is null
        ? AllocationSummary
        : $"{AllocationSummary}\n{HandMeDownSummary}";
}

public static class PortfolioAllocationPresentationResolver
{
    private const int MaximumPreviewItems = 2;

    public static PortfolioAllocationPresentation Resolve(
        PortfolioCandidate candidate,
        IReadOnlyDictionary<EquipmentLoadoutPosition, PortfolioExactItem?> baseline,
        Func<uint, string> resolveItemName,
        IReadOnlyDictionary<PortfolioAllocationKey, IReadOnlyList<string>>? handMeDownConsumers = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(resolveItemName);
        var replacements = candidate.SelectedReplacements
            .Where(value => !baseline.TryGetValue(value.Position, out var equipped) || !ExactMatch(value.Item, equipped))
            .OrderBy(value => value.Position)
            .ToArray();
        var preview = replacements.Take(MaximumPreviewItems)
            .Select(value => $"{value.Position}: {resolveItemName(value.Item.ItemId)} {(value.Item.IsHighQuality ? "HQ" : "NQ")}")
            .ToArray();
        var itemPreview = replacements.Length switch
        {
            0 => "Current gear retained",
            <= MaximumPreviewItems => string.Join(" / ", preview),
            _ => $"{string.Join(" / ", preview)} / +{replacements.Length - MaximumPreviewItems:N0} more",
        };
        var sourceCounts = candidate.Demands
            .GroupBy(value => value.Allocation.SourceKind)
            .Select(group => (Kind: group.Key, Quantity: group.Aggregate(0u, (sum, value) => checked(sum + value.Quantity))))
            .OrderBy(value => SourceOrder(value.Kind))
            .Select(value => $"{SourceLabel(value.Kind)} {value.Quantity:N0}")
            .ToArray();
        var allocationSummary = sourceCounts.Length == 0
            ? "No item allocation required"
            : string.Join(" / ", sourceCounts);
        var consumerLabels = candidate.HandMeDowns
            .SelectMany(value => handMeDownConsumers?.GetValueOrDefault(value.ReleasedAllocation) ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var handMeDownSummary = candidate.HandMeDowns.Count switch
        {
            0 => null,
            1 when consumerLabels.Length == 0 => "1 hand-me-down becomes available",
            1 => $"1 hand-me-down -> {ConsumerPreview(consumerLabels)}",
            _ when consumerLabels.Length == 0 => $"{candidate.HandMeDowns.Count:N0} hand-me-downs become available",
            _ => $"{candidate.HandMeDowns.Count:N0} hand-me-downs -> {ConsumerPreview(consumerLabels)}",
        };
        return new(
            replacements.Length switch
            {
                0 => "No slot changes",
                1 => "1 slot changes",
                _ => $"{replacements.Length:N0} slots change",
            },
            itemPreview,
            allocationSummary,
            handMeDownSummary);
    }

    private static bool ExactMatch(PortfolioExactItem selected, PortfolioExactItem? equipped) =>
        equipped is not null &&
        selected.ItemId == equipped.ItemId &&
        selected.IsHighQuality == equipped.IsHighQuality &&
        string.Equals(selected.InstanceId, equipped.InstanceId, StringComparison.Ordinal);

    private static string ConsumerPreview(IReadOnlyList<string> consumers) => consumers.Count switch
    {
        1 => consumers[0],
        2 => $"{consumers[0]}, {consumers[1]}",
        _ => $"{consumers[0]}, {consumers[1]} +{consumers.Count - 2:N0}",
    };

    private static int SourceOrder(PortfolioAllocationSourceKind kind) => kind switch
    {
        PortfolioAllocationSourceKind.OwnedInstance => 0,
        PortfolioAllocationSourceKind.HandMeDown => 1,
        PortfolioAllocationSourceKind.MarketListing => 2,
        PortfolioAllocationSourceKind.GilVendor => 3,
        PortfolioAllocationSourceKind.Craft => 4,
        _ => 5,
    };

    private static string SourceLabel(PortfolioAllocationSourceKind kind) => kind switch
    {
        PortfolioAllocationSourceKind.OwnedInstance => "Owned",
        PortfolioAllocationSourceKind.MarketListing => "Market",
        PortfolioAllocationSourceKind.GilVendor => "Vendor",
        PortfolioAllocationSourceKind.Craft => "Craft",
        PortfolioAllocationSourceKind.HandMeDown => "Hand-me-down",
        _ => "Other",
    };
}

public sealed record PortfolioConsequencePresentation(
    string LosingTargetLabel,
    string ConflictSummary,
    string WinnerSummary);

public static class PortfolioConsequencePresentationResolver
{
    public static PortfolioConsequencePresentation Resolve(
        PortfolioAllocationConsequence consequence,
        IReadOnlyDictionary<string, string> targetLabels,
        Func<uint, string> resolveItemName)
    {
        ArgumentNullException.ThrowIfNull(consequence);
        ArgumentNullException.ThrowIfNull(targetLabels);
        ArgumentNullException.ThrowIfNull(resolveItemName);
        var winners = consequence.HigherPriorityWinnerTargetKeys
            .Select(key => targetLabels.GetValueOrDefault(key) ?? "Higher-priority target")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new(
            targetLabels.GetValueOrDefault(consequence.LosingTargetKey) ?? "Lower-priority target",
            $"{SourceLabel(consequence.ScarceAllocation.SourceKind)} conflict / {resolveItemName(consequence.ScarceAllocation.ItemId)}: " +
            $"needs {consequence.RequiredQuantity:N0}, {consequence.AvailableQuantity:N0} available",
            winners.Length == 0
                ? "No higher-priority winner recorded"
                : $"Allocated first to {string.Join(", ", winners)}");
    }

    private static string SourceLabel(PortfolioAllocationSourceKind kind) => kind switch
    {
        PortfolioAllocationSourceKind.OwnedInstance => "Owned item",
        PortfolioAllocationSourceKind.MarketListing => "Market listing",
        PortfolioAllocationSourceKind.GilVendor => "Vendor stock",
        PortfolioAllocationSourceKind.Craft => "Craft material",
        PortfolioAllocationSourceKind.HandMeDown => "Hand-me-down",
        _ => "Item allocation",
    };
}

public sealed record PortfolioConsequenceDisclosurePresentation(
    string Summary,
    string ActionLabel,
    bool DrawRows);

public static class PortfolioConsequenceDisclosurePresentationResolver
{
    public static PortfolioConsequenceDisclosurePresentation Resolve(int count, bool expanded)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        return count switch
        {
            0 => new("No lower-priority allocation conflicts.", string.Empty, false),
            _ when expanded => new(
                $"Showing {count:N0} lower-priority alternative{(count == 1 ? string.Empty : "s")} that lost a scarce allocation.",
                "Hide allocation details",
                true),
            _ => new(
                $"{count:N0} lower-priority alternative{(count == 1 ? string.Empty : "s")} deferred by scarce allocations.",
                "Show allocation details",
                false),
        };
    }
}
