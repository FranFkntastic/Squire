using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Plots;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class AdvisorFrontierPresentationTests
{
    [Fact]
    public void LargeFrontier_UsesBoundedExactWindowsAndKeepsEverySolutionReachable()
    {
        const int solutionCount = 2_030;
        var pareto = new EquipmentParetoResult(
            Enumerable.Range(0, solutionCount).Select(Solution).ToArray(),
            [],
            [],
            []);
        var presentation = new AdvisorFrontierPresentation(pareto);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < solutionCount; offset += AdvisorFrontierPresentation.MaxFrameSolutionCount)
        {
            var page = presentation.WindowFrom(offset);
            Assert.InRange(page.Solutions.Count, 1, AdvisorFrontierPresentation.MaxFrameSolutionCount);
            foreach (var solution in page.Solutions)
                reached.Add(solution.Candidate.SolutionId);
        }

        var selected = presentation.At(1_000);
        var selectedWindow = presentation.WindowAround(selected.Candidate.SolutionId);
        var plot = new ParetoFrontierPlotBuilder().Build(selectedWindow.ToPlotResult());
        Assert.Equal(solutionCount, presentation.Count);
        Assert.Equal(solutionCount, reached.Count);
        Assert.Equal("solution-00999", presentation.Previous(selected.Candidate.SolutionId)!.Candidate.SolutionId);
        Assert.Equal("solution-01001", presentation.Next(selected.Candidate.SolutionId)!.Candidate.SolutionId);
        Assert.Contains(selected, selectedWindow.Solutions);
        Assert.Equal(AdvisorFrontierPresentation.MaxFrameSolutionCount, selectedWindow.Solutions.Count);
        Assert.Equal(AdvisorFrontierPresentation.MaxFrameSolutionCount, plot.SolutionsByDatumId.Count);
        Assert.True(
            plot.Spec.Layers.Sum(layer => layer switch
            {
                PlotPointLayer points => points.Data.Count,
                PlotPolylineLayer line => line.Data.Count,
                _ => 0,
            }) <= AdvisorFrontierPresentation.MaxFrameSolutionCount * 2);
    }

    [Fact]
    public void Decision_choices_collapse_interchangeable_exact_paths_and_preserve_nomination()
    {
        var solutions = new[]
        {
            Choice(0, 0, 0),
            Choice(1, 100, 10),
            Choice(2, 80, 10),
            Choice(3, 120, 20),
            Choice(4, 140, 20),
        };
        var pareto = new EquipmentParetoResult(solutions, [], [], []);
        var authority = solutions.ToDictionary(
            solution => solution.Candidate.SolutionId,
            solution => new AdvisorAuthorityAssessment(
                solution.Candidate.SolutionId != "solution-00000",
                solution.Candidate.SolutionId == "solution-00000" ? UpgradeAssessment.Equivalent : UpgradeAssessment.ClearImprovement,
                solution.Candidate.SolutionId == "solution-00000" ? [] : ["no-loss-strength-gain"],
                []),
            StringComparer.Ordinal);

        var presentation = new AdvisorFrontierPresentation(pareto, authority, "solution-00003");

        Assert.Equal(5, presentation.TotalExactCount);
        Assert.Equal(3, presentation.Count);
        Assert.True(presentation.TryGet("solution-00002", out _));
        Assert.True(presentation.TryGet("solution-00003", out _));
        Assert.False(presentation.TryGet("solution-00001", out _));
        Assert.False(presentation.TryGet("solution-00004", out _));
    }

    private static EquipmentDecisionSolution Choice(int index, ulong cost, double utility) =>
        Solution(index) with
        {
            AcquisitionCostGil = cost,
            Utility = Solution(index).Utility with { UtilityScore = utility },
        };

    private static EquipmentDecisionSolution Solution(int index) => new(
        new($"solution-{index:D5}", []),
        new(
            new("test", "1"),
            new("test", 16, 100, "Test", []),
            index,
            new(index, index, []),
            UpgradeAssessment.ClearImprovement,
            [],
            [],
            [],
            EquipmentEvaluationConfidence.High,
            []),
        checked((ulong)index),
        new(0, 0, 0),
        new(0, 0, 0),
        []);
}
