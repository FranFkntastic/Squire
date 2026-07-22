using System;
using System.Collections.Generic;

namespace MarketMafioso.Automation.MarketBoard;

public enum MarketBoardListingReadState
{
    Unavailable,
    Loading,
    SwitchingItem,
    FreshPartial,
    FreshComplete,
}

public sealed record MarketBoardLiveListing
{
    public uint ItemId { get; init; }
    public uint? RawItemId { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public string RetainerName { get; init; } = string.Empty;
    public uint UnitPrice { get; init; }
    public uint Quantity { get; init; }
    public bool IsHq { get; init; }
}

public sealed record MarketBoardReadResult
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public MarketBoardListingReadState ReadState { get; init; } = MarketBoardListingReadState.FreshComplete;
    public bool IsFresh =>
        ReadState is MarketBoardListingReadState.FreshPartial or MarketBoardListingReadState.FreshComplete;
    public uint ItemId { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public int ReportedListingCount { get; init; }
    public int ListingCapacity { get; init; }
    public bool IsAtListingCapacity { get; init; }
    public bool IsListingCountTruncated { get; init; }
    public int ReadableListingCount => Listings.Count;
    public int UnreadListingCount => Math.Max(0, ReportedListingCount - ReadableListingCount);
    public bool HasIncompleteCoverage => UnreadListingCount > 0;
    public byte CurrentRequestId { get; init; }
    public byte NextRequestId { get; init; }
    public IReadOnlyDictionary<uint, int> RawItemIdMismatchCounts { get; init; } =
        new Dictionary<uint, int>();
    public IReadOnlyList<MarketBoardLiveListing> Listings { get; init; } = [];
}

public sealed record MarketBoardAccumulatedReadResult
{
    public uint ItemId { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public int ReportedListingCount { get; init; }
    public int ListingCapacity { get; init; }
    public int PageCount { get; init; }
    public byte CurrentRequestId { get; init; }
    public byte NextRequestId { get; init; }
    public IReadOnlyList<MarketBoardLiveListing> Listings { get; init; } = [];

    public int ReadableListingCount => Listings.Count;

    public int UnreadListingCount => Math.Max(0, ReportedListingCount - ReadableListingCount);

    public bool HasIncompleteCoverage => UnreadListingCount > 0;

    public bool IsAtListingCapacity =>
        ListingCapacity > 0 &&
        Listings.Count >= ListingCapacity;

    public bool IsListingCountTruncated =>
        ReportedListingCount > Listings.Count;

    public static MarketBoardAccumulatedReadResult FromReadResult(MarketBoardReadResult readResult)
    {
        ArgumentNullException.ThrowIfNull(readResult);

        return new MarketBoardAccumulatedReadResult
        {
            ItemId = readResult.ItemId,
            WorldName = readResult.WorldName,
            ReportedListingCount = Math.Max(readResult.ReportedListingCount, readResult.Listings.Count),
            ListingCapacity = readResult.ListingCapacity,
            PageCount = 1,
            CurrentRequestId = readResult.CurrentRequestId,
            NextRequestId = readResult.NextRequestId,
            Listings = Deduplicate(readResult.Listings),
        };
    }

    public MarketBoardAccumulatedReadResult Append(MarketBoardReadResult readResult)
    {
        ArgumentNullException.ThrowIfNull(readResult);

        if (readResult.ItemId != ItemId)
            throw new InvalidOperationException("Cannot merge market board reads for different item ids.");

        if (!readResult.WorldName.Equals(WorldName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot merge market board reads for different worlds.");

        var merged = new List<MarketBoardLiveListing>(Listings.Count + readResult.Listings.Count);
        var listingIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var listing in Listings)
        {
            if (listingIds.Add(GetMergeKey(listing)))
                merged.Add(listing);
        }

        foreach (var listing in readResult.Listings)
        {
            if (listingIds.Add(GetMergeKey(listing)))
                merged.Add(listing);
        }

        return this with
        {
            ReportedListingCount = Math.Max(Math.Max(ReportedListingCount, readResult.ReportedListingCount), merged.Count),
            ListingCapacity = Math.Max(ListingCapacity, readResult.ListingCapacity),
            PageCount = PageCount + 1,
            CurrentRequestId = readResult.CurrentRequestId,
            NextRequestId = readResult.NextRequestId,
            Listings = merged,
        };
    }

    public MarketBoardReadResult ToReadResult() =>
        new()
        {
            Status = "Ready",
            Message = IsListingCountTruncated
                ? $"Read {Listings.Count:N0}/{ReportedListingCount:N0} accumulated market board listing(s); deeper listings are still unread."
                : $"Read {Listings.Count:N0} accumulated market board listing(s).",
            ReadState = IsListingCountTruncated
                ? MarketBoardListingReadState.FreshPartial
                : MarketBoardListingReadState.FreshComplete,
            ItemId = ItemId,
            WorldName = WorldName,
            ReportedListingCount = Math.Max(ReportedListingCount, Listings.Count),
            ListingCapacity = ListingCapacity,
            IsAtListingCapacity = IsAtListingCapacity,
            IsListingCountTruncated = IsListingCountTruncated,
            CurrentRequestId = CurrentRequestId,
            NextRequestId = NextRequestId,
            Listings = Listings,
        };

    private static IReadOnlyList<MarketBoardLiveListing> Deduplicate(IReadOnlyList<MarketBoardLiveListing> listings)
    {
        var merged = new List<MarketBoardLiveListing>(listings.Count);
        var listingIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var listing in listings)
        {
            if (listingIds.Add(GetMergeKey(listing)))
                merged.Add(listing);
        }

        return merged;
    }

    private static string GetMergeKey(MarketBoardLiveListing listing) =>
        !string.IsNullOrWhiteSpace(listing.ListingId)
            ? listing.ListingId
            : $"{listing.RetainerId}:{listing.UnitPrice}:{listing.Quantity}:{listing.IsHq}";
}
