using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Franthropy.Dalamud.Persistence;
using MarketMafioso.Automation.Diagnostics;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteDiagnostics : IDisposable
{
    private static readonly MarketAcquisitionRouteDiagnostics DisabledInstance = new(
        AutomationDiagnosticsLog.Disabled,
        null,
        null,
        null,
        null,
        null,
        DateTimeOffset.MinValue,
        string.Empty,
        string.Empty);

    private readonly object sync = new();
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly AutomationDiagnosticsLog log;
    private readonly AutomationCsvLog? observedListingsCsv;
    private readonly AutomationCsvLog? purchaseRecordsCsv;
    private readonly StreamWriter? routeEventsWriter;
    private readonly DateTimeOffset startedAt;
    private readonly string packageKind;
    private readonly string runId;
    private string captureStatus = "Active";
    private long nextEventSequence;
    private bool disposed;

    private MarketAcquisitionRouteDiagnostics(
        AutomationDiagnosticsLog log,
        AutomationCsvLog? observedListingsCsv,
        AutomationCsvLog? purchaseRecordsCsv,
        StreamWriter? routeEventsWriter,
        string? manifestPath,
        string? packageDirectoryPath,
        DateTimeOffset startedAt,
        string packageKind,
        string runId)
    {
        this.log = log;
        this.observedListingsCsv = observedListingsCsv;
        this.purchaseRecordsCsv = purchaseRecordsCsv;
        this.routeEventsWriter = routeEventsWriter;
        this.startedAt = startedAt;
        this.packageKind = packageKind;
        this.runId = runId;
        ObservedListingsCsvPath = observedListingsCsv?.FilePath;
        PurchaseRecordsCsvPath = purchaseRecordsCsv?.FilePath;
        RouteEventsJsonlPath = routeEventsWriter == null
            ? null
            : Path.Combine(packageDirectoryPath!, "route-events.jsonl");
        ManifestPath = manifestPath;
        PackageDirectoryPath = packageDirectoryPath;
    }

    public static MarketAcquisitionRouteDiagnostics Disabled => DisabledInstance;

    public bool IsEnabled => log.IsEnabled;

    public string? FilePath => log.FilePath;

    public string? ObservedListingsCsvPath { get; }

    public string? PurchaseRecordsCsvPath { get; }

    public string? RouteEventsJsonlPath { get; }

    public string? ManifestPath { get; }

    public string? PackageDirectoryPath { get; }

    public static MarketAcquisitionRouteDiagnostics CreateEnabled(string directory, DateTimeOffset startedAt)
    {
        return CreatePackage(directory, startedAt, "route");
    }

    public static MarketAcquisitionRouteDiagnostics CreateEnabled(
        string directory,
        DateTimeOffset startedAt,
        string packageKind)
    {
        if (!packageKind.Equals("route", StringComparison.OrdinalIgnoreCase) &&
            !packageKind.Equals("dry-run", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Package kind must be route or dry-run.", nameof(packageKind));
        return CreatePackage(directory, startedAt, packageKind);
    }

    public static MarketAcquisitionRouteDiagnostics CreateInputCapture(string directory, DateTimeOffset startedAt)
    {
        return CreatePackage(directory, startedAt, "input-capture");
    }

    private static MarketAcquisitionRouteDiagnostics CreatePackage(
        string directory,
        DateTimeOffset startedAt,
        string filePrefix)
    {
        var createCompanionCsvs =
            filePrefix.Equals("route", StringComparison.OrdinalIgnoreCase) ||
            filePrefix.Equals("dry-run", StringComparison.OrdinalIgnoreCase);
        var packageDirectory = CreatePackageDirectory(directory, startedAt, filePrefix);
        AutomationCsvLog? observedListingsCsv = null;
        AutomationCsvLog? purchaseRecordsCsv = null;
        StreamWriter? routeEventsWriter = null;
        AutomationDiagnosticsLog? log = null;

        try
        {
            observedListingsCsv = createCompanionCsvs
                ? AutomationCsvLog.CreateAtPath(Path.Combine(packageDirectory, "observed-listings.csv"), ObservedListingsHeader)
                : null;
            purchaseRecordsCsv = createCompanionCsvs
                ? AutomationCsvLog.CreateAtPath(Path.Combine(packageDirectory, "purchase-records.csv"), PurchaseRecordsHeader)
                : null;
            var routeEventsJsonlPath = Path.Combine(packageDirectory, "route-events.jsonl");
            routeEventsWriter = new StreamWriter(File.Open(routeEventsJsonlPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            var manifestPath = Path.Combine(packageDirectory, "manifest.json");
            log = AutomationDiagnosticsLog.CreateEnabledAtPath(
                Path.Combine(packageDirectory, $"{filePrefix}.log"),
                startedAt,
                "Market acquisition route diagnostics started.",
                new Dictionary<string, string?>
                {
                    ["packageDirectoryPath"] = packageDirectory,
                    ["observedListingsCsvPath"] = observedListingsCsv?.FilePath,
                    ["purchaseRecordsCsvPath"] = purchaseRecordsCsv?.FilePath,
                });

            var diagnostics = new MarketAcquisitionRouteDiagnostics(
                log,
                observedListingsCsv,
                purchaseRecordsCsv,
                routeEventsWriter,
                manifestPath,
                packageDirectory,
                startedAt,
                filePrefix,
                Path.GetFileName(packageDirectory));

            diagnostics.WriteManifest();
            diagnostics.RecordRouteEvent(
                "start",
                "Market acquisition route diagnostics started.",
                new Dictionary<string, string?>
                {
                    ["runId"] = diagnostics.runId,
                    ["packageKind"] = filePrefix,
                    ["routeLog"] = Path.GetFileName(diagnostics.FilePath),
                    ["observedListingsCsv"] = Path.GetFileName(diagnostics.ObservedListingsCsvPath),
                    ["purchaseRecordsCsv"] = Path.GetFileName(diagnostics.PurchaseRecordsCsvPath),
                    ["routeEventsJsonl"] = Path.GetFileName(diagnostics.RouteEventsJsonlPath),
                    ["manifest"] = Path.GetFileName(diagnostics.ManifestPath),
                });

            return diagnostics;
        }
        catch
        {
            log?.Dispose();
            routeEventsWriter?.Dispose();
            purchaseRecordsCsv?.Dispose();
            observedListingsCsv?.Dispose();
            throw;
        }
    }

    private static string CreatePackageDirectory(
        string directory,
        DateTimeOffset startedAt,
        string filePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePrefix);

        Directory.CreateDirectory(directory);
        var baseName = $"{filePrefix}-{startedAt:yyyyMMdd-HHmmss}";
        var packageDirectory = Path.Combine(directory, baseName);
        if (!Directory.Exists(packageDirectory))
        {
            Directory.CreateDirectory(packageDirectory);
            return packageDirectory;
        }

        for (var suffix = 1; suffix < 1000; suffix++)
        {
            packageDirectory = Path.Combine(directory, $"{baseName}-{suffix}");
            if (Directory.Exists(packageDirectory))
                continue;

            Directory.CreateDirectory(packageDirectory);
            return packageDirectory;
        }

        throw new IOException($"Unable to create a unique market acquisition diagnostics package under {directory}.");
    }

    public void Record(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        lock (sync)
        {
            if (disposed)
                return;

            log.Record(eventName, message, details);
            WriteRouteEventUnsafe(eventName, message, details);
            if (IsTerminalEvent(eventName))
            {
                captureStatus = "Finalizing";
                WriteManifest();
            }
        }
    }

    public void Complete(string message)
    {
        Record("complete", message);
        Dispose();
    }

    public void RecordAutomationSnapshot(MarketBoardAutomationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Record(
            "automation-snapshot",
            $"{snapshot.Step}/{snapshot.Phase}: observed {snapshot.Observed}; outcome {snapshot.Outcome}; next {snapshot.NextAction}.",
            snapshot.ToDetails());
    }

    public void RecordObservedListings(
        string requestId,
        string currentWorld,
        string? dataCenter,
        MarketAcquisitionWorldItemSubtask? activeSubtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);

        var eventDetails = new Dictionary<string, string?>
        {
            ["requestId"] = requestId,
            ["currentWorld"] = currentWorld,
            ["dataCenter"] = dataCenter,
            ["lineId"] = activeSubtask?.LineId,
            ["lineOrdinal"] = activeSubtask?.LineOrdinal.ToString(CultureInfo.InvariantCulture),
            ["itemId"] = activeSubtask?.ItemId.ToString(CultureInfo.InvariantCulture),
            ["planStatus"] = candidatePlan.Status,
            ["listingReadState"] = candidatePlan.ListingReadState.ToString(),
            ["listingReadFresh"] = candidatePlan.IsListingReadFresh.ToString(),
            ["readableListings"] = candidatePlan.ReadableListingCount.ToString(CultureInfo.InvariantCulture),
            ["reportedListings"] = candidatePlan.ReportedListingCount.ToString(CultureInfo.InvariantCulture),
            ["visibleListingCacheTruncated"] = candidatePlan.IsVisibleListingCacheTruncated.ToString(),
            ["coverageStatus"] = FormatCoverageStatus(candidatePlan),
        };

        lock (sync)
        {
            if (disposed)
                return;

            WriteRouteEventUnsafe(
                "observed-listings",
                "Recorded observed market-board listing evidence.",
                eventDetails);
            if (observedListingsCsv == null)
                return;

            if (candidatePlan.Rows.Count == 0)
            {
                WriteObservedListingRow(
                    requestId,
                    currentWorld,
                    dataCenter,
                    activeSubtask,
                    candidatePlan,
                    rowOrdinal: 0,
                    row: null);
                return;
            }

            for (var i = 0; i < candidatePlan.Rows.Count; i++)
            {
                WriteObservedListingRow(
                    requestId,
                    currentWorld,
                    dataCenter,
                    activeSubtask,
                    candidatePlan,
                    i + 1,
                    candidatePlan.Rows[i]);
            }
        }
    }

    public void RecordPurchaseAudit(
        string requestId,
        string? dataCenter,
        string lineId,
        string? itemName,
        string worldName,
        string listingId,
        string retainerId,
        uint quantity,
        uint totalGil,
        string result,
        string? source,
        uint? itemId = null,
        string? sourceCandidateStatus = null)
    {
        var eventDetails = new Dictionary<string, string?>
        {
            ["requestId"] = requestId,
            ["dataCenter"] = dataCenter,
            ["lineId"] = lineId,
            ["itemId"] = itemId?.ToString(CultureInfo.InvariantCulture),
            ["itemName"] = itemName,
            ["worldName"] = worldName,
            ["listingId"] = listingId,
            ["retainerId"] = retainerId,
            ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
            ["totalGil"] = totalGil.ToString(CultureInfo.InvariantCulture),
            ["result"] = result,
            ["source"] = source,
            ["sourceCandidateStatus"] = sourceCandidateStatus,
        };

        lock (sync)
        {
            if (disposed)
                return;

            WriteRouteEventUnsafe(
                "purchase-audit",
                "Recorded market-board purchase audit evidence.",
                eventDetails);
            if (purchaseRecordsCsv == null)
                return;

            purchaseRecordsCsv.WriteRow(
            [
                FormatElapsed(),
                requestId,
                worldName,
                dataCenter,
                lineId,
                itemId?.ToString(CultureInfo.InvariantCulture),
                itemName,
                source,
                sourceCandidateStatus,
                "purchase-audit",
                result,
                listingId,
                retainerId,
                quantity.ToString(CultureInfo.InvariantCulture),
                totalGil.ToString(CultureInfo.InvariantCulture),
                quantity == 0
                    ? null
                    : (totalGil / quantity).ToString(CultureInfo.InvariantCulture),
                null,
                null,
            ]);
        }
    }

    public void Fail(string message, Exception? exception = null)
    {
        Record(
            "failed",
            message,
            exception == null
                ? null
                : new Dictionary<string, string?>
                {
                    ["exceptionType"] = exception.GetType().FullName,
                    ["exceptionMessage"] = exception.Message,
                });
        Dispose();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            var terminalCapture = string.Equals(captureStatus, "Finalizing", StringComparison.Ordinal);
            try
            {
                observedListingsCsv?.Dispose();
                purchaseRecordsCsv?.Dispose();
                routeEventsWriter?.Dispose();
                log.Dispose();
            }
            finally
            {
                captureStatus = terminalCapture ? "Complete" : "Incomplete";
                WriteManifest();
                disposed = true;
            }
        }
    }

    private static IReadOnlyList<string> ObservedListingsHeader =>
    [
        "elapsed",
        "requestId",
        "currentWorld",
        "dataCenter",
        "lineId",
        "lineOrdinal",
        "source",
        "itemId",
        "itemName",
        "planStatus",
        "planMessage",
        "readableListings",
        "reportedListings",
        "listingCapacity",
        "visibleListingCacheTruncated",
        "listingReadState",
        "listingReadFresh",
        "coverageStatus",
        "unreadListings",
        "rawItemIdMismatchCounts",
        "requestedQuantity",
        "wouldBuyQuantity",
        "wouldSpendGil",
        "rowOrdinal",
        "decision",
        "reason",
        "message",
        "listingItemId",
        "rawItemId",
        "listingWorld",
        "listingId",
        "retainerId",
        "retainerName",
        "unitPrice",
        "quantity",
        "totalGil",
        "isHq",
        "runningQuantityAfter",
        "runningGilAfter",
    ];

    private static IReadOnlyList<string> PurchaseRecordsHeader =>
    [
        "elapsed",
        "requestId",
        "world",
        "dataCenter",
        "lineId",
        "itemId",
        "itemName",
        "source",
        "sourceCandidateStatus",
        "event",
        "result",
        "listingId",
        "retainerId",
        "quantity",
        "totalGil",
        "unitPrice",
        "message",
        "notes",
    ];

    private void WriteObservedListingRow(
        string requestId,
        string currentWorld,
        string? dataCenter,
        MarketAcquisitionWorldItemSubtask? activeSubtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        int rowOrdinal,
        MarketAcquisitionLiveCandidateRow? row)
    {
        var listing = row?.LiveListing;
        observedListingsCsv!.WriteRow(
        [
            FormatElapsed(),
            requestId,
            currentWorld,
            dataCenter,
            activeSubtask?.LineId,
            activeSubtask?.LineOrdinal.ToString(CultureInfo.InvariantCulture),
            activeSubtask?.Source,
            activeSubtask?.ItemId.ToString(CultureInfo.InvariantCulture),
            activeSubtask?.ItemName,
            candidatePlan.Status,
            candidatePlan.Message,
            candidatePlan.ReadableListingCount.ToString(CultureInfo.InvariantCulture),
            candidatePlan.ReportedListingCount.ToString(CultureInfo.InvariantCulture),
            candidatePlan.ListingCapacity.ToString(CultureInfo.InvariantCulture),
            candidatePlan.IsVisibleListingCacheTruncated.ToString(),
            candidatePlan.ListingReadState.ToString(),
            candidatePlan.IsListingReadFresh.ToString(),
            FormatCoverageStatus(candidatePlan),
            FormatUnreadListings(candidatePlan),
            FormatRawItemIdMismatchCounts(candidatePlan.RawItemIdMismatchCounts),
            candidatePlan.RequestedQuantity.ToString(CultureInfo.InvariantCulture),
            candidatePlan.WouldBuyQuantity.ToString(CultureInfo.InvariantCulture),
            candidatePlan.WouldSpendGil.ToString(CultureInfo.InvariantCulture),
            rowOrdinal.ToString(CultureInfo.InvariantCulture),
            row?.Decision,
            row?.Reason,
            row?.Message,
            listing?.ItemId.ToString(CultureInfo.InvariantCulture),
            listing?.RawItemId?.ToString(CultureInfo.InvariantCulture),
            listing?.WorldName,
            listing?.ListingId,
            listing?.RetainerId,
            listing?.RetainerName,
            listing?.UnitPrice.ToString(CultureInfo.InvariantCulture),
            listing?.Quantity.ToString(CultureInfo.InvariantCulture),
            listing == null
                ? null
                : ((ulong)listing.UnitPrice * listing.Quantity).ToString(CultureInfo.InvariantCulture),
            listing?.IsHq.ToString(),
            row?.RunningQuantityAfter.ToString(CultureInfo.InvariantCulture),
            row?.RunningGilAfter.ToString(CultureInfo.InvariantCulture),
        ]);
    }

    private void RecordRouteEvent(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? details)
    {
        if (routeEventsWriter == null)
            return;

        lock (sync)
        {
            if (disposed)
                return;

            WriteRouteEventUnsafe(eventName, message, details);
        }
    }

    private void WriteRouteEventUnsafe(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? details)
    {
        if (routeEventsWriter == null)
            return;

        var filteredDetails = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (details != null)
        {
            foreach (var detail in details)
            {
                if (detail.Value != null)
                    filteredDetails[detail.Key] = detail.Value;
            }
        }

        var routeEvent = new MarketAcquisitionRouteDiagnosticEvent
        {
            SchemaVersion = MarketAcquisitionRouteDiagnosticEvent.CurrentSchemaVersion,
            Sequence = ++nextEventSequence,
            ElapsedMilliseconds = (long)stopwatch.Elapsed.TotalMilliseconds,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            EventName = eventName,
            Message = message,
            Details = filteredDetails,
        };

        routeEventsWriter.WriteLine(JsonSerializer.Serialize(routeEvent, JsonOptions));
    }

    private void WriteManifest()
    {
        if (ManifestPath == null || RouteEventsJsonlPath == null)
            return;

        var assembly = typeof(Plugin).Assembly;
        var manifest = new MarketAcquisitionRouteDiagnosticManifest
        {
            SchemaVersion = MarketAcquisitionRouteDiagnosticEvent.CurrentSchemaVersion,
            RunId = runId,
            PackageKind = packageKind,
            CaptureStatus = captureStatus,
            StartedAtUtc = startedAt,
            AssemblyName = assembly.GetName().Name,
            AssemblyVersion = assembly.GetName().Version?.ToString(),
            InformationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion,
            Artifacts = BuildArtifacts(),
            CaptureCapabilities = BuildCaptureCapabilities(),
        };

        AtomicJsonFile.Write(ManifestPath, manifest, JsonOptions);
    }

    private IReadOnlyList<string> BuildCaptureCapabilities()
    {
        var capabilities = new List<string>
        {
            "route-events-jsonl-v1",
            "route-log",
        };

        if (observedListingsCsv != null)
            capabilities.Add("observed-listings-csv");
        if (purchaseRecordsCsv != null)
            capabilities.Add("purchase-records-csv");

        return capabilities;
    }

    private IReadOnlyDictionary<string, string> BuildArtifacts()
    {
        var artifacts = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["manifest"] = Path.GetFileName(ManifestPath) ?? throw new InvalidOperationException("Manifest path is invalid."),
            ["routeEventsJsonl"] = Path.GetFileName(RouteEventsJsonlPath) ?? throw new InvalidOperationException("Route event path is invalid."),
        };

        if (FilePath != null)
            artifacts["routeLog"] = Path.GetFileName(FilePath);
        if (ObservedListingsCsvPath != null)
            artifacts["observedListingsCsv"] = Path.GetFileName(ObservedListingsCsvPath);
        if (PurchaseRecordsCsvPath != null)
            artifacts["purchaseRecordsCsv"] = Path.GetFileName(PurchaseRecordsCsvPath);

        return artifacts;
    }

    private static bool IsTerminalEvent(string eventName) =>
        eventName is "complete" or "failed" or "stopped" or "input-capture-finalized";

    private static string FormatCoverageStatus(MarketAcquisitionLiveCandidatePlan candidatePlan) =>
        candidatePlan.ReportedListingCount > candidatePlan.ReadableListingCount
            ? "Incomplete"
            : "Complete";

    private static string FormatUnreadListings(MarketAcquisitionLiveCandidatePlan candidatePlan) =>
        Math.Max(0, candidatePlan.ReportedListingCount - candidatePlan.ReadableListingCount)
            .ToString(CultureInfo.InvariantCulture);

    private string FormatElapsed()
    {
        var elapsed = stopwatch.Elapsed;
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
