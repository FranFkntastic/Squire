using System;
using System.Linq;
using MarketMafioso.Squire.Outfitter.Acquisition;

namespace MarketMafioso.MarketAcquisition;

internal static class MarketAcquisitionRequestDocumentPersistence
{
    public static MarketAcquisitionRequestDocument Restore(
        Configuration config,
        string characterName = "",
        string world = "")
    {
        var stored = config.ActiveMarketAcquisitionRequestDocument;
        if (stored == null || string.IsNullOrWhiteSpace(stored.LocalRequestId))
            return MarketAcquisitionRequestDocument.CreateDefault(characterName, world);

        return new MarketAcquisitionRequestDocument
        {
            LocalRequestId = stored.LocalRequestId,
            LocalRevision = Math.Max(1, stored.LocalRevision),
            TargetCharacterName = string.IsNullOrWhiteSpace(stored.TargetCharacterName)
                ? characterName
                : stored.TargetCharacterName,
            TargetWorld = string.IsNullOrWhiteSpace(stored.TargetWorld)
                ? world
                : stored.TargetWorld,
            Region = string.IsNullOrWhiteSpace(stored.Region) ? "North America" : stored.Region,
            WorldMode = MarketAcquisitionRequestDocumentMapper.NormalizeBuilderWorldMode(stored.WorldMode),
            SweepScope = string.IsNullOrWhiteSpace(stored.SweepScope) ? "Region" : stored.SweepScope,
            SweepDataCenters = stored.SweepDataCenters ?? [],
            Lines = stored.Lines
                .Select(line => new MarketAcquisitionRequestLineDocument
                {
                    ItemId = line.ItemId,
                    ItemName = line.ItemName,
                    ItemKind = line.ItemKind,
                    QuantityMode = string.IsNullOrWhiteSpace(line.QuantityMode)
                        ? "AllBelowThreshold"
                        : line.QuantityMode,
                    TargetQuantity = line.TargetQuantity,
                    MaxQuantity = line.MaxQuantity,
                    HqPolicy = string.IsNullOrWhiteSpace(line.HqPolicy) ? "Either" : line.HqPolicy,
                    MaxUnitPrice = line.MaxUnitPrice,
                    GilCap = line.GilCap,
                })
                .ToList(),
            OutfitterAuthority = OutfitterWorkbenchAuthorityPersistence.Restore(stored.OutfitterAuthorityJson),
            RemoteRequestId = stored.RemoteRequestId,
            RemoteRevision = stored.RemoteRevision,
            RemoteOrigin = stored.RemoteOrigin,
            LastSyncedHash = stored.LastSyncedHash,
            RemoteHash = stored.RemoteHash,
            LastPlanHash = stored.LastPlanHash,
            SyncStatus = string.IsNullOrWhiteSpace(stored.SyncStatus) ? "NewDraft" : stored.SyncStatus,
            UpdatedAtUtc = stored.UpdatedAtUtc == default
                ? DateTimeOffset.UtcNow
                : new DateTimeOffset(DateTime.SpecifyKind(stored.UpdatedAtUtc, DateTimeKind.Utc)),
        };
    }

    public static void Save(Configuration config, MarketAcquisitionRequestDocument document)
    {
        config.ActiveMarketAcquisitionRequestDocument = new PersistedMarketAcquisitionRequestDocument
        {
            LocalRequestId = document.LocalRequestId,
            LocalRevision = document.LocalRevision,
            TargetCharacterName = document.TargetCharacterName,
            TargetWorld = document.TargetWorld,
            Region = document.Region,
            WorldMode = document.WorldMode,
            SweepScope = document.SweepScope,
            SweepDataCenters = document.SweepDataCenters.ToList(),
            Lines = document.Lines
                .Select(line => new PersistedMarketAcquisitionRequestLineDocument
                {
                    ItemId = line.ItemId,
                    ItemName = line.ItemName,
                    ItemKind = line.ItemKind,
                    QuantityMode = line.QuantityMode,
                    TargetQuantity = line.TargetQuantity,
                    MaxQuantity = line.MaxQuantity,
                    HqPolicy = line.HqPolicy,
                    MaxUnitPrice = line.MaxUnitPrice,
                    GilCap = line.GilCap,
                })
                .ToList(),
            OutfitterAuthorityJson = OutfitterWorkbenchAuthorityPersistence.Serialize(document.OutfitterAuthority),
            RemoteRequestId = document.RemoteRequestId,
            RemoteRevision = document.RemoteRevision,
            RemoteOrigin = document.RemoteOrigin,
            LastSyncedHash = document.LastSyncedHash,
            RemoteHash = document.RemoteHash,
            LastPlanHash = document.LastPlanHash,
            SyncStatus = document.SyncStatus,
            UpdatedAtUtc = document.UpdatedAtUtc.UtcDateTime,
        };
    }
}
