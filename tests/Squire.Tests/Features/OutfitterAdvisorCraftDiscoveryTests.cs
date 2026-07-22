using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterAdvisorCraftDiscoveryTests
{
    private static readonly DateTimeOffset PublishedAt = DateTimeOffset.Parse("2026-07-19T20:00:00Z");

    [Fact]
    public async Task ConfiguredCompositionPreparesBoundedGraphWithNoUnlockRequired()
    {
        const uint recipeId = 500;
        const uint materialItemId = 3_000;
        var fixture = Fixture();
        var root = new PlanNode
        {
            NodeId = "root",
            ItemId = checked((int)fixture.Definition.ItemId),
            Name = fixture.Definition.Name,
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Job = "Blacksmith",
            RecipeLevel = 90,
            RecipeDisplayLevel = 90,
            Yield = 1,
        };
        var material = new PlanNode
        {
            NodeId = "material",
            ParentNodeId = root.NodeId,
            Parent = root,
            ItemId = checked((int)materialItemId),
            Name = "Test Material",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = false,
        };
        root.Children.Add(material);
        var planBuilder = new FixedPlanBuilder(new CraftingPlan { RootItems = [root] });
        object[] recipeRows = [new FakeRecipe(recipeId, new FakeRowRef(0, null, false))];

        using var composition = OutfitterPassiveCraftComposition.Create(
            FakeDataManager.Create(),
            recipeRows,
            services =>
            {
                services.RemoveAll<ICoreRecipePlanBuilder>();
                services.AddSingleton<ICoreRecipePlanBuilder>(planBuilder);
                services.RemoveAll<GarlandService>();
                services.AddSingleton(new GarlandService(new HttpClient(new GarlandFixtureHandler(
                    fixture.Definition.ItemId,
                    recipeId,
                    materialItemId))));
            });

        var operation = composition.Discovery.StartPreparation(
            6,
            fixture.Baseline,
            fixture.Catalog,
            CrafterAdvisorStatFamily.Instance,
            null,
            CancellationToken.None);
        var poll = await WaitAsync(operation, 6);

        Assert.Equal(OutfitterAdvisorCraftDiscoveryPollStatus.Completed, poll.Status);
        var preparation = Assert.Single(poll.Result!.Preparations);
        var recipe = Assert.Single(preparation.RecipeGraph.FindRecipes(fixture.Definition.ItemId));
        Assert.Equal(recipeId, recipe.RecipeId);
        Assert.Equal(0u, recipe.RecipeUnlockItemId);
        Assert.Equal(new uint[] { materialItemId }, preparation.RecipeGraph.TerminalMaterialItemIds);
        Assert.Equal(CraftRecipeGraphLimitsV1.Default.MaximumDepth, preparation.RecipeGraph.MaximumDepth);
        Assert.Equal(CraftRecipeGraphLimitsV1.Default.MaximumExpandedNodeCount, preparation.RecipeGraph.MaximumExpandedNodeCount);
        Assert.Single(planBuilder.TargetItems);
    }

    [Fact]
    public async Task Success_returns_only_authoritative_ready_offers_with_concise_coverage()
    {
        var fixture = Fixture();
        var ready = BuildReadyOffer(fixture);
        var provider = new RecordingProvider((_, _, _, _) => ready);
        var discovery = new OutfitterAdvisorCraftDiscovery(provider);

        var preparationOperation = discovery.StartPreparation(
            7,
            fixture.Baseline,
            fixture.Catalog,
            CrafterAdvisorStatFamily.Instance,
            null,
            CancellationToken.None);
        var preparationPoll = await WaitAsync(preparationOperation, 7);
        var operation = discovery.StartFinalization(
            7,
            fixture.Baseline,
            fixture.Evidence,
            preparationPoll.Result!,
            CrafterAdvisorStatFamily.Instance,
            null,
            CancellationToken.None);
        var poll = await WaitAsync(operation, 7);

        Assert.Equal(OutfitterAdvisorCraftDiscoveryPollStatus.Completed, poll.Status);
        Assert.Single(poll.Result!.Offers);
        Assert.Equal(EquipmentAcquisitionSourceKind.Craft, poll.Result.Offers[0].SolverOffer.Offer.SourceKind);
        Assert.Equal([3_000u], preparationPoll.Result!.RequiredMaterialItemIds);
        Assert.Single(provider.Calls);
        Assert.Contains("1 ready", poll.Result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("1/1 eligible", poll.Result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_stops_provider_work_and_never_returns_offers()
    {
        var fixture = Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingProvider(
            (_, _, _, _) => throw new InvalidOperationException("Finalization should not run."),
            async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Cancellation should stop this provider call.");
            });
        var discovery = new OutfitterAdvisorCraftDiscovery(provider);
        using var cancellation = new CancellationTokenSource();
        var operation = discovery.StartPreparation(
            8,
            fixture.Baseline,
            fixture.Catalog,
            CrafterAdvisorStatFamily.Instance,
            null,
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var poll = await WaitAsync(operation, 8);

        Assert.Equal(OutfitterAdvisorCraftDiscoveryPollStatus.Cancelled, poll.Status);
        Assert.Null(poll.Result);
    }

    [Fact]
    public async Task Stale_generation_discards_completed_provider_results()
    {
        var fixture = Fixture();
        var ready = BuildReadyOffer(fixture);
        var provider = new RecordingProvider((_, _, _, _) => ready);
        var operation = new OutfitterAdvisorCraftDiscovery(provider).StartPreparation(
            9,
            fixture.Baseline,
            fixture.Catalog,
            CrafterAdvisorStatFamily.Instance,
            null,
            CancellationToken.None);
        await provider.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var poll = operation.Poll(10);

        Assert.Equal(OutfitterAdvisorCraftDiscoveryPollStatus.Stale, poll.Status);
        Assert.Null(poll.Result);
    }

    [Fact]
    public async Task Provider_failure_is_coverage_unavailable_not_a_session_failure()
    {
        var fixture = Fixture();
        var provider = new RecordingProvider(
            (_, _, _, _) => throw new InvalidOperationException("Finalization should not run."),
            (_, _) => Task.FromException<OutfitterPassiveCraftOfferPreparationResult>(new InvalidOperationException("provider unavailable")));
        var operation = new OutfitterAdvisorCraftDiscovery(provider).StartPreparation(
            11,
            fixture.Baseline,
            fixture.Catalog,
            CrafterAdvisorStatFamily.Instance,
            null,
            CancellationToken.None);

        var poll = await WaitAsync(operation, 11);

        Assert.Equal(OutfitterAdvisorCraftDiscoveryPollStatus.Completed, poll.Status);
        Assert.Empty(poll.Result!.Offers);
        Assert.Equal(1, poll.Result.UnavailableCount);
        Assert.Contains("1 unavailable", poll.Result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("provider unavailable", poll.Result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_are_deduplicated_bounded_and_never_called_inline_from_the_caller_path()
    {
        var fixture = Fixture();
        var definitions = Enumerable.Range(0, 20)
            .Select(index => Definition((uint)(2_000 + index), itemLevel: (uint)(700 + index)))
            .ToArray();
        var duplicated = definitions[0];
        var dictionary = definitions
            .Select((definition, index) => new KeyValuePair<uint, EquipmentItemDefinition>((uint)index, definition))
            .Append(new(999, duplicated))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var catalog = fixture.Catalog with { Definitions = dictionary };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerThreadId = Environment.CurrentManagedThreadId;
        var provider = new RecordingProvider(
            (_, _, _, _) => throw new InvalidOperationException("Finalization should not run."),
            async (definition, token) =>
            {
                await release.Task.WaitAsync(token);
                return Preparation(definition);
            });
        var operation = new OutfitterAdvisorCraftDiscovery(provider).StartPreparation(
            12,
            fixture.Baseline,
            catalog,
            CrafterAdvisorStatFamily.Instance,
            null,
            CancellationToken.None);

        Assert.True(provider.WaitForFirstCall(TimeSpan.FromSeconds(5)));
        Assert.NotEqual(callerThreadId, provider.FirstThreadId);
        Assert.Equal(OutfitterAdvisorCraftDiscoveryPollStatus.Pending, operation.Poll(12).Status);
        release.TrySetResult();
        var poll = await WaitAsync(operation, 12);

        Assert.Equal(OutfitterAdvisorCraftDiscoveryPollStatus.Completed, poll.Status);
        Assert.Equal(OutfitterAdvisorCraftDiscovery.MaximumCandidateRequests, provider.Calls.Count);
        Assert.Equal(provider.Calls.Count, provider.Calls.Select(value => value.ItemId).Distinct().Count());
        Assert.Equal(20, poll.Result!.EligibleCandidateCount);
        Assert.Contains("4 deferred", poll.Result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminal_material_evidence_scope_is_bounded_before_market_publication()
    {
        var fixture = Fixture();
        var definitions = Enumerable.Range(0, 5)
            .Select(index => Definition((uint)(4_000 + index), (uint)(800 - index)))
            .ToArray();
        var catalog = fixture.Catalog with
        {
            Definitions = definitions.ToDictionary(definition => definition.ItemId),
        };
        var provider = new RecordingProvider(
            (_, _, _, _) => throw new InvalidOperationException("Finalization should not run."),
            (definition, _) => Task.FromResult(Preparation(
                definition,
                Enumerable.Range(0, 32).Select(index => definition.ItemId * 100 + (uint)index + 1).ToArray())));
        var operation = new OutfitterAdvisorCraftDiscovery(provider).StartPreparation(
            13,
            fixture.Baseline,
            catalog,
            CrafterAdvisorStatFamily.Instance,
            null,
            CancellationToken.None);

        var poll = await WaitAsync(operation, 13);

        Assert.Equal(OutfitterAdvisorCraftDiscoveryPollStatus.Completed, poll.Status);
        Assert.Equal(OutfitterAdvisorCraftDiscovery.MaximumMaterialItemRequests, poll.Result!.RequiredMaterialItemIds.Count);
        Assert.Equal(4, poll.Result.Preparations.Count);
        Assert.Equal(1, poll.Result.DeferredCandidateCount);
    }

    private static async Task<OutfitterAdvisorCraftDiscoveryPoll> WaitAsync(
        OutfitterAdvisorCraftDiscoveryOperation operation,
        long generation)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var poll = operation.Poll(generation);
            if (poll.Status != OutfitterAdvisorCraftDiscoveryPollStatus.Pending)
                return poll;
            await Task.Delay(10);
        }
        throw new TimeoutException("Craft discovery did not reach a terminal state.");
    }

    private static OutfitterPassiveCraftOfferResult BuildReadyOffer(FixtureData fixture)
    {
        var response = new CraftRecipeGraphResponseV1
        {
            ProviderVersion = "session-test-v1",
            RecipeDataIdentity = $"sha256:{new string('A', 64)}",
            IsComplete = true,
            RootItemId = fixture.Definition.ItemId,
            RootItemName = fixture.Definition.Name,
            Limits = CraftRecipeGraphLimitsV1.Default,
            Recipes =
            [
                new CraftRecipeDefinitionV1
                {
                    RecipeId = 500,
                    OutputItemId = fixture.Definition.ItemId,
                    OutputItemName = fixture.Definition.Name,
                    OutputQuantity = 1,
                    RequiredClassJobId = 9,
                    RequiredClassJobName = "Blacksmith",
                    RequiredLevel = 90,
                    RecipeUnlockItemId = 0,
                    UnlockEvidence = CraftRecipeUnlockEvidenceV1.NoUnlockRequired,
                    ResolutionConfidence = CraftRecipeResolutionConfidenceV1.Exact,
                    DataSource = CraftRecipeDataSourceV1.GarlandStandardCraft,
                    Ingredients = [new CraftRecipeIngredientV1 { ItemId = 3_000, ItemName = "Material", QuantityPerCraft = 1 }],
                    StructuralDiagnostics = [],
                },
            ],
            TerminalMaterialItemIds = [3_000],
            Diagnostics = [],
        };
        return new OutfitterPassiveCraftOfferService(
            OutfitterGilVendorCatalog.FromTrustedSnapshot(
            [
                new(3_000, 20, 30, "Merchant", 40, "Territory", 100),
            ]),
            new FixedTimeProvider(PublishedAt.AddSeconds(30))).Build(
                response,
                fixture.Baseline,
                fixture.Evidence,
                fixture.Definition,
                CrafterAdvisorStatFamily.Instance);
    }

    private static FixtureData Fixture()
    {
        var scope = new CharacterScope(77, "Crafter", 21);
        var snapshotTime = PublishedAt.AddSeconds(-1);
        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(scope, 21, 9, snapshotTime, true, SnapshotComponentStatus.Complete),
            [],
            [],
            [],
            new Dictionary<uint, EquipmentItemDefinition>(),
            new([new("identity", SnapshotComponentStatus.Complete), new("equipped", SnapshotComponentStatus.Complete)]));
        var stats = new Dictionary<EquipmentStatSemantic, int>
        {
            [EquipmentStatSemantic.Craftsmanship] = 5_399,
            [EquipmentStatSemantic.Control] = 5_200,
            [EquipmentStatSemantic.CraftingPoints] = 950,
        };
        var captures = PlayerAdvisorEquippedSlotMap.All.Select(position => new PlayerAdvisorEquippedItemCapture(
            position.EquippedIndex,
            0,
            EquipmentQuality.Normal,
            stats.ToDictionary(pair => pair.Key, _ => 0),
            [],
            [])).ToArray();
        var baseline = PlayerAdvisorBaselineAssembler.Assemble(
            snapshot,
            new(scope, 21, 9, 100, 100, false),
            CrafterAdvisorStatFamily.Instance,
            stats,
            captures,
            PlayerAdvisorTrustedCapture.Complete(Guid.NewGuid(), PublishedAt));
        var definition = Definition(2_000, 700);
        var listing = new OutfitterMarketListingEvidence(
            definition.ItemId,
            EquipmentQuality.Normal,
            "gear",
            "Siren",
            1,
            "Retainer",
            "retainer",
            1,
            1_000,
            PublishedAt,
            PublishedAt,
            "gear-r1");
        var evidence = new OutfitterMarketEvidenceBook(
            Guid.NewGuid(),
            1,
            OutfitterMarketEvidenceBook.CurrentSchemaVersion,
            "universalis",
            "North America",
            PublishedAt,
            PublishedAt,
            OutfitterMarketEvidenceGenerationStatus.Complete,
            new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 1, 1, 100, [definition.ItemId]),
            [new(definition.ItemId, OutfitterMarketEvidenceItemStatus.Fresh, [listing], PublishedAt, "gear-r1")]);
        var catalog = new MinerBotanistAdvisorCatalogResult(
            "Test coverage.",
            [definition.ItemId],
            [],
            new Dictionary<uint, EquipmentItemDefinition> { [definition.ItemId] = definition },
            "Test catalog.");
        return new(baseline, evidence, catalog, definition);
    }

    private static EquipmentItemDefinition Definition(uint itemId, uint itemLevel) => new(
        itemId,
        $"Craft Gear {itemId}",
        100,
        itemLevel,
        EquipmentSlot.MainHand,
        new HashSet<uint> { 9 },
        1,
        true,
        false,
        true,
        true,
        1,
        true,
        null,
        null,
        false,
        StatProfile: new(
            [new(74, EquipmentStatSemantic.Craftsmanship, 400, false, "Craftsmanship")],
            0,
            0,
            0,
            0,
            true));

    private static OutfitterPassiveCraftOfferPreparationResult Preparation(
        EquipmentItemDefinition definition,
        IReadOnlyList<uint>? terminalMaterialItemIds = null) => new(
        new(
            definition,
            OutfitterExactRecipeGraph.Create(
                "session-test",
                "v1",
                definition.ItemId,
                definition.Name,
                [
                    new(
                        500,
                        definition.ItemId,
                        definition.Name,
                        1,
                        9,
                        90,
                        0,
                        (terminalMaterialItemIds ?? [3_000u])
                            .Select(itemId => new OutfitterExactRecipeIngredient(itemId, $"Material {itemId}", 1))
                            .ToImmutableArray()),
                ],
                 terminalMaterialItemIds ?? [3_000u])),
        []);

    private sealed class FixedPlanBuilder(CraftingPlan plan) : ICoreRecipePlanBuilder
    {
        public List<(int itemId, string name, int quantity, bool isHqRequired)> TargetItems { get; } = [];

        public Task<CraftingPlan> BuildPlanAsync(
            List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
            string dataCenter,
            string world,
            CancellationToken ct = default)
        {
            TargetItems.AddRange(targetItems);
            return Task.FromResult(plan);
        }

        public Task FetchVendorPricesAsync(CraftingPlan value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class GarlandFixtureHandler(uint rootItemId, uint recipeId, uint materialItemId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestedItemId = uint.Parse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath));
            var item = new GarlandItem
            {
                Id = checked((int)requestedItemId),
                Name = requestedItemId == rootItemId ? "Craft Gear" : "Test Material",
                Crafts = requestedItemId == rootItemId
                    ?
                    [
                        new GarlandCraft
                        {
                            Id = recipeId.ToString(),
                            JobId = 9,
                            RecipeLevel = 90,
                            DisplayLevel = 90,
                            UnlockItemId = null,
                            Yield = 1,
                            Ingredients =
                            [
                                new GarlandIngredient
                                {
                                    Id = checked((int)materialItemId),
                                    Name = "Test Material",
                                    Amount = 1,
                                },
                            ],
                        },
                    ]
                    : [],
            };
            var json = JsonSerializer.Serialize(new GarlandItemResponse { Item = item });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private class FakeDataManager : DispatchProxy
    {
        public static IDataManager Create() => DispatchProxy.Create<IDataManager, FakeDataManager>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected IDataManager call: {targetMethod?.Name}.");
    }

    private sealed record FakeRecipe(uint RowId, FakeRowRef SecretRecipeBook);
    private sealed record FakeRowRef(uint RowId, object? ValueNullable, bool IsValid);

    private sealed record FixtureData(
        PlayerAdvisorBaseline Baseline,
        OutfitterMarketEvidenceBook Evidence,
        MinerBotanistAdvisorCatalogResult Catalog,
        EquipmentItemDefinition Definition);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingProvider : IOutfitterPassiveCraftOfferProvider
    {
        private readonly Func<OutfitterPassiveCraftOfferPreparation, PlayerAdvisorBaseline, OutfitterMarketEvidenceBook, IAdvisorStatFamily, OutfitterPassiveCraftOfferResult> build;
        private readonly Func<EquipmentItemDefinition, CancellationToken, Task<OutfitterPassiveCraftOfferPreparationResult>> prepare;
        private int firstThreadId;
        private readonly ManualResetEventSlim firstCallSignal = new();

        public RecordingProvider(
            Func<OutfitterPassiveCraftOfferPreparation, PlayerAdvisorBaseline, OutfitterMarketEvidenceBook, IAdvisorStatFamily, OutfitterPassiveCraftOfferResult> build,
            Func<EquipmentItemDefinition, CancellationToken, Task<OutfitterPassiveCraftOfferPreparationResult>>? prepare = null)
        {
            this.build = build;
            this.prepare = prepare ?? ((definition, _) => Task.FromResult(Preparation(definition)));
        }

        public ConcurrentBag<EquipmentItemDefinition> Calls { get; } = [];
        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FirstThreadId => Volatile.Read(ref firstThreadId);

        public bool WaitForFirstCall(TimeSpan timeout) => firstCallSignal.Wait(timeout);

        public Task<OutfitterPassiveCraftOfferPreparationResult> PrepareAsync(
            EquipmentItemDefinition definition,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(definition);
            Interlocked.CompareExchange(ref firstThreadId, Environment.CurrentManagedThreadId, 0);
            firstCallSignal.Set();
            FirstCall.TrySetResult();
            return prepare(definition, cancellationToken);
        }

        public OutfitterPassiveCraftOfferResult Build(
            OutfitterPassiveCraftOfferPreparation preparation,
            PlayerAdvisorBaseline freshlyRevalidatedBaseline,
            OutfitterMarketEvidenceBook publishedMarketEvidence,
            IAdvisorStatFamily family) =>
            build(preparation, freshlyRevalidatedBaseline, publishedMarketEvidence, family);

        public async Task<OutfitterPassiveCraftOfferResult> BuildAsync(
            PlayerAdvisorBaseline freshlyRevalidatedBaseline,
            OutfitterMarketEvidenceBook publishedMarketEvidence,
            EquipmentItemDefinition definition,
            IAdvisorStatFamily family,
            CancellationToken cancellationToken = default)
        {
            var prepared = await PrepareAsync(definition, cancellationToken);
            return prepared.Preparation is null
                ? new(OutfitterPassiveCraftOfferStatus.Abstained, null, null, prepared.Diagnostics)
                : Build(prepared.Preparation, freshlyRevalidatedBaseline, publishedMarketEvidence, family);
        }
    }
}
