using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Travel;
using MarketMafioso.Squire.Outfitter.Acquisition;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngine : IDisposable
{
    private static readonly TimeSpan RouteMonitorInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MarketBoardItemSearchOperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TravelPreparationOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorldTravelArrivalOperationTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan MarketBoardPurchaseConfirmationWatchdog = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MarketBoardPurchaseInitialMonitorDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MarketBoardPurchaseListingRemovalWatchdog = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MarketBoardPurchaseMonitorInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan UniversalisFreshnessVerificationDelay = TimeSpan.FromSeconds(10);
    private readonly MarketAcquisitionRouteRunner runner;
    private readonly IMarketAcquisitionRouteContext context;
    private readonly IMarketAcquisitionRouteUiAutomation uiAutomation;
    private readonly IMarketAcquisitionRouteTravelCleanup travelCleanup;
    private readonly IMarketAcquisitionMarketBoardIo marketBoard;
    private readonly IMarketAcquisitionPurchaseIo purchase;
    private readonly IMarketAcquisitionRouteEvidenceRecorder evidence;
    private readonly MarketAcquisitionRouteReportDispatcher reportDispatcher;
    private readonly IMarketAcquisitionRouteClock clock;
    private readonly MarketBoardListingReadAccumulator listingReadAccumulator = new();
    private readonly MarketBoardAutomationController purchaseAutomation = new();
    private readonly MarketAcquisitionRouteOperationExecutor operationExecutor = new();
    private readonly MarketAcquisitionRouteEngineState state = new();
    private CancellationTokenSource freshnessCancellation = new();
    private MarketAcquisitionClaimView? claimedRequest;
    private MarketAcquisitionTravelLease? activeTravelLease;
    private MarketAcquisitionTravelLease? unresolvedTravelLease;
    private MarketAcquisitionApproachLease? activeApproachLease;
    private bool travelInterruptedByCleanup;
    private long operationSequence;
    private readonly IOutfitterRouteExecutionStateStore outfitterStateStore;
    private OutfitterRouteAuthoritySession? outfitterAuthority;
    private OutfitterDryRunScenario outfitterDryRunScenario;
    private bool outfitterDryRunFaultEligible;
    private bool outfitterDryRunFaultInjected;
    private bool outfitterDryRunNoViableConsumed;

    public MarketAcquisitionRouteEngine(
        MarketAcquisitionRouteRunner runner,
        IMarketAcquisitionRouteContext context,
        IMarketAcquisitionRouteUiAutomation uiAutomation,
        IMarketAcquisitionRouteTravelCleanup travelCleanup,
        IMarketAcquisitionMarketBoardIo marketBoard,
        IMarketAcquisitionPurchaseIo purchase,
        IMarketAcquisitionRouteReporter reporter,
        IMarketAcquisitionRouteEvidenceRecorder evidence,
        MarketAcquisitionClaimLifecycleController claimLifecycle,
        IMarketAcquisitionRouteCallbackDispatcher callbackDispatcher,
        IMarketAcquisitionRouteClock clock,
        IOutfitterRouteExecutionStateStore outfitterStateStore,
        IMarketAcquisitionReportOutbox? reportOutbox = null)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.uiAutomation = uiAutomation ?? throw new ArgumentNullException(nameof(uiAutomation));
        this.travelCleanup = travelCleanup ?? throw new ArgumentNullException(nameof(travelCleanup));
        this.marketBoard = marketBoard ?? throw new ArgumentNullException(nameof(marketBoard));
        this.purchase = purchase ?? throw new ArgumentNullException(nameof(purchase));
        this.evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        reportDispatcher = new MarketAcquisitionRouteReportDispatcher(
            reporter ?? throw new ArgumentNullException(nameof(reporter)),
            claimLifecycle ?? throw new ArgumentNullException(nameof(claimLifecycle)),
            callbackDispatcher ?? throw new ArgumentNullException(nameof(callbackDispatcher)),
            reportOutbox);
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.outfitterStateStore = outfitterStateStore ?? throw new ArgumentNullException(nameof(outfitterStateStore));
    }

    public bool IsRouteActive =>
        runner.IsRunning ||
        runner.IsPaused ||
        outfitterAuthority?.State.Phase is OutfitterRouteAuthorityPhase.Preparing or
            OutfitterRouteAuthorityPhase.Active or OutfitterRouteAuthorityPhase.RecoveryNeeded ||
        state.ProbeRunning ||
        operationExecutor.ActiveSnapshot != null ||
        purchaseAutomation.PurchaseSession?.IsActive == true;

    public OutfitterDryRunScenario ArmedOutfitterDryRunScenario => outfitterDryRunScenario;
    public bool IsOutfitterDryRunFaultEligible => outfitterDryRunFaultEligible;
    public bool WasOutfitterDryRunFaultInjected => outfitterDryRunFaultInjected;

    public bool ArmOutfitterDryRunScenario(OutfitterDryRunScenario scenario)
    {
        if (IsRouteActive)
            return false;
        outfitterDryRunScenario = scenario;
        outfitterDryRunFaultInjected = false;
        outfitterDryRunNoViableConsumed = false;
        return true;
    }

    public bool ConsumeNoViableOutfitterDryRunScenario()
    {
        if (!outfitterDryRunFaultEligible || !outfitterDryRunFaultInjected || outfitterDryRunNoViableConsumed ||
            outfitterDryRunScenario != OutfitterDryRunScenario.NoViableRecovery || !NeedsOutfitterRecovery)
            return false;
        outfitterDryRunNoViableConsumed = true;
        return true;
    }

    public MarketAcquisitionRouteEngineSnapshot CreateSnapshot() => new()
    {
        ExecutionMode = state.ExecutionMode,
        StatusMessage = runner.StatusMessage,
        VisibleAcquisitionStatus = state.AcquisitionStatus,
        IsRouteActive = IsRouteActive,
        IsRunning = runner.IsRunning,
        IsPaused = runner.IsPaused,
        CanRestart = runner.CanRestart,
        CanFinalizeInputCaptureLog = runner.CanFinalizeInputCaptureLog,
        CompletedOrProbedStopCount = runner.CompletedOrProbedStops.Count,
        RouteState = runner.State,
        ActiveStop = runner.ActiveStop,
        Stops = runner.Stops,
        ActivePlan = runner.ActivePlan,
        IsProbeRunning = state.ProbeRunning,
        MarketBoardReadResult = state.MarketBoardReadResult,
        MarketBoardReconciliation = state.MarketBoardReconciliation,
        LiveCandidatePlan = state.LiveCandidatePlan,
        ActiveOperation = operationExecutor.ActiveSnapshot,
        LastOperation = operationExecutor.LastSnapshot,
        PurchaseSession = purchaseAutomation.PurchaseSession,
        LastPurchaseResult = purchaseAutomation.LastPurchaseResult,
        PurchaseEvidenceState = purchase.PurchaseEvidenceState,
        ActiveWorldPurchasedQuantity = state.ActiveWorldPurchasedQuantity,
        ActiveWorldSpentGil = state.ActiveWorldSpentGil,
        ActiveLinePurchasedQuantity = state.ActiveLinePurchasedQuantity,
        ActiveLineSpentGil = state.ActiveLineSpentGil,
        LastDiagnosticFilePath = runner.LastDiagnosticFilePath,
        LastObservedListingsCsvPath = runner.LastObservedListingsCsvPath,
        LastPurchaseRecordsCsvPath = runner.LastPurchaseRecordsCsvPath,
        LastRunSummary = runner.LastRunSummary,
        LatestWorldCompletionSummary = runner.LatestWorldCompletionSummary,
        LastRunDiagnosticSummary = runner.LastRunDiagnosticSummary,
        OutfitterExecution = outfitterAuthority?.State,
    };

    public MarketAcquisitionRouteActionResult Start(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed,
        bool enableDiagnostics,
        bool includeOpportunisticChecks,
        OutfitterExecutionContract? outfitterContract = null,
        MarketAcquisitionRequestDocument? workbenchDocument = null,
        MarketAcquisitionExecutionMode executionMode = MarketAcquisitionExecutionMode.Live)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));

        claimedRequest = claimed;
        ClearExecutionState();
        state.ExecutionMode = executionMode;
        outfitterDryRunFaultEligible = executionMode == MarketAcquisitionExecutionMode.DryRun &&
                                       outfitterContract?.Transfer.DryRunOnly == true;
        outfitterDryRunFaultInjected = false;
        outfitterDryRunNoViableConsumed = false;
        var routePlan = plan;
        if (outfitterContract is not null)
        {
            if (outfitterContract.Transfer.DryRunOnly && executionMode != MarketAcquisitionExecutionMode.DryRun)
            {
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(
                    "This diagnostic Squire contract is permanently restricted to non-spending dry runs."));
            }
            if (workbenchDocument is null)
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail("Squire Route start requires its finalized Workbench document."));
            try
            {
                IOutfitterRouteExecutionStateStore authorityStore = outfitterStateStore;
                if (executionMode == MarketAcquisitionExecutionMode.DryRun)
                {
                    routePlan = OutfitterDryRunExecutionStateRestorer.RestoreRemainingPlan(
                        outfitterContract,
                        workbenchDocument,
                        claimed,
                        plan,
                        outfitterStateStore.Restore());
                    authorityStore = new RestoreOnlyOutfitterRouteExecutionStateStore(outfitterStateStore);
                }
                outfitterAuthority = OutfitterRouteAuthoritySession.Consume(
                    outfitterContract,
                    workbenchDocument,
                    routePlan,
                    claimed,
                    authorityStore);
                outfitterAuthority.CompletePreflight(routePlan);
            }
            catch (Exception exception)
            {
                var message = $"Squire preflight stopped before travel or purchase: {exception.Message}";
                outfitterAuthority?.Pause(message);
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(message));
            }
        }
        else
        {
            outfitterAuthority = null;
        }
        if (executionMode == MarketAcquisitionExecutionMode.Live)
            reportDispatcher.BeginSession(claimed);
        MarketAcquisitionRouteActionResult result;
        try
        {
            result = runner.Start(routePlan, enableDiagnostics || executionMode == MarketAcquisitionExecutionMode.DryRun, includeOpportunisticChecks, executionMode);
        }
        catch (Exception exception) when (outfitterAuthority is not null)
        {
            outfitterAuthority.Pause($"Squire Route start failed before travel: {exception.Message}");
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(outfitterAuthority.State.Message));
        }
        if (!result.Success && outfitterAuthority is not null)
        {
            outfitterAuthority.Pause($"Squire Route start stopped safely: {result.Message}");
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(outfitterAuthority.State.Message));
        }
        state.AcquisitionStatus = result.Message;
        return result;
    }

    public bool NeedsOutfitterRecovery => outfitterAuthority?.State.NeedsRecovery == true;

    public MarketAcquisitionClaimView CreateOutfitterRecoveryClaim(MarketAcquisitionClaimView claim) =>
        outfitterAuthority?.CreateRecoveryClaim(claim) ??
        throw new InvalidOperationException("No Squire recovery is pending.");

    public MarketAcquisitionRouteActionResult StartOutfitterRecovery(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView remainingClaim,
        MarketAcquisitionRequestDocument workbenchDocument)
    {
        if (outfitterAuthority is null || !outfitterAuthority.State.NeedsRecovery)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail("No Squire recovery is pending."));
        try
        {
            outfitterAuthority.ValidateCurrentDocument(workbenchDocument);
            outfitterAuthority.BeginRecovery(plan);
            claimedRequest = remainingClaim;
            ClearExecutionState(preserveExecutionMode: true);
            if (state.ExecutionMode == MarketAcquisitionExecutionMode.Live)
                reportDispatcher.BeginSession(remainingClaim);
            var result = runner.Start(
                plan,
                enableDiagnostics: state.ExecutionMode == MarketAcquisitionExecutionMode.DryRun,
                includeOpportunisticChecks: false,
                state.ExecutionMode);
            if (!result.Success)
            {
                outfitterAuthority.Pause($"Recovery start stopped safely: {result.Message}");
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(outfitterAuthority.State.Message));
            }
            return UpdateStatus(result);
        }
        catch (Exception exception)
        {
            outfitterAuthority.Pause($"Recovery preflight stopped safely: {exception.Message}");
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(outfitterAuthority.State.Message));
        }
    }

    public void PauseOutfitterRecovery(string message)
    {
        outfitterAuthority?.Pause(message);
        state.AcquisitionStatus = message;
    }

    public void RequestOutfitterRecovery(MarketAcquisitionRequestDocument workbenchDocument)
    {
        if (outfitterAuthority?.State.Phase != OutfitterRouteAuthorityPhase.Paused)
            return;
        try
        {
            outfitterAuthority.ValidateCurrentDocument(workbenchDocument);
            outfitterAuthority.RequestRecovery();
        }
        catch (Exception exception)
        {
            outfitterAuthority.Pause(exception.Message);
        }
        state.AcquisitionStatus = outfitterAuthority.State.Message;
    }

    private MarketAcquisitionRouteActionResult TransitionToOutfitterRecovery(string reason)
    {
        if (outfitterAuthority is null)
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reason));

        CleanupOwnedApproach("Squire recovery");
        CleanupOwnedTravel("Squire recovery");
        CancelActiveOperation("Visible market rows changed; preparing Squire recovery.");
        if (runner.IsRunning || runner.IsPaused)
            runner.Stop();
        outfitterAuthority.RequestRecovery($"{reason} Refreshing and optimizing the complete remaining exact-quality route.");
        state.AcquisitionStatus = outfitterAuthority.State.Message;
        return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(outfitterAuthority.State.Message));
    }

    internal MarketAcquisitionRouteActionResult? EnforceOutfitterCandidateAuthority(
        MarketAcquisitionWorldItemSubtask subtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        if (outfitterAuthority is null)
            return null;
        if (outfitterDryRunFaultEligible && !outfitterDryRunFaultInjected &&
            outfitterDryRunScenario is OutfitterDryRunScenario.ChangedListingRecovery or OutfitterDryRunScenario.NoViableRecovery)
        {
            outfitterDryRunFaultInjected = true;
            return TransitionToOutfitterRecovery(
                outfitterDryRunScenario == OutfitterDryRunScenario.ChangedListingRecovery
                    ? "Diagnostic dry run substituted a changed visible row after preflight."
                    : "Diagnostic dry run removed every in-envelope visible row after preflight.");
        }
        var authorization = outfitterAuthority.AuthorizeCandidate(subtask, candidatePlan);
        return authorization.IsValid
            ? null
            : TransitionToOutfitterRecovery(
                authorization.Error ?? "Visible market rows exceeded Squire authority.");
    }

    public MarketAcquisitionRouteActionResult StartEvidenceRefresh(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed,
        bool enableDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));

        claimedRequest = claimed;
        ClearExecutionState();
        state.EvidenceRefreshOnly = true;
        reportDispatcher.BeginSession(claimed);
        var result = runner.Start(plan, enableDiagnostics, includeOpportunisticChecks: false);
        state.AcquisitionStatus = result.Success
            ? $"Evidence refresh started. {result.Message}"
            : result.Message;
        return result;
    }

    public MarketAcquisitionRouteActionResult Pause()
    {
        travelInterruptedByCleanup = activeTravelLease != null;
        CleanupOwnedApproach("Pause");
        CleanupOwnedTravel("Pause");
        CancelActiveOperation("Route paused; active operation cancelled.");
        var result = runner.Pause();
        if (result.Success)
            outfitterAuthority?.Pause("Squire route paused; no purchase authority is active.");
        return UpdateStatus(result);
    }

    public MarketAcquisitionRouteActionResult Resume()
    {
        if (travelInterruptedByCleanup)
        {
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(
                "World travel was interrupted while paused; restart the route only after reconciling the current world."));
        }

        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));

        if (outfitterAuthority is not null && runner.ActivePlan is { } plan)
        {
            try
            {
                outfitterAuthority.CompletePreflight(plan);
            }
            catch (Exception exception)
            {
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail($"Squire resume preflight failed: {exception.Message}"));
            }
        }
        return UpdateStatus(runner.Resume());
    }

    public MarketAcquisitionRouteActionResult Stop()
    {
        uiAutomation.TryCloseMarketBoardWindows();
        CleanupOwnedApproach("Stop");
        CleanupOwnedTravel("Stop");
        CancelActiveOperation("Route stopped.");
        var result = runner.Stop();
        if (result.Success)
            outfitterAuthority?.Pause("Squire route stopped; persisted purchases remain reconciled for a later restart.");
        listingReadAccumulator.Clear();
        purchaseAutomation.Clear();
        reportDispatcher.ResetSession();
        freshnessCancellation.Cancel();
        return UpdateStatus(result);
    }

    public MarketAcquisitionRouteActionResult Restart(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        CleanupOwnedApproach("Replacement");
        CleanupOwnedTravel("Replacement");
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));
        claimedRequest = claimed;
        ClearExecutionState(preserveExecutionMode: true);
        if (outfitterAuthority is not null)
        {
            try
            {
                outfitterAuthority.BeginRecovery(plan);
            }
            catch (Exception exception)
            {
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail($"Squire restart preflight failed: {exception.Message}"));
            }
        }
        if (state.ExecutionMode == MarketAcquisitionExecutionMode.Live)
            reportDispatcher.BeginSession(claimed);
        var result = runner.Restart(plan);
        if (!result.Success && outfitterAuthority is not null)
        {
            outfitterAuthority.Pause($"Squire restart stopped safely: {result.Message}");
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(outfitterAuthority.State.Message));
        }
        return UpdateStatus(result);
    }

    public MarketAcquisitionRouteActionResult ReprepareAndRestart(
        MarketAcquisitionPlan plan,
        DateTimeOffset preparedAtUtc,
        MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        CleanupOwnedApproach("Replacement");
        CleanupOwnedTravel("Replacement");
        if (!TryReconcileUnresolvedTravelLease(out var reconciliationFailure))
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(reconciliationFailure));
        claimedRequest = claimed;
        ClearExecutionState(preserveExecutionMode: true);
        if (outfitterAuthority is not null)
        {
            try
            {
                outfitterAuthority.BeginRecovery(plan);
            }
            catch (Exception exception)
            {
                return UpdateStatus(MarketAcquisitionRouteActionResult.Fail($"Squire restart preflight failed: {exception.Message}"));
            }
        }
        if (state.ExecutionMode == MarketAcquisitionExecutionMode.Live)
            reportDispatcher.BeginSession(claimed);
        var result = runner.ReprepareAndRestart(plan, preparedAtUtc);
        if (!result.Success && outfitterAuthority is not null)
        {
            outfitterAuthority.Pause($"Squire restart stopped safely: {result.Message}");
            return UpdateStatus(MarketAcquisitionRouteActionResult.Fail(outfitterAuthority.State.Message));
        }
        return UpdateStatus(result);
    }

    public void Reset(string status)
    {
        CleanupOwnedApproach("Reset");
        CleanupOwnedTravel("Reset");
        CancelActiveOperation(status);
        runner.Reset(status);
        ClearExecutionState();
        state.AcquisitionStatus = status;
        claimedRequest = null;
    }

    public MarketAcquisitionRouteActionResult CaptureInputState(string label) =>
        runner.RecordInputCapture(label, marketBoard.CaptureInputState());

    public MarketAcquisitionRouteActionResult FinalizeInputCaptureLog() =>
        runner.FinalizeInputCaptureLog();

    public MarketPurchaseTerminalResolutionResult ReconcileTerminalPurchaseEvidence(
        bool purchaseOccurred,
        string resolution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolution);
        if (purchase.PurchaseEvidenceState is not { } terminal || terminal is PendingMarketPurchase)
            return new(MarketPurchaseTerminalResolutionStatus.NoTerminalEvidence, "No terminal purchase evidence requires reconciliation.");
        if (terminal is ConfirmedMarketPurchase && !purchaseOccurred)
            return new(MarketPurchaseTerminalResolutionStatus.InvalidDisposition,
                "Confirmed server purchase evidence must be applied; it cannot be discarded as a failed purchase.");

        if (purchaseOccurred)
        {
            if (outfitterAuthority is null || runner.ActivePlan is null)
                return new(MarketPurchaseTerminalResolutionStatus.InvalidDisposition,
                    "Applying a purchase requires the matching finalized Squire authority and plan to be loaded.");
            try
            {
                var intent = terminal.Intent;
                outfitterAuthority.RecordPurchase(intent.LineId, new MarketBoardPurchaseCandidate
                {
                    ItemId = intent.ItemId,
                    WorldName = intent.WorldName,
                    ListingId = intent.ListingId,
                    RetainerId = intent.RetainerId ?? string.Empty,
                    UnitPrice = intent.UnitPrice,
                    Quantity = intent.Quantity,
                    IsHq = intent.IsHighQuality,
                }, runner.ActivePlan);
            }
            catch (Exception exception)
            {
                return new(MarketPurchaseTerminalResolutionStatus.InvalidDisposition,
                    $"Purchase reconciliation could not persist exact sunk authority: {exception.Message}");
            }
        }

        var disposition = terminal is ConfirmedMarketPurchase && purchaseOccurred
            ? MarketPurchaseTerminalDisposition.AppliedExactlyOnce
            : MarketPurchaseTerminalDisposition.ManuallyReconciled;
        return purchase.ResolvePurchaseEvidence(
            terminal.Intent.IntentId,
            disposition,
            clock.UtcNow,
            resolution.Trim());
    }

    public MarketAcquisitionRouteEngineTickResult TickRoute(bool isRequestBusy)
    {
        if (TryFailExpiredOperation())
        {
            ReportRouteProgress();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc);
        }

        if (isRequestBusy || state.ProbeRunning || !runner.IsRunning)
            return MarketAcquisitionRouteEngineTickResult.Idle();

        var now = clock.UtcNow;
        if (now < state.NextRouteMonitorUtc)
            return MarketAcquisitionRouteEngineTickResult.Idle("Waiting for next route monitor tick.");

        state.NextRouteMonitorUtc = now.Add(RouteMonitorInterval);
        try
        {
            var activeStop = runner.ActiveStop;
            if (activeStop == null)
                return MarketAcquisitionRouteEngineTickResult.Idle("Route has no active stop.");

            if (string.Equals(activeStop.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                HandlePendingStop(activeStop);
            else if (!context.IsCurrentWorldAvailable)
                UpdateStatus(runner.RecordCurrentWorldUnavailable());
            else
                HandleWorldScopedStop(activeStop, context.GetCurrentWorldName());

            if (runner.ActiveStop is { Status: "Purchasing" } &&
                purchaseAutomation.PurchaseSession?.IsActive != true)
            {
                BeginNextWorldPurchase();
            }

            ReportRouteProgress();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc);
        }
        catch (Exception ex)
        {
            var result = FailRoute($"Unable to monitor guided route. {ex.Message}", ex);
            state.AcquisitionStatus = result.Message;
            ReportRouteProgress();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc);
        }
    }

    public void ProbeLiveMarketBoard()
    {
        try
        {
            ProbeLiveMarketBoardCore(
                runner.ActivePlan ?? throw new InvalidOperationException("Prepare a live candidate plan before probing live market board listings."),
                claimedRequest ?? throw new InvalidOperationException("No dashboard request is accepted."),
                recordRouteResult: true);
            var activeStop = runner.ActiveStop;
            if (activeStop is { Status: "Arrived" } &&
                !string.Equals(state.MarketBoardReadResult?.Status, "Ready", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(state.MarketBoardReadResult?.Status, "NoSearchItem", StringComparison.OrdinalIgnoreCase))
                    runner.ClearSearchSubmission("Market board results did not expose a searched item id.");

                UpdateStatus(runner.BeginProbe(
                    $"Arrived on {activeStop.WorldName}; waiting for live listings. {state.MarketBoardReadResult?.Message ?? "Market board read has not completed."}"));
            }
        }
        catch (Exception ex)
        {
            var activeStop = runner.ActiveStop;
            var activeLine = claimedRequest == null ? null : GetActiveRouteLine(claimedRequest);
            var itemLabel = activeLine == null ? "active item" : FormatItem(activeLine);
            var worldLabel = activeStop?.WorldName ??
                             (context.IsCurrentWorldAvailable ? context.GetCurrentWorldName() : "unknown world");
            var message = $"Live market board probe failed for {itemLabel} on {worldLabel}. {ex.Message}";
            FailRoute(message, ex);
            state.AcquisitionStatus = message;
        }
        finally
        {
            if (runner.ActiveStop?.Status != "Arrived")
                runner.ClearSearchSubmission("Route advanced or stopped before the next live listing read.");

            state.ProbeRunning = false;
            state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
            ReportRouteProgress();
        }
    }

    public void ProbePreparedPlan(MarketAcquisitionPlan plan, MarketAcquisitionClaimView claimed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claimed);
        if (IsRouteActive)
            throw new InvalidOperationException("A prepared-plan probe cannot run while a route or purchase is active.");

        claimedRequest = claimed;
        state.ProbeRunning = true;
        try
        {
            ProbeLiveMarketBoardCore(plan, claimed, recordRouteResult: false);
        }
        catch (Exception ex)
        {
            state.AcquisitionStatus = $"Live market board probe failed: {ex.Message}";
        }
        finally
        {
            state.ProbeRunning = false;
        }
    }

    private void HandlePendingStop(MarketAcquisitionGuidedRouteStop activeStop)
    {
        var currentWorld = context.IsCurrentWorldAvailable ? context.GetCurrentWorldName() : null;
        var requiresTravelPreparation = runner.MarketBoardCloseRequiredBeforeTravel ||
            !context.IsCurrentWorldAvailable ||
            !activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase);
        MarketAcquisitionRouteOperationSnapshot? preparation = null;
        if (requiresTravelPreparation ||
            operationExecutor.ActiveSnapshot?.Kind == MarketAcquisitionRouteOperationKind.TravelPreparation)
        {
            preparation = EnsureTravelPreparationOperation(activeStop);
        }

        if (runner.MarketBoardCloseRequiredBeforeTravel)
        {
            if (uiAutomation.TryCloseMarketBoardWindows())
            {
                ObserveTravelPreparationOperation(
                    preparation!,
                    MarketAcquisitionRouteOperationDisposition.Pending,
                    $"Waiting for market board windows to close before traveling to {activeStop.WorldName}.",
                    new Dictionary<string, string?>
                    {
                        ["preparationState"] = "MarketBoardCloseRequested",
                    });
                state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
                return;
            }

            UpdateStatus(runner.RecordMarketBoardClosedBeforeTravel());
        }

        if (preparation != null)
        {
            if (!context.IsCurrentWorldAvailable)
            {
                ObserveTravelPreparationOperation(
                    preparation,
                    MarketAcquisitionRouteOperationDisposition.Pending,
                    "Waiting for current world information before travel preparation.",
                    new Dictionary<string, string?>
                    {
                        ["preparationState"] = "CurrentWorldUnavailable",
                    });
                UpdateStatus(runner.RecordCurrentWorldUnavailable());
                state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                return;
            }

            if (activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            {
                ObserveTravelPreparationOperation(
                    preparation,
                    MarketAcquisitionRouteOperationDisposition.Succeeded,
                    $"Already on {activeStop.WorldName}; travel preparation complete.",
                    new Dictionary<string, string?>
                    {
                        ["preparationState"] = "TargetWorldReached",
                    });
            }
            else
            {
                var preflight = uiAutomation.CheckTravelPreflight();
                if (!preflight.CanSendCommand)
                {
                    UpdateStatus(runner.RecordTravelBlockedByUi(preflight));
                    ObserveTravelPreparationOperation(
                        preparation,
                        MarketAcquisitionRouteOperationDisposition.Pending,
                        preflight.Message,
                        new Dictionary<string, string?>
                        {
                            ["preparationState"] = "UiBlocked",
                            ["blockingAddons"] = string.Join(", ", preflight.BlockingAddons),
                        });
                    state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                    return;
                }

                ObserveTravelPreparationOperation(
                    preparation,
                    MarketAcquisitionRouteOperationDisposition.Succeeded,
                    $"Travel UI preflight passed for {activeStop.WorldName}.",
                    new Dictionary<string, string?>
                    {
                        ["preparationState"] = "ReadyToTravel",
                    });
            }
        }

        var needsWorldTravel = !activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase);
        var travelOperation = needsWorldTravel
            ? EnsureWorldTravelArrivalOperation(activeStop, currentWorld)
            : null;
        var travelResult = runner.PreparePendingStopForCurrentWorld(
            context.IsCurrentWorldAvailable,
            currentWorld,
            uiAutomation.ProcessCommand);
        if (travelOperation != null && travelResult.Success && runner.ActiveStop?.Status == "TravelCommandSent")
        {
            var lease = activeTravelLease ?? throw new InvalidOperationException("World travel command was accepted without a travel lease.");
            activeTravelLease = lease with { IsOwned = true };
            ObserveWorldTravelOperation(
                travelOperation,
                MarketAcquisitionRouteOperationDisposition.Pending,
                $"Lifestream command accepted for {activeStop.WorldName}; waiting for world arrival.",
                new Dictionary<string, string?>
                {
                    ["commandAccepted"] = "True",
                    ["leaseId"] = activeTravelLease.LeaseId,
                    ["leaseOwnership"] = "Owned",
                });
        }
        else if (travelOperation != null && !travelResult.Success)
        {
            ObserveWorldTravelOperation(
                travelOperation,
                MarketAcquisitionRouteOperationDisposition.Failed,
                travelResult.Message,
                new Dictionary<string, string?>
                {
                    ["commandAccepted"] = "False",
                    ["leaseId"] = activeTravelLease?.LeaseId,
                    ["leaseOwnership"] = "NotOwned",
                });
            activeTravelLease = null;
        }
        UpdateStatus(travelResult);
        if (!travelResult.Success && string.Equals(runner.State, "Failed", StringComparison.OrdinalIgnoreCase))
            UpdateStatus(FailRoute(travelResult.Message));
        state.NextRouteMonitorUtc = clock.UtcNow.AddSeconds(2);
    }

    private MarketAcquisitionRouteOperationSnapshot EnsureTravelPreparationOperation(
        MarketAcquisitionGuidedRouteStop activeStop)
    {
        if (operationExecutor.ActiveSnapshot is { } active)
        {
            if (active.Kind != MarketAcquisitionRouteOperationKind.TravelPreparation)
                throw new InvalidOperationException($"Cannot prepare travel while {active.Kind} operation {active.OperationId} is active.");

            return active;
        }

        var operation = operationExecutor.Begin(new MarketAcquisitionRouteOperationStart
        {
            OperationId = $"{state.ProgressNonce}:travel-preparation:{++operationSequence}",
            Kind = MarketAcquisitionRouteOperationKind.TravelPreparation,
            StartedAtUtc = clock.UtcNow,
            StartedAtMonotonicMilliseconds = clock.MonotonicMilliseconds,
            Timeout = TravelPreparationOperationTimeout,
            TimeoutDisposition = MarketAcquisitionRouteOperationDisposition.Failed,
            TimeoutMessage =
                $"Travel preparation timed out after {TravelPreparationOperationTimeout.TotalSeconds:N0}s while waiting to travel to {activeStop.WorldName}.",
            Context = new Dictionary<string, string?>
            {
                ["world"] = activeStop.WorldName,
                ["timeoutPolicySource"] = "NightmareToolsDefaultBoundProvisional",
            },
        });
        runner.RecordRouteOperationSnapshot(operation);
        return operation;
    }

    private MarketAcquisitionRouteOperationSnapshot ObserveTravelPreparationOperation(
        MarketAcquisitionRouteOperationSnapshot operation,
        MarketAcquisitionRouteOperationDisposition disposition,
        string message,
        IReadOnlyDictionary<string, string?> details)
    {
        var result = operationExecutor.Observe(
            new MarketAcquisitionRouteOperationObservation
            {
                OperationId = operation.OperationId,
                Disposition = disposition,
                Message = message,
                Details = details,
            },
            clock.UtcNow,
            clock.MonotonicMilliseconds);
        if (!result.Accepted || result.Snapshot == null)
            throw new InvalidOperationException(result.Message);

        runner.RecordRouteOperationSnapshot(result.Snapshot);
        return result.Snapshot;
    }

    private MarketAcquisitionRouteOperationSnapshot EnsureWorldTravelArrivalOperation(
        MarketAcquisitionGuidedRouteStop activeStop,
        string? currentWorld)
    {
        if (operationExecutor.ActiveSnapshot is { } active)
        {
            if (active.Kind != MarketAcquisitionRouteOperationKind.Travel)
                throw new InvalidOperationException($"Cannot start world travel while {active.Kind} operation {active.OperationId} is active.");

            return active;
        }

        var operation = operationExecutor.Begin(new MarketAcquisitionRouteOperationStart
        {
            OperationId = $"{state.ProgressNonce}:world-travel:{++operationSequence}",
            Kind = MarketAcquisitionRouteOperationKind.Travel,
            StartedAtUtc = clock.UtcNow,
            StartedAtMonotonicMilliseconds = clock.MonotonicMilliseconds,
            Timeout = WorldTravelArrivalOperationTimeout,
            TimeoutDisposition = MarketAcquisitionRouteOperationDisposition.Failed,
            TimeoutMessage =
                $"World travel timed out after {WorldTravelArrivalOperationTimeout.TotalSeconds:N0}s while waiting to arrive on {activeStop.WorldName}.",
            Context = new Dictionary<string, string?>
            {
                ["world"] = activeStop.WorldName,
                ["sourceWorld"] = currentWorld,
                ["dependency"] = "Lifestream",
                ["timeoutPolicySource"] = "NightmareToolsDefaultBoundProvisional",
            },
        });
        activeTravelLease = new MarketAcquisitionTravelLease
        {
            LeaseId = $"{state.ProgressNonce}:lifestream:{operation.OperationId}",
            RouteRunId = state.ProgressNonce,
            OperationId = operation.OperationId,
            Dependency = "Lifestream",
            TargetWorld = activeStop.WorldName,
            IsOwned = false,
        };
        runner.RecordRouteOperationSnapshot(operation);
        runner.RecordRouteCleanup(
            "Travel lease created before Lifestream command dispatch.",
            CreateTravelCleanupDetails(activeTravelLease, "Start", "LeaseCreated", unresolvedExternalAutomation: false));
        return operation;
    }

    private MarketAcquisitionRouteOperationSnapshot ObserveWorldTravelOperation(
        MarketAcquisitionRouteOperationSnapshot operation,
        MarketAcquisitionRouteOperationDisposition disposition,
        string message,
        IReadOnlyDictionary<string, string?> details)
    {
        var result = operationExecutor.Observe(
            new MarketAcquisitionRouteOperationObservation
            {
                OperationId = operation.OperationId,
                Disposition = disposition,
                Message = message,
                Details = details,
            },
            clock.UtcNow,
            clock.MonotonicMilliseconds);
        if (!result.Accepted || result.Snapshot == null)
            throw new InvalidOperationException(result.Message);

        runner.RecordRouteOperationSnapshot(result.Snapshot);
        return result.Snapshot;
    }

    private bool EnsureRouteTravelUiIsClear()
    {
        var preflight = uiAutomation.CheckTravelPreflight();
        if (preflight.CanSendCommand)
            return true;

        UpdateStatus(runner.RecordTravelBlockedByUi(preflight));
        return false;
    }

    private void HandleWorldScopedStop(MarketAcquisitionGuidedRouteStop activeStop, string currentWorld)
    {
        if (!activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
        {
            if (operationExecutor.ActiveSnapshot is { Kind: MarketAcquisitionRouteOperationKind.Travel } travel)
            {
                ObserveWorldTravelOperation(
                    travel,
                    MarketAcquisitionRouteOperationDisposition.Pending,
                    $"Waiting for Lifestream arrival on {activeStop.WorldName}; current world is {currentWorld}.",
                    new Dictionary<string, string?>
                    {
                        ["currentWorld"] = currentWorld,
                        ["leaseId"] = activeTravelLease?.LeaseId,
                    });
            }
            UpdateStatus(runner.RecordCurrentWorld(currentWorld));
            return;
        }

        if (operationExecutor.ActiveSnapshot is { Kind: MarketAcquisitionRouteOperationKind.Travel } travelArrival)
        {
            ObserveWorldTravelOperation(
                travelArrival,
                MarketAcquisitionRouteOperationDisposition.Succeeded,
                $"Confirmed Lifestream arrival on {activeStop.WorldName}.",
                new Dictionary<string, string?>
                {
                    ["currentWorld"] = currentWorld,
                    ["leaseId"] = activeTravelLease?.LeaseId,
                    ["leaseOwnership"] = activeTravelLease?.IsOwned.ToString(),
                });
            activeTravelLease = null;
        }

        if (string.Equals(activeStop.Status, "TravelCommandSent", StringComparison.OrdinalIgnoreCase))
            UpdateStatus(runner.RecordCurrentWorld(currentWorld));

        if (runner.ActiveStop?.Status == "Arrived")
            HandleArrivedStop(currentWorld);
    }

    private void HandleArrivedStop(string currentWorld)
    {
        var claimed = claimedRequest ?? throw new InvalidOperationException("No dashboard request is accepted.");
        if (!runner.SearchSubmitted)
        {
            var approachResult = marketBoard.OpenOrApproachMarketBoard();
            if (approachResult.ActionKind == MarketBoardApproachActionKind.NavigationStarted)
            {
                activeApproachLease = new MarketAcquisitionApproachLease
                {
                    LeaseId = $"{state.ProgressNonce}:vnavmesh:{++operationSequence}",
                    RouteRunId = state.ProgressNonce,
                    OperationId = $"{state.ProgressNonce}:market-board-approach:{operationSequence}",
                    Dependency = "VNavmesh",
                };
                runner.RecordRouteCleanup(
                    "Route-owned vnavmesh approach started.",
                    new Dictionary<string, string?>
                    {
                        ["routeRunId"] = activeApproachLease.RouteRunId,
                        ["operationId"] = activeApproachLease.OperationId,
                        ["leaseId"] = activeApproachLease.LeaseId,
                        ["dependency"] = activeApproachLease.Dependency,
                        ["adapterCapability"] = "GlobalPathStopOnly",
                    });
            }
            else if (approachResult.ReadyToSearch)
            {
                activeApproachLease = null;
            }
            UpdateStatus(runner.RecordMarketBoardApproach(approachResult));
            if (approachResult.MarketBoardTravelNeeded)
            {
                if (!EnsureRouteTravelUiIsClear())
                {
                    state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                    return;
                }

                UpdateStatus(runner.ExecuteMarketBoardTravelCommand(uiAutomation.ProcessCommand));
                state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(750);
                return;
            }

            if (!approachResult.ReadyToSearch)
            {
                state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
                return;
            }

            var activeLine = GetActiveRouteLine(claimed);
            var operation = EnsureItemSearchOperation(activeLine, currentWorld);
            var deadline = operationExecutor.CheckDeadline(clock.UtcNow, clock.MonotonicMilliseconds);
            if (deadline.Snapshot is { IsTerminal: true } timedOut)
            {
                runner.RecordRouteOperationSnapshot(timedOut);
                UpdateStatus(FailRoute(timedOut.Message));
                return;
            }

            var searchResult = marketBoard.SearchItem(activeLine.ItemId, activeLine.ItemName);
            UpdateStatus(runner.RecordSearchResult(searchResult, clock.UtcNow));
            var operationResult = ObserveItemSearchOperation(operation, searchResult);
            if (operationResult.Disposition == MarketAcquisitionRouteOperationDisposition.Pending)
            {
                state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                return;
            }

            if (operationResult.Disposition != MarketAcquisitionRouteOperationDisposition.Succeeded)
            {
                UpdateStatus(FailRoute(operationResult.Message));
                return;
            }
        }

        state.NextRouteMonitorUtc = clock.UtcNow;
        UpdateStatus(runner.BeginProbe($"Arrived on {currentWorld}. Reading live listings for {FormatItem(GetActiveRouteLine(claimed))}."));
        state.ProbeRunning = true;
        ProbeLiveMarketBoard();
    }

    private MarketAcquisitionRouteOperationSnapshot EnsureItemSearchOperation(
        MarketAcquisitionRequestView activeLine,
        string currentWorld)
    {
        if (operationExecutor.ActiveSnapshot is { } active)
        {
            if (active.Kind != MarketAcquisitionRouteOperationKind.ItemSearch)
                throw new InvalidOperationException($"Cannot start item search while {active.Kind} operation {active.OperationId} is active.");

            return active;
        }

        var operation = operationExecutor.Begin(new MarketAcquisitionRouteOperationStart
        {
            OperationId = $"{state.ProgressNonce}:item-search:{++operationSequence}",
            Kind = MarketAcquisitionRouteOperationKind.ItemSearch,
            StartedAtUtc = clock.UtcNow,
            StartedAtMonotonicMilliseconds = clock.MonotonicMilliseconds,
            Timeout = MarketBoardItemSearchOperationTimeout,
            TimeoutDisposition = MarketAcquisitionRouteOperationDisposition.Failed,
            TimeoutMessage =
                $"Market board item search timed out after {MarketBoardItemSearchOperationTimeout.TotalSeconds:N0}s while waiting for listings for {FormatItem(activeLine)}.",
            Context = new Dictionary<string, string?>
            {
                ["world"] = currentWorld,
                ["lineId"] = runner.ActiveStop?.ActiveItemSubtask?.LineId,
                ["itemId"] = activeLine.ItemId.ToString(),
                ["itemName"] = activeLine.ItemName,
            },
        });
        runner.RecordRouteOperationSnapshot(operation);
        return operation;
    }

    private MarketAcquisitionRouteOperationSnapshot ObserveItemSearchOperation(
        MarketAcquisitionRouteOperationSnapshot operation,
        MarketBoardItemSearchResult searchResult)
    {
        var disposition = ClassifyItemSearchResult(searchResult);
        var message = disposition == MarketAcquisitionRouteOperationDisposition.Failed &&
                      !string.Equals(searchResult.Status, "SearchSubmitFailed", StringComparison.OrdinalIgnoreCase)
            ? $"Market board item search returned unsupported terminal status {searchResult.Status}. {searchResult.Message}"
            : searchResult.Message;
        var result = operationExecutor.Observe(
            new MarketAcquisitionRouteOperationObservation
            {
                OperationId = operation.OperationId,
                Disposition = disposition,
                Message = message,
                Details = searchResult.Details,
            },
            clock.UtcNow,
            clock.MonotonicMilliseconds);
        if (!result.Accepted || result.Snapshot == null)
            throw new InvalidOperationException(result.Message);

        runner.RecordRouteOperationSnapshot(result.Snapshot);
        return result.Snapshot;
    }

    internal static MarketAcquisitionRouteOperationDisposition ClassifyItemSearchResult(
        MarketBoardItemSearchResult searchResult)
    {
        ArgumentNullException.ThrowIfNull(searchResult);
        return searchResult.ReadyForListings
            ? MarketAcquisitionRouteOperationDisposition.Succeeded
            : searchResult.IsInProgress
                ? MarketAcquisitionRouteOperationDisposition.Pending
                : MarketAcquisitionRouteOperationDisposition.Failed;
    }

    private void ProbeLiveMarketBoardCore(
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claimed,
        bool recordRouteResult)
    {
        var activeLine = GetActiveRouteLine(claimed);
        var activeSubtask = recordRouteResult ? runner.ActiveStop?.ActiveItemSubtask : null;
        var currentWorld = context.GetCurrentWorldName();

        state.MarketBoardReconciliation = null;
        state.LiveCandidatePlan = null;
        state.MarketBoardReadResult = listingReadAccumulator.Merge(marketBoard.ReadCurrentListings(currentWorld));

        var canBuildLiveCandidatePlan = state.MarketBoardReadResult.Status is "Ready" or "NoListings";
        state.MarketBoardReconciliation = state.MarketBoardReadResult.Status == "Ready"
            ? activeSubtask == null
                ? MarketBoardListingReconciler.Reconcile(plan, currentWorld, state.MarketBoardReadResult.ItemId, state.MarketBoardReadResult.Listings)
                : MarketBoardListingReconciler.Reconcile(plan, activeSubtask, currentWorld, state.MarketBoardReadResult.ItemId, state.MarketBoardReadResult.Listings)
            : null;
        if (!state.MarketBoardReadResult.IsFresh)
        {
            if (runner.IsRunning)
                runner.RecordListingReadPending(currentWorld, state.MarketBoardReadResult);

            state.AcquisitionStatus = state.MarketBoardReadResult.Message;
            return;
        }

        var totals = recordRouteResult
            ? ResolveActiveRouteLinePurchaseTotals(activeSubtask)
            : default;
        var candidateRead = activeSubtask is null
            ? state.MarketBoardReadResult
            : ExcludeSunkOutfitterListings(state.MarketBoardReadResult);
        state.LiveCandidatePlan = canBuildLiveCandidatePlan
            ? activeSubtask == null
                ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, currentWorld, state.MarketBoardReadResult, totals.PurchasedQuantity, totals.SpentGil)
                : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, activeSubtask, currentWorld, candidateRead, totals.PurchasedQuantity, totals.SpentGil)
            : null;
        if (state.LiveCandidatePlan != null &&
            TryContinueVisibleListingRead(
                currentWorld,
                state.MarketBoardReadResult,
                state.LiveCandidatePlan,
                requireRunningRoute: recordRouteResult))
            return;

        if (state.MarketBoardReadResult.IsFresh)
            ReportMarketObservation(claimed, activeLine, activeSubtask, currentWorld, state.MarketBoardReadResult);

        MarketAcquisitionRouteActionResult? authorityFailure = null;
        if (recordRouteResult && !state.EvidenceRefreshOnly && activeSubtask is not null && state.LiveCandidatePlan is not null && outfitterAuthority is not null)
        {
            authorityFailure = EnforceOutfitterCandidateAuthority(activeSubtask, state.LiveCandidatePlan);
        }
        var probeResult = authorityFailure is null && recordRouteResult && runner.IsRunning && runner.ActiveStop is { Status: "Arrived" } && state.LiveCandidatePlan != null
            ? runner.RecordProbe(currentWorld, state.LiveCandidatePlan, allowPurchases: !state.EvidenceRefreshOnly)
            : null;
        if (probeResult?.Success == true && state.LiveCandidatePlan != null)
        {
            evidence.RecordProbeVisit(currentWorld, activeLine, activeSubtask, state.LiveCandidatePlan, claimed.Id, state.ProgressNonce);
            if (state.EvidenceRefreshOnly && runner.State.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                ReportRouteProgress(includeEvidenceRefresh: true);
        }
        EvaluateOutfitterRouteCompletion();

        state.AcquisitionStatus = state.MarketBoardReconciliation == null
            ? state.MarketBoardReadResult.Message
            : $"Live listing reconciliation {state.MarketBoardReconciliation.Status}; live candidates {state.LiveCandidatePlan?.Status ?? "Unavailable"}.";
        if (probeResult != null)
            state.AcquisitionStatus = $"{state.AcquisitionStatus} Route: {probeResult.Message}";
    }

    private bool TryContinueVisibleListingRead(
        string currentWorld,
        MarketBoardReadResult readResult,
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        bool requireRunningRoute = true)
    {
        if ((requireRunningRoute && !runner.IsRunning) ||
            !listingReadAccumulator.TryBeginContinuation(readResult, candidatePlan, out var continuation))
            return false;

        if (!uiAutomation.TryScrollMarketBoardListingsToRow(continuation.RequestedRow, out var scrollMessage))
        {
            state.AcquisitionStatus = scrollMessage;
            if (requireRunningRoute)
            {
                var scrollPending = runner.RecordListingReadPending(
                    currentWorld,
                    readResult with { Message = $"{continuation.Message} {scrollMessage}" });
                state.AcquisitionStatus = scrollPending.Success ? scrollMessage : scrollPending.Message;
            }

            return true;
        }

        var message = $"{continuation.Message} {scrollMessage}";
        if (!requireRunningRoute)
        {
            state.AcquisitionStatus = message;
            return true;
        }

        var pending = runner.RecordListingReadPending(currentWorld, readResult with { Message = message });
        state.AcquisitionStatus = pending.Success ? message : pending.Message;
        state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
        return true;
    }

    private MarketAcquisitionRequestView GetActiveRouteLine(MarketAcquisitionRequestView claimed)
    {
        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
        return activeSubtask == null
            ? claimed
            : claimed with
            {
                ItemId = activeSubtask.ItemId,
                ItemName = activeSubtask.ItemName,
                QuantityMode = activeSubtask.QuantityMode,
                Quantity = activeSubtask.RequestedQuantity,
                HqPolicy = activeSubtask.HqPolicy,
                MaxUnitPrice = activeSubtask.MaxUnitPrice,
                MaxTotalGil = activeSubtask.GilCap,
            };
    }

    private MarketAcquisitionRouteLinePurchaseTotals ResolveActiveRouteLinePurchaseTotals(MarketAcquisitionWorldItemSubtask? activeSubtask)
    {
        if (activeSubtask == null)
            return new MarketAcquisitionRouteLinePurchaseTotals(state.ActiveWorldPurchasedQuantity, state.ActiveWorldSpentGil);

        var completed = runner.GetLinePurchaseTotals(activeSubtask.LineId);
        return new MarketAcquisitionRouteLinePurchaseTotals(
            checked(completed.PurchasedQuantity + state.ActiveLinePurchasedQuantity),
            checked(completed.SpentGil + state.ActiveLineSpentGil));
    }

    private static string FormatItem(MarketAcquisitionRequestView line) =>
        string.IsNullOrWhiteSpace(line.ItemName) ? line.ItemId.ToString() : $"{line.ItemName} ({line.ItemId})";

    public void BeginNextWorldPurchase()
    {
        var activeStop = runner.ActiveStop;
        if (activeStop is not { Status: "Purchasing" })
            return;

        var claimed = claimedRequest ?? throw new InvalidOperationException("No dashboard request is accepted.");
        var plan = runner.ActivePlan ?? throw new InvalidOperationException("No market acquisition plan is prepared.");
        var currentWorld = context.GetCurrentWorldName();
        if (!activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cannot purchase on {currentWorld}; active route stop is {activeStop.WorldName}.");

        if (!string.Equals(state.ActiveWorldPurchaseBatchWorld, activeStop.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            state.ActiveWorldPurchaseBatchWorld = activeStop.WorldName;
            state.ActiveWorldPurchasedQuantity = 0;
            state.ActiveWorldSpentGil = 0;
        }

        var activeLine = GetActiveRouteLine(claimed);
        var activeLineId = GetActiveRouteLineId(claimed);
        if (!string.Equals(state.ActivePurchaseLineId, activeLineId, StringComparison.Ordinal))
        {
            state.ActivePurchaseLineId = activeLineId;
            state.ActiveLinePurchasedQuantity = 0;
            state.ActiveLineSpentGil = 0;
            if (activeStop.ActiveItemSubtask != null)
                ReportAcquisitionLineProgress(activeStop.ActiveItemSubtask, "Running", 0, 0,
                    $"Started purchasing {FormatItem(activeLine)} on {activeStop.WorldName}.");
        }

        var freshRead = listingReadAccumulator.Merge(marketBoard.ReadCurrentListings(currentWorld));
        state.MarketBoardReadResult = freshRead;
        if (!freshRead.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
        {
            if (!freshRead.IsFresh)
            {
                runner.RecordListingReadPending(currentWorld, freshRead);
                state.AcquisitionStatus = $"Waiting for fresh market listings. {freshRead.Message}";
                state.NextRouteMonitorUtc = clock.UtcNow.Add(RouteMonitorInterval);
                return;
            }

            throw new InvalidOperationException(freshRead.Message);
        }

        var totals = ResolveActiveRouteLinePurchaseTotals(activeStop.ActiveItemSubtask);
        var candidateRead = activeStop.ActiveItemSubtask is null
            ? freshRead
            : ExcludeSunkOutfitterListings(freshRead);
        state.LiveCandidatePlan = activeStop.ActiveItemSubtask == null
            ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, currentWorld, freshRead, totals.PurchasedQuantity, totals.SpentGil)
            : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(activeLine, plan, activeStop.ActiveItemSubtask, currentWorld, candidateRead, totals.PurchasedQuantity, totals.SpentGil);
        if (TryContinueVisibleListingRead(currentWorld, freshRead, state.LiveCandidatePlan))
            return;

        if (outfitterAuthority is not null && activeStop.ActiveItemSubtask is { } authoritySubtask)
        {
            if (EnforceOutfitterCandidateAuthority(authoritySubtask, state.LiveCandidatePlan) is not null)
            {
                return;
            }
        }

        if (state.ExecutionMode == MarketAcquisitionExecutionMode.DryRun)
        {
            SimulateDryRunPurchase(currentWorld);
            return;
        }

        var selection = purchase.ExecuteFirstCandidate(state.LiveCandidatePlan, freshRead);
        var now = clock.UtcNow;
        purchaseAutomation.RecordPurchaseSelection(selection, now, MarketBoardPurchaseConfirmationWatchdog);
        runner.RecordAutomationSnapshot(CreatePurchaseSelectionSnapshot(selection));

        if (selection.Status.Equals("NoCandidate", StringComparison.OrdinalIgnoreCase))
        {
            if (ShouldFailWorldPurchaseBatchOnNoCandidate(state.LiveCandidatePlan))
            {
                UpdateStatus(FailRoute(state.LiveCandidatePlan.Message));
                ReportRouteProgress();
                return;
            }

            CompleteActiveWorldPurchaseBatch(currentWorld);
            return;
        }

        if (ClassifyPurchaseSelectionOutcome(selection.Status) == MarketBoardAutomationOutcome.Recoverable)
        {
            state.AcquisitionStatus = $"Purchase: {selection.Status}. {selection.Message}";
            state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
            return;
        }

        if (!selection.Status.Equals("PurchaseSelectionSent", StringComparison.OrdinalIgnoreCase) || selection.Candidate == null)
        {
            UpdateStatus(FailRoute($"World purchase batch stopped: {selection.Message}"));
            ReportRouteProgress();
            return;
        }

        purchaseAutomation.ScheduleNextMonitor(now, MarketBoardPurchaseInitialMonitorDelay);
        state.AcquisitionStatus = $"Purchase: {selection.Status}. {selection.Message}";
    }

    private MarketBoardReadResult ExcludeSunkOutfitterListings(MarketBoardReadResult readResult)
    {
        if (outfitterAuthority is null || outfitterAuthority.State.SunkPurchases.Count == 0)
            return readResult;

        var remaining = new List<MarketBoardLiveListing>(readResult.Listings.Count);
        foreach (var listing in readResult.Listings)
        {
            if (!outfitterAuthority.IsSunkListing(listing))
                remaining.Add(listing);
        }

        var excludedCount = readResult.Listings.Count - remaining.Count;
        return excludedCount == 0
            ? readResult
            : readResult with
            {
                ReportedListingCount = Math.Max(0, readResult.ReportedListingCount - excludedCount),
                Listings = remaining,
            };
    }

    public MarketAcquisitionRouteEngineTickResult MonitorMarketBoardPurchase()
    {
        var previousSession = purchaseAutomation.PurchaseSession;
        if (previousSession?.IsActive != true)
            return MarketAcquisitionRouteEngineTickResult.Idle();

        var now = clock.UtcNow;
        if (!purchaseAutomation.IsMonitorDue(now))
            return MarketAcquisitionRouteEngineTickResult.Idle("Waiting for purchase monitor tick.");

        if (outfitterAuthority is not null && previousSession.Phase == MarketBoardPurchaseSessionPhase.WaitingForListingRemoval)
            return MonitorOutfitterServerPurchaseEvidence(previousSession, now);

        try
        {
            var tick = purchaseAutomation.MonitorPurchase(
                now,
                MarketBoardPurchaseMonitorInterval,
                MarketBoardPurchaseListingRemovalWatchdog,
                candidate => outfitterAuthority is null
                    ? purchase.TryConfirmPendingPurchase(candidate)
                    : purchase.TryConfirmPendingPurchase(candidate, CreatePurchaseIntentContext()),
                () => marketBoard.ReadCurrentListings(context.GetCurrentWorldName()),
                monitorListingRemoval: outfitterAuthority is null);
            if (!tick.DidWork)
                return MarketAcquisitionRouteEngineTickResult.Idle("Purchase monitor had no due work.");

            ApplyPurchaseMonitorTick(tick, previousSession);
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, purchaseAutomation.NextMonitorUtc);
        }
        catch (Exception ex)
        {
            purchaseAutomation.RecordMonitorFailure("PurchaseMonitorFailed", ex.Message);
            state.AcquisitionStatus = $"Purchase monitor failed: {ex.Message}";
            FailRoute(state.AcquisitionStatus, ex);
            ReportRouteProgress();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, purchaseAutomation.NextMonitorUtc);
        }
    }

    private MarketAcquisitionRouteEngineTickResult MonitorOutfitterServerPurchaseEvidence(
        MarketBoardPurchaseSession session,
        DateTimeOffset nowUtc)
    {
        if (!purchase.HasServerPurchaseEvidence)
        {
            return StopForTerminalPurchaseEvidence(
                "Server purchase evidence became unavailable after confirmation submission; purchase outcome is indeterminate.");
        }

        var advance = purchase.AdvancePurchaseEvidence(nowUtc);
        if (advance.Status == MarketPurchaseEvidenceAdvanceStatus.PersistenceFailed)
            return StopForTerminalPurchaseEvidence(advance.Message);
        var evidenceState = advance.State ?? purchase.PurchaseEvidenceState;
        switch (evidenceState)
        {
            case PendingMarketPurchase:
                purchaseAutomation.ScheduleNextMonitor(nowUtc, MarketBoardPurchaseMonitorInterval);
                state.AcquisitionStatus = "Purchase: waiting for durable server confirmation evidence.";
                return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, purchaseAutomation.NextMonitorUtc);
            case ConfirmedMarketPurchase confirmed:
                return ApplyConfirmedOutfitterPurchase(session, confirmed, nowUtc);
            case TimedOutIndeterminateMarketPurchase timedOut:
                return StopForTerminalPurchaseEvidence(
                    $"Purchase evidence timed out for intent {timedOut.Intent.IntentId}; reconcile outcome before any retry.");
            case ConflictingMarketPurchasePacket conflicting:
                return StopForTerminalPurchaseEvidence(
                    $"A conflicting purchase packet followed intent {conflicting.Intent.IntentId}; reconcile outcome before any retry.");
            default:
                return StopForTerminalPurchaseEvidence(
                    "Durable purchase intent disappeared before terminal server evidence was applied.");
        }
    }

    private MarketAcquisitionRouteEngineTickResult ApplyConfirmedOutfitterPurchase(
        MarketBoardPurchaseSession session,
        ConfirmedMarketPurchase confirmed,
        DateTimeOffset nowUtc)
    {
        var candidate = session.Candidate;
        var intent = confirmed.Intent;
        var lineId = GetActiveRouteLineId(claimedRequest!);
        if (!intent.RouteId.Equals(claimedRequest!.Id, StringComparison.Ordinal) ||
            !intent.RouteRunId.Equals(state.ProgressNonce, StringComparison.Ordinal) ||
            !intent.AttemptId.Equals(state.ProgressNonce, StringComparison.Ordinal) ||
            !intent.LineId.Equals(lineId, StringComparison.Ordinal) ||
            intent.ItemId != candidate.ItemId || intent.IsHighQuality != candidate.IsHq ||
            intent.Quantity != candidate.Quantity || intent.ListingId != candidate.ListingId ||
            intent.RetainerId != candidate.RetainerId || intent.UnitPrice != candidate.UnitPrice ||
            intent.TotalGil != candidate.TotalGil ||
            !intent.WorldName.Equals(candidate.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            return StopForTerminalPurchaseEvidence(
                "Confirmed server evidence does not match the active route, line, world, or exact listing intent.");
        }

        try
        {
            var activePlan = runner.ActivePlan ?? throw new InvalidOperationException("Active purchase plan is unavailable.");
            outfitterAuthority!.RecordPurchase(lineId, candidate, activePlan);
            var resolved = purchase.ResolvePurchaseEvidence(
                intent.IntentId,
                MarketPurchaseTerminalDisposition.AppliedExactlyOnce,
                nowUtc,
                $"Applied exact Squire sunk receipt for listing {candidate.ListingId}.");
            if (!resolved.IsResolved)
                return StopForTerminalPurchaseEvidence(resolved.Message);

            state.ActiveWorldPurchasedQuantity = checked(state.ActiveWorldPurchasedQuantity + candidate.Quantity);
            state.ActiveWorldSpentGil = checked(state.ActiveWorldSpentGil + candidate.TotalGil);
            state.ActiveLinePurchasedQuantity = checked(state.ActiveLinePurchasedQuantity + candidate.Quantity);
            state.ActiveLineSpentGil = checked(state.ActiveLineSpentGil + candidate.TotalGil);
            state.AcquisitionStatus = "Purchase: confirmed by server packet and persisted exactly once.";
            ReportConfirmedPurchase(candidate, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil);
            ClearMarketBoardAutomationState();
            BeginNextWorldPurchase();
            return MarketAcquisitionRouteEngineTickResult.Worked(state.AcquisitionStatus, state.NextRouteMonitorUtc);
        }
        catch (Exception exception)
        {
            return StopForTerminalPurchaseEvidence(
                $"Confirmed purchase could not be applied safely: {exception.Message}");
        }
    }

    private MarketAcquisitionRouteEngineTickResult StopForTerminalPurchaseEvidence(string message)
    {
        ClearMarketBoardAutomationState();
        outfitterAuthority?.Pause(message);
        state.AcquisitionStatus = message;
        UpdateStatus(FailRoute(message));
        ReportRouteProgress();
        return MarketAcquisitionRouteEngineTickResult.Worked(message, state.NextRouteMonitorUtc);
    }

    private MarketPurchaseIntentContext CreatePurchaseIntentContext()
    {
        var claimed = claimedRequest ?? throw new InvalidOperationException("No claimed route can arm purchase evidence.");
        return new()
        {
            RouteId = claimed.Id,
            RouteRunId = state.ProgressNonce,
            AttemptId = state.ProgressNonce,
            LineId = GetActiveRouteLineId(claimed),
            EvidenceTimeout = MarketBoardPurchaseListingRemovalWatchdog,
        };
    }

    private void ApplyPurchaseMonitorTick(MarketBoardPurchaseMonitorTick tick, MarketBoardPurchaseSession previousSession)
    {
        if (tick.ConfirmationResult != null)
        {
            var candidate = tick.ConfirmationResult.Candidate ?? previousSession.Candidate;
            runner.RecordAutomationSnapshot(CreatePurchaseConfirmationSnapshot(tick.ConfirmationResult, candidate));
        }

        if (tick.FreshRead != null)
        {
            state.MarketBoardReadResult = tick.FreshRead;
            if (tick.FreshReadSession != null)
                runner.RecordAutomationSnapshot(tick.FreshReadSession.CreateFreshReadSnapshot(tick.FreshRead));
        }

        var session = tick.Session ?? previousSession;
        state.AcquisitionStatus = $"Purchase: {session.Status}. {session.Message}";
        if (session.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = session.Candidate;
            state.ActiveWorldPurchasedQuantity = checked(state.ActiveWorldPurchasedQuantity + candidate.Quantity);
            state.ActiveWorldSpentGil = checked(state.ActiveWorldSpentGil + candidate.TotalGil);
            state.ActiveLinePurchasedQuantity = checked(state.ActiveLinePurchasedQuantity + candidate.Quantity);
            state.ActiveLineSpentGil = checked(state.ActiveLineSpentGil + candidate.TotalGil);
            outfitterAuthority?.RecordPurchase(GetActiveRouteLineId(claimedRequest!), candidate, runner.ActivePlan);
            ReportConfirmedPurchase(candidate, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil);
            ClearMarketBoardAutomationState();
            if (state.MarketBoardReadResult?.Status is "MarketBoardNotOpen" or "NoListings")
                CompleteActiveWorldPurchaseBatch(context.GetCurrentWorldName());
            else
                BeginNextWorldPurchase();
        }
        else if (!session.IsActive)
        {
            var message = $"World purchase batch stopped: {session.Message}";
            outfitterAuthority?.Pause(message);
            UpdateStatus(FailRoute(message));
            ReportRouteProgress();
        }
    }

    private void CompleteActiveWorldPurchaseBatch(string currentWorld)
    {
        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
        if (activeSubtask != null)
        {
            var lineStatus = ResolveZeroPurchaseLineStatus(state.LiveCandidatePlan, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil);
            ReportAcquisitionLineProgress(activeSubtask, lineStatus, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil,
                state.ExecutionMode == MarketAcquisitionExecutionMode.DryRun
                    ? $"Dry run for {FormatItem(GetActiveRouteLine(claimedRequest!))} on {currentWorld}: would purchase {state.ActiveLinePurchasedQuantity:N0}, would spend {state.ActiveLineSpentGil:N0} gil."
                    : $"Completed {FormatItem(GetActiveRouteLine(claimedRequest!))} on {currentWorld}: purchased {state.ActiveLinePurchasedQuantity:N0}, spent {state.ActiveLineSpentGil:N0} gil.");
        }

        var result = runner.RecordWorldPurchaseBatchComplete(
            currentWorld,
            activeSubtask == null ? state.ActiveWorldPurchasedQuantity : state.ActiveLinePurchasedQuantity,
            activeSubtask == null ? state.ActiveWorldSpentGil : state.ActiveLineSpentGil,
            state.ActiveLinePurchasedQuantity == 0 && state.ActiveLineSpentGil == 0
                ? ResolveZeroPurchaseLineStatus(state.LiveCandidatePlan, state.ActiveLinePurchasedQuantity, state.ActiveLineSpentGil)
                : null,
            state.ActiveLinePurchasedQuantity == 0 && state.ActiveLineSpentGil == 0 ? state.LiveCandidatePlan?.Message : null);
        state.AcquisitionStatus = result.Message;
        ClearMarketBoardAutomationState();

        var nextStop = runner.ActiveStop;
        if (nextStop == null || !nextStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            uiAutomation.TryCloseMarketBoardWindows();
        if (nextStop == null || !nextStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
        {
            state.ActiveWorldPurchasedQuantity = 0;
            state.ActiveWorldSpentGil = 0;
            state.ActiveWorldPurchaseBatchWorld = null;
            state.ActivePurchaseLineId = null;
            state.ActiveLinePurchasedQuantity = 0;
            state.ActiveLineSpentGil = 0;
        }
        else if (activeSubtask != null && nextStop.ActiveItemSubtask != null &&
                 !activeSubtask.LineId.Equals(nextStop.ActiveItemSubtask.LineId, StringComparison.Ordinal))
        {
            ResetMarketBoardStateForNextRouteItem();
            state.ActiveWorldPurchaseBatchWorld = nextStop.WorldName;
        }

        ReportRouteProgress();
        EvaluateOutfitterRouteCompletion();
        if (result.Success &&
            runner.LatestWorldCompletionSummary?.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase) == true)
        {
            _ = ReportUniversalisFreshnessAsync(currentWorld, freshnessCancellation.Token);
        }
    }

    private async Task ReportUniversalisFreshnessAsync(string worldName, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(UniversalisFreshnessVerificationDelay, cancellationToken).ConfigureAwait(false);
            await runner.VerifyWorldFreshnessAsync(worldName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                state.AcquisitionStatus = $"Unable to record Universalis freshness diagnostics: {ex.Message}";
        }
    }

    private void ResetMarketBoardStateForNextRouteItem()
    {
        state.MarketBoardReadResult = null;
        state.MarketBoardReconciliation = null;
        state.LiveCandidatePlan = null;
        ClearMarketBoardAutomationState();
        runner.ClearSearchSubmission("Advancing to next route item.");
        uiAutomation.TryCloseMarketBoardWindows();
        state.NextRouteMonitorUtc = clock.UtcNow.AddMilliseconds(250);
    }

    private void ClearMarketBoardAutomationState()
    {
        listingReadAccumulator.Clear();
        purchaseAutomation.Clear();
    }

    private void SimulateDryRunPurchase(string currentWorld)
    {
        var candidates = new List<MarketBoardPurchaseCandidate>();
        foreach (var row in state.LiveCandidatePlan!.Rows)
        {
            if (row.Decision.Equals("WouldBuy", StringComparison.OrdinalIgnoreCase) &&
                MarketBoardListingIntegrity.IsRealListing(row.LiveListing))
                candidates.Add(MarketBoardPurchaseCandidate.FromLiveListing(row.LiveListing));
        }

        if (candidates.Count == 0)
        {
            if (ShouldFailWorldPurchaseBatchOnNoCandidate(state.LiveCandidatePlan!))
            {
                UpdateStatus(FailRoute(state.LiveCandidatePlan!.Message));
                return;
            }

            CompleteActiveWorldPurchaseBatch(currentWorld);
            return;
        }

        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
        var lineId = activeSubtask?.LineId ?? GetActiveRouteLineId(claimedRequest!);
        foreach (var candidate in candidates)
        {
            state.ActiveWorldPurchasedQuantity = checked(state.ActiveWorldPurchasedQuantity + candidate.Quantity);
            state.ActiveWorldSpentGil = checked(state.ActiveWorldSpentGil + candidate.TotalGil);
            state.ActiveLinePurchasedQuantity = checked(state.ActiveLinePurchasedQuantity + candidate.Quantity);
            state.ActiveLineSpentGil = checked(state.ActiveLineSpentGil + candidate.TotalGil);
            outfitterAuthority?.RecordPurchase(lineId, candidate);
            runner.RecordPurchaseAudit(
                lineId,
                activeSubtask?.ItemName,
                currentWorld,
                candidate.ListingId,
                candidate.RetainerId,
                candidate.Quantity,
                candidate.TotalGil,
                "DryRunWouldPurchase",
                activeSubtask?.Source);
        }

        state.AcquisitionStatus = $"Dry run: would purchase {state.ActiveLinePurchasedQuantity:N0} for {state.ActiveLineSpentGil:N0} gil; no purchase UI was invoked.";
        CompleteActiveWorldPurchaseBatch(currentWorld);
    }

    private void EvaluateOutfitterRouteCompletion()
    {
        if (runner.ActiveStop is not null || outfitterAuthority is null || runner.ActivePlan is not { } completedPlan)
            return;
        outfitterAuthority.EvaluateRouteEnd(completedPlan);
        state.AcquisitionStatus = outfitterAuthority.State.Message;
    }

    private void ReportConfirmedPurchase(MarketBoardPurchaseCandidate candidate, uint linePurchasedQuantity, uint lineSpentGil)
    {
        var claimed = claimedRequest;
        var activeSubtask = runner.ActiveStop?.ActiveItemSubtask;
        if (claimed == null || activeSubtask == null || string.IsNullOrWhiteSpace(claimed.ClaimToken))
            return;

        var lineId = string.IsNullOrWhiteSpace(activeSubtask.LineId) ? GetActiveRouteLineId(claimed) : activeSubtask.LineId;
        var worldName = string.IsNullOrWhiteSpace(candidate.WorldName) ? context.GetCurrentWorldName() : candidate.WorldName;
        var message = $"Purchased {candidate.Quantity:N0} {FormatItem(GetActiveRouteLine(claimed))} on {worldName} for {candidate.TotalGil:N0} gil.";
        runner.RecordPurchaseAudit(lineId, activeSubtask.ItemName, worldName, candidate.ListingId, candidate.RetainerId, candidate.Quantity, candidate.TotalGil, "Purchased", activeSubtask.Source);
        runner.RecordLineProgress(lineId, activeSubtask.ItemName, "Running", linePurchasedQuantity, lineSpentGil, message, activeSubtask.Source);
        evidence.RecordPurchaseVisit(candidate, activeSubtask, worldName, claimed.Id, state.ProgressNonce);
        ReportPurchaseAudit(claimed, lineId, activeSubtask.ItemName, candidate, worldName, message);
        ReportLineProgress(claimed, lineId, activeSubtask.ItemName, "Running", linePurchasedQuantity, lineSpentGil, message, null);
    }

    private void ReportAcquisitionLineProgress(MarketAcquisitionWorldItemSubtask subtask, string status, uint purchasedQuantity, uint spentGil, string message)
    {
        var claimed = claimedRequest;
        if (claimed == null || string.IsNullOrWhiteSpace(claimed.ClaimToken))
            return;

        var lineId = string.IsNullOrWhiteSpace(subtask.LineId) ? GetActiveRouteLineId(claimed) : subtask.LineId;
        runner.RecordLineProgress(lineId, subtask.ItemName, status, purchasedQuantity, spentGil, message, subtask.Source);
        ReportLineProgress(claimed, lineId, subtask.ItemName, status, purchasedQuantity, spentGil, message, null);
    }

    private void ReportPurchaseAudit(MarketAcquisitionClaimView claimed, string lineId, string? itemName, MarketBoardPurchaseCandidate candidate, string worldName, string message)
    {
        if (!reportDispatcher.CanReport)
            return;

        var sequence = ++state.ProgressReportSequence;
        reportDispatcher.EnqueuePurchaseAudit(
            new MarketAcquisitionPurchaseAuditReport(
                claimed.Id,
                claimed.ClaimToken,
                state.ProgressNonce,
                sequence,
                lineId,
                worldName,
                candidate.ItemId,
                itemName,
                candidate,
                message));
    }

    private void ReportLineProgress(MarketAcquisitionClaimView claimed, string lineId, string? itemName, string status, uint purchasedQuantity, uint spentGil, string message, string? reason)
    {
        if (!reportDispatcher.CanReport)
            return;

        var sequence = ++state.ProgressReportSequence;
        reportDispatcher.EnqueueLineProgress(
            new MarketAcquisitionLineProgressReport(
                claimed.Id,
                claimed.ClaimToken,
                state.ProgressNonce,
                sequence,
                lineId,
                itemName,
                status,
                purchasedQuantity,
                spentGil,
                message,
                reason));
    }

    private void ReportMarketObservation(
        MarketAcquisitionClaimView claimed,
        MarketAcquisitionRequestView activeLine,
        MarketAcquisitionWorldItemSubtask? activeSubtask,
        string worldName,
        MarketBoardReadResult readResult)
    {
        if (!reportDispatcher.CanReport || string.IsNullOrWhiteSpace(claimed.ClaimToken))
            return;

        var lineId = !string.IsNullOrWhiteSpace(activeSubtask?.LineId)
            ? activeSubtask.LineId
            : GetActiveRouteLineId(claimed);
        var itemId = activeSubtask?.ItemId ?? activeLine.ItemId;
        var itemName = activeSubtask?.ItemName ?? activeLine.ItemName;
        var dataCenter = !string.IsNullOrWhiteSpace(activeSubtask?.DataCenter)
            ? activeSubtask.DataCenter
            : MarketAcquisitionWorldCatalog.ResolveDataCenter(worldName);
        reportDispatcher.EnqueueMarketObservation(new MarketAcquisitionMarketObservationReport(
            claimed.Id,
            claimed.ClaimToken,
            state.ProgressNonce,
            ++state.ProgressReportSequence,
            lineId,
            itemId,
            itemName,
            dataCenter,
            worldName,
            clock.UtcNow,
            readResult));
    }

    private string GetActiveRouteLineId(MarketAcquisitionClaimView claimed)
    {
        var lineId = runner.ActiveStop?.ActiveItemSubtask?.LineId;
        return !string.IsNullOrWhiteSpace(lineId) ? lineId : claimed.Id;
    }

    private static string ResolveZeroPurchaseLineStatus(MarketAcquisitionLiveCandidatePlan? candidatePlan, uint purchasedQuantity, uint spentGil) =>
        purchasedQuantity > 0 || spentGil > 0
            ? "Complete"
            : MarketAcquisitionLiveCandidateStatuses.IsIncompleteListingCoverage(candidatePlan?.Status)
                ? "SkippedIncompleteListingCoverage"
                : "SkippedNoLiveStock";

    internal static bool ShouldFailWorldPurchaseBatchOnNoCandidate(MarketAcquisitionLiveCandidatePlan? candidatePlan) =>
        MarketAcquisitionLiveCandidateStatuses.IsIncompleteListingCoverage(candidatePlan?.Status);

    private static MarketBoardAutomationSnapshot CreatePurchaseSelectionSnapshot(MarketBoardPurchaseResult result)
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["resultMessage"] = result.Message,
        };
        if (result.Candidate != null)
        {
            details["candidateItemId"] = result.Candidate.ItemId.ToString();
            details["candidateWorld"] = result.Candidate.WorldName;
            details["candidateListingId"] = result.Candidate.ListingId;
            details["candidateRetainerId"] = result.Candidate.RetainerId;
            details["candidateRetainerName"] = result.Candidate.RetainerName;
            details["candidateQuantity"] = result.Candidate.Quantity.ToString();
            details["candidateUnitPrice"] = result.Candidate.UnitPrice.ToString();
            details["candidateTotalGil"] = result.Candidate.TotalGil.ToString();
        }

        foreach (var pair in result.Diagnostics)
            details[pair.Key] = pair.Value;

        return MarketBoardAutomationSnapshot.Create(
            "BuyListing",
            "Selection",
            "ClickableMarketBoardListing",
            result.Status,
            ClassifyPurchaseSelectionOutcome(result.Status),
            ChoosePurchaseSelectionNextAction(result.Status),
            details);
    }

    private static MarketBoardAutomationSnapshot CreatePurchaseConfirmationSnapshot(MarketBoardPurchaseResult result, MarketBoardPurchaseCandidate candidate) =>
        MarketBoardAutomationSnapshot.Create("BuyListing", "Confirmation", "PurchasePrompt", result.Status,
            result.Status is "ConfirmationSubmitted" or "ConfirmationPending" ? MarketBoardAutomationOutcome.InProgress : MarketBoardAutomationOutcome.Fatal,
            result.Status switch
            {
                "ConfirmationSubmitted" => "VerifyListingRemoval",
                "ConfirmationPending" => "ContinueMonitoring",
                _ => "StopRoute",
            },
            new Dictionary<string, string?>
            {
                ["candidateItemId"] = candidate.ItemId.ToString(),
                ["candidateWorld"] = candidate.WorldName,
                ["candidateListingId"] = candidate.ListingId,
                ["candidateRetainerId"] = candidate.RetainerId,
                ["candidateRetainerName"] = candidate.RetainerName,
                ["candidateQuantity"] = candidate.Quantity.ToString(),
                ["candidateUnitPrice"] = candidate.UnitPrice.ToString(),
                ["candidateTotalGil"] = candidate.TotalGil.ToString(),
                ["confirmationAddon"] = result.ConfirmationAddonName,
                ["confirmationPromptText"] = result.ConfirmationPromptText,
            });

    private static MarketBoardAutomationOutcome ClassifyPurchaseSelectionOutcome(string status) => status switch
    {
        "PurchaseSelectionSent" => MarketBoardAutomationOutcome.InProgress,
        "NoCandidate" => MarketBoardAutomationOutcome.ExpectedAlternate,
        "MarketBoardNotOpen" or "InfoProxyUnavailable" or "ListingListUnavailable" or "ListingListNotReady" => MarketBoardAutomationOutcome.Recoverable,
        _ => MarketBoardAutomationOutcome.Fatal,
    };

    private static string ChoosePurchaseSelectionNextAction(string status) => status switch
    {
        "PurchaseSelectionSent" => "WaitForConfirmation",
        "NoCandidate" => "CompleteWorldBatch",
        "MarketBoardNotOpen" => "ReopenMarketBoard",
        _ => "StopRoute",
    };

    public void ReportRouteProgress(bool includeEvidenceRefresh = false)
    {
        if (state.EvidenceRefreshOnly && !includeEvidenceRefresh)
            return;

        var claimed = claimedRequest;
        if (claimed == null || string.IsNullOrWhiteSpace(claimed.ClaimToken) || !reportDispatcher.CanReport ||
            string.Equals(runner.State, "Idle", StringComparison.OrdinalIgnoreCase))
            return;

        var routeState = runner.State;
        if (!MarketAcquisitionRouteProgressReporter.CanReportForRouteState(routeState) ||
            !MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(claimed.Status))
            return;

        var message = runner.StatusMessage;
        var activeStop = runner.ActiveStop;
        var report = new MarketAcquisitionRouteProgressReport(
            claimed.Id,
            claimed.ClaimToken,
            routeState,
            state.ProgressNonce,
            ++state.ProgressReportSequence,
            activeStop == null ? null : $"{activeStop.DataCenter}:{activeStop.WorldName}",
            activeStop?.WorldName,
            activeStop?.Status ?? routeState,
            message);
        reportDispatcher.EnqueueRouteProgress(report);
    }

    private void ClearExecutionState(bool preserveExecutionMode = false)
    {
        CleanupOwnedApproach("Replacement");
        CleanupOwnedTravel("Replacement");
        travelInterruptedByCleanup = false;
        CancelActiveOperation("Route execution state reset.");
        state.ResetRouteExecutionState(preserveExecutionMode);
        listingReadAccumulator.Clear();
        purchaseAutomation.Clear();
        reportDispatcher.ResetSession();
        freshnessCancellation.Cancel();
        freshnessCancellation.Dispose();
        freshnessCancellation = new CancellationTokenSource();
    }

    private MarketAcquisitionRouteActionResult UpdateStatus(MarketAcquisitionRouteActionResult result)
    {
        state.AcquisitionStatus = result.Message;
        return result;
    }

    public void Dispose()
    {
        CleanupOwnedApproach("Dispose");
        CleanupOwnedTravel("Dispose");
        CancelActiveOperation("Route engine disposed.");
        purchaseAutomation.Dispose();
        reportDispatcher.Dispose();
        freshnessCancellation.Cancel();
        freshnessCancellation.Dispose();
        runner.Dispose();
    }

    private void CancelActiveOperation(string message)
    {
        var cancellation = operationExecutor.Cancel(clock.UtcNow, clock.MonotonicMilliseconds, message);
        if (cancellation.Accepted && cancellation.Snapshot != null)
            runner.RecordRouteOperationSnapshot(cancellation.Snapshot);
    }

    private bool TryFailExpiredOperation()
    {
        if (operationExecutor.ActiveSnapshot == null)
            return false;

        var deadline = operationExecutor.CheckDeadline(clock.UtcNow, clock.MonotonicMilliseconds);
        if (deadline.Snapshot is not { IsTerminal: true } timedOut)
            return false;

        runner.RecordRouteOperationSnapshot(timedOut);
        UpdateStatus(FailRoute(timedOut.Message));
        return true;
    }

    private MarketAcquisitionRouteActionResult FailRoute(string message, Exception? exception = null)
    {
        CleanupOwnedApproach("Failure");
        CleanupOwnedTravel("Failure");
        CancelActiveOperation($"Route failed; active operation cancelled. {message}");
        return runner.FailRoute(message, exception);
    }

    private void CleanupOwnedApproach(string terminalReason)
    {
        var lease = activeApproachLease;
        if (lease == null)
            return;

        activeApproachLease = null;
        MarketAcquisitionApproachCleanupResult result;
        try
        {
            result = marketBoard.StopOwnedApproach(lease);
        }
        catch (Exception ex)
        {
            result = new MarketAcquisitionApproachCleanupResult
            {
                Status = MarketAcquisitionTravelCleanupStatus.Failed,
                Message = $"vnavmesh cleanup adapter threw {ex.GetType().Name}: {ex.Message}",
                AdapterCapability = "AdapterException",
            };
        }

        runner.RecordRouteCleanup(
            result.Message,
            new Dictionary<string, string?>
            {
                ["routeRunId"] = lease.RouteRunId,
                ["operationId"] = lease.OperationId,
                ["leaseId"] = lease.LeaseId,
                ["dependency"] = lease.Dependency,
                ["terminalReason"] = terminalReason,
                ["cleanupStatus"] = result.Status.ToString(),
                ["adapterCapability"] = result.AdapterCapability,
            });
    }

    private void CleanupOwnedTravel(string terminalReason)
    {
        var lease = activeTravelLease;
        if (lease == null)
            return;

        var cleanupId = Guid.NewGuid().ToString("N");

        runner.RecordRouteCleanup(
            "Route cleanup requested for Lifestream travel.",
            CreateTravelCleanupDetails(lease, terminalReason, "Requested", unresolvedExternalAutomation: false, cleanupId: cleanupId));

        // Fence the local lease before calling an external dependency. Any observation after this point
        // is either rejected by the operation executor or belongs to a later lease.
        activeTravelLease = null;
        if (operationExecutor.ActiveSnapshot is { } active &&
            string.Equals(active.OperationId, lease.OperationId, StringComparison.Ordinal))
        {
            var cancellation = operationExecutor.Cancel(
                clock.UtcNow,
                clock.MonotonicMilliseconds,
                $"Route cleanup requested ({terminalReason}) for Lifestream travel to {lease.TargetWorld}.");
            if (cancellation.Accepted && cancellation.Snapshot != null)
                runner.RecordRouteOperationSnapshot(cancellation.Snapshot);
        }

        MarketAcquisitionTravelCleanupResult result;
        if (!lease.IsOwned)
        {
            result = new MarketAcquisitionTravelCleanupResult
            {
                Status = MarketAcquisitionTravelCleanupStatus.NothingOwned,
                Message = "Lifestream command was not accepted; no owned travel requires cancellation.",
                AdapterCapability = "LeaseNotOwned",
            };
        }
        else
        {
            try
            {
                result = travelCleanup.CancelOwnedTravel(lease);
            }
            catch (Exception ex)
            {
                result = new MarketAcquisitionTravelCleanupResult
                {
                    Status = MarketAcquisitionTravelCleanupStatus.Failed,
                    Message = $"Lifestream cleanup adapter threw {ex.GetType().Name}: {ex.Message}",
                    UnresolvedExternalAutomation = true,
                    AdapterCapability = "AdapterException",
                    ExceptionType = ex.GetType().FullName,
                };
            }
        }

        var unresolved = result.UnresolvedExternalAutomation ||
                         result.Status is MarketAcquisitionTravelCleanupStatus.Unsupported or MarketAcquisitionTravelCleanupStatus.Unavailable or MarketAcquisitionTravelCleanupStatus.Failed;
        if (unresolved)
            unresolvedTravelLease = lease;

        runner.RecordRouteCleanup(
            result.Message,
            CreateTravelCleanupDetails(lease, terminalReason, result.Status.ToString(), unresolved, result, cleanupId));
        runner.RecordRouteCleanup(
            unresolved
                ? "Route cleanup completed with unresolved external Lifestream automation."
                : "Route cleanup completed.",
            CreateTravelCleanupDetails(lease, terminalReason, "Aggregate", unresolved, result, cleanupId));
    }

    private bool TryReconcileUnresolvedTravelLease(out string message)
    {
        var lease = unresolvedTravelLease;
        if (lease == null)
        {
            message = string.Empty;
            return true;
        }

        if (context.IsCurrentWorldAvailable &&
            lease.TargetWorld.Equals(context.GetCurrentWorldName(), StringComparison.OrdinalIgnoreCase))
        {
            unresolvedTravelLease = null;
            runner.RecordRouteCleanup(
                $"Resolved previous unsupported Lifestream travel lease after arrival on {lease.TargetWorld}.",
                CreateTravelCleanupDetails(lease, "Reconcile", "ResolvedByArrival", unresolvedExternalAutomation: false));
            message = string.Empty;
            return true;
        }

        message = $"Cannot start a new route while previous Lifestream travel to {lease.TargetWorld} remains unresolved. Confirm arrival on that world before restarting.";
        return false;
    }

    private IReadOnlyDictionary<string, string?> CreateTravelCleanupDetails(
        MarketAcquisitionTravelLease lease,
        string terminalReason,
        string status,
        bool unresolvedExternalAutomation,
        MarketAcquisitionTravelCleanupResult? result = null,
        string? cleanupId = null) =>
        new Dictionary<string, string?>
        {
            ["cleanupId"] = cleanupId ?? Guid.NewGuid().ToString("N"),
            ["routeRunId"] = lease.RouteRunId,
            ["operationId"] = lease.OperationId,
            ["leaseId"] = lease.LeaseId,
            ["dependency"] = lease.Dependency,
            ["targetWorld"] = lease.TargetWorld,
            ["terminalReason"] = terminalReason,
            ["leaseOwnership"] = lease.IsOwned.ToString(),
            ["cleanupStatus"] = status,
            ["unresolvedExternalAutomation"] = unresolvedExternalAutomation.ToString(),
            ["adapterCapability"] = result?.AdapterCapability,
            ["exceptionType"] = result?.ExceptionType,
            ["cleanupRecordedAtUtc"] = clock.UtcNow.ToString("O"),
            ["cleanupRecordedAtMonotonicMilliseconds"] = clock.MonotonicMilliseconds.ToString(),
        };
}
