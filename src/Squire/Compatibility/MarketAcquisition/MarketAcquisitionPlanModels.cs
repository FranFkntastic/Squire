using System;
using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionListing
{
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string ListingId { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public uint WorldId { get; init; }
    public string RetainerName { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint UnitPrice { get; init; }
    public bool IsHq { get; init; }
    public DateTimeOffset LastReviewTimeUtc { get; init; }
    public uint TotalGil => checked(UnitPrice * Quantity);
}

public sealed record MarketAcquisitionPlannedListing
{
    public string LineId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string ListingId { get; init; } = string.Empty;
    public string RetainerName { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint UnitPrice { get; init; }
    public uint TotalGil { get; init; }
    public bool IsHq { get; init; }
    public DateTimeOffset LastReviewTimeUtc { get; init; }
}

public sealed record MarketAcquisitionPlanLine
{
    public string LineId { get; init; } = string.Empty;
    public int Ordinal { get; init; }
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string QuantityMode { get; init; } = string.Empty;
    public uint RequestedQuantity { get; init; }
    public string HqPolicy { get; init; } = string.Empty;
    public uint MaxUnitPrice { get; init; }
    public uint GilCap { get; init; }
    public string Status { get; init; } = string.Empty;
    public uint PlannedQuantity { get; init; }
    public uint PlannedGil { get; init; }
}

public sealed record MarketAcquisitionWorldItemSubtask
{
    public string LineId { get; init; } = string.Empty;
    public int LineOrdinal { get; init; }
    public string Source { get; init; } = "Planned";
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public string DataCenter { get; init; } = string.Empty;
    public string QuantityMode { get; init; } = string.Empty;
    public uint RequestedQuantity { get; init; }
    public string HqPolicy { get; init; } = string.Empty;
    public uint MaxUnitPrice { get; init; }
    public uint GilCap { get; init; }
    public uint PlannedQuantity { get; init; }
    public uint PlannedGil { get; init; }
    public bool ExceedsRequestedQuantity { get; init; }
    public IReadOnlyList<MarketAcquisitionPlannedListing> Listings { get; init; } = [];
}

public sealed record MarketAcquisitionWorldBatch
{
    public string WorldName { get; init; } = string.Empty;
    public string DataCenter { get; init; } = string.Empty;
    public uint PlannedQuantity { get; init; }
    public uint PlannedGil { get; init; }
    public bool ExceedsRequestedQuantity { get; init; }
    public IReadOnlyList<MarketAcquisitionWorldItemSubtask> ItemSubtasks { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionPlannedListing> Listings { get; init; } = [];
}

public sealed record MarketAcquisitionPlan
{
    public string RequestId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string WorldMode { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public uint RequestedQuantity { get; init; }
    public uint PlannedQuantity { get; init; }
    public uint PlannedGil { get; init; }
    public DateTimeOffset PreparedAtUtc { get; init; }
    public MarketAcquisitionPlanDiagnostics Diagnostics { get; init; } = new();
    public IReadOnlyList<MarketAcquisitionPlanLine> Lines { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionWorldBatch> WorldBatches { get; init; } = [];
}

public sealed record MarketAcquisitionPlanDiagnostics
{
    public int SourceListingCount { get; init; }
    public int NonZeroListingCount { get; init; }
    public int PriceSupportedListingCount { get; init; }
    public int HqSupportedListingCount { get; init; }
    public int WorldSupportedListingCount { get; init; }
    public int PlannedListingCount { get; init; }
    public IReadOnlyList<MarketAcquisitionListingDecision> ListingDecisions { get; init; } = [];
}

public sealed record MarketAcquisitionListingDecision
{
    public string LineId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint UnitPrice { get; init; }
    public bool IsHq { get; init; }
}
