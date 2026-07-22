using System;
using System.Collections;
using System.Collections.Generic;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using MarketMafioso.Squire.Outfitter.Crafting;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class CraftArchitectExactRecipeGraphAdapterTests
{
    [Fact]
    public void Adapt_MapsOneCompletePublicProducerResponse()
    {
        var result = new CraftArchitectExactRecipeGraphAdapter().Adapt(Response());

        Assert.True(result.IsComplete, string.Join(" ", result.Diagnostics));
        Assert.NotNull(result.Graph);
        Assert.Equal(100u, result.Graph.RootItemId);
        Assert.Equal(new uint[] { 300, 400 }, result.Graph.TerminalMaterialItemIds);
        Assert.Equal(16, result.Graph.MaximumDepth);
        Assert.Equal(1_024, result.Graph.MaximumExpandedNodeCount);
        var root = Assert.Single(result.Graph.FindRecipes(100));
        Assert.Equal(500u, root.RecipeId);
        Assert.Equal(8u, root.RequiredClassJobId);
        Assert.Equal(2u, root.OutputQuantity);
        Assert.Equal(0u, root.RecipeUnlockItemId);
        Assert.Equal(new uint[] { 200, 300 }, root.Ingredients.Select(value => value.ItemId));
    }

    [Fact]
    public void Adapt_RejectsFallbackStructuralAndOversizedResponsesWithoutCreatingAGraph()
    {
        var exact = Response();
        var fallback = exact with
        {
            Recipes = [exact.Recipes[0] with { ResolutionConfidence = CraftRecipeResolutionConfidenceV1.Fallback }],
        };
        var structural = exact with
        {
            Recipes =
            [
                exact.Recipes[0] with
                {
                    StructuralDiagnostics = [Diagnostic("IngredientChildQuantityMismatch")],
                },
            ],
        };
        var oversized = exact with
        {
            Recipes = new CountOnlyList<CraftRecipeDefinitionV1>(
                OutfitterExactRecipeGraph.MaximumRecipeDefinitionCount + 1,
                exact.Recipes[0]),
        };

        Assert.False(new CraftArchitectExactRecipeGraphAdapter().Adapt(fallback).IsComplete);
        Assert.False(new CraftArchitectExactRecipeGraphAdapter().Adapt(structural).IsComplete);
        Assert.False(new CraftArchitectExactRecipeGraphAdapter().Adapt(oversized).IsComplete);
    }

    [Fact]
    public void Adapt_EnforcesProducerDeclaredLimitsAndTopLevelDiagnostics()
    {
        var exact = Response();
        var underDeclared = exact with
        {
            Limits = exact.Limits with { MaximumRecipeDefinitionCount = 1 },
        };
        var unsafeLimits = exact with
        {
            Limits = exact.Limits with { MaximumDepth = OutfitterExactRecipeGraph.MaximumAllowedDepth + 1 },
        };
        var diagnosed = exact with
        {
            Diagnostics = [Diagnostic("ProviderDiagnostic")],
        };
        var badIdentity = exact with { RecipeDataIdentity = "sha256:NOT-A-DIGEST" };

        Assert.False(new CraftArchitectExactRecipeGraphAdapter().Adapt(underDeclared).IsComplete);
        Assert.False(new CraftArchitectExactRecipeGraphAdapter().Adapt(unsafeLimits).IsComplete);
        var diagnosedResult = new CraftArchitectExactRecipeGraphAdapter().Adapt(diagnosed);
        Assert.False(diagnosedResult.IsComplete);
        Assert.Contains(diagnosedResult.Diagnostics, value => value.Contains("ProviderDiagnostic: Test diagnostic.", StringComparison.Ordinal));
        Assert.False(new CraftArchitectExactRecipeGraphAdapter().Adapt(badIdentity).IsComplete);
    }

    [Fact]
    public void Adapt_RequiresExplicitInternallyConsistentUnlockEvidence()
    {
        var noUnlock = new CraftArchitectExactRecipeGraphAdapter().Adapt(Response());
        var positive = new CraftArchitectExactRecipeGraphAdapter().Adapt(Response(
            CraftRecipeUnlockEvidenceV1.UnlockItemRequired,
            99));
        var unknown = new CraftArchitectExactRecipeGraphAdapter().Adapt(Response(
            CraftRecipeUnlockEvidenceV1.Unknown,
            0));
        var inconsistent = new CraftArchitectExactRecipeGraphAdapter().Adapt(Response(
            CraftRecipeUnlockEvidenceV1.UnlockItemRequired,
            0));

        Assert.True(noUnlock.IsComplete);
        Assert.True(positive.IsComplete);
        Assert.Equal(99u, positive.Graph!.FindRecipes(100).Single().RecipeUnlockItemId);
        Assert.False(unknown.IsComplete);
        Assert.False(inconsistent.IsComplete);
    }

    [Fact]
    public void Lumina_resolution_factory_supplies_valid_unlock_evidence_without_overriding_garland()
    {
        object[] rows =
        [
            new FakeRecipe(500, new FakeRowRef(0, null, false)),
            new FakeRecipe(501, new FakeRowRef(
                7,
                new FakeSecretRecipeBook(new FakeRowRef(12_345, null, true)),
                true)),
        ];
        var noBook = ResolveThroughFactory(500, null, rows);
        var bookItem = ResolveThroughFactory(501, null, rows);
        var explicitGarland = ResolveThroughFactory(500, 42, rows);

        Assert.Equal(0, noBook.RecipeUnlockItemId);
        Assert.Equal(12_345, bookItem.RecipeUnlockItemId);
        Assert.Equal(42, explicitGarland.RecipeUnlockItemId);
    }

    [Fact]
    public void Lumina_resolution_factory_keeps_malformed_or_missing_references_unknown()
    {
        object[] rows =
        [
            new FakeRecipeWithoutSecretBook(502),
            new FakeRecipe(503, new FakeRowRef(7, null, false)),
            new FakeRecipe(504, new FakeRowRef(7, null, true)),
            new FakeRecipe(505, new FakeRowRef(
                7,
                new FakeSecretRecipeBook(new FakeRowRef(0, null, false)),
                true)),
            new FakeRecipe(506, new FakeRowRef(
                7,
                new FakeSecretRecipeBook(new FakeRowRef(12_345, null, false)),
                true)),
            new FakeRecipe(507, new FakeRowRef(
                7,
                new FakeSecretRecipeBookWithoutItem(),
                true)),
        ];

        foreach (var recipeId in new uint[] { 502, 503, 504, 505, 506, 507 })
            Assert.Null(ResolveThroughFactory(recipeId, null, rows).RecipeUnlockItemId);
    }

    internal static CraftRecipeGraphResponseV1 Response(
        CraftRecipeUnlockEvidenceV1 rootUnlockEvidence = CraftRecipeUnlockEvidenceV1.NoUnlockRequired,
        uint rootUnlockItemId = 0) => new()
    {
        ProviderVersion = "ca-test-v1",
        RecipeDataIdentity = $"sha256:{new string('A', 64)}",
        IsComplete = true,
        RootItemId = 100,
        RootItemName = "Test Gear",
        Limits = CraftRecipeGraphLimitsV1.Default,
        Recipes =
        [
            Recipe(
                500,
                100,
                "Test Gear",
                2,
                8,
                90,
                rootUnlockEvidence,
                rootUnlockItemId,
                [Ingredient(200, "Test Ingot", 2), Ingredient(300, "Test Cloth", 3)]),
            Recipe(
                501,
                200,
                "Test Ingot",
                1,
                8,
                80,
                CraftRecipeUnlockEvidenceV1.NoUnlockRequired,
                0,
                [Ingredient(400, "Test Ore", 4)]),
        ],
        TerminalMaterialItemIds = [300, 400],
        Diagnostics = [],
    };

    private static CraftRecipeDefinitionV1 Recipe(
        uint recipeId,
        uint outputItemId,
        string outputItemName,
        uint outputQuantity,
        uint requiredClassJobId,
        int requiredLevel,
        CraftRecipeUnlockEvidenceV1 unlockEvidence,
        uint unlockItemId,
        IReadOnlyList<CraftRecipeIngredientV1> ingredients) => new()
    {
        RecipeId = recipeId,
        OutputItemId = outputItemId,
        OutputItemName = outputItemName,
        OutputQuantity = outputQuantity,
        RequiredClassJobId = requiredClassJobId,
        RequiredClassJobName = "Test Crafter",
        RequiredLevel = requiredLevel,
        RecipeUnlockItemId = unlockItemId,
        UnlockEvidence = unlockEvidence,
        ResolutionConfidence = CraftRecipeResolutionConfidenceV1.Exact,
        DataSource = CraftRecipeDataSourceV1.GarlandStandardCraft,
        Ingredients = ingredients,
        StructuralDiagnostics = [],
    };

    private static CraftRecipeIngredientV1 Ingredient(uint itemId, string name, uint quantity) => new()
    {
        ItemId = itemId,
        ItemName = name,
        QuantityPerCraft = quantity,
    };

    private static CraftRecipeGraphDiagnosticV1 Diagnostic(string code) => new()
    {
        Code = code,
        Severity = CraftRecipeGraphDiagnosticSeverityV1.Error,
        Message = "Test diagnostic.",
    };

    private static RecipeResolutionResult ResolveThroughFactory(
        uint recipeId,
        int? garlandUnlockItemId,
        IEnumerable<object> rows) =>
        LuminaCraftArchitectRecipeResolutionService.Create(
                new FixedRecipeResolutionService(Resolution(recipeId, garlandUnlockItemId)),
                rows)
            .Resolve(null!, null);

    private static RecipeResolutionResult Resolution(uint recipeId, int? unlockItemId) => new(
        RecipeOperationKind.StandardCraft,
        RecipeResolutionConfidence.Exact,
        RecipeDataSourceKind.GarlandStandardCraft,
        recipeId,
        13,
        "Weaver",
        61,
        61,
        0,
        unlockItemId,
        1,
        1,
        null,
        null,
        []);

    private sealed class FixedRecipeResolutionService(RecipeResolutionResult result) : IRecipeResolutionService
    {
        public RecipeResolutionResult Resolve(PlanNode node, GarlandItem? itemData) => result;
    }

    private sealed record FakeRecipe(uint RowId, FakeRowRef SecretRecipeBook);
    private sealed record FakeRecipeWithoutSecretBook(uint RowId);
    private sealed record FakeSecretRecipeBook(FakeRowRef Item);
    private sealed record FakeSecretRecipeBookWithoutItem;
    private sealed record FakeRowRef(uint RowId, object? ValueNullable, bool IsValid);

    private sealed class CountOnlyList<T>(int count, T value) : IReadOnlyList<T>
    {
        public int Count { get; } = count;
        public T this[int index] => index >= 0 && index < Count ? value : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Oversized input must be rejected from Count.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
