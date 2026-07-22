using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRouteTablePresenter
{
    public static IReadOnlyList<MarketAcquisitionRouteStopRow> BuildRows(
        IReadOnlyList<MarketAcquisitionGuidedRouteStop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);

        return stops.Select(BuildStopRow).ToArray();
    }

    private static MarketAcquisitionRouteStopRow BuildStopRow(MarketAcquisitionGuidedRouteStop stop)
    {
        var lines = stop.LineStates
            .Select(BuildLineRow)
            .ToArray();
        var aggregate = BuildAggregate(stop);

        return new MarketAcquisitionRouteStopRow
        {
            WorldName = stop.WorldName,
            DataCenter = stop.DataCenter,
            RouteLines = FormatRouteLines(stop.LineStates),
            LineMix = FormatLineMix(stop.LineStates),
            State = aggregate.State,
            Intent = FormatPlannedIntent(stop.LineStates),
            Result = FormatBoughtResult(stop.LineStates),
            Notes = FormatNotes(stop.LineStates, aggregate),
            Aggregate = aggregate,
            Lines = lines,
        };
    }

    private static MarketAcquisitionRouteLineRow BuildLineRow(MarketAcquisitionRouteLineState line) =>
        new()
        {
            Item = FormatItem(line.ItemName, line.ItemId),
            Source = string.IsNullOrWhiteSpace(line.Source) ? "Planned" : line.Source,
            State = FormatState(line.Status),
            Planned = line.PlannedQuantity == 0 && line.PlannedGil == 0
                ? "-"
                : FormatQuantityGil(line.PlannedQuantity, line.PlannedGil),
            Discovered = string.IsNullOrWhiteSpace(line.LiveCandidateStatus)
                ? "-"
                : FormatQuantityGil(line.LiveObservedQuantity, line.LiveObservedGil),
            Bought = line.PurchasedQuantity == 0 && line.SpentGil == 0
                ? "-"
                : FormatQuantityGil(line.PurchasedQuantity, line.SpentGil),
            Notes = FormatLineNotes(line),
        };

    private static MarketAcquisitionRouteStopAggregate BuildAggregate(MarketAcquisitionGuidedRouteStop stop)
    {
        var completed = stop.LineStates.Count(IsComplete);
        var skipped = stop.LineStates.Count(IsSkipped);
        var failed = stop.LineStates.Count(IsFailed);
        var opportunistic = stop.LineStates.Count(line =>
            line.Source.Equals("Opportunistic", StringComparison.OrdinalIgnoreCase));
        var purchasedLines = stop.LineStates.Count(line => line.PurchasedQuantity > 0 || line.SpentGil > 0);
        var state = ResolveStopState(stop, completed, skipped, failed, purchasedLines);

        return new MarketAcquisitionRouteStopAggregate
        {
            State = state,
            CompletedLineCount = completed,
            SkippedLineCount = skipped,
            FailedLineCount = failed,
            OpportunisticLineCount = opportunistic,
            PurchasedLineCount = purchasedLines,
            TotalLineCount = stop.LineStates.Count,
        };
    }

    private static string ResolveStopState(
        MarketAcquisitionGuidedRouteStop stop,
        int completed,
        int skipped,
        int failed,
        int purchasedLines)
    {
        if (failed > 0 || stop.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            return "Blocked";

        if (stop.Status.Equals("Purchasing", StringComparison.OrdinalIgnoreCase))
            return "Buying";

        if (stop.Status.Equals("TravelCommandSent", StringComparison.OrdinalIgnoreCase))
            return "Traveling";

        if (purchasedLines > 0 && skipped > 0)
            return "Partial";

        if (stop.LineStates.Count > 0 && completed + skipped == stop.LineStates.Count)
            return "Complete";

        return string.IsNullOrWhiteSpace(stop.Status)
            ? "Pending"
            : FormatState(stop.Status);
    }

    private static string FormatRouteLines(IReadOnlyList<MarketAcquisitionRouteLineState> lines)
    {
        var names = lines
            .Select(line => FormatItem(line.ItemName, line.ItemId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
            return "-";

        var shown = string.Join(", ", names.Take(2));
        return names.Length <= 2
            ? shown
            : $"{shown} +{names.Length - 2:N0}";
    }

    private static string FormatLineMix(IReadOnlyList<MarketAcquisitionRouteLineState> lines)
    {
        var planned = lines.Count(line => !line.Source.Equals("Opportunistic", StringComparison.OrdinalIgnoreCase));
        var opportunistic = lines.Count - planned;
        if (planned == 0 && opportunistic == 0)
            return "No route lines";
        if (opportunistic == 0)
            return FormatCount(planned, "planned line");
        if (planned == 0)
            return FormatCount(opportunistic, "opportunistic line");

        return $"{planned:N0} planned / {opportunistic:N0} opportunistic";
    }

    private static string FormatNotes(
        IReadOnlyList<MarketAcquisitionRouteLineState> lines,
        MarketAcquisitionRouteStopAggregate aggregate)
    {
        var notes = new List<string>();
        if (aggregate.SkippedLineCount > 0)
            notes.Add(FormatCount(aggregate.SkippedLineCount, "skipped line"));
        if (aggregate.FailedLineCount > 0)
            notes.Add(FormatCount(aggregate.FailedLineCount, "failed line"));
        if (aggregate.OpportunisticLineCount > 0)
            notes.Add(FormatCount(aggregate.OpportunisticLineCount, "opportunistic check"));

        var noSafeStock = lines.Count(line =>
            line.Status.Contains("NoLiveStock", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(line.LiveCandidateStatus, "NoSafeListings", StringComparison.OrdinalIgnoreCase));
        if (noSafeStock > 0)
            notes.Add($"{noSafeStock:N0} no safe stock");

        return notes.Count == 0 ? "-" : string.Join("; ", notes);
    }

    private static string FormatPlannedIntent(IReadOnlyList<MarketAcquisitionRouteLineState> lines)
    {
        var plannedQuantity = 0u;
        var plannedGil = 0ul;
        foreach (var line in lines)
        {
            plannedQuantity = checked(plannedQuantity + line.PlannedQuantity);
            plannedGil = checked(plannedGil + line.PlannedGil);
        }

        return plannedQuantity == 0 && plannedGil == 0
            ? "-"
            : FormatQuantityGil(plannedQuantity, plannedGil);
    }

    private static string FormatBoughtResult(IReadOnlyList<MarketAcquisitionRouteLineState> lines)
    {
        var boughtQuantity = 0u;
        var spentGil = 0ul;
        foreach (var line in lines)
        {
            boughtQuantity = checked(boughtQuantity + line.PurchasedQuantity);
            spentGil = checked(spentGil + line.SpentGil);
        }

        return boughtQuantity == 0 && spentGil == 0
            ? "-"
            : FormatQuantityGil(boughtQuantity, spentGil);
    }

    private static string FormatLineNotes(MarketAcquisitionRouteLineState line)
    {
        if (!string.IsNullOrWhiteSpace(line.LatestMessage))
            return line.LatestMessage;
        if (!string.IsNullOrWhiteSpace(line.LiveCandidateStatus))
            return FormatState(line.LiveCandidateStatus);

        return "-";
    }

    private static string FormatItem(string? itemName, uint itemId)
    {
        var name = string.IsNullOrWhiteSpace(itemName)
            ? $"Item {itemId.ToString(CultureInfo.InvariantCulture)}"
            : itemName;
        return $"{name} ({itemId.ToString(CultureInfo.InvariantCulture)})";
    }

    private static string FormatQuantityGil(uint quantity, ulong gil) =>
        $"{quantity.ToString("N0", CultureInfo.InvariantCulture)} / {gil.ToString("N0", CultureInfo.InvariantCulture)} gil";

    private static string FormatCount(int count, string singular)
    {
        if (count == 1)
            return $"1 {singular}";

        return $"{count.ToString("N0", CultureInfo.InvariantCulture)} {singular}s";
    }

    private static string FormatState(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return "Pending";

        return state;
    }

    private static bool IsComplete(MarketAcquisitionRouteLineState line) =>
        line.Status.Equals("Complete", StringComparison.OrdinalIgnoreCase);

    private static bool IsSkipped(MarketAcquisitionRouteLineState line) =>
        line.Status.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailed(MarketAcquisitionRouteLineState line) =>
        line.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase);
}

public sealed record MarketAcquisitionRouteStopRow
{
    public string WorldName { get; init; } = string.Empty;
    public string DataCenter { get; init; } = string.Empty;
    public string RouteLines { get; init; } = string.Empty;
    public string LineMix { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Intent { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public MarketAcquisitionRouteStopAggregate Aggregate { get; init; } = new();
    public IReadOnlyList<MarketAcquisitionRouteLineRow> Lines { get; init; } = [];
}

public sealed record MarketAcquisitionRouteLineRow
{
    public string Item { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Planned { get; init; } = string.Empty;
    public string Discovered { get; init; } = string.Empty;
    public string Bought { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionRouteStopAggregate
{
    public string State { get; init; } = string.Empty;
    public int CompletedLineCount { get; init; }
    public int SkippedLineCount { get; init; }
    public int FailedLineCount { get; init; }
    public int OpportunisticLineCount { get; init; }
    public int PurchasedLineCount { get; init; }
    public int TotalLineCount { get; init; }
}
