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

internal static class SquireCleanupToolbarPresentation
{
    public const string FilterLabel = "Filter candidates";
    public const string FilterControlId = "squire.cleanup.filter";
    public const string ColumnsLabel = "Columns";
    public const string ColumnsControlId = "squire.cleanup.columns";
    public const float MinimumControlHeight = 24f;

    public static float ResolveFramePaddingY(float fontSize, float currentPaddingY)
    {
        if (fontSize < 0f)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (currentPaddingY < 0f)
            throw new ArgumentOutOfRangeException(nameof(currentPaddingY));
        return Math.Max(currentPaddingY, (MinimumControlHeight - fontSize) * 0.5f);
    }

    public static float ResolveControlHeight(float currentFrameHeight)
    {
        if (currentFrameHeight < 0f)
            throw new ArgumentOutOfRangeException(nameof(currentFrameHeight));
        return Math.Max(MinimumControlHeight, currentFrameHeight);
    }
}

internal static class SquireCleanupFilterEdit
{
    public static void Apply(
        SquireCleanupWorkbenchState workbench,
        string? expression,
        Action<string>? persist)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        expression ??= string.Empty;
        workbench.Search = expression;
        workbench.Filter.SetExpression(expression);
        persist?.Invoke(expression);
    }
}

internal static class SquireCleanupRunControlIds
{
    public const string Confirm = "squire.run.confirm";
    public const string Diagnostic = "squire.run.diagnostic";
    public const string Cleanup = "squire.run.cleanup";
    public const string Cancel = "squire.run.cancel";

    public static IReadOnlyList<string> All { get; } = [Confirm, Diagnostic, Cleanup, Cancel];
}

internal sealed class SquireCleanupColumnMenuRequest
{
    private bool requested;

    public void Request() => requested = true;

    public bool Consume()
    {
        var result = requested;
        requested = false;
        return result;
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
