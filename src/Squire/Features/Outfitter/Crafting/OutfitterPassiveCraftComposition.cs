using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace MarketMafioso.Squire.Outfitter.Crafting;

/// <summary>
/// Owns the scoped Craft Architect graph used by passive Advisor discovery.
/// </summary>
internal sealed class OutfitterPassiveCraftComposition : IDisposable
{
    private ServiceProvider? serviceProvider;
    private IServiceScope? scope;

    private OutfitterPassiveCraftComposition(
        ServiceProvider serviceProvider,
        IServiceScope scope,
        OutfitterAdvisorCraftDiscovery discovery)
    {
        this.serviceProvider = serviceProvider;
        this.scope = scope;
        Discovery = discovery;
    }

    public OutfitterAdvisorCraftDiscovery Discovery { get; }

    public static OutfitterPassiveCraftComposition Create(IDataManager dataManager)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        return Create(
            dataManager,
            LuminaCraftArchitectRecipeResolutionService.Create(dataManager),
            configureServices: null);
    }

    internal static OutfitterPassiveCraftComposition Create<TRecipe>(
        IDataManager dataManager,
        IEnumerable<TRecipe> recipes,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        ArgumentNullException.ThrowIfNull(recipes);
        return Create(
            dataManager,
            LuminaCraftArchitectRecipeResolutionService.Create(new RecipeResolutionService(), recipes),
            configureServices);
    }

    private static OutfitterPassiveCraftComposition Create(
        IDataManager dataManager,
        IRecipeResolutionService recipeResolutionService,
        Action<IServiceCollection>? configureServices)
    {
        ServiceProvider? serviceProvider = null;
        IServiceScope? scope = null;
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(recipeResolutionService);
            services.AddWorkshopHostCraftAppraisal();
            configureServices?.Invoke(services);
            serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            scope = serviceProvider.CreateScope();
            var provider = new OutfitterPassiveCraftOfferProvider(
                scope.ServiceProvider.GetRequiredService<ICraftRecipeGraphService>(),
                new OutfitterPassiveCraftOfferService(new OutfitterGilVendorCatalog(dataManager)));
            return new(serviceProvider, scope, new(provider));
        }
        catch
        {
            scope?.Dispose();
            serviceProvider?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref scope, null)?.Dispose();
        Interlocked.Exchange(ref serviceProvider, null)?.Dispose();
    }
}
