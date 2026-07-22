using System;
using System.Collections.Generic;

namespace MarketMafioso;

// Compatibility model for the acquisition engine that now ships with Squire.
// It deliberately contains only the state that engine owns.
public sealed class Configuration : global::MarketMafioso.Squire.ISquireConfigurationStore
{
    private readonly Action save;

    public Configuration(Action? save = null) => this.save = save ?? (() => { });

    public string ServerUrl { get; set; } = "http://localhost:8080/inventory";
    public string ApiKey { get; set; } = string.Empty;
    public string CommandPickupApiKey { get; set; } = string.Empty;
    public string PluginInstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public PersistedMarketAcquisitionClaim? ActiveMarketAcquisitionClaim { get; set; }
    public PersistedMarketAcquisitionRequestDocument? ActiveMarketAcquisitionRequestDocument { get; set; }
    public List<PersistedMarketAcquisitionWorkbenchComposition> MarketAcquisitionWorkbenchCompositions { get; set; } = [];
    public string? SelectedMarketAcquisitionWorkbenchCompositionId { get; set; }
    public bool EnableMarketAcquisition { get; set; }
    public DateTime? MarketAcquisitionUnlockedAtUtc { get; set; }
    public List<PersistedMarketAcquisitionWorldVisit> MarketAcquisitionWorldVisits { get; set; } = [];
    public global::MarketMafioso.Squire.SquireConfiguration Squire { get; set; } = new();
    public string? OutfitterRouteExecutionStateJson { get; set; }
    public bool EnableMarketAcquisitionDryRunTools { get; set; }

    public void Save() => save();
}

public class CachedRetainer
{
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public string? OwnerCharacterName { get; set; }
    public string? OwnerHomeWorld { get; set; }
    public DateTime LastUpdated { get; set; }
    public ulong Gil { get; set; }
    public List<CachedBag> Bags { get; set; } = [];
    public List<CachedMarketListing> MarketListings { get; set; } = [];
}

public class CachedBag
{
    public string BagName { get; set; } = string.Empty;
    public string? Location { get; set; }
    public List<CachedItem> Items { get; set; } = [];
}

public class CachedItem
{
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string? ItemType { get; set; }
    public uint Quantity { get; set; }
    public bool IsHQ { get; set; }
    public float Condition { get; set; }
    public string? ContainerKey { get; set; }
    public int? SlotIndex { get; set; }
    public float? ConditionPercent { get; set; }
    public bool? Equipped { get; set; }
}

public class CachedMarketListing
{
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string? ItemType { get; set; }
    public uint Quantity { get; set; }
    public bool IsHQ { get; set; }
    public float Condition { get; set; }
    public string? ContainerKey { get; set; }
    public int? SlotIndex { get; set; }
    public float? ConditionPercent { get; set; }
    public uint? UnitPrice { get; set; }
    public string? ListedAt { get; set; }
}

public sealed class PersistedMarketAcquisitionClaim
{
    public string Id { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string? CreatedByPluginInstanceId { get; set; }
    public string TargetCharacterName { get; set; } = string.Empty;
    public string TargetWorld { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string QuantityMode { get; set; } = string.Empty;
    public uint Quantity { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public uint MaxTotalGil { get; set; }
    public string WorldMode { get; set; } = string.Empty;
    public List<string> SelectedWorlds { get; set; } = [];
    public string ClaimToken { get; set; } = string.Empty;
    public string? AcceptIdempotencyKey { get; set; }
    public string? RejectIdempotencyKey { get; set; }
    public List<PersistedMarketAcquisitionLine> Lines { get; set; } = [];
}

public sealed class PersistedMarketAcquisitionLine
{
    public string LineId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string? ItemKind { get; set; }
    public string QuantityMode { get; set; } = string.Empty;
    public uint TargetQuantity { get; set; }
    public uint MaxQuantity { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public uint GilCap { get; set; }
    public string Status { get; set; } = string.Empty;
    public uint PurchasedQuantity { get; set; }
    public uint SpentGil { get; set; }
    public string? LatestMessage { get; set; }
}

public sealed class PersistedMarketAcquisitionRequestDocument
{
    public string LocalRequestId { get; set; } = string.Empty;
    public int LocalRevision { get; set; }
    public string TargetCharacterName { get; set; } = string.Empty;
    public string TargetWorld { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string WorldMode { get; set; } = string.Empty;
    public string SweepScope { get; set; } = string.Empty;
    public List<string> SweepDataCenters { get; set; } = [];
    public List<PersistedMarketAcquisitionRequestLineDocument> Lines { get; set; } = [];
    public string? OutfitterAuthorityJson { get; set; }
    public string? RemoteRequestId { get; set; }
    public int RemoteRevision { get; set; }
    public string? RemoteOrigin { get; set; }
    public string? LastSyncedHash { get; set; }
    public string? RemoteHash { get; set; }
    public string? LastPlanHash { get; set; }
    public string SyncStatus { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class PersistedMarketAcquisitionRequestLineDocument
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string? ItemKind { get; set; }
    public string QuantityMode { get; set; } = string.Empty;
    public uint TargetQuantity { get; set; }
    public uint MaxQuantity { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public uint GilCap { get; set; }
}

public sealed class PersistedMarketAcquisitionWorkbenchComposition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string WorldMode { get; set; } = string.Empty;
    public string SweepScope { get; set; } = string.Empty;
    public List<string> SweepDataCenters { get; set; } = [];
    public List<PersistedMarketAcquisitionRequestLineDocument> Lines { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class PersistedMarketAcquisitionWorldVisit
{
    public string WorldName { get; set; } = string.Empty;
    public string DataCenter { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public string Result { get; set; } = string.Empty;
    public uint PurchasedQuantity { get; set; }
    public uint SpentGil { get; set; }
    public int ObservedLegalListingCount { get; set; }
    public uint ObservedLegalQuantity { get; set; }
    public ulong ObservedLegalGil { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string? RouteRunId { get; set; }
    public string? RouteStopId { get; set; }
}
