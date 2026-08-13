using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

public sealed class RetainerProcurementUtilityProfile :
    IEquipmentExactSolverUtilityModel,
    IEquipmentPartialDominanceCoordinateModel
{
    public const string BattleProfileId = "squire.retainer.battle-procurement";
    public const string GatheringProfileId = "squire.retainer.gathering-procurement";
    public const string ProfileVersion = "7.51-v1";
    public const string ContextId = "targeted-procurement-venture";
    private const string ItemLevelSumKey = "retainer-item-level-sum";
    private const string GatheringKey = "retainer-gathering";
    private const string PerceptionKey = "retainer-perception";

    private readonly RetainerProcurementObjective objective;
    private readonly RetainerProcurementStats baseline;
    private readonly RetainerProcurementOutcome baselineOutcome;
    private readonly uint classJobId;
    private readonly uint level;
    private readonly int slotCount;

    public RetainerProcurementUtilityProfile(
        RetainerProcurementObjective objective,
        RetainerProcurementStats baseline,
        uint classJobId,
        uint level,
        int slotCount)
    {
        ArgumentNullException.ThrowIfNull(objective);
        if (classJobId == 0 || level == 0 || slotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotCount), "A retainer utility profile requires a job, level, and positive worn-slot count.");
        this.objective = objective;
        this.baseline = baseline;
        this.classJobId = classJobId;
        this.level = level;
        this.slotCount = slotCount;
        baselineOutcome = RetainerProcurementOutcomeEvaluator.Evaluate(objective, baseline);
        if (baselineOutcome.Status is RetainerProcurementOutcomeStatus.InvalidEvidence or RetainerProcurementOutcomeStatus.Unsupported)
            throw new ArgumentException(baselineOutcome.Diagnostic, nameof(objective));
    }

    public EquipmentPartialUtilityDominance ComparePartial(
        EquipmentSolverUtilityVector candidate,
        EquipmentSolverUtilityVector other)
    {
        var keys = objective.Profile == RetainerProcurementProfileKind.Battle
            ? new[] { ItemLevelSumKey }
            : new[] { GatheringKey, PerceptionKey };
        var noWorse = keys.All(key => candidate.Get(key) >= other.Get(key));
        return new(noWorse, noWorse && keys.Any(key => candidate.Get(key) > other.Get(key)));
    }

    public IReadOnlyList<long> GetPartialDominanceCoordinates(EquipmentSolverUtilityVector utility) =>
        objective.Profile == RetainerProcurementProfileKind.Battle
            ? [utility.Get(ItemLevelSumKey)]
            : [utility.Get(GatheringKey), utility.Get(PerceptionKey)];

    public EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed)
    {
        var stats = Stats(completed);
        var outcome = RetainerProcurementOutcomeEvaluator.Evaluate(objective, stats);
        var assessment = CompareOutcome(outcome, baselineOutcome);
        var score = OutcomeScore(outcome);
        var raw = objective.Profile == RetainerProcurementProfileKind.Battle
            ? new[] { new EquipmentStatObservation(EquipmentStatSemantic.ItemLevel, stats.AverageItemLevel, "Complete retainer loadout") }
            : new[]
            {
                new EquipmentStatObservation(EquipmentStatSemantic.Gathering, stats.Gathering, "Complete retainer loadout"),
                new EquipmentStatObservation(EquipmentStatSemantic.Perception, stats.Perception, "Complete retainer loadout"),
            };
        var eligibilitySemantic = objective.Profile == RetainerProcurementProfileKind.Battle
            ? EquipmentStatSemantic.ItemLevel
            : EquipmentStatSemantic.Gathering;
        var yieldSemantic = objective.Profile == RetainerProcurementProfileKind.Battle
            ? EquipmentStatSemantic.ItemLevel
            : EquipmentStatSemantic.Perception;
        var thresholds = new List<EquipmentUtilityThreshold>
        {
            new(
                $"{objective.VentureKey}:eligibility",
                "Venture eligibility",
                objective.RequiredEligibilityStat,
                null,
                outcome.EligibilityStat >= objective.RequiredEligibilityStat,
                $"Installed-game {eligibilitySemantic} requirement for {objective.VentureKey}."),
        };
        thresholds.AddRange(objective.YieldThresholds.OrderBy(value => value.RequiredStat).Select(value => new EquipmentUtilityThreshold(
            $"{objective.VentureKey}:yield:{value.Quantity}",
            $"{value.Quantity:N0} item venture yield",
            value.RequiredStat,
            null,
            outcome.YieldStat >= value.RequiredStat,
            $"Installed-game {yieldSemantic} threshold for {objective.VentureKey}.")));
        return new(
            new(objective.Profile == RetainerProcurementProfileKind.Battle ? BattleProfileId : GatheringProfileId, ProfileVersion),
            new(ContextId, classJobId, level, $"Targeted procurement venture {objective.VentureKey}", ["retainer", objective.Profile.ToString()]),
            score,
            new(score, score, []),
            assessment,
            raw,
            [],
            thresholds,
            EquipmentEvaluationConfidence.High,
            [outcome.Diagnostic]);
    }

    public RetainerProcurementStats Stats(EquipmentSolverUtilityVector vector) => objective.Profile switch
    {
        RetainerProcurementProfileKind.Battle => new(
            checked((int)(vector.Get(ItemLevelSumKey) / slotCount)), 0, 0, 0),
        RetainerProcurementProfileKind.Gathering => new(
            0,
            checked((int)vector.Get(GatheringKey)),
            checked((int)vector.Get(PerceptionKey)),
            0),
        _ => new(-1, -1, -1, -1),
    };

    public static EquipmentSolverUtilityVector Vector(
        RetainerProcurementProfileKind profile,
        int itemLevel,
        int gathering,
        int perception) => profile switch
    {
        RetainerProcurementProfileKind.Battle => new([new(ItemLevelSumKey, itemLevel)]),
        RetainerProcurementProfileKind.Gathering => new EquipmentSolverUtilityVector([
            new(GatheringKey, gathering),
            new(PerceptionKey, perception),
        ]).Normalize(),
        _ => EquipmentSolverUtilityVector.Empty,
    };

    private static UpgradeAssessment CompareOutcome(
        RetainerProcurementOutcome candidate,
        RetainerProcurementOutcome baseline)
    {
        var candidateRank = OutcomeRank(candidate);
        var baselineRank = OutcomeRank(baseline);
        return candidateRank > baselineRank
            ? UpgradeAssessment.ClearImprovement
            : candidateRank < baselineRank
                ? UpgradeAssessment.ClearRegression
                : UpgradeAssessment.Equivalent;
    }

    private static long OutcomeRank(RetainerProcurementOutcome outcome) =>
        checked((outcome.IsEligible ? 1_000_000_000L : 0L) + outcome.Quantity * 1_000_000L);

    private static double OutcomeScore(RetainerProcurementOutcome outcome) =>
        OutcomeRank(outcome) + Math.Max(0, outcome.EligibilityStat) + Math.Max(0, outcome.YieldStat) / 100_000d;
}

public sealed class RetainerAdvisorStatFamily : IAdvisorStatFamily
{
    private readonly RetainerProcurementObjective objective;
    private readonly uint classJobId;
    private readonly int slotCount;
    private readonly bool mainHandCountsTwice;
    private readonly EquipmentStatSemantic[] semantics;
    private readonly AdvisorUtilityProfileDescriptor descriptor;

    public RetainerAdvisorStatFamily(
        RetainerProcurementObjective objective,
        uint classJobId,
        int slotCount,
        bool mainHandCountsTwice = false)
    {
        this.objective = objective ?? throw new ArgumentNullException(nameof(objective));
        this.classJobId = classJobId != 0 ? classJobId : throw new ArgumentOutOfRangeException(nameof(classJobId));
        this.slotCount = slotCount > 0 ? slotCount : throw new ArgumentOutOfRangeException(nameof(slotCount));
        this.mainHandCountsTwice = mainHandCountsTwice;
        SupportedClassJobIds = new HashSet<uint> { classJobId };
        semantics = objective.Profile == RetainerProcurementProfileKind.Battle
            ? [EquipmentStatSemantic.ItemLevel]
            : [EquipmentStatSemantic.Gathering, EquipmentStatSemantic.Perception];
        var context = new AdvisorUtilityContextDescriptor(
            RetainerProcurementUtilityProfile.ContextId,
            objective.VentureKey,
            $"{objective.VentureKey} outcome");
        descriptor = new(
            objective.Profile == RetainerProcurementProfileKind.Battle
                ? RetainerProcurementUtilityProfile.BattleProfileId
                : RetainerProcurementUtilityProfile.GatheringProfileId,
            RetainerProcurementUtilityProfile.ProfileVersion,
            RetainerVentureOutcomeCalibration.IsValid(objective)
                ? AdvisorProfileCalibrationState.Supported
                : AdvisorProfileCalibrationState.Experimental,
            [context],
            context.Id);
    }

    public AdvisorUtilityProfileDescriptor ProfileDescriptor => descriptor;
    public bool MainHandCountsTwice => mainHandCountsTwice;
    public IReadOnlySet<uint> SupportedClassJobIds { get; }
    public string FamilyLabel => objective.Profile == RetainerProcurementProfileKind.Battle ? "Battle retainer" : "Gathering retainer";
    public string CoverageJobLabel => FamilyLabel;
    public IReadOnlyList<EquipmentStatSemantic> RelevantSemantics => semantics;
    public bool IsRelevantSemantic(EquipmentStatSemantic semantic) => semantics.Contains(semantic);
    public AdvisorUtilityContextDescriptor ResolveContext(string? value) => descriptor.DefaultContext;

    public EquipmentSolverUtilityVector VectorFromSemantics(IReadOnlyDictionary<EquipmentStatSemantic, int> stats) =>
        RetainerProcurementUtilityProfile.Vector(
            objective.Profile,
            stats.GetValueOrDefault(EquipmentStatSemantic.ItemLevel),
            stats.GetValueOrDefault(EquipmentStatSemantic.Gathering),
            stats.GetValueOrDefault(EquipmentStatSemantic.Perception));

    public EquipmentSolverUtilityVector VectorFromRenderedSlot(
        EquipmentLoadoutPosition position,
        IReadOnlyDictionary<EquipmentStatSemantic, int> stats) =>
        RetainerProcurementUtilityProfile.Vector(
            objective.Profile,
            checked(stats.GetValueOrDefault(EquipmentStatSemantic.ItemLevel) *
                (mainHandCountsTwice && position == EquipmentLoadoutPosition.MainHand ? 2 : 1)),
            stats.GetValueOrDefault(EquipmentStatSemantic.Gathering),
            stats.GetValueOrDefault(EquipmentStatSemantic.Perception));

    public IEquipmentExactSolverUtilityModel CreateUtilityModel(
        string contextId,
        IReadOnlyDictionary<EquipmentStatSemantic, int> baseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int>? fixedStats,
        uint requestedClassJobId,
        uint characterLevel)
    {
        if (requestedClassJobId != classJobId)
            throw new InvalidOperationException("Retainer target job changed before utility construction.");
        var stats = objective.Profile == RetainerProcurementProfileKind.Battle
            ? new RetainerProcurementStats(baseline.GetValueOrDefault(EquipmentStatSemantic.ItemLevel) / slotCount, 0, 0, 0)
            : new RetainerProcurementStats(0,
                baseline.GetValueOrDefault(EquipmentStatSemantic.Gathering),
                baseline.GetValueOrDefault(EquipmentStatSemantic.Perception), 0);
        return new RetainerProcurementUtilityProfile(objective, stats, classJobId, characterLevel, slotCount);
    }

    public AdvisorAuthorityAssessment AssessAuthority(
        IEquipmentExactSolverUtilityModel model,
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil)
    {
        var mayConsider = candidate.Assessment == UpgradeAssessment.ClearImprovement;
        return new(
            mayConsider,
            candidate.Assessment,
            candidate.Thresholds.Where(value => value.Satisfied).Select(value => value.ThresholdId).ToArray(),
            mayConsider
                ? ["The loadout improves an observed retainer eligibility or yield outcome."]
                : ["The loadout does not improve the selected observed retainer outcome."]);
    }

    public IAdvisorSolverReplay? CaptureReplay(
        EquipmentExactFrontierRequest request,
        string contextId,
        uint requestedClassJobId,
        uint characterLevel,
        IReadOnlyDictionary<EquipmentStatSemantic, int> offerBaseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int> fixedStats) => null;

    public EquipmentSolverUtilityVector VectorFromDefinition(EquipmentStatProfile profile) =>
        throw new InvalidOperationException("Retainer offer vectors require the equipment definition.");

    public EquipmentSolverUtilityVector VectorFromDefinition(EquipmentItemDefinition definition, EquipmentStatProfile profile)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(profile);
        int Sum(EquipmentStatSemantic semantic) => profile.Parameters.Where(value => value.Semantic == semantic).Sum(value => value.Value);
        return RetainerProcurementUtilityProfile.Vector(
            objective.Profile,
            checked((int)definition.ItemLevel *
                (mainHandCountsTwice && definition.Slot == EquipmentSlot.MainHand ? 2 : 1)),
            Sum(EquipmentStatSemantic.Gathering),
            Sum(EquipmentStatSemantic.Perception));
    }

    public bool IsDefinitionOwnedSemantic(EquipmentStatSemantic semantic) => semantic == EquipmentStatSemantic.ItemLevel;

    public bool TryGetNonParameterDefinitionValue(EquipmentStatProfile profile, EquipmentStatSemantic semantic, out int value)
    {
        value = 0;
        return false;
    }

}
