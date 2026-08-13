using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter;

internal static class RetainerAdvisorBaselineAssembler
{
    public static PlayerAdvisorBaseline Assemble(
        CharacterEquipmentSnapshot ownerSnapshot,
        OutfitterTarget target,
        IAdvisorStatFamily family,
        Func<string, IReadOnlyList<EquipmentItemDefinition>> findDefinitionsByExactName)
    {
        ArgumentNullException.ThrowIfNull(ownerSnapshot);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(findDefinitionsByExactName);

        if (target is not
            {
                Kind: OutfitterTargetKind.Retainer,
                RetainerMetadata: { } metadata,
                RetainerEquipmentEvidence: { Status: RenderedRetainerEquipmentEvidenceStatus.Complete } evidence,
                RetainerObjective: { IsDefinitionComplete: true },
            })
        {
            return Failure(ownerSnapshot, target, "A complete rendered retainer identity, worn-equipment scan, and venture objective are required.");
        }
        if (ownerSnapshot.Identity is not
            {
                Status: SnapshotComponentStatus.Complete,
                IsLoggedIn: true,
                Scope: { } owner,
            } || owner.LocalContentId != metadata.OwnerContentId ||
            !Same(owner.Name, evidence.OwnerCharacterName) ||
            !Same(metadata.RetainerName, evidence.RetainerName) ||
            metadata.ClassJobId != evidence.ClassJobId || metadata.Level != evidence.Level ||
            !string.Equals(target.Key, evidence.TargetKey, StringComparison.Ordinal))
        {
            return Failure(ownerSnapshot, target, "The rendered retainer evidence does not belong to the active owner and selected retainer identity.");
        }
        if (!family.SupportedClassJobIds.Contains(metadata.ClassJobId) || metadata.Level is < 1 or > 100)
            return Failure(ownerSnapshot, target, "The retainer job, level, and procurement profile are incompatible.", PlayerAdvisorBaselineStatus.Unsupported);

        var canonical = PlayerAdvisorEquippedSlotMap.All.ToDictionary(value => value.PositionKey, StringComparer.Ordinal);
        var observations = evidence.Equipment
            .Where(value => canonical.ContainsKey(value.PositionKey))
            .GroupBy(value => value.PositionKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (canonical.Keys.Any(key => !observations.TryGetValue(key, out var values) || values.Length != 1))
            return Failure(ownerSnapshot, target, "All twelve canonical retainer worn slots require one rendered observation.");

        var definitions = ownerSnapshot.Definitions.ToDictionary(value => value.Key, value => value.Value);
        var instances = ownerSnapshot.Instances.ToList();
        var slots = new List<PlayerAdvisorEquippedSlot>(canonical.Count);
        var totals = family.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0);
        foreach (var position in PlayerAdvisorEquippedSlotMap.All)
        {
            var observation = observations[position.PositionKey][0];
            if (observation is { Status: RenderedEquipmentSlotObservationStatus.Empty, Item: null })
            {
                slots.Add(new(
                    position.Position,
                    position.PositionKey,
                    null,
                    null,
                    null,
                    EquipmentSolverUtilityVector.Empty,
                    [],
                    []));
                continue;
            }
            if (observation.Item is not { Status: RenderedItemDetailStatus.Complete, Name: { } itemName,
                    Quality: { } renderedQuality, ItemLevel: { } itemLevel, EquipLevel: { } equipLevel })
                return Failure(ownerSnapshot, target, $"Retainer slot '{position.PositionKey}' has no complete rendered item identity.");

            var candidates = findDefinitionsByExactName(itemName)
                .Where(definition => definition.ItemLevel == itemLevel && definition.EquipLevel == equipLevel)
                .Where(definition => definition.EligibleClassJobIds.Contains(metadata.ClassJobId))
                .Where(definition => MatchesPosition(definition, position.Position))
                .ToArray();
            if (candidates.Length != 1)
                return Failure(ownerSnapshot, target,
                    $"Rendered item '{itemName}' in '{position.PositionKey}' resolved to {candidates.Length:N0} exact static definitions.");

            var definition = candidates[0];
            var quality = renderedQuality == RenderedItemQuality.High ? EquipmentQuality.High : EquipmentQuality.Normal;
            var semanticValues = family.RelevantSemantics.ToDictionary(
                semantic => semantic,
                semantic => RenderedSemanticValue(observation.Item, definition, semantic));
            var utility = family is RetainerAdvisorStatFamily retainerFamily
                ? retainerFamily.VectorFromRenderedSlot(position.Position, semanticValues)
                : family.VectorFromSemantics(semanticValues);
            foreach (var semantic in family.RelevantSemantics)
                totals[semantic] = checked(totals[semantic] + semanticValues[semantic]);

            var fingerprint = new EquipmentInstanceFingerprint(
                owner,
                $"RetainerWorn:{metadata.RetainerId}",
                position.EquippedIndex,
                definition.ItemId,
                quality == EquipmentQuality.High,
                1,
                0,
                0,
                null,
                [],
                null,
                [],
                []);
            var instance = new EquipmentInstanceSnapshot(fingerprint, evidence.CapturedAtUtc, true);
            instances.Add(instance);
            definitions[definition.ItemId] = definition;
            slots.Add(new(position.Position, position.PositionKey, instance, definition, quality, utility, [], []));
        }

        if (family is RetainerAdvisorStatFamily { MainHandCountsTwice: true } &&
            slots.Single(value => value.Position == EquipmentLoadoutPosition.MainHand).Definition is { } mainHand)
        {
            totals[EquipmentStatSemantic.ItemLevel] = checked(
                totals.GetValueOrDefault(EquipmentStatSemantic.ItemLevel) + (int)mainHand.ItemLevel);
        }

        var completedAt = evidence.CapturedAtUtc > ownerSnapshot.Identity.CapturedAt
            ? evidence.CapturedAtUtc
            : ownerSnapshot.Identity.CapturedAt;
        var snapshot = ownerSnapshot with
        {
            GenerationId = Guid.NewGuid(),
            Instances = instances,
            Definitions = definitions,
        };
        var fingerprintValue = AuthorityFingerprint(target, evidence);
        return new(
            PlayerAdvisorBaselineStatus.Complete,
            owner,
            metadata.ClassJobId,
            checked((short)metadata.Level),
            checked((short)metadata.Level),
            false,
            totals,
            family.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0),
            slots,
            snapshot,
            $"Rendered {family.CoverageJobLabel} worn-equipment baseline is complete.",
            new(PlayerAdvisorBaselineTargetKind.Retainer, target.Key, fingerprintValue))
        {
            CaptureProvenance = new(
                Guid.NewGuid(),
                completedAt,
                snapshot.GenerationId,
                ownerSnapshot.Identity.CapturedAt,
                ownerSnapshot.Identity.CurrentWorldId ?? owner.HomeWorldId),
        };
    }

    private static int RenderedSemanticValue(
        RenderedItemDetailObservation item,
        EquipmentItemDefinition definition,
        EquipmentStatSemantic semantic) => semantic switch
    {
        EquipmentStatSemantic.ItemLevel => checked((int)definition.ItemLevel),
        EquipmentStatSemantic.Gathering => Stat(item, "Gathering"),
        EquipmentStatSemantic.Perception => Stat(item, "Perception"),
        _ => 0,
    };

    private static int Stat(RenderedItemDetailObservation item, string name) =>
        checked(item.Stats.GetValueOrDefault(name) + item.MateriaStats.GetValueOrDefault(name));

    private static string AuthorityFingerprint(OutfitterTarget target, RenderedRetainerEquipmentEvidence evidence)
    {
        var text = new StringBuilder()
            .Append(target.Key).Append('|')
            .Append(evidence.OwnerCharacterName).Append('|')
            .Append(evidence.OwnerHomeWorld).Append('|')
            .Append(evidence.RetainerName).Append('|')
            .Append(evidence.ClassJobId).Append('|')
            .Append(evidence.Level).Append('|')
            .Append(evidence.CapturedAtUtc.ToUnixTimeMilliseconds()).Append('|')
            .Append(target.RetainerObjective?.EvidenceGenerationId);
        foreach (var slot in evidence.Equipment.OrderBy(value => value.PositionKey, StringComparer.Ordinal))
            text.Append('|').Append(slot.PositionKey).Append(':').Append(slot.Item?.Name).Append(':')
                .Append(slot.Item?.Quality).Append(':').Append(slot.Item?.ItemLevel);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesPosition(EquipmentItemDefinition definition, EquipmentLoadoutPosition position) => position switch
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

    private static PlayerAdvisorBaseline Failure(
        CharacterEquipmentSnapshot snapshot,
        OutfitterTarget target,
        string diagnostic,
        PlayerAdvisorBaselineStatus status = PlayerAdvisorBaselineStatus.Incomplete) =>
        new(
            status,
            snapshot.Identity.Scope,
            target.RetainerMetadata?.ClassJobId,
            target.RetainerMetadata is { Level: > 0 } metadata ? checked((short)metadata.Level) : null,
            target.RetainerMetadata is { Level: > 0 } effective ? checked((short)effective.Level) : null,
            false,
            new Dictionary<EquipmentStatSemantic, int>(),
            new Dictionary<EquipmentStatSemantic, int>(),
            [],
            snapshot,
            diagnostic,
            new(PlayerAdvisorBaselineTargetKind.Retainer, target.Key, string.Empty));
}
