using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Portfolio;

public sealed record PortfolioVendorIdentity(
    uint ShopId,
    uint VendorId,
    uint TerritoryId,
    string VendorName,
    string TerritoryName,
    uint UnitPriceGil,
    string CatalogVersion);

public sealed record PortfolioCraftRecipeEvidence(
    string PlanSha256,
    uint RecipeId,
    uint CraftCount,
    string ItemName,
    int Depth);

public sealed record PortfolioCraftMaterialEvidence(
    string PlanSha256,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    uint RequiredQuantity,
    string SourceKey,
    PortfolioAllocationSourceKind SourceKind,
    uint UnitPriceGil,
    uint ObservedAvailableQuantity,
    string EvidenceGeneration,
    DateTimeOffset ReviewedAtUtc,
    string? WorldName = null,
    string? ObservationId = null,
    string? SourceRevision = null,
    PortfolioVendorIdentity? Vendor = null,
    string? RetainerName = null,
    string? RetainerId = null);

public sealed record PortfolioAcquisitionMarketConsumer(
    string LineageKey,
    string TargetKey,
    string CandidateKey,
    string ComponentKey,
    uint RequiredQuantity,
    bool IsCraftMaterial);

public sealed record PortfolioCraftAcquisitionEvidence(
    string TargetKey,
    string CandidateKey,
    IReadOnlyList<PortfolioAllocationKey> ParentAllocations,
    IReadOnlyList<PortfolioCraftRecipeEvidence> Recipes,
    IReadOnlyList<PortfolioCraftMaterialEvidence> Materials)
{
    public IReadOnlyList<string> PlanIdentities { get; init; } = [];
}

public sealed record PortfolioAcquisitionMarketLot(
    string LineageKey,
    string TargetKey,
    string CandidateKey,
    string ComponentKey,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    uint RequiredQuantity,
    uint ObservedAvailableQuantity,
    string WorldName,
    uint UnitPriceGil,
    string ObservationId,
    string SourceRevision,
    DateTimeOffset ReviewedAtUtc,
    bool IsCraftMaterial,
    string RetainerName,
    string RetainerId,
    IReadOnlyList<PortfolioAcquisitionMarketConsumer> Consumers);

public sealed record PortfolioVendorAcquisitionAction(
    string LineageKey,
    string TargetKey,
    string CandidateKey,
    string ComponentKey,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    uint Quantity,
    PortfolioVendorIdentity Vendor,
    string EvidenceGeneration,
    DateTimeOffset ReviewedAtUtc,
    bool IsCraftMaterial);

public sealed record PortfolioArtisanRecipeLine(
    string LineageKey,
    string TargetKey,
    string CandidateKey,
    string PlanSha256,
    uint RecipeId,
    uint CraftCount,
    string ItemName,
    int Depth);

public enum PortfolioAcquisitionActionKind
{
    MarketReview,
    VendorChecklist,
    ArtisanExport,
}

public enum PortfolioAcquisitionActionStatus
{
    Pending,
    StagedForReview,
    UserConfirmed,
    Exported,
}

public sealed record PortfolioAcquisitionActionProgress(
    string LineageKey,
    PortfolioAcquisitionActionKind Kind,
    PortfolioAcquisitionActionStatus Status,
    string EvidenceGeneration,
    DateTimeOffset UpdatedAtUtc,
    string? Receipt = null);

public static class PortfolioAcquisitionComposition
{
    public static (
        IReadOnlyList<PortfolioAcquisitionMarketLot> MarketLots,
        IReadOnlyList<PortfolioVendorAcquisitionAction> VendorActions,
        IReadOnlyList<PortfolioArtisanRecipeLine> ArtisanRecipes,
        ulong MarketTotalGil) Build(
        PortfolioAuthorityFingerprint fingerprint,
        IReadOnlyList<PortfolioAcquisitionLine> lines,
        IReadOnlyList<PortfolioCraftAcquisitionEvidence> craftEvidence)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(craftEvidence);
        var markets = new List<PortfolioAcquisitionMarketLot>();
        var vendors = new List<PortfolioVendorAcquisitionAction>();
        var recipes = new List<PortfolioArtisanRecipeLine>();

        foreach (var line in lines)
        {
            var componentKey = $"selected:{line.Allocation.SourceKey}";
            if (line.Allocation.SourceKind == PortfolioAllocationSourceKind.MarketListing)
            {
                var lineageKey = PortfolioAcquisitionLineage.Encode(new(
                    fingerprint,
                    line.TargetKey,
                    line.CandidateKey,
                    line.Allocation));
                markets.Add(Market(
                    fingerprint,
                    line.TargetKey,
                    line.CandidateKey,
                    componentKey,
                    line.Allocation.ItemId,
                    line.ItemName,
                    line.Allocation.IsHighQuality,
                    line.Quantity,
                    line.ObservedAvailableQuantity,
                    line.WorldName!,
                    line.UnitPriceGil!.Value,
                    line.ObservationId!,
                    line.SourceRevision!,
                    line.ReviewedAtUtc,
                    false,
                    lineageKey,
                    line.RetainerName!,
                    line.RetainerId!));
            }
            else if (line.Allocation.SourceKind == PortfolioAllocationSourceKind.GilVendor)
            {
                var vendor = line.Vendor ?? throw new InvalidOperationException("Vendor equipment requires exact shop, NPC, territory, and price identity.");
                var lineageKey = PortfolioAcquisitionLineage.Encode(new(
                    fingerprint,
                    line.TargetKey,
                    line.CandidateKey,
                    line.Allocation));
                vendors.Add(Vendor(fingerprint, line.TargetKey, line.CandidateKey, componentKey,
                    line.Allocation.ItemId, line.ItemName, line.Allocation.IsHighQuality, line.Quantity,
                    vendor, line.EvidenceGeneration, line.ReviewedAtUtc, false, lineageKey));
            }
        }

        foreach (var craft in craftEvidence)
        {
            if (craft.ParentAllocations.Count == 0 || craft.Recipes.Count == 0)
                throw new InvalidOperationException("A portfolio craft handoff requires selected parent allocations and a full recipe tree.");
            var recipeSetIdentities = craft.Recipes.Select(value => value.PlanSha256)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var exactPlanIdentities = craft.PlanIdentities.Count > 0
                ? craft.PlanIdentities.Distinct(StringComparer.Ordinal).ToArray()
                : craft.Materials.Select(value => value.PlanSha256)
                    .Concat(recipeSetIdentities)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            if (recipeSetIdentities.Length != 1 || exactPlanIdentities.Length == 0 ||
                exactPlanIdentities.Any(string.IsNullOrWhiteSpace) ||
                craft.Materials.Any(value => !exactPlanIdentities.Contains(value.PlanSha256, StringComparer.Ordinal)) ||
                craft.Recipes.Any(value => value.RecipeId == 0 || value.CraftCount == 0 || string.IsNullOrWhiteSpace(value.ItemName)))
                throw new InvalidOperationException("A portfolio craft handoff requires exact plan identities and valid complete recipe requests.");
            foreach (var recipe in craft.Recipes)
            {
                var component = $"artisan:{recipe.PlanSha256}:{recipe.RecipeId}";
                recipes.Add(new(
                    Lineage(fingerprint, craft.TargetKey, craft.CandidateKey, "artisan", component),
                    craft.TargetKey,
                    craft.CandidateKey,
                    recipe.PlanSha256,
                    recipe.RecipeId,
                    recipe.CraftCount,
                    recipe.ItemName,
                    recipe.Depth));
            }
            foreach (var material in craft.Materials.Where(value => value.RequiredQuantity > 0))
            {
                var component = $"material:{material.PlanSha256}:{material.SourceKey}";
                switch (material.SourceKind)
                {
                    case PortfolioAllocationSourceKind.MarketListing:
                        markets.Add(Market(
                            fingerprint,
                            craft.TargetKey,
                            craft.CandidateKey,
                            component,
                            material.ItemId,
                            material.ItemName,
                            material.IsHighQuality,
                            material.RequiredQuantity,
                            material.ObservedAvailableQuantity,
                            material.WorldName!,
                            material.UnitPriceGil,
                            material.ObservationId!,
                            material.SourceRevision!,
                            material.ReviewedAtUtc,
                            true,
                            retainerName: material.RetainerName,
                            retainerId: material.RetainerId));
                        break;
                    case PortfolioAllocationSourceKind.GilVendor:
                        var vendor = material.Vendor ?? throw new InvalidOperationException("Craft vendor material requires exact vendor identity.");
                        if (vendor.UnitPriceGil != material.UnitPriceGil)
                            throw new InvalidOperationException("Craft vendor material disagrees with the exact catalog price.");
                        vendors.Add(Vendor(fingerprint, craft.TargetKey, craft.CandidateKey, component,
                            material.ItemId, material.ItemName, material.IsHighQuality, material.RequiredQuantity,
                            vendor, material.EvidenceGeneration, material.ReviewedAtUtc, true));
                        break;
                    case PortfolioAllocationSourceKind.OwnedInstance:
                        break;
                    default:
                        throw new InvalidOperationException("Portfolio craft material has an unsupported acquisition source.");
                }
            }
        }

        var orderedMarkets = AggregatePhysicalMarketLots(fingerprint, markets, lines)
            .OrderByDescending(value => PriorityFor(lines, value.TargetKey))
            .ThenBy(value => value.TargetKey, StringComparer.Ordinal)
            .ThenBy(value => value.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.LineageKey, StringComparer.Ordinal)
            .ToArray();
        var orderedVendors = vendors.OrderByDescending(value => PriorityFor(lines, value.TargetKey))
            .ThenBy(value => value.TargetKey, StringComparer.Ordinal)
            .ThenBy(value => value.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.LineageKey, StringComparer.Ordinal)
            .ToArray();
        var orderedRecipes = recipes.OrderByDescending(value => PriorityFor(lines, value.TargetKey))
            .ThenByDescending(value => value.Depth)
            .ThenBy(value => value.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.LineageKey, StringComparer.Ordinal)
            .ToArray();
        var total = orderedMarkets.Aggregate(0ul, (sum, value) =>
            checked(sum + checked((ulong)value.UnitPriceGil * value.RequiredQuantity)));
        return (orderedMarkets, orderedVendors, orderedRecipes, total);
    }

    public static IReadOnlyList<PortfolioAcquisitionActionProgress> InitialProgress(
        PortfolioAcquisitionTransfer transfer,
        DateTimeOffset nowUtc,
        bool marketStaged)
    {
        var progress = transfer.MarketLots.Select(value => new PortfolioAcquisitionActionProgress(
                value.LineageKey,
                PortfolioAcquisitionActionKind.MarketReview,
                marketStaged ? PortfolioAcquisitionActionStatus.StagedForReview : PortfolioAcquisitionActionStatus.Pending,
                transfer.AuthorityFingerprint.Sha256,
                nowUtc))
            .Concat(transfer.VendorActions.Select(value => new PortfolioAcquisitionActionProgress(
                value.LineageKey,
                PortfolioAcquisitionActionKind.VendorChecklist,
                PortfolioAcquisitionActionStatus.Pending,
                value.EvidenceGeneration,
                nowUtc)))
            .Concat(transfer.ArtisanRecipes
                .GroupBy(value => (value.TargetKey, value.CandidateKey))
                .Select(group => new PortfolioAcquisitionActionProgress(
                    ArtisanExportLineageKey(transfer.AuthorityFingerprint, group.Key.TargetKey, group.Key.CandidateKey),
                    PortfolioAcquisitionActionKind.ArtisanExport,
                    PortfolioAcquisitionActionStatus.Pending,
                    transfer.AuthorityFingerprint.Sha256,
                    nowUtc)))
            .OrderBy(value => value.Kind)
            .ThenBy(value => value.LineageKey, StringComparer.Ordinal)
            .ToArray();
        return progress;
    }

    public static string ArtisanExportLineageKey(
        PortfolioAuthorityFingerprint fingerprint,
        string targetKey,
        string candidateKey) =>
        Lineage(fingerprint, targetKey, candidateKey, "artisan-export", "all-recipes");

    private static PortfolioAcquisitionMarketLot Market(
        PortfolioAuthorityFingerprint fingerprint,
        string targetKey,
        string candidateKey,
        string componentKey,
        uint itemId,
        string itemName,
        bool highQuality,
        uint quantity,
        uint available,
        string world,
        uint unitPrice,
        string observationId,
        string sourceRevision,
        DateTimeOffset reviewedAt,
        bool craftMaterial,
        string? lineageKey = null,
        string? retainerName = null,
        string? retainerId = null)
    {
        if (itemId == 0 || quantity == 0 || available < quantity || unitPrice == 0 || reviewedAt == default ||
            string.IsNullOrWhiteSpace(world) || string.IsNullOrWhiteSpace(observationId) || string.IsNullOrWhiteSpace(sourceRevision) ||
            string.IsNullOrWhiteSpace(retainerName) || string.IsNullOrWhiteSpace(retainerId))
            throw new InvalidOperationException("Portfolio market component requires exact current listing evidence.");
        var consumerLineage = lineageKey ?? Lineage(fingerprint, targetKey, candidateKey, "market", componentKey);
        return new(
            consumerLineage,
            targetKey, candidateKey, componentKey, itemId, itemName, highQuality, quantity, available,
            world, unitPrice, observationId, sourceRevision, reviewedAt, craftMaterial,
            retainerName, retainerId,
            [new(consumerLineage, targetKey, candidateKey, componentKey, quantity, craftMaterial)]);
    }

    private static PortfolioVendorAcquisitionAction Vendor(
        PortfolioAuthorityFingerprint fingerprint,
        string targetKey,
        string candidateKey,
        string componentKey,
        uint itemId,
        string itemName,
        bool highQuality,
        uint quantity,
        PortfolioVendorIdentity vendor,
        string evidenceGeneration,
        DateTimeOffset reviewedAt,
        bool craftMaterial,
        string? lineageKey = null)
    {
        if (itemId == 0 || quantity == 0 || highQuality || vendor.ShopId == 0 || vendor.VendorId == 0 ||
            vendor.TerritoryId == 0 || vendor.UnitPriceGil == 0 || string.IsNullOrWhiteSpace(vendor.VendorName) ||
            string.IsNullOrWhiteSpace(vendor.TerritoryName) || string.IsNullOrWhiteSpace(vendor.CatalogVersion) ||
            string.IsNullOrWhiteSpace(evidenceGeneration) || reviewedAt == default)
            throw new InvalidOperationException("Portfolio vendor action requires exact NQ item, shop, NPC, territory, price, and catalog evidence.");
        return new(
            lineageKey ?? Lineage(fingerprint, targetKey, candidateKey, "vendor", componentKey),
            targetKey, candidateKey, componentKey, itemId, itemName, false, quantity, vendor,
            evidenceGeneration, reviewedAt, craftMaterial);
    }

    private static IReadOnlyList<PortfolioAcquisitionMarketLot> AggregatePhysicalMarketLots(
        PortfolioAuthorityFingerprint fingerprint,
        IReadOnlyList<PortfolioAcquisitionMarketLot> lots,
        IReadOnlyList<PortfolioAcquisitionLine> lines)
    {
        var result = new List<PortfolioAcquisitionMarketLot>();
        foreach (var group in lots.GroupBy(value => (
                     value.WorldName,
                     value.ObservationId,
                     value.SourceRevision,
                     value.ItemId,
                     value.IsHighQuality)))
        {
            var available = group.Select(value => value.ObservedAvailableQuantity).Distinct().ToArray();
            var exact = group.Select(value => (value.UnitPriceGil, value.RetainerName, value.RetainerId, value.ItemName)).Distinct().ToArray();
            if (available.Length != 1 || exact.Length != 1)
                throw new InvalidOperationException("Shared portfolio market evidence disagrees about the exact physical listing.");
            var required = group.Aggregate(0u, (sum, value) => checked(sum + value.RequiredQuantity));
            if (required > available[0])
                throw new InvalidOperationException("Shared portfolio market allocation exceeds the exact observed listing capacity.");
            var consumers = group.SelectMany(value => value.Consumers)
                .OrderByDescending(value => PriorityFor(lines, value.TargetKey))
                .ThenBy(value => value.TargetKey, StringComparer.Ordinal)
                .ThenBy(value => value.LineageKey, StringComparer.Ordinal)
                .ToArray();
            var representative = group.OrderByDescending(value => PriorityFor(lines, value.TargetKey))
                .ThenBy(value => value.TargetKey, StringComparer.Ordinal).First();
            var physicalLineage = Lineage(
                fingerprint,
                representative.TargetKey,
                representative.CandidateKey,
                "physical-market-listing",
                $"{group.Key.WorldName}:{group.Key.ObservationId}:{group.Key.SourceRevision}");
            result.Add(representative with
            {
                LineageKey = physicalLineage,
                RequiredQuantity = available[0],
                IsCraftMaterial = consumers.All(value => value.IsCraftMaterial),
                Consumers = consumers,
            });
        }
        return result;
    }

    public static PortfolioAllocationKey MaterialAllocation(PortfolioCraftMaterialEvidence material) =>
        new(
            material.SourceKind,
            material.SourceKind switch
            {
                PortfolioAllocationSourceKind.MarketListing =>
                    $"craft-material-market:{material.WorldName}:{material.ObservationId}:{material.SourceRevision}",
                PortfolioAllocationSourceKind.GilVendor when material.Vendor is { } vendor =>
                    $"craft-material-vendor:{vendor.CatalogVersion}:{vendor.ShopId}:{vendor.VendorId}:{vendor.TerritoryId}:{material.ItemId}",
                _ => throw new InvalidOperationException("Craft material does not have a supported exact allocation identity."),
            },
            material.ItemId,
            material.IsHighQuality);

    private static int PriorityFor(IReadOnlyList<PortfolioAcquisitionLine> lines, string targetKey) =>
        lines.FirstOrDefault(value => string.Equals(value.TargetKey, targetKey, StringComparison.Ordinal))?.Priority ?? 0;

    private static string Lineage(
        PortfolioAuthorityFingerprint fingerprint,
        string targetKey,
        string candidateKey,
        string componentKind,
        string componentKey) =>
        PortfolioAcquisitionLineage.Encode(new(
            fingerprint,
            targetKey,
            candidateKey,
            new(PortfolioAllocationSourceKind.Craft, componentKey, 1, false),
            componentKind,
            componentKey));
}
