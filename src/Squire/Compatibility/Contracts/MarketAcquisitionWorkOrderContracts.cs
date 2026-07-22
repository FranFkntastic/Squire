namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionCreateRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string Origin { get; init; } = MarketAcquisitionOrigins.DashboardCreated;
    public string? CreatedByPluginInstanceId { get; init; }
    public string TargetCharacterName { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string QuantityMode { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public string HqPolicy { get; init; } = string.Empty;
    public uint MaxUnitPrice { get; init; }
    public uint MaxTotalGil { get; init; }
    public string WorldMode { get; init; } = string.Empty;
    public IReadOnlyList<string> SelectedWorlds { get; init; } = [];
    public string SweepScope { get; init; } = "Region";
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];
    public int ExpiresInSeconds { get; init; } = 300;
}

public sealed record MarketAcquisitionBatchAppendLinesRequest
{
    public int ExpectedRevision { get; init; }
    public int ExpiresInSeconds { get; init; } = 300;
    public IReadOnlyList<MarketAcquisitionBatchLineCreateRequest> Lines { get; init; } = [];
}

public static class MarketAcquisitionStatuses
{
    public const string PendingPickup = "PendingPickup";
    public const string Claimed = "Claimed";
    public const string AcceptedInPlugin = "AcceptedInPlugin";
    public const string Running = "Running";
    public const string RecoveryRequired = "RecoveryRequired";
    public const string Complete = "Complete";
    public const string Failed = "Failed";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
    public const string Shelved = "Shelved";
    public const string Archived = "Archived";
}

public static class MarketAcquisitionWorkOrderStates
{
    public const string Inbox = "Inbox";
    public const string Working = "Working";
    public const string Recovery = "Recovery";
    public const string Shelved = "Shelved";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string Archived = "Archived";
}

public sealed record MarketAcquisitionWorkOrderView
{
    public string Id { get; init; } = string.Empty;
    public int Revision { get; init; }
    public MarketAcquisitionRequestView Request { get; init; } = new();
    public string State { get; init; } = MarketAcquisitionWorkOrderStates.Inbox;
    public string Title { get; init; } = string.Empty;
    public int Priority { get; init; }
    public string? ParentWorkOrderId { get; init; }
    public string? MergeSourceWorkOrderId { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? ShelvedAtUtc { get; init; }
    public DateTimeOffset? ArchivedAtUtc { get; init; }
}

public sealed record MarketAcquisitionWorkOrderCommand
{
    public int ExpectedRevision { get; init; }
}

public sealed record MarketAcquisitionWorkOrderCloneRequest
{
    public int ExpectedRevision { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
    public string? Title { get; init; }
}

public sealed record MarketAcquisitionWorkOrderMergeRequest
{
    public string SourceWorkOrderId { get; init; } = string.Empty;
    public int ExpectedTargetRevision { get; init; }
    public int ExpectedSourceRevision { get; init; }
}

public sealed record MarketAcquisitionWorkOrderMergeConflict
{
    public string Field { get; init; } = string.Empty;
    public string TargetValue { get; init; } = string.Empty;
    public string SourceValue { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionWorkOrderMergePreview
{
    public string TargetWorkOrderId { get; init; } = string.Empty;
    public string SourceWorkOrderId { get; init; } = string.Empty;
    public bool CanMerge => Conflicts.Count == 0;
    public int ResultLineCount { get; init; }
    public IReadOnlyList<MarketAcquisitionWorkOrderMergeConflict> Conflicts { get; init; } = [];
}

public sealed record MarketAcquisitionWorkOrderRevisionView
{
    public string WorkOrderId { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string ChangeKind { get; init; } = string.Empty;
    public MarketAcquisitionRequestView Snapshot { get; init; } = new();
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record MarketAcquisitionExecutionSnapshotView
{
    public string SnapshotId { get; init; } = string.Empty;
    public string WorkOrderId { get; init; } = string.Empty;
    public int Revision { get; init; }
    public MarketAcquisitionRequestView Request { get; init; } = new();
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record MarketAcquisitionRunReceiptView
{
    public string ReceiptId { get; init; } = string.Empty;
    public string WorkOrderId { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public uint PurchasedQuantity { get; init; }
    public ulong SpentGil { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record MarketAcquisitionWorkOrderHistoryView
{
    public MarketAcquisitionWorkOrderView WorkOrder { get; init; } = new();
    public IReadOnlyList<MarketAcquisitionWorkOrderRevisionView> Revisions { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionExecutionSnapshotView> ExecutionSnapshots { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionRunReceiptView> Receipts { get; init; } = [];
}
