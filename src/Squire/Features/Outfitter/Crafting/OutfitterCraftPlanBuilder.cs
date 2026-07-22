using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Crafting;

internal sealed record OutfitterCraftPlanBuildRequest(uint GearItemId, uint Quantity);

internal enum OutfitterCraftPlanBuildStatus
{
    EconomyReady,
    DisplayOnly,
    Abstained,
}

internal enum OutfitterCraftPlanBuildDiagnosticCode
{
    InvalidRequest,
    InvalidBaseline,
    InvalidMarketEvidence,
    InvalidRecipeGraph,
    AmbiguousRecipe,
    CircularRecipe,
    MaximumDepthExceeded,
    MaximumSizeExceeded,
    UnsupportedRecipe,
    IneligibleCrafter,
    IncompleteMaterialCoverage,
    InvalidVendorCatalog,
    ArithmeticOverflow,
}

internal sealed record OutfitterCraftPlanBuildDiagnostic(
    OutfitterCraftPlanBuildDiagnosticCode Code,
    string Message,
    string? NodeId = null);

internal sealed record OutfitterCraftPlanBuildResult(
    OutfitterCraftPlanBuildStatus Status,
    OutfitterCraftPlan? Plan,
    ImmutableArray<OutfitterCraftPlanBuildDiagnostic> Diagnostics)
{
    public bool IsEconomyReady => Status == OutfitterCraftPlanBuildStatus.EconomyReady;
}

/// <summary>
/// Pure process-local builder from one exact graph and one authority envelope. It does not publish
/// offers or transfer plans into Advisor, solver, Workbench, Artisan, or acquisition machinery.
/// </summary>
internal sealed class OutfitterCraftPlanBuilder
{
    private const int MaximumWholeListingAllocationStates = 100_000;
    private const int MaximumMarketListingsPerMaterial = 100;

    private readonly OutfitterGilVendorCatalog vendorCatalog;
    private readonly TimeProvider timeProvider;

    public OutfitterCraftPlanBuilder(
        OutfitterGilVendorCatalog vendorCatalog,
        TimeProvider? timeProvider = null)
    {
        this.vendorCatalog = vendorCatalog ?? throw new ArgumentNullException(nameof(vendorCatalog));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public OutfitterCraftPlanBuildResult Build(
        OutfitterCraftPlanBuildRequest request,
        PlayerAdvisorBaseline freshlyRevalidatedBaseline,
        OutfitterMarketEvidenceBook? publishedMarketEvidence,
        OutfitterExactRecipeGraph recipeGraph)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(freshlyRevalidatedBaseline);
        ArgumentNullException.ThrowIfNull(recipeGraph);

        var builtAtUtc = timeProvider.GetUtcNow();
        if (request.GearItemId == 0 || request.Quantity == 0)
            return Abstain(OutfitterCraftPlanBuildDiagnosticCode.InvalidRequest, "Craft planning requires one non-zero NQ gear item and quantity.");
        if (builtAtUtc == default)
            return Abstain(OutfitterCraftPlanBuildDiagnosticCode.InvalidRequest, "Craft planning requires a non-default authoritative build time.");
        if (recipeGraph.RootItemId != request.GearItemId)
            return Abstain(OutfitterCraftPlanBuildDiagnosticCode.InvalidRecipeGraph, "The exact recipe graph root does not match the requested gear item.");

        OutfitterCrafterObservationIdentity crafterAuthority;
        try
        {
            crafterAuthority = OutfitterCrafterObservationIdentity.FromBaseline(freshlyRevalidatedBaseline, builtAtUtc);
            if (!crafterAuthority.MatchesCurrentBaseline(freshlyRevalidatedBaseline, builtAtUtc))
                throw new InvalidOperationException("The trusted player baseline changed during revalidation.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Abstain(OutfitterCraftPlanBuildDiagnosticCode.InvalidBaseline, exception.Message);
        }

        CraftMarketEvidenceReference? marketEvidence = null;
        try
        {
            if (publishedMarketEvidence is not null)
            {
                marketEvidence = CraftMarketEvidenceReference.FromPublishedBook(publishedMarketEvidence);
                if (marketEvidence.PublishedAtUtc > builtAtUtc)
                    throw new InvalidOperationException("Craft planning cannot use a market publication from the future.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            return Abstain(OutfitterCraftPlanBuildDiagnosticCode.InvalidMarketEvidence, exception.Message);
        }

        ImmutableArray<OutfitterCraftNode> nodes;
        ImmutableArray<OutfitterCraftDiagnostic> planDiagnostics;
        try
        {
            var expansion = new Expansion(recipeGraph, crafterAuthority);
            expansion.Expand(request.GearItemId, recipeGraph.RootItemName, request.Quantity);
            nodes = expansion.Nodes.ToImmutableArray();
            planDiagnostics = expansion.Diagnostics.ToImmutableArray();
        }
        catch (BuildFailure exception)
        {
            return Abstain(exception.Code, exception.Message, exception.NodeId);
        }
        catch (OverflowException)
        {
            return Abstain(OutfitterCraftPlanBuildDiagnosticCode.ArithmeticOverflow, "Expanded recipe quantity arithmetic overflowed.");
        }

        ImmutableArray<OutfitterTerminalMaterialLine> materials;
        try
        {
            materials = AllocateMaterials(nodes, marketEvidence, builtAtUtc);
            _ = materials.Aggregate(
                0ul,
                (total, line) => checked(total + checked((ulong)line.PurchasedQuantity * line.Source.UnitPriceGil)));
        }
        catch (BuildFailure exception)
        {
            return Abstain(exception.Code, exception.Message, exception.NodeId);
        }
        catch (InvalidOperationException exception)
        {
            return Abstain(OutfitterCraftPlanBuildDiagnosticCode.InvalidVendorCatalog, exception.Message);
        }
        catch (OverflowException)
        {
            return Abstain(OutfitterCraftPlanBuildDiagnosticCode.ArithmeticOverflow, "Terminal material quantity or gil arithmetic overflowed.");
        }

        var plan = new OutfitterCraftPlan(
            OutfitterCraftPlan.CurrentSchemaVersion,
            PlanId(recipeGraph, request, builtAtUtc),
            request.GearItemId,
            EquipmentQuality.Normal,
            request.Quantity,
            "root",
            crafterAuthority,
            recipeGraph.MaximumDepth,
            recipeGraph.MaximumExpandedNodeCount,
            nodes,
            materials,
            marketEvidence,
            builtAtUtc,
            planDiagnostics);
        var structuralValidation = plan.Validate();
        if (!structuralValidation.IsValid)
        {
            return Abstain(
                OutfitterCraftPlanBuildDiagnosticCode.InvalidRecipeGraph,
                $"The expanded exact recipe graph failed closed: {string.Join(" ", structuralValidation.Errors)}");
        }

        var economyValidation = plan.Validate(requireEconomyReady: true);
        if (economyValidation.IsValid)
            return new(OutfitterCraftPlanBuildStatus.EconomyReady, plan, ImmutableArray<OutfitterCraftPlanBuildDiagnostic>.Empty);

        var displayDiagnostics = planDiagnostics
            .Select(diagnostic => new OutfitterCraftPlanBuildDiagnostic(
                diagnostic.Code is OutfitterCraftDiagnosticCode.IneligibleCrafter or OutfitterCraftDiagnosticCode.UnprovenCrafter
                    ? OutfitterCraftPlanBuildDiagnosticCode.IneligibleCrafter
                    : OutfitterCraftPlanBuildDiagnosticCode.UnsupportedRecipe,
                diagnostic.Message,
                diagnostic.NodeId))
            .ToImmutableArray();
        if (displayDiagnostics.IsDefaultOrEmpty)
        {
            displayDiagnostics = economyValidation.Errors
                .Select(error => new OutfitterCraftPlanBuildDiagnostic(
                    OutfitterCraftPlanBuildDiagnosticCode.UnsupportedRecipe,
                    error))
                .ToImmutableArray();
        }
        return new(OutfitterCraftPlanBuildStatus.DisplayOnly, plan, displayDiagnostics);
    }

    private ImmutableArray<OutfitterTerminalMaterialLine> AllocateMaterials(
        ImmutableArray<OutfitterCraftNode> nodes,
        CraftMarketEvidenceReference? evidence,
        DateTimeOffset builtAtUtc)
    {
        var requiredByItem = new SortedDictionary<uint, uint>();
        foreach (var node in nodes.Where(node => node.Kind == OutfitterCraftNodeKind.Material))
            requiredByItem[node.ItemId] = checked(requiredByItem.GetValueOrDefault(node.ItemId) + node.RequiredQuantity);

        var lines = ImmutableArray.CreateBuilder<OutfitterTerminalMaterialLine>();
        foreach (var requirement in requiredByItem)
        {
            var matchingListings = new List<CraftMarketListingIdentity>(MaximumMarketListingsPerMaterial);
            foreach (var listing in evidence?.Listings ?? ImmutableArray<CraftMarketListingIdentity>.Empty)
            {
                if (listing.ItemId != requirement.Key ||
                    listing.Quality != EquipmentQuality.Normal ||
                    !CraftMarketEvidenceFreshness.IsFresh(
                        listing.ReviewedAtUtc,
                        listing.CapturedAtUtc,
                        evidence!.PublishedAtUtc,
                        builtAtUtc))
                {
                    continue;
                }

                if (matchingListings.Count == MaximumMarketListingsPerMaterial)
                {
                    throw new BuildFailure(
                        OutfitterCraftPlanBuildDiagnosticCode.MaximumSizeExceeded,
                        $"Whole-listing optimization for item {requirement.Key} exceeds the bounded {MaximumMarketListingsPerMaterial}-listing input limit.");
                }
                matchingListings.Add(listing);
            }

            var marketListings = matchingListings
                .OrderBy(listing => listing.WorldId)
                .ThenBy(listing => listing.ListingId, StringComparer.Ordinal)
                .ToArray();

            var vendor = vendorCatalog.FindOffers(requirement.Key)
                .OrderBy(offer => offer.UnitPriceGil)
                .ThenBy(offer => offer.ShopId)
                .ThenBy(offer => offer.VendorId)
                .ThenBy(offer => offer.TerritoryId)
                .FirstOrDefault();
            var allocation = OptimizeWholeListings(requirement.Key, requirement.Value, marketListings, vendor);
            if (allocation is null)
            {
                throw new BuildFailure(
                    OutfitterCraftPlanBuildDiagnosticCode.IncompleteMaterialCoverage,
                    $"No authoritative NQ market listings or concrete gil vendors cover all {requirement.Value.ToString(CultureInfo.InvariantCulture)} required units of item {requirement.Key.ToString(CultureInfo.InvariantCulture)}.");
            }

            var remaining = requirement.Value;
            foreach (var index in allocation.ListingIndices)
            {
                var listing = marketListings[index];
                var consumed = Math.Min(remaining, listing.AvailableQuantity);
                remaining = checked(remaining - consumed);
                lines.Add(new(
                    OutfitterCraftPlan.MaterialKey(requirement.Key, EquipmentQuality.Normal),
                    requirement.Key,
                    EquipmentQuality.Normal,
                    consumed,
                    listing.AvailableQuantity,
                    checked(listing.AvailableQuantity - consumed),
                    new OutfitterMarketMaterialSourceIdentity(
                        listing.ItemId,
                        listing.Quality,
                        listing.UnitPriceGil,
                        listing.AvailableQuantity,
                        listing.ListingId,
                        listing.WorldId,
                        listing.WorldName,
                        listing.ReviewedAtUtc,
                        listing.CapturedAtUtc,
                        listing.SourceRevision,
                        evidence!.GenerationId,
                        evidence.Revision)));
            }
            if (remaining != 0 && vendor is not null)
            {
                lines.Add(new(
                    OutfitterCraftPlan.MaterialKey(requirement.Key, EquipmentQuality.Normal),
                    requirement.Key,
                    EquipmentQuality.Normal,
                    remaining,
                    remaining,
                    0,
                    OutfitterGilVendorMaterialSourceIdentity.FromCatalog(vendorCatalog, vendor)));
                remaining = 0;
            }
            if (remaining != 0)
                throw new InvalidOperationException("Exact material allocation did not cover its proven quantity.");
        }

        return lines.ToImmutable();
    }

    private static WholeListingAllocation? OptimizeWholeListings(
        uint itemId,
        uint requiredQuantity,
        IReadOnlyList<CraftMarketListingIdentity> listings,
        OutfitterGilVendorOffer? vendor)
    {
        var states = new Dictionary<uint, WholeListingState>
        {
            [0] = new(0, 0, 0, ImmutableArray<int>.Empty),
        };
        for (var index = 0; index < listings.Count; index++)
        {
            var listing = listings[index];
            var priorStates = states.Values.ToArray();
            foreach (var prior in priorStates)
            {
                var purchased = checked(prior.PurchasedQuantity + listing.AvailableQuantity);
                var coverage = (uint)Math.Min((ulong)requiredQuantity, purchased);
                var marketGil = checked(prior.MarketGil + checked((ulong)listing.AvailableQuantity * listing.UnitPriceGil));
                var candidate = new WholeListingState(
                    coverage,
                    purchased,
                    marketGil,
                    prior.ListingIndices.Add(index));
                if (!states.TryGetValue(coverage, out var current) || BetterState(candidate, current))
                    states[coverage] = candidate;
                if (states.Count > MaximumWholeListingAllocationStates)
                {
                    throw new BuildFailure(
                        OutfitterCraftPlanBuildDiagnosticCode.MaximumSizeExceeded,
                        $"Exact whole-listing optimization for item {itemId} exceeded {MaximumWholeListingAllocationStates} bounded states.");
                }
            }
        }

        WholeListingAllocation? best = null;
        foreach (var state in states.Values)
        {
            if (state.CoverageQuantity < requiredQuantity && vendor is null)
                continue;
            var vendorQuantity = checked(requiredQuantity - state.CoverageQuantity);
            var totalGil = checked(state.MarketGil + checked((ulong)vendorQuantity * (vendor?.UnitPriceGil ?? 0)));
            var totalPurchased = checked(state.PurchasedQuantity + vendorQuantity);
            var candidate = new WholeListingAllocation(
                totalGil,
                checked(totalPurchased - requiredQuantity),
                state.ListingIndices);
            if (best is null || BetterAllocation(candidate, best))
                best = candidate;
        }
        return best;
    }

    private static bool BetterState(WholeListingState candidate, WholeListingState current)
    {
        var cost = candidate.MarketGil.CompareTo(current.MarketGil);
        if (cost != 0)
            return cost < 0;
        var quantity = candidate.PurchasedQuantity.CompareTo(current.PurchasedQuantity);
        return quantity != 0
            ? quantity < 0
            : CompareIndices(candidate.ListingIndices, current.ListingIndices) < 0;
    }

    private static bool BetterAllocation(WholeListingAllocation candidate, WholeListingAllocation current)
    {
        var cost = candidate.TotalGil.CompareTo(current.TotalGil);
        if (cost != 0)
            return cost < 0;
        var surplus = candidate.SurplusQuantity.CompareTo(current.SurplusQuantity);
        return surplus != 0
            ? surplus < 0
            : CompareIndices(candidate.ListingIndices, current.ListingIndices) < 0;
    }

    private static int CompareIndices(ImmutableArray<int> left, ImmutableArray<int> right)
    {
        var common = Math.Min(left.Length, right.Length);
        for (var index = 0; index < common; index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
                return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static string PlanId(
        OutfitterExactRecipeGraph graph,
        OutfitterCraftPlanBuildRequest request,
        DateTimeOffset builtAtUtc)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{graph.ContentIdentity}|{request.GearItemId}|{request.Quantity}|{builtAtUtc.UtcDateTime.Ticks}");
        return $"craft-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..20]}";
    }

    private static OutfitterCraftPlanBuildResult Abstain(
        OutfitterCraftPlanBuildDiagnosticCode code,
        string message,
        string? nodeId = null) =>
        new(
            OutfitterCraftPlanBuildStatus.Abstained,
            null,
            [new(code, message, nodeId)]);

    private sealed class Expansion
    {
        private readonly OutfitterExactRecipeGraph graph;
        private readonly OutfitterCrafterObservationIdentity crafter;
        private readonly HashSet<uint> activeRecipeItems = [];

        public Expansion(OutfitterExactRecipeGraph graph, OutfitterCrafterObservationIdentity crafter)
        {
            this.graph = graph;
            this.crafter = crafter;
        }

        public List<OutfitterCraftNode> Nodes { get; } = [];
        public List<OutfitterCraftDiagnostic> Diagnostics { get; } = [];

        public void Expand(uint itemId, string itemName, uint quantity) =>
            ExpandNode("root", null, itemId, itemName, quantity, 0, 0);

        private void ExpandNode(
            string nodeId,
            string? parentNodeId,
            uint itemId,
            string itemName,
            uint requiredQuantity,
            uint quantityPerParentCraft,
            int depth)
        {
            if (depth > graph.MaximumDepth)
                throw new BuildFailure(OutfitterCraftPlanBuildDiagnosticCode.MaximumDepthExceeded,
                    $"Exact recipe expansion exceeded maximum depth {graph.MaximumDepth}.", nodeId);
            if (Nodes.Count >= graph.MaximumExpandedNodeCount)
                throw new BuildFailure(OutfitterCraftPlanBuildDiagnosticCode.MaximumSizeExceeded,
                    $"Exact recipe expansion exceeded maximum size {graph.MaximumExpandedNodeCount}.", nodeId);

            var candidates = graph.FindRecipes(itemId);
            if (candidates.Length == 0)
            {
                if (!graph.IsTerminalMaterial(itemId))
                    throw new BuildFailure(OutfitterCraftPlanBuildDiagnosticCode.InvalidRecipeGraph,
                        $"Item {itemId} is not classified by the exact recipe graph.", nodeId);
                Nodes.Add(new(
                    nodeId,
                    parentNodeId,
                    OutfitterCraftNodeKind.Material,
                    itemId,
                    EquipmentQuality.Normal,
                    requiredQuantity,
                    quantityPerParentCraft));
                return;
            }
            if (candidates.Length != 1)
                throw new BuildFailure(OutfitterCraftPlanBuildDiagnosticCode.AmbiguousRecipe,
                    $"Item {itemId} resolves to {candidates.Length} exact recipe candidates.", nodeId);
            if (!activeRecipeItems.Add(itemId))
                throw new BuildFailure(OutfitterCraftPlanBuildDiagnosticCode.CircularRecipe,
                    $"Exact recipe expansion encountered a cycle at item {itemId}.", nodeId);

            try
            {
                var recipe = candidates[0];
                if (!CrafterUtilityProfile.CrafterClassJobIds.Contains(recipe.RequiredClassJobId))
                    throw new BuildFailure(OutfitterCraftPlanBuildDiagnosticCode.UnsupportedRecipe,
                        $"Recipe {recipe.RecipeId} requires unsupported class/job {recipe.RequiredClassJobId}.", nodeId);
                var childNodeIds = recipe.Ingredients
                    .Select((_, index) => $"{nodeId}/{index.ToString("D2", CultureInfo.InvariantCulture)}")
                    .ToArray();
                var resolvedIngredients = recipe.Ingredients
                    .Select((ingredient, index) => new OutfitterResolvedRecipeIngredient(
                        childNodeIds[index],
                        ingredient.ItemId,
                        EquipmentQuality.Normal,
                        ingredient.QuantityPerCraft,
                        ingredient.ItemName))
                    .ToImmutableArray();
                var resolvedRecipe = new OutfitterResolvedRecipeSnapshot(
                    graph.ResolverId,
                    graph.ResolverVersion,
                    graph.DefinitionFingerprint(recipe.RecipeId),
                    recipe.RecipeId,
                    recipe.OutputItemId,
                    recipe.OutputQuantity,
                    recipe.RequiredClassJobId,
                    recipe.RequiredLevel,
                    recipe.RecipeUnlockItemId,
                    resolvedIngredients,
                    recipe.OutputItemName);
                var eligible = !crafter.IsLevelSynced &&
                    crafter.ClassJobId == recipe.RequiredClassJobId &&
                    crafter.EffectiveLevel >= recipe.RequiredLevel;
                var eligibility = new OutfitterCraftEligibilityEvidence(
                    eligible ? OutfitterCraftEligibilityState.ProvenEligible : OutfitterCraftEligibilityState.ProvenIneligible,
                    crafter.BaselineAuthorityFingerprint,
                    crafter.Character,
                    nodeId,
                    recipe.RecipeId,
                    recipe.RequiredClassJobId,
                    recipe.RequiredLevel,
                    crafter.ClassJobId,
                    crafter.EffectiveLevel,
                    eligible ? null : "The active crafter does not satisfy this recipe's exact job and level requirement.");
                Nodes.Add(new(
                    nodeId,
                    parentNodeId,
                    OutfitterCraftNodeKind.Craft,
                    itemId,
                    EquipmentQuality.Normal,
                    requiredQuantity,
                    quantityPerParentCraft,
                    recipe.RecipeId,
                    recipe.OutputQuantity,
                    recipe.RecipeUnlockItemId,
                    resolvedRecipe,
                    eligibility));

                if (recipe.RecipeUnlockItemId != 0)
                {
                    Diagnostics.Add(new(
                        OutfitterCraftDiagnosticCode.MasterRecipe,
                        $"Recipe {recipe.RecipeId} requires master-recipe unlock item {recipe.RecipeUnlockItemId} and is display-only.",
                        nodeId));
                }
                if (!eligible)
                {
                    Diagnostics.Add(new(
                        OutfitterCraftDiagnosticCode.IneligibleCrafter,
                        eligibility.Diagnostic!,
                        nodeId));
                }

                var craftCount = ((ulong)requiredQuantity + recipe.OutputQuantity - 1) / recipe.OutputQuantity;
                for (var index = 0; index < recipe.Ingredients.Length; index++)
                {
                    var ingredient = recipe.Ingredients[index];
                    var childQuantity = checked(craftCount * ingredient.QuantityPerCraft);
                    if (childQuantity > uint.MaxValue)
                        throw new OverflowException();
                    ExpandNode(
                        childNodeIds[index],
                        nodeId,
                        ingredient.ItemId,
                        ingredient.ItemName,
                        (uint)childQuantity,
                        ingredient.QuantityPerCraft,
                        checked(depth + 1));
                }
            }
            finally
            {
                activeRecipeItems.Remove(itemId);
            }
        }
    }

    private sealed record WholeListingState(
        uint CoverageQuantity,
        ulong PurchasedQuantity,
        ulong MarketGil,
        ImmutableArray<int> ListingIndices);

    private sealed record WholeListingAllocation(
        ulong TotalGil,
        ulong SurplusQuantity,
        ImmutableArray<int> ListingIndices);

    private sealed class BuildFailure : Exception
    {
        public BuildFailure(
            OutfitterCraftPlanBuildDiagnosticCode code,
            string message,
            string? nodeId = null)
            : base(message)
        {
            Code = code;
            NodeId = nodeId;
        }

        public OutfitterCraftPlanBuildDiagnosticCode Code { get; }
        public string? NodeId { get; }
    }
}
