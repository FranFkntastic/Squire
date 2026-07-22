using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter;

public enum SavedGearsetTargetResolutionStatus
{
    Complete,
    Incomplete,
    Inconsistent,
    Unsupported,
}

public sealed record SavedGearsetSlotFingerprint(
    EquipmentLoadoutPosition Position,
    uint? ItemId,
    EquipmentQuality? Quality,
    IReadOnlyList<uint> MateriaIds,
    IReadOnlyList<byte> MateriaGrades);

public sealed record SavedGearsetTargetFingerprint(
    CharacterScope Character,
    int GearsetId,
    string GearsetName,
    uint ClassJobId,
    uint JobLevel,
    IReadOnlyList<SavedGearsetSlotFingerprint> Slots,
    string Value);

public sealed record SavedGearsetSlotResolution(
    EquipmentLoadoutPosition Position,
    GearsetItemReference? Reference,
    EquipmentInstanceSnapshot? Instance,
    EquipmentItemDefinition? Definition,
    string? Diagnostic)
{
    public bool IsEmpty => Reference is null;
    public bool IsResolved => IsEmpty || Instance is not null && Definition is not null && Diagnostic is null;
}

public sealed record SavedGearsetTargetResolution(
    SavedGearsetTargetResolutionStatus Status,
    SavedGearsetTargetFingerprint? Fingerprint,
    IReadOnlyList<SavedGearsetSlotResolution> Slots,
    string Diagnostic);

public static class SavedGearsetTargetResolver
{
    private static readonly EquipmentLoadoutPosition[] CanonicalPositions =
    [
        EquipmentLoadoutPosition.MainHand,
        EquipmentLoadoutPosition.OffHand,
        EquipmentLoadoutPosition.Head,
        EquipmentLoadoutPosition.Body,
        EquipmentLoadoutPosition.Hands,
        EquipmentLoadoutPosition.Legs,
        EquipmentLoadoutPosition.Feet,
        EquipmentLoadoutPosition.Ears,
        EquipmentLoadoutPosition.Neck,
        EquipmentLoadoutPosition.Wrists,
        EquipmentLoadoutPosition.LeftRing,
        EquipmentLoadoutPosition.RightRing,
    ];

    public static SavedGearsetTargetResolution Resolve(CharacterEquipmentSnapshot snapshot, OutfitterTarget target)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);

        if (target.Kind != OutfitterTargetKind.Gearset || target.Gearset is null || target.Job is null)
            return Failure(SavedGearsetTargetResolutionStatus.Unsupported, "Saved-gearset resolution requires one gearset target with a known job.");
        if (snapshot.Identity is not { Status: SnapshotComponentStatus.Complete, Scope: { } character, IsLoggedIn: true })
            return Failure(SavedGearsetTargetResolutionStatus.Incomplete, "Current-character identity is unavailable.");

        var matchingGearsets = snapshot.Gearsets
            .Where(value => value.GearsetId == target.Gearset.GearsetId)
            .ToArray();
        if (matchingGearsets.Length != 1)
            return Failure(SavedGearsetTargetResolutionStatus.Incomplete, "Selected saved gearset no longer has one current-character identity.");
        var gearset = matchingGearsets[0];
        var matchingJobs = snapshot.Jobs
            .Where(value => value.ClassJobId == gearset.ClassJobId)
            .ToArray();
        if (matchingJobs.Length != 1)
            return Failure(SavedGearsetTargetResolutionStatus.Incomplete, "Saved gearset no longer maps to one current-character job.");
        var job = matchingJobs[0];
        if (!gearset.IsValid || gearset.GearsetId < 0 || string.IsNullOrWhiteSpace(gearset.Name))
            return Failure(SavedGearsetTargetResolutionStatus.Incomplete, "Saved gearset identity is incomplete.");
        if (gearset.ClassJobId == 0 || gearset.ClassJobId != job.ClassJobId || job.Level is < 1 or > 100 || job.IsUnlocked != true)
            return Failure(SavedGearsetTargetResolutionStatus.Inconsistent, "Saved gearset job identity does not match one unlocked current-character job and level.");
        if (!ComponentIsComplete(snapshot, "gearsets") ||
            !ComponentIsComplete(snapshot, "equipped") ||
            !ComponentIsComplete(snapshot, "armoury"))
        {
            return Failure(SavedGearsetTargetResolutionStatus.Incomplete, "Saved-gearset resolution requires complete gearset, equipped, and armoury evidence.");
        }

        var canonicalReferences = new List<(EquipmentLoadoutPosition Position, GearsetItemReference Reference)>();
        foreach (var reference in gearset.Items.Where(value => value.ItemId != 0))
        {
            var position = reference.Position ?? InferLegacyPosition(reference.Slot);
            if (position is null)
            {
                if (reference.Slot == EquipmentSlot.SoulCrystal)
                    continue;
                return Failure(SavedGearsetTargetResolutionStatus.Incomplete,
                    $"Saved gearset item {reference.ItemId} has no canonical loadout position.");
            }
            if (!SlotMatchesPosition(reference.Slot, position.Value))
                return Failure(SavedGearsetTargetResolutionStatus.Inconsistent,
                    $"Saved gearset item {reference.ItemId} has contradictory slot and position evidence.");
            canonicalReferences.Add((position.Value, reference));
        }
        if (canonicalReferences.GroupBy(value => value.Position).Any(group => group.Count() != 1))
            return Failure(SavedGearsetTargetResolutionStatus.Inconsistent, "Saved gearset contains duplicate canonical slot assignments.");

        var byPosition = canonicalReferences.ToDictionary(value => value.Position, value => value.Reference);
        var fingerprintSlots = CanonicalPositions.Select(position =>
        {
            if (!byPosition.TryGetValue(position, out var reference))
                return new SavedGearsetSlotFingerprint(position, null, null, [], []);
            return new SavedGearsetSlotFingerprint(
                position,
                reference.ItemId,
                reference.IsHighQuality switch
                {
                    true => EquipmentQuality.High,
                    false => EquipmentQuality.Normal,
                    null => null,
                },
                reference.MateriaIds ?? [],
                reference.MateriaGrades ?? []);
        }).ToArray();
        var fingerprint = CreateFingerprint(character, gearset, job.Level, fingerprintSlots);

        var slots = new List<SavedGearsetSlotResolution>(CanonicalPositions.Length);
        var usedInstances = new HashSet<EquipmentInstanceFingerprint>(EquipmentInstanceFingerprintComparer.Instance);
        foreach (var position in CanonicalPositions)
        {
            if (!byPosition.TryGetValue(position, out var reference))
            {
                slots.Add(new(position, null, null, null, null));
                continue;
            }

            var referenceDiagnostic = ValidateReference(reference);
            if (referenceDiagnostic is not null)
            {
                slots.Add(new(position, reference, null, null, referenceDiagnostic));
                continue;
            }

            var candidates = snapshot.Instances
                .Where(instance => InstanceMatches(instance, reference))
                .ToArray();
            if (candidates.Length > 1 && reference.Stains is not null)
            {
                var appearanceMatches = candidates.Where(instance =>
                    instance.Fingerprint.GlamourId == reference.GlamourId &&
                    instance.Fingerprint.Stains.SequenceEqual(reference.Stains)).ToArray();
                if (appearanceMatches.Length == 1)
                    candidates = appearanceMatches;
            }
            if (candidates.Length != 1)
            {
                var reason = candidates.Length == 0 ? "is not present in equipped or armoury inventory" : "matches multiple owned instances";
                slots.Add(new(position, reference, null, null,
                    $"{PositionKey(position)} item {reference.ItemId} {reason}."));
                continue;
            }

            var instance = candidates[0];
            if (!usedInstances.Add(instance.Fingerprint))
            {
                slots.Add(new(position, reference, null, null,
                    $"{PositionKey(position)} resolves to an instance already assigned to another saved-gearset slot."));
                continue;
            }
            if (!snapshot.Definitions.TryGetValue(reference.ItemId, out var definition))
            {
                slots.Add(new(position, reference, instance, null,
                    $"{PositionKey(position)} item {reference.ItemId} has no equipment definition."));
                continue;
            }
            if (!DefinitionMatchesPosition(definition, position) ||
                !definition.EligibleClassJobIds.Contains(job.ClassJobId) ||
                definition.EquipLevel > job.Level)
            {
                slots.Add(new(position, reference, instance, definition,
                    $"{PositionKey(position)} item {reference.ItemId} is not valid for {job.Abbreviation} at level {job.Level}."));
                continue;
            }
            slots.Add(new(position, reference, instance, definition, null));
        }

        var gaps = slots.Where(slot => !slot.IsResolved).ToArray();
        return new(
            gaps.Length == 0 ? SavedGearsetTargetResolutionStatus.Complete : SavedGearsetTargetResolutionStatus.Incomplete,
            fingerprint,
            slots,
            gaps.Length == 0
                ? $"Saved gearset '{gearset.Name}' resolved all twelve canonical positions without changing jobs."
                : $"Saved gearset '{gearset.Name}' has {gaps.Length} unresolved canonical slot assignment(s): {string.Join(" ", gaps.Select(gap => gap.Diagnostic))}");
    }

    private static string? ValidateReference(GearsetItemReference reference)
    {
        if (reference.IsMissing)
            return $"Saved gearset item {reference.ItemId} is marked missing by the game.";
        if (reference.IsHighQuality is null)
            return $"Saved gearset item {reference.ItemId} has no exact quality identity.";
        if (reference.MateriaIds is null || reference.MateriaGrades is null ||
            reference.MateriaIds.Count != reference.MateriaGrades.Count)
            return $"Saved gearset item {reference.ItemId} has incomplete materia identity.";
        return null;
    }

    private static bool InstanceMatches(EquipmentInstanceSnapshot instance, GearsetItemReference reference)
    {
        var fingerprint = instance.Fingerprint;
        return IsGearsetContainer(fingerprint.Container) &&
            fingerprint.ItemId == reference.ItemId &&
            fingerprint.IsHighQuality == reference.IsHighQuality &&
            fingerprint.MateriaIds.SequenceEqual(reference.MateriaIds!) &&
            (fingerprint.MateriaGrades ?? []).SequenceEqual(reference.MateriaGrades!);
    }

    private static bool IsGearsetContainer(string container) =>
        string.Equals(container, "EquippedItems", StringComparison.Ordinal) ||
        container.StartsWith("Armory", StringComparison.Ordinal);

    private static EquipmentLoadoutPosition? InferLegacyPosition(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.MainHand => EquipmentLoadoutPosition.MainHand,
        EquipmentSlot.OffHand => EquipmentLoadoutPosition.OffHand,
        EquipmentSlot.Head => EquipmentLoadoutPosition.Head,
        EquipmentSlot.Body => EquipmentLoadoutPosition.Body,
        EquipmentSlot.Hands => EquipmentLoadoutPosition.Hands,
        EquipmentSlot.Legs => EquipmentLoadoutPosition.Legs,
        EquipmentSlot.Feet => EquipmentLoadoutPosition.Feet,
        EquipmentSlot.Ears => EquipmentLoadoutPosition.Ears,
        EquipmentSlot.Neck => EquipmentLoadoutPosition.Neck,
        EquipmentSlot.Wrists => EquipmentLoadoutPosition.Wrists,
        _ => null,
    };

    private static bool SlotMatchesPosition(EquipmentSlot slot, EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => slot == EquipmentSlot.MainHand,
        EquipmentLoadoutPosition.OffHand => slot == EquipmentSlot.OffHand,
        EquipmentLoadoutPosition.Head => slot == EquipmentSlot.Head,
        EquipmentLoadoutPosition.Body => slot == EquipmentSlot.Body,
        EquipmentLoadoutPosition.Hands => slot == EquipmentSlot.Hands,
        EquipmentLoadoutPosition.Legs => slot == EquipmentSlot.Legs,
        EquipmentLoadoutPosition.Feet => slot == EquipmentSlot.Feet,
        EquipmentLoadoutPosition.Ears => slot == EquipmentSlot.Ears,
        EquipmentLoadoutPosition.Neck => slot == EquipmentSlot.Neck,
        EquipmentLoadoutPosition.Wrists => slot == EquipmentSlot.Wrists,
        EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing => slot == EquipmentSlot.Ring,
        _ => false,
    };

    private static bool DefinitionMatchesPosition(EquipmentItemDefinition definition, EquipmentLoadoutPosition position) =>
        SlotMatchesPosition(definition.Slot, position) && position switch
        {
            EquipmentLoadoutPosition.LeftRing => definition.FitsLeftRing,
            EquipmentLoadoutPosition.RightRing => definition.FitsRightRing,
            _ => true,
        };

    private static SavedGearsetTargetFingerprint CreateFingerprint(
        CharacterScope character,
        GearsetSnapshot gearset,
        uint jobLevel,
        IReadOnlyList<SavedGearsetSlotFingerprint> slots)
    {
        var canonical = string.Join('|',
            character.LocalContentId,
            character.HomeWorldId,
            gearset.GearsetId,
            gearset.Name.Trim(),
            gearset.ClassJobId,
            jobLevel,
            string.Join(';', slots.Select(slot => string.Join(',',
                slot.Position,
                slot.ItemId?.ToString() ?? "empty",
                slot.Quality?.ToString() ?? "unknown",
                string.Join('.', slot.MateriaIds),
                string.Join('.', slot.MateriaGrades)))));
        var value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(character, gearset.GearsetId, gearset.Name.Trim(), gearset.ClassJobId, jobLevel, slots, value);
    }

    private static string PositionKey(EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => "Main hand",
        EquipmentLoadoutPosition.OffHand => "Off hand",
        EquipmentLoadoutPosition.LeftRing => "Left ring",
        EquipmentLoadoutPosition.RightRing => "Right ring",
        _ => position.ToString(),
    };

    private static bool ComponentIsComplete(CharacterEquipmentSnapshot snapshot, string component) =>
        snapshot.Diagnostics.Components.Any(value =>
            string.Equals(value.Component, component, StringComparison.Ordinal) &&
            value.Status == SnapshotComponentStatus.Complete);

    private static SavedGearsetTargetResolution Failure(SavedGearsetTargetResolutionStatus status, string diagnostic) =>
        new(status, null, [], diagnostic);
}
