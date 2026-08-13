using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using Newtonsoft.Json;

namespace MarketMafioso.Squire.Outfitter.Portfolio;

public sealed record PortfolioEquipmentExecutionCommandResult(
    PortfolioEquipmentExecutionState? State,
    PortfolioEquipmentMoveResult? Move,
    string Message);

/// <summary>
/// Persisted coordinator for one user-triggered slot at a time. It saves the exact authorized
/// before-state before invoking the native move and accepts completion only from a newer exact
/// source/destination observation. A reload can therefore reconcile an interrupted submission.
/// </summary>
public sealed class PortfolioEquipmentExecutionCoordinator
{
    private static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromSeconds(5);
    private readonly ISquireConfigurationStore config;
    private readonly IPortfolioEquipmentMoveRuntime runtime;
    private readonly ISavedGearsetActivationRuntime activationRuntime;
    private readonly Func<ActivePlayerActivationProof>? captureActivePlayerProof;
    private readonly Func<string, RenderedRetainerEquipmentEvidence?>? captureRetainerProof;
    private bool submitAfterActivation;
    private PortfolioAuthorityFingerprint? pendingAuthorityFingerprint;

    public PortfolioEquipmentExecutionCoordinator(
        ISquireConfigurationStore config,
        IPortfolioEquipmentMoveRuntime runtime,
        ISavedGearsetActivationRuntime? activationRuntime = null,
        Func<ActivePlayerActivationProof>? captureActivePlayerProof = null,
        Func<string, RenderedRetainerEquipmentEvidence?>? captureRetainerProof = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.activationRuntime = activationRuntime ?? new UnavailableSavedGearsetActivationRuntime();
        this.captureActivePlayerProof = captureActivePlayerProof;
        this.captureRetainerProof = captureRetainerProof;
        State = Load(config.Squire.OutfitterEquipmentExecutionStateJson);
        NeedsRestartReconciliation = State?.Status == PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence;
        InterruptPersistedActivationWaits();
    }

    public PortfolioEquipmentExecutionState? State { get; private set; }
    public bool NeedsRestartReconciliation { get; private set; }

    public void Start(PortfolioEquipmentExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != PortfolioEquipmentExecution.CurrentSchemaVersion)
            throw new InvalidOperationException("Equipment execution schema is not supported.");
        State = state;
        NeedsRestartReconciliation = false;
        Save();
    }

    public PortfolioEquipmentExecutionCommandResult SubmitNext(
        PortfolioEquipmentMoveTarget target,
        DateTimeOffset nowUtc,
        PortfolioAuthorityFingerprint? currentAuthorityFingerprint = null)
    {
        var activationMessage = EnsureTargetActivation(target, nowUtc, explicitRequest: true);
        if (activationMessage is not null)
        {
            submitAfterActivation = State?.TargetActivations?.GetValueOrDefault(target.TargetKey)?.Status == EquipmentTargetActivationStatus.AwaitingFreshProof;
            pendingAuthorityFingerprint = currentAuthorityFingerprint;
            return new(State, null, activationMessage);
        }
        return SubmitAuthorizedTarget(target, nowUtc, currentAuthorityFingerprint);
    }

    public PortfolioEquipmentExecutionCommandResult AdvanceTargetActivation(
        PortfolioEquipmentMoveTarget target,
        DateTimeOffset nowUtc)
    {
        var message = EnsureTargetActivation(target, nowUtc, explicitRequest: false);
        if (message is not null)
            return new(State, null, message);
        if (!submitAfterActivation)
            return new(State, null, "The exact target is active and freshly proven.");
        submitAfterActivation = false;
        var authority = pendingAuthorityFingerprint;
        pendingAuthorityFingerprint = null;
        return SubmitAuthorizedTarget(target, nowUtc, authority);
    }

    public void InvalidateAuthority(string reason)
    {
        if (State is null || State.Status is PortfolioEquipmentExecutionStatus.Completed or PortfolioEquipmentExecutionStatus.UnexecutedSuffixRolledBack)
            return;
        State = PortfolioEquipmentExecution.StopForRuntime(State, reason);
        submitAfterActivation = false;
        pendingAuthorityFingerprint = null;
        Save();
    }

    private PortfolioEquipmentExecutionCommandResult SubmitAuthorizedTarget(
        PortfolioEquipmentMoveTarget target,
        DateTimeOffset nowUtc,
        PortfolioAuthorityFingerprint? currentAuthorityFingerprint)
    {
        if (State?.NextStep is not { } step)
            return new(State, null, "There is no remaining equipment step.");
        if (!string.Equals(step.TargetKey, target.TargetKey, StringComparison.Ordinal))
            return Stop("The selected equipment target does not own the next exact slot step.");
        if (!TryParseInstanceAddress(step.SourceItem, out var source, out var sourceReason))
            return Stop(sourceReason);
        var destination = new PortfolioEquipmentSlotAddress(
            target.Kind == PortfolioEquipmentMoveTargetKind.Retainer ? "RetainerEquippedItems" : "EquippedItems",
            DestinationSlot(step.Position));
        if (!TryObserve(step, target, source, destination, nowUtc, out var before, out var observeReason))
            return Stop(observeReason);

        State = PortfolioEquipmentExecution.AuthorizeNext(State, before, nowUtc, MaximumEvidenceAge, currentAuthorityFingerprint);
        Save(); // Deliberately before the native call: reload can reconcile this exact intent.
        if (State.Status != PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence)
            return new(State, null, State.StopReason ?? "The slot step was not authorized.");

        var move = PortfolioEquipmentMove.Execute(new(
            State.ExecutionId,
            step.StepId,
            target,
            step.Position,
            source,
            destination,
            step.SourceItem,
            step.ExpectedBefore), runtime);
        if (!move.WasSubmitted)
        {
            State = PortfolioEquipmentExecution.StopForRuntime(State, move.Message);
            Save();
        }
        NeedsRestartReconciliation = false;
        return new(State, move, move.Message);
    }

    public PortfolioEquipmentExecutionCommandResult ObservePending(
        PortfolioEquipmentMoveTarget target,
        DateTimeOffset nowUtc,
        PortfolioAuthorityFingerprint? currentAuthorityFingerprint = null)
    {
        if (State is not { Status: PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, NextStep: { } step })
            return new(State, null, "No submitted equipment step is awaiting after-evidence.");
        if (!TryParseInstanceAddress(step.SourceItem, out var source, out var sourceReason))
            return Stop(sourceReason);
        var destination = new PortfolioEquipmentSlotAddress(
            target.Kind == PortfolioEquipmentMoveTargetKind.Retainer ? "RetainerEquippedItems" : "EquippedItems",
            DestinationSlot(step.Position));
        if (!TryObserve(step, target, source, destination, nowUtc, out var after, out var observeReason))
            return Stop(observeReason);

        if (after.EquippedItem == step.ExpectedBefore &&
            after.AvailableSourceItems.Contains(step.SourceItem))
        {
            if (State.AfterEvidenceDeadlineUtc is { } deadline && nowUtc < deadline)
                return new(State, null, "Waiting for the exact slot transition to settle; the before-state remains intact.");
            State = PortfolioEquipmentExecution.RecoverAfterRestart(
                State,
                after,
                nowUtc,
                MaximumEvidenceAge,
                currentAuthorityFingerprint);
            NeedsRestartReconciliation = false;
            Save();
            return new(State, null, "The move did not change the exact slots before the settle deadline; the step is ready to retry.");
        }

        var accepted = PortfolioEquipmentExecution.AcceptAfter(
            State,
            after,
            nowUtc,
            MaximumEvidenceAge,
            currentAuthorityFingerprint);
        if (accepted.Status != PortfolioEquipmentExecutionStatus.StoppedForDrift &&
            accepted.CompletedStepCount == State.CompletedStepCount + 1 &&
            !TryPersistCompletedSavedGearsetTarget(State, step, target, out var saveReason))
            return AwaitSavedGearsetPersistence(saveReason);
        State = accepted;
        NeedsRestartReconciliation = false;
        Save();
        return new(State, null, State.Status == PortfolioEquipmentExecutionStatus.StoppedForDrift
            ? State.StopReason ?? "The equipment step drifted."
            : State.Status == PortfolioEquipmentExecutionStatus.Completed
                ? "Every exact equipment slot is complete."
                : "The exact slot transition is proven; the next step is ready.");
    }

    public PortfolioEquipmentExecutionCommandResult ReconcileAfterReload(
        PortfolioEquipmentMoveTarget target,
        DateTimeOffset nowUtc,
        PortfolioAuthorityFingerprint? currentAuthorityFingerprint = null)
    {
        if (State is not { Status: PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, NextStep: { } step })
            return new(State, null, "No interrupted equipment step requires reconciliation.");
        if (!TryParseInstanceAddress(step.SourceItem, out var source, out var sourceReason))
            return Stop(sourceReason);
        var destination = new PortfolioEquipmentSlotAddress(
            target.Kind == PortfolioEquipmentMoveTargetKind.Retainer ? "RetainerEquippedItems" : "EquippedItems",
            DestinationSlot(step.Position));
        if (!TryObserve(step, target, source, destination, nowUtc, out var observation, out var observeReason))
            return Stop(observeReason);
        var recovered = PortfolioEquipmentExecution.RecoverAfterRestart(
            State,
            observation,
            nowUtc,
            MaximumEvidenceAge,
            currentAuthorityFingerprint);
        if (recovered.Status != PortfolioEquipmentExecutionStatus.StoppedForDrift &&
            recovered.CompletedStepCount == State.CompletedStepCount + 1 &&
            !TryPersistCompletedSavedGearsetTarget(State, step, target, out var saveReason))
            return AwaitSavedGearsetPersistence(saveReason);
        State = recovered;
        NeedsRestartReconciliation = false;
        Save();
        return new(State, null, State.StopReason ?? "Interrupted equipment intent was reconciled from exact current slots.");
    }

    public void RollbackRemaining()
    {
        if (State is null)
            return;
        State = PortfolioEquipmentExecution.RollbackUnexecutedSuffix(State);
        NeedsRestartReconciliation = false;
        Save();
    }

    private bool TryObserve(
        PortfolioEquipmentStep step,
        PortfolioEquipmentMoveTarget target,
        PortfolioEquipmentSlotAddress source,
        PortfolioEquipmentSlotAddress destination,
        DateTimeOffset observedAtUtc,
        out PortfolioEquipmentObservation observation,
        out string reason)
    {
        observation = null!;
        if (!runtime.TryReadActiveTarget(target, out var active, out reason) || active is null || active != target)
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "The exact equipment target is no longer active." : reason;
            return false;
        }
        if (!runtime.TryReadSlot(source, out var sourceItem, out reason) ||
            !runtime.TryReadSlot(destination, out var destinationItem, out reason))
            return false;
        var sourceExact = sourceItem is null ? null : new PortfolioExactItem(
            sourceItem.ItemId,
            sourceItem.IsHighQuality,
            PortfolioEquipmentMove.ExactInstanceId(target.Owner, source, sourceItem));
        var destinationExact = destinationItem is null ? null : new PortfolioExactItem(
            destinationItem.ItemId,
            destinationItem.IsHighQuality,
            PortfolioEquipmentMove.ExactInstanceId(target.Owner, destination, destinationItem));
        observation = new(
            step.TargetKey,
            step.Position,
            destinationExact,
            sourceExact is null ? [] : [sourceExact],
            $"{observedAtUtc.UtcTicks}:{sourceExact?.InstanceId}:{destinationExact?.InstanceId}",
            observedAtUtc);
        reason = string.Empty;
        return true;
    }

    private bool TryPersistCompletedSavedGearsetTarget(
        PortfolioEquipmentExecutionState priorState,
        PortfolioEquipmentStep completedStep,
        PortfolioEquipmentMoveTarget target,
        out string reason)
    {
        reason = string.Empty;
        if (target.Kind != PortfolioEquipmentMoveTargetKind.SavedGearset ||
            priorState.Steps.Skip(priorState.CompletedStepCount + 1).Any(step =>
                string.Equals(step.TargetKey, completedStep.TargetKey, StringComparison.Ordinal)))
            return true;
        if (target.GearsetId is not { } gearsetId)
        {
            reason = "The completed saved target has no exact gearset ID to persist.";
            return false;
        }
        if (priorState.SavedLoadoutExpectations is null ||
            !priorState.SavedLoadoutExpectations.TryGetValue(target.TargetKey, out var expectation))
        {
            reason = "The completed saved target has no full authorized loadout expectation.";
            return false;
        }
        if (!TryReadExactSavedGearsetIdentity(target, gearsetId, out reason))
            return false;
        if (!TryProveActiveLoadout(expectation, out reason))
            return false;
        try
        {
            _ = activationRuntime.UpdateSavedGearset(gearsetId);
        }
        catch (Exception exception)
        {
            reason = $"The exact slots changed, but saving the target gearset failed before proof: {exception.Message}";
            return false;
        }

        if (!TryReadExactSavedGearsetIdentity(target, gearsetId, out reason))
            return false;

        foreach (var slot in expectation.Slots)
        {
            if (!activationRuntime.TryReadSavedGearsetItem(gearsetId, slot.Position, out var observed, out var diagnostic))
            {
                reason = string.IsNullOrWhiteSpace(diagnostic)
                    ? "The updated saved gearset could not be reread for exact proof."
                    : diagnostic;
                return false;
            }
            if (!SameItemAndQuality(slot.ExpectedItem, observed))
            {
                reason = $"The saved gearset did not retain the exact {slot.Position} item, quality, or empty state after update.";
                return false;
            }
        }
        return true;
    }

    private bool TryProveActiveLoadout(
        PortfolioSavedLoadoutExpectation expectation,
        out string reason)
    {
        foreach (var slot in expectation.Slots)
        {
            var canonical = PlayerAdvisorEquippedSlotMap.All.Single(value => value.Position == slot.Position);
            var address = new PortfolioEquipmentSlotAddress("EquippedItems", canonical.EquippedIndex);
            if (!runtime.TryReadSlot(address, out var observed, out var diagnostic))
            {
                reason = string.IsNullOrWhiteSpace(diagnostic)
                    ? $"The active {slot.Position} slot could not be reread before updating the saved gearset."
                    : diagnostic;
                return false;
            }
            if (!SameItemAndQuality(slot.ExpectedItem, observed))
            {
                reason = $"The active {slot.Position} slot drifted from the full authorized saved-loadout expectation; the gearset was not updated.";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    private static bool SameItemAndQuality(
        PortfolioExactItem? expected,
        PortfolioEquipmentSlotItem? observed) =>
        expected is null || observed is null
            ? expected is null && observed is null
            : expected.ItemId == observed.ItemId && expected.IsHighQuality == observed.IsHighQuality;

    private bool TryReadExactSavedGearsetIdentity(
        PortfolioEquipmentMoveTarget target,
        int gearsetId,
        out string reason)
    {
        if (!activationRuntime.TryReadSavedGearset(gearsetId, out var observed, out var diagnostic) || observed is null)
        {
            reason = string.IsNullOrWhiteSpace(diagnostic)
                ? "The saved gearset identity could not be reread for exact update proof."
                : diagnostic;
            return false;
        }
        if (observed.GearsetId != gearsetId ||
            observed.ClassJobId != target.ActiveClassJobId ||
            !string.Equals(observed.GearsetName, target.GearsetName, StringComparison.Ordinal))
        {
            reason = "The saved gearset ID, name, or class/job changed before update proof completed.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private PortfolioEquipmentExecutionCommandResult AwaitSavedGearsetPersistence(string reason)
    {
        // The physical slot already matches ExpectedAfter, but the saved target has not yet been
        // proven durable. Keep the pre-accept AwaitingAfterEvidence state so the same observation
        // can safely retry UpdateGearset and exact reread now or after reload.
        NeedsRestartReconciliation = true;
        Save();
        return new(State, null, reason);
    }

    private PortfolioEquipmentExecutionCommandResult Stop(string reason)
    {
        if (State is not null)
        {
            State = PortfolioEquipmentExecution.StopForRuntime(State, reason);
            Save();
        }
        return new(State, null, reason);
    }

    private void Save()
    {
        config.Squire.OutfitterEquipmentExecutionStateJson = State is null
            ? null
            : JsonConvert.SerializeObject(State, Formatting.None);
        config.Save();
    }

    private string? EnsureTargetActivation(
        PortfolioEquipmentMoveTarget target,
        DateTimeOffset nowUtc,
        bool explicitRequest)
    {
        if (State?.TargetActivations is null)
            return null;
        if (!State.TargetActivations.TryGetValue(target.TargetKey, out var activation))
            return "The equipment target has no persisted activation authority.";
        if (activation.Status == EquipmentTargetActivationStatus.Proven)
            return null;
        if (activation.Status == EquipmentTargetActivationStatus.Refused)
            return activation.Diagnostic ?? "Target activation stopped safely.";
        if (activation.Status == EquipmentTargetActivationStatus.Interrupted)
            activation = EquipmentTargetActivationCoordinator.ResumeProofWaitAfterReload(activation, nowUtc);
        if (activation.Status == EquipmentTargetActivationStatus.Ready)
        {
            if (!explicitRequest)
                return "The target is ready for activation.";
            activation = EquipmentTargetActivationCoordinator.Request(activation, activationRuntime, nowUtc);
        }
        SetActivation(activation);
        if (activation.Status != EquipmentTargetActivationStatus.AwaitingFreshProof)
            return activation.Diagnostic ?? "Target activation stopped safely.";

        if (activation.Target.Kind == EquipmentTargetActivationKind.Retainer)
        {
            var evidence = captureRetainerProof?.Invoke(target.TargetKey);
            if (evidence is null || activation.RequestedAtUtc is not { } requested || evidence.CapturedAtUtc <= requested)
                return "Opening and re-observing the exact retainer before the slot move.";
            activation = EquipmentTargetActivationCoordinator.AcceptRetainerProof(
                activation,
                evidence,
                nowUtc,
                MaximumEvidenceAge);
        }
        else
        {
            if (captureActivePlayerProof is null)
                return "Fresh active-player proof is unavailable.";
            var proof = captureActivePlayerProof();
            if (activation.RequestedAtUtc is not { } requested || proof.BaselineCompletedAtUtc <= requested)
                return "Refreshing the exact active equipment baseline before the slot move.";
            activation = EquipmentTargetActivationCoordinator.AcceptActivePlayerProof(
                activation,
                proof,
                nowUtc,
                MaximumEvidenceAge);
        }
        SetActivation(activation);
        return activation.Status == EquipmentTargetActivationStatus.Proven
            ? null
            : activation.Diagnostic ?? "Target activation stopped safely.";
    }

    private void SetActivation(EquipmentTargetActivationState activation)
    {
        if (State?.TargetActivations is null)
            return;
        var activations = new Dictionary<string, EquipmentTargetActivationState>(State.TargetActivations, StringComparer.Ordinal)
        {
            [activation.Target.TargetKey] = activation,
        };
        State = State with { TargetActivations = activations };
        Save();
    }

    private void InterruptPersistedActivationWaits()
    {
        if (State?.TargetActivations is null)
            return;
        var changed = false;
        var activations = State.TargetActivations.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        foreach (var key in activations.Keys.ToArray())
        {
            if (activations[key].Status != EquipmentTargetActivationStatus.AwaitingFreshProof)
                continue;
            activations[key] = EquipmentTargetActivationCoordinator.Interrupt(
                activations[key],
                "Activation proof was interrupted by reload; the native action will not be replayed.");
            changed = true;
        }
        if (changed)
            State = State with { TargetActivations = activations };
    }

    private sealed class UnavailableSavedGearsetActivationRuntime : ISavedGearsetActivationRuntime
    {
        public bool TryReadSavedGearset(int gearsetId, out SavedGearsetActivationIdentity? identity, out string diagnostic)
        {
            identity = null;
            diagnostic = "Saved gearset activation is unavailable in this runtime.";
            return false;
        }

        public int ActivateSavedGearset(int gearsetId) =>
            throw new InvalidOperationException("Saved gearset activation is unavailable in this runtime.");

        public int UpdateSavedGearset(int gearsetId) =>
            throw new InvalidOperationException("Saved gearset update is unavailable in this runtime.");

        public bool TryReadSavedGearsetItem(
            int gearsetId,
            EquipmentLoadoutPosition position,
            out PortfolioEquipmentSlotItem? item,
            out string diagnostic)
        {
            item = null;
            diagnostic = "Saved gearset update proof is unavailable in this runtime.";
            return false;
        }
    }

    private static PortfolioEquipmentExecutionState? Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var state = JsonConvert.DeserializeObject<PortfolioEquipmentExecutionState>(json);
            return state?.SchemaVersion == PortfolioEquipmentExecution.CurrentSchemaVersion ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseInstanceAddress(
        PortfolioExactItem item,
        out PortfolioEquipmentSlotAddress address,
        out string reason)
    {
        address = null!;
        var parts = item.InstanceId.Split(':');
        if (parts.Length != 5 || !int.TryParse(parts[2], out var slot) ||
            !uint.TryParse(parts[3], out var itemId) || !bool.TryParse(parts[4], out var highQuality) ||
            itemId != item.ItemId || highQuality != item.IsHighQuality)
        {
            reason = "The exact source instance does not encode one coherent container, slot, item, and quality identity.";
            return false;
        }
        address = new(parts[1], slot);
        reason = string.Empty;
        return true;
    }

    private static int DestinationSlot(EquipmentLoadoutPosition position) =>
        PlayerAdvisorEquippedSlotMap.All.SingleOrDefault(value => value.Position == position)?.EquippedIndex ?? -1;
}
