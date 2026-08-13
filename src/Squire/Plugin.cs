using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using Squire.AgentBridge;
using Squire.Interop;
using Squire.Persistence;
using Squire.UI;
using System.Text.Json;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Observations;
using MarketMafioso.Automation.Travel;
using MarketMafioso.Diagnostics;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Windows.Squire;

namespace Squire;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;

    private const string Command = "/squire";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly PluginConfiguration configuration;
    private readonly WindowSystem windows = new("Squire");
    private readonly SquireWindow window;
    private readonly SquireTabPanel featurePanel;
    private readonly UiStateCaptureService uiStateCapture;
    private readonly HttpClient marketHttpClient;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly SquireIpcProvider ipc;
    private readonly MarketMafiosoAcquisitionIpcClient marketMafiosoAcquisition;
    private readonly Squire.AgentBridge.AgentBridgeHost agentBridge;
    private readonly IFramework framework;
    private readonly DalamudSharedObservationHost? sharedObservationHost;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework)
    {
        ECommonsMain.ReducedLogging = true;
        ECommonsMain.Init(pluginInterface, this);
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        var loadedConfiguration = pluginInterface.GetPluginConfig() as PluginConfiguration;
        configuration = loadedConfiguration ?? new PluginConfiguration();
        configuration.SaveAction = SaveConfiguration;
        var configurationChanged = loadedConfiguration is null;
        if (string.IsNullOrWhiteSpace(configuration.PluginInstanceId))
        {
            configuration.PluginInstanceId = Guid.NewGuid().ToString("N");
            configurationChanged = true;
        }
        var archivalResult = LegacySettingsArchivalConsolidator.Consolidate(configuration, DateTimeOffset.UtcNow);
        if (configurationChanged || archivalResult.Changed)
            SaveConfiguration();

        var configDirectory = pluginInterface.GetPluginConfigDirectory();
        try
        {
            sharedObservationHost = new DalamudSharedObservationHost(new DalamudSharedObservationHostOptions
            {
                PluginConfigDirectory = configDirectory,
                PluginName = "Squire",
                PluginInstanceId = Guid.NewGuid().ToString("N"),
                GameBuild = Franthropy.Dalamud.Diagnostics.GamePatchCompatibilityGate.ReadCurrentGameVersion(),
                GameInventory = GameInventory,
                PlayerState = PlayerState,
                AddonLifecycle = AddonLifecycle,
                Diagnostic = (message, exception) =>
                {
                    if (exception is null) Log.Warning(message);
                    else Log.Error(exception, message);
                },
            });
            sharedObservationHost.Start();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Squire shared-observation hosting is unavailable.");
        }
        var pluginConfigRoot = Directory.GetParent(configDirectory)?.FullName
            ?? throw new InvalidOperationException("Plugin configuration root is unavailable.");
        var importer = new LegacyMmfImporter(
            Path.Combine(pluginConfigRoot, "MarketMafioso.json"),
            configuration,
            SaveConfiguration);
        var migrationPreview = importer.Preview();
        if (migrationPreview.CanImport)
            _ = importer.Import();
        var snapshotSource = new DalamudCharacterEquipmentSnapshotSource(PlayerState, DataManager, Log);
        var advisorCharacterSource = new DalamudAdvisorCharacterSubjectSource(PlayerState, DataManager);
        var capabilities = new DalamudSquireDispositionCapabilitySource();
        var ruleStore = new SquireCleanupRuleStore(configuration);
        var vnavmesh = new VNavmeshIpc(new DalamudVNavmeshIpcAdapter(pluginInterface, Log));
        var lifestream = new LifestreamIpc(pluginInterface, Log);
        uiStateCapture = new UiStateCaptureService(
            AddonLifecycle,
            framework,
            Condition,
            Path.Combine(configDirectory, "ui-state-captures"));
        marketHttpClient = new HttpClient();
        var listingSource = new UniversalisMarketAcquisitionPlanSource(marketHttpClient);
        reviewRegistry = new AgentBridgeUiReviewRegistry();
        featurePanel = new SquireTabPanel(
            configuration,
            snapshotSource,
            new DalamudSquireActionGameAdapter(
                snapshotSource,
                PlayerState,
                Condition,
                GameGui,
                framework,
                DataManager,
                capabilities,
                commands,
                ObjectTable,
                TargetManager,
                pluginInterface,
                Log,
                () => ruleStore.CreatePolicy(PlayerState.IsLoaded && PlayerState.ContentId != 0 ? PlayerState.ContentId : null),
                () => SquireExecutionRecoveryPolicy.From(configuration.FeatureSettings),
                () => vnavmesh.IsRunning
                    ? "vnavmesh is currently moving the character."
                    : lifestream.IsAvailable && lifestream.TryIsBusy(out var busy) && busy
                        ? "Lifestream is currently handling travel."
                        : null),
            capabilities,
            reviewRegistry,
            Path.Combine(configDirectory, "squire-logs"),
            uiStateCapture,
            GameInventory,
            DataManager,
            listingSource,
            new DalamudPlayerAdvisorBaselineSource(snapshotSource, PlayerState, DataManager),
            new AutoRetainerOutfitterMetadataSource(pluginInterface, Log),
            advisorCharacterSource.Capture,
            () => configuration.ActiveMarketAcquisitionRequestDocument?.Region
                  ?? configuration.ActiveMarketAcquisitionClaim?.Region
                  ?? "North America",
            () => configuration.EnableAgentBridgeAudit,
            enabled => configuration.EnableAgentBridgeAudit = enabled);
        marketMafiosoAcquisition = new MarketMafiosoAcquisitionIpcClient(pluginInterface);
        featurePanel.ConnectMarketAcquisition(marketMafiosoAcquisition.Stage);
        window = new(SaveAndPublish, importer, featurePanel, reviewRegistry);
        windows.AddWindow(window);
        ipc = new(new DalamudIpcRegistrar(pluginInterface), configuration.PluginInstanceId, CreateSnapshot, OpenWindow);
        agentBridge = new(
            configuration,
            configDirectory,
            SaveConfiguration,
            (action, _) => framework.RunOnTick(action),
            new SquireBridgeProvider(CreateBridgeTruth, OpenWindow, () => window.IsOpen = false, reviewRegistry));

        commands.AddHandler(Command, new CommandInfo((_, arguments) => HandleCommand(arguments))
        {
            HelpMessage = "Open standalone Squire.",
        });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        pluginInterface.UiBuilder.OpenConfigUi += OpenSettingsWindow;
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        ipc.Dispose();
        agentBridge.Dispose();
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenSettingsWindow;
        framework.Update -= OnFrameworkUpdate;
        commands.RemoveHandler(Command);
        windows.RemoveAllWindows();
        featurePanel.Dispose();
        uiStateCapture.Dispose();
        marketHttpClient.Dispose();
        sharedObservationHost?.Dispose();
        ECommonsMain.Dispose();
    }

    private void OpenWindow() => window.IsOpen = true;

    private void OpenSettingsWindow()
    {
        featurePanel.OpenSettings();
        window.IsOpen = true;
    }

    private void HandleCommand(string arguments)
    {
#if DEBUG
        var normalized = arguments.Trim();
        if (normalized.Equals("bridge on", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("bridge off", StringComparison.OrdinalIgnoreCase))
        {
            configuration.EnableAgentBridge = normalized.EndsWith("on", StringComparison.OrdinalIgnoreCase);
            SaveConfiguration();
            return;
        }
#endif
        OpenWindow();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        agentBridge.Tick();
        featurePanel.OnFrameworkUpdate();
    }

    private void SaveConfiguration() => pluginInterface.SavePluginConfig(configuration);

    private void SaveAndPublish()
    {
        SaveConfiguration();
        ipc.PublishChanged();
    }

    private string CreateSnapshot() => JsonSerializer.Serialize(new
    {
        schema = "gooseworks-squire-snapshot/v1",
        providerInstanceId = configuration.PluginInstanceId,
        featureState = "standalone",
        windowOpen = window.IsOpen,
        workspace = configuration.FeatureSettings.SelectedWorkspace,
        cleanupRuleCount = configuration.FeatureSettings.CleanupRules.Count,
        characterRuleCount = configuration.FeatureSettings.RulesByCharacter.Values.Sum(rules => rules.Count),
        legacyMmfImportedAtUtc = configuration.LegacyMmfMigration?.ImportedAtUtc,
    });

    private SquireBridgeTruth CreateBridgeTruth() => new(
        5,
        configuration.PluginInstanceId,
        Environment.ProcessId,
        GetType().Assembly.GetName().Version?.ToString() ?? "unknown",
        window.IsOpen,
        "standalone",
        configuration.FeatureSettings.SelectedWorkspace,
        configuration.FeatureSettings.CleanupRules.Count,
        configuration.FeatureSettings.RulesByCharacter.Values.Sum(rules => rules.Count),
        configuration.LegacyMmfMigration?.ImportedAtUtc,
        featurePanel.CreateStandaloneBridgeTruth());
}
