using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.Squire.Outfitter.MarketEvidence;

namespace MarketMafioso.Squire.Outfitter.Acquisition;

public sealed record OutfitterWorkbenchReplacementRow(
    OutfitterWorkbenchMarketLot AcceptedLot,
    OutfitterMarketListingEvidence Listing,
    bool IsSameWorld,
    bool CanFulfillRequiredQuantityAlone,
    long UnitPriceDeltaGil,
    ulong RequiredQuantityCostGil,
    decimal RequiredQuantityCostDeltaGil);

/// <summary>
/// Enumerates observable, exact-quality rows that could inform recovery. This is deliberately not
/// an approval filter or route planner: partial rows remain visible, no deviation is called safe,
/// and no replacement is selected.
/// </summary>
public static class OutfitterWorkbenchReplacementRows
{
    public static IReadOnlyList<OutfitterWorkbenchReplacementRow> Enumerate(
        OutfitterWorkbenchTransfer transfer,
        OutfitterMarketEvidenceBook currentEvidence)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(currentEvidence);
        _ = OutfitterWorkbenchTransferReviewer.Review(transfer, currentEvidence);

        var rows = new List<OutfitterWorkbenchReplacementRow>();
        foreach (var accepted in transfer.MarketLots)
        {
            var item = currentEvidence.Items.SingleOrDefault(candidate =>
                candidate.ItemId == accepted.OfferKey.ItemId &&
                candidate.Status == OutfitterMarketEvidenceItemStatus.Fresh);
            if (item is null)
                continue;
            foreach (var listing in item.Listings.Where(candidate =>
                          candidate.ItemId == accepted.OfferKey.ItemId &&
                          candidate.Quality == accepted.OfferKey.Quality &&
                          !(string.Equals(candidate.ListingId, accepted.DiscoveryObservationId, StringComparison.Ordinal) &&
                            string.Equals(candidate.WorldName, accepted.WorldName, StringComparison.OrdinalIgnoreCase))))
            {
                var requiredCost = checked((ulong)listing.UnitPriceGil * accepted.RequiredQuantity);
                rows.Add(new(
                    accepted,
                    listing,
                    string.Equals(listing.WorldName, accepted.WorldName, StringComparison.OrdinalIgnoreCase),
                    listing.Quantity >= accepted.RequiredQuantity,
                    (long)listing.UnitPriceGil - accepted.ObservedUnitPriceGil,
                    requiredCost,
                    (decimal)requiredCost - accepted.ObservedTotalPriceGil));
            }
        }

        return rows
            .OrderBy(row => row.AcceptedLot.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(row => row.IsSameWorld)
            .ThenByDescending(row => row.CanFulfillRequiredQuantityAlone)
            .ThenBy(row => row.RequiredQuantityCostGil)
            .ThenBy(row => row.Listing.ListingId, StringComparer.Ordinal)
            .ToArray();
    }
}
