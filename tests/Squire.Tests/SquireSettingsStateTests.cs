using MarketMafioso;
using MarketMafioso.Squire;
using Squire.UI;

namespace Squire.Tests;

public sealed class SquireSettingsStateTests
{
    [Fact]
    public void MainWorkspaceRestoresPersistedSelectionWithoutSaving()
    {
        var configuration = new TestConfiguration();
        configuration.Squire.SelectedWorkspace = SquireWorkspaces.Outfitter;

        var state = new SquireWorkspaceState(configuration);

        Assert.Equal(SquireWorkspaces.Outfitter, state.SelectedWorkspace);
        Assert.Equal(0, configuration.SaveCount);
    }

    [Fact]
    public void ConfigureEntryPointSelectsSettingsAndPersistsOnce()
    {
        var configuration = new TestConfiguration();
        configuration.Squire.SelectedWorkspace = SquireWorkspaces.Outfitter;
        var state = new SquireWorkspaceState(configuration);

        Assert.True(state.OpenSettings());

        Assert.Equal(SquireWorkspaces.Settings, state.SelectedWorkspace);
        Assert.Equal(SquireWorkspaces.Settings, configuration.Squire.SelectedWorkspace);
        Assert.Equal(1, configuration.SaveCount);
    }

    [Fact]
    public void PolicyWriteSavesReevaluatesAndFeedsRuntimePolicy()
    {
        var configuration = new TestConfiguration();
        var reevaluations = 0;
        var state = new SquireSettingsState(configuration, () => reevaluations++, () => false, _ => { });

        state.UpdatePolicy(settings => settings.ProtectBlueAndPurpleGear = false);

        Assert.False(configuration.Squire.ProtectBlueAndPurpleGear);
        Assert.False(new SquireRuleStore(configuration).CreatePolicy(null).ProtectBlueAndPurpleGear);
        Assert.Equal(1, configuration.SaveCount);
        Assert.Equal(1, reevaluations);
    }

    [Fact]
    public void DeveloperPreferencesSaveWithoutPolicyReevaluation()
    {
        var configuration = new TestConfiguration();
        var reevaluations = 0;
        var auditEnabled = false;
        var state = new SquireSettingsState(
            configuration,
            () => reevaluations++,
            () => auditEnabled,
            value => auditEnabled = value);

        state.SetAdvisorFixturesEnabled(true);
        state.SetAgentBridgeAuditEnabled(true);
        state.SetRouteDiagnosticsVisible(true);

        Assert.True(configuration.EnableMarketAcquisitionDryRunTools);
        Assert.True(auditEnabled);
        Assert.True(state.RouteDiagnosticsVisible);
        Assert.Equal(2, configuration.SaveCount);
        Assert.Equal(0, reevaluations);
    }

    [Fact]
    public void ReviewedSettingsControlIdsAreStableAndUnique()
    {
        Assert.Equal(25, SquireSettingsControlIds.All.Count);
        Assert.Equal(SquireSettingsControlIds.All.Count, SquireSettingsControlIds.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("squire.settings.page.safety", SquireSettingsControlIds.All);
        Assert.Contains("squire.settings.safety.protect-blue-purple", SquireSettingsControlIds.All);
        Assert.Contains("squire.settings.developer.advisor-fixtures", SquireSettingsControlIds.All);
        Assert.DoesNotContain(SquireSettingsControlIds.All, id => id.Contains("execute", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TestConfiguration : ISquireConfigurationStore
    {
        public SquireConfiguration Squire { get; set; } = new();
        public string? OutfitterRouteExecutionStateJson { get; set; }
        public bool EnableMarketAcquisitionDryRunTools { get; set; }
        public PersistedMarketAcquisitionRequestDocument? ActiveMarketAcquisitionRequestDocument { get; set; }
        public PersistedMarketAcquisitionClaim? ActiveMarketAcquisitionClaim { get; set; }
        public int SaveCount { get; private set; }
        public void Save() => SaveCount++;
    }
}
