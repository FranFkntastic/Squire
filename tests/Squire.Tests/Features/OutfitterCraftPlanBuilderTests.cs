using System.Collections.Immutable;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterCraftPlanBuilderTests
{
    private static readonly DateTimeOffset BuiltAt = DateTimeOffset.Parse("2026-07-20T00:10:00Z");
    private static readonly DateTimeOffset ReviewedAt = DateTimeOffset.Parse("2026-07-20T00:00:00Z");
    private static readonly DateTimeOffset CapturedAt = DateTimeOffset.Parse("2026-07-20T00:01:00Z");
    private static readonly DateTimeOffset PublishedAt = DateTimeOffset.Parse("2026-07-20T00:02:00Z");
    private static readonly CharacterScope Crafter = new(1234, "Test Crafter", 74);

    [Fact]
    public void Build_ExpandsRecursiveYieldsAndAllocatesCheapestCompleteSources()
    {
        var result = Build(Graph(), Evidence(), Vendors());

        Assert.Equal(OutfitterCraftPlanBuildStatus.EconomyReady, result.Status);
        Assert.True(result.IsEconomyReady);
        Assert.Empty(result.Diagnostics);
        var plan = Assert.IsType<OutfitterCraftPlan>(result.Plan);
        Assert.True(plan.Validate(requireEconomyReady: true).IsValid);
        Assert.Equal(EquipmentQuality.Normal, plan.GearQuality);
        Assert.All(plan.ExpandedNodes, node => Assert.Equal(EquipmentQuality.Normal, node.Quality));
        Assert.Equal(6u, plan.ExpandedNodes.Single(node => node.ItemId == 200).RequiredQuantity);
        Assert.Equal(10u, plan.ExpandedNodes.Single(node => node.ItemId == 300).RequiredQuantity);
        Assert.Equal(6u, plan.ExpandedNodes.Where(node => node.ItemId == 400).Sum(node => node.RequiredQuantity));

        var ore = plan.TerminalMaterials.Where(line => line.ItemId == 300).ToArray();
        Assert.Collection(
            ore,
            line =>
            {
                var market = Assert.IsType<OutfitterMarketMaterialSourceIdentity>(line.Source);
                Assert.Equal(6u, line.ConsumedQuantity);
                Assert.Equal("ore-cheap", market.ListingId);
            },
            line =>
            {
                var vendor = Assert.IsType<OutfitterGilVendorMaterialSourceIdentity>(line.Source);
                Assert.Equal(4u, line.ConsumedQuantity);
                Assert.Equal(6u, vendor.UnitPriceGil);
                Assert.StartsWith("sha256:", vendor.CatalogVersion, StringComparison.Ordinal);
            });
        Assert.Equal(10u, ore.Sum(line => line.ConsumedQuantity));

        var cloth = plan.TerminalMaterials.Where(line => line.ItemId == 400).ToArray();
        Assert.Equal([2u, 4u], cloth.Select(line => line.ConsumedQuantity));
        Assert.IsType<OutfitterMarketMaterialSourceIdentity>(cloth[0].Source);
        Assert.IsType<OutfitterGilVendorMaterialSourceIdentity>(cloth[1].Source);
        Assert.Equal(EvidenceGeneration, plan.MarketEvidence!.GenerationId);
    }

    [Fact]
    public void Build_ChargesEntireFiveOfNinetyNineListingAndExposesSurplus()
    {
        var graph = SingleMaterialGraph(300, 5);
        var evidence = Evidence(items:
        [
            FreshItem(300, [Listing(300, "stack-99", 99, 2, ReviewedAt, CapturedAt)]),
        ]);

        var result = Build(graph, evidence, Vendors(includeOffers: false), quantity: 1);

        Assert.Equal(OutfitterCraftPlanBuildStatus.EconomyReady, result.Status);
        var line = Assert.Single(result.Plan!.TerminalMaterials);
        Assert.Equal(5u, line.ConsumedQuantity);
        Assert.Equal(99u, line.PurchasedQuantity);
        Assert.Equal(94u, line.SurplusQuantity);
        Assert.Equal(198ul, (ulong)line.PurchasedQuantity * line.Source.UnitPriceGil);
    }

    [Fact]
    public void Build_SelectsDeterministicMinimumCostWholeListingCombinationInsteadOfUnitGreedyStack()
    {
        var graph = SingleMaterialGraph(300, 10);
        var listings = new[]
        {
            Listing(300, "unit-cheap-stack-99", 99, 1, ReviewedAt, CapturedAt),
            Listing(300, "stack-five-a", 5, 6, ReviewedAt, CapturedAt),
            Listing(300, "stack-five-b", 5, 6, ReviewedAt, CapturedAt),
        };
        var first = Build(
            graph,
            Evidence(items: [FreshItem(300, listings)]),
            Vendors(includeOffers: false),
            quantity: 1);
        var second = Build(
            graph,
            Evidence(items: [FreshItem(300, listings.Reverse().ToArray())]),
            Vendors(includeOffers: false),
            quantity: 1);

        Assert.Equal(OutfitterCraftPlanBuildStatus.EconomyReady, first.Status);
        Assert.Equal(first.Plan!.ComputeStructuralIdentity(), second.Plan!.ComputeStructuralIdentity());
        var market = first.Plan.TerminalMaterials
            .Select(line => Assert.IsType<OutfitterMarketMaterialSourceIdentity>(line.Source))
            .ToArray();
        Assert.Equal(["stack-five-a", "stack-five-b"], market.Select(source => source.ListingId));
        Assert.Equal(60ul, first.Plan.TerminalMaterials.Aggregate(
            0ul,
            (sum, line) => sum + (ulong)line.PurchasedQuantity * line.Source.UnitPriceGil));
        Assert.All(first.Plan.TerminalMaterials, line => Assert.Equal(0u, line.SurplusQuantity));
    }

    [Fact]
    public void Build_IsDeterministicAcrossProviderAndCatalogInputOrder()
    {
        var first = Build(Graph(reverseRecipes: false), Evidence(reverseItemsAndListings: false), Vendors(reverse: false));
        var second = Build(Graph(reverseRecipes: true), Evidence(reverseItemsAndListings: true), Vendors(reverse: true));

        Assert.Equal(OutfitterCraftPlanBuildStatus.EconomyReady, first.Status);
        Assert.Equal(first.Plan!.ComputeStructuralIdentity(), second.Plan!.ComputeStructuralIdentity());
        Assert.Equal(first.Plan.PlanId, second.Plan.PlanId);
        Assert.Equal(first.Plan.ExpandedNodes.Select(node => node.NodeId), second.Plan.ExpandedNodes.Select(node => node.NodeId));
        Assert.Equal(
            first.Plan.TerminalMaterials.Select(line => (line.MaterialKey, line.ConsumedQuantity, line.PurchasedQuantity, line.SurplusQuantity, line.Source.Kind)),
            second.Plan.TerminalMaterials.Select(line => (line.MaterialKey, line.ConsumedQuantity, line.PurchasedQuantity, line.SurplusQuantity, line.Source.Kind)));
    }

    [Fact]
    public void ExactGraph_CopiesNormalizesAndContentAddressesProviderInput()
    {
        var recipes = Recipes().ToList();
        var terminals = new List<uint> { 400, 300 };
        var graph = OutfitterExactRecipeGraph.Create("craft-architect", "7.51", 100, "Gear", recipes, terminals);
        recipes.Clear();
        terminals.Clear();

        Assert.Equal([500u, 501u], graph.Recipes.Select(recipe => recipe.RecipeId));
        Assert.True(graph.TerminalMaterialItemIds.SequenceEqual([300u, 400u]));
        Assert.Equal(64, graph.ContentIdentity.Length);
        Assert.Equal(64, graph.DefinitionFingerprint(500).Length);
        Assert.Equal(
            graph.ContentIdentity,
            OutfitterExactRecipeGraph.Create("craft-architect", "7.51", 100, "Gear", Recipes().Reverse(), [300u, 400u]).ContentIdentity);
        Assert.Throws<ArgumentException>(() => OutfitterExactRecipeGraph.Create(
            "craft-architect", "7.51", 100, "Gear", Recipes(), [300u]));
        Assert.Throws<ArgumentOutOfRangeException>(() => OutfitterExactRecipeGraph.Create(
            "craft-architect", "7.51", 100, "Gear", Recipes(), [300u, 400u], maximumDepth: 65));
    }

    [Fact]
    public void ExactGraph_BoundsLazyRecipeAndTerminalInputsBeforeNormalizationOrHashing()
    {
        var recipe = Recipe(500, 100, 1, [Ingredient(300, 1)]);

        Assert.Throws<ArgumentException>(() => OutfitterExactRecipeGraph.Create(
            "craft-architect",
            "7.51",
            100,
            "Gear",
            OversizedLazy(recipe, OutfitterExactRecipeGraph.MaximumRecipeDefinitionCount),
            [300u]));
        Assert.Throws<ArgumentException>(() => OutfitterExactRecipeGraph.Create(
            "craft-architect",
            "7.51",
            100,
            "Gear",
            [recipe],
            OversizedLazy(300u, OutfitterExactRecipeGraph.MaximumTerminalMaterialCount)));
    }

    [Fact]
    public void Build_VendorOnlyPlanNeedsNoMarketBook()
    {
        var result = new OutfitterCraftPlanBuilder(Vendors(), new FixedTimeProvider(BuiltAt))
            .Build(new(100, 3), Baseline(), null, Graph());

        Assert.Equal(OutfitterCraftPlanBuildStatus.EconomyReady, result.Status);
        Assert.Null(result.Plan!.MarketEvidence);
        Assert.All(result.Plan.TerminalMaterials, line => Assert.IsType<OutfitterGilVendorMaterialSourceIdentity>(line.Source));
    }

    [Theory]
    [InlineData(8, 89)]
    [InlineData(9, 100)]
    public void Build_IneligibleActiveCrafterProducesValidDisplayOnlyPlan(uint classJobId, short level)
    {
        var result = Build(Graph(), Evidence(), Vendors(), Baseline(classJobId, level));

        Assert.Equal(OutfitterCraftPlanBuildStatus.DisplayOnly, result.Status);
        Assert.NotNull(result.Plan);
        Assert.True(result.Plan.Validate().IsValid);
        Assert.False(result.Plan.Validate(requireEconomyReady: true).IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == OutfitterCraftPlanBuildDiagnosticCode.IneligibleCrafter);
    }

    [Fact]
    public void Build_MasterSubcraftProducesDisplayOnlyPlan()
    {
        var recipes = Recipes()
            .Select(recipe => recipe.RecipeId == 501 ? recipe with { RecipeUnlockItemId = 99 } : recipe)
            .ToArray();
        var graph = OutfitterExactRecipeGraph.Create("craft-architect", "7.51", 100, "Gear", recipes, [300u, 400u]);

        var result = Build(graph, Evidence(), Vendors());

        Assert.Equal(OutfitterCraftPlanBuildStatus.DisplayOnly, result.Status);
        Assert.True(result.Plan!.Validate().IsValid);
        Assert.Contains(result.Plan.Diagnostics, diagnostic =>
            diagnostic.Code == OutfitterCraftDiagnosticCode.MasterRecipe && diagnostic.NodeId == "root/00");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == OutfitterCraftPlanBuildDiagnosticCode.UnsupportedRecipe);
    }

    [Fact]
    public void Build_UnsupportedNonCrafterSubcraftAbstains()
    {
        var recipes = Recipes()
            .Select(recipe => recipe.RecipeId == 501 ? recipe with { RequiredClassJobId = 1 } : recipe)
            .ToArray();
        var graph = OutfitterExactRecipeGraph.Create("craft-architect", "7.51", 100, "Gear", recipes, [300u, 400u]);

        var result = Build(graph, Evidence(), Vendors());

        Assert.Equal(OutfitterCraftPlanBuildStatus.Abstained, result.Status);
        Assert.Null(result.Plan);
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == OutfitterCraftPlanBuildDiagnosticCode.UnsupportedRecipe);
    }

    [Fact]
    public void Build_RejectsAmbiguousCycleDepthAndSizeDeterministically()
    {
        var ambiguous = OutfitterExactRecipeGraph.Create(
            "craft-architect", "7.51", 100, "Gear",
            [Recipe(500, 100, 1, [Ingredient(300, 1)]), Recipe(502, 100, 1, [Ingredient(300, 2)])],
            [300u]);
        var cycle = OutfitterExactRecipeGraph.Create(
            "craft-architect", "7.51", 100, "Gear",
            [Recipe(500, 100, 1, [Ingredient(200, 1)]), Recipe(501, 200, 1, [Ingredient(100, 1)])],
            []);
        var depth = OutfitterExactRecipeGraph.Create(
            "craft-architect", "7.51", 100, "Gear", Recipes(), [300u, 400u], maximumDepth: 1);
        var size = OutfitterExactRecipeGraph.Create(
            "craft-architect", "7.51", 100, "Gear",
            [Recipe(500, 100, 1, [Ingredient(300, 1), Ingredient(400, 1)])],
            [300u, 400u], maximumExpandedNodeCount: 2);

        AssertAbstains(ambiguous, OutfitterCraftPlanBuildDiagnosticCode.AmbiguousRecipe);
        AssertAbstains(cycle, OutfitterCraftPlanBuildDiagnosticCode.CircularRecipe);
        AssertAbstains(depth, OutfitterCraftPlanBuildDiagnosticCode.MaximumDepthExceeded);
        AssertAbstains(size, OutfitterCraftPlanBuildDiagnosticCode.MaximumSizeExceeded);
        Assert.True(Build(cycle, Evidence(), Vendors()).Diagnostics.SequenceEqual(
            Build(cycle, Evidence(), Vendors()).Diagnostics));
    }

    [Fact]
    public void Build_IgnoresStaleMaterialListingsButRejectsFutureAndMixedLineageEvidence()
    {
        var stale = Evidence(
            reviewedAt: BuiltAt.AddMinutes(-20),
            capturedAt: BuiltAt.AddMinutes(-19),
            publishedAt: BuiltAt.AddMinutes(-18));
        var future = Evidence(
            reviewedAt: BuiltAt.AddMinutes(1),
            capturedAt: BuiltAt.AddMinutes(2),
            publishedAt: BuiltAt.AddMinutes(3));
        var mixedItems = DefaultEvidenceItems(ReviewedAt, CapturedAt)
            .Select(item => item.ItemId != 300
                ? item
                : item with
                {
                    Listings = item.Listings.Select(listing => listing with { SourceRevision = "other-generation-r2" }).ToArray(),
                })
            .ToArray();
        var mixed = Evidence(items: mixedItems);

        var staleResult = Build(Graph(), stale, Vendors());
        Assert.Equal(OutfitterCraftPlanBuildStatus.EconomyReady, staleResult.Status);
        Assert.NotNull(staleResult.Plan!.MarketEvidence);
        Assert.All(staleResult.Plan.TerminalMaterials, line => Assert.IsType<OutfitterGilVendorMaterialSourceIdentity>(line.Source));
        AssertEvidenceAbstains(future);
        AssertEvidenceAbstains(mixed);
    }

    [Fact]
    public void Build_RejectsIncompleteTerminalCoverageWithoutPublishingPartialPlan()
    {
        var graph = OutfitterExactRecipeGraph.Create(
            "craft-architect", "7.51", 100, "Gear",
            [Recipe(500, 100, 1, [Ingredient(999, 2)])],
            [999u]);
        var missing = new OutfitterMarketItemEvidence(
            999,
            OutfitterMarketEvidenceItemStatus.Missing,
            [],
            CapturedAt,
            "missing-r1");

        var result = Build(graph, Evidence(items: [missing]), Vendors(includeOffers: false));

        Assert.Equal(OutfitterCraftPlanBuildStatus.Abstained, result.Status);
        Assert.Null(result.Plan);
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == OutfitterCraftPlanBuildDiagnosticCode.IncompleteMaterialCoverage);
    }

    [Fact]
    public void Build_RejectsQuantityAndGilOverflow()
    {
        var quantityOverflow = OutfitterExactRecipeGraph.Create(
            "craft-architect", "7.51", 100, "Gear",
            [Recipe(500, 100, 1, [Ingredient(300, 2)])],
            [300u]);
        var gilOverflow = OutfitterExactRecipeGraph.Create(
            "craft-architect", "7.51", 100, "Gear",
            [Recipe(500, 100, 1, [Ingredient(300, 1), Ingredient(400, 1)])],
            [300u, 400u]);
        var maximumPriceVendors = OutfitterGilVendorCatalog.FromTrustedSnapshot(
        [
            new(300, 10, 20, "Ore Merchant", 30, "Test Place", uint.MaxValue),
            new(400, 11, 21, "Cloth Merchant", 31, "Test Place", uint.MaxValue),
        ]);

        var quantityResult = Build(quantityOverflow, Evidence(), Vendors(), quantity: uint.MaxValue);
        var gilResult = Build(gilOverflow, Evidence(items:
        [
            Missing(300, "ore-missing"),
            Missing(400, "cloth-missing"),
        ]), maximumPriceVendors, quantity: uint.MaxValue);

        Assert.Single(quantityResult.Diagnostics, diagnostic => diagnostic.Code == OutfitterCraftPlanBuildDiagnosticCode.ArithmeticOverflow);
        Assert.Single(gilResult.Diagnostics, diagnostic => diagnostic.Code == OutfitterCraftPlanBuildDiagnosticCode.ArithmeticOverflow);
        Assert.Null(quantityResult.Plan);
        Assert.Null(gilResult.Plan);
    }

    [Fact]
    public void Build_RejectsUntrustedOrExpiredBaselineAndReadsClockOnce()
    {
        var clock = new CountingTimeProvider(BuiltAt);
        var builder = new OutfitterCraftPlanBuilder(Vendors(), clock);
        var untrusted = Baseline() with { CaptureProvenance = null };
        var expired = Baseline(capturedAt: BuiltAt - PlayerAdvisorCaptureFreshness.TimeToLive - TimeSpan.FromTicks(1));
        var oldSnapshotWithNewCompletion = Baseline(
            capturedAt: BuiltAt.AddSeconds(-1),
            snapshotCapturedAt: BuiltAt - PlayerAdvisorCaptureFreshness.TimeToLive - TimeSpan.FromTicks(1));

        var untrustedResult = builder.Build(new(100, 3), untrusted, Evidence(), Graph());
        var expiredResult = Build(Graph(), Evidence(), Vendors(), expired);
        var oldSnapshotResult = Build(Graph(), Evidence(), Vendors(), oldSnapshotWithNewCompletion);

        Assert.Equal(1, clock.ReadCount);
        Assert.Single(untrustedResult.Diagnostics, diagnostic => diagnostic.Code == OutfitterCraftPlanBuildDiagnosticCode.InvalidBaseline);
        Assert.Single(expiredResult.Diagnostics, diagnostic => diagnostic.Code == OutfitterCraftPlanBuildDiagnosticCode.InvalidBaseline);
        Assert.Single(oldSnapshotResult.Diagnostics, diagnostic => diagnostic.Code == OutfitterCraftPlanBuildDiagnosticCode.InvalidBaseline);
    }

    [Fact]
    public void VendorCatalog_ContentIdentityIsOrderInvariantAndEvidenceSensitive()
    {
        var first = Vendors(reverse: false);
        var second = Vendors(reverse: true);
        var changed = OutfitterGilVendorCatalog.FromTrustedSnapshot(
            VendorOffers().Select(offer => offer.ItemId == 300 ? offer with { UnitPriceGil = offer.UnitPriceGil + 1 } : offer));

        Assert.Equal(first.CatalogVersion, second.CatalogVersion);
        Assert.NotEqual(first.CatalogVersion, changed.CatalogVersion);
        Assert.Throws<InvalidOperationException>(() => OutfitterGilVendorCatalog.FromTrustedSnapshot(
        [
            new(300, 10, 20, "A", 30, "Place", 5),
            new(300, 10, 20, "A", 30, "Place", 6),
        ]));

        var member = first.FindOffers(300).Single();
        var identity = OutfitterGilVendorMaterialSourceIdentity.FromCatalog(first, member);
        Assert.True(identity.HasExactCatalogMembership());
        Assert.False((identity with { UnitPriceGil = identity.UnitPriceGil + 1 }).HasExactCatalogMembership());
        Assert.Throws<InvalidOperationException>(() =>
            OutfitterGilVendorMaterialSourceIdentity.FromCatalog(first, member with { UnitPriceGil = member.UnitPriceGil + 1 }));
    }

    private static readonly Guid EvidenceGeneration = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static OutfitterCraftPlanBuildResult Build(
        OutfitterExactRecipeGraph graph,
        OutfitterMarketEvidenceBook evidence,
        OutfitterGilVendorCatalog vendors,
        PlayerAdvisorBaseline? baseline = null,
        uint quantity = 3) =>
        new OutfitterCraftPlanBuilder(vendors, new FixedTimeProvider(BuiltAt))
            .Build(new(100, quantity), baseline ?? Baseline(), evidence, graph);

    private static void AssertAbstains(
        OutfitterExactRecipeGraph graph,
        OutfitterCraftPlanBuildDiagnosticCode code)
    {
        var result = Build(graph, Evidence(), Vendors());
        Assert.Equal(OutfitterCraftPlanBuildStatus.Abstained, result.Status);
        Assert.Null(result.Plan);
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    private static void AssertEvidenceAbstains(OutfitterMarketEvidenceBook evidence)
    {
        var result = Build(Graph(), evidence, Vendors());
        Assert.Equal(OutfitterCraftPlanBuildStatus.Abstained, result.Status);
        Assert.Null(result.Plan);
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == OutfitterCraftPlanBuildDiagnosticCode.InvalidMarketEvidence);
    }

    private static OutfitterExactRecipeGraph Graph(bool reverseRecipes = false)
    {
        var recipes = reverseRecipes ? Recipes().Reverse() : Recipes();
        return OutfitterExactRecipeGraph.Create(
            "craft-architect",
            "7.51",
            100,
            "Test Gear",
            recipes,
            reverseRecipes ? [400u, 300u] : [300u, 400u]);
    }

    private static OutfitterExactRecipeGraph SingleMaterialGraph(uint itemId, uint quantity) =>
        OutfitterExactRecipeGraph.Create(
            "craft-architect",
            "7.51",
            100,
            "Test Gear",
            [Recipe(500, 100, 1, [Ingredient(itemId, quantity)])],
            [itemId]);

    private static IEnumerable<OutfitterExactRecipeDefinition> Recipes() =>
    [
        Recipe(500, 100, 2, [Ingredient(200, 3), Ingredient(400, 1)], level: 90),
        Recipe(501, 200, 4, [Ingredient(300, 5), Ingredient(400, 2)], level: 80),
    ];

    private static OutfitterExactRecipeDefinition Recipe(
        uint recipeId,
        uint outputItemId,
        uint outputQuantity,
        ImmutableArray<OutfitterExactRecipeIngredient> ingredients,
        uint classJobId = 8,
        int level = 1) =>
        new(
            recipeId,
            outputItemId,
            $"Item {outputItemId}",
            outputQuantity,
            classJobId,
            level,
            0,
            ingredients);

    private static OutfitterExactRecipeIngredient Ingredient(uint itemId, uint quantity) =>
        new(itemId, $"Item {itemId}", quantity);

    private static OutfitterGilVendorCatalog Vendors(bool reverse = false, bool includeOffers = true)
    {
        var offers = includeOffers ? VendorOffers() : [];
        return OutfitterGilVendorCatalog.FromTrustedSnapshot(reverse ? offers.Reverse() : offers);
    }

    private static OutfitterGilVendorOffer[] VendorOffers() =>
    [
        new(300, 10, 20, "Ore Merchant", 30, "Test Place", 6),
        new(400, 11, 21, "Cloth Merchant", 31, "Test Place", 4),
    ];

    private static OutfitterMarketEvidenceBook Evidence(
        bool reverseItemsAndListings = false,
        DateTimeOffset? reviewedAt = null,
        DateTimeOffset? capturedAt = null,
        DateTimeOffset? publishedAt = null,
        IReadOnlyList<OutfitterMarketItemEvidence>? items = null)
    {
        var reviewed = reviewedAt ?? ReviewedAt;
        var captured = capturedAt ?? CapturedAt;
        var published = publishedAt ?? PublishedAt;
        var values = (items ?? DefaultEvidenceItems(reviewed, captured)).ToArray();
        if (reverseItemsAndListings)
        {
            values = values
                .Reverse()
                .Select(item => item with { Listings = item.Listings.Reverse().ToArray() })
                .ToArray();
        }
        var ids = values.Select(item => item.ItemId).ToArray();
        return new(
            EvidenceGeneration,
            7,
            OutfitterMarketEvidenceBook.CurrentSchemaVersion,
            "universalis",
            "North America",
            reviewed,
            published,
            OutfitterMarketEvidenceGenerationStatus.Complete,
            new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, ids.Length, ids.Length, 100, ids),
            values);
    }

    private static IReadOnlyList<OutfitterMarketItemEvidence> DefaultEvidenceItems(
        DateTimeOffset reviewed,
        DateTimeOffset captured) =>
    [
        new(
            300,
            OutfitterMarketEvidenceItemStatus.Fresh,
            [
                Listing(300, "ore-expensive", 10, 7, reviewed, captured, worldId: 75),
                Listing(300, "ore-cheap", 6, 5, reviewed, captured),
            ],
            captured,
            "item-300-r1"),
        new(
            400,
            OutfitterMarketEvidenceItemStatus.Fresh,
            [Listing(400, "cloth-cheap", 2, 3, reviewed, captured)],
            captured,
            "item-400-r1"),
    ];

    private static OutfitterMarketListingEvidence Listing(
        uint itemId,
        string listingId,
        uint quantity,
        uint unitPrice,
        DateTimeOffset reviewed,
        DateTimeOffset captured,
        uint worldId = 74) =>
        new(
            itemId,
            EquipmentQuality.Normal,
            listingId,
            worldId == 74 ? "Test World" : "Other World",
            worldId,
            "Test Retainer",
            $"retainer-{listingId}",
            quantity,
            unitPrice,
            reviewed,
            captured,
            $"item-{itemId}-r1");

    private static OutfitterMarketItemEvidence Missing(uint itemId, string revision) =>
        new(itemId, OutfitterMarketEvidenceItemStatus.Missing, [], CapturedAt, revision);

    private static OutfitterMarketItemEvidence FreshItem(
        uint itemId,
        IReadOnlyList<OutfitterMarketListingEvidence> listings) =>
        new(itemId, OutfitterMarketEvidenceItemStatus.Fresh, listings, CapturedAt, $"item-{itemId}-r1");

    private static IEnumerable<T> OversizedLazy<T>(T value, int maximum)
    {
        for (var index = 0; index <= maximum; index++)
            yield return value;
        throw new InvalidOperationException("The bounded factory enumerated beyond its declared input limit.");
    }

    private static PlayerAdvisorBaseline Baseline(
        uint classJobId = 8,
        short level = 100,
        DateTimeOffset? capturedAt = null,
        DateTimeOffset? snapshotCapturedAt = null)
    {
        var snapshotTime = snapshotCapturedAt ?? (capturedAt ?? BuiltAt.AddSeconds(-30)).AddSeconds(-1);
        var snapshot = new CharacterEquipmentSnapshot(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            new(Crafter, 74, classJobId, snapshotTime, true, SnapshotComponentStatus.Complete),
            [],
            [],
            [],
            new Dictionary<uint, EquipmentItemDefinition>(),
            new([
                new("identity", SnapshotComponentStatus.Complete),
                new("equipped", SnapshotComponentStatus.Complete),
            ]));
        var stats = new Dictionary<EquipmentStatSemantic, int>
        {
            [EquipmentStatSemantic.Craftsmanship] = 5_399,
            [EquipmentStatSemantic.Control] = 5_200,
            [EquipmentStatSemantic.CraftingPoints] = 950,
        };
        var zeroStats = stats.ToDictionary(pair => pair.Key, _ => 0);
        var equipped = PlayerAdvisorEquippedSlotMap.All
            .Select(position => new PlayerAdvisorEquippedItemCapture(
                position.EquippedIndex,
                0,
                EquipmentQuality.Normal,
                zeroStats,
                [],
                []))
            .ToArray();
        return PlayerAdvisorBaselineAssembler.Assemble(
            snapshot,
            new(Crafter, 74, classJobId, level, level, false),
            AdvisorStatFamilies.Resolve(classJobId),
            stats,
            equipped,
            PlayerAdvisorTrustedCapture.Complete(
                Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"),
                capturedAt ?? BuiltAt.AddSeconds(-30)));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset now;

        public FixedTimeProvider(DateTimeOffset now) => this.now = now;

        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CountingTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset now;

        public CountingTimeProvider(DateTimeOffset now) => this.now = now;

        public int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return now;
        }
    }
}
