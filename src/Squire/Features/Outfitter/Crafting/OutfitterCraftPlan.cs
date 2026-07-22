using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Crafting;

internal enum OutfitterCraftNodeKind
{
    Craft,
    Material,
}

internal enum OutfitterCraftDiagnosticCode
{
    CircularRecipe,
    AmbiguousRecipe,
    MaximumDepthExceeded,
    IncompleteMaterialCoverage,
    IneligibleCrafter,
    UnprovenCrafter,
    MasterRecipe,
    HqOutcomeUnproven,
    ArithmeticOverflow,
    InvalidIdentity,
}

internal sealed record OutfitterCraftDiagnostic(OutfitterCraftDiagnosticCode Code, string Message, string? NodeId = null);

internal enum OutfitterCraftEligibilityState
{
    ProvenEligible,
    ProvenIneligible,
    Unproven,
}

/// <summary>Baseline-derived active-job authority for one exact recipe node.</summary>
internal sealed record OutfitterCraftEligibilityEvidence(
    OutfitterCraftEligibilityState State,
    string CrafterAuthorityFingerprint,
    CharacterScope? Character,
    string NodeId,
    uint RecipeId,
    uint RequiredClassJobId,
    int RequiredLevel,
    uint ObservedClassJobId,
    int ObservedLevel,
    string? Diagnostic = null);

internal sealed record OutfitterCrafterObservationIdentity
{
    private OutfitterCrafterObservationIdentity(
        string baselineAuthorityFingerprint,
        CharacterScope character,
        uint classJobId,
        int actualLevel,
        int effectiveLevel,
        bool isLevelSynced,
        Guid captureId,
        DateTimeOffset capturedAtUtc,
        Guid equipmentGenerationId,
        DateTimeOffset equipmentIdentityCapturedAtUtc,
        string equipmentSnapshotFingerprint)
    {
        BaselineAuthorityFingerprint = baselineAuthorityFingerprint;
        Character = character;
        ClassJobId = classJobId;
        ActualLevel = actualLevel;
        EffectiveLevel = effectiveLevel;
        IsLevelSynced = isLevelSynced;
        CaptureId = captureId;
        CapturedAtUtc = capturedAtUtc;
        EquipmentGenerationId = equipmentGenerationId;
        EquipmentIdentityCapturedAtUtc = equipmentIdentityCapturedAtUtc;
        EquipmentSnapshotFingerprint = equipmentSnapshotFingerprint;
    }

    public string BaselineAuthorityFingerprint { get; }
    public CharacterScope Character { get; }
    public uint ClassJobId { get; }
    public int ActualLevel { get; }
    public int EffectiveLevel { get; }
    public bool IsLevelSynced { get; }
    public Guid CaptureId { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public Guid EquipmentGenerationId { get; }
    public DateTimeOffset EquipmentIdentityCapturedAtUtc { get; }
    public string EquipmentSnapshotFingerprint { get; }

    public static OutfitterCrafterObservationIdentity FromBaseline(
        PlayerAdvisorBaseline baseline,
        DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (!PlayerAdvisorBaselineAssembler.IsCompleteAndConsistent(
                baseline,
                CrafterAdvisorStatFamily.Instance,
                out var baselineDiagnostic) ||
            baseline is not
            {
                Character: { } character,
                ClassJobId: { } classJobId,
                Level: { } level,
                EffectiveLevel: { } effectiveLevel,
                IsLevelSynced: { } isLevelSynced,
                CaptureProvenance: { } provenance,
            })
        {
            throw new InvalidOperationException(
                $"Craft planning requires an assembled, complete active-crafter player baseline: {baselineDiagnostic}");
        }
        if (asOfUtc == default ||
            provenance.CompletedAtUtc > asOfUtc ||
            provenance.EquipmentIdentityCapturedAtUtc > provenance.CompletedAtUtc ||
            asOfUtc - provenance.CompletedAtUtc > PlayerAdvisorCaptureFreshness.TimeToLive ||
            asOfUtc - provenance.EquipmentIdentityCapturedAtUtc > PlayerAdvisorCaptureFreshness.TimeToLive)
        {
            throw new InvalidOperationException("Craft planning requires a fresh trusted player baseline capture.");
        }
        if (!CraftEquipmentSnapshotIdentity.TryCompute(baseline.EquipmentSnapshot, out var equipmentSnapshotFingerprint))
            throw new InvalidOperationException("Craft planning requires one canonical trusted equipment snapshot.");

        var canonical = new StringBuilder()
            .Append(character.LocalContentId.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(character.HomeWorldId.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(classJobId.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(level.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(effectiveLevel.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(isLevelSynced ? '1' : '0');
        AppendCanonicalString(canonical, character.Name);
        foreach (var stat in baseline.TotalStats.OrderBy(value => value.Key))
            canonical.Append('|')
                .Append(((int)stat.Key).ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(stat.Value.ToString(CultureInfo.InvariantCulture));
        foreach (var slot in baseline.EquippedSlots.OrderBy(value => value.Position))
        {
            canonical.Append('|')
                .Append(((int)slot.Position).ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append((slot.Definition?.ItemId ?? 0).ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append((slot.Quality is { } quality ? (int)quality : -1).ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < slot.MateriaIds.Count; index++)
            {
                canonical.Append(':')
                    .Append(index.ToString(CultureInfo.InvariantCulture)).Append('=')
                    .Append(slot.MateriaIds[index].ToString(CultureInfo.InvariantCulture)).Append('@')
                    .Append(slot.MateriaGrades[index].ToString(CultureInfo.InvariantCulture));
            }
        }
        return new(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))),
            character,
            classJobId,
            level,
            effectiveLevel,
            isLevelSynced,
            provenance.CaptureId,
            provenance.CompletedAtUtc,
            provenance.EquipmentGenerationId,
            provenance.EquipmentIdentityCapturedAtUtc,
            equipmentSnapshotFingerprint);
    }

    public bool MatchesCurrentBaseline(PlayerAdvisorBaseline baseline, DateTimeOffset asOfUtc)
    {
        try
        {
            var current = FromBaseline(baseline, asOfUtc);
            return string.Equals(BaselineAuthorityFingerprint, current.BaselineAuthorityFingerprint, StringComparison.Ordinal) &&
                Character == current.Character &&
                ClassJobId == current.ClassJobId &&
                ActualLevel == current.ActualLevel &&
                EffectiveLevel == current.EffectiveLevel &&
                IsLevelSynced == current.IsLevelSynced;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void AppendCanonicalString(StringBuilder canonical, string value) =>
        canonical.Append('|')
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
}

internal static class PlayerAdvisorCaptureFreshness
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(1);
}

internal static class CraftEquipmentSnapshotIdentity
{
    public static bool TryCompute(CharacterEquipmentSnapshot? snapshot, out string identity)
    {
        identity = string.Empty;
        if (snapshot is not
            {
                GenerationId: var generationId,
                Identity: { Scope: { } character } captureIdentity,
                Instances: { } instances,
            } ||
            generationId == Guid.Empty ||
            character.LocalContentId == 0 ||
            character.HomeWorldId == 0 ||
            string.IsNullOrWhiteSpace(character.Name) ||
            captureIdentity.CurrentWorldId is null or 0 ||
            captureIdentity.ActiveClassJobId is null or 0 ||
            captureIdentity.CapturedAt == default ||
            !captureIdentity.IsLoggedIn ||
            captureIdentity.Status != SnapshotComponentStatus.Complete ||
            instances.Any(instance => instance?.Fingerprint is null) ||
            instances.GroupBy(instance => (
                    instance.Fingerprint.Character,
                    instance.Fingerprint.Container,
                    instance.Fingerprint.SlotIndex))
                .Any(group => group.Count() != 1))
        {
            return false;
        }

        var canonical = new StringBuilder()
            .Append(generationId).Append('|')
            .AppendInvariant(character.LocalContentId).Append('|')
            .AppendInvariant(character.HomeWorldId);
        AppendString(canonical, character.Name);
        canonical.Append('|').AppendInvariant(captureIdentity.CurrentWorldId.Value)
            .Append('|').AppendInvariant(captureIdentity.ActiveClassJobId.Value)
            .Append('|').AppendInvariant(captureIdentity.CapturedAt.UtcDateTime.Ticks);

        foreach (var instance in instances
                     .OrderBy(value => value.Fingerprint.Container, StringComparer.Ordinal)
                     .ThenBy(value => value.Fingerprint.SlotIndex)
                     .ThenBy(value => value.Fingerprint.ItemId)
                     .ThenBy(value => value.Fingerprint.IsHighQuality)
                     .ThenBy(value => value.Fingerprint.Quantity))
        {
            var fingerprint = instance.Fingerprint;
            if (fingerprint.Character != character ||
                string.IsNullOrWhiteSpace(fingerprint.Container) ||
                fingerprint.SlotIndex < 0 ||
                fingerprint.ItemId == 0 ||
                fingerprint.Quantity == 0 ||
                instance.CapturedAt != captureIdentity.CapturedAt)
            {
                return false;
            }

            canonical.Append("|instance|").AppendInvariant(fingerprint.Character.LocalContentId)
                .Append('|').AppendInvariant(fingerprint.Character.HomeWorldId);
            AppendString(canonical, fingerprint.Character.Name);
            AppendString(canonical, fingerprint.Container);
            canonical.Append('|').AppendInvariant(fingerprint.SlotIndex)
                .Append('|').AppendInvariant(fingerprint.ItemId)
                .Append('|').AppendInvariant(fingerprint.IsHighQuality ? 1 : 0)
                .Append('|').AppendInvariant(fingerprint.Quantity)
                .Append('|').AppendInvariant(instance.CapturedAt.UtcDateTime.Ticks)
                .Append('|').AppendInvariant(instance.IsEquipped ? 1 : 0);
        }

        identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return true;
    }

    private static void AppendString(StringBuilder canonical, string value) =>
        canonical.Append('|').AppendInvariant(value.Length).Append(':').Append(value);
}

/// <summary>
/// Future verifier attestation for a requested HQ outcome. This opaque record is lineage only and
/// deliberately does not make a plan economy-ready until a trusted verifier is implemented.
/// </summary>
internal sealed record OutfitterCraftHqCapabilityAttestation(
    string AttestationId,
    string ModelId,
    string ModelVersion,
    string CrafterAuthorityFingerprint,
    DateTimeOffset AttestedAtUtc,
    string NodeId,
    uint RecipeId,
    uint ItemId,
    EquipmentQuality Quality,
    uint Quantity);

internal sealed record OutfitterResolvedRecipeIngredient(
    string ChildNodeId,
    uint ItemId,
    EquipmentQuality Quality,
    uint QuantityPerCraft,
    string ItemName = "");

/// <summary>
/// Immutable static recipe resolution captured by a future trusted resolver. The expanded tree
/// must match this complete snapshot exactly; the contract never re-resolves recipes itself.
/// </summary>
internal sealed record OutfitterResolvedRecipeSnapshot(
    string ResolverId,
    string ResolverVersion,
    string DefinitionFingerprint,
    uint RecipeId,
    uint OutputItemId,
    uint OutputQuantity,
    uint RequiredClassJobId,
    int RequiredLevel,
    uint RecipeUnlockItemId,
    ImmutableArray<OutfitterResolvedRecipeIngredient> Ingredients,
    string OutputItemName = "");

internal sealed record OutfitterCraftNode(
    string NodeId,
    string? ParentNodeId,
    OutfitterCraftNodeKind Kind,
    uint ItemId,
    EquipmentQuality Quality,
    uint RequiredQuantity,
    uint QuantityPerParentCraft = 0,
    uint RecipeId = 0,
    uint RecipeOutputQuantity = 0,
    uint RecipeUnlockItemId = 0,
    OutfitterResolvedRecipeSnapshot? ResolvedRecipe = null,
    OutfitterCraftEligibilityEvidence? Eligibility = null,
    OutfitterCraftHqCapabilityAttestation? HqCapabilityAttestation = null);

internal enum OutfitterMaterialSourceKind
{
    MarketListing,
    GilVendor,
}

internal static class CraftMarketEvidenceFreshness
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(15);

    public static bool IsFresh(
        DateTimeOffset reviewedAtUtc,
        DateTimeOffset capturedAtUtc,
        DateTimeOffset publishedAtUtc,
        DateTimeOffset asOfUtc) =>
        reviewedAtUtc != default &&
        capturedAtUtc != default &&
        publishedAtUtc != default &&
        asOfUtc != default &&
        reviewedAtUtc <= capturedAtUtc &&
        capturedAtUtc <= publishedAtUtc &&
        publishedAtUtc <= asOfUtc &&
        asOfUtc - reviewedAtUtc <= TimeToLive;

    public static bool IsFresh(OutfitterMarketEvidenceBook book, DateTimeOffset asOfUtc) =>
        book.IsPublishable &&
        book.PublishedAtUtc is { } publishedAtUtc &&
        publishedAtUtc <= asOfUtc &&
        asOfUtc - publishedAtUtc <= TimeToLive;
}

internal sealed record CraftMarketItemSourceRevision(
    uint ItemId,
    DateTimeOffset CapturedAtUtc,
    string SourceRevision);

internal sealed record CraftMarketListingIdentity(
    uint ItemId,
    EquipmentQuality Quality,
    string ListingId,
    uint WorldId,
    string WorldName,
    uint AvailableQuantity,
    uint UnitPriceGil,
    DateTimeOffset ReviewedAtUtc,
    DateTimeOffset CapturedAtUtc,
    string SourceRevision);

/// <summary>Canonical identity of one complete published market-evidence book.</summary>
internal sealed record CraftMarketEvidenceReference
{
    private CraftMarketEvidenceReference(
        Guid generationId,
        long revision,
        string schemaVersion,
        string sourceKey,
        string region,
        DateTimeOffset createdAtUtc,
        DateTimeOffset publishedAtUtc,
        OutfitterMarketCoverageMode coverageMode,
        int catalogItemCount,
        int queriedItemCount,
        int listingLimit,
        ImmutableArray<uint> queriedItemIds,
        ImmutableArray<CraftMarketItemSourceRevision> itemSourceRevisions,
        ImmutableArray<CraftMarketListingIdentity> listings,
        string contentIdentity)
    {
        GenerationId = generationId;
        Revision = revision;
        SchemaVersion = schemaVersion;
        SourceKey = sourceKey;
        Region = region;
        CreatedAtUtc = createdAtUtc;
        PublishedAtUtc = publishedAtUtc;
        CoverageMode = coverageMode;
        CatalogItemCount = catalogItemCount;
        QueriedItemCount = queriedItemCount;
        ListingLimit = listingLimit;
        QueriedItemIds = queriedItemIds;
        ItemSourceRevisions = itemSourceRevisions;
        Listings = listings;
        ContentIdentity = contentIdentity;
    }

    public Guid GenerationId { get; }
    public long Revision { get; }
    public string SchemaVersion { get; }
    public string SourceKey { get; }
    public string Region { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public OutfitterMarketCoverageMode CoverageMode { get; }
    public int CatalogItemCount { get; }
    public int QueriedItemCount { get; }
    public int ListingLimit { get; }
    public ImmutableArray<uint> QueriedItemIds { get; }
    public ImmutableArray<CraftMarketItemSourceRevision> ItemSourceRevisions { get; }
    public ImmutableArray<CraftMarketListingIdentity> Listings { get; }
    public string ContentIdentity { get; }

    public static CraftMarketEvidenceReference FromPublishedBook(OutfitterMarketEvidenceBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var publishedAtUtc = book.PublishedAtUtc;
        var coverage = book.Coverage;
        if (publishedAtUtc is null ||
            book.GenerationId == Guid.Empty ||
            book.Revision <= 0 ||
            book.SchemaVersion != OutfitterMarketEvidenceBook.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(book.SourceKey) ||
            string.IsNullOrWhiteSpace(book.Region) ||
            book.CreatedAtUtc == default ||
            book.CreatedAtUtc > publishedAtUtc ||
            coverage is null ||
            !Enum.IsDefined(coverage.Mode) ||
            coverage.CatalogItemCount <= 0 ||
            coverage.QueriedItemCount <= 0 ||
            coverage.ListingLimit is < 1 or > 100 ||
            coverage.QueriedItemIds is null ||
            book.Items is null ||
            book.Status != OutfitterMarketEvidenceGenerationStatus.Complete ||
            coverage.CatalogItemCount < coverage.QueriedItemCount ||
            (coverage.Mode == OutfitterMarketCoverageMode.ExhaustiveWithinScope &&
                coverage.CatalogItemCount != coverage.QueriedItemCount))
        {
            throw new InvalidOperationException("Craft evidence identity requires one complete published evidence book.");
        }

        if (coverage.QueriedItemIds.Count != coverage.QueriedItemCount ||
            book.Items.Count != coverage.QueriedItemCount)
        {
            throw new InvalidOperationException("Published craft evidence exceeds its authoritative queried-item bounds.");
        }

        var queriedItemInputs = CopyBounded(coverage.QueriedItemIds, coverage.QueriedItemCount);
        var itemInputs = new OutfitterMarketItemEvidence[coverage.QueriedItemCount];
        for (var index = 0; index < itemInputs.Length; index++)
        {
            var item = book.Items[index];
            if (item is null || item.Listings is null)
            {
                throw new InvalidOperationException("Published craft evidence exceeds its authoritative item or listing bounds.");
            }

            var listingCount = item.Listings.Count;
            if (listingCount > coverage.ListingLimit)
                throw new InvalidOperationException("Published craft evidence exceeds its authoritative item or listing bounds.");

            itemInputs[index] = item with
            {
                Listings = CopyBounded(item.Listings, listingCount),
            };
        }

        var queriedItemIds = queriedItemInputs.Order().ToImmutableArray();
        var items = itemInputs.OrderBy(item => item.ItemId).ToArray();
        if (queriedItemIds.Any(itemId => itemId == 0) ||
            queriedItemIds.Distinct().Count() != coverage.QueriedItemCount ||
            items.Select(item => item.ItemId).Distinct().Count() != items.Length ||
            !items.Select(item => item.ItemId).SequenceEqual(queriedItemIds))
        {
            throw new InvalidOperationException("Published craft evidence coverage does not exactly identify its item evidence.");
        }

        foreach (var item in items)
            ValidatePublishedItem(item, publishedAtUtc.Value);
        if (items
            .SelectMany(item => item.Listings)
            .GroupBy(listing => (listing.WorldId, listing.ListingId))
            .Any(group => group.Count() != 1))
        {
            throw new InvalidOperationException("Published craft evidence contains a conflicting physical market-listing identity.");
        }

        var sourceRevisions = items
            .Select(item => new CraftMarketItemSourceRevision(item.ItemId, item.CapturedAtUtc, item.SourceRevision))
            .ToImmutableArray();
        var listings = items
            .SelectMany(item => item.Listings)
            .OrderBy(listing => listing.ItemId)
            .ThenBy(listing => listing.WorldId)
            .ThenBy(listing => listing.ListingId, StringComparer.Ordinal)
            .ThenBy(listing => listing.Quality)
            .Select(listing => new CraftMarketListingIdentity(
                listing.ItemId,
                listing.Quality,
                listing.ListingId,
                listing.WorldId,
                listing.WorldName,
                listing.Quantity,
                listing.UnitPriceGil,
                listing.ListingReviewedAtUtc,
                listing.CapturedAtUtc,
                listing.SourceRevision))
            .ToImmutableArray();
        return new(
            book.GenerationId,
            book.Revision,
            book.SchemaVersion,
            book.SourceKey,
            book.Region,
            book.CreatedAtUtc,
            publishedAtUtc.Value,
            coverage.Mode,
            coverage.CatalogItemCount,
            coverage.QueriedItemCount,
            coverage.ListingLimit,
            queriedItemIds,
            sourceRevisions,
            listings,
            ComputeContentIdentity(book, queriedItemIds, items, publishedAtUtc.Value));
    }

    private static T[] CopyBounded<T>(IReadOnlyList<T> values, int count)
    {
        var copy = new T[count];
        for (var index = 0; index < count; index++)
            copy[index] = values[index];
        return copy;
    }

    public bool MatchesItemSource(uint itemId, DateTimeOffset capturedAtUtc, string sourceRevision) =>
        ItemSourceRevisions.Any(item =>
            item.ItemId == itemId &&
            item.CapturedAtUtc == capturedAtUtc &&
            string.Equals(item.SourceRevision, sourceRevision, StringComparison.Ordinal));

    public bool MatchesListing(
        uint itemId,
        EquipmentQuality quality,
        string listingId,
        uint worldId,
        string worldName,
        uint availableQuantity,
        uint unitPriceGil,
        DateTimeOffset reviewedAtUtc,
        DateTimeOffset capturedAtUtc,
        string sourceRevision) =>
        Listings.Any(listing =>
            listing.ItemId == itemId &&
            listing.Quality == quality &&
            string.Equals(listing.ListingId, listingId, StringComparison.Ordinal) &&
            listing.WorldId == worldId &&
            string.Equals(listing.WorldName, worldName, StringComparison.Ordinal) &&
            listing.AvailableQuantity == availableQuantity &&
            listing.UnitPriceGil == unitPriceGil &&
            listing.ReviewedAtUtc == reviewedAtUtc &&
            listing.CapturedAtUtc == capturedAtUtc &&
            string.Equals(listing.SourceRevision, sourceRevision, StringComparison.Ordinal));

    private static void ValidatePublishedItem(OutfitterMarketItemEvidence item, DateTimeOffset publishedAtUtc)
    {
        if (item.ItemId == 0 ||
            item.Status is not (OutfitterMarketEvidenceItemStatus.Fresh or OutfitterMarketEvidenceItemStatus.Missing) ||
            item.Listings is null ||
            item.CapturedAtUtc == default ||
            item.CapturedAtUtc > publishedAtUtc ||
            publishedAtUtc - item.CapturedAtUtc > CraftMarketEvidenceFreshness.TimeToLive ||
            string.IsNullOrWhiteSpace(item.SourceRevision) ||
            (item.Status == OutfitterMarketEvidenceItemStatus.Fresh) != (item.Listings.Count > 0))
        {
            throw new InvalidOperationException($"Published craft evidence item {item.ItemId} is incomplete or stale.");
        }

        var listingIds = new HashSet<(uint WorldId, string ListingId)>();
        foreach (var listing in item.Listings)
        {
            if (listing is null ||
                listing.ItemId != item.ItemId ||
                listing.Quality is not (EquipmentQuality.Normal or EquipmentQuality.High) ||
                string.IsNullOrWhiteSpace(listing.ListingId) ||
                !listingIds.Add((listing.WorldId, listing.ListingId)) ||
                listing.WorldId == 0 ||
                string.IsNullOrWhiteSpace(listing.WorldName) ||
                listing.Quantity == 0 ||
                listing.UnitPriceGil == 0 ||
                listing.CapturedAtUtc != item.CapturedAtUtc ||
                !string.Equals(listing.SourceRevision, item.SourceRevision, StringComparison.Ordinal) ||
                listing.ListingReviewedAtUtc == default ||
                listing.ListingReviewedAtUtc > listing.CapturedAtUtc)
            {
                throw new InvalidOperationException($"Published craft evidence item {item.ItemId} contains an invalid listing lineage.");
            }
        }
    }

    private static string ComputeContentIdentity(
        OutfitterMarketEvidenceBook book,
        ImmutableArray<uint> queriedItemIds,
        IReadOnlyList<OutfitterMarketItemEvidence> items,
        DateTimeOffset publishedAtUtc)
    {
        var canonical = new StringBuilder();
        canonical.Append(book.GenerationId).Append('|').AppendInvariant(book.Revision);
        AppendCanonicalString(canonical, book.SchemaVersion);
        AppendCanonicalString(canonical, book.SourceKey);
        AppendCanonicalString(canonical, book.Region);
        canonical.Append('|').AppendInvariant(book.CreatedAtUtc.UtcDateTime.Ticks)
            .Append('|').AppendInvariant(publishedAtUtc.UtcDateTime.Ticks)
            .Append('|').AppendInvariant((int)book.Status)
            .Append('|').AppendInvariant((int)book.Coverage.Mode)
            .Append('|').AppendInvariant(book.Coverage.CatalogItemCount)
            .Append('|').AppendInvariant(book.Coverage.QueriedItemCount)
            .Append('|').AppendInvariant(book.Coverage.ListingLimit);
        foreach (var itemId in queriedItemIds)
            canonical.Append('|').AppendInvariant(itemId);
        foreach (var item in items)
        {
            canonical.Append("|item|").AppendInvariant(item.ItemId)
                .Append('|').AppendInvariant((int)item.Status)
                .Append('|').AppendInvariant(item.CapturedAtUtc.UtcDateTime.Ticks);
            AppendCanonicalString(canonical, item.SourceRevision);
            AppendCanonicalString(canonical, item.Diagnostic);
            canonical.Append('|').AppendInvariant(item.RetryAfterUtc?.UtcDateTime.Ticks ?? 0);
            foreach (var listing in item.Listings
                         .OrderBy(listing => listing.WorldId)
                         .ThenBy(listing => listing.ListingId, StringComparer.Ordinal)
                         .ThenBy(listing => listing.Quality))
            {
                canonical.Append("|listing|").AppendInvariant(listing.ItemId).Append('|').AppendInvariant((int)listing.Quality);
                AppendCanonicalString(canonical, listing.ListingId);
                canonical.Append('|').AppendInvariant(listing.WorldId);
                AppendCanonicalString(canonical, listing.WorldName);
                AppendCanonicalString(canonical, listing.RetainerId);
                AppendCanonicalString(canonical, listing.RetainerName);
                canonical.Append('|').AppendInvariant(listing.Quantity)
                    .Append('|').AppendInvariant(listing.UnitPriceGil)
                    .Append('|').AppendInvariant(listing.ListingReviewedAtUtc.UtcDateTime.Ticks)
                    .Append('|').AppendInvariant(listing.CapturedAtUtc.UtcDateTime.Ticks);
                AppendCanonicalString(canonical, listing.SourceRevision);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendCanonicalString(StringBuilder canonical, string? value)
    {
        canonical.Append('|');
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }
        canonical.AppendInvariant(value.Length).Append(':').Append(value);
    }
}

internal abstract record OutfitterMaterialSourceIdentity(
    uint ItemId,
    EquipmentQuality Quality,
    uint UnitPriceGil)
{
    public abstract OutfitterMaterialSourceKind Kind { get; }
}

internal sealed record OutfitterMarketMaterialSourceIdentity(
    uint ItemId,
    EquipmentQuality Quality,
    uint UnitPriceGil,
    uint AvailableQuantity,
    string ListingId,
    uint WorldId,
    string WorldName,
    DateTimeOffset ReviewedAtUtc,
    DateTimeOffset CapturedAtUtc,
    string SourceRevision,
    Guid EvidenceGenerationId,
    long EvidenceRevision)
    : OutfitterMaterialSourceIdentity(ItemId, Quality, UnitPriceGil)
{
    public override OutfitterMaterialSourceKind Kind => OutfitterMaterialSourceKind.MarketListing;
    public OutfitterMarketPhysicalSourceKey PhysicalSourceKey => new(EvidenceGenerationId, EvidenceRevision, WorldId, ListingId);
}

internal readonly record struct OutfitterMarketPhysicalSourceKey(
    Guid EvidenceGenerationId,
    long EvidenceRevision,
    uint WorldId,
    string ListingId);

internal sealed record OutfitterGilVendorMaterialSourceIdentity : OutfitterMaterialSourceIdentity
{
    private readonly OutfitterGilVendorCatalogReference catalog;

    private OutfitterGilVendorMaterialSourceIdentity(
        OutfitterGilVendorCatalogReference catalog,
        OutfitterGilVendorOffer offer)
        : base(offer.ItemId, EquipmentQuality.Normal, offer.UnitPriceGil)
    {
        this.catalog = catalog;
        ShopId = offer.ShopId;
        VendorId = offer.VendorId;
        TerritoryId = offer.TerritoryId;
        VendorName = offer.VendorName;
        TerritoryName = offer.TerritoryName;
        CatalogVersion = catalog.CatalogVersion;
    }

    public override OutfitterMaterialSourceKind Kind => OutfitterMaterialSourceKind.GilVendor;
    public uint ShopId { get; }
    public uint VendorId { get; }
    public uint TerritoryId { get; }
    public string VendorName { get; }
    public string TerritoryName { get; }
    public string CatalogVersion { get; }
    public OutfitterGilVendorPhysicalSourceKey PhysicalSourceKey => new(ShopId, VendorId, TerritoryId);

    internal static OutfitterGilVendorMaterialSourceIdentity FromCatalog(
        OutfitterGilVendorCatalog vendorCatalog,
        OutfitterGilVendorOffer offer)
    {
        ArgumentNullException.ThrowIfNull(vendorCatalog);
        ArgumentNullException.ThrowIfNull(offer);
        var catalog = vendorCatalog.CaptureReference();
        if (!catalog.Matches(offer))
            throw new InvalidOperationException("Gil-vendor material identity requires exact catalog membership.");
        return new(catalog, offer);
    }

    internal bool HasExactCatalogMembership() => catalog is not null &&
        string.Equals(CatalogVersion, catalog.CatalogVersion, StringComparison.Ordinal) &&
        catalog.Matches(new(
            ItemId,
            ShopId,
            VendorId,
            VendorName,
            TerritoryId,
            TerritoryName,
            UnitPriceGil));
}

internal readonly record struct OutfitterGilVendorPhysicalSourceKey(uint ShopId, uint VendorId, uint TerritoryId);

internal sealed record OutfitterTerminalMaterialLine(
    string MaterialKey,
    uint ItemId,
    EquipmentQuality Quality,
    uint ConsumedQuantity,
    uint PurchasedQuantity,
    uint SurplusQuantity,
    OutfitterMaterialSourceIdentity Source);

internal sealed record OutfitterCraftPlanIdentity(string Sha256)
{
    public override string ToString() => Sha256;
}

internal sealed record OutfitterCraftPlanValidation(bool IsValid, ImmutableArray<string> Errors)
{
    public static OutfitterCraftPlanValidation Valid { get; } = new(true, ImmutableArray<string>.Empty);
}

/// <summary>
/// Immutable, fully expanded recipe tree. Consumers must use ExpandedNodes and must never expand recipes again.
/// This contract describes craft-cost evidence; it is not a solver offer, Workbench contract, or purchase authority.
/// </summary>
[JsonConverter(typeof(ContractOnlyCraftPlanJsonConverter))]
[Newtonsoft.Json.JsonConverter(typeof(NewtonsoftContractOnlyCraftPlanJsonConverter))]
internal sealed record OutfitterCraftPlan(
    string SchemaVersion,
    string PlanId,
    uint GearItemId,
    EquipmentQuality GearQuality,
    uint GearQuantity,
    string RootNodeId,
    OutfitterCrafterObservationIdentity CrafterObservation,
    int MaximumDepth,
    int MaximumExpandedNodeCount,
    ImmutableArray<OutfitterCraftNode> ExpandedNodes,
    ImmutableArray<OutfitterTerminalMaterialLine> TerminalMaterials,
    CraftMarketEvidenceReference? MarketEvidence,
    DateTimeOffset BuiltAtUtc,
    ImmutableArray<OutfitterCraftDiagnostic> Diagnostics)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-outfitter-craft-plan/v4";
    public OutfitterCraftPlanValidation Validate(bool requireEconomyReady = false)
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            errors.Add("Unsupported craft-plan schema version.");
        if (string.IsNullOrWhiteSpace(PlanId) || GearItemId == 0 || GearQuantity == 0 || string.IsNullOrWhiteSpace(RootNodeId))
            errors.Add("Plan, gear, root, and quantity identity must be complete.");
        if (!IsExactQuality(GearQuality))
            errors.Add("Gear quality must be exact NQ or HQ.");
        if (MaximumDepth is < 1 or > 64)
            errors.Add("Maximum recipe depth must be between 1 and 64.");
        if (MaximumExpandedNodeCount is < 1 or > OutfitterExactRecipeGraph.MaximumAllowedExpandedNodeCount)
            errors.Add($"Maximum expanded node count must be between 1 and {OutfitterExactRecipeGraph.MaximumAllowedExpandedNodeCount}.");
        if (BuiltAtUtc == default)
            errors.Add("Craft plans require an explicit non-default build time.");
        var crafterObservationValid = ValidCrafterObservation(CrafterObservation);
        if (!crafterObservationValid)
            errors.Add("Craft plans require one complete baseline-derived crafter authority identity.");
        else if (CrafterObservation.CapturedAtUtc > BuiltAtUtc ||
                  CrafterObservation.EquipmentIdentityCapturedAtUtc > CrafterObservation.CapturedAtUtc ||
                  BuiltAtUtc - CrafterObservation.CapturedAtUtc > PlayerAdvisorCaptureFreshness.TimeToLive ||
                  BuiltAtUtc - CrafterObservation.EquipmentIdentityCapturedAtUtc > PlayerAdvisorCaptureFreshness.TimeToLive)
            errors.Add("Craft plans require fresh baseline-derived crafter authority.");
        if (ExpandedNodes.IsDefaultOrEmpty)
            errors.Add("The expanded recipe tree is empty.");
        else if (ExpandedNodes.Length > MaximumExpandedNodeCount ||
                 ExpandedNodes.Length > OutfitterExactRecipeGraph.MaximumAllowedExpandedNodeCount)
        {
            errors.Add("The expanded recipe tree exceeds its frozen node-count limit.");
            return Invalid(errors);
        }
        if (TerminalMaterials.IsDefault)
            errors.Add("Terminal material lines must be initialized.");
        if (Diagnostics.IsDefault)
            errors.Add("Craft diagnostics must be initialized.");
        if (ExpandedNodes.IsDefault || TerminalMaterials.IsDefault || Diagnostics.IsDefault)
            return Invalid(errors);
        if (ExpandedNodes.Any(node => node is null) ||
            TerminalMaterials.Any(line => line is null) ||
            Diagnostics.Any(diagnostic => diagnostic is null))
        {
            errors.Add("Craft-plan collections cannot contain null records.");
            return Invalid(errors);
        }
        if (Diagnostics.Any(diagnostic =>
                !Enum.IsDefined(diagnostic.Code) || string.IsNullOrWhiteSpace(diagnostic.Message)))
        {
            errors.Add("Craft diagnostics contain an unsupported code or empty message.");
        }
        if (!crafterObservationValid)
            return Invalid(errors);

        var duplicateNodeIds = ExpandedNodes
            .GroupBy(node => node.NodeId, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateNodeIds.Length != 0 || ExpandedNodes.Any(node => string.IsNullOrWhiteSpace(node.NodeId)))
            errors.Add("Expanded node identity is ambiguous.");

        var nodes = ExpandedNodes
            .Where(node => !string.IsNullOrWhiteSpace(node.NodeId))
            .GroupBy(node => node.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var childrenByParent = ExpandedNodes
            .Where(node => node.ParentNodeId is not null)
            .GroupBy(node => node.ParentNodeId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        if (!nodes.TryGetValue(RootNodeId, out var root) ||
            root.ParentNodeId is not null ||
            root.Kind != OutfitterCraftNodeKind.Craft ||
            root.ItemId != GearItemId ||
            root.Quality != GearQuality ||
            root.RequiredQuantity != GearQuantity ||
            root.QuantityPerParentCraft != 0)
        {
            errors.Add("The root node must exactly identify the requested gear and quantity.");
        }

        foreach (var node in ExpandedNodes)
        {
            if (!Enum.IsDefined(node.Kind))
            {
                errors.Add($"Node '{node.NodeId}' has an unsupported node kind.");
                continue;
            }
            if (node.ItemId == 0 || node.RequiredQuantity == 0 || !IsExactQuality(node.Quality))
                errors.Add($"Node '{node.NodeId}' has incomplete item, quality, or quantity identity.");
            if (node.NodeId != RootNodeId &&
                (node.ParentNodeId is null || !nodes.ContainsKey(node.ParentNodeId) || node.QuantityPerParentCraft == 0))
            {
                errors.Add($"Node '{node.NodeId}' is disconnected from the expanded tree or lacks parent quantity identity.");
            }

            var children = childrenByParent.GetValueOrDefault(node.NodeId) ?? [];
            switch (node.Kind)
            {
                case OutfitterCraftNodeKind.Craft:
                    if (node.RecipeId == 0 || node.RecipeOutputQuantity == 0 || node.ResolvedRecipe is null || node.Eligibility is null)
                        errors.Add($"Craft node '{node.NodeId}' lacks recipe, frozen resolution, yield, or eligibility identity.");
                    if (children.Length == 0)
                        errors.Add($"Craft node '{node.NodeId}' has no expanded ingredients.");
                    if (node.ResolvedRecipe is not null)
                        ValidateResolvedRecipe(node, children, errors);
                    if (node.Eligibility is not null)
                        ValidateEligibility(node, errors);
                    ValidateHqAttestation(node, errors);
                    break;
                case OutfitterCraftNodeKind.Material:
                    if (node.RecipeId != 0 || node.RecipeOutputQuantity != 0 || node.RecipeUnlockItemId != 0 ||
                        node.ResolvedRecipe is not null || node.Eligibility is not null || node.HqCapabilityAttestation is not null || children.Length != 0)
                    {
                        errors.Add($"Material node '{node.NodeId}' cannot carry recipe, resolution, eligibility, proof, or child identity.");
                    }
                    break;
            }
        }

        ValidateTreeDepthAndCycles(nodes, errors);
        ValidateExpandedQuantities(nodes, errors);
        ValidateTerminalCoverage(errors);

        var blockingExpansionDiagnostics = Diagnostics.Any(diagnostic => diagnostic.Code is
            OutfitterCraftDiagnosticCode.CircularRecipe or
            OutfitterCraftDiagnosticCode.AmbiguousRecipe or
            OutfitterCraftDiagnosticCode.MaximumDepthExceeded or
            OutfitterCraftDiagnosticCode.IncompleteMaterialCoverage or
            OutfitterCraftDiagnosticCode.ArithmeticOverflow or
            OutfitterCraftDiagnosticCode.InvalidIdentity);
        if (blockingExpansionDiagnostics)
            errors.Add("Expansion diagnostics make the plan structurally invalid.");

        if (requireEconomyReady)
        {
            if (Diagnostics.Length != 0)
                errors.Add("Economy-ready plans cannot retain unresolved diagnostics.");
            if (ExpandedNodes.Any(node => node.Kind == OutfitterCraftNodeKind.Craft &&
                (node.Eligibility?.State != OutfitterCraftEligibilityState.ProvenEligible || node.RecipeUnlockItemId != 0)))
            {
                errors.Add("Economy-ready plans require proven active-job eligibility for every non-master recipe node.");
            }
            if (CrafterObservation.IsLevelSynced || CrafterObservation.EffectiveLevel != CrafterObservation.ActualLevel)
                errors.Add("Economy-ready plans cannot use a level-synchronized crafter authority.");
            if (ExpandedNodes.Any(node => node.Kind == OutfitterCraftNodeKind.Craft &&
                node.Quality == EquipmentQuality.High))
            {
                errors.Add("HQ craft outcomes remain display-only until a trusted capability verifier is implemented.");
            }
        }

        return errors.Count == 0 ? OutfitterCraftPlanValidation.Valid : Invalid(errors);
    }

    public static string MaterialKey(uint itemId, EquipmentQuality quality) =>
        string.Create(CultureInfo.InvariantCulture, $"{itemId}:{(int)quality}");

    public bool RevalidateCrafterAuthority(PlayerAdvisorBaseline currentBaseline, DateTimeOffset asOfUtc) =>
        CrafterObservation.MatchesCurrentBaseline(currentBaseline, asOfUtc);

    public OutfitterCraftPlanIdentity ComputeStructuralIdentity()
    {
        var canonical = new StringBuilder();
        AppendString(canonical, CurrentSchemaVersion);
        canonical.Append('|').AppendInvariant(GearItemId)
            .Append('|').AppendInvariant((int)GearQuality)
            .Append('|').AppendInvariant(GearQuantity)
            .Append('|').AppendInvariant(MaximumDepth)
            .Append('|').AppendInvariant(MaximumExpandedNodeCount);
        AppendString(canonical, RootNodeId);
        AppendCrafterObservation(canonical, CrafterObservation);
        AppendMarketEvidence(canonical, MarketEvidence);
        canonical.Append('|').Append(BuiltAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));

        foreach (var node in ExpandedNodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
        {
            canonical.Append("|node");
            AppendString(canonical, node.NodeId);
            AppendString(canonical, node.ParentNodeId);
            canonical.Append('|').AppendInvariant((int)node.Kind)
                .Append('|').AppendInvariant(node.ItemId)
                .Append('|').AppendInvariant((int)node.Quality)
                .Append('|').AppendInvariant(node.RequiredQuantity)
                .Append('|').AppendInvariant(node.QuantityPerParentCraft)
                .Append('|').AppendInvariant(node.RecipeId)
                .Append('|').AppendInvariant(node.RecipeOutputQuantity)
                .Append('|').AppendInvariant(node.RecipeUnlockItemId);
            AppendResolvedRecipe(canonical, node.ResolvedRecipe);
            AppendEligibility(canonical, node.Eligibility);
            AppendHqAttestation(canonical, node.HqCapabilityAttestation);
        }

        foreach (var line in TerminalMaterials
                     .OrderBy(line => line.MaterialKey, StringComparer.Ordinal)
                     .ThenBy(line => line.ItemId)
                     .ThenBy(line => line.Quality)
                     .ThenBy(line => line.ConsumedQuantity)
                     .ThenBy(line => SourceSortKey(line.Source), StringComparer.Ordinal))
        {
            canonical.Append("|material");
            AppendString(canonical, line.MaterialKey);
            canonical.Append('|').AppendInvariant(line.ItemId)
                .Append('|').AppendInvariant((int)line.Quality)
                .Append('|').AppendInvariant(line.ConsumedQuantity)
                .Append('|').AppendInvariant(line.PurchasedQuantity)
                .Append('|').AppendInvariant(line.SurplusQuantity);
            AppendSource(canonical, line.Source);
        }

        foreach (var diagnostic in Diagnostics
                     .OrderBy(diagnostic => diagnostic.Code)
                     .ThenBy(diagnostic => diagnostic.NodeId, StringComparer.Ordinal)
                     .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal))
        {
            canonical.Append("|diagnostic|").AppendInvariant((int)diagnostic.Code);
            AppendString(canonical, diagnostic.NodeId);
            AppendString(canonical, diagnostic.Message);
        }

        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))));
    }

    private void ValidateTreeDepthAndCycles(IReadOnlyDictionary<string, OutfitterCraftNode> nodes, List<string> errors)
    {
        foreach (var node in ExpandedNodes)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { node.NodeId };
            var cursor = node;
            var depth = 0;
            while (cursor.ParentNodeId is { } parentId)
            {
                if (!nodes.TryGetValue(parentId, out var parent))
                    break;
                if (!seen.Add(parentId))
                {
                    errors.Add("The expanded recipe tree is circular.");
                    break;
                }
                depth = checked(depth + 1);
                if (depth > MaximumDepth)
                {
                    errors.Add($"Node '{node.NodeId}' exceeds maximum recipe depth {MaximumDepth}.");
                    break;
                }
                cursor = parent;
            }
            if (cursor.ParentNodeId is null && cursor.NodeId != RootNodeId)
                errors.Add($"Node '{node.NodeId}' does not descend from the declared root.");
        }
    }

    private void ValidateExpandedQuantities(IReadOnlyDictionary<string, OutfitterCraftNode> nodes, List<string> errors)
    {
        try
        {
            foreach (var node in ExpandedNodes.Where(node => node.ParentNodeId is not null))
            {
                if (!nodes.TryGetValue(node.ParentNodeId!, out var parent) ||
                    parent.Kind != OutfitterCraftNodeKind.Craft ||
                    parent.RecipeOutputQuantity == 0)
                {
                    continue;
                }

                var parentCraftCount = ((ulong)parent.RequiredQuantity + parent.RecipeOutputQuantity - 1) / parent.RecipeOutputQuantity;
                var expectedQuantity = checked(parentCraftCount * node.QuantityPerParentCraft);
                if (expectedQuantity != node.RequiredQuantity)
                    errors.Add($"Node '{node.NodeId}' required quantity does not match its expanded parent recipe quantity.");
            }
        }
        catch (OverflowException)
        {
            errors.Add("Expanded recipe quantity arithmetic overflowed.");
        }
    }

    private void ValidateTerminalCoverage(List<string> errors)
    {
        var expected = new Dictionary<string, uint>(StringComparer.Ordinal);
        var actual = new Dictionary<string, uint>(StringComparer.Ordinal);
        var marketSourceCount = 0;
        try
        {
            foreach (var node in ExpandedNodes.Where(node => node.Kind == OutfitterCraftNodeKind.Material))
            {
                var key = MaterialKey(node.ItemId, node.Quality);
                expected[key] = checked(expected.GetValueOrDefault(key) + node.RequiredQuantity);
            }

            foreach (var line in TerminalMaterials)
            {
                if (line.MaterialKey != MaterialKey(line.ItemId, line.Quality) ||
                    line.ItemId == 0 ||
                    line.ConsumedQuantity == 0 ||
                    line.PurchasedQuantity == 0 ||
                    line.PurchasedQuantity < line.ConsumedQuantity ||
                    line.SurplusQuantity != line.PurchasedQuantity - line.ConsumedQuantity ||
                    !IsExactQuality(line.Quality) ||
                    line.Source is null)
                {
                    errors.Add("A terminal material line has incomplete exact identity.");
                    continue;
                }

                ValidateSource(line, errors);
                if (line.Source.Kind == OutfitterMaterialSourceKind.MarketListing)
                    marketSourceCount = checked(marketSourceCount + 1);
                actual[line.MaterialKey] = checked(actual.GetValueOrDefault(line.MaterialKey) + line.ConsumedQuantity);
            }

            foreach (var allocation in TerminalMaterials
                         .Where(line => line.Source is OutfitterMarketMaterialSourceIdentity)
                          .GroupBy(line =>
                          {
                              var market = (OutfitterMarketMaterialSourceIdentity)line.Source;
                              return market.PhysicalSourceKey;
                          }))
            {
                var sources = allocation
                    .Select(line => (OutfitterMarketMaterialSourceIdentity)line.Source)
                    .Distinct()
                    .ToArray();
                if (sources.Length != 1)
                {
                    errors.Add("One market listing identity cannot carry conflicting listing evidence.");
                    continue;
                }
                if (allocation.Count() != 1)
                    errors.Add("One indivisible market listing must be represented by exactly one terminal line.");
            }
        }
        catch (OverflowException)
        {
            errors.Add("Terminal material quantity arithmetic overflowed.");
        }

        if (!expected.OrderBy(pair => pair.Key).SequenceEqual(actual.OrderBy(pair => pair.Key)))
            errors.Add("Terminal material lines do not completely cover the expanded tree.");
        if (MarketEvidence is not null && !ValidateMarketEvidenceReference(MarketEvidence))
            errors.Add("Optional market comparison lineage must identify one complete publication available when the plan was built.");
        if (marketSourceCount > 0 && MarketEvidence is null)
            errors.Add("Market material sources require one complete market evidence reference.");
    }

    private void ValidateSource(OutfitterTerminalMaterialLine line, List<string> errors)
    {
        var source = line.Source;
        if (source.ItemId != line.ItemId || source.Quality != line.Quality || source.UnitPriceGil == 0)
        {
            errors.Add($"Material '{line.MaterialKey}' has incomplete source identity.");
            return;
        }

        switch (source)
        {
            case OutfitterMarketMaterialSourceIdentity market:
                if (market.AvailableQuantity != line.PurchasedQuantity ||
                    line.SurplusQuantity != market.AvailableQuantity - line.ConsumedQuantity ||
                    string.IsNullOrWhiteSpace(market.ListingId) ||
                    market.WorldId == 0 ||
                    string.IsNullOrWhiteSpace(market.WorldName) ||
                    string.IsNullOrWhiteSpace(market.SourceRevision) ||
                    market.EvidenceGenerationId == Guid.Empty ||
                    market.EvidenceRevision <= 0 ||
                    MarketEvidence is null ||
                    !CraftMarketEvidenceFreshness.IsFresh(
                        market.ReviewedAtUtc,
                        market.CapturedAtUtc,
                        MarketEvidence.PublishedAtUtc,
                        BuiltAtUtc))
                {
                    errors.Add($"Market material '{line.MaterialKey}' has incomplete or stale listing evidence.");
                }
                if (MarketEvidence is null ||
                    market.EvidenceGenerationId != MarketEvidence.GenerationId ||
                    market.EvidenceRevision != MarketEvidence.Revision ||
                    !MarketEvidence.MatchesItemSource(market.ItemId, market.CapturedAtUtc, market.SourceRevision) ||
                    !MarketEvidence.MatchesListing(
                        market.ItemId,
                        market.Quality,
                        market.ListingId,
                        market.WorldId,
                        market.WorldName,
                        market.AvailableQuantity,
                        market.UnitPriceGil,
                        market.ReviewedAtUtc,
                        market.CapturedAtUtc,
                        market.SourceRevision))
                {
                    errors.Add("Market material sources must use the plan's exact published market evidence lineage.");
                }
                break;
            case OutfitterGilVendorMaterialSourceIdentity vendor:
                if (vendor.Quality != EquipmentQuality.Normal ||
                    line.PurchasedQuantity != line.ConsumedQuantity ||
                    line.SurplusQuantity != 0 ||
                    vendor.ShopId == 0 ||
                    vendor.VendorId == 0 ||
                    vendor.TerritoryId == 0 ||
                    string.IsNullOrWhiteSpace(vendor.VendorName) ||
                    string.IsNullOrWhiteSpace(vendor.TerritoryName) ||
                    string.IsNullOrWhiteSpace(vendor.CatalogVersion) ||
                    !vendor.HasExactCatalogMembership())
                {
                    errors.Add($"Gil-vendor material '{line.MaterialKey}' has incomplete vendor catalog identity.");
                }
                break;
            default:
                errors.Add($"Material '{line.MaterialKey}' uses an unsupported source kind.");
                break;
        }
    }

    private static void ValidateResolvedRecipe(
        OutfitterCraftNode node,
        IReadOnlyCollection<OutfitterCraftNode> children,
        List<string> errors)
    {
        var recipe = node.ResolvedRecipe!;
        if (string.IsNullOrWhiteSpace(recipe.ResolverId) ||
            string.IsNullOrWhiteSpace(recipe.ResolverVersion) ||
            string.IsNullOrWhiteSpace(recipe.DefinitionFingerprint) ||
            recipe.RecipeId != node.RecipeId ||
            recipe.OutputItemId != node.ItemId ||
            recipe.OutputQuantity != node.RecipeOutputQuantity ||
            recipe.RequiredClassJobId != node.Eligibility?.RequiredClassJobId ||
            recipe.RequiredLevel != node.Eligibility?.RequiredLevel ||
            recipe.RecipeUnlockItemId != node.RecipeUnlockItemId ||
            recipe.Ingredients.IsDefault)
        {
            errors.Add($"Craft node '{node.NodeId}' does not match its frozen static recipe resolution.");
            return;
        }
        if (recipe.Ingredients.Any(ingredient => ingredient is null ||
            string.IsNullOrWhiteSpace(ingredient.ChildNodeId) ||
            ingredient.ItemId == 0 ||
            ingredient.QuantityPerCraft == 0 ||
            !IsExactQuality(ingredient.Quality)))
        {
            errors.Add($"Craft node '{node.NodeId}' has incomplete frozen ingredient identity.");
            return;
        }

        var resolvedIngredients = recipe.Ingredients
            .OrderBy(ingredient => ingredient.ChildNodeId, StringComparer.Ordinal)
            .ThenBy(ingredient => ingredient.ItemId)
            .ThenBy(ingredient => ingredient.Quality)
            .ThenBy(ingredient => ingredient.QuantityPerCraft)
            .Select(ingredient => (ingredient.ChildNodeId, ingredient.ItemId, ingredient.Quality, ingredient.QuantityPerCraft));
        var expandedIngredients = children
            .OrderBy(child => child.NodeId, StringComparer.Ordinal)
            .ThenBy(child => child.ItemId)
            .ThenBy(child => child.Quality)
            .ThenBy(child => child.QuantityPerParentCraft)
            .Select(child => (child.NodeId, child.ItemId, child.Quality, child.QuantityPerParentCraft));
        if (!resolvedIngredients.SequenceEqual(expandedIngredients))
            errors.Add($"Craft node '{node.NodeId}' expanded ingredients do not match its frozen static recipe resolution.");
    }

    private void ValidateEligibility(OutfitterCraftNode node, List<string> errors)
    {
        var evidence = node.Eligibility!;
        if (string.IsNullOrWhiteSpace(evidence.CrafterAuthorityFingerprint) ||
            evidence.NodeId != node.NodeId ||
            evidence.RecipeId != node.RecipeId ||
            evidence.RequiredClassJobId != node.ResolvedRecipe?.RequiredClassJobId ||
            evidence.RequiredLevel != node.ResolvedRecipe?.RequiredLevel ||
            !CrafterUtilityProfile.CrafterClassJobIds.Contains(evidence.RequiredClassJobId) ||
            evidence.RequiredLevel is < 1 or > 100 ||
            evidence.Character != CrafterObservation.Character ||
            !string.Equals(evidence.CrafterAuthorityFingerprint, CrafterObservation.BaselineAuthorityFingerprint, StringComparison.Ordinal) ||
            evidence.ObservedClassJobId != CrafterObservation.ClassJobId ||
            evidence.ObservedLevel != CrafterObservation.EffectiveLevel)
        {
            errors.Add($"Craft node '{node.NodeId}' has incomplete or mismatched active-job eligibility evidence.");
            return;
        }

        switch (evidence.State)
        {
            case OutfitterCraftEligibilityState.ProvenEligible:
                if (CrafterObservation.IsLevelSynced ||
                    evidence.ObservedClassJobId != evidence.RequiredClassJobId ||
                    !CrafterUtilityProfile.CrafterClassJobIds.Contains(evidence.ObservedClassJobId) ||
                    evidence.ObservedLevel is < 1 or > 100 ||
                    evidence.ObservedLevel < evidence.RequiredLevel ||
                    !string.IsNullOrWhiteSpace(evidence.Diagnostic))
                {
                    errors.Add($"Craft node '{node.NodeId}' claims eligibility without matching active crafting job and level proof.");
                }
                break;
            case OutfitterCraftEligibilityState.ProvenIneligible:
                if (evidence.ObservedClassJobId == 0 ||
                    evidence.ObservedLevel is < 1 or > 100 ||
                    (evidence.ObservedClassJobId == evidence.RequiredClassJobId &&
                        evidence.ObservedLevel >= evidence.RequiredLevel) ||
                    string.IsNullOrWhiteSpace(evidence.Diagnostic))
                {
                    errors.Add($"Craft node '{node.NodeId}' has incomplete ineligibility evidence.");
                }
                break;
            case OutfitterCraftEligibilityState.Unproven:
                if (!CrafterObservation.IsLevelSynced || string.IsNullOrWhiteSpace(evidence.Diagnostic))
                {
                    errors.Add($"Craft node '{node.NodeId}' has incomplete unproven eligibility evidence.");
                }
                break;
            default:
                errors.Add($"Craft node '{node.NodeId}' has an unsupported eligibility state.");
                break;
        }
    }

    private void ValidateHqAttestation(OutfitterCraftNode node, List<string> errors)
    {
        var proof = node.HqCapabilityAttestation;
        if (node.Quality == EquipmentQuality.Normal)
        {
            if (proof is not null)
                errors.Add($"NQ craft node '{node.NodeId}' cannot carry an HQ capability attestation.");
            return;
        }
        if (proof is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(proof.AttestationId) ||
            string.IsNullOrWhiteSpace(proof.ModelId) ||
            string.IsNullOrWhiteSpace(proof.ModelVersion) ||
            string.IsNullOrWhiteSpace(proof.CrafterAuthorityFingerprint) ||
            proof.AttestedAtUtc == default ||
            proof.NodeId != node.NodeId ||
            proof.RecipeId != node.RecipeId ||
            proof.ItemId != node.ItemId ||
            proof.Quality != EquipmentQuality.High ||
            proof.Quantity < node.RequiredQuantity ||
            proof.CrafterAuthorityFingerprint != node.Eligibility?.CrafterAuthorityFingerprint ||
            proof.CrafterAuthorityFingerprint != CrafterObservation.BaselineAuthorityFingerprint ||
            node.Eligibility is null ||
            proof.AttestedAtUtc > BuiltAtUtc)
        {
            errors.Add($"HQ craft node '{node.NodeId}' has incomplete or mismatched capability attestation.");
        }
    }

    private bool ValidateMarketEvidenceReference(CraftMarketEvidenceReference? evidence) =>
        evidence is not null &&
        evidence.GenerationId != Guid.Empty &&
        evidence.Revision > 0 &&
        evidence.SchemaVersion == OutfitterMarketEvidenceBook.CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(evidence.SourceKey) &&
        !string.IsNullOrWhiteSpace(evidence.Region) &&
        evidence.CreatedAtUtc != default &&
        evidence.PublishedAtUtc >= evidence.CreatedAtUtc &&
        evidence.PublishedAtUtc <= BuiltAtUtc &&
        Enum.IsDefined(evidence.CoverageMode) &&
        evidence.CatalogItemCount > 0 &&
        evidence.QueriedItemCount > 0 &&
        evidence.ListingLimit is >= 1 and <= 100 &&
        !evidence.QueriedItemIds.IsDefaultOrEmpty &&
        evidence.QueriedItemCount == evidence.QueriedItemIds.Distinct().Count() &&
        evidence.QueriedItemIds.All(itemId => itemId != 0) &&
        !evidence.ItemSourceRevisions.IsDefaultOrEmpty &&
        evidence.ItemSourceRevisions.Length == evidence.QueriedItemCount &&
        evidence.ItemSourceRevisions.All(item => item is not null &&
            item.ItemId != 0 && item.CapturedAtUtc != default && !string.IsNullOrWhiteSpace(item.SourceRevision)) &&
        !evidence.Listings.IsDefault &&
        evidence.Listings.All(listing => listing is not null) &&
        !string.IsNullOrWhiteSpace(evidence.ContentIdentity);

    private static bool ValidCrafterObservation(OutfitterCrafterObservationIdentity? observation) =>
        observation is not null &&
        ValidCharacter(observation.Character) &&
        !string.IsNullOrWhiteSpace(observation.BaselineAuthorityFingerprint) &&
        CrafterUtilityProfile.CrafterClassJobIds.Contains(observation.ClassJobId) &&
        observation.ActualLevel is >= 1 and <= 100 &&
        observation.EffectiveLevel is >= 1 and <= 100 &&
        observation.EffectiveLevel <= observation.ActualLevel &&
        observation.CaptureId != Guid.Empty &&
        observation.CapturedAtUtc != default &&
        observation.EquipmentGenerationId != Guid.Empty &&
        observation.EquipmentIdentityCapturedAtUtc != default &&
        observation.EquipmentIdentityCapturedAtUtc <= observation.CapturedAtUtc &&
        !string.IsNullOrWhiteSpace(observation.EquipmentSnapshotFingerprint);

    private static bool ValidCharacter(CharacterScope? character) =>
        character is { LocalContentId: > 0, HomeWorldId: > 0 } && !string.IsNullOrWhiteSpace(character.Name);

    private static bool IsExactQuality(EquipmentQuality quality) =>
        quality is EquipmentQuality.Normal or EquipmentQuality.High;

    private static OutfitterCraftPlanValidation Invalid(IEnumerable<string> errors) =>
        new(false, errors.Distinct(StringComparer.Ordinal).ToImmutableArray());

    private static string SourceSortKey(OutfitterMaterialSourceIdentity source)
    {
        var canonical = new StringBuilder();
        AppendSource(canonical, source);
        return canonical.ToString();
    }

    private static void AppendCrafterObservation(StringBuilder canonical, OutfitterCrafterObservationIdentity observation)
    {
        canonical.Append("|crafter-observation")
            .Append('|').AppendInvariant(observation.Character.LocalContentId)
            .Append('|').AppendInvariant(observation.Character.HomeWorldId);
        AppendString(canonical, observation.Character.Name);
        AppendString(canonical, observation.BaselineAuthorityFingerprint);
        canonical.Append('|').AppendInvariant(observation.ClassJobId)
            .Append('|').AppendInvariant(observation.ActualLevel)
            .Append('|').AppendInvariant(observation.EffectiveLevel)
            .Append('|').AppendInvariant(observation.IsLevelSynced ? 1 : 0)
            .Append('|').Append(observation.CaptureId)
            .Append('|').AppendInvariant(observation.CapturedAtUtc.UtcDateTime.Ticks)
            .Append('|').Append(observation.EquipmentGenerationId)
            .Append('|').AppendInvariant(observation.EquipmentIdentityCapturedAtUtc.UtcDateTime.Ticks);
        AppendString(canonical, observation.EquipmentSnapshotFingerprint);
    }

    private static void AppendResolvedRecipe(StringBuilder canonical, OutfitterResolvedRecipeSnapshot? recipe)
    {
        canonical.Append("|resolved-recipe|").AppendInvariant(recipe is null ? 0 : 1);
        if (recipe is null)
            return;
        AppendString(canonical, recipe.ResolverId);
        AppendString(canonical, recipe.ResolverVersion);
        AppendString(canonical, recipe.DefinitionFingerprint);
        canonical.Append('|').AppendInvariant(recipe.RecipeId)
            .Append('|').AppendInvariant(recipe.OutputItemId)
            .Append('|').AppendInvariant(recipe.OutputQuantity)
            .Append('|').AppendInvariant(recipe.RequiredClassJobId)
            .Append('|').AppendInvariant(recipe.RequiredLevel)
            .Append('|').AppendInvariant(recipe.RecipeUnlockItemId);
        foreach (var ingredient in recipe.Ingredients
                     .OrderBy(ingredient => ingredient.ChildNodeId, StringComparer.Ordinal)
                     .ThenBy(ingredient => ingredient.ItemId)
                     .ThenBy(ingredient => ingredient.Quality)
                     .ThenBy(ingredient => ingredient.QuantityPerCraft))
        {
            canonical.Append("|ingredient");
            AppendString(canonical, ingredient.ChildNodeId);
            canonical.Append('|').AppendInvariant(ingredient.ItemId)
                .Append('|').AppendInvariant((int)ingredient.Quality)
                .Append('|').AppendInvariant(ingredient.QuantityPerCraft);
        }
    }

    private static void AppendMarketEvidence(StringBuilder canonical, CraftMarketEvidenceReference? evidence)
    {
        canonical.Append("|market-evidence|").AppendInvariant(evidence is null ? 0 : 1);
        if (evidence is null)
            return;
        canonical.Append('|').Append(evidence.GenerationId).Append('|').AppendInvariant(evidence.Revision);
        AppendString(canonical, evidence.SchemaVersion);
        AppendString(canonical, evidence.SourceKey);
        AppendString(canonical, evidence.Region);
        canonical.Append('|').AppendInvariant(evidence.CreatedAtUtc.UtcDateTime.Ticks)
            .Append('|').AppendInvariant(evidence.PublishedAtUtc.UtcDateTime.Ticks)
            .Append('|').AppendInvariant((int)evidence.CoverageMode)
            .Append('|').AppendInvariant(evidence.CatalogItemCount)
            .Append('|').AppendInvariant(evidence.QueriedItemCount)
            .Append('|').AppendInvariant(evidence.ListingLimit);
        foreach (var itemId in evidence.QueriedItemIds.Order())
            canonical.Append('|').AppendInvariant(itemId);
        foreach (var sourceRevision in evidence.ItemSourceRevisions.OrderBy(item => item.ItemId))
        {
            canonical.Append('|').AppendInvariant(sourceRevision.ItemId)
                .Append('|').AppendInvariant(sourceRevision.CapturedAtUtc.UtcDateTime.Ticks);
            AppendString(canonical, sourceRevision.SourceRevision);
        }
        foreach (var listing in evidence.Listings
                     .OrderBy(item => item.ItemId)
                     .ThenBy(item => item.WorldId)
                     .ThenBy(item => item.ListingId, StringComparer.Ordinal)
                     .ThenBy(item => item.Quality))
        {
            canonical.Append('|').AppendInvariant(listing.ItemId)
                .Append('|').AppendInvariant((int)listing.Quality);
            AppendString(canonical, listing.ListingId);
            canonical.Append('|').AppendInvariant(listing.WorldId);
            AppendString(canonical, listing.WorldName);
            canonical.Append('|').AppendInvariant(listing.AvailableQuantity)
                .Append('|').AppendInvariant(listing.UnitPriceGil)
                .Append('|').AppendInvariant(listing.ReviewedAtUtc.UtcDateTime.Ticks)
                .Append('|').AppendInvariant(listing.CapturedAtUtc.UtcDateTime.Ticks);
            AppendString(canonical, listing.SourceRevision);
        }
        AppendString(canonical, evidence.ContentIdentity);
    }

    private static void AppendEligibility(StringBuilder canonical, OutfitterCraftEligibilityEvidence? evidence)
    {
        canonical.Append("|eligibility|").AppendInvariant(evidence is null ? 0 : 1);
        if (evidence is null)
            return;
        canonical.Append('|').AppendInvariant((int)evidence.State);
        AppendString(canonical, evidence.CrafterAuthorityFingerprint);
        canonical.Append('|').AppendInvariant(evidence.Character?.LocalContentId ?? 0)
            .Append('|').AppendInvariant(evidence.Character?.HomeWorldId ?? 0);
        AppendString(canonical, evidence.Character?.Name);
        AppendString(canonical, evidence.NodeId);
        canonical.Append('|').AppendInvariant(evidence.RecipeId)
            .Append('|').AppendInvariant(evidence.RequiredClassJobId)
            .Append('|').AppendInvariant(evidence.RequiredLevel)
            .Append('|').AppendInvariant(evidence.ObservedClassJobId)
            .Append('|').AppendInvariant(evidence.ObservedLevel);
        AppendString(canonical, evidence.Diagnostic);
    }

    private static void AppendHqAttestation(StringBuilder canonical, OutfitterCraftHqCapabilityAttestation? proof)
    {
        canonical.Append("|hq-proof|").AppendInvariant(proof is null ? 0 : 1);
        if (proof is null)
            return;
        AppendString(canonical, proof.AttestationId);
        AppendString(canonical, proof.ModelId);
        AppendString(canonical, proof.ModelVersion);
        AppendString(canonical, proof.CrafterAuthorityFingerprint);
        canonical.Append('|').AppendInvariant(proof.AttestedAtUtc.UtcDateTime.Ticks);
        AppendString(canonical, proof.NodeId);
        canonical.Append('|').AppendInvariant(proof.RecipeId)
            .Append('|').AppendInvariant(proof.ItemId)
            .Append('|').AppendInvariant((int)proof.Quality)
            .Append('|').AppendInvariant(proof.Quantity);
    }

    private static void AppendSource(StringBuilder canonical, OutfitterMaterialSourceIdentity source)
    {
        canonical.Append('|').AppendInvariant((int)source.Kind)
            .Append('|').AppendInvariant(source.ItemId)
            .Append('|').AppendInvariant((int)source.Quality)
            .Append('|').AppendInvariant(source.UnitPriceGil);
        switch (source)
        {
            case OutfitterMarketMaterialSourceIdentity market:
                canonical.Append('|').AppendInvariant(market.AvailableQuantity)
                    .Append('|').AppendInvariant(market.WorldId);
                AppendString(canonical, market.ListingId);
                AppendString(canonical, market.WorldName);
                canonical.Append('|').AppendInvariant(market.ReviewedAtUtc.UtcDateTime.Ticks);
                canonical.Append('|').AppendInvariant(market.CapturedAtUtc.UtcDateTime.Ticks);
                AppendString(canonical, market.SourceRevision);
                canonical.Append('|').Append(market.EvidenceGenerationId).Append('|').AppendInvariant(market.EvidenceRevision);
                break;
            case OutfitterGilVendorMaterialSourceIdentity vendor:
                canonical.Append('|').AppendInvariant(vendor.ShopId)
                    .Append('|').AppendInvariant(vendor.VendorId)
                    .Append('|').AppendInvariant(vendor.TerritoryId);
                AppendString(canonical, vendor.VendorName);
                AppendString(canonical, vendor.TerritoryName);
                AppendString(canonical, vendor.CatalogVersion);
                break;
        }
    }

    private static void AppendString(StringBuilder canonical, string? value)
    {
        canonical.Append('|');
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }
        canonical.AppendInvariant(value.Length).Append(':').Append(value);
    }
}

internal sealed class ContractOnlyCraftPlanJsonConverter : JsonConverter<OutfitterCraftPlan>
{
    public override OutfitterCraftPlan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Contract-only craft plans are process-local and have no replay DTO.");

    public override void Write(Utf8JsonWriter writer, OutfitterCraftPlan value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Contract-only craft plans are process-local and have no replay DTO.");
}

internal sealed class NewtonsoftContractOnlyCraftPlanJsonConverter : Newtonsoft.Json.JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(OutfitterCraftPlan);

    public override object? ReadJson(
        Newtonsoft.Json.JsonReader reader,
        Type objectType,
        object? existingValue,
        Newtonsoft.Json.JsonSerializer serializer) =>
        throw new NotSupportedException("Contract-only craft plans are process-local and have no replay DTO.");

    public override void WriteJson(
        Newtonsoft.Json.JsonWriter writer,
        object? value,
        Newtonsoft.Json.JsonSerializer serializer) =>
        throw new NotSupportedException("Contract-only craft plans are process-local and have no replay DTO.");
}

internal static class CraftCanonicalStringBuilderExtensions
{
    public static StringBuilder AppendInvariant(this StringBuilder builder, int value) =>
        builder.Append(value.ToString(CultureInfo.InvariantCulture));

    public static StringBuilder AppendInvariant(this StringBuilder builder, uint value) =>
        builder.Append(value.ToString(CultureInfo.InvariantCulture));

    public static StringBuilder AppendInvariant(this StringBuilder builder, long value) =>
        builder.Append(value.ToString(CultureInfo.InvariantCulture));

    public static StringBuilder AppendInvariant(this StringBuilder builder, ulong value) =>
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
}
