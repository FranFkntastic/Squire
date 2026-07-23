using System;
using System.Collections.Generic;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

/// <summary>
/// Narrow persistence boundary owned by Squire. The embedded MMF host implements this today;
/// the standalone plugin can provide its own store without inheriting unrelated MMF settings.
/// </summary>
public interface ISquireConfigurationStore
{
    SquireConfiguration Squire { get; set; }
    string? OutfitterRouteExecutionStateJson { get; set; }
    bool EnableMarketAcquisitionDryRunTools { get; set; }
    global::MarketMafioso.PersistedMarketAcquisitionRequestDocument? ActiveMarketAcquisitionRequestDocument { get; set; }
    global::MarketMafioso.PersistedMarketAcquisitionClaim? ActiveMarketAcquisitionClaim { get; set; }
    void Save();
}

[Serializable]
public sealed class SquireConfiguration
{
    public string SelectedWorkspace { get; set; } = "Cleanup";
    public string OutfitterAdvisorContext { get; set; } = "OrdinaryResourceBenchmark";
    public int OutfitterAdvisorContextDefaultVersion { get; set; }
    public bool ShowProtected { get; set; }
    public bool ShowNonEquipment { get; set; }
    public string Search { get; set; } = string.Empty;
    public int RuleSchemaVersion { get; set; } = 2;
    public List<SquireCleanupRuleConfiguration> CleanupRules { get; set; } = [];
    public Dictionary<string, SquireBuiltInRuleOverrideConfiguration> BuiltInRuleOverrides { get; set; } = new();
    [Obsolete("Migrated to CleanupRules by SquireCleanupRuleMigration.")]
    public Dictionary<string, List<SquireRuleConfiguration>> RulesByCharacter { get; set; } = new();
    [Obsolete("Migrated to RulesByCharacter by SquireRuleMigration.")]
    public Dictionary<string, List<uint>> ExcludedItemIdsByCharacter { get; set; } = new();
    [Obsolete("Migrated to RulesByCharacter by SquireRuleMigration.")]
    public Dictionary<string, List<SquireDuplicateRetentionConfiguration>> DuplicateRetentionByCharacter { get; set; } = new();
    [Obsolete("Per-item high-rarity cleanup authorization has been replaced by the blue/purple protection toggle and character rules.")]
    public Dictionary<string, List<uint>> HighRarityCleanupItemIdsByCharacter { get; set; } = new();
    public bool ProtectBlueAndPurpleGear { get; set; } = true;
    public bool ProtectMateria { get; set; } = true;
    public bool AllowRiskyMateriaRetrieval { get; set; }
    public bool ProtectPlayerSignedGear { get; set; }
    public bool ProtectArmoireEligible { get; set; } = true;
    public bool ProtectFutureLevelingGearOptIn { get; set; }
    public int AuditRetentionDays { get; set; } = 30;
    public bool RecoverFromKnockout { get; set; } = true;
    public bool WaitForCombatToEnd { get; set; } = true;
    public int CombatRecoveryTimeoutSeconds { get; set; } = 90;
    public bool LeaveDutyToExecute { get; set; }
    public bool PauseGatherBuddyReborn { get; set; } = true;
    public bool PauseQuestionable { get; set; } = true;
    public bool PauseArtisan { get; set; } = true;
    public bool CloseSafeUserMenus { get; set; } = true;
}

[Serializable]
public sealed class SquireRuleConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SquireRuleKind Kind { get; set; }
    public uint ItemId { get; set; }
    public SquireRuleQuality Quality { get; set; } = SquireRuleQuality.Any;
    public int MinimumCopies { get; set; }
    public bool Enabled { get; set; } = true;
    public string Note { get; set; } = string.Empty;
}

[Serializable]
public sealed class SquireCleanupRuleConfiguration
{
    public string Id { get; set; } = $"user.{Guid.NewGuid():N}";
    public string Name { get; set; } = "New cleanup rule";
    public SquireCleanupRuleScope Scope { get; set; } = SquireCleanupRuleScope.Global;
    public ulong? CharacterContentId { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 700;
    public SquireCleanupRuleConditionConfiguration Condition { get; set; } = new();
    public SquireCleanupRuleEffectConfiguration Effect { get; set; } = new();
    public string Note { get; set; } = string.Empty;
}

[Serializable]
public sealed class SquireCleanupRuleConditionConfiguration
{
    public List<uint>? ItemIds { get; set; }
    public SquireRuleQuality Quality { get; set; } = SquireRuleQuality.Any;
    public List<EquipmentRarity>? Rarities { get; set; }
    public List<EquipmentUseStatus>? UseStatuses { get; set; }
    public bool? IsEquipment { get; set; }
    public bool? IsPlayerSigned { get; set; }
    public bool? IsArmoireEligible { get; set; }
    public bool? HasMateria { get; set; }
    public bool? HasFutureLevelingUse { get; set; }
    public int? MinimumEquipLevel { get; set; }
    public int? MaximumEquipLevel { get; set; }
    public List<SquireDisposition>? SupportedDispositions { get; set; }
}

[Serializable]
public sealed class SquireCleanupRuleEffectConfiguration
{
    public SquireCleanupDecision Decision { get; set; }
    public SquireDisposition? PreferredDisposition { get; set; }
    public int MinimumCopies { get; set; }
    public SquireCleanupAuthorization Authorizations { get; set; }
}

[Serializable]
public sealed class SquireBuiltInRuleOverrideConfiguration
{
    public bool? Enabled { get; set; }
    public int? Priority { get; set; }
}

[Serializable]
public sealed class SquireDuplicateRetentionConfiguration
{
    public uint ItemId { get; set; }
    public bool IsHighQuality { get; set; }
    public int MinimumCopies { get; set; }
}
