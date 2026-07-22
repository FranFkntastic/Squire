using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class PhysicalRangedCatalogTests
{
    [Fact]
    public void Damage_only_weapon_profile_is_relevant_through_non_parameter_field()
    {
        var profile = new EquipmentStatProfile([], 141, 0, 0, 0, true);
        var definition = Definition(profile);

        Assert.True(MinerBotanistAdvisorCatalog.HasRelevantCompleteProfile(
            definition,
            PhysicalRangedAdvisorStatFamily.Instance));
    }

    [Fact]
    public void Incomplete_or_magical_only_profile_is_not_relevant()
    {
        Assert.False(MinerBotanistAdvisorCatalog.HasRelevantCompleteProfile(
            Definition(new([], 141, 0, 0, 0, false)),
            PhysicalRangedAdvisorStatFamily.Instance));
        Assert.False(MinerBotanistAdvisorCatalog.HasRelevantCompleteProfile(
            Definition(new([], 0, 141, 0, 0, true)),
            PhysicalRangedAdvisorStatFamily.Instance));
    }

    private static EquipmentItemDefinition Definition(EquipmentStatProfile profile) => new(
        50_000,
        "Catalog weapon",
        100,
        700,
        EquipmentSlot.MainHand,
        new HashSet<uint> { PhysicalRangedUtilityProfile.BardClassJobId },
        1,
        true,
        false,
        true,
        true,
        1,
        true,
        false,
        true,
        false,
        StatProfile: profile);
}
