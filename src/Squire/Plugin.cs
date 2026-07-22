using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Squire.AgentBridge;
using Squire.Interop;
using Squire.Persistence;
using Squire.UI;
using System.Text.Json;

namespace Squire;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/squire";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly PluginConfiguration configuration;
    private readonly WindowSystem windows = new("Squire");
    private readonly SquireWindow window;
    private readonly SquireIpcProvider ipc;
    private readonly AgentBridgeHost agentBridge;
    private readonly IFramework framework;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        if (string.IsNullOrWhiteSpace(configuration.PluginInstanceId))
            configuration.PluginInstanceId = Guid.NewGuid().ToString("N");
        configuration.Settings ??= new SquireSettings();
        SaveConfiguration();

        var configDirectory = pluginInterface.GetPluginConfigDirectory();
        var pluginConfigRoot = Directory.GetParent(configDirectory)?.FullName
            ?? throw new InvalidOperationException("Plugin configuration root is unavailable.");
        var importer = new LegacyMmfImporter(
            Path.Combine(pluginConfigRoot, "MarketMafioso.json"),
            configuration,
            SaveConfiguration);
        window = new(configuration, SaveAndPublish, importer);
        windows.AddWindow(window);
        ipc = new(new DalamudIpcRegistrar(pluginInterface), configuration.PluginInstanceId, CreateSnapshot, OpenWindow);
        agentBridge = new(
            configuration,
            configDirectory,
            SaveConfiguration,
            (action, _) => framework.RunOnTick(action),
            new SquireBridgeProvider(CreateBridgeTruth, OpenWindow, () => window.IsOpen = false));

        commands.AddHandler(Command, new CommandInfo((_, arguments) => HandleCommand(arguments))
        {
            HelpMessage = "Open standalone Squire.",
        });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        pluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        ipc.Dispose();
        agentBridge.Dispose();
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        framework.Update -= OnFrameworkUpdate;
        commands.RemoveHandler(Command);
        windows.RemoveAllWindows();
    }

    private void OpenWindow() => window.IsOpen = true;

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

    private void OnFrameworkUpdate(IFramework _) => agentBridge.Tick();

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
        featureState = "migration-shell",
        windowOpen = window.IsOpen,
        workspace = configuration.Settings.SelectedWorkspace,
        cleanupRuleCount = configuration.Settings.CleanupRules.Count,
        characterRuleCount = configuration.Settings.RulesByCharacter.Values.Sum(rules => rules.Count),
        legacyMmfImportedAtUtc = configuration.LegacyMmfMigration?.ImportedAtUtc,
    });

    private SquireBridgeTruth CreateBridgeTruth() => new(
        1,
        configuration.PluginInstanceId,
        Environment.ProcessId,
        GetType().Assembly.GetName().Version?.ToString() ?? "unknown",
        window.IsOpen,
        "migration-shell",
        configuration.Settings.SelectedWorkspace,
        configuration.Settings.CleanupRules.Count,
        configuration.Settings.RulesByCharacter.Values.Sum(rules => rules.Count),
        configuration.LegacyMmfMigration?.ImportedAtUtc);
}
