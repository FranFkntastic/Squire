using System;
using System.Collections.Generic;
using System.Linq;

#pragma warning disable CS0618 // Kept only to migrate schema-v1 configuration into configurable cleanup rules.

namespace MarketMafioso.Squire;

public sealed class SquireRuleStore
{
    private readonly ISquireConfigurationStore config;
    private readonly Action save;

    public SquireRuleStore(ISquireConfigurationStore config, Action? save = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.save = save ?? config.Save;
    }

    public IReadOnlyList<SquireRule> Get(ulong? contentId)
    {
        if (contentId is null || !config.Squire.RulesByCharacter.TryGetValue(contentId.Value.ToString(), out var values) || values is null)
            return [];
        return values.Select(ToRule).ToArray();
    }

    public SquireProtectionPolicy CreatePolicy(ulong? contentId) => new(
        config.Squire.ProtectPlayerSignedGear,
        config.Squire.ProtectFutureLevelingGearOptIn,
        config.Squire.ProtectBlueAndPurpleGear,
        config.Squire.AllowRiskyMateriaRetrieval,
        Get(contentId));

    public void SetItemProtection(ulong? contentId, uint itemId, bool protectedFromCleanup, string? note = null)
    {
        if (contentId is null || itemId == 0)
            return;
        var rules = GetConfigurations(contentId.Value);
        var existing = rules.FirstOrDefault(rule => rule.Kind == SquireRuleKind.ProtectItem && rule.ItemId == itemId);
        if (!protectedFromCleanup)
        {
            if (existing is not null)
                rules.Remove(existing);
        }
        else if (existing is null)
        {
            rules.Add(new SquireRuleConfiguration
            {
                Kind = SquireRuleKind.ProtectItem,
                ItemId = itemId,
                Quality = SquireRuleQuality.Any,
                Enabled = true,
                Note = note?.Trim() ?? string.Empty,
            });
        }
        else
        {
            existing.Enabled = true;
            if (note is not null)
                existing.Note = note.Trim();
        }
        save();
    }

    public void SetRetention(ulong? contentId, uint itemId, bool isHighQuality, int minimumCopies, string? note = null)
    {
        if (contentId is null || itemId == 0)
            return;
        var quality = isHighQuality ? SquireRuleQuality.HighQuality : SquireRuleQuality.NormalQuality;
        var rules = GetConfigurations(contentId.Value);
        var existing = rules.FirstOrDefault(rule =>
            rule.Kind == SquireRuleKind.RetainCopies && rule.ItemId == itemId && rule.Quality == quality);
        if (minimumCopies <= 0)
        {
            if (existing is not null)
                rules.Remove(existing);
        }
        else if (existing is null)
        {
            rules.Add(new SquireRuleConfiguration
            {
                Kind = SquireRuleKind.RetainCopies,
                ItemId = itemId,
                Quality = quality,
                MinimumCopies = Math.Clamp(minimumCopies, 1, 99),
                Enabled = true,
                Note = note?.Trim() ?? string.Empty,
            });
        }
        else
        {
            existing.MinimumCopies = Math.Clamp(minimumCopies, 1, 99);
            existing.Enabled = true;
            if (note is not null)
                existing.Note = note.Trim();
        }
        save();
    }

    public void Update(ulong contentId, Guid id, bool? enabled = null, int? minimumCopies = null, string? note = null)
    {
        var rule = GetConfigurations(contentId).FirstOrDefault(value => value.Id == id);
        if (rule is null)
            return;
        if (enabled is not null)
            rule.Enabled = enabled.Value;
        if (minimumCopies is not null && rule.Kind == SquireRuleKind.RetainCopies)
            rule.MinimumCopies = Math.Clamp(minimumCopies.Value, 1, 99);
        if (note is not null)
            rule.Note = note.Trim();
        save();
    }

    public void Remove(ulong contentId, Guid id)
    {
        var rules = GetConfigurations(contentId);
        if (rules.RemoveAll(rule => rule.Id == id) > 0)
            save();
    }

    private List<SquireRuleConfiguration> GetConfigurations(ulong contentId)
    {
        var key = contentId.ToString();
        if (!config.Squire.RulesByCharacter.TryGetValue(key, out var values) || values is null)
            config.Squire.RulesByCharacter[key] = values = [];
        return values;
    }

    private static SquireRule ToRule(SquireRuleConfiguration value) => new(
        value.Id,
        value.Kind,
        value.ItemId,
        value.Quality,
        value.MinimumCopies,
        value.Enabled,
        value.Note ?? string.Empty);
}

public static class SquireRuleMigration
{
    public static bool Migrate(ISquireConfigurationStore config)
    {
        config.Squire ??= new SquireConfiguration();
        return Migrate(config.Squire);
    }

    public static bool Migrate(SquireConfiguration config)
    {
        var changed = false;
        config.RulesByCharacter ??= new();
        config.ExcludedItemIdsByCharacter ??= new();
        config.DuplicateRetentionByCharacter ??= new();
        foreach (var pair in config.ExcludedItemIdsByCharacter)
        {
            var target = GetTarget(config, pair.Key);
            foreach (var itemId in (pair.Value ?? []).Where(itemId => itemId != 0).Distinct())
            {
                if (target.Any(rule => rule.Kind == SquireRuleKind.ProtectItem && rule.ItemId == itemId))
                    continue;
                target.Add(new SquireRuleConfiguration
                {
                    Kind = SquireRuleKind.ProtectItem,
                    ItemId = itemId,
                    Quality = SquireRuleQuality.Any,
                    Note = "Migrated cleanup protection",
                });
                changed = true;
            }
        }
        foreach (var pair in config.DuplicateRetentionByCharacter)
        {
            var target = GetTarget(config, pair.Key);
            foreach (var group in (pair.Value ?? [])
                         .Where(value => value.ItemId != 0 && value.MinimumCopies > 0)
                         .GroupBy(value => new { value.ItemId, value.IsHighQuality }))
            {
                var quality = group.Key.IsHighQuality ? SquireRuleQuality.HighQuality : SquireRuleQuality.NormalQuality;
                if (target.Any(rule => rule.Kind == SquireRuleKind.RetainCopies && rule.ItemId == group.Key.ItemId && rule.Quality == quality))
                    continue;
                target.Add(new SquireRuleConfiguration
                {
                    Kind = SquireRuleKind.RetainCopies,
                    ItemId = group.Key.ItemId,
                    Quality = quality,
                    MinimumCopies = group.Max(value => value.MinimumCopies),
                    Note = "Migrated duplicate retention",
                });
                changed = true;
            }
        }
        if (config.ExcludedItemIdsByCharacter.Count > 0 || config.DuplicateRetentionByCharacter.Count > 0)
        {
            config.ExcludedItemIdsByCharacter.Clear();
            config.DuplicateRetentionByCharacter.Clear();
            changed = true;
        }
        config.RuleSchemaVersion = Math.Max(config.RuleSchemaVersion, 1);
        return changed;
    }

    private static List<SquireRuleConfiguration> GetTarget(SquireConfiguration config, string key)
    {
        if (!config.RulesByCharacter.TryGetValue(key, out var target) || target is null)
            config.RulesByCharacter[key] = target = [];
        return target;
    }
}

#pragma warning restore CS0618
