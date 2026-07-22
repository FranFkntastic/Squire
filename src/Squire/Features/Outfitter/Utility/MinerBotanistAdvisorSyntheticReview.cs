#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.MarketEvidence;

namespace MarketMafioso.Squire.Outfitter.Utility;

public enum MinerBotanistAdvisorSyntheticScenarioKind
{
    Success,
    Refreshing,
    StaleEvidence,
    IncompleteEvidence,
    Abstention,
}

internal sealed record MinerBotanistAdvisorSyntheticPresentation(
    MinerBotanistAdvisorSyntheticScenarioKind Kind,
    string Label,
    MinerBotanistAdvisorSessionStage Stage,
    string Message,
    bool ShowPriorFrontier,
    bool ShowProgress,
    int Completed,
    int Total,
    bool AdviceIsRetained);

internal sealed record MinerBotanistAdvisorDryRunFixture(
    MinerBotanistReadOnlyAdvice Advice,
    OutfitterMarketEvidenceBook Evidence,
    string SelectedSolutionId,
    string Diagnostic);

/// <summary>
/// Privacy-minimized debug replay derived from the frozen model-gearset challenge family.
/// Runtime builds never load the research oracle. Item identities are stable game data; gil
/// estimates are a frozen Aether sale-history snapshot so UI review remains deterministic.
/// </summary>
internal static class MinerBotanistAdvisorSyntheticReview
{
    public static MinerBotanistAdvisorSyntheticPresentation Present(
        MinerBotanistAdvisorSyntheticScenarioKind kind,
        MinerBotanistReadOnlyAdvice advice) => kind switch
    {
        MinerBotanistAdvisorSyntheticScenarioKind.Refreshing => new(
            kind,
            "Refreshing with prior frontier",
            MinerBotanistAdvisorSessionStage.DiscoveringMarket,
            "Fetched 18 of 48 missing or stale market entries. The last valid frontier remains visible until a complete generation publishes.",
            true,
            true,
            18,
            48,
            true),
        MinerBotanistAdvisorSyntheticScenarioKind.StaleEvidence => new(
            kind,
            "Stale evidence rejected",
            MinerBotanistAdvisorSessionStage.Abstained,
            "Refresh abstained: 7 exact-quality rows are stale and their retry window has not elapsed. The last valid frontier remains visible.",
            true,
            false,
            41,
            48,
            true),
        MinerBotanistAdvisorSyntheticScenarioKind.IncompleteEvidence => new(
            kind,
            "Incomplete generation",
            MinerBotanistAdvisorSessionStage.Abstained,
            "Refresh abstained: 5 of 48 scoped items failed exact-quality discovery. The incomplete generation was not published; the last valid frontier remains visible.",
            true,
            false,
            43,
            48,
            true),
        MinerBotanistAdvisorSyntheticScenarioKind.Abstention => new(
            kind,
            "No authoritative recommendation",
            MinerBotanistAdvisorSessionStage.Abstained,
            "The rendered baseline contains an unresolved equipment slot, so the advisor stopped before market discovery and produced no frontier.",
            false,
            false,
            11,
            12,
            false),
        _ => new(
            kind,
            "Complete generation",
            MinerBotanistAdvisorSessionStage.Complete,
            advice.Diagnostic,
            true,
            false,
            48,
            48,
            false),
    };

    internal const string PriceEvidenceLabel = "Aether sale-history median · 2026-07-16";

    public static async Task<MinerBotanistAdvisorDryRunFixture> BuildDryRunFixtureAsync(
        IMarketAcquisitionListingSource listingSource,
        string region,
        MinerBotanistUtilityContextKind context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listingSource);
        var items = BaseCraftedItems();
        var request = new OutfitterMarketEvidenceRequest(
            "universalis",
            string.IsNullOrWhiteSpace(region) ? "North America" : region.Trim(),
            items.Select(item => item.ItemId).Distinct().ToArray(),
            ListingLimit: 100,
            CoverageMode: OutfitterMarketCoverageMode.ExhaustiveWithinScope,
            MaxConcurrency: 4);
        var discovery = new OutfitterMarketEvidenceDiscoveryService(
            listingSource,
            new(TimeSpan.FromMinutes(15), TimeSpan.FromHours(6), maxEntries: 64));
        var result = await discovery.DiscoverAsync(request, cancellationToken).ConfigureAwait(false);
        var evidence = MinerBotanistAdvisorSessionEvidencePolicy.SelectCurrent(result, request)
            ?? throw new InvalidOperationException("Live diagnostic evidence did not publish as one complete generation.");
        var selected = SelectDuplicatedSlotMarketRows(items, evidence)
            ?? throw new InvalidOperationException("No pair of distinct same-quality current listing rows can fulfill the duplicated equipment slots as indivisible full stacks; the diagnostic fixture abstained.");
        var advice = BuildDryRunAdvice(context, items, selected.Item, selected.Listings, evidence);
        var solutionId = advice.Frontier!.Pareto.Frontier.Single().Candidate.SolutionId;
        var totalGil = selected.Listings.Aggregate(
            0ul,
            (sum, listing) => checked(sum + ((ulong)listing.Quantity * listing.UnitPriceGil)));
        return new(
            advice,
            evidence,
            solutionId,
            $"This level-100 calibration item is intentionally unrelated to the logged-in character's equip level. It tests Route integration only: {selected.RequiredQuantity:N0} current {selected.Listings[0].Quality} {selected.Item.Name} across {selected.Listings.Count:N0} exact listing row(s), {totalGil:N0} gil total; permanently dry-run-only.");
    }

    private sealed record Benchmark(
        string Id,
        string Label,
        MinerBotanistUtilityStats Stats,
        string[] Assumptions,
        bool IsDerivedAdversarial = false,
        int PriceScalePermille = 1000);

    private sealed record MateriaEvidence(
        uint ItemId,
        int Tier,
        ulong UnitPriceGil);

    private sealed record ItemEvidence(
        EquipmentLoadoutPosition Position,
        uint ItemId,
        string Name,
        uint ItemLevel,
        EquipmentQuality Quality,
        EquipmentAcquisitionSourceKind SourceKind,
        ulong GearCostGil,
        int GuaranteedMateriaSlots,
        IReadOnlyList<MateriaEvidence> Materia);

    private sealed record DryRunMarketSelection(
        ItemEvidence Item,
        uint RequiredQuantity,
        IReadOnlyList<OutfitterMarketListingEvidence> Listings);

    private static readonly Benchmark[] Benchmarks =
    [
        new("crafted-unmelded", "Unmelded HQ crafted set",
            new(4_879, 4_880, 884),
            ["No food", "Every equipment piece is marketable and gil-acquirable"]),
        new("published-mid-crafted", "Mid-tier crafted-tool meld set",
            new(5_403, 5_408, 905),
            ["No food", "Published mid-tier meld map projected onto the marketable Gold Thumb's Pickaxe"]),
        new("published-high-crafted", "High-tier crafted-tool meld set",
            new(5_593, 5_574, 985),
            ["No food", "Published high-tier meld map projected onto the marketable Gold Thumb's Pickaxe"]),
        new("derived-high-regression", "Weaker, dearer adversarial set",
            new(5_583, 5_564, 975),
            ["Derived witness · 10% quote premium over the high-tier snapshot"],
            IsDerivedAdversarial: true,
            PriceScalePermille: 1100),
        new("derived-high-cost-only", "Identical-stat dearer adversarial set",
            new(5_593, 5_574, 985),
            ["Derived witness · 20% quote premium over the high-tier snapshot"],
            IsDerivedAdversarial: true,
            PriceScalePermille: 1200),
    ];

    public static MinerBotanistReadOnlyAdvice Build(MinerBotanistUtilityContextKind context)
    {
        var baseline = Benchmarks[0];
        var profile = new MinerBotanistUtilityProfile(
            context,
            baseline.Stats,
            MinerBotanistUtilityProfile.MinerClassJobId);
        var offers = new Dictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer>();
        var solutions = Benchmarks.Select(benchmark => BuildSolution(benchmark, profile, offers)).ToArray();
        var pareto = new EquipmentParetoFrontierBuilder().Build(solutions);
        var authority = pareto.Frontier.ToDictionary(
            solution => solution.Candidate.SolutionId,
            solution => profile.AssessAuthority(
                solution.Utility,
                solution.AcquisitionCostGil),
            StringComparer.Ordinal);
        var nomination = pareto.Frontier
            .Where(solution => authority[solution.Candidate.SolutionId].AdvisorMayConsider)
            .OrderBy(solution => solution.AcquisitionCostGil)
            .ThenByDescending(solution => solution.Utility.UtilityScore)
            .FirstOrDefault();
        var exact = new EquipmentExactFrontierResult(
            pareto,
            new(
                ExpandedStateCount: 0,
                InfeasibleTransitionCount: 0,
                DominatedStateCount: pareto.Dominated.Count,
                CompactedEquivalentStateCount: pareto.EquivalenceGroups.Count,
                PeakRetainedStateCount: pareto.Frontier.Count,
                CompleteSolutionCount: solutions.Length,
                RetainedCompletePathCount: solutions.Length,
                RetainedRepresentativeLimit: 16,
                BaselineSolutionId: baseline.Id,
                Elapsed: TimeSpan.Zero),
            []);
        return new(
            MinerBotanistAdvisorStatus.Complete,
            MinerBotanistReadOnlyAdvisor.AdvisoryRule,
            exact,
            nomination,
            authority,
            offers,
            nomination is null
                ? "Synthetic replay is complete; the advisor abstains under the displayed rule."
                : $"Synthetic replay nominates {nomination.VariantLabels[0]} under the displayed rule.");
    }

    private static EquipmentDecisionSolution BuildSolution(
        Benchmark benchmark,
        MinerBotanistUtilityProfile profile,
        IDictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer> offers)
    {
        var evidence = Items(benchmark).ToArray();
        var selections = new List<EquipmentLoadoutSelection>(evidence.Length);
        foreach (var item in evidence)
        {
            var itemMeldCost = EstimateMelds([item]);
            var acquisitionCost = checked(item.GearCostGil + itemMeldCost.ExpectedCostGil);
            var definition = new EquipmentItemDefinition(
                ItemId: item.ItemId,
                Name: item.Name,
                EquipLevel: 100,
                ItemLevel: item.ItemLevel,
                Slot: Slot(item.Position),
                EligibleClassJobIds: new HashSet<uint> { MinerBotanistUtilityProfile.MinerClassJobId, MinerBotanistUtilityProfile.BotanistClassJobId },
                Rarity: item.ItemLevel == 780 ? (byte)4 : (byte)3,
                IsEquipment: true,
                IsSoulCrystal: false,
                IsDesynthesizable: null,
                IsVendorSellable: null,
                VendorSellPrice: null,
                IsDiscardable: null,
                IsArmoireEligible: null,
                IsRecoverable: null,
                IsExplicitlyProtectedFamily: false);
            var sourceCatalogKey = $"synthetic-review:{benchmark.Id}:{item.Position}";
            var sourceLabel = item.Materia.Count == 0
                ? "Market history · HQ gear median"
                : "Market history · HQ gear median + expected meld failures";
            var offer = new EquipmentLoadoutOffer(
                definition,
                item.SourceKind,
                sourceLabel,
                UnitPriceGil: checked((uint)acquisitionCost),
                PriceIsEstimate: true,
                Quality: item.Quality,
                SourceCatalogKey: sourceCatalogKey);
            var exact = new EquipmentExactSolverOffer(
                offer,
                ObservationId: sourceCatalogKey,
                Positions: new HashSet<EquipmentLoadoutPosition> { item.Position },
                AvailableQuantity: 1,
                Utility: EquipmentSolverUtilityVector.Empty,
                AcquisitionCostGil: acquisitionCost,
                WorldVisitKey: "aether-history-snapshot",
                VendorStopKey: null,
                PurchaseTransactions: 1,
                EvidenceRisk: new(0, 0, 0),
                VariantLabels: [item.Quality == EquipmentQuality.High ? "HQ" : "NQ", PriceEvidenceLabel]);
            offers.Add(exact.AllocationKey, exact);
            selections.Add(new(item.Position, offer.Key, ObservationId: exact.ObservationId));
        }

        var gearCost = evidence.Aggregate(0UL, (total, item) => checked(total + item.GearCostGil));
        var meldCost = EstimateMelds(evidence);
        var totalCost = selections.Aggregate(0UL, (total, selection) =>
            checked(total + offers[selection.AllocationKey].AcquisitionCostGil));
        var expectedMateriaCost = checked(totalCost - gearCost);
        var planningCost = checked(gearCost + meldCost.PlanningCostGil);
        var labels = new List<string>
        {
            benchmark.Label,
            PriceEvidenceLabel,
            $"Stats {benchmark.Stats.Gathering}/{benchmark.Stats.Perception}/{benchmark.Stats.GatheringPoints}",
        };
        labels.AddRange(benchmark.Assumptions);
        if (meldCost.Lines.Count > 0)
        {
            labels.Add($"Materia if every meld succeeds first try: {meldCost.OneCopyCostGil:N0} gil");
            labels.Add($"Expected materia spend with failures: {expectedMateriaCost:N0} gil");
            labels.Add($"90% whole-set stocking ceiling: {meldCost.PlanningCostGil:N0} gil materia · {planningCost:N0} gil total");
        }
        if (benchmark.IsDerivedAdversarial)
            labels.Add("Adversarial witness; not a published recommendation");
        return new(
            new(benchmark.Id, selections),
            profile.Evaluate(benchmark.Stats),
            totalCost,
            new(
                WorldVisits: 1,
                VendorStops: 0,
                PurchaseTransactions: evidence.Length + evidence.SelectMany(item => item.Materia).Select(materia => materia.ItemId).Distinct().Count()),
            new(0, 0, 0),
            labels,
            new(
                OptimisticCostGil: checked(gearCost + meldCost.OneCopyCostGil),
                ExpectedCostGil: totalCost,
                PlanningCostGil: planningCost,
                PlanningConfidence: meldCost.PlanningConfidence,
                Reasons: meldCost.Lines.Count == 0
                    ? ["The loadout contains no materia, so optimistic, expected, and planning costs are identical."]
                    :
                    [
                        "Expected cost includes geometric materia loss at each advanced-meld success rate.",
                        "The planning ceiling stocks every risky meld so the whole set completes within that stock at least 90% of the time.",
                    ]));
    }

    private static MinerBotanistReadOnlyAdvice BuildDryRunAdvice(
        MinerBotanistUtilityContextKind context,
        IReadOnlyList<ItemEvidence> items,
        ItemEvidence marketItem,
        IReadOnlyList<OutfitterMarketListingEvidence> marketListings,
        OutfitterMarketEvidenceBook evidence)
    {
        const string solutionId = "diagnostic-live-listing-dry-run";
        var profile = new MinerBotanistUtilityProfile(
            context,
            Benchmarks[0].Stats,
            MinerBotanistUtilityProfile.MinerClassJobId);
        var offers = new Dictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer>();
        var selections = new List<EquipmentLoadoutSelection>(items.Count);
        var marketPositions = items
            .Where(item => item.ItemId == marketItem.ItemId)
            .Select(item => item.Position)
            .Order()
            .ToArray();
        var marketRowsByPosition = new Dictionary<EquipmentLoadoutPosition, OutfitterMarketListingEvidence>();
        var positionIndex = 0;
        foreach (var listing in marketListings)
        {
            for (var quantity = 0u; quantity < listing.Quantity; quantity++)
                marketRowsByPosition.Add(marketPositions[positionIndex++], listing);
        }
        if (positionIndex != marketPositions.Length)
            throw new InvalidOperationException("Current diagnostic listing rows do not exactly fill the duplicated equipment positions.");

        foreach (var item in items)
        {
            var definition = Definition(item);
            EquipmentExactSolverOffer exact;
            if (marketRowsByPosition.TryGetValue(item.Position, out var listing))
            {
                var sourceCatalogKey = $"diagnostic-live:{evidence.GenerationId:N}:{item.ItemId}:{listing.Quality}:{listing.ListingId}";
                var key = new EquipmentOfferKey(
                    item.ItemId,
                    listing.Quality,
                    EquipmentAcquisitionSourceKind.MarketBoard,
                    sourceCatalogKey);
                var observation = new EquipmentOfferObservation(
                    key,
                    evidence.GenerationId,
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
                    $"Live diagnostic listing - {listing.WorldName}",
                    listing.UnitPriceGil,
                    PriceIsEstimate: false,
                    Quality: listing.Quality,
                    SourceCatalogKey: sourceCatalogKey,
                    Observation: observation);
                var positions = marketRowsByPosition
                    .Where(pair => pair.Value.ListingId == listing.ListingId && pair.Value.RetainerId == listing.RetainerId)
                    .Select(pair => pair.Key)
                    .ToHashSet();
                exact = new EquipmentExactSolverOffer(
                    offer,
                    listing.ListingId,
                    positions,
                    listing.Quantity,
                    EquipmentSolverUtilityVector.Empty,
                    listing.UnitPriceGil,
                    listing.WorldName,
                    null,
                    1,
                    new(0, 0, 0),
                    [listing.Quality == EquipmentQuality.High ? "HQ" : "NQ", listing.WorldName, "dry-run-only"]);
                if (offers.TryGetValue(exact.AllocationKey, out var existing))
                    exact = existing;
            }
            else
            {
                var sourceCatalogKey = $"diagnostic-owned-placeholder:{item.Position}";
                var offer = new EquipmentLoadoutOffer(
                    definition,
                    EquipmentAcquisitionSourceKind.Owned,
                    "Diagnostic owned baseline placeholder",
                    Quality: item.Quality,
                    SourceCatalogKey: sourceCatalogKey);
                exact = new(
                    offer,
                    sourceCatalogKey,
                    new HashSet<EquipmentLoadoutPosition> { item.Position },
                    1,
                    EquipmentSolverUtilityVector.Empty,
                    0,
                    null,
                    null,
                    0,
                    new(0, 0, 0),
                    ["Diagnostic baseline", "dry-run-only"]);
            }
            offers.TryAdd(exact.AllocationKey, exact);
            selections.Add(new(item.Position, exact.Offer.Key, ObservationId: exact.ObservationId));
        }

        var utility = profile.Evaluate(Benchmarks[1].Stats);
        var acquisitionCost = marketRowsByPosition.Values.Aggregate(
            0ul,
            (sum, listing) => checked(sum + listing.UnitPriceGil));
        var worldVisits = marketListings.Select(listing => listing.WorldName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var solution = new EquipmentDecisionSolution(
            new(solutionId, selections),
            utility,
            acquisitionCost,
            new(worldVisits, 0, marketListings.Count),
            new(0, 0, 0),
            ["Live exact-quality duplicated-slot diagnostic fixture", marketItem.Name, $"Required quantity {marketPositions.Length}", "dry-run-only"]);
        var pareto = new EquipmentParetoFrontierBuilder().Build([solution]);
        var frontier = new EquipmentExactFrontierResult(
            pareto,
            new(0, 0, 0, 0, 1, 1, 1, 16, solutionId, TimeSpan.Zero),
            []);
        return new(
            MinerBotanistAdvisorStatus.Complete,
            MinerBotanistReadOnlyAdvisor.AdvisoryRule,
            frontier,
            solution,
            new Dictionary<string, AdvisorAuthorityAssessment>(StringComparer.Ordinal)
            {
                [solutionId] = profile.AssessAuthority(utility, acquisitionCost),
            },
            offers,
            $"Diagnostic Advisor solution binds {marketPositions.Length:N0} duplicated slots to current exact-quality listing rows for {marketItem.Name}; execution is permanently dry-run-only.");
    }

    private static DryRunMarketSelection? SelectDuplicatedSlotMarketRows(
        IReadOnlyList<ItemEvidence> items,
        OutfitterMarketEvidenceBook evidence)
    {
        var duplicated = items
            .GroupBy(item => item.ItemId)
            .Where(group => group.Count() >= 2)
            .ToDictionary(group => group.Key, group => (Item: group.First(), Required: (uint)group.Count()));
        return evidence.Items
            .Where(item => item.Status == OutfitterMarketEvidenceItemStatus.Fresh && duplicated.ContainsKey(item.ItemId))
            .SelectMany(item => item.Listings
                .Where(listing => listing.Quantity > 0 && listing.UnitPriceGil > 0)
                .GroupBy(listing => listing.Quality)
                .SelectMany(group => ExactQuantityListingSets(group, duplicated[item.ItemId].Required)
                    .Select(listings => new DryRunMarketSelection(
                        duplicated[item.ItemId].Item,
                        duplicated[item.ItemId].Required,
                        listings))))
            .OrderBy(selection => selection.Listings.Select(listing => listing.WorldName).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .ThenByDescending(selection => selection.Listings[0].Quality == EquipmentQuality.High)
            .ThenBy(selection => selection.Listings.Aggregate(
                0ul,
                (sum, listing) => checked(sum + ((ulong)listing.Quantity * listing.UnitPriceGil))))
            .ThenBy(selection => string.Join("|", selection.Listings.Select(listing => listing.WorldName)), StringComparer.OrdinalIgnoreCase)
            .ThenBy(selection => string.Join("|", selection.Listings.Select(listing => listing.ListingId)), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static IEnumerable<IReadOnlyList<OutfitterMarketListingEvidence>> ExactQuantityListingSets(
        IEnumerable<OutfitterMarketListingEvidence> listings,
        uint requiredQuantity)
    {
        var candidates = listings
            .Where(listing => listing.Quantity <= requiredQuantity)
            .OrderBy(listing => listing.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(listing => listing.UnitPriceGil)
            .ThenBy(listing => listing.ListingId, StringComparer.Ordinal)
            .ToArray();
        return Enumerate(0, requiredQuantity, []);

        IEnumerable<IReadOnlyList<OutfitterMarketListingEvidence>> Enumerate(
            int index,
            uint remaining,
            IReadOnlyList<OutfitterMarketListingEvidence> selected)
        {
            if (remaining == 0)
            {
                if (selected.Count >= 2 &&
                    selected.Select(ListingIdentity).Distinct(StringComparer.Ordinal).Count() == selected.Count)
                    yield return selected;
                yield break;
            }
            for (var candidateIndex = index; candidateIndex < candidates.Length; candidateIndex++)
            {
                var candidate = candidates[candidateIndex];
                if (candidate.Quantity > remaining)
                    continue;
                foreach (var result in Enumerate(
                    candidateIndex + 1,
                    remaining - candidate.Quantity,
                    selected.Append(candidate).ToArray()))
                    yield return result;
            }
        }

        static string ListingIdentity(OutfitterMarketListingEvidence listing) =>
            $"{listing.WorldName.ToUpperInvariant()}|{listing.ListingId}|{listing.RetainerId}";
    }

    private static EquipmentItemDefinition Definition(ItemEvidence item) => new(
        ItemId: item.ItemId,
        Name: item.Name,
        EquipLevel: 100,
        ItemLevel: item.ItemLevel,
        Slot: Slot(item.Position),
        EligibleClassJobIds: new HashSet<uint>
        {
            MinerBotanistUtilityProfile.MinerClassJobId,
            MinerBotanistUtilityProfile.BotanistClassJobId,
        },
        Rarity: 3,
        IsEquipment: true,
        IsSoulCrystal: false,
        IsDesynthesizable: null,
        IsVendorSellable: null,
        VendorSellPrice: null,
        IsDiscardable: null,
        IsArmoireEligible: null,
        IsRecoverable: null,
        IsExplicitlyProtectedFamily: false);

    private static IEnumerable<ItemEvidence> Items(Benchmark benchmark)
    {
        var items = benchmark.Id switch
        {
            "crafted-unmelded" => BaseCraftedItems(),
            "published-mid-crafted" => MidTierItems(),
            _ => HighTierItems(),
        };
        if (benchmark.PriceScalePermille == 1000)
            return items;
        return items.Select(item => item with
        {
            GearCostGil = Scale(item.GearCostGil, benchmark.PriceScalePermille),
            Materia = item.Materia.Select(materia => materia with
            {
                UnitPriceGil = Scale(materia.UnitPriceGil, benchmark.PriceScalePermille),
            }).ToArray(),
        });
    }

    private static ItemEvidence[] BaseCraftedItems() =>
    [
        Market(EquipmentLoadoutPosition.MainHand, 47171, "Gold Thumb's Pickaxe", 300_767, 1),
        Market(EquipmentLoadoutPosition.OffHand, 47182, "Gold Thumb's Sledgehammer", 349_975, 1),
        Market(EquipmentLoadoutPosition.Head, 47189, "Crested Hood of Gathering", 258_926, 2),
        Market(EquipmentLoadoutPosition.Body, 47190, "Crested Coat of Gathering", 319_980, 2),
        Market(EquipmentLoadoutPosition.Hands, 47191, "Crested Gloves of Gathering", 296_897, 2),
        Market(EquipmentLoadoutPosition.Legs, 47192, "Crested Slops of Gathering", 299_868, 2),
        Market(EquipmentLoadoutPosition.Feet, 47193, "Crested Boots of Gathering", 259_445, 2),
        Market(EquipmentLoadoutPosition.Ears, 47198, "Crested Earrings of Gathering", 125_000, 1),
        Market(EquipmentLoadoutPosition.Neck, 47199, "Crested Necklace of Gathering", 130_327, 1),
        Market(EquipmentLoadoutPosition.Wrists, 47200, "Crested Bracelet of Gathering", 120_000, 1),
        Market(EquipmentLoadoutPosition.LeftRing, 47201, "Crested Ring of Gathering", 129_997, 1),
        Market(EquipmentLoadoutPosition.RightRing, 47201, "Crested Ring of Gathering", 129_997, 1),
    ];

    private static ItemEvidence[] MidTierItems() => ApplyMelds(BaseCraftedItems(), new Dictionary<EquipmentLoadoutPosition, MateriaEvidence[]>
    {
        [EquipmentLoadoutPosition.MainHand] = [M(41777, 12, 600)],
        [EquipmentLoadoutPosition.OffHand] = [M(41776, 12, 999), M(33936, 10, 739)],
        [EquipmentLoadoutPosition.Head] = [M(41775, 12, 977), M(41776, 12, 999), M(33936, 10, 739), M(33922, 9, 1_798)],
        [EquipmentLoadoutPosition.Body] = [M(41775, 12, 977), M(41775, 12, 977), M(33935, 10, 746), M(33923, 9, 650)],
        [EquipmentLoadoutPosition.Hands] = [M(41775, 12, 977), M(41775, 12, 977), M(33935, 10, 746), M(33923, 9, 650)],
        [EquipmentLoadoutPosition.Legs] = [M(41775, 12, 977), M(41776, 12, 999), M(33935, 10, 746), M(33922, 9, 1_798), M(5688, 5, 310)],
        [EquipmentLoadoutPosition.Feet] = [M(41776, 12, 999), M(41776, 12, 999), M(33937, 10, 317), M(41763, 11, 1_150)],
        [EquipmentLoadoutPosition.Ears] = [M(41776, 12, 999), M(33935, 10, 746), M(33923, 9, 650), M(33922, 9, 1_798)],
        [EquipmentLoadoutPosition.Neck] = [M(41776, 12, 999), M(33935, 10, 746), M(33923, 9, 650), M(33922, 9, 1_798)],
        [EquipmentLoadoutPosition.Wrists] = [M(41776, 12, 999), M(33935, 10, 746), M(33923, 9, 650), M(33922, 9, 1_798)],
        [EquipmentLoadoutPosition.LeftRing] = [M(41776, 12, 999), M(33935, 10, 746), M(33923, 9, 650), M(33922, 9, 1_798)],
        [EquipmentLoadoutPosition.RightRing] = [M(41776, 12, 999), M(33935, 10, 746), M(33923, 9, 650), M(33922, 9, 1_798)],
    });

    private static ItemEvidence[] HighTierItems() => ApplyMelds(BaseCraftedItems(), new Dictionary<EquipmentLoadoutPosition, MateriaEvidence[]>
    {
        [EquipmentLoadoutPosition.MainHand] = [M(41776, 12, 999), M(41776, 12, 999), M(41763, 11, 1_150), M(41763, 11, 1_150), M(41763, 11, 1_150)],
        [EquipmentLoadoutPosition.OffHand] = [M(41776, 12, 999), M(41776, 12, 999), M(41763, 11, 1_150), M(41763, 11, 1_150), M(41762, 11, 1_000)],
        [EquipmentLoadoutPosition.Head] = [M(41775, 12, 977), M(41775, 12, 977), M(41775, 12, 977), M(41763, 11, 1_150), M(41764, 11, 1_088)],
        [EquipmentLoadoutPosition.Body] = [M(41775, 12, 977), M(41775, 12, 977), M(41776, 12, 999), M(41763, 11, 1_150), M(41762, 11, 1_000)],
        [EquipmentLoadoutPosition.Hands] = [M(41775, 12, 977), M(41775, 12, 977), M(41775, 12, 977), M(41762, 11, 1_000), M(41763, 11, 1_150)],
        [EquipmentLoadoutPosition.Legs] = [M(41775, 12, 977), M(41775, 12, 977), M(41776, 12, 999), M(33922, 9, 1_798), M(5692, 4, 1_898)],
        [EquipmentLoadoutPosition.Feet] = [M(41776, 12, 999), M(41776, 12, 999), M(41777, 12, 600), M(41764, 11, 1_088), M(41763, 11, 1_150)],
        [EquipmentLoadoutPosition.Ears] = [M(41775, 12, 977), M(41776, 12, 999), M(41764, 11, 1_088), M(41762, 11, 1_000), M(41764, 11, 1_088)],
        [EquipmentLoadoutPosition.Neck] = [M(41775, 12, 977), M(41776, 12, 999), M(41764, 11, 1_088), M(41762, 11, 1_000), M(41764, 11, 1_088)],
        [EquipmentLoadoutPosition.Wrists] = [M(41775, 12, 977), M(41776, 12, 999), M(41764, 11, 1_088), M(41762, 11, 1_000), M(41764, 11, 1_088)],
        [EquipmentLoadoutPosition.LeftRing] = [M(41775, 12, 977), M(41776, 12, 999), M(41764, 11, 1_088), M(41762, 11, 1_000), M(41763, 11, 1_150)],
        [EquipmentLoadoutPosition.RightRing] = [M(41775, 12, 977), M(41776, 12, 999), M(41764, 11, 1_088), M(41762, 11, 1_000), M(41763, 11, 1_150)],
    });

    private static ItemEvidence Market(
        EquipmentLoadoutPosition position,
        uint itemId,
        string name,
        ulong gearCost,
        int guaranteedMateriaSlots) => new(
        position,
        itemId,
        name,
        750,
        EquipmentQuality.High,
        EquipmentAcquisitionSourceKind.MarketBoard,
        gearCost,
        guaranteedMateriaSlots,
        []);

    private static MateriaEvidence M(uint itemId, int tier, ulong unitPriceGil) => new(itemId, tier, unitPriceGil);

    private static ItemEvidence[] ApplyMelds(
        IEnumerable<ItemEvidence> items,
        IReadOnlyDictionary<EquipmentLoadoutPosition, MateriaEvidence[]> melds) => items
        .Select(item => item with { Materia = melds.GetValueOrDefault(item.Position) ?? [] })
        .ToArray();

    private static MateriaMeldCostEstimate EstimateMelds(IEnumerable<ItemEvidence> items) =>
        MateriaMeldCostEstimator.Estimate(items.SelectMany(item => item.Materia.Select((materia, index) =>
            new MateriaMeldCostInput(
                $"{item.Position}:{index + 1}",
                materia.UnitPriceGil,
                index < item.GuaranteedMateriaSlots
                    ? 1d
                    : DohDolMateriaMeldingRates.Resolve(true, materia.Tier, index - item.GuaranteedMateriaSlots))))
            .ToArray());

    private static ulong Scale(ulong value, int permille) =>
        checked(value * (ulong)permille / 1000UL);

    private static EquipmentSlot Slot(EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => EquipmentSlot.MainHand,
        EquipmentLoadoutPosition.OffHand => EquipmentSlot.OffHand,
        EquipmentLoadoutPosition.Head => EquipmentSlot.Head,
        EquipmentLoadoutPosition.Body => EquipmentSlot.Body,
        EquipmentLoadoutPosition.Hands => EquipmentSlot.Hands,
        EquipmentLoadoutPosition.Legs => EquipmentSlot.Legs,
        EquipmentLoadoutPosition.Feet => EquipmentSlot.Feet,
        EquipmentLoadoutPosition.Ears => EquipmentSlot.Ears,
        EquipmentLoadoutPosition.Neck => EquipmentSlot.Neck,
        EquipmentLoadoutPosition.Wrists => EquipmentSlot.Wrists,
        EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing => EquipmentSlot.Ring,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, null),
    };
}
#endif
