using System;
using System.Net;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionClaimLifecycleController
{
    private readonly Configuration config;
    private readonly Func<MarketAcquisitionClaimView?> getClaimedRequest;
    private readonly Action<MarketAcquisitionClaimView?> setClaimedRequest;
    private readonly Func<string?> getAcceptIdempotencyKey;
    private readonly Func<string?> getRejectIdempotencyKey;
    private readonly Action clearClaimMetadata;
    private readonly Action<string> setStatus;
    private readonly Func<string> getRouteStatusMessage;
    private readonly Action saveConfig;

    public MarketAcquisitionClaimLifecycleController(
        Configuration config,
        Func<MarketAcquisitionClaimView?> getClaimedRequest,
        Action<MarketAcquisitionClaimView?> setClaimedRequest,
        Func<string?> getAcceptIdempotencyKey,
        Func<string?> getRejectIdempotencyKey,
        Action clearClaimMetadata,
        Action<string> setStatus,
        Func<string> getRouteStatusMessage,
        Action saveConfig)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.getClaimedRequest = getClaimedRequest ?? throw new ArgumentNullException(nameof(getClaimedRequest));
        this.setClaimedRequest = setClaimedRequest ?? throw new ArgumentNullException(nameof(setClaimedRequest));
        this.getAcceptIdempotencyKey = getAcceptIdempotencyKey ?? throw new ArgumentNullException(nameof(getAcceptIdempotencyKey));
        this.getRejectIdempotencyKey = getRejectIdempotencyKey ?? throw new ArgumentNullException(nameof(getRejectIdempotencyKey));
        this.clearClaimMetadata = clearClaimMetadata ?? throw new ArgumentNullException(nameof(clearClaimMetadata));
        this.setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        this.getRouteStatusMessage = getRouteStatusMessage ?? throw new ArgumentNullException(nameof(getRouteStatusMessage));
        this.saveConfig = saveConfig ?? throw new ArgumentNullException(nameof(saveConfig));
    }

    public void ApplySuccessfulRouteProgressReport(
        MarketAcquisitionRouteProgressReportOutcome outcome,
        MarketAcquisitionClaimView claimed,
        long reportSessionVersion,
        long currentSessionVersion,
        string message)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(claimed);
        if (currentSessionVersion != reportSessionVersion || getClaimedRequest()?.Id != claimed.Id)
            return;

        if (outcome.Action.Equals(MarketAcquisitionRouteProgressReporter.CompleteAction, StringComparison.OrdinalIgnoreCase))
        {
            MarketAcquisitionClaimPersistence.Clear(config);
            setClaimedRequest(null);
            clearClaimMetadata();
            setStatus($"Route complete: {message}");
        }
        else
        {
            var updated = claimed with { Status = outcome.Request.Status };
            setClaimedRequest(updated);
            MarketAcquisitionClaimPersistence.Save(config, updated, getAcceptIdempotencyKey(), getRejectIdempotencyKey());
        }

        saveConfig();
    }

    public void SetStatus(string status) => setStatus(status);

    public bool TryHandleRouteProgressConflict(
        Exception exception,
        MarketAcquisitionClaimView claimed,
        long reportSessionVersion,
        long currentSessionVersion)
    {
        if (exception is not MarketAcquisitionLifecycleHttpException { StatusCode: HttpStatusCode.Conflict } conflict ||
            !TryExtractInvalidTransitionSourceStatus(conflict.Error, out var sourceStatus))
        {
            return false;
        }

        if (currentSessionVersion != reportSessionVersion || getClaimedRequest()?.Id != claimed.Id)
            return true;

        if (sourceStatus.Equals("Complete", StringComparison.OrdinalIgnoreCase))
        {
            MarketAcquisitionClaimPersistence.Clear(config);
            setClaimedRequest(null);
            clearClaimMetadata();
            setStatus("Server already marked this route complete.");
        }
        else if (sourceStatus.Equals("Failed", StringComparison.OrdinalIgnoreCase))
        {
            PersistStatus(claimed, sourceStatus);
            setStatus("Server already marked this route failed. Restart to reopen the request.");
        }
        else if (!MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(sourceStatus))
        {
            MarketAcquisitionClaimPersistence.Clear(config);
            setClaimedRequest(null);
            clearClaimMetadata();
            setStatus($"Server request moved to {sourceStatus}; fetch dashboard requests to continue.");
        }
        else
        {
            PersistStatus(claimed, sourceStatus);
            setStatus(getRouteStatusMessage());
        }

        saveConfig();
        return true;
    }

    private void PersistStatus(MarketAcquisitionClaimView claimed, string status)
    {
        var updated = claimed with { Status = status };
        setClaimedRequest(updated);
        MarketAcquisitionClaimPersistence.Save(config, updated, getAcceptIdempotencyKey(), getRejectIdempotencyKey());
    }

    private static bool TryExtractInvalidTransitionSourceStatus(string? error, out string sourceStatus)
    {
        const string prefix = "Cannot move acquisition request from ";
        const string separator = " to ";
        sourceStatus = string.Empty;
        if (string.IsNullOrWhiteSpace(error) || !error.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var start = prefix.Length;
        var end = error.IndexOf(separator, start, StringComparison.OrdinalIgnoreCase);
        if (end <= start)
            return false;

        sourceStatus = error[start..end].Trim();
        return !string.IsNullOrWhiteSpace(sourceStatus);
    }
}
