using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public enum SquireCleanupRuleOrigin
{
    BuiltIn,
    User,
}

public enum SquireCleanupRuleScope
{
    Global,
    Character,
}

public enum SquireCleanupDecision
{
    NoChange,
    Protect,
    AllowCleanup,
}

[Flags]
public enum SquireCleanupAuthorization
{
    None = 0,
    HighRarity = 1 << 0,
    MateriaRetrievalRisk = 1 << 1,
    PlayerSignature = 1 << 2,
    ArmoireEligible = 1 << 3,
    FutureLevelingUse = 1 << 4,
}

public sealed record SquireCleanupRuleCondition(
    IReadOnlySet<uint>? ItemIds = null,
    SquireRuleQuality Quality = SquireRuleQuality.Any,
    IReadOnlySet<EquipmentRarity>? Rarities = null,
    IReadOnlySet<EquipmentUseStatus>? UseStatuses = null,
    bool? IsEquipment = null,
    bool? IsPlayerSigned = null,
    bool? IsArmoireEligible = null,
    bool? HasMateria = null,
    bool? HasFutureLevelingUse = null,
    int? MinimumEquipLevel = null,
    int? MaximumEquipLevel = null,
    IReadOnlySet<SquireDisposition>? SupportedDispositions = null)
{
    public bool Matches(SquireCleanupRuleContext context)
    {
        if (ItemIds is { Count: > 0 } && !ItemIds.Contains(context.ItemId))
            return false;
        if (Quality != SquireRuleQuality.Any &&
            Quality != (context.IsHighQuality ? SquireRuleQuality.HighQuality : SquireRuleQuality.NormalQuality))
            return false;
        if (Rarities is { Count: > 0 } && !Rarities.Contains(context.Rarity))
            return false;
        if (UseStatuses is { Count: > 0 } &&
            (context.UseStatus is null || !UseStatuses.Contains(context.UseStatus.Value)))
            return false;
        if (IsEquipment is not null && IsEquipment != context.IsEquipment)
            return false;
        if (IsPlayerSigned is not null && IsPlayerSigned != context.IsPlayerSigned)
            return false;
        if (IsArmoireEligible is not null && IsArmoireEligible != context.IsArmoireEligible)
            return false;
        if (HasMateria is not null && HasMateria != context.HasMateria)
            return false;
        if (HasFutureLevelingUse is not null && HasFutureLevelingUse != context.HasFutureLevelingUse)
            return false;
        if (MinimumEquipLevel is not null && context.EquipLevel < MinimumEquipLevel)
            return false;
        if (MaximumEquipLevel is not null && context.EquipLevel > MaximumEquipLevel)
            return false;
        if (SupportedDispositions is { Count: > 0 } && !SupportedDispositions.Overlaps(context.SupportedDispositions))
            return false;
        return true;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ItemIds is { Count: 0 })
            errors.Add("Item IDs are present but empty.");
        else if (ItemIds?.Contains(0) == true)
            errors.Add("Item ID zero is not valid.");
        if (!Enum.IsDefined(Quality))
            errors.Add($"Quality scope {(int)Quality} is unknown.");
        if (Rarities is { Count: 0 })
            errors.Add("Rarities are present but empty.");
        else if (Rarities?.Any(rarity => !Enum.IsDefined(rarity)) == true)
            errors.Add("Rarities contain an unknown value.");
        if (UseStatuses is { Count: 0 })
            errors.Add("Equipment-use statuses are present but empty.");
        else if (UseStatuses?.Any(status => !Enum.IsDefined(status)) == true)
            errors.Add("Equipment-use statuses contain an unknown value.");
        if (SupportedDispositions is { Count: 0 })
            errors.Add("Supported dispositions are present but empty.");
        else if (SupportedDispositions?.Any(disposition =>
                     !Enum.IsDefined(disposition) || disposition is SquireDisposition.Keep or SquireDisposition.Unsupported) == true)
            errors.Add("Supported dispositions contain a non-cleanup route.");
        if (MinimumEquipLevel is < 0)
            errors.Add("Minimum equip level cannot be negative.");
        if (MaximumEquipLevel is < 0)
            errors.Add("Maximum equip level cannot be negative.");
        if (MinimumEquipLevel is not null && MaximumEquipLevel is not null && MinimumEquipLevel > MaximumEquipLevel)
            errors.Add("Minimum equip level cannot exceed maximum equip level.");
        return errors;
    }
}

public sealed record SquireCleanupRuleEffect(
    SquireCleanupDecision Decision = SquireCleanupDecision.NoChange,
    SquireDisposition? PreferredDisposition = null,
    int MinimumCopies = 0,
    SquireCleanupAuthorization Authorizations = SquireCleanupAuthorization.None)
{
    public bool IsEmpty => Decision == SquireCleanupDecision.NoChange &&
                           PreferredDisposition is null &&
                           MinimumCopies == 0 &&
                           Authorizations == SquireCleanupAuthorization.None;
}

public sealed record SquireCleanupRule(
    string Id,
    string Name,
    SquireCleanupRuleOrigin Origin,
    SquireCleanupRuleScope Scope,
    ulong? CharacterContentId,
    bool Enabled,
    int Priority,
    SquireCleanupRuleCondition Condition,
    SquireCleanupRuleEffect Effect,
    string Note = "")
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id))
            errors.Add("Rule ID is required.");
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add($"Rule {Id} needs a name.");
        if (!Enum.IsDefined(Origin))
            errors.Add($"Rule {Id} has unknown origin {(int)Origin}.");
        if (!Enum.IsDefined(Scope))
            errors.Add($"Rule {Id} has unknown scope {(int)Scope}.");
        if (Scope == SquireCleanupRuleScope.Character && CharacterContentId is null or 0)
            errors.Add($"Character rule {Id} has no character content ID.");
        if (Scope == SquireCleanupRuleScope.Global && CharacterContentId is not null)
            errors.Add($"Global rule {Id} cannot name a character content ID.");
        if (Priority is < 0 or > 10_000)
            errors.Add($"Rule {Id} priority must be between 0 and 10000.");
        errors.AddRange(Condition.Validate().Select(error => $"Rule {Id}: {error}"));
        if (!Enum.IsDefined(Effect.Decision))
            errors.Add($"Rule {Id} has unknown decision {(int)Effect.Decision}.");
        if (Effect.PreferredDisposition is not null && !Enum.IsDefined(Effect.PreferredDisposition.Value))
            errors.Add($"Rule {Id} has an unknown preferred cleanup route.");
        else if (Effect.PreferredDisposition is SquireDisposition.Keep or SquireDisposition.Unsupported)
            errors.Add($"Rule {Id} cannot prefer {Effect.PreferredDisposition} as a cleanup route.");
        if (Effect.MinimumCopies < 0)
            errors.Add($"Rule {Id} cannot retain a negative number of copies.");
        const SquireCleanupAuthorization knownAuthorizations =
            SquireCleanupAuthorization.HighRarity |
            SquireCleanupAuthorization.MateriaRetrievalRisk |
            SquireCleanupAuthorization.PlayerSignature |
            SquireCleanupAuthorization.ArmoireEligible |
            SquireCleanupAuthorization.FutureLevelingUse;
        if ((Effect.Authorizations & ~knownAuthorizations) != SquireCleanupAuthorization.None)
            errors.Add($"Rule {Id} contains an unknown authorization flag.");
        if (Effect.IsEmpty)
            errors.Add($"Rule {Id} has no effect.");
        return errors;
    }

    public bool Matches(SquireCleanupRuleContext context) => Enabled &&
        (Scope == SquireCleanupRuleScope.Global || CharacterContentId == context.CharacterContentId) &&
        Condition.Matches(context);
}

public sealed record SquireCleanupRuleContext(
    ulong CharacterContentId,
    uint ItemId,
    bool IsHighQuality,
    EquipmentRarity Rarity,
    EquipmentUseStatus? UseStatus,
    bool IsEquipment,
    bool IsPlayerSigned,
    bool? IsArmoireEligible,
    bool HasMateria,
    bool HasFutureLevelingUse,
    int EquipLevel,
    IReadOnlySet<SquireDisposition> SupportedDispositions);

public sealed record SquireCleanupRuleTrace(
    string RuleId,
    string RuleName,
    int Priority,
    SquireCleanupRuleEffect Effect,
    bool WonDecision,
    bool WonDisposition);

public sealed record SquireCleanupRuleEvaluation(
    SquireCleanupDecision Decision,
    SquireDisposition? PreferredDisposition,
    int MinimumCopies,
    SquireCleanupAuthorization Authorizations,
    IReadOnlyList<SquireCleanupRuleTrace> MatchedRules,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class SquireCleanupRuleEngine
{
    public SquireCleanupRuleEvaluation Evaluate(
        SquireCleanupRuleContext context,
        IEnumerable<SquireCleanupRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var enabled = rules.Where(rule => rule.Enabled).ToArray();
        var errors = enabled.SelectMany(rule => rule.Validate()).ToList();
        var duplicateIds = enabled.GroupBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"Enabled rule ID '{group.Key}' is duplicated.");
        errors.AddRange(duplicateIds);
        if (errors.Count > 0)
            return Invalid(errors);

        var matched = enabled.Where(rule => rule.Matches(context))
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Id, StringComparer.Ordinal)
            .ToArray();

        var decisionRules = matched.Where(rule => rule.Effect.Decision != SquireCleanupDecision.NoChange).ToArray();
        var decisionPriority = decisionRules.Select(rule => (int?)rule.Priority).FirstOrDefault();
        var decisionAtPriority = decisionPriority is null
            ? []
            : decisionRules.Where(rule => rule.Priority == decisionPriority).ToArray();
        var decision = decisionAtPriority.Any(rule => rule.Effect.Decision == SquireCleanupDecision.Protect)
            ? SquireCleanupDecision.Protect
            : decisionAtPriority.FirstOrDefault()?.Effect.Decision ?? SquireCleanupDecision.NoChange;

        var dispositionRules = matched.Where(rule => rule.Effect.PreferredDisposition is not null).ToArray();
        var dispositionPriority = dispositionRules.Select(rule => (int?)rule.Priority).FirstOrDefault();
        var dispositionAtPriority = dispositionPriority is null
            ? []
            : dispositionRules.Where(rule => rule.Priority == dispositionPriority).ToArray();
        var requestedDispositions = dispositionAtPriority.Select(rule => rule.Effect.PreferredDisposition!.Value).Distinct().ToArray();
        if (requestedDispositions.Length > 1)
            errors.Add($"Rules at priority {dispositionPriority} request conflicting cleanup routes: {string.Join(", ", requestedDispositions)}.");
        SquireDisposition? disposition = requestedDispositions.Length == 1 ? requestedDispositions[0] : null;
        if (disposition is not null && !context.SupportedDispositions.Contains(disposition.Value))
            errors.Add($"The winning rule requests unsupported cleanup route {disposition} for item {context.ItemId}.");

        var minimumCopies = matched.Select(rule => rule.Effect.MinimumCopies).DefaultIfEmpty(0).Max();
        var authorizations = matched.Aggregate(
            SquireCleanupAuthorization.None,
            (value, rule) => value | rule.Effect.Authorizations);
        var trace = matched.Select(rule => new SquireCleanupRuleTrace(
            rule.Id,
            rule.Name,
            rule.Priority,
            rule.Effect,
            decisionPriority == rule.Priority && rule.Effect.Decision == decision,
            dispositionPriority == rule.Priority && rule.Effect.PreferredDisposition == disposition)).ToArray();

        return new SquireCleanupRuleEvaluation(
            decision,
            errors.Count == 0 ? disposition : null,
            minimumCopies,
            authorizations,
            trace,
            errors);
    }

    private static SquireCleanupRuleEvaluation Invalid(IReadOnlyList<string> errors) => new(
        SquireCleanupDecision.NoChange,
        null,
        0,
        SquireCleanupAuthorization.None,
        [],
        errors);
}

public static class SquireBuiltInCleanupRules
{
    public static IReadOnlyList<SquireCleanupRule> CreateDefaults() =>
    [
        Protect("builtin.protect-high-rarity", "Protect blue and purple gear", 600,
            new(Rarities: new HashSet<EquipmentRarity> { EquipmentRarity.Rare, EquipmentRarity.Relic }, IsEquipment: true)),
        Protect("builtin.protect-player-signed", "Protect player-signed gear", 600,
            new(IsEquipment: true, IsPlayerSigned: true), enabled: false),
        Protect("builtin.protect-future-leveling", "Protect future-leveling gear", 600,
            new(IsEquipment: true, HasFutureLevelingUse: true), enabled: false),
        Protect("builtin.protect-armoire", "Protect Armoire-eligible gear", 600,
            new(IsEquipment: true, IsArmoireEligible: true)),
        Protect("builtin.protect-materia-risk", "Protect gear with materia", 600,
            new(IsEquipment: true, HasMateria: true)),
        Protect("builtin.protect-cosmetic", "Protect likely cosmetic gear", 600,
            new(UseStatuses: new HashSet<EquipmentUseStatus> { EquipmentUseStatus.LikelyCosmetic }, IsEquipment: true)),
        Protect("builtin.protect-special-purpose", "Protect special-purpose gear", 600,
            new(UseStatuses: new HashSet<EquipmentUseStatus> { EquipmentUseStatus.SpecialPurpose }, IsEquipment: true)),
        Route("builtin.route-expert-delivery", "Prefer Expert Delivery", 400, SquireDisposition.ExpertDelivery),
        Route("builtin.route-desynthesis", "Prefer desynthesis", 300, SquireDisposition.Desynthesize),
        Route("builtin.route-vendor", "Prefer vendor sale", 200, SquireDisposition.VendorSell),
        Route("builtin.route-discard", "Prefer discard", 100, SquireDisposition.Discard),
    ];

    private static SquireCleanupRule Protect(
        string id,
        string name,
        int priority,
        SquireCleanupRuleCondition condition,
        bool enabled = true) => new(
        id,
        name,
        SquireCleanupRuleOrigin.BuiltIn,
        SquireCleanupRuleScope.Global,
        null,
        enabled,
        priority,
        condition,
        new(Decision: SquireCleanupDecision.Protect));

    private static SquireCleanupRule Route(
        string id,
        string name,
        int priority,
        SquireDisposition disposition) => new(
        id,
        name,
        SquireCleanupRuleOrigin.BuiltIn,
        SquireCleanupRuleScope.Global,
        null,
        true,
        priority,
        new(SupportedDispositions: new HashSet<SquireDisposition> { disposition }),
        new(PreferredDisposition: disposition));
}

public static class SquireLegacyCleanupRuleAdapter
{
    public static IReadOnlyList<SquireCleanupRule> Create(SquireProtectionPolicy policy)
    {
        var builtIns = SquireBuiltInCleanupRules.CreateDefaults().Select(rule => rule.Id switch
        {
            "builtin.protect-high-rarity" => rule with { Enabled = policy.ProtectBlueAndPurpleGear },
            "builtin.protect-player-signed" => rule with { Enabled = policy.ProtectSignedGear },
            "builtin.protect-future-leveling" => rule with { Enabled = policy.ProtectFutureLevelingGear },
            "builtin.protect-materia-risk" => rule with { Enabled = !policy.AllowRiskyMateriaRetrieval },
            _ => rule,
        });
        var itemRules = (policy.Rules ?? []).Select(rule => new SquireCleanupRule(
            $"legacy.{rule.Id:N}",
            string.IsNullOrWhiteSpace(rule.Note) ? $"Legacy {rule.Kind} rule" : rule.Note,
            SquireCleanupRuleOrigin.User,
            SquireCleanupRuleScope.Global,
            null,
            rule.Enabled,
            1_000,
            new(
                ItemIds: new HashSet<uint> { rule.ItemId },
                Quality: rule.Quality),
            rule.Kind == SquireRuleKind.ProtectItem
                ? new(Decision: SquireCleanupDecision.Protect)
                : new(MinimumCopies: rule.MinimumCopies),
            rule.Note));
        return builtIns.Concat(itemRules).ToArray();
    }
}
