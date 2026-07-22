using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

public enum PhysicalRangedUtilityContextKind
{
    GeneralCombat,
}

public sealed record PhysicalRangedUtilityStats(
    int Dexterity,
    int Vitality,
    int PhysicalDamage,
    int PhysicalDefense,
    int MagicalDefense,
    int CriticalHit,
    int Determination,
    int DirectHit,
    int SkillSpeed);

/// <summary>
/// Experimental shared BRD/MCH/DNC ordering model. It recognizes only componentwise no-loss
/// improvements; job-specific speed tiers, rotations, and encounter simulation remain outside
/// this role-level prerequisite.
/// </summary>
public sealed class PhysicalRangedUtilityProfile : IEquipmentExactSolverUtilityModel, IEquipmentPartialDominanceCoordinateModel, IEquipmentSeparablePartialUtilityCanonicalizationModel
{
    public const uint BardClassJobId = 23;
    public const uint MachinistClassJobId = 31;
    public const uint DancerClassJobId = 38;
    public const string ProfileId = "squire.physical-ranged.player";
    public const string ProfileVersion = "7.51-v2";
    public const string GeneralCombatContextId = "general-physical-ranged-combat";
    public const AdvisorProfileCalibrationState CalibrationState = AdvisorProfileCalibrationState.Experimental;

    private const string DexterityKey = "dexterity";
    private const string VitalityKey = "vitality";
    private const string PhysicalDamageKey = "physical-damage";
    private const string PhysicalDefenseKey = "physical-defense";
    private const string MagicalDefenseKey = "magical-defense";
    private const string CriticalHitKey = "critical-hit";
    private const string DeterminationKey = "determination";
    private const string DirectHitKey = "direct-hit";
    private const string SkillSpeedKey = "skill-speed";
    private const string DexterityGainCapabilityId = "no-loss-dexterity-gain";
    private const string WeaponDamageGainCapabilityId = "no-loss-physical-damage-gain";
    private const double CapabilityStep = 1_000;

    private static readonly JobUtilityProfile Profile = new(
        new(ProfileId, ProfileVersion),
        "Player Physical Ranged DPS",
        new HashSet<uint>(AdvisorCombatRoles.PhysicalRanged.ClassJobIds),
        new HashSet<string>(StringComparer.Ordinal) { GeneralCombatContextId },
        [
            Rule("dexterity", EquipmentStatSemantic.Dexterity, "Dexterity is the shared physical-ranged main attribute."),
            Rule("vitality", EquipmentStatSemantic.Vitality, "Vitality is retained as a no-loss survivability component."),
            Rule("physical-damage", EquipmentStatSemantic.PhysicalDamage, "Physical weapon damage is retained as a no-loss primary throughput component."),
            Rule("physical-defense", EquipmentStatSemantic.PhysicalDefense, "Physical defense is retained componentwise without claiming encounter value."),
            Rule("magical-defense", EquipmentStatSemantic.MagicalDefense, "Magical defense is retained componentwise without claiming encounter value."),
            Rule("critical-hit", EquipmentStatSemantic.CriticalHit, "Critical Hit is retained componentwise; no job-specific scaling claim is made."),
            Rule("determination", EquipmentStatSemantic.Determination, "Determination is retained componentwise; no job-specific scaling claim is made."),
            Rule("direct-hit", EquipmentStatSemantic.DirectHit, "Direct Hit is retained componentwise; no job-specific scaling claim is made."),
            new("skill-speed", EquipmentStatSemantic.SkillSpeed, EquipmentUtilityRuleKind.ContextualOnly, 0d, null,
                "Skill Speed remains visible, but every change requires job- and encounter-specific timing analysis."),
        ],
        "Patch 7.51 experimental shared physical-ranged profile. It exposes componentwise no-loss ordering only and cannot grant recommendation authority until independent calibration and live proof pass.");

    private static readonly EquipmentUtilityComponentDefinition[] Components =
    [
        Component(DexterityKey, EquipmentStatSemantic.Dexterity, 100),
        Component(VitalityKey, EquipmentStatSemantic.Vitality, 100),
        Component(PhysicalDamageKey, EquipmentStatSemantic.PhysicalDamage, 1),
        Component(PhysicalDefenseKey, EquipmentStatSemantic.PhysicalDefense, 100),
        Component(MagicalDefenseKey, EquipmentStatSemantic.MagicalDefense, 100),
        Component(CriticalHitKey, EquipmentStatSemantic.CriticalHit, 100),
        Component(DeterminationKey, EquipmentStatSemantic.Determination, 100),
        Component(DirectHitKey, EquipmentStatSemantic.DirectHit, 100),
        Component(SkillSpeedKey, EquipmentStatSemantic.SkillSpeed, 100),
    ];

    private readonly EquipmentThresholdUtilityModel model;
    private readonly EquipmentUtilityEvaluation baselineEvaluation;

    public PhysicalRangedUtilityProfile(
        PhysicalRangedUtilityContextKind contextKind,
        PhysicalRangedUtilityStats baseline,
        uint classJobId,
        uint characterLevel = 100,
        PhysicalRangedUtilityStats? fixedStats = null)
    {
        ContextKind = contextKind;
        var supportedLevel = characterLevel >= MinimumLevel(classJobId) && characterLevel <= 100;
        var supported = Profile.SupportedClassJobIds.Contains(classJobId) && supportedLevel;
        var diagnostics = new List<string>();
        if (!Profile.SupportedClassJobIds.Contains(classJobId))
            diagnostics.Add("The shared physical-ranged profile supports Bard, Machinist, and Dancer only.");
        if (!supportedLevel)
            diagnostics.Add("The physical-ranged profile requires a valid unlocked job level through 100.");

        var totalBaseline = fixedStats is null ? baseline : Add(baseline, fixedStats);
        var capabilities = Capabilities(totalBaseline);
        model = new(new(
            Profile,
            new(
                GeneralCombatContextId,
                classJobId,
                characterLevel,
                "General physical-ranged combat with componentwise no-loss ordering",
                ["patch:7.51", "current-player", "physical-ranged", "experimental"]),
            ToVector(baseline),
            Components,
            capabilities,
            UncertaintyRadius: 1_000,
            UncertaintyReasons:
            [
                "No job-specific rotation, speed-tier, proc, party-buff, or encounter model has been calibrated.",
                "Secondary-stat and Skill Speed trades remain visible but cannot authorize a recommendation.",
            ],
            IsSupported: supported,
            Diagnostics: diagnostics,
            FixedComponents: fixedStats is null ? null : ToVector(fixedStats),
            RawScoreMaximum: MaximumRawScore(capabilities),
            NormalizedScoreMaximum: 100d));
        baselineEvaluation = model.Evaluate(ToVector(baseline));
    }

    public PhysicalRangedUtilityContextKind ContextKind { get; }
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
    public EquipmentUtilityEvaluation Evaluate(PhysicalRangedUtilityStats stats) => model.Evaluate(ToVector(stats));

    public AdvisorAuthorityAssessment AssessAuthority(
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil,
        bool evidenceComplete = true,
        bool patchMatches = true,
        bool hasUnmodeledRelevantEffect = false) => AssessAuthorityCore(
            candidate,
            additionalCostGil,
            evidenceComplete,
            patchMatches,
            hasUnmodeledRelevantEffect,
            calibrationApproved: CalibrationState == AdvisorProfileCalibrationState.Supported);

    private AdvisorAuthorityAssessment AssessAuthorityCore(
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil,
        bool evidenceComplete,
        bool patchMatches,
        bool hasUnmodeledRelevantEffect,
        bool calibrationApproved)
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
        if (!calibrationApproved)
            reasons.Add("The physical-ranged profile is experimental; a separate frozen holdout and live gate have not passed.");
        if (candidate.Assessment == UpgradeAssessment.Unsupported)
            reasons.Add("This target or context is unsupported.");
        if (candidate.Assessment == UpgradeAssessment.ContextDependent)
            reasons.Add("The candidate trades a speed, secondary, defense, Vitality, Dexterity, or weapon-damage component; the shared role profile abstains.");
        if (candidate.Assessment is UpgradeAssessment.Equivalent or UpgradeAssessment.ClearRegression)
            reasons.Add("The candidate is not a componentwise no-loss improvement over the observed baseline.");
        if (Stat(candidate, EquipmentStatSemantic.SkillSpeed) is not { } candidateSkillSpeed ||
            candidateSkillSpeed != Stat(baselineEvaluation, EquipmentStatSemantic.SkillSpeed))
        {
            reasons.Add("Skill Speed changed or could not be verified; Bard, Machinist, and Dancer require separate rotation and encounter timing evidence.");
        }

        var baselineThresholds = baselineEvaluation.Thresholds.ToDictionary(value => value.ThresholdId, StringComparer.Ordinal);
        var gainedCapabilities = candidate.Thresholds
            .Where(value => value.Satisfied && baselineThresholds.TryGetValue(value.ThresholdId, out var baseline) && !baseline.Satisfied)
            .Select(value => value.ThresholdId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (candidate.Assessment == UpgradeAssessment.ClearImprovement &&
            !gainedCapabilities.Contains(WeaponDamageGainCapabilityId, StringComparer.Ordinal))
        {
            reasons.Add("Physical-ranged nomination requires a physical weapon-damage gain with no modeled component loss; raw Dexterity gains remain visible until effective damage tiers are modeled.");
        }

        return new(
            reasons.Count == 0 && candidate.Assessment == UpgradeAssessment.ClearImprovement,
            candidate.Assessment,
            gainedCapabilities,
            reasons);
    }

    public static EquipmentSolverUtilityVector ToVector(PhysicalRangedUtilityStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var values = new[]
        {
            stats.Dexterity, stats.Vitality, stats.PhysicalDamage, stats.PhysicalDefense, stats.MagicalDefense,
            stats.CriticalHit, stats.Determination, stats.DirectHit, stats.SkillSpeed,
        };
        if (values.Any(value => value < 0))
            throw new ArgumentOutOfRangeException(nameof(stats), "Physical-ranged utility stats cannot be negative.");
        return new([
            new(DexterityKey, stats.Dexterity),
            new(VitalityKey, stats.Vitality),
            new(PhysicalDamageKey, stats.PhysicalDamage),
            new(PhysicalDefenseKey, stats.PhysicalDefense),
            new(MagicalDefenseKey, stats.MagicalDefense),
            new(CriticalHitKey, stats.CriticalHit),
            new(DeterminationKey, stats.Determination),
            new(DirectHitKey, stats.DirectHit),
            new(SkillSpeedKey, stats.SkillSpeed),
        ]);
    }

    private static IReadOnlyList<EquipmentUtilityCapabilityDefinition> Capabilities(PhysicalRangedUtilityStats baseline) =>
    [
        NoLossCapability(DexterityGainCapabilityId, "No-loss Dexterity gain", DexterityKey, baseline.Dexterity, baseline),
        NoLossCapability(WeaponDamageGainCapabilityId, "No-loss physical weapon-damage gain", PhysicalDamageKey, baseline.PhysicalDamage, baseline),
    ];

    private static EquipmentUtilityCapabilityDefinition NoLossCapability(
        string id,
        string label,
        string gainedComponent,
        int baselineValue,
        PhysicalRangedUtilityStats baseline) => new(
        id,
        label,
        Requirements(baseline).Select(requirement =>
            string.Equals(requirement.ComponentKey, gainedComponent, StringComparison.Ordinal)
                ? requirement with { Minimum = checked(baselineValue + 1) }
                : requirement).ToArray(),
        CapabilityStep,
        $"{label} requires every modeled physical-ranged component to remain at or above the observed baseline.");

    private static IReadOnlyList<EquipmentUtilityCapabilityRequirement> Requirements(PhysicalRangedUtilityStats value) =>
    [
        new(DexterityKey, value.Dexterity),
        new(VitalityKey, value.Vitality),
        new(PhysicalDamageKey, value.PhysicalDamage),
        new(PhysicalDefenseKey, value.PhysicalDefense),
        new(MagicalDefenseKey, value.MagicalDefense),
        new(CriticalHitKey, value.CriticalHit),
        new(DeterminationKey, value.Determination),
        new(DirectHitKey, value.DirectHit),
        new(SkillSpeedKey, value.SkillSpeed),
    ];

    private static PhysicalRangedUtilityStats Add(PhysicalRangedUtilityStats left, PhysicalRangedUtilityStats right) => new(
        checked(left.Dexterity + right.Dexterity),
        checked(left.Vitality + right.Vitality),
        checked(left.PhysicalDamage + right.PhysicalDamage),
        checked(left.PhysicalDefense + right.PhysicalDefense),
        checked(left.MagicalDefense + right.MagicalDefense),
        checked(left.CriticalHit + right.CriticalHit),
        checked(left.Determination + right.Determination),
        checked(left.DirectHit + right.DirectHit),
        checked(left.SkillSpeed + right.SkillSpeed));

    private static uint MinimumLevel(uint classJobId) => classJobId == DancerClassJobId ? 60u : 30u;

    private static EquipmentUtilityRule Rule(string key, EquipmentStatSemantic semantic, string rationale) =>
        new(key, semantic, EquipmentUtilityRuleKind.PreferMore, 1d, null, rationale);

    private static int? Stat(EquipmentUtilityEvaluation evaluation, EquipmentStatSemantic semantic)
    {
        var matches = evaluation.RawStats.Where(value => value.Semantic == semantic).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Value : null;
    }

    private static EquipmentUtilityComponentDefinition Component(string key, EquipmentStatSemantic semantic, double divisor) =>
        new(key, semantic, divisor, 100, $"Bounded {semantic} progress inside the experimental componentwise role profile.");

    private static double MaximumRawScore(IReadOnlyList<EquipmentUtilityCapabilityDefinition> capabilities) =>
        Components.Sum(component => component.MaximumContribution) + capabilities.Sum(capability => capability.ScoreContribution);
}
