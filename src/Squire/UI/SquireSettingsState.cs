using MarketMafioso.Squire;

namespace Squire.UI;

internal static class SquireWorkspaces
{
    public const string Cleanup = "Cleanup";
    public const string Outfitter = "Outfitter";
    public const string Settings = "Settings";

    public static bool IsKnown(string? workspace) => workspace is Cleanup or Outfitter or Settings;
}

internal sealed class SquireWorkspaceState
{
    private readonly ISquireConfigurationStore configuration;

    public SquireWorkspaceState(ISquireConfigurationStore configuration)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        if (SquireWorkspaces.IsKnown(configuration.Squire.SelectedWorkspace))
        {
            SelectedWorkspace = configuration.Squire.SelectedWorkspace;
            return;
        }

        SelectedWorkspace = SquireWorkspaces.Cleanup;
        configuration.Squire.SelectedWorkspace = SelectedWorkspace;
        configuration.Save();
    }

    public string SelectedWorkspace { get; private set; }

    public bool OpenSettings() => Select(SquireWorkspaces.Settings);

    public bool Select(string workspace)
    {
        if (!SquireWorkspaces.IsKnown(workspace))
            throw new ArgumentOutOfRangeException(nameof(workspace), workspace, "Unknown Squire workspace.");
        if (string.Equals(SelectedWorkspace, workspace, StringComparison.Ordinal))
            return false;

        SelectedWorkspace = workspace;
        configuration.Squire.SelectedWorkspace = workspace;
        configuration.Save();
        return true;
    }
}

internal static class SquireSettingsControlIds
{
    public const string Workspace = "squire.workspace.settings";
    public const string SafetyPage = "squire.settings.page.safety";
    public const string CleanupPage = "squire.settings.page.cleanup";
    public const string RecoveryPage = "squire.settings.page.recovery";
    public const string IntegrationsPage = "squire.settings.page.integrations";
    public const string DeveloperPage = "squire.settings.page.developer";
    public const string ProtectBlueAndPurple = "squire.settings.safety.protect-blue-purple";
    public const string ProtectMateria = "squire.settings.safety.protect-materia";
    public const string ProtectPlayerSigned = "squire.settings.safety.protect-player-signed";
    public const string ProtectArmoireEligible = "squire.settings.safety.protect-armoire-eligible";
    public const string AuditRetentionDays = "squire.settings.safety.audit-retention-days";
    public const string ProtectFutureLeveling = "squire.settings.cleanup.protect-future-leveling";
    public const string AllowRiskyMateriaRetrieval = "squire.settings.cleanup.allow-risky-materia-retrieval";
    public const string ReviewRules = "squire.settings.cleanup.review-rules";
    public const string RecoverFromKnockout = "squire.settings.recovery.recover-from-knockout";
    public const string WaitForCombat = "squire.settings.recovery.wait-for-combat";
    public const string CombatTimeout = "squire.settings.recovery.combat-timeout-seconds";
    public const string LeaveDuty = "squire.settings.recovery.leave-duty";
    public const string PauseGatherBuddy = "squire.settings.integrations.pause-gatherbuddy";
    public const string PauseQuestionable = "squire.settings.integrations.pause-questionable";
    public const string PauseArtisan = "squire.settings.integrations.pause-artisan";
    public const string CloseSafeMenus = "squire.settings.integrations.close-safe-menus";
    public const string RouteDiagnostics = "squire.settings.developer.route-diagnostics";
    public const string AdvisorFixtures = "squire.settings.developer.advisor-fixtures";
    public const string AgentBridgeAudit = "squire.settings.developer.agent-bridge-audit";

    public static IReadOnlyList<string> All { get; } =
    [
        Workspace,
        SafetyPage,
        CleanupPage,
        RecoveryPage,
        IntegrationsPage,
        DeveloperPage,
        ProtectBlueAndPurple,
        ProtectMateria,
        ProtectPlayerSigned,
        ProtectArmoireEligible,
        AuditRetentionDays,
        ProtectFutureLeveling,
        AllowRiskyMateriaRetrieval,
        ReviewRules,
        RecoverFromKnockout,
        WaitForCombat,
        CombatTimeout,
        LeaveDuty,
        PauseGatherBuddy,
        PauseQuestionable,
        PauseArtisan,
        CloseSafeMenus,
        RouteDiagnostics,
        AdvisorFixtures,
        AgentBridgeAudit,
    ];
}

internal sealed class SquireSettingsState
{
    private readonly ISquireConfigurationStore configuration;
    private readonly Action requestPolicyReevaluation;
    private readonly Func<bool> getAgentBridgeAudit;
    private readonly Action<bool> setAgentBridgeAudit;

    public SquireSettingsState(
        ISquireConfigurationStore configuration,
        Action requestPolicyReevaluation,
        Func<bool> getAgentBridgeAudit,
        Action<bool> setAgentBridgeAudit)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.requestPolicyReevaluation = requestPolicyReevaluation ?? throw new ArgumentNullException(nameof(requestPolicyReevaluation));
        this.getAgentBridgeAudit = getAgentBridgeAudit ?? throw new ArgumentNullException(nameof(getAgentBridgeAudit));
        this.setAgentBridgeAudit = setAgentBridgeAudit ?? throw new ArgumentNullException(nameof(setAgentBridgeAudit));
    }

    public SquireConfiguration Policy => configuration.Squire;
    public bool RouteDiagnosticsVisible { get; private set; }
    public bool AdvisorFixturesEnabled => configuration.EnableMarketAcquisitionDryRunTools;
    public bool AgentBridgeAuditEnabled => getAgentBridgeAudit();

    public void UpdatePolicy(Action<SquireConfiguration> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        update(configuration.Squire);
        configuration.Save();
        requestPolicyReevaluation();
    }

    public void SetRouteDiagnosticsVisible(bool visible) => RouteDiagnosticsVisible = visible;

    public void SetAdvisorFixturesEnabled(bool enabled)
    {
        configuration.EnableMarketAcquisitionDryRunTools = enabled;
        configuration.Save();
    }

    public void SetAgentBridgeAuditEnabled(bool enabled)
    {
        setAgentBridgeAudit(enabled);
        configuration.Save();
    }
}
