using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter;

internal sealed record SavedGearsetItemStatCapture(
    EquipmentLoadoutPosition Position,
    EquipmentInstanceFingerprint Fingerprint,
    IReadOnlyDictionary<EquipmentStatSemantic, int> Contributions);

internal static class SavedGearsetAdvisorBaselineAssembler
{
    private const int BaseCraftingPoints = 180;
    private const int BaseGatheringPoints = 400;

    public static PlayerAdvisorBaseline Assemble(
        CharacterEquipmentSnapshot snapshot,
        OutfitterTarget target,
        SavedGearsetTargetResolution resolution,
        IAdvisorStatFamily? family,
        IReadOnlyList<SavedGearsetItemStatCapture> captures,
        PlayerAdvisorTrustedCapture? trustedCapture = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(captures);

        var fingerprint = resolution.Fingerprint;
        var job = fingerprint is null
            ? null
            : snapshot.Jobs.SingleOrDefault(value => value.ClassJobId == fingerprint.ClassJobId);
        if (job is null || fingerprint is null)
            return Failure(PlayerAdvisorBaselineStatus.Incomplete, snapshot, target, "Saved-gearset target identity is incomplete.");
        if (family is null || !family.SupportedClassJobIds.Contains(job.ClassJobId))
            return Failure(PlayerAdvisorBaselineStatus.Unsupported, snapshot, target, AdvisorStatFamilies.UnsupportedDiagnostic(job.ClassJobId));
        if (family is PhysicalRangedAdvisorStatFamily)
            return Failure(PlayerAdvisorBaselineStatus.Unsupported, snapshot, target,
                "Inactive combat baselines remain unsupported until level/job base-stat derivation is proven.");
        if (resolution.Status != SavedGearsetTargetResolutionStatus.Complete ||
            resolution.Slots.Count != PlayerAdvisorEquippedSlotMap.All.Count)
            return Failure(PlayerAdvisorBaselineStatus.Incomplete, snapshot, target, resolution.Diagnostic);

        var fixedStats = FixedStats(family);
        if (fixedStats is null)
            return Failure(PlayerAdvisorBaselineStatus.Unsupported, snapshot, target,
                "This advisor family has no proven unbuffed inactive-job fixed-stat baseline.");

        var captureGroups = captures.GroupBy(value => value.Position).ToDictionary(group => group.Key, group => group.ToArray());
        var slots = new List<PlayerAdvisorEquippedSlot>(PlayerAdvisorEquippedSlotMap.All.Count);
        var equippedTotals = family.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0);
        foreach (var canonical in PlayerAdvisorEquippedSlotMap.All)
        {
            var resolved = resolution.Slots.Single(value => value.Position == canonical.Position);
            if (resolved.IsEmpty)
            {
                if (captureGroups.ContainsKey(canonical.Position))
                    return Failure(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, target,
                        $"Empty saved-gearset position '{canonical.PositionKey}' has contradictory item-stat evidence.");
                slots.Add(new(canonical.Position, canonical.PositionKey, null, null, null,
                    family.VectorFromSemantics(family.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0)), [], []));
                continue;
            }

            if (resolved.Instance is null || resolved.Definition is null || resolved.Reference is null ||
                !captureGroups.TryGetValue(canonical.Position, out var positionCaptures) || positionCaptures.Length != 1)
            {
                return Failure(PlayerAdvisorBaselineStatus.Incomplete, snapshot, target,
                    $"Saved-gearset position '{canonical.PositionKey}' requires one exact item-stat capture.");
            }
            var capture = positionCaptures[0];
            if (!EquipmentInstanceFingerprintComparer.Instance.Equals(capture.Fingerprint, resolved.Instance.Fingerprint) ||
                capture.Contributions.Keys.Order().SequenceEqual(family.RelevantSemantics.Order()) == false ||
                capture.Contributions.Values.Any(value => value < 0))
            {
                return Failure(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, target,
                    $"Saved-gearset position '{canonical.PositionKey}' stat evidence does not match its resolved instance.");
            }

            foreach (var semantic in family.RelevantSemantics)
                equippedTotals[semantic] = checked(equippedTotals[semantic] + capture.Contributions[semantic]);
            slots.Add(new(
                canonical.Position,
                canonical.PositionKey,
                resolved.Instance,
                resolved.Definition,
                resolved.Reference.IsHighQuality == true ? EquipmentQuality.High : EquipmentQuality.Normal,
                family.VectorFromSemantics(capture.Contributions),
                resolved.Reference.MateriaIds ?? [],
                resolved.Reference.MateriaGrades ?? []));
        }

        if (captureGroups.Keys.Any(position => resolution.Slots.All(slot => slot.Position != position)))
            return Failure(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, target, "Item-stat evidence contains a non-canonical saved-gearset position.");

        var totalStats = family.RelevantSemantics.ToDictionary(
            semantic => semantic,
            semantic => checked(fixedStats[semantic] + equippedTotals[semantic]));
        var result = new PlayerAdvisorBaseline(
            PlayerAdvisorBaselineStatus.Complete,
            fingerprint.Character,
            job.ClassJobId,
            checked((short)job.Level),
            checked((short)job.Level),
            false,
            totalStats,
            fixedStats,
            slots,
            snapshot,
            $"Unbuffed saved gearset '{fingerprint.GearsetName}' baseline is complete without changing jobs.",
            new(PlayerAdvisorBaselineTargetKind.SavedGearset, target.Key, fingerprint.Value, fingerprint));
        if (trustedCapture is null)
            return result;
        return result with
        {
            CaptureProvenance = new(
                trustedCapture.CaptureId,
                trustedCapture.CompletedAtUtc,
                snapshot.GenerationId,
                snapshot.Identity.CapturedAt,
                snapshot.Identity.CurrentWorldId ?? 0),
        };
    }

    private static IReadOnlyDictionary<EquipmentStatSemantic, int>? FixedStats(IAdvisorStatFamily family) => family switch
    {
        GathererAdvisorStatFamily => new Dictionary<EquipmentStatSemantic, int>
        {
            [EquipmentStatSemantic.Gathering] = 0,
            [EquipmentStatSemantic.Perception] = 0,
            [EquipmentStatSemantic.GatheringPoints] = BaseGatheringPoints,
        },
        CrafterAdvisorStatFamily => new Dictionary<EquipmentStatSemantic, int>
        {
            [EquipmentStatSemantic.Craftsmanship] = 0,
            [EquipmentStatSemantic.Control] = 0,
            [EquipmentStatSemantic.CraftingPoints] = BaseCraftingPoints,
        },
        _ => null,
    };

    private static PlayerAdvisorBaseline Failure(
        PlayerAdvisorBaselineStatus status,
        CharacterEquipmentSnapshot snapshot,
        OutfitterTarget target,
        string diagnostic) =>
        new(
            status,
            snapshot.Identity.Scope,
            target.Job?.ClassJobId,
            target.Job is null ? null : checked((short)target.Job.Level),
            target.Job is null ? null : checked((short)target.Job.Level),
            false,
            new Dictionary<EquipmentStatSemantic, int>(),
            new Dictionary<EquipmentStatSemantic, int>(),
            [],
            snapshot,
            diagnostic,
            target.Gearset is null
                ? null
                : new(PlayerAdvisorBaselineTargetKind.SavedGearset, target.Key, string.Empty));
}
