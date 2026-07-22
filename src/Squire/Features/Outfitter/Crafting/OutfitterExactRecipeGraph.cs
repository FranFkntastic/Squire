using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MarketMafioso.Squire.Outfitter.Crafting;

internal sealed record OutfitterExactRecipeIngredient(
    uint ItemId,
    string ItemName,
    uint QuantityPerCraft);

internal sealed record OutfitterExactRecipeDefinition(
    uint RecipeId,
    uint OutputItemId,
    string OutputItemName,
    uint OutputQuantity,
    uint RequiredClassJobId,
    int RequiredLevel,
    uint RecipeUnlockItemId,
    ImmutableArray<OutfitterExactRecipeIngredient> Ingredients);

/// <summary>
/// Process-local, provider-neutral closure of exact recipe candidates and terminal materials.
/// Ambiguous candidates and cycles are retained so the builder can reject them deterministically.
/// </summary>
internal sealed class OutfitterExactRecipeGraph
{
    public const int MaximumRecipeDefinitionCount = 4_096;
    public const int MaximumTerminalMaterialCount = 16_384;
    public const int MaximumIngredientsPerRecipe = 32;
    public const int MaximumTotalIngredientCount = 16_384;
    public const int MaximumAllowedDepth = 64;
    public const int MaximumAllowedExpandedNodeCount = 4_096;

    private readonly ImmutableDictionary<uint, ImmutableArray<OutfitterExactRecipeDefinition>> recipesByOutputItem;
    private readonly ImmutableDictionary<uint, string> definitionFingerprints;
    private readonly ImmutableHashSet<uint> terminalMaterialItemIds;

    private OutfitterExactRecipeGraph(
        string resolverId,
        string resolverVersion,
        uint rootItemId,
        string rootItemName,
        int maximumDepth,
        int maximumExpandedNodeCount,
        ImmutableArray<OutfitterExactRecipeDefinition> recipes,
        ImmutableHashSet<uint> terminalMaterials,
        ImmutableDictionary<uint, string> fingerprints,
        string contentIdentity)
    {
        ResolverId = resolverId;
        ResolverVersion = resolverVersion;
        RootItemId = rootItemId;
        RootItemName = rootItemName;
        MaximumDepth = maximumDepth;
        MaximumExpandedNodeCount = maximumExpandedNodeCount;
        Recipes = recipes;
        TerminalMaterialItemIds = terminalMaterials.Order().ToImmutableArray();
        definitionFingerprints = fingerprints;
        terminalMaterialItemIds = terminalMaterials;
        recipesByOutputItem = recipes
            .GroupBy(recipe => recipe.OutputItemId)
            .ToImmutableDictionary(
                group => group.Key,
                group => group.OrderBy(recipe => recipe.RecipeId).ToImmutableArray());
        ContentIdentity = contentIdentity;
    }

    public string ResolverId { get; }
    public string ResolverVersion { get; }
    public uint RootItemId { get; }
    public string RootItemName { get; }
    public int MaximumDepth { get; }
    public int MaximumExpandedNodeCount { get; }
    public ImmutableArray<OutfitterExactRecipeDefinition> Recipes { get; }
    public ImmutableArray<uint> TerminalMaterialItemIds { get; }
    public string ContentIdentity { get; }

    public static OutfitterExactRecipeGraph Create(
        string resolverId,
        string resolverVersion,
        uint rootItemId,
        string rootItemName,
        IEnumerable<OutfitterExactRecipeDefinition> recipes,
        IEnumerable<uint> terminalMaterialItemIds,
        int maximumDepth = 16,
        int maximumExpandedNodeCount = 1_024)
    {
        if (string.IsNullOrWhiteSpace(resolverId) || string.IsNullOrWhiteSpace(resolverVersion))
            throw new ArgumentException("An exact recipe graph requires resolver identity and version.");
        if (rootItemId == 0 || string.IsNullOrWhiteSpace(rootItemName))
            throw new ArgumentException("An exact recipe graph requires one named root item.");
        if (maximumDepth is < 1 or > MaximumAllowedDepth)
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (maximumExpandedNodeCount is < 1 or > MaximumAllowedExpandedNodeCount)
            throw new ArgumentOutOfRangeException(nameof(maximumExpandedNodeCount));
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(terminalMaterialItemIds);

        var recipeInputs = recipes.Take(MaximumRecipeDefinitionCount + 1).ToArray();
        if (recipeInputs.Length is 0 || recipeInputs.Length > MaximumRecipeDefinitionCount)
            throw new ArgumentException($"Exact recipe graphs require 1-{MaximumRecipeDefinitionCount} recipe definitions.");
        var terminalInputs = terminalMaterialItemIds.Take(MaximumTerminalMaterialCount + 1).ToArray();
        if (terminalInputs.Length > MaximumTerminalMaterialCount)
            throw new ArgumentException($"Exact recipe graphs cannot exceed {MaximumTerminalMaterialCount} terminal materials.");

        var normalizedRecipes = recipeInputs
            .Select(Normalize)
            .OrderBy(recipe => recipe.OutputItemId)
            .ThenBy(recipe => recipe.RecipeId)
            .ToImmutableArray();
        if (normalizedRecipes.Select(recipe => recipe.RecipeId).Distinct().Count() != normalizedRecipes.Length)
            throw new ArgumentException("Exact recipe IDs must be unique within one graph.");
        if (normalizedRecipes.Sum(recipe => (long)recipe.Ingredients.Length) > MaximumTotalIngredientCount)
            throw new ArgumentException($"Exact recipe graphs cannot exceed {MaximumTotalIngredientCount} ingredients.");

        var terminals = terminalInputs.ToImmutableHashSet();
        if (terminals.Contains(0))
            throw new ArgumentException("Terminal material IDs must be non-zero.");
        var outputItems = normalizedRecipes.Select(recipe => recipe.OutputItemId).ToImmutableHashSet();
        if (!outputItems.Contains(rootItemId))
            throw new ArgumentException("The exact recipe graph must contain at least one root recipe candidate.");
        if (outputItems.Overlaps(terminals))
            throw new ArgumentException("An exact graph item cannot be both a recipe output and a terminal material.");
        var referencedItems = normalizedRecipes.SelectMany(recipe => recipe.Ingredients).Select(ingredient => ingredient.ItemId);
        if (referencedItems.Any(itemId => !outputItems.Contains(itemId) && !terminals.Contains(itemId)))
            throw new ArgumentException("Every exact ingredient must resolve to recipe candidates or an explicit terminal material.");

        var fingerprints = normalizedRecipes.ToImmutableDictionary(
            recipe => recipe.RecipeId,
            recipe => ComputeDefinitionFingerprint(resolverId, resolverVersion, recipe));
        var contentIdentity = ComputeContentIdentity(
            resolverId,
            resolverVersion,
            rootItemId,
            rootItemName,
            maximumDepth,
            maximumExpandedNodeCount,
            normalizedRecipes,
            terminals,
            fingerprints);
        return new(
            resolverId,
            resolverVersion,
            rootItemId,
            rootItemName,
            maximumDepth,
            maximumExpandedNodeCount,
            normalizedRecipes,
            terminals,
            fingerprints,
            contentIdentity);
    }

    internal ImmutableArray<OutfitterExactRecipeDefinition> FindRecipes(uint outputItemId) =>
        recipesByOutputItem.GetValueOrDefault(outputItemId, ImmutableArray<OutfitterExactRecipeDefinition>.Empty);

    internal bool IsTerminalMaterial(uint itemId) => terminalMaterialItemIds.Contains(itemId);

    internal string DefinitionFingerprint(uint recipeId) =>
        definitionFingerprints.TryGetValue(recipeId, out var fingerprint)
            ? fingerprint
            : throw new InvalidOperationException($"Recipe {recipeId} is not a member of this exact graph.");

    private static OutfitterExactRecipeDefinition Normalize(OutfitterExactRecipeDefinition recipe)
    {
        if (recipe is null ||
            recipe.RecipeId == 0 ||
            recipe.OutputItemId == 0 ||
            string.IsNullOrWhiteSpace(recipe.OutputItemName) ||
            recipe.OutputQuantity == 0 ||
            recipe.RequiredClassJobId == 0 ||
            recipe.RequiredLevel is < 1 or > 100 ||
            recipe.Ingredients.IsDefaultOrEmpty ||
            recipe.Ingredients.Length > MaximumIngredientsPerRecipe ||
            recipe.Ingredients.Any(ingredient => ingredient is null ||
                ingredient.ItemId == 0 ||
                string.IsNullOrWhiteSpace(ingredient.ItemName) ||
                ingredient.QuantityPerCraft == 0))
        {
            throw new ArgumentException("Exact recipe definitions require complete output, eligibility, yield, and ingredient identity.");
        }
        if (recipe.Ingredients.Select(ingredient => ingredient.ItemId).Distinct().Count() != recipe.Ingredients.Length)
            throw new ArgumentException($"Recipe {recipe.RecipeId} contains a duplicated ingredient item.");

        return recipe with
        {
            Ingredients = recipe.Ingredients
                .OrderBy(ingredient => ingredient.ItemId)
                .ThenBy(ingredient => ingredient.QuantityPerCraft)
                .ThenBy(ingredient => ingredient.ItemName, StringComparer.Ordinal)
                .ToImmutableArray(),
        };
    }

    private static string ComputeDefinitionFingerprint(
        string resolverId,
        string resolverVersion,
        OutfitterExactRecipeDefinition recipe)
    {
        var canonical = new StringBuilder("marketmafioso-exact-recipe/v1");
        Append(canonical, resolverId);
        Append(canonical, resolverVersion);
        AppendRecipe(canonical, recipe);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string ComputeContentIdentity(
        string resolverId,
        string resolverVersion,
        uint rootItemId,
        string rootItemName,
        int maximumDepth,
        int maximumExpandedNodeCount,
        ImmutableArray<OutfitterExactRecipeDefinition> recipes,
        ImmutableHashSet<uint> terminals,
        ImmutableDictionary<uint, string> fingerprints)
    {
        var canonical = new StringBuilder("marketmafioso-exact-recipe-graph/v1");
        Append(canonical, resolverId);
        Append(canonical, resolverVersion);
        canonical.Append('|').AppendInvariant(rootItemId);
        Append(canonical, rootItemName);
        canonical.Append('|').AppendInvariant(maximumDepth).Append('|').AppendInvariant(maximumExpandedNodeCount);
        foreach (var recipe in recipes)
        {
            AppendRecipe(canonical, recipe);
            Append(canonical, fingerprints[recipe.RecipeId]);
        }
        foreach (var terminal in terminals.Order())
            canonical.Append("|terminal|").AppendInvariant(terminal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendRecipe(StringBuilder canonical, OutfitterExactRecipeDefinition recipe)
    {
        canonical.Append("|recipe|").AppendInvariant(recipe.RecipeId)
            .Append('|').AppendInvariant(recipe.OutputItemId);
        Append(canonical, recipe.OutputItemName);
        canonical.Append('|').AppendInvariant(recipe.OutputQuantity)
            .Append('|').AppendInvariant(recipe.RequiredClassJobId)
            .Append('|').AppendInvariant(recipe.RequiredLevel)
            .Append('|').AppendInvariant(recipe.RecipeUnlockItemId);
        foreach (var ingredient in recipe.Ingredients)
        {
            canonical.Append("|ingredient|").AppendInvariant(ingredient.ItemId)
                .Append('|').AppendInvariant(ingredient.QuantityPerCraft);
            Append(canonical, ingredient.ItemName);
        }
    }

    private static void Append(StringBuilder canonical, string value) =>
        canonical.Append('|')
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
}
