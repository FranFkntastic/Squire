using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed record ObservedMarketSnapshotKey(
    uint ItemId,
    string QueryTarget,
    string Source);

public enum ObservedMarketSnapshotLookupStatus
{
    Hit,
    Miss,
    Expired,
}

public sealed record ObservedMarketSnapshot
{
    public ObservedMarketSnapshotKey Key { get; init; } = new(0, string.Empty, string.Empty);
    public IReadOnlyList<MarketAcquisitionListing> Listings { get; init; } = [];
    public DateTimeOffset FetchedAtUtc { get; init; }
    public string SourceFreshness { get; init; } = string.Empty;
    public string DiagnosticStatus { get; init; } = string.Empty;
    public string DiagnosticSummary { get; init; } = string.Empty;
}

public sealed record ObservedMarketSnapshotLookup
{
    public bool Found => Snapshot is not null && Status == ObservedMarketSnapshotLookupStatus.Hit;
    public ObservedMarketSnapshotLookupStatus Status { get; init; }
    public ObservedMarketSnapshot? Snapshot { get; init; }
}

public sealed class ObservedMarketSnapshotCache
{
    private readonly int maxEntries;
    private readonly TimeSpan ttl;
    private readonly object gate = new();
    private readonly Dictionary<ObservedMarketSnapshotKey, CacheEntry> entries = new();
    private readonly LinkedList<ObservedMarketSnapshotKey> leastRecentlyUsed = new();

    public ObservedMarketSnapshotCache(int maxEntries, TimeSpan ttl)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Snapshot cache must allow at least one entry.");
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "Snapshot cache TTL must be positive.");

        this.maxEntries = maxEntries;
        this.ttl = ttl;
    }

    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    public ObservedMarketSnapshotLookup TryGet(ObservedMarketSnapshotKey key, DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var entry))
            {
                return new ObservedMarketSnapshotLookup
                {
                    Status = ObservedMarketSnapshotLookupStatus.Miss,
                };
            }

            if (nowUtc - entry.Snapshot.FetchedAtUtc > ttl)
            {
                Remove(key, entry);
                return new ObservedMarketSnapshotLookup
                {
                    Status = ObservedMarketSnapshotLookupStatus.Expired,
                };
            }

            Touch(entry);
            return new ObservedMarketSnapshotLookup
            {
                Status = ObservedMarketSnapshotLookupStatus.Hit,
                Snapshot = entry.Snapshot,
            };
        }
    }

    public void Replace(
        ObservedMarketSnapshotKey key,
        IEnumerable<MarketAcquisitionListing> listings,
        DateTimeOffset fetchedAtUtc,
        string sourceFreshness,
        string diagnosticStatus,
        string diagnosticSummary)
    {
        ArgumentNullException.ThrowIfNull(listings);
        var listingRows = listings.ToArray();

        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing))
                Remove(key, existing);

            var snapshot = new ObservedMarketSnapshot
            {
                Key = key,
                Listings = listingRows,
                FetchedAtUtc = fetchedAtUtc,
                SourceFreshness = sourceFreshness,
                DiagnosticStatus = diagnosticStatus,
                DiagnosticSummary = diagnosticSummary,
            };
            var node = leastRecentlyUsed.AddLast(key);
            entries[key] = new CacheEntry(snapshot, node);

            while (entries.Count > maxEntries)
            {
                var lruNode = leastRecentlyUsed.First;
                if (lruNode is null)
                    break;

                entries.Remove(lruNode.Value);
                leastRecentlyUsed.RemoveFirst();
            }
        }
    }

    private void Touch(CacheEntry entry)
    {
        leastRecentlyUsed.Remove(entry.Node);
        leastRecentlyUsed.AddLast(entry.Node);
    }

    private void Remove(ObservedMarketSnapshotKey key, CacheEntry entry)
    {
        entries.Remove(key);
        leastRecentlyUsed.Remove(entry.Node);
    }

    private sealed record CacheEntry(
        ObservedMarketSnapshot Snapshot,
        LinkedListNode<ObservedMarketSnapshotKey> Node);
}
