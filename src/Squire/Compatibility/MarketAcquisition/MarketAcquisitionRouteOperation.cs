using System;
using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public enum MarketAcquisitionRouteOperationKind
{
    ItemSearch,
    TravelPreparation,
    Travel,
    MarketBoardApproach,
    ListingRead,
    ListingContinuation,
    PurchaseSelection,
    PurchaseConfirmation,
    PurchaseReconciliation,
}

public enum MarketAcquisitionRouteOperationPhase
{
    Started,
    Waiting,
    Completed,
    TimedOut,
    Cancelled,
}

public enum MarketAcquisitionRouteOperationDisposition
{
    Pending,
    Succeeded,
    RetryScheduled,
    SkippedItem,
    SkippedWorld,
    Failed,
    Cancelled,
    Indeterminate,
}

public sealed record MarketAcquisitionRouteOperationStart
{
    public required string OperationId { get; init; }

    public required MarketAcquisitionRouteOperationKind Kind { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required long StartedAtMonotonicMilliseconds { get; init; }

    public required TimeSpan Timeout { get; init; }

    public required MarketAcquisitionRouteOperationDisposition TimeoutDisposition { get; init; }

    public required string TimeoutMessage { get; init; }

    public int Attempt { get; init; } = 1;

    public IReadOnlyDictionary<string, string?> Context { get; init; } = new Dictionary<string, string?>();
}

public sealed record MarketAcquisitionRouteOperationObservation
{
    public required string OperationId { get; init; }

    public required MarketAcquisitionRouteOperationDisposition Disposition { get; init; }

    public required string Message { get; init; }

    public IReadOnlyDictionary<string, string?> Details { get; init; } = new Dictionary<string, string?>();
}

public sealed record MarketAcquisitionRouteOperationSnapshot
{
    public required string OperationId { get; init; }

    public required MarketAcquisitionRouteOperationKind Kind { get; init; }

    public required MarketAcquisitionRouteOperationPhase Phase { get; init; }

    public required MarketAcquisitionRouteOperationDisposition Disposition { get; init; }

    public required MarketAcquisitionRouteOperationDisposition TimeoutDisposition { get; init; }

    public required string TimeoutMessage { get; init; }

    public required int Attempt { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset DeadlineUtc { get; init; }

    public required long StartedAtMonotonicMilliseconds { get; init; }

    public required long DeadlineMonotonicMilliseconds { get; init; }

    public required long UpdatedAtMonotonicMilliseconds { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public required string Message { get; init; }

    public required IReadOnlyDictionary<string, string?> Context { get; init; }

    public required IReadOnlyDictionary<string, string?> Details { get; init; }

    public bool IsTerminal => Disposition != MarketAcquisitionRouteOperationDisposition.Pending;
}

public sealed record MarketAcquisitionRouteOperationApplyResult
{
    public required bool Accepted { get; init; }

    public required bool IsLateOrMismatched { get; init; }

    public required string Message { get; init; }

    public MarketAcquisitionRouteOperationSnapshot? Snapshot { get; init; }
}
