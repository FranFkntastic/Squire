using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace MarketMafioso.Automation.MarketBoard;

public static class MarketBoardListingListProbe
{
    private static readonly uint[] CandidateListingListIds = Enumerable.Range(1, 80).Select(static id => (uint)id).ToArray();

    public static unsafe MarketBoardListingListProbeResult Probe(AddonItemSearchResult* addon, int requestedRow)
    {
        if (addon == null)
        {
            return new MarketBoardListingListProbeResult(
                IsReady: false,
                ComponentId: null,
                VisibleItemCount: 0,
                RequestedRow: requestedRow,
                Diagnostic: "ItemSearchResult addon was unavailable.");
        }

        if (!addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
        {
            return new MarketBoardListingListProbeResult(
                IsReady: false,
                ComponentId: null,
                VisibleItemCount: 0,
                RequestedRow: requestedRow,
                Diagnostic: $"ItemSearchResult addon was not ready. ready={addon->AtkUnitBase.IsReady}, visible={addon->AtkUnitBase.IsVisible}.");
        }

        uint? bestListId = null;
        var bestItemCount = 0;
        var bestIsInteractive = false;
        var listCounts = new List<string>();

        foreach (var listId in CandidateListingListIds)
        {
            var list = addon->AtkUnitBase.GetComponentListById(listId);
            if (list == null)
                continue;

            var itemCount = list->GetItemCount();
            var isInteractive = list->IsItemInteractionEnabled || list->IsItemClickEnabled;
            listCounts.Add($"{listId}:{itemCount}:interactive={isInteractive}");
            if (!IsBetterListingListCandidate(itemCount, isInteractive, bestItemCount, bestIsInteractive))
                continue;

            bestListId = listId;
            bestItemCount = itemCount;
            bestIsInteractive = isInteractive;
        }

        if (bestListId == null)
        {
            return new MarketBoardListingListProbeResult(
                IsReady: false,
                ComponentId: null,
                VisibleItemCount: 0,
                RequestedRow: requestedRow,
                Diagnostic: listCounts.Count == 0
                    ? "No candidate list components were present."
                    : $"Candidate list item counts: {string.Join(", ", listCounts)}.");
        }

        if (bestItemCount <= 0)
        {
            return new MarketBoardListingListProbeResult(
                IsReady: false,
                ComponentId: bestListId,
                VisibleItemCount: bestItemCount,
                RequestedRow: requestedRow,
                Diagnostic: $"Selected list {bestListId} but it had no visible items. Candidate list item counts: {string.Join(", ", listCounts)}.");
        }

        if (!bestIsInteractive)
        {
            return new MarketBoardListingListProbeResult(
                IsReady: false,
                ComponentId: bestListId,
                VisibleItemCount: bestItemCount,
                RequestedRow: requestedRow,
                Diagnostic: $"Selected list {bestListId} with {bestItemCount} visible item(s), but it was not interactive. Candidate list item counts: {string.Join(", ", listCounts)}.");
        }

        return new MarketBoardListingListProbeResult(
            IsReady: true,
            ComponentId: bestListId,
            VisibleItemCount: bestItemCount,
            RequestedRow: requestedRow,
            Diagnostic: $"Selected list {bestListId} with {bestItemCount} visible item(s) for absolute row {requestedRow}. Candidate list item counts: {string.Join(", ", listCounts)}.");
    }

    internal static bool IsBetterListingListCandidate(
        int itemCount,
        bool isInteractive,
        int bestItemCount,
        bool bestIsInteractive)
    {
        if (isInteractive != bestIsInteractive)
            return isInteractive;

        return itemCount > bestItemCount;
    }

    public static unsafe string DescribeListingLists(AddonItemSearchResult* addon)
    {
        if (addon == null)
            return "addon-unavailable";

        var listCounts = new List<string>();
        foreach (var listId in CandidateListingListIds)
        {
            var list = addon->AtkUnitBase.GetComponentListById(listId);
            if (list == null)
                continue;

            listCounts.Add($"{listId}:{list->GetItemCount()}:interactive={list->IsItemInteractionEnabled || list->IsItemClickEnabled}");
        }

        return listCounts.Count == 0
            ? "none"
            : string.Join(", ", listCounts);
    }
}

public sealed record MarketBoardListingListProbeResult(
    bool IsReady,
    uint? ComponentId,
    int VisibleItemCount,
    int RequestedRow,
    string Diagnostic);
