using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

internal static class AdvisorEquipmentSupportPolicy
{
    public static bool HasUnmodeledEffectOrRestriction(EquipmentItemDefinition definition) =>
        !HasModeledSpecialBonus(definition) ||
        definition.ItemActionId != 0 ||
        definition.HasUnmodeledEquipRestriction;

    private static bool HasModeledSpecialBonus(EquipmentItemDefinition definition) =>
        definition.ItemSpecialBonusId == 0 ||
        // Patch 7.51 ItemSpecialBonus row 1 has no name or requirement text and param 0.
        // Crafted equipment uses it alongside the ordinary NQ/HQ BaseParam arrays already
        // modeled by EquipmentStatProfile; no additional gameplay effect is hidden.
        definition is { ItemSpecialBonusId: 1, ItemSpecialBonusParam: 0 };
}
