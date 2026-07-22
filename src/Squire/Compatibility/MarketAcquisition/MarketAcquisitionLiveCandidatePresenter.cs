using System;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionLiveCandidatePresenter
{
    public static MarketAcquisitionLiveCandidateSummary BuildSummary(MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);

        return new MarketAcquisitionLiveCandidateSummary
        {
            Status = candidatePlan.Status,
            Message = candidatePlan.Message,
            RequestedQuantity = candidatePlan.RequestedQuantity,
            WouldBuyQuantity = candidatePlan.WouldBuyQuantity,
            WouldSpendGil = candidatePlan.WouldSpendGil,
            WouldBuyRows = candidatePlan.Rows.Count(row => row.Decision == "WouldBuy"),
            SkippedRows = candidatePlan.Rows.Count(row => row.Decision != "WouldBuy"),
            TotalRows = candidatePlan.Rows.Count,
        };
    }
}

public sealed record MarketAcquisitionLiveCandidateSummary
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public uint RequestedQuantity { get; init; }
    public uint WouldBuyQuantity { get; init; }
    public uint WouldSpendGil { get; init; }
    public int WouldBuyRows { get; init; }
    public int SkippedRows { get; init; }
    public int TotalRows { get; init; }
}
