using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionLiveCandidatePlan
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public MarketBoardListingReadState ListingReadState { get; init; } = MarketBoardListingReadState.FreshComplete;
    public bool IsListingReadFresh =>
        ListingReadState is MarketBoardListingReadState.FreshPartial or MarketBoardListingReadState.FreshComplete;
    public IReadOnlyDictionary<uint, int> RawItemIdMismatchCounts { get; init; } =
        new Dictionary<uint, int>();
    public int ReadableListingCount { get; init; }
    public int ReportedListingCount { get; init; }
    public int ListingCapacity { get; init; }
    public bool IsVisibleListingCacheTruncated { get; init; }
    public uint RequestedQuantity { get; init; }
    public uint WouldBuyQuantity { get; init; }
    public uint WouldSpendGil { get; init; }
    public IReadOnlyList<MarketAcquisitionLiveCandidateRow> Rows { get; init; } = [];
}

public sealed record MarketAcquisitionLiveCandidateRow
{
    public string Decision { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public MarketBoardLiveListing LiveListing { get; init; } = new();
    public uint RunningQuantityAfter { get; init; }
    public uint RunningGilAfter { get; init; }
}
