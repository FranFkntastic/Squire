using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Crafting;

internal sealed class OutfitterCraftAcquisitionSourceIdentity
{
    private OutfitterCraftAcquisitionSourceIdentity(
        OutfitterCraftPlan plan,
        OutfitterCraftPlanIdentity planIdentity,
        ulong totalGil)
    {
        Plan = plan;
        PlanIdentity = planIdentity;
        TotalGil = totalGil;
        SourceCatalogKey = string.Create(
            CultureInfo.InvariantCulture,
            $"craft:{plan.GearItemId}:{planIdentity.Sha256}");
    }

    public OutfitterCraftPlan Plan { get; }
    public OutfitterCraftPlanIdentity PlanIdentity { get; }
    public ulong TotalGil { get; }
    public string SourceCatalogKey { get; }

    internal static OutfitterCraftAcquisitionSourceIdentity FromEconomyReadyPlan(OutfitterCraftPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var validation = plan.Validate(requireEconomyReady: true);
        if (!validation.IsValid || plan.GearQuality != EquipmentQuality.Normal || plan.GearQuantity != 1)
            throw new InvalidOperationException("Passive craft offers require one economy-ready NQ gear plan.");
        var total = plan.TerminalMaterials.Aggregate(
            0ul,
            (sum, line) => checked(sum + checked((ulong)line.PurchasedQuantity * line.Source.UnitPriceGil)));
        return new(plan, plan.ComputeStructuralIdentity(), total);
    }
}

internal sealed class OutfitterCraftAdvisorOffer
{
    internal OutfitterCraftAdvisorOffer(
        OutfitterCraftAcquisitionSourceIdentity source,
        EquipmentExactSolverOffer solverOffer)
    {
        Source = source;
        SolverOffer = solverOffer;
    }

    public OutfitterCraftAcquisitionSourceIdentity Source { get; }
    public EquipmentExactSolverOffer SolverOffer { get; }

    public bool Matches(
        PlayerAdvisorBaseline baseline,
        OutfitterMarketEvidenceBook marketEvidence) =>
        Matches(baseline, marketEvidence, Source.Plan.BuiltAtUtc);

    public bool Matches(
        PlayerAdvisorBaseline baseline,
        OutfitterMarketEvidenceBook marketEvidence,
        DateTimeOffset asOfUtc)
    {
        if (!CraftMarketEvidenceFreshness.IsFresh(marketEvidence, asOfUtc) ||
            !Source.Plan.RevalidateCrafterAuthority(baseline, asOfUtc) ||
            Source.Plan.GearItemId != SolverOffer.Offer.Definition.ItemId ||
            Source.PlanIdentity != Source.Plan.ComputeStructuralIdentity() ||
            SolverOffer.Offer.SourceKind != EquipmentAcquisitionSourceKind.Craft ||
            SolverOffer.Offer.ResolvedQuality != EquipmentQuality.Normal ||
            SolverOffer.Offer.Key.SourceCatalogKey != Source.SourceCatalogKey ||
            SolverOffer.AvailableQuantity != 1 ||
            SolverOffer.AcquisitionCostGil != Source.TotalGil ||
            SolverOffer.ObservationId is not null ||
            Source.Plan.MarketEvidence is not { } planEvidence ||
            Source.Plan.TerminalMaterials
                .Select(line => line.Source)
                .OfType<OutfitterMarketMaterialSourceIdentity>()
                .Any(source => !CraftMarketEvidenceFreshness.IsFresh(
                    source.ReviewedAtUtc,
                    source.CapturedAtUtc,
                    planEvidence.PublishedAtUtc,
                    asOfUtc)))
        {
            return false;
        }

        try
        {
            return planEvidence.ContentIdentity == CraftMarketEvidenceReference.FromPublishedBook(marketEvidence).ContentIdentity;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }
}

internal enum OutfitterPassiveCraftOfferStatus
{
    OfferReady,
    DisplayOnly,
    Abstained,
}

internal sealed record OutfitterPassiveCraftOfferResult(
    OutfitterPassiveCraftOfferStatus Status,
    OutfitterCraftAdvisorOffer? Offer,
    OutfitterCraftPlan? Plan,
    ImmutableArray<string> Diagnostics);

internal sealed record OutfitterPassiveCraftOfferPreparation(
    EquipmentItemDefinition Definition,
    OutfitterExactRecipeGraph RecipeGraph);

internal sealed record OutfitterPassiveCraftOfferPreparationResult(
    OutfitterPassiveCraftOfferPreparation? Preparation,
    ImmutableArray<string> Diagnostics)
{
    public bool IsPrepared => Preparation is not null;
}

/// <summary>
/// Builds one passive solver offer from exact provider data and current authority. It performs no
/// provider I/O and grants no Workbench, Artisan, route, crafting, deployment, or spending authority.
/// </summary>
internal sealed class OutfitterPassiveCraftOfferService
{
    private readonly CraftArchitectExactRecipeGraphAdapter adapter = new();
    private readonly OutfitterCraftPlanBuilder planBuilder;

    public OutfitterPassiveCraftOfferService(
        OutfitterGilVendorCatalog vendorCatalog,
        TimeProvider? timeProvider = null)
    {
        planBuilder = new(vendorCatalog, timeProvider);
    }

    public OutfitterPassiveCraftOfferResult Build(
        CraftRecipeGraphResponseV1 providerResponse,
        PlayerAdvisorBaseline freshlyRevalidatedBaseline,
        OutfitterMarketEvidenceBook publishedMarketEvidence,
        EquipmentItemDefinition definition,
        IAdvisorStatFamily family)
    {
        ArgumentNullException.ThrowIfNull(providerResponse);
        ArgumentNullException.ThrowIfNull(freshlyRevalidatedBaseline);
        ArgumentNullException.ThrowIfNull(publishedMarketEvidence);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(family);

        var preparation = Prepare(providerResponse, definition);
        return preparation.Preparation is null
            ? Abstained(preparation.Diagnostics)
            : Build(preparation.Preparation, freshlyRevalidatedBaseline, publishedMarketEvidence, family);
    }

    public OutfitterPassiveCraftOfferPreparationResult Prepare(
        CraftRecipeGraphResponseV1 providerResponse,
        EquipmentItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(providerResponse);
        ArgumentNullException.ThrowIfNull(definition);
        var adaptation = adapter.Adapt(providerResponse);
        if (!adaptation.IsComplete)
            return new(null, adaptation.Diagnostics);
        if (definition.ItemId != adaptation.Graph!.RootItemId)
        {
            return new(
                null,
                ["The exact craft root does not match the requested equipment definition."]);
        }
        return new(new(definition, adaptation.Graph), ImmutableArray<string>.Empty);
    }

    public OutfitterPassiveCraftOfferResult Build(
        OutfitterPassiveCraftOfferPreparation preparation,
        PlayerAdvisorBaseline freshlyRevalidatedBaseline,
        OutfitterMarketEvidenceBook publishedMarketEvidence,
        IAdvisorStatFamily family)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(freshlyRevalidatedBaseline);
        ArgumentNullException.ThrowIfNull(publishedMarketEvidence);
        ArgumentNullException.ThrowIfNull(family);
        var definition = preparation.Definition;
        if (definition.ItemId != preparation.RecipeGraph.RootItemId ||
            definition.ResolveStatProfile(EquipmentQuality.Normal) is not { IsComplete: true } profile ||
            !profile.Parameters.Any(value => value.Value > 0 && family.IsRelevantSemantic(value.Semantic)) ||
            AdvisorEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(definition) ||
            MinerBotanistReadOnlyAdvisor.Positions(definition).Count == 0 ||
            freshlyRevalidatedBaseline.Level is not { } targetLevel ||
            definition.EquipLevel > targetLevel ||
            !family.SupportedClassJobIds.Contains(freshlyRevalidatedBaseline.ClassJobId ?? 0) ||
            !definition.EligibleClassJobIds.Contains(freshlyRevalidatedBaseline.ClassJobId ?? 0))
        {
            return Abstained("The exact craft root does not match one complete NQ definition eligible for the active Advisor target.");
        }

        var planResult = planBuilder.Build(
            new(definition.ItemId, 1),
            freshlyRevalidatedBaseline,
            publishedMarketEvidence,
            preparation.RecipeGraph);
        if (!planResult.IsEconomyReady || planResult.Plan is null)
        {
            var diagnostics = planResult.Diagnostics.Select(value => value.Message).ToImmutableArray();
            return new(
                planResult.Status == OutfitterCraftPlanBuildStatus.DisplayOnly
                    ? OutfitterPassiveCraftOfferStatus.DisplayOnly
                    : OutfitterPassiveCraftOfferStatus.Abstained,
                null,
                planResult.Plan,
                diagnostics);
        }

        try
        {
            var source = OutfitterCraftAcquisitionSourceIdentity.FromEconomyReadyPlan(planResult.Plan);
            if (source.TotalGil is 0 or > uint.MaxValue)
                return Abstained("The complete craft route cannot be represented as one exact positive Advisor gil cost.");

            var marketSources = planResult.Plan.TerminalMaterials
                .Select(line => line.Source)
                .OfType<OutfitterMarketMaterialSourceIdentity>()
                .ToArray();
            var worlds = marketSources.Select(value => (value.WorldId, value.WorldName)).Distinct().ToArray();
            var vendors = planResult.Plan.TerminalMaterials
                .Select(line => line.Source)
                .OfType<OutfitterGilVendorMaterialSourceIdentity>()
                .Select(value => value.PhysicalSourceKey)
                .Distinct()
                .ToArray();
            if (worlds.Length > 1 || vendors.Length > 1)
                return Abstained("The current Advisor burden contract cannot represent a craft route spanning multiple worlds or vendor stops.");

            var craftNodeCount = planResult.Plan.ExpandedNodes.Count(node => node.Kind == OutfitterCraftNodeKind.Craft);
            var routeInteractions = checked(planResult.Plan.TerminalMaterials.Length + craftNodeCount);
            var loadoutOffer = new EquipmentLoadoutOffer(
                definition,
                EquipmentAcquisitionSourceKind.Craft,
                "Craft yourself · exact NQ material route",
                checked((uint)source.TotalGil),
                PriceIsEstimate: false,
                Quality: EquipmentQuality.Normal,
                SourceCatalogKey: source.SourceCatalogKey);
            var solverOffer = new EquipmentExactSolverOffer(
                loadoutOffer,
                null,
                MinerBotanistReadOnlyAdvisor.Positions(definition),
                1,
                family.VectorFromDefinition(profile),
                source.TotalGil,
                worlds.Length == 1 ? worlds[0].WorldName : null,
                vendors.Length == 1
                    ? string.Create(CultureInfo.InvariantCulture, $"vendor:{vendors[0].ShopId}:{vendors[0].VendorId}:{vendors[0].TerritoryId}")
                    : null,
                routeInteractions,
                new(0, 0, 0),
                ["NQ", "Craft", $"{craftNodeCount.ToString(CultureInfo.InvariantCulture)} craft step(s)"]);
            return new(
                OutfitterPassiveCraftOfferStatus.OfferReady,
                new(source, solverOffer),
                planResult.Plan,
                ImmutableArray<string>.Empty);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return Abstained($"Passive craft-offer construction failed closed: {exception.Message}");
        }
    }

    private static OutfitterPassiveCraftOfferResult Abstained(string diagnostic) =>
        Abstained([diagnostic]);

    private static OutfitterPassiveCraftOfferResult Abstained(ImmutableArray<string> diagnostics) =>
        new(OutfitterPassiveCraftOfferStatus.Abstained, null, null, diagnostics);
}
