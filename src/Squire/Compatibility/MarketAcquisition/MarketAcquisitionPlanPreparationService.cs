using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public interface IMarketAcquisitionListingSource
{
    Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsAsync(
        string region,
        uint itemId,
        int listingLimit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsForWorldAsync(
        string worldName,
        uint itemId,
        int listingLimit,
        CancellationToken cancellationToken);
}

public sealed class MarketAcquisitionPlanPreparationService
{
    public static bool CanPrepareForStatus(string status) =>
        string.Equals(status, "AcceptedInPlugin", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase) ||
        IsFailedStatus(status);

    public static bool IsFailedStatus(string status) =>
        string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase);

    private readonly IMarketAcquisitionListingSource listingSource;
    private readonly MarketAcquisitionWorldVisitCatalog worldVisitCatalog;
    private readonly Action<Exception, string, uint>? freshEvidenceWarningSink;

    public MarketAcquisitionPlanPreparationService(
        IMarketAcquisitionListingSource listingSource,
        MarketAcquisitionWorldVisitCatalog worldVisitCatalog,
        Action<Exception, string, uint>? freshEvidenceWarningSink = null)
    {
        this.listingSource = listingSource ?? throw new ArgumentNullException(nameof(listingSource));
        this.worldVisitCatalog = worldVisitCatalog ?? throw new ArgumentNullException(nameof(worldVisitCatalog));
        this.freshEvidenceWarningSink = freshEvidenceWarningSink;
    }

    public async Task<MarketAcquisitionPlanPreparationResult> PrepareAsync(
        MarketAcquisitionPlanPreparationRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var claimed = request.Claim ?? throw new InvalidOperationException("No dashboard request is accepted.");
        var planLines = GetPlanLines(claimed);
        var listings = new List<MarketAcquisitionListing>();
        var sweepWorldExclusions = new List<MarketAcquisitionSweepWorldExclusion>();
        var freshEvidenceWorldCount = 0;
        var recentSkippedWorldCount = 0;
        var preparedAtUtc = request.PreparedAtUtc;
        var isAllWorldSweep = claimed.WorldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase);
        foreach (var line in planLines)
        {
            var lineListings = await listingSource.FetchListingsAsync(
                claimed.Region,
                line.ItemId,
                100,
                token).ConfigureAwait(false);
            if (isAllWorldSweep)
            {
                var evidenceWorlds = await FindWorldsWithNewerUsefulUniversalisEvidenceAsync(
                    line,
                    preparedAtUtc,
                    request.RecentWorldTtl,
                    request.IgnoreRecentWorldVisitsForSweep,
                    token).ConfigureAwait(false);
                freshEvidenceWorldCount += evidenceWorlds.Count;

                var filterResult = MarketAcquisitionRecentWorldPolicy.FilterListings(
                    line,
                    lineListings,
                    worldVisitCatalog,
                    preparedAtUtc,
                    request.RecentWorldTtl,
                    request.IgnoreRecentWorldVisitsForSweep,
                    evidenceWorlds);
                lineListings = filterResult.Listings;

                var lineExclusions = MarketAcquisitionRecentWorldPolicy.BuildSweepWorldExclusions(
                    line,
                    worldVisitCatalog,
                    preparedAtUtc,
                    request.RecentWorldTtl,
                    request.IgnoreRecentWorldVisitsForSweep,
                    evidenceWorlds);
                sweepWorldExclusions.AddRange(lineExclusions);
                recentSkippedWorldCount += lineExclusions.Count;
            }

            listings.AddRange(lineListings);
        }

        if (string.IsNullOrWhiteSpace(request.CurrentWorld))
            throw new InvalidOperationException("Current world is required before preparing a route-aware advisory plan.");

        var plan = MarketAcquisitionPlanner.BuildPlan(
            claimed,
            listings,
            preparedAtUtc,
            request.CurrentWorld,
            sweepWorldExclusions);
        var statusMessage = plan.Status == "Ready"
            ? BuildPreparedPlanStatus(plan, recentSkippedWorldCount, freshEvidenceWorldCount)
            : BuildNoSupportedListingsStatus(plan);

        return new MarketAcquisitionPlanPreparationResult
        {
            Plan = plan,
            StatusMessage = statusMessage,
            RecentSkippedWorldCount = recentSkippedWorldCount,
            FreshEvidenceWorldCount = freshEvidenceWorldCount,
        };
    }

    public static IReadOnlyList<MarketAcquisitionBatchLineView> GetPlanLines(MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(claimed);

        if (claimed.Lines.Count > 0)
            return claimed.Lines
                .OrderBy(line => line.Ordinal)
                .ToList();

        return
        [
            new MarketAcquisitionBatchLineView
            {
                LineId = claimed.Id,
                Ordinal = 0,
                ItemId = claimed.ItemId,
                ItemName = claimed.ItemName,
                QuantityMode = claimed.QuantityMode,
                TargetQuantity = claimed.Quantity,
                MaxQuantity = claimed.Quantity,
                HqPolicy = claimed.HqPolicy,
                MaxUnitPrice = claimed.MaxUnitPrice,
                GilCap = claimed.MaxTotalGil,
            },
        ];
    }

    private async Task<IReadOnlyList<string>> FindWorldsWithNewerUsefulUniversalisEvidenceAsync(
        MarketAcquisitionBatchLineView line,
        DateTimeOffset preparedAtUtc,
        TimeSpan ttl,
        bool ignoreRecentVisits,
        CancellationToken token)
    {
        if (ignoreRecentVisits || ttl <= TimeSpan.Zero)
            return [];

        var hqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy);
        var recentVisits = worldVisitCatalog.FindRecentWorlds(
            line.ItemId,
            hqPolicy,
            line.MaxUnitPrice,
            preparedAtUtc,
            ttl);
        if (recentVisits.Count == 0)
            return [];

        var evidenceWorlds = new List<string>();
        foreach (var visit in recentVisits)
        {
            try
            {
                var worldListings = await listingSource.FetchListingsForWorldAsync(
                    visit.WorldName,
                    line.ItemId,
                    100,
                    token).ConfigureAwait(false);
                var checkedAtUtc = DateTime.SpecifyKind(visit.CheckedAtUtc, DateTimeKind.Utc);
                if (worldListings.Any(listing => IsNewerUsefulUniversalisListing(line, hqPolicy, checkedAtUtc, listing)))
                    evidenceWorlds.Add(visit.WorldName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                freshEvidenceWarningSink?.Invoke(ex, visit.WorldName, line.ItemId);
            }
        }

        return evidenceWorlds;
    }

    private static bool IsNewerUsefulUniversalisListing(
        MarketAcquisitionBatchLineView line,
        string hqPolicy,
        DateTime checkedAtUtc,
        MarketAcquisitionListing listing) =>
        listing.ItemId == line.ItemId &&
        listing.Quantity > 0 &&
        listing.UnitPrice > 0 &&
        listing.UnitPrice <= line.MaxUnitPrice &&
        MarketAcquisitionPolicy.HqMatches(hqPolicy, listing.IsHq) &&
        listing.LastReviewTimeUtc > new DateTimeOffset(checkedAtUtc);

    private static string BuildNoSupportedListingsStatus(MarketAcquisitionPlan plan)
    {
        var diagnostics = plan.Diagnostics;
        return "No supported listings found under the configured thresholds. " +
               $"Fetched {diagnostics.SourceListingCount:N0}; " +
               $"{diagnostics.NonZeroListingCount:N0} non-zero; " +
               $"{diagnostics.PriceSupportedListingCount:N0} at/below max unit; " +
               $"{diagnostics.HqSupportedListingCount:N0} after HQ policy; " +
               $"{diagnostics.WorldSupportedListingCount:N0} after world mode; " +
               $"{diagnostics.PlannedListingCount:N0} planned.";
    }

    private static string BuildPreparedPlanStatus(
        MarketAcquisitionPlan plan,
        int recentSkippedWorldCount,
        int freshEvidenceWorldCount)
    {
        var status = $"Prepared {plan.WorldBatches.Count:N0} world batch(es).";
        if (recentSkippedWorldCount > 0)
            status += $" Skipped {recentSkippedWorldCount:N0} recent sweep world/item check(s).";
        if (freshEvidenceWorldCount > 0)
            status += $" Reopened {freshEvidenceWorldCount:N0} recent world/item check(s) with fresh Universalis evidence.";

        return status;
    }
}

public sealed record MarketAcquisitionPlanPreparationRequest
{
    public MarketAcquisitionClaimView? Claim { get; init; }
    public string CurrentWorld { get; init; } = string.Empty;
    public DateTimeOffset PreparedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan RecentWorldTtl { get; init; }
    public bool IgnoreRecentWorldVisitsForSweep { get; init; }
}

public sealed record MarketAcquisitionPlanPreparationResult
{
    public MarketAcquisitionPlan Plan { get; init; } = new();
    public string StatusMessage { get; init; } = string.Empty;
    public int RecentSkippedWorldCount { get; init; }
    public int FreshEvidenceWorldCount { get; init; }
}
