using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Portfolio;

public sealed record PortfolioEquipmentSourceEvidence(
    PortfolioAllocationKey Allocation,
    PortfolioEquipmentMoveOwner Owner,
    PortfolioEquipmentSlotAddress Address,
    PortfolioExactItem Item,
    string EvidenceGeneration);

public sealed record PortfolioEquipmentDestinationEvidence(
    PortfolioEquipmentMoveTarget Target,
    EquipmentLoadoutPosition Position,
    PortfolioEquipmentSlotAddress Address,
    PortfolioExactItem? EquippedItem,
    string EvidenceGeneration);

public sealed record PortfolioOrderedEquipmentSwap(
    int Order,
    PortfolioEquipmentStep Step,
    PortfolioEquipmentMoveTarget Target,
    PortfolioEquipmentSlotAddress SourceAddress,
    PortfolioEquipmentSlotAddress DestinationAddress,
    string? SourceEvidenceGeneration,
    string DestinationEvidenceGeneration,
    string? RequiresFreshSourceEvidenceAfterStepId);

/// <summary>
/// Converts a selected global portfolio into an ordered swap checklist. A hand-me-down is sourced
/// from the upstream replacement's verified source slot because that is where the displaced worn
/// item lands; its old worn address and instance identity are never reused downstream.
/// </summary>
public static class PortfolioHandMeDownExecutionPlanner
{
    public static IReadOnlyList<PortfolioOrderedEquipmentSwap> Build(
        PortfolioAuthorityEnvelope authority,
        OutfitterPortfolioPlan plan,
        IReadOnlyList<PortfolioEquipmentSourceEvidence> baseSources,
        IReadOnlyList<PortfolioEquipmentDestinationEvidence> destinations)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(baseSources);
        ArgumentNullException.ThrowIfNull(destinations);
        if (!authority.HasValidFingerprint())
            throw new InvalidOperationException("Portfolio authority fingerprint is invalid.");
        var current = PortfolioAuthorityEnvelopeFactory.Create(authority.Lineage, plan);
        if (current.Fingerprint != authority.Fingerprint)
            throw new InvalidOperationException("The portfolio plan no longer matches its persisted authority.");

        var selected = plan.SelectedCandidates.ToDictionary(candidate => candidate.TargetKey, StringComparer.Ordinal);
        var targetRank = plan.OrderedTargets
            .OrderByDescending(target => target.Priority)
            .ThenBy(target => target.ProgressionHorizon)
            .ThenBy(target => target.TargetKey, StringComparer.Ordinal)
            .Select((target, index) => (target.TargetKey, Rank: index))
            .ToDictionary(value => value.TargetKey, value => value.Rank, StringComparer.Ordinal);
        var sourceByAllocation = baseSources.ToDictionary(source => source.Allocation);
        var destinationBySlot = destinations.ToDictionary(destination =>
            (destination.Target.TargetKey, destination.Position));
        var releases = authority.HandMeDownChain.ToDictionary(value =>
            (value.DownstreamTargetKey, value.DownstreamCandidateKey, value.Allocation));
        var emittedByRelease = new Dictionary<(string TargetKey, EquipmentLoadoutPosition Position), PortfolioOrderedEquipmentSwap>();
        var output = new List<PortfolioOrderedEquipmentSwap>();

        foreach (var target in plan.OrderedTargets
                     .OrderBy(target => targetRank[target.TargetKey]))
        {
            if (!selected.TryGetValue(target.TargetKey, out var candidate))
                continue;
            foreach (var replacement in candidate.SelectedReplacements.OrderBy(value => value.Position))
            {
                if (!destinationBySlot.TryGetValue((candidate.TargetKey, replacement.Position), out var destination))
                    throw new InvalidOperationException("A selected replacement has no exact destination evidence.");
                if (SameItemAndQuality(replacement.Item, destination.EquippedItem))
                    continue;
                var demand = FindReplacementDemand(candidate, replacement);

                PortfolioEquipmentMoveOwner sourceOwner;
                PortfolioEquipmentSlotAddress sourceAddress;
                PortfolioExactItem sourceItem;
                string? sourceGeneration;
                string? dependency;
                if (demand.Allocation.SourceKind == PortfolioAllocationSourceKind.HandMeDown)
                {
                    if (!releases.TryGetValue((candidate.TargetKey, candidate.CandidateKey, demand.Allocation), out var chain) ||
                        !emittedByRelease.TryGetValue((chain.UpstreamTargetKey, FindReleasePosition(selected, chain)), out var upstream))
                        throw new InvalidOperationException("A hand-me-down cannot be ordered before its selected upstream release.");
                    if (upstream.Step.ExpectedBefore != chain.ReleasedItem)
                        throw new InvalidOperationException("The upstream destination no longer contains the exact released item and quality.");

                    sourceOwner = upstream.Target.Owner;
                    sourceAddress = upstream.SourceAddress;
                    sourceItem = Rebind(chain.ReleasedItem, sourceOwner, sourceAddress);
                    sourceGeneration = null;
                    dependency = upstream.Step.StepId;
                }
                else
                {
                    if (!sourceByAllocation.TryGetValue(demand.Allocation, out var source))
                        throw new InvalidOperationException("A selected owned or listing allocation has no exact source evidence.");
                    if (source.Item.ItemId != replacement.Item.ItemId || source.Item.IsHighQuality != replacement.Item.IsHighQuality)
                        throw new InvalidOperationException("Source evidence does not match the selected replacement item and quality.");
                    sourceOwner = source.Owner;
                    sourceAddress = source.Address;
                    sourceItem = source.Item;
                    sourceGeneration = source.EvidenceGeneration;
                    dependency = null;
                }

                if (sourceOwner != destination.Target.Owner)
                    throw new InvalidOperationException("Equipment movement cannot cross owner authority.");
                var expectedAfter = Rebind(replacement.Item, destination.Target.Owner, destination.Address);
                var stepId = $"portfolio:{candidate.TargetKey}:{candidate.CandidateKey}:{replacement.Position}";
                var swap = new PortfolioOrderedEquipmentSwap(
                    output.Count,
                    new(stepId, candidate.TargetKey, replacement.Position, sourceItem, destination.EquippedItem, expectedAfter),
                    destination.Target,
                    sourceAddress,
                    destination.Address,
                    sourceGeneration,
                    destination.EvidenceGeneration,
                    dependency);
                output.Add(swap);
                emittedByRelease[(candidate.TargetKey, replacement.Position)] = swap;
            }
        }

        return output;
    }

    private static PortfolioAllocationDemand FindReplacementDemand(
        PortfolioCandidate candidate,
        PortfolioSelectedReplacement replacement)
    {
        var matches = candidate.Demands.Where(demand =>
                demand.Quantity == 1 &&
                demand.Allocation.ItemId == replacement.Item.ItemId &&
                demand.Allocation.IsHighQuality == replacement.Item.IsHighQuality &&
                (demand.Allocation.InstanceId is null ||
                 string.Equals(demand.Allocation.InstanceId, replacement.Item.InstanceId, StringComparison.Ordinal)))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException("Each selected replacement must resolve to one exact allocation demand.");
    }

    private static EquipmentLoadoutPosition FindReleasePosition(
        IReadOnlyDictionary<string, PortfolioCandidate> selected,
        PortfolioAuthorityHandMeDown chain)
    {
        if (!selected.TryGetValue(chain.UpstreamTargetKey, out var upstream))
            throw new InvalidOperationException("The hand-me-down upstream candidate is no longer selected.");
        return upstream.HandMeDowns.Single(release => release.ReleasedAllocation == chain.Allocation).Position;
    }

    private static bool SameItemAndQuality(PortfolioExactItem selected, PortfolioExactItem? equipped) =>
        equipped is not null && selected.ItemId == equipped.ItemId &&
        selected.IsHighQuality == equipped.IsHighQuality &&
        string.Equals(selected.InstanceId, equipped.InstanceId, StringComparison.Ordinal);

    private static PortfolioExactItem Rebind(
        PortfolioExactItem item,
        PortfolioEquipmentMoveOwner owner,
        PortfolioEquipmentSlotAddress address) =>
        new(
            item.ItemId,
            item.IsHighQuality,
            PortfolioEquipmentMove.ExactInstanceId(
                owner,
                address,
                new PortfolioEquipmentSlotItem(item.ItemId, item.IsHighQuality)));
}
