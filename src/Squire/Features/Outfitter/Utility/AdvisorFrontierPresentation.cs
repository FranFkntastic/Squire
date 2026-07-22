using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

internal sealed record AdvisorFrontierWindow(
    int Offset,
    IReadOnlyList<EquipmentDecisionSolution> Solutions,
    int TotalCount)
{
    public int EndOffset => Offset + Solutions.Count;
    public bool HasPrevious => Offset > 0;
    public bool HasNext => EndOffset < TotalCount;

    public EquipmentParetoResult ToPlotResult() => new(Solutions, [], [], []);
}

/// <summary>
/// Indexes the complete authoritative frontier once, then exposes bounded exact presentation
/// windows without copying or pruning the underlying solutions.
/// </summary>
internal sealed class AdvisorFrontierPresentation
{
    public const int MaxFrameSolutionCount = 64;

    private readonly EquipmentDecisionSolution[] ordered;
    private readonly Dictionary<string, int> indices;

    public AdvisorFrontierPresentation(EquipmentParetoResult pareto)
    {
        ArgumentNullException.ThrowIfNull(pareto);
        ordered = pareto.Frontier
            .OrderBy(value => value.AcquisitionCostGil)
            .ThenBy(value => value.Utility.UtilityScore)
            .ThenBy(value => value.Candidate.SolutionId, StringComparer.Ordinal)
            .ToArray();
        indices = ordered
            .Select((solution, index) => (solution.Candidate.SolutionId, Index: index))
            .ToDictionary(value => value.SolutionId, value => value.Index, StringComparer.Ordinal);
    }

    public int Count => ordered.Length;
    public EquipmentDecisionSolution First => ordered[0];

    public bool TryGet(string? solutionId, out EquipmentDecisionSolution solution)
    {
        if (solutionId is not null && indices.TryGetValue(solutionId, out var index))
        {
            solution = ordered[index];
            return true;
        }

        solution = null!;
        return false;
    }

    public int IndexOf(string solutionId) => indices.GetValueOrDefault(solutionId, -1);

    public EquipmentDecisionSolution At(int index) => ordered[Math.Clamp(index, 0, ordered.Length - 1)];

    public EquipmentDecisionSolution? Previous(string solutionId) =>
        indices.TryGetValue(solutionId, out var index) && index > 0 ? ordered[index - 1] : null;

    public EquipmentDecisionSolution? Next(string solutionId) =>
        indices.TryGetValue(solutionId, out var index) && index + 1 < ordered.Length ? ordered[index + 1] : null;

    public AdvisorFrontierWindow WindowAround(string? solutionId, int count = MaxFrameSolutionCount)
    {
        var selectedIndex = solutionId is not null && indices.TryGetValue(solutionId, out var found) ? found : 0;
        var boundedCount = Math.Clamp(count, 1, MaxFrameSolutionCount);
        var offset = Math.Clamp(selectedIndex - boundedCount / 2, 0, Math.Max(0, ordered.Length - boundedCount));
        return WindowFrom(offset, boundedCount);
    }

    public AdvisorFrontierWindow WindowFrom(int offset, int count = MaxFrameSolutionCount)
    {
        var boundedCount = Math.Clamp(count, 1, MaxFrameSolutionCount);
        var boundedOffset = Math.Clamp(offset, 0, Math.Max(0, ordered.Length - 1));
        var visibleCount = Math.Min(boundedCount, ordered.Length - boundedOffset);
        return new(boundedOffset, new ArraySegment<EquipmentDecisionSolution>(ordered, boundedOffset, visibleCount), ordered.Length);
    }
}
