using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Equipment;
using Lumina.Excel.Sheets;
using MarketMafioso.Squire.Observation;

namespace MarketMafioso.Squire.Outfitter.Utility;

public sealed record MinerBotanistAdvisorCatalogResult(
    string CoverageLabel,
    IReadOnlyList<uint> MarketItemIds,
    IReadOnlyList<EquipmentLoadoutOffer> VendorOffers,
    IReadOnlyDictionary<uint, EquipmentItemDefinition> Definitions,
    string Diagnostic);

/// <summary>
/// Static, patch-matched discovery for the first advisor release. It declares a bounded level
/// horizon and never reads character, inventory, agent, or gearset state.
/// </summary>
public sealed class MinerBotanistAdvisorCatalog
{
    public const uint MaximumEquipLevel = 100;

    private readonly IDataManager dataManager;
    private readonly LuminaRenderedEquipmentDefinitionLookup definitions;
    private readonly OutfitterGilVendorCatalog vendors;
    private readonly Dictionary<(uint ClassJobId, uint CharacterLevel), MinerBotanistAdvisorCatalogResult> byTarget = [];

    public MinerBotanistAdvisorCatalog(IDataManager dataManager)
    {
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        definitions = new(dataManager);
        vendors = new(dataManager);
    }

    public MinerBotanistAdvisorCatalogResult Build(uint classJobId, uint characterLevel, IAdvisorStatFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        if (!family.SupportedClassJobIds.Contains(classJobId))
            throw new ArgumentOutOfRangeException(nameof(classJobId), $"The advisor catalog supports the {family.CoverageJobLabel} player scope only.");
        if (characterLevel is < 1 or > MaximumEquipLevel)
            throw new ArgumentOutOfRangeException(nameof(characterLevel), "The player advisor supports levels 1 through 100.");
        if (byTarget.TryGetValue((classJobId, characterLevel), out var cached))
            return cached;

        var minimumEquipLevel = MinimumEquipLevel(characterLevel);

        var itemSheet = dataManager.GetExcelSheet<Item>() ?? throw new InvalidOperationException("Item sheet is unavailable.");
        var specialBonusSheet = dataManager.GetExcelSheet<ItemSpecialBonus>() ?? throw new InvalidOperationException("ItemSpecialBonus sheet is unavailable.");
        var found = new Dictionary<uint, EquipmentItemDefinition>();
        var marketItemIds = new List<uint>();
        var vendorOffers = new List<EquipmentLoadoutOffer>();
        var scanned = 0;
        var unresolved = 0;
        var wrongJob = 0;
        var incompleteProfile = 0;
        var unmodeled = 0;
        var acceptedSamples = new List<string>();
        var unmodeledSamples = new List<string>();
        foreach (var item in itemSheet.Where(value =>
                     value.RowId > 0 &&
                     value.LevelEquip >= minimumEquipLevel &&
                     value.LevelEquip <= characterLevel &&
                     value.EquipSlotCategory.RowId != 0))
        {
            scanned++;
            var definition = definitions.FindByItemId(item.RowId).SingleOrDefault();
            if (definition is null)
            {
                unresolved++;
                continue;
            }
            if (!definition.EligibleClassJobIds.Contains(classJobId))
            {
                wrongJob++;
                continue;
            }
            if (!HasRelevantCompleteProfile(definition, family))
            {
                incompleteProfile++;
                continue;
            }
            if (AdvisorEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(definition))
            {
                unmodeled++;
                if (unmodeledSamples.Count < 4)
                {
                    var specialBonus = specialBonusSheet.GetRowOrDefault(definition.ItemSpecialBonusId);
                    unmodeledSamples.Add(
                        $"{definition.Name}({definition.ItemId}:bonus={definition.ItemSpecialBonusId}:{specialBonus?.Name}:{specialBonus?.RequirementText},action={definition.ItemActionId},restriction={definition.EquipRestrictionId},gc={definition.GrandCompanyId},pvp={definition.RequiredPvpRank})");
                }
                continue;
            }
            found[definition.ItemId] = definition;
            if (acceptedSamples.Count < 4)
                acceptedSamples.Add(
                    $"{definition.Name}({definition.ItemId}:untradable={item.IsUntradable},search={item.ItemSearchCategory.RowId})");

            if (!item.IsUntradable && item.ItemSearchCategory.RowId != 0)
                marketItemIds.Add(definition.ItemId);

            var vendor = vendors.FindOffers(definition.ItemId)
                .OrderBy(value => value.UnitPriceGil)
                .ThenBy(value => value.SourceLabel, StringComparer.Ordinal)
                .FirstOrDefault();
            if (vendor is not null)
                vendorOffers.Add(new(
                    definition,
                    EquipmentAcquisitionSourceKind.GilVendor,
                    vendor.SourceLabel,
                    vendor.UnitPriceGil,
                    Quality: EquipmentQuality.Normal,
                    SourceCatalogKey: $"vendor:{vendor.ShopId}:{vendor.VendorId}:{vendor.TerritoryId}:{definition.ItemId}"));
        }

        var diagnostic =
            $"Catalog {minimumEquipLevel}-{characterLevel}: scanned {scanned:N0}; accepted {found.Count:N0}; " +
            $"market {marketItemIds.Distinct().Count():N0}; gil vendor {vendorOffers.Count:N0}; " +
            $"unresolved {unresolved:N0}; wrong job {wrongJob:N0}; incomplete relevant stats {incompleteProfile:N0}; unmodeled effect/restriction {unmodeled:N0}. " +
            $"Accepted samples [{string.Join("; ", acceptedSamples)}]. Rejected samples [{string.Join("; ", unmodeledSamples)}].";
        var result = new MinerBotanistAdvisorCatalogResult(
            $"Current equipped player state plus level {minimumEquipLevel}-{characterLevel} {family.CoverageJobLabel} market and gil-vendor equipment; owned inventory is not yet observed.",
            marketItemIds.Distinct().Order().ToArray(),
            vendorOffers,
            found,
            diagnostic);
        byTarget[(classJobId, characterLevel)] = result;
        return result;
    }

    internal static uint MinimumEquipLevel(uint characterLevel) => characterLevel switch
    {
        <= 10 => 1,
        _ => Math.Max(1u, characterLevel - 10),
    };

    internal static bool HasRelevantCompleteProfile(EquipmentItemDefinition definition, IAdvisorStatFamily family) =>
        Relevant(definition.StatProfile, family) || Relevant(definition.HighQualityStatProfile, family);

    private static bool Relevant(EquipmentStatProfile? profile, IAdvisorStatFamily family) =>
        profile is { IsComplete: true } && family.VectorFromDefinition(profile).Components.Any(value => value.Units > 0);

}
