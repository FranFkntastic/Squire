using System;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire.Outfitter;

namespace MarketMafioso.Squire.Observation;

public enum RetainerTargetObservationStatus
{
    Idle,
    Preparing,
    OpeningTarget,
    ReadingIdentity,
    ScanningEquipment,
    Complete,
    Failed,
    Cancelled,
}

public enum RetainerTargetOpenStatus
{
    Accepted,
    Waiting,
    Refused,
}

public sealed record RetainerTargetOpenResult(
    RetainerTargetOpenStatus Status,
    string Diagnostic);

public interface IRetainerTargetObservationRuntime
{
    RenderedRetainerUiPreparationProgress BeginPreparation(string ownerHomeWorld);
    RenderedRetainerUiPreparationProgress AdvancePreparation();
    RenderedRetainerUiPreparationProgress CancelPreparation();
    RetainerTargetOpenResult TryOpenRetainer(string retainerName);
    AgentBridgeRenderedUiSnapshot CaptureRetainerUi();
    RenderedEquipmentScanProgress BeginRetainerEquipmentScan();
    RenderedEquipmentScanStepResult AdvanceRetainerEquipmentScan();
    RenderedEquipmentScanProgress CancelRetainerEquipmentScan();
}

public sealed record RetainerTargetObservationProgress(
    RetainerTargetObservationStatus Status,
    string? TargetKey,
    RenderedRetainerIdentityObservation? Identity,
    RenderedEquipmentScanProgress? EquipmentScan,
    RenderedRetainerEquipmentEvidence? Evidence,
    string Diagnostic);

/// <summary>
/// Coordinates one owner-bound rendered retainer observation. It does not infer target identity,
/// does not accept cached worn gear, and rechecks the owner and rendered identity on every scan
/// step so a retainer switch cannot publish evidence for the prior target.
/// </summary>
public sealed class RetainerTargetObservationController
{
    private readonly IRetainerTargetObservationRuntime runtime;
    private readonly TimeSpan openTimeout;
    private OutfitterTarget? target;
    private RetainerObservationOwnerScope? owner;
    private RenderedRetainerIdentityObservation? identity;
    private RenderedEquipmentScanProgress? equipmentScan;
    private RenderedRetainerEquipmentEvidence? evidence;
    private RetainerTargetObservationStatus status = RetainerTargetObservationStatus.Idle;
    private DateTimeOffset phaseStartedAt;
    private string diagnostic = "Retainer observation has not started.";

    public RetainerTargetObservationController(
        IRetainerTargetObservationRuntime runtime,
        TimeSpan? openTimeout = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.openTimeout = openTimeout ?? TimeSpan.FromSeconds(45);
        if (this.openTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(openTimeout));
    }

    public RetainerTargetObservationProgress Begin(
        OutfitterTarget target,
        RetainerObservationOwnerScope owner,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);
        Reset(target, owner, nowUtc);
        if (!OwnerMatchesTarget(owner))
            return Fail("The active character does not exactly own the selected retainer target.");

        var preparation = runtime.BeginPreparation(owner.HomeWorld);
        return ApplyPreparation(preparation, nowUtc);
    }

    public RetainerTargetObservationProgress Advance(
        RetainerObservationOwnerScope currentOwner,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(currentOwner);
        if (status is RetainerTargetObservationStatus.Idle or RetainerTargetObservationStatus.Complete or
            RetainerTargetObservationStatus.Failed or RetainerTargetObservationStatus.Cancelled)
            return Snapshot();
        if (!OwnerMatchesTarget(currentOwner) || owner is null ||
            currentOwner.LocalContentId != owner.LocalContentId ||
            !Same(currentOwner.CharacterName, owner.CharacterName) ||
            !Same(currentOwner.HomeWorld, owner.HomeWorld))
            return Fail("The active character scope changed during retainer observation; captured evidence was discarded.");

        return status switch
        {
            RetainerTargetObservationStatus.Preparing => ApplyPreparation(runtime.AdvancePreparation(), nowUtc),
            RetainerTargetObservationStatus.OpeningTarget => AdvanceOpening(currentOwner, nowUtc),
            RetainerTargetObservationStatus.ReadingIdentity => AdvanceIdentity(currentOwner, nowUtc),
            RetainerTargetObservationStatus.ScanningEquipment => AdvanceEquipment(currentOwner, nowUtc),
            _ => Fail("Retainer observation entered an unsupported state."),
        };
    }

    public RetainerTargetObservationProgress Cancel()
    {
        if (status is RetainerTargetObservationStatus.Idle or RetainerTargetObservationStatus.Complete or
            RetainerTargetObservationStatus.Failed or RetainerTargetObservationStatus.Cancelled)
            return Snapshot();
        runtime.CancelPreparation();
        runtime.CancelRetainerEquipmentScan();
        identity = null;
        equipmentScan = null;
        evidence = null;
        status = RetainerTargetObservationStatus.Cancelled;
        diagnostic = "Retainer observation was cancelled; incomplete evidence was discarded.";
        return Snapshot();
    }

    public RetainerTargetObservationProgress Snapshot() =>
        new(status, target?.Key, identity, equipmentScan, evidence, diagnostic);

    private RetainerTargetObservationProgress ApplyPreparation(
        RenderedRetainerUiPreparationProgress preparation,
        DateTimeOffset nowUtc)
    {
        switch (preparation.Status)
        {
            case RenderedRetainerUiPreparationStatus.Complete:
                status = RetainerTargetObservationStatus.OpeningTarget;
                phaseStartedAt = nowUtc;
                diagnostic = "The rendered Retainer List is ready; opening the exact selected retainer.";
                break;
            case RenderedRetainerUiPreparationStatus.Failed:
                return Fail(preparation.Diagnostic);
            case RenderedRetainerUiPreparationStatus.Cancelled:
                status = RetainerTargetObservationStatus.Cancelled;
                diagnostic = preparation.Diagnostic;
                break;
            default:
                status = RetainerTargetObservationStatus.Preparing;
                diagnostic = preparation.Diagnostic;
                break;
        }
        return Snapshot();
    }

    private RetainerTargetObservationProgress AdvanceOpening(
        RetainerObservationOwnerScope currentOwner,
        DateTimeOffset nowUtc)
    {
        var rendered = runtime.CaptureRetainerUi();
        var parsed = RenderedRetainerIdentityParser.Parse(rendered, target!, currentOwner);
        if (parsed.Status == RenderedRetainerIdentityStatus.Complete)
        {
            status = RetainerTargetObservationStatus.ReadingIdentity;
            identity = parsed;
            diagnostic = parsed.Diagnostic;
            return AdvanceIdentity(currentOwner, nowUtc);
        }
        if (parsed.Status == RenderedRetainerIdentityStatus.Ambiguous)
            return Fail(parsed.Diagnostic);
        if (nowUtc - phaseStartedAt > openTimeout)
            return Fail("The selected retainer's rendered attributes and gear surface did not open within the bounded timeout.");

        var open = runtime.TryOpenRetainer(target!.RetainerMetadata!.RetainerName);
        if (open.Status == RetainerTargetOpenStatus.Refused)
            return Fail(open.Diagnostic);
        diagnostic = open.Diagnostic;
        return Snapshot();
    }

    private RetainerTargetObservationProgress AdvanceIdentity(
        RetainerObservationOwnerScope currentOwner,
        DateTimeOffset nowUtc)
    {
        var rendered = runtime.CaptureRetainerUi();
        var parsed = RenderedRetainerIdentityParser.Parse(rendered, target!, currentOwner);
        if (parsed.Status != RenderedRetainerIdentityStatus.Complete)
            return Fail($"The selected retainer identity changed before equipment observation began: {parsed.Diagnostic}");
        identity = parsed;
        equipmentScan = runtime.BeginRetainerEquipmentScan();
        if (equipmentScan.Status == RenderedEquipmentScanStatus.Failed)
            return Fail(equipmentScan.Diagnostic);
        if (equipmentScan.Status != RenderedEquipmentScanStatus.ReadyToHover)
            return Fail("The rendered retainer equipment scan did not enter its exact ready state.");
        status = RetainerTargetObservationStatus.ScanningEquipment;
        phaseStartedAt = nowUtc;
        diagnostic = equipmentScan.Diagnostic;
        return Snapshot();
    }

    private RetainerTargetObservationProgress AdvanceEquipment(
        RetainerObservationOwnerScope currentOwner,
        DateTimeOffset nowUtc)
    {
        var rendered = runtime.CaptureRetainerUi();
        var currentIdentity = RenderedRetainerIdentityParser.Parse(rendered, target!, currentOwner);
        if (currentIdentity.Status != RenderedRetainerIdentityStatus.Complete || identity is null ||
            currentIdentity.ClassJobId != identity.ClassJobId || currentIdentity.Level != identity.Level ||
            !Same(currentIdentity.RetainerName, identity.RetainerName))
            return Fail("The rendered retainer identity changed during equipment observation; captured equipment was discarded.");

        var step = runtime.AdvanceRetainerEquipmentScan();
        equipmentScan = step.Progress;
        if (equipmentScan.Status == RenderedEquipmentScanStatus.Failed)
            return Fail(equipmentScan.Diagnostic);
        if (equipmentScan.Status == RenderedEquipmentScanStatus.Cancelled)
        {
            status = RetainerTargetObservationStatus.Cancelled;
            identity = null;
            equipmentScan = null;
            diagnostic = "The rendered retainer equipment scan was cancelled; incomplete evidence was discarded.";
            return Snapshot();
        }
        if (equipmentScan.Status != RenderedEquipmentScanStatus.Complete)
        {
            diagnostic = equipmentScan.Diagnostic;
            return Snapshot();
        }

        var scan = new RenderedRetainerEquipmentScanObservation(
            equipmentScan.Status,
            rendered.CapturedAtUtc > identity.CapturedAtUtc ? rendered.CapturedAtUtc : identity.CapturedAtUtc,
            identity.OwnerCharacterName,
            identity.OwnerHomeWorld,
            identity.RetainerName,
            equipmentScan.CompletedSlots,
            equipmentScan.TotalSlots,
            equipmentScan.Observations,
            equipmentScan.Diagnostic);
        evidence = RenderedRetainerEquipmentEvidenceAssembler.Assemble(target!, identity, scan);
        if (evidence.Status != RenderedRetainerEquipmentEvidenceStatus.Complete)
            return Fail(evidence.Diagnostic);
        status = RetainerTargetObservationStatus.Complete;
        diagnostic = evidence.Diagnostic;
        return Snapshot();
    }

    private bool OwnerMatchesTarget(RetainerObservationOwnerScope current) =>
        current.IsAvailable && target is
        {
            Kind: OutfitterTargetKind.Retainer,
            IsCurrentCharacter: true,
            RetainerMetadata: { } metadata,
        } &&
        metadata.OwnerContentId == current.LocalContentId &&
        Same(target.OwnerCharacterName, current.CharacterName) &&
        Same(target.OwnerHomeWorld, current.HomeWorld);

    private void Reset(OutfitterTarget newTarget, RetainerObservationOwnerScope newOwner, DateTimeOffset nowUtc)
    {
        target = newTarget;
        owner = newOwner;
        identity = null;
        equipmentScan = null;
        evidence = null;
        status = RetainerTargetObservationStatus.Idle;
        phaseStartedAt = nowUtc;
        diagnostic = "Retainer observation is starting.";
    }

    private RetainerTargetObservationProgress Fail(string message)
    {
        runtime.CancelPreparation();
        runtime.CancelRetainerEquipmentScan();
        identity = null;
        equipmentScan = null;
        evidence = null;
        status = RetainerTargetObservationStatus.Failed;
        diagnostic = message;
        return Snapshot();
    }

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
