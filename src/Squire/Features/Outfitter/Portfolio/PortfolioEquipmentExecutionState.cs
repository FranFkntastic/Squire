using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;

namespace MarketMafioso.Squire.Outfitter.Portfolio;

public enum PortfolioEquipmentExecutionStatus
{
    Ready,
    AwaitingAfterEvidence,
    StoppedForDrift,
    Completed,
    UnexecutedSuffixRolledBack,
}

public sealed record PortfolioEquipmentStep(
    string StepId,
    string TargetKey,
    EquipmentLoadoutPosition Position,
    PortfolioExactItem SourceItem,
    PortfolioExactItem? ExpectedBefore,
    PortfolioExactItem ExpectedAfter);

public sealed record PortfolioEquipmentObservation(
    string TargetKey,
    EquipmentLoadoutPosition Position,
    PortfolioExactItem? EquippedItem,
    IReadOnlyList<PortfolioExactItem> AvailableSourceItems,
    string EvidenceGeneration,
    DateTimeOffset ObservedAtUtc);

public sealed record PortfolioSavedLoadoutSlotExpectation(
    EquipmentLoadoutPosition Position,
    PortfolioExactItem? ExpectedItem);

/// <summary>
/// The complete saved-loadout result authorized by one portfolio decision. UpdateGearset snapshots
/// the whole active loadout, so a partial changed-slot expectation can never authorize it.
/// </summary>
public sealed record PortfolioSavedLoadoutExpectation(
    string TargetKey,
    int GearsetId,
    string GearsetName,
    uint ClassJobId,
    IReadOnlyList<PortfolioSavedLoadoutSlotExpectation> Slots);

public sealed record PortfolioEquipmentExecutionState(
    string SchemaVersion,
    string ExecutionId,
    IReadOnlyList<PortfolioEquipmentStep> Steps,
    int CompletedStepCount,
    PortfolioEquipmentExecutionStatus Status,
    string? AuthorizedBeforeEvidenceGeneration,
    DateTimeOffset? AuthorizedBeforeObservedAtUtc,
    string? StopReason,
    IReadOnlyList<string> RolledBackUnexecutedStepIds,
    IReadOnlyDictionary<string, PortfolioEquipmentMoveTarget>? Targets = null,
    PortfolioAuthorityFingerprint? AuthorityFingerprint = null,
    PortfolioAuthorityEnvelope? AuthorityEnvelope = null,
    IReadOnlyDictionary<string, EquipmentTargetActivationState>? TargetActivations = null,
    DateTimeOffset? AfterEvidenceDeadlineUtc = null,
    IReadOnlyDictionary<string, PortfolioSavedLoadoutExpectation>? SavedLoadoutExpectations = null)
{
    public PortfolioEquipmentStep? NextStep => CompletedStepCount < Steps.Count ? Steps[CompletedStepCount] : null;
}

/// <summary>
/// Pure persisted state transitions for user-triggered equipping. It authorizes one exact slot at
/// a time, but never performs gameplay. Executed steps are immutable history; rollback cancels only
/// the unexecuted suffix.
/// </summary>
public static class PortfolioEquipmentExecution
{
    public const string CurrentSchemaVersion = "squire-outfitter-portfolio-equipment-execution/v3";

    public static PortfolioEquipmentExecutionState Create(
        string executionId,
        IReadOnlyList<PortfolioEquipmentStep> steps,
        IReadOnlyDictionary<string, PortfolioEquipmentMoveTarget>? targets = null,
        PortfolioAuthorityFingerprint? authorityFingerprint = null,
        PortfolioAuthorityEnvelope? authorityEnvelope = null,
        IReadOnlyDictionary<string, EquipmentTargetActivationState>? targetActivations = null,
        IReadOnlyDictionary<string, PortfolioSavedLoadoutExpectation>? savedLoadoutExpectations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
            throw new ArgumentException("Equipment execution requires at least one exact slot step.", nameof(steps));
        if (steps.Any(step => string.IsNullOrWhiteSpace(step.StepId) ||
                              string.IsNullOrWhiteSpace(step.TargetKey) ||
                              !IsExact(step.SourceItem) ||
                              (step.ExpectedBefore is not null && !IsExact(step.ExpectedBefore)) ||
                              !IsExact(step.ExpectedAfter) ||
                              step.SourceItem.ItemId != step.ExpectedAfter.ItemId ||
                              step.SourceItem.IsHighQuality != step.ExpectedAfter.IsHighQuality))
            throw new ArgumentException("Each equipment step must bind a target, position, exact source instance, and a destination result with matching item and quality.", nameof(steps));
        if (steps.Select(step => step.StepId).Distinct(StringComparer.Ordinal).Count() != steps.Count)
            throw new ArgumentException("Equipment step IDs must be unique.", nameof(steps));
        if (targets is not null && steps.Any(step => !targets.ContainsKey(step.TargetKey)))
            throw new ArgumentException("Every equipment step requires one persisted exact target authority.", nameof(targets));
        if (targets is not null && targets.Any(target =>
                !string.Equals(target.Key, target.Value.TargetKey, StringComparison.Ordinal)))
            throw new ArgumentException("Each persisted target authority must be stored under its exact target key.", nameof(targets));
        if (authorityEnvelope is not null && (!authorityEnvelope.HasValidFingerprint() ||
                                             authorityFingerprint != authorityEnvelope.Fingerprint))
            throw new ArgumentException("Equipment execution authority must carry one valid matching portfolio envelope and fingerprint.", nameof(authorityEnvelope));
        if (authorityEnvelope is not null && steps.Any(step => !authorityEnvelope.Targets.Any(target =>
                string.Equals(target.TargetKey, step.TargetKey, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(target.SelectedCandidateKey))))
            throw new ArgumentException("Every equipment step requires a selected target in the persisted portfolio envelope.", nameof(authorityEnvelope));
        if (targetActivations is not null && steps.Any(step => !targetActivations.ContainsKey(step.TargetKey)))
            throw new ArgumentException("Every equipment step requires one persisted target-activation authority.", nameof(targetActivations));
        ValidateSavedLoadoutExpectations(steps, targets, savedLoadoutExpectations);
        if (targetActivations is not null && targetActivations.Any(activation =>
                !string.Equals(activation.Key, activation.Value.Target.TargetKey, StringComparison.Ordinal) ||
                activation.Value.SchemaVersion != EquipmentTargetActivationCoordinator.CurrentSchemaVersion))
            throw new ArgumentException("Each persisted target activation must use its exact target key and current schema.", nameof(targetActivations));

        var lastBySlot = new Dictionary<(string Target, EquipmentLoadoutPosition Position), PortfolioExactItem?>();
        foreach (var step in steps)
        {
            var key = (step.TargetKey, step.Position);
            if (lastBySlot.TryGetValue(key, out var prior) && prior != step.ExpectedBefore)
                throw new ArgumentException("Repeated slot steps must continue from the exact prior expected result.", nameof(steps));
            lastBySlot[key] = step.ExpectedAfter;
        }

        return new(
            CurrentSchemaVersion,
            executionId,
            steps.ToArray(),
            0,
            PortfolioEquipmentExecutionStatus.Ready,
            null,
            null,
            null,
            [],
            targets is null ? null : new Dictionary<string, PortfolioEquipmentMoveTarget>(targets, StringComparer.Ordinal),
            authorityFingerprint,
            authorityEnvelope,
            targetActivations is null ? null : new Dictionary<string, EquipmentTargetActivationState>(targetActivations, StringComparer.Ordinal),
            null,
            savedLoadoutExpectations is null ? null : new Dictionary<string, PortfolioSavedLoadoutExpectation>(savedLoadoutExpectations, StringComparer.Ordinal));
    }

    public static PortfolioEquipmentExecutionState AuthorizeNext(
        PortfolioEquipmentExecutionState state,
        PortfolioEquipmentObservation before,
        DateTimeOffset nowUtc,
        TimeSpan maximumEvidenceAge,
        PortfolioAuthorityFingerprint? currentAuthorityFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(before);
        if (state.Status is not (PortfolioEquipmentExecutionStatus.Ready or PortfolioEquipmentExecutionStatus.StoppedForDrift))
            throw new InvalidOperationException("The execution is not ready to authorize its next slot.");
        if (AuthorityMismatch(state, currentAuthorityFingerprint) is { } authorityError)
            return Stop(state, authorityError);
        var step = state.NextStep ?? throw new InvalidOperationException("The execution has no remaining step.");
        var error = ValidateObservation(step, before, nowUtc, maximumEvidenceAge, requireSource: true, expectedEquipped: step.ExpectedBefore);
        if (error is not null)
            return Stop(state, error);

        return state with
        {
            Status = PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence,
            AuthorizedBeforeEvidenceGeneration = before.EvidenceGeneration,
            AuthorizedBeforeObservedAtUtc = before.ObservedAtUtc,
            AfterEvidenceDeadlineUtc = nowUtc + maximumEvidenceAge,
            StopReason = null,
        };
    }

    public static PortfolioEquipmentExecutionState AcceptAfter(
        PortfolioEquipmentExecutionState state,
        PortfolioEquipmentObservation after,
        DateTimeOffset nowUtc,
        TimeSpan maximumEvidenceAge,
        PortfolioAuthorityFingerprint? currentAuthorityFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(after);
        if (state.Status != PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence)
            throw new InvalidOperationException("No exact slot action is awaiting after-evidence.");
        if (AuthorityMismatch(state, currentAuthorityFingerprint) is { } authorityError)
            return Stop(state, authorityError);
        var step = state.NextStep ?? throw new InvalidOperationException("The execution has no remaining step.");
        var error = ValidateObservation(step, after, nowUtc, maximumEvidenceAge, requireSource: false, expectedEquipped: step.ExpectedAfter);
        if (error is null && (string.Equals(after.EvidenceGeneration, state.AuthorizedBeforeEvidenceGeneration, StringComparison.Ordinal) ||
                              after.ObservedAtUtc <= state.AuthorizedBeforeObservedAtUtc))
            error = "After-evidence must be a fresh generation observed after the authorized before-evidence.";
        if (error is not null)
            return Stop(state, error);

        var completed = state.CompletedStepCount + 1;
        return state with
        {
            CompletedStepCount = completed,
            Status = completed == state.Steps.Count
                ? PortfolioEquipmentExecutionStatus.Completed
                : PortfolioEquipmentExecutionStatus.Ready,
            AuthorizedBeforeEvidenceGeneration = null,
            AuthorizedBeforeObservedAtUtc = null,
            AfterEvidenceDeadlineUtc = null,
            StopReason = null,
        };
    }

    /// <summary>
    /// Reconciles a persisted in-flight step after restart. Exact after-state completes it, exact
    /// before-state waits through the persisted settle deadline and then safely returns it to
    /// Ready, while every third state stops as drift.
    /// </summary>
    public static PortfolioEquipmentExecutionState RecoverAfterRestart(
        PortfolioEquipmentExecutionState state,
        PortfolioEquipmentObservation observation,
        DateTimeOffset nowUtc,
        TimeSpan maximumEvidenceAge,
        PortfolioAuthorityFingerprint? currentAuthorityFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);
        if (state.Status != PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence)
            return state;
        if (AuthorityMismatch(state, currentAuthorityFingerprint) is { } authorityError)
            return Stop(state, authorityError);
        var step = state.NextStep ?? throw new InvalidOperationException("The execution has no remaining step.");
        var commonError = ValidateObservationIdentity(step, observation, nowUtc, maximumEvidenceAge);
        if (commonError is not null)
            return Stop(state, commonError);
        if (observation.EquippedItem == step.ExpectedAfter &&
            !string.Equals(observation.EvidenceGeneration, state.AuthorizedBeforeEvidenceGeneration, StringComparison.Ordinal) &&
            observation.ObservedAtUtc > state.AuthorizedBeforeObservedAtUtc)
            return AcceptAfter(state, observation, nowUtc, maximumEvidenceAge);
        if (observation.EquippedItem == step.ExpectedBefore && observation.AvailableSourceItems.Contains(step.SourceItem) &&
            state.AfterEvidenceDeadlineUtc is { } settleDeadline && nowUtc < settleDeadline)
            return state;
        if (observation.EquippedItem == step.ExpectedBefore && observation.AvailableSourceItems.Contains(step.SourceItem))
            return state with
            {
                Status = PortfolioEquipmentExecutionStatus.Ready,
                AuthorizedBeforeEvidenceGeneration = null,
                AuthorizedBeforeObservedAtUtc = null,
                AfterEvidenceDeadlineUtc = null,
                StopReason = null,
            };
        return Stop(state, "Restart reconciliation found neither the exact before-state nor the exact expected after-state.");
    }

    public static PortfolioEquipmentExecutionState RollbackUnexecutedSuffix(PortfolioEquipmentExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status == PortfolioEquipmentExecutionStatus.Completed)
            throw new InvalidOperationException("Executed equipment history cannot be rolled back by the state model.");
        var removed = state.Steps.Skip(state.CompletedStepCount).Select(step => step.StepId).ToArray();
        return state with
        {
            Steps = state.Steps.Take(state.CompletedStepCount).ToArray(),
            Status = PortfolioEquipmentExecutionStatus.UnexecutedSuffixRolledBack,
            AuthorizedBeforeEvidenceGeneration = null,
            AuthorizedBeforeObservedAtUtc = null,
            AfterEvidenceDeadlineUtc = null,
            StopReason = null,
            RolledBackUnexecutedStepIds = removed,
        };
    }

    public static PortfolioEquipmentExecutionState StopForRuntime(
        PortfolioEquipmentExecutionState state,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (state.Status == PortfolioEquipmentExecutionStatus.Completed)
            throw new InvalidOperationException("Completed equipment execution cannot be stopped for a runtime failure.");
        return Stop(state, reason.Trim());
    }

    private static string? ValidateObservation(
        PortfolioEquipmentStep step,
        PortfolioEquipmentObservation observation,
        DateTimeOffset nowUtc,
        TimeSpan maximumEvidenceAge,
        bool requireSource,
        PortfolioExactItem? expectedEquipped)
    {
        var error = ValidateObservationIdentity(step, observation, nowUtc, maximumEvidenceAge);
        if (error is not null)
            return error;
        if (observation.EquippedItem != expectedEquipped)
            return $"Observed {step.Position} does not match the exact expected item and quality.";
        if (requireSource && !observation.AvailableSourceItems.Contains(step.SourceItem))
            return "The exact source item instance is no longer available.";
        return null;
    }

    private static string? ValidateObservationIdentity(
        PortfolioEquipmentStep step,
        PortfolioEquipmentObservation observation,
        DateTimeOffset nowUtc,
        TimeSpan maximumEvidenceAge)
    {
        if (maximumEvidenceAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumEvidenceAge));
        if (!string.Equals(observation.TargetKey, step.TargetKey, StringComparison.Ordinal) || observation.Position != step.Position)
            return "Equipment evidence belongs to a different target or slot.";
        if (string.IsNullOrWhiteSpace(observation.EvidenceGeneration))
            return "Equipment evidence has no stable generation.";
        if (observation.ObservedAtUtc > nowUtc || nowUtc - observation.ObservedAtUtc > maximumEvidenceAge)
            return "Equipment evidence is not fresh enough to authorize this slot.";
        return null;
    }

    private static PortfolioEquipmentExecutionState Stop(PortfolioEquipmentExecutionState state, string reason) => state with
    {
        Status = PortfolioEquipmentExecutionStatus.StoppedForDrift,
        StopReason = reason,
        AuthorizedBeforeEvidenceGeneration = null,
        AuthorizedBeforeObservedAtUtc = null,
        AfterEvidenceDeadlineUtc = null,
    };

    private static bool IsExact(PortfolioExactItem item) =>
        item.ItemId != 0 && !string.IsNullOrWhiteSpace(item.InstanceId);

    private static void ValidateSavedLoadoutExpectations(
        IReadOnlyList<PortfolioEquipmentStep> steps,
        IReadOnlyDictionary<string, PortfolioEquipmentMoveTarget>? targets,
        IReadOnlyDictionary<string, PortfolioSavedLoadoutExpectation>? expectations)
    {
        var savedTargetKeys = targets?.Values
            .Where(value => value.Kind == PortfolioEquipmentMoveTargetKind.SavedGearset)
            .Select(value => value.TargetKey)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        if (savedTargetKeys.Count == 0 && expectations is null)
            return;
        if (expectations is null || !savedTargetKeys.SetEquals(expectations.Keys))
            throw new ArgumentException("Every saved-gearset target requires one complete persisted loadout expectation.", nameof(expectations));
        var canonical = PlayerAdvisorEquippedSlotMap.All.Select(value => value.Position).ToHashSet();
        foreach (var (targetKey, expectation) in expectations)
        {
            var target = targets![targetKey];
            if (!string.Equals(expectation.TargetKey, targetKey, StringComparison.Ordinal) ||
                expectation.GearsetId != target.GearsetId ||
                expectation.ClassJobId != target.ActiveClassJobId ||
                !string.Equals(expectation.GearsetName, target.GearsetName, StringComparison.Ordinal) ||
                expectation.Slots.Count != canonical.Count ||
                expectation.Slots.Select(value => value.Position).ToHashSet().SetEquals(canonical) == false ||
                expectation.Slots.GroupBy(value => value.Position).Any(group => group.Count() != 1) ||
                expectation.Slots.Any(value => value.ExpectedItem is { } item && !IsExact(item)))
                throw new ArgumentException("Saved-loadout expectation must bind exact target identity and every canonical slot.", nameof(expectations));
            foreach (var step in steps.Where(value => string.Equals(value.TargetKey, targetKey, StringComparison.Ordinal)))
            {
                var expected = expectation.Slots.Single(value => value.Position == step.Position).ExpectedItem;
                if (expected is null || expected.ItemId != step.ExpectedAfter.ItemId || expected.IsHighQuality != step.ExpectedAfter.IsHighQuality)
                    throw new ArgumentException("Saved-loadout expectation must include every selected replacement result.", nameof(expectations));
            }
        }
    }

    private static string? AuthorityMismatch(
        PortfolioEquipmentExecutionState state,
        PortfolioAuthorityFingerprint? currentAuthorityFingerprint)
    {
        if (state.AuthorityFingerprint is null)
            return null;
        if (currentAuthorityFingerprint is null)
            return "Current portfolio authority lineage is unavailable; slot execution stopped.";
        return state.AuthorityFingerprint == currentAuthorityFingerprint
            ? null
            : "Portfolio target priority, evidence lineage, allocation, or hand-me-down authority changed; slot execution stopped.";
    }
}
