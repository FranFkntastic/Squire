using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionLiveCandidatePlanner
{
    public static MarketAcquisitionLiveCandidatePlan BuildCandidatePlan(
        MarketAcquisitionRequestView request,
        MarketAcquisitionPlan plan,
        string currentWorld,
        uint itemId,
        IEnumerable<MarketBoardLiveListing> liveListings,
        uint alreadyPurchasedQuantity = 0,
        uint alreadySpentGil = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(liveListings);

        Validate(request, plan, currentWorld, itemId);
        return BuildCandidatePlanCore(
            request,
            currentWorld,
            itemId,
            liveListings,
            alreadyPurchasedQuantity,
            alreadySpentGil);
    }

    public static MarketAcquisitionLiveCandidatePlan BuildCandidatePlan(
        MarketAcquisitionRequestView request,
        MarketAcquisitionPlan plan,
        string currentWorld,
        MarketBoardReadResult readResult,
        uint alreadyPurchasedQuantity = 0,
        uint alreadySpentGil = 0)
    {
        ArgumentNullException.ThrowIfNull(readResult);

        Validate(request, plan, currentWorld, readResult.ItemId);
        EnsureFresh(readResult);
        return BuildCandidatePlanCore(
            request,
            currentWorld,
            readResult.ItemId,
            readResult.Listings,
            alreadyPurchasedQuantity,
            alreadySpentGil,
            readResult);
    }

    public static MarketAcquisitionLiveCandidatePlan BuildCandidatePlan(
        MarketAcquisitionRequestView request,
        MarketAcquisitionPlan plan,
        string currentWorld,
        MarketBoardAccumulatedReadResult accumulatedRead,
        uint alreadyPurchasedQuantity = 0,
        uint alreadySpentGil = 0)
    {
        ArgumentNullException.ThrowIfNull(accumulatedRead);

        return BuildCandidatePlan(
            request,
            plan,
            currentWorld,
            accumulatedRead.ToReadResult(),
            alreadyPurchasedQuantity,
            alreadySpentGil);
    }

    public static MarketAcquisitionLiveCandidatePlan BuildCandidatePlan(
        MarketAcquisitionRequestView request,
        MarketAcquisitionPlan plan,
        MarketAcquisitionWorldItemSubtask activeSubtask,
        string currentWorld,
        uint itemId,
        IEnumerable<MarketBoardLiveListing> liveListings,
        uint alreadyPurchasedQuantity = 0,
        uint alreadySpentGil = 0)
    {
        ArgumentNullException.ThrowIfNull(activeSubtask);

        Validate(request, plan, activeSubtask, currentWorld, itemId);
        return BuildCandidatePlanCore(
            request,
            currentWorld,
            itemId,
            liveListings,
            alreadyPurchasedQuantity,
            alreadySpentGil);
    }

    public static MarketAcquisitionLiveCandidatePlan BuildCandidatePlan(
        MarketAcquisitionRequestView request,
        MarketAcquisitionPlan plan,
        MarketAcquisitionWorldItemSubtask activeSubtask,
        string currentWorld,
        MarketBoardReadResult readResult,
        uint alreadyPurchasedQuantity = 0,
        uint alreadySpentGil = 0)
    {
        ArgumentNullException.ThrowIfNull(activeSubtask);
        ArgumentNullException.ThrowIfNull(readResult);

        Validate(request, plan, activeSubtask, currentWorld, readResult.ItemId);
        EnsureFresh(readResult);
        return BuildCandidatePlanCore(
            request,
            currentWorld,
            readResult.ItemId,
            readResult.Listings,
            alreadyPurchasedQuantity,
            alreadySpentGil,
            readResult);
    }

    public static MarketAcquisitionLiveCandidatePlan BuildCandidatePlan(
        MarketAcquisitionRequestView request,
        MarketAcquisitionPlan plan,
        MarketAcquisitionWorldItemSubtask activeSubtask,
        string currentWorld,
        MarketBoardAccumulatedReadResult accumulatedRead,
        uint alreadyPurchasedQuantity = 0,
        uint alreadySpentGil = 0)
    {
        ArgumentNullException.ThrowIfNull(accumulatedRead);

        return BuildCandidatePlan(
            request,
            plan,
            activeSubtask,
            currentWorld,
            accumulatedRead.ToReadResult(),
            alreadyPurchasedQuantity,
            alreadySpentGil);
    }

    private static MarketAcquisitionLiveCandidatePlan BuildCandidatePlanCore(
        MarketAcquisitionRequestView request,
        string currentWorld,
        uint itemId,
        IEnumerable<MarketBoardLiveListing> liveListings,
        uint alreadyPurchasedQuantity,
        uint alreadySpentGil,
        MarketBoardReadResult? readResult = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(liveListings);

        var mode = NormalizeQuantityMode(request.QuantityMode);
        var hasGilCap = request.MaxTotalGil > 0;
        var hasMaxQuantity = mode == "AllBelowThreshold" && request.Quantity > 0;
        var selectedQuantity = 0u;
        var selectedGil = 0u;
        var rows = new List<MarketAcquisitionLiveCandidateRow>();

        var realListings = liveListings
            .Where(MarketBoardListingIntegrity.IsRealListing)
            .Select(listing => ValidateLiveListing(listing, currentWorld, itemId))
            .OrderBy(listing => listing.UnitPrice)
            .ThenByDescending(listing => listing.Quantity)
            .ThenBy(listing => listing.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(listing => listing.ListingId, StringComparer.Ordinal)
            .ToList();
        var maxMeaningfulObservationUnitPrice = CalculateMeaningfulObservationUnitPriceThreshold(
            realListings,
            request.MaxUnitPrice);
        var candidates = realListings
            .Where(listing => MarketBoardListingIntegrity.IsMeaningfulObservation(listing, maxMeaningfulObservationUnitPrice))
            .ToList();

        foreach (var listing in candidates)
        {
            if (listing.UnitPrice > request.MaxUnitPrice)
            {
                rows.Add(Skipped(listing, "AboveThreshold", "Live unit price is above the configured max unit price.", selectedQuantity, selectedGil));
                continue;
            }

            if (!MarketAcquisitionPolicy.HqMatches(request.HqPolicy, listing.IsHq))
            {
                rows.Add(Skipped(listing, "HqPolicyMismatch", "Live HQ flag does not match the request HQ policy.", selectedQuantity, selectedGil));
                continue;
            }

            var runningTotalQuantity = checked(alreadyPurchasedQuantity + selectedQuantity);
            if (mode == "TargetQuantity" && runningTotalQuantity >= request.Quantity)
            {
                rows.Add(Skipped(listing, "TargetSatisfied", "Target quantity is already satisfied by cheaper confirmed live listings.", selectedQuantity, selectedGil));
                continue;
            }

            var nextSelectedQuantity = checked(selectedQuantity + listing.Quantity);
            var nextTotalQuantity = checked(alreadyPurchasedQuantity + nextSelectedQuantity);
            if (hasMaxQuantity && nextTotalQuantity > request.Quantity)
            {
                rows.Add(Skipped(listing, "MaxQuantityExceeded", "Buying this whole listing would exceed the configured max quantity.", selectedQuantity, selectedGil));
                continue;
            }

            var listingGil = checked(listing.UnitPrice * listing.Quantity);
            var nextSelectedGil = checked(selectedGil + listingGil);
            var nextTotalGil = checked(alreadySpentGil + nextSelectedGil);
            if (hasGilCap && nextTotalGil > request.MaxTotalGil)
            {
                rows.Add(Skipped(listing, "GilCapExceeded", "Buying this whole listing would exceed the configured gil cap.", selectedQuantity, selectedGil));
                continue;
            }

            selectedQuantity = nextSelectedQuantity;
            selectedGil = nextSelectedGil;
            rows.Add(new MarketAcquisitionLiveCandidateRow
            {
                Decision = "WouldBuy",
                Reason = "SafeLiveCandidate",
                Message = "Would buy this confirmed live listing in a guarded purchase pass.",
                LiveListing = listing,
                RunningQuantityAfter = selectedQuantity,
                RunningGilAfter = selectedGil,
            });
        }

        var status = ResolveStatus(
            mode,
            request.Quantity,
            checked(alreadyPurchasedQuantity + selectedQuantity),
            selectedQuantity,
            rows,
            readResult);
        return new MarketAcquisitionLiveCandidatePlan
        {
            Status = status,
            Message = ResolveMessage(status, mode, request.Quantity, alreadyPurchasedQuantity, selectedQuantity, readResult),
            ListingReadState = readResult?.ReadState ?? MarketBoardListingReadState.FreshComplete,
            RawItemIdMismatchCounts = readResult?.RawItemIdMismatchCounts ?? new Dictionary<uint, int>(),
            ReadableListingCount = readResult?.Listings.Count ?? rows.Count,
            ReportedListingCount = readResult?.ReportedListingCount ?? rows.Count,
            ListingCapacity = readResult?.ListingCapacity ?? rows.Count,
            IsVisibleListingCacheTruncated = readResult?.IsListingCountTruncated == true,
            RequestedQuantity = request.Quantity,
            WouldBuyQuantity = selectedQuantity,
            WouldSpendGil = selectedGil,
            Rows = rows,
        };
    }

    private static void Validate(
        MarketAcquisitionRequestView request,
        MarketAcquisitionPlan plan,
        string currentWorld,
        uint itemId)
    {
        if (string.IsNullOrWhiteSpace(currentWorld))
            throw new InvalidOperationException("Current market board world is required.");

        if (request.ItemId != itemId)
            throw new InvalidOperationException("Current market board search item does not match the acquisition request.");

        var batch = plan.WorldBatches.SingleOrDefault(batch => batch.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase));
        if (batch == null)
            throw new InvalidOperationException($"Current market board world {currentWorld} is not present in the prepared plan.");

        if (batch.ItemSubtasks.Count > 0 && !batch.ItemSubtasks.Any(subtask => subtask.ItemId == itemId))
            throw new InvalidOperationException("Current market board search item is not present in the active route stop.");

        if (batch.ItemSubtasks.Count == 0 && plan.ItemId != itemId)
            throw new InvalidOperationException("Current market board search item does not match the prepared plan item.");

        _ = NormalizeQuantityMode(request.QuantityMode);

        _ = MarketAcquisitionPolicy.NormalizeHqPolicy(request.HqPolicy);
    }

    private static void Validate(
        MarketAcquisitionRequestView request,
        MarketAcquisitionPlan plan,
        MarketAcquisitionWorldItemSubtask activeSubtask,
        string currentWorld,
        uint itemId)
    {
        if (string.IsNullOrWhiteSpace(currentWorld))
            throw new InvalidOperationException("Current market board world is required.");

        if (request.ItemId != itemId)
            throw new InvalidOperationException("Current market board search item does not match the acquisition request.");

        if (activeSubtask.ItemId != itemId)
            throw new InvalidOperationException("Current market board search item does not match the active route item.");

        if (!activeSubtask.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Current market board world {currentWorld} does not match the active route stop {activeSubtask.WorldName}.");

        if (!plan.WorldBatches.Any(batch => batch.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Current market board world {currentWorld} is not present in the prepared plan.");

        _ = NormalizeQuantityMode(request.QuantityMode);

        _ = MarketAcquisitionPolicy.NormalizeHqPolicy(request.HqPolicy);
    }

    private static void EnsureFresh(MarketBoardReadResult readResult)
    {
        if (!readResult.IsFresh)
        {
            throw new InvalidOperationException(
                $"Market board listings are not fresh enough to plan purchases: {readResult.Status}.");
        }
    }

    private static MarketBoardLiveListing ValidateLiveListing(
        MarketBoardLiveListing listing,
        string currentWorld,
        uint itemId)
    {
        if (listing.ItemId != itemId)
            throw new InvalidOperationException("Live market board rows include a different item id than the current search item.");

        if (!listing.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Live market board rows include a different world than the current market board world.");

        return listing;
    }

    private static string NormalizeQuantityMode(string quantityMode) =>
        quantityMode switch
        {
            "TargetQuantity" => "TargetQuantity",
            "AllBelowThreshold" => "AllBelowThreshold",
            _ => throw new InvalidOperationException($"Unknown quantity mode {quantityMode}."),
        };

    private static MarketAcquisitionLiveCandidateRow Skipped(
        MarketBoardLiveListing listing,
        string reason,
        string message,
        uint runningQuantity,
        uint runningGil) =>
        new()
        {
            Decision = "Skipped",
            Reason = reason,
            Message = message,
            LiveListing = listing,
            RunningQuantityAfter = runningQuantity,
            RunningGilAfter = runningGil,
        };

    private static string ResolveStatus(
        string mode,
        uint requestedQuantity,
        uint totalQuantityAfter,
        uint selectedQuantity,
        IReadOnlyList<MarketAcquisitionLiveCandidateRow> rows,
        MarketBoardReadResult? readResult)
    {
        if (selectedQuantity == 0)
        {
            if (readResult?.HasIncompleteCoverage == true)
                return MarketAcquisitionLiveCandidateStatuses.IncompleteListingCoverage;

            return MarketAcquisitionLiveCandidateStatuses.NoSafeListings;
        }

        if (mode == "TargetQuantity" && totalQuantityAfter < requestedQuantity)
            return MarketAcquisitionLiveCandidateStatuses.UnderProcured;

        return MarketAcquisitionLiveCandidateStatuses.Ready;
    }

    private static ulong CalculateMeaningfulObservationUnitPriceThreshold(
        IReadOnlyList<MarketBoardLiveListing> listings,
        uint configuredMaxUnitPrice)
    {
        if (listings.Count == 0)
            return ulong.MaxValue;

        var modeSource = configuredMaxUnitPrice == 0
            ? listings
            : listings
                .Where(listing => listing.UnitPrice <= (decimal)configuredMaxUnitPrice * 10m)
                .ToArray();
        var modePrice = modeSource.Count == 0 ? 0 : CalculateModePrice(modeSource);
        var baselinePrice = modePrice > 0
            ? modePrice
            : configuredMaxUnitPrice;

        if (baselinePrice <= 0)
            return ulong.MaxValue;

        return (ulong)Math.Ceiling(baselinePrice * 2.5m);
    }

    private static decimal CalculateModePrice(IReadOnlyList<MarketBoardLiveListing> listings)
    {
        var sortedByPrice = listings.OrderBy(listing => listing.UnitPrice).ToArray();
        var halfCount = Math.Max(1, sortedByPrice.Length / 2);
        var cheapestHalf = sortedByPrice.Take(halfCount).ToArray();
        var baselinePrice = cheapestHalf.Length > 0
            ? cheapestHalf.Average(listing => (decimal)listing.UnitPrice)
            : sortedByPrice[0].UnitPrice;
        var reasonableListings = listings
            .Where(listing => listing.UnitPrice <= baselinePrice * 10m)
            .ToArray();

        if (reasonableListings.Length == 0)
            reasonableListings = sortedByPrice.Take(3).ToArray();

        return reasonableListings
            .GroupBy(listing => listing.UnitPrice)
            .Select(group => new
            {
                Price = (decimal)group.Key,
                Quantity = group.Aggregate(0ul, (total, listing) => checked(total + listing.Quantity)),
            })
            .OrderByDescending(group => group.Quantity)
            .ThenBy(group => group.Price)
            .FirstOrDefault()
            ?.Price ?? 0m;
    }

    private static string ResolveMessage(
        string status,
        string mode,
        uint requestedQuantity,
        uint alreadyPurchasedQuantity,
        uint selectedQuantity,
        MarketBoardReadResult? readResult) =>
        status switch
        {
            MarketAcquisitionLiveCandidateStatuses.Ready when mode == "TargetQuantity" => $"Would satisfy target quantity with {alreadyPurchasedQuantity + selectedQuantity:N0}/{requestedQuantity:N0} confirmed live item(s).",
            MarketAcquisitionLiveCandidateStatuses.Ready => $"Would buy {selectedQuantity:N0} confirmed live item(s) below threshold.",
            MarketAcquisitionLiveCandidateStatuses.UnderProcured => $"Only {alreadyPurchasedQuantity + selectedQuantity:N0}/{requestedQuantity:N0} requested item(s) are safely available so far.",
            MarketAcquisitionLiveCandidateStatuses.IncompleteListingCoverage => $"No visible live listings satisfy the request constraints; the game reported {readResult?.ReportedListingCount ?? 0:N0} listing(s), but only {readResult?.Listings.Count ?? 0:N0} are currently readable from the visible listing cache.",
            _ => "No visible live listings satisfy the request constraints.",
        };
}
