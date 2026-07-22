using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

public enum CrafterUtilityContextKind
{
    OrdinaryCraftBenchmark,
}

public sealed record CrafterUtilityStats(
    int Craftsmanship,
    int Control,
    int CraftingPoints);

/// <summary>
/// Patch-bounded crafter utility calibration. It shares the gatherer profile's shape:
/// bounded monotonic stat components plus baseline-relative dominance capabilities.
/// Economic nomination remains a separate MarketMafioso policy over the completed frontier.
/// </summary>
public sealed class CrafterUtilityProfile : IEquipmentExactSolverUtilityModel, IEquipmentPartialDominanceCoordinateModel, IEquipmentSeparablePartialUtilityCanonicalizationModel
{
    public const uint CarpenterClassJobId = 8;
    public const uint BlacksmithClassJobId = 9;
    public const uint ArmorerClassJobId = 10;
    public const uint GoldsmithClassJobId = 11;
    public const uint LeatherworkerClassJobId = 12;
    public const uint WeaverClassJobId = 13;
    public const uint AlchemistClassJobId = 14;
    public const uint CulinarianClassJobId = 15;

    public static readonly IReadOnlySet<uint> CrafterClassJobIds = new HashSet<uint>
    {
        CarpenterClassJobId, BlacksmithClassJobId, ArmorerClassJobId, GoldsmithClassJobId,
        LeatherworkerClassJobId, WeaverClassJobId, AlchemistClassJobId, CulinarianClassJobId,
    };

    public const string ProfileId = "squire.crafter.player";
    public const string ProfileVersion = "7.51-v1";
    public const string OrdinaryCraftBenchmarkContextId = "ordinary-craft-benchmark";
    public const AdvisorProfileCalibrationState CalibrationState = AdvisorProfileCalibrationState.Supported;

    private const string CraftsmanshipKey = "craftsmanship";
    private const string ControlKey = "control";
    private const string CraftingPointsKey = "crafting-points";
    private const double OrdinaryDominanceStep = 100;

    private static readonly JobUtilityProfile Profile = new(
        new(ProfileId, ProfileVersion),
        "Player Crafters",
        new HashSet<uint>(CrafterClassJobIds),
        new HashSet<string>(StringComparer.Ordinal) { OrdinaryCraftBenchmarkContextId },
        [
            new("craftsmanship", EquipmentStatSemantic.Craftsmanship, EquipmentUtilityRuleKind.PreferMore, 1d / 65d, null,
                "Craftsmanship contributes bounded monotonic progress efficiency; named capability thresholds provide the material steps."),
            new("control", EquipmentStatSemantic.Control, EquipmentUtilityRuleKind.PreferMore, 1d / 65d, null,
                "Control contributes bounded monotonic quality efficiency; named capability thresholds provide the material steps."),
            new("cp", EquipmentStatSemantic.CraftingPoints, EquipmentUtilityRuleKind.PreferMore, 1d / 11d, null,
                "CP contributes bounded monotonic action capacity; only explicitly modeled thresholds provide a material step."),
        ],
        "Patch 7.51 player crafter profile. Each task normalizes its capability steps and bounded monotonic tie-breaks onto a stable 0-100 envelope. The score orders supported loadouts but does not price gil or grant its own recommendation authority.");

    private static readonly EquipmentUtilityComponentDefinition[] Components =
    [
        new(CraftsmanshipKey, EquipmentStatSemantic.Craftsmanship, 65, 100,
            "Bounded Craftsmanship progress inside the supported player profile."),
        new(ControlKey, EquipmentStatSemantic.Control, 65, 100,
            "Bounded Control progress inside the supported player profile."),
        new(CraftingPointsKey, EquipmentStatSemantic.CraftingPoints, 11, 100,
            "Bounded CP progress inside the supported player profile."),
    ];

    private readonly EquipmentThresholdUtilityModel model;
    private readonly EquipmentUtilityEvaluation baselineEvaluation;

    public CrafterUtilityProfile(
        CrafterUtilityContextKind contextKind,
        CrafterUtilityStats baseline,
        uint classJobId,
        uint characterLevel = 100,
        CrafterUtilityStats? fixedStats = null)
    {
        ContextKind = contextKind;
        var supportedLevel = characterLevel is >= 1 and <= 100;
        var supported = Profile.SupportedClassJobIds.Contains(classJobId) && supportedLevel;
        var diagnostics = new List<string>();
        if (!Profile.SupportedClassJobIds.Contains(classJobId))
            diagnostics.Add("The crafter profile supports the eight crafting jobs only.");
        if (!supportedLevel)
            diagnostics.Add("The ordinary crafter profile supports levels 1 through 100.");

        var context = new EquipmentUtilityContext(
            OrdinaryCraftBenchmarkContextId,
            classJobId,
            characterLevel,
            "General crafting progress and quality efficiency",
            ["patch:7.51", "current-player", "ordinary-craft"]);
        var totalBaseline = fixedStats is null
            ? baseline
            : new(
                baseline.Craftsmanship + fixedStats.Craftsmanship,
                baseline.Control + fixedStats.Control,
                baseline.CraftingPoints + fixedStats.CraftingPoints);
        var capabilities = Capabilities(contextKind, totalBaseline);
        model = new(new(
            Profile,
            context,
            ToVector(baseline),
            Components,
            capabilities,
            UncertaintyRadius: 300,
            UncertaintyReasons:
            [
                "The bounded monotonic portion is an ordering index, not a crafting simulator.",
                "Food, specialist actions, recipe-specific breakpoints, and meld economics are outside this profile until the craft-cost work models them.",
            ],
            IsSupported: supported,
            Diagnostics: diagnostics,
            FixedComponents: fixedStats is null ? null : ToVector(fixedStats),
            RawScoreMaximum: MaximumRawScore(capabilities),
            NormalizedScoreMaximum: 100d));
        baselineEvaluation = model.Evaluate(ToVector(baseline));
    }

    public CrafterUtilityContextKind ContextKind { get; }
    public JobUtilityProfile Definition => Profile;
    public EquipmentUtilityEvaluation BaselineEvaluation => baselineEvaluation;

    public EquipmentPartialUtilityDominance ComparePartial(
        EquipmentSolverUtilityVector candidate,
        EquipmentSolverUtilityVector other) => model.ComparePartial(candidate, other);

    public IReadOnlyList<long> GetPartialDominanceCoordinates(EquipmentSolverUtilityVector utility) =>
        model.GetPartialDominanceCoordinates(utility);

    public EquipmentSolverUtilityVector CanonicalizePartialUtility(EquipmentSolverUtilityVector utility) =>
        model.CanonicalizePartialUtility(utility);

    public long CanonicalizePartialUtilityComponent(string componentKey, long units) =>
        model.CanonicalizePartialUtilityComponent(componentKey, units);

    public EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed) => model.Evaluate(completed);

    public EquipmentUtilityEvaluation Evaluate(CrafterUtilityStats stats) => model.Evaluate(ToVector(stats));

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
            !string.Equals(candidate.Context.ContextId, model.Definition.Context.ContextId, StringComparison.Ordinal))
            reasons.Add("The evaluation does not belong to this profile and context.");
        if (!evidenceComplete)
            reasons.Add("The decision-critical UI evidence is incomplete.");
        if (!patchMatches)
            reasons.Add("The utility profile patch envelope does not match the current game definitions.");
        if (hasUnmodeledRelevantEffect)
            reasons.Add("A relevant item or tool effect is not modeled by this profile.");
        if (!calibrationApproved)
            reasons.Add("The profile is experimental; its independent numeric-threshold holdout has not passed.");
        if (candidate.Assessment == UpgradeAssessment.Unsupported)
            reasons.Add("This target or context is unsupported.");
        if (candidate.Assessment == UpgradeAssessment.ContextDependent)
            reasons.Add("The candidate gains one supported stat while losing another.");
        if (candidate.Assessment is UpgradeAssessment.Equivalent or UpgradeAssessment.ClearRegression)
            reasons.Add("The candidate is not a gameplay improvement over the observed baseline.");

        var baselineThresholds = baselineEvaluation.Thresholds.ToDictionary(value => value.ThresholdId, StringComparer.Ordinal);
        var gainedCapabilities = candidate.Thresholds
            .Where(value => value.Satisfied &&
                baselineThresholds.TryGetValue(value.ThresholdId, out var baselineThreshold) &&
                !baselineThreshold.Satisfied)
            .Select(value => value.ThresholdId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (reasons.Count == 0 &&
            candidate.Assessment == UpgradeAssessment.ClearImprovement &&
            additionalCostGil > 0 &&
            gainedCapabilities.Length == 0)
        {
            reasons.Add("The score rises monotonically, but no supported capability step justifies an economic nomination.");
        }

        return new(
            reasons.Count == 0 && candidate.Assessment == UpgradeAssessment.ClearImprovement,
            candidate.Assessment,
            gainedCapabilities,
            reasons);
    }

    public static EquipmentSolverUtilityVector ToVector(CrafterUtilityStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        if (stats.Craftsmanship < 0 || stats.Control < 0 || stats.CraftingPoints < 0)
            throw new ArgumentOutOfRangeException(nameof(stats), "Crafting stats cannot be negative.");
        return new([
            new(CraftsmanshipKey, stats.Craftsmanship),
            new(ControlKey, stats.Control),
            new(CraftingPointsKey, stats.CraftingPoints),
        ]);
    }

    private static IReadOnlyList<EquipmentUtilityCapabilityDefinition> Capabilities(
        CrafterUtilityContextKind contextKind,
        CrafterUtilityStats baseline) => contextKind switch
    {
        CrafterUtilityContextKind.OrdinaryCraftBenchmark =>
        [
            new(
                "ordinary-balanced-stat-dominance",
                "Balanced ordinary crafting stat improvement",
                [
                    new(CraftsmanshipKey, checked(baseline.Craftsmanship + 1)),
                    new(ControlKey, checked(baseline.Control + 1)),
                    new(CraftingPointsKey, baseline.CraftingPoints),
                ],
                OrdinaryDominanceStep,
                "The candidate improves both Craftsmanship and Control without surrendering CP. This is a conservative dominance rule, not a claim about a recipe-specific breakpoint."),
        ],
        _ => [],
    };

    private static double MaximumRawScore(IReadOnlyList<EquipmentUtilityCapabilityDefinition> capabilities) =>
        Components.Sum(component => component.MaximumContribution) +
        capabilities.Sum(capability => capability.ScoreContribution);
}
