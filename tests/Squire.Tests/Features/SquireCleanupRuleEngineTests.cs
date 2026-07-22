using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireCleanupRuleEngineTests
{
    private readonly SquireCleanupRuleEngine engine = new();

    [Fact]
    public void Condition_AndsFieldsAndOrsValuesWithinAField()
    {
        var condition = new SquireCleanupRuleCondition(
            ItemIds: new HashSet<uint> { 10, 20 },
            Quality: SquireRuleQuality.HighQuality,
            Rarities: new HashSet<EquipmentRarity> { EquipmentRarity.Uncommon, EquipmentRarity.Rare },
            IsEquipment: true,
            MinimumEquipLevel: 40,
            MaximumEquipLevel: 60);

        Assert.True(condition.Matches(Context(itemId: 20, isHighQuality: true, rarity: EquipmentRarity.Rare, equipLevel: 50)));
        Assert.False(condition.Matches(Context(itemId: 20, isHighQuality: false, rarity: EquipmentRarity.Rare, equipLevel: 50)));
        Assert.False(condition.Matches(Context(itemId: 30, isHighQuality: true, rarity: EquipmentRarity.Rare, equipLevel: 50)));
    }

    [Fact]
    public void HigherPriorityCleanupAuthorization_OverridesBuiltInProtection()
    {
        var rules = new[]
        {
            Rule("protect-rare", 600, decision: SquireCleanupDecision.Protect,
                condition: new(Rarities: new HashSet<EquipmentRarity> { EquipmentRarity.Rare })),
            Rule("allow-item", 700, decision: SquireCleanupDecision.AllowCleanup,
                condition: new(ItemIds: new HashSet<uint> { 20 }),
                authorizations: SquireCleanupAuthorization.HighRarity),
        };

        var result = engine.Evaluate(Context(itemId: 20, rarity: EquipmentRarity.Rare), rules);

        Assert.True(result.IsValid);
        Assert.Equal(SquireCleanupDecision.AllowCleanup, result.Decision);
        Assert.True(result.Authorizations.HasFlag(SquireCleanupAuthorization.HighRarity));
        Assert.Equal("allow-item", Assert.Single(result.MatchedRules, rule => rule.WonDecision).RuleId);
    }

    [Fact]
    public void EqualPriorityProtection_WinsConservatively()
    {
        var result = engine.Evaluate(Context(),
        [
            Rule("allow", 700, decision: SquireCleanupDecision.AllowCleanup),
            Rule("protect", 700, decision: SquireCleanupDecision.Protect),
        ]);

        Assert.True(result.IsValid);
        Assert.Equal(SquireCleanupDecision.Protect, result.Decision);
        Assert.Equal("protect", Assert.Single(result.MatchedRules, rule => rule.WonDecision).RuleId);
    }

    [Fact]
    public void ConflictingRoutesAtOnePriority_FailExplicitly()
    {
        var result = engine.Evaluate(Context(supported: new HashSet<SquireDisposition> { SquireDisposition.Desynthesize, SquireDisposition.ExpertDelivery }),
        [
            Rule("desynth", 500, disposition: SquireDisposition.Desynthesize),
            Rule("delivery", 500, disposition: SquireDisposition.ExpertDelivery),
        ]);

        Assert.False(result.IsValid);
        Assert.Null(result.PreferredDisposition);
        Assert.Contains(result.Errors, error => error.Contains("conflicting cleanup routes", StringComparison.Ordinal));
    }

    [Fact]
    public void RetentionUsesMaximumAndAuthorizationsAccumulate()
    {
        var result = engine.Evaluate(Context(),
        [
            Rule("keep-one", 100, minimumCopies: 1, authorizations: SquireCleanupAuthorization.PlayerSignature),
            Rule("keep-three", 50, minimumCopies: 3, authorizations: SquireCleanupAuthorization.MateriaRetrievalRisk),
        ]);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.MinimumCopies);
        Assert.True(result.Authorizations.HasFlag(SquireCleanupAuthorization.PlayerSignature));
        Assert.True(result.Authorizations.HasFlag(SquireCleanupAuthorization.MateriaRetrievalRisk));
    }

    [Fact]
    public void BuiltInDefaults_ExpressProtectionAndRouteOrdering()
    {
        var rules = SquireBuiltInCleanupRules.CreateDefaults();
        var result = engine.Evaluate(
            Context(
                rarity: EquipmentRarity.Rare,
                supported: new HashSet<SquireDisposition> { SquireDisposition.Desynthesize, SquireDisposition.ExpertDelivery }),
            rules);

        Assert.True(result.IsValid);
        Assert.Equal(SquireCleanupDecision.Protect, result.Decision);
        Assert.Equal(SquireDisposition.ExpertDelivery, result.PreferredDisposition);
        Assert.False(Assert.Single(rules, rule => rule.Id == "builtin.protect-player-signed").Enabled);
        Assert.False(Assert.Single(rules, rule => rule.Id == "builtin.protect-future-leveling").Enabled);
    }

    [Fact]
    public void UnsupportedWinningRoute_FailsInsteadOfFallingBack()
    {
        var result = engine.Evaluate(Context(supported: new HashSet<SquireDisposition> { SquireDisposition.Desynthesize }),
        [
            Rule("force-delivery", 900, disposition: SquireDisposition.ExpertDelivery),
        ]);

        Assert.False(result.IsValid);
        Assert.Null(result.PreferredDisposition);
        Assert.Contains(result.Errors, error => error.Contains("unsupported cleanup route", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidEnabledRule_BlocksTheWholePolicyEvaluation()
    {
        var invalid = Rule("invalid", 900, decision: SquireCleanupDecision.AllowCleanup) with
        {
            Condition = new(ItemIds: new HashSet<uint>()),
        };

        var result = engine.Evaluate(Context(),
        [
            invalid,
            Rule("otherwise-valid", 100, decision: SquireCleanupDecision.Protect),
        ]);

        Assert.False(result.IsValid);
        Assert.Equal(SquireCleanupDecision.NoChange, result.Decision);
        Assert.Empty(result.MatchedRules);
        Assert.Contains(result.Errors, error => error.Contains("present but empty", StringComparison.Ordinal));
    }

    private static SquireCleanupRule Rule(
        string id,
        int priority,
        SquireCleanupDecision decision = SquireCleanupDecision.NoChange,
        SquireDisposition? disposition = null,
        int minimumCopies = 0,
        SquireCleanupAuthorization authorizations = SquireCleanupAuthorization.None,
        SquireCleanupRuleCondition? condition = null) => new(
        id,
        id,
        SquireCleanupRuleOrigin.User,
        SquireCleanupRuleScope.Global,
        null,
        true,
        priority,
        condition ?? new(),
        new(decision, disposition, minimumCopies, authorizations));

    private static SquireCleanupRuleContext Context(
        uint itemId = 20,
        bool isHighQuality = false,
        EquipmentRarity rarity = EquipmentRarity.Normal,
        int equipLevel = 50,
        IReadOnlySet<SquireDisposition>? supported = null) => new(
        42,
        itemId,
        isHighQuality,
        rarity,
        EquipmentUseStatus.Obsolete,
        true,
        false,
        false,
        false,
        false,
        equipLevel,
        supported ?? new HashSet<SquireDisposition> { SquireDisposition.Desynthesize });
}
