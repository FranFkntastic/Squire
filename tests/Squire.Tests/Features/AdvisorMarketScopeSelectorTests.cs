using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace Squire.Tests.Features;

public sealed class AdvisorMarketScopeSelectorTests
{
    [Fact]
    public void Broad_catalog_is_bounded_while_preserving_every_slot_and_strongest_items()
    {
        var slots = Enum.GetValues<EquipmentSlot>()
            .Where(slot => slot is not EquipmentSlot.Unknown and not EquipmentSlot.SoulCrystal)
            .ToArray();
        var definitions = slots
            .SelectMany((slot, slotIndex) => Enumerable.Range(1, 12)
                .Select(rank => Definition((uint)(slotIndex * 100 + rank), slot, (uint)rank)))
            .ToDictionary(definition => definition.ItemId);
        var catalog = new MinerBotanistAdvisorCatalogResult(
            "fixture",
            definitions.Keys.Order().ToArray(),
            [],
            definitions,
            "fixture");

        var selected = AdvisorMarketScopeSelector.Select(catalog);

        Assert.Equal(AdvisorMarketScopeSelector.MaximumSampleSize, selected.Count);
        foreach (var slot in slots)
        {
            var selectedForSlot = selected.Count(itemId => definitions[itemId].Slot == slot);
            Assert.True(selectedForSlot >= 6, $"Expected at least six {slot} candidates, found {selectedForSlot}.");
            Assert.Contains(definitions.Values.Single(value => value.Slot == slot && value.ItemLevel == 12).ItemId, selected);
        }
    }

    [Fact]
    public void Small_catalog_remains_exhaustive()
    {
        var definitions = Enumerable.Range(1, 4)
            .Select(rank => Definition((uint)rank, EquipmentSlot.Body, (uint)rank))
            .ToDictionary(definition => definition.ItemId);
        var catalog = new MinerBotanistAdvisorCatalogResult("fixture", [4, 2, 1, 3], [], definitions, "fixture");

        Assert.Equal([1u, 2u, 3u, 4u], AdvisorMarketScopeSelector.Select(catalog));
    }

    private static EquipmentItemDefinition Definition(uint itemId, EquipmentSlot slot, uint itemLevel) => new(
        itemId,
        $"Item {itemId}",
        itemLevel,
        itemLevel,
        slot,
        new HashSet<uint> { TankUtilityProfile.MarauderClassJobId },
        1,
        true,
        false,
        true,
        true,
        1,
        true,
        false,
        true,
        false);
}
