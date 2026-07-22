using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public enum StockAvailabilityStatus
{
    Enough,
    Partial,
    None,
    Invalid,
    Depth,
}

public sealed record StockAvailabilityRequest(
    string LineId,
    uint ItemId,
    string QuantityMode,
    string HqPolicy,
    uint MaxUnitPrice,
    uint? DesiredQuantity,
    uint? PurchaseCap,
    IReadOnlySet<string>? RouteWorlds = null);

public sealed record StockAvailabilityResult
{
    public string LineId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public StockAvailabilityStatus Status { get; init; }
    public bool IsOpenEndedDepth { get; init; }
    public uint EligibleQuantity { get; init; }
    public int EligibleListingCount { get; init; }
    public uint? RequiredQuantity { get; init; }
    public uint? ShortfallQuantity { get; init; }
    public string Diagnostic { get; init; } = string.Empty;
    public IReadOnlyList<MarketAcquisitionListing> EligibleListings { get; init; } = [];
}

public static class StockAvailabilityService
{
    public static StockAvailabilityResult Analyze(
        StockAvailabilityRequest request,
        IEnumerable<MarketAcquisitionListing> listings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(listings);

        var invalidDiagnostic = Validate(request);
        if (invalidDiagnostic is not null)
            return Invalid(request, invalidDiagnostic);

        var eligibleListings = listings
            .Where(listing => ListingMatches(request, listing))
            .OrderBy(listing => listing.UnitPrice)
            .ThenByDescending(listing => listing.Quantity)
            .ThenBy(listing => listing.LastReviewTimeUtc)
            .ThenBy(listing => listing.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(listing => listing.RetainerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var eligibleQuantity = SumQuantity(eligibleListings);

        if (IsUncappedAllBelowThreshold(request))
        {
            return new StockAvailabilityResult
            {
                LineId = request.LineId,
                ItemId = request.ItemId,
                Status = StockAvailabilityStatus.Depth,
                IsOpenEndedDepth = true,
                EligibleQuantity = eligibleQuantity,
                EligibleListingCount = eligibleListings.Length,
                Diagnostic = eligibleListings.Length == 0
                    ? "Observed no under-threshold listings in the selected route scope."
                    : $"Observed {eligibleQuantity:N0} under-threshold units across {eligibleListings.Length:N0} listings in the selected route scope.",
                EligibleListings = eligibleListings,
            };
        }

        var requiredQuantity = ResolveRequiredQuantity(request);
        if (requiredQuantity == 0)
            return Invalid(request, "Capped stock availability requires a positive target quantity or purchase cap.");

        var status = eligibleQuantity switch
        {
            0 => StockAvailabilityStatus.None,
            var quantity when quantity >= requiredQuantity => StockAvailabilityStatus.Enough,
            _ => StockAvailabilityStatus.Partial,
        };

        return new StockAvailabilityResult
        {
            LineId = request.LineId,
            ItemId = request.ItemId,
            Status = status,
            EligibleQuantity = eligibleQuantity,
            EligibleListingCount = eligibleListings.Length,
            RequiredQuantity = requiredQuantity,
            ShortfallQuantity = status == StockAvailabilityStatus.Enough
                ? 0
                : requiredQuantity - eligibleQuantity,
            Diagnostic = status switch
            {
                StockAvailabilityStatus.Enough => $"Eligible stock covers the requested {requiredQuantity:N0} units.",
                StockAvailabilityStatus.Partial => $"Eligible stock covers {eligibleQuantity:N0} of {requiredQuantity:N0} requested units.",
                _ => $"No eligible stock observed for the requested {requiredQuantity:N0} units.",
            },
            EligibleListings = eligibleListings,
        };
    }

    private static string? Validate(StockAvailabilityRequest request)
    {
        if (request.ItemId == 0)
            return "Stock availability requires a resolved item id.";
        if (request.MaxUnitPrice == 0)
            return "Stock availability requires a positive max unit price.";

        try
        {
            _ = MarketAcquisitionPolicy.NormalizeHqPolicy(request.HqPolicy);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        return request.QuantityMode switch
        {
            "TargetQuantity" when request.DesiredQuantity is null or 0 =>
                "Target quantity stock availability requires a positive target quantity.",
            "AllBelowThreshold" when request.PurchaseCap == 0 =>
                "All-below-threshold purchase cap must be positive when supplied.",
            "TargetQuantity" or "AllBelowThreshold" => null,
            _ => $"Unknown quantity mode {request.QuantityMode}.",
        };
    }

    private static StockAvailabilityResult Invalid(StockAvailabilityRequest request, string diagnostic) =>
        new()
        {
            LineId = request.LineId,
            ItemId = request.ItemId,
            Status = StockAvailabilityStatus.Invalid,
            Diagnostic = diagnostic,
        };

    private static bool ListingMatches(StockAvailabilityRequest request, MarketAcquisitionListing listing)
    {
        if (listing.ItemId != request.ItemId)
            return false;
        if (listing.Quantity == 0 || listing.UnitPrice == 0)
            return false;
        if (listing.UnitPrice > request.MaxUnitPrice)
            return false;
        if (!MarketAcquisitionPolicy.HqMatches(request.HqPolicy, listing.IsHq))
            return false;
        if (request.RouteWorlds is { Count: > 0 } &&
            !request.RouteWorlds.Any(world => world.Equals(listing.WorldName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static uint ResolveRequiredQuantity(StockAvailabilityRequest request) =>
        request.QuantityMode == "AllBelowThreshold"
            ? request.PurchaseCap.GetValueOrDefault()
            : request.DesiredQuantity.GetValueOrDefault();

    private static bool IsUncappedAllBelowThreshold(StockAvailabilityRequest request) =>
        request.QuantityMode == "AllBelowThreshold" && request.PurchaseCap is null;

    private static uint SumQuantity(IEnumerable<MarketAcquisitionListing> listings)
    {
        ulong total = 0;
        foreach (var listing in listings)
            total = checked(total + listing.Quantity);
        return total > uint.MaxValue ? uint.MaxValue : (uint)total;
    }
}
