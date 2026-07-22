using System.Collections.Generic;
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketBoardListingReconciliation
{
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<MarketBoardListingReconciliationRow> Listings { get; init; } = [];
}

public sealed record MarketBoardListingReconciliationRow
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool IsExactMatch { get; init; }
    public MarketAcquisitionPlannedListing PlannedListing { get; init; } = new();
    public MarketBoardLiveListing? LiveListing { get; init; }
}
