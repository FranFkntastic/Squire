using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace Squire.Tests.Features;

public sealed class AdvisorEquipmentSupportPolicyTests
{
    [Fact]
    public void Equip_restriction_is_not_an_unmodeled_stat_effect()
    {
        var restricted = Definition(equipRestrictionId: 2);

        Assert.False(AdvisorEquipmentSupportPolicy.HasUnmodeledEffect(restricted));
        Assert.True(AdvisorEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(restricted));
    }

    [Fact]
    public void Item_action_remains_an_unmodeled_stat_effect()
    {
        var actionable = Definition(itemActionId: 1);

        Assert.True(AdvisorEquipmentSupportPolicy.HasUnmodeledEffect(actionable));
        Assert.True(AdvisorEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(actionable));
    }

    private static EquipmentItemDefinition Definition(uint equipRestrictionId = 0, uint itemActionId = 0) => new(
        1,
        "Test gear",
        1,
        1,
        EquipmentSlot.Hands,
        new HashSet<uint> { TankUtilityProfile.MarauderClassJobId },
        1,
        true,
        false,
        null,
        null,
        null,
        null,
        null,
        null,
        false,
        EquipRestrictionId: equipRestrictionId,
        ItemActionId: itemActionId);
}
