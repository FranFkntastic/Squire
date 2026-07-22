using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using LuminaRecipe = Lumina.Excel.Sheets.Recipe;

namespace MarketMafioso.Squire.Outfitter.Crafting;

/// <summary>
/// Supplies version-matched recipe-book evidence when Garland omits its optional unlock field.
/// Missing or malformed Lumina rows remain unknown and therefore fail closed in Craft Architect.
/// </summary>
internal sealed class LuminaCraftArchitectRecipeResolutionService : IRecipeResolutionService
{
    private readonly IRecipeResolutionService inner;
    private readonly IReadOnlyDictionary<uint, int> unlockItemIds;

    internal LuminaCraftArchitectRecipeResolutionService(
        IRecipeResolutionService inner,
        IReadOnlyDictionary<uint, int> unlockItemIds)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.unlockItemIds = unlockItemIds ?? throw new ArgumentNullException(nameof(unlockItemIds));
    }

    public RecipeResolutionResult Resolve(PlanNode node, GarlandItem? itemData)
    {
        var result = inner.Resolve(node, itemData);
        return result.Kind == RecipeOperationKind.StandardCraft &&
               result.RecipeId is { } recipeId &&
               result.RecipeUnlockItemId is null &&
               unlockItemIds.TryGetValue(recipeId, out var unlockItemId)
            ? result with { RecipeUnlockItemId = unlockItemId }
            : result;
    }

    internal static LuminaCraftArchitectRecipeResolutionService Create(IDataManager dataManager)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        var recipes = dataManager.GetExcelSheet<LuminaRecipe>() ??
                      throw new InvalidOperationException("The Recipe sheet is unavailable for craft unlock evidence.");
        return Create(new RecipeResolutionService(), recipes);
    }

    internal static LuminaCraftArchitectRecipeResolutionService Create<TRecipe>(
        IRecipeResolutionService inner,
        IEnumerable<TRecipe> recipes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(recipes);
        var unlockItemIds = new Dictionary<uint, int>();
        foreach (var recipe in recipes)
        {
            if (recipe is null ||
                !TryReadRowId(recipe, out var recipeId) ||
                recipeId == 0 ||
                !TryReadUnlockItemId(recipe, out var unlockItemId))
                continue;
            unlockItemIds[recipeId] = unlockItemId;
        }
        return new(inner, unlockItemIds);
    }

    internal static bool TryReadUnlockItemId(object recipe, out int unlockItemId)
    {
        unlockItemId = 0;
        try
        {
            var secretBook = recipe.GetType().GetProperty("SecretRecipeBook")?.GetValue(recipe);
            if (secretBook is null || !TryReadRowId(secretBook, out var secretBookId))
                return false;
            if (secretBookId == 0)
                return true;
            if (secretBook.GetType().GetProperty("IsValid")?.GetValue(secretBook) is not true)
                return false;
            var valueProperty = secretBook.GetType().GetProperty("ValueNullable") ??
                                secretBook.GetType().GetProperty("Value");
            var secretBookValue = valueProperty?.GetValue(secretBook);
            var item = secretBookValue?.GetType().GetProperty("Item")?.GetValue(secretBookValue);
            if (item is null ||
                !TryReadRowId(item, out var itemId) ||
                itemId == 0 ||
                itemId > int.MaxValue ||
                item.GetType().GetProperty("IsValid")?.GetValue(item) is not true)
                return false;
            unlockItemId = (int)itemId;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadRowId(object value, out uint rowId)
    {
        rowId = 0;
        try
        {
            var raw = value.GetType().GetProperty("RowId")?.GetValue(value);
            if (raw is null)
                return false;
            rowId = Convert.ToUInt32(raw);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
