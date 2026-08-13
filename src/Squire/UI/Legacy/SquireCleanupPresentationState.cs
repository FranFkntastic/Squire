using System;

namespace MarketMafioso.Windows.Squire;

internal enum SquireCleanupSurfaceState
{
    WaitingForAnalysis,
    WaitingForCharacter,
    SnapshotIncomplete,
    ReadyEmpty,
    Ready,
}

internal static class SquireCleanupSurfaceStateResolver
{
    public static SquireCleanupSurfaceState Resolve(
        bool hasAnalysis,
        bool hasCharacter,
        bool snapshotComplete,
        int candidateCount)
    {
        if (candidateCount < 0)
            throw new ArgumentOutOfRangeException(nameof(candidateCount));
        if (!hasAnalysis)
            return SquireCleanupSurfaceState.WaitingForAnalysis;
        if (!hasCharacter)
            return SquireCleanupSurfaceState.WaitingForCharacter;
        if (!snapshotComplete)
            return SquireCleanupSurfaceState.SnapshotIncomplete;
        return candidateCount == 0
            ? SquireCleanupSurfaceState.ReadyEmpty
            : SquireCleanupSurfaceState.Ready;
    }
}

internal enum SquireOperationalStatusKind
{
    Progress,
    Success,
    Failure,
    Boundary,
}

internal enum SquireOperationalStatusSource
{
    Refresh,
    Export,
    Run,
    Recovery,
    Audit,
}

internal sealed record SquireOperationalStatus(
    SquireOperationalStatusKind Kind,
    SquireOperationalStatusSource Source,
    string Message,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc)
{
    public bool CanDismiss => Kind is SquireOperationalStatusKind.Failure or SquireOperationalStatusKind.Boundary;
}

internal sealed class SquireOperationalStatusState
{
    internal static readonly TimeSpan DefaultSuccessLifetime = TimeSpan.FromSeconds(8);

    private readonly object gate = new();
    private SquireOperationalStatus? current;

    public SquireOperationalStatus? Current(DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            if (current?.ExpiresAtUtc is { } expiry && nowUtc >= expiry)
                current = null;
            return current;
        }
    }

    public void ReportProgress(SquireOperationalStatusSource source, string message, DateTimeOffset nowUtc) =>
        Set(SquireOperationalStatusKind.Progress, source, message, nowUtc, null);

    public void ReportSuccess(SquireOperationalStatusSource source, string message, DateTimeOffset nowUtc, TimeSpan? lifetime = null) =>
        Set(SquireOperationalStatusKind.Success, source, message, nowUtc, nowUtc.Add(lifetime ?? DefaultSuccessLifetime));

    public void ReportFailure(SquireOperationalStatusSource source, string message, DateTimeOffset nowUtc) =>
        Set(SquireOperationalStatusKind.Failure, source, message, nowUtc, null);

    public void ReportBoundary(SquireOperationalStatusSource source, string message, DateTimeOffset nowUtc) =>
        Set(SquireOperationalStatusKind.Boundary, source, message, nowUtc, null);

    public void Clear()
    {
        lock (gate)
            current = null;
    }

    public void Dismiss(DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            if (current?.ExpiresAtUtc is { } expiry && nowUtc >= expiry)
                current = null;
            if (current?.CanDismiss == true)
                current = null;
        }
    }

    public void Resolve(SquireOperationalStatusSource source, DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            if (current?.ExpiresAtUtc is { } expiry && nowUtc >= expiry)
                current = null;
            if (current?.Source == source)
                current = null;
        }
    }

    private void Set(
        SquireOperationalStatusKind kind,
        SquireOperationalStatusSource source,
        string message,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("An operational status message is required.", nameof(message));
        lock (gate)
        {
            if (current?.CanDismiss == true && current.Source != source)
                return;
            current = new(kind, source, message, createdAtUtc, expiresAtUtc);
        }
    }
}
