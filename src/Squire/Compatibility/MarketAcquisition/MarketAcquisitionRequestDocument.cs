using System;
using System.Collections.Generic;
using MarketMafioso.Squire.Outfitter.Acquisition;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRequestDocument
{
    public string LocalRequestId { get; init; } = Guid.NewGuid().ToString("N");
    public int LocalRevision { get; init; } = 1;
    public string TargetCharacterName { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public string Region { get; init; } = "North America";
    public string WorldMode { get; init; } = "Recommended";
    public string SweepScope { get; init; } = "Region";
    public List<string> SweepDataCenters { get; init; } = [];
    public List<MarketAcquisitionRequestLineDocument> Lines { get; init; } = [];
    public OutfitterWorkbenchAuthority? OutfitterAuthority { get; init; }
    public string? RemoteRequestId { get; init; }
    public int RemoteRevision { get; init; }
    public string? RemoteOrigin { get; init; }
    public string? LastSyncedHash { get; init; }
    public string? RemoteHash { get; init; }
    public string? LastPlanHash { get; init; }
    public string SyncStatus { get; init; } = "NewDraft";
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static MarketAcquisitionRequestDocument CreateDefault(
        string characterName = "",
        string world = "") =>
        new()
        {
            TargetCharacterName = characterName,
            TargetWorld = world,
        };

    public MarketAcquisitionRequestDocument WithNextRevision(string syncStatus) =>
        this with
        {
            LocalRevision = LocalRevision + 1,
            SyncStatus = syncStatus,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    public MarketAcquisitionRequestDocument WithNewIdentity() =>
        this with
        {
            LocalRequestId = Guid.NewGuid().ToString("N"),
            LocalRevision = 1,
            RemoteRequestId = null,
            RemoteRevision = 0,
            RemoteOrigin = null,
            LastSyncedHash = null,
            RemoteHash = null,
            LastPlanHash = null,
            OutfitterAuthority = OutfitterAuthority is null
                ? null
                : OutfitterAuthority with { FinalizedContract = null },
            SyncStatus = "NewDraft",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
}

public sealed record MarketAcquisitionRequestLineDocument
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? ItemKind { get; init; }
    public string QuantityMode { get; init; } = "AllBelowThreshold";
    public uint TargetQuantity { get; init; }
    public uint MaxQuantity { get; init; }
    public string HqPolicy { get; init; } = "Either";
    public uint MaxUnitPrice { get; init; }
    public uint GilCap { get; init; }
}
