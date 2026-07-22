using System.Collections.Generic;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRouteEngineSnapshot
{
    public MarketAcquisitionExecutionMode ExecutionMode { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
    public string VisibleAcquisitionStatus { get; init; } = string.Empty;
    public bool IsRouteActive { get; init; }
    public bool IsRunning { get; init; }
    public bool IsPaused { get; init; }
    public bool CanRestart { get; init; }
    public bool CanFinalizeInputCaptureLog { get; init; }
    public int CompletedOrProbedStopCount { get; init; }
    public string RouteState { get; init; } = string.Empty;
    public MarketAcquisitionGuidedRouteStop? ActiveStop { get; init; }
    public IReadOnlyList<MarketAcquisitionGuidedRouteStop> Stops { get; init; } = [];
    public MarketAcquisitionPlan? ActivePlan { get; init; }
    public bool IsProbeRunning { get; init; }
    public MarketBoardReadResult? MarketBoardReadResult { get; init; }
    public MarketBoardListingReconciliation? MarketBoardReconciliation { get; init; }
    public MarketAcquisitionLiveCandidatePlan? LiveCandidatePlan { get; init; }
    public MarketAcquisitionRouteOperationSnapshot? ActiveOperation { get; init; }
    public MarketAcquisitionRouteOperationSnapshot? LastOperation { get; init; }
    public MarketBoardPurchaseSession? PurchaseSession { get; init; }
    public MarketBoardPurchaseResult? LastPurchaseResult { get; init; }
    public MarketPurchaseEvidenceState? PurchaseEvidenceState { get; init; }
    public uint ActiveWorldPurchasedQuantity { get; init; }
    public uint ActiveWorldSpentGil { get; init; }
    public uint ActiveLinePurchasedQuantity { get; init; }
    public uint ActiveLineSpentGil { get; init; }
    public string? LastDiagnosticFilePath { get; init; }
    public string? LastObservedListingsCsvPath { get; init; }
    public string? LastPurchaseRecordsCsvPath { get; init; }
    public MarketAcquisitionRouteRunSummary? LastRunSummary { get; init; }
    public MarketAcquisitionWorldCompletionSummary? LatestWorldCompletionSummary { get; init; }
    public MarketAcquisitionRunDiagnosticSummary LastRunDiagnosticSummary { get; init; } = new();
    public OutfitterRouteExecutionState? OutfitterExecution { get; init; }
}
