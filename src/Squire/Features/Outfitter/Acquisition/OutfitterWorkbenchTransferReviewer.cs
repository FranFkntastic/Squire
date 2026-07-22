using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.MarketEvidence;

namespace MarketMafioso.Squire.Outfitter.Acquisition;

[Flags]
public enum OutfitterWorkbenchLotChange
{
    None = 0,
    EvidenceGenerationChanged = 1 << 0,
    EvidenceRevisionChanged = 1 << 1,
    ListingMissing = 1 << 2,
    ExactQualityChanged = 1 << 3,
    WorldChanged = 1 << 4,
    UnitPriceIncreased = 1 << 5,
    UnitPriceDecreased = 1 << 6,
    AvailableQuantityIncreased = 1 << 7,
    AvailableQuantityDecreased = 1 << 8,
    RequiredQuantityUnavailable = 1 << 9,
    SourceRevisionChanged = 1 << 10,
}

public sealed record OutfitterWorkbenchMarketLotReview(
    OutfitterWorkbenchMarketLot AcceptedLot,
    OutfitterMarketListingEvidence? CurrentListing,
    OutfitterWorkbenchLotChange Changes);

/// <summary>
/// Describes current market evidence relative to an accepted transfer. It deliberately does not
/// decide whether a change is acceptable, mutate the Workbench, substitute another listing, or
/// authorize Route execution.
/// </summary>
public sealed record OutfitterWorkbenchTransferReview(
    OutfitterWorkbenchEvidenceLineage CurrentEvidence,
    IReadOnlyList<OutfitterWorkbenchMarketLotReview> Lots);

public static class OutfitterWorkbenchTransferReviewer
{
    public static OutfitterWorkbenchTransferReview Review(
        OutfitterWorkbenchTransfer transfer,
        OutfitterMarketEvidenceBook currentEvidence)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(currentEvidence);
        if (!string.Equals(
                transfer.SchemaVersion,
                OutfitterWorkbenchTransfer.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Outfitter Workbench transfer schema is unsupported.");
        }

        if (!currentEvidence.IsPublishable ||
            currentEvidence.PublishedAtUtc is null ||
            currentEvidence.GenerationId == Guid.Empty ||
            currentEvidence.Revision <= 0)
        {
            throw new InvalidOperationException("Transfer review requires published, complete current market evidence.");
        }
        if (!string.Equals(currentEvidence.SchemaVersion, transfer.Evidence.SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(currentEvidence.SourceKey, transfer.Evidence.SourceKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(currentEvidence.Region, transfer.Evidence.Region, StringComparison.OrdinalIgnoreCase) ||
            currentEvidence.Coverage.Mode != transfer.Evidence.CoverageMode)
        {
            throw new InvalidOperationException("Current market evidence does not match the accepted transfer scope.");
        }

        var queriedItemIds = currentEvidence.Coverage.QueriedItemIds.ToHashSet();
        if (transfer.MarketLots.Any(lot => !queriedItemIds.Contains(lot.OfferKey.ItemId)))
            throw new InvalidOperationException("Current market evidence did not query every accepted market item.");

        var reviews = transfer.MarketLots.Select(lot => ReviewLot(transfer, lot, currentEvidence)).ToArray();
        return new(
            new(
                currentEvidence.GenerationId,
                currentEvidence.Revision,
                currentEvidence.SchemaVersion,
                currentEvidence.SourceKey,
                currentEvidence.Region,
                currentEvidence.Coverage.Mode,
                currentEvidence.PublishedAtUtc.Value),
            reviews);
    }

    private static OutfitterWorkbenchMarketLotReview ReviewLot(
        OutfitterWorkbenchTransfer transfer,
        OutfitterWorkbenchMarketLot lot,
        OutfitterMarketEvidenceBook currentEvidence)
    {
        var changes = OutfitterWorkbenchLotChange.None;
        if (transfer.Evidence.GenerationId != currentEvidence.GenerationId)
            changes |= OutfitterWorkbenchLotChange.EvidenceGenerationChanged;
        if (transfer.Evidence.Revision != currentEvidence.Revision)
            changes |= OutfitterWorkbenchLotChange.EvidenceRevisionChanged;

        var item = currentEvidence.Items.SingleOrDefault(candidate =>
            candidate.ItemId == lot.OfferKey.ItemId &&
            candidate.Status == OutfitterMarketEvidenceItemStatus.Fresh);
        var listing = item?.Listings.SingleOrDefault(candidate =>
            string.Equals(candidate.ListingId, lot.DiscoveryObservationId, StringComparison.Ordinal) &&
            string.Equals(candidate.WorldName, lot.WorldName, StringComparison.OrdinalIgnoreCase));
        if (listing is null)
            return new(lot, null, changes | OutfitterWorkbenchLotChange.ListingMissing);

        if (listing.Quality != lot.OfferKey.Quality)
            changes |= OutfitterWorkbenchLotChange.ExactQualityChanged;
        if (!string.Equals(listing.WorldName, lot.WorldName, StringComparison.OrdinalIgnoreCase))
            changes |= OutfitterWorkbenchLotChange.WorldChanged;
        if (listing.UnitPriceGil > lot.ObservedUnitPriceGil)
            changes |= OutfitterWorkbenchLotChange.UnitPriceIncreased;
        else if (listing.UnitPriceGil < lot.ObservedUnitPriceGil)
            changes |= OutfitterWorkbenchLotChange.UnitPriceDecreased;
        if (listing.Quantity > lot.ObservedAvailableQuantity)
            changes |= OutfitterWorkbenchLotChange.AvailableQuantityIncreased;
        else if (listing.Quantity < lot.ObservedAvailableQuantity)
            changes |= OutfitterWorkbenchLotChange.AvailableQuantityDecreased;
        if (listing.Quantity < lot.RequiredQuantity)
            changes |= OutfitterWorkbenchLotChange.RequiredQuantityUnavailable;
        if (!string.Equals(listing.SourceRevision, lot.SourceRevision, StringComparison.Ordinal))
            changes |= OutfitterWorkbenchLotChange.SourceRevisionChanged;

        return new(lot, listing, changes);
    }
}
