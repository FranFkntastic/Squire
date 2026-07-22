using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteRunner : IDisposable
{
    private const string LocalMarketBoardCommand = "/li mb";
    private static readonly TimeSpan MarketBoardListingFreshnessWatchdog = TimeSpan.FromSeconds(15);

    private readonly string diagnosticsDirectory;
    private readonly UniversalisFreshnessVerifierDelegate? universalisFreshnessVerifier;
    private readonly Dictionary<FreshnessObservationKey, FreshnessObservation> freshnessObservations = [];
    private readonly HashSet<FreshnessObservationKey> verifiedFreshnessObservations = [];
    private readonly List<string> freshnessWarnings = [];
    private int freshnessConfirmedCount;
    private int freshnessUnconfirmedCount;
    private int freshnessUnavailableCount;
    private MarketAcquisitionGuidedRouteSession? session;
    private MarketAcquisitionRouteDiagnostics diagnostics = MarketAcquisitionRouteDiagnostics.Disabled;
    private bool diagnosticsRequested;
    private bool includeOpportunisticChecksRequested;
    private bool standaloneInputCaptureLogOpen;
    private DateTimeOffset? itemSearchAutomationStartedUtc;
    private DateTimeOffset? listingReadPendingStartedUtc;
    private string? listingReadPendingSignature;
    private string? lastWorldSummarySignature;
    private string? currentRequestId;
    private MarketAcquisitionExecutionMode executionMode = MarketAcquisitionExecutionMode.Live;

    public MarketAcquisitionRouteRunner(string diagnosticsDirectory)
        : this(diagnosticsDirectory, null)
    {
    }

    public MarketAcquisitionRouteRunner(
        string diagnosticsDirectory,
        UniversalisFreshnessVerifierDelegate? universalisFreshnessVerifier)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsDirectory))
            throw new ArgumentException("Diagnostics directory is required.", nameof(diagnosticsDirectory));

        this.diagnosticsDirectory = diagnosticsDirectory;
        this.universalisFreshnessVerifier = universalisFreshnessVerifier;
    }

    public string State { get; private set; } = "Idle";

    public string StatusMessage { get; private set; } = "No route has started.";

    public string? LastDiagnosticFilePath { get; private set; }

    public string? LastObservedListingsCsvPath { get; private set; }

    public string? LastPurchaseRecordsCsvPath { get; private set; }

    public MarketAcquisitionRouteRunSummary? LastRunSummary { get; private set; }

    public MarketAcquisitionWorldCompletionSummary? LatestWorldCompletionSummary { get; private set; }

    public MarketAcquisitionPlan? ActivePlan { get; private set; }

    public MarketAcquisitionRunDiagnosticSummary LastRunDiagnosticSummary => new()
    {
        FreshnessConfirmedCount = freshnessConfirmedCount,
        FreshnessUnconfirmedCount = freshnessUnconfirmedCount,
        FreshnessUnavailableCount = freshnessUnavailableCount,
        Warnings = freshnessWarnings.ToArray(),
    };

    public bool SearchSubmitted { get; private set; }

    public bool MarketBoardCloseRequiredBeforeTravel { get; private set; }

    public bool CanFinalizeInputCaptureLog => standaloneInputCaptureLogOpen && diagnostics.IsEnabled;

    public MarketAcquisitionGuidedRouteStop? ActiveStop =>
        State is "Completed" or "Stopped" or "Failed"
            ? null
            : session?.ActiveStop;

    public IReadOnlyList<MarketAcquisitionGuidedRouteStop> Stops => session?.Stops ?? [];

    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);

    public bool IsPaused => string.Equals(State, "Paused", StringComparison.OrdinalIgnoreCase);

    public bool CanRestart => session != null;

    public IReadOnlyList<MarketAcquisitionCompletedRouteStop> CompletedOrProbedStops =>
        session?.Stops
            .Where(IsCompletedOrProbed)
            .Select(stop => new MarketAcquisitionCompletedRouteStop
            {
                WorldName = stop.WorldName,
                Result = stop.PurchasedQuantity > 0 || stop.SpentGil > 0 ? "Purchased" : "Probed",
            })
            .ToArray() ?? [];

    public MarketAcquisitionRouteActionResult Start(
        MarketAcquisitionPlan plan,
        bool enableDiagnostics = false,
        bool includeOpportunisticChecks = false,
        MarketAcquisitionExecutionMode executionMode = MarketAcquisitionExecutionMode.Live)
    {
        ArgumentNullException.ThrowIfNull(plan);

        CloseDiagnostics();
        diagnosticsRequested = enableDiagnostics;
        includeOpportunisticChecksRequested = includeOpportunisticChecks;
        this.executionMode = executionMode;
        diagnostics = diagnosticsRequested
            ? MarketAcquisitionRouteDiagnostics.CreateEnabled(
                diagnosticsDirectory,
                DateTimeOffset.Now,
                executionMode == MarketAcquisitionExecutionMode.DryRun ? "dry-run" : "route")
            : MarketAcquisitionRouteDiagnostics.Disabled;
        LastDiagnosticFilePath = diagnostics.FilePath;
        LastObservedListingsCsvPath = diagnostics.ObservedListingsCsvPath;
        LastPurchaseRecordsCsvPath = diagnostics.PurchaseRecordsCsvPath;
        LastRunSummary = null;
        ActivePlan = plan;
        session = MarketAcquisitionGuidedRouteSession.Start(plan, includeOpportunisticChecks);
        currentRequestId = plan.RequestId;
        State = "Running";
        SearchSubmitted = false;
        MarketBoardCloseRequiredBeforeTravel = false;
        standaloneInputCaptureLogOpen = false;
        itemSearchAutomationStartedUtc = null;
        ClearListingReadPendingWatchdog();
        LatestWorldCompletionSummary = null;
        lastWorldSummarySignature = null;
        freshnessObservations.Clear();
        verifiedFreshnessObservations.Clear();
        ResetRunDiagnosticSummary();
        StatusMessage = $"Route started. Next stop: {session.ActiveStop?.WorldName}.";
        diagnostics.Record(
            "route-start",
            StatusMessage,
            new Dictionary<string, string?>
            {
                ["requestId"] = plan.RequestId,
                ["worldMode"] = plan.WorldMode,
                ["lineCount"] = plan.Lines.Count.ToString(),
                ["worldCount"] = plan.WorldBatches.Count.ToString(),
                ["plannedQuantity"] = plan.PlannedQuantity.ToString(),
                ["plannedGil"] = plan.PlannedGil.ToString(),
                ["firstStop"] = session.ActiveStop?.WorldName,
                ["firstItem"] = FormatRouteItem(session.ActiveStop?.ActiveItemSubtask),
                ["firstItemSource"] = session.ActiveStop?.ActiveItemSubtask?.Source,
                ["sourceListingCount"] = plan.Diagnostics.SourceListingCount.ToString(),
                ["plannedListingCount"] = plan.Diagnostics.PlannedListingCount.ToString(),
                ["opportunisticChecks"] = includeOpportunisticChecks.ToString(),
                ["executionMode"] = executionMode.ToString(),
            });
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult Restart(MarketAcquisitionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        diagnostics.Record("route-restart", "Restarting market acquisition route.");
        return Start(plan, diagnosticsRequested, includeOpportunisticChecksRequested, executionMode);
    }

    public MarketAcquisitionRouteActionResult ReprepareAndRestart(
        MarketAcquisitionPlan plan,
        DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var reprepare = MarketAcquisitionPlanRepreparer.FilterCompletedOrProbedStops(
            plan,
            CompletedOrProbedStops,
            preparedAtUtc);
        if (!reprepare.CanStart)
        {
            StatusMessage = reprepare.Message;
            diagnostics.Record("route-reprepare-empty", reprepare.Message);
            return MarketAcquisitionRouteActionResult.Fail(reprepare.Message);
        }

        diagnostics.Record(
            "route-reprepare",
            reprepare.Message,
            new Dictionary<string, string?>
            {
                ["skippedWorlds"] = string.Join(", ", reprepare.SkippedWorlds),
                ["remainingWorldCount"] = reprepare.Plan.WorldBatches.Count.ToString(),
            });
        var startResult = Start(reprepare.Plan, diagnosticsRequested, includeOpportunisticChecksRequested, executionMode);
        if (!startResult.Success)
            return startResult;

        StatusMessage = $"{reprepare.Message} {StatusMessage}";
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteLinePurchaseTotals GetLinePurchaseTotals(string lineId) =>
        session?.GetLinePurchaseTotals(lineId) ?? default;

    public MarketAcquisitionRouteActionResult Pause()
    {
        if (!IsRunning)
            return Fail($"Route cannot be paused while {State}.");

        State = "Paused";
        StatusMessage = "Route paused.";
        diagnostics.Record("paused", StatusMessage);
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult Resume()
    {
        if (!IsPaused)
            return Fail($"Route cannot be resumed while {State}.");

        State = "Running";
        StatusMessage = $"Route resumed. Next stop: {ActiveStop?.WorldName}.";
        diagnostics.Record("resumed", StatusMessage);
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult Stop()
    {
        if (State is "Idle" or "Completed" or "Stopped")
            return Fail($"Route cannot be stopped while {State}.");

        State = "Stopped";
        StatusMessage = "Route stopped.";
        SearchSubmitted = false;
        MarketBoardCloseRequiredBeforeTravel = false;
        standaloneInputCaptureLogOpen = false;
        itemSearchAutomationStartedUtc = null;
        ClearListingReadPendingWatchdog();
        diagnostics.Record("stopped", StatusMessage);
        CloseDiagnostics();
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public void Reset(string statusMessage)
    {
        CloseDiagnostics();
        session = null;
        ActivePlan = null;
        currentRequestId = null;
        State = "Idle";
        StatusMessage = statusMessage;
        SearchSubmitted = false;
        MarketBoardCloseRequiredBeforeTravel = false;
        standaloneInputCaptureLogOpen = false;
        itemSearchAutomationStartedUtc = null;
        ClearListingReadPendingWatchdog();
        diagnosticsRequested = false;
        includeOpportunisticChecksRequested = false;
        LastDiagnosticFilePath = null;
        LastObservedListingsCsvPath = null;
        LastPurchaseRecordsCsvPath = null;
        LastRunSummary = null;
        LatestWorldCompletionSummary = null;
        lastWorldSummarySignature = null;
        freshnessObservations.Clear();
        verifiedFreshnessObservations.Clear();
        ResetRunDiagnosticSummary();
    }

    public MarketAcquisitionRouteActionResult ExecutePendingTravelCommand(Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);

        if (!IsRunning)
            return Fail($"Route is {State}; no command was sent.");

        var activeStop = ActiveStop;
        if (activeStop == null)
            return Complete("Route complete.");

        if (!string.Equals(activeStop.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);

        if (MarketBoardCloseRequiredBeforeTravel)
        {
            StatusMessage = $"Waiting for market board windows to close before traveling to {activeStop.WorldName}.";
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
        }

        var result = session!.ExecuteActiveStop(processCommand);
        StatusMessage = result.Message;
        diagnostics.Record(
            "travel-command",
            result.Message,
            new Dictionary<string, string?>
            {
                ["world"] = activeStop.WorldName,
                ["command"] = activeStop.LifestreamCommand,
                ["success"] = result.Success.ToString(),
            });

        if (!result.Success)
            State = "Failed";

        return result.Success
            ? MarketAcquisitionRouteActionResult.Ok(result.Message)
            : Fail(result.Message);
    }

    public MarketAcquisitionRouteActionResult RecordMarketBoardClosedBeforeTravel()
    {
        if (!IsRunning)
            return Fail($"Route is {State}; market board close was not recorded.");

        if (!MarketBoardCloseRequiredBeforeTravel)
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);

        MarketBoardCloseRequiredBeforeTravel = false;
        var activeStop = ActiveStop;
        StatusMessage = activeStop == null
            ? "Market board windows closed."
            : $"Market board windows closed. Next stop: {activeStop.WorldName}.";
        diagnostics.Record(
            "market-board-closed",
            StatusMessage,
            new Dictionary<string, string?>
            {
                ["nextWorld"] = activeStop?.WorldName,
            });
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult RecordTravelBlockedByUi(AutomationTravelPreflightResult preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        if (!IsRunning)
            return Fail($"Route is {State}; travel preflight was not recorded.");

        StatusMessage = preflight.Message;
        diagnostics.Record(
            "travel-ui-blocked",
            preflight.Message,
            new Dictionary<string, string?>
            {
                ["blockingAddons"] = string.Join(", ", preflight.BlockingAddons),
            });
        return MarketAcquisitionRouteActionResult.Ok(preflight.Message);
    }

    public MarketAcquisitionRouteActionResult PreparePendingStopForCurrentWorld(
        bool currentWorldIsValid,
        string? currentWorld,
        Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);

        if (!IsRunning)
            return Fail($"Route is {State}; pending stop was not prepared.");

        var activeStop = ActiveStop;
        if (activeStop == null)
            return Complete("Route complete.");

        if (!string.Equals(activeStop.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);

        if (currentWorldIsValid &&
            activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
        {
            return RecordCurrentWorld(currentWorld!);
        }

        if (!currentWorldIsValid)
            return RecordCurrentWorldUnavailable();

        return ExecutePendingTravelCommand(processCommand);
    }

    public MarketAcquisitionRouteActionResult RecordCurrentWorldUnavailable()
    {
        if (!IsRunning)
            return Fail($"Route is {State}; current world was not recorded.");

        var result = session?.RecordCurrentWorldUnavailable() ??
                     MarketAcquisitionGuidedRouteResult.Fail("No route has started.");
        StatusMessage = result.Message;
        diagnostics.Record("world-unavailable", result.Message);
        return MarketAcquisitionRouteActionResult.Fail(result.Message);
    }

    public MarketAcquisitionRouteActionResult RecordCurrentWorld(string currentWorld)
    {
        if (!IsRunning)
            return Fail($"Route is {State}; current world was not recorded.");

        var result = session?.RecordCurrentWorld(currentWorld) ??
                     MarketAcquisitionGuidedRouteResult.Fail("No route has started.");
        StatusMessage = result.Message;
        diagnostics.Record(
            "current-world",
            result.Message,
            new Dictionary<string, string?>
            {
                ["currentWorld"] = currentWorld,
                ["success"] = result.Success.ToString(),
            });
        return result.Success
            ? MarketAcquisitionRouteActionResult.Ok(result.Message)
            : MarketAcquisitionRouteActionResult.Fail(result.Message);
    }

    public MarketAcquisitionRouteActionResult RecordSearchResult(MarketBoardItemSearchResult searchResult)
    {
        return RecordSearchResult(searchResult, DateTimeOffset.UtcNow);
    }

    public MarketAcquisitionRouteActionResult RecordInputCapture(string label, MarketBoardInputCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (!diagnostics.IsEnabled)
        {
            diagnostics = MarketAcquisitionRouteDiagnostics.CreateInputCapture(diagnosticsDirectory, DateTimeOffset.Now);
            LastDiagnosticFilePath = diagnostics.FilePath;
            standaloneInputCaptureLogOpen = true;
        }

        var details = new Dictionary<string, string?>
        {
            ["label"] = label,
            ["status"] = capture.Status,
        };
        foreach (var pair in capture.Details)
            details[pair.Key] = pair.Value;

        diagnostics.Record("input-capture", capture.Message, details);
        StatusMessage = $"Captured market board input state: {label}.";
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult RecordAutomationSnapshot(MarketBoardAutomationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!diagnostics.IsEnabled)
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);

        diagnostics.RecordAutomationSnapshot(snapshot);
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public void RecordRouteOperationSnapshot(MarketAcquisitionRouteOperationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var details = new Dictionary<string, string?>
        {
            ["operationId"] = snapshot.OperationId,
            ["kind"] = snapshot.Kind.ToString(),
            ["phase"] = snapshot.Phase.ToString(),
            ["disposition"] = snapshot.Disposition.ToString(),
            ["timeoutDisposition"] = snapshot.TimeoutDisposition.ToString(),
            ["attempt"] = snapshot.Attempt.ToString(CultureInfo.InvariantCulture),
            ["startedAtUtc"] = snapshot.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["deadlineUtc"] = snapshot.DeadlineUtc.ToString("O", CultureInfo.InvariantCulture),
            ["startedAtMonotonicMilliseconds"] = snapshot.StartedAtMonotonicMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["deadlineMonotonicMilliseconds"] = snapshot.DeadlineMonotonicMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["updatedAtMonotonicMilliseconds"] = snapshot.UpdatedAtMonotonicMilliseconds.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var pair in snapshot.Context)
            details[$"context.{pair.Key}"] = pair.Value;
        foreach (var pair in snapshot.Details)
            details[$"detail.{pair.Key}"] = pair.Value;

        diagnostics.Record("route-operation", snapshot.Message, details);
    }

    public void RecordRouteCleanup(
        string message,
        IReadOnlyDictionary<string, string?> details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(details);
        diagnostics.Record("route-cleanup", message, details);
    }

    public MarketAcquisitionRouteActionResult FinalizeInputCaptureLog()
    {
        if (diagnosticsRequested && IsRunning)
        {
            StatusMessage = "Active route diagnostics are still running; stop or complete the route to finalize that log.";
            return MarketAcquisitionRouteActionResult.Fail(StatusMessage);
        }

        if (!CanFinalizeInputCaptureLog)
        {
            StatusMessage = "No standalone input capture log is open.";
            return MarketAcquisitionRouteActionResult.Fail(StatusMessage);
        }

        StatusMessage = "Standalone input capture log finalized.";
        diagnostics.Record("input-capture-finalized", StatusMessage);
        standaloneInputCaptureLogOpen = false;
        CloseDiagnostics();
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public MarketAcquisitionRouteActionResult RecordSearchResult(MarketBoardItemSearchResult searchResult, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(searchResult);

        if (!IsRunning)
            return Fail($"Route is {State}; search result was not recorded.");

        if (searchResult.ReadyForListings)
        {
            itemSearchAutomationStartedUtc = null;
            ClearListingReadPendingWatchdog();
        }
        else if (searchResult.IsInProgress)
        {
            itemSearchAutomationStartedUtc ??= nowUtc;
            ClearListingReadPendingWatchdog();
        }

        StatusMessage = searchResult.ReadyForListings
            ? searchResult.Message
            : $"Waiting for market board listings. {searchResult.Message}";
        SearchSubmitted = searchResult.ReadyForListings;
        var details = new Dictionary<string, string?>
        {
            ["automationTaskName"] = "MarketBoardItemSearch",
            ["lastWaitingPredicate"] = searchResult.ReadyForListings ? "ListingsReady" : searchResult.Status,
            ["status"] = searchResult.Status,
            ["searchSubmitted"] = SearchSubmitted.ToString(),
            ["subtaskSource"] = session?.ActiveStop?.ActiveItemSubtask?.Source,
        };
        if (itemSearchAutomationStartedUtc is { } startedAt)
        {
            var elapsed = nowUtc - startedAt;
            details["automationElapsedSeconds"] = Math.Max(0, elapsed.TotalSeconds).ToString("F1");
        }

        foreach (var pair in searchResult.Details)
            details[pair.Key] = pair.Value;

        diagnostics.Record(
            "item-search",
            searchResult.Message,
            details);

        var snapshot = CreateSearchAutomationSnapshot(searchResult, "Observed", details);
        diagnostics.RecordAutomationSnapshot(snapshot);

        return searchResult.IsInProgress || searchResult.ReadyForListings
            ? MarketAcquisitionRouteActionResult.Ok(searchResult.Message)
            : MarketAcquisitionRouteActionResult.Fail(searchResult.Message);
    }

    public MarketAcquisitionRouteActionResult RecordMarketBoardApproach(MarketBoardApproachResult approachResult)
    {
        ArgumentNullException.ThrowIfNull(approachResult);

        if (!IsRunning)
            return Fail($"Route is {State}; market board approach was not recorded.");

        StatusMessage = approachResult.Message;
        diagnostics.Record(
            "market-board-approach",
            approachResult.Message,
            new Dictionary<string, string?>
            {
                ["status"] = approachResult.Status,
            }.Concat(approachResult.Details).ToDictionary(pair => pair.Key, pair => pair.Value));

        return approachResult.ReadyToSearch || approachResult.ActionTaken
            ? MarketAcquisitionRouteActionResult.Ok(approachResult.Message)
            : MarketAcquisitionRouteActionResult.Fail(approachResult.Message);
    }

    public MarketAcquisitionRouteActionResult ExecuteMarketBoardTravelCommand(Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);

        if (!IsRunning)
            return Fail($"Route is {State}; market board travel command was not sent.");

        var activeStop = ActiveStop;
        if (activeStop == null)
            return Complete("Route complete.");

        if (!string.Equals(activeStop.Status, "Arrived", StringComparison.OrdinalIgnoreCase))
            return Fail($"Cannot request market board travel while stop is {activeStop.Status}.");

        if (activeStop.MarketBoardTravelCommandSent)
        {
            StatusMessage = "Waiting for Lifestream market board travel to finish.";
            diagnostics.Record(
                "market-board-travel-wait",
                StatusMessage,
                new Dictionary<string, string?>
                {
                    ["world"] = activeStop.WorldName,
                    ["command"] = LocalMarketBoardCommand,
                });
            return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
        }

        if (!processCommand(LocalMarketBoardCommand))
        {
            StatusMessage = $"Lifestream command was not handled: {LocalMarketBoardCommand}";
            diagnostics.Record(
                "market-board-travel-command",
                StatusMessage,
                new Dictionary<string, string?>
                {
                    ["world"] = activeStop.WorldName,
                    ["command"] = LocalMarketBoardCommand,
                    ["success"] = false.ToString(),
                });
            return MarketAcquisitionRouteActionResult.Fail(StatusMessage);
        }

        activeStop.MarketBoardTravelCommandSent = true;
        StatusMessage = "Sent /li mb. Waiting for Lifestream market board travel to finish.";
        diagnostics.Record(
            "market-board-travel-command",
            StatusMessage,
            new Dictionary<string, string?>
            {
                ["world"] = activeStop.WorldName,
                ["command"] = LocalMarketBoardCommand,
                ["success"] = true.ToString(),
            });
        return MarketAcquisitionRouteActionResult.Ok(StatusMessage);
    }

    public void ClearSearchSubmission(string reason)
    {
        SearchSubmitted = false;
        itemSearchAutomationStartedUtc = null;

        diagnostics.Record("item-search-reset", reason);
    }

    public MarketAcquisitionRouteActionResult BeginProbe(string message)
    {
        if (!IsRunning)
            return Fail($"Route is {State}; probe was not started.");

        StatusMessage = message;
        diagnostics.Record("probe-start", message);
        itemSearchAutomationStartedUtc = null;
        return MarketAcquisitionRouteActionResult.Ok(message);
    }

    public MarketAcquisitionRouteActionResult RecordListingReadPending(string currentWorld, MarketBoardReadResult readResult)
    {
        return RecordListingReadPending(currentWorld, readResult, DateTimeOffset.UtcNow);
    }

    internal MarketAcquisitionRouteActionResult RecordListingReadPending(
        string currentWorld,
        MarketBoardReadResult readResult,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(readResult);

        if (!IsRunning)
            return Fail($"Route is {State}; listing read was not recorded.");

        var activeSubtask = session?.ActiveStop?.ActiveItemSubtask;
        var signature = BuildListingReadPendingSignature(currentWorld, readResult);
        if (!string.Equals(listingReadPendingSignature, signature, StringComparison.Ordinal))
        {
            listingReadPendingSignature = signature;
            listingReadPendingStartedUtc = nowUtc;
        }

        StatusMessage = readResult.Message;
        diagnostics.Record(
            "listing-read-pending",
            readResult.Message,
            new Dictionary<string, string?>
            {
                ["currentWorld"] = currentWorld,
                ["itemId"] = readResult.ItemId.ToString(),
                ["itemName"] = activeSubtask?.ItemName,
                ["status"] = readResult.Status,
                ["readState"] = readResult.ReadState.ToString(),
                ["isFresh"] = readResult.IsFresh.ToString(),
                ["readableListings"] = readResult.Listings.Count.ToString(),
                ["reportedListings"] = readResult.ReportedListingCount.ToString(),
                ["listingCapacity"] = readResult.ListingCapacity.ToString(),
                ["coverageStatus"] = readResult.HasIncompleteCoverage ? "Incomplete" : "Complete",
                ["unreadListings"] = readResult.UnreadListingCount.ToString(),
                ["rawItemIdMismatchCounts"] = FormatRawItemIdMismatchCounts(readResult.RawItemIdMismatchCounts),
                ["subtaskSource"] = activeSubtask?.Source,
            });

        if (listingReadPendingStartedUtc is { } startedAt &&
            nowUtc - startedAt > MarketBoardListingFreshnessWatchdog)
        {
            var timeoutMessage =
                $"Market board listing cache did not become fresh for {activeSubtask?.ItemName ?? $"item {readResult.ItemId}"} ({readResult.ItemId}) on {currentWorld}. {readResult.Message}";
            return FailRoute(timeoutMessage);
        }

        return MarketAcquisitionRouteActionResult.Ok(readResult.Message);
    }

    public MarketAcquisitionRouteActionResult RecordProbe(
        string currentWorld,
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        bool allowPurchases = true)
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);

        if (!IsRunning)
            return Fail($"Route is {State}; probe result was not recorded.");

        var activeSubtask = session?.ActiveStop?.ActiveItemSubtask;
        var activeStop = session?.ActiveStop;
        var observedQuantity = SumObservedQuantity(candidatePlan);
        var observedGil = SumObservedGil(candidatePlan);
        var result = session?.RecordProbe(currentWorld, candidatePlan, allowPurchases) ??
                     MarketAcquisitionGuidedRouteResult.Fail("No route has started.");
        StatusMessage = result.Message;
        SearchSubmitted = false;
        itemSearchAutomationStartedUtc = null;
        ClearListingReadPendingWatchdog();
        diagnostics.Record(
            "probe-result",
            result.Message,
            new Dictionary<string, string?>
            {
                ["currentWorld"] = currentWorld,
                ["itemId"] = activeSubtask?.ItemId.ToString(),
                ["itemName"] = activeSubtask?.ItemName,
                ["liveCandidateStatus"] = candidatePlan.Status,
                ["readableListings"] = candidatePlan.ReadableListingCount.ToString(),
                ["reportedListings"] = candidatePlan.ReportedListingCount.ToString(),
                ["listingCapacity"] = candidatePlan.ListingCapacity.ToString(),
                ["visibleListingCacheTruncated"] = candidatePlan.IsVisibleListingCacheTruncated.ToString(),
                ["listingReadState"] = candidatePlan.ListingReadState.ToString(),
                ["listingReadFresh"] = candidatePlan.IsListingReadFresh.ToString(),
                ["coverageStatus"] = candidatePlan.ReportedListingCount > candidatePlan.ReadableListingCount
                    ? "Incomplete"
                    : "Complete",
                ["unreadListings"] = Math.Max(0, candidatePlan.ReportedListingCount - candidatePlan.ReadableListingCount).ToString(),
                ["rawItemIdMismatchCounts"] = FormatRawItemIdMismatchCounts(candidatePlan.RawItemIdMismatchCounts),
                ["observedQuantity"] = observedQuantity.ToString(),
                ["observedGil"] = observedGil.ToString(),
                ["wouldBuyQuantity"] = candidatePlan.WouldBuyQuantity.ToString(),
                ["wouldSpendGil"] = candidatePlan.WouldSpendGil.ToString(),
                ["wouldBuyRows"] = candidatePlan.Rows.Count(row =>
                    row.Decision.Equals("WouldBuy", StringComparison.OrdinalIgnoreCase)).ToString(),
                ["allowPurchases"] = allowPurchases.ToString(),
                ["skippedRows"] = candidatePlan.Rows.Count(row =>
                    !row.Decision.Equals("WouldBuy", StringComparison.OrdinalIgnoreCase)).ToString(),
                ["skipReasons"] = SummarizeSkipReasons(candidatePlan),
                ["subtaskSource"] = activeSubtask?.Source,
                ["success"] = result.Success.ToString(),
            });
        diagnostics.RecordObservedListings(
            currentRequestId ?? string.Empty,
            currentWorld,
            activeStop?.DataCenter,
            activeSubtask,
            candidatePlan);

        if (result.Success)
            RecordLatestWorldSummary();

        if (!result.Success)
            return FailRoute(result.Message);

        if (result.Success && session?.ActiveStop == null)
            return Complete(result.Message);

        if (result.Success &&
            string.Equals(session?.ActiveStop?.Status, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            MarketBoardCloseRequiredBeforeTravel = true;
            StatusMessage = $"{result.Message} Closing market board windows before next travel.";
            diagnostics.Record(
                "market-board-close-required",
                StatusMessage,
                new Dictionary<string, string?>
                {
                    ["nextWorld"] = session?.ActiveStop?.WorldName,
                });
        }

        return result.Success
            ? MarketAcquisitionRouteActionResult.Ok(result.Message)
            : MarketAcquisitionRouteActionResult.Fail(result.Message);
    }

    private static string SummarizeSkipReasons(MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        var reasons = candidatePlan.Rows
            .Where(row => !row.Decision.Equals("WouldBuy", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => string.IsNullOrWhiteSpace(row.Reason) ? "Unspecified" : row.Reason)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}={group.Count()}");

        return string.Join("; ", reasons);
    }

    private static string? FormatRawItemIdMismatchCounts(IReadOnlyDictionary<uint, int> counts)
    {
        if (counts.Count == 0)
            return null;

        return string.Join(
            ";",
            counts
                .OrderBy(count => count.Key)
                .Select(count => $"{count.Key}={count.Value}"));
    }

    private static string BuildListingReadPendingSignature(string currentWorld, MarketBoardReadResult readResult) =>
        string.Join(
            "|",
            currentWorld,
            readResult.ItemId.ToString(),
            readResult.ReadState.ToString(),
            FormatRawItemIdMismatchCounts(readResult.RawItemIdMismatchCounts) ?? string.Empty);

    private void ClearListingReadPendingWatchdog()
    {
        listingReadPendingStartedUtc = null;
        listingReadPendingSignature = null;
    }

    private static uint SumObservedQuantity(MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        var total = 0u;
        foreach (var row in candidatePlan.Rows)
            total = checked(total + row.LiveListing.Quantity);

        return total;
    }

    private static ulong SumObservedGil(MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        var total = 0ul;
        foreach (var row in candidatePlan.Rows)
            total = checked(total + ((ulong)row.LiveListing.UnitPrice * row.LiveListing.Quantity));

        return total;
    }

    public void RecordLineProgress(
        string lineId,
        string? itemName,
        string status,
        uint purchasedQuantity,
        uint spentGil,
        string message,
        string? source = null)
    {
        diagnostics.Record(
            "line-progress",
            message,
            new Dictionary<string, string?>
            {
                ["lineId"] = lineId,
                ["itemName"] = itemName,
                ["status"] = status,
                ["source"] = source,
                ["purchasedQuantity"] = purchasedQuantity.ToString(),
                ["spentGil"] = spentGil.ToString(),
            });
    }

    public void RecordPurchaseAudit(
        string lineId,
        string? itemName,
        string worldName,
        string listingId,
        string retainerId,
        uint quantity,
        uint totalGil,
        string result,
        string? source = null)
    {
        diagnostics.Record(
            "purchase-audit",
            $"Purchase audit {result}: {itemName ?? lineId} on {worldName}, listing {listingId}.",
            new Dictionary<string, string?>
            {
                ["lineId"] = lineId,
                ["itemName"] = itemName,
                ["world"] = worldName,
                ["listingId"] = listingId,
                ["retainerId"] = retainerId,
                ["source"] = source,
                ["quantity"] = quantity.ToString(),
                ["totalGil"] = totalGil.ToString(),
                ["result"] = result,
            });
        var activeLine = session?.ActiveStop?.LineStates.FirstOrDefault(line => line.LineId.Equals(lineId, StringComparison.Ordinal));
        var purchaseSource = source ?? activeLine?.Source;
        var sourceCandidateStatus = activeLine?.LiveCandidateStatus ?? session?.ActiveStop?.LiveCandidateStatus;

        diagnostics.RecordPurchaseAudit(
            currentRequestId ?? string.Empty,
            session?.ActiveStop?.DataCenter,
            lineId,
            itemName ?? activeLine?.ItemName,
            worldName,
            listingId,
            retainerId,
            quantity,
            totalGil,
            result,
            purchaseSource,
            activeLine?.ItemId,
            sourceCandidateStatus);

        if (result.Equals("Purchased", StringComparison.OrdinalIgnoreCase))
            RecordFreshnessObservation(lineId, itemName, worldName, listingId);
    }

    public void RecordWorldSummary(MarketAcquisitionWorldCompletionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (executionMode == MarketAcquisitionExecutionMode.DryRun)
        {
            summary = summary with
            {
                Message = summary.Message
                    .Replace("bought", "would buy", StringComparison.OrdinalIgnoreCase)
                    .Replace("spent", "would spend", StringComparison.OrdinalIgnoreCase),
            };
        }

        LatestWorldCompletionSummary = summary;
        diagnostics.Record(
            "world-summary",
            summary.Message,
            new Dictionary<string, string?>
            {
                ["world"] = summary.WorldName,
                ["dataCenter"] = summary.DataCenter,
                ["purchasedQuantity"] = summary.PurchasedQuantity.ToString(),
                ["spentGil"] = summary.SpentGil.ToString(),
                ["completedLineCount"] = summary.CompletedLineCount.ToString(),
                ["skippedLineCount"] = summary.SkippedLineCount.ToString(),
                ["failedLineCount"] = summary.FailedLineCount.ToString(),
            });
    }

    public MarketAcquisitionRouteActionResult RecordWorldPurchaseBatchComplete(
        string currentWorld,
        uint purchasedQuantity,
        uint spentGil,
        string? zeroPurchaseStatus = null,
        string? zeroPurchaseMessage = null)
    {
        if (!IsRunning)
            return Fail($"Route is {State}; world purchase result was not recorded.");

        var completedSubtaskSource = session?.ActiveStop?.ActiveItemSubtask?.Source;
        var result = session?.RecordWorldPurchaseBatchComplete(
            currentWorld,
            purchasedQuantity,
            spentGil,
            zeroPurchaseStatus,
            zeroPurchaseMessage,
            executionMode == MarketAcquisitionExecutionMode.DryRun) ??
                      MarketAcquisitionGuidedRouteResult.Fail("No route has started.");
        StatusMessage = result.Message;
        SearchSubmitted = false;
        itemSearchAutomationStartedUtc = null;
        diagnostics.Record(
            "world-purchase-complete",
            result.Message,
            new Dictionary<string, string?>
            {
                ["currentWorld"] = currentWorld,
                ["purchasedQuantity"] = purchasedQuantity.ToString(),
                ["spentGil"] = spentGil.ToString(),
                ["subtaskSource"] = completedSubtaskSource,
                ["zeroPurchaseStatus"] = zeroPurchaseStatus,
                ["zeroPurchaseMessage"] = zeroPurchaseMessage,
                ["success"] = result.Success.ToString(),
            });

        if (result.Success)
            RecordLatestWorldSummary();

        if (result.Success && session?.ActiveStop == null)
            return Complete(result.Message);

        if (result.Success &&
            string.Equals(session?.ActiveStop?.Status, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            MarketBoardCloseRequiredBeforeTravel = true;
            StatusMessage = $"{result.Message} Closing market board windows before next travel.";
            diagnostics.Record(
                "market-board-close-required",
                StatusMessage,
                new Dictionary<string, string?>
                {
                    ["nextWorld"] = session?.ActiveStop?.WorldName,
                });
        }

        return result.Success
            ? MarketAcquisitionRouteActionResult.Ok(result.Message)
            : MarketAcquisitionRouteActionResult.Fail(result.Message);
    }

    public async Task<MarketAcquisitionRouteActionResult> VerifyLatestWorldFreshnessAsync(
        CancellationToken cancellationToken)
    {
        if (universalisFreshnessVerifier == null)
            return MarketAcquisitionRouteActionResult.Ok("Universalis freshness verification is not configured.");

        var summary = LatestWorldCompletionSummary;
        if (summary == null)
            return MarketAcquisitionRouteActionResult.Ok("No completed world is available for Universalis freshness verification.");

        return await VerifyWorldFreshnessAsync(summary.WorldName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MarketAcquisitionRouteActionResult> VerifyWorldFreshnessAsync(
        string worldName,
        CancellationToken cancellationToken)
    {
        if (universalisFreshnessVerifier == null)
            return MarketAcquisitionRouteActionResult.Ok("Universalis freshness verification is not configured.");
        if (string.IsNullOrWhiteSpace(worldName))
            throw new ArgumentException("World name is required.", nameof(worldName));

        var verificationFinished = false;
        try
        {
            var observations = freshnessObservations
                .Where(pair => pair.Key.WorldName.Equals(worldName, StringComparison.OrdinalIgnoreCase))
                .Where(pair => !verifiedFreshnessObservations.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToList();

            if (observations.Count == 0)
            {
                verificationFinished = true;
                return MarketAcquisitionRouteActionResult.Ok($"No purchased listings require Universalis freshness verification for {worldName}.");
            }

            foreach (var observation in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                UniversalisFreshnessResult result;
                try
                {
                    result = await universalisFreshnessVerifier(
                        observation.WorldName,
                        observation.ItemId,
                        observation.ObservedAtUtc,
                        observation.ListingIds,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    result = UniversalisFreshnessResult.Unavailable(ex.Message);
                }

                cancellationToken.ThrowIfCancellationRequested();

                diagnostics.Record(
                    "universalis-freshness",
                    $"{result.Status}: {FormatFreshnessItem(observation)} on {observation.WorldName}. {result.Message}",
                    new Dictionary<string, string?>
                    {
                        ["world"] = observation.WorldName,
                        ["itemId"] = observation.ItemId.ToString(),
                        ["itemName"] = observation.ItemName,
                        ["status"] = result.Status,
                        ["message"] = result.Message,
                        ["observedAtUtc"] = observation.ObservedAtUtc.ToString("O"),
                        ["listingIds"] = string.Join(", ", observation.ListingIds),
                    });
                RecordFreshnessDiagnosticResult(observation, result);
                verifiedFreshnessObservations.Add(new FreshnessObservationKey(observation.WorldName, observation.ItemId));
            }

            RefreshLastRunSummary();
            verificationFinished = true;
            return MarketAcquisitionRouteActionResult.Ok(
                $"Recorded Universalis freshness for {observations.Count:N0} item(s) on {worldName}.");
        }
        finally
        {
            if (verificationFinished && State == "Completed")
                FinalizeCompletedDiagnostics();
        }
    }

    public MarketAcquisitionRouteActionResult FailRoute(string message, Exception? exception = null)
    {
        State = "Failed";
        StatusMessage = message;
        SearchSubmitted = false;
        MarketBoardCloseRequiredBeforeTravel = false;
        standaloneInputCaptureLogOpen = false;
        itemSearchAutomationStartedUtc = null;
        ClearListingReadPendingWatchdog();
        diagnostics.Fail(message, exception);
        RefreshLastRunSummary();
        CloseDiagnostics();
        return MarketAcquisitionRouteActionResult.Fail(message);
    }

    public void Dispose()
    {
        CloseDiagnostics();
    }

    private void RecordLatestWorldSummary()
    {
        var summary = session?.LastWorldCompletionSummary;
        if (summary == null)
            return;

        var signature = $"{summary.DataCenter}:{summary.WorldName}:{summary.PurchasedQuantity}:{summary.SpentGil}:{summary.CompletedLineCount}:{summary.SkippedLineCount}:{summary.FailedLineCount}";
        if (string.Equals(lastWorldSummarySignature, signature, StringComparison.Ordinal))
            return;

        lastWorldSummarySignature = signature;
        RecordWorldSummary(summary);
    }

    private MarketAcquisitionRouteActionResult Complete(string message)
    {
        StatusMessage = message;
        SearchSubmitted = false;
        MarketBoardCloseRequiredBeforeTravel = false;
        standaloneInputCaptureLogOpen = false;
        itemSearchAutomationStartedUtc = null;
        RefreshLastRunSummary();
        State = "Completed";
        if (universalisFreshnessVerifier == null)
            FinalizeCompletedDiagnostics();
        return MarketAcquisitionRouteActionResult.Ok(message);
    }

    private void FinalizeCompletedDiagnostics()
    {
        diagnostics.Complete(StatusMessage);
        CloseDiagnostics();
    }

    private static bool IsCompletedOrProbed(MarketAcquisitionGuidedRouteStop stop)
    {
        if (stop.PurchasedQuantity > 0 || stop.SpentGil > 0)
            return true;

        if (MarketAcquisitionLiveCandidateStatuses.IsIncompleteListingCoverage(stop.LiveCandidateStatus) ||
            stop.LineStates.Any(line =>
                MarketAcquisitionLiveCandidateStatuses.IsIncompleteListingCoverage(line.LiveCandidateStatus) ||
                line.Status.Equals("SkippedIncompleteListingCoverage", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return stop.Status.Equals("Complete", StringComparison.OrdinalIgnoreCase) ||
               stop.LineStates.Any(line =>
                   line.Status.Equals("Complete", StringComparison.OrdinalIgnoreCase) ||
                   line.Status.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshLastRunSummary()
    {
        if (session == null)
            return;

        LastRunSummary = MarketAcquisitionRouteRunSummary.Build(
            session.Stops,
            LastRunDiagnosticSummary,
            LastDiagnosticFilePath,
            LastObservedListingsCsvPath,
            LastPurchaseRecordsCsvPath);
    }

    private MarketAcquisitionRouteActionResult Fail(string message)
    {
        StatusMessage = message;
        return MarketAcquisitionRouteActionResult.Fail(message);
    }

    private void CloseDiagnostics()
    {
        diagnostics.Dispose();
        diagnostics = MarketAcquisitionRouteDiagnostics.Disabled;
    }

    private static MarketBoardAutomationSnapshot CreateSearchAutomationSnapshot(
        MarketBoardItemSearchResult searchResult,
        string phase,
        IReadOnlyDictionary<string, string?> details)
    {
        var outcome = phase.Equals("TimedOut", StringComparison.OrdinalIgnoreCase)
            ? MarketBoardAutomationOutcome.Fatal
            : ClassifySearchOutcome(searchResult);
        return MarketBoardAutomationSnapshot.Create(
            "SearchItem",
            phase,
            "ItemSearchResultReady",
            searchResult.Status,
            outcome,
            ChooseSearchNextAction(searchResult, outcome),
            details);
    }

    private static MarketBoardAutomationOutcome ClassifySearchOutcome(MarketBoardItemSearchResult searchResult)
    {
        if (searchResult.ReadyForListings)
            return MarketBoardAutomationOutcome.Success;

        if (searchResult.IsInProgress)
            return MarketBoardAutomationOutcome.InProgress;

        return MarketBoardAutomationOutcome.Recoverable;
    }

    private static string ChooseSearchNextAction(
        MarketBoardItemSearchResult searchResult,
        MarketBoardAutomationOutcome outcome)
    {
        if (outcome == MarketBoardAutomationOutcome.Fatal)
            return "CaptureInputState";

        if (searchResult.ReadyForListings)
            return "ReadLiveListings";

        if (searchResult.IsInProgress)
            return "ContinuePolling";

        return "TryAlternateInputPath";
    }

    private static string? FormatRouteItem(MarketAcquisitionWorldItemSubtask? subtask)
    {
        if (subtask == null)
            return null;

        return string.IsNullOrWhiteSpace(subtask.ItemName)
            ? subtask.ItemId.ToString()
            : $"{subtask.ItemName} ({subtask.ItemId})";
    }

    private void RecordFreshnessObservation(
        string lineId,
        string? itemName,
        string worldName,
        string listingId)
    {
        if (string.IsNullOrWhiteSpace(worldName) ||
            string.IsNullOrWhiteSpace(listingId))
        {
            return;
        }

        var itemId = ResolveLineItemId(lineId);
        if (itemId == 0)
            return;

        var key = new FreshnessObservationKey(worldName, itemId);
        if (!freshnessObservations.TryGetValue(key, out var observation))
        {
            observation = new FreshnessObservation(worldName, itemId, itemName);
            freshnessObservations.Add(key, observation);
        }

        observation.ObservedAtUtc = DateTimeOffset.UtcNow;
        observation.ListingIds.Add(listingId);
    }

    private void RecordFreshnessDiagnosticResult(
        FreshnessObservation observation,
        UniversalisFreshnessResult result)
    {
        if (result.Status.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))
        {
            freshnessConfirmedCount++;
            return;
        }

        if (result.Status.Equals("Unconfirmed", StringComparison.OrdinalIgnoreCase))
            freshnessUnconfirmedCount++;
        else if (result.Status.Equals("Unavailable", StringComparison.OrdinalIgnoreCase))
            freshnessUnavailableCount++;

        var warning = $"Universalis freshness {result.Status.ToLowerInvariant()} for {FormatFreshnessItem(observation)} on {observation.WorldName} after local market-board observation. {result.Message}";
        freshnessWarnings.Add(warning);
        diagnostics.Record(
            "universalis-freshness-warning",
            warning,
            new Dictionary<string, string?>
            {
                ["world"] = observation.WorldName,
                ["itemId"] = observation.ItemId.ToString(),
                ["itemName"] = observation.ItemName,
                ["status"] = result.Status,
                ["message"] = result.Message,
                ["observedAtUtc"] = observation.ObservedAtUtc.ToString("O"),
                ["listingIds"] = string.Join(", ", observation.ListingIds),
            });
    }

    private void ResetRunDiagnosticSummary()
    {
        freshnessConfirmedCount = 0;
        freshnessUnconfirmedCount = 0;
        freshnessUnavailableCount = 0;
        freshnessWarnings.Clear();
    }

    private uint ResolveLineItemId(string lineId)
    {
        if (string.IsNullOrWhiteSpace(lineId))
            return session?.ActiveStop?.ActiveItemSubtask?.ItemId ?? 0;

        return session?.ActiveStop?.LineStates.FirstOrDefault(line =>
                   line.LineId.Equals(lineId, StringComparison.Ordinal))?.ItemId ??
               session?.ActiveStop?.ActiveItemSubtask?.ItemId ??
               0;
    }

    private static string FormatFreshnessItem(FreshnessObservation observation) =>
        string.IsNullOrWhiteSpace(observation.ItemName)
            ? $"item {observation.ItemId}"
            : $"{observation.ItemName} ({observation.ItemId})";

    private sealed record FreshnessObservationKey(string WorldName, uint ItemId);

    private sealed class FreshnessObservation
    {
        public FreshnessObservation(string worldName, uint itemId, string? itemName)
        {
            WorldName = worldName;
            ItemId = itemId;
            ItemName = itemName;
        }

        public string WorldName { get; }
        public uint ItemId { get; }
        public string? ItemName { get; }
        public DateTimeOffset ObservedAtUtc { get; set; }
        public HashSet<string> ListingIds { get; } = new(StringComparer.Ordinal);
    }
}

public delegate Task<UniversalisFreshnessResult> UniversalisFreshnessVerifierDelegate(
    string worldName,
    uint itemId,
    DateTimeOffset observedAtUtc,
    IReadOnlyCollection<string> purchasedListingIds,
    CancellationToken cancellationToken);

public sealed record MarketAcquisitionRouteActionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;

    public static MarketAcquisitionRouteActionResult Ok(string message) => new()
    {
        Success = true,
        Message = message,
    };

    public static MarketAcquisitionRouteActionResult Fail(string message) => new()
    {
        Success = false,
        Message = message,
    };
}

public sealed record MarketAcquisitionRunDiagnosticSummary
{
    public int FreshnessConfirmedCount { get; init; }
    public int FreshnessUnconfirmedCount { get; init; }
    public int FreshnessUnavailableCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
