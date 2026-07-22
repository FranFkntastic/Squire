using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Franthropy.Dalamud.Persistence;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketPurchaseEvidenceFileStore : IMarketPurchaseEvidenceStateStore
{
    private const int CurrentVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly string backupPath;

    public MarketPurchaseEvidenceFileStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A purchase evidence state path is required.", nameof(path));
        this.path = path;
        backupPath = path + ".bak";
    }

    public MarketPurchaseEvidenceSnapshot? Load() => LoadCandidate(path) ?? LoadCandidate(backupPath);

    public void Save(MarketPurchaseEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (File.Exists(path))
            File.Copy(path, backupPath, overwrite: true);
        AtomicJsonFile.Write(path, ToDocument(snapshot), JsonOptions);
    }

    private static MarketPurchaseEvidenceSnapshot? LoadCandidate(string candidatePath)
    {
        if (!File.Exists(candidatePath))
            return null;
        try
        {
            var document = AtomicJsonFile.Read<Document>(candidatePath, JsonOptions)
                ?? throw new InvalidDataException("Purchase evidence state is empty.");
            return FromDocument(document);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static Document ToDocument(MarketPurchaseEvidenceSnapshot snapshot) => new()
    {
        Version = CurrentVersion,
        Revision = snapshot.Revision,
        State = snapshot.State is null ? null : ToStateDocument(snapshot.State),
        Observations = snapshot.Observations.ToList(),
        History = snapshot.History.Select(entry => new HistoryDocument
        {
            State = ToStateDocument(entry.TerminalState),
            Disposition = entry.Disposition,
            ResolvedAtUtc = entry.ResolvedAtUtc,
            Resolution = entry.Resolution,
        }).ToList(),
    };

    private static MarketPurchaseEvidenceSnapshot FromDocument(Document document)
    {
        if (document.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported purchase evidence state version {document.Version}.");
        if (document.Revision < 0 || document.Observations.Count > MarketPurchaseEvidenceCoordinator.MaxObservationHistory ||
            document.History.Count > MarketPurchaseEvidenceCoordinator.MaxResolvedAttemptHistory)
            throw new InvalidDataException("Purchase evidence state exceeds its bounded schema.");

        var state = document.State is null ? null : FromStateDocument(document.State);
        var history = document.History.Select(entry =>
        {
            if (string.IsNullOrWhiteSpace(entry.Resolution))
                throw new InvalidDataException("Purchase evidence history has no resolution.");
            var terminal = FromStateDocument(entry.State);
            if (terminal is PendingMarketPurchase)
                throw new InvalidDataException("Purchase evidence history contains a pending intent.");
            if (!Enum.IsDefined(entry.Disposition) ||
                entry.Disposition == MarketPurchaseTerminalDisposition.AppliedExactlyOnce && terminal is not ConfirmedMarketPurchase)
                throw new InvalidDataException("Purchase evidence history has an invalid terminal disposition.");
            return new MarketPurchaseEvidenceHistoryEntry(terminal, entry.Disposition, entry.ResolvedAtUtc, entry.Resolution);
        }).ToArray();
        return new MarketPurchaseEvidenceSnapshot
        {
            Revision = document.Revision,
            State = state,
            Observations = document.Observations.ToArray(),
            History = history,
        };
    }

    private static StateDocument ToStateDocument(MarketPurchaseEvidenceState state) => new()
    {
        Kind = state.Kind,
        Intent = state.Intent,
        Evidence = state is ConfirmedMarketPurchase confirmed ? confirmed.Evidence
            : state is ConflictingMarketPurchasePacket conflicting ? conflicting.Evidence : null,
        TimedOutAtUtc = state is TimedOutIndeterminateMarketPurchase timedOut ? timedOut.TimedOutAtUtc : null,
        PendingPhase = state is PendingMarketPurchase pending ? pending.Phase : null,
        ConfirmationSubmittedAtUtc = state is PendingMarketPurchase submitted ? submitted.ConfirmationSubmittedAtUtc : null,
    };

    private static MarketPurchaseEvidenceState FromStateDocument(StateDocument document)
    {
        if (document.Intent is null)
            throw new InvalidDataException("Purchase evidence state has no intent.");
        MarketPurchaseEvidenceState state = document.Kind switch
        {
            MarketPurchaseEvidenceStateKind.Pending => new PendingMarketPurchase(
                document.Intent,
                document.PendingPhase ?? PendingMarketPurchasePhase.ArmedBeforeConfirmation,
                document.ConfirmationSubmittedAtUtc),
            MarketPurchaseEvidenceStateKind.Confirmed when document.Evidence is not null =>
                new ConfirmedMarketPurchase(document.Intent, document.Evidence),
            MarketPurchaseEvidenceStateKind.TimedOutIndeterminate when document.TimedOutAtUtc is not null =>
                new TimedOutIndeterminateMarketPurchase(document.Intent, document.TimedOutAtUtc.Value),
            MarketPurchaseEvidenceStateKind.ConflictingPacket when document.Evidence is not null =>
                new ConflictingMarketPurchasePacket(document.Intent, document.Evidence),
            _ => throw new InvalidDataException("Purchase evidence state is incomplete."),
        };
        ValidateState(state);
        return state;
    }

    private static void ValidateState(MarketPurchaseEvidenceState state)
    {
        var intent = state.Intent;
        if (string.IsNullOrWhiteSpace(intent.IntentId) || string.IsNullOrWhiteSpace(intent.RouteId) ||
            string.IsNullOrWhiteSpace(intent.RouteRunId) || string.IsNullOrWhiteSpace(intent.AttemptId) ||
            string.IsNullOrWhiteSpace(intent.LineId) || string.IsNullOrWhiteSpace(intent.ListingId) ||
            string.IsNullOrWhiteSpace(intent.WorldName) || string.IsNullOrWhiteSpace(intent.PacketFloor.Epoch) ||
            intent.PacketFloor.Sequence < 0 || intent.ItemId == 0 || intent.Quantity == 0 ||
            intent.UnitPrice == 0 || intent.WorldId == 0 || (ulong)intent.UnitPrice * intent.Quantity != intent.TotalGil ||
            intent.DeadlineUtc <= intent.ArmedAtUtc)
            throw new InvalidDataException("Purchase evidence intent is invalid.");

        if (state is PendingMarketPurchase pending)
        {
            if (!Enum.IsDefined(pending.Phase) ||
                pending.Phase == PendingMarketPurchasePhase.ArmedBeforeConfirmation && pending.ConfirmationSubmittedAtUtc is not null ||
                pending.Phase == PendingMarketPurchasePhase.ConfirmationSubmitted &&
                (pending.ConfirmationSubmittedAtUtc is not DateTimeOffset submittedAtUtc ||
                 submittedAtUtc < intent.ArmedAtUtc || submittedAtUtc > intent.DeadlineUtc))
                throw new InvalidDataException("Pending purchase evidence has an invalid submission phase.");
            return;
        }

        if (state is TimedOutIndeterminateMarketPurchase timedOut)
        {
            if (timedOut.TimedOutAtUtc != intent.DeadlineUtc)
                throw new InvalidDataException("Indeterminate purchase evidence has an invalid deadline.");
            return;
        }

        var evidence = state switch
        {
            ConfirmedMarketPurchase confirmed => confirmed.Evidence,
            ConflictingMarketPurchasePacket conflicting => conflicting.Evidence,
            _ => throw new InvalidDataException("Purchase evidence state is unsupported."),
        };
        if (!evidence.Position.IsAfter(intent.PacketFloor))
            throw new InvalidDataException("Terminal packet evidence does not follow the intent floor.");
        if (state is ConfirmedMarketPurchase &&
            (evidence.ObservedAtUtc < intent.ArmedAtUtc || evidence.ObservedAtUtc > intent.DeadlineUtc ||
             evidence.ItemId != intent.ItemId || evidence.IsHighQuality != intent.IsHighQuality ||
             evidence.Quantity != intent.Quantity))
            throw new InvalidDataException("Confirmed packet evidence does not match its purchase intent.");
    }

    private sealed record Document
    {
        public int Version { get; init; }
        public long Revision { get; init; }
        public StateDocument? State { get; init; }
        public List<MarketPurchasePacketObservation> Observations { get; init; } = [];
        public List<HistoryDocument> History { get; init; } = [];
    }

    private sealed record StateDocument
    {
        public MarketPurchaseEvidenceStateKind Kind { get; init; }
        public MarketPurchaseIntent? Intent { get; init; }
        public MarketPurchasePacketObservation? Evidence { get; init; }
        public DateTimeOffset? TimedOutAtUtc { get; init; }
        public PendingMarketPurchasePhase? PendingPhase { get; init; }
        public DateTimeOffset? ConfirmationSubmittedAtUtc { get; init; }
    }

    private sealed record HistoryDocument
    {
        public StateDocument State { get; init; } = new();
        public MarketPurchaseTerminalDisposition Disposition { get; init; }
        public DateTimeOffset ResolvedAtUtc { get; init; }
        public string Resolution { get; init; } = string.Empty;
    }
}
