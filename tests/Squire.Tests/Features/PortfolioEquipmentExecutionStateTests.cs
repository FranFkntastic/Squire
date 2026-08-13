using System.Text.Json;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Portfolio;

namespace MarketMafioso.Tests.Squire;

public sealed class PortfolioEquipmentExecutionStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(10);

    [Fact]
    public void AuthorizeNext_RequiresFreshExactTargetSlotSourceItemAndQuality()
    {
        var state = PortfolioEquipmentExecution.Create("execution-1", [Step("head")]);

        var authorized = PortfolioEquipmentExecution.AuthorizeNext(
            state,
            Before("head", "before-1", Now - TimeSpan.FromSeconds(1)),
            Now,
            FreshFor);
        var stale = PortfolioEquipmentExecution.AuthorizeNext(
            state,
            Before("head", "before-2", Now - TimeSpan.FromMinutes(1)),
            Now,
            FreshFor);
        var wrongQuality = PortfolioEquipmentExecution.AuthorizeNext(
            state,
            Before("head", "before-3", Now - TimeSpan.FromSeconds(1)) with
            {
                AvailableSourceItems = [Item(200, highQuality: false, "source-head")],
            },
            Now,
            FreshFor);

        Assert.Equal(PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, authorized.Status);
        Assert.Equal("before-1", authorized.AuthorizedBeforeEvidenceGeneration);
        Assert.Equal(PortfolioEquipmentExecutionStatus.StoppedForDrift, stale.Status);
        Assert.Contains("fresh", stale.StopReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PortfolioEquipmentExecutionStatus.StoppedForDrift, wrongQuality.Status);
        Assert.Contains("exact source", wrongQuality.StopReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptAfter_AdvancesOnlyForNewFreshExactAfterEvidence()
    {
        var state = PortfolioEquipmentExecution.AuthorizeNext(
            PortfolioEquipmentExecution.Create("execution-1", [Step("head")]),
            Before("head", "before-1", Now - TimeSpan.FromSeconds(2)),
            Now,
            FreshFor);

        var completed = PortfolioEquipmentExecution.AcceptAfter(
            state,
            After("head", "after-1", Now - TimeSpan.FromSeconds(1)),
            Now,
            FreshFor);

        Assert.Equal(PortfolioEquipmentExecutionStatus.Completed, completed.Status);
        Assert.Equal(1, completed.CompletedStepCount);
        Assert.Null(completed.StopReason);
    }

    [Fact]
    public void AcceptAfter_StopsOnSlotDriftWithoutAdvancing()
    {
        var state = PortfolioEquipmentExecution.AuthorizeNext(
            PortfolioEquipmentExecution.Create("execution-1", [Step("head")]),
            Before("head", "before-1", Now - TimeSpan.FromSeconds(2)),
            Now,
            FreshFor);
        var drifted = After("head", "after-1", Now - TimeSpan.FromSeconds(1)) with
        {
            EquippedItem = Item(999, highQuality: false, "unexpected"),
        };

        var stopped = PortfolioEquipmentExecution.AcceptAfter(state, drifted, Now, FreshFor);

        Assert.Equal(PortfolioEquipmentExecutionStatus.StoppedForDrift, stopped.Status);
        Assert.Equal(0, stopped.CompletedStepCount);
        Assert.Contains("expected item and quality", stopped.StopReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoverAfterRestart_ReconcilesPersistedInFlightStepFromExactAfterEvidence()
    {
        var inFlight = PortfolioEquipmentExecution.AuthorizeNext(
            PortfolioEquipmentExecution.Create("execution-1", [Step("head")]),
            Before("head", "before-1", Now - TimeSpan.FromSeconds(2)),
            Now,
            FreshFor);
        var persisted = JsonSerializer.Serialize(inFlight);
        var restored = JsonSerializer.Deserialize<PortfolioEquipmentExecutionState>(persisted)!;

        Assert.Equal(PortfolioEquipmentExecution.CurrentSchemaVersion, restored.SchemaVersion);

        var recovered = PortfolioEquipmentExecution.RecoverAfterRestart(
            restored,
            After("head", "restart-after", Now - TimeSpan.FromSeconds(1)),
            Now,
            FreshFor);

        Assert.Equal(PortfolioEquipmentExecutionStatus.Completed, recovered.Status);
        Assert.Equal(1, recovered.CompletedStepCount);
    }

    [Fact]
    public void RecoverAfterRestart_WaitsThroughSettleWindowThenReturnsToReadyWhenSlotNeverChanged()
    {
        var inFlight = PortfolioEquipmentExecution.AuthorizeNext(
            PortfolioEquipmentExecution.Create("execution-1", [Step("head")]),
            Before("head", "before-1", Now - TimeSpan.FromSeconds(2)),
            Now,
            FreshFor);

        var settling = PortfolioEquipmentExecution.RecoverAfterRestart(
            inFlight,
            Before("head", "restart-before", Now - TimeSpan.FromSeconds(1)),
            Now,
            FreshFor);

        Assert.Equal(PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, settling.Status);
        var recovered = PortfolioEquipmentExecution.RecoverAfterRestart(
            settling,
            Before("head", "restart-before-expired", Now + TimeSpan.FromSeconds(10)),
            Now + TimeSpan.FromSeconds(11),
            FreshFor);

        Assert.Equal(PortfolioEquipmentExecutionStatus.Ready, recovered.Status);
        Assert.Equal(0, recovered.CompletedStepCount);
        Assert.Null(recovered.AuthorizedBeforeEvidenceGeneration);
    }

    [Fact]
    public void StoppedExecutionCanResumeOnlyAfterExactFreshEvidenceReturns()
    {
        var state = PortfolioEquipmentExecution.Create("execution-1", [Step("head")]);
        var stopped = PortfolioEquipmentExecution.AuthorizeNext(
            state,
            Before("other-target", "bad", Now - TimeSpan.FromSeconds(1)),
            Now,
            FreshFor);

        var resumed = PortfolioEquipmentExecution.AuthorizeNext(
            stopped,
            Before("head", "good", Now - TimeSpan.FromSeconds(1)),
            Now,
            FreshFor);

        Assert.Equal(PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, resumed.Status);
        Assert.Null(resumed.StopReason);
    }

    [Fact]
    public void RollBackUnexecutedSuffix_PreservesExecutedPrefixAndNeverClaimsGameplayRollback()
    {
        var state = PortfolioEquipmentExecution.Create("execution-1", [Step("head"), Step("body")]);
        state = PortfolioEquipmentExecution.AuthorizeNext(state, Before("head", "before-1", Now - TimeSpan.FromSeconds(2)), Now, FreshFor);
        state = PortfolioEquipmentExecution.AcceptAfter(state, After("head", "after-1", Now - TimeSpan.FromSeconds(1)), Now, FreshFor);

        var rolledBack = PortfolioEquipmentExecution.RollbackUnexecutedSuffix(state);

        Assert.Equal(PortfolioEquipmentExecutionStatus.UnexecutedSuffixRolledBack, rolledBack.Status);
        Assert.Equal(1, rolledBack.CompletedStepCount);
        Assert.Equal("step-head", Assert.Single(rolledBack.Steps).StepId);
        Assert.Equal(["step-body"], rolledBack.RolledBackUnexecutedStepIds);
        Assert.Null(rolledBack.NextStep);
    }

    [Fact]
    public void Create_SavedGearsetRequiresAndPersistsEveryCanonicalSlotIncludingEmptySlots()
    {
        var target = SavedGearsetTarget();
        var step = Step("head") with { TargetKey = target.TargetKey };
        var targets = new Dictionary<string, PortfolioEquipmentMoveTarget>(StringComparer.Ordinal)
        {
            [target.TargetKey] = target,
        };
        var missing = Assert.Throws<ArgumentException>(() =>
            PortfolioEquipmentExecution.Create("execution-saved", [step], targets));
        Assert.Contains("complete persisted loadout", missing.Message, StringComparison.OrdinalIgnoreCase);

        var partialExpectation = SavedExpectation(target) with
        {
            Slots = SavedExpectation(target).Slots.Take(1).ToArray(),
        };
        Assert.Throws<ArgumentException>(() =>
            PortfolioEquipmentExecution.Create(
                "execution-saved",
                [step],
                targets,
                savedLoadoutExpectations: new Dictionary<string, PortfolioSavedLoadoutExpectation>(StringComparer.Ordinal)
                {
                    [target.TargetKey] = partialExpectation,
                }));

        var state = PortfolioEquipmentExecution.Create(
            "execution-saved",
            [step],
            targets,
            savedLoadoutExpectations: new Dictionary<string, PortfolioSavedLoadoutExpectation>(StringComparer.Ordinal)
            {
                [target.TargetKey] = SavedExpectation(target),
            });
        var restored = JsonSerializer.Deserialize<PortfolioEquipmentExecutionState>(JsonSerializer.Serialize(state))!;
        var expectation = Assert.Single(restored.SavedLoadoutExpectations!).Value;

        Assert.Equal(PlayerAdvisorEquippedSlotMap.All.Count, expectation.Slots.Count);
        Assert.Equal(
            PlayerAdvisorEquippedSlotMap.All.Select(value => value.Position).Order(),
            expectation.Slots.Select(value => value.Position).Order());
        Assert.Equal(11, expectation.Slots.Count(value => value.ExpectedItem is null));
        Assert.Equal(Item(200, highQuality: true, "equipped-head"),
            expectation.Slots.Single(value => value.Position == EquipmentLoadoutPosition.Head).ExpectedItem);
    }

    private static PortfolioEquipmentStep Step(string slot) => slot switch
    {
        "head" => new(
            "step-head",
            "head",
            EquipmentLoadoutPosition.Head,
            Item(200, highQuality: true, "source-head"),
            Item(100, highQuality: false, "old-head"),
            Item(200, highQuality: true, "equipped-head")),
        "body" => new(
            "step-body",
            "body",
            EquipmentLoadoutPosition.Body,
            Item(201, highQuality: false, "source-body"),
            Item(101, highQuality: false, "old-body"),
            Item(201, highQuality: false, "equipped-body")),
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    private static PortfolioEquipmentObservation Before(string target, string generation, DateTimeOffset observedAt) =>
        new(
            target,
            EquipmentLoadoutPosition.Head,
            Item(100, highQuality: false, "old-head"),
            [Item(200, highQuality: true, "source-head")],
            generation,
            observedAt);

    private static PortfolioEquipmentObservation After(string target, string generation, DateTimeOffset observedAt) =>
        new(
            target,
            EquipmentLoadoutPosition.Head,
            Item(200, highQuality: true, "equipped-head"),
            [],
            generation,
            observedAt);

    private static PortfolioExactItem Item(uint itemId, bool highQuality, string instance) =>
        new(itemId, highQuality, instance);

    private static PortfolioEquipmentMoveTarget SavedGearsetTarget() => new(
        "gearset:7",
        PortfolioEquipmentMoveTargetKind.SavedGearset,
        new(1234, "Tester", 74),
        ActiveClassJobId: 3,
        GearsetId: 7,
        GearsetName: "Marauder");

    private static PortfolioSavedLoadoutExpectation SavedExpectation(PortfolioEquipmentMoveTarget target) => new(
        target.TargetKey,
        target.GearsetId!.Value,
        target.GearsetName!,
        target.ActiveClassJobId!.Value,
        PlayerAdvisorEquippedSlotMap.All.Select(canonical => new PortfolioSavedLoadoutSlotExpectation(
            canonical.Position,
            canonical.Position == EquipmentLoadoutPosition.Head
                ? Item(200, highQuality: true, "equipped-head")
                : null)).ToArray());
}
