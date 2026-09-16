using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class AdvisorComparisonPlanPresentationTests
{
    [Fact]
    public void Build_UsesEvaluationLandmarksInsteadOfSelectedSolution()
    {
        var bestOwned = Solution("owned", 0, 10);
        var recommended = Solution("recommended", 50, 20);
        var higherUtility = Solution("higher", 75, 21);
        var fartherHigherUtility = Solution("farther", 200, 30);
        var pareto = new EquipmentParetoResult(
            [bestOwned, recommended, higherUtility, fartherHigherUtility],
            [],
            [],
            []);
        var authority = new Dictionary<string, AdvisorAuthorityAssessment>(StringComparer.Ordinal)
        {
            [bestOwned.Candidate.SolutionId] = Assessment(false),
            [recommended.Candidate.SolutionId] = Assessment(true),
            [higherUtility.Candidate.SolutionId] = Assessment(false),
            [fartherHigherUtility.Candidate.SolutionId] = Assessment(false),
        };
        var advice = new MinerBotanistReadOnlyAdvice(
            MinerBotanistAdvisorStatus.Complete,
            "test",
            null,
            recommended,
            authority,
            new Dictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer>(),
            "test");

        var plans = AdvisorComparisonPlanPresentation.Build(
            new AdvisorFrontierPresentation(pareto),
            advice);

        Assert.Collection(
            plans,
            value =>
            {
                Assert.Equal("Recommended", value.Label);
                Assert.Same(recommended, value.Solution);
            },
            value =>
            {
                Assert.Equal("Higher utility · not nominated", value.Label);
                Assert.Same(higherUtility, value.Solution);
            },
            value =>
            {
                Assert.Equal("Best owned", value.Label);
                Assert.Same(bestOwned, value.Solution);
            });
    }

    private static AdvisorAuthorityAssessment Assessment(bool mayConsider) =>
        new(
            mayConsider,
            UpgradeAssessment.ClearImprovement,
            [],
            []);

    private static EquipmentDecisionSolution Solution(string id, ulong cost, double utility) => new(
        new(id, []),
        new(
            new("test", "1"),
            new("test", 16, 100, "Test", []),
            utility,
            new(0, 0, []),
            UpgradeAssessment.ClearImprovement,
            [],
            [],
            [],
            EquipmentEvaluationConfidence.High,
            []),
        cost,
        new(0, 0, 0),
        new(0, 0, 0),
        []);
}
