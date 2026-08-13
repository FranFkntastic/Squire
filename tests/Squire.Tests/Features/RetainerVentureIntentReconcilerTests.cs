using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class RetainerVentureIntentReconcilerTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Exact_task_rehydrates_current_definition_and_survives_duplicate_item_names()
    {
        var oldGeneration = Guid.NewGuid();
        var currentGeneration = Guid.NewGuid();
        var target = Target();
        var result = RetainerVentureIntentReconciler.Reconcile(
            [target],
            new Dictionary<string, string> { [target.Key] = "Copper Ore" },
            new Dictionary<string, uint> { [target.Key] = 22 },
            (_, _) => new([
                Option(21, oldGeneration),
                Option(22, currentGeneration),
            ], "two definitions"));

        Assert.Equal(currentGeneration, Assert.Single(result.Objectives).Value.EvidenceGenerationId);
        Assert.Empty(result.Refusals);
    }

    [Fact]
    public void Removed_or_ambiguous_exact_task_refuses_without_restoring_stale_objective()
    {
        var target = Target();
        var removed = RetainerVentureIntentReconciler.Reconcile(
            [target],
            new Dictionary<string, string> { [target.Key] = "Copper Ore" },
            new Dictionary<string, uint> { [target.Key] = 99 },
            (_, _) => new([Option(21, Guid.NewGuid())], "one definition"));
        var ambiguous = RetainerVentureIntentReconciler.Reconcile(
            [target],
            new Dictionary<string, string> { [target.Key] = "Copper Ore" },
            new Dictionary<string, uint> { [target.Key] = 21 },
            (_, _) => new([Option(21, Guid.NewGuid()), Option(21, Guid.NewGuid())], "bad duplicate"));

        Assert.Empty(removed.Objectives);
        Assert.Contains("no longer exists", Assert.Single(removed.Refusals).Value);
        Assert.Empty(ambiguous.Objectives);
        Assert.Contains("ambiguous", Assert.Single(ambiguous.Refusals).Value);
    }

    private static OutfitterTarget Target() => new(
        "retainer:42",
        OutfitterTargetKind.Retainer,
        "Venture",
        "MIN · Lv. 100");

    private static RetainerVentureObjectiveOption Option(uint taskId, Guid generation) => new(
        taskId,
        1,
        "Copper Ore",
        1,
        new(
            $"retainer-task:{taskId}:Copper Ore",
            RetainerProcurementProfileKind.Gathering,
            1,
            [new(0, 1)],
            generation,
            CapturedAt,
            true),
        $"Copper Ore · task {taskId}");
}
