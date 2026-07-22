using System;
using System.Threading;
using System.Threading.Tasks;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Crafting;

internal interface IOutfitterPassiveCraftOfferProvider
{
    Task<OutfitterPassiveCraftOfferPreparationResult> PrepareAsync(
        EquipmentItemDefinition definition,
        CancellationToken cancellationToken = default);

    OutfitterPassiveCraftOfferResult Build(
        OutfitterPassiveCraftOfferPreparation preparation,
        PlayerAdvisorBaseline freshlyRevalidatedBaseline,
        OutfitterMarketEvidenceBook publishedMarketEvidence,
        IAdvisorStatFamily family);

    Task<OutfitterPassiveCraftOfferResult> BuildAsync(
        PlayerAdvisorBaseline freshlyRevalidatedBaseline,
        OutfitterMarketEvidenceBook publishedMarketEvidence,
        EquipmentItemDefinition definition,
        IAdvisorStatFamily family,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-local composition seam for orchestration code. Callers schedule provider work away from
/// rendering; the seam returns only passive recipe and Advisor evidence.
/// </summary>
internal sealed class OutfitterPassiveCraftOfferProvider : IOutfitterPassiveCraftOfferProvider
{
    private readonly ICraftRecipeGraphService recipeGraphService;
    private readonly OutfitterPassiveCraftOfferService offerService;

    public OutfitterPassiveCraftOfferProvider(
        ICraftRecipeGraphService recipeGraphService,
        OutfitterPassiveCraftOfferService offerService)
    {
        this.recipeGraphService = recipeGraphService ?? throw new ArgumentNullException(nameof(recipeGraphService));
        this.offerService = offerService ?? throw new ArgumentNullException(nameof(offerService));
    }

    public async Task<OutfitterPassiveCraftOfferResult> BuildAsync(
        PlayerAdvisorBaseline freshlyRevalidatedBaseline,
        OutfitterMarketEvidenceBook publishedMarketEvidence,
        EquipmentItemDefinition definition,
        IAdvisorStatFamily family,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(freshlyRevalidatedBaseline);
        ArgumentNullException.ThrowIfNull(publishedMarketEvidence);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(family);

        var preparation = await PrepareAsync(definition, cancellationToken).ConfigureAwait(false);
        return preparation.Preparation is null
            ? new(OutfitterPassiveCraftOfferStatus.Abstained, null, null, preparation.Diagnostics)
            : Build(preparation.Preparation, freshlyRevalidatedBaseline, publishedMarketEvidence, family);
    }

    public async Task<OutfitterPassiveCraftOfferPreparationResult> PrepareAsync(
        EquipmentItemDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            var response = await recipeGraphService.BuildAsync(
                new CraftRecipeGraphRequestV1 { ItemId = definition.ItemId, ItemName = definition.Name },
                cancellationToken).ConfigureAwait(false);
            return offerService.Prepare(response, definition);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(null, [$"Craft Architect exact-recipe provider failed safely: {exception.Message}"]);
        }
    }

    public OutfitterPassiveCraftOfferResult Build(
        OutfitterPassiveCraftOfferPreparation preparation,
        PlayerAdvisorBaseline freshlyRevalidatedBaseline,
        OutfitterMarketEvidenceBook publishedMarketEvidence,
        IAdvisorStatFamily family) =>
        offerService.Build(preparation, freshlyRevalidatedBaseline, publishedMarketEvidence, family);
}
