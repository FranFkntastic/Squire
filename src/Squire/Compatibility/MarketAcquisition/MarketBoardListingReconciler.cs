using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketBoardListingReconciler
{
    public static MarketBoardListingReconciliation Reconcile(
        MarketAcquisitionPlan plan,
        string currentWorld,
        uint itemId,
        IEnumerable<MarketBoardLiveListing> liveListings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(liveListings);

        if (string.IsNullOrWhiteSpace(currentWorld))
            throw new InvalidOperationException("Current market board world is required.");

        var batch = plan.WorldBatches.SingleOrDefault(
            candidate => candidate.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase));
        if (batch == null)
            throw new InvalidOperationException($"Current market board world {currentWorld} is not present in the prepared plan.");

        if (batch.ItemSubtasks.Count > 0 && !batch.ItemSubtasks.Any(subtask => subtask.ItemId == itemId))
            throw new InvalidOperationException("Current market board search item is not present in the active route stop.");

        if (batch.ItemSubtasks.Count == 0 && plan.ItemId != itemId)
            throw new InvalidOperationException("Current market board search item does not match the prepared plan item.");

        var live = liveListings.ToList();
        if (live.Any(listing => listing.ItemId != itemId))
            throw new InvalidOperationException("Live market board rows include a different item id than the current search item.");

        if (live.Any(listing => !listing.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Live market board rows include a different world than the current market board world.");

        var rows = batch.Listings
            .Select(planned => ReconcileListing(planned, live))
            .ToList();

        return new MarketBoardListingReconciliation
        {
            Status = rows.All(row => row.Status == "Matched") ? "Ready" : "Blocked",
            Listings = rows,
        };
    }

    public static MarketBoardListingReconciliation Reconcile(
        MarketAcquisitionPlan plan,
        MarketAcquisitionWorldItemSubtask activeSubtask,
        string currentWorld,
        uint itemId,
        IEnumerable<MarketBoardLiveListing> liveListings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(activeSubtask);
        ArgumentNullException.ThrowIfNull(liveListings);

        if (string.IsNullOrWhiteSpace(currentWorld))
            throw new InvalidOperationException("Current market board world is required.");

        if (activeSubtask.ItemId != itemId)
            throw new InvalidOperationException("Current market board search item does not match the active route item.");

        if (!activeSubtask.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Current market board world {currentWorld} does not match the active route stop {activeSubtask.WorldName}.");

        if (!plan.WorldBatches.Any(batch => batch.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Current market board world {currentWorld} is not present in the prepared plan.");

        var live = liveListings.ToList();
        if (live.Any(listing => listing.ItemId != itemId))
            throw new InvalidOperationException("Live market board rows include a different item id than the current search item.");

        if (live.Any(listing => !listing.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Live market board rows include a different world than the current market board world.");

        var rows = activeSubtask.Listings
            .Select(planned => ReconcileListing(planned, live))
            .ToList();

        return new MarketBoardListingReconciliation
        {
            Status = rows.All(row => row.Status == "Matched") ? "Ready" : "Blocked",
            Listings = rows,
        };
    }

    private static MarketBoardListingReconciliationRow ReconcileListing(
        MarketAcquisitionPlannedListing planned,
        IReadOnlyList<MarketBoardLiveListing> liveListings)
    {
        var live = liveListings.FirstOrDefault(candidate =>
            candidate.ListingId.Equals(planned.ListingId, StringComparison.Ordinal) &&
            candidate.RetainerId.Equals(planned.RetainerId, StringComparison.Ordinal));

        if (live == null)
        {
            return new MarketBoardListingReconciliationRow
            {
                Status = "Missing",
                Message = "Planned listing is not visible in the live market board rows.",
                PlannedListing = planned,
            };
        }

        if (live.UnitPrice != planned.UnitPrice)
            return Mismatch(planned, live, "PriceChanged", "Live unit price no longer matches the planned unit price.");

        if (live.Quantity != planned.Quantity)
            return Mismatch(planned, live, "QuantityChanged", "Live quantity no longer matches the planned listing quantity.");

        if (live.IsHq != planned.IsHq)
            return Mismatch(planned, live, "QualityChanged", "Live HQ flag no longer matches the planned listing.");

        return new MarketBoardListingReconciliationRow
        {
            Status = "Matched",
            Message = "Live listing matches the planned listing.",
            IsExactMatch = true,
            PlannedListing = planned,
            LiveListing = live,
        };
    }

    private static MarketBoardListingReconciliationRow Mismatch(
        MarketAcquisitionPlannedListing planned,
        MarketBoardLiveListing live,
        string status,
        string message) =>
        new()
        {
            Status = status,
            Message = message,
            PlannedListing = planned,
            LiveListing = live,
        };
}
