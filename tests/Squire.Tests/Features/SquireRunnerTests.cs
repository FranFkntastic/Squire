using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireRunnerTests
{
    private static readonly CharacterScope Scope = new(1, "Runner", 21);

    [Fact]
    public async Task ExactSlotMismatch_StopsWithoutExecutingOrSearching()
    {
        var adapter = new FakeAdapter { Validation = SquireRevalidationResult.Fail("ExactSlotMismatch", "Moved") };
        var result = await new SquireRunner(adapter).RunAsync(Plan(), true, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("ExactSlotMismatch", result.Code);
        Assert.Equal(0, adapter.ExecuteCount);
        Assert.True(adapter.Released);
    }

    [Fact]
    public async Task MissingConfirmation_NeverTouchesGameAdapter()
    {
        var adapter = new FakeAdapter();
        var result = await new SquireRunner(adapter).RunAsync(Plan(SquireDisposition.Discard), false, CancellationToken.None);
        Assert.Equal("ConfirmationRequired", result.Code);
        Assert.Equal(0, adapter.RevalidateCount);
        Assert.Equal(0, adapter.ExecuteCount);
    }

    [Fact]
    public async Task CharacterChange_StopsPlan()
    {
        var adapter = new FakeAdapter { ActiveScope = new CharacterScope(2, "Other", 21) };
        var result = await new SquireRunner(adapter).RunAsync(Plan(), true, CancellationToken.None);
        Assert.Equal("CharacterScopeChanged", result.Code);
        Assert.Equal(0, adapter.ExecuteCount);
    }

    [Fact]
    public async Task Cancellation_ReleasesOwnedState()
    {
        var adapter = new FakeAdapter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new SquireRunner(adapter).RunAsync(Plan(), true, cancellation.Token);
        Assert.Equal("Cancelled", result.Code);
        Assert.True(adapter.Released);
    }

    [Fact]
    public async Task MixedDispositionPlan_RoutesEachActionByItsOwnDisposition()
    {
        var first = new EquipmentInstanceFingerprint(Scope, "Inventory1", 2, 100, false, 1, 30000, 0, null, [], null, []);
        var second = new EquipmentInstanceFingerprint(Scope, "Inventory1", 3, 200, false, 1, 30000, 0, null, [], null, []);
        var plan = new SquireActionPlan(Guid.NewGuid(), Scope, SquireDisposition.Unsupported, DateTimeOffset.UtcNow,
        [
            new(first, SquireDisposition.ExpertDelivery, ["RetainedCoverageForAllUnlockedJobs"]),
            new(second, SquireDisposition.Desynthesize, ["RetainedCoverageForAllUnlockedJobs"]),
        ]);
        var adapter = new FakeAdapter();

        var result = await new SquireRunner(adapter).RunAsync(plan, true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal([SquireDisposition.Desynthesize, SquireDisposition.ExpertDelivery], adapter.ExecutedDispositions);
        Assert.Equal([SquireDisposition.Desynthesize, SquireDisposition.ExpertDelivery], adapter.BegunGroups);
        Assert.Equal([SquireDisposition.Desynthesize, SquireDisposition.ExpertDelivery], adapter.EndedGroups);
        Assert.All(
            result.Events.Where(value => value.Kind == "DispositionGroupPreparation"),
            value => Assert.DoesNotContain("slot transition", value.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiagnosticRun_ExecutesEveryActionAndUsesDiagnosticEvents()
    {
        var adapter = new FakeAdapter();
        var result = await new SquireRunner(adapter).RunDiagnosticAsync(Plan(SquireDisposition.Desynthesize), true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("DiagnosticCompleted", result.Code);
        Assert.Equal(1, adapter.ExecuteCount);
        Assert.Contains(result.Events, value => value.Kind == "DiagnosticActionResult");
    }

    [Fact]
    public async Task DiagnosticRun_StillRequiresDestructiveConfirmation()
    {
        var adapter = new FakeAdapter();
        var result = await new SquireRunner(adapter).RunDiagnosticAsync(Plan(SquireDisposition.Desynthesize), false, CancellationToken.None);

        Assert.Equal("ConfirmationRequired", result.Code);
        Assert.Equal(0, adapter.ExecuteCount);
    }

    [Fact]
    public async Task RecoveryFailure_StopsBeforeGroupPreparationOrExecution()
    {
        var adapter = new FakeAdapter
        {
            Recovery = SquireActionResult.Fail("PlayerInCombat", "Combat did not settle."),
        };

        var result = await new SquireRunner(adapter).RunAsync(Plan(), true, CancellationToken.None);

        Assert.Equal("PlayerInCombat", result.Code);
        Assert.Equal(1, adapter.RecoveryCount);
        Assert.Empty(adapter.BegunGroups);
        Assert.Equal(0, adapter.ExecuteCount);
        Assert.True(adapter.Released);
    }

    [Fact]
    public async Task Recovery_IsPerformedOnceAtTheDispositionOwnershipBoundary()
    {
        var adapter = new FakeAdapter();

        var result = await new SquireRunner(adapter).RunAsync(Plan(), true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, adapter.RecoveryCount);
        Assert.Single(result.Events, value => value.Kind == "ExecutionRecovery");
    }

    [Fact]
    public async Task Recovery_DoesNotInterruptAnOwnedMultiItemDispositionBatch()
    {
        var adapter = new FakeAdapter();
        var plan = new SquireActionPlan(
            Guid.NewGuid(),
            Scope,
            SquireDisposition.ExpertDelivery,
            DateTimeOffset.UtcNow,
            [Selection(1, SquireDisposition.ExpertDelivery), Selection(2, SquireDisposition.ExpertDelivery)]);

        var result = await new SquireRunner(adapter).RunAsync(plan, true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, adapter.RecoveryCount);
        Assert.Equal(2, adapter.ExecuteCount);
        Assert.Equal([SquireDisposition.ExpertDelivery], adapter.BegunGroups);
        Assert.Equal([SquireDisposition.ExpertDelivery], adapter.EndedGroups);
    }

    [Fact]
    public async Task CheckpointResume_AuditsResumeAndExecutesOnlyCheckpointSuffix()
    {
        var checkpoint = new SquireActionPlan(
            Guid.NewGuid(),
            Scope,
            SquireDisposition.VendorSell,
            DateTimeOffset.UtcNow,
            [Selection(2, SquireDisposition.VendorSell)]);
        var adapter = new FakeAdapter();

        var result = await new SquireRunner(adapter).ResumeFromCheckpointAsync(
            checkpoint,
            diagnostic: false,
            cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(adapter.ExecutedDispositions);
        Assert.Contains(result.Events, value => value.Kind == "CheckpointResume" && value.Message.Contains("1 unfinished action"));
    }

    [Fact]
    public async Task RecoveryFailureBetweenGroups_DoesNotCloseCompletedGroupTwice()
    {
        var first = Selection(1, SquireDisposition.Desynthesize);
        var second = Selection(2, SquireDisposition.ExpertDelivery);
        var adapter = new FakeAdapter();
        adapter.RecoveryResults.Enqueue(SquireActionResult.Completed("Ready for first group."));
        adapter.RecoveryResults.Enqueue(SquireActionResult.Fail("PlayerInCombat", "Combat began between groups."));
        var plan = new SquireActionPlan(Guid.NewGuid(), Scope, SquireDisposition.Unsupported, DateTimeOffset.UtcNow, [first, second]);

        var result = await new SquireRunner(adapter).RunAsync(plan, true, CancellationToken.None);

        Assert.Equal("PlayerInCombat", result.Code);
        Assert.Equal([SquireDisposition.Desynthesize], adapter.EndedGroups);
        Assert.Equal([SquireDisposition.Desynthesize], adapter.ExecutedDispositions);
    }

    [Fact]
    public void DispositionBatching_GroupsActionsInExecutionOrderAndPreservesOrderWithinGroup()
    {
        var actions = new[]
        {
            Selection(1, SquireDisposition.ExpertDelivery),
            Selection(2, SquireDisposition.Desynthesize),
            Selection(3, SquireDisposition.ExpertDelivery),
            Selection(4, SquireDisposition.Discard),
            Selection(5, SquireDisposition.VendorSell),
            Selection(6, SquireDisposition.Desynthesize),
        };

        var ordered = SquireDispositionBatching.Order(actions);

        Assert.Equal([2, 6, 1, 3, 5, 4], ordered.Select(value => value.Fingerprint.SlotIndex));
    }

    private static SquireReviewedSelection Selection(int slot, SquireDisposition disposition) =>
        new(new EquipmentInstanceFingerprint(Scope, "Inventory1", slot, (uint)(100 + slot), false, 1, 30000, 0, null, [], null, []), disposition, []);

    private static SquireActionPlan Plan(SquireDisposition disposition = SquireDisposition.VendorSell)
    {
        var fingerprint = new EquipmentInstanceFingerprint(Scope, "Inventory1", 2, 100, false, 1, 30000, 0, null, [], null, []);
        return new SquireActionPlan(Guid.NewGuid(), Scope, disposition, DateTimeOffset.UtcNow,
            [new SquireReviewedSelection(fingerprint, disposition, ["RetainedCoverageForAllUnlockedJobs"])]);
    }

    private sealed class FakeAdapter : ISquireActionGameAdapter
    {
        public CharacterScope? ActiveScope { get; set; } = Scope;
        public SquireRevalidationResult Validation { get; set; } = SquireRevalidationResult.Valid();
        public SquireActionResult Recovery { get; set; } = SquireActionResult.Completed("Ready.");
        public Queue<SquireActionResult> RecoveryResults { get; } = new();
        public int RecoveryCount { get; private set; }
        public int RevalidateCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public int ProbeCount { get; private set; }
        public bool Released { get; private set; }
        public List<SquireDisposition> ExecutedDispositions { get; } = [];
        public List<SquireDisposition> BegunGroups { get; } = [];
        public List<SquireDisposition> EndedGroups { get; } = [];
        public CharacterScope? GetActiveCharacter() => ActiveScope;
        public bool HasConflictingAutomation(SquireDisposition disposition) => false;
        public SquireRevalidationResult Revalidate(EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition)
        {
            RevalidateCount++;
            return Validation;
        }
        public SquireRevalidationResult RevalidateBatch(SquireActionPlan plan, IReadOnlyList<SquireReviewedSelection> remainingActions) => Validation;
        public SquireRevalidationResult RevalidateEvidence(SquireReviewedSelection selection) => Validation;
        public Task<SquireActionResult> ExecuteAsync(SquireActionPlan plan, IReadOnlyList<SquireReviewedSelection> remainingActions, SquireReviewedSelection action, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            ExecutedDispositions.Add(action.Disposition);
            return Task.FromResult(SquireActionResult.Completed("Expected slot transition was observed."));
        }
        public Task<SquireActionResult> ProbeAsync(EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition, CancellationToken cancellationToken)
        {
            ProbeCount++;
            return Task.FromResult(new SquireActionResult(true, "DiagnosticProbePassed", "Passed."));
        }
        public Task<SquireActionResult> BeginDispositionGroupAsync(SquireDisposition disposition, CancellationToken cancellationToken)
        {
            BegunGroups.Add(disposition);
            return Task.FromResult(SquireActionResult.Completed($"{disposition} test batch is ready."));
        }
        public Task<SquireActionResult> RecoverExecutionStateAsync(CancellationToken cancellationToken)
        {
            RecoveryCount++;
            return Task.FromResult(RecoveryResults.TryDequeue(out var result) ? result : Recovery);
        }
        public Task EndDispositionGroupAsync(SquireDisposition disposition, CancellationToken cancellationToken)
        {
            EndedGroups.Add(disposition);
            return Task.CompletedTask;
        }
        public void ReleaseOwnedState() => Released = true;
    }
}
