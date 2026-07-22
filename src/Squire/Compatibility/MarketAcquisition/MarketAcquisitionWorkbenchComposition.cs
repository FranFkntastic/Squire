using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionWorkbenchComposition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Untitled composition";
    public string Region { get; init; } = "North America";
    public string WorldMode { get; init; } = "Recommended";
    public string SweepScope { get; init; } = "Region";
    public List<string> SweepDataCenters { get; init; } = [];
    public List<MarketAcquisitionRequestLineDocument> Lines { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static MarketAcquisitionWorkbenchComposition FromDocument(
        string name,
        MarketAcquisitionRequestDocument document,
        DateTimeOffset nowUtc) =>
        new()
        {
            Name = name.Trim(),
            Region = document.Region,
            WorldMode = document.WorldMode,
            SweepScope = document.SweepScope,
            SweepDataCenters = document.SweepDataCenters.ToList(),
            Lines = CloneLines(document.Lines),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };

    public MarketAcquisitionWorkbenchComposition WithDocument(
        MarketAcquisitionRequestDocument document,
        DateTimeOffset nowUtc) =>
        this with
        {
            Region = document.Region,
            WorldMode = document.WorldMode,
            SweepScope = document.SweepScope,
            SweepDataCenters = document.SweepDataCenters.ToList(),
            Lines = CloneLines(document.Lines),
            UpdatedAtUtc = nowUtc,
        };

    public MarketAcquisitionRequestDocument CreateDocument(string characterName, string world) =>
        MarketAcquisitionRequestDocument.CreateDefault(characterName, world) with
        {
            Region = Region,
            WorldMode = WorldMode,
            SweepScope = SweepScope,
            SweepDataCenters = SweepDataCenters.ToList(),
            Lines = CloneLines(Lines),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    internal static List<MarketAcquisitionRequestLineDocument> CloneLines(
        IEnumerable<MarketAcquisitionRequestLineDocument> lines) =>
        lines.Select(line => line with { }).ToList();
}
