using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRouteRunSummary
{
    public uint PurchasedQuantity { get; init; }
    public uint SpentGil { get; init; }
    public uint PlannedPurchasedQuantity { get; init; }
    public uint PlannedSpentGil { get; init; }
    public uint OpportunisticPurchasedQuantity { get; init; }
    public uint OpportunisticSpentGil { get; init; }
    public int CompletedWorldCount { get; init; }
    public int PartialWorldCount { get; init; }
    public int FailedWorldCount { get; init; }
    public int CompletedLineCount { get; init; }
    public int SkippedLineCount { get; init; }
    public int FailedLineCount { get; init; }
    public int FreshnessConfirmedCount { get; init; }
    public int FreshnessUnconfirmedCount { get; init; }
    public int FreshnessUnavailableCount { get; init; }
    public string? DiagnosticsPath { get; init; }
    public string? ObservedListingsCsvPath { get; init; }
    public string? PurchaseRecordsCsvPath { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionRouteItemRunSummary> TopItemsBySpentGil { get; init; } = [];

    public static MarketAcquisitionRouteRunSummary Build(
        IReadOnlyList<MarketAcquisitionGuidedRouteStop> stops,
        MarketAcquisitionRunDiagnosticSummary diagnostics,
        string? diagnosticsPath,
        string? observedListingsCsvPath,
        string? purchaseRecordsCsvPath)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var lines = stops.SelectMany(stop => stop.LineStates).ToArray();
        var plannedLines = lines.Where(line =>
            !line.Source.Equals("Opportunistic", StringComparison.OrdinalIgnoreCase));
        var opportunisticLines = lines.Where(line =>
            line.Source.Equals("Opportunistic", StringComparison.OrdinalIgnoreCase));

        return new MarketAcquisitionRouteRunSummary
        {
            PurchasedQuantity = SumQuantity(lines),
            SpentGil = SumGil(lines),
            PlannedPurchasedQuantity = SumQuantity(plannedLines),
            PlannedSpentGil = SumGil(plannedLines),
            OpportunisticPurchasedQuantity = SumQuantity(opportunisticLines),
            OpportunisticSpentGil = SumGil(opportunisticLines),
            CompletedWorldCount = stops.Count(IsCompleteWorld),
            PartialWorldCount = stops.Count(IsPartialWorld),
            FailedWorldCount = stops.Count(IsFailedWorld),
            CompletedLineCount = lines.Count(IsCompleteLine),
            SkippedLineCount = lines.Count(IsSkippedLine),
            FailedLineCount = lines.Count(IsFailedLine),
            FreshnessConfirmedCount = diagnostics.FreshnessConfirmedCount,
            FreshnessUnconfirmedCount = diagnostics.FreshnessUnconfirmedCount,
            FreshnessUnavailableCount = diagnostics.FreshnessUnavailableCount,
            Warnings = diagnostics.Warnings.ToArray(),
            DiagnosticsPath = diagnosticsPath,
            ObservedListingsCsvPath = observedListingsCsvPath,
            PurchaseRecordsCsvPath = purchaseRecordsCsvPath,
            TopItemsBySpentGil = BuildTopItems(lines),
        };
    }

    private static IReadOnlyList<MarketAcquisitionRouteItemRunSummary> BuildTopItems(
        IReadOnlyList<MarketAcquisitionRouteLineState> lines) =>
        lines
            .Where(line => line.PurchasedQuantity > 0 || line.SpentGil > 0)
            .GroupBy(line => new ItemKey(line.ItemId, line.ItemName), ItemKeyComparer.Instance)
            .Select(group => new MarketAcquisitionRouteItemRunSummary
            {
                ItemId = group.Key.ItemId,
                ItemName = group.Key.ItemName,
                PurchasedQuantity = SumQuantity(group),
                SpentGil = SumGil(group),
            })
            .OrderByDescending(item => item.SpentGil)
            .ThenBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

    private static bool IsCompleteWorld(MarketAcquisitionGuidedRouteStop stop) =>
        stop.LineStates.Count > 0 &&
        stop.LineStates.All(line => IsCompleteLine(line) || IsSkippedLine(line)) &&
        stop.LineStates.Any(IsCompleteLine) &&
        !stop.LineStates.Any(IsSkippedLine) &&
        !stop.LineStates.Any(IsFailedLine);

    private static bool IsPartialWorld(MarketAcquisitionGuidedRouteStop stop) =>
        stop.LineStates.Any(IsCompleteLine) &&
        stop.LineStates.Any(IsSkippedLine) &&
        !stop.LineStates.Any(IsFailedLine);

    private static bool IsFailedWorld(MarketAcquisitionGuidedRouteStop stop) =>
        stop.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
        stop.LineStates.Any(IsFailedLine);

    private static bool IsCompleteLine(MarketAcquisitionRouteLineState line) =>
        line.Status.Equals("Complete", StringComparison.OrdinalIgnoreCase);

    private static bool IsSkippedLine(MarketAcquisitionRouteLineState line) =>
        line.Status.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedLine(MarketAcquisitionRouteLineState line) =>
        line.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase);

    private static uint SumQuantity(IEnumerable<MarketAcquisitionRouteLineState> lines)
    {
        var total = 0u;
        foreach (var line in lines)
            total = checked(total + line.PurchasedQuantity);

        return total;
    }

    private static uint SumGil(IEnumerable<MarketAcquisitionRouteLineState> lines)
    {
        var total = 0u;
        foreach (var line in lines)
            total = checked(total + line.SpentGil);

        return total;
    }

    private sealed record ItemKey(uint ItemId, string? ItemName);

    private sealed class ItemKeyComparer : IEqualityComparer<ItemKey>
    {
        public static readonly ItemKeyComparer Instance = new();

        public bool Equals(ItemKey? x, ItemKey? y)
        {
            if (x == null || y == null)
                return x == y;

            return x.ItemId == y.ItemId;
        }

        public int GetHashCode(ItemKey obj) => obj.ItemId.GetHashCode();
    }
}

public sealed record MarketAcquisitionRouteItemRunSummary
{
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public uint PurchasedQuantity { get; init; }
    public uint SpentGil { get; init; }
}
