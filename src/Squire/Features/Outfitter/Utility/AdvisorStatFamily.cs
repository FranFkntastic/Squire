using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

public sealed record AdvisorCombatRoleDescriptor(
    string Id,
    string Label,
    IReadOnlySet<uint> ClassJobIds);

public static class AdvisorCombatRoles
{
    public static readonly AdvisorCombatRoleDescriptor PhysicalRanged = new(
        "physical-ranged",
        "Physical ranged DPS",
        new HashSet<uint>
        {
            PhysicalRangedUtilityProfile.BardClassJobId,
            PhysicalRangedUtilityProfile.MachinistClassJobId,
            PhysicalRangedUtilityProfile.DancerClassJobId,
        });

    public static IReadOnlyList<AdvisorCombatRoleDescriptor> All { get; } = [PhysicalRanged];

    public static AdvisorCombatRoleDescriptor? Resolve(uint classJobId) =>
        All.FirstOrDefault(role => role.ClassJobIds.Contains(classJobId));
}

public enum AdvisorProfileCalibrationState
{
    Experimental,
    Supported,
}

public sealed record AdvisorUtilityContextDescriptor(
    string Id,
    string ConfigurationValue,
    string Label);

public sealed record AdvisorUtilityProfileDescriptor(
    string Id,
    string Version,
    AdvisorProfileCalibrationState CalibrationState,
    IReadOnlyList<AdvisorUtilityContextDescriptor> Contexts,
    string DefaultContextId)
{
    public AdvisorUtilityContextDescriptor DefaultContext =>
        Contexts.First(context => string.Equals(context.Id, DefaultContextId, StringComparison.Ordinal));

    public AdvisorUtilityContextDescriptor ResolveContext(string? value) =>
        Contexts.FirstOrDefault(context =>
            string.Equals(context.Id, value, StringComparison.Ordinal) ||
            string.Equals(context.ConfigurationValue, value, StringComparison.Ordinal))
        ?? DefaultContext;
}

/// <summary>
/// The seam between the shared advisor machinery (offers, solver, nomination, evidence)
/// and one job family's calibration: which stats matter, how vectors are built, how the
/// utility model is constructed, and how authority is assessed.
/// </summary>
public interface IAdvisorStatFamily
{
    AdvisorUtilityProfileDescriptor ProfileDescriptor { get; }
    IReadOnlySet<uint> SupportedClassJobIds { get; }
    string CoverageJobLabel { get; }
    IReadOnlyList<EquipmentStatSemantic> RelevantSemantics { get; }
    bool IsRelevantSemantic(EquipmentStatSemantic semantic);
    EquipmentSolverUtilityVector VectorFromSemantics(IReadOnlyDictionary<EquipmentStatSemantic, int> stats);
    IEquipmentExactSolverUtilityModel CreateUtilityModel(
        string contextId,
        IReadOnlyDictionary<EquipmentStatSemantic, int> baseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int>? fixedStats,
        uint classJobId,
        uint characterLevel);
    AdvisorAuthorityAssessment AssessAuthority(
        IEquipmentExactSolverUtilityModel model,
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil);
    IAdvisorSolverReplay? CaptureReplay(
        EquipmentExactFrontierRequest request,
        string contextId,
        uint classJobId,
        uint characterLevel,
        IReadOnlyDictionary<EquipmentStatSemantic, int> offerBaseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int> fixedStats);
    EquipmentSolverUtilityVector VectorFromDefinition(EquipmentStatProfile profile);
    bool TryGetNonParameterDefinitionValue(
        EquipmentStatProfile profile,
        EquipmentStatSemantic semantic,
        out int value)
    {
        value = 0;
        return false;
    }
    AdvisorUtilityContextDescriptor ResolveContext(string? value) => ProfileDescriptor.ResolveContext(value);
}

public static class AdvisorStatFamilies
{
    public const uint FisherClassJobId = 18;

    public static IReadOnlyList<IAdvisorStatFamily> All { get; } =
        [GathererAdvisorStatFamily.Instance, CrafterAdvisorStatFamily.Instance, PhysicalRangedAdvisorStatFamily.Instance];

    public static IAdvisorStatFamily? Resolve(uint classJobId) =>
        All.FirstOrDefault(family => family.SupportedClassJobIds.Contains(classJobId));

    public static string UnsupportedDiagnostic(uint classJobId) => classJobId == FisherClassJobId
        ? "Fisher is permanently unsupported and out of scope for Squire Outfitter."
        : $"Class/job {classJobId} has no advisor stat family yet.";
}

public sealed class GathererAdvisorStatFamily : IAdvisorStatFamily
{
    public static readonly GathererAdvisorStatFamily Instance = new();

    private static readonly IReadOnlySet<uint> Jobs = new HashSet<uint>
    {
        MinerBotanistUtilityProfile.MinerClassJobId,
        MinerBotanistUtilityProfile.BotanistClassJobId,
    };

    private static readonly EquipmentStatSemantic[] Semantics =
    [
        EquipmentStatSemantic.Gathering,
        EquipmentStatSemantic.Perception,
        EquipmentStatSemantic.GatheringPoints,
    ];

    public static readonly AdvisorUtilityContextDescriptor OrdinaryResourceContext = new(
        MinerBotanistUtilityProfile.OrdinaryResourceBenchmarkContextId,
        nameof(MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark),
        "Ordinary nodes · general yield");
    public static readonly AdvisorUtilityContextDescriptor LegendaryNodeContext = new(
        MinerBotanistUtilityProfile.LegendaryContextId,
        nameof(MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield),
        "Legendary nodes · general yield");
    public static readonly AdvisorUtilityContextDescriptor CollectableContext = new(
        MinerBotanistUtilityProfile.CollectableContextId,
        nameof(MinerBotanistUtilityContextKind.CollectableEfficiency),
        "Collectables · i730 efficiency");

    private static readonly AdvisorUtilityProfileDescriptor Descriptor = new(
        MinerBotanistUtilityProfile.ProfileId,
        MinerBotanistUtilityProfile.ProfileVersion,
        MinerBotanistUtilityProfile.CalibrationState,
        [OrdinaryResourceContext, LegendaryNodeContext, CollectableContext],
        OrdinaryResourceContext.Id);

    public AdvisorUtilityProfileDescriptor ProfileDescriptor => Descriptor;
    public IReadOnlySet<uint> SupportedClassJobIds => Jobs;
    public string CoverageJobLabel => "MIN/BTN";
    public IReadOnlyList<EquipmentStatSemantic> RelevantSemantics => Semantics;
    public AdvisorUtilityContextDescriptor ResolveContext(string? value) => Descriptor.ResolveContext(value);

    public bool IsRelevantSemantic(EquipmentStatSemantic semantic) => semantic is
        EquipmentStatSemantic.Gathering or EquipmentStatSemantic.Perception or EquipmentStatSemantic.GatheringPoints;

    public EquipmentSolverUtilityVector VectorFromSemantics(IReadOnlyDictionary<EquipmentStatSemantic, int> stats) =>
        MinerBotanistUtilityProfile.ToVector(FromSemantics(stats));

    public IEquipmentExactSolverUtilityModel CreateUtilityModel(
        string contextId,
        IReadOnlyDictionary<EquipmentStatSemantic, int> baseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int>? fixedStats,
        uint classJobId,
        uint characterLevel)
    {
        var contextKind = ContextKindFor(contextId);
        return new MinerBotanistUtilityProfile(
            contextKind,
            FromSemantics(baseline),
            classJobId,
            characterLevel,
            fixedStats is null ? null : FromSemantics(fixedStats));
    }

    public AdvisorAuthorityAssessment AssessAuthority(
        IEquipmentExactSolverUtilityModel model,
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil) =>
        ((MinerBotanistUtilityProfile)model).AssessAuthority(candidate, additionalCostGil);

    public IAdvisorSolverReplay? CaptureReplay(
        EquipmentExactFrontierRequest request,
        string contextId,
        uint classJobId,
        uint characterLevel,
        IReadOnlyDictionary<EquipmentStatSemantic, int> offerBaseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int> fixedStats)
    {
        var contextKind = ContextKindFor(contextId);
        return MinerBotanistSolverReplay.Capture(
            request,
            contextKind,
            classJobId,
            characterLevel,
            FromSemantics(offerBaseline),
            FromSemantics(fixedStats));
    }

    public EquipmentSolverUtilityVector VectorFromDefinition(EquipmentStatProfile profile)
    {
        int Sum(EquipmentStatSemantic semantic) => profile.Parameters.Where(value => value.Semantic == semantic).Sum(value => value.Value);
        return MinerBotanistUtilityProfile.ToVector(new(
            Sum(EquipmentStatSemantic.Gathering),
            Sum(EquipmentStatSemantic.Perception),
            Sum(EquipmentStatSemantic.GatheringPoints)));
    }

    private static MinerBotanistUtilityStats FromSemantics(IReadOnlyDictionary<EquipmentStatSemantic, int> stats) =>
        new(
            stats.GetValueOrDefault(EquipmentStatSemantic.Gathering),
            stats.GetValueOrDefault(EquipmentStatSemantic.Perception),
            stats.GetValueOrDefault(EquipmentStatSemantic.GatheringPoints));

    internal static MinerBotanistUtilityContextKind ContextKindFor(string? value) =>
        Instance.ResolveContext(value).Id switch
        {
            MinerBotanistUtilityProfile.LegendaryContextId => MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
            MinerBotanistUtilityProfile.CollectableContextId => MinerBotanistUtilityContextKind.CollectableEfficiency,
            _ => MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
        };
}

public sealed class CrafterAdvisorStatFamily : IAdvisorStatFamily
{
    public static readonly CrafterAdvisorStatFamily Instance = new();

    private static readonly EquipmentStatSemantic[] Semantics =
    [
        EquipmentStatSemantic.Craftsmanship,
        EquipmentStatSemantic.Control,
        EquipmentStatSemantic.CraftingPoints,
    ];

    public static readonly AdvisorUtilityContextDescriptor OrdinaryCraftContext = new(
        CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
        nameof(CrafterUtilityContextKind.OrdinaryCraftBenchmark),
        "Ordinary crafts");

    private static readonly AdvisorUtilityProfileDescriptor Descriptor = new(
        CrafterUtilityProfile.ProfileId,
        CrafterUtilityProfile.ProfileVersion,
        CrafterUtilityProfile.CalibrationState,
        [OrdinaryCraftContext],
        OrdinaryCraftContext.Id);

    public AdvisorUtilityProfileDescriptor ProfileDescriptor => Descriptor;
    public IReadOnlySet<uint> SupportedClassJobIds => CrafterUtilityProfile.CrafterClassJobIds;
    public string CoverageJobLabel => "crafter";
    public IReadOnlyList<EquipmentStatSemantic> RelevantSemantics => Semantics;
    public AdvisorUtilityContextDescriptor ResolveContext(string? value) => Descriptor.ResolveContext(value);

    public bool IsRelevantSemantic(EquipmentStatSemantic semantic) => semantic is
        EquipmentStatSemantic.Craftsmanship or EquipmentStatSemantic.Control or EquipmentStatSemantic.CraftingPoints;

    public EquipmentSolverUtilityVector VectorFromSemantics(IReadOnlyDictionary<EquipmentStatSemantic, int> stats) =>
        CrafterUtilityProfile.ToVector(FromSemantics(stats));

    public IEquipmentExactSolverUtilityModel CreateUtilityModel(
        string contextId,
        IReadOnlyDictionary<EquipmentStatSemantic, int> baseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int>? fixedStats,
        uint classJobId,
        uint characterLevel) =>
        new CrafterUtilityProfile(
            CrafterUtilityContextKind.OrdinaryCraftBenchmark,
            FromSemantics(baseline),
            classJobId,
            characterLevel,
            fixedStats is null ? null : FromSemantics(fixedStats));

    public AdvisorAuthorityAssessment AssessAuthority(
        IEquipmentExactSolverUtilityModel model,
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil) =>
        ((CrafterUtilityProfile)model).AssessAuthority(candidate, additionalCostGil);

    public IAdvisorSolverReplay? CaptureReplay(
        EquipmentExactFrontierRequest request,
        string contextId,
        uint classJobId,
        uint characterLevel,
        IReadOnlyDictionary<EquipmentStatSemantic, int> offerBaseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int> fixedStats) =>
        null;

    public EquipmentSolverUtilityVector VectorFromDefinition(EquipmentStatProfile profile)
    {
        int Sum(EquipmentStatSemantic semantic) => profile.Parameters.Where(value => value.Semantic == semantic).Sum(value => value.Value);
        return CrafterUtilityProfile.ToVector(new(
            Sum(EquipmentStatSemantic.Craftsmanship),
            Sum(EquipmentStatSemantic.Control),
            Sum(EquipmentStatSemantic.CraftingPoints)));
    }

    private static CrafterUtilityStats FromSemantics(IReadOnlyDictionary<EquipmentStatSemantic, int> stats) =>
        new(
            stats.GetValueOrDefault(EquipmentStatSemantic.Craftsmanship),
            stats.GetValueOrDefault(EquipmentStatSemantic.Control),
            stats.GetValueOrDefault(EquipmentStatSemantic.CraftingPoints));

}

public sealed class PhysicalRangedAdvisorStatFamily : IAdvisorStatFamily
{
    public static readonly PhysicalRangedAdvisorStatFamily Instance = new();

    private static readonly EquipmentStatSemantic[] Semantics =
    [
        EquipmentStatSemantic.Dexterity,
        EquipmentStatSemantic.Vitality,
        EquipmentStatSemantic.PhysicalDamage,
        EquipmentStatSemantic.PhysicalDefense,
        EquipmentStatSemantic.MagicalDefense,
        EquipmentStatSemantic.CriticalHit,
        EquipmentStatSemantic.Determination,
        EquipmentStatSemantic.DirectHit,
        EquipmentStatSemantic.SkillSpeed,
    ];

    public static readonly AdvisorUtilityContextDescriptor GeneralCombatContext = new(
        PhysicalRangedUtilityProfile.GeneralCombatContextId,
        nameof(PhysicalRangedUtilityContextKind.GeneralCombat),
        "General physical-ranged combat");

    private static readonly AdvisorUtilityProfileDescriptor Descriptor = new(
        PhysicalRangedUtilityProfile.ProfileId,
        PhysicalRangedUtilityProfile.ProfileVersion,
        PhysicalRangedUtilityProfile.CalibrationState,
        [GeneralCombatContext],
        GeneralCombatContext.Id);

    public AdvisorUtilityProfileDescriptor ProfileDescriptor => Descriptor;
    public IReadOnlySet<uint> SupportedClassJobIds => AdvisorCombatRoles.PhysicalRanged.ClassJobIds;
    public string CoverageJobLabel => "BRD/MCH/DNC";
    public IReadOnlyList<EquipmentStatSemantic> RelevantSemantics => Semantics;
    public AdvisorUtilityContextDescriptor ResolveContext(string? value) => Descriptor.ResolveContext(value);

    public bool IsRelevantSemantic(EquipmentStatSemantic semantic) => Semantics.Contains(semantic);

    public EquipmentSolverUtilityVector VectorFromSemantics(IReadOnlyDictionary<EquipmentStatSemantic, int> stats) =>
        PhysicalRangedUtilityProfile.ToVector(FromSemantics(stats));

    public IEquipmentExactSolverUtilityModel CreateUtilityModel(
        string contextId,
        IReadOnlyDictionary<EquipmentStatSemantic, int> baseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int>? fixedStats,
        uint classJobId,
        uint characterLevel) =>
        new PhysicalRangedUtilityProfile(
            PhysicalRangedUtilityContextKind.GeneralCombat,
            FromSemantics(baseline),
            classJobId,
            characterLevel,
            fixedStats is null ? null : FromSemantics(fixedStats));

    public AdvisorAuthorityAssessment AssessAuthority(
        IEquipmentExactSolverUtilityModel model,
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil) =>
        ((PhysicalRangedUtilityProfile)model).AssessAuthority(candidate, additionalCostGil);

    public IAdvisorSolverReplay? CaptureReplay(
        EquipmentExactFrontierRequest request,
        string contextId,
        uint classJobId,
        uint characterLevel,
        IReadOnlyDictionary<EquipmentStatSemantic, int> offerBaseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int> fixedStats) =>
        null;

    public EquipmentSolverUtilityVector VectorFromDefinition(EquipmentStatProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        int Sum(EquipmentStatSemantic semantic) => profile.Parameters.Where(value => value.Semantic == semantic).Sum(value => value.Value);
        return PhysicalRangedUtilityProfile.ToVector(new(
            Sum(EquipmentStatSemantic.Dexterity),
            Sum(EquipmentStatSemantic.Vitality),
            profile.PhysicalDamage,
            profile.PhysicalDefense,
            profile.MagicalDefense,
            Sum(EquipmentStatSemantic.CriticalHit),
            Sum(EquipmentStatSemantic.Determination),
            Sum(EquipmentStatSemantic.DirectHit),
            Sum(EquipmentStatSemantic.SkillSpeed)));
    }

    public bool TryGetNonParameterDefinitionValue(
        EquipmentStatProfile profile,
        EquipmentStatSemantic semantic,
        out int value)
    {
        if (!profile.IsComplete)
        {
            value = 0;
            return false;
        }
        value = semantic switch
        {
            EquipmentStatSemantic.PhysicalDamage => profile.PhysicalDamage,
            EquipmentStatSemantic.PhysicalDefense => profile.PhysicalDefense,
            EquipmentStatSemantic.MagicalDefense => profile.MagicalDefense,
            _ => 0,
        };
        return semantic is EquipmentStatSemantic.PhysicalDamage or
            EquipmentStatSemantic.PhysicalDefense or EquipmentStatSemantic.MagicalDefense;
    }

    private static PhysicalRangedUtilityStats FromSemantics(IReadOnlyDictionary<EquipmentStatSemantic, int> stats) =>
        new(
            stats.GetValueOrDefault(EquipmentStatSemantic.Dexterity),
            stats.GetValueOrDefault(EquipmentStatSemantic.Vitality),
            stats.GetValueOrDefault(EquipmentStatSemantic.PhysicalDamage),
            stats.GetValueOrDefault(EquipmentStatSemantic.PhysicalDefense),
            stats.GetValueOrDefault(EquipmentStatSemantic.MagicalDefense),
            stats.GetValueOrDefault(EquipmentStatSemantic.CriticalHit),
            stats.GetValueOrDefault(EquipmentStatSemantic.Determination),
            stats.GetValueOrDefault(EquipmentStatSemantic.DirectHit),
            stats.GetValueOrDefault(EquipmentStatSemantic.SkillSpeed));
}
