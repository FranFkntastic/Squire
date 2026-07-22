using System;
using System.Collections.Generic;

namespace MarketMafioso.Squire.Outfitter.MarketEvidence;

public enum OutfitterMarketEvidenceCacheStatus
{
    Fresh,
    StaleUsable,
    Failed,
    Missing,
}

public sealed record OutfitterMarketEvidenceCacheLookup(
    OutfitterMarketEvidenceCacheStatus Status,
    OutfitterMarketItemEvidence? Evidence = null);

public sealed class OutfitterMarketEvidenceCache
{
    private readonly TimeSpan freshFor;
    private readonly TimeSpan staleFor;
    private readonly int maxEntries;
    private readonly object gate = new();
    private readonly Dictionary<OutfitterMarketEvidenceCacheKey, CacheEntry> entries = [];
    private readonly LinkedList<OutfitterMarketEvidenceCacheKey> leastRecentlyUsed = [];

    public OutfitterMarketEvidenceCache(TimeSpan freshFor, TimeSpan staleFor, int maxEntries = 4096)
    {
        if (freshFor <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(freshFor));
        if (staleFor <= freshFor)
            throw new ArgumentOutOfRangeException(nameof(staleFor), "Stale lifetime must exceed fresh lifetime.");
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        this.freshFor = freshFor;
        this.staleFor = staleFor;
        this.maxEntries = maxEntries;
    }

    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    public OutfitterMarketEvidenceCacheLookup Get(OutfitterMarketEvidenceCacheKey key, DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var entry))
                return new(OutfitterMarketEvidenceCacheStatus.Missing);
            if (entry.Evidence.Status == OutfitterMarketEvidenceItemStatus.Failed)
            {
                if (entry.Evidence.RetryAfterUtc is { } retry && retry > nowUtc)
                {
                    Touch(entry);
                    return new(OutfitterMarketEvidenceCacheStatus.Failed, entry.Evidence);
                }
                Remove(key, entry);
                return new(OutfitterMarketEvidenceCacheStatus.Missing);
            }

            var age = nowUtc - entry.Evidence.CapturedAtUtc;
            if (age <= freshFor)
            {
                Touch(entry);
                return new(OutfitterMarketEvidenceCacheStatus.Fresh, entry.Evidence with
                {
                    Status = entry.Evidence.Listings.Count == 0
                        ? OutfitterMarketEvidenceItemStatus.Missing
                        : OutfitterMarketEvidenceItemStatus.Fresh,
                });
            }
            if (age <= staleFor)
            {
                Touch(entry);
                return new(OutfitterMarketEvidenceCacheStatus.StaleUsable, entry.Evidence with
                {
                    Status = OutfitterMarketEvidenceItemStatus.StaleUsable,
                });
            }

            Remove(key, entry);
            return new(OutfitterMarketEvidenceCacheStatus.Missing);
        }
    }

    public void Store(OutfitterMarketEvidenceCacheKey key, OutfitterMarketItemEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (key.ItemId != evidence.ItemId)
            throw new InvalidOperationException("Cache key and item evidence disagree.");
        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                if (existing.Evidence.CapturedAtUtc > evidence.CapturedAtUtc)
                    return;
                Remove(key, existing);
            }
            var node = leastRecentlyUsed.AddLast(key);
            entries[key] = new(evidence, node);
            while (entries.Count > maxEntries && leastRecentlyUsed.First is { } oldest)
            {
                entries.Remove(oldest.Value);
                leastRecentlyUsed.RemoveFirst();
            }
        }
    }

    private void Touch(CacheEntry entry)
    {
        leastRecentlyUsed.Remove(entry.Node);
        leastRecentlyUsed.AddLast(entry.Node);
    }

    private void Remove(OutfitterMarketEvidenceCacheKey key, CacheEntry entry)
    {
        entries.Remove(key);
        leastRecentlyUsed.Remove(entry.Node);
    }

    private sealed record CacheEntry(
        OutfitterMarketItemEvidence Evidence,
        LinkedListNode<OutfitterMarketEvidenceCacheKey> Node);
}
