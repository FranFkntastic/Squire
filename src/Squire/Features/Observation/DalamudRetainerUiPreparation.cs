using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudRetainerUiPreparation
{
    private readonly ICommandManager commandManager;
    private readonly LifestreamIpc lifestream;
    private readonly Func<AgentBridge.AgentBridgeRenderedUiSnapshot> captureRetainerUi;
    private readonly Func<bool> activateRenderedSummoningBell;
    private readonly RenderedRetainerUiPreparationCoordinator coordinator = new();
    private string? lastSemanticActionDiagnostic;

    public DalamudRetainerUiPreparation(
        ICommandManager commandManager,
        LifestreamIpc lifestream,
        Func<AgentBridge.AgentBridgeRenderedUiSnapshot> captureRetainerUi,
        Func<bool> activateRenderedSummoningBell)
    {
        this.commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        this.lifestream = lifestream ?? throw new ArgumentNullException(nameof(lifestream));
        this.captureRetainerUi = captureRetainerUi ?? throw new ArgumentNullException(nameof(captureRetainerUi));
        this.activateRenderedSummoningBell = activateRenderedSummoningBell ?? throw new ArgumentNullException(nameof(activateRenderedSummoningBell));
    }

    public RenderedRetainerUiPreparationProgress Begin(string ownerHomeWorld) => coordinator.Begin(
        DateTimeOffset.UtcNow,
        RetainerListVisible(),
        lifestream.IsAvailable,
        ownerHomeWorld,
        ProcessSemanticCommand);

    public RenderedRetainerUiPreparationProgress Advance()
    {
        var renderedUi = captureRetainerUi();
        var marketBoardUiVisible = AddonVisible(renderedUi, "ItemSearch") ||
                                   AddonVisible(renderedUi, "ItemSearchResult");
        if (marketBoardUiVisible)
            CloseRenderedMarketBoardUi();
        var stateAvailable = lifestream.TryIsBusy(out var busy);
        var progress = coordinator.Advance(
            DateTimeOffset.UtcNow,
            AddonVisible(renderedUi, "RetainerList"),
            stateAvailable,
            busy,
            marketBoardUiVisible,
            ProcessSemanticCommand);
        return progress.Status == RenderedRetainerUiPreparationStatus.Failed &&
               !string.IsNullOrWhiteSpace(lastSemanticActionDiagnostic)
            ? progress with { Diagnostic = $"{progress.Diagnostic} {lastSemanticActionDiagnostic}" }
            : progress;
    }

    public RenderedRetainerUiPreparationProgress Cancel() => coordinator.Cancel();

    private bool RetainerListVisible() => AddonVisible(captureRetainerUi(), "RetainerList");

    private static bool AddonVisible(AgentBridge.AgentBridgeRenderedUiSnapshot snapshot, string name) =>
        snapshot.Addons.Any(value =>
        string.Equals(value.Name, name, StringComparison.Ordinal) &&
        value is { Present: true, Ready: true, Visible: true });

    private static unsafe void CloseRenderedMarketBoardUi()
    {
        CloseRenderedAddon("ItemSearchResult");
        CloseRenderedAddon("ItemSearch");
    }

    private static unsafe void CloseRenderedAddon(string addonName)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon != null && addon->IsReady && addon->IsVisible)
            addon->Close(true);
    }

    private bool ProcessSemanticCommand(string command)
    {
        lastSemanticActionDiagnostic = null;
        if (command.StartsWith("/li ", StringComparison.Ordinal))
            return commandManager.ProcessCommand(command);
        const string lifestreamObjectPrefix = "lifestream:interact-object:";
        if (command.StartsWith(lifestreamObjectPrefix, StringComparison.Ordinal) &&
            uint.TryParse(command[lifestreamObjectPrefix.Length..], out var dataId))
            return lifestream.TryEnqueueObjectInteraction(dataId);
        if (string.Equals(command, "rendered-ui:activate-summoning-bell", StringComparison.Ordinal))
            return activateRenderedSummoningBell();
        return false;
    }
}
