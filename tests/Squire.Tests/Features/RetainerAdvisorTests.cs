using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RetainerAdvisorTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Installed_venture_definition_generation_is_stable_and_changes_with_thresholds()
    {
        var first = RetainerVentureObjectiveCatalog.DefinitionGeneration(
            10, 20, RetainerProcurementProfileKind.Battle, 15, [new(15, 5), new(20, 7)]);
        var repeated = RetainerVentureObjectiveCatalog.DefinitionGeneration(
            10, 20, RetainerProcurementProfileKind.Battle, 15, [new(15, 5), new(20, 7)]);
        var changed = RetainerVentureObjectiveCatalog.DefinitionGeneration(
            10, 20, RetainerProcurementProfileKind.Battle, 15, [new(15, 5), new(21, 7)]);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void Battle_profile_authorizes_only_an_observed_venture_outcome_gain()
    {
        var objective = Objective(RetainerProcurementProfileKind.Battle, required: 15,
            new(15, 5), new(20, 7));
        var model = new RetainerProcurementUtilityProfile(
            objective,
            new(15, 0, 0, 0),
            classJobId: 21,
            level: 100,
            slotCount: 12);

        var sameYield = model.Evaluate(RetainerProcurementUtilityProfile.Vector(
            RetainerProcurementProfileKind.Battle, itemLevel: 19 * 12, gathering: 0, perception: 0));
        var improvedYield = model.Evaluate(RetainerProcurementUtilityProfile.Vector(
            RetainerProcurementProfileKind.Battle, itemLevel: 20 * 12, gathering: 0, perception: 0));

        Assert.Equal(UpgradeAssessment.Equivalent, sameYield.Assessment);
        Assert.Equal(UpgradeAssessment.ClearImprovement, improvedYield.Assessment);
        Assert.Equal(7, RetainerProcurementOutcomeEvaluator.Evaluate(objective, model.Stats(
            RetainerProcurementUtilityProfile.Vector(RetainerProcurementProfileKind.Battle, 20 * 12, 0, 0))).Quantity);
    }

    [Fact]
    public void Gathering_profile_models_gathering_and_perception_without_player_stats()
    {
        var objective = Objective(RetainerProcurementProfileKind.Gathering, required: 100,
            new(100, 5), new(200, 7));
        var family = new RetainerAdvisorStatFamily(objective, classJobId: 16, slotCount: 12);
        var definition = Definition(1, "Gathering helm", EquipmentSlot.Head, 16, itemLevel: 10,
            gathering: 12, perception: 18);

        var vector = family.VectorFromDefinition(definition, definition.StatProfile!);
        var semantics = vector.Components.ToDictionary(value => value.Key, value => value.Units);

        Assert.Equal(2, semantics.Count);
        Assert.DoesNotContain(EquipmentStatSemantic.ItemLevel, family.RelevantSemantics);
        Assert.Throws<InvalidOperationException>(() => family.VectorFromDefinition(definition.StatProfile!));
    }

    [Fact]
    public void Installed_venture_arrays_map_base_and_progressive_yield_tiers_exactly()
    {
        var thresholds = RetainerVentureObjectiveCatalog.BuildThresholds(
            [5, 7, 10, 12, 15],
            [5, 11, 16, 21]);

        Assert.Equal(
            [(0, 5), (5, 7), (11, 10), (16, 12), (21, 15)],
            thresholds.Select(value => (value.RequiredStat, value.Quantity)).ToArray());
    }

    [Fact]
    public void Rendered_retainer_baseline_is_owner_target_and_slot_bound()
    {
        var snapshot = Snapshot();
        var objective = Objective(RetainerProcurementProfileKind.Battle, required: 10, new(10, 5), new(20, 7));
        var evidence = Evidence();
        var target = Target(evidence, objective);
        var definitions = Definitions();
        var family = new RetainerAdvisorStatFamily(objective, 21, 12);

        var baseline = RetainerAdvisorBaselineAssembler.Assemble(
            snapshot,
            target,
            family,
            name => definitions.Values.Where(value => value.Name == name).ToArray());

        Assert.Equal(PlayerAdvisorBaselineStatus.Complete, baseline.Status);
        Assert.Equal(PlayerAdvisorBaselineTargetKind.Retainer, baseline.Target?.Kind);
        Assert.Equal(12, baseline.EquippedSlots.Count);
        Assert.Equal(120, baseline.TotalStats[EquipmentStatSemantic.ItemLevel]);
        Assert.All(baseline.EquippedSlots, slot => Assert.StartsWith("RetainerWorn:42", slot.Instance?.Fingerprint.Container));
        Assert.True(PlayerAdvisorBaselineAssembler.IsCompleteAndConsistent(baseline, family, out var diagnostic), diagnostic);
    }

    [Fact]
    public void Rendered_retainer_baseline_refuses_owner_or_slot_identity_drift()
    {
        var snapshot = Snapshot();
        var objective = Objective(RetainerProcurementProfileKind.Battle, required: 10,
            new RetainerYieldThreshold(10, 5));
        var evidence = Evidence();
        var target = Target(evidence, objective);
        var family = new RetainerAdvisorStatFamily(objective, 21, 12);
        var definitions = Definitions();

        var wrongOwner = RetainerAdvisorBaselineAssembler.Assemble(
            snapshot,
            target with { RetainerMetadata = target.RetainerMetadata! with { OwnerContentId = 99 } },
            family,
            name => definitions.Values.Where(value => value.Name == name).ToArray());
        var ambiguousItem = RetainerAdvisorBaselineAssembler.Assemble(
            snapshot,
            target,
            family,
            name => definitions.Values.Where(value => value.Name == name).Concat(
                definitions.Values.Where(value => value.Name == name)).ToArray());

        Assert.Equal(PlayerAdvisorBaselineStatus.Incomplete, wrongOwner.Status);
        Assert.Equal(PlayerAdvisorBaselineStatus.Incomplete, ambiguousItem.Status);
    }

    [Fact]
    public void Explicit_rendered_empty_slot_remains_a_truthful_zero_utility_baseline_gap()
    {
        var snapshot = Snapshot();
        var objective = Objective(RetainerProcurementProfileKind.Battle, required: 9, new(9, 5), new(10, 7));
        var complete = Evidence();
        var emptyHead = complete with
        {
            Equipment = complete.Equipment.Select(value => value.PositionKey == "head"
                ? value with { Status = RenderedEquipmentSlotObservationStatus.Empty, Item = null }
                : value).ToArray(),
        };
        var definitions = Definitions();
        var family = new RetainerAdvisorStatFamily(objective, 21, 12);

        var baseline = RetainerAdvisorBaselineAssembler.Assemble(
            snapshot,
            Target(emptyHead, objective),
            family,
            name => definitions.Values.Where(value => value.Name == name).ToArray());

        Assert.Equal(PlayerAdvisorBaselineStatus.Complete, baseline.Status);
        var head = Assert.Single(baseline.EquippedSlots, value => value.PositionKey == "head");
        Assert.Null(head.Instance);
        Assert.Null(head.Definition);
        Assert.Equal(110, baseline.TotalStats[EquipmentStatSemantic.ItemLevel]);
        Assert.True(PlayerAdvisorBaselineAssembler.IsCompleteAndConsistent(baseline, family, out var diagnostic), diagnostic);
    }

    [Fact]
    public void Battle_retainer_without_an_offhand_counts_the_main_hand_twice()
    {
        var snapshot = Snapshot();
        var objective = Objective(
            RetainerProcurementProfileKind.Battle,
            required: 10,
            new RetainerYieldThreshold(10, 5));
        var complete = Evidence();
        var emptyOffhand = complete with
        {
            Equipment = complete.Equipment.Select(value => value.PositionKey == "off-hand"
                ? value with { Status = RenderedEquipmentSlotObservationStatus.Empty, Item = null }
                : value).ToArray(),
        };
        var definitions = Definitions();
        var family = new RetainerAdvisorStatFamily(objective, 21, 12, mainHandCountsTwice: true);

        var baseline = RetainerAdvisorBaselineAssembler.Assemble(
            snapshot,
            Target(emptyOffhand, objective),
            family,
            name => definitions.Values.Where(value => value.Name == name).ToArray());
        var mainHand = definitions.Values.Single(value => value.Slot == EquipmentSlot.MainHand);
        var vector = family.VectorFromDefinition(mainHand, mainHand.StatProfile!);

        Assert.Equal(120, baseline.TotalStats[EquipmentStatSemantic.ItemLevel]);
        Assert.Equal(20, vector.Components.Single().Units);
        Assert.True(PlayerAdvisorBaselineAssembler.IsCompleteAndConsistent(baseline, family, out var diagnostic), diagnostic);
    }

    private static RetainerProcurementObjective Objective(
        RetainerProcurementProfileKind profile,
        int required,
        params RetainerYieldThreshold[] thresholds) =>
        new("venture:synthetic", profile, required, thresholds, Guid.NewGuid(), CapturedAt, true);

    private static OutfitterTarget Target(
        RenderedRetainerEquipmentEvidence evidence,
        RetainerProcurementObjective objective) =>
        new(
            "retainer:42",
            OutfitterTargetKind.Retainer,
            "Venture",
            "WAR · Lv. 100",
            RetainerMetadata: new(10, "Fran Example", "Siren", 42, "Venture", 21, 100),
            OwnerCharacterName: "Fran Example",
            OwnerHomeWorld: "Siren",
            IsCurrentCharacter: true,
            IsReady: true,
            RetainerEquipmentEvidence: evidence,
            RetainerObjective: objective);

    private static RenderedRetainerEquipmentEvidence Evidence() => new(
        RenderedRetainerEquipmentEvidenceStatus.Complete,
        "retainer:42",
        CapturedAt,
        "Fran Example",
        "Siren",
        "Venture",
        21,
        100,
        PlayerAdvisorEquippedSlotMap.All.Select((position, index) => new RenderedEquipmentSlotObservation(
            position.PositionKey,
            Slot(position.Position),
            RenderedEquipmentSlotObservationStatus.Equipped,
            new(
                RenderedItemDetailStatus.Complete,
                $"Retainer item {index + 1}",
                RenderedItemQuality.Normal,
                10,
                1,
                "Disciples of War or Magic",
                null,
                new Dictionary<string, int>(),
                new Dictionary<string, int>(),
                "synthetic"))).ToArray(),
        "synthetic complete evidence");

    private static CharacterEquipmentSnapshot Snapshot()
    {
        var scope = new CharacterScope(10, "Fran Example", 57);
        return new(
            Guid.NewGuid(),
            new(scope, 57, 3, CapturedAt, true, SnapshotComponentStatus.Complete),
            [],
            [],
            [],
            new Dictionary<uint, EquipmentItemDefinition>(),
            new([
                new("identity", SnapshotComponentStatus.Complete),
                new("equipped", SnapshotComponentStatus.Complete),
            ]));
    }

    private static Dictionary<uint, EquipmentItemDefinition> Definitions() =>
        PlayerAdvisorEquippedSlotMap.All.Select((position, index) =>
            Definition((uint)(index + 1), $"Retainer item {index + 1}", Slot(position.Position), 21, 10))
            .ToDictionary(value => value.ItemId);

    private static EquipmentItemDefinition Definition(
        uint itemId,
        string name,
        EquipmentSlot slot,
        uint classJobId,
        uint itemLevel,
        int gathering = 0,
        int perception = 0) => new(
        itemId,
        name,
        1,
        itemLevel,
        slot,
        new HashSet<uint> { classJobId },
        1,
        true,
        false,
        null,
        true,
        1,
        true,
        false,
        false,
        false,
        new([
            new(72, EquipmentStatSemantic.Gathering, gathering, false),
            new(73, EquipmentStatSemantic.Perception, perception, false),
        ], 0, 0, 0, 0, true));

    private static EquipmentSlot Slot(EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => EquipmentSlot.MainHand,
        EquipmentLoadoutPosition.OffHand => EquipmentSlot.OffHand,
        EquipmentLoadoutPosition.Head => EquipmentSlot.Head,
        EquipmentLoadoutPosition.Body => EquipmentSlot.Body,
        EquipmentLoadoutPosition.Hands => EquipmentSlot.Hands,
        EquipmentLoadoutPosition.Legs => EquipmentSlot.Legs,
        EquipmentLoadoutPosition.Feet => EquipmentSlot.Feet,
        EquipmentLoadoutPosition.Ears => EquipmentSlot.Ears,
        EquipmentLoadoutPosition.Neck => EquipmentSlot.Neck,
        EquipmentLoadoutPosition.Wrists => EquipmentSlot.Wrists,
        _ => EquipmentSlot.Ring,
    };
}
