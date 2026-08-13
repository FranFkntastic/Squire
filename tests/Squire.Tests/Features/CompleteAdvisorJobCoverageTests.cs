using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace Squire.Tests.Features;

public sealed class CompleteAdvisorJobCoverageTests
{
    [Fact]
    public void EveryOriginalScopeClassJobHasExactlyOneFamilyExceptFisher()
    {
        for (uint classJobId = 1; classJobId <= 42; classJobId++)
        {
            var matches = AdvisorStatFamilies.All.Where(family => family.SupportedClassJobIds.Contains(classJobId)).ToArray();
            if (classJobId == AdvisorStatFamilies.FisherClassJobId)
                Assert.Empty(matches);
            else
                Assert.Single(matches);
        }
    }

    [Theory]
    [InlineData(1u, "Tank")]
    [InlineData(19u, "Tank")]
    [InlineData(2u, "Strength melee DPS")]
    [InlineData(39u, "Strength melee DPS")]
    [InlineData(29u, "Scouting melee DPS")]
    [InlineData(41u, "Scouting melee DPS")]
    [InlineData(5u, "Physical ranged DPS")]
    [InlineData(38u, "Physical ranged DPS")]
    [InlineData(6u, "Healer")]
    [InlineData(40u, "Healer")]
    [InlineData(7u, "Magical ranged DPS")]
    [InlineData(42u, "Magical ranged DPS")]
    [InlineData(8u, "Crafting")]
    [InlineData(15u, "Crafting")]
    [InlineData(16u, "Gathering")]
    [InlineData(17u, "Gathering")]
    public void ResolvesExpectedFamily(uint classJobId, string label) =>
        Assert.Equal(label, AdvisorStatFamilies.Resolve(classJobId)!.FamilyLabel);

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(29u)]
    [InlineData(6u)]
    [InlineData(7u)]
    public void ConservativeFamiliesAuthorizeOnlyComponentwiseNoLossWithUnchangedSpeed(uint classJobId)
    {
        var family = AdvisorStatFamilies.Resolve(classJobId)!;
        var baseline = family.RelevantSemantics.ToDictionary(semantic => semantic, _ => 100);
        var fixedStats = family.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0);
        var model = family.CreateUtilityModel(family.ProfileDescriptor.DefaultContextId, baseline, fixedStats, classJobId, 100);

        var improved = baseline.ToDictionary(value => value.Key, value => value.Value);
        improved[family.RelevantSemantics[0]]++;
        var evaluation = model.Evaluate(family.VectorFromSemantics(improved));
        Assert.True(family.AssessAuthority(model, evaluation, 0).AdvisorMayConsider);

        var speed = family.RelevantSemantics.Single(semantic => semantic is EquipmentStatSemantic.SkillSpeed or EquipmentStatSemantic.SpellSpeed);
        var speedTrade = improved.ToDictionary(value => value.Key, value => value.Value);
        speedTrade[speed]--;
        var traded = model.Evaluate(family.VectorFromSemantics(speedTrade));
        Assert.False(family.AssessAuthority(model, traded, 0).AdvisorMayConsider);
    }

    [Fact]
    public void ShieldTankCarriesBothShieldScalarsThroughDefinitionVector()
    {
        var family = AdvisorStatFamilies.Resolve(19)!;
        var profile = new EquipmentStatProfile([], 10, 0, 20, 21, true, BlockStrength: 30, BlockRate: 31);

        var vector = family.VectorFromDefinition(profile);

        Assert.Equal(30, vector.Get("blockstrength"));
        Assert.Equal(31, vector.Get("blockrate"));
        Assert.Contains(EquipmentStatSemantic.BlockStrength, family.RelevantSemantics);
        Assert.Contains(EquipmentStatSemantic.BlockRate, family.RelevantSemantics);
    }
}
