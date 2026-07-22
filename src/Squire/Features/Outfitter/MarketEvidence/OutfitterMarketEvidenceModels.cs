using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.MarketEvidence;

public enum OutfitterMarketCoverageMode
{
    ExhaustiveWithinScope,
    Sampled,
}

public enum OutfitterMarketEvidenceItemStatus
{
    Fresh,
    StaleUsable,
    Missing,
    Failed,
}

public enum OutfitterMarketEvidenceGenerationStatus
{
    Complete,
    Partial,
    Cancelled,
    Failed,
}

public enum OutfitterMarketDiscoveryStage
{
    Idle,
    Cataloging,
    CacheLookup,
    Fetching,
    Merging,
    Publishing,
    Complete,
    Partial,
    Cancelled,
    Failed,
}

public sealed record OutfitterMarketEvidenceRequest(
    string SourceKey,
    string Region,
    IReadOnlyList<uint> ItemIds,
    int ListingLimit = 100,
    OutfitterMarketCoverageMode CoverageMode = OutfitterMarketCoverageMode.ExhaustiveWithinScope,
    int? SampleSize = null,
    int MaxConcurrency = 4);

public sealed record OutfitterMarketEvidenceCacheKey(
    string SourceKey,
    string Region,
    uint ItemId,
    int ListingLimit);

public sealed record OutfitterMarketListingEvidence(
    uint ItemId,
    EquipmentQuality Quality,
    string ListingId,
    string WorldName,
    uint WorldId,
    string RetainerName,
    string RetainerId,
    uint Quantity,
    uint UnitPriceGil,
    DateTimeOffset ListingReviewedAtUtc,
    DateTimeOffset CapturedAtUtc,
    string SourceRevision);

public sealed record OutfitterMarketItemEvidence(
    uint ItemId,
    OutfitterMarketEvidenceItemStatus Status,
    IReadOnlyList<OutfitterMarketListingEvidence> Listings,
    DateTimeOffset CapturedAtUtc,
    string SourceRevision,
    string? Diagnostic = null,
    DateTimeOffset? RetryAfterUtc = null);

public sealed record OutfitterMarketCoverage(
    OutfitterMarketCoverageMode Mode,
    int CatalogItemCount,
    int QueriedItemCount,
    int ListingLimit,
    IReadOnlyList<uint> QueriedItemIds)
{
    public bool IsSampled => Mode == OutfitterMarketCoverageMode.Sampled;
}

public sealed record OutfitterMarketEvidenceBook(
    Guid GenerationId,
    long Revision,
    string SchemaVersion,
    string SourceKey,
    string Region,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    OutfitterMarketEvidenceGenerationStatus Status,
    OutfitterMarketCoverage Coverage,
    IReadOnlyList<OutfitterMarketItemEvidence> Items)
{
    public const string CurrentSchemaVersion = "marketmafioso-outfitter-market-evidence/v1";
    public bool IsPublishable => Status == OutfitterMarketEvidenceGenerationStatus.Complete &&
        Coverage.QueriedItemCount > 0 &&
        Items.All(item => item.Status is OutfitterMarketEvidenceItemStatus.Fresh or OutfitterMarketEvidenceItemStatus.Missing);

    public bool Matches(OutfitterMarketEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestedIds = request.ItemIds
            .Where(itemId => itemId != 0)
            .Distinct()
            .Order()
            .ToArray();
        if (request.CoverageMode == OutfitterMarketCoverageMode.Sampled)
            requestedIds = requestedIds
                .Take(Math.Clamp(request.SampleSize ?? 1, 1, Math.Max(1, requestedIds.Length)))
                .ToArray();
        return IsPublishable &&
               string.Equals(SourceKey, request.SourceKey.Trim(), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Region, request.Region.Trim(), StringComparison.OrdinalIgnoreCase) &&
               Coverage.Mode == request.CoverageMode &&
               Coverage.CatalogItemCount == request.ItemIds.Where(itemId => itemId != 0).Distinct().Count() &&
               Coverage.ListingLimit == Math.Clamp(request.ListingLimit, 1, 100) &&
               Coverage.QueriedItemIds.Order().SequenceEqual(requestedIds);
    }
}

public sealed record OutfitterMarketDiscoveryProgress(
    OutfitterMarketDiscoveryStage Stage,
    int Completed,
    int? Total,
    string Message,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? RetryAfterUtc = null)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Completed / Total.Value, 0d, 1d) : null;
}

public sealed record OutfitterMarketDiscoveryLiveState(
    OutfitterMarketDiscoveryProgress Progress,
    OutfitterMarketEvidenceBook? PreviousPublishedBook,
    IReadOnlyList<OutfitterMarketItemEvidence> VisibleItems);

public sealed record OutfitterMarketDiscoveryResult(
    OutfitterMarketEvidenceBook? PreviousPublishedBook,
    OutfitterMarketEvidenceBook WorkingBook,
    OutfitterMarketEvidenceBook? PublishedBook,
    bool PublishedChanged);

public interface IOutfitterMarketEvidenceBookStore
{
    Task<OutfitterMarketEvidenceBook?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(OutfitterMarketEvidenceBook book, CancellationToken cancellationToken);
}
