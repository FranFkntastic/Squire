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
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Crafting;

internal enum CraftCostComparisonStatus
{
    Complete,
    DisplayOnly,
    Abstained,
}

internal abstract record ComparedGearSourceIdentity
{
    public abstract EquipmentAcquisitionSourceKind Kind { get; }
    public abstract string SourceCatalogKey { get; }
    public abstract string? ObservationId { get; }
}

internal sealed record ComparedGearMarketSourceIdentity(
    CraftMarketEvidenceReference Evidence,
    CraftMarketListingIdentity Listing,
    uint ConsumedQuantity,
    uint PurchasedQuantity,
    uint SurplusQuantity,
    ulong TotalPriceGil) : ComparedGearSourceIdentity
{
    public override EquipmentAcquisitionSourceKind Kind => EquipmentAcquisitionSourceKind.MarketBoard;
    public override string SourceCatalogKey => Evidence is null || Listing is null
        ? string.Empty
        : string.Create(
            CultureInfo.InvariantCulture,
            $"market:{Evidence.SourceKey}:{Listing.WorldId}:{Listing.ItemId}:{Listing.Quality}");
    public override string ObservationId => Listing is null
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $"{Listing.WorldId}:{Listing.ListingId}");
}

internal sealed record ComparedGearOwnedInstanceIdentity
{
    private ComparedGearOwnedInstanceIdentity(
        CharacterScope character,
        string container,
        int slotIndex,
        uint itemId,
        EquipmentQuality quality,
        uint quantity)
    {
        Character = character;
        Container = container;
        SlotIndex = slotIndex;
        ItemId = itemId;
        Quality = quality;
        Quantity = quantity;
    }

    public CharacterScope Character { get; }
    public string Container { get; }
    public int SlotIndex { get; }
    public uint ItemId { get; }
    public EquipmentQuality Quality { get; }
    public uint Quantity { get; }

    private static ComparedGearOwnedInstanceIdentity FromSnapshot(EquipmentInstanceSnapshot instance) => new(
        instance.Fingerprint.Character,
        instance.Fingerprint.Container,
        instance.Fingerprint.SlotIndex,
        instance.Fingerprint.ItemId,
        instance.Fingerprint.IsHighQuality ? EquipmentQuality.High : EquipmentQuality.Normal,
        instance.Fingerprint.Quantity);

    internal static ComparedGearOwnedInstanceIdentity FromTrustedSnapshotMember(EquipmentInstanceSnapshot instance) =>
        FromSnapshot(instance);
}

internal sealed record ComparedGearOwnedSourceIdentity : ComparedGearSourceIdentity
{
    private ComparedGearOwnedSourceIdentity(
        Guid captureId,
        Guid captureGenerationId,
        DateTimeOffset capturedAtUtc,
        string equipmentSnapshotFingerprint,
        ComparedGearOwnedInstanceIdentity instance)
    {
        CaptureId = captureId;
        CaptureGenerationId = captureGenerationId;
        CapturedAtUtc = capturedAtUtc;
        EquipmentSnapshotFingerprint = equipmentSnapshotFingerprint;
        Instance = instance;
    }

    public Guid CaptureId { get; }
    public Guid CaptureGenerationId { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public string EquipmentSnapshotFingerprint { get; }
    public ComparedGearOwnedInstanceIdentity Instance { get; }
    public override EquipmentAcquisitionSourceKind Kind => EquipmentAcquisitionSourceKind.Owned;
    public override string SourceCatalogKey
    {
        get
        {
            if (Instance is null)
                return string.Empty;
            var value = new StringBuilder("owned:")
                .Append(CaptureId.ToString("N", CultureInfo.InvariantCulture)).Append(':')
                .Append(CaptureGenerationId.ToString("N", CultureInfo.InvariantCulture)).Append(':')
                .AppendInvariant(CapturedAtUtc.UtcDateTime.Ticks).Append(':')
                .Append(EquipmentSnapshotFingerprint).Append(':')
                .AppendInvariant(Instance.Character.LocalContentId).Append(':')
                .AppendInvariant(Instance.Character.HomeWorldId).Append(':');
            AppendKeyPart(value, Instance.Character.Name);
            AppendKeyPart(value, Instance.Container);
            return value.AppendInvariant(Instance.SlotIndex).Append(':')
                .AppendInvariant(Instance.ItemId).Append(':')
                .AppendInvariant((int)Instance.Quality).Append(':')
                .AppendInvariant(Instance.Quantity)
                .ToString();
        }
    }
    public override string? ObservationId => null;

    public static ComparedGearOwnedSourceIdentity FromBaseline(
        PlayerAdvisorBaseline baseline,
        string container,
        int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (!PlayerAdvisorBaselineAssembler.IsCompleteAndConsistent(
                baseline,
                CrafterAdvisorStatFamily.Instance,
                out var diagnostic) ||
            baseline is not
            {
                Character: { } character,
                EquipmentSnapshot: { } snapshot,
                CaptureProvenance: { } provenance,
            })
        {
            throw new InvalidOperationException(
                $"Owned comparison identity requires one complete trusted player baseline: {diagnostic}");
        }
        if (string.IsNullOrWhiteSpace(container) || slotIndex < 0)
            throw new ArgumentException("Owned comparison identity requires one exact container and slot.");

        var matches = snapshot.Instances
            .Where(instance => instance?.Fingerprint is not null &&
                instance.Fingerprint.Character == character &&
                string.Equals(instance.Fingerprint.Container, container, StringComparison.Ordinal) &&
                instance.Fingerprint.SlotIndex == slotIndex)
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Owned comparison identity requires one exact snapshot member.");

        var match = matches[0];
        var fingerprint = match.Fingerprint;
        if (!CraftEquipmentSnapshotIdentity.TryCompute(snapshot, out var equipmentSnapshotFingerprint) ||
            snapshot.GenerationId != provenance.EquipmentGenerationId ||
            snapshot.Identity.CapturedAt != provenance.EquipmentIdentityCapturedAtUtc ||
            match.CapturedAt != snapshot.Identity.CapturedAt ||
            fingerprint.ItemId == 0 ||
            fingerprint.Quantity == 0)
        {
            throw new InvalidOperationException("Owned comparison identity requires exact character, generation, time, item, and quality membership.");
        }

        return new(
            provenance.CaptureId,
            snapshot.GenerationId,
            snapshot.Identity.CapturedAt,
            equipmentSnapshotFingerprint,
            ComparedGearOwnedInstanceIdentity.FromTrustedSnapshotMember(match));
    }

    private static void AppendKeyPart(StringBuilder value, string part) =>
        value.AppendInvariant(part.Length).Append(':').Append(part).Append(':');
}

internal sealed record ComparedGearGilVendorSourceIdentity : ComparedGearSourceIdentity
{
    private readonly OutfitterGilVendorCatalogReference catalog;

    private ComparedGearGilVendorSourceIdentity(
        OutfitterGilVendorCatalogReference catalog,
        OutfitterGilVendorOffer offer,
        uint purchasedQuantity,
        ulong totalPriceGil)
    {
        this.catalog = catalog;
        ItemId = offer.ItemId;
        Quality = EquipmentQuality.Normal;
        ShopId = offer.ShopId;
        VendorId = offer.VendorId;
        VendorName = offer.VendorName;
        TerritoryId = offer.TerritoryId;
        TerritoryName = offer.TerritoryName;
        CatalogVersion = catalog.CatalogVersion;
        PurchasedQuantity = purchasedQuantity;
        UnitPriceGil = offer.UnitPriceGil;
        TotalPriceGil = totalPriceGil;
    }

    public uint ItemId { get; }
    public EquipmentQuality Quality { get; }
    public uint ShopId { get; }
    public uint VendorId { get; }
    public string VendorName { get; }
    public uint TerritoryId { get; }
    public string TerritoryName { get; }
    public string CatalogVersion { get; }
    public uint PurchasedQuantity { get; }
    public uint UnitPriceGil { get; }
    public ulong TotalPriceGil { get; }
    public override EquipmentAcquisitionSourceKind Kind => EquipmentAcquisitionSourceKind.GilVendor;
    public override string SourceCatalogKey => string.Create(
        CultureInfo.InvariantCulture,
        $"vendor:{ShopId}:{VendorId}:{TerritoryId}:{ItemId}");
    public override string? ObservationId => null;

    internal static ComparedGearGilVendorSourceIdentity FromCatalog(
        OutfitterGilVendorCatalog catalog,
        OutfitterGilVendorOffer offer,
        uint purchasedQuantity)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(offer);
        var reference = catalog.CaptureReference();
        if (purchasedQuantity == 0 || !reference.Matches(offer))
            throw new InvalidOperationException("Compared gil-vendor identity requires exact catalog membership and quantity.");
        return new(reference, offer, purchasedQuantity, checked((ulong)purchasedQuantity * offer.UnitPriceGil));
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

internal sealed record ComparedGearAllocation(
    EquipmentOfferAllocationKey AllocationKey,
    uint ConsumedQuantity,
    ulong TotalGil,
    ComparedGearSourceIdentity Source);

internal sealed record CraftAcquisitionBurden(
    int CraftNodeCount,
    int SubcraftNodeCount,
    int DistinctMaterialCount,
    int MarketSourceCount,
    int VendorSourceCount);

internal sealed record CraftCostComparisonValidation(bool IsValid, ImmutableArray<string> Errors);

internal sealed record CraftCostComparisonIdentity(string Sha256)
{
    public override string ToString() => Sha256;
}

/// <summary>
/// Passive process-local economy comparison over one frozen expanded recipe tree. It carries no
/// solver, Workbench, Artisan, route, purchase, persistence, or replay authority.
/// </summary>
[JsonConverter(typeof(ContractOnlyCraftCostComparisonJsonConverter))]
[Newtonsoft.Json.JsonConverter(typeof(NewtonsoftContractOnlyCraftCostComparisonJsonConverter))]
internal sealed record CraftCostComparison(
    string SchemaVersion,
    string ComparisonId,
    CraftCostComparisonStatus Status,
    OutfitterCraftPlan Plan,
    OutfitterCraftPlanIdentity PlanIdentity,
    ulong TotalGil,
    uint EffectiveUnitGil,
    ComparedGearAllocation ComparedAllocation,
    long SavingsGil,
    CraftAcquisitionBurden Burden,
    DateTimeOffset BuiltAtUtc,
    ImmutableArray<string> Diagnostics)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-outfitter-craft-cost-comparison/v4";

    public CraftCostComparisonValidation Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(ComparisonId))
            errors.Add("Cost-comparison schema and identity must be complete.");
        if (!Enum.IsDefined(Status))
            errors.Add("Cost-comparison status is unsupported.");
        if (BuiltAtUtc == default)
            errors.Add("Cost comparisons require an explicit non-default build time.");
        if (Diagnostics.IsDefault)
            errors.Add("Cost-comparison diagnostics must be initialized.");
        else if (Diagnostics.Any(diagnostic => string.IsNullOrWhiteSpace(diagnostic)))
            errors.Add("Cost-comparison diagnostics cannot contain null or empty entries.");
        if (Plan is null)
        {
            errors.Add("A cost comparison requires a craft plan.");
            return Invalid(errors);
        }

        var planValidation = Plan.Validate(Status == CraftCostComparisonStatus.Complete);
        errors.AddRange(planValidation.Errors);
        if (BuiltAtUtc < Plan.BuiltAtUtc)
            errors.Add("A cost comparison cannot predate its frozen craft plan.");
        if (planValidation.IsValid &&
            Plan.MarketEvidence is { } planEvidence &&
            Plan.TerminalMaterials
                .Select(line => line.Source)
                .OfType<OutfitterMarketMaterialSourceIdentity>()
                .Any(source => !CraftMarketEvidenceFreshness.IsFresh(
                    source.ReviewedAtUtc,
                    source.CapturedAtUtc,
                    planEvidence.PublishedAtUtc,
                    BuiltAtUtc)))
        {
            errors.Add("A cost comparison cannot reuse stale market material evidence.");
        }

        if (planValidation.IsValid)
        {
            if (PlanIdentity is null ||
                string.IsNullOrWhiteSpace(PlanIdentity.Sha256) ||
                PlanIdentity != Plan.ComputeStructuralIdentity())
            {
                errors.Add("Cost comparison must bind the exact structural craft-plan identity.");
            }
        }
        else if (PlanIdentity is null || string.IsNullOrWhiteSpace(PlanIdentity.Sha256))
        {
            errors.Add("Cost comparison must carry a structural craft-plan identity.");
        }

        var allocationValid = ValidateComparedAllocation(errors);
        if (planValidation.IsValid && allocationValid)
        {
            try
            {
                var materialTotal = Plan.TerminalMaterials.Aggregate(
                    0ul,
                    (sum, line) => checked(sum + checked((ulong)line.PurchasedQuantity * line.Source.UnitPriceGil)));
                if (materialTotal != TotalGil)
                    errors.Add("Cost-comparison total does not equal complete terminal material cost.");

                var quotient = TotalGil / Plan.GearQuantity;
                var remainder = TotalGil % Plan.GearQuantity;
                var expectedUnit = checked((uint)(quotient + (remainder == 0 ? 0ul : 1ul)));
                if (EffectiveUnitGil != expectedUnit)
                    errors.Add("Effective unit gil is inconsistent with total and exact gear quantity.");

                var expectedSavings = checked(checked((long)ComparedAllocation.TotalGil) - checked((long)TotalGil));
                if (SavingsGil != expectedSavings)
                    errors.Add("Savings do not match the compared exact gear allocation.");
            }
            catch (OverflowException)
            {
                errors.Add("Cost-comparison gil arithmetic overflowed.");
            }

            ValidateBurden(errors);
        }

        if (!Diagnostics.IsDefault)
        {
            if (Status == CraftCostComparisonStatus.Complete && Diagnostics.Length != 0)
                errors.Add("A complete cost comparison cannot retain unresolved diagnostics.");
            if (Status != CraftCostComparisonStatus.Complete && Diagnostics.Length == 0)
                errors.Add("A display-only or abstained cost comparison requires an explanatory diagnostic.");
        }

        return errors.Count == 0 ? new(true, ImmutableArray<string>.Empty) : Invalid(errors);
    }

    public CraftCostComparisonIdentity ComputeStructuralIdentity()
    {
        var canonical = new StringBuilder();
        AppendString(canonical, CurrentSchemaVersion);
        canonical.Append('|').AppendInvariant((int)Status);
        AppendString(canonical, PlanIdentity.Sha256);
        canonical.Append('|').AppendInvariant(TotalGil)
            .Append('|').AppendInvariant(EffectiveUnitGil);
        ComparedGearAllocationIdentity.Append(canonical, ComparedAllocation);
        canonical.Append('|').AppendInvariant(SavingsGil)
            .Append('|').AppendInvariant(Burden.CraftNodeCount)
            .Append('|').AppendInvariant(Burden.SubcraftNodeCount)
            .Append('|').AppendInvariant(Burden.DistinctMaterialCount)
            .Append('|').AppendInvariant(Burden.MarketSourceCount)
            .Append('|').AppendInvariant(Burden.VendorSourceCount)
            .Append('|').AppendInvariant(BuiltAtUtc.UtcDateTime.Ticks);
        foreach (var diagnostic in Diagnostics.Order(StringComparer.Ordinal))
            AppendString(canonical, diagnostic);
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))));
    }

    private bool ValidateComparedAllocation(List<string> errors)
    {
        var allocation = ComparedAllocation;
        var key = allocation?.AllocationKey;
        var offerKey = key?.OfferKey;
        var source = allocation?.Source;
        if (allocation is null || key is null || offerKey is null || source is null)
        {
            errors.Add("Compared gear allocation must use the exact typed source identity derived from its evidence.");
            return false;
        }

        var sourceValid = source switch
        {
            ComparedGearMarketSourceIdentity market => ValidateMarketAllocation(market, errors),
            ComparedGearOwnedSourceIdentity owned => ValidateOwnedAllocation(owned, errors),
            ComparedGearGilVendorSourceIdentity vendor => ValidateVendorAllocation(vendor, errors),
            _ => InvalidSource(errors),
        };
        if (!sourceValid)
            return false;

        if (
            offerKey.ItemId != Plan.GearItemId ||
            offerKey.Quality != Plan.GearQuality ||
            offerKey.SourceKind != source.Kind ||
            !string.Equals(offerKey.SourceCatalogKey, source.SourceCatalogKey, StringComparison.Ordinal) ||
            !string.Equals(key.ObservationId, source.ObservationId, StringComparison.Ordinal) ||
            allocation.ConsumedQuantity != Plan.GearQuantity)
        {
            errors.Add("Compared gear allocation must use the exact typed source identity derived from its evidence.");
            return false;
        }
        return true;
    }

    private bool ValidateMarketAllocation(ComparedGearMarketSourceIdentity market, List<string> errors)
    {
        var evidence = market.Evidence;
        var listing = market.Listing;
        if (evidence is null || listing is null ||
            Plan.MarketEvidence is null ||
            !string.Equals(evidence.ContentIdentity, Plan.MarketEvidence.ContentIdentity, StringComparison.Ordinal) ||
            evidence.GenerationId == Guid.Empty || evidence.Revision <= 0 ||
            evidence.SchemaVersion != MarketEvidence.OutfitterMarketEvidenceBook.CurrentSchemaVersion ||
            evidence.CreatedAtUtc > evidence.PublishedAtUtc ||
            evidence.QueriedItemIds.IsDefaultOrEmpty || !evidence.QueriedItemIds.Contains(Plan.GearItemId) ||
            string.IsNullOrWhiteSpace(evidence.ContentIdentity) ||
            listing.ItemId != Plan.GearItemId ||
            listing.Quality != Plan.GearQuality ||
            listing.WorldId == 0 || string.IsNullOrWhiteSpace(listing.WorldName) ||
            listing.AvailableQuantity != market.PurchasedQuantity ||
            market.ConsumedQuantity != ComparedAllocation.ConsumedQuantity ||
            market.ConsumedQuantity == 0 ||
            market.PurchasedQuantity < market.ConsumedQuantity ||
            market.SurplusQuantity != market.PurchasedQuantity - market.ConsumedQuantity ||
            listing.UnitPriceGil == 0 ||
            market.TotalPriceGil != ComparedAllocation.TotalGil ||
            string.IsNullOrWhiteSpace(listing.SourceRevision) ||
            !evidence.MatchesItemSource(listing.ItemId, listing.CapturedAtUtc, listing.SourceRevision) ||
            !evidence.MatchesListing(
                listing.ItemId,
                listing.Quality,
                listing.ListingId,
                listing.WorldId,
                listing.WorldName,
                listing.AvailableQuantity,
                listing.UnitPriceGil,
                listing.ReviewedAtUtc,
                listing.CapturedAtUtc,
                listing.SourceRevision) ||
            !CraftMarketEvidenceFreshness.IsFresh(
                listing.ReviewedAtUtc,
                listing.CapturedAtUtc,
                evidence.PublishedAtUtc,
                BuiltAtUtc))
        {
            errors.Add("A compared market allocation requires complete, fresh publication and listing-source lineage.");
            return false;
        }

        try
        {
            if (checked((ulong)market.PurchasedQuantity * listing.UnitPriceGil) != market.TotalPriceGil)
            {
                errors.Add("Compared market listing stack quantity, unit price, and full-stack total price must agree exactly.");
                return false;
            }
        }
        catch (OverflowException)
        {
            errors.Add("Compared market listing price arithmetic overflowed.");
            return false;
        }
        return true;
    }

    private bool ValidateOwnedAllocation(ComparedGearOwnedSourceIdentity owned, List<string> errors)
    {
        var instance = owned.Instance;
        var crafter = Plan.CrafterObservation;
        if (ComparedAllocation.TotalGil != 0 ||
            owned.CaptureId == Guid.Empty ||
            crafter is null ||
            owned.CaptureId != crafter.CaptureId ||
            owned.CaptureGenerationId == Guid.Empty ||
            owned.CaptureGenerationId != crafter.EquipmentGenerationId ||
            owned.CapturedAtUtc == default ||
            owned.CapturedAtUtc != crafter.EquipmentIdentityCapturedAtUtc ||
            string.IsNullOrWhiteSpace(owned.EquipmentSnapshotFingerprint) ||
            !string.Equals(owned.EquipmentSnapshotFingerprint, crafter.EquipmentSnapshotFingerprint, StringComparison.Ordinal) ||
            instance?.Character is not { LocalContentId: > 0, HomeWorldId: > 0 } character ||
            character != crafter.Character ||
            string.IsNullOrWhiteSpace(character.Name) ||
            string.IsNullOrWhiteSpace(instance.Container) ||
            instance.SlotIndex < 0 ||
            instance.ItemId != Plan.GearItemId ||
            instance.Quality != Plan.GearQuality ||
            instance.Quality is not (EquipmentQuality.Normal or EquipmentQuality.High) ||
             instance.Quantity < ComparedAllocation.ConsumedQuantity)
        {
            errors.Add("A compared owned allocation requires one exact same-character capture and zero-cost instance identity.");
            return false;
        }
        return true;
    }

    private bool ValidateVendorAllocation(ComparedGearGilVendorSourceIdentity vendor, List<string> errors)
    {
        if (vendor.ItemId != Plan.GearItemId ||
            vendor.Quality != Plan.GearQuality ||
            vendor.Quality != EquipmentQuality.Normal ||
            vendor.ShopId == 0 || vendor.VendorId == 0 || vendor.TerritoryId == 0 ||
            string.IsNullOrWhiteSpace(vendor.VendorName) ||
            string.IsNullOrWhiteSpace(vendor.TerritoryName) ||
            string.IsNullOrWhiteSpace(vendor.CatalogVersion) ||
            !vendor.HasExactCatalogMembership() ||
            vendor.PurchasedQuantity != ComparedAllocation.ConsumedQuantity ||
            vendor.UnitPriceGil == 0 ||
            vendor.TotalPriceGil != ComparedAllocation.TotalGil)
        {
            errors.Add("A compared gil-vendor allocation requires complete exact vendor evidence.");
            return false;
        }

        try
        {
            if (checked((ulong)vendor.PurchasedQuantity * vendor.UnitPriceGil) != vendor.TotalPriceGil)
            {
                errors.Add("Compared vendor quantity, unit price, and total price must agree exactly.");
                return false;
            }
        }
        catch (OverflowException)
        {
            errors.Add("Compared vendor price arithmetic overflowed.");
            return false;
        }
        return true;
    }

    private static bool InvalidSource(List<string> errors)
    {
        errors.Add("Compared gear allocation uses an unsupported typed source identity.");
        return false;
    }

    private void ValidateBurden(List<string> errors)
    {
        var craftNodeCount = Plan.ExpandedNodes.Count(node => node.Kind == OutfitterCraftNodeKind.Craft);
        var subcraftNodeCount = Math.Max(0, craftNodeCount - 1);
        var distinctMaterialCount = Plan.TerminalMaterials
            .Select(line => line.MaterialKey)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var marketSourceCount = Plan.TerminalMaterials
            .Select(line => line.Source)
            .OfType<OutfitterMarketMaterialSourceIdentity>()
            .Select(source => source.PhysicalSourceKey)
            .Distinct()
            .Count();
        var vendorSourceCount = Plan.TerminalMaterials
            .Select(line => line.Source)
            .OfType<OutfitterGilVendorMaterialSourceIdentity>()
            .Select(source => source.PhysicalSourceKey)
            .Distinct()
            .Count();

        if (Burden is null ||
            Burden.CraftNodeCount != craftNodeCount ||
            Burden.SubcraftNodeCount != subcraftNodeCount ||
            Burden.DistinctMaterialCount != distinctMaterialCount ||
            Burden.MarketSourceCount != marketSourceCount ||
            Burden.VendorSourceCount != vendorSourceCount)
        {
            errors.Add("Craft acquisition burden is inconsistent with the frozen recipe tree and terminal sources.");
        }
    }

    private static CraftCostComparisonValidation Invalid(IEnumerable<string> errors) =>
        new(false, errors.Distinct(StringComparer.Ordinal).ToImmutableArray());

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

internal static class ComparedGearAllocationIdentity
{
    public static bool TryCompute(ComparedGearAllocation? allocation, out string identity)
    {
        identity = string.Empty;
        if (allocation?.AllocationKey?.OfferKey is null || allocation.Source is null)
            return false;
        try
        {
            var canonical = new StringBuilder();
            Append(canonical, allocation);
            identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    public static void Append(StringBuilder canonical, ComparedGearAllocation allocation)
    {
        var key = allocation.AllocationKey;
        var offer = key.OfferKey;
        canonical.Append("|allocation|").AppendInvariant(offer.ItemId)
            .Append('|').AppendInvariant((int)offer.Quality)
            .Append('|').AppendInvariant((int)offer.SourceKind);
        AppendString(canonical, offer.SourceCatalogKey);
        AppendString(canonical, key.ObservationId);
        canonical.Append('|').AppendInvariant(allocation.ConsumedQuantity).Append('|').AppendInvariant(allocation.TotalGil);

        switch (allocation.Source)
        {
            case ComparedGearMarketSourceIdentity market:
                canonical.Append("|market|").Append(market.Evidence.GenerationId)
                    .Append('|').AppendInvariant(market.Evidence.Revision);
                AppendString(canonical, market.Evidence.ContentIdentity);
                AppendString(canonical, market.Evidence.SourceKey);
                AppendListing(canonical, market.Listing);
                canonical.Append('|').AppendInvariant(market.ConsumedQuantity)
                    .Append('|').AppendInvariant(market.PurchasedQuantity)
                    .Append('|').AppendInvariant(market.SurplusQuantity)
                    .Append('|').AppendInvariant(market.TotalPriceGil);
                break;
            case ComparedGearOwnedSourceIdentity owned:
                canonical.Append("|owned|").Append(owned.CaptureId)
                    .Append('|').Append(owned.CaptureGenerationId)
                    .Append('|').AppendInvariant(owned.CapturedAtUtc.UtcDateTime.Ticks);
                AppendString(canonical, owned.EquipmentSnapshotFingerprint);
                canonical
                    .Append('|').AppendInvariant(owned.Instance.Character.LocalContentId)
                    .Append('|').AppendInvariant(owned.Instance.Character.HomeWorldId);
                AppendString(canonical, owned.Instance.Character.Name);
                AppendString(canonical, owned.Instance.Container);
                canonical.Append('|').AppendInvariant(owned.Instance.SlotIndex)
                    .Append('|').AppendInvariant(owned.Instance.ItemId)
                    .Append('|').AppendInvariant((int)owned.Instance.Quality)
                    .Append('|').AppendInvariant(owned.Instance.Quantity);
                break;
            case ComparedGearGilVendorSourceIdentity vendor:
                canonical.Append("|vendor|").AppendInvariant(vendor.ItemId)
                    .Append('|').AppendInvariant((int)vendor.Quality)
                    .Append('|').AppendInvariant(vendor.ShopId)
                    .Append('|').AppendInvariant(vendor.VendorId);
                AppendString(canonical, vendor.VendorName);
                canonical.Append('|').AppendInvariant(vendor.TerritoryId);
                AppendString(canonical, vendor.TerritoryName);
                AppendString(canonical, vendor.CatalogVersion);
                canonical.Append('|').AppendInvariant(vendor.PurchasedQuantity)
                    .Append('|').AppendInvariant(vendor.UnitPriceGil)
                    .Append('|').AppendInvariant(vendor.TotalPriceGil);
                break;
            default:
                throw new InvalidOperationException("Unsupported compared gear source identity.");
        }
    }

    private static void AppendListing(StringBuilder canonical, CraftMarketListingIdentity listing)
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

internal sealed class ContractOnlyCraftCostComparisonJsonConverter : JsonConverter<CraftCostComparison>
{
    public override CraftCostComparison Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Contract-only craft comparisons are process-local and have no replay DTO.");

    public override void Write(Utf8JsonWriter writer, CraftCostComparison value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Contract-only craft comparisons are process-local and have no replay DTO.");
}

internal sealed class NewtonsoftContractOnlyCraftCostComparisonJsonConverter : Newtonsoft.Json.JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(CraftCostComparison);

    public override object? ReadJson(
        Newtonsoft.Json.JsonReader reader,
        Type objectType,
        object? existingValue,
        Newtonsoft.Json.JsonSerializer serializer) =>
        throw new NotSupportedException("Contract-only craft comparisons are process-local and have no replay DTO.");

    public override void WriteJson(
        Newtonsoft.Json.JsonWriter writer,
        object? value,
        Newtonsoft.Json.JsonSerializer serializer) =>
        throw new NotSupportedException("Contract-only craft comparisons are process-local and have no replay DTO.");
}
