using System;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketBoardPurchasePlanner
{
    public static MarketBoardPurchaseCandidate? SelectFirstCandidate(MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);

        var row = candidatePlan.Rows.FirstOrDefault(row =>
            row.Decision.Equals("WouldBuy", StringComparison.OrdinalIgnoreCase) &&
            MarketBoardListingIntegrity.IsRealListing(row.LiveListing));
        return row == null
            ? null
            : MarketBoardPurchaseCandidate.FromLiveListing(row.LiveListing);
    }

    public static MarketBoardPurchaseCandidate? SelectFirstFreshSafeCandidate(
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        MarketBoardReadResult freshRead)
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);
        ArgumentNullException.ThrowIfNull(freshRead);

        foreach (var freshListing in freshRead.Listings.Where(MarketBoardListingIntegrity.IsRealListing))
        {
            var safeRow = candidatePlan.Rows.FirstOrDefault(row =>
                row.Decision.Equals("WouldBuy", StringComparison.OrdinalIgnoreCase) &&
                MarketBoardListingIntegrity.IsRealListing(row.LiveListing) &&
                MarketBoardPurchaseCandidate.FromLiveListing(row.LiveListing).Matches(freshListing));
            if (safeRow != null)
                return MarketBoardPurchaseCandidate.FromLiveListing(freshListing);
        }

        return null;
    }

    public static MarketBoardPurchaseRevalidation RevalidateCandidate(
        MarketBoardPurchaseCandidate candidate,
        MarketBoardReadResult freshRead)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(freshRead);

        if (!MarketBoardListingIntegrity.IsRealCandidate(candidate))
        {
            return MarketBoardPurchaseRevalidation.Fail(
                "InvalidCandidate",
                "Guarded purchase candidate does not contain a real market-board listing identity.");
        }

        if (!freshRead.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
        {
            return MarketBoardPurchaseRevalidation.Fail(
                freshRead.Status,
                $"Fresh live listing read is not ready: {freshRead.Message}");
        }

        if (freshRead.ItemId != candidate.ItemId)
        {
            return MarketBoardPurchaseRevalidation.Fail(
                "WrongItem",
                "Fresh live listing read item id does not match the guarded purchase candidate.");
        }

        if (!freshRead.WorldName.Equals(candidate.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            return MarketBoardPurchaseRevalidation.Fail(
                "WrongWorld",
                "Fresh live listing read world does not match the guarded purchase candidate.");
        }

        var sameIdentity = freshRead.Listings.FirstOrDefault(listing =>
            MarketBoardListingIntegrity.IsRealListing(listing) &&
            listing.ListingId.Equals(candidate.ListingId, StringComparison.Ordinal) &&
            listing.RetainerId.Equals(candidate.RetainerId, StringComparison.Ordinal));
        if (sameIdentity == null)
        {
            return MarketBoardPurchaseRevalidation.Fail(
                "ListingMissing",
                "Fresh live listing read no longer contains the guarded purchase candidate.");
        }

        return candidate.Matches(sameIdentity)
            ? MarketBoardPurchaseRevalidation.Ready(candidate, sameIdentity)
            : MarketBoardPurchaseRevalidation.Fail(
                "ListingChanged",
                "Fresh live listing identity still exists, but item, price, quantity, world, or HQ changed.");
    }
}
