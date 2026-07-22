using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MarketMafioso.Automation.Diagnostics;

public sealed class AutomationDiagnosticsLog : IDisposable
{
    private static readonly AutomationDiagnosticsLog DisabledInstance = new();

    private readonly object sync = new();
    private readonly Stopwatch stopwatch;
    private readonly StreamWriter? writer;
    private string? lastEventName;
    private string? lastMessage;
    private string? lastSignature;
    private int repeatCount;
    private bool disposed;

    private AutomationDiagnosticsLog()
    {
        stopwatch = Stopwatch.StartNew();
    }

    private AutomationDiagnosticsLog(
        string filePath,
        DateTimeOffset startedAt,
        string startMessage,
        IReadOnlyDictionary<string, string?>? metadata)
    {
        FilePath = filePath;
        stopwatch = Stopwatch.StartNew();
        writer = new StreamWriter(File.Open(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

        var startDetails = new Dictionary<string, string?>
        {
            ["startedAt"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
            ["assemblyName"] = PluginAssembly.GetName().Name,
            ["assemblyVersion"] = PluginAssembly.GetName().Version?.ToString(),
            ["informationalVersion"] = PluginAssembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion,
            ["assemblyLocation"] = PluginAssembly.Location,
        };

        if (metadata != null)
        {
            foreach (var detail in metadata)
                startDetails[detail.Key] = detail.Value;
        }

        Record("start", startMessage, startDetails);
    }

    public static AutomationDiagnosticsLog Disabled => DisabledInstance;

    public bool IsEnabled => writer != null;

    public string? FilePath { get; }

    private static Assembly PluginAssembly => typeof(Plugin).Assembly;

    public static AutomationDiagnosticsLog CreateEnabled(
        string directory,
        DateTimeOffset startedAt,
        string filePrefix,
        string startMessage,
        IReadOnlyDictionary<string, string?>? metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(startMessage);

        Directory.CreateDirectory(directory);
        return new AutomationDiagnosticsLog(
            GetAvailablePath(directory, startedAt, filePrefix, ".log"),
            startedAt,
            startMessage,
            metadata);
    }

    public static AutomationDiagnosticsLog CreateEnabledAtPath(
        string filePath,
        DateTimeOffset startedAt,
        string startMessage,
        IReadOnlyDictionary<string, string?>? metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(startMessage);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        return new AutomationDiagnosticsLog(
            filePath,
            startedAt,
            startMessage,
            metadata);
    }

    public void Record(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        if (writer == null)
            return;

        lock (sync)
        {
            if (disposed)
                return;

            var filteredDetails = FilterDetails(details);
            var signature = BuildSignature(eventName, message, filteredDetails);
            if (signature == lastSignature)
            {
                repeatCount++;
                return;
            }

            FlushRepeatSummary();
            WriteEvent(eventName, message, filteredDetails);
            lastEventName = eventName;
            lastMessage = message;
            lastSignature = signature;
        }
    }

    public void Complete(string message)
    {
        Record("complete", message);
    }

    public void Fail(string message, Exception? exception = null)
    {
        var details = exception == null
            ? null
            : new Dictionary<string, string?>
            {
                ["exceptionType"] = exception.GetType().FullName,
                ["exceptionMessage"] = exception.Message,
            };

        Record("failed", message, details);
    }

    public void Dispose()
    {
        if (writer == null)
            return;

        lock (sync)
        {
            if (disposed)
                return;

            FlushRepeatSummary();
            disposed = true;
            writer.Dispose();
        }
    }

    internal static string GetAvailablePath(
        string directory,
        DateTimeOffset startedAt,
        string filePrefix,
        string extension)
    {
        var baseName = $"{filePrefix}-{startedAt:yyyyMMdd-HHmmss}";
        var path = Path.Combine(directory, $"{baseName}{extension}");
        if (!File.Exists(path))
            return path;

        for (var suffix = 1; suffix < 1000; suffix++)
        {
            path = Path.Combine(directory, $"{baseName}-{suffix}{extension}");
            if (!File.Exists(path))
                return path;
        }

        throw new IOException($"Unable to create a unique automation diagnostics file under {directory}.");
    }

    private void WriteEvent(
        string eventName,
        string message,
        IReadOnlyList<KeyValuePair<string, string>> details)
    {
        writer!.WriteLine($"[{FormatElapsed()}] {eventName}");
        writer.WriteLine($"  {Escape(message)}");

        foreach (var detail in details)
        {
            writer.WriteLine($"  {detail.Key}: {Escape(detail.Value)}");
        }

        writer.WriteLine();
    }

    private void FlushRepeatSummary()
    {
        if (repeatCount == 0)
            return;

        writer!.WriteLine($"[{FormatElapsed()}] repeat");
        writer.WriteLine($"  Previous event repeated {repeatCount.ToString(CultureInfo.InvariantCulture)} more time(s).");
        writer.WriteLine($"  event: {lastEventName}");
        writer.WriteLine($"  message: {Escape(lastMessage ?? string.Empty)}");
        writer.WriteLine();

        repeatCount = 0;
    }

    private static List<KeyValuePair<string, string>> FilterDetails(IReadOnlyDictionary<string, string?>? details)
    {
        if (details == null)
            return [];

        return details
            .Where(detail => detail.Value != null)
            .Select(detail => new KeyValuePair<string, string>(detail.Key, detail.Value!))
            .ToList();
    }

    private static string BuildSignature(
        string eventName,
        string message,
        IReadOnlyList<KeyValuePair<string, string>> details)
    {
        var parts = new List<string>
        {
            eventName,
            message,
        };

        parts.AddRange(details.Select(detail => $"{detail.Key}={detail.Value}"));
        return string.Join('\u001f', parts);
    }

    private string FormatElapsed()
    {
        var elapsed = stopwatch.Elapsed;
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value)
    {
        return value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
