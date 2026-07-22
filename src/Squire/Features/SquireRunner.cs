using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed record SquireRevalidationResult(bool Success, string Code, string Message)
{
    public static SquireRevalidationResult Valid() => new(true, "Valid", "Exact item identity is valid.");
    public static SquireRevalidationResult Fail(string code, string message) => new(false, code, message);
}

public sealed record SquireActionResult(bool Success, string Code, string Message)
{
    public static SquireActionResult Completed(string message) => new(true, "Completed", message);
    public static SquireActionResult Fail(string code, string message) => new(false, code, message);
}

public interface ISquireActionGameAdapter
{
    CharacterScope? GetActiveCharacter();
    bool HasConflictingAutomation(SquireDisposition disposition);
    SquireRevalidationResult Revalidate(EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition);
    SquireRevalidationResult RevalidateBatch(SquireActionPlan plan, IReadOnlyList<SquireReviewedSelection> remainingActions);
    SquireRevalidationResult RevalidateEvidence(SquireReviewedSelection selection);
    Task<SquireActionResult> ProbeAsync(EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition, CancellationToken cancellationToken);
    Task<SquireActionResult> ExecuteAsync(SquireActionPlan plan, IReadOnlyList<SquireReviewedSelection> remainingActions, SquireReviewedSelection action, CancellationToken cancellationToken);
    Task<SquireActionResult> BeginDispositionGroupAsync(SquireDisposition disposition, CancellationToken cancellationToken) =>
        Task.FromResult(SquireActionResult.Completed($"{disposition} requires no shared batch preparation."));
    Task<SquireActionResult> RecoverExecutionStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(SquireActionResult.Completed("No execution recovery was required."));
    Task<SquireActionResult> RecoverOwnedStateAsync(CancellationToken cancellationToken)
    {
        ReleaseOwnedState();
        return Task.FromResult(SquireActionResult.Completed("Owned Squire state was released."));
    }
    Task EndDispositionGroupAsync(SquireDisposition disposition, CancellationToken cancellationToken) => Task.CompletedTask;
    void ReleaseOwnedState();
    void CloseDiagnosticUi() => ReleaseOwnedState();
    string DiagnosticStatus => string.Empty;
}

public sealed record SquireRunEvent(
    DateTimeOffset Timestamp,
    string Kind,
    string Code,
    string Message,
    EquipmentInstanceFingerprint? Item = null);

public sealed record SquireRunResult(bool Success, string Code, IReadOnlyList<SquireRunEvent> Events);

public sealed class SquireRunner
{
    private readonly ISquireActionGameAdapter adapter;
    private readonly Action<SquireRunEvent> audit;

    public SquireRunner(ISquireActionGameAdapter adapter, Action<SquireRunEvent>? audit = null)
    {
        this.adapter = adapter;
        this.audit = audit ?? (_ => { });
    }

    public async Task<SquireRunResult> RunAsync(SquireActionPlan plan, bool explicitlyConfirmed, CancellationToken cancellationToken)
        => await RunCoreAsync(plan, explicitlyConfirmed, diagnostic: false, checkpointResume: false, cancellationToken).ConfigureAwait(false);

    public async Task<SquireRunResult> RunDiagnosticAsync(SquireActionPlan plan, bool explicitlyConfirmed, CancellationToken cancellationToken)
        => await RunCoreAsync(plan, explicitlyConfirmed, diagnostic: true, checkpointResume: false, cancellationToken).ConfigureAwait(false);

    public async Task<SquireRunResult> ResumeFromCheckpointAsync(SquireActionPlan checkpointPlan, bool diagnostic, CancellationToken cancellationToken)
        => await RunCoreAsync(
            checkpointPlan,
            explicitlyConfirmed: true,
            diagnostic: diagnostic,
            checkpointResume: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    private async Task<SquireRunResult> RunCoreAsync(
        SquireActionPlan plan,
        bool explicitlyConfirmed,
        bool diagnostic,
        bool checkpointResume,
        CancellationToken cancellationToken)
    {
        var events = new List<SquireRunEvent>();
        void Record(string kind, string code, string message, EquipmentInstanceFingerprint? item = null)
        {
            var value = new SquireRunEvent(DateTimeOffset.UtcNow, kind, code, message, item);
            events.Add(value);
            audit(value);
        }

        if (!explicitlyConfirmed)
            return Stop("ConfirmationRequired", "The reviewed run was not explicitly confirmed.");
        if (checkpointResume)
            Record("CheckpointResume", "CheckpointResume", $"Resuming {plan.Actions.Count} unfinished action(s) from the last approved plan checkpoint.");

        SquireDisposition? activeGroup = null;
        try
        {
            var orderedActions = SquireDispositionBatching.Order(plan.Actions);
            for (var actionIndex = 0; actionIndex < orderedActions.Count; actionIndex++)
            {
                var action = orderedActions[actionIndex];
                if (activeGroup != action.Disposition)
                {
                    if (activeGroup is { } completedGroup)
                    {
                        await adapter.EndDispositionGroupAsync(completedGroup, cancellationToken).ConfigureAwait(false);
                        Record("DispositionGroupEnd", completedGroup.ToString(), $"Finished {completedGroup} batch.");
                        activeGroup = null;
                    }
                    var groupRecovery = await adapter.RecoverExecutionStateAsync(cancellationToken).ConfigureAwait(false);
                    Record("ExecutionRecovery", groupRecovery.Code, groupRecovery.Message, action.Fingerprint);
                    if (!groupRecovery.Success)
                        return Stop(groupRecovery.Code, groupRecovery.Message, action.Fingerprint);
                    activeGroup = action.Disposition;
                    var groupPreparation = await adapter.BeginDispositionGroupAsync(activeGroup.Value, cancellationToken).ConfigureAwait(false);
                    Record("DispositionGroupPreparation", groupPreparation.Code, groupPreparation.Message);
                    if (!groupPreparation.Success)
                        return Stop(groupPreparation.Code, groupPreparation.Message, action.Fingerprint);
                    var groupCount = orderedActions.Count(value => value.Disposition == activeGroup);
                    Record("DispositionGroupStart", activeGroup.ToString()!, $"Starting {activeGroup} batch containing {groupCount} item(s).");
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (adapter.GetActiveCharacter() != plan.Character)
                    return Stop("CharacterScopeChanged", "The active character no longer matches the approved plan.", action.Fingerprint);
                if (adapter.HasConflictingAutomation(action.Disposition))
                    return Stop("ConflictingAutomation", "Another automation or an unrecoverable game state owns the required game state.", action.Fingerprint);
                var remaining = orderedActions.Skip(actionIndex).ToArray();
                var batchValidation = adapter.RevalidateBatch(plan, remaining);
                Record("BatchRevalidation", batchValidation.Code, batchValidation.Message, action.Fingerprint);
                if (!batchValidation.Success)
                    return Stop(batchValidation.Code, batchValidation.Message, action.Fingerprint);
                var validation = adapter.Revalidate(action.Fingerprint, action.Disposition);
                Record("Revalidation", validation.Code, validation.Message, action.Fingerprint);
                if (!validation.Success)
                    return Stop(validation.Code, validation.Message, action.Fingerprint);

                var evidence = adapter.RevalidateEvidence(action);
                Record("EvidenceRevalidation", evidence.Code, evidence.Message, action.Fingerprint);
                if (!evidence.Success)
                    return Stop(evidence.Code, evidence.Message, action.Fingerprint);

                Record(diagnostic ? "DiagnosticActionStart" : "ActionStart", action.Disposition.ToString(), $"Starting validated action {actionIndex + 1} of {orderedActions.Count}{(diagnostic ? " with diagnostics enabled" : string.Empty)}.", action.Fingerprint);
                var result = await adapter.ExecuteAsync(plan, remaining, action, cancellationToken).ConfigureAwait(false);
                Record(diagnostic ? "DiagnosticActionResult" : "ActionResult", result.Code, result.Message, action.Fingerprint);
                if (!result.Success)
                    return Stop(result.Code, result.Message, action.Fingerprint);
            }

            if (activeGroup is { } finalGroup)
            {
                await adapter.EndDispositionGroupAsync(finalGroup, cancellationToken).ConfigureAwait(false);
                Record("DispositionGroupEnd", finalGroup.ToString(), $"Finished {finalGroup} batch.");
                activeGroup = null;
            }

            var completedCode = diagnostic ? "DiagnosticCompleted" : "Completed";
            Record("RunComplete", completedCode, diagnostic ? "Every approved action completed with diagnostic capture enabled." : "Every approved action completed.");
            return new SquireRunResult(true, completedCode, events);
        }
        catch (OperationCanceledException)
        {
            return Stop("Cancelled", "The run was cancelled.");
        }
        catch (Exception ex)
        {
            return Stop("UnclassifiedFailure", ex.ToString());
        }
        finally
        {
            if (activeGroup is { } unfinishedGroup)
            {
                try { await adapter.EndDispositionGroupAsync(unfinishedGroup, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
            adapter.ReleaseOwnedState();
        }

        SquireRunResult Stop(string code, string message, EquipmentInstanceFingerprint? item = null)
        {
            Record("RunStopped", code, message, item);
            return new SquireRunResult(false, code, events);
        }
    }
}
