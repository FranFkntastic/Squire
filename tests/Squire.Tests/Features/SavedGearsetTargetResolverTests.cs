using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class SavedGearsetTargetResolverTests
{
    private static readonly CharacterScope Scope = new(77, "Advisor", 21);
    private static readonly CharacterJobSnapshot Miner = new(
        16,
        "MIN",
        "Miner",
        100,
        true,
        null,
        "Gatherer",
        EquipmentStatSemantic.Gathering,
        EquipmentDiscipline.Gatherer);

    [Fact]
    public void Resolve_preserves_ring_side_and_exact_owned_instance_identity()
    {
        var left = Reference(EquipmentLoadoutPosition.LeftRing, 10_001, materiaId: 501, materiaGrade: 11);
        var right = Reference(EquipmentLoadoutPosition.RightRing, 10_002, materiaId: 502, materiaGrade: 12);
        var target = Target([left, right]);
        var snapshot = Snapshot(
            target.Gearset!,
            [Instance(3, left), Instance(8, right)],
            [Definition(left), Definition(right)]);

        var result = SavedGearsetTargetResolver.Resolve(snapshot, target);

        Assert.Equal(SavedGearsetTargetResolutionStatus.Complete, result.Status);
        Assert.NotNull(result.Fingerprint);
        Assert.Equal(64, result.Fingerprint.Value.Length);
        Assert.Equal(12, result.Slots.Count);
        Assert.Equal(3, Assert.Single(result.Slots, slot => slot.Position == EquipmentLoadoutPosition.LeftRing).Instance!.Fingerprint.SlotIndex);
        Assert.Equal(8, Assert.Single(result.Slots, slot => slot.Position == EquipmentLoadoutPosition.RightRing).Instance!.Fingerprint.SlotIndex);
    }

    [Fact]
    public void Resolve_exposes_ambiguous_duplicate_as_target_gap()
    {
        var head = Reference(EquipmentLoadoutPosition.Head, 10_001, materiaId: 501, materiaGrade: 11);
        var target = Target([head]);
        var snapshot = Snapshot(
            target.Gearset!,
            [Instance(3, head, "ArmoryHead"), Instance(8, head, "ArmoryHead")],
            [Definition(head)]);

        var result = SavedGearsetTargetResolver.Resolve(snapshot, target);

        Assert.Equal(SavedGearsetTargetResolutionStatus.Incomplete, result.Status);
        var gap = Assert.Single(result.Slots, slot => slot.Position == EquipmentLoadoutPosition.Head);
        Assert.False(gap.IsResolved);
        Assert.Contains("multiple owned instances", gap.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_keeps_missing_assignment_explicit_even_when_similar_item_exists()
    {
        var head = Reference(EquipmentLoadoutPosition.Head, 10_001, materiaId: 501, materiaGrade: 11) with { IsMissing = true };
        var target = Target([head]);
        var snapshot = Snapshot(target.Gearset!, [Instance(3, head, "ArmoryHead")], [Definition(head)]);

        var result = SavedGearsetTargetResolver.Resolve(snapshot, target);

        Assert.Equal(SavedGearsetTargetResolutionStatus.Incomplete, result.Status);
        Assert.Contains("marked missing", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_tracks_saved_materia_but_not_inventory_location()
    {
        var head = Reference(EquipmentLoadoutPosition.Head, 10_001, materiaId: 501, materiaGrade: 11);
        var target = Target([head]);
        var first = SavedGearsetTargetResolver.Resolve(
            Snapshot(target.Gearset!, [Instance(3, head, "ArmoryHead")], [Definition(head)]),
            target);
        var moved = SavedGearsetTargetResolver.Resolve(
            Snapshot(target.Gearset!, [Instance(9, head, "ArmoryHead")], [Definition(head)]),
            target);
        var remeldedReference = head with { MateriaGrades = [12] };
        var remeldedTarget = Target([remeldedReference]);
        var remelded = SavedGearsetTargetResolver.Resolve(
            Snapshot(remeldedTarget.Gearset!, [Instance(3, remeldedReference, "ArmoryHead")], [Definition(remeldedReference)]),
            remeldedTarget);

        Assert.Equal(first.Fingerprint!.Value, moved.Fingerprint!.Value);
        Assert.NotEqual(first.Fingerprint.Value, remelded.Fingerprint!.Value);
    }

    [Fact]
    public void Assemble_builds_unbuffed_inactive_gatherer_baseline_from_exact_slot_contributions()
    {
        var head = Reference(EquipmentLoadoutPosition.Head, 10_001, materiaId: 501, materiaGrade: 11);
        var target = Target([head]);
        var snapshot = Snapshot(target.Gearset!, [Instance(3, head, "ArmoryHead")], [Definition(head)]);
        var resolution = SavedGearsetTargetResolver.Resolve(snapshot, target);
        var capture = new SavedGearsetItemStatCapture(
            EquipmentLoadoutPosition.Head,
            resolution.Slots.Single(slot => slot.Position == EquipmentLoadoutPosition.Head).Instance!.Fingerprint,
            new Dictionary<EquipmentStatSemantic, int>
            {
                [EquipmentStatSemantic.Gathering] = 100,
                [EquipmentStatSemantic.Perception] = 80,
                [EquipmentStatSemantic.GatheringPoints] = 10,
            });

        var baseline = SavedGearsetAdvisorBaselineAssembler.Assemble(
            snapshot,
            target,
            resolution,
            GathererAdvisorStatFamily.Instance,
            [capture],
            PlayerAdvisorTrustedCapture.Complete(Guid.NewGuid(), DateTimeOffset.UtcNow));

        Assert.Equal(PlayerAdvisorBaselineStatus.Complete, baseline.Status);
        Assert.Equal(PlayerAdvisorBaselineTargetKind.SavedGearset, baseline.Target?.Kind);
        Assert.Equal(resolution.Fingerprint!.Value, baseline.Target?.AuthorityFingerprint);
        Assert.Equal(0, baseline.FixedStats[EquipmentStatSemantic.Gathering]);
        Assert.Equal(0, baseline.FixedStats[EquipmentStatSemantic.Perception]);
        Assert.Equal(400, baseline.FixedStats[EquipmentStatSemantic.GatheringPoints]);
        Assert.Equal(100, baseline.TotalStats[EquipmentStatSemantic.Gathering]);
        Assert.Equal(80, baseline.TotalStats[EquipmentStatSemantic.Perception]);
        Assert.Equal(410, baseline.TotalStats[EquipmentStatSemantic.GatheringPoints]);
        Assert.True(PlayerAdvisorBaselineAssembler.IsCompleteAndConsistent(
            baseline,
            GathererAdvisorStatFamily.Instance,
            out var diagnostic), diagnostic);

        var changedHead = baseline.EquippedSlots.Single(slot => slot.Position == EquipmentLoadoutPosition.Head) with
        {
            MateriaGrades = [12],
        };
        var changedMateria = baseline with
        {
            EquippedSlots = baseline.EquippedSlots
                .Select(slot => slot.Position == EquipmentLoadoutPosition.Head ? changedHead : slot)
                .ToArray(),
        };
        Assert.False(PlayerAdvisorBaselineAssembler.IsCompleteAndConsistent(
            changedMateria,
            GathererAdvisorStatFamily.Instance,
            out _));
    }

    [Fact]
    public void Assemble_rejects_stat_capture_from_different_owned_instance()
    {
        var head = Reference(EquipmentLoadoutPosition.Head, 10_001, materiaId: 501, materiaGrade: 11);
        var target = Target([head]);
        var snapshot = Snapshot(target.Gearset!, [Instance(3, head, "ArmoryHead")], [Definition(head)]);
        var resolution = SavedGearsetTargetResolver.Resolve(snapshot, target);
        var wrongFingerprint = resolution.Slots
            .Single(slot => slot.Position == EquipmentLoadoutPosition.Head)
            .Instance!.Fingerprint with { SlotIndex = 9 };
        var capture = new SavedGearsetItemStatCapture(
            EquipmentLoadoutPosition.Head,
            wrongFingerprint,
            new Dictionary<EquipmentStatSemantic, int>
            {
                [EquipmentStatSemantic.Gathering] = 100,
                [EquipmentStatSemantic.Perception] = 80,
                [EquipmentStatSemantic.GatheringPoints] = 10,
            });

        var baseline = SavedGearsetAdvisorBaselineAssembler.Assemble(
            snapshot,
            target,
            resolution,
            GathererAdvisorStatFamily.Instance,
            [capture]);

        Assert.Equal(PlayerAdvisorBaselineStatus.Inconsistent, baseline.Status);
        Assert.Contains("does not match", baseline.Diagnostic, StringComparison.Ordinal);
    }

    private static OutfitterTarget Target(IReadOnlyList<GearsetItemReference> items)
    {
        var gearset = new GearsetSnapshot(4, "Miner", Miner.ClassJobId, items, true);
        return new("gearset:4", OutfitterTargetKind.Gearset, gearset.Name, "Gearset 5", Miner, gearset);
    }

    private static CharacterEquipmentSnapshot Snapshot(
        GearsetSnapshot gearset,
        IReadOnlyList<EquipmentInstanceSnapshot> instances,
        IReadOnlyList<EquipmentItemDefinition> definitions) =>
        new(
            Guid.NewGuid(),
            new(Scope, 21, 8, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            [Miner],
            [gearset],
            instances,
            definitions.ToDictionary(value => value.ItemId),
            new(
            [
                new("identity", SnapshotComponentStatus.Complete),
                new("gearsets", SnapshotComponentStatus.Complete),
                new("equipped", SnapshotComponentStatus.Complete),
                new("armoury", SnapshotComponentStatus.Complete),
                new("definitions", SnapshotComponentStatus.Complete),
            ]));

    private static GearsetItemReference Reference(
        EquipmentLoadoutPosition position,
        uint itemId,
        uint materiaId,
        byte materiaGrade) =>
        new(
            position is EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing
                ? EquipmentSlot.Ring
                : EquipmentSlot.Head,
            itemId,
            false,
            position,
            [materiaId],
            [materiaGrade],
            null,
            [0, 0]);

    private static EquipmentInstanceSnapshot Instance(
        int slotIndex,
        GearsetItemReference reference,
        string container = "ArmoryRings") =>
        new(
            new(
                Scope,
                container,
                slotIndex,
                reference.ItemId,
                reference.IsHighQuality == true,
                1,
                30_000,
                0,
                null,
                reference.MateriaIds!,
                reference.GlamourId,
                reference.Stains!,
                reference.MateriaGrades),
            DateTimeOffset.UtcNow,
            false);

    private static EquipmentItemDefinition Definition(GearsetItemReference reference) =>
        new(
            reference.ItemId,
            $"Item {reference.ItemId}",
            1,
            1,
            reference.Slot,
            new HashSet<uint> { Miner.ClassJobId },
            1,
            true,
            false,
            true,
            true,
            1,
            true,
            false,
            true,
            false);
}
