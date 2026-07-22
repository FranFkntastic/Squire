using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Squire.Outfitter.MarketEvidence;

public sealed class OutfitterMarketEvidenceDiscoveryService
{
    private readonly IMarketAcquisitionListingSource listingSource;
    private readonly OutfitterMarketEvidenceCache cache;
    private readonly IOutfitterMarketEvidenceBookStore? bookStore;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly ConcurrentDictionary<string, Lazy<Task<OutfitterMarketDiscoveryResult>>> inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OutfitterMarketDiscoveryLiveState> liveStates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private OutfitterMarketEvidenceBook? latestPublished;
    private bool loaded;
    private long revision;

    public OutfitterMarketEvidenceDiscoveryService(
        IMarketAcquisitionListingSource listingSource,
        OutfitterMarketEvidenceCache cache,
        IOutfitterMarketEvidenceBookStore? bookStore = null,
        Func<DateTimeOffset>? utcNow = null,
        OutfitterMarketEvidenceBook? initialPublishedBook = null)
    {
        this.listingSource = listingSource ?? throw new ArgumentNullException(nameof(listingSource));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.bookStore = bookStore;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        latestPublished = initialPublishedBook is null ? null : NormalizeBook(initialPublishedBook);
        loaded = initialPublishedBook is not null || bookStore is null;
        revision = latestPublished?.Revision ?? 0;
        if (latestPublished is not null)
            SeedCache(latestPublished);
    }

    public event Action<OutfitterMarketDiscoveryLiveState>? StateChanged;

    public OutfitterMarketDiscoveryLiveState? GetLiveState(OutfitterMarketEvidenceRequest request) =>
        liveStates.GetValueOrDefault(Signature(request));

    public Task<OutfitterMarketDiscoveryResult> DiscoverAsync(
        OutfitterMarketEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var signature = Signature(request);
        var candidate = new Lazy<Task<OutfitterMarketDiscoveryResult>>(
            () => DiscoverAndReleaseAsync(signature, request, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        return inFlight.GetOrAdd(signature, candidate).Value;
    }

    private async Task<OutfitterMarketDiscoveryResult> DiscoverAndReleaseAsync(
        string signature,
        OutfitterMarketEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DiscoverCoreAsync(signature, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            inFlight.TryRemove(signature, out _);
        }
    }

    private async Task<OutfitterMarketDiscoveryResult> DiscoverCoreAsync(
        string signature,
        OutfitterMarketEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        await EnsurePublishedLoadedAsync(cancellationToken).ConfigureAwait(false);
        var previous = latestPublished is { } publishedCandidate && publishedCandidate.Matches(request)
            ? publishedCandidate
            : null;
        var now = utcNow();
        var catalog = request.ItemIds.Where(itemId => itemId != 0).Distinct().Order().ToArray();
        var selected = request.CoverageMode == OutfitterMarketCoverageMode.Sampled
            ? catalog.Take(Math.Clamp(request.SampleSize ?? 1, 1, Math.Max(1, catalog.Length))).ToArray()
            : catalog;
        var coverage = new OutfitterMarketCoverage(
            request.CoverageMode,
            catalog.Length,
            selected.Length,
            Math.Clamp(request.ListingLimit, 1, 100),
            selected);
        var visible = new ConcurrentDictionary<uint, OutfitterMarketItemEvidence>();
        PublishState(signature, previous, visible.Values, new(
            OutfitterMarketDiscoveryStage.Cataloging,
            selected.Length,
            catalog.Length,
            coverage.IsSampled
                ? $"Selected {selected.Length} of {catalog.Length} catalog items for sampled discovery."
                : $"Cataloged {selected.Length} items for exhaustive discovery within scope.",
            now));

        try
        {
            var fetch = new List<(uint ItemId, OutfitterMarketEvidenceCacheKey Key, OutfitterMarketItemEvidence? Stale)>();
            var cacheCompleted = 0;
            foreach (var itemId in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Key(request, itemId);
                var lookup = cache.Get(key, utcNow());
                switch (lookup.Status)
                {
                    case OutfitterMarketEvidenceCacheStatus.Fresh:
                        visible[itemId] = lookup.Evidence!;
                        break;
                    case OutfitterMarketEvidenceCacheStatus.StaleUsable:
                        visible[itemId] = lookup.Evidence!;
                        if (lookup.Evidence!.RetryAfterUtc is not { } retryAfter || retryAfter <= utcNow())
                            fetch.Add((itemId, key, lookup.Evidence));
                        break;
                    case OutfitterMarketEvidenceCacheStatus.Failed:
                        visible[itemId] = lookup.Evidence!;
                        break;
                    default:
                        fetch.Add((itemId, key, null));
                        break;
                }
                cacheCompleted++;
                PublishState(signature, previous, visible.Values, new(
                    OutfitterMarketDiscoveryStage.CacheLookup,
                    cacheCompleted,
                    selected.Length,
                    $"Checked cached market evidence for {cacheCompleted} of {selected.Length} items.",
                    utcNow()));
            }

            var fetched = 0;
            using var concurrency = new SemaphoreSlim(request.MaxConcurrency, request.MaxConcurrency);
            var fetchTasks = fetch.Select(async target =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await FetchOneAsync(request, target, visible, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    concurrency.Release();
                    var completed = Interlocked.Increment(ref fetched);
                    PublishState(signature, previous, visible.Values, new(
                        OutfitterMarketDiscoveryStage.Fetching,
                        completed,
                        fetch.Count,
                        $"Fetched {completed} of {fetch.Count} missing or stale market entries.",
                        utcNow(),
                        visible.Values.Where(value => value.RetryAfterUtc is not null).Select(value => value.RetryAfterUtc).Min()));
                }
            }).ToArray();
            if (fetchTasks.Length > 0)
                await Task.WhenAll(fetchTasks).ConfigureAwait(false);

            PublishState(signature, previous, visible.Values, new(
                OutfitterMarketDiscoveryStage.Merging,
                visible.Count,
                selected.Length,
                "Merging exact-quality listing evidence into one generation.",
                utcNow()));
            var items = selected
                .Select(itemId => visible.GetValueOrDefault(itemId) ?? Failed(itemId, utcNow(), "Evidence disappeared during generation merge."))
                .ToArray();
            var hasUnresolved = items.Any(item => item.Status is OutfitterMarketEvidenceItemStatus.StaleUsable or OutfitterMarketEvidenceItemStatus.Failed);
            var generationStatus = hasUnresolved
                ? items.All(item => item.Status == OutfitterMarketEvidenceItemStatus.Failed)
                    ? OutfitterMarketEvidenceGenerationStatus.Failed
                    : OutfitterMarketEvidenceGenerationStatus.Partial
                : OutfitterMarketEvidenceGenerationStatus.Complete;
            var working = new OutfitterMarketEvidenceBook(
                Guid.NewGuid(),
                Interlocked.Increment(ref revision),
                OutfitterMarketEvidenceBook.CurrentSchemaVersion,
                request.SourceKey.Trim(),
                request.Region.Trim(),
                utcNow(),
                null,
                generationStatus,
                coverage,
                items);

            OutfitterMarketEvidenceBook? published = previous;
            var changed = false;
            if (working.IsPublishable)
            {
                PublishState(signature, previous, visible.Values, new(
                    OutfitterMarketDiscoveryStage.Publishing,
                    0,
                    1,
                    "Publishing complete market evidence generation.",
                    utcNow()));
                var candidate = working with { PublishedAtUtc = utcNow() };
                if (bookStore is not null)
                    await bookStore.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
                changed = !EquivalentEvidence(previous, candidate);
                latestPublished = candidate;
                published = candidate;
            }

            var finalStage = generationStatus switch
            {
                OutfitterMarketEvidenceGenerationStatus.Complete => OutfitterMarketDiscoveryStage.Complete,
                OutfitterMarketEvidenceGenerationStatus.Partial => OutfitterMarketDiscoveryStage.Partial,
                _ => OutfitterMarketDiscoveryStage.Failed,
            };
            PublishState(signature, previous, items, new(
                finalStage,
                items.Count(item => item.Status is OutfitterMarketEvidenceItemStatus.Fresh or OutfitterMarketEvidenceItemStatus.Missing),
                items.Length,
                generationStatus == OutfitterMarketEvidenceGenerationStatus.Complete
                    ? "Market evidence generation is complete."
                    : "Market evidence is incomplete; the previous published generation remains authoritative.",
                utcNow(),
                items.Where(value => value.RetryAfterUtc is not null).Select(value => value.RetryAfterUtc).Min()));
            return new(previous, working, published, changed);
        }
        catch (OperationCanceledException)
        {
            PublishState(signature, previous, visible.Values, new(
                OutfitterMarketDiscoveryStage.Cancelled,
                visible.Count,
                selected.Length,
                "Market evidence discovery was cancelled; visible prior evidence remains unchanged.",
                utcNow()));
            throw;
        }
        catch (Exception exception)
        {
            PublishState(signature, previous, visible.Values, new(
                OutfitterMarketDiscoveryStage.Failed,
                visible.Count,
                selected.Length,
                $"Market evidence discovery failed: {exception.Message}",
                utcNow()));
            throw;
        }
    }

    private async Task FetchOneAsync(
        OutfitterMarketEvidenceRequest request,
        (uint ItemId, OutfitterMarketEvidenceCacheKey Key, OutfitterMarketItemEvidence? Stale) target,
        ConcurrentDictionary<uint, OutfitterMarketItemEvidence> visible,
        CancellationToken cancellationToken)
    {
        try
        {
            var listings = await listingSource.FetchListingsAsync(
                request.Region,
                target.ItemId,
                Math.Clamp(request.ListingLimit, 1, 100),
                cancellationToken).ConfigureAwait(false);
            var capturedAt = utcNow();
            var valid = NormalizeListings(listings
                .Where(listing => listing.ItemId == target.ItemId && listing.Quantity > 0 && listing.UnitPrice > 0)
                .OrderBy(listing => listing.UnitPrice)
                .ThenBy(listing => listing.IsHq)
                .ThenBy(listing => listing.WorldName, StringComparer.Ordinal)
                .ThenBy(listing => listing.ListingId, StringComparer.Ordinal)
                .ToArray());
            var sourceRevision = Revision(valid);
            var evidenceListings = valid.Select(listing => new OutfitterMarketListingEvidence(
                target.ItemId,
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
                sourceRevision)).ToArray();
            var evidence = new OutfitterMarketItemEvidence(
                target.ItemId,
                evidenceListings.Length == 0 ? OutfitterMarketEvidenceItemStatus.Missing : OutfitterMarketEvidenceItemStatus.Fresh,
                evidenceListings,
                capturedAt,
                sourceRevision);
            cache.Store(target.Key, evidence);
            visible[target.ItemId] = evidence;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var retryAfter = exception is UniversalisMarketListingsHttpException universalis && universalis.RetryAfterUtc is { } providerRetry
                ? providerRetry
                : utcNow().AddSeconds(15);
            if (target.Stale is not null)
            {
                var stale = target.Stale with
                {
                    Status = OutfitterMarketEvidenceItemStatus.StaleUsable,
                    Diagnostic = $"Refresh failed: {exception.Message}",
                    RetryAfterUtc = retryAfter,
                };
                cache.Store(target.Key, stale);
                visible[target.ItemId] = stale;
                return;
            }
            var failure = Failed(target.ItemId, utcNow(), exception.Message, retryAfter);
            cache.Store(target.Key, failure);
            visible[target.ItemId] = failure;
        }
    }

    private async Task EnsurePublishedLoadedAsync(CancellationToken cancellationToken)
    {
        if (loaded)
            return;
        await loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (loaded)
                return;
            latestPublished = await bookStore!.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (latestPublished is not null)
                latestPublished = NormalizeBook(latestPublished);
            if (latestPublished is not null)
            {
                revision = Math.Max(revision, latestPublished.Revision);
                SeedCache(latestPublished);
            }
            loaded = true;
        }
        finally
        {
            loadGate.Release();
        }
    }

    private void PublishState(
        string signature,
        OutfitterMarketEvidenceBook? previous,
        IEnumerable<OutfitterMarketItemEvidence> visible,
        OutfitterMarketDiscoveryProgress progress)
    {
        var state = new OutfitterMarketDiscoveryLiveState(
            progress,
            previous,
            visible.OrderBy(value => value.ItemId).ToArray());
        liveStates[signature] = state;
        StateChanged?.Invoke(state);
    }

    private void SeedCache(OutfitterMarketEvidenceBook book)
    {
        foreach (var item in book.Items)
        {
            cache.Store(new(
                book.SourceKey,
                book.Region,
                item.ItemId,
                book.Coverage.ListingLimit), item);
        }
    }

    private static OutfitterMarketItemEvidence Failed(
        uint itemId,
        DateTimeOffset capturedAt,
        string diagnostic,
        DateTimeOffset? retryAfter = null) => new(
            itemId,
            OutfitterMarketEvidenceItemStatus.Failed,
            [],
            capturedAt,
            string.Empty,
            diagnostic,
            retryAfter);

    private static string Revision(IReadOnlyList<MarketAcquisitionListing> listings)
    {
        var text = string.Join('\n', listings.Select(listing =>
            $"{listing.ItemId}|{listing.ListingId}|{listing.IsHq}|{listing.WorldId}|{listing.Quantity}|{listing.UnitPrice}|{listing.LastReviewTimeUtc:O}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static IReadOnlyList<MarketAcquisitionListing> NormalizeListings(
        IReadOnlyList<MarketAcquisitionListing> listings)
    {
        var normalized = new List<MarketAcquisitionListing>(listings.Count);
        foreach (var group in listings.GroupBy(listing => (listing.WorldId, listing.ListingId)))
        {
            var first = group.First();
            if (group.Skip(1).Any(candidate => !SameObservableRow(first, candidate)))
                throw new InvalidOperationException($"Listing '{group.Key.ListingId}' on world {group.Key.WorldId} appeared with conflicting observable market rows.");
            normalized.Add(first);
        }

        return normalized;
    }

    private static bool SameObservableRow(MarketAcquisitionListing left, MarketAcquisitionListing right) =>
        left.ItemId == right.ItemId &&
        left.IsHq == right.IsHq &&
        left.WorldId == right.WorldId &&
        string.Equals(left.WorldName, right.WorldName, StringComparison.Ordinal) &&
        string.Equals(left.RetainerId, right.RetainerId, StringComparison.Ordinal) &&
        string.Equals(left.RetainerName, right.RetainerName, StringComparison.Ordinal) &&
        left.Quantity == right.Quantity &&
        left.UnitPrice == right.UnitPrice &&
        left.LastReviewTimeUtc == right.LastReviewTimeUtc;

    private static OutfitterMarketEvidenceBook NormalizeBook(OutfitterMarketEvidenceBook book)
    {
        var changed = false;
        var items = book.Items.Select(item =>
        {
            var listings = NormalizeEvidenceListings(item.Listings);
            if (ReferenceEquals(listings, item.Listings))
                return item;
            changed = true;
            return item with { Listings = listings };
        }).ToArray();
        return changed ? book with { Items = items } : book;
    }

    private static IReadOnlyList<OutfitterMarketListingEvidence> NormalizeEvidenceListings(
        IReadOnlyList<OutfitterMarketListingEvidence> listings)
    {
        var groups = listings.GroupBy(listing => (listing.WorldId, listing.ListingId)).ToArray();
        if (groups.All(group => group.Count() == 1))
            return listings;
        return groups.SelectMany(group => group.Skip(1).All(candidate => candidate == group.First())
            ? group.Take(1)
            : group)
        .ToArray();
    }

    private static bool EquivalentEvidence(OutfitterMarketEvidenceBook? left, OutfitterMarketEvidenceBook right)
    {
        if (left is null ||
            left.Coverage.Mode != right.Coverage.Mode ||
            left.Coverage.CatalogItemCount != right.Coverage.CatalogItemCount ||
            left.Coverage.QueriedItemCount != right.Coverage.QueriedItemCount ||
            left.Coverage.ListingLimit != right.Coverage.ListingLimit ||
            !left.Coverage.QueriedItemIds.SequenceEqual(right.Coverage.QueriedItemIds) ||
            left.Items.Count != right.Items.Count)
            return false;
        return left.Items.OrderBy(value => value.ItemId)
            .Zip(right.Items.OrderBy(value => value.ItemId))
            .All(pair => pair.First.ItemId == pair.Second.ItemId &&
                string.Equals(pair.First.SourceRevision, pair.Second.SourceRevision, StringComparison.Ordinal));
    }

    private static OutfitterMarketEvidenceCacheKey Key(OutfitterMarketEvidenceRequest request, uint itemId) => new(
        request.SourceKey.Trim(),
        request.Region.Trim(),
        itemId,
        Math.Clamp(request.ListingLimit, 1, 100));

    private static string Signature(OutfitterMarketEvidenceRequest request) => string.Join('|',
        request.SourceKey.Trim(),
        request.Region.Trim(),
        Math.Clamp(request.ListingLimit, 1, 100),
        request.CoverageMode,
        request.SampleSize,
        request.MaxConcurrency,
        string.Join(',', request.ItemIds.Where(value => value != 0).Distinct().Order()));

    private static void Validate(OutfitterMarketEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);
        ArgumentNullException.ThrowIfNull(request.ItemIds);
        if (request.MaxConcurrency is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(request.MaxConcurrency));
        if (request.CoverageMode == OutfitterMarketCoverageMode.Sampled && request.SampleSize is null or <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.SampleSize), "Sampled discovery requires an explicit positive sample size.");
    }
}
