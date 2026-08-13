#if DEBUG
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Windows.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireCleanupSyntheticReviewTests
{
    [Fact]
    public void ReviewedControlsAreStableAndUnique()
    {
        Assert.Equal("squire.cleanup.synthetic-review", SquireCleanupReviewedControlIds.SyntheticReview);
        Assert.Equal(5, SquireCleanupReviewedControlIds.ScenarioIds.Count);
        Assert.Equal(
            SquireCleanupReviewedControlIds.ScenarioIds.Count,
            SquireCleanupReviewedControlIds.ScenarioIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            SquireCleanupReviewedControlIds.ScenarioIds,
            id => Assert.StartsWith(SquireCleanupReviewedControlIds.ScenarioPrefix, id, StringComparison.Ordinal));
    }

    [Fact]
    public void ScenarioMatrixOwnsOnlyFrozenPresentationState()
    {
        var zero = SquireCleanupSyntheticReview.Create(SquireCleanupSyntheticScenarioKind.CompleteZero);
        var populated = SquireCleanupSyntheticReview.Create(SquireCleanupSyntheticScenarioKind.Populated);
        var filteredZero = SquireCleanupSyntheticReview.Create(SquireCleanupSyntheticScenarioKind.FilteredZero);
        var invalid = SquireCleanupSyntheticReview.Create(SquireCleanupSyntheticScenarioKind.InvalidLastValid);
        var hidden = SquireCleanupSyntheticReview.Create(SquireCleanupSyntheticScenarioKind.HiddenSelected);

        Assert.True(zero.Analysis.Snapshot.Diagnostics.IsComplete);
        Assert.Empty(zero.Analysis.Candidates);
        Assert.Empty(zero.ResolveVisibleCandidates());

        Assert.Equal(3, populated.Analysis.Candidates.Count);
        Assert.Equal(3, populated.ResolveVisibleCandidates().Length);

        Assert.True(filteredZero.Workbench.Filter.IsValid);
        Assert.Empty(filteredZero.ResolveVisibleCandidates());

        Assert.False(invalid.Workbench.Filter.IsValid);
        Assert.NotNull(invalid.Workbench.Filter.Error);
        Assert.Equal("quality:", invalid.Workbench.Filter.Expression);
        Assert.Single(invalid.ResolveVisibleCandidates());

        var hiddenVisible = hidden.ResolveVisibleCandidates();
        Assert.Single(hiddenVisible);
        Assert.Single(hidden.Workbench.Review.Selections);
        Assert.Single(hidden.Workbench.TableSelection.SelectedKeys);
        Assert.DoesNotContain(
            hidden.Workbench.Review.Selections.Keys,
            fingerprint => hiddenVisible.Any(candidate =>
                EquipmentInstanceFingerprintComparer.Instance.Equals(candidate.Instance.Fingerprint, fingerprint)));

        Assert.False(SquireCleanupRunAuthorization.Resolve(
            deterministicReviewActive: true,
            snapshotComplete: true,
            selectionCount: 1,
            supportedBatch: true,
            hiddenSelectionCount: 0,
            validationSucceeded: true,
            running: false));
        Assert.True(SquireCleanupRunAuthorization.Resolve(
            deterministicReviewActive: false,
            snapshotComplete: true,
            selectionCount: 1,
            supportedBatch: true,
            hiddenSelectionCount: 0,
            validationSucceeded: true,
            running: false));
    }

    [Fact]
    public void FrozenReviewDoesNotMutateLiveWorkbenchState()
    {
        var live = new SquireCleanupWorkbenchState("quality:nq", showProtected: false, showNonEquipment: true)
        {
            SelectionMode = true,
            ShowBatchOnly = true,
            HiddenBatchCount = 4,
        };

        var review = SquireCleanupSyntheticReview.Create(SquireCleanupSyntheticScenarioKind.HiddenSelected);
        review.Workbench.ShowBatchOnly = true;
        review.Workbench.Search = "route:desynth";

        Assert.Equal("quality:nq", live.Search);
        Assert.True(live.Filter.IsValid);
        Assert.False(live.ShowProtected);
        Assert.True(live.ShowNonEquipment);
        Assert.True(live.SelectionMode);
        Assert.True(live.ShowBatchOnly);
        Assert.Equal(4, live.HiddenBatchCount);
        Assert.Empty(live.Review.Selections);
        Assert.Empty(live.TableSelection.SelectedKeys);
    }

    [Fact]
    public void EntryRefusesLiveRunOrRecoveryAndAlwaysAllowsReturnToLive()
    {
        var runActive = SquireCleanupSyntheticReviewAuthority.ResolveEntry(
            deterministicReviewActive: false,
            liveRunActive: true,
            liveRecoveryActive: false);
        var recoveryActive = SquireCleanupSyntheticReviewAuthority.ResolveEntry(
            deterministicReviewActive: false,
            liveRunActive: false,
            liveRecoveryActive: true);
        var returnToLive = SquireCleanupSyntheticReviewAuthority.ResolveEntry(
            deterministicReviewActive: true,
            liveRunActive: true,
            liveRecoveryActive: true);

        Assert.False(runActive.Enabled);
        Assert.Contains("run is active", runActive.Status, StringComparison.Ordinal);
        Assert.False(recoveryActive.Enabled);
        Assert.Contains("recovery is active", recoveryActive.Status, StringComparison.Ordinal);
        Assert.True(returnToLive.Enabled);
        Assert.False(returnToLive.InvalidateLiveAuthorization);
        Assert.Equal("synthetic", returnToLive.Status);
    }

    [Fact]
    public void EntryInvalidatesPriorLiveAuthorizationAndSyntheticBlocksRunMutation()
    {
        var enter = SquireCleanupSyntheticReviewAuthority.ResolveEntry(
            deterministicReviewActive: false,
            liveRunActive: false,
            liveRecoveryActive: false);

        Assert.True(enter.Enabled);
        Assert.True(enter.InvalidateLiveAuthorization);
        Assert.Equal("live", enter.Status);
        Assert.False(SquireCleanupSyntheticReviewAuthority.AllowsLiveRunMutation(deterministicReviewActive: true));
        Assert.True(SquireCleanupSyntheticReviewAuthority.AllowsLiveRunMutation(deterministicReviewActive: false));
        Assert.True(SquireCleanupSyntheticReviewAuthority.AllowsScenarioMutation(deterministicReviewActive: true));
        Assert.False(SquireCleanupSyntheticReviewAuthority.AllowsScenarioMutation(deterministicReviewActive: false));
    }
}
#endif
