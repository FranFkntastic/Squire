using MarketMafioso.Windows.Squire;
using Xunit;

namespace Squire.Tests.Features;

public sealed class SquireCleanupPresentationStateTests
{
    [Fact]
    public void Cleanup_toolbar_has_persistent_label_stable_control_and_minimum_target_height()
    {
        Assert.Equal("Filter candidates", SquireCleanupToolbarPresentation.FilterLabel);
        Assert.Equal("squire.cleanup.filter", SquireCleanupToolbarPresentation.FilterControlId);
        Assert.Equal("Columns", SquireCleanupToolbarPresentation.ColumnsLabel);
        Assert.Equal("squire.cleanup.columns", SquireCleanupToolbarPresentation.ColumnsControlId);

        var paddingY = SquireCleanupToolbarPresentation.ResolveFramePaddingY(fontSize: 13f, currentPaddingY: 3f);
        Assert.True(13f + (paddingY * 2f) >= SquireCleanupToolbarPresentation.MinimumControlHeight);
        Assert.Equal(24f, SquireCleanupToolbarPresentation.ResolveControlHeight(currentFrameHeight: 22f));
        Assert.Equal(31f, SquireCleanupToolbarPresentation.ResolveControlHeight(currentFrameHeight: 31f));
    }

    [Fact]
    public void Cleanup_run_control_ids_include_the_active_cancel_boundary()
    {
        Assert.Equal("squire.run.confirm", SquireCleanupRunControlIds.Confirm);
        Assert.Equal("squire.run.diagnostic", SquireCleanupRunControlIds.Diagnostic);
        Assert.Equal("squire.run.cleanup", SquireCleanupRunControlIds.Cleanup);
        Assert.Equal("squire.run.cancel", SquireCleanupRunControlIds.Cancel);
        Assert.Equal(
            SquireCleanupRunControlIds.All.Count,
            SquireCleanupRunControlIds.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Reviewed_filter_edit_uses_the_existing_filter_without_changing_selection_or_run_authority()
    {
        var persisted = string.Empty;
        var workbench = new SquireCleanupWorkbenchState("quality:nq", showProtected: false, showNonEquipment: false)
        {
            SelectionMode = true,
        };
        var runBefore = SquireCleanupRunAuthorization.Resolve(
            deterministicReviewActive: false,
            snapshotComplete: true,
            selectionCount: 1,
            supportedBatch: true,
            hiddenSelectionCount: 0,
            validationSucceeded: true,
            running: false);

        SquireCleanupFilterEdit.Apply(workbench, "name:copper", value => persisted = value);

        Assert.Equal("name:copper", workbench.Search);
        Assert.Equal("name:copper", persisted);
        Assert.True(workbench.Filter.IsValid);
        Assert.True(workbench.SelectionMode);
        Assert.Empty(workbench.Review.Selections);
        Assert.Empty(workbench.TableSelection.SelectedKeys);
        Assert.Equal(
            runBefore,
            SquireCleanupRunAuthorization.Resolve(
                deterministicReviewActive: false,
                snapshotComplete: true,
                selectionCount: 1,
                supportedBatch: true,
                hiddenSelectionCount: 0,
                validationSucceeded: true,
                running: false));
    }

    [Fact]
    public void Cleanup_column_menu_request_is_one_shot_and_owns_no_cleanup_state()
    {
        var request = new SquireCleanupColumnMenuRequest();
        var workbench = new SquireCleanupWorkbenchState("quality:hq", showProtected: true, showNonEquipment: false)
        {
            SelectionMode = true,
        };
        var runAllowedBefore = SquireCleanupRunAuthorization.Resolve(
            deterministicReviewActive: false,
            snapshotComplete: true,
            selectionCount: 1,
            supportedBatch: true,
            hiddenSelectionCount: 0,
            validationSucceeded: true,
            running: false);

        Assert.False(request.Consume());
        request.Request();
        Assert.True(request.Consume());
        Assert.False(request.Consume());

        Assert.Equal("quality:hq", workbench.Search);
        Assert.True(workbench.ShowProtected);
        Assert.False(workbench.ShowNonEquipment);
        Assert.True(workbench.SelectionMode);
        Assert.Empty(workbench.Review.Selections);
        Assert.Empty(workbench.TableSelection.SelectedKeys);
        Assert.Equal(
            runAllowedBefore,
            SquireCleanupRunAuthorization.Resolve(
                deterministicReviewActive: false,
                snapshotComplete: true,
                selectionCount: 1,
                supportedBatch: true,
                hiddenSelectionCount: 0,
                validationSucceeded: true,
                running: false));
    }

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
