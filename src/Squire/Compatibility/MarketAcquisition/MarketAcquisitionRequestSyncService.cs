using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRequestSyncService
{
    private readonly IMarketAcquisitionRequestClient client;

    public MarketAcquisitionRequestSyncService(IMarketAcquisitionRequestClient client)
    {
        this.client = client;
    }

    public async Task<MarketAcquisitionRequestSyncResult> SyncAsync(
        MarketAcquisitionRequestSyncRequest request,
        CancellationToken cancellationToken)
    {
        var validation = MarketAcquisitionRequestDocumentValidator.Validate(
            request.Document,
            request.ClientApiKey,
            request.CharacterName,
            request.World);
        if (!validation.IsValid)
            throw new InvalidOperationException(string.Join(" ", validation.Errors));

        if (!string.IsNullOrWhiteSpace(request.Document.RemoteRequestId) &&
            request.ExistingClaim is not null &&
            string.Equals(request.ExistingClaim.Id, request.Document.RemoteRequestId, StringComparison.Ordinal))
        {
            return await ReplaceExistingAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await CreateClaimAndAcceptAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MarketAcquisitionRequestSyncResult> CreateClaimAndAcceptAsync(
        MarketAcquisitionRequestSyncRequest request,
        CancellationToken cancellationToken)
    {
        var createRequest = MarketAcquisitionRequestDocumentMapper.BuildCreateRequest(
            request.Document,
            request.CharacterName,
            request.World,
            request.PluginInstanceId);
        var created = await client.CreateBatchAsync(
            request.ServerUrl,
            request.ClientApiKey,
            createRequest,
            cancellationToken).ConfigureAwait(false);
        var claimed = await client.ClaimAsync(
            request.ServerUrl,
            request.ClientApiKey,
            created.Id,
            request.CharacterName,
            request.World,
            request.PluginInstanceId,
            cancellationToken).ConfigureAwait(false);
        var acceptKey = MarketAcquisitionRequestDocumentMapper.BuildAcceptIdempotencyKey(
            request.PluginInstanceId,
            request.Document);
        var accepted = await client.AcceptAsync(
            request.ServerUrl,
            request.ClientApiKey,
            claimed.Id,
            claimed.ClaimToken,
            acceptKey,
            cancellationToken).ConfigureAwait(false);

        var mergedClaim = MarketAcquisitionRequestDocumentMapper.MergeClaimWithRequest(claimed, accepted);
        var syncedDocument = MarkSynced(request.Document, accepted);
        return new MarketAcquisitionRequestSyncResult(
            syncedDocument,
            mergedClaim,
            acceptKey,
            RejectIdempotencyKey: null,
            WasReplacement: false);
    }

    private async Task<MarketAcquisitionRequestSyncResult> ReplaceExistingAsync(
        MarketAcquisitionRequestSyncRequest request,
        CancellationToken cancellationToken)
    {
        var existingClaim = request.ExistingClaim ??
                            throw new InvalidOperationException("Existing claim is required for request replacement.");
        var requestId = request.Document.RemoteRequestId ??
                        throw new InvalidOperationException("Remote request id is required for request replacement.");
        var expectedRevision = request.Document.RemoteRevision > 0
            ? request.Document.RemoteRevision
            : existingClaim.Revision;
        var replaceRequest = MarketAcquisitionRequestDocumentMapper.BuildReplaceRequest(
            request.Document,
            expectedRevision);

        MarketAcquisitionRequestView updated;
        try
        {
            updated = await client.ReplaceBatchAsync(
                request.ServerUrl,
                request.ClientApiKey,
                requestId,
                replaceRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (MarketAcquisitionLifecycleHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The local document is authoritative. If the receiver has no corresponding row,
            // publish the current document instead of exposing the missing synchronization detail.
            return await CreateClaimAndAcceptAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var mergedClaim = MarketAcquisitionRequestDocumentMapper.MergeClaimWithRequest(existingClaim, updated);
        var syncedDocument = MarkSynced(request.Document, updated);
        return new MarketAcquisitionRequestSyncResult(
            syncedDocument,
            mergedClaim,
            AcceptIdempotencyKey: null,
            RejectIdempotencyKey: null,
            WasReplacement: true);
    }

    private static MarketAcquisitionRequestDocument MarkSynced(
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionRequestView request)
    {
        var synced = document with
        {
            TargetCharacterName = request.TargetCharacterName,
            TargetWorld = request.TargetWorld,
            RemoteRequestId = request.Id,
            RemoteRevision = request.Revision,
            RemoteOrigin = request.Origin,
            SyncStatus = "SyncedClean",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        var hash = MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(synced);
        return synced with
        {
            LastSyncedHash = hash,
            RemoteHash = hash,
        };
    }
}

public sealed record MarketAcquisitionRequestSyncRequest(
    string ServerUrl,
    string ClientApiKey,
    string CharacterName,
    string World,
    string PluginInstanceId,
    MarketAcquisitionRequestDocument Document,
    MarketAcquisitionClaimView? ExistingClaim);

public sealed record MarketAcquisitionRequestSyncResult(
    MarketAcquisitionRequestDocument Document,
    MarketAcquisitionClaimView Claim,
    string? AcceptIdempotencyKey,
    string? RejectIdempotencyKey,
    bool WasReplacement);
