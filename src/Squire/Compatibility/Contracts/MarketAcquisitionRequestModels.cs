using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MarketMafioso.MarketAcquisition;

public record MarketAcquisitionRequestView
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("revision")]
    public int Revision { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("origin")]
    public string Origin { get; init; } = MarketAcquisitionOrigins.DashboardCreated;

    [JsonPropertyName("createdByPluginInstanceId")]
    public string? CreatedByPluginInstanceId { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("expiresAtUtc")]
    public DateTimeOffset ExpiresAtUtc { get; init; }

    [JsonPropertyName("claimedAtUtc")]
    public DateTimeOffset? ClaimedAtUtc { get; init; }

    [JsonPropertyName("claimExpiresAtUtc")]
    public DateTimeOffset? ClaimExpiresAtUtc { get; init; }

    [JsonPropertyName("targetCharacterName")]
    public string TargetCharacterName { get; init; } = string.Empty;

    [JsonPropertyName("targetWorld")]
    public string TargetWorld { get; init; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("quantityMode")]
    public string QuantityMode { get; init; } = string.Empty;

    [JsonPropertyName("quantity")]
    public uint Quantity { get; init; }

    [JsonPropertyName("hqPolicy")]
    public string HqPolicy { get; init; } = string.Empty;

    [JsonPropertyName("maxUnitPrice")]
    public uint MaxUnitPrice { get; init; }

    [JsonPropertyName("maxTotalGil")]
    public uint MaxTotalGil { get; init; }

    [JsonPropertyName("worldMode")]
    public string WorldMode { get; init; } = string.Empty;

    [JsonPropertyName("selectedWorlds")]
    public IReadOnlyList<string> SelectedWorlds { get; init; } = [];

    [JsonPropertyName("sweepScope")]
    public string SweepScope { get; init; } = "Region";

    [JsonPropertyName("sweepDataCenters")]
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];

    [JsonPropertyName("latestEventType")]
    public string? LatestEventType { get; init; }

    [JsonPropertyName("latestRunnerState")]
    public string? LatestRunnerState { get; init; }

    [JsonPropertyName("latestMessage")]
    public string? LatestMessage { get; init; }

    [JsonPropertyName("latestReason")]
    public string? LatestReason { get; init; }

    [JsonPropertyName("latestEventAtUtc")]
    public DateTimeOffset? LatestEventAtUtc { get; init; }

    [JsonPropertyName("latestAttemptId")]
    public string? LatestAttemptId { get; init; }

    [JsonPropertyName("latestAttemptSequence")]
    public long? LatestAttemptSequence { get; init; }

    [JsonPropertyName("latestAttemptEventType")]
    public string? LatestAttemptEventType { get; init; }

    [JsonPropertyName("latestAttemptPhase")]
    public string? LatestAttemptPhase { get; init; }

    [JsonPropertyName("latestAttemptWorld")]
    public string? LatestAttemptWorld { get; init; }

    [JsonPropertyName("latestAttemptResult")]
    public string? LatestAttemptResult { get; init; }

    [JsonPropertyName("latestAttemptPluginVersion")]
    public string? LatestAttemptPluginVersion { get; init; }

    [JsonPropertyName("lines")]
    public IReadOnlyList<MarketAcquisitionBatchLineView> Lines { get; init; } = [];
}

public sealed record MarketAcquisitionClaimView : MarketAcquisitionRequestView
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionPendingResponse
{
    [JsonPropertyName("requests")]
    public IReadOnlyList<MarketAcquisitionRequestView> Requests { get; init; } = [];
}

public sealed record MarketAcquisitionBatchPendingResponse
{
    [JsonPropertyName("batches")]
    public IReadOnlyList<MarketAcquisitionRequestView> Batches { get; init; } = [];
}

public sealed record MarketAcquisitionBatchCreateRequest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonPropertyName("origin")]
    public string Origin { get; init; } = MarketAcquisitionOrigins.PluginBuilder;

    [JsonPropertyName("createdByPluginInstanceId")]
    public string? CreatedByPluginInstanceId { get; init; }

    [JsonPropertyName("targetCharacterName")]
    public string TargetCharacterName { get; init; } = string.Empty;

    [JsonPropertyName("targetWorld")]
    public string TargetWorld { get; init; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; init; } = string.Empty;

    [JsonPropertyName("worldMode")]
    public string WorldMode { get; init; } = string.Empty;

    [JsonPropertyName("selectedWorlds")]
    public IReadOnlyList<string> SelectedWorlds { get; init; } = [];

    [JsonPropertyName("sweepScope")]
    public string SweepScope { get; init; } = "Region";

    [JsonPropertyName("sweepDataCenters")]
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];

    [JsonPropertyName("expiresInSeconds")]
    public int ExpiresInSeconds { get; init; } = 300;

    [JsonPropertyName("lines")]
    public IReadOnlyList<MarketAcquisitionBatchLineCreateRequest> Lines { get; init; } = [];
}

public sealed record MarketAcquisitionBatchReplaceRequest
{
    [JsonPropertyName("expectedRevision")]
    public int ExpectedRevision { get; init; }

    [JsonPropertyName("region")]
    public string Region { get; init; } = string.Empty;

    [JsonPropertyName("worldMode")]
    public string WorldMode { get; init; } = string.Empty;

    [JsonPropertyName("selectedWorlds")]
    public IReadOnlyList<string> SelectedWorlds { get; init; } = [];

    [JsonPropertyName("sweepScope")]
    public string SweepScope { get; init; } = "Region";

    [JsonPropertyName("sweepDataCenters")]
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];

    [JsonPropertyName("expiresInSeconds")]
    public int ExpiresInSeconds { get; init; } = 300;

    [JsonPropertyName("lines")]
    public IReadOnlyList<MarketAcquisitionBatchLineCreateRequest> Lines { get; init; } = [];
}

public sealed record MarketAcquisitionBatchLineCreateRequest
{
    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("itemKind")]
    public string? ItemKind { get; init; }

    [JsonPropertyName("quantityMode")]
    public string QuantityMode { get; init; } = string.Empty;

    [JsonPropertyName("targetQuantity")]
    public uint TargetQuantity { get; init; }

    [JsonPropertyName("maxQuantity")]
    public uint MaxQuantity { get; init; }

    [JsonPropertyName("hqPolicy")]
    public string HqPolicy { get; init; } = string.Empty;

    [JsonPropertyName("maxUnitPrice")]
    public uint MaxUnitPrice { get; init; }

    [JsonPropertyName("gilCap")]
    public uint GilCap { get; init; }
}

public sealed record MarketAcquisitionBatchLineView
{
    [JsonPropertyName("lineId")]
    public string LineId { get; init; } = string.Empty;

    [JsonPropertyName("batchId")]
    public string BatchId { get; init; } = string.Empty;

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; init; }

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("itemKind")]
    public string? ItemKind { get; init; }

    [JsonPropertyName("quantityMode")]
    public string QuantityMode { get; init; } = string.Empty;

    [JsonPropertyName("targetQuantity")]
    public uint TargetQuantity { get; init; }

    [JsonPropertyName("maxQuantity")]
    public uint MaxQuantity { get; init; }

    [JsonPropertyName("hqPolicy")]
    public string HqPolicy { get; init; } = string.Empty;

    [JsonPropertyName("maxUnitPrice")]
    public uint MaxUnitPrice { get; init; }

    [JsonPropertyName("gilCap")]
    public uint GilCap { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("purchasedQuantity")]
    public uint PurchasedQuantity { get; init; }

    [JsonPropertyName("spentGil")]
    public uint SpentGil { get; init; }

    [JsonPropertyName("latestMessage")]
    public string? LatestMessage { get; init; }
}

public static class MarketAcquisitionOrigins
{
    public const string DashboardCreated = "DashboardCreated";
    public const string PluginBuilder = "PluginBuilder";
    public const string ClientQuickShop = "ClientQuickShop";
    public const string CraftArchitect = "CraftArchitect";
}

public sealed record MarketAcquisitionClaimRequest
{
    [JsonPropertyName("characterName")]
    public string CharacterName { get; init; } = string.Empty;

    [JsonPropertyName("world")]
    public string World { get; init; } = string.Empty;

    [JsonPropertyName("pluginInstanceId")]
    public string PluginInstanceId { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionClaimTokenRequest
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionLeaseRenewRequest
{
    public string ClaimToken { get; init; } = string.Empty;
    public string PluginInstanceId { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionExecutionLeaseView
{
    public string WorkOrderId { get; init; } = string.Empty;
    public string PluginInstanceId { get; init; } = string.Empty;
    public DateTimeOffset RenewedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
}

public sealed record MarketAcquisitionLifecycleRequest
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonPropertyName("runnerState")]
    public string? RunnerState { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record MarketAcquisitionLineProgressRequest
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; init; } = string.Empty;

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("purchasedQuantity")]
    public uint PurchasedQuantity { get; init; }

    [JsonPropertyName("spentGil")]
    public uint SpentGil { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record MarketAcquisitionPurchaseAuditRequest
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; init; } = string.Empty;

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("lineId")]
    public string LineId { get; init; } = string.Empty;

    [JsonPropertyName("worldName")]
    public string WorldName { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("listingId")]
    public string ListingId { get; init; } = string.Empty;

    [JsonPropertyName("retainerName")]
    public string RetainerName { get; init; } = string.Empty;

    [JsonPropertyName("retainerId")]
    public string RetainerId { get; init; } = string.Empty;

    [JsonPropertyName("quantity")]
    public uint Quantity { get; init; }

    [JsonPropertyName("unitPrice")]
    public uint UnitPrice { get; init; }

    [JsonPropertyName("totalGil")]
    public uint TotalGil { get; init; }

    [JsonPropertyName("isHq")]
    public bool IsHq { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed record MarketAcquisitionPurchaseAuditView
{
    [JsonPropertyName("auditId")]
    public string AuditId { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("lineId")]
    public string LineId { get; init; } = string.Empty;

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; init; } = string.Empty;

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("worldName")]
    public string WorldName { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("listingId")]
    public string ListingId { get; init; } = string.Empty;

    [JsonPropertyName("retainerName")]
    public string RetainerName { get; init; } = string.Empty;

    [JsonPropertyName("retainerId")]
    public string RetainerId { get; init; } = string.Empty;

    [JsonPropertyName("quantity")]
    public uint Quantity { get; init; }

    [JsonPropertyName("unitPrice")]
    public uint UnitPrice { get; init; }

    [JsonPropertyName("totalGil")]
    public uint TotalGil { get; init; }

    [JsonPropertyName("isHq")]
    public bool IsHq { get; init; }

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record MarketAcquisitionMarketObservationRequest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; init; } = string.Empty;

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("lineId")]
    public string LineId { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("dataCenter")]
    public string DataCenter { get; init; } = string.Empty;

    [JsonPropertyName("worldName")]
    public string WorldName { get; init; } = string.Empty;

    [JsonPropertyName("readState")]
    public string ReadState { get; init; } = string.Empty;

    [JsonPropertyName("reportedListingCount")]
    public int ReportedListingCount { get; init; }

    [JsonPropertyName("listingCapacity")]
    public int ListingCapacity { get; init; }

    [JsonPropertyName("isTruncated")]
    public bool IsTruncated { get; init; }

    [JsonPropertyName("observedAtUtc")]
    public DateTimeOffset ObservedAtUtc { get; init; }

    [JsonPropertyName("listings")]
    public IReadOnlyList<MarketAcquisitionMarketObservationListing> Listings { get; init; } = [];
}

public sealed record MarketAcquisitionMarketObservationListing
{
    [JsonPropertyName("listingId")]
    public string ListingId { get; init; } = string.Empty;

    [JsonPropertyName("retainerId")]
    public string RetainerId { get; init; } = string.Empty;

    [JsonPropertyName("retainerName")]
    public string RetainerName { get; init; } = string.Empty;

    [JsonPropertyName("quantity")]
    public uint Quantity { get; init; }

    [JsonPropertyName("unitPrice")]
    public uint UnitPrice { get; init; }

    [JsonPropertyName("isHq")]
    public bool IsHq { get; init; }
}

public sealed record MarketAcquisitionMarketObservationView
{
    [JsonPropertyName("observationId")]
    public string ObservationId { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; init; } = string.Empty;

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("lineId")]
    public string LineId { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("dataCenter")]
    public string DataCenter { get; init; } = string.Empty;

    [JsonPropertyName("worldName")]
    public string WorldName { get; init; } = string.Empty;

    [JsonPropertyName("readState")]
    public string ReadState { get; init; } = string.Empty;

    [JsonPropertyName("reportedListingCount")]
    public int ReportedListingCount { get; init; }

    [JsonIgnore]
    public int ReadableListingCount => Listings.Count;

    [JsonPropertyName("listingCapacity")]
    public int ListingCapacity { get; init; }

    [JsonPropertyName("isTruncated")]
    public bool IsTruncated { get; init; }

    [JsonPropertyName("observedAtUtc")]
    public DateTimeOffset ObservedAtUtc { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("listings")]
    public IReadOnlyList<MarketAcquisitionMarketObservationListing> Listings { get; init; } = [];
}

public sealed record MarketAcquisitionAttemptEventRequest
{
    [JsonPropertyName("claimToken")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonPropertyName("pluginInstanceId")]
    public string PluginInstanceId { get; init; } = string.Empty;

    [JsonPropertyName("runnerState")]
    public string? RunnerState { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; init; } = string.Empty;

    [JsonPropertyName("eventSequence")]
    public long EventSequence { get; init; }

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = string.Empty;

    [JsonPropertyName("routeStopId")]
    public string? RouteStopId { get; init; }

    [JsonPropertyName("worldName")]
    public string? WorldName { get; init; }

    [JsonPropertyName("pluginVersion")]
    public string? PluginVersion { get; init; }

    [JsonPropertyName("clientTimestampUtc")]
    public DateTimeOffset ClientTimestampUtc { get; init; }
}

public sealed record MarketAcquisitionAttemptEventResult
{
    [JsonPropertyName("request")]
    public MarketAcquisitionRequestView Request { get; init; } = new();

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record MarketAcquisitionRequestTimelineView
{
    public MarketAcquisitionRequestView Request { get; init; } = new();
    public IReadOnlyList<MarketAcquisitionLifecycleEventView> LifecycleEvents { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionAttemptEventView> AttemptEvents { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionMarketObservationView> MarketObservations { get; init; } = [];
}

public sealed record MarketAcquisitionLifecycleEventView
{
    public string EventType { get; init; } = string.Empty;
    public string ResultStatus { get; init; } = string.Empty;
    public string? RunnerState { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record MarketAcquisitionAttemptEventView
{
    public string AttemptId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public string? RouteStopId { get; init; }
    public string? WorldName { get; init; }
    public string Result { get; init; } = string.Empty;
    public string? RunnerState { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
    public string? PluginVersion { get; init; }
    public DateTimeOffset? ClientTimestampUtc { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public static class MarketAcquisitionAttemptEventResults
{
    public const string Accepted = "accepted";
    public const string Replayed = "replayed";
    public const string StaleAttempt = "stale_attempt";
    public const string RequestTerminal = "request_terminal";
}
