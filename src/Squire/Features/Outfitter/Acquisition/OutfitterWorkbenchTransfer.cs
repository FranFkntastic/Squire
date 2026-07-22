using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Acquisition;

public sealed record OutfitterWorkbenchEvidenceLineage(
    Guid GenerationId,
    long Revision,
    string SchemaVersion,
    string SourceKey,
    string Region,
    OutfitterMarketCoverageMode CoverageMode,
    DateTimeOffset PublishedAtUtc);

public sealed record OutfitterWorkbenchMarketLot(
    EquipmentOfferKey OfferKey,
    string ItemName,
    uint RequiredQuantity,
    uint ObservedAvailableQuantity,
    string WorldName,
    uint ObservedUnitPriceGil,
    ulong ObservedTotalPriceGil,
    string DiscoveryObservationId,
    string SourceRevision,
    DateTimeOffset ReviewedAtUtc,
    string? RetainerName = null,
    string? RetainerId = null,
    string ItemKind = "Equipment");

public sealed record OutfitterWorkbenchSelectionLineage(
    EquipmentLoadoutPosition Position,
    EquipmentOfferKey OfferKey,
    uint Quantity,
    string? ObservationId,
    string SourceLabel);

/// <summary>
/// Immutable, non-persisted input for the existing Market Acquisition Workbench. It is not an
/// execution manifest, lease, request editor, or purchase authorization. In particular, observed
/// prices are evidence and do not imply a permitted deviation. A later M8 slice must persist this
/// lineage through Workbench finalization before Route may consume it.
/// </summary>
public sealed record OutfitterWorkbenchTransfer(
    string SchemaVersion,
    string Origin,
    string SelectedSolutionId,
    string? AdvisorNominationSolutionId,
    EquipmentUtilityProfileKey Profile,
    EquipmentUtilityContext Context,
    OutfitterWorkbenchEvidenceLineage Evidence,
    IReadOnlyList<OutfitterWorkbenchSelectionLineage> SelectedLoadout,
    IReadOnlyList<OutfitterWorkbenchMarketLot> MarketLots,
    ulong ObservedMarketTotalGil,
    bool DryRunOnly = false)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-outfitter-workbench-transfer/v1";
    public const string SquireOutfitterOrigin = "SquireOutfitter";
}

internal static class OutfitterWorkbenchTransferBuilder
{
    public static OutfitterWorkbenchTransfer Build(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        OutfitterMarketEvidenceBook evidence,
        OutfitterWorkbenchPlayerValidation playerValidation,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(advice);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedSolutionId);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(playerValidation);

        if (!playerValidation.IsCurrentFor(advice, selectedSolutionId, evidence))
            throw new InvalidOperationException("Workbench transfer requires a current player baseline revalidation for this exact advice, solution, and evidence generation.");

        if (advice is not { Status: MinerBotanistAdvisorStatus.Complete, Frontier: { } frontier })
            throw new InvalidOperationException("Workbench transfer requires complete read-only advisor evidence.");
        if (!evidence.IsPublishable || evidence.PublishedAtUtc is null || evidence.GenerationId == Guid.Empty || evidence.Revision <= 0)
            throw new InvalidOperationException("Workbench transfer requires a published, complete market evidence generation.");

        var selected = frontier.Pareto.Frontier.SingleOrDefault(solution =>
            string.Equals(solution.Candidate.SolutionId, selectedSolutionId, StringComparison.Ordinal));
        if (selected is null)
            throw new InvalidOperationException("The selected solution is not present in the authoritative frontier.");

        var selectedOffers = selected.Candidate.Selections
            .Select(selection => advice.OffersByAllocation.TryGetValue(selection.AllocationKey, out var offer)
                ? offer
                : throw new InvalidOperationException("The selected solution references an offer outside its authoritative offer book."))
            .DistinctBy(offer => offer.AllocationKey)
            .ToArray();
        var containsCraft = selectedOffers.Any(offer => offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.Craft);
        var craftHandoff = containsCraft
            ? OutfitterCraftHandoffProjection.Build(
                advice,
                selectedSolutionId,
                playerValidation.RecapturedBaseline
                    ?? throw new InvalidOperationException("Craft-material transfer requires a freshly recaptured player baseline."),
                evidence,
                (timeProvider ?? TimeProvider.System).GetUtcNow())
            : null;
        var marketLots = new List<OutfitterWorkbenchMarketLot>();
        var selectedLoadout = new List<OutfitterWorkbenchSelectionLineage>();
        foreach (var group in selected.Candidate.Selections.GroupBy(selection => selection.AllocationKey))
        {
            if (!advice.OffersByAllocation.TryGetValue(group.Key, out var offer))
                throw new InvalidOperationException("The selected solution references an offer outside its authoritative offer book.");
            var requiredQuantity = SumQuantity(group);
            if (requiredQuantity == 0 || requiredQuantity > offer.AvailableQuantity)
                throw new InvalidOperationException("The selected solution requires an unavailable offer quantity.");

            selectedLoadout.AddRange(group.Select(selection => new OutfitterWorkbenchSelectionLineage(
                selection.Position,
                selection.OfferKey,
                selection.Quantity,
                selection.ObservationId,
                offer.Offer.SourceLabel)));

            switch (offer.Offer.SourceKind)
            {
                case EquipmentAcquisitionSourceKind.Owned:
                case EquipmentAcquisitionSourceKind.GilVendor:
                    break;
                case EquipmentAcquisitionSourceKind.MarketBoard:
                    if (!containsCraft)
                        marketLots.Add(BuildMarketLot(offer, requiredQuantity, evidence));
                    break;
                case EquipmentAcquisitionSourceKind.Craft:
                    if (craftHandoff is null)
                        throw new InvalidOperationException("The selected craft source has no reviewed material handoff.");
                    break;
                default:
                    throw new InvalidOperationException("The selected solution contains an unsupported acquisition source.");
            }
        }

        if (craftHandoff is not null)
            marketLots.AddRange(craftHandoff.MarketMaterials.Select(material => BuildCraftMarketLot(material, evidence)));

        if (marketLots.Count == 0)
            throw new InvalidOperationException("The selected solution has no market acquisition to transfer to the Workbench.");

        var orderedSelections = selectedLoadout
            .OrderBy(selection => selection.Position)
            .ThenBy(selection => selection.OfferKey.ItemId)
            .ThenBy(selection => selection.OfferKey.Quality)
            .ThenBy(selection => selection.ObservationId, StringComparer.Ordinal)
            .ToArray();
        var orderedLots = marketLots
            .OrderBy(lot => lot.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(lot => lot.OfferKey.ItemId)
            .ThenBy(lot => lot.OfferKey.Quality)
            .ThenBy(lot => lot.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(lot => lot.DiscoveryObservationId, StringComparer.Ordinal)
            .ToArray();
        var total = orderedLots.Aggregate(0ul, (sum, lot) => checked(sum + lot.ObservedTotalPriceGil));
        return new(
            OutfitterWorkbenchTransfer.CurrentSchemaVersion,
            OutfitterWorkbenchTransfer.SquireOutfitterOrigin,
            selected.Candidate.SolutionId,
            advice.Nomination?.Candidate.SolutionId,
            selected.Utility.Profile,
            selected.Utility.Context,
            new(
                evidence.GenerationId,
                evidence.Revision,
                evidence.SchemaVersion,
                evidence.SourceKey,
                evidence.Region,
                evidence.Coverage.Mode,
                evidence.PublishedAtUtc.Value),
            orderedSelections,
            orderedLots,
            total,
            playerValidation.DryRunOnly);
    }

    private static OutfitterWorkbenchMarketLot BuildCraftMarketLot(
        OutfitterCraftHandoffMaterial material,
        OutfitterMarketEvidenceBook evidence)
    {
        if (material.Source is not OutfitterMarketMaterialSourceIdentity source ||
            source.EvidenceGenerationId != evidence.GenerationId ||
            source.EvidenceRevision != evidence.Revision ||
            material.PurchasedQuantity == 0 ||
            material.PurchasedQuantity > source.AvailableQuantity)
        {
            throw new InvalidOperationException("A selected craft material does not match current published market evidence.");
        }
        var item = evidence.Items.SingleOrDefault(candidate =>
            candidate.ItemId == material.ItemId && candidate.Status == OutfitterMarketEvidenceItemStatus.Fresh);
        var listing = item?.Listings.SingleOrDefault(candidate =>
            string.Equals(candidate.ListingId, source.ListingId, StringComparison.Ordinal) &&
            candidate.ItemId == material.ItemId &&
            candidate.Quality == material.Quality &&
            candidate.Quantity == source.AvailableQuantity &&
            candidate.UnitPriceGil == source.UnitPriceGil &&
            string.Equals(candidate.WorldName, source.WorldName, StringComparison.OrdinalIgnoreCase));
        if (listing is null)
            throw new InvalidOperationException("A selected craft-material listing is absent from current published evidence.");

        return new(
            new(
                material.ItemId,
                material.Quality,
                EquipmentAcquisitionSourceKind.MarketBoard,
                $"craft-material:{material.PlanIdentity.Sha256}:{source.ListingId}"),
            material.ItemName,
            material.PurchasedQuantity,
            source.AvailableQuantity,
            source.WorldName,
            source.UnitPriceGil,
            checked((ulong)source.UnitPriceGil * material.PurchasedQuantity),
            source.ListingId,
            source.SourceRevision,
            source.ReviewedAtUtc,
            listing.RetainerName,
            listing.RetainerId,
            "Crafting material");
    }

    private static OutfitterWorkbenchMarketLot BuildMarketLot(
        EquipmentExactSolverOffer offer,
        uint requiredQuantity,
        OutfitterMarketEvidenceBook evidence)
    {
        var observation = offer.Offer.GetValidatedObservation()
            ?? throw new InvalidOperationException("A selected market offer has no exact observation lineage.");
        var row = observation.ObservableMarketRow
            ?? throw new InvalidOperationException("A selected market offer has no observable market-row evidence.");
        if (observation.EvidenceGenerationId != evidence.GenerationId ||
            !string.Equals(observation.ObservationId, offer.ObservationId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(observation.World) ||
            requiredQuantity > row.Quantity)
        {
            throw new InvalidOperationException("A selected market offer does not match the accepted evidence generation or quantity.");
        }

        var itemEvidence = evidence.Items.SingleOrDefault(item =>
            item.ItemId == offer.Offer.Definition.ItemId &&
            item.Status == OutfitterMarketEvidenceItemStatus.Fresh)
            ?? throw new InvalidOperationException("A selected market offer is absent from fresh published item evidence.");
        var listing = itemEvidence.Listings.SingleOrDefault(candidate =>
            string.Equals(candidate.ListingId, row.RowId, StringComparison.Ordinal) &&
            candidate.ItemId == row.ItemId &&
            candidate.Quality == row.Quality &&
            candidate.Quantity == row.Quantity &&
            candidate.UnitPriceGil == row.UnitPriceGil &&
            string.Equals(candidate.WorldName, observation.World, StringComparison.OrdinalIgnoreCase));
        if (listing is null)
            throw new InvalidOperationException("A selected market offer tuple is absent from its published evidence generation.");

        return new(
            offer.Offer.Key,
            offer.Offer.Definition.Name,
            requiredQuantity,
            row.Quantity,
            listing.WorldName,
            row.UnitPriceGil,
            checked((ulong)row.UnitPriceGil * requiredQuantity),
            observation.ObservationId,
            listing.SourceRevision,
            listing.ListingReviewedAtUtc,
            listing.RetainerName,
            listing.RetainerId);
    }

    private static uint SumQuantity(IEnumerable<EquipmentLoadoutSelection> selections)
    {
        var total = 0u;
        foreach (var selection in selections)
            total = checked(total + selection.Quantity);
        return total;
    }
}
