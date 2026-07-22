using System;

namespace MarketMafioso.Automation.MarketBoard;

public static class MarketBoardListingIntegrity
{
    public static bool IsRealListing(MarketBoardLiveListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return HasRealListingIdentity(
            listing.ListingId,
            listing.RetainerId,
            listing.UnitPrice,
            listing.Quantity);
    }

    public static bool IsMeaningfulObservation(
        MarketBoardLiveListing listing,
        ulong maxMeaningfulUnitPrice)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return listing.UnitPrice <= maxMeaningfulUnitPrice;
    }

    public static bool IsRealCandidate(MarketBoardPurchaseCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return HasRealListingIdentity(
            candidate.ListingId,
            candidate.RetainerId,
            candidate.UnitPrice,
            candidate.Quantity);
    }

    public static bool HasRealListingIdentity(
        string listingId,
        string retainerId,
        uint unitPrice,
        uint quantity) =>
        !string.IsNullOrWhiteSpace(listingId) &&
        !listingId.Equals("0", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(retainerId) &&
        !retainerId.Equals("0", StringComparison.Ordinal) &&
        unitPrice > 0 &&
        quantity > 0;
}
