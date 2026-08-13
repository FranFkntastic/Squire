using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Portfolio;

public enum PortfolioProgressionHorizon
{
    Immediate = 0,
    NearTerm = 1,
    LongTerm = 2,
}

public enum PortfolioAllocationSourceKind
{
    OwnedInstance,
    MarketListing,
    GilVendor,
    Craft,
    HandMeDown,
}

public sealed record PortfolioTargetPriority(
    string TargetKey,
    string TargetLabel,
    int Priority,
    PortfolioProgressionHorizon ProgressionHorizon);

public sealed record PortfolioExactItem(
    uint ItemId,
    bool IsHighQuality,
    string InstanceId);

public sealed record PortfolioAllocationKey(
    PortfolioAllocationSourceKind SourceKind,
    string SourceKey,
    uint ItemId,
    bool IsHighQuality,
    string? InstanceId = null);

public sealed record PortfolioAllocationCapacity(
    PortfolioAllocationKey Allocation,
    uint Quantity);

public sealed record PortfolioAllocationDemand(
    PortfolioAllocationKey Allocation,
    uint Quantity);

public sealed record PortfolioSelectedReplacement(
    EquipmentLoadoutPosition Position,
    PortfolioExactItem Item);

/// <summary>
/// Evidence that selecting a specific upstream replacement releases the exact previously worn
/// instance. This is the only way a hand-me-down allocation enters the portfolio.
/// </summary>
public sealed record PortfolioHandMeDownEvidence(
    string EvidenceGeneration,
    EquipmentLoadoutPosition Position,
    PortfolioExactItem ReleasedItem,
    PortfolioExactItem SelectedReplacement,
    PortfolioAllocationKey ReleasedAllocation);

public sealed record PortfolioCandidate(
    string TargetKey,
    string CandidateKey,
    long UtilityGain,
    IReadOnlyList<PortfolioAllocationDemand> Demands,
    IReadOnlyList<PortfolioSelectedReplacement> SelectedReplacements,
    IReadOnlyList<PortfolioHandMeDownEvidence> HandMeDowns);

public sealed record PortfolioAllocationConsequence(
    string LosingTargetKey,
    string LosingCandidateKey,
    PortfolioAllocationKey ScarceAllocation,
    uint AvailableQuantity,
    uint RequiredQuantity,
    IReadOnlyList<string> HigherPriorityWinnerTargetKeys,
    string Reason);

public enum PortfolioTargetDispositionKind
{
    SelectedCandidate,
    TerminalNoUpgrade,
    TerminalAbstention,
    Incomplete,
}

public sealed record PortfolioTargetDisposition(
    string TargetKey,
    PortfolioTargetDispositionKind Kind,
    string? EvidenceGeneration,
    string Reason,
    string? SelectedCandidateKey = null)
{
    public bool BlocksAuthority => Kind == PortfolioTargetDispositionKind.Incomplete;
}

public sealed record OutfitterPortfolioPlan(
    IReadOnlyList<PortfolioTargetPriority> OrderedTargets,
    IReadOnlyList<PortfolioCandidate> SelectedCandidates,
    IReadOnlyList<PortfolioAllocationConsequence> AllocationConsequences,
    bool IsComplete = true,
    string? Diagnostic = null,
    long ExploredStateCount = 0,
    IReadOnlyList<PortfolioTargetDisposition>? TargetDispositions = null)
{
    public PortfolioCandidate? SelectionFor(string targetKey) =>
        SelectedCandidates.SingleOrDefault(candidate =>
            string.Equals(candidate.TargetKey, targetKey, StringComparison.Ordinal));

    public PortfolioTargetDisposition? DispositionFor(string targetKey) =>
        TargetDispositions?.SingleOrDefault(disposition =>
            string.Equals(disposition.TargetKey, targetKey, StringComparison.Ordinal));

    public bool HasExecutableTargetDispositions =>
        PortfolioTargetDispositionResolver.Validate(this, out _);
}

public static class PortfolioTargetDispositionResolver
{
    public static OutfitterPortfolioPlan Apply(
        OutfitterPortfolioPlan plan,
        IReadOnlyDictionary<string, (string EvidenceGeneration, string? TerminalReason, PortfolioTargetDispositionKind TerminalKind)> evaluated,
        IReadOnlyDictionary<string, string> incomplete)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(evaluated);
        ArgumentNullException.ThrowIfNull(incomplete);
        var dispositions = new List<PortfolioTargetDisposition>(plan.OrderedTargets.Count);
        foreach (var target in plan.OrderedTargets)
        {
            var selected = plan.SelectionFor(target.TargetKey);
            if (selected is not null)
            {
                if (!evaluated.TryGetValue(target.TargetKey, out var evidence) || string.IsNullOrWhiteSpace(evidence.EvidenceGeneration))
                {
                    dispositions.Add(new(target.TargetKey, PortfolioTargetDispositionKind.Incomplete, null,
                        "The selected candidate has no exact target evidence generation."));
                    continue;
                }
                dispositions.Add(new(
                    target.TargetKey,
                    PortfolioTargetDispositionKind.SelectedCandidate,
                    evidence.EvidenceGeneration,
                    $"Selected exact candidate {selected.CandidateKey}.",
                    selected.CandidateKey));
                continue;
            }

            if (incomplete.TryGetValue(target.TargetKey, out var incompleteReason))
            {
                dispositions.Add(new(
                    target.TargetKey,
                    PortfolioTargetDispositionKind.Incomplete,
                    null,
                    string.IsNullOrWhiteSpace(incompleteReason) ? "Target evaluation is incomplete." : incompleteReason));
                continue;
            }
            if (evaluated.TryGetValue(target.TargetKey, out var terminal) && !string.IsNullOrWhiteSpace(terminal.EvidenceGeneration))
            {
                var reason = string.IsNullOrWhiteSpace(terminal.TerminalReason)
                    ? plan.AllocationConsequences.FirstOrDefault(value => value.LosingTargetKey == target.TargetKey)?.Reason ??
                      "Exact evidence found no globally allocatable upgrade."
                    : terminal.TerminalReason;
                dispositions.Add(new(
                    target.TargetKey,
                    terminal.TerminalKind is PortfolioTargetDispositionKind.TerminalNoUpgrade or PortfolioTargetDispositionKind.TerminalAbstention
                        ? terminal.TerminalKind
                        : PortfolioTargetDispositionKind.TerminalNoUpgrade,
                    terminal.EvidenceGeneration,
                    reason));
                continue;
            }
            dispositions.Add(new(
                target.TargetKey,
                PortfolioTargetDispositionKind.Incomplete,
                null,
                "The included target has neither exact evaluation evidence nor an explicit terminal result."));
        }

        var complete = plan.IsComplete && dispositions.All(disposition => !disposition.BlocksAuthority);
        var diagnostic = complete
            ? plan.Diagnostic
            : dispositions.First(disposition => disposition.BlocksAuthority).Reason;
        return plan with { IsComplete = complete, Diagnostic = diagnostic, TargetDispositions = dispositions };
    }

    public static bool Validate(OutfitterPortfolioPlan plan, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var dispositions = plan.TargetDispositions;
        if (dispositions is null || dispositions.Count != plan.OrderedTargets.Count ||
            dispositions.Select(value => value.TargetKey).Distinct(StringComparer.Ordinal).Count() != dispositions.Count ||
            plan.OrderedTargets.Any(target => dispositions.All(value => !string.Equals(value.TargetKey, target.TargetKey, StringComparison.Ordinal))))
        {
            diagnostic = "Every included portfolio target requires exactly one disposition.";
            return false;
        }
        foreach (var disposition in dispositions)
        {
            var selected = plan.SelectionFor(disposition.TargetKey);
            if (string.IsNullOrWhiteSpace(disposition.Reason))
            {
                diagnostic = $"Portfolio target {disposition.TargetKey} has no disposition reason.";
                return false;
            }
            if (disposition.Kind == PortfolioTargetDispositionKind.Incomplete)
            {
                diagnostic = $"Portfolio target {disposition.TargetKey} is incomplete: {disposition.Reason}";
                return false;
            }
            if (string.IsNullOrWhiteSpace(disposition.EvidenceGeneration))
            {
                diagnostic = $"Portfolio target {disposition.TargetKey} has no exact disposition evidence generation.";
                return false;
            }
            if (disposition.Kind == PortfolioTargetDispositionKind.SelectedCandidate)
            {
                if (selected is null || !string.Equals(disposition.SelectedCandidateKey, selected.CandidateKey, StringComparison.Ordinal))
                {
                    diagnostic = $"Portfolio target {disposition.TargetKey} does not bind its exact selected candidate.";
                    return false;
                }
            }
            else if (selected is not null || disposition.SelectedCandidateKey is not null)
            {
                diagnostic = $"Portfolio target {disposition.TargetKey} has conflicting selected and terminal dispositions.";
                return false;
            }
        }
        diagnostic = string.Empty;
        return true;
    }
}

/// <summary>
/// Exhaustive deterministic planner. Portfolio target counts are intentionally small, so this
/// chooses correctness over a heuristic: the lexicographic objective honors priority, then
/// progression horizon, then per-target utility without ever over-allocating shared evidence.
/// </summary>
public static class OutfitterPortfolioPlanner
{
    public const int DefaultMaximumExploredStates = 50_000;

    public static OutfitterPortfolioPlan Plan(
        IReadOnlyList<PortfolioTargetPriority> targets,
        IReadOnlyList<PortfolioCandidate> candidates,
        IReadOnlyList<PortfolioAllocationCapacity> capacities,
        CancellationToken cancellationToken = default,
        int maximumExploredStates = DefaultMaximumExploredStates)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(capacities);
        if (maximumExploredStates <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumExploredStates));
        ValidateInputs(targets, candidates, capacities);

        var orderedTargets = targets
            .OrderByDescending(target => target.Priority)
            .ThenBy(target => target.ProgressionHorizon)
            .ThenBy(target => target.TargetKey, StringComparer.Ordinal)
            .ToArray();
        var byTarget = candidates
            .GroupBy(candidate => candidate.TargetKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(candidate => candidate.UtilityGain)
                    .ThenBy(candidate => candidate.CandidateKey, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        List<PortfolioCandidate>? best = null;
        var current = new List<PortfolioCandidate>();
        long explored = 0;
        var budgetExceeded = false;
        Search(0);
        if (budgetExceeded)
            return new(orderedTargets, [], [], false,
                $"Exact portfolio planning stopped after {explored:N0} states; narrow the target set or frontier before allocating anything.", explored);
        best ??= [];

        return new(
            orderedTargets,
            best.ToArray(),
            ExplainScarcity(orderedTargets, candidates, capacities, best),
            true,
            null,
            explored);

        void Search(int targetIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++explored > maximumExploredStates)
            {
                budgetExceeded = true;
                return;
            }
            if (targetIndex == orderedTargets.Length)
            {
                if (!IsFeasible(orderedTargets, current, capacities))
                    return;
                if (best is null || Compare(orderedTargets, current, best) > 0)
                    best = current.ToList();
                return;
            }

            var target = orderedTargets[targetIndex];
            if (byTarget.TryGetValue(target.TargetKey, out var choices))
            {
                var feasible = choices.Where(choice => IsFeasible(orderedTargets, current.Append(choice).ToArray(), capacities)).ToArray();
                var bestUtility = feasible.Length == 0 ? 0 : feasible.Max(choice => choice.UtilityGain);
                foreach (var choice in feasible.Where(choice => choice.UtilityGain == bestUtility))
                {
                    current.Add(choice);
                    Search(targetIndex + 1);
                    current.RemoveAt(current.Count - 1);
                    if (budgetExceeded)
                        return;
                }
                if (feasible.Length > 0)
                    return;
            }

            Search(targetIndex + 1);
        }
    }

    private static void ValidateInputs(
        IReadOnlyList<PortfolioTargetPriority> targets,
        IReadOnlyList<PortfolioCandidate> candidates,
        IReadOnlyList<PortfolioAllocationCapacity> capacities)
    {
        if (targets.Count == 0)
            throw new ArgumentException("A portfolio requires at least one target.", nameof(targets));
        if (targets.Any(target => string.IsNullOrWhiteSpace(target.TargetKey) ||
                                  string.IsNullOrWhiteSpace(target.TargetLabel) ||
                                  target.Priority < 0))
            throw new ArgumentException("Portfolio targets require stable keys, labels, and non-negative priorities.", nameof(targets));
        if (targets.Select(target => target.TargetKey).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new ArgumentException("Portfolio target keys must be unique.", nameof(targets));

        var targetKeys = targets.Select(target => target.TargetKey).ToHashSet(StringComparer.Ordinal);
        if (candidates.Any(candidate => !targetKeys.Contains(candidate.TargetKey) ||
                                        string.IsNullOrWhiteSpace(candidate.CandidateKey) ||
                                        candidate.UtilityGain <= 0))
            throw new ArgumentException("Candidates require a known target, stable key, and positive utility gain.", nameof(candidates));
        if (candidates.Select(candidate => (candidate.TargetKey, candidate.CandidateKey)).Distinct().Count() != candidates.Count)
            throw new ArgumentException("Candidate keys must be unique within each target.", nameof(candidates));

        foreach (var candidate in candidates)
        {
            ValidateDemands(candidate);
            ValidateHandMeDowns(candidate);
            if (candidate.SelectedReplacements.Any(replacement => !IsExact(replacement.Item)))
                throw new ArgumentException("Selected replacements require exact item, quality, and instance identities.", nameof(candidates));
        }

        if (capacities.Any(capacity => capacity.Quantity == 0 || capacity.Allocation.SourceKind == PortfolioAllocationSourceKind.HandMeDown))
            throw new ArgumentException("Base capacities must be positive owned or listing allocations; hand-me-downs require release evidence.", nameof(capacities));
        if (capacities.Select(capacity => capacity.Allocation).Distinct().Count() != capacities.Count)
            throw new ArgumentException("Base allocation capacities must be unique.", nameof(capacities));
        foreach (var capacity in capacities)
            ValidateAllocationIdentity(capacity.Allocation, capacity.Quantity);
    }

    private static void ValidateDemands(PortfolioCandidate candidate)
    {
        foreach (var demand in candidate.Demands)
        {
            if (demand.Quantity == 0)
                throw new ArgumentException("Allocation demands must be positive.", nameof(candidate));
            ValidateAllocationIdentity(demand.Allocation, demand.Quantity);
        }
    }

    private static void ValidateHandMeDowns(PortfolioCandidate candidate)
    {
        foreach (var handMeDown in candidate.HandMeDowns)
        {
            if (string.IsNullOrWhiteSpace(handMeDown.EvidenceGeneration) ||
                !IsExact(handMeDown.ReleasedItem) ||
                !IsExact(handMeDown.SelectedReplacement) ||
                handMeDown.ReleasedAllocation.SourceKind != PortfolioAllocationSourceKind.HandMeDown ||
                handMeDown.ReleasedAllocation.ItemId != handMeDown.ReleasedItem.ItemId ||
                handMeDown.ReleasedAllocation.IsHighQuality != handMeDown.ReleasedItem.IsHighQuality ||
                !string.Equals(handMeDown.ReleasedAllocation.InstanceId, handMeDown.ReleasedItem.InstanceId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Hand-me-down evidence must bind an exact released item, quality, instance, and evidence generation.", nameof(candidate));
            }

            var replacement = candidate.SelectedReplacements.SingleOrDefault(selected => selected.Position == handMeDown.Position);
            if (replacement is null || replacement.Item != handMeDown.SelectedReplacement)
                throw new ArgumentException("Hand-me-down evidence requires the selected candidate to install the exact replacement at that position.", nameof(candidate));
        }
    }

    private static void ValidateAllocationIdentity(PortfolioAllocationKey allocation, uint quantity)
    {
        if (string.IsNullOrWhiteSpace(allocation.SourceKey) || allocation.ItemId == 0)
            throw new ArgumentException("Allocations require stable source and item identities.");
        if (allocation.SourceKind is PortfolioAllocationSourceKind.OwnedInstance or PortfolioAllocationSourceKind.HandMeDown)
        {
            if (quantity != 1 || string.IsNullOrWhiteSpace(allocation.InstanceId))
                throw new ArgumentException("Owned and hand-me-down allocations require one exact instance.");
        }
    }

    private static bool IsExact(PortfolioExactItem item) =>
        item.ItemId != 0 && !string.IsNullOrWhiteSpace(item.InstanceId);

    private static bool IsFeasible(
        IReadOnlyList<PortfolioTargetPriority> orderedTargets,
        IReadOnlyList<PortfolioCandidate> selected,
        IReadOnlyList<PortfolioAllocationCapacity> baseCapacities)
    {
        var targetRank = orderedTargets
            .Select((target, index) => (target.TargetKey, Index: index))
            .ToDictionary(value => value.TargetKey, value => value.Index, StringComparer.Ordinal);
        var capacities = baseCapacities.ToDictionary(capacity => capacity.Allocation, capacity => capacity.Quantity);
        var releaseOwners = new Dictionary<PortfolioAllocationKey, string>();
        foreach (var candidate in selected)
        {
            foreach (var handMeDown in candidate.HandMeDowns)
            {
                if (!releaseOwners.TryAdd(handMeDown.ReleasedAllocation, candidate.TargetKey))
                    return false;
                capacities.Add(handMeDown.ReleasedAllocation, 1);
            }
        }

        var used = new Dictionary<PortfolioAllocationKey, uint>();
        foreach (var candidate in selected)
        {
            foreach (var demand in candidate.Demands)
            {
                if (!capacities.TryGetValue(demand.Allocation, out var available))
                    return false;
                if (demand.Allocation.SourceKind == PortfolioAllocationSourceKind.HandMeDown)
                {
                    if (!releaseOwners.TryGetValue(demand.Allocation, out var owner) ||
                        targetRank[owner] >= targetRank[candidate.TargetKey])
                        return false;
                }

                var total = checked(used.GetValueOrDefault(demand.Allocation) + demand.Quantity);
                if (total > available)
                    return false;
                used[demand.Allocation] = total;
            }
        }

        return true;
    }

    private static int Compare(
        IReadOnlyList<PortfolioTargetPriority> orderedTargets,
        IReadOnlyList<PortfolioCandidate> left,
        IReadOnlyList<PortfolioCandidate> right)
    {
        foreach (var target in orderedTargets)
        {
            var leftUtility = left.SingleOrDefault(candidate => candidate.TargetKey == target.TargetKey)?.UtilityGain ?? 0;
            var rightUtility = right.SingleOrDefault(candidate => candidate.TargetKey == target.TargetKey)?.UtilityGain ?? 0;
            var comparison = leftUtility.CompareTo(rightUtility);
            if (comparison != 0)
                return comparison;
        }

        var leftKey = string.Join("\n", left.OrderBy(candidate => candidate.TargetKey).Select(candidate => candidate.CandidateKey));
        var rightKey = string.Join("\n", right.OrderBy(candidate => candidate.TargetKey).Select(candidate => candidate.CandidateKey));
        return -StringComparer.Ordinal.Compare(leftKey, rightKey);
    }

    private static IReadOnlyList<PortfolioAllocationConsequence> ExplainScarcity(
        IReadOnlyList<PortfolioTargetPriority> orderedTargets,
        IReadOnlyList<PortfolioCandidate> candidates,
        IReadOnlyList<PortfolioAllocationCapacity> baseCapacities,
        IReadOnlyList<PortfolioCandidate> selected)
    {
        var targetByKey = orderedTargets.ToDictionary(target => target.TargetKey, StringComparer.Ordinal);
        var selectedByTarget = selected.ToDictionary(candidate => candidate.TargetKey, StringComparer.Ordinal);
        var consequences = new List<PortfolioAllocationConsequence>();
        foreach (var foregone in candidates
                     .Where(candidate => !selectedByTarget.TryGetValue(candidate.TargetKey, out var winner) || winner.CandidateKey != candidate.CandidateKey)
                     .OrderBy(candidate => targetByKey[candidate.TargetKey].Priority)
                     .ThenByDescending(candidate => candidate.UtilityGain))
        {
            var hypothetical = selected.Where(candidate => candidate.TargetKey != foregone.TargetKey).Append(foregone).ToArray();
            if (IsFeasible(orderedTargets, hypothetical, baseCapacities))
                continue;

            var capacities = baseCapacities.ToDictionary(capacity => capacity.Allocation, capacity => capacity.Quantity);
            foreach (var candidate in hypothetical)
            foreach (var release in candidate.HandMeDowns)
                capacities.TryAdd(release.ReleasedAllocation, 1);

            var existing = selected.Where(candidate => candidate.TargetKey != foregone.TargetKey).ToArray();
            foreach (var demand in foregone.Demands)
            {
                var available = capacities.GetValueOrDefault(demand.Allocation);
                var used = existing.SelectMany(candidate => candidate.Demands)
                    .Where(existingDemand => existingDemand.Allocation == demand.Allocation)
                    .Aggregate(0u, (sum, existingDemand) => checked(sum + existingDemand.Quantity));
                if (checked(used + demand.Quantity) <= available)
                    continue;

                var orderedRank = orderedTargets
                    .Select((target, index) => (target.TargetKey, Index: index))
                    .ToDictionary(value => value.TargetKey, value => value.Index, StringComparer.Ordinal);
                var higherPriorityWinners = existing
                    .Where(candidate => orderedRank[candidate.TargetKey] < orderedRank[foregone.TargetKey] &&
                                        candidate.Demands.Any(existingDemand => existingDemand.Allocation == demand.Allocation))
                    .Select(candidate => candidate.TargetKey)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (higherPriorityWinners.Length == 0)
                    continue;

                consequences.Add(new(
                    foregone.TargetKey,
                    foregone.CandidateKey,
                    demand.Allocation,
                    available,
                    checked(used + demand.Quantity),
                    higherPriorityWinners,
                    $"{foregone.CandidateKey} was not selected because higher-priority target(s) {string.Join(", ", higherPriorityWinners)} consume the scarce {demand.Allocation.SourceKind} allocation {demand.Allocation.SourceKey}."));
                break;
            }
        }

        return consequences;
    }
}
