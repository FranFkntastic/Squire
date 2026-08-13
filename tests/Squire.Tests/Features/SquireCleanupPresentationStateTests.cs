using MarketMafioso.Windows.Squire;
using Xunit;

namespace Squire.Tests.Features;

public sealed class SquireCleanupPresentationStateTests
{
    [Theory]
    [InlineData(false, false, false, 0, "WaitingForAnalysis")]
    [InlineData(true, false, false, 0, "WaitingForCharacter")]
    [InlineData(true, true, false, 0, "SnapshotIncomplete")]
    [InlineData(true, true, true, 0, "ReadyEmpty")]
    [InlineData(true, true, true, 1, "Ready")]
    public void Resolver_separates_unavailable_and_ready_states(
        bool hasAnalysis,
        bool hasCharacter,
        bool snapshotComplete,
        int candidateCount,
        string expected)
    {
        Assert.Equal(expected, SquireCleanupSurfaceStateResolver.Resolve(
            hasAnalysis,
            hasCharacter,
            snapshotComplete,
            candidateCount).ToString());
    }

    [Fact]
    public void Success_expires_even_when_no_draw_occurs()
    {
        var state = new SquireOperationalStatusState();
        var createdAt = new DateTimeOffset(2026, 8, 13, 4, 0, 0, TimeSpan.Zero);

        state.ReportSuccess(SquireOperationalStatusSource.Export, "Exported snapshot.json", createdAt);

        Assert.Equal(SquireOperationalStatusKind.Success, state.Current(createdAt)?.Kind);
        Assert.Null(state.Current(createdAt.Add(SquireOperationalStatusState.DefaultSuccessLifetime)));
    }

    [Fact]
    public void Failure_persists_until_dismissed_or_replaced()
    {
        var state = new SquireOperationalStatusState();
        var createdAt = new DateTimeOffset(2026, 8, 13, 4, 0, 0, TimeSpan.Zero);

        state.ReportFailure(SquireOperationalStatusSource.Export, "Export failed: disk full", createdAt);

        Assert.Equal(SquireOperationalStatusKind.Failure, state.Current(createdAt.AddDays(1))?.Kind);
        state.Dismiss(createdAt.AddDays(1));
        Assert.Null(state.Current(createdAt.AddDays(1)));
    }

    [Fact]
    public void Progress_and_boundary_do_not_expire_implicitly()
    {
        var state = new SquireOperationalStatusState();
        var createdAt = new DateTimeOffset(2026, 8, 13, 4, 0, 0, TimeSpan.Zero);

        state.ReportProgress(SquireOperationalStatusSource.Refresh, "Refreshing equipment", createdAt);
        Assert.Equal(SquireOperationalStatusKind.Progress, state.Current(createdAt.AddDays(1))?.Kind);

        state.ReportBoundary(SquireOperationalStatusSource.Run, "Run blocked: confirmation is required.", createdAt.AddDays(1));
        Assert.Equal(SquireOperationalStatusKind.Boundary, state.Current(createdAt.AddDays(2))?.Kind);
        Assert.True(state.Current(createdAt.AddDays(2))?.CanDismiss);
        state.Dismiss(createdAt.AddDays(2));
        Assert.Null(state.Current(createdAt.AddDays(2)));
    }

    [Fact]
    public void Success_resolves_only_the_failure_from_the_same_operation()
    {
        var state = new SquireOperationalStatusState();
        var createdAt = new DateTimeOffset(2026, 8, 13, 4, 0, 0, TimeSpan.Zero);
        state.ReportFailure(SquireOperationalStatusSource.Export, "Export failed", createdAt);

        state.Resolve(SquireOperationalStatusSource.Refresh, createdAt.AddMinutes(1));
        Assert.Equal(SquireOperationalStatusSource.Export, state.Current(createdAt.AddMinutes(1))?.Source);

        state.ReportProgress(SquireOperationalStatusSource.Run, "Starting run", createdAt.AddMinutes(1));
        Assert.Equal(SquireOperationalStatusSource.Export, state.Current(createdAt.AddMinutes(1))?.Source);

        state.Resolve(SquireOperationalStatusSource.Export, createdAt.AddMinutes(2));
        Assert.Null(state.Current(createdAt.AddMinutes(2)));
    }
}
