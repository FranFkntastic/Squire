using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class TankUtilityProfileTests
{
    private static readonly TankUtilityStats Baseline = new(
        1_000, 1_200, 20, 500, 450, 200, 180, 140, 160, 120);

    [Fact]
    public void Role_registry_contains_supported_two_handed_tanks_and_excludes_paladin()
    {
        var role = AdvisorCombatRoles.TwoHandedTank;

        Assert.Equal("two-handed-tank", role.Id);
        Assert.Equal(
            [TankUtilityProfile.MarauderClassJobId, TankUtilityProfile.WarriorClassJobId,
                TankUtilityProfile.DarkKnightClassJobId, TankUtilityProfile.GunbreakerClassJobId],
            role.ClassJobIds.Order().ToArray());
        Assert.Same(role, AdvisorCombatRoles.Resolve(TankUtilityProfile.MarauderClassJobId));
        Assert.Null(AdvisorCombatRoles.Resolve(19));
    }

    [Theory]
    [InlineData(TankUtilityProfile.MarauderClassJobId, 1u)]
    [InlineData(TankUtilityProfile.WarriorClassJobId, 30u)]
    [InlineData(TankUtilityProfile.DarkKnightClassJobId, 30u)]
    [InlineData(TankUtilityProfile.GunbreakerClassJobId, 60u)]
    public void Supported_jobs_accept_their_unlock_level(uint classJobId, uint level)
    {
        var profile = Profile(classJobId, level);

        Assert.Equal(
            UpgradeAssessment.ClearImprovement,
            profile.Evaluate(Baseline with { Vitality = Baseline.Vitality + 1 }).Assessment);
    }

    [Theory]
    [InlineData(TankUtilityProfile.WarriorClassJobId, 29u)]
    [InlineData(TankUtilityProfile.DarkKnightClassJobId, 29u)]
    [InlineData(TankUtilityProfile.GunbreakerClassJobId, 59u)]
    [InlineData(19u, 100u)]
    public void Unsupported_job_or_pre_unlock_level_fails_closed(uint classJobId, uint level)
    {
        var profile = Profile(classJobId, level);

        Assert.Equal(
            UpgradeAssessment.Unsupported,
            profile.Evaluate(Baseline with { Vitality = Baseline.Vitality + 1 }).Assessment);
    }

    [Fact]
    public void Exact_no_loss_gain_is_authoritative()
    {
        var profile = Profile(TankUtilityProfile.MarauderClassJobId, 10);
        var candidate = profile.Evaluate(Baseline with
        {
            Strength = Baseline.Strength + 2,
            PhysicalDamage = Baseline.PhysicalDamage + 1,
        });

        var authority = profile.AssessAuthority(candidate, 500);

        Assert.Equal(UpgradeAssessment.ClearImprovement, candidate.Assessment);
        Assert.True(authority.AdvisorMayConsider);
        Assert.Contains("no-loss-strength-gain", authority.GainedCapabilityIds);
        Assert.Contains("no-loss-physical-damage-gain", authority.GainedCapabilityIds);
        Assert.Empty(authority.Reasons);
    }

    [Fact]
    public void A_trade_or_skill_speed_change_never_receives_authority()
    {
        var profile = Profile(TankUtilityProfile.MarauderClassJobId, 10);
        var trade = profile.Evaluate(Baseline with
        {
            Strength = Baseline.Strength + 10,
            PhysicalDefense = Baseline.PhysicalDefense - 1,
        });
        var speed = profile.Evaluate(Baseline with { SkillSpeed = Baseline.SkillSpeed + 1 });

        var tradeAuthority = profile.AssessAuthority(trade, 0);
        var speedAuthority = profile.AssessAuthority(speed, 0);

        Assert.False(tradeAuthority.AdvisorMayConsider);
        Assert.Contains(tradeAuthority.Reasons, reason => reason.Contains("trades", StringComparison.OrdinalIgnoreCase));
        Assert.False(speedAuthority.AdvisorMayConsider);
        Assert.Contains(speedAuthority.Reasons, reason => reason.Contains("Skill Speed changed", StringComparison.Ordinal));
    }

    [Fact]
    public void Definition_vector_includes_tank_parameters_damage_and_defenses()
    {
        var profile = new EquipmentStatProfile(
            [
                new(1, EquipmentStatSemantic.Strength, 40, false),
                new(3, EquipmentStatSemantic.Vitality, 50, false),
                new(27, EquipmentStatSemantic.CriticalHit, 20, false),
                new(44, EquipmentStatSemantic.Determination, 18, false),
                new(22, EquipmentStatSemantic.DirectHit, 14, false),
                new(19, EquipmentStatSemantic.Tenacity, 16, false),
                new(45, EquipmentStatSemantic.SkillSpeed, 12, false),
            ],
            PhysicalDamage: 21,
            MagicalDamage: 0,
            PhysicalDefense: 52,
            MagicalDefense: 46,
            IsComplete: true);

        var vector = TankAdvisorStatFamily.Instance.VectorFromDefinition(profile);

        Assert.Equal(40, vector.Get("strength"));
        Assert.Equal(21, vector.Get("physical-damage"));
        Assert.Equal(52, vector.Get("physical-defense"));
        Assert.Equal(46, vector.Get("magical-defense"));
        Assert.Equal(16, vector.Get("tenacity"));
        Assert.True(MinerBotanistAdvisorCatalog.HasRelevantCompleteProfile(Definition(profile), TankAdvisorStatFamily.Instance));
    }

    private static TankUtilityProfile Profile(uint classJobId, uint level) => new(
        TankUtilityContextKind.GeneralCombat,
        Baseline,
        classJobId,
        level);

    private static EquipmentItemDefinition Definition(EquipmentStatProfile profile) => new(
        60_000,
        "Tank fixture",
        10,
        10,
        EquipmentSlot.MainHand,
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
        false,
        StatProfile: profile);
}
