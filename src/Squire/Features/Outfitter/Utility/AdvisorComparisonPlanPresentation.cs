using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

internal sealed record AdvisorComparisonPlan(
    string Label,
    EquipmentDecisionSolution Solution);

internal static class AdvisorComparisonPlanPresentation
{
    public static IReadOnlyList<AdvisorComparisonPlan> Build(
        AdvisorFrontierPresentation presentation,
        MinerBotanistReadOnlyAdvice advice)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(advice);

        var result = new List<AdvisorComparisonPlan>(4);
        var included = new HashSet<string>(StringComparer.Ordinal);
        var all = Enumerable.Range(0, presentation.Count)
            .Select(presentation.At)
            .ToArray();
        var cheapestSupported = all
            .Where(value => advice.AuthorityBySolutionId.TryGetValue(value.Candidate.SolutionId, out var authority) &&
                            authority.AdvisorMayConsider)
            .OrderBy(value => value.AcquisitionCostGil)
            .ThenBy(value => value.Burden.WorldVisits)
            .ThenBy(value => value.Burden.PurchaseTransactions)
            .ThenByDescending(value => value.Utility.UtilityScore)
            .FirstOrDefault();

        Add("Recommended", advice.Nomination);
        Add("Cheapest supported", cheapestSupported);

        var reference = advice.Nomination ?? cheapestSupported ?? presentation.First;
        var higherUtility = all
            .Where(value => value.Utility.UtilityScore > reference.Utility.UtilityScore)
            .OrderBy(value => value.AcquisitionCostGil)
            .ThenBy(value => value.Burden.WorldVisits)
            .ThenBy(value => value.Burden.PurchaseTransactions)
            .ThenByDescending(value => value.Utility.UtilityScore)
            .FirstOrDefault();
        if (higherUtility is not null)
        {
            var mayNominate = advice.AuthorityBySolutionId.TryGetValue(
                                  higherUtility.Candidate.SolutionId,
                                  out var authority) &&
                              authority.AdvisorMayConsider;
            Add(mayNominate ? "Higher utility" : "Higher utility · not nominated", higherUtility);
        }

        Add(
            "Best owned",
            all.Where(value => value.AcquisitionCostGil == 0)
                .OrderByDescending(value => value.Utility.UtilityScore)
                .ThenBy(value => value.Burden.WorldVisits)
                .ThenBy(value => value.Burden.PurchaseTransactions)
                .FirstOrDefault());
        return result;

        void Add(string label, EquipmentDecisionSolution? solution)
        {
            if (solution is not null && included.Add(solution.Candidate.SolutionId))
                result.Add(new(label, solution));
        }
    }
}
