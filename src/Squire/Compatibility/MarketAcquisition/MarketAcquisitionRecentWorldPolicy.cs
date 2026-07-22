using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRecentWorldPolicy
{
    public static MarketAcquisitionRecentWorldListingFilterResult FilterListings(
        MarketAcquisitionBatchLineView line,
        IEnumerable<MarketAcquisitionListing> listings,
        MarketAcquisitionWorldVisitCatalog catalog,
        DateTimeOffset nowUtc,
        TimeSpan ttl,
        bool ignoreRecentVisits,
        IEnumerable<string> worldsWithNewerUsefulUniversalisEvidence)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(worldsWithNewerUsefulUniversalisEvidence);

        var sourceListings = listings.ToList();
        if (ignoreRecentVisits || ttl <= TimeSpan.Zero)
        {
            return new MarketAcquisitionRecentWorldListingFilterResult
            {
                Listings = sourceListings,
                SkippedRecentWorlds = [],
            };
        }

        var evidenceWorlds = worldsWithNewerUsefulUniversalisEvidence
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skippedWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filtered = new List<MarketAcquisitionListing>();
        var hqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy);

        foreach (var listing in sourceListings)
        {
            if (evidenceWorlds.Contains(listing.WorldName) ||
                !catalog.WasRecentlyChecked(listing.WorldName, line.ItemId, hqPolicy, line.MaxUnitPrice, nowUtc, ttl))
            {
                filtered.Add(listing);
                continue;
            }

            skippedWorlds.Add(listing.WorldName);
        }

        return new MarketAcquisitionRecentWorldListingFilterResult
        {
            Listings = filtered,
            SkippedRecentWorlds = skippedWorlds.OrderBy(world => world, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    public static IReadOnlyList<MarketAcquisitionSweepWorldExclusion> BuildSweepWorldExclusions(
        MarketAcquisitionBatchLineView line,
        MarketAcquisitionWorldVisitCatalog catalog,
        DateTimeOffset nowUtc,
        TimeSpan ttl,
        bool ignoreRecentVisits,
        IEnumerable<string> worldsWithNewerUsefulUniversalisEvidence)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(worldsWithNewerUsefulUniversalisEvidence);

        if (ignoreRecentVisits || ttl <= TimeSpan.Zero)
            return [];

        var hqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy);
        var evidenceWorlds = worldsWithNewerUsefulUniversalisEvidence
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return catalog.FindRecentWorlds(line.ItemId, hqPolicy, line.MaxUnitPrice, nowUtc, ttl)
            .Where(visit => !evidenceWorlds.Contains(visit.WorldName))
            .Select(visit => new MarketAcquisitionSweepWorldExclusion
            {
                LineId = line.LineId,
                ItemId = line.ItemId,
                WorldName = visit.WorldName,
                HqPolicy = hqPolicy,
                MaxUnitPrice = line.MaxUnitPrice,
                CheckedAtUtc = DateTime.SpecifyKind(visit.CheckedAtUtc, DateTimeKind.Utc),
            })
            .ToArray();
    }
}

public sealed record MarketAcquisitionRecentWorldListingFilterResult
{
    public IReadOnlyList<MarketAcquisitionListing> Listings { get; init; } = [];
    public IReadOnlyList<string> SkippedRecentWorlds { get; init; } = [];
}

public sealed record MarketAcquisitionSweepWorldExclusion
{
    public string LineId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public string HqPolicy { get; init; } = string.Empty;
    public uint MaxUnitPrice { get; init; }
    public DateTime CheckedAtUtc { get; init; }
}
