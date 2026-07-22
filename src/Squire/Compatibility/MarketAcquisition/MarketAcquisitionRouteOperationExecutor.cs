using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteOperationExecutor
{
    private readonly object sync = new();
    private readonly HashSet<string> issuedOperationIds = new(StringComparer.Ordinal);
    private MarketAcquisitionRouteOperationSnapshot? activeSnapshot;
    private MarketAcquisitionRouteOperationSnapshot? lastSnapshot;

    public MarketAcquisitionRouteOperationSnapshot? ActiveSnapshot
    {
        get
        {
            lock (sync)
                return activeSnapshot;
        }
    }

    public MarketAcquisitionRouteOperationSnapshot? LastSnapshot
    {
        get
        {
            lock (sync)
                return lastSnapshot;
        }
        private set => lastSnapshot = value;
    }

    public MarketAcquisitionRouteOperationSnapshot Begin(MarketAcquisitionRouteOperationStart operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.OperationId);
        if (!Enum.IsDefined(operation.Kind))
            throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, "Operation kind is invalid.");
        if (operation.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(operation), operation.Timeout, "Operation timeout must be positive.");
        if (!IsValidTimeoutDisposition(operation.TimeoutDisposition))
            throw new ArgumentOutOfRangeException(nameof(operation), operation.TimeoutDisposition, "Operation timeout disposition is invalid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.TimeoutMessage);
        if (operation.Attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(operation), operation.Attempt, "Operation attempt must be at least one.");

        var timeoutMilliseconds = checked((long)Math.Ceiling(operation.Timeout.TotalMilliseconds));
        var deadlineUtc = operation.StartedAtUtc.Add(operation.Timeout);
        var deadlineMonotonicMilliseconds = checked(operation.StartedAtMonotonicMilliseconds + timeoutMilliseconds);
        var context = CopyDetails(operation.Context);

        lock (sync)
        {
            if (activeSnapshot != null)
                throw new InvalidOperationException($"Operation {activeSnapshot.OperationId} is already active.");
            if (!issuedOperationIds.Add(operation.OperationId))
                throw new InvalidOperationException($"Operation ID {operation.OperationId} has already been issued.");

            activeSnapshot = new MarketAcquisitionRouteOperationSnapshot
            {
                OperationId = operation.OperationId,
                Kind = operation.Kind,
                Phase = MarketAcquisitionRouteOperationPhase.Started,
                Disposition = MarketAcquisitionRouteOperationDisposition.Pending,
                TimeoutDisposition = operation.TimeoutDisposition,
                TimeoutMessage = operation.TimeoutMessage,
                Attempt = operation.Attempt,
                StartedAtUtc = operation.StartedAtUtc,
                DeadlineUtc = deadlineUtc,
                StartedAtMonotonicMilliseconds = operation.StartedAtMonotonicMilliseconds,
                DeadlineMonotonicMilliseconds = deadlineMonotonicMilliseconds,
                UpdatedAtMonotonicMilliseconds = operation.StartedAtMonotonicMilliseconds,
                UpdatedAtUtc = operation.StartedAtUtc,
                Message = $"{operation.Kind} operation started.",
                Context = context,
                Details = EmptyDetails,
            };
            LastSnapshot = activeSnapshot;
            return activeSnapshot;
        }
    }

    public MarketAcquisitionRouteOperationApplyResult Observe(
        MarketAcquisitionRouteOperationObservation observation,
        DateTimeOffset observedAtUtc,
        long observedAtMonotonicMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.Message);

        lock (sync)
        {
            if (activeSnapshot == null ||
                !string.Equals(activeSnapshot.OperationId, observation.OperationId, StringComparison.Ordinal))
            {
                return RejectedLateOrMismatched(observation.OperationId);
            }

            EnsureMonotonicTimeDoesNotMoveBackward(activeSnapshot, observedAtMonotonicMilliseconds);
            if (observedAtMonotonicMilliseconds >= activeSnapshot.DeadlineMonotonicMilliseconds)
                return CompleteTimedOutUnsafe(activeSnapshot, observedAtUtc, observedAtMonotonicMilliseconds);

            if (!Enum.IsDefined(observation.Disposition))
            {
                return CompleteUnsafe(
                    activeSnapshot,
                    MarketAcquisitionRouteOperationPhase.Completed,
                    MarketAcquisitionRouteOperationDisposition.Failed,
                    observedAtUtc,
                    observedAtMonotonicMilliseconds,
                    $"Unsupported operation observation disposition: {(int)observation.Disposition}.",
                    observation.Details);
            }

            if (observation.Disposition == MarketAcquisitionRouteOperationDisposition.Pending)
            {
                activeSnapshot = activeSnapshot with
                {
                    Phase = MarketAcquisitionRouteOperationPhase.Waiting,
                    UpdatedAtUtc = observedAtUtc,
                    UpdatedAtMonotonicMilliseconds = observedAtMonotonicMilliseconds,
                    Message = observation.Message,
                    Details = CopyDetails(observation.Details),
                };
                LastSnapshot = activeSnapshot;
                return Accepted(activeSnapshot);
            }

            if (observation.Disposition == MarketAcquisitionRouteOperationDisposition.Cancelled)
            {
                return CompleteUnsafe(
                    activeSnapshot,
                    MarketAcquisitionRouteOperationPhase.Cancelled,
                    MarketAcquisitionRouteOperationDisposition.Cancelled,
                    observedAtUtc,
                    observedAtMonotonicMilliseconds,
                    observation.Message,
                    observation.Details);
            }

            return CompleteUnsafe(
                activeSnapshot,
                MarketAcquisitionRouteOperationPhase.Completed,
                observation.Disposition,
                observedAtUtc,
                observedAtMonotonicMilliseconds,
                observation.Message,
                observation.Details);
        }
    }

    public MarketAcquisitionRouteOperationApplyResult CheckDeadline(
        DateTimeOffset observedAtUtc,
        long observedAtMonotonicMilliseconds)
    {
        lock (sync)
        {
            if (activeSnapshot == null)
                return RejectedNoActiveOperation();

            EnsureMonotonicTimeDoesNotMoveBackward(activeSnapshot, observedAtMonotonicMilliseconds);
            return observedAtMonotonicMilliseconds >= activeSnapshot.DeadlineMonotonicMilliseconds
                ? CompleteTimedOutUnsafe(activeSnapshot, observedAtUtc, observedAtMonotonicMilliseconds)
                : Accepted(activeSnapshot);
        }
    }

    public MarketAcquisitionRouteOperationApplyResult Cancel(
        DateTimeOffset cancelledAtUtc,
        long cancelledAtMonotonicMilliseconds,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        lock (sync)
        {
            if (activeSnapshot == null)
                return RejectedNoActiveOperation();

            EnsureMonotonicTimeDoesNotMoveBackward(activeSnapshot, cancelledAtMonotonicMilliseconds);
            return CompleteUnsafe(
                activeSnapshot,
                MarketAcquisitionRouteOperationPhase.Cancelled,
                MarketAcquisitionRouteOperationDisposition.Cancelled,
                cancelledAtUtc,
                cancelledAtMonotonicMilliseconds,
                message,
                EmptyDetails);
        }
    }

    private MarketAcquisitionRouteOperationApplyResult CompleteTimedOutUnsafe(
        MarketAcquisitionRouteOperationSnapshot snapshot,
        DateTimeOffset observedAtUtc,
        long observedAtMonotonicMilliseconds) =>
        CompleteUnsafe(
            snapshot,
            MarketAcquisitionRouteOperationPhase.TimedOut,
            snapshot.TimeoutDisposition,
            observedAtUtc,
            observedAtMonotonicMilliseconds,
            snapshot.TimeoutMessage,
            snapshot.Details);

    private MarketAcquisitionRouteOperationApplyResult CompleteUnsafe(
        MarketAcquisitionRouteOperationSnapshot snapshot,
        MarketAcquisitionRouteOperationPhase phase,
        MarketAcquisitionRouteOperationDisposition disposition,
        DateTimeOffset observedAtUtc,
        long observedAtMonotonicMilliseconds,
        string message,
        IReadOnlyDictionary<string, string?> details)
    {
        var completed = snapshot with
        {
            Phase = phase,
            Disposition = disposition,
            UpdatedAtUtc = observedAtUtc,
            UpdatedAtMonotonicMilliseconds = observedAtMonotonicMilliseconds,
            Message = message,
            Details = CopyDetails(details),
        };
        activeSnapshot = null;
        LastSnapshot = completed;
        return Accepted(completed);
    }

    private MarketAcquisitionRouteOperationApplyResult RejectedLateOrMismatched(string operationId) => new()
    {
        Accepted = false,
        IsLateOrMismatched = true,
        Message = $"Observation for inactive or mismatched operation {operationId} was ignored.",
        Snapshot = lastSnapshot != null && string.Equals(lastSnapshot.OperationId, operationId, StringComparison.Ordinal)
            ? lastSnapshot
            : null,
    };

    private MarketAcquisitionRouteOperationApplyResult RejectedNoActiveOperation() => new()
    {
        Accepted = false,
        IsLateOrMismatched = false,
        Message = "No route operation is active.",
        Snapshot = LastSnapshot,
    };

    private static MarketAcquisitionRouteOperationApplyResult Accepted(
        MarketAcquisitionRouteOperationSnapshot snapshot) => new()
        {
            Accepted = true,
            IsLateOrMismatched = false,
            Message = snapshot.Message,
            Snapshot = snapshot,
        };

    private static void EnsureMonotonicTimeDoesNotMoveBackward(
        MarketAcquisitionRouteOperationSnapshot snapshot,
        long observedAtMonotonicMilliseconds)
    {
        if (observedAtMonotonicMilliseconds < snapshot.StartedAtMonotonicMilliseconds ||
            observedAtMonotonicMilliseconds < snapshot.UpdatedAtMonotonicMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedAtMonotonicMilliseconds),
                observedAtMonotonicMilliseconds,
                "Operation monotonic time cannot move backward.");
        }
    }

    private static bool IsValidTimeoutDisposition(MarketAcquisitionRouteOperationDisposition disposition) =>
        disposition is MarketAcquisitionRouteOperationDisposition.RetryScheduled or
            MarketAcquisitionRouteOperationDisposition.SkippedItem or
            MarketAcquisitionRouteOperationDisposition.SkippedWorld or
            MarketAcquisitionRouteOperationDisposition.Failed or
            MarketAcquisitionRouteOperationDisposition.Indeterminate;

    private static IReadOnlyDictionary<string, string?> CopyDetails(
        IReadOnlyDictionary<string, string?> details) =>
        new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(details, StringComparer.Ordinal));

    private static readonly IReadOnlyDictionary<string, string?> EmptyDetails =
        new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>());
}
