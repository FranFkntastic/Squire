using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteReportDispatcher : IDisposable
{
    private const int MaxAttempts = 3;
    private const string RouteProgressType = "route-progress.v1";
    private const string PurchaseAuditType = "purchase-audit.v1";
    private const string LineProgressType = "line-progress.v1";
    private const string MarketObservationType = "market-observation.v1";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReplayInterval = TimeSpan.FromSeconds(30);

    private readonly object sync = new();
    private readonly IMarketAcquisitionRouteReporter reporter;
    private readonly MarketAcquisitionClaimLifecycleController claimLifecycle;
    private readonly IMarketAcquisitionRouteCallbackDispatcher callbackDispatcher;
    private readonly IMarketAcquisitionReportOutbox outbox;
    private readonly VolatileMarketAcquisitionReportOutbox volatileFallback = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly HashSet<string> inFlightEntryIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> entrySessionVersions = new(StringComparer.Ordinal);
    private readonly Task replayLoop;
    private Task queueTail = Task.CompletedTask;
    private MarketAcquisitionClaimView? claimed;
    private long sessionVersion;
    private string? lastSuccessfulRouteKey;

    public MarketAcquisitionRouteReportDispatcher(
        IMarketAcquisitionRouteReporter reporter,
        MarketAcquisitionClaimLifecycleController claimLifecycle,
        IMarketAcquisitionRouteCallbackDispatcher callbackDispatcher,
        IMarketAcquisitionReportOutbox? outbox = null)
    {
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.claimLifecycle = claimLifecycle ?? throw new ArgumentNullException(nameof(claimLifecycle));
        this.callbackDispatcher = callbackDispatcher ?? throw new ArgumentNullException(nameof(callbackDispatcher));
        this.outbox = outbox ?? new VolatileMarketAcquisitionReportOutbox();
        QueuePendingOutboxEntries();
        replayLoop = Task.Run(() => ReplayLoopAsync(lifetimeCancellation.Token));
    }

    public bool CanReport => reporter.CanReport;

    public void BeginSession(MarketAcquisitionClaimView sessionClaim)
    {
        ArgumentNullException.ThrowIfNull(sessionClaim);
        lock (sync)
        {
            claimed = sessionClaim;
            sessionVersion++;
            lastSuccessfulRouteKey = null;
        }

        QueuePendingOutboxEntries();
    }

    public void ResetSession()
    {
        lock (sync)
        {
            claimed = null;
            sessionVersion++;
            lastSuccessfulRouteKey = null;
        }
    }

    public void EnqueueRouteProgress(MarketAcquisitionRouteProgressReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var routeKey = $"{report.RequestId}|{report.RouteState}|{report.RouteStopId}|{report.ActiveWorld}|{report.Phase}|{report.Message}";
        MarketAcquisitionClaimView sessionClaim;
        lock (sync)
        {
            if (claimed == null || routeKey.Equals(lastSuccessfulRouteKey, StringComparison.Ordinal))
                return;
            sessionClaim = claimed;
        }

        var entry = Persist(
            $"route|{report.RequestId}|{report.AttemptId}|{report.Sequence}",
            RouteProgressType,
            new DurableRouteProgress(report, sessionClaim, routeKey));
        QueueOutboxEntry(entry);
    }

    public void EnqueuePurchaseAudit(MarketAcquisitionPurchaseAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        QueueOutboxEntry(Persist(
            $"purchase|{report.RequestId}|{report.AttemptId}|{report.Sequence}",
            PurchaseAuditType,
            report));
    }

    public void EnqueueLineProgress(MarketAcquisitionLineProgressReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        QueueOutboxEntry(Persist(
            $"line|{report.RequestId}|{report.AttemptId}|{report.Sequence}",
            LineProgressType,
            report));
    }

    public void EnqueueMarketObservation(MarketAcquisitionMarketObservationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        QueueOutboxEntry(Persist(
            $"observation|{report.RequestId}|{report.AttemptId}|{report.Sequence}",
            MarketObservationType,
            report));
    }

    private MarketAcquisitionReportOutboxEntry Persist<T>(string id, string reportType, T payload)
    {
        try
        {
            var entry = outbox.Put(id, reportType, payload);
            lock (sync)
                entrySessionVersions[id] = sessionVersion;
            return entry;
        }
        catch (Exception ex)
        {
            claimLifecycle.SetStatus($"Could not persist the report outbox; sending this report without crash recovery: {ex.Message}");
            var entry = volatileFallback.Put(id, reportType, payload);
            lock (sync)
                entrySessionVersions[id] = sessionVersion;
            return entry;
        }
    }

    private void QueuePendingOutboxEntries()
    {
        foreach (var entry in outbox.Snapshot())
            QueueOutboxEntry(entry);
    }

    private void QueueOutboxEntry(MarketAcquisitionReportOutboxEntry entry)
    {
        lock (sync)
        {
            if (!inFlightEntryIds.Add(entry.Id))
                return;

            var token = lifetimeCancellation.Token;
            queueTail = queueTail
                .ContinueWith(
                    _ => token.IsCancellationRequested ? Task.CompletedTask : SendAndAcknowledgeAsync(entry, token),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task SendAndAcknowledgeAsync(
        MarketAcquisitionReportOutboxEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            if (HasEarlierPendingEntryForSameRequest(entry))
                return;

            await SendAsync(entry, cancellationToken).ConfigureAwait(false);
            outbox.Remove(entry.Id);
            volatileFallback.Remove(entry.Id);
            lock (sync)
                entrySessionVersions.Remove(entry.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsForCurrentClaim(entry))
            {
                await callbackDispatcher.DispatchAsync(() =>
                    claimLifecycle.SetStatus($"Report retained for automatic retry after {MaxAttempts} attempts: {ex.Message}"))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            lock (sync)
                inFlightEntryIds.Remove(entry.Id);
        }
    }

    private bool HasEarlierPendingEntryForSameRequest(MarketAcquisitionReportOutboxEntry entry)
    {
        var requestId = GetRequestId(entry);
        foreach (var candidate in outbox.Snapshot())
        {
            if (candidate.Id.Equals(entry.Id, StringComparison.Ordinal))
                return false;
            if (requestId.Equals(GetRequestId(candidate), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private bool IsForCurrentClaim(MarketAcquisitionReportOutboxEntry entry)
    {
        string? currentRequestId;
        lock (sync)
        {
            if (entrySessionVersions.TryGetValue(entry.Id, out var reportSessionVersion) &&
                reportSessionVersion != sessionVersion)
            {
                return false;
            }
            currentRequestId = claimed?.Id;
        }
        if (string.IsNullOrWhiteSpace(currentRequestId))
            return false;

        var entryRequestId = GetRequestId(entry);
        return currentRequestId.Equals(entryRequestId, StringComparison.Ordinal);
    }

    private string GetRequestId(MarketAcquisitionReportOutboxEntry entry) => entry.ReportType switch
        {
            RouteProgressType => outbox.Deserialize<DurableRouteProgress>(entry).Report.RequestId,
            PurchaseAuditType => outbox.Deserialize<MarketAcquisitionPurchaseAuditReport>(entry).RequestId,
            LineProgressType => outbox.Deserialize<MarketAcquisitionLineProgressReport>(entry).RequestId,
            MarketObservationType => outbox.Deserialize<MarketAcquisitionMarketObservationReport>(entry).RequestId,
            _ => string.Empty,
        };

    private Task SendAsync(MarketAcquisitionReportOutboxEntry entry, CancellationToken cancellationToken) =>
        entry.ReportType switch
        {
            RouteProgressType => SendRouteProgressAsync(
                outbox.Deserialize<DurableRouteProgress>(entry),
                cancellationToken),
            PurchaseAuditType => ExecuteWithRetryAsync(
                token => reporter.ReportPurchaseAuditAsync(
                    outbox.Deserialize<MarketAcquisitionPurchaseAuditReport>(entry),
                    token),
                cancellationToken),
            LineProgressType => ExecuteWithRetryAsync(
                token => reporter.ReportLineProgressAsync(
                    outbox.Deserialize<MarketAcquisitionLineProgressReport>(entry),
                    token),
                cancellationToken),
            MarketObservationType => ExecuteWithRetryAsync(
                token => reporter.ReportMarketObservationAsync(
                    outbox.Deserialize<MarketAcquisitionMarketObservationReport>(entry),
                    token),
                cancellationToken),
            _ => throw new InvalidOperationException($"Unknown market acquisition outbox report type '{entry.ReportType}'."),
        };

    private async Task SendRouteProgressAsync(
        DurableRouteProgress durable,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await ExecuteWithRetryAsync(
                token => reporter.ReportRouteProgressAsync(durable.Report, token),
                cancellationToken).ConfigureAwait(false);
            var currentVersion = CurrentSessionVersion;
            await callbackDispatcher.DispatchAsync(() =>
            {
                claimLifecycle.ApplySuccessfulRouteProgressReport(
                    outcome,
                    durable.Claim,
                    currentVersion,
                    CurrentSessionVersion,
                    durable.Report.Message);
                lock (sync)
                    lastSuccessfulRouteKey = durable.RouteKey;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var handled = false;
            var currentVersion = CurrentSessionVersion;
            await callbackDispatcher.DispatchAsync(() =>
            {
                handled = claimLifecycle.TryHandleRouteProgressConflict(
                    ex,
                    durable.Claim,
                    currentVersion,
                    CurrentSessionVersion);
            }).ConfigureAwait(false);
            if (!handled)
                throw;
        }
    }

    private long CurrentSessionVersion
    {
        get
        {
            lock (sync)
                return sessionVersion;
        }
    }

    private async Task ReplayLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ReplayInterval, cancellationToken).ConfigureAwait(false);
                QueuePendingOutboxEntries();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal Task DrainAsync()
    {
        lock (sync)
            return queueTail;
    }

    private static async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(AttemptTimeout);
            try
            {
                return await operation(attemptCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (
                !cancellationToken.IsCancellationRequested && attemptCancellation.IsCancellationRequested)
            {
                lastException = new TimeoutException($"Report attempt timed out after {AttemptTimeout.TotalSeconds:N0} seconds.", ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
            }

            if (attempt < MaxAttempts)
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw lastException ?? new InvalidOperationException("Report operation failed without an exception.");
    }

    private static async Task ExecuteWithRetryAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
        lock (sync)
        {
            claimed = null;
            inFlightEntryIds.Clear();
            entrySessionVersions.Clear();
        }
        _ = replayLoop;
    }

    private sealed record DurableRouteProgress(
        MarketAcquisitionRouteProgressReport Report,
        MarketAcquisitionClaimView Claim,
        string RouteKey);
}
