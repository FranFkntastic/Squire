using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;
using MarketMafioso.Windows.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireRunPresentationTests
{
    [Fact]
    public void CreateSeparatesCompletedFailedAndRemainingActions()
    {
        var scope = new CharacterScope(1, "Squire", 57);
        var completed = Action(scope, 1, 100);
        var failed = Action(scope, 2, 200);
        var remaining = Action(scope, 3, 300);
        var plan = new SquireActionPlan(Guid.NewGuid(), scope, SquireDisposition.Desynthesize, DateTimeOffset.UtcNow,
            [completed, failed, remaining]);
        var result = new SquireRunResult(false, "UiTransitionFailed",
        [
            new(DateTimeOffset.UtcNow, "ActionResult", "Completed", "Done", completed.Fingerprint),
            new(DateTimeOffset.UtcNow, "ActionResult", "UiTransitionFailed", "Failed", failed.Fingerprint),
            new(DateTimeOffset.UtcNow, "RunStopped", "UiTransitionFailed", "Failed", failed.Fingerprint),
        ]);

        var presentation = SquireRunPresentation.Create(plan, result, "audit.jsonl");

        Assert.Equal([completed], presentation.Completed);
        Assert.Equal([failed], presentation.Failed);
        Assert.Equal([remaining], presentation.Remaining);
        Assert.Equal([failed, remaining], presentation.Retryable);

        var checkpoint = presentation.CreateCheckpointPlan();
        Assert.Equal(plan.SnapshotGenerationId, checkpoint.SnapshotGenerationId);
        Assert.Equal(plan.ApprovedAt, checkpoint.ApprovedAt);
        Assert.Equal([failed, remaining], checkpoint.Actions);
    }

    [Fact]
    public void CompletedActionsWithTeardownFailureRequireInteractionRecoveryNotItemRetry()
    {
        var scope = new CharacterScope(1, "Squire", 57);
        var completed = Action(scope, 1, 100);
        var plan = new SquireActionPlan(Guid.NewGuid(), scope, SquireDisposition.VendorSell, DateTimeOffset.UtcNow, [completed]);
        var result = new SquireRunResult(false, "UnclassifiedFailure",
        [
            new(DateTimeOffset.UtcNow, "ActionResult", "Completed", "Sold", completed.Fingerprint),
            new(DateTimeOffset.UtcNow, "RunStopped", "UnclassifiedFailure", "Vendor interaction did not settle."),
        ]);

        var presentation = SquireRunPresentation.Create(plan, result, "audit.jsonl");

        Assert.True(presentation.NeedsInteractionRecovery);
        Assert.Single(presentation.Completed);
        Assert.Empty(presentation.Retryable);
    }

    private static SquireReviewedSelection Action(CharacterScope scope, int slot, uint itemId) => new(
        new EquipmentInstanceFingerprint(scope, "Inventory1", slot, itemId, false, 1, 30000, 0, null, [], null, []),
        SquireDisposition.Desynthesize,
        ["RetainedCoverageForAllUnlockedJobs"]);
}
