using System;
using System.Collections.Immutable;
using System.Linq;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

namespace MarketMafioso.Squire.Outfitter.Crafting;

internal sealed record CraftArchitectExactRecipeGraphAdaptation(
    OutfitterExactRecipeGraph? Graph,
    ImmutableArray<string> Diagnostics)
{
    public bool IsComplete => Graph is not null && Diagnostics.IsDefaultOrEmpty;
}

internal sealed class CraftArchitectExactRecipeGraphAdapter
{
    private const int MaximumDiagnosticCount = 256;

    public CraftArchitectExactRecipeGraphAdaptation Adapt(CraftRecipeGraphResponseV1 response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        if (response.SchemaVersion != CraftRecipeGraphResponseV1.CurrentSchemaVersion ||
            response.ProviderId != CraftRecipeGraphResponseV1.ExactProviderId ||
            string.IsNullOrWhiteSpace(response.ProviderVersion) ||
            !IsSha256Identity(response.RecipeDataIdentity))
        {
            diagnostics.Add("The Craft Architect exact-recipe provider identity is unsupported or incomplete.");
        }
        if (!response.IsComplete)
            diagnostics.Add("Craft Architect did not publish a complete exact-recipe response.");
        if (response.RootItemId == 0 || string.IsNullOrWhiteSpace(response.RootItemName))
            diagnostics.Add("The Craft Architect exact-recipe response has no named root item.");
        if (!ValidLimits(response.Limits))
        {
            diagnostics.Add("Craft Architect declared unsupported or unsafe exact-recipe graph limits.");
            return Rejected(diagnostics);
        }
        if (response.Recipes is null || response.TerminalMaterialItemIds is null || response.Diagnostics is null)
        {
            diagnostics.Add("The Craft Architect exact-recipe response has uninitialized collections.");
            return Rejected(diagnostics);
        }

        var limits = response.Limits;
        if (response.Recipes.Count is 0 ||
            response.Recipes.Count > limits.MaximumRecipeDefinitionCount ||
            response.Recipes.Count > OutfitterExactRecipeGraph.MaximumRecipeDefinitionCount ||
            response.TerminalMaterialItemIds.Count > limits.MaximumTerminalMaterialCount ||
            response.TerminalMaterialItemIds.Count > OutfitterExactRecipeGraph.MaximumTerminalMaterialCount ||
            response.Diagnostics.Count > limits.MaximumDiagnosticCount ||
            response.Diagnostics.Count > MaximumDiagnosticCount)
        {
            diagnostics.Add("The Craft Architect exact-recipe response exceeds its declared or consumer graph limits.");
            return Rejected(diagnostics);
        }
        if (response.Diagnostics.Any(diagnostic => diagnostic is null ||
            string.IsNullOrWhiteSpace(diagnostic.Code) ||
            string.IsNullOrWhiteSpace(diagnostic.Message) ||
            !Enum.IsDefined(diagnostic.Severity)))
        {
            diagnostics.Add("The Craft Architect exact-recipe response contains malformed diagnostics.");
        }
        if (response.Diagnostics.Count != 0)
        {
            diagnostics.Add($"Craft Architect reported {response.Diagnostics.Count} unresolved exact-recipe diagnostic(s).");
            foreach (var diagnostic in response.Diagnostics
                         .Where(value => value is not null && !string.IsNullOrWhiteSpace(value.Code) && !string.IsNullOrWhiteSpace(value.Message))
                         .Take(3))
                diagnostics.Add($"Craft Architect {diagnostic.Code}: {diagnostic.Message}");
        }

        var totalIngredientCount = 0L;
        var totalStructuralDiagnosticCount = 0;
        var recipes = ImmutableArray.CreateBuilder<OutfitterExactRecipeDefinition>(response.Recipes.Count);
        foreach (var recipe in response.Recipes)
        {
            if (recipe is null ||
                recipe.ResolutionConfidence != CraftRecipeResolutionConfidenceV1.Exact ||
                recipe.DataSource != CraftRecipeDataSourceV1.GarlandStandardCraft)
            {
                diagnostics.Add("Every Craft Architect recipe must be an exact standard craft.");
                continue;
            }
            totalStructuralDiagnosticCount = checked(totalStructuralDiagnosticCount + (recipe.StructuralDiagnostics?.Count ?? 0));
            if (totalStructuralDiagnosticCount > limits.MaximumDiagnosticCount ||
                totalStructuralDiagnosticCount > MaximumDiagnosticCount)
            {
                diagnostics.Add("Craft Architect recipe structural diagnostics exceed the declared or consumer total diagnostic limit.");
                break;
            }
            if (recipe.StructuralDiagnostics is null ||
                recipe.StructuralDiagnostics.Count > limits.MaximumDiagnosticCount ||
                recipe.StructuralDiagnostics.Count > MaximumDiagnosticCount ||
                recipe.StructuralDiagnostics.Any(diagnostic => diagnostic is null ||
                    string.IsNullOrWhiteSpace(diagnostic.Code) ||
                    string.IsNullOrWhiteSpace(diagnostic.Message) ||
                    !Enum.IsDefined(diagnostic.Severity)))
            {
                diagnostics.Add($"Craft Architect recipe {recipe.RecipeId} contains malformed structural diagnostics.");
                continue;
            }
            if (recipe.StructuralDiagnostics.Count != 0)
            {
                diagnostics.Add($"Craft Architect recipe {recipe.RecipeId} has unresolved structural diagnostics.");
                continue;
            }
            if (recipe.Ingredients is null ||
                recipe.Ingredients.Count is 0 ||
                recipe.Ingredients.Count > limits.MaximumIngredientsPerRecipe ||
                recipe.Ingredients.Count > OutfitterExactRecipeGraph.MaximumIngredientsPerRecipe ||
                recipe.Ingredients.Any(ingredient => ingredient is null))
            {
                diagnostics.Add($"Craft Architect recipe {recipe.RecipeId} has an invalid bounded ingredient collection.");
                continue;
            }
            if (!ValidUnlockEvidence(recipe))
            {
                diagnostics.Add($"Craft Architect recipe {recipe.RecipeId} lacks explicit, internally consistent unlock evidence.");
                continue;
            }

            totalIngredientCount += recipe.Ingredients.Count;
            recipes.Add(new(
                recipe.RecipeId,
                recipe.OutputItemId,
                recipe.OutputItemName,
                recipe.OutputQuantity,
                recipe.RequiredClassJobId,
                recipe.RequiredLevel,
                recipe.UnlockEvidence == CraftRecipeUnlockEvidenceV1.UnlockItemRequired
                    ? recipe.RecipeUnlockItemId
                    : 0,
                recipe.Ingredients.Select(ingredient => new OutfitterExactRecipeIngredient(
                    ingredient.ItemId,
                    ingredient.ItemName,
                    ingredient.QuantityPerCraft)).ToImmutableArray()));
        }

        if (totalIngredientCount > limits.MaximumTotalIngredientCount ||
            totalIngredientCount > OutfitterExactRecipeGraph.MaximumTotalIngredientCount)
        {
            diagnostics.Add("The Craft Architect exact-recipe response exceeds its declared or consumer total ingredient limit.");
        }
        if (diagnostics.Count != 0)
            return Rejected(diagnostics);
        try
        {
            var resolverVersion = $"{response.ProviderVersion.Length}:{response.ProviderVersion}{response.RecipeDataIdentity.Length}:{response.RecipeDataIdentity}";
            var graph = OutfitterExactRecipeGraph.Create(
                "craft-architect-exact-recipe-provider",
                resolverVersion,
                response.RootItemId,
                response.RootItemName,
                recipes,
                response.TerminalMaterialItemIds,
                limits.MaximumDepth,
                limits.MaximumExpandedNodeCount);
            return new(graph, ImmutableArray<string>.Empty);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            diagnostics.Add($"Craft Architect exact-recipe adaptation failed closed: {exception.Message}");
            return Rejected(diagnostics);
        }
    }

    private static bool ValidLimits(CraftRecipeGraphLimitsV1? limits) => limits is not null &&
        limits.MaximumDepth is >= 1 and <= OutfitterExactRecipeGraph.MaximumAllowedDepth &&
        limits.MaximumExpandedNodeCount is >= 1 and <= OutfitterExactRecipeGraph.MaximumAllowedExpandedNodeCount &&
        limits.MaximumRecipeDefinitionCount is >= 1 and <= OutfitterExactRecipeGraph.MaximumRecipeDefinitionCount &&
        limits.MaximumTerminalMaterialCount is >= 1 and <= OutfitterExactRecipeGraph.MaximumTerminalMaterialCount &&
        limits.MaximumIngredientsPerRecipe is >= 1 and <= OutfitterExactRecipeGraph.MaximumIngredientsPerRecipe &&
        limits.MaximumTotalIngredientCount is >= 1 and <= OutfitterExactRecipeGraph.MaximumTotalIngredientCount &&
        limits.MaximumDiagnosticCount is >= 1 and <= MaximumDiagnosticCount;

    private static bool IsSha256Identity(string? value) => value is not null &&
        value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToString().All(Uri.IsHexDigit);

    private static bool ValidUnlockEvidence(CraftRecipeDefinitionV1 recipe) => recipe.UnlockEvidence switch
    {
        CraftRecipeUnlockEvidenceV1.NoUnlockRequired => recipe.RecipeUnlockItemId == 0,
        CraftRecipeUnlockEvidenceV1.UnlockItemRequired => recipe.RecipeUnlockItemId != 0,
        _ => false,
    };

    private static CraftArchitectExactRecipeGraphAdaptation Rejected(
        ImmutableArray<string>.Builder diagnostics) =>
        new(null, diagnostics.ToImmutable());
}
