using Dalamud.Configuration;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;
using Newtonsoft.Json;

namespace Squire;

[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration, ISquireConfigurationStore
{
    public int Version { get; set; } = 1;
    public string PluginInstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public SquireSettings Settings { get; set; } = new();
    public MarketMafioso.Squire.SquireConfiguration FeatureSettings { get; set; } = new();
    public string? OutfitterRouteExecutionStateJson { get; set; }
    public bool EnableMarketAcquisitionDryRunTools { get; set; }
    public MarketMafioso.PersistedMarketAcquisitionRequestDocument? ActiveMarketAcquisitionRequestDocument { get; set; }
    public MarketMafioso.PersistedMarketAcquisitionClaim? ActiveMarketAcquisitionClaim { get; set; }
    public LegacyMmfMigrationReceipt? LegacyMmfMigration { get; set; }
    public bool EnableAgentBridge { get; set; }
    public bool EnableAgentBridgeAudit { get; set; }
    public string AgentBridgeProtectedAccessToken { get; set; } = string.Empty;

    [JsonIgnore]
    internal Action SaveAction { get; set; } = () => { };

    MarketMafioso.Squire.SquireConfiguration ISquireConfigurationStore.Squire
    {
        get => FeatureSettings;
        set => FeatureSettings = value;
    }

    public void Save() => SaveAction();
}

[Serializable]
public sealed class SquireSettings
{
    public string SelectedWorkspace { get; set; } = "Outfitter";
    public string OutfitterAdvisorContext { get; set; } = "OrdinaryResourceBenchmark";
    public int OutfitterAdvisorContextDefaultVersion { get; set; }
    public bool ShowProtected { get; set; }
    public bool ShowNonEquipment { get; set; }
    public string Search { get; set; } = string.Empty;
    public int RuleSchemaVersion { get; set; } = 2;
    public List<SquireCleanupRuleConfiguration> CleanupRules { get; set; } = [];
    public Dictionary<string, SquireBuiltInRuleOverrideConfiguration> BuiltInRuleOverrides { get; set; } = new();
    public Dictionary<string, List<SquireRuleConfiguration>> RulesByCharacter { get; set; } = new();
    public Dictionary<string, List<uint>> ExcludedItemIdsByCharacter { get; set; } = new();
    public Dictionary<string, List<SquireDuplicateRetentionConfiguration>> DuplicateRetentionByCharacter { get; set; } = new();
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

public enum SquireRuleKind
{
    ProtectItem,
    RetainCopies,
}

public enum SquireRuleQuality
{
    Any,
    NormalQuality,
    HighQuality,
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

public enum SquireDisposition
{
    Keep,
    ExpertDelivery,
    Desynthesize,
    VendorSell,
    Discard,
    Unsupported,
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

public sealed record LegacyMmfMigrationReceipt(
    string SourceSha256,
    DateTimeOffset ImportedAtUtc,
    int CleanupRuleCount,
    int CharacterRuleCount);
