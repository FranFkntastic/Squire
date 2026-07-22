using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRequestDocumentMapper
{
    public const int DefaultExpiresInSeconds = 300;

    public static MarketAcquisitionBatchCreateRequest BuildCreateRequest(
        MarketAcquisitionRequestDocument document,
        string characterName,
        string world,
        string pluginInstanceId)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(pluginInstanceId))
            throw new ArgumentException("Plugin instance id is required.", nameof(pluginInstanceId));

        var region = MarketAcquisitionWorldCatalog.NormalizeRegion(document.Region);
        return new MarketAcquisitionBatchCreateRequest
        {
            SchemaVersion = 1,
            IdempotencyKey = BuildCreateIdempotencyKey(pluginInstanceId, document),
            Origin = MarketAcquisitionOrigins.PluginBuilder,
            CreatedByPluginInstanceId = pluginInstanceId,
            TargetCharacterName = characterName.Trim(),
            TargetWorld = world.Trim(),
            Region = region,
            WorldMode = NormalizeBuilderWorldMode(document.WorldMode),
            SweepScope = NormalizeSweepScope(document.SweepScope),
            SweepDataCenters = NormalizeSweepDataCenters(region, document.SweepDataCenters),
            ExpiresInSeconds = DefaultExpiresInSeconds,
            Lines = document.Lines.Select(ToCreateLine).ToList(),
        };
    }

    public static MarketAcquisitionBatchReplaceRequest BuildReplaceRequest(
        MarketAcquisitionRequestDocument document,
        int expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(document);
        var region = MarketAcquisitionWorldCatalog.NormalizeRegion(document.Region);
        return new MarketAcquisitionBatchReplaceRequest
        {
            ExpectedRevision = expectedRevision,
            Region = region,
            WorldMode = NormalizeBuilderWorldMode(document.WorldMode),
            SweepScope = NormalizeSweepScope(document.SweepScope),
            SweepDataCenters = NormalizeSweepDataCenters(region, document.SweepDataCenters),
            ExpiresInSeconds = DefaultExpiresInSeconds,
            Lines = document.Lines.Select(ToCreateLine).ToList(),
        };
    }

    public static MarketAcquisitionRequestDocument FromRequestView(MarketAcquisitionRequestView request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var document = new MarketAcquisitionRequestDocument
        {
            TargetCharacterName = request.TargetCharacterName,
            TargetWorld = request.TargetWorld,
            Region = request.Region,
            WorldMode = NormalizeBuilderWorldMode(request.WorldMode),
            SweepScope = string.IsNullOrWhiteSpace(request.SweepScope) ? "Region" : request.SweepScope,
            SweepDataCenters = request.SweepDataCenters.ToList(),
            RemoteRequestId = request.Id,
            RemoteRevision = request.Revision,
            RemoteOrigin = request.Origin,
            Lines = GetLines(request).Select(ToDocumentLine).ToList(),
            SyncStatus = "SyncedClean",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        var hash = MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(document);
        return document with
        {
            LastSyncedHash = hash,
            RemoteHash = hash,
        };
    }

    public static IReadOnlyList<MarketAcquisitionBatchLineView> GetRequestLines(
        MarketAcquisitionRequestView request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetLines(request);
    }

    public static MarketAcquisitionRequestLineDocument FromRequestLine(
        MarketAcquisitionBatchLineView line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return ToDocumentLine(line);
    }

    public static string BuildCreateIdempotencyKey(
        string pluginInstanceId,
        MarketAcquisitionRequestDocument document)
    {
        if (string.IsNullOrWhiteSpace(pluginInstanceId))
            throw new ArgumentException("Plugin instance id is required.", nameof(pluginInstanceId));

        return $"{pluginInstanceId}:request-builder:{document.LocalRequestId}:{document.LocalRevision}";
    }

    public static string BuildAcceptIdempotencyKey(
        string pluginInstanceId,
        MarketAcquisitionRequestDocument document)
    {
        if (string.IsNullOrWhiteSpace(pluginInstanceId))
            throw new ArgumentException("Plugin instance id is required.", nameof(pluginInstanceId));

        return $"{pluginInstanceId}:request-builder:{document.LocalRequestId}:{document.LocalRevision}:accept";
    }

    public static MarketAcquisitionClaimView MergeClaimWithRequest(
        MarketAcquisitionClaimView claim,
        MarketAcquisitionRequestView request) =>
        claim with
        {
            Revision = request.Revision,
            Status = request.Status,
            Origin = request.Origin,
            CreatedByPluginInstanceId = request.CreatedByPluginInstanceId,
            TargetCharacterName = request.TargetCharacterName,
            TargetWorld = request.TargetWorld,
            Region = request.Region,
            ItemId = request.ItemId,
            ItemName = request.ItemName,
            QuantityMode = request.QuantityMode,
            Quantity = request.Quantity,
            HqPolicy = request.HqPolicy,
            MaxUnitPrice = request.MaxUnitPrice,
            MaxTotalGil = request.MaxTotalGil,
            WorldMode = request.WorldMode,
            SelectedWorlds = request.SelectedWorlds,
            SweepScope = request.SweepScope,
            SweepDataCenters = request.SweepDataCenters,
            Lines = request.Lines,
        };

    private static MarketAcquisitionBatchLineCreateRequest ToCreateLine(MarketAcquisitionRequestLineDocument line)
    {
        var mode = line.QuantityMode.Trim();
        return new MarketAcquisitionBatchLineCreateRequest
        {
            ItemId = line.ItemId,
            ItemName = string.IsNullOrWhiteSpace(line.ItemName) ? null : line.ItemName.Trim(),
            ItemKind = string.IsNullOrWhiteSpace(line.ItemKind) ? null : line.ItemKind.Trim(),
            QuantityMode = mode,
            TargetQuantity = mode == "TargetQuantity" ? line.TargetQuantity : 0,
            MaxQuantity = mode == "AllBelowThreshold" ? line.MaxQuantity : 0,
            HqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy),
            MaxUnitPrice = line.MaxUnitPrice,
            GilCap = line.GilCap,
        };
    }

    private static MarketAcquisitionRequestLineDocument ToDocumentLine(MarketAcquisitionBatchLineView line) =>
        new()
        {
            ItemId = line.ItemId,
            ItemName = line.ItemName ?? string.Empty,
            ItemKind = line.ItemKind,
            QuantityMode = line.QuantityMode,
            TargetQuantity = line.TargetQuantity,
            MaxQuantity = line.MaxQuantity,
            HqPolicy = line.HqPolicy,
            MaxUnitPrice = line.MaxUnitPrice,
            GilCap = line.GilCap,
        };

    private static IReadOnlyList<MarketAcquisitionBatchLineView> GetLines(MarketAcquisitionRequestView request) =>
        request.Lines.Count == 0
            ? [CreateFallbackLine(request)]
            : request.Lines;

    private static MarketAcquisitionBatchLineView CreateFallbackLine(MarketAcquisitionRequestView request) =>
        new()
        {
            LineId = $"{request.Id}-line-1",
            BatchId = request.Id,
            Ordinal = 0,
            ItemId = request.ItemId,
            ItemName = request.ItemName,
            QuantityMode = request.QuantityMode,
            TargetQuantity = request.QuantityMode == "TargetQuantity" ? request.Quantity : 0,
            MaxQuantity = request.QuantityMode == "AllBelowThreshold" ? request.Quantity : 0,
            HqPolicy = request.HqPolicy,
            MaxUnitPrice = request.MaxUnitPrice,
            GilCap = request.MaxTotalGil,
            Status = request.Status,
        };

    public static string NormalizeBuilderWorldMode(string? worldMode) =>
        string.Equals(worldMode?.Trim(), "AllWorldSweep", StringComparison.OrdinalIgnoreCase)
            ? "AllWorldSweep"
            : "Recommended";

    private static string NormalizeSweepScope(string sweepScope) =>
        string.IsNullOrWhiteSpace(sweepScope) ? "Region" : sweepScope.Trim();

    private static List<string> NormalizeSweepDataCenters(string region, IReadOnlyList<string> dataCenters) =>
        dataCenters
            .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
            .Select(dataCenter => MarketAcquisitionWorldCatalog.NormalizeDataCenterName(region, dataCenter))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
