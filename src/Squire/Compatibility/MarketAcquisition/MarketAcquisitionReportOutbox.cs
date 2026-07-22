using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Franthropy.Dalamud.Persistence;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionReportOutboxEntry
{
    public string Id { get; init; } = string.Empty;
    public string ReportType { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = string.Empty;
    public DateTimeOffset EnqueuedAtUtc { get; init; }
}

public interface IMarketAcquisitionReportOutbox
{
    MarketAcquisitionReportOutboxEntry Put<T>(string id, string reportType, T payload);
    IReadOnlyList<MarketAcquisitionReportOutboxEntry> Snapshot();
    void Remove(string id);
    T Deserialize<T>(MarketAcquisitionReportOutboxEntry entry);
}

public sealed class FileMarketAcquisitionReportOutbox : IMarketAcquisitionReportOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object sync = new();
    private readonly string path;
    private readonly string backupPath;
    private List<MarketAcquisitionReportOutboxEntry> entries;

    public FileMarketAcquisitionReportOutbox(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An outbox path is required.", nameof(path));

        this.path = path;
        backupPath = path + ".bak";
        entries = Load(path) ?? Load(backupPath) ?? [];
    }

    public MarketAcquisitionReportOutboxEntry Put<T>(string id, string reportType, T payload)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("An outbox entry id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(reportType))
            throw new ArgumentException("A report type is required.", nameof(reportType));

        lock (sync)
        {
            var existing = entries.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.Ordinal));
            if (existing != null)
                return existing;

            var entry = new MarketAcquisitionReportOutboxEntry
            {
                Id = id,
                ReportType = reportType,
                PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
            };
            entries.Add(entry);
            try
            {
                Persist();
                return entry;
            }
            catch
            {
                entries.Remove(entry);
                throw;
            }
        }
    }

    public IReadOnlyList<MarketAcquisitionReportOutboxEntry> Snapshot()
    {
        lock (sync)
            return entries.OrderBy(entry => entry.EnqueuedAtUtc).ToArray();
    }

    public void Remove(string id)
    {
        lock (sync)
        {
            var removed = entries.Where(entry => entry.Id.Equals(id, StringComparison.Ordinal)).ToArray();
            if (removed.Length == 0)
                return;

            entries.RemoveAll(entry => entry.Id.Equals(id, StringComparison.Ordinal));
            try
            {
                Persist();
            }
            catch
            {
                entries.AddRange(removed);
                throw;
            }
        }
    }

    public T Deserialize<T>(MarketAcquisitionReportOutboxEntry entry) =>
        JsonSerializer.Deserialize<T>(entry.PayloadJson, JsonOptions)
        ?? throw new InvalidDataException($"Outbox entry '{entry.Id}' has an empty {entry.ReportType} payload.");

    private List<MarketAcquisitionReportOutboxEntry>? Load(string candidatePath)
    {
        if (!File.Exists(candidatePath))
            return null;

        try
        {
            return AtomicJsonFile.Read<List<MarketAcquisitionReportOutboxEntry>>(candidatePath, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Persist()
    {
        if (File.Exists(path))
            File.Copy(path, backupPath, overwrite: true);
        AtomicJsonFile.Write(path, entries, JsonOptions);
    }
}

internal sealed class VolatileMarketAcquisitionReportOutbox : IMarketAcquisitionReportOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly List<MarketAcquisitionReportOutboxEntry> entries = [];

    public MarketAcquisitionReportOutboxEntry Put<T>(string id, string reportType, T payload)
    {
        lock (sync)
        {
            var existing = entries.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.Ordinal));
            if (existing != null)
                return existing;
            var entry = new MarketAcquisitionReportOutboxEntry
            {
                Id = id,
                ReportType = reportType,
                PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
            };
            entries.Add(entry);
            return entry;
        }
    }

    public IReadOnlyList<MarketAcquisitionReportOutboxEntry> Snapshot()
    {
        lock (sync)
            return entries.ToArray();
    }

    public void Remove(string id)
    {
        lock (sync)
            entries.RemoveAll(entry => entry.Id.Equals(id, StringComparison.Ordinal));
    }

    public T Deserialize<T>(MarketAcquisitionReportOutboxEntry entry) =>
        JsonSerializer.Deserialize<T>(entry.PayloadJson, JsonOptions)
        ?? throw new InvalidDataException($"Outbox entry '{entry.Id}' could not be deserialized.");
}
