using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

public enum TankUtilityContextKind
{
    GeneralCombat,
}

public sealed record TankUtilityStats(
    int Strength,
    int Vitality,
    int PhysicalDamage,
    int PhysicalDefense,
    int MagicalDefense,
    int CriticalHit,
    int Determination,
    int DirectHit,
    int Tenacity,
    int SkillSpeed);

/// <summary>
/// Conservative MRD/WAR/DRK/GNB ordering model. It can authorize only an exact
/// componentwise no-loss improvement while Skill Speed is unchanged. GLD/PLD are
/// deliberately excluded until shield block strength and block rate participate in
/// the same observed baseline, offer, replay, and revalidation contracts.
/// </summary>
public sealed class TankUtilityProfile : IEquipmentExactSolverUtilityModel,
    IEquipmentPartialDominanceCoordinateModel,
    IEquipmentSeparablePartialUtilityCanonicalizationModel
{
    public const uint MarauderClassJobId = 3;
    public const uint WarriorClassJobId = 21;
    public const uint DarkKnightClassJobId = 32;
    public const uint GunbreakerClassJobId = 37;
    public const string ProfileId = "squire.two-handed-tank.player";
    public const string ProfileVersion = "7.51-v1";
    public const string GeneralCombatContextId = "general-tank-combat";
    public const AdvisorProfileCalibrationState CalibrationState = AdvisorProfileCalibrationState.Supported;

    private const string StrengthKey = "strength";
    private const string VitalityKey = "vitality";
    private const string PhysicalDamageKey = "physical-damage";
    private const string PhysicalDefenseKey = "physical-defense";
    private const string MagicalDefenseKey = "magical-defense";
    private const string CriticalHitKey = "critical-hit";
    private const string DeterminationKey = "determination";
    private const string DirectHitKey = "direct-hit";
    private const string TenacityKey = "tenacity";
    private const string SkillSpeedKey = "skill-speed";
    private const double CapabilityStep = 1_000;

    private static readonly JobUtilityProfile Profile = new(
        new(ProfileId, ProfileVersion),
        "Player two-handed tank",
        new HashSet<uint>(AdvisorCombatRoles.TwoHandedTank.ClassJobIds),
        new HashSet<string>(StringComparer.Ordinal) { GeneralCombatContextId },
        [
            Rule(StrengthKey, EquipmentStatSemantic.Strength, "Strength is the shared tank damage attribute."),
            Rule(VitalityKey, EquipmentStatSemantic.Vitality, "Vitality is retained as a no-loss survivability component."),
            Rule(PhysicalDamageKey, EquipmentStatSemantic.PhysicalDamage, "Physical weapon damage is retained as a no-loss throughput component."),
            Rule(PhysicalDefenseKey, EquipmentStatSemantic.PhysicalDefense, "Physical defense is retained as a no-loss mitigation component."),
            Rule(MagicalDefenseKey, EquipmentStatSemantic.MagicalDefense, "Magical defense is retained as a no-loss mitigation component."),
            Rule(CriticalHitKey, EquipmentStatSemantic.CriticalHit, "Critical Hit is retained componentwise without claiming encounter-specific value."),
            Rule(DeterminationKey, EquipmentStatSemantic.Determination, "Determination is retained componentwise without claiming encounter-specific value."),
            Rule(DirectHitKey, EquipmentStatSemantic.DirectHit, "Direct Hit is retained componentwise without claiming encounter-specific value."),
            Rule(TenacityKey, EquipmentStatSemantic.Tenacity, "Tenacity is retained as a tank-specific no-loss component."),
            new(SkillSpeedKey, EquipmentStatSemantic.SkillSpeed, EquipmentUtilityRuleKind.ContextualOnly, 0d, null,
                "Skill Speed remains visible, but every change requires job- and rotation-specific timing analysis."),
        ],
        "Patch 7.51 conservative two-handed-tank profile. It authorizes only exact componentwise no-loss improvements with unchanged Skill Speed.");

    private static readonly EquipmentUtilityComponentDefinition[] Components =
    [
        Component(StrengthKey, EquipmentStatSemantic.Strength, 100),
        Component(VitalityKey, EquipmentStatSemantic.Vitality, 100),
        Component(PhysicalDamageKey, EquipmentStatSemantic.PhysicalDamage, 1),
        Component(PhysicalDefenseKey, EquipmentStatSemantic.PhysicalDefense, 100),
        Component(MagicalDefenseKey, EquipmentStatSemantic.MagicalDefense, 100),
        Component(CriticalHitKey, EquipmentStatSemantic.CriticalHit, 100),
        Component(DeterminationKey, EquipmentStatSemantic.Determination, 100),
        Component(DirectHitKey, EquipmentStatSemantic.DirectHit, 100),
        Component(TenacityKey, EquipmentStatSemantic.Tenacity, 100),
        Component(SkillSpeedKey, EquipmentStatSemantic.SkillSpeed, 100),
    ];

    private readonly EquipmentThresholdUtilityModel model;
    private readonly EquipmentUtilityEvaluation baselineEvaluation;

    public TankUtilityProfile(
        TankUtilityContextKind contextKind,
        TankUtilityStats baseline,
        uint classJobId,
        uint characterLevel = 100,
        TankUtilityStats? fixedStats = null)
    {
        ContextKind = contextKind;
        var supportedLevel = characterLevel >= MinimumLevel(classJobId) && characterLevel <= 100;
        var supported = Profile.SupportedClassJobIds.Contains(classJobId) && supportedLevel;
        var diagnostics = new List<string>();
        if (!Profile.SupportedClassJobIds.Contains(classJobId))
            diagnostics.Add("The two-handed tank profile supports Marauder, Warrior, Dark Knight, and Gunbreaker only.");
        if (!supportedLevel)
            diagnostics.Add("The two-handed tank profile requires a valid unlocked class/job level through 100.");

        var totalBaseline = fixedStats is null ? baseline : Add(baseline, fixedStats);
        var capabilities = Capabilities(totalBaseline);
        model = new(new(
            Profile,
            new(
                GeneralCombatContextId,
                classJobId,
                characterLevel,
                "General tank combat with exact componentwise no-loss ordering",
                ["patch:7.51", "current-player", "two-handed-tank", "supported"]),
            ToVector(baseline),
            Components,
            capabilities,
            UncertaintyRadius: 1_000,
            UncertaintyReasons:
            [
                "Skill Speed trades require job- and rotation-specific timing evidence.",
                "Encounter-specific damage and mitigation weights are intentionally not inferred.",
            ],
            IsSupported: supported,
            Diagnostics: diagnostics,
            FixedComponents: fixedStats is null ? null : ToVector(fixedStats),
            RawScoreMaximum: MaximumRawScore(capabilities),
            NormalizedScoreMaximum: 100d));
        baselineEvaluation = model.Evaluate(ToVector(baseline));
    }

    public TankUtilityContextKind ContextKind { get; }
    public JobUtilityProfile Definition => Profile;
    public EquipmentUtilityEvaluation BaselineEvaluation => baselineEvaluation;

    public EquipmentPartialUtilityDominance ComparePartial(EquipmentSolverUtilityVector candidate, EquipmentSolverUtilityVector other) =>
        model.ComparePartial(candidate, other);

    public IReadOnlyList<long> GetPartialDominanceCoordinates(EquipmentSolverUtilityVector utility) =>
        model.GetPartialDominanceCoordinates(utility);

    public EquipmentSolverUtilityVector CanonicalizePartialUtility(EquipmentSolverUtilityVector utility) =>
        model.CanonicalizePartialUtility(utility);

    public long CanonicalizePartialUtilityComponent(string componentKey, long units) =>
        model.CanonicalizePartialUtilityComponent(componentKey, units);

    public EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed) => model.Evaluate(completed);
    public EquipmentUtilityEvaluation Evaluate(TankUtilityStats stats) => model.Evaluate(ToVector(stats));

    public AdvisorAuthorityAssessment AssessAuthority(
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil,
        bool evidenceComplete = true,
        bool patchMatches = true,
        bool hasUnmodeledRelevantEffect = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var reasons = new List<string>();
        if (candidate.Profile != Profile.Key ||
            !string.Equals(candidate.Context.ContextId, GeneralCombatContextId, StringComparison.Ordinal))
            reasons.Add("The evaluation does not belong to this profile and context.");
        if (!evidenceComplete)
            reasons.Add("Decision-critical evidence is incomplete.");
        if (!patchMatches)
            reasons.Add("The utility profile patch envelope does not match the current game definitions.");
        if (hasUnmodeledRelevantEffect)
            reasons.Add("A relevant item effect or equip restriction is not modeled by this profile.");
        if (candidate.Assessment == UpgradeAssessment.Unsupported)
            reasons.Add("This target or context is unsupported.");
        if (candidate.Assessment == UpgradeAssessment.ContextDependent)
            reasons.Add("The candidate trades a tank damage, mitigation, survivability, secondary, or speed component; the conservative tank profile abstains.");
        if (candidate.Assessment is UpgradeAssessment.Equivalent or UpgradeAssessment.ClearRegression)
            reasons.Add("The candidate is not an exact componentwise no-loss improvement over the observed baseline.");
        if (Stat(candidate, EquipmentStatSemantic.SkillSpeed) is not { } candidateSkillSpeed ||
            candidateSkillSpeed != Stat(baselineEvaluation, EquipmentStatSemantic.SkillSpeed))
            reasons.Add("Skill Speed changed or could not be verified; job-specific timing evidence is required.");

        var baselineThresholds = baselineEvaluation.Thresholds.ToDictionary(value => value.ThresholdId, StringComparer.Ordinal);
        var gainedCapabilities = candidate.Thresholds
            .Where(value => value.Satisfied && baselineThresholds.TryGetValue(value.ThresholdId, out var baseline) && !baseline.Satisfied)
            .Select(value => value.ThresholdId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new(
            reasons.Count == 0 && candidate.Assessment == UpgradeAssessment.ClearImprovement,
            candidate.Assessment,
            gainedCapabilities,
            reasons);
    }

    public static EquipmentSolverUtilityVector ToVector(TankUtilityStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var values = new[]
        {
            stats.Strength, stats.Vitality, stats.PhysicalDamage, stats.PhysicalDefense, stats.MagicalDefense,
            stats.CriticalHit, stats.Determination, stats.DirectHit, stats.Tenacity, stats.SkillSpeed,
        };
        if (values.Any(value => value < 0))
            throw new ArgumentOutOfRangeException(nameof(stats), "Tank utility stats cannot be negative.");
        return new([
            new(StrengthKey, stats.Strength),
            new(VitalityKey, stats.Vitality),
            new(PhysicalDamageKey, stats.PhysicalDamage),
            new(PhysicalDefenseKey, stats.PhysicalDefense),
            new(MagicalDefenseKey, stats.MagicalDefense),
            new(CriticalHitKey, stats.CriticalHit),
            new(DeterminationKey, stats.Determination),
            new(DirectHitKey, stats.DirectHit),
            new(TenacityKey, stats.Tenacity),
            new(SkillSpeedKey, stats.SkillSpeed),
        ]);
    }

    private static IReadOnlyList<EquipmentUtilityCapabilityDefinition> Capabilities(TankUtilityStats baseline) =>
    [
        NoLossCapability("no-loss-strength-gain", "No-loss Strength gain", StrengthKey, baseline.Strength, baseline),
        NoLossCapability("no-loss-physical-damage-gain", "No-loss physical weapon-damage gain", PhysicalDamageKey, baseline.PhysicalDamage, baseline),
        NoLossCapability("no-loss-vitality-gain", "No-loss Vitality gain", VitalityKey, baseline.Vitality, baseline),
        NoLossCapability("no-loss-physical-defense-gain", "No-loss physical-defense gain", PhysicalDefenseKey, baseline.PhysicalDefense, baseline),
        NoLossCapability("no-loss-magical-defense-gain", "No-loss magical-defense gain", MagicalDefenseKey, baseline.MagicalDefense, baseline),
        NoLossCapability("no-loss-tenacity-gain", "No-loss Tenacity gain", TenacityKey, baseline.Tenacity, baseline),
    ];

    private static EquipmentUtilityCapabilityDefinition NoLossCapability(
        string id,
        string label,
        string gainedComponent,
        int baselineValue,
        TankUtilityStats baseline) => new(
        id,
        label,
        Requirements(baseline).Select(requirement =>
            string.Equals(requirement.ComponentKey, gainedComponent, StringComparison.Ordinal)
                ? requirement with { Minimum = checked(baselineValue + 1) }
                : requirement).ToArray(),
        CapabilityStep,
        $"{label} requires every modeled tank component to remain at or above the observed baseline.");

    private static IReadOnlyList<EquipmentUtilityCapabilityRequirement> Requirements(TankUtilityStats value) =>
    [
        new(StrengthKey, value.Strength),
        new(VitalityKey, value.Vitality),
        new(PhysicalDamageKey, value.PhysicalDamage),
        new(PhysicalDefenseKey, value.PhysicalDefense),
        new(MagicalDefenseKey, value.MagicalDefense),
        new(CriticalHitKey, value.CriticalHit),
        new(DeterminationKey, value.Determination),
        new(DirectHitKey, value.DirectHit),
        new(TenacityKey, value.Tenacity),
        new(SkillSpeedKey, value.SkillSpeed),
    ];

    private static TankUtilityStats Add(TankUtilityStats left, TankUtilityStats right) => new(
        checked(left.Strength + right.Strength),
        checked(left.Vitality + right.Vitality),
        checked(left.PhysicalDamage + right.PhysicalDamage),
        checked(left.PhysicalDefense + right.PhysicalDefense),
        checked(left.MagicalDefense + right.MagicalDefense),
        checked(left.CriticalHit + right.CriticalHit),
        checked(left.Determination + right.Determination),
        checked(left.DirectHit + right.DirectHit),
        checked(left.Tenacity + right.Tenacity),
        checked(left.SkillSpeed + right.SkillSpeed));

    private static uint MinimumLevel(uint classJobId) => classJobId switch
    {
        MarauderClassJobId => 1,
        WarriorClassJobId or DarkKnightClassJobId => 30,
        GunbreakerClassJobId => 60,
        _ => uint.MaxValue,
    };

    private static EquipmentUtilityRule Rule(string key, EquipmentStatSemantic semantic, string rationale) =>
        new(key, semantic, EquipmentUtilityRuleKind.PreferMore, 1d, null, rationale);

    private static int? Stat(EquipmentUtilityEvaluation evaluation, EquipmentStatSemantic semantic)
    {
        var matches = evaluation.RawStats.Where(value => value.Semantic == semantic).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Value : null;
    }

    private static EquipmentUtilityComponentDefinition Component(string key, EquipmentStatSemantic semantic, double divisor) =>
        new(key, semantic, divisor, 100, $"Bounded {semantic} progress inside the conservative componentwise tank profile.");

    private static double MaximumRawScore(IReadOnlyList<EquipmentUtilityCapabilityDefinition> capabilities) =>
        Components.Sum(component => component.MaximumContribution) + capabilities.Sum(capability => capability.ScoreContribution);
}
