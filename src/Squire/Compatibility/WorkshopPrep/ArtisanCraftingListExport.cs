using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketMafioso.WorkshopPrep;

internal readonly record struct ArtisanCraftingListRecipeRequest(
    uint RecipeId,
    int CraftCount,
    bool NQOnly = true,
    bool Skipping = false);

internal sealed record ArtisanCraftingListExportResult(
    string Json,
    int RecipeCount,
    int ExpandedEntryCount);

internal static class ArtisanCraftingListExport
{
    internal const int MaximumExpandedEntries = 10_000;

    internal static ArtisanCraftingListExportResult Create(
        string name,
        IEnumerable<ArtisanCraftingListRecipeRequest> requests,
        int? listId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(requests);
        if (listId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(listId), listId, "Artisan list ID must be positive.");

        var recipes = new List<ArtisanListItem>();
        var recipeIndexes = new Dictionary<(uint RecipeId, bool NQOnly, bool Skipping), int>();
        var expandedEntryCount = 0;
        foreach (var request in requests)
        {
            if (request.RecipeId == 0)
                throw new ArgumentOutOfRangeException(nameof(requests), "Artisan recipe IDs must be positive.");
            if (request.CraftCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(requests), "Artisan craft counts must be positive.");
            if (request.CraftCount > MaximumExpandedEntries - expandedEntryCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requests),
                    $"Artisan crafting lists cannot exceed {MaximumExpandedEntries:N0} expanded entries.");
            }

            expandedEntryCount += request.CraftCount;
            var key = (request.RecipeId, request.NQOnly, request.Skipping);
            if (recipeIndexes.TryGetValue(key, out var existingIndex))
            {
                recipes[existingIndex] = recipes[existingIndex] with
                {
                    Quantity = recipes[existingIndex].Quantity + request.CraftCount,
                };
                continue;
            }

            recipeIndexes.Add(key, recipes.Count);
            recipes.Add(new ArtisanListItem(
                request.RecipeId,
                request.CraftCount,
                new ArtisanListItemOptions(request.NQOnly, request.Skipping)));
        }

        var expandedList = new List<uint>(expandedEntryCount);
        foreach (var recipe in recipes)
        {
            for (var index = 0; index < recipe.Quantity; index++)
                expandedList.Add(recipe.ID);
        }

        var artisanList = new ArtisanCraftingList(
            listId ?? Random.Shared.Next(100, 50000),
            name,
            recipes,
            expandedList,
            SkipIfEnough: false,
            SkipLiteral: false,
            Materia: false,
            Repair: false,
            RepairPercent: 50,
            AddAsQuickSynth: false,
            TidyAfter: true,
            OnlyRestockNonCrafted: false);

        return new ArtisanCraftingListExportResult(
            JsonSerializer.Serialize(artisanList),
            recipes.Count,
            expandedEntryCount);
    }

    private sealed record ArtisanCraftingList(
        [property: JsonPropertyName("ID")] int ID,
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Recipes")] IReadOnlyList<ArtisanListItem> Recipes,
        [property: JsonPropertyName("ExpandedList")] IReadOnlyList<uint> ExpandedList,
        [property: JsonPropertyName("SkipIfEnough")] bool SkipIfEnough,
        [property: JsonPropertyName("SkipLiteral")] bool SkipLiteral,
        [property: JsonPropertyName("Materia")] bool Materia,
        [property: JsonPropertyName("Repair")] bool Repair,
        [property: JsonPropertyName("RepairPercent")] int RepairPercent,
        [property: JsonPropertyName("AddAsQuickSynth")] bool AddAsQuickSynth,
        [property: JsonPropertyName("TidyAfter")] bool TidyAfter,
        [property: JsonPropertyName("OnlyRestockNonCrafted")] bool OnlyRestockNonCrafted);

    private sealed record ArtisanListItem(
        [property: JsonPropertyName("ID")] uint ID,
        [property: JsonPropertyName("Quantity")] int Quantity,
        [property: JsonPropertyName("ListItemOptions")] ArtisanListItemOptions ListItemOptions);

    private sealed record ArtisanListItemOptions(
        [property: JsonPropertyName("NQOnly")] bool NQOnly,
        [property: JsonPropertyName("Skipping")] bool Skipping);
}
