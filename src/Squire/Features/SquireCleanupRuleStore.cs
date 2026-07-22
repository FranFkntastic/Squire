using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.Squire;

public sealed class SquireCleanupRuleStore
{
    private readonly ISquireConfigurationStore config;
    private readonly Action save;

    public SquireCleanupRuleStore(ISquireConfigurationStore config, Action? save = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.save = save ?? config.Save;
    }

    public IReadOnlyList<SquireCleanupRule> GetApplicable(ulong? characterContentId)
    {
        var builtIns = SquireBuiltInCleanupRules.CreateDefaults()
            .Select(ApplyOverride);
        var userRules = (config.Squire.CleanupRules ?? [])
            .Select(ToDomain)
            .Where(rule => rule.Scope == SquireCleanupRuleScope.Global ||
                           characterContentId is not null && rule.CharacterContentId == characterContentId);
        return builtIns.Concat(userRules).ToArray();
    }

    public IReadOnlyList<SquireCleanupRule> GetAll() => SquireBuiltInCleanupRules.CreateDefaults()
        .Select(ApplyOverride)
        .Concat((config.Squire.CleanupRules ?? []).Select(ToDomain))
        .ToArray();

    public SquireProtectionPolicy CreatePolicy(ulong? characterContentId)
    {
        var rules = GetApplicable(characterContentId);
        return new SquireProtectionPolicy(
            CharacterContentId: characterContentId ?? 0,
            CleanupRules: rules);
    }

    public SquireCleanupRule Add(SquireCleanupRule rule)
    {
        if (rule.Origin != SquireCleanupRuleOrigin.User)
            throw new InvalidOperationException("Only user rules can be added to configuration.");
        if (GetAll().Any(value => string.Equals(value.Id, rule.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Rule ID '{rule.Id}' already exists.");
        config.Squire.CleanupRules ??= [];
        config.Squire.CleanupRules.Add(ToConfiguration(rule));
        save();
        return rule;
    }

    public bool Replace(SquireCleanupRule rule)
    {
        if (rule.Origin != SquireCleanupRuleOrigin.User)
            throw new InvalidOperationException("Built-in rules use overrides and cannot be replaced.");
        var index = config.Squire.CleanupRules.FindIndex(value =>
            string.Equals(value.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;
        config.Squire.CleanupRules[index] = ToConfiguration(rule);
        save();
        return true;
    }

    public bool Remove(string id)
    {
        var removed = config.Squire.CleanupRules.RemoveAll(value =>
            string.Equals(value.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            save();
        return removed;
    }

    public void SetBuiltInOverride(string id, bool? enabled = null, int? priority = null)
    {
        if (!SquireBuiltInCleanupRules.CreateDefaults().Any(rule => rule.Id == id))
            throw new InvalidOperationException($"Built-in rule '{id}' does not exist.");
        config.Squire.BuiltInRuleOverrides ??= new();
        if (!config.Squire.BuiltInRuleOverrides.TryGetValue(id, out var value) || value is null)
            config.Squire.BuiltInRuleOverrides[id] = value = new();
        if (enabled is not null)
            value.Enabled = enabled;
        if (priority is not null)
            value.Priority = Math.Clamp(priority.Value, 0, 10_000);
        save();
    }

    public SquireCleanupRule CopyBuiltIn(string id, ulong? characterContentId = null)
    {
        var source = GetAll().Single(rule => rule.Origin == SquireCleanupRuleOrigin.BuiltIn && rule.Id == id);
        var copy = source with
        {
            Id = $"user.{Guid.NewGuid():N}",
            Name = $"Copy of {source.Name}",
            Origin = SquireCleanupRuleOrigin.User,
            Scope = characterContentId is null ? SquireCleanupRuleScope.Global : SquireCleanupRuleScope.Character,
            CharacterContentId = characterContentId,
            Priority = Math.Min(10_000, source.Priority + 100),
        };
        return Add(copy);
    }

    public bool IsItemProtected(ulong? characterContentId, uint itemId) => GetApplicable(characterContentId).Any(rule =>
        rule.Enabled &&
        rule.Origin == SquireCleanupRuleOrigin.User &&
        rule.Effect.Decision == SquireCleanupDecision.Protect &&
        IsExactItemCondition(rule.Condition, itemId, SquireRuleQuality.Any));

    public void SetItemProtection(ulong? characterContentId, uint itemId, bool protectedFromCleanup, string? note = null)
    {
        if (characterContentId is null or 0 || itemId == 0)
            return;
        var existing = GetApplicable(characterContentId).FirstOrDefault(rule =>
            rule.Origin == SquireCleanupRuleOrigin.User &&
            rule.Scope == SquireCleanupRuleScope.Character &&
            rule.Effect.Decision == SquireCleanupDecision.Protect &&
            IsExactItemCondition(rule.Condition, itemId, SquireRuleQuality.Any));
        if (!protectedFromCleanup)
        {
            if (existing is not null)
                Remove(existing.Id);
            return;
        }

        var value = existing ?? new SquireCleanupRule(
            $"user.item-protection.{characterContentId}.{itemId}",
            $"Protect item {itemId}",
            SquireCleanupRuleOrigin.User,
            SquireCleanupRuleScope.Character,
            characterContentId,
            true,
            1_000,
            new(ItemIds: new HashSet<uint> { itemId }),
            new(Decision: SquireCleanupDecision.Protect));
        value = value with { Enabled = true, Note = note?.Trim() ?? value.Note };
        if (existing is null)
            Add(value);
        else
            Replace(value);
    }

    public int MinimumCopiesToKeep(ulong? characterContentId, uint itemId, bool isHighQuality) =>
        GetApplicable(characterContentId)
            .Where(rule => rule.Enabled && rule.Effect.MinimumCopies > 0 &&
                           IsExactItemCondition(
                               rule.Condition,
                               itemId,
                               isHighQuality ? SquireRuleQuality.HighQuality : SquireRuleQuality.NormalQuality))
            .Select(rule => rule.Effect.MinimumCopies)
            .DefaultIfEmpty(0)
            .Max();

    public void SetRetention(ulong? characterContentId, uint itemId, bool isHighQuality, int minimumCopies, string? note = null)
    {
        if (characterContentId is null or 0 || itemId == 0)
            return;
        var quality = isHighQuality ? SquireRuleQuality.HighQuality : SquireRuleQuality.NormalQuality;
        var existing = GetApplicable(characterContentId).FirstOrDefault(rule =>
            rule.Origin == SquireCleanupRuleOrigin.User &&
            rule.Scope == SquireCleanupRuleScope.Character &&
            rule.Effect.MinimumCopies > 0 &&
            IsExactItemCondition(rule.Condition, itemId, quality));
        if (minimumCopies <= 0)
        {
            if (existing is not null)
                Remove(existing.Id);
            return;
        }

        var value = existing ?? new SquireCleanupRule(
            $"user.retention.{characterContentId}.{itemId}.{(isHighQuality ? "hq" : "nq")}",
            $"Retain copies of item {itemId}",
            SquireCleanupRuleOrigin.User,
            SquireCleanupRuleScope.Character,
            characterContentId,
            true,
            1_000,
            new(ItemIds: new HashSet<uint> { itemId }, Quality: quality),
            new(MinimumCopies: 1));
        value = value with
        {
            Enabled = true,
            Effect = value.Effect with { MinimumCopies = Math.Clamp(minimumCopies, 1, 99) },
            Note = note?.Trim() ?? value.Note,
        };
        if (existing is null)
            Add(value);
        else
            Replace(value);
    }

    private SquireCleanupRule ApplyOverride(SquireCleanupRule rule)
    {
        if (config.Squire.BuiltInRuleOverrides?.TryGetValue(rule.Id, out var value) != true || value is null)
            return rule;
        return rule with
        {
            Enabled = value.Enabled ?? rule.Enabled,
            Priority = value.Priority ?? rule.Priority,
        };
    }

    private static bool IsExactItemCondition(
        SquireCleanupRuleCondition condition,
        uint itemId,
        SquireRuleQuality quality) =>
        condition.ItemIds is { Count: 1 } && condition.ItemIds.Contains(itemId) &&
        condition.Quality == quality &&
        condition.Rarities is null &&
        condition.UseStatuses is null &&
        condition.IsEquipment is null &&
        condition.IsPlayerSigned is null &&
        condition.IsArmoireEligible is null &&
        condition.HasMateria is null &&
        condition.HasFutureLevelingUse is null &&
        condition.MinimumEquipLevel is null &&
        condition.MaximumEquipLevel is null &&
        condition.SupportedDispositions is null;

    internal static SquireCleanupRule ToDomain(SquireCleanupRuleConfiguration value) => new(
        value.Id ?? string.Empty,
        value.Name ?? string.Empty,
        SquireCleanupRuleOrigin.User,
        value.Scope,
        value.CharacterContentId,
        value.Enabled,
        value.Priority,
        new SquireCleanupRuleCondition(
            ToSet(value.Condition?.ItemIds),
            value.Condition?.Quality ?? SquireRuleQuality.Any,
            ToSet(value.Condition?.Rarities),
            ToSet(value.Condition?.UseStatuses),
            value.Condition?.IsEquipment,
            value.Condition?.IsPlayerSigned,
            value.Condition?.IsArmoireEligible,
            value.Condition?.HasMateria,
            value.Condition?.HasFutureLevelingUse,
            value.Condition?.MinimumEquipLevel,
            value.Condition?.MaximumEquipLevel,
            ToSet(value.Condition?.SupportedDispositions)),
        new SquireCleanupRuleEffect(
            value.Effect?.Decision ?? SquireCleanupDecision.NoChange,
            value.Effect?.PreferredDisposition,
            value.Effect?.MinimumCopies ?? 0,
            value.Effect?.Authorizations ?? SquireCleanupAuthorization.None),
        value.Note ?? string.Empty);

    internal static SquireCleanupRuleConfiguration ToConfiguration(SquireCleanupRule value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        Scope = value.Scope,
        CharacterContentId = value.CharacterContentId,
        Enabled = value.Enabled,
        Priority = value.Priority,
        Condition = new SquireCleanupRuleConditionConfiguration
        {
            ItemIds = ToList(value.Condition.ItemIds),
            Quality = value.Condition.Quality,
            Rarities = ToList(value.Condition.Rarities),
            UseStatuses = ToList(value.Condition.UseStatuses),
            IsEquipment = value.Condition.IsEquipment,
            IsPlayerSigned = value.Condition.IsPlayerSigned,
            IsArmoireEligible = value.Condition.IsArmoireEligible,
            HasMateria = value.Condition.HasMateria,
            HasFutureLevelingUse = value.Condition.HasFutureLevelingUse,
            MinimumEquipLevel = value.Condition.MinimumEquipLevel,
            MaximumEquipLevel = value.Condition.MaximumEquipLevel,
            SupportedDispositions = ToList(value.Condition.SupportedDispositions),
        },
        Effect = new SquireCleanupRuleEffectConfiguration
        {
            Decision = value.Effect.Decision,
            PreferredDisposition = value.Effect.PreferredDisposition,
            MinimumCopies = value.Effect.MinimumCopies,
            Authorizations = value.Effect.Authorizations,
        },
        Note = value.Note,
    };

    private static IReadOnlySet<T>? ToSet<T>(List<T>? values) where T : notnull =>
        values is null ? null : new HashSet<T>(values);

    private static List<T>? ToList<T>(IReadOnlySet<T>? values) => values?.ToList();
}

public static class SquireCleanupRuleMigration
{
    public static bool Migrate(ISquireConfigurationStore config)
    {
        config.Squire ??= new SquireConfiguration();
        var squire = config.Squire;
        squire.CleanupRules ??= [];
        squire.BuiltInRuleOverrides ??= new();
#pragma warning disable CS0618
        squire.RulesByCharacter ??= new();
        squire.HighRarityCleanupItemIdsByCharacter ??= new();
        var hadLegacyRules = squire.RulesByCharacter.Count > 0 || squire.HighRarityCleanupItemIdsByCharacter.Count > 0;
#pragma warning restore CS0618
        if (squire.RuleSchemaVersion >= 2 && !hadLegacyRules)
            return false;

        SetEnabled(squire, "builtin.protect-high-rarity", squire.ProtectBlueAndPurpleGear);
        SetEnabled(squire, "builtin.protect-player-signed", squire.ProtectPlayerSignedGear);
        SetEnabled(squire, "builtin.protect-future-leveling", squire.ProtectFutureLevelingGearOptIn);
        SetEnabled(squire, "builtin.protect-armoire", squire.ProtectArmoireEligible);
        SetEnabled(squire, "builtin.protect-materia-risk", squire.ProtectMateria && !squire.AllowRiskyMateriaRetrieval);

#pragma warning disable CS0618
        foreach (var pair in squire.RulesByCharacter)
        {
            _ = ulong.TryParse(pair.Key, out var contentId);
            foreach (var rule in pair.Value ?? [])
            {
                var id = $"user.{rule.Id:N}";
                if (squire.CleanupRules.Any(value => string.Equals(value.Id, id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                squire.CleanupRules.Add(new SquireCleanupRuleConfiguration
                {
                    Id = id,
                    Name = rule.Kind == SquireRuleKind.ProtectItem
                        ? $"Protect item {rule.ItemId}"
                        : $"Retain {rule.MinimumCopies} {(rule.Quality == SquireRuleQuality.HighQuality ? "HQ" : "NQ")} copies of item {rule.ItemId}",
                    Scope = SquireCleanupRuleScope.Character,
                    CharacterContentId = contentId,
                    Enabled = rule.Enabled,
                    Priority = 1_000,
                    Condition = new SquireCleanupRuleConditionConfiguration
                    {
                        ItemIds = [rule.ItemId],
                        Quality = rule.Quality,
                    },
                    Effect = new SquireCleanupRuleEffectConfiguration
                    {
                        Decision = rule.Kind == SquireRuleKind.ProtectItem
                            ? SquireCleanupDecision.Protect
                            : SquireCleanupDecision.NoChange,
                        MinimumCopies = rule.Kind == SquireRuleKind.RetainCopies ? rule.MinimumCopies : 0,
                    },
                    Note = rule.Note ?? string.Empty,
                });
            }
        }
        foreach (var pair in squire.HighRarityCleanupItemIdsByCharacter)
        {
            _ = ulong.TryParse(pair.Key, out var contentId);
            foreach (var itemId in (pair.Value ?? []).Where(itemId => itemId != 0).Distinct())
            {
                var id = $"user.migrated-high-rarity-{contentId}-{itemId}";
                if (squire.CleanupRules.Any(value => value.Id == id))
                    continue;
                squire.CleanupRules.Add(new SquireCleanupRuleConfiguration
                {
                    Id = id,
                    Name = $"Allow high-rarity cleanup for item {itemId}",
                    Scope = SquireCleanupRuleScope.Character,
                    CharacterContentId = contentId,
                    Priority = 1_000,
                    Condition = new() { ItemIds = [itemId] },
                    Effect = new()
                    {
                        Decision = SquireCleanupDecision.AllowCleanup,
                        Authorizations = SquireCleanupAuthorization.HighRarity,
                    },
                    Note = "Migrated high-rarity cleanup authorization",
                });
            }
        }
        squire.RulesByCharacter.Clear();
        squire.HighRarityCleanupItemIdsByCharacter.Clear();
#pragma warning restore CS0618
        squire.RuleSchemaVersion = 2;
        return true;
    }

    private static void SetEnabled(SquireConfiguration config, string id, bool enabled)
    {
        if (!config.BuiltInRuleOverrides.TryGetValue(id, out var value) || value is null)
            config.BuiltInRuleOverrides[id] = value = new();
        value.Enabled = enabled;
    }
}
