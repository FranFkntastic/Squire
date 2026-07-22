using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Observation;

public enum PlayerAdvisorBaselineStatus
{
    Complete,
    Unavailable,
    Incomplete,
    Inconsistent,
    Unsupported,
}

public enum PlayerAdvisorBaselineTargetKind
{
    ActiveLoadout,
    SavedGearset,
}

public sealed record PlayerAdvisorBaselineTarget(
    PlayerAdvisorBaselineTargetKind Kind,
    string Key,
    string AuthorityFingerprint,
    SavedGearsetTargetFingerprint? SavedGearset = null);

public sealed record PlayerAdvisorEquippedPosition(
    int EquippedIndex,
    EquipmentLoadoutPosition Position,
    string PositionKey);

public static class PlayerAdvisorEquippedSlotMap
{
    public static IReadOnlyList<PlayerAdvisorEquippedPosition> All { get; } =
    [
        new(0, EquipmentLoadoutPosition.MainHand, "main-hand"),
        new(1, EquipmentLoadoutPosition.OffHand, "off-hand"),
        new(2, EquipmentLoadoutPosition.Head, "head"),
        new(3, EquipmentLoadoutPosition.Body, "body"),
        new(4, EquipmentLoadoutPosition.Hands, "hands"),
        new(6, EquipmentLoadoutPosition.Legs, "legs"),
        new(7, EquipmentLoadoutPosition.Feet, "feet"),
        new(8, EquipmentLoadoutPosition.Ears, "ears"),
        new(9, EquipmentLoadoutPosition.Neck, "neck"),
        new(10, EquipmentLoadoutPosition.Wrists, "wrists"),
        new(11, EquipmentLoadoutPosition.RightRing, "ring-right"),
        new(12, EquipmentLoadoutPosition.LeftRing, "ring-left"),
    ];

    public static PlayerAdvisorEquippedPosition? Find(int equippedIndex) =>
        All.FirstOrDefault(value => value.EquippedIndex == equippedIndex);
}

public sealed record PlayerAdvisorEquippedSlot(
    EquipmentLoadoutPosition Position,
    string PositionKey,
    EquipmentInstanceSnapshot? Instance,
    EquipmentItemDefinition? Definition,
    EquipmentQuality? Quality,
    EquipmentSolverUtilityVector Utility,
    IReadOnlyList<uint> MateriaIds,
    IReadOnlyList<byte> MateriaGrades);

public sealed record PlayerAdvisorBaseline(
    PlayerAdvisorBaselineStatus Status,
    CharacterScope? Character,
    uint? ClassJobId,
    short? Level,
    short? EffectiveLevel,
    bool? IsLevelSynced,
    IReadOnlyDictionary<EquipmentStatSemantic, int> TotalStats,
    IReadOnlyDictionary<EquipmentStatSemantic, int> FixedStats,
    IReadOnlyList<PlayerAdvisorEquippedSlot> EquippedSlots,
    CharacterEquipmentSnapshot? EquipmentSnapshot,
    string Diagnostic,
    PlayerAdvisorBaselineTarget? Target = null)
{
    internal PlayerAdvisorCaptureProvenance? CaptureProvenance { get; init; }
}

public interface IPlayerAdvisorBaselineSource
{
    PlayerAdvisorBaseline Capture();
}

public interface IOutfitterTargetAdvisorBaselineSource : IPlayerAdvisorBaselineSource
{
    PlayerAdvisorBaseline Capture(OutfitterTarget target);
}

internal sealed record PlayerAdvisorCaptureHeader(
    CharacterScope Character,
    uint CurrentWorldId,
    uint ClassJobId,
    short Level,
    short EffectiveLevel,
    bool IsLevelSynced);

internal sealed record PlayerAdvisorTrustedCapture
{
    private PlayerAdvisorTrustedCapture(Guid captureId, DateTimeOffset completedAtUtc)
    {
        CaptureId = captureId;
        CompletedAtUtc = completedAtUtc;
    }

    public Guid CaptureId { get; }
    public DateTimeOffset CompletedAtUtc { get; }

    public static PlayerAdvisorTrustedCapture Complete(Guid captureId, DateTimeOffset completedAtUtc)
    {
        if (captureId == Guid.Empty || completedAtUtc == default)
            throw new ArgumentException("A non-default trusted player capture identity and completion time are required.");
        return new(captureId, completedAtUtc);
    }
}

internal sealed record PlayerAdvisorCaptureProvenance(
    Guid CaptureId,
    DateTimeOffset CompletedAtUtc,
    Guid EquipmentGenerationId,
    DateTimeOffset EquipmentIdentityCapturedAtUtc,
    uint CurrentWorldId);

internal sealed record PlayerAdvisorEquippedItemCapture(
    int EquippedIndex,
    uint ItemId,
    EquipmentQuality Quality,
    IReadOnlyDictionary<EquipmentStatSemantic, int> Contributions,
    IReadOnlyList<uint> MateriaIds,
    IReadOnlyList<byte> MateriaGrades);

internal static class PlayerAdvisorBaselineAssembler
{
    private const string EquippedContainer = "EquippedItems";

    public static PlayerAdvisorBaseline Assemble(
        CharacterEquipmentSnapshot snapshot,
        PlayerAdvisorCaptureHeader header,
        IAdvisorStatFamily? family,
        IReadOnlyDictionary<EquipmentStatSemantic, int> totalStats,
        IReadOnlyList<PlayerAdvisorEquippedItemCapture> equipped,
        PlayerAdvisorTrustedCapture? trustedCapture = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(totalStats);
        ArgumentNullException.ThrowIfNull(equipped);

        if (family is null)
            return Result(PlayerAdvisorBaselineStatus.Unsupported, snapshot, header, totalStats, EmptyStats(), [],
                AdvisorStatFamilies.UnsupportedDiagnostic(header.ClassJobId));
        if (header.Level is < 1 or > 100 || header.EffectiveLevel is < 1 or > 100 || header.EffectiveLevel > header.Level)
            return Result(PlayerAdvisorBaselineStatus.Incomplete, snapshot, header, totalStats, EmptyStats(), [],
                "A valid level and effective level are required for the advisor baseline.");
        if (header.IsLevelSynced)
            return Result(PlayerAdvisorBaselineStatus.Unsupported, snapshot, header, totalStats, EmptyStats(), [],
                "The advisor abstains while level sync is active because synced item contributions are not yet modeled.");
        if (snapshot.Identity.Status != SnapshotComponentStatus.Complete || snapshot.Identity.Scope is null ||
            snapshot.Identity.Scope != header.Character || snapshot.Identity.ActiveClassJobId != header.ClassJobId)
            return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                "The character equipment snapshot identity does not match the advisor capture header.");
        if (!ComponentIsComplete(snapshot, "equipped"))
            return Result(PlayerAdvisorBaselineStatus.Incomplete, snapshot, header, totalStats, EmptyStats(), [],
                "The equipped-items component is incomplete or unavailable.");
        if (family.RelevantSemantics.Any(semantic => !totalStats.ContainsKey(semantic)))
            return Result(PlayerAdvisorBaselineStatus.Unavailable, snapshot, header, totalStats, EmptyStats(), [],
                "One or more family-relevant player attributes are unavailable.");

        var capturesByIndex = equipped
            .GroupBy(value => value.EquippedIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (PlayerAdvisorEquippedSlotMap.All.Any(position =>
                !capturesByIndex.TryGetValue(position.EquippedIndex, out var captures) || captures.Length != 1))
            return Result(PlayerAdvisorBaselineStatus.Incomplete, snapshot, header, totalStats, EmptyStats(), [],
                "All twelve explicit equipped indices require one current item capture.");

        var slots = new List<PlayerAdvisorEquippedSlot>(PlayerAdvisorEquippedSlotMap.All.Count);
        var equippedTotals = family.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0);
        foreach (var position in PlayerAdvisorEquippedSlotMap.All)
        {
            var captured = capturesByIndex[position.EquippedIndex][0];
            var instances = snapshot.Instances.Where(value =>
                    value.IsEquipped &&
                    string.Equals(value.Fingerprint.Container, EquippedContainer, StringComparison.Ordinal) &&
                    value.Fingerprint.SlotIndex == position.EquippedIndex)
                .ToArray();
            if (captured.ItemId == 0)
            {
                if (instances.Length != 0 || captured.MateriaIds.Count != 0 || captured.MateriaGrades.Count != 0 ||
                    family.RelevantSemantics.Any(semantic => captured.Contributions.GetValueOrDefault(semantic) != 0))
                    return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                        $"Empty equipped index {position.EquippedIndex} has contradictory item or stat evidence.");
                slots.Add(new(
                    position.Position,
                    position.PositionKey,
                    null,
                    null,
                    null,
                    family.VectorFromSemantics(captured.Contributions),
                    [],
                    []));
                continue;
            }
            if (instances.Length != 1)
                return Result(PlayerAdvisorBaselineStatus.Incomplete, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} is missing or duplicated in the character equipment snapshot.");
            var instance = instances[0];
            if (instance.Fingerprint.ItemId != captured.ItemId ||
                instance.Fingerprint.IsHighQuality != (captured.Quality == EquipmentQuality.High) ||
                !instance.Fingerprint.MateriaIds.SequenceEqual(captured.MateriaIds))
                return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} changed while the advisor baseline was captured.");
            if (!snapshot.Definitions.TryGetValue(captured.ItemId, out var definition))
                return Result(PlayerAdvisorBaselineStatus.Incomplete, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} item {captured.ItemId} has no static equipment definition.");
            if (definition.ItemId != captured.ItemId || !DefinitionMatchesPosition(definition, position.Position))
                return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} definition does not match {position.PositionKey}.");
            if (!definition.EligibleClassJobIds.Contains(header.ClassJobId))
                return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} item {captured.ItemId} is not eligible for class/job {header.ClassJobId}.");
            if (definition.EquipLevel > header.Level)
                return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} item {captured.ItemId} requires level {definition.EquipLevel}, above the actual level {header.Level}.");
            if (family.RelevantSemantics.Any(semantic => !captured.Contributions.ContainsKey(semantic)))
                return Result(PlayerAdvisorBaselineStatus.Unavailable, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} is missing a family-relevant exact stat contribution.");

            foreach (var semantic in family.RelevantSemantics)
                equippedTotals[semantic] = checked(equippedTotals[semantic] + captured.Contributions[semantic]);
            slots.Add(new(
                position.Position,
                position.PositionKey,
                instance,
                definition,
                captured.Quality,
                family.VectorFromSemantics(captured.Contributions),
                captured.MateriaIds,
                captured.MateriaGrades));
        }

        var fixedStats = new Dictionary<EquipmentStatSemantic, int>();
        foreach (var semantic in family.RelevantSemantics)
        {
            var remainder = checked(totalStats[semantic] - equippedTotals[semantic]);
            if (remainder < 0)
                return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), slots,
                    $"Exact equipped {semantic} contributions exceed the player total.");
            fixedStats[semantic] = remainder;
        }

        return Result(
            PlayerAdvisorBaselineStatus.Complete,
            snapshot,
            header,
            totalStats,
            fixedStats,
            slots,
            $"Windowless {family.CoverageJobLabel} player baseline is complete.",
            trustedCapture);
    }

    public static bool IsCompleteAndConsistent(
        PlayerAdvisorBaseline? baseline,
        IAdvisorStatFamily? family,
        out string diagnostic)
    {
        diagnostic = "The player advisor baseline is incomplete or inconsistent.";
        if (baseline is not
            {
                Status: PlayerAdvisorBaselineStatus.Complete,
                Character: { } character,
                ClassJobId: { } classJobId,
                Level: { } level,
                EffectiveLevel: { } effectiveLevel,
                IsLevelSynced: false,
                EquipmentSnapshot: { } snapshot,
                CaptureProvenance: { } provenance,
            } ||
            family is null ||
            !family.SupportedClassJobIds.Contains(classJobId) ||
            character.LocalContentId == 0 ||
            character.HomeWorldId == 0 ||
            string.IsNullOrWhiteSpace(character.Name) ||
            level is < 1 or > 100 ||
            effectiveLevel != level ||
            baseline.TotalStats is null ||
            baseline.FixedStats is null ||
            baseline.EquippedSlots is null ||
            baseline.EquippedSlots.Count != PlayerAdvisorEquippedSlotMap.All.Count ||
            snapshot.GenerationId == Guid.Empty ||
            snapshot.Identity is null ||
            provenance.CaptureId == Guid.Empty ||
            provenance.CompletedAtUtc == default ||
            provenance.EquipmentGenerationId != snapshot.GenerationId ||
            provenance.EquipmentIdentityCapturedAtUtc == default ||
            provenance.EquipmentIdentityCapturedAtUtc != snapshot.Identity.CapturedAt ||
            provenance.CurrentWorldId == 0 ||
            snapshot.Instances is null ||
            snapshot.Definitions is null ||
            snapshot.Diagnostics?.Components is null ||
            snapshot.Diagnostics.Components.Any(component => component is null))
        {
            return false;
        }

        if (snapshot.Identity.Status != SnapshotComponentStatus.Complete ||
            !snapshot.Identity.IsLoggedIn ||
            snapshot.Identity.Scope != character ||
            snapshot.Identity.CurrentWorldId != provenance.CurrentWorldId ||
            ((baseline.Target?.Kind ?? PlayerAdvisorBaselineTargetKind.ActiveLoadout) == PlayerAdvisorBaselineTargetKind.ActiveLoadout &&
             snapshot.Identity.ActiveClassJobId != classJobId) ||
            snapshot.Identity.CapturedAt > provenance.CompletedAtUtc ||
            !ComponentIsComplete(snapshot, "identity") ||
            !ComponentIsComplete(snapshot, "equipped") ||
            baseline.Target is { Kind: PlayerAdvisorBaselineTargetKind.SavedGearset } target &&
            (string.IsNullOrWhiteSpace(target.Key) || string.IsNullOrWhiteSpace(target.AuthorityFingerprint)))
        {
            diagnostic = "The player advisor baseline does not match one complete equipment snapshot identity.";
            return false;
        }

        var isActiveLoadout = (baseline.Target?.Kind ?? PlayerAdvisorBaselineTargetKind.ActiveLoadout) ==
            PlayerAdvisorBaselineTargetKind.ActiveLoadout;
        if (snapshot.Instances.Any(instance => instance is null) ||
            baseline.EquippedSlots.Any(slot => slot is null) ||
            baseline.EquippedSlots.GroupBy(slot => slot.Position).Any(group => group.Count() != 1) ||
            PlayerAdvisorEquippedSlotMap.All.Any(position =>
                baseline.EquippedSlots.Count(slot =>
                    slot.Position == position.Position &&
                    string.Equals(slot.PositionKey, position.PositionKey, StringComparison.Ordinal)) != 1))
        {
            diagnostic = "The player advisor baseline requires every unique canonical equipped slot.";
            return false;
        }

        if (isActiveLoadout)
        {
            var canonicalIndices = PlayerAdvisorEquippedSlotMap.All
                .Select(position => position.EquippedIndex)
                .ToHashSet();
            var equippedInstances = snapshot.Instances
                .Where(instance => instance.IsEquipped)
                .ToArray();
            var supplementalEquipped = equippedInstances
                .Where(instance => !canonicalIndices.Contains(instance.Fingerprint.SlotIndex))
                .ToArray();
            if (equippedInstances.Any(instance =>
                    instance.CapturedAt == default ||
                    instance.CapturedAt > provenance.CompletedAtUtc ||
                    instance.Fingerprint is null ||
                    instance.Fingerprint.Character != character ||
                    !string.Equals(instance.Fingerprint.Container, EquippedContainer, StringComparison.Ordinal)) ||
                supplementalEquipped.Length > 1 ||
                supplementalEquipped.Any(instance =>
                    instance.Fingerprint.SlotIndex != 13 ||
                    !snapshot.Definitions.TryGetValue(instance.Fingerprint.ItemId, out var definition) ||
                    definition.Slot != EquipmentSlot.SoulCrystal ||
                    !definition.IsSoulCrystal))
            {
                diagnostic = "The active player advisor baseline has invalid equipped-instance evidence.";
                return false;
            }
        }
        else
        {
            var assignedInstances = new HashSet<EquipmentInstanceFingerprint>(EquipmentInstanceFingerprintComparer.Instance);
            if (baseline.EquippedSlots
                .Where(slot => slot.Instance is not null)
                .Any(slot => !assignedInstances.Add(slot.Instance!.Fingerprint)))
            {
                diagnostic = "Saved-gearset positions must resolve to distinct owned instances.";
                return false;
            }
        }

        var expectedEquipped = new Dictionary<EquipmentStatSemantic, int>();
        if (!baseline.TotalStats.Keys.Order().SequenceEqual(family.RelevantSemantics.Order()) ||
            !baseline.FixedStats.Keys.Order().SequenceEqual(family.RelevantSemantics.Order()))
        {
            diagnostic = "The player advisor baseline must contain exactly the family-relevant stat semantics.";
            return false;
        }
        foreach (var semantic in family.RelevantSemantics)
        {
            if (!baseline.TotalStats.TryGetValue(semantic, out var total) ||
                !baseline.FixedStats.TryGetValue(semantic, out var fixedValue) ||
                total < 0 ||
                fixedValue < 0 ||
                fixedValue > total)
            {
                diagnostic = "The player advisor baseline has inconsistent family-relevant total and fixed stats.";
                return false;
            }
            expectedEquipped[semantic] = total - fixedValue;
        }

        var actualEquipped = EquipmentSolverUtilityVector.Empty;
        foreach (var position in PlayerAdvisorEquippedSlotMap.All)
        {
            var slot = baseline.EquippedSlots.Single(value => value.Position == position.Position);
            if (slot.Utility?.Components is null ||
                slot.Utility.Components.Any(component =>
                    component is null || string.IsNullOrWhiteSpace(component.Key) || component.Units < 0) ||
                slot.MateriaIds is null ||
                slot.MateriaGrades is null ||
                slot.MateriaIds.Count != slot.MateriaGrades.Count)
            {
                diagnostic = $"Equipped slot '{position.PositionKey}' has malformed utility or materia evidence.";
                return false;
            }

            var instances = isActiveLoadout
                ? snapshot.Instances.Where(value =>
                        value.IsEquipped &&
                        string.Equals(value.Fingerprint.Container, EquippedContainer, StringComparison.Ordinal) &&
                        value.Fingerprint.SlotIndex == position.EquippedIndex)
                    .ToArray()
                : slot.Instance is null
                    ? []
                    : snapshot.Instances.Where(value =>
                            EquipmentInstanceFingerprintComparer.Instance.Equals(value.Fingerprint, slot.Instance.Fingerprint))
                        .ToArray();
            if (slot.Instance is null || slot.Definition is null || slot.Quality is null)
            {
                if (slot.Instance is not null || slot.Definition is not null || slot.Quality is not null ||
                    instances.Length != 0 || slot.MateriaIds.Count != 0 || slot.MateriaGrades.Count != 0 ||
                    slot.Utility.Components.Any(component => component.Units != 0))
                {
                    diagnostic = $"Empty equipped slot '{position.PositionKey}' has contradictory evidence.";
                    return false;
                }
                continue;
            }

            var instance = slot.Instance;
            var fingerprint = instance.Fingerprint;
            var definition = slot.Definition;
            if (fingerprint is null ||
                instances.Length != 1 ||
                !EquipmentInstanceFingerprintComparer.Instance.Equals(instances[0].Fingerprint, fingerprint) ||
                fingerprint.Character != character ||
                fingerprint.ItemId == 0 ||
                fingerprint.ItemId != definition.ItemId ||
                fingerprint.IsHighQuality != (slot.Quality == EquipmentQuality.High) ||
                slot.Quality is not (EquipmentQuality.Normal or EquipmentQuality.High) ||
                fingerprint.MateriaIds is null ||
                !fingerprint.MateriaIds.SequenceEqual(slot.MateriaIds) ||
                !(fingerprint.MateriaGrades ?? []).SequenceEqual(slot.MateriaGrades) ||
                !snapshot.Definitions.TryGetValue(definition.ItemId, out var snapshotDefinition) ||
                snapshotDefinition is null ||
                (!ReferenceEquals(snapshotDefinition, definition) && snapshotDefinition != definition) ||
                !DefinitionMatchesPosition(definition, position.Position) ||
                definition.EligibleClassJobIds is null ||
                !definition.EligibleClassJobIds.Contains(classJobId) ||
                definition.EquipLevel > level)
            {
                diagnostic = $"Equipped slot '{position.PositionKey}' is inconsistent with its snapshot instance and definition.";
                return false;
            }

            try
            {
                actualEquipped = actualEquipped.Add(slot.Utility);
            }
            catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
            {
                diagnostic = $"Equipped slot '{position.PositionKey}' has invalid utility evidence.";
                return false;
            }
        }

        try
        {
            var expected = family.VectorFromSemantics(expectedEquipped).Normalize().Components;
            var actual = actualEquipped.Normalize().Components;
            if (!actual.SequenceEqual(expected))
            {
                diagnostic = "Equipped utility does not reconcile with the player total and fixed stats.";
                return false;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
        {
            diagnostic = "The player advisor baseline has invalid family-relevant utility arithmetic.";
            return false;
        }

        diagnostic = baseline.Diagnostic;
        return true;
    }

    public static PlayerAdvisorBaseline Failure(
        PlayerAdvisorBaselineStatus status,
        string diagnostic,
        CharacterEquipmentSnapshot? snapshot = null,
        PlayerAdvisorCaptureHeader? header = null) =>
        new(
            status,
            header?.Character ?? snapshot?.Identity.Scope,
            header?.ClassJobId ?? snapshot?.Identity.ActiveClassJobId,
            header?.Level,
            header?.EffectiveLevel,
            header?.IsLevelSynced,
            new Dictionary<EquipmentStatSemantic, int>(),
            new Dictionary<EquipmentStatSemantic, int>(),
            [],
            snapshot,
            diagnostic);

    private static bool DefinitionMatchesPosition(
        EquipmentItemDefinition definition,
        EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => definition.Slot == EquipmentSlot.MainHand,
        EquipmentLoadoutPosition.OffHand => definition.Slot == EquipmentSlot.OffHand,
        EquipmentLoadoutPosition.Head => definition.Slot == EquipmentSlot.Head,
        EquipmentLoadoutPosition.Body => definition.Slot == EquipmentSlot.Body,
        EquipmentLoadoutPosition.Hands => definition.Slot == EquipmentSlot.Hands,
        EquipmentLoadoutPosition.Legs => definition.Slot == EquipmentSlot.Legs,
        EquipmentLoadoutPosition.Feet => definition.Slot == EquipmentSlot.Feet,
        EquipmentLoadoutPosition.Ears => definition.Slot == EquipmentSlot.Ears,
        EquipmentLoadoutPosition.Neck => definition.Slot == EquipmentSlot.Neck,
        EquipmentLoadoutPosition.Wrists => definition.Slot == EquipmentSlot.Wrists,
        EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing => definition.Slot == EquipmentSlot.Ring,
        _ => false,
    };

    private static IReadOnlyDictionary<EquipmentStatSemantic, int> EmptyStats() =>
        new Dictionary<EquipmentStatSemantic, int>();

    private static bool ComponentIsComplete(CharacterEquipmentSnapshot snapshot, string component) =>
        snapshot.Diagnostics.Components.Any(value =>
            string.Equals(value.Component, component, StringComparison.Ordinal) &&
            value.Status == SnapshotComponentStatus.Complete);

    private static PlayerAdvisorBaseline Result(
        PlayerAdvisorBaselineStatus status,
        CharacterEquipmentSnapshot snapshot,
        PlayerAdvisorCaptureHeader header,
        IReadOnlyDictionary<EquipmentStatSemantic, int> totalStats,
        IReadOnlyDictionary<EquipmentStatSemantic, int> fixedStats,
        IReadOnlyList<PlayerAdvisorEquippedSlot> slots,
        string diagnostic,
        PlayerAdvisorTrustedCapture? trustedCapture = null)
    {
        var result = new PlayerAdvisorBaseline(
            status,
            header.Character,
            header.ClassJobId,
            header.Level,
            header.EffectiveLevel,
            header.IsLevelSynced,
            totalStats,
            fixedStats,
            slots,
            snapshot,
            diagnostic);
        if (trustedCapture is null)
            return result;
        return result with
        {
            CaptureProvenance = new(
                trustedCapture.CaptureId,
                trustedCapture.CompletedAtUtc,
                snapshot.GenerationId,
                snapshot.Identity.CapturedAt,
                header.CurrentWorldId),
        };
    }
}
