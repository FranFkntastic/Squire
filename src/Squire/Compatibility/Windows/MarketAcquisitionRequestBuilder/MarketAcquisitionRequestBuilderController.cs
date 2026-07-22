using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.Acquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed class MarketAcquisitionRequestBuilderController
{
    private static readonly TimeSpan AutomaticSyncDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan AutomaticRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RemotePollInterval = TimeSpan.FromSeconds(2);
    private readonly Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest;
    private readonly Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest;
    private readonly Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted;
    private readonly Action<MarketAcquisitionRequestDocument> persistDocument;
    private bool syncRequested;
    private DateTimeOffset syncDueAtUtc = DateTimeOffset.MaxValue;
    private DateTimeOffset nextRemotePollAtUtc = DateTimeOffset.UtcNow;
    private Task refreshTask = Task.CompletedTask;
    private MarketAcquisitionRequestDocument? cachedIntentHashDocument;
    private string? cachedIntentHash;
    private MarketAcquisitionRequestDocument? cachedValidationDocument;
    private MarketAcquisitionRequestValidationResult? cachedValidation;

    public MarketAcquisitionRequestBuilderController(
        Configuration config,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest,
        Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted)
        : this(
            MarketAcquisitionRequestDocumentPersistence.Restore(config),
            syncRequest,
            refreshRequest,
            documentAdopted,
            CreatePersistence(config))
    {
    }

    internal MarketAcquisitionRequestBuilderController(
        MarketAcquisitionRequestDocument document,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest,
        Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted,
        Action<MarketAcquisitionRequestDocument> persistDocument)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        this.syncRequest = syncRequest ?? throw new ArgumentNullException(nameof(syncRequest));
        this.refreshRequest = refreshRequest ?? throw new ArgumentNullException(nameof(refreshRequest));
        this.documentAdopted = documentAdopted ?? throw new ArgumentNullException(nameof(documentAdopted));
        this.persistDocument = persistDocument ?? throw new ArgumentNullException(nameof(persistDocument));
        if (Document.Lines.Count > 0 &&
            !Document.SyncStatus.Equals("SyncedClean", StringComparison.OrdinalIgnoreCase))
        {
            RequestAutomaticSync();
        }
    }

    public MarketAcquisitionRequestDocument Document { get; private set; }

    public int SelectedLineIndex { get; private set; } = -1;

    public string Status { get; private set; } = "Request builder ready.";

    public bool IsSyncing { get; private set; }

    public bool IsRefreshing { get; private set; }

    public string CurrentIntentHash
    {
        get
        {
            if (!ReferenceEquals(cachedIntentHashDocument, Document))
            {
                cachedIntentHashDocument = Document;
                cachedIntentHash = MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(Document);
            }

            return cachedIntentHash!;
        }
    }

    public MarketAcquisitionRequestValidationResult DraftValidation
    {
        get
        {
            if (!ReferenceEquals(cachedValidationDocument, Document))
            {
                cachedValidationDocument = Document;
                cachedValidation = MarketAcquisitionRequestDocumentValidator.ValidateDraft(Document);
            }

            return cachedValidation!;
        }
    }

    public OutfitterWorkbenchAuthorityValidation OutfitterFinalizationValidation =>
        OutfitterWorkbenchAuthorityService.ValidateForFinalization(Document);

    public bool IsOutfitterLine(int index) =>
        OutfitterWorkbenchAuthorityService.IsAuthorityLine(Document, index);

    public void SetStatus(string status) => Status = status;

    public void MarkPlanPrepared(string planHash)
    {
        Document = Document with { LastPlanHash = planHash, UpdatedAtUtc = DateTimeOffset.UtcNow };
        SaveDocument();
    }

    public void AdoptRequest(MarketAcquisitionRequestView request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Document = AdoptRemoteDocument(Document, request);
        ResetSelection();
        Status = "Loaded request into builder.";
        SaveDocument();
        documentAdopted(Document, request);
    }

    public bool AdoptRestoredRequestIfSafe(MarketAcquisitionRequestView request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ShouldAdoptRestoredRequest(request))
            return false;

        Document = AdoptRemoteDocument(Document, request);
        ResetSelection();
        Status = "Loaded restored request into builder.";
        SaveDocument();
        return true;
    }

    public void EnsureCharacterScope(string characterName, string world)
    {
        if (string.Equals(Document.TargetCharacterName, characterName, StringComparison.Ordinal) &&
            string.Equals(Document.TargetWorld, world, StringComparison.Ordinal))
        {
            return;
        }

        var targetChanged = !string.IsNullOrWhiteSpace(Document.TargetCharacterName) &&
                            (!string.Equals(Document.TargetCharacterName, characterName, StringComparison.Ordinal) ||
                             !string.Equals(Document.TargetWorld, world, StringComparison.Ordinal));
        var previous = Document;
        Document = (Document with
        {
            TargetCharacterName = characterName,
            TargetWorld = world,
            RemoteRequestId = targetChanged ? null : Document.RemoteRequestId,
            RemoteRevision = targetChanged ? 0 : Document.RemoteRevision,
            RemoteOrigin = targetChanged ? null : Document.RemoteOrigin,
            LastSyncedHash = targetChanged ? null : Document.LastSyncedHash,
            RemoteHash = targetChanged ? null : Document.RemoteHash,
            SyncStatus = targetChanged ? "NewDraft" : Document.SyncStatus,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }).WithNextRevision(targetChanged || string.IsNullOrWhiteSpace(Document.RemoteRequestId) ? "NewDraft" : "LocalEdits");
        Document = OutfitterWorkbenchAuthorityService.ReconcileEdit(previous, Document);
        SaveDocument();
        RequestAutomaticSync();
    }

    public void PumpAutomaticSynchronization(
        string characterName,
        string world,
        bool canSynchronize,
        DateTimeOffset? nowUtc = null)
    {
        if (!canSynchronize || IsSyncing || IsRefreshing || Document.Lines.Count == 0)
            return;

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        if (syncRequested && now >= syncDueAtUtc)
        {
            syncRequested = false;
            _ = SyncAsync(characterName, world);
            return;
        }

        if (!syncRequested &&
            !string.IsNullOrWhiteSpace(Document.RemoteRequestId) &&
            Document.SyncStatus.Equals("SyncedClean", StringComparison.OrdinalIgnoreCase) &&
            now >= nextRemotePollAtUtc)
        {
            nextRemotePollAtUtc = now.Add(RemotePollInterval);
            refreshTask = RefreshAsync();
        }
    }

    public Task WaitForRefreshAsync() => refreshTask;

    public void UpdateRouteScope(RequestRouteScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CommitLocalEdit(RequestDocumentMutation.ApplyRouteScope(Document, scope));
    }

    public void StageOutfitterTransfer(OutfitterWorkbenchTransfer transfer)
    {
        Document = OutfitterWorkbenchAuthorityService.Stage(Document, transfer);
        ResetSelection();
        FinishLocalEdit("Selected exact-quality Squire solution added to the Workbench.");
    }

    public void UpdateOutfitterPriceFlex(int priceFlexPercent)
    {
        Document = OutfitterWorkbenchAuthorityService.UpdatePriceFlex(Document, priceFlexPercent);
        ResetSelection();
        FinishLocalEdit("Squire fixed gil ceilings updated.");
    }

    public bool FinalizeOutfitterAuthority()
    {
        var validation = OutfitterFinalizationValidation;
        if (!validation.IsValid)
        {
            Status = validation.Error ?? "Squire execution authority is incomplete.";
            return false;
        }

        Document = OutfitterWorkbenchAuthorityService.Finalize(Document);
        Status = Document.OutfitterAuthority?.Transfer.DryRunOnly == true
            ? "Diagnostic Squire contract confirmed for this Workbench revision; dry-run execution only."
            : "Squire execution contract confirmed for this Workbench revision.";
        SaveDocument();
        return true;
    }

    public bool SelectLine(int index)
    {
        if (index < 0 || index >= Document.Lines.Count)
            return false;

        SelectedLineIndex = index;
        return true;
    }

    public void ClearSelection() => SelectedLineIndex = -1;

    public bool SetLineMaxUnitPrice(int index, uint maxUnitPrice, string message)
    {
        if (!SelectLine(index))
            return false;

        CommitLocalEdit(RequestDocumentMutation.ApplyMaxUnitPrice(Document, index, maxUnitPrice), message);
        return true;
    }

    public bool SetLineGilCap(int index, uint gilCap, string message)
    {
        if (!SelectLine(index))
            return false;

        var line = Document.Lines[index];
        CommitLocalEdit(RequestDocumentMutation.ApplyPricing(Document, index, line.MaxUnitPrice, gilCap), message);
        return true;
    }

    public bool ApplyLineEdit(
        int index,
        string quantityMode,
        uint targetQuantity,
        uint maxQuantity,
        string hqPolicy,
        uint maxUnitPrice,
        uint gilCap,
        string message)
    {
        if (!SelectLine(index))
            return false;

        CommitLocalEdit(
            RequestDocumentMutation.ApplyLineEdit(
                Document,
                index,
                quantityMode,
                targetQuantity,
                maxQuantity,
                hqPolicy,
                maxUnitPrice,
                gilCap),
            message);
        return true;
    }

    public void ApplyEditorLine(MarketAcquisitionRequestLineDocument line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (SelectedLineIndex >= 0 && SelectedLineIndex < Document.Lines.Count)
        {
            CommitLocalEdit(RequestDocumentMutation.ReplaceLine(Document, SelectedLineIndex, line), "Work-order draft updated.");
            return;
        }

        var previous = Document;
        Document = RequestDocumentMutation.AddLine(Document, line);
        Document = OutfitterWorkbenchAuthorityService.ReconcileEdit(previous, Document);
        SelectedLineIndex = -1;
        FinishLocalEdit("Work-order draft updated.");
    }

    public int AddLines(IEnumerable<MarketAcquisitionRequestLineDocument> lines) =>
        AddLines(lines, "Outfitter");

    public int MergeComposition(MarketAcquisitionWorkbenchComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        return AddLines(composition.Lines, composition.Name);
    }

    public void LoadComposition(
        MarketAcquisitionWorkbenchComposition composition,
        string characterName,
        string world)
    {
        ArgumentNullException.ThrowIfNull(composition);
        Document = composition.CreateDocument(characterName, world);
        ResetSelection();
        Status = $"Loaded {composition.Name} as a new Workbench draft.";
        SaveDocument();
        RequestAutomaticSync();
    }

    private int AddLines(IEnumerable<MarketAcquisitionRequestLineDocument> lines, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var previous = Document;
        var added = 0;
        foreach (var line in lines)
        {
            if (line.ItemId == 0 || Document.Lines.Any(existing => existing.ItemId == line.ItemId))
                continue;
            Document = RequestDocumentMutation.AddLine(Document, line);
            added++;
        }
        if (added == 0)
        {
            Status = $"All {sourceLabel} items are already present in the Workbench.";
            return 0;
        }
        SelectedLineIndex = -1;
        Document = OutfitterWorkbenchAuthorityService.ReconcileEdit(previous, Document);
        FinishLocalEdit($"Added {added:N0} {sourceLabel} line{(added == 1 ? string.Empty : "s")}.");
        return added;
    }

    public bool RemoveLine(int index)
    {
        if (index < 0 || index >= Document.Lines.Count)
            return false;

        var previous = Document;
        Document = RequestDocumentMutation.RemoveLine(Document, index);
        Document = OutfitterWorkbenchAuthorityService.ReconcileEdit(previous, Document);
        SelectedLineIndex = -1;
        FinishLocalEdit("Line removed.");
        return true;
    }

    public int RemoveLinesByItemId(IEnumerable<uint> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var removedIds = itemIds.Where(itemId => itemId != 0).ToHashSet();
        if (removedIds.Count == 0)
            return 0;

        var remaining = Document.Lines.Where(line => !removedIds.Contains(line.ItemId)).ToList();
        var removed = Document.Lines.Count - remaining.Count;
        if (removed == 0)
        {
            Status = "The selected inbox items are not in the Workbench.";
            return 0;
        }

        var previous = Document;
        Document = Document with { Lines = remaining };
        Document = Document.WithNextRevision(string.IsNullOrWhiteSpace(Document.RemoteRequestId) ? "NewDraft" : "LocalEdits");
        Document = OutfitterWorkbenchAuthorityService.ReconcileEdit(previous, Document);
        SelectedLineIndex = -1;
        FinishLocalEdit($"Returned {removed:N0} item{(removed == 1 ? string.Empty : "s")} to the inbox selection.");
        return removed;
    }

    public async Task SyncAsync(string characterName, string world)
    {
        if (IsSyncing || IsRefreshing)
            return;

        if (!DraftValidation.IsValid)
        {
            syncRequested = false;
            syncDueAtUtc = DateTimeOffset.MaxValue;
            return;
        }

        IsSyncing = true;
        try
        {
            var operationDocument = Document;
            var scopedDocument = operationDocument with
            {
                TargetCharacterName = characterName,
                TargetWorld = world,
            };
            var outcome = await syncRequest(scopedDocument).ConfigureAwait(false);
            if (ReferenceEquals(Document, operationDocument))
            {
                Document = PreserveLocalAuthority(operationDocument, outcome.Document);
                Status = outcome.StatusMessage;
                syncRequested = false;
            }
            else
            {
                Document = MergeSyncMetadata(Document, outcome.Document);
                Status = $"{outcome.StatusMessage} A newer local edit is queued for synchronization.";
                RequestAutomaticSync();
            }

            nextRemotePollAtUtc = DateTimeOffset.UtcNow.Add(RemotePollInterval);
            SaveDocument();
        }
        catch (Exception ex)
        {
            Document = Document with { SyncStatus = "SyncFailed", UpdatedAtUtc = DateTimeOffset.UtcNow };
            Status = $"Synchronization failed; retrying automatically. {ex.Message}";
            RequestAutomaticSync(AutomaticRetryDelay);
            SaveDocument();
        }
        finally
        {
            IsSyncing = false;
        }
    }

    public async Task RefreshAsync()
    {
        if (IsRefreshing || IsSyncing)
            return;

        IsRefreshing = true;
        try
        {
            var operationDocument = Document;
            var outcome = await refreshRequest(operationDocument).ConfigureAwait(false);
            if (ReferenceEquals(Document, operationDocument))
            {
                Status = outcome.StatusMessage;
                if (!HasSameRemoteState(operationDocument, outcome.Document))
                {
                    Document = PreserveLocalIdentity(operationDocument, outcome.Document);
                    ResetSelection();
                    documentAdopted(Document, outcome.RemoteRequest);
                    SaveDocument();
                }
            }
            else
            {
                Status = "A newer local edit superseded the server refresh and is queued for synchronization.";
                RequestAutomaticSync();
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Status = "The server copy is missing; republishing the current work-order draft automatically.";
            RequestAutomaticSync(TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            Status = $"Server refresh failed; the local draft remains available. {ex.Message}";
            RequestAutomaticSync(AutomaticRetryDelay);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void ClearDraft(string characterName, string world)
    {
        Document = MarketAcquisitionRequestDocument.CreateDefault(characterName, world);
        ResetSelection();
        Status = "Work-order draft cleared.";
        SaveDocument();
    }

    private void CommitLocalEdit(MarketAcquisitionRequestDocument updated, string? message = null)
    {
        Document = OutfitterWorkbenchAuthorityService.ReconcileEdit(Document, updated);
        FinishLocalEdit(message);
    }

    private void FinishLocalEdit(string? message)
    {
        if (message is not null)
            Status = message;
        SaveDocument();
        RequestAutomaticSync();
    }

    private void RequestAutomaticSync(TimeSpan? delay = null)
    {
        if (!DraftValidation.IsValid)
        {
            syncRequested = false;
            syncDueAtUtc = DateTimeOffset.MaxValue;
            return;
        }

        syncRequested = true;
        syncDueAtUtc = DateTimeOffset.UtcNow.Add(delay ?? AutomaticSyncDelay);
    }

    private void ResetSelection() => SelectedLineIndex = -1;

    private bool ShouldAdoptRestoredRequest(MarketAcquisitionRequestView request)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            return false;

        if (string.IsNullOrWhiteSpace(Document.RemoteRequestId))
            return true;

        if (!string.Equals(Document.RemoteRequestId, request.Id, StringComparison.Ordinal))
            return true;

        if (Document.SyncStatus.Equals("LocalEdits", StringComparison.OrdinalIgnoreCase) ||
            Document.SyncStatus.Equals("RemoteChanged", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Document.Lines.Count == 0 ||
               Document.RemoteRevision != request.Revision ||
               !Document.SyncStatus.Equals("SyncedClean", StringComparison.OrdinalIgnoreCase);
    }

    private void SaveDocument() => persistDocument(Document);

    private static bool HasSameRemoteState(
        MarketAcquisitionRequestDocument current,
        MarketAcquisitionRequestDocument refreshed)
    {
        if (!string.Equals(current.RemoteRequestId, refreshed.RemoteRequestId, StringComparison.Ordinal) ||
            current.RemoteRevision != refreshed.RemoteRevision)
        {
            return false;
        }

        var currentHash = string.IsNullOrWhiteSpace(current.RemoteHash)
            ? MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(current)
            : current.RemoteHash;
        var refreshedHash = string.IsNullOrWhiteSpace(refreshed.RemoteHash)
            ? MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(refreshed)
            : refreshed.RemoteHash;
        return string.Equals(currentHash, refreshedHash, StringComparison.Ordinal);
    }

    private static MarketAcquisitionRequestDocument PreserveLocalIdentity(
        MarketAcquisitionRequestDocument current,
        MarketAcquisitionRequestDocument refreshed)
    {
        var merged = refreshed with
        {
            LocalRequestId = current.LocalRequestId,
            LocalRevision = current.LocalRevision,
            OutfitterAuthority = current.OutfitterAuthority,
        };
        return OutfitterWorkbenchAuthorityService.ReconcileEdit(current, merged);
    }

    private static MarketAcquisitionRequestDocument AdoptRemoteDocument(
        MarketAcquisitionRequestDocument current,
        MarketAcquisitionRequestView request)
    {
        var remote = MarketAcquisitionRequestDocumentMapper.FromRequestView(request);
        return string.Equals(current.RemoteRequestId, request.Id, StringComparison.Ordinal) &&
               current.OutfitterAuthority is not null
            ? PreserveLocalIdentity(current, remote)
            : remote;
    }

    private static MarketAcquisitionRequestDocument PreserveLocalAuthority(
        MarketAcquisitionRequestDocument current,
        MarketAcquisitionRequestDocument synced) =>
        current.OutfitterAuthority is null
            ? synced
            : synced with { OutfitterAuthority = current.OutfitterAuthority };

    private static MarketAcquisitionRequestDocument MergeSyncMetadata(
        MarketAcquisitionRequestDocument current,
        MarketAcquisitionRequestDocument synced) =>
        current with
        {
            RemoteRequestId = synced.RemoteRequestId,
            RemoteRevision = synced.RemoteRevision,
            RemoteOrigin = synced.RemoteOrigin,
            LastSyncedHash = synced.LastSyncedHash,
            RemoteHash = synced.RemoteHash,
            SyncStatus = "LocalEdits",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static Action<MarketAcquisitionRequestDocument> CreatePersistence(Configuration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return document =>
        {
            MarketAcquisitionRequestDocumentPersistence.Save(config, document);
            config.Save();
        };
    }
}
