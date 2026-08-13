using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

public sealed record ConservativeCombatFamilySpec(
    string Id,
    string Label,
    string CoverageLabel,
    string ContextLabel,
    EquipmentStatSemantic PrimaryStat,
    EquipmentStatSemantic WeaponDamage,
    EquipmentStatSemantic Speed,
    IReadOnlySet<uint> ClassJobIds,
    IReadOnlyDictionary<uint, uint> MinimumLevels,
    IReadOnlyList<EquipmentStatSemantic> AdditionalSemantics);

public static class ConservativeCombatAdvisorStatFamilies
{
    public static readonly IAdvisorStatFamily ShieldTank = Family(new(
        "shield-tank", "Tank", "GLD/PLD", "General shield-tank combat",
        EquipmentStatSemantic.Strength, EquipmentStatSemantic.PhysicalDamage, EquipmentStatSemantic.SkillSpeed,
        Set(1, 19), Levels((1, 1), (19, 30)),
        [EquipmentStatSemantic.Tenacity, EquipmentStatSemantic.BlockStrength, EquipmentStatSemantic.BlockRate]));

    public static readonly IAdvisorStatFamily StrengthMelee = Family(new(
        "strength-melee", "Strength melee DPS", "PGL/MNK/LNC/DRG/SAM/RPR", "General melee combat",
        EquipmentStatSemantic.Strength, EquipmentStatSemantic.PhysicalDamage, EquipmentStatSemantic.SkillSpeed,
        Set(2, 4, 20, 22, 34, 39), Levels((2, 1), (4, 1), (20, 30), (22, 30), (34, 50), (39, 70)), []));

    public static readonly IAdvisorStatFamily ScoutingMelee = Family(new(
        "scouting-melee", "Scouting melee DPS", "ROG/NIN/VPR", "General scouting combat",
        EquipmentStatSemantic.Dexterity, EquipmentStatSemantic.PhysicalDamage, EquipmentStatSemantic.SkillSpeed,
        Set(29, 30, 41), Levels((29, 1), (30, 30), (41, 80)), []));

    public static readonly IAdvisorStatFamily Healer = Family(new(
        "healer", "Healer", "CNJ/WHM/SCH/AST/SGE", "General healing combat",
        EquipmentStatSemantic.Mind, EquipmentStatSemantic.MagicalDamage, EquipmentStatSemantic.SpellSpeed,
        Set(6, 24, 28, 33, 40), Levels((6, 1), (24, 30), (28, 30), (33, 30), (40, 70)),
        [EquipmentStatSemantic.Piety]));

    public static readonly IAdvisorStatFamily MagicalRanged = Family(new(
        "magical-ranged", "Magical ranged DPS", "THM/BLM/ACN/SMN/RDM/BLU/PCT", "General magical combat",
        EquipmentStatSemantic.Intelligence, EquipmentStatSemantic.MagicalDamage, EquipmentStatSemantic.SpellSpeed,
        Set(7, 25, 26, 27, 35, 36, 42), Levels((7, 1), (25, 30), (26, 1), (27, 30), (35, 50), (36, 1), (42, 80)), []));

    private static IAdvisorStatFamily Family(ConservativeCombatFamilySpec spec) => new ConservativeCombatAdvisorStatFamily(spec);
    private static IReadOnlySet<uint> Set(params uint[] ids) => ids.ToHashSet();
    private static IReadOnlyDictionary<uint, uint> Levels(params (uint Id, uint Level)[] values) =>
        values.ToDictionary(value => value.Id, value => value.Level);
}

public sealed class ConservativeCombatAdvisorStatFamily : IAdvisorStatFamily
{
    private readonly ConservativeCombatFamilySpec spec;
    private readonly EquipmentStatSemantic[] semantics;
    private readonly AdvisorUtilityProfileDescriptor descriptor;

    public ConservativeCombatAdvisorStatFamily(ConservativeCombatFamilySpec spec)
    {
        this.spec = spec;
        semantics = new[]
        {
            spec.PrimaryStat, EquipmentStatSemantic.Vitality, spec.WeaponDamage,
            EquipmentStatSemantic.PhysicalDefense, EquipmentStatSemantic.MagicalDefense,
            EquipmentStatSemantic.CriticalHit, EquipmentStatSemantic.Determination,
            EquipmentStatSemantic.DirectHit, spec.Speed,
        }.Concat(spec.AdditionalSemantics).Distinct().ToArray();
        var context = new AdvisorUtilityContextDescriptor(
            $"general-{spec.Id}-combat",
            $"General{spec.Id.Replace("-", string.Empty)}Combat",
            spec.ContextLabel);
        descriptor = new($"squire.{spec.Id}.player", "7.51-v1", AdvisorProfileCalibrationState.Supported, [context], context.Id);
    }

    public AdvisorUtilityProfileDescriptor ProfileDescriptor => descriptor;
    public IReadOnlySet<uint> SupportedClassJobIds => spec.ClassJobIds;
    public string FamilyLabel => spec.Label;
    public string CoverageJobLabel => spec.CoverageLabel;
    public IReadOnlyList<EquipmentStatSemantic> RelevantSemantics => semantics;
    public bool IsRelevantSemantic(EquipmentStatSemantic semantic) => semantics.Contains(semantic);
    public AdvisorUtilityContextDescriptor ResolveContext(string? value) => descriptor.ResolveContext(value);

    public EquipmentSolverUtilityVector VectorFromSemantics(IReadOnlyDictionary<EquipmentStatSemantic, int> stats) =>
        new(semantics.Select(semantic => new EquipmentSolverUtilityComponent(Key(semantic), stats.GetValueOrDefault(semantic))).ToArray());

    public IEquipmentExactSolverUtilityModel CreateUtilityModel(
        string contextId,
        IReadOnlyDictionary<EquipmentStatSemantic, int> baseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int>? fixedStats,
        uint classJobId,
        uint characterLevel) =>
        new ConservativeCombatUtilityProfile(
            spec,
            descriptor,
            VectorFromSemantics(baseline),
            fixedStats is null ? null : VectorFromSemantics(fixedStats),
            classJobId,
            characterLevel);

    public AdvisorAuthorityAssessment AssessAuthority(
        IEquipmentExactSolverUtilityModel model,
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil) =>
        ((ConservativeCombatUtilityProfile)model).AssessAuthority(candidate);

    public IAdvisorSolverReplay? CaptureReplay(
        EquipmentExactFrontierRequest request,
        string contextId,
        uint classJobId,
        uint characterLevel,
        IReadOnlyDictionary<EquipmentStatSemantic, int> offerBaseline,
        IReadOnlyDictionary<EquipmentStatSemantic, int> fixedStats) => null;

    public EquipmentSolverUtilityVector VectorFromDefinition(EquipmentStatProfile profile) =>
        VectorFromSemantics(semantics.ToDictionary(semantic => semantic, semantic => DefinitionValue(profile, semantic)));

    public bool IsDefinitionOwnedSemantic(EquipmentStatSemantic semantic) => semantic is
        EquipmentStatSemantic.PhysicalDamage or EquipmentStatSemantic.MagicalDamage or
        EquipmentStatSemantic.PhysicalDefense or EquipmentStatSemantic.MagicalDefense or
        EquipmentStatSemantic.BlockStrength or EquipmentStatSemantic.BlockRate;

    public bool TryGetNonParameterDefinitionValue(
        EquipmentStatProfile profile,
        EquipmentStatSemantic semantic,
        out int value)
    {
        value = DefinitionValue(profile, semantic);
        return profile.IsComplete && IsDefinitionOwnedSemantic(semantic);
    }

    private static int DefinitionValue(EquipmentStatProfile profile, EquipmentStatSemantic semantic) => semantic switch
    {
        EquipmentStatSemantic.PhysicalDamage => profile.PhysicalDamage,
        EquipmentStatSemantic.MagicalDamage => profile.MagicalDamage,
        EquipmentStatSemantic.PhysicalDefense => profile.PhysicalDefense,
        EquipmentStatSemantic.MagicalDefense => profile.MagicalDefense,
        EquipmentStatSemantic.BlockStrength => profile.BlockStrength,
        EquipmentStatSemantic.BlockRate => profile.BlockRate,
        _ => profile.Parameters.Where(value => value.Semantic == semantic).Sum(value => value.Value),
    };

    internal static string Key(EquipmentStatSemantic semantic) => semantic.ToString().ToLowerInvariant();
}

public sealed class ConservativeCombatUtilityProfile :
    IEquipmentExactSolverUtilityModel,
    IEquipmentPartialDominanceCoordinateModel,
    IEquipmentSeparablePartialUtilityCanonicalizationModel
{
    private readonly ConservativeCombatFamilySpec spec;
    private readonly EquipmentThresholdUtilityModel model;
    private readonly EquipmentUtilityEvaluation baseline;

    public ConservativeCombatUtilityProfile(
        ConservativeCombatFamilySpec spec,
        AdvisorUtilityProfileDescriptor descriptor,
        EquipmentSolverUtilityVector baseline,
        EquipmentSolverUtilityVector? fixedStats,
        uint classJobId,
        uint level)
    {
        this.spec = spec;
        var semantics = baseline.Components
            .Select(component => Enum.Parse<EquipmentStatSemantic>(component.Key, true))
            .ToArray();
        var profile = new JobUtilityProfile(
            new(descriptor.Id, descriptor.Version),
            spec.Label,
            spec.ClassJobIds,
            new HashSet<string>(StringComparer.Ordinal) { descriptor.DefaultContextId },
            semantics.Select(semantic => new EquipmentUtilityRule(
                ConservativeCombatAdvisorStatFamily.Key(semantic),
                semantic,
                semantic == spec.Speed ? EquipmentUtilityRuleKind.ContextualOnly : EquipmentUtilityRuleKind.PreferMore,
                semantic == spec.Speed ? 0d : 1d,
                null,
                semantic == spec.Speed
                    ? $"{semantic} changes require job-specific timing evidence."
                    : $"{semantic} is retained componentwise.")).ToArray(),
            $"Patch 7.51 conservative {spec.Label} profile: exact componentwise no-loss ordering with unchanged {spec.Speed}.");
        var supported = spec.ClassJobIds.Contains(classJobId) &&
            spec.MinimumLevels.TryGetValue(classJobId, out var minimum) &&
            level >= minimum && level <= 100;
        var components = semantics.Select(semantic => new EquipmentUtilityComponentDefinition(
            ConservativeCombatAdvisorStatFamily.Key(semantic),
            semantic,
            semantic is EquipmentStatSemantic.PhysicalDamage or EquipmentStatSemantic.MagicalDamage ? 1d : 100d,
            100d,
            $"Bounded {semantic} progress inside the conservative {spec.Label} profile.")).ToArray();
        model = new(new(
            profile,
            new(descriptor.DefaultContextId, classJobId, level, spec.ContextLabel, ["patch:7.51", spec.Id, "supported"]),
            baseline,
            components,
            [],
            1d,
            [$"{spec.Speed} trades require job-specific timing evidence.", "Encounter-specific stat weights are intentionally not inferred."],
            supported,
            supported ? [] : [$"{spec.CoverageLabel} requires a valid supported class/job level through 100."],
            fixedStats,
            components.Sum(component => component.MaximumContribution),
            100d));
        this.baseline = model.Evaluate(baseline);
    }

    public EquipmentUtilityEvaluation Evaluate(EquipmentSolverUtilityVector completed) => model.Evaluate(completed);
    public EquipmentPartialUtilityDominance ComparePartial(EquipmentSolverUtilityVector candidate, EquipmentSolverUtilityVector other) => model.ComparePartial(candidate, other);
    public IReadOnlyList<long> GetPartialDominanceCoordinates(EquipmentSolverUtilityVector utility) => model.GetPartialDominanceCoordinates(utility);
    public EquipmentSolverUtilityVector CanonicalizePartialUtility(EquipmentSolverUtilityVector utility) => model.CanonicalizePartialUtility(utility);
    public long CanonicalizePartialUtilityComponent(string componentKey, long units) => model.CanonicalizePartialUtilityComponent(componentKey, units);

    public AdvisorAuthorityAssessment AssessAuthority(EquipmentUtilityEvaluation candidate)
    {
        var reasons = new List<string>();
        if (candidate.Assessment != UpgradeAssessment.ClearImprovement)
        {
            reasons.Add(candidate.Assessment == UpgradeAssessment.ContextDependent
                ? $"The candidate trades a modeled {spec.Label} component; the conservative profile abstains."
                : "The candidate is not an exact componentwise no-loss improvement over the observed baseline.");
        }
        var speedKey = ConservativeCombatAdvisorStatFamily.Key(spec.Speed);
        if (candidate.RawStats.Single(value => value.Source == speedKey).Value !=
            baseline.RawStats.Single(value => value.Source == speedKey).Value)
        {
            reasons.Add($"{spec.Speed} changed; job-specific timing evidence is required.");
        }
        return new(reasons.Count == 0, candidate.Assessment, [], reasons);
    }
}
