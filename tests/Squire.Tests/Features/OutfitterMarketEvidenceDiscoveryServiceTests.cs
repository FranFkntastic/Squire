using System.Net;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterMarketEvidenceDiscoveryServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T16:00:00Z");

    [Fact]
    public async Task WarmCache_PerformsNoFetchAndPublishesCompleteGeneration()
    {
        var cache = Cache();
        cache.Store(Key(10), ItemEvidence(10, Now, Listing(10, "nq", false, 500)));
        var source = new StubListingSource((_, _) => throw new InvalidOperationException("Warm cache must not fetch."));
        var service = new OutfitterMarketEvidenceDiscoveryService(source, cache, utcNow: () => Now);

        var result = await service.DiscoverAsync(Request([10]), CancellationToken.None);

        Assert.Empty(source.Requests);
        Assert.Equal(OutfitterMarketEvidenceGenerationStatus.Complete, result.WorkingBook.Status);
        Assert.True(result.PublishedBook?.IsPublishable);
        Assert.Equal(OutfitterMarketEvidenceItemStatus.Fresh, Assert.Single(result.WorkingBook.Items).Status);
    }

    [Fact]
    public async Task Discovery_PreservesNqAndHqAsSeparateListingRows()
    {
        var source = new StubListingSource((itemId, _) => Task.FromResult<IReadOnlyList<MarketAcquisitionListing>>(
        [
            Listing(itemId, "same-price-nq", false, 500),
            Listing(itemId, "same-price-hq", true, 500),
        ]));
        var service = new OutfitterMarketEvidenceDiscoveryService(source, Cache(), utcNow: () => Now);

        var result = await service.DiscoverAsync(Request([10]), CancellationToken.None);

        var listings = Assert.Single(result.WorkingBook.Items).Listings;
        Assert.Equal(2, listings.Count);
        Assert.Contains(listings, listing => listing.Quality == EquipmentQuality.Normal && listing.ListingId == "same-price-nq");
        Assert.Contains(listings, listing => listing.Quality == EquipmentQuality.High && listing.ListingId == "same-price-hq");
    }

    [Fact]
    public async Task Discovery_CoalescesExactDuplicateProviderRows()
    {
        var duplicate = Listing(10, "duplicate", false, 500);
        var source = new StubListingSource((_, _) => Task.FromResult<IReadOnlyList<MarketAcquisitionListing>>(
            [duplicate, duplicate with { }]));
        var service = new OutfitterMarketEvidenceDiscoveryService(source, Cache(), utcNow: () => Now);

        var result = await service.DiscoverAsync(Request([10]), CancellationToken.None);

        var item = Assert.Single(result.WorkingBook.Items);
        Assert.Equal(OutfitterMarketEvidenceItemStatus.Fresh, item.Status);
        Assert.Equal("duplicate", Assert.Single(item.Listings).ListingId);
    }

    [Fact]
    public async Task Discovery_RejectsConflictingRowsWithTheSameListingIdentity()
    {
        var first = Listing(10, "collision", false, 500);
        var source = new StubListingSource((_, _) => Task.FromResult<IReadOnlyList<MarketAcquisitionListing>>(
            [first, first with { UnitPrice = 501 }]));
        var service = new OutfitterMarketEvidenceDiscoveryService(source, Cache(), utcNow: () => Now);

        var result = await service.DiscoverAsync(Request([10]), CancellationToken.None);

        var item = Assert.Single(result.WorkingBook.Items);
        Assert.Equal(OutfitterMarketEvidenceItemStatus.Failed, item.Status);
        Assert.Contains("conflicting observable market rows", item.Diagnostic, StringComparison.Ordinal);
        Assert.Null(result.PublishedBook);
    }

    [Fact]
    public async Task Discovery_PreservesSameRawListingIdAcrossWorlds()
    {
        var first = Listing(10, "cross-world-id", false, 500);
        var second = first with { WorldId = 74, WorldName = "Coeurl", LastReviewTimeUtc = Now.AddMinutes(-2) };
        var source = new StubListingSource((_, _) => Task.FromResult<IReadOnlyList<MarketAcquisitionListing>>([first, second]));
        var service = new OutfitterMarketEvidenceDiscoveryService(source, Cache(), utcNow: () => Now);

        var result = await service.DiscoverAsync(Request([10]), CancellationToken.None);

        var item = Assert.Single(result.WorkingBook.Items);
        Assert.Equal(OutfitterMarketEvidenceItemStatus.Fresh, item.Status);
        Assert.Equal(2, item.Listings.Count);
        Assert.Contains(item.Listings, listing => listing.WorldName == "Siren" && listing.ListingId == "cross-world-id");
        Assert.Contains(item.Listings, listing => listing.WorldName == "Coeurl" && listing.ListingId == "cross-world-id");
        Assert.NotNull(result.PublishedBook);
    }

    [Fact]
    public async Task PersistedExactDuplicates_AreRepairedBeforeWarmCacheUse()
    {
        var evidence = ItemEvidence(10, Now, Listing(10, "persisted-duplicate", false, 500));
        var duplicate = evidence with { Listings = [evidence.Listings[0], evidence.Listings[0] with { }] };
        var source = new StubListingSource((_, _) => throw new InvalidOperationException("Repaired warm cache must not fetch."));
        var service = new OutfitterMarketEvidenceDiscoveryService(
            source,
            Cache(),
            utcNow: () => Now,
            initialPublishedBook: PublishedBook(duplicate));

        var result = await service.DiscoverAsync(Request([10]), CancellationToken.None);

        Assert.Empty(source.Requests);
        Assert.Single(Assert.Single(result.WorkingBook.Items).Listings);
        Assert.Single(Assert.Single(result.PublishedBook!.Items).Listings);
    }

    [Fact]
    public async Task DuplicateDiscovery_IsCoalescedIntoOneProviderFetch()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new StubListingSource(async (itemId, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return [Listing(itemId, "listing", false, 100)];
        });
        var service = new OutfitterMarketEvidenceDiscoveryService(source, Cache(), utcNow: () => Now);
        var request = Request([10]);

        var first = service.DiscoverAsync(request, CancellationToken.None);
        await started.Task;
        var second = service.DiscoverAsync(request, CancellationToken.None);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(source.Requests);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task StaleEvidence_RemainsVisibleDuringRefreshThenIsReplaced()
    {
        var cache = Cache();
        cache.Store(Key(10), ItemEvidence(10, Now.AddMinutes(-10), Listing(10, "stale", false, 500)));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new StubListingSource(async (itemId, cancellationToken) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return [Listing(itemId, "fresh", true, 700)];
        });
        var previous = PublishedBook(ItemEvidence(10, Now.AddMinutes(-15), Listing(10, "published", false, 600)));
        var service = new OutfitterMarketEvidenceDiscoveryService(source, cache, utcNow: () => Now, initialPublishedBook: previous);
        var request = Request([10]);

        var discovery = service.DiscoverAsync(request, CancellationToken.None);
        await started.Task;
        var live = service.GetLiveState(request);
        Assert.Equal(OutfitterMarketEvidenceItemStatus.StaleUsable, Assert.Single(live!.VisibleItems).Status);
        Assert.Same(previous, live.PreviousPublishedBook);
        release.SetResult();
        var result = await discovery;

        var published = Assert.Single(result.PublishedBook!.Items);
        Assert.Equal(EquipmentQuality.High, Assert.Single(published.Listings).Quality);
        Assert.Equal("fresh", published.Listings[0].ListingId);
    }

    [Fact]
    public async Task RefreshFailure_KeepsPreviousPublishedAndExposesRetryAfter()
    {
        var cache = Cache();
        cache.Store(Key(10), ItemEvidence(10, Now.AddMinutes(-10), Listing(10, "stale", false, 500)));
        var retryAfter = Now.AddMinutes(2);
        var source = new StubListingSource((_, _) => throw new UniversalisMarketListingsHttpException(
            HttpStatusCode.TooManyRequests,
            new("https://example.invalid"),
            null,
            retryAfter));
        var previous = PublishedBook(ItemEvidence(10, Now.AddMinutes(-15), Listing(10, "published", false, 600)));
        var service = new OutfitterMarketEvidenceDiscoveryService(source, cache, utcNow: () => Now, initialPublishedBook: previous);
        var request = Request([10]);

        var result = await service.DiscoverAsync(request, CancellationToken.None);

        Assert.Equal(OutfitterMarketEvidenceGenerationStatus.Partial, result.WorkingBook.Status);
        Assert.Same(previous, result.PublishedBook);
        var stale = Assert.Single(result.WorkingBook.Items);
        Assert.Equal(OutfitterMarketEvidenceItemStatus.StaleUsable, stale.Status);
        Assert.Equal(retryAfter, stale.RetryAfterUtc);
        Assert.Equal(OutfitterMarketDiscoveryStage.Partial, service.GetLiveState(request)?.Progress.Stage);
        Assert.Equal(retryAfter, service.GetLiveState(request)?.Progress.RetryAfterUtc);

        var deferred = await service.DiscoverAsync(request, CancellationToken.None);
        Assert.Single(source.Requests);
        Assert.Equal(OutfitterMarketEvidenceGenerationStatus.Partial, deferred.WorkingBook.Status);
    }

    [Fact]
    public async Task PreviousPublishedGeneration_IsNotExposedAcrossDifferentRequestScope()
    {
        var previous = PublishedBook(ItemEvidence(10, Now.AddMinutes(-15), Listing(10, "published", false, 600)));
        var source = new StubListingSource((itemId, _) => Task.FromResult<IReadOnlyList<MarketAcquisitionListing>>(
            [Listing(itemId, "fresh", false, 700)]));
        var service = new OutfitterMarketEvidenceDiscoveryService(source, Cache(), utcNow: () => Now, initialPublishedBook: previous);
        var request = Request([10, 11]);

        var result = await service.DiscoverAsync(request, CancellationToken.None);

        Assert.Null(result.PreviousPublishedBook);
        Assert.Equal(2, result.PublishedBook?.Coverage.CatalogItemCount);
    }

    [Fact]
    public void PublishedGeneration_MatchesOnlyExactSourceRegionCoverageAndListingDepth()
    {
        var book = PublishedBook(ItemEvidence(10, Now, Listing(10, "published", false, 600)));

        Assert.True(book.Matches(Request([10])));
        Assert.False(book.Matches(Request([10]) with { Region = "Europe" }));
        Assert.False(book.Matches(Request([10]) with { ListingLimit = 50 }));
        Assert.False(book.Matches(Request([10, 11])));
        Assert.False(book.Matches(Request([10], OutfitterMarketCoverageMode.Sampled, 1)));
    }

    [Fact]
    public void AdvisorSession_PromotesOnlyACompleteNewGeneration()
    {
        var published = PublishedBook(ItemEvidence(10, Now, Listing(10, "published", false, 600)));
        var partial = published with
        {
            GenerationId = Guid.NewGuid(),
            PublishedAtUtc = null,
            Status = OutfitterMarketEvidenceGenerationStatus.Partial,
            Items = [Assert.Single(published.Items) with { Status = OutfitterMarketEvidenceItemStatus.StaleUsable }],
        };
        var request = Request([10]);

        Assert.Null(MinerBotanistAdvisorSessionEvidencePolicy.SelectCurrent(
            new(published, partial, published, false),
            request));
        Assert.Same(published, MinerBotanistAdvisorSessionEvidencePolicy.SelectCurrent(
            new(null, published, published, true),
            request));
    }

    [Fact]
    public async Task SampledCoverage_IsExplicitAndDeterministic()
    {
        var source = new StubListingSource((itemId, _) => Task.FromResult<IReadOnlyList<MarketAcquisitionListing>>(
            [Listing(itemId, $"listing-{itemId}", false, itemId)]));
        var service = new OutfitterMarketEvidenceDiscoveryService(source, Cache(), utcNow: () => Now);
        var request = Request([10, 9, 8, 7, 6], OutfitterMarketCoverageMode.Sampled, 3);

        var result = await service.DiscoverAsync(request, CancellationToken.None);

        Assert.True(result.WorkingBook.Coverage.IsSampled);
        Assert.Equal(5, result.WorkingBook.Coverage.CatalogItemCount);
        Assert.Equal([6u, 7u, 8u], result.WorkingBook.Coverage.QueriedItemIds);
        Assert.Equal([6u, 7u, 8u], source.Requests.Order().ToArray());
    }

    [Fact]
    public async Task Cancellation_RemainsVisibleInDiscoveryTruth()
    {
        var source = new StubListingSource(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });
        var service = new OutfitterMarketEvidenceDiscoveryService(source, Cache(), utcNow: () => Now);
        var request = Request([10]);
        using var cancellation = new CancellationTokenSource();

        var discovery = service.DiscoverAsync(request, cancellation.Token);
        while (source.Requests.Count == 0)
            await Task.Yield();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
        Assert.Equal(OutfitterMarketDiscoveryStage.Cancelled, service.GetLiveState(request)?.Progress.Stage);
    }

    [Fact]
    public async Task FileStore_RoundTripsOnlyPublishedCompleteGeneration()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mmf-market-evidence-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "published.json");
        try
        {
            var store = new OutfitterMarketEvidenceFileStore(path);
            var book = PublishedBook(ItemEvidence(10, Now, Listing(10, "listing", true, 700)));

            await store.SaveAsync(book, CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.Equal(book.GenerationId, loaded?.GenerationId);
            Assert.Equal(EquipmentQuality.High, Assert.Single(Assert.Single(loaded!.Items).Listings).Quality);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PersistedBook_SeedsWarmCacheOnStartup()
    {
        var persisted = PublishedBook(ItemEvidence(10, Now, Listing(10, "persisted", false, 450)));
        var store = new StubBookStore(persisted);
        var source = new StubListingSource((_, _) => throw new InvalidOperationException("Persisted warm evidence must avoid a fetch."));
        var service = new OutfitterMarketEvidenceDiscoveryService(source, Cache(), store, () => Now);

        var result = await service.DiscoverAsync(Request([10]), CancellationToken.None);

        Assert.Empty(source.Requests);
        Assert.Equal("persisted", Assert.Single(Assert.Single(result.WorkingBook.Items).Listings).ListingId);
    }

    [Fact]
    public async Task ExhaustiveDiscovery_DoesNotSilentlyCapAtTwentyFourItems()
    {
        var source = new StubListingSource((itemId, _) => Task.FromResult<IReadOnlyList<MarketAcquisitionListing>>(
            [Listing(itemId, $"listing-{itemId}", false, 100)]));
        var service = new OutfitterMarketEvidenceDiscoveryService(source, Cache(), utcNow: () => Now);
        var itemIds = Enumerable.Range(1, 30).Select(value => (uint)value).ToArray();

        var result = await service.DiscoverAsync(Request(itemIds), CancellationToken.None);

        Assert.Equal(30, result.WorkingBook.Coverage.QueriedItemCount);
        Assert.Equal(30, source.Requests.Count);
    }

    private static OutfitterMarketEvidenceCache Cache() => new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));

    private static OutfitterMarketEvidenceRequest Request(
        IReadOnlyList<uint> itemIds,
        OutfitterMarketCoverageMode coverageMode = OutfitterMarketCoverageMode.ExhaustiveWithinScope,
        int? sampleSize = null) => new("universalis", "North America", itemIds, 100, coverageMode, sampleSize, 3);

    private static OutfitterMarketEvidenceCacheKey Key(uint itemId) => new("universalis", "North America", itemId, 100);

    private static OutfitterMarketItemEvidence ItemEvidence(
        uint itemId,
        DateTimeOffset capturedAt,
        MarketAcquisitionListing listing)
    {
        var evidence = new OutfitterMarketListingEvidence(
            itemId,
            listing.IsHq ? EquipmentQuality.High : EquipmentQuality.Normal,
            listing.ListingId,
            listing.WorldName,
            listing.WorldId,
            listing.RetainerName,
            listing.RetainerId,
            listing.Quantity,
            listing.UnitPrice,
            listing.LastReviewTimeUtc,
            capturedAt,
            listing.ListingId);
        return new(itemId, OutfitterMarketEvidenceItemStatus.Fresh, [evidence], capturedAt, listing.ListingId);
    }

    private static OutfitterMarketEvidenceBook PublishedBook(params OutfitterMarketItemEvidence[] items) => new(
        Guid.NewGuid(),
        1,
        OutfitterMarketEvidenceBook.CurrentSchemaVersion,
        "universalis",
        "North America",
        Now.AddMinutes(-15),
        Now.AddMinutes(-15),
        OutfitterMarketEvidenceGenerationStatus.Complete,
        new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, items.Length, items.Length, 100, items.Select(item => item.ItemId).ToArray()),
        items);

    private static MarketAcquisitionListing Listing(uint itemId, string listingId, bool hq, uint unitPrice) => new()
    {
        ItemId = itemId,
        ListingId = listingId,
        IsHq = hq,
        UnitPrice = unitPrice,
        Quantity = 1,
        WorldName = "Siren",
        WorldId = 64,
        RetainerName = "Retainer",
        RetainerId = "retainer",
        LastReviewTimeUtc = Now.AddMinutes(-1),
    };

    private sealed class StubListingSource(
        Func<uint, CancellationToken, Task<IReadOnlyList<MarketAcquisitionListing>>> fetch) : IMarketAcquisitionListingSource
    {
        private readonly object gate = new();
        private readonly List<uint> requests = [];

        public IReadOnlyList<uint> Requests
        {
            get
            {
                lock (gate)
                    return requests.ToArray();
            }
        }

        public Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsAsync(
            string region,
            uint itemId,
            int listingLimit,
            CancellationToken cancellationToken)
        {
            lock (gate)
                requests.Add(itemId);
            return fetch(itemId, cancellationToken);
        }

        public Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsForWorldAsync(
            string worldName,
            uint itemId,
            int listingLimit,
            CancellationToken cancellationToken) => FetchListingsAsync(worldName, itemId, listingLimit, cancellationToken);
    }

    private sealed class StubBookStore(OutfitterMarketEvidenceBook? book) : IOutfitterMarketEvidenceBookStore
    {
        public Task<OutfitterMarketEvidenceBook?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(book);

        public Task SaveAsync(OutfitterMarketEvidenceBook saved, CancellationToken cancellationToken)
        {
            book = saved;
            return Task.CompletedTask;
        }
    }
}
