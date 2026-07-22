using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace MarketMafioso.Automation.MarketBoard;

public sealed class MarketBoardListingReader
{
    private const string ItemSearchResultAddon = "ItemSearchResult";

    private readonly IGameGui gameGui;

    public MarketBoardListingReader(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public unsafe MarketBoardReadResult ReadCurrentListings(string currentWorld)
    {
        if (string.IsNullOrWhiteSpace(currentWorld))
            throw new InvalidOperationException("Current world is required before reading market board listings.");

        var addon = gameGui.GetAddonByName<AddonItemSearchResult>(ItemSearchResultAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
        {
            return new MarketBoardReadResult
            {
                Status = "MarketBoardNotOpen",
                Message = "Open market board search results for the planned item before running the read-only probe.",
                ReadState = MarketBoardListingReadState.Unavailable,
            };
        }

        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null)
        {
            return new MarketBoardReadResult
            {
                Status = "InfoProxyUnavailable",
                Message = "InfoProxyItemSearch is unavailable.",
                ReadState = MarketBoardListingReadState.Unavailable,
            };
        }

        var itemId = infoProxy->SearchItemId;
        if (itemId == 0)
        {
            return new MarketBoardReadResult
            {
                Status = "NoSearchItem",
                Message = "Market board search results are open, but no searched item id is available.",
                ReadState = MarketBoardListingReadState.Unavailable,
            };
        }

        var listings = new List<MarketBoardLiveListing>();
        var reportedListingCount = (int)infoProxy->ListingCount;
        var listingCapacity = infoProxy->Listings.Length;
        var listingCount = Math.Min(reportedListingCount, listingCapacity);
        foreach (var listing in infoProxy->Listings[..listingCount])
        {
            if (listing.ListingId == 0 ||
                listing.RetainerId == 0 ||
                listing.UnitPrice == 0 ||
                listing.Quantity == 0)
            {
                continue;
            }

            listings.Add(new MarketBoardLiveListing
            {
                ItemId = listing.ItemId,
                RawItemId = listing.ItemId,
                WorldName = currentWorld,
                ListingId = listing.ListingId.ToString(),
                RetainerId = listing.RetainerId.ToString(),
                RetainerName = string.Empty,
                UnitPrice = listing.UnitPrice,
                Quantity = listing.Quantity,
                IsHq = listing.IsHqItem,
            });
        }

        return BuildReadResult(
            infoProxy->WaitingForListings,
            itemId,
            currentWorld,
            listings,
            reportedListingCount,
            listingCapacity,
            infoProxy->InfoProxyPageInterface.CurrentRequestId,
            infoProxy->InfoProxyPageInterface.NextRequestId);
    }

    internal static MarketBoardReadResult BuildReadResult(
        bool waitingForListings,
        uint itemId,
        string currentWorld,
        IReadOnlyList<MarketBoardLiveListing> listings,
        int? reportedListingCount = null,
        int? listingCapacity = null,
        byte currentRequestId = 0,
        byte nextRequestId = 0)
    {
        var realListings = listings
            .Where(MarketBoardListingIntegrity.IsRealListing)
            .ToArray();
        var rawItemIdMismatchCounts = BuildRawItemIdMismatchCounts(itemId, realListings);
        var effectiveReportedListingCount = Math.Max(reportedListingCount ?? realListings.Length, realListings.Length);
        var effectiveListingCapacity = Math.Max(listingCapacity ?? realListings.Length, realListings.Length);
        var isAtListingCapacity = effectiveListingCapacity > 0 && realListings.Length >= effectiveListingCapacity;
        var isListingCountTruncated = effectiveReportedListingCount > realListings.Length;
        var readState = isListingCountTruncated
            ? MarketBoardListingReadState.FreshPartial
            : MarketBoardListingReadState.FreshComplete;
        var capacityNote = effectiveListingCapacity > 0
            ? $" Listing cache capacity {realListings.Length}/{effectiveListingCapacity}."
            : string.Empty;
        var truncatedNote = isListingCountTruncated
            ? $" Reported listing count {effectiveReportedListingCount} was truncated to the readable cache."
            : string.Empty;
        if (rawItemIdMismatchCounts.Count > 0)
        {
            return new MarketBoardReadResult
            {
                Status = "ListingCacheSwitching",
                Message = $"Market board listing cache is still switching to item {itemId}; raw row item ids included {FormatRawItemIdMismatchCounts(rawItemIdMismatchCounts)}.",
                ReadState = MarketBoardListingReadState.SwitchingItem,
                ItemId = itemId,
                WorldName = currentWorld,
                ReportedListingCount = effectiveReportedListingCount,
                ListingCapacity = effectiveListingCapacity,
                IsAtListingCapacity = isAtListingCapacity,
                IsListingCountTruncated = isListingCountTruncated,
                CurrentRequestId = currentRequestId,
                NextRequestId = nextRequestId,
                RawItemIdMismatchCounts = rawItemIdMismatchCounts,
                Listings = [],
            };
        }

        if (realListings.Length > 0)
        {
            var waitingNote = waitingForListings
                ? " Waiting flag is still set, but visible listing rows were present."
                : string.Empty;
            return new MarketBoardReadResult
            {
                Status = "Ready",
                Message = $"Read {realListings.Length} live market board listing(s).{capacityNote}{truncatedNote}{waitingNote}",
                ReadState = readState,
                ItemId = itemId,
                WorldName = currentWorld,
                ReportedListingCount = effectiveReportedListingCount,
                ListingCapacity = effectiveListingCapacity,
                IsAtListingCapacity = isAtListingCapacity,
                IsListingCountTruncated = isListingCountTruncated,
                CurrentRequestId = currentRequestId,
                NextRequestId = nextRequestId,
                RawItemIdMismatchCounts = rawItemIdMismatchCounts,
                Listings = realListings,
            };
        }

        return new MarketBoardReadResult
        {
            Status = waitingForListings ? "WaitingForListings" : "NoListings",
            Message = waitingForListings
                ? "Market board listings are still loading."
                : "No live market board listings were available for the current search.",
            ReadState = waitingForListings
                ? MarketBoardListingReadState.Loading
                : MarketBoardListingReadState.FreshComplete,
            ItemId = itemId,
            WorldName = currentWorld,
            ReportedListingCount = effectiveReportedListingCount,
            ListingCapacity = effectiveListingCapacity,
            IsAtListingCapacity = isAtListingCapacity,
            IsListingCountTruncated = isListingCountTruncated,
            CurrentRequestId = currentRequestId,
            NextRequestId = nextRequestId,
            RawItemIdMismatchCounts = rawItemIdMismatchCounts,
            Listings = [],
        };
    }

    private static IReadOnlyDictionary<uint, int> BuildRawItemIdMismatchCounts(
        uint itemId,
        IReadOnlyList<MarketBoardLiveListing> listings)
    {
        if (itemId == 0 || listings.Count == 0)
            return new Dictionary<uint, int>();

        var counts = new Dictionary<uint, int>();
        foreach (var listing in listings)
        {
            var rawItemId = listing.RawItemId ?? listing.ItemId;
            if (rawItemId == itemId && listing.ItemId == itemId)
                continue;

            var mismatchItemId = rawItemId != itemId
                ? rawItemId
                : listing.ItemId;

            counts[mismatchItemId] = counts.GetValueOrDefault(mismatchItemId) + 1;
        }

        return counts;
    }

    private static string FormatRawItemIdMismatchCounts(IReadOnlyDictionary<uint, int> counts) =>
        string.Join(
            ", ",
            counts
                .OrderBy(count => count.Key)
                .Select(count => $"{count.Key}={count.Value}"));
}
