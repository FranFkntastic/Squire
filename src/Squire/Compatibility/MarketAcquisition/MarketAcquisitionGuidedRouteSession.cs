using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionGuidedRouteSession
{
    private int activeStopIndex;

    private MarketAcquisitionGuidedRouteSession(IReadOnlyList<MarketAcquisitionGuidedRouteStop> stops)
    {
        Stops = stops;
        Status = stops.Count == 0 ? "Complete" : "Active";
    }

    public string Status { get; private set; }
    public IReadOnlyList<MarketAcquisitionGuidedRouteStop> Stops { get; }
    public MarketAcquisitionWorldCompletionSummary? LastWorldCompletionSummary { get; private set; }
    public MarketAcquisitionGuidedRouteStop? ActiveStop =>
        Status == "Complete" || activeStopIndex >= Stops.Count
            ? null
            : Stops[activeStopIndex];
    public bool ShouldMonitorActiveStop =>
        ActiveStop?.Status is "TravelCommandSent" or "Arrived" or "Purchasing";

    public MarketAcquisitionRouteLinePurchaseTotals GetLinePurchaseTotals(string lineId)
    {
        if (string.IsNullOrWhiteSpace(lineId))
            return default;

        var purchasedQuantity = 0u;
        var spentGil = 0u;
        foreach (var line in Stops.SelectMany(stop => stop.LineStates))
        {
            if (!line.LineId.Equals(lineId, StringComparison.Ordinal))
                continue;

            purchasedQuantity = checked(purchasedQuantity + line.PurchasedQuantity);
            spentGil = checked(spentGil + line.SpentGil);
        }

        return new MarketAcquisitionRouteLinePurchaseTotals(purchasedQuantity, spentGil);
    }

    public static MarketAcquisitionGuidedRouteSession Start(
        MarketAcquisitionPlan plan,
        bool includeOpportunisticChecks = false)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!string.Equals(plan.Status, "Ready", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A ready market acquisition plan is required before starting a guided route.");

        var stops = plan.WorldBatches
            .Where(batch => !string.IsNullOrWhiteSpace(batch.WorldName))
            .Select(batch =>
            {
                var dataCenter = string.IsNullOrWhiteSpace(batch.DataCenter)
                    ? MarketAcquisitionPlanner.ResolveNorthAmericaDataCenter(batch.WorldName)
                    : batch.DataCenter;
                var subtasks = includeOpportunisticChecks
                    ? AddOpportunisticSubtasks(batch, plan.Lines, dataCenter)
                    : batch.ItemSubtasks;

                return new MarketAcquisitionGuidedRouteStop
                {
                    WorldName = batch.WorldName,
                    DataCenter = dataCenter,
                    PlannedQuantity = batch.PlannedQuantity,
                    PlannedGil = batch.PlannedGil,
                    ItemSubtasks = subtasks,
                    LineStates = subtasks
                        .Select(subtask => new MarketAcquisitionRouteLineState
                        {
                            LineId = subtask.LineId,
                            ItemId = subtask.ItemId,
                            ItemName = subtask.ItemName,
                            Source = subtask.Source,
                            PlannedQuantity = subtask.PlannedQuantity,
                            PlannedGil = subtask.PlannedGil,
                            Status = "Pending",
                        })
                        .ToList(),
                    LifestreamCommand = BuildLifestreamCommand(batch.WorldName),
                    Status = "Pending",
                };
            })
            .ToList();

        if (stops.Count == 0)
            throw new InvalidOperationException("A guided route requires at least one planned world batch.");

        return new MarketAcquisitionGuidedRouteSession(stops);
    }

    private static IReadOnlyList<MarketAcquisitionWorldItemSubtask> AddOpportunisticSubtasks(
        MarketAcquisitionWorldBatch batch,
        IReadOnlyList<MarketAcquisitionPlanLine> lines,
        string dataCenter)
    {
        if (lines.Count == 0)
            return batch.ItemSubtasks;

        var existing = batch.ItemSubtasks
            .Select(subtask => subtask.LineId)
            .ToHashSet(StringComparer.Ordinal);
        var subtasks = batch.ItemSubtasks
            .Select(subtask => subtask.Source.Equals("Planned", StringComparison.OrdinalIgnoreCase)
                ? subtask
                : subtask with { Source = "Planned" })
            .ToList();

        foreach (var line in lines.OrderBy(line => line.Ordinal))
        {
            if (existing.Contains(line.LineId))
                continue;

            subtasks.Add(new MarketAcquisitionWorldItemSubtask
            {
                LineId = line.LineId,
                LineOrdinal = line.Ordinal,
                Source = "Opportunistic",
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                WorldName = batch.WorldName,
                DataCenter = dataCenter,
                QuantityMode = line.QuantityMode,
                RequestedQuantity = line.RequestedQuantity,
                HqPolicy = line.HqPolicy,
                MaxUnitPrice = line.MaxUnitPrice,
                GilCap = line.GilCap,
                PlannedQuantity = 0,
                PlannedGil = 0,
                Listings = [],
            });
        }

        return subtasks
            .OrderBy(subtask => subtask.LineOrdinal)
            .ThenBy(subtask => subtask.Source.Equals("Planned", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
    }

    public MarketAcquisitionGuidedRouteResult RecordCurrentWorld(string currentWorld)
    {
        var stop = ActiveStop;
        if (stop == null)
            return MarketAcquisitionGuidedRouteResult.Fail("Guided route is already complete.");

        if (!stop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionGuidedRouteResult.Fail($"Waiting for {stop.WorldName}; current world is {currentWorld}.");

        stop.Status = "Arrived";
        return MarketAcquisitionGuidedRouteResult.Ok($"Arrived on {stop.WorldName}. Searching the market board item when the market board is ready.");
    }

    public MarketAcquisitionGuidedRouteResult RecordCurrentWorldUnavailable()
    {
        var stop = ActiveStop;
        if (stop == null)
            return MarketAcquisitionGuidedRouteResult.Fail("Guided route is already complete.");

        return MarketAcquisitionGuidedRouteResult.Fail(
            $"Waiting for {stop.WorldName}; current world is unavailable during world travel.");
    }

    public MarketAcquisitionGuidedRouteResult ExecuteActiveStop(Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);

        var stop = ActiveStop;
        if (stop == null)
            return MarketAcquisitionGuidedRouteResult.Fail("Guided route is already complete.");

        if (!processCommand(stop.LifestreamCommand))
            return MarketAcquisitionGuidedRouteResult.Fail($"Lifestream command was not handled: {stop.LifestreamCommand}");

        stop.Status = "TravelCommandSent";
        return MarketAcquisitionGuidedRouteResult.Ok($"Sent {stop.LifestreamCommand}. Waiting for arrival on {stop.WorldName}.");
    }

    public MarketAcquisitionGuidedRouteResult RecordProbe(
        string currentWorld,
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        bool allowPurchases = true)
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);

        var stop = ActiveStop;
        if (stop == null)
            return MarketAcquisitionGuidedRouteResult.Fail("Guided route is already complete.");

        if (!stop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionGuidedRouteResult.Fail($"Cannot record probe for {currentWorld}; active stop is {stop.WorldName}.");

        stop.LiveCandidateStatus = candidatePlan.Status;
        stop.WouldBuyQuantity = candidatePlan.WouldBuyQuantity;
        stop.WouldSpendGil = candidatePlan.WouldSpendGil;
        UpdateActiveLineProbe(stop, candidatePlan);

        if (allowPurchases && candidatePlan.WouldBuyQuantity > 0)
        {
            stop.Status = "Purchasing";
            UpdateActiveLine(
                stop,
                status: "Purchasing",
                purchasedQuantity: 0,
                spentGil: 0,
                $"Purchasing {candidatePlan.WouldBuyQuantity:N0} safe live item(s), {candidatePlan.WouldSpendGil:N0} gil.");
            return MarketAcquisitionGuidedRouteResult.Ok(
                $"Purchasing {FormatActiveItem(stop)} on {stop.WorldName}: {candidatePlan.WouldBuyQuantity:N0} safe live item(s), {candidatePlan.WouldSpendGil:N0} gil.");
        }

        if (!allowPurchases)
        {
            const string observedMessage = "Fresh market evidence was recorded without purchasing.";
            if (TryAdvanceActiveItemSubtask(
                    stop,
                    zeroPurchaseStatus: "Observed",
                    zeroPurchaseMessage: observedMessage))
            {
                return MarketAcquisitionGuidedRouteResult.Ok(
                    $"Recorded fresh evidence for {FormatPreviousItem(stop)} on {currentWorld}. Next item: {FormatActiveItem(stop)}.");
            }

            CompleteActiveStop(stop.PurchasedQuantity, stop.SpentGil);
            return Status == "Complete"
                ? MarketAcquisitionGuidedRouteResult.Ok("Evidence refresh complete. No purchases were attempted.")
                : MarketAcquisitionGuidedRouteResult.Ok(
                    $"Recorded fresh evidence for {FormatPreviousItem(stop)} on {currentWorld}. Next stop: {ActiveStop?.WorldName}.");
        }

        if (TryAdvanceActiveItemSubtask(
                stop,
                zeroPurchaseStatus: ResolveZeroPurchaseLineStatus(candidatePlan.Status),
                zeroPurchaseMessage: candidatePlan.Message))
        {
            return MarketAcquisitionGuidedRouteResult.Ok(
                $"{FormatZeroPurchaseProbeMessage(candidatePlan.Status)} for {FormatPreviousItem(stop)} on {currentWorld}. Next item: {FormatActiveItem(stop)}.");
        }

        CompleteActiveStop(stop.PurchasedQuantity, stop.SpentGil);
        if (Status == "Complete")
            return MarketAcquisitionGuidedRouteResult.Ok(
                $"Guided route complete. {FormatZeroPurchaseProbeMessage(candidatePlan.Status)}.");

        return MarketAcquisitionGuidedRouteResult.Ok(
            $"{FormatZeroPurchaseProbeMessage(candidatePlan.Status)} for {FormatPreviousItem(stop)} on {currentWorld}. Next stop: {ActiveStop?.WorldName}.");
    }

    public MarketAcquisitionGuidedRouteResult RecordWorldPurchaseBatchComplete(
        string currentWorld,
        uint purchasedQuantity,
        uint spentGil,
        string? zeroPurchaseStatus = null,
        string? zeroPurchaseMessage = null,
        bool dryRun = false)
    {
        var stop = ActiveStop;
        if (stop == null)
            return MarketAcquisitionGuidedRouteResult.Fail("Guided route is already complete.");

        if (!stop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionGuidedRouteResult.Fail($"Cannot complete purchases for {currentWorld}; active stop is {stop.WorldName}.");

        if (!stop.Status.Equals("Purchasing", StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionGuidedRouteResult.Fail($"Cannot complete purchases while stop is {stop.Status}.");

        if (stop.ItemSubtasks.Count == 0)
        {
            CompleteActiveStop(purchasedQuantity, spentGil);
        }
        else if (TryAdvanceActiveItemSubtask(stop, purchasedQuantity, spentGil, zeroPurchaseStatus, zeroPurchaseMessage, dryRun))
        {
            return MarketAcquisitionGuidedRouteResult.Ok(
                dryRun
                    ? $"Completed dry-run check for {FormatPreviousItem(stop)} on {currentWorld}: would purchase {purchasedQuantity:N0} item(s), would spend {spentGil:N0} gil. Next item: {FormatActiveItem(stop)}."
                    : $"Completed {FormatPreviousItem(stop)} on {currentWorld}: purchased {purchasedQuantity:N0} item(s), spent {spentGil:N0} gil. Next item: {FormatActiveItem(stop)}.");
        }
        else
        {
            CompleteActiveStop(stop.PurchasedQuantity, stop.SpentGil);
        }
        if (Status == "Complete")
        {
            var routeTotals = BuildRouteTotals();
            return MarketAcquisitionGuidedRouteResult.Ok(
                dryRun
                    ? $"Dry run complete. Would purchase {routeTotals.PurchasedQuantity:N0} item(s), would spend {routeTotals.SpentGil:N0} gil."
                    : $"Guided route complete. Purchased {routeTotals.PurchasedQuantity:N0} item(s), spent {routeTotals.SpentGil:N0} gil.");
        }

        return MarketAcquisitionGuidedRouteResult.Ok(
            dryRun
                ? $"Completed dry-run check for {currentWorld}: would purchase {stop.PurchasedQuantity:N0} item(s), would spend {stop.SpentGil:N0} gil. Next stop: {ActiveStop?.WorldName}."
                : $"Completed {currentWorld}: purchased {stop.PurchasedQuantity:N0} item(s), spent {stop.SpentGil:N0} gil. Next stop: {ActiveStop?.WorldName}.");
    }

    private void CompleteActiveStop(uint purchasedQuantity, uint spentGil)
    {
        var stop = ActiveStop;
        if (stop == null)
            return;

        stop.Status = "Complete";
        stop.PurchasedQuantity = purchasedQuantity;
        stop.SpentGil = spentGil;
        LastWorldCompletionSummary = BuildWorldSummary(stop);
        activeStopIndex++;
        if (activeStopIndex >= Stops.Count)
            Status = "Complete";
    }

    private static string BuildLifestreamCommand(string worldName) => $"/li {worldName} mb";

    private static bool TryAdvanceActiveItemSubtask(
        MarketAcquisitionGuidedRouteStop stop,
        uint purchasedQuantity = 0,
        uint spentGil = 0,
        string? zeroPurchaseStatus = null,
        string? zeroPurchaseMessage = null,
        bool dryRun = false)
    {
        if (stop.ItemSubtasks.Count == 0)
            return false;

        var didPurchase = purchasedQuantity > 0 || spentGil > 0;
        stop.PurchasedQuantity = checked(stop.PurchasedQuantity + purchasedQuantity);
        stop.SpentGil = checked(stop.SpentGil + spentGil);
        UpdateActiveLine(
            stop,
            status: didPurchase ? "Complete" : zeroPurchaseStatus ?? "SkippedNoLiveStock",
            purchasedQuantity,
            spentGil,
            !didPurchase
                ? zeroPurchaseMessage ?? "No safe live candidates remained."
                : dryRun
                    ? $"Would purchase {purchasedQuantity:N0} item(s), would spend {spentGil:N0} gil."
                    : $"Purchased {purchasedQuantity:N0} item(s), spent {spentGil:N0} gil.");
        stop.CompletedItemSubtaskCount++;
        if (stop.CompletedItemSubtaskCount >= stop.ItemSubtasks.Count)
            return false;

        stop.Status = "Arrived";
        stop.LiveCandidateStatus = null;
        stop.WouldBuyQuantity = 0;
        stop.WouldSpendGil = 0;
        return true;
    }

    private static void UpdateActiveLine(
        MarketAcquisitionGuidedRouteStop stop,
        string status,
        uint purchasedQuantity,
        uint spentGil,
        string? message)
    {
        var activeSubtask = stop.ActiveItemSubtask;
        if (activeSubtask == null)
            return;

        var line = stop.LineStates.FirstOrDefault(lineState =>
            lineState.LineId.Equals(activeSubtask.LineId, StringComparison.Ordinal));
        if (line == null)
            return;

        line.Status = status;
        line.PurchasedQuantity = checked(line.PurchasedQuantity + purchasedQuantity);
        line.SpentGil = checked(line.SpentGil + spentGil);
        line.LatestMessage = message;
    }

    private static void UpdateActiveLineProbe(
        MarketAcquisitionGuidedRouteStop stop,
        MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        var activeSubtask = stop.ActiveItemSubtask;
        if (activeSubtask == null)
            return;

        var line = stop.LineStates.FirstOrDefault(lineState =>
            lineState.LineId.Equals(activeSubtask.LineId, StringComparison.Ordinal));
        if (line == null)
            return;

        line.LiveCandidateStatus = candidatePlan.Status;
        line.LiveReadableListingCount = candidatePlan.ReadableListingCount;
        line.LiveReportedListingCount = candidatePlan.ReportedListingCount;
        line.LiveObservedQuantity = SumObservedQuantity(candidatePlan);
        line.LiveObservedGil = SumObservedGil(candidatePlan);
        line.WouldBuyQuantity = candidatePlan.WouldBuyQuantity;
        line.WouldSpendGil = candidatePlan.WouldSpendGil;
    }

    private static uint SumObservedQuantity(MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        var total = 0u;
        foreach (var row in candidatePlan.Rows)
            total = checked(total + row.LiveListing.Quantity);

        return total;
    }

    private static ulong SumObservedGil(MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        var total = 0ul;
        foreach (var row in candidatePlan.Rows)
            total = checked(total + ((ulong)row.LiveListing.UnitPrice * row.LiveListing.Quantity));

        return total;
    }

    private static MarketAcquisitionWorldCompletionSummary BuildWorldSummary(MarketAcquisitionGuidedRouteStop stop) => new()
    {
        WorldName = stop.WorldName,
        DataCenter = stop.DataCenter,
        PurchasedQuantity = stop.PurchasedQuantity,
        SpentGil = stop.SpentGil,
        CompletedLineCount = stop.LineStates.Count(line => line.Status.Equals("Complete", StringComparison.OrdinalIgnoreCase)),
        SkippedLineCount = stop.LineStates.Count(line => line.Status.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase)),
        FailedLineCount = stop.LineStates.Count(line => line.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)),
        Message = $"{stop.WorldName} complete: bought {stop.PurchasedQuantity:N0} item(s), spent {stop.SpentGil:N0} gil across {stop.LineStates.Count:N0} line(s).",
    };

    private RouteTotals BuildRouteTotals()
    {
        var purchasedQuantity = 0u;
        var spentGil = 0u;
        foreach (var stop in Stops)
        {
            purchasedQuantity = checked(purchasedQuantity + stop.PurchasedQuantity);
            spentGil = checked(spentGil + stop.SpentGil);
        }

        return new RouteTotals(purchasedQuantity, spentGil);
    }

    private static string ResolveZeroPurchaseLineStatus(string candidateStatus) =>
        MarketAcquisitionLiveCandidateStatuses.IsIncompleteListingCoverage(candidateStatus)
            ? "SkippedIncompleteListingCoverage"
            : "SkippedNoLiveStock";

    private static string FormatZeroPurchaseProbeMessage(string candidateStatus) =>
        MarketAcquisitionLiveCandidateStatuses.IsIncompleteListingCoverage(candidateStatus)
            ? "Incomplete listing coverage"
            : "No safe live candidates remained";

    private static string FormatActiveItem(MarketAcquisitionGuidedRouteStop stop) =>
        FormatItem(stop.ActiveItemSubtask);

    private static string FormatPreviousItem(MarketAcquisitionGuidedRouteStop stop)
    {
        if (stop.ItemSubtasks.Count == 0)
            return "item";

        var previousIndex = Math.Clamp(stop.CompletedItemSubtaskCount - 1, 0, stop.ItemSubtasks.Count - 1);
        return FormatItem(stop.ItemSubtasks[previousIndex]);
    }

    private static string FormatItem(MarketAcquisitionWorldItemSubtask? subtask)
    {
        if (subtask == null)
            return "item";

        return string.IsNullOrWhiteSpace(subtask.ItemName)
            ? $"item {subtask.ItemId}"
            : $"{subtask.ItemName} ({subtask.ItemId})";
    }

    private sealed record RouteTotals(uint PurchasedQuantity, uint SpentGil);
}

public sealed record MarketAcquisitionGuidedRouteStop
{
    public string WorldName { get; init; } = string.Empty;
    public string DataCenter { get; init; } = string.Empty;
    public string LifestreamCommand { get; init; } = string.Empty;
    public uint PlannedQuantity { get; init; }
    public uint PlannedGil { get; init; }
    public IReadOnlyList<MarketAcquisitionWorldItemSubtask> ItemSubtasks { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionRouteLineState> LineStates { get; init; } = [];
    public int CompletedItemSubtaskCount { get; set; }
    public MarketAcquisitionWorldItemSubtask? ActiveItemSubtask =>
        CompletedItemSubtaskCount >= ItemSubtasks.Count
            ? null
            : ItemSubtasks[CompletedItemSubtaskCount];
    public string Status { get; set; } = string.Empty;
    public string? LiveCandidateStatus { get; set; }
    public uint WouldBuyQuantity { get; set; }
    public uint WouldSpendGil { get; set; }
    public uint PurchasedQuantity { get; set; }
    public uint SpentGil { get; set; }
    public bool MarketBoardTravelCommandSent { get; set; }
}

public sealed record MarketAcquisitionRouteLineState
{
    public string LineId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string Source { get; init; } = "Planned";
    public string Status { get; set; } = "Pending";
    public uint PlannedQuantity { get; init; }
    public uint PlannedGil { get; init; }
    public uint PurchasedQuantity { get; set; }
    public uint SpentGil { get; set; }
    public string? LiveCandidateStatus { get; set; }
    public int LiveReadableListingCount { get; set; }
    public int LiveReportedListingCount { get; set; }
    public uint LiveObservedQuantity { get; set; }
    public ulong LiveObservedGil { get; set; }
    public uint WouldBuyQuantity { get; set; }
    public uint WouldSpendGil { get; set; }
    public string? LatestMessage { get; set; }
}

public sealed record MarketAcquisitionWorldCompletionSummary
{
    public string WorldName { get; init; } = string.Empty;
    public string DataCenter { get; init; } = string.Empty;
    public uint PurchasedQuantity { get; init; }
    public uint SpentGil { get; init; }
    public int CompletedLineCount { get; init; }
    public int SkippedLineCount { get; init; }
    public int FailedLineCount { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionGuidedRouteResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;

    public static MarketAcquisitionGuidedRouteResult Ok(string message) => new()
    {
        Success = true,
        Message = message,
    };

    public static MarketAcquisitionGuidedRouteResult Fail(string message) => new()
    {
        Success = false,
        Message = message,
    };
}

public readonly record struct MarketAcquisitionRouteLinePurchaseTotals(uint PurchasedQuantity, uint SpentGil);
