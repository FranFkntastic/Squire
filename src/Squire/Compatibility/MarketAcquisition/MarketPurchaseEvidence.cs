using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MarketMafioso.MarketAcquisition;

public static class MarketPurchaseCatalogId
{
    public static uint Normalize(uint rawCatalogId) =>
        rawCatalogId >= 1_000_000 ? rawCatalogId % 1_000_000 : rawCatalogId;
}

public sealed record MarketPurchasePacketPosition
{
    public required string Epoch { get; init; }
    public required long Sequence { get; init; }

    public bool IsAfter(MarketPurchasePacketPosition floor) =>
        Epoch.Equals(floor.Epoch, StringComparison.Ordinal) && Sequence > floor.Sequence;
}

public sealed record MarketPurchasePacketObservation
{
    public required MarketPurchasePacketPosition Position { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public required uint RawCatalogId { get; init; }
    public required uint ItemId { get; init; }
    public required bool IsHighQuality { get; init; }
    public required uint Quantity { get; init; }
}

public interface IMarketPurchasePacketEvidenceQueue
{
    bool IsAvailable { get; }
    MarketPurchasePacketPosition CurrentPosition { get; }
    MarketPurchasePacketPosition ReserveIntentFloor();
    void ActivateIntentFloor(MarketPurchasePacketPosition floor);
    MarketPurchasePacketObservation Enqueue(
        DateTimeOffset observedAtUtc,
        uint rawCatalogId,
        bool isHighQuality,
        uint quantity);
    bool TryPeek(out MarketPurchasePacketObservation observation);
    bool TryDequeue(out MarketPurchasePacketObservation observation);
}

/// <summary>Thread-safe callback boundary. Packet hooks should only call <see cref="Enqueue"/> and return.</summary>
public sealed class MarketPurchasePacketEvidenceQueue : IMarketPurchasePacketEvidenceQueue
{
    private readonly ConcurrentQueue<MarketPurchasePacketObservation> queue = new();
    private readonly string processEpoch;
    private EpochCounter activeEpoch;
    private long generation;

    public MarketPurchasePacketEvidenceQueue(bool isAvailable, string? epoch = null)
    {
        IsAvailable = isAvailable;
        processEpoch = string.IsNullOrWhiteSpace(epoch) ? Guid.NewGuid().ToString("N") : epoch;
        activeEpoch = new EpochCounter($"{processEpoch}/0");
    }

    public bool IsAvailable { get; }
    public MarketPurchasePacketPosition CurrentPosition
    {
        get
        {
            var current = Volatile.Read(ref activeEpoch);
            return new MarketPurchasePacketPosition
            {
                Epoch = current.Id,
                Sequence = Interlocked.Read(ref current.Sequence),
            };
        }
    }

    public MarketPurchasePacketPosition ReserveIntentFloor() => new()
    {
        Epoch = $"{processEpoch}/{Interlocked.Increment(ref generation)}",
        Sequence = 0,
    };

    public void ActivateIntentFloor(MarketPurchasePacketPosition floor)
    {
        ArgumentNullException.ThrowIfNull(floor);
        if (floor.Sequence != 0 || !floor.Epoch.StartsWith($"{processEpoch}/", StringComparison.Ordinal))
            throw new ArgumentException("The packet floor was not reserved by this evidence queue.", nameof(floor));
        Interlocked.Exchange(ref activeEpoch, new EpochCounter(floor.Epoch));
    }

    public MarketPurchasePacketObservation Enqueue(
        DateTimeOffset observedAtUtc,
        uint rawCatalogId,
        bool isHighQuality,
        uint quantity)
    {
        var current = Volatile.Read(ref activeEpoch);
        var observation = new MarketPurchasePacketObservation
        {
            Position = new MarketPurchasePacketPosition
            {
                Epoch = current.Id,
                Sequence = Interlocked.Increment(ref current.Sequence),
            },
            ObservedAtUtc = observedAtUtc,
            RawCatalogId = rawCatalogId,
            ItemId = MarketPurchaseCatalogId.Normalize(rawCatalogId),
            IsHighQuality = isHighQuality,
            Quantity = quantity,
        };
        queue.Enqueue(observation);
        return observation;
    }

    public bool TryPeek(out MarketPurchasePacketObservation observation) => queue.TryPeek(out observation!);
    public bool TryDequeue(out MarketPurchasePacketObservation observation) => queue.TryDequeue(out observation!);

    private sealed class EpochCounter(string id)
    {
        public string Id { get; } = id;
        public long Sequence;
    }
}

public record MarketPurchaseIntentDraft
{
    public required string IntentId { get; init; }
    public required string RouteId { get; init; }
    public required string RouteRunId { get; init; }
    public required string AttemptId { get; init; }
    public required string LineId { get; init; }
    public required uint ItemId { get; init; }
    public required bool IsHighQuality { get; init; }
    public required uint Quantity { get; init; }
    public required string ListingId { get; init; }
    public string? RetainerId { get; init; }
    public required uint UnitPrice { get; init; }
    public required uint TotalGil { get; init; }
    public required uint WorldId { get; init; }
    public required string WorldName { get; init; }
    public required DateTimeOffset ArmedAtUtc { get; init; }
    public required DateTimeOffset DeadlineUtc { get; init; }
}

public sealed record MarketPurchaseIntentContext
{
    public required string RouteId { get; init; }
    public required string RouteRunId { get; init; }
    public required string AttemptId { get; init; }
    public required string LineId { get; init; }
    public required TimeSpan EvidenceTimeout { get; init; }
}

public sealed record MarketPurchaseIntent : MarketPurchaseIntentDraft
{
    public required MarketPurchasePacketPosition PacketFloor { get; init; }
}

public enum MarketPurchaseEvidenceStateKind
{
    Pending,
    Confirmed,
    TimedOutIndeterminate,
    ConflictingPacket,
}

public enum PendingMarketPurchasePhase
{
    ArmedBeforeConfirmation,
    ConfirmationSubmitted,
}

public abstract record MarketPurchaseEvidenceState(MarketPurchaseEvidenceStateKind Kind, MarketPurchaseIntent Intent);
public sealed record PendingMarketPurchase(
    MarketPurchaseIntent PendingIntent,
    PendingMarketPurchasePhase Phase = PendingMarketPurchasePhase.ArmedBeforeConfirmation,
    DateTimeOffset? ConfirmationSubmittedAtUtc = null)
    : MarketPurchaseEvidenceState(MarketPurchaseEvidenceStateKind.Pending, PendingIntent);
public sealed record ConfirmedMarketPurchase(MarketPurchaseIntent ConfirmedIntent, MarketPurchasePacketObservation Evidence)
    : MarketPurchaseEvidenceState(MarketPurchaseEvidenceStateKind.Confirmed, ConfirmedIntent);
public sealed record TimedOutIndeterminateMarketPurchase(MarketPurchaseIntent TimedOutIntent, DateTimeOffset TimedOutAtUtc)
    : MarketPurchaseEvidenceState(MarketPurchaseEvidenceStateKind.TimedOutIndeterminate, TimedOutIntent);
public sealed record ConflictingMarketPurchasePacket(MarketPurchaseIntent ConflictingIntent, MarketPurchasePacketObservation Evidence)
    : MarketPurchaseEvidenceState(MarketPurchaseEvidenceStateKind.ConflictingPacket, ConflictingIntent);

public enum MarketPurchaseTerminalDisposition
{
    AppliedExactlyOnce,
    ManuallyReconciled,
}

public sealed record MarketPurchaseEvidenceHistoryEntry(
    MarketPurchaseEvidenceState TerminalState,
    MarketPurchaseTerminalDisposition Disposition,
    DateTimeOffset ResolvedAtUtc,
    string Resolution);

public sealed record MarketPurchaseEvidenceSnapshot
{
    public long Revision { get; init; }
    public MarketPurchaseEvidenceState? State { get; init; }
    public IReadOnlyList<MarketPurchasePacketObservation> Observations { get; init; } = [];
    public IReadOnlyList<MarketPurchaseEvidenceHistoryEntry> History { get; init; } = [];
}

public interface IMarketPurchaseEvidenceStateStore
{
    MarketPurchaseEvidenceSnapshot? Load();
    void Save(MarketPurchaseEvidenceSnapshot snapshot);
}

public enum MarketPurchaseIntentArmStatus
{
    Armed,
    EvidenceUnavailable,
    PendingIntentExists,
    TerminalEvidenceRequiresReconciliation,
    InvalidIntent,
    PersistenceFailed,
    EvidenceQueueActivationFailed,
}

public sealed record MarketPurchaseIntentArmResult(
    MarketPurchaseIntentArmStatus Status,
    string Message,
    MarketPurchaseIntent? Intent = null)
{
    public bool IsArmed => Status == MarketPurchaseIntentArmStatus.Armed;
}

public enum MarketPurchaseSubmissionStatus
{
    Recorded,
    NoPendingIntent,
    IntentMismatch,
    AlreadyRecorded,
    InvalidSubmissionTime,
    PersistenceFailed,
}

public sealed record MarketPurchaseSubmissionResult(MarketPurchaseSubmissionStatus Status, string Message)
{
    public bool IsRecorded => Status is MarketPurchaseSubmissionStatus.Recorded or MarketPurchaseSubmissionStatus.AlreadyRecorded;
}

public enum MarketPurchaseEvidenceAdvanceStatus
{
    Applied,
    NoChange,
    PersistenceFailed,
}

public sealed record MarketPurchaseEvidenceAdvanceResult(
    MarketPurchaseEvidenceAdvanceStatus Status,
    int DequeuedObservationCount,
    MarketPurchaseEvidenceState? State,
    string Message);

public enum MarketPurchaseTerminalResolutionStatus
{
    Resolved,
    NoTerminalEvidence,
    IntentMismatch,
    InvalidDisposition,
    PersistenceFailed,
}

public sealed record MarketPurchaseTerminalResolutionResult(
    MarketPurchaseTerminalResolutionStatus Status,
    string Message)
{
    public bool IsResolved => Status == MarketPurchaseTerminalResolutionStatus.Resolved;
}

public sealed class MarketPurchaseEvidenceCoordinator
{
    public const int MaxObservationHistory = 128;
    public const int MaxResolvedAttemptHistory = 64;

    private readonly IMarketPurchaseEvidenceStateStore store;
    private readonly IMarketPurchasePacketEvidenceQueue packetQueue;
    private readonly int frameworkThreadId;
    private MarketPurchaseEvidenceSnapshot snapshot;

    public MarketPurchaseEvidenceCoordinator(
        IMarketPurchaseEvidenceStateStore store,
        IMarketPurchasePacketEvidenceQueue packetQueue)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.packetQueue = packetQueue ?? throw new ArgumentNullException(nameof(packetQueue));
        frameworkThreadId = Environment.CurrentManagedThreadId;
        snapshot = Clone(store.Load() ?? new MarketPurchaseEvidenceSnapshot());
    }

    public bool IsAvailable => packetQueue.IsAvailable;
    public MarketPurchaseEvidenceState? State => snapshot.State;
    public MarketPurchaseEvidenceSnapshot Snapshot() => Clone(snapshot);

    public MarketPurchaseIntentArmResult TryArm(MarketPurchaseIntentDraft draft)
    {
        EnsureFrameworkThread();
        var validation = Validate(draft);
        if (validation != null)
            return new(MarketPurchaseIntentArmStatus.InvalidIntent, validation);
        if (!packetQueue.IsAvailable)
            return new(MarketPurchaseIntentArmStatus.EvidenceUnavailable, "Server purchase evidence is unavailable; confirmation is blocked.");
        if (snapshot.State is PendingMarketPurchase)
            return new(MarketPurchaseIntentArmStatus.PendingIntentExists, "Another purchase intent is already awaiting server evidence.");
        if (snapshot.State is not null)
            return new(MarketPurchaseIntentArmStatus.TerminalEvidenceRequiresReconciliation, "Terminal purchase evidence must be applied or reconciled before another purchase can be armed.");

        // A fresh epoch is persisted first, then activated. Packets racing the disk write stay in the prior epoch
        // without ever blocking the native callback on file I/O.
        var floor = packetQueue.ReserveIntentFloor();
        var intent = new MarketPurchaseIntent
        {
            IntentId = draft.IntentId,
            RouteId = draft.RouteId,
            RouteRunId = draft.RouteRunId,
            AttemptId = draft.AttemptId,
            LineId = draft.LineId,
            ItemId = draft.ItemId,
            IsHighQuality = draft.IsHighQuality,
            Quantity = draft.Quantity,
            ListingId = draft.ListingId,
            RetainerId = draft.RetainerId,
            UnitPrice = draft.UnitPrice,
            TotalGil = draft.TotalGil,
            WorldId = draft.WorldId,
            WorldName = draft.WorldName,
            ArmedAtUtc = draft.ArmedAtUtc,
            DeadlineUtc = draft.DeadlineUtc,
            PacketFloor = floor,
        };
        var next = snapshot with
        {
            Revision = snapshot.Revision + 1,
            State = new PendingMarketPurchase(intent),
        };
        try
        {
            store.Save(next);
            snapshot = next;
        }
        catch (Exception exception)
        {
            return new(MarketPurchaseIntentArmStatus.PersistenceFailed, $"Purchase intent persistence failed: {exception.Message}");
        }

        try
        {
            packetQueue.ActivateIntentFloor(floor);
            return new MarketPurchaseIntentArmResult(
                MarketPurchaseIntentArmStatus.Armed,
                "Exact purchase intent was durably armed before confirmation.",
                intent);
        }
        catch (Exception exception)
        {
            return new(MarketPurchaseIntentArmStatus.EvidenceQueueActivationFailed,
                $"Purchase intent is durable but its packet epoch could not be activated; confirmation is blocked: {exception.Message}",
                intent);
        }
    }

    public MarketPurchaseSubmissionResult MarkConfirmationSubmitted(string intentId, DateTimeOffset submittedAtUtc)
    {
        EnsureFrameworkThread();
        if (snapshot.State is not PendingMarketPurchase pending)
            return new(MarketPurchaseSubmissionStatus.NoPendingIntent, "No armed purchase intent can accept a confirmation submission.");
        if (!pending.Intent.IntentId.Equals(intentId, StringComparison.Ordinal))
            return new(MarketPurchaseSubmissionStatus.IntentMismatch, "The armed purchase intent has a different identity.");
        if (pending.Phase == PendingMarketPurchasePhase.ConfirmationSubmitted)
            return new(MarketPurchaseSubmissionStatus.AlreadyRecorded, "Purchase confirmation submission was already recorded.");
        if (submittedAtUtc < pending.Intent.ArmedAtUtc || submittedAtUtc > pending.Intent.DeadlineUtc)
            return new(MarketPurchaseSubmissionStatus.InvalidSubmissionTime, "Purchase confirmation was not submitted inside the armed evidence window.");

        var next = snapshot with
        {
            Revision = snapshot.Revision + 1,
            State = pending with
            {
                Phase = PendingMarketPurchasePhase.ConfirmationSubmitted,
                ConfirmationSubmittedAtUtc = submittedAtUtc,
            },
        };
        try
        {
            store.Save(next);
            snapshot = next;
            return new(MarketPurchaseSubmissionStatus.Recorded, "Purchase confirmation submission was durably recorded.");
        }
        catch (Exception exception)
        {
            return new(MarketPurchaseSubmissionStatus.PersistenceFailed, $"Purchase confirmation submission persistence failed: {exception.Message}");
        }
    }

    public MarketPurchaseEvidenceAdvanceResult AdvanceOnFrameworkThread(DateTimeOffset nowUtc)
    {
        EnsureFrameworkThread();
        var dequeued = 0;
        var changed = false;
        while (packetQueue.TryPeek(out var observation))
        {
            var next = Apply(snapshot, observation);
            if (!ReferenceEquals(next, snapshot))
            {
                try
                {
                    store.Save(next);
                }
                catch (Exception exception)
                {
                    return new(MarketPurchaseEvidenceAdvanceStatus.PersistenceFailed, dequeued, snapshot.State,
                        $"Packet evidence persistence failed before dequeue: {exception.Message}");
                }
                snapshot = next;
                changed = true;
            }

            if (!packetQueue.TryDequeue(out var removed) || removed.Position != observation.Position)
                throw new InvalidOperationException("Purchase packet evidence queue order changed during framework-thread application.");
            dequeued++;
        }

        if (snapshot.State is PendingMarketPurchase pending && nowUtc > pending.Intent.DeadlineUtc)
        {
            var next = snapshot with
            {
                Revision = snapshot.Revision + 1,
                State = new TimedOutIndeterminateMarketPurchase(pending.Intent, pending.Intent.DeadlineUtc),
            };
            try
            {
                store.Save(next);
            }
            catch (Exception exception)
            {
                return new(MarketPurchaseEvidenceAdvanceStatus.PersistenceFailed, dequeued, snapshot.State,
                    $"Purchase timeout persistence failed: {exception.Message}");
            }
            snapshot = next;
            changed = true;
        }

        return new(
            changed ? MarketPurchaseEvidenceAdvanceStatus.Applied : MarketPurchaseEvidenceAdvanceStatus.NoChange,
            dequeued,
            snapshot.State,
            changed ? "Queued packet evidence and deadline were applied durably." : "No queued purchase evidence or due deadline changed state.");
    }

    public MarketPurchaseTerminalResolutionResult ResolveTerminal(
        string intentId,
        MarketPurchaseTerminalDisposition disposition,
        DateTimeOffset resolvedAtUtc,
        string resolution)
    {
        EnsureFrameworkThread();
        if (snapshot.State is null or PendingMarketPurchase)
            return new(MarketPurchaseTerminalResolutionStatus.NoTerminalEvidence, "No terminal purchase evidence is available to resolve.");
        if (!snapshot.State.Intent.IntentId.Equals(intentId, StringComparison.Ordinal))
            return new(MarketPurchaseTerminalResolutionStatus.IntentMismatch, "The terminal purchase evidence belongs to a different intent.");
        if (disposition == MarketPurchaseTerminalDisposition.AppliedExactlyOnce && snapshot.State is not ConfirmedMarketPurchase)
            return new(MarketPurchaseTerminalResolutionStatus.InvalidDisposition, "Only confirmed packet evidence can be marked applied.");
        if (string.IsNullOrWhiteSpace(resolution))
            return new(MarketPurchaseTerminalResolutionStatus.InvalidDisposition, "A durable terminal resolution is required.");

        var history = snapshot.History.Append(new MarketPurchaseEvidenceHistoryEntry(
            snapshot.State,
            disposition,
            resolvedAtUtc,
            resolution)).TakeLast(MaxResolvedAttemptHistory).ToArray();
        var next = snapshot with
        {
            Revision = snapshot.Revision + 1,
            State = null,
            History = history,
        };
        try
        {
            store.Save(next);
            snapshot = next;
            return new(MarketPurchaseTerminalResolutionStatus.Resolved, "Terminal purchase evidence was durably resolved.");
        }
        catch (Exception exception)
        {
            return new(MarketPurchaseTerminalResolutionStatus.PersistenceFailed, $"Terminal evidence resolution persistence failed: {exception.Message}");
        }
    }

    private static MarketPurchaseEvidenceSnapshot Apply(
        MarketPurchaseEvidenceSnapshot current,
        MarketPurchasePacketObservation observation)
    {
        if (current.Observations.Any(existing => existing.Position == observation.Position))
            return current;

        var observations = current.Observations.Append(observation).TakeLast(MaxObservationHistory).ToArray();
        MarketPurchaseEvidenceState? state = current.State;
        if (state is PendingMarketPurchase pending && observation.Position.IsAfter(pending.Intent.PacketFloor))
        {
            if (observation.ObservedAtUtc > pending.Intent.DeadlineUtc)
            {
                state = new TimedOutIndeterminateMarketPurchase(pending.Intent, pending.Intent.DeadlineUtc);
            }
            else if (observation.ObservedAtUtc < pending.Intent.ArmedAtUtc ||
                     pending.Phase != PendingMarketPurchasePhase.ConfirmationSubmitted)
            {
                state = new ConflictingMarketPurchasePacket(pending.Intent, observation);
            }
            else
            {
                var matches = observation.ItemId == pending.Intent.ItemId &&
                              observation.IsHighQuality == pending.Intent.IsHighQuality &&
                              observation.Quantity == pending.Intent.Quantity;
                state = matches
                    ? new ConfirmedMarketPurchase(pending.Intent, observation)
                    : new ConflictingMarketPurchasePacket(pending.Intent, observation);
            }
        }

        return current with
        {
            Revision = current.Revision + 1,
            State = state,
            Observations = observations,
        };
    }

    private void EnsureFrameworkThread()
    {
        if (Environment.CurrentManagedThreadId != frameworkThreadId)
            throw new InvalidOperationException("Purchase evidence state may only be armed, applied, or resolved on its framework thread.");
    }

    private static string? Validate(MarketPurchaseIntentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.IntentId) || string.IsNullOrWhiteSpace(draft.RouteId) ||
            string.IsNullOrWhiteSpace(draft.RouteRunId) || string.IsNullOrWhiteSpace(draft.AttemptId) ||
            string.IsNullOrWhiteSpace(draft.LineId) || string.IsNullOrWhiteSpace(draft.ListingId) ||
            string.IsNullOrWhiteSpace(draft.WorldName))
            return "Purchase intent route, attempt, line, listing, and world identities are required.";
        if (draft.ItemId == 0 || draft.Quantity == 0 || draft.UnitPrice == 0 || draft.WorldId == 0)
            return "Purchase intent item, quantity, unit price, and world must be non-zero.";
        if ((ulong)draft.UnitPrice * draft.Quantity != draft.TotalGil)
            return "Purchase intent total gil must exactly equal unit price times quantity.";
        if (draft.DeadlineUtc <= draft.ArmedAtUtc)
            return "Purchase intent deadline must follow its armed time.";
        return null;
    }

    private static MarketPurchaseEvidenceSnapshot Clone(MarketPurchaseEvidenceSnapshot value) => value with
    {
        Observations = value.Observations.ToArray(),
        History = value.History.ToArray(),
    };
}
