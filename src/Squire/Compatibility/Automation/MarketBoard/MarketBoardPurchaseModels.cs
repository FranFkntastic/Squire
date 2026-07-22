using System.Collections.Generic;

namespace MarketMafioso.Automation.MarketBoard;

public sealed record MarketBoardPurchaseCandidate
{
    public uint ItemId { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public string RetainerName { get; init; } = string.Empty;
    public uint UnitPrice { get; init; }
    public uint Quantity { get; init; }
    public bool IsHq { get; init; }
    public uint TotalGil => checked(UnitPrice * Quantity);

    public static MarketBoardPurchaseCandidate FromLiveListing(MarketBoardLiveListing listing) =>
        new()
        {
            ItemId = listing.ItemId,
            WorldName = listing.WorldName,
            ListingId = listing.ListingId,
            RetainerId = listing.RetainerId,
            RetainerName = listing.RetainerName,
            UnitPrice = listing.UnitPrice,
            Quantity = listing.Quantity,
            IsHq = listing.IsHq,
        };

    public bool Matches(MarketBoardLiveListing listing) =>
        ItemId == listing.ItemId &&
        WorldName.Equals(listing.WorldName, System.StringComparison.OrdinalIgnoreCase) &&
        ListingId.Equals(listing.ListingId, System.StringComparison.Ordinal) &&
        RetainerId.Equals(listing.RetainerId, System.StringComparison.Ordinal) &&
        UnitPrice == listing.UnitPrice &&
        Quantity == listing.Quantity &&
        IsHq == listing.IsHq;
}

public sealed record MarketBoardPurchaseRevalidation
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public MarketBoardPurchaseCandidate? Candidate { get; init; }
    public MarketBoardLiveListing? FreshListing { get; init; }
    public bool CanAttemptPurchase => Status == "Ready" && Candidate != null && FreshListing != null;

    public static MarketBoardPurchaseRevalidation Ready(
        MarketBoardPurchaseCandidate candidate,
        MarketBoardLiveListing freshListing) =>
        new()
        {
            Status = "Ready",
            Message = "Fresh live listing still matches the guarded purchase candidate.",
            Candidate = candidate,
            FreshListing = freshListing,
        };

    public static MarketBoardPurchaseRevalidation Fail(string status, string message) =>
        new()
        {
            Status = status,
            Message = message,
        };
}

public sealed record MarketBoardPurchaseResult
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public MarketBoardPurchaseCandidate? Candidate { get; init; }
    public string? ConfirmationPromptText { get; init; }
    public string? ConfirmationAddonName { get; init; }
    public IReadOnlyDictionary<string, string> Diagnostics { get; init; } = new Dictionary<string, string>();
}
