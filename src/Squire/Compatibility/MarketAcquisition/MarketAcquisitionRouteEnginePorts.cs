using System;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.MarketAcquisition;

public interface IMarketAcquisitionRouteClock
{
    DateTimeOffset UtcNow { get; }
    long MonotonicMilliseconds { get; }
}

public sealed class SystemMarketAcquisitionRouteClock : IMarketAcquisitionRouteClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long MonotonicMilliseconds => Environment.TickCount64;
}

public interface IMarketAcquisitionRouteContext
{
    bool IsCurrentWorldAvailable { get; }
    string GetCurrentWorldName();
    bool TryGetCharacterScope(out string characterName, out string homeWorld);
}

public interface IMarketAcquisitionRouteUiAutomation
{
    bool ProcessCommand(string command);
    bool TryCloseMarketBoardWindows();
    AutomationTravelPreflightResult CheckTravelPreflight();
    bool TryScrollMarketBoardListingsToRow(int requestedRow, out string message);
}

public interface IMarketAcquisitionRouteTravelCleanup
{
    MarketAcquisitionTravelCleanupResult CancelOwnedTravel(MarketAcquisitionTravelLease lease);
}

public enum MarketAcquisitionTravelCleanupStatus
{
    Cancelled,
    AlreadyResolved,
    NothingOwned,
    Unsupported,
    Unavailable,
    Failed,
}

public sealed record MarketAcquisitionTravelLease
{
    public required string LeaseId { get; init; }
    public required string RouteRunId { get; init; }
    public required string OperationId { get; init; }
    public required string Dependency { get; init; }
    public required string TargetWorld { get; init; }
    public required bool IsOwned { get; init; }
}

public sealed record MarketAcquisitionTravelCleanupResult
{
    public required MarketAcquisitionTravelCleanupStatus Status { get; init; }
    public required string Message { get; init; }
    public bool UnresolvedExternalAutomation { get; init; }
    public string? AdapterCapability { get; init; }
    public string? ExceptionType { get; init; }
}

public sealed class UnsupportedMarketAcquisitionRouteTravelCleanup : IMarketAcquisitionRouteTravelCleanup
{
    public MarketAcquisitionTravelCleanupResult CancelOwnedTravel(MarketAcquisitionTravelLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new MarketAcquisitionTravelCleanupResult
        {
            Status = MarketAcquisitionTravelCleanupStatus.Unsupported,
            Message = "Lifestream does not expose a lease-scoped cancellation API; external travel remains unresolved.",
            UnresolvedExternalAutomation = true,
            AdapterCapability = "LeaseScopedCancellationUnavailable",
        };
    }
}

public interface IMarketAcquisitionMarketBoardIo
{
    MarketBoardApproachResult OpenOrApproachMarketBoard();
    MarketAcquisitionApproachCleanupResult StopOwnedApproach(MarketAcquisitionApproachLease lease);
    MarketBoardItemSearchResult SearchItem(uint itemId, string? itemName);
    MarketBoardReadResult ReadCurrentListings(string currentWorld);
    MarketBoardInputCapture CaptureInputState();
}

public sealed record MarketAcquisitionApproachLease
{
    public required string LeaseId { get; init; }
    public required string RouteRunId { get; init; }
    public required string OperationId { get; init; }
    public required string Dependency { get; init; }
}

public sealed record MarketAcquisitionApproachCleanupResult
{
    public required MarketAcquisitionTravelCleanupStatus Status { get; init; }
    public required string Message { get; init; }
    public string? AdapterCapability { get; init; }
}

public interface IMarketAcquisitionPurchaseIo
{
    bool HasServerPurchaseEvidence => false;
    MarketPurchaseEvidenceState? PurchaseEvidenceState => null;

    MarketBoardPurchaseResult ExecuteFirstCandidate(
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        MarketBoardReadResult freshRead);

    MarketBoardPurchaseResult TryConfirmPendingPurchase(MarketBoardPurchaseCandidate candidate);

    MarketBoardPurchaseResult TryConfirmPendingPurchase(
        MarketBoardPurchaseCandidate candidate,
        MarketPurchaseIntentContext context) =>
        TryConfirmPendingPurchase(candidate);

    MarketPurchaseEvidenceAdvanceResult AdvancePurchaseEvidence(DateTimeOffset nowUtc) =>
        new(MarketPurchaseEvidenceAdvanceStatus.NoChange, 0, null, "Server purchase evidence is unavailable.");

    MarketPurchaseTerminalResolutionResult ResolvePurchaseEvidence(
        string intentId,
        MarketPurchaseTerminalDisposition disposition,
        DateTimeOffset resolvedAtUtc,
        string resolution) =>
        new(MarketPurchaseTerminalResolutionStatus.NoTerminalEvidence, "Server purchase evidence is unavailable.");
}

public interface IMarketAcquisitionRouteReporter
{
    bool CanReport { get; }
    Task<MarketAcquisitionRouteProgressReportOutcome> ReportRouteProgressAsync(
        MarketAcquisitionRouteProgressReport report,
        CancellationToken cancellationToken);
    Task ReportPurchaseAuditAsync(MarketAcquisitionPurchaseAuditReport report, CancellationToken cancellationToken);
    Task ReportLineProgressAsync(MarketAcquisitionLineProgressReport report, CancellationToken cancellationToken);
    Task ReportMarketObservationAsync(MarketAcquisitionMarketObservationReport report, CancellationToken cancellationToken);
}

public interface IMarketAcquisitionRouteEvidenceRecorder
{
    void RecordProbeVisit(
        string currentWorld,
        MarketAcquisitionRequestView activeLine,
        MarketAcquisitionWorldItemSubtask? activeSubtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        string? requestId,
        string routeRunId);

    void RecordPurchaseVisit(
        MarketBoardPurchaseCandidate candidate,
        MarketAcquisitionWorldItemSubtask activeSubtask,
        string worldName,
        string? requestId,
        string routeRunId);
}

public interface IMarketAcquisitionRouteCallbackDispatcher
{
    Task DispatchAsync(Action callback);
}

public sealed record MarketAcquisitionRouteProgressReport(
    string RequestId,
    string ClaimToken,
    string RouteState,
    string AttemptId,
    long Sequence,
    string? RouteStopId,
    string? ActiveWorld,
    string Phase,
    string Message);

public sealed record MarketAcquisitionRouteProgressReportOutcome(
    string Action,
    MarketAcquisitionRequestView Request);

public sealed record MarketAcquisitionPurchaseAuditReport(
    string RequestId,
    string ClaimToken,
    string AttemptId,
    long Sequence,
    string LineId,
    string WorldName,
    uint ItemId,
    string? ItemName,
    MarketBoardPurchaseCandidate Candidate,
    string Message);

public sealed record MarketAcquisitionLineProgressReport(
    string RequestId,
    string ClaimToken,
    string AttemptId,
    long Sequence,
    string LineId,
    string? ItemName,
    string Status,
    uint PurchasedQuantity,
    uint SpentGil,
    string Message,
    string? Reason);

public sealed record MarketAcquisitionMarketObservationReport(
    string RequestId,
    string ClaimToken,
    string AttemptId,
    long Sequence,
    string LineId,
    uint ItemId,
    string? ItemName,
    string DataCenter,
    string WorldName,
    DateTimeOffset ObservedAtUtc,
    MarketBoardReadResult ReadResult);
