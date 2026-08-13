using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

internal static class AdvisorMarketScopeSelector
{
    public const int MaximumSampleSize = 96;
    private const int MinimumPerSlot = 6;

    public static IReadOnlyList<uint> Select(MinerBotanistAdvisorCatalogResult catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.MarketItemIds.Count <= MaximumSampleSize)
            return catalog.MarketItemIds.Order().ToArray();

        var ranked = catalog.MarketItemIds
            .Select(itemId => catalog.Definitions.GetValueOrDefault(itemId))
            .Where(definition => definition is not null)
            .Cast<EquipmentItemDefinition>()
            .OrderByDescending(definition => definition.ItemLevel)
            .ThenByDescending(definition => definition.EquipLevel)
            .ThenBy(definition => definition.ItemId)
            .ToArray();
        var selected = ranked
            .GroupBy(definition => definition.Slot)
            .OrderBy(group => group.Key)
            .SelectMany(group => group.Take(MinimumPerSlot))
            .Select(definition => definition.ItemId)
            .ToHashSet();
        foreach (var definition in ranked)
        {
            if (selected.Count >= MaximumSampleSize)
                break;
            selected.Add(definition.ItemId);
        }
        return selected.Order().ToArray();
    }
}
