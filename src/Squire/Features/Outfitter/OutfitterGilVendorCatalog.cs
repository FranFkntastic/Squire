using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MarketMafioso.Squire.Outfitter;

public sealed record OutfitterGilVendorOffer(
    uint ItemId,
    uint ShopId,
    uint VendorId,
    string VendorName,
    uint TerritoryId,
    string TerritoryName,
    uint UnitPriceGil)
{
    public string SourceLabel => $"{VendorName} · {TerritoryName}";
}

/// <summary>
/// Builds the conservative, travel-ready subset of normal gil-shop offers.
/// A GilShopItem row alone is not enough: recovery and event shops share that
/// sheet, so an offer is admitted only when it has no unlock requirements and
/// can be tied to a concrete NPC spawn in the world.
/// </summary>
public sealed class OutfitterGilVendorCatalog
{
    private readonly IDataManager? dataManager;
    private IReadOnlyDictionary<uint, IReadOnlyList<OutfitterGilVendorOffer>>? offersByItemId;
    private string? catalogVersion;
    private OutfitterGilVendorCatalogReference? catalogReference;

    public OutfitterGilVendorCatalog(IDataManager dataManager)
    {
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
    }

    private OutfitterGilVendorCatalog(IReadOnlyList<OutfitterGilVendorOffer> offers)
    {
        offersByItemId = Normalize(offers);
        catalogVersion = ComputeCatalogVersion(offersByItemId);
    }

    internal static OutfitterGilVendorCatalog FromTrustedSnapshot(
        IEnumerable<OutfitterGilVendorOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        return new(offers.ToArray());
    }

    internal string CatalogVersion
    {
        get
        {
            EnsureBuilt();
            return catalogVersion!;
        }
    }

    internal OutfitterGilVendorCatalogReference CaptureReference()
    {
        EnsureBuilt();
        return catalogReference ??= OutfitterGilVendorCatalogReference.FromCatalog(
            catalogVersion!,
            offersByItemId!.Values.SelectMany(offers => offers));
    }

    public IReadOnlyList<OutfitterGilVendorOffer> FindOffers(uint itemId)
    {
        EnsureBuilt();
        return offersByItemId!.TryGetValue(itemId, out var offers) ? offers : [];
    }

    private void EnsureBuilt()
    {
        if (offersByItemId is not null)
            return;
        offersByItemId = Build();
        catalogVersion = ComputeCatalogVersion(offersByItemId);
    }

    private IReadOnlyDictionary<uint, IReadOnlyList<OutfitterGilVendorOffer>> Build()
    {
        if (dataManager is null)
            throw new InvalidOperationException("The gil-vendor catalog has no game-data source.");
        var shops = dataManager.GetExcelSheet<GilShop>()
            ?? throw new InvalidOperationException("GilShop sheet unavailable.");
        var residents = dataManager.GetExcelSheet<ENpcResident>()
            ?? throw new InvalidOperationException("ENpcResident sheet unavailable.");
        var levels = dataManager.GetExcelSheet<Level>()
            ?? throw new InvalidOperationException("Level sheet unavailable.");

        var directVendorsByShop = dataManager.GetExcelSheet<ENpcBase>()
            .SelectMany(npc => EnumerateGilShopIds(npc)
                .Select(shopId => new { ShopId = shopId, VendorId = npc.RowId }))
            .GroupBy(value => value.ShopId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => value.VendorId).Distinct().ToArray());

        var spawnsByVendor = levels
            .Where(level => level.Type == 8 && level.Object.Is<ENpcBase>())
            .GroupBy(level => level.Object.RowId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var offers = new List<OutfitterGilVendorOffer>();
        foreach (var row in dataManager.GetSubrowExcelSheet<GilShopItem>().Flatten())
        {
            if (row.Item.RowId == 0 || row.QuestRequired.Any(quest => quest.RowId != 0) ||
                row.AchievementRequired.RowId != 0)
                continue;

            var shop = shops.GetRowOrDefault(row.RowId);
            if (shop is null || shop.Value.Quest.RowId != 0 || shop.Value.FestivalId != 0 ||
                !directVendorsByShop.TryGetValue(row.RowId, out var vendorIds))
                continue;

            var item = row.Item.Value;
            if (item.PriceMid == 0)
                continue;

            foreach (var vendorId in vendorIds)
            {
                if (!spawnsByVendor.TryGetValue(vendorId, out var spawns))
                    continue;

                var vendorName = residents.GetRowOrDefault(vendorId)?.Singular.ToString();
                if (string.IsNullOrWhiteSpace(vendorName))
                    vendorName = shop.Value.Name.ToString();
                if (string.IsNullOrWhiteSpace(vendorName))
                    vendorName = "Merchant";
                vendorName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(vendorName);

                foreach (var spawn in spawns)
                {
                    var territory = spawn.Territory.Value;
                    var territoryName = territory.PlaceName.Value.Name.ToString();
                    if (string.IsNullOrWhiteSpace(territoryName))
                        territoryName = territory.PlaceNameZone.Value.Name.ToString();
                    if (string.IsNullOrWhiteSpace(territoryName))
                        continue;

                    offers.Add(new(
                        row.Item.RowId,
                        row.RowId,
                        vendorId,
                        vendorName,
                        spawn.Territory.RowId,
                        territoryName,
                        item.PriceMid));
                }
            }
        }

        return Normalize(offers);
    }

    private static IReadOnlyDictionary<uint, IReadOnlyList<OutfitterGilVendorOffer>> Normalize(
        IReadOnlyList<OutfitterGilVendorOffer> offers)
    {
        if (offers.Any(offer => offer is null ||
            offer.ItemId == 0 ||
            offer.ShopId == 0 ||
            offer.VendorId == 0 ||
            offer.TerritoryId == 0 ||
            offer.UnitPriceGil == 0 ||
            string.IsNullOrWhiteSpace(offer.VendorName) ||
            string.IsNullOrWhiteSpace(offer.TerritoryName)))
        {
            throw new InvalidOperationException("Gil-vendor snapshots require complete concrete vendor offers.");
        }

        var conflicts = offers
            .GroupBy(offer => (offer.ItemId, offer.ShopId, offer.VendorId, offer.TerritoryId))
            .Where(group => group.Distinct().Count() != 1)
            .ToArray();
        if (conflicts.Length != 0)
            throw new InvalidOperationException("One physical gil-vendor source cannot carry conflicting offer evidence.");

        return offers
            .GroupBy(offer => offer.ItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OutfitterGilVendorOffer>)group
                    .OrderBy(offer => offer.UnitPriceGil)
                    .ThenBy(offer => offer.TerritoryName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(offer => offer.VendorName, StringComparer.OrdinalIgnoreCase)
                    .DistinctBy(offer => (offer.ShopId, offer.VendorId, offer.TerritoryId))
                    .ToArray());
    }

    private static string ComputeCatalogVersion(
        IReadOnlyDictionary<uint, IReadOnlyList<OutfitterGilVendorOffer>> offersByItem)
    {
        var canonical = new StringBuilder("marketmafioso-gil-vendor-catalog/v1");
        foreach (var offer in offersByItem.Values
                     .SelectMany(offers => offers)
                     .OrderBy(offer => offer.ItemId)
                     .ThenBy(offer => offer.ShopId)
                     .ThenBy(offer => offer.VendorId)
                     .ThenBy(offer => offer.TerritoryId))
        {
            canonical.Append('|').Append(offer.ItemId.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(offer.ShopId.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(offer.VendorId.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(offer.TerritoryId.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(offer.UnitPriceGil.ToString(CultureInfo.InvariantCulture));
            AppendCanonical(canonical, offer.VendorName);
            AppendCanonical(canonical, offer.TerritoryName);
        }
        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))}";
    }

    private static void AppendCanonical(StringBuilder canonical, string value) =>
        canonical.Append('|')
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);

    private static IEnumerable<uint> EnumerateGilShopIds(ENpcBase npc)
    {
        foreach (var data in npc.ENpcData)
        {
            if (data.Is<GilShop>())
            {
                yield return data.RowId;
                continue;
            }

            if (data.Is<PreHandler>() && data.TryGetValue(out PreHandler preHandler) &&
                preHandler.Target.Is<GilShop>())
            {
                yield return preHandler.Target.RowId;
                continue;
            }

            if (data.Is<TopicSelect>() && data.TryGetValue(out TopicSelect topic))
            {
                foreach (var shop in topic.Shop.Where(shop => shop.Is<GilShop>()))
                    yield return shop.RowId;
            }
        }
    }
}

internal sealed record OutfitterGilVendorCatalogReference
{
    private OutfitterGilVendorCatalogReference(
        string catalogVersion,
        ImmutableArray<OutfitterGilVendorOffer> offers)
    {
        CatalogVersion = catalogVersion;
        Offers = offers;
    }

    public string CatalogVersion { get; }
    public ImmutableArray<OutfitterGilVendorOffer> Offers { get; }

    internal static OutfitterGilVendorCatalogReference FromCatalog(
        string catalogVersion,
        IEnumerable<OutfitterGilVendorOffer> offers)
    {
        if (string.IsNullOrWhiteSpace(catalogVersion))
            throw new InvalidOperationException("A gil-vendor catalog reference requires an exact version.");
        var members = offers
            .OrderBy(offer => offer.ItemId)
            .ThenBy(offer => offer.ShopId)
            .ThenBy(offer => offer.VendorId)
            .ThenBy(offer => offer.TerritoryId)
            .ToImmutableArray();
        return new(catalogVersion, members);
    }

    internal bool Matches(OutfitterGilVendorOffer offer) =>
        offer is not null && Offers.Any(member => member == offer);
}
