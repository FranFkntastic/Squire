using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Equipment;
using Lumina.Excel.Sheets;
using MarketMafioso.Squire.Observation;
using System.Security.Cryptography;
using System.Text;

namespace MarketMafioso.Squire.Outfitter.Utility;

public sealed record RetainerVentureObjectiveOption(
    uint TaskId,
    uint ItemId,
    string ItemName,
    uint RequiredRetainerLevel,
    RetainerProcurementObjective Objective,
    string Label);

public sealed record RetainerVentureObjectiveResolution(
    IReadOnlyList<RetainerVentureObjectiveOption> Options,
    string Diagnostic);

/// <summary>
/// Resolves a name-first targeted-procurement objective from the installed game's current
/// RetainerTask definitions. The selected item is user intent; eligibility and yield thresholds
/// remain patch-matched game data rather than mutable plugin constants.
/// </summary>
public sealed class RetainerVentureObjectiveCatalog
{
    private readonly IDataManager dataManager;

    public RetainerVentureObjectiveCatalog(IDataManager dataManager) =>
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));

    public RetainerVentureObjectiveResolution Resolve(
        string exactItemName,
        string jobAbbreviation,
        EquipmentDiscipline discipline,
        uint retainerLevel,
        DateTimeOffset capturedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(exactItemName))
            return new([], "Enter the exact item name produced by the targeted-procurement venture.");
        if (string.IsNullOrWhiteSpace(jobAbbreviation) || retainerLevel is < 1 or > 100 ||
            discipline is not (EquipmentDiscipline.Combat or EquipmentDiscipline.Gatherer))
            return new([], "The selected retainer has no supported battle or MIN/BTN job identity.");

        var items = dataManager.GetExcelSheet<Item>() ?? throw new InvalidOperationException("Item sheet is unavailable.");
        var normals = dataManager.GetExcelSheet<RetainerTaskNormal>() ??
                      throw new InvalidOperationException("RetainerTaskNormal sheet is unavailable.");
        var tasks = dataManager.GetExcelSheet<RetainerTask>() ??
                    throw new InvalidOperationException("RetainerTask sheet is unavailable.");
        var matchingItems = items.Where(value => value.RowId > 0 &&
                string.Equals(value.Name.ToString(), exactItemName.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(value => value.RowId)
            .ToHashSet();
        if (matchingItems.Count == 0)
            return new([], $"No installed-game item exactly matches '{exactItemName.Trim()}'.");

        var normalRows = normals.Where(value => matchingItems.Contains(value.Item.RowId))
            .ToDictionary(value => value.RowId, value => value);
        var options = new List<RetainerVentureObjectiveOption>();
        foreach (var task in tasks.Where(value =>
                     !value.IsRandom &&
                     value.Task.RowId != 0 &&
                     normalRows.ContainsKey(value.Task.RowId) &&
                     value.RetainerLevel <= retainerLevel &&
                     DalamudCharacterEquipmentSnapshotSource.IsEligible(value.ClassJobCategory.Value, jobAbbreviation)))
        {
            var normal = normalRows[task.Task.RowId];
            var profile = discipline == EquipmentDiscipline.Combat
                ? RetainerProcurementProfileKind.Battle
                : RetainerProcurementProfileKind.Gathering;
            var parameters = task.RetainerTaskParameter.Value;
            var thresholds = BuildThresholds(
                normal.Quantity.ToArray(),
                profile == RetainerProcurementProfileKind.Battle
                    ? parameters.ItemLevelDoW.Select(value => checked((int)value)).ToArray()
                    : parameters.PerceptionDoL.Select(value => checked((int)value)).ToArray());
            var required = profile == RetainerProcurementProfileKind.Battle
                ? task.RequiredItemLevel
                : task.RequiredGathering;
            if (thresholds.Count == 0)
                continue;
            var itemName = normal.Item.Value.Name.ToString();
            var objective = new RetainerProcurementObjective(
                $"retainer-task:{task.RowId}:{itemName}",
                profile,
                required,
                thresholds,
                DefinitionGeneration(task.RowId, normal.Item.RowId, profile, required, thresholds),
                capturedAtUtc,
                true);
            options.Add(new(
                task.RowId,
                normal.Item.RowId,
                itemName,
                task.RetainerLevel,
                objective,
                $"{itemName} · Lv. {task.RetainerLevel:N0} · {thresholds[^1].Quantity:N0} max"));
        }

        var ordered = options.OrderByDescending(value => value.RequiredRetainerLevel).ThenBy(value => value.TaskId).ToArray();
        return ordered.Length == 0
            ? new([], $"'{exactItemName.Trim()}' has no targeted-procurement venture compatible with {jobAbbreviation} at level {retainerLevel:N0}.")
            : new(ordered, $"Resolved {ordered.Length:N0} current installed-game venture definition{(ordered.Length == 1 ? string.Empty : "s")}.");
    }

    internal static IReadOnlyList<RetainerYieldThreshold> BuildThresholds(
        IReadOnlyList<byte> quantities,
        IReadOnlyList<int> requirements)
    {
        if (quantities.Count == 0 || requirements.Any(value => value < 0))
            return [];
        var count = Math.Min(requirements.Count, quantities.Count - 1);
        var values = new List<RetainerYieldThreshold>(count + 1)
        {
            new(0, quantities[0]),
        };
        for (var index = 0; index < count; index++)
        {
            if (quantities[index + 1] <= 0)
                continue;
            values.Add(new(requirements[index], quantities[index + 1]));
        }
        return values
            .GroupBy(value => value.RequiredStat)
            .Select(group => group.OrderByDescending(value => value.Quantity).First())
            .OrderBy(value => value.RequiredStat)
            .ToArray();
    }

    internal static Guid DefinitionGeneration(
        uint taskId,
        uint itemId,
        RetainerProcurementProfileKind profile,
        int requiredEligibility,
        IReadOnlyList<RetainerYieldThreshold> thresholds)
    {
        var canonical = new StringBuilder()
            .Append("squire-retainer-venture/v1|")
            .Append(taskId).Append('|').Append(itemId).Append('|').Append(profile).Append('|')
            .Append(requiredEligibility);
        foreach (var threshold in thresholds.OrderBy(value => value.RequiredStat))
            canonical.Append('|').Append(threshold.RequiredStat).Append(':').Append(threshold.Quantity);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
