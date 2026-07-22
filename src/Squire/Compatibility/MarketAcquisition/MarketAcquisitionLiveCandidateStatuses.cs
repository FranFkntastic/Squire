using System;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionLiveCandidateStatuses
{
    public const string Ready = "Ready";
    public const string UnderProcured = "UnderProcured";
    public const string NoSafeListings = "NoSafeListings";
    public const string IncompleteListingCoverage = "IncompleteListingCoverage";
    public const string LegalStockObserved = "LegalStockObserved";
    public const string Purchased = "Purchased";

    private const string LegacyVisibleCacheExhausted = "VisibleCacheExhausted";

    public static bool IsIncompleteListingCoverage(string? status) =>
        Equals(status, IncompleteListingCoverage) ||
        Equals(status, LegacyVisibleCacheExhausted);

    public static bool IsConclusiveWorldVisitResult(string? result) =>
        Equals(result, NoSafeListings) ||
        Equals(result, LegalStockObserved) ||
        Equals(result, Purchased);

    private static bool Equals(string? left, string right) =>
        left?.Equals(right, StringComparison.OrdinalIgnoreCase) == true;
}
