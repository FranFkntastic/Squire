using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

public enum MinerBotanistUtilityContextKind
{
    LegendaryNodeGeneralYield,
    CollectableEfficiency,
    OrdinaryResourceBenchmark,
}

public sealed record MinerBotanistUtilityStats(
    int Gathering,
    int Perception,
    int GatheringPoints);

public sealed record AdvisorAuthorityAssessment(
    bool AdvisorMayConsider,
    UpgradeAssessment Assessment,
    IReadOnlyList<string> GainedCapabilityIds,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Patch-bounded MIN/BTN utility calibration. It is intentionally a small, inspectable index;
/// economic nomination remains a separate MarketMafioso policy over the completed frontier.
/// </summary>
public sealed class MinerBotanistUtilityProfile : IEquipmentExactSolverUtilityModel, IEquipmentPartialDominanceCoordinateModel, IEquipmentSeparablePartialUtilityCanonicalizationModel
{
    public const uint MinerClassJobId = 16;
    public const uint BotanistClassJobId = 17;
    public const string ProfileId = "squire.min-btn.player";
    public const string ProfileVersion = "7.51-v4";
    public const string LegendaryContextId = "legendary-node-general-yield";
    public const string CollectableContextId = "collectable-i730-efficiency";
    public const string OrdinaryResourceBenchmarkContextId = "ordinary-resource-general-yield";
    public const AdvisorProfileCalibrationState CalibrationState = AdvisorProfileCalibrationState.Supported;

    private const string GatheringKey = "gathering";
    private const string PerceptionKey = "perception";
    private const string GatheringPointsKey = "gathering-points";
    private const double CapabilityStep = 1_000;
    private const double OrdinaryDominanceStep = 100;

    private static readonly JobUtilityProfile Profile = new(
        new(ProfileId, ProfileVersion),
        "Player Miner / Botanist",
        new HashSet<uint> { MinerClassJobId, BotanistClassJobId },
        new HashSet<string>(StringComparer.Ordinal) { LegendaryContextId, CollectableContextId, OrdinaryResourceBenchmarkContextId },
        [
            new("gathering", EquipmentStatSemantic.Gathering, EquipmentUtilityRuleKind.PreferMore, 1d / 65d, null,
                "Gathering contributes bounded monotonic progress; named capability thresholds provide the material steps."),
            new("perception", EquipmentStatSemantic.Perception, EquipmentUtilityRuleKind.PreferMore, 1d / 65d, null,
                "Perception contributes bounded monotonic progress; named access, boon, and collectable thresholds provide the material steps."),
            new("gp", EquipmentStatSemantic.GatheringPoints, EquipmentUtilityRuleKind.PreferMore, 1d / 11d, null,
                "GP contributes bounded monotonic progress; only explicitly modeled action or integrity thresholds provide a material step."),
        ],
        "Patch 7.51 player profile. Each task normalizes its capability steps and bounded monotonic tie-breaks onto a stable 0-100 envelope. The score orders supported loadouts but does not price gil or grant its own recommendation authority.");

    private static readonly EquipmentUtilityComponentDefinition[] Components =
    [
        new(GatheringKey, EquipmentStatSemantic.Gathering, 65, 100,
            "Bounded Gathering progress inside the supported player profile."),
        new(PerceptionKey, EquipmentStatSemantic.Perception, 65, 100,
            "Bounded Perception progress inside the supported player profile."),
        new(GatheringPointsKey, EquipmentStatSemantic.GatheringPoints, 11, 100,
            "Bounded GP progress inside the supported player profile."),
    ];

    private readonly EquipmentThresholdUtilityModel model;
    private readonly EquipmentUtilityEvaluation baselineEvaluation;

    public MinerBotanistUtilityProfile(
        MinerBotanistUtilityContextKind contextKind,
        MinerBotanistUtilityStats baseline,
        uint classJobId,
        uint characterLevel = 100,
        MinerBotanistUtilityStats? fixedStats = null)
    {
        ContextKind = contextKind;
        var contextId = ContextId(contextKind);
        var supportedLevel = contextKind == MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark
            ? characterLevel is >= 1 and <= 100
            : characterLevel == 100;
        var supported = Profile.SupportedContextIds.Contains(contextId) &&
            Profile.SupportedClassJobIds.Contains(classJobId) &&
            supportedLevel;
        var diagnostics = new List<string>();
        if (classJobId == AdvisorStatFamilies.FisherClassJobId)
            diagnostics.Add(AdvisorStatFamilies.UnsupportedDiagnostic(classJobId));
        else if (!Profile.SupportedClassJobIds.Contains(classJobId))
            diagnostics.Add("The MIN/BTN profile supports Miner and Botanist only.");
        if (!supportedLevel)
            diagnostics.Add(contextKind == MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark
                ? "The ordinary-node player profile supports levels 1 through 100."
                : "Legendary and collectable calibration is currently level 100 only.");
        if (contextKind == MinerBotanistUtilityContextKind.CollectableEfficiency)
            diagnostics.Add("The 1000-GP collectable rotation is excluded until its node-return and food assumptions are explicit.");

        var context = new EquipmentUtilityContext(
            contextId,
            classJobId,
            characterLevel,
            Scenario(contextKind),
            contextKind == MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark
                ? ["patch:7.51", "current-player", "ordinary-resource-node"]
                : ["patch:7.51", "current-player", contextKind == MinerBotanistUtilityContextKind.CollectableEfficiency ? "collectable" : "legendary-node"]);
        var totalBaseline = fixedStats is null
            ? baseline
            : new(
                baseline.Gathering + fixedStats.Gathering,
                baseline.Perception + fixedStats.Perception,
                baseline.GatheringPoints + fixedStats.GatheringPoints);
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
                "The bounded monotonic portion is an ordering index, not a yield simulator.",
                "Food, meld economics, unique tool effects, and node-specific exceptions are outside this pilot.",
            ],
            IsSupported: supported,
            Diagnostics: diagnostics,
            FixedComponents: fixedStats is null ? null : ToVector(fixedStats),
            RawScoreMaximum: MaximumRawScore(capabilities),
            NormalizedScoreMaximum: 100d));
        baselineEvaluation = model.Evaluate(ToVector(baseline));
    }

    public MinerBotanistUtilityContextKind ContextKind { get; }
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

    public EquipmentUtilityEvaluation Evaluate(MinerBotanistUtilityStats stats) => model.Evaluate(ToVector(stats));

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

    public static EquipmentSolverUtilityVector ToVector(MinerBotanistUtilityStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        if (stats.Gathering < 0 || stats.Perception < 0 || stats.GatheringPoints < 0)
            throw new ArgumentOutOfRangeException(nameof(stats), "Gathering stats cannot be negative.");
        return new([
            new(GatheringKey, stats.Gathering),
            new(PerceptionKey, stats.Perception),
            new(GatheringPointsKey, stats.GatheringPoints),
        ]);
    }

    private static IReadOnlyList<EquipmentUtilityCapabilityDefinition> Capabilities(
        MinerBotanistUtilityContextKind contextKind,
        MinerBotanistUtilityStats baseline) => contextKind switch
    {
        MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield =>
        [
            Capability("legendary-minimum-perception", "Current legendary-node access", PerceptionKey, 5_090),
            Capability("bountiful-yield-plus-two", "Bountiful Yield / Harvest II +2", GatheringKey, 5_116),
            Capability("node-yield-plus-one", "Node bonus: +1 yield", GatheringKey, 5_400),
            Capability("node-yield-plus-two", "Hidden node bonus: +2 yield", GatheringKey, 5_643),
            Capability("node-yield-plus-three", "Hidden node bonus: +3 yield", GatheringKey, 5_886),
            Capability("bountiful-yield-plus-three", "Bountiful Yield / Harvest II +3", GatheringKey, 6_253),
            Capability("boon-plus-thirty-percent", "Node bonus: +30% Gatherer's Boon", PerceptionKey, 5_600),
            Capability("integrity-plus-one", "Node bonus: +1 integrity", GatheringPointsKey, 960),
            Capability("integrity-plus-two", "Hidden node bonus: +2 integrity", GatheringPointsKey, 990),
        ],
        MinerBotanistUtilityContextKind.CollectableEfficiency =>
        [
            new(
                "collectable-actions-i730",
                "i730 collectable action requirements",
                [new(GatheringKey, 5_173), new(PerceptionKey, 5_173)],
                CapabilityStep,
                "Both Gathering and Perception are required; partial satisfaction is not a capability."),
            Capability("meticulous-proc-cap-i730", "i730 Meticulous proc cap", GatheringKey, 5_445),
            Capability("intuition-proc-cap-i730", "i730 Intuition proc cap", PerceptionKey, 5_445),
        ],
        MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark =>
        [
            new(
                "ordinary-balanced-stat-dominance",
                "Balanced ordinary-node stat improvement",
                [
                    new(GatheringKey, checked(baseline.Gathering + 1)),
                    new(PerceptionKey, checked(baseline.Perception + 1)),
                    new(GatheringPointsKey, baseline.GatheringPoints),
                ],
                OrdinaryDominanceStep,
                "The candidate improves both Gathering and Perception without surrendering GP. This is a conservative dominance rule, not a claim about a node-specific breakpoint."),
        ],
        _ => [],
    };

    private static EquipmentUtilityCapabilityDefinition Capability(
        string id,
        string label,
        string componentKey,
        long minimum) => new(
            id,
            label,
            [new(componentKey, minimum)],
            CapabilityStep,
            $"{label} is a named, patch-bounded capability step at {minimum:N0}.");

    private static string ContextId(MinerBotanistUtilityContextKind contextKind) => contextKind switch
    {
        MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield => LegendaryContextId,
        MinerBotanistUtilityContextKind.CollectableEfficiency => CollectableContextId,
        _ => OrdinaryResourceBenchmarkContextId,
    };

    private static string Scenario(MinerBotanistUtilityContextKind contextKind) => contextKind switch
    {
        MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield => "Patch 7.51 general legendary-node yield",
        MinerBotanistUtilityContextKind.CollectableEfficiency => "Patch 7.51 i730 collectable action and proc efficiency",
        _ => "General regular non-legendary resource-node yield",
    };

    private static double MaximumRawScore(IReadOnlyList<EquipmentUtilityCapabilityDefinition> capabilities) =>
        Components.Sum(component => component.MaximumContribution) +
        capabilities.Sum(capability => capability.ScoreContribution);
}
