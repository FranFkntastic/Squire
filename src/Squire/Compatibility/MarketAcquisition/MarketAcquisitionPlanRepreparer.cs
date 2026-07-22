using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionPlanRepreparer
{
    public static MarketAcquisitionPlanReprepareResult FilterCompletedOrProbedStops(
        MarketAcquisitionPlan plan,
        IEnumerable<MarketAcquisitionCompletedRouteStop> completedOrProbedStops,
        DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(completedOrProbedStops);

        var skippedWorlds = completedOrProbedStops
            .Select(stop => stop.WorldName)
            .Where(world => !string.IsNullOrWhiteSpace(world))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var skippedSet = skippedWorlds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remainingBatches = plan.WorldBatches
            .Where(batch => !skippedSet.Contains(batch.WorldName))
            .ToArray();

        var filtered = plan with
        {
            Status = remainingBatches.Length == 0 ? "NoSupportedListings" : "Ready",
            PreparedAtUtc = preparedAtUtc,
            PlannedQuantity = (uint)remainingBatches.Sum(batch => batch.PlannedQuantity),
            PlannedGil = (uint)remainingBatches.Sum(batch => batch.PlannedGil),
            Diagnostics = plan.Diagnostics with
            {
                PlannedListingCount = remainingBatches.Sum(batch => batch.Listings.Count),
            },
            Lines = RecalculateLines(plan.Lines, remainingBatches),
            WorldBatches = remainingBatches,
        };

        return new MarketAcquisitionPlanReprepareResult
        {
            Plan = filtered,
            SkippedWorlds = skippedWorlds,
            CanStart = remainingBatches.Length > 0,
            Message = remainingBatches.Length == 0
                ? $"No unvisited worlds remain after skipping {skippedWorlds.Length:N0} completed/probed world(s)."
                : $"Re-prepared route. Skipped {skippedWorlds.Length:N0} completed/probed world(s); {remainingBatches.Length:N0} world(s) remain.",
        };
    }

    private static IReadOnlyList<MarketAcquisitionPlanLine> RecalculateLines(
        IReadOnlyList<MarketAcquisitionPlanLine> lines,
        IReadOnlyList<MarketAcquisitionWorldBatch> remainingBatches)
    {
        if (lines.Count == 0)
            return lines;

        var subtasksByLine = remainingBatches
            .SelectMany(batch => batch.ItemSubtasks)
            .GroupBy(subtask => subtask.LineId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        return lines
            .Select(line =>
            {
                subtasksByLine.TryGetValue(line.LineId, out var subtasks);
                subtasks ??= [];
                return line with
                {
                    Status = subtasks.Length == 0 ? "NoSupportedListings" : "Ready",
                    PlannedQuantity = (uint)subtasks.Sum(subtask => subtask.PlannedQuantity),
                    PlannedGil = (uint)subtasks.Sum(subtask => subtask.PlannedGil),
                };
            })
            .ToArray();
    }
}

public sealed record MarketAcquisitionPlanReprepareResult
{
    public MarketAcquisitionPlan Plan { get; init; } = new();
    public IReadOnlyList<string> SkippedWorlds { get; init; } = [];
    public bool CanStart { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionCompletedRouteStop
{
    public string WorldName { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
}
