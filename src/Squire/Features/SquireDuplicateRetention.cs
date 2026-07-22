using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public static class SquireDuplicateRetention
{
    public static IReadOnlyList<SquireRule> Merge(
        IReadOnlyList<SquireRule>? approved,
        IReadOnlyList<SquireRule>? current)
    {
        var active = (approved ?? []).Concat(current ?? [])
            .Where(rule => rule.Enabled)
            .ToArray();
        var invalid = active.Where(rule => !rule.IsValid(out _));
        var protections = active
            .Where(rule => rule.IsValid(out _) && rule.Kind == SquireRuleKind.ProtectItem)
            .GroupBy(rule => rule.ItemId)
            .Select(group => group.First());
        var retention = active
            .Where(rule => rule.IsValid(out _) && rule.Kind == SquireRuleKind.RetainCopies)
            .GroupBy(rule => new { rule.ItemId, rule.Quality })
            .Select(group => group.OrderByDescending(rule => rule.MinimumCopies).First());
        return invalid.Concat(protections).Concat(retention).ToArray();
    }

    public static bool DoesNotReduceRequiredMultiplicity(
        IEnumerable<EquipmentInstanceSnapshot> before,
        IEnumerable<EquipmentInstanceSnapshot> after,
        SquireProtectionPolicy policy,
        out string message)
    {
        var beforeArray = before.ToArray();
        var afterArray = after.ToArray();
        foreach (var rule in Merge(policy.Rules, null).Where(rule => rule.Kind == SquireRuleKind.RetainCopies))
        {
            var beforeCount = Count(beforeArray, rule);
            var afterCount = Count(afterArray, rule);
            var required = Math.Min(beforeCount, rule.MinimumCopies);
            if (afterCount >= required)
                continue;
            var quality = rule.Quality == SquireRuleQuality.HighQuality ? "HQ" : "normal-quality";
            var identity = string.IsNullOrWhiteSpace(rule.Note) ? rule.Id.ToString("N") : $"'{rule.Note}' ({rule.Id:N})";
            message = $"The selected batch would leave {afterCount} {quality} copies of item {rule.ItemId}; retention rule {identity} retains at least {required}.";
            return false;
        }

        message = "All explicit duplicate retention floors remain satisfied.";
        return true;
    }

    public static bool DoesNotReduceRequiredMultiplicity(
        IEnumerable<EquipmentInstanceSnapshot> before,
        IEnumerable<EquipmentInstanceSnapshot> after,
        IEnumerable<SquireCandidate> evaluatedCandidates,
        out string message)
    {
        var beforeArray = before.ToArray();
        var afterArray = after.ToArray();
        var floors = evaluatedCandidates
            .Where(candidate => candidate.DuplicateStatus?.UserMinimumCopies > 0)
            .GroupBy(candidate => new
            {
                candidate.Definition.ItemId,
                candidate.Instance.Fingerprint.IsHighQuality,
            })
            .Select(group => new
            {
                group.Key.ItemId,
                group.Key.IsHighQuality,
                Minimum = group.Max(candidate => candidate.DuplicateStatus!.UserMinimumCopies),
            });
        foreach (var floor in floors)
        {
            var beforeCount = beforeArray.Count(instance =>
                instance.Fingerprint.ItemId == floor.ItemId &&
                instance.Fingerprint.IsHighQuality == floor.IsHighQuality);
            var afterCount = afterArray.Count(instance =>
                instance.Fingerprint.ItemId == floor.ItemId &&
                instance.Fingerprint.IsHighQuality == floor.IsHighQuality);
            var required = Math.Min(beforeCount, floor.Minimum);
            if (afterCount >= required)
                continue;
            message = $"The selected batch would leave {afterCount} {(floor.IsHighQuality ? "HQ" : "normal-quality")} copies of item {floor.ItemId}; the cleanup policy retains at least {required}.";
            return false;
        }

        message = "All configured retained-copy floors remain satisfied.";
        return true;
    }

    private static int Count(IEnumerable<EquipmentInstanceSnapshot> instances, SquireRule rule) =>
        instances.Count(instance =>
            instance.Fingerprint.ItemId == rule.ItemId &&
            instance.Fingerprint.IsHighQuality == (rule.Quality == SquireRuleQuality.HighQuality));
}
