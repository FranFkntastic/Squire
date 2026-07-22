using System;
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.MarketAcquisition;

public enum OutfitterDryRunScenario
{
    Ordinary,
    ChangedListingRecovery,
    NoViableRecovery,
}

internal sealed class MarketAcquisitionRouteEngineState
{
    public MarketAcquisitionExecutionMode ExecutionMode { get; set; } = MarketAcquisitionExecutionMode.Live;
    public MarketBoardReadResult? MarketBoardReadResult { get; set; }
    public MarketBoardListingReconciliation? MarketBoardReconciliation { get; set; }
    public MarketAcquisitionLiveCandidatePlan? LiveCandidatePlan { get; set; }
    public uint ActiveWorldPurchasedQuantity { get; set; }
    public uint ActiveWorldSpentGil { get; set; }
    public string? ActiveWorldPurchaseBatchWorld { get; set; }
    public string? ActivePurchaseLineId { get; set; }
    public uint ActiveLinePurchasedQuantity { get; set; }
    public uint ActiveLineSpentGil { get; set; }
    public bool ProbeRunning { get; set; }
    public bool EvidenceRefreshOnly { get; set; }
    public DateTimeOffset NextRouteMonitorUtc { get; set; } = DateTimeOffset.MinValue;
    public long ProgressReportSequence { get; set; }
    public string ProgressNonce { get; set; } = Guid.NewGuid().ToString("N");
    public string AcquisitionStatus { get; set; } = "No route has started.";

    public void ResetRouteExecutionState(bool preserveExecutionMode = false)
    {
        if (!preserveExecutionMode)
            ExecutionMode = MarketAcquisitionExecutionMode.Live;
        MarketBoardReadResult = null;
        MarketBoardReconciliation = null;
        LiveCandidatePlan = null;
        ActiveWorldPurchasedQuantity = 0;
        ActiveWorldSpentGil = 0;
        ActiveWorldPurchaseBatchWorld = null;
        ActivePurchaseLineId = null;
        ActiveLinePurchasedQuantity = 0;
        ActiveLineSpentGil = 0;
        ProbeRunning = false;
        EvidenceRefreshOnly = false;
        NextRouteMonitorUtc = DateTimeOffset.MinValue;
        ProgressReportSequence = 0;
        ProgressNonce = Guid.NewGuid().ToString("N");
    }
}
