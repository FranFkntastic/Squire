namespace MarketMafioso.Squire.Outfitter.Utility;

public sealed record RetainerVentureIntentReconciliation(
    IReadOnlyDictionary<string, RetainerProcurementObjective> Objectives,
    IReadOnlyDictionary<string, string> Refusals);

/// <summary>
/// Rehydrates persisted name-first retainer venture intent against the current installed-game
/// definition. A task id disambiguates duplicate output names; no stale definition is retained.
/// </summary>
public static class RetainerVentureIntentReconciler
{
    public static RetainerVentureIntentReconciliation Reconcile(
        IReadOnlyList<OutfitterTarget> targets,
        IReadOnlyDictionary<string, string> itemNames,
        IReadOnlyDictionary<string, uint> taskIds,
        Func<OutfitterTarget, string, RetainerVentureObjectiveResolution> resolve)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(itemNames);
        ArgumentNullException.ThrowIfNull(taskIds);
        ArgumentNullException.ThrowIfNull(resolve);
        var objectives = new Dictionary<string, RetainerProcurementObjective>(StringComparer.Ordinal);
        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in targets.Where(value => value.Kind == OutfitterTargetKind.Retainer))
        {
            if (!itemNames.TryGetValue(target.Key, out var itemName) || string.IsNullOrWhiteSpace(itemName) ||
                !taskIds.TryGetValue(target.Key, out var taskId) || taskId == 0)
                continue;
            var resolution = resolve(target, itemName);
            var matching = resolution.Options.Where(option => option.TaskId == taskId).ToArray();
            if (matching.Length == 1 && matching[0].Objective.IsDefinitionComplete)
                objectives[target.Key] = matching[0].Objective;
            else
                refusals[target.Key] = matching.Length == 0
                    ? $"The saved venture for {target.Name} no longer exists in the installed game definition."
                    : $"The saved venture for {target.Name} is ambiguous in the installed game definition.";
        }
        return new(objectives, refusals);
    }
}
