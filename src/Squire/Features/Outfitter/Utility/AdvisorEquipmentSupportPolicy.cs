using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

internal static class AdvisorEquipmentSupportPolicy
{
    public static bool HasUnmodeledEffectOrRestriction(EquipmentItemDefinition definition) =>
        HasUnmodeledEffect(definition) ||
        definition.HasUnmodeledEquipRestriction;

    /// <summary>
    /// Returns whether the item's stat contribution can be changed by an effect we do not model.
    /// Equip restrictions are deliberately separate: a currently equipped item has already proved
    /// that the active character can wear it, and restrictions do not alter its rendered stats.
    /// Candidate and saved-gearset paths must continue to use <see cref="HasUnmodeledEffectOrRestriction"/>.
    /// </summary>
    public static bool HasUnmodeledEffect(EquipmentItemDefinition definition) =>
        !HasModeledSpecialBonus(definition) ||
        definition.ItemActionId != 0;

    private static bool HasModeledSpecialBonus(EquipmentItemDefinition definition) =>
        definition.ItemSpecialBonusId == 0 ||
        // Patch 7.51 ItemSpecialBonus row 1 has no name or requirement text and param 0.
        // Crafted equipment uses it alongside the ordinary NQ/HQ BaseParam arrays already
        // modeled by EquipmentStatProfile; no additional gameplay effect is hidden.
        definition is { ItemSpecialBonusId: 1, ItemSpecialBonusParam: 0 };
}
