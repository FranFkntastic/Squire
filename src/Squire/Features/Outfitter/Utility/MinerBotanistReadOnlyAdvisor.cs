using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.MarketEvidence;

namespace MarketMafioso.Squire.Outfitter.Utility;

public enum MinerBotanistAdvisorStatus
{
    Complete,
    Abstained,
}

public sealed record MinerBotanistReadOnlyAdvice(
    MinerBotanistAdvisorStatus Status,
    string AdvisoryRule,
    EquipmentExactFrontierResult? Frontier,
    EquipmentDecisionSolution? Nomination,
    IReadOnlyDictionary<string, AdvisorAuthorityAssessment> AuthorityBySolutionId,
    IReadOnlyDictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer> OffersByAllocation,
    string Diagnostic)
{
    internal IReadOnlyDictionary<EquipmentOfferAllocationKey, OutfitterCraftAdvisorOffer> CraftOffersByAllocation { get; init; } =
        new Dictionary<EquipmentOfferAllocationKey, OutfitterCraftAdvisorOffer>();
}

public sealed record MinerBotanistOwnedItemEvidence(
    uint ItemId,
    bool IsHighQuality,
    string ContainerLabel,
    EquipmentInstanceSnapshot? Instance = null,
    bool UtilityIsExact = true);

/// <summary>
/// Read-only player advisor. It consumes one reconciled player baseline, exact-quality market
/// evidence, version-matched static item definitions, and owned-inventory evidence from the same
/// atomic character equipment snapshot.
/// </summary>
public sealed class MinerBotanistReadOnlyAdvisor
{
    public const string AdvisoryRule =
        "Prefer the least-cost complete loadout that gains a supported capability. " +
        "Abstain when evidence is incomplete, the stat trade is context-dependent, or a paid gain remains entirely inside monotonic score space.";

    internal MinerBotanistReadOnlyAdvice Build(
        PlayerAdvisorBaseline baseline,
        OutfitterMarketEvidenceBook marketEvidence,
        Func<uint, IReadOnlyList<EquipmentItemDefinition>> findDefinitionsByItemId,
        IAdvisorStatFamily family,
        string contextId,
        IReadOnlyList<EquipmentLoadoutOffer>? vendorOffers = null,
        IReadOnlyList<MinerBotanistOwnedItemEvidence>? ownedItems = null,
        CancellationToken cancellationToken = default,
        Action<EquipmentExactFrontierProgress>? reportProgress = null,
        Action<IAdvisorSolverReplay>? captureReplay = null,
        bool ownedInventoryCoverageComplete = true,
        IReadOnlyList<OutfitterCraftAdvisorOffer>? craftOffers = null,
        IReadOnlySet<uint>? equipmentMarketScope = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(marketEvidence);
        ArgumentNullException.ThrowIfNull(findDefinitionsByItemId);
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(contextId);
        if (baseline is not { Status: PlayerAdvisorBaselineStatus.Complete, Character: not null, ClassJobId: { } classJobId,
                Level: { } characterLevel, EffectiveLevel: not null, IsLevelSynced: not null } ||
            characterLevel is < 1 or > 100 || baseline.EquippedSlots.Count != PlayerAdvisorEquippedSlotMap.All.Count)
            return Abstain("A complete level 1-100 player baseline and twelve resolved equipped slots are required.");
        if (!family.SupportedClassJobIds.Contains(classJobId))
            return Abstain($"The active job is outside the supported {family.CoverageJobLabel} player scope.");
        if (family.RelevantSemantics.Any(semantic =>
                !baseline.TotalStats.ContainsKey(semantic) || !baseline.FixedStats.ContainsKey(semantic)))
            return Abstain("The player baseline is missing one or more family-relevant semantic totals.");
        if (!marketEvidence.IsPublishable)
        {
            var unresolved = marketEvidence.Items
                .Where(item => item.Status is not (OutfitterMarketEvidenceItemStatus.Fresh or OutfitterMarketEvidenceItemStatus.Missing))
                .Take(8)
                .Select(item => $"{item.ItemId}:{item.Status}{(string.IsNullOrWhiteSpace(item.Diagnostic) ? string.Empty : $" ({item.Diagnostic})")}")
                .ToArray();
            var detail = unresolved.Length == 0
                ? string.Empty
                : $" Unresolved items: {string.Join("; ", unresolved)}.";
            return Abstain($"The exact-quality market evidence generation is incomplete or stale; the advisor will not nominate from it.{detail}");
        }
        var ineligibleCurrent = baseline.EquippedSlots.FirstOrDefault(value =>
            value.Definition is { } definition &&
            (definition.EquipLevel > characterLevel || !definition.EligibleClassJobIds.Contains(classJobId)));
        if (ineligibleCurrent is not null)
            return Abstain($"Currently equipped {ineligibleCurrent.Definition!.Name} does not match the active job and level.");
        var unsupportedCurrent = baseline.EquippedSlots.FirstOrDefault(value =>
            value.Definition is { } definition && AdvisorEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(definition));
        if (unsupportedCurrent is not null)
            return Abstain($"Currently equipped {unsupportedCurrent.Definition!.Name} has an unmodeled effect or equip restriction.");

        var offerSemantics = family.RelevantSemantics.ToDictionary(
            semantic => semantic,
            semantic => checked(baseline.TotalStats[semantic] - baseline.FixedStats[semantic]));
        var profile = family.CreateUtilityModel(
            contextId,
            offerSemantics,
            baseline.FixedStats,
            classJobId,
            checked((uint)characterLevel));
        var offers = new List<EquipmentExactSolverOffer>();
        var hasUnprovenRelevantOwnedUtility = false;
        var savedGearsetBaseline = baseline.Target?.Kind == PlayerAdvisorBaselineTargetKind.SavedGearset;
        var required = baseline.EquippedSlots.Select(value => value.Position).ToHashSet();
        var baselineKeys = required.ToDictionary(position => position, _ => (EquipmentOfferAllocationKey?)null);
        foreach (var slot in baseline.EquippedSlots)
        {
            if (slot is not { Definition: { } definition, Instance: { } instance, Quality: { } quality })
                continue;
            var sourceKey = savedGearsetBaseline
                ? $"saved-gearset-baseline:{slot.PositionKey}"
                : $"player-current:{slot.PositionKey}";
            var currentOffer = new EquipmentLoadoutOffer(
                definition,
                EquipmentAcquisitionSourceKind.Owned,
                savedGearsetBaseline ? "Saved gearset · exact owned instance" : "Currently equipped · player state",
                UnitPriceGil: 0,
                Instance: instance,
                PriceIsEstimate: false,
                Quality: quality,
                SourceCatalogKey: sourceKey);
            var occupiedPositions = new HashSet<EquipmentLoadoutPosition> { slot.Position };
            var exact = new EquipmentExactSolverOffer(
                currentOffer,
                sourceKey,
                occupiedPositions,
                1,
                slot.Utility,
                0,
                null,
                null,
                0,
                new(0, 0, 0),
                [savedGearsetBaseline ? "Saved gearset" : "Currently equipped", quality == EquipmentQuality.High ? "HQ" : "NQ"]);
            offers.Add(exact);
            foreach (var occupied in occupiedPositions.Where(baselineKeys.ContainsKey))
                baselineKeys[occupied] = exact.AllocationKey;
        }

        foreach (var owned in ownedItems ?? [])
        {
            // Owned items span every job the target plays; anything not eligible for the
            // current target is skipped silently rather than treated as an evidence fault.
            var ownedDefinitions = findDefinitionsByItemId(owned.ItemId)
                .Where(value => value.ItemId == owned.ItemId && value.EquipLevel <= characterLevel && value.EligibleClassJobIds.Contains(classJobId))
                .ToArray();
            if (ownedDefinitions.Length != 1)
                continue;
            var ownedDefinition = ownedDefinitions[0];
            if (AdvisorEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(ownedDefinition) ||
                !MinerBotanistAdvisorCatalog.HasRelevantCompleteProfile(ownedDefinition, family))
                continue;
            var ownedQuality = owned.IsHighQuality ? EquipmentQuality.High : EquipmentQuality.Normal;
            var ownedProfile = ownedDefinition.ResolveStatProfile(ownedQuality);
            if (ownedProfile is not { IsComplete: true })
                continue;
            var ownedPositions = Positions(ownedDefinition);
            if (ownedPositions.Count == 0 || !ownedPositions.Overlaps(required))
                continue;
            if (!owned.UtilityIsExact)
            {
                hasUnprovenRelevantOwnedUtility = true;
                continue;
            }
            var ownedOffer = new EquipmentLoadoutOffer(
                ownedDefinition,
                EquipmentAcquisitionSourceKind.Owned,
                $"Owned · {owned.ContainerLabel}",
                0,
                Instance: owned.Instance,
                PriceIsEstimate: false,
                Quality: ownedQuality,
                SourceCatalogKey: owned.Instance is null
                    ? $"owned-fixture:{owned.ContainerLabel}:{ownedDefinition.ItemId}:{ownedQuality}"
                    : null);
            offers.Add(new(
                ownedOffer,
                null,
                ownedPositions,
                1,
                family.VectorFromDefinition(ownedProfile),
                0,
                null,
                null,
                0,
                new(0, 0, 0),
                [ownedQuality == EquipmentQuality.High ? "HQ" : "NQ", "Owned"]));
        }

        foreach (var itemEvidence in marketEvidence.Items.Where(value =>
                     value.Status == OutfitterMarketEvidenceItemStatus.Fresh &&
                     (equipmentMarketScope is null || equipmentMarketScope.Contains(value.ItemId))))
        {
            var definitions = findDefinitionsByItemId(itemEvidence.ItemId)
                .Where(value => value.ItemId == itemEvidence.ItemId && value.EquipLevel <= characterLevel && value.EligibleClassJobIds.Contains(classJobId))
                .ToArray();
            if (definitions.Length != 1)
                return Abstain($"Market item {itemEvidence.ItemId} did not resolve to exactly one eligible static equipment definition.");
            var definition = definitions[0];
            if (AdvisorEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(definition))
                return Abstain($"{definition.Name} has an unmodeled effect or equip restriction.");
            foreach (var listing in RelevantListings(itemEvidence.Listings))
            {
                var profileForQuality = definition.ResolveStatProfile(listing.Quality);
                if (profileForQuality is not { IsComplete: true })
                    return Abstain($"{definition.Name} has no complete {listing.Quality} stat profile.");
                var positions = Positions(definition);
                if (positions.Count == 0 || !positions.Overlaps(required))
                    continue;
                var sourceCatalogKey = $"market:{marketEvidence.SourceKey}:{listing.WorldId}:{definition.ItemId}:{listing.Quality}";
                var key = new EquipmentOfferKey(definition.ItemId, listing.Quality, EquipmentAcquisitionSourceKind.MarketBoard, sourceCatalogKey);
                var observation = new EquipmentOfferObservation(
                    key,
                    marketEvidence.GenerationId,
                    listing.ListingId,
                    listing.ListingReviewedAtUtc,
                    ObservableMarketRow: new(
                        listing.ListingId,
                        listing.ItemId,
                        listing.Quality,
                        listing.Quantity,
                        listing.UnitPriceGil,
                        listing.WorldName,
                        listing.RetainerName),
                    World: listing.WorldName,
                    AvailableQuantity: listing.Quantity,
                    UnitPriceGil: listing.UnitPriceGil);
                var offer = new EquipmentLoadoutOffer(
                    definition,
                    EquipmentAcquisitionSourceKind.MarketBoard,
                    $"Market board · {listing.WorldName}",
                    listing.UnitPriceGil,
                    PriceIsEstimate: false,
                    Quality: listing.Quality,
                    SourceCatalogKey: sourceCatalogKey,
                    Observation: observation);
                offers.Add(new(
                    offer,
                    listing.ListingId,
                    positions,
                    listing.Quantity,
                    family.VectorFromDefinition(profileForQuality),
                    listing.UnitPriceGil,
                    listing.WorldName,
                    null,
                    1,
                    new(0, 0, 0),
                    [listing.Quality == EquipmentQuality.High ? "HQ" : "NQ", listing.WorldName]));
            }
        }

        foreach (var vendor in vendorOffers ?? [])
        {
            if (vendor.SourceKind != EquipmentAcquisitionSourceKind.GilVendor || vendor.UnitPriceGil is not { } price ||
                !vendor.Definition.EligibleClassJobIds.Contains(classJobId) || vendor.Definition.EquipLevel > characterLevel)
                return Abstain("Vendor offer evidence did not match the supported rendered MIN/BTN target.");
            if (AdvisorEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(vendor.Definition))
                return Abstain($"{vendor.Definition.Name} has an unmodeled effect or equip restriction.");
            var statProfile = vendor.ResolveStatProfile();
            if (statProfile is not { IsComplete: true })
                return Abstain($"{vendor.Definition.Name} has no complete {vendor.ResolvedQuality} stat profile.");
            var positions = Positions(vendor.Definition);
            if (positions.Count == 0 || !positions.Overlaps(required))
                continue;
            offers.Add(new(
                vendor,
                null,
                positions,
                1,
                family.VectorFromDefinition(statProfile),
                price,
                null,
                vendor.Key.SourceCatalogKey,
                1,
                new(0, 0, 0),
                [vendor.ResolvedQuality == EquipmentQuality.High ? "HQ" : "NQ", vendor.SourceLabel]));
        }

        foreach (var craft in craftOffers ?? [])
        {
            if (craft is null || !craft.Matches(baseline, marketEvidence))
                return Abstain("A passive craft offer no longer matches the active crafter or market-evidence authority.");
            if (!craft.SolverOffer.Offer.Definition.EligibleClassJobIds.Contains(classJobId) ||
                craft.SolverOffer.Offer.Definition.EquipLevel > characterLevel)
            {
                return Abstain("A passive craft offer does not match the supported active Advisor target.");
            }
            offers.Add(craft.SolverOffer);
        }

        EquipmentExactFrontierResult frontier;
        try
        {
            var request = new EquipmentExactFrontierRequest(
                offers,
                required,
                baselineKeys,
                profile);
            var replay = family.CaptureReplay(
                request,
                contextId,
                classJobId,
                checked((uint)characterLevel),
                offerSemantics,
                baseline.FixedStats);
            if (replay is not null)
                captureReplay?.Invoke(replay);
            frontier = new EquipmentExactFrontierSolver().Solve(request, cancellationToken, reportProgress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Abstain($"Exact frontier construction failed safely: {ex.Message}");
        }

        var craftOffersByAllocation = (craftOffers ?? [])
            .ToDictionary(craft => craft.SolverOffer.AllocationKey);
        var authority = frontier.Pareto.Frontier.ToDictionary(
            solution => solution.Candidate.SolutionId,
            solution =>
            {
                var assessment = family.AssessAuthority(profile, solution.Utility, solution.AcquisitionCostGil);
                var selectedCraftOffers = solution.Candidate.Selections
                    .Select(selection => selection.AllocationKey)
                    .Distinct()
                    .Select(key => craftOffersByAllocation.GetValueOrDefault(key))
                    .Where(craft => craft is not null)
                    .Cast<OutfitterCraftAdvisorOffer>()
                    .ToArray();
                var duplicateCraftListing = selectedCraftOffers
                    .SelectMany(craft => craft.Source.Plan.TerminalMaterials)
                    .Select(line => line.Source)
                    .OfType<OutfitterMarketMaterialSourceIdentity>()
                    .GroupBy(source => source.PhysicalSourceKey)
                    .Any(group => group.Count() != 1);
                if (duplicateCraftListing)
                {
                    assessment = assessment with
                    {
                        AdvisorMayConsider = false,
                        Reasons = [.. assessment.Reasons, "Selected craft plans reuse one indivisible market listing; this solution cannot be nominated or handed off."],
                    };
                }
                var paidOwnedEvidenceReason = !ownedInventoryCoverageComplete
                    ? "Owned inventory coverage is partial; the advisor will not nominate a paid loadout that may duplicate available gear."
                    : hasUnprovenRelevantOwnedUtility
                        ? "A relevant unequipped owned item has melds whose exact effective utility is unproven; it was excluded and paid nomination is blocked."
                        : null;
                return paidOwnedEvidenceReason is not null && solution.AcquisitionCostGil > 0 && assessment.AdvisorMayConsider
                    ? assessment with
                    {
                        AdvisorMayConsider = false,
                        Reasons = [.. assessment.Reasons, paidOwnedEvidenceReason],
                    }
                    : assessment;
            },
            StringComparer.Ordinal);
        var nomination = frontier.Pareto.Frontier
            .Where(solution => authority[solution.Candidate.SolutionId].AdvisorMayConsider)
            .OrderBy(solution => solution.AcquisitionCostGil)
            .ThenBy(solution => solution.Burden.WorldVisits)
            .ThenBy(solution => solution.Burden.PurchaseTransactions)
            .ThenByDescending(solution => solution.Utility.UtilityScore)
            .FirstOrDefault();
        return new(
            MinerBotanistAdvisorStatus.Complete,
            AdvisoryRule,
            frontier,
            nomination,
            authority,
            offers.ToDictionary(value => value.AllocationKey),
            nomination is null
                ? "Frontier is complete, but the advisor abstains under the displayed rule."
                : $"Advisor nominates {nomination.Candidate.SolutionId} under the displayed rule.")
        {
            CraftOffersByAllocation = craftOffersByAllocation,
        };
    }

    internal static HashSet<EquipmentLoadoutPosition> Positions(EquipmentItemDefinition definition) => definition.Slot switch
    {
        EquipmentSlot.MainHand => [EquipmentLoadoutPosition.MainHand],
        EquipmentSlot.OffHand => [EquipmentLoadoutPosition.OffHand],
        EquipmentSlot.Head => [EquipmentLoadoutPosition.Head],
        EquipmentSlot.Body => [EquipmentLoadoutPosition.Body],
        EquipmentSlot.Hands => [EquipmentLoadoutPosition.Hands],
        EquipmentSlot.Legs => [EquipmentLoadoutPosition.Legs],
        EquipmentSlot.Feet => [EquipmentLoadoutPosition.Feet],
        EquipmentSlot.Ears => [EquipmentLoadoutPosition.Ears],
        EquipmentSlot.Neck => [EquipmentLoadoutPosition.Neck],
        EquipmentSlot.Wrists => [EquipmentLoadoutPosition.Wrists],
        EquipmentSlot.Ring => [EquipmentLoadoutPosition.LeftRing, EquipmentLoadoutPosition.RightRing],
        _ => [],
    };

    private static IEnumerable<OutfitterMarketListingEvidence> RelevantListings(
        IReadOnlyList<OutfitterMarketListingEvidence> listings)
    {
        foreach (var qualityGroup in listings
                     .Where(value => value.Quantity > 0)
                     .GroupBy(value => value.Quality))
        {
            uint coveredUnits = 0;
            foreach (var listing in qualityGroup
                         .OrderBy(value => value.UnitPriceGil)
                         .ThenByDescending(value => value.ListingReviewedAtUtc)
                         .ThenBy(value => value.ListingId, StringComparer.Ordinal))
            {
                yield return listing;
                coveredUnits = checked(coveredUnits + listing.Quantity);
                if (coveredUnits >= 2)
                    break;
            }
        }
    }

    private static MinerBotanistReadOnlyAdvice Abstain(string diagnostic) =>
        new(MinerBotanistAdvisorStatus.Abstained, AdvisoryRule, null, null,
            new Dictionary<string, AdvisorAuthorityAssessment>(),
            new Dictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer>(),
            diagnostic);
}
