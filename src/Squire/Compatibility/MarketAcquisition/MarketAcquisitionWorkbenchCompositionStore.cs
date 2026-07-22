using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionWorkbenchCompositionStoreSnapshot(
    IReadOnlyList<MarketAcquisitionWorkbenchComposition> Compositions,
    string? SelectedCompositionId);

public interface IMarketAcquisitionWorkbenchCompositionStore
{
    MarketAcquisitionWorkbenchCompositionStoreSnapshot Load();

    void Save(MarketAcquisitionWorkbenchCompositionStoreSnapshot snapshot);
}

public sealed class ConfigurationMarketAcquisitionWorkbenchCompositionStore : IMarketAcquisitionWorkbenchCompositionStore
{
    private readonly Configuration config;
    private readonly Action saveConfig;

    public ConfigurationMarketAcquisitionWorkbenchCompositionStore(Configuration config, Action saveConfig)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.saveConfig = saveConfig ?? throw new ArgumentNullException(nameof(saveConfig));
    }

    public MarketAcquisitionWorkbenchCompositionStoreSnapshot Load()
    {
        var compositions = (config.MarketAcquisitionWorkbenchCompositions ?? [])
            .Where(stored => !string.IsNullOrWhiteSpace(stored.Id) && !string.IsNullOrWhiteSpace(stored.Name))
            .Select(FromPersisted)
            .OrderBy(composition => composition.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedId = compositions.Any(composition => composition.Id == config.SelectedMarketAcquisitionWorkbenchCompositionId)
            ? config.SelectedMarketAcquisitionWorkbenchCompositionId
            : compositions.FirstOrDefault()?.Id;
        return new MarketAcquisitionWorkbenchCompositionStoreSnapshot(compositions, selectedId);
    }

    public void Save(MarketAcquisitionWorkbenchCompositionStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        config.MarketAcquisitionWorkbenchCompositions = snapshot.Compositions.Select(ToPersisted).ToList();
        config.SelectedMarketAcquisitionWorkbenchCompositionId = snapshot.SelectedCompositionId;
        saveConfig();
    }

    private static MarketAcquisitionWorkbenchComposition FromPersisted(
        PersistedMarketAcquisitionWorkbenchComposition stored) =>
        new()
        {
            Id = stored.Id,
            Name = stored.Name.Trim(),
            Region = string.IsNullOrWhiteSpace(stored.Region) ? "North America" : stored.Region,
            WorldMode = MarketAcquisitionRequestDocumentMapper.NormalizeBuilderWorldMode(stored.WorldMode),
            SweepScope = string.IsNullOrWhiteSpace(stored.SweepScope) ? "Region" : stored.SweepScope,
            SweepDataCenters = stored.SweepDataCenters?.ToList() ?? [],
            Lines = (stored.Lines ?? []).Select(FromPersistedLine).ToList(),
            CreatedAtUtc = ToUtc(stored.CreatedAtUtc),
            UpdatedAtUtc = ToUtc(stored.UpdatedAtUtc),
        };

    private static PersistedMarketAcquisitionWorkbenchComposition ToPersisted(
        MarketAcquisitionWorkbenchComposition composition) =>
        new()
        {
            Id = composition.Id,
            Name = composition.Name,
            Region = composition.Region,
            WorldMode = composition.WorldMode,
            SweepScope = composition.SweepScope,
            SweepDataCenters = composition.SweepDataCenters.ToList(),
            Lines = composition.Lines.Select(ToPersistedLine).ToList(),
            CreatedAtUtc = composition.CreatedAtUtc.UtcDateTime,
            UpdatedAtUtc = composition.UpdatedAtUtc.UtcDateTime,
        };

    private static MarketAcquisitionRequestLineDocument FromPersistedLine(
        PersistedMarketAcquisitionRequestLineDocument line) =>
        new()
        {
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            ItemKind = line.ItemKind,
            QuantityMode = string.IsNullOrWhiteSpace(line.QuantityMode) ? "AllBelowThreshold" : line.QuantityMode,
            TargetQuantity = line.TargetQuantity,
            MaxQuantity = line.MaxQuantity,
            HqPolicy = string.IsNullOrWhiteSpace(line.HqPolicy) ? "Either" : line.HqPolicy,
            MaxUnitPrice = line.MaxUnitPrice,
            GilCap = line.GilCap,
        };

    private static PersistedMarketAcquisitionRequestLineDocument ToPersistedLine(
        MarketAcquisitionRequestLineDocument line) =>
        new()
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
        };

    private static DateTimeOffset ToUtc(DateTime value) =>
        value == default
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
