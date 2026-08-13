using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Portfolio;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Observation;

public enum EquipmentTargetActivationKind
{
    ActivePlayer,
    SavedGearset,
    Retainer,
}

public enum EquipmentTargetActivationStatus
{
    Ready,
    AwaitingFreshProof,
    Proven,
    Interrupted,
    Refused,
}

public sealed record EquipmentTargetActivationTarget(
    string TargetKey,
    EquipmentTargetActivationKind Kind,
    PortfolioEquipmentMoveOwner Owner,
    uint ClassJobId,
    int? GearsetId = null,
    string? GearsetName = null,
    ulong? RetainerId = null,
    string? RetainerName = null,
    string? OwnerHomeWorldName = null);

public sealed record SavedGearsetActivationIdentity(
    int GearsetId,
    string GearsetName,
    uint ClassJobId);

public sealed record ActivePlayerActivationProof(
    PortfolioEquipmentMoveOwner Owner,
    uint ClassJobId,
    SavedGearsetActivationIdentity? CurrentGearset,
    Guid EquipmentGeneration,
    DateTimeOffset BaselineCompletedAtUtc,
    bool BaselineCompleteAndConsistent,
    PlayerAdvisorBaselineTargetKind BaselineTargetKind);

public sealed record EquipmentTargetActivationState(
    string SchemaVersion,
    string ActivationId,
    EquipmentTargetActivationTarget Target,
    EquipmentTargetActivationStatus Status,
    DateTimeOffset? RequestedAtUtc,
    int? NativeResult,
    Guid? ProvenEquipmentGeneration,
    string? Diagnostic)
{
    public bool CanMoveSlots => Status == EquipmentTargetActivationStatus.Proven;
}

public interface ISavedGearsetActivationRuntime
{
    bool TryReadSavedGearset(
        int gearsetId,
        out SavedGearsetActivationIdentity? identity,
        out string diagnostic);

    int ActivateSavedGearset(int gearsetId);

    int UpdateSavedGearset(int gearsetId);

    bool TryReadSavedGearsetItem(
        int gearsetId,
        Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition position,
        out PortfolioEquipmentSlotItem? item,
        out string diagnostic);
}

/// <summary>
/// Persistable target-activation authority for portfolio equipping. A native gearset return is
/// recorded only as a submission receipt: slot moves remain unavailable until a newer, complete
/// active-player baseline and the live current-gearset identity prove the requested transition.
/// </summary>
public static class EquipmentTargetActivationCoordinator
{
    public const string CurrentSchemaVersion = "squire-outfitter-equipment-target-activation/v1";

    public static EquipmentTargetActivationState Create(
        string activationId,
        EquipmentTargetActivationTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activationId);
        ArgumentNullException.ThrowIfNull(target);
        var diagnostic = ValidateTarget(target);
        return new(
            CurrentSchemaVersion,
            activationId,
            target,
            diagnostic is null ? EquipmentTargetActivationStatus.Ready : EquipmentTargetActivationStatus.Refused,
            null,
            null,
            null,
            diagnostic);
    }

    public static EquipmentTargetActivationState Request(
        EquipmentTargetActivationState state,
        ISavedGearsetActivationRuntime runtime,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtime);
        if (state.Status != EquipmentTargetActivationStatus.Ready)
            throw new InvalidOperationException("Target activation is not ready for an explicit request.");
        if (nowUtc == default)
            throw new ArgumentOutOfRangeException(nameof(nowUtc));

        if (state.Target.Kind != EquipmentTargetActivationKind.SavedGearset)
            return state with
            {
                Status = EquipmentTargetActivationStatus.AwaitingFreshProof,
                RequestedAtUtc = nowUtc,
                NativeResult = null,
                ProvenEquipmentGeneration = null,
                Diagnostic = "Fresh exact target evidence is required before any slot move.",
            };

        var expected = ExpectedGearset(state.Target);
        if (!runtime.TryReadSavedGearset(expected.GearsetId, out var observed, out var diagnostic) || observed is null)
            return Refuse(state, string.IsNullOrWhiteSpace(diagnostic) ? "The saved gearset is unavailable." : diagnostic);
        if (observed != expected)
            return Refuse(state, "The saved gearset ID, name, or class/job changed before activation.");

        try
        {
            var nativeResult = runtime.ActivateSavedGearset(expected.GearsetId);
            return state with
            {
                Status = EquipmentTargetActivationStatus.AwaitingFreshProof,
                RequestedAtUtc = nowUtc,
                NativeResult = nativeResult,
                ProvenEquipmentGeneration = null,
                Diagnostic = "Gearset activation was submitted; fresh active-player evidence is required.",
            };
        }
        catch (Exception exception)
        {
            return Refuse(state, $"Gearset activation failed before proof could be observed: {exception.Message}");
        }
    }

    public static EquipmentTargetActivationState AcceptActivePlayerProof(
        EquipmentTargetActivationState state,
        ActivePlayerActivationProof proof,
        DateTimeOffset nowUtc,
        TimeSpan maximumProofAge)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(proof);
        if (state.Status != EquipmentTargetActivationStatus.AwaitingFreshProof)
            throw new InvalidOperationException("Target activation is not awaiting fresh proof.");
        if (state.Target.Kind == EquipmentTargetActivationKind.Retainer)
            return Refuse(state, "A retainer target requires rendered retainer evidence, not active-player proof.");

        var error = ValidateCommonActiveProof(state, proof, nowUtc, maximumProofAge);
        if (error is null && state.Target.Kind == EquipmentTargetActivationKind.SavedGearset &&
            proof.CurrentGearset != ExpectedGearset(state.Target))
        {
            error = "The current gearset ID, name, or class/job does not match the requested saved gearset.";
        }
        if (error is not null)
            return Refuse(state, error);

        return state with
        {
            Status = EquipmentTargetActivationStatus.Proven,
            ProvenEquipmentGeneration = proof.EquipmentGeneration,
            Diagnostic = "Fresh active-player identity, gearset, and equipment baseline prove the target transition.",
        };
    }

    public static EquipmentTargetActivationState AcceptRetainerProof(
        EquipmentTargetActivationState state,
        RenderedRetainerEquipmentEvidence evidence,
        DateTimeOffset nowUtc,
        TimeSpan maximumProofAge)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evidence);
        if (state.Status != EquipmentTargetActivationStatus.AwaitingFreshProof)
            throw new InvalidOperationException("Target activation is not awaiting fresh proof.");
        if (state.Target.Kind != EquipmentTargetActivationKind.Retainer)
            return Refuse(state, "Rendered retainer evidence cannot authorize an active-player target.");
        if (maximumProofAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumProofAge));

        var target = state.Target;
        var error = evidence.Status != RenderedRetainerEquipmentEvidenceStatus.Complete
            ? "Rendered retainer equipment evidence is incomplete."
            : !string.Equals(evidence.TargetKey, target.TargetKey, StringComparison.Ordinal) ||
              !Same(evidence.OwnerCharacterName, target.Owner.CharacterName) ||
              !Same(evidence.OwnerHomeWorld, target.OwnerHomeWorldName) ||
              !Same(evidence.RetainerName, target.RetainerName) ||
              evidence.ClassJobId != target.ClassJobId
                ? "Rendered retainer owner, target, name, or class/job does not match the requested target."
                : !IsFresh(evidence.CapturedAtUtc, state.RequestedAtUtc, nowUtc, maximumProofAge)
                    ? "Rendered retainer evidence is not a fresh post-request observation."
                    : !HasEveryCanonicalRetainerSlot(evidence)
                        ? "Rendered retainer proof does not contain every canonical equipment slot."
                        : null;
        if (error is not null)
            return Refuse(state, error);

        return state with
        {
            Status = EquipmentTargetActivationStatus.Proven,
            ProvenEquipmentGeneration = null,
            Diagnostic = "Fresh rendered retainer identity and every equipment slot prove the selected target.",
        };
    }

    public static EquipmentTargetActivationState Interrupt(
        EquipmentTargetActivationState state,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (state.Status != EquipmentTargetActivationStatus.AwaitingFreshProof)
            return state;
        return state with
        {
            Status = EquipmentTargetActivationStatus.Interrupted,
            Diagnostic = reason,
            ProvenEquipmentGeneration = null,
        };
    }

    /// <summary>
    /// Restores an interrupted persisted proof wait without invoking activation again. A new exact
    /// proof remains mandatory, so reload cannot duplicate the native action or unlock slot moves.
    /// </summary>
    public static EquipmentTargetActivationState ResumeProofWaitAfterReload(
        EquipmentTargetActivationState state,
        DateTimeOffset resumedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status != EquipmentTargetActivationStatus.Interrupted)
            return state;
        if (resumedAtUtc == default)
            throw new ArgumentOutOfRangeException(nameof(resumedAtUtc));
        return state with
        {
            Status = EquipmentTargetActivationStatus.AwaitingFreshProof,
            RequestedAtUtc = resumedAtUtc,
            NativeResult = null,
            ProvenEquipmentGeneration = null,
            Diagnostic = "Activation proof wait resumed after reload; no activation was replayed.",
        };
    }

    private static string? ValidateCommonActiveProof(
        EquipmentTargetActivationState state,
        ActivePlayerActivationProof proof,
        DateTimeOffset nowUtc,
        TimeSpan maximumProofAge)
    {
        if (maximumProofAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumProofAge));
        if (!SameOwner(state.Target.Owner, proof.Owner))
            return "The active character owner changed before activation proof.";
        if (proof.ClassJobId == 0 || proof.ClassJobId != state.Target.ClassJobId)
            return "The active class/job does not match the requested target.";
        if (!proof.BaselineCompleteAndConsistent || proof.BaselineTargetKind != PlayerAdvisorBaselineTargetKind.ActiveLoadout)
            return "Activation proof requires one complete active-player baseline.";
        if (proof.EquipmentGeneration == Guid.Empty)
            return "Activation proof has no equipment generation.";
        if (!IsFresh(proof.BaselineCompletedAtUtc, state.RequestedAtUtc, nowUtc, maximumProofAge))
            return "The active-player baseline is not a fresh post-request observation.";
        return null;
    }

    private static bool IsFresh(
        DateTimeOffset observedAtUtc,
        DateTimeOffset? requestedAtUtc,
        DateTimeOffset nowUtc,
        TimeSpan maximumProofAge) =>
        requestedAtUtc is { } requested &&
        observedAtUtc > requested &&
        observedAtUtc <= nowUtc &&
        nowUtc - observedAtUtc <= maximumProofAge;

    private static SavedGearsetActivationIdentity ExpectedGearset(EquipmentTargetActivationTarget target) =>
        new(target.GearsetId!.Value, target.GearsetName!, target.ClassJobId);

    private static string? ValidateTarget(EquipmentTargetActivationTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.TargetKey) ||
            target.Owner.LocalContentId == 0 ||
            target.Owner.HomeWorldId == 0 ||
            string.IsNullOrWhiteSpace(target.Owner.CharacterName) ||
            target.ClassJobId == 0)
            return "Equipment activation target identity is incomplete.";
        return target.Kind switch
        {
            EquipmentTargetActivationKind.ActivePlayer when
                target.GearsetId is null && target.RetainerId is null => null,
            EquipmentTargetActivationKind.SavedGearset when
                target.GearsetId is >= 0 and < 100 &&
                !string.IsNullOrWhiteSpace(target.GearsetName) &&
                target.RetainerId is null => null,
            EquipmentTargetActivationKind.Retainer when
                target.RetainerId is > 0 &&
                !string.IsNullOrWhiteSpace(target.RetainerName) &&
                !string.IsNullOrWhiteSpace(target.OwnerHomeWorldName) &&
                target.GearsetId is null => null,
            _ => "Equipment activation target fields contradict its authority kind.",
        };
    }

    private static bool SameOwner(PortfolioEquipmentMoveOwner left, PortfolioEquipmentMoveOwner right) =>
        left.LocalContentId == right.LocalContentId &&
        left.HomeWorldId == right.HomeWorldId &&
        Same(left.CharacterName, right.CharacterName);

    private static bool HasEveryCanonicalRetainerSlot(RenderedRetainerEquipmentEvidence evidence)
    {
        var expected = PlayerAdvisorEquippedSlotMap.All
            .Select(value => value.PositionKey)
            .ToHashSet(StringComparer.Ordinal);
        return evidence.Equipment.Count == expected.Count &&
            evidence.Equipment.Select(value => value.PositionKey).ToHashSet(StringComparer.Ordinal).SetEquals(expected) &&
            evidence.Equipment.All(value => value.Status switch
            {
                RenderedEquipmentSlotObservationStatus.Equipped =>
                    value.Item is { Status: RenderedItemDetailStatus.Complete },
                RenderedEquipmentSlotObservationStatus.Empty => value.Item is null,
                _ => false,
            });
    }

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static EquipmentTargetActivationState Refuse(
        EquipmentTargetActivationState state,
        string diagnostic) => state with
        {
            Status = EquipmentTargetActivationStatus.Refused,
            ProvenEquipmentGeneration = null,
            Diagnostic = diagnostic,
        };
}
