using Newtonsoft.Json;
using MarketMafioso.Squire;
using MarketMafioso.SquireIntegration;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireConfigurationTests
{
    [Fact]
    public void AdvisorContextMigration_MovesExistingInstallsToOrdinaryNodesOnce()
    {
        var config = new Configuration();
        config.Squire.OutfitterAdvisorContext = "LegendaryNodeGeneralYield";

        Assert.True(SquireAdvisorConfigurationMigration.Migrate(config));
        Assert.Equal("OrdinaryResourceBenchmark", config.Squire.OutfitterAdvisorContext);
        Assert.Equal(1, config.Squire.OutfitterAdvisorContextDefaultVersion);
        Assert.False(SquireAdvisorConfigurationMigration.Migrate(config));
    }

    [Fact]
    public void LegacyImplicitSignedGearDefault_DoesNotBecomeAnOptIn()
    {
        var config = JsonConvert.DeserializeObject<SquireConfiguration>(
            "{\"ProtectSignedGear\":true,\"ProtectFutureLevelingGear\":true}")!;

        Assert.False(config.ProtectPlayerSignedGear);
        Assert.False(config.ProtectFutureLevelingGearOptIn);
    }

    [Fact]
    public void LegacyItemPolicies_MigrateIntoCanonicalRulesOnce()
    {
        var config = new SquireConfiguration();
#pragma warning disable CS0618
        config.ExcludedItemIdsByCharacter["42"] = [100, 100];
        config.DuplicateRetentionByCharacter["42"] =
        [
            new() { ItemId = 200, IsHighQuality = true, MinimumCopies = 2 },
            new() { ItemId = 200, IsHighQuality = true, MinimumCopies = 4 },
        ];
#pragma warning restore CS0618

        Assert.True(SquireRuleMigration.Migrate(config));
#pragma warning disable CS0618
        var rules = config.RulesByCharacter["42"];
#pragma warning restore CS0618
        Assert.Single(rules, rule => rule is { Kind: SquireRuleKind.ProtectItem, ItemId: 100, Quality: SquireRuleQuality.Any });
        Assert.Equal(4, Assert.Single(rules, rule => rule is
            { Kind: SquireRuleKind.RetainCopies, ItemId: 200, Quality: SquireRuleQuality.HighQuality }).MinimumCopies);
#pragma warning disable CS0618
        Assert.Empty(config.ExcludedItemIdsByCharacter);
        Assert.Empty(config.DuplicateRetentionByCharacter);
#pragma warning restore CS0618
        Assert.False(SquireRuleMigration.Migrate(config));
        Assert.Equal(2, rules.Count);
    }

    [Fact]
    public void LegacySettingsPageSelection_MigratesToUnifiedRulesPage()
    {
        var config = new Configuration { SettingsSelectedPageId = "squire.duplicates" };

        Assert.True(MarketMafiosoSquireConfigurationMigration.MigrateLegacyRules(config));
        Assert.Equal("squire.rules", config.SettingsSelectedPageId);
    }

    [Fact]
    public void Policy_UsesOnlyEnabledRulesAndKeepsRuleIdentity()
    {
        var active = new SquireRule(Guid.NewGuid(), SquireRuleKind.RetainCopies, 100,
            SquireRuleQuality.NormalQuality, 3, true, "Retainer hand-me-down");
        var disabled = new SquireRule(Guid.Empty, SquireRuleKind.RetainCopies, 0,
            SquireRuleQuality.Any, 0, false, "Broken but disabled reservation");
        var policy = new SquireProtectionPolicy(Rules: [active, disabled]);

        Assert.Equal(3, policy.MinimumCopiesToKeep(100, false));
        Assert.Equal(active.Id, Assert.Single(policy.MatchingRules(100, false)).Id);
        Assert.Empty(policy.ValidationErrors);
    }

    [Fact]
    public void RuleStore_ManagesProtectionAndRetentionThroughOneCollection()
    {
        var config = new Configuration();
        var saves = 0;
        var store = new SquireRuleStore(config, () => saves++);

        store.SetItemProtection(42, 100, true, "Do not dismantle");
        store.SetRetention(42, 200, true, 3, "Retainer hand-me-down");

        var rules = store.Get(42);
        Assert.Equal(2, rules.Count);
        Assert.True(store.CreatePolicy(42).IsItemProtected(100));
        Assert.Equal(3, store.CreatePolicy(42).MinimumCopiesToKeep(200, true));
        var retention = Assert.Single(rules, rule => rule.Kind == SquireRuleKind.RetainCopies);
        store.Update(42, retention.Id, enabled: false);
        Assert.Equal(0, store.CreatePolicy(42).MinimumCopiesToKeep(200, true));
        store.Remove(42, retention.Id);
        Assert.Single(store.Get(42));
        Assert.Equal(4, saves);
    }

    [Fact]
    public void ConfigurableRuleMigration_PreservesLegacyPolicyAndIsIdempotent()
    {
        var legacyRuleId = Guid.NewGuid();
        var config = new Configuration { SettingsSelectedPageId = "squire.policy" };
        config.Squire.RuleSchemaVersion = 1;
        config.Squire.ProtectBlueAndPurpleGear = false;
        config.Squire.ProtectPlayerSignedGear = true;
#pragma warning disable CS0618
        config.Squire.RulesByCharacter["42"] =
        [
            new()
            {
                Id = legacyRuleId,
                Kind = SquireRuleKind.ProtectItem,
                ItemId = 100,
                Quality = SquireRuleQuality.Any,
                Enabled = true,
                Note = "Keep this",
            },
        ];
        config.Squire.HighRarityCleanupItemIdsByCharacter["42"] = [200];
#pragma warning restore CS0618

        Assert.True(MarketMafiosoSquireConfigurationMigration.MigrateCleanupRules(config));

        Assert.Equal(2, config.Squire.RuleSchemaVersion);
        Assert.Equal("squire.rules", config.SettingsSelectedPageId);
        Assert.False(config.Squire.BuiltInRuleOverrides["builtin.protect-high-rarity"].Enabled);
        Assert.True(config.Squire.BuiltInRuleOverrides["builtin.protect-player-signed"].Enabled);
        Assert.Contains(config.Squire.CleanupRules, rule =>
            rule.Id == $"user.{legacyRuleId:N}" &&
            rule.CharacterContentId == 42 &&
            rule.Effect.Decision == SquireCleanupDecision.Protect);
        Assert.Contains(config.Squire.CleanupRules, rule =>
            rule.Condition.ItemIds!.SequenceEqual([200u]) &&
            rule.Effect.Decision == SquireCleanupDecision.AllowCleanup &&
            rule.Effect.Authorizations.HasFlag(SquireCleanupAuthorization.HighRarity));
        Assert.False(SquireCleanupRuleMigration.Migrate(config));
        Assert.Equal(2, config.Squire.CleanupRules.Count);
    }

    [Fact]
    public void ConfigurableRuleStore_CombinesBuiltInsGlobalAndCurrentCharacterRules()
    {
        var config = new Configuration();
        var saves = 0;
        var store = new SquireCleanupRuleStore(config, () => saves++);
        var global = CreateCleanupRule("global", SquireCleanupRuleScope.Global, null, 100);
        var current = CreateCleanupRule("current", SquireCleanupRuleScope.Character, 42, 200);
        var other = CreateCleanupRule("other", SquireCleanupRuleScope.Character, 84, 300);

        store.Add(global);
        store.Add(current);
        store.Add(other);
        store.SetBuiltInOverride("builtin.protect-high-rarity", enabled: false, priority: 750);

        var applicable = store.GetApplicable(42);
        Assert.Contains(applicable, rule => rule.Id == global.Id);
        Assert.Contains(applicable, rule => rule.Id == current.Id);
        Assert.DoesNotContain(applicable, rule => rule.Id == other.Id);
        var rarity = Assert.Single(applicable, rule => rule.Id == "builtin.protect-high-rarity");
        Assert.False(rarity.Enabled);
        Assert.Equal(750, rarity.Priority);
        Assert.Equal(4, saves);
    }

    [Fact]
    public void ConfigurableRuleStore_ProtectsAndRetainsExactCharacterItems()
    {
        var config = new Configuration();
        var store = new SquireCleanupRuleStore(config, () => { });

        store.SetItemProtection(42, 100, true, "Keep this");
        store.SetRetention(42, 200, true, 3, "Retainer hand-me-down");

        Assert.True(store.IsItemProtected(42, 100));
        Assert.False(store.IsItemProtected(84, 100));
        Assert.Equal(3, store.MinimumCopiesToKeep(42, 200, true));
        Assert.Equal(0, store.MinimumCopiesToKeep(42, 200, false));
        store.SetItemProtection(42, 100, false);
        store.SetRetention(42, 200, true, 0);
        Assert.Empty(config.Squire.CleanupRules);
    }

    private static SquireCleanupRule CreateCleanupRule(
        string id,
        SquireCleanupRuleScope scope,
        ulong? contentId,
        uint itemId) => new(
        $"user.{id}",
        id,
        SquireCleanupRuleOrigin.User,
        scope,
        contentId,
        true,
        700,
        new(ItemIds: new HashSet<uint> { itemId }),
        new(Decision: SquireCleanupDecision.Protect));
}
