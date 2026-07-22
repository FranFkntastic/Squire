using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionWorldVisitCatalog
{
    private readonly Configuration config;

    public MarketAcquisitionWorldVisitCatalog(Configuration config)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public IReadOnlyList<PersistedMarketAcquisitionWorldVisit> Visits => config.MarketAcquisitionWorldVisits;

    public void RecordProbe(MarketAcquisitionWorldVisitRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.WorldName))
            throw new InvalidOperationException("World name is required before recording a market acquisition visit.");
        if (record.ItemId == 0)
            throw new InvalidOperationException("Item id is required before recording a market acquisition visit.");
        if (string.IsNullOrWhiteSpace(record.HqPolicy))
            throw new InvalidOperationException("HQ policy is required before recording a market acquisition visit.");
        if (record.MaxUnitPrice == 0)
            throw new InvalidOperationException("Max unit price is required before recording a market acquisition visit.");
        if (record.CheckedAtUtc == default)
            throw new InvalidOperationException("Checked timestamp is required before recording a market acquisition visit.");

        var existing = config.MarketAcquisitionWorldVisits.FirstOrDefault(visit =>
            SameKey(
                visit.WorldName,
                visit.ItemId,
                visit.HqPolicy,
                visit.MaxUnitPrice,
                record.WorldName,
                record.ItemId,
                record.HqPolicy,
                record.MaxUnitPrice));
        if (existing == null)
        {
            config.MarketAcquisitionWorldVisits.Add(ToPersisted(record, canonicalWorldName: null));
            return;
        }

        var index = config.MarketAcquisitionWorldVisits.IndexOf(existing);
        config.MarketAcquisitionWorldVisits[index] = ToPersisted(record, existing.WorldName);
    }

    public bool WasRecentlyChecked(
        string worldName,
        uint itemId,
        string hqPolicy,
        uint maxUnitPrice,
        DateTimeOffset nowUtc,
        TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            return false;

        return config.MarketAcquisitionWorldVisits.Any(visit =>
            SameKey(visit.WorldName, visit.ItemId, visit.HqPolicy, visit.MaxUnitPrice, worldName, itemId, hqPolicy, maxUnitPrice) &&
            MarketAcquisitionLiveCandidateStatuses.IsConclusiveWorldVisitResult(visit.Result) &&
            IsWithinTtl(visit.CheckedAtUtc, nowUtc, ttl));
    }

    public PersistedMarketAcquisitionWorldVisit? FindRecent(
        string worldName,
        uint itemId,
        string hqPolicy,
        uint maxUnitPrice,
        DateTimeOffset nowUtc,
        TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            return null;

        return config.MarketAcquisitionWorldVisits
            .Where(visit =>
                SameKey(visit.WorldName, visit.ItemId, visit.HqPolicy, visit.MaxUnitPrice, worldName, itemId, hqPolicy, maxUnitPrice) &&
                MarketAcquisitionLiveCandidateStatuses.IsConclusiveWorldVisitResult(visit.Result) &&
                IsWithinTtl(visit.CheckedAtUtc, nowUtc, ttl))
            .OrderByDescending(visit => visit.CheckedAtUtc)
            .FirstOrDefault();
    }

    public IReadOnlyList<PersistedMarketAcquisitionWorldVisit> FindRecentWorlds(
        uint itemId,
        string hqPolicy,
        uint maxUnitPrice,
        DateTimeOffset nowUtc,
        TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            return [];

        return config.MarketAcquisitionWorldVisits
            .Where(visit =>
                visit.ItemId == itemId &&
                visit.MaxUnitPrice == maxUnitPrice &&
                visit.HqPolicy.Equals(hqPolicy, StringComparison.OrdinalIgnoreCase) &&
                MarketAcquisitionLiveCandidateStatuses.IsConclusiveWorldVisitResult(visit.Result) &&
                IsWithinTtl(visit.CheckedAtUtc, nowUtc, ttl))
            .GroupBy(visit => visit.WorldName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(visit => visit.CheckedAtUtc).First())
            .OrderBy(visit => visit.WorldName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Prune(int maxRecords)
    {
        if (maxRecords < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRecords), "Max records must be one or greater.");

        if (config.MarketAcquisitionWorldVisits.Count <= maxRecords)
            return;

        config.MarketAcquisitionWorldVisits = config.MarketAcquisitionWorldVisits
            .OrderByDescending(visit => visit.CheckedAtUtc)
            .Take(maxRecords)
            .OrderBy(visit => visit.CheckedAtUtc)
            .ToList();
    }

    private static PersistedMarketAcquisitionWorldVisit ToPersisted(
        MarketAcquisitionWorldVisitRecord record,
        string? canonicalWorldName) =>
        new()
        {
            WorldName = canonicalWorldName ?? record.WorldName.Trim(),
            DataCenter = record.DataCenter?.Trim() ?? string.Empty,
            ItemId = record.ItemId,
            ItemName = record.ItemName,
            HqPolicy = record.HqPolicy.Trim(),
            MaxUnitPrice = record.MaxUnitPrice,
            CheckedAtUtc = record.CheckedAtUtc.UtcDateTime,
            Result = record.Result.Trim(),
            PurchasedQuantity = record.PurchasedQuantity,
            SpentGil = record.SpentGil,
            ObservedLegalListingCount = record.ObservedLegalListingCount,
            ObservedLegalQuantity = record.ObservedLegalQuantity,
            ObservedLegalGil = record.ObservedLegalGil,
            Source = record.Source.Trim(),
            RequestId = record.RequestId,
            RouteRunId = record.RouteRunId,
            RouteStopId = record.RouteStopId,
        };

    private static bool SameKey(
        string leftWorld,
        uint leftItemId,
        string leftHqPolicy,
        uint leftMaxUnitPrice,
        string rightWorld,
        uint rightItemId,
        string rightHqPolicy,
        uint rightMaxUnitPrice) =>
        leftItemId == rightItemId &&
        leftMaxUnitPrice == rightMaxUnitPrice &&
        leftWorld.Equals(rightWorld, StringComparison.OrdinalIgnoreCase) &&
        leftHqPolicy.Equals(rightHqPolicy, StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinTtl(DateTime checkedAtUtc, DateTimeOffset nowUtc, TimeSpan ttl) =>
        nowUtc - new DateTimeOffset(DateTime.SpecifyKind(checkedAtUtc, DateTimeKind.Utc)) < ttl;
}

public sealed record MarketAcquisitionWorldVisitRecord
{
    public string WorldName { get; init; } = string.Empty;
    public string? DataCenter { get; init; }
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string HqPolicy { get; init; } = string.Empty;
    public uint MaxUnitPrice { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; }
    public string Result { get; init; } = string.Empty;
    public uint PurchasedQuantity { get; init; }
    public uint SpentGil { get; init; }
    public int ObservedLegalListingCount { get; init; }
    public uint ObservedLegalQuantity { get; init; }
    public ulong ObservedLegalGil { get; init; }
    public string Source { get; init; } = string.Empty;
    public string? RequestId { get; init; }
    public string? RouteRunId { get; init; }
    public string? RouteStopId { get; init; }
}
