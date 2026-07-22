using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Ui;
using MarketMafioso.AgentBridge;

namespace MarketMafioso.Squire.Observation;

/// <summary>
/// Opens and inspects rendered Character, inventory, and retainer UI for diagnostics and audits.
/// </summary>
public sealed class DalamudRenderedCharacterUiProbe
{
    private static readonly string[] AddonNames =
    [
        "Character",
        "CharacterProfile",
        "CharacterClass",
        "CharacterRepute",
        "GearSetList",
        "ArmouryBoard",
        "Inventory",
        "InventoryLarge",
        "InventoryGrid",
        "InventoryExpansion",
        "InventoryBuddy",
        "ContextMenu",
        "ChatLog",
        "ItemDetail",
        "_TextError",
        "SelectYesno",
        "SelectString",
        "SelectIconString",
        "Talk",
        "ItemSearch",
        "ItemSearchResult",
        "MarketBoard",
        "Shop",
        "RetainerSell",
        "ContextMenu",
    ];

    private static readonly string[] RetainerAddonNames =
    [
        "RetainerList",
        "RetainerCharacter",
        "SelectString",
        "SelectIconString",
        "Talk",
        "ItemSearch",
        "ItemSearchResult",
        "_TargetInfo",
        "_TargetInfoMainTarget",
        "_NamePlate",
        "ItemDetail",
    ];

    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly Franthropy.Dalamud.AgentBridge.DalamudRenderedUiTextActionDispatcher renderedTextActions;
    private readonly RenderedGatheringStatsStabilizer gatheringStatsStabilizer = new(TimeSpan.FromSeconds(3));
    private readonly RenderedCharacterEquipmentScanCoordinator equipmentScan = new();
    private string? lastGearsetSelectionDiagnostic;

    public DalamudRenderedCharacterUiProbe(IGameGui gameGui, IDataManager dataManager)
    {
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        renderedTextActions = new(gameGui);
    }

    public AgentBridgeUiAutomationCapabilities Capabilities => new(
        "rendered-native-item-tooltip-request",
        MovesOperatingSystemCursor: false,
        ActivatesGameWindow: false,
        RequiresGameForeground: false,
        RequiresVisibleCharacterAddon: true,
        UsesRenderedTooltipAsAuthority: true,
        SupportsDeterministicReplay: false,
        "Equipment slots request the game's own ItemDetail tooltip through AtkTooltipManager with no cursor, focus, input, or event injection. This rendered path supports post-patch player/inventory audits and remains authoritative for rendered retainer observations.");

    public unsafe bool Open()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        if (addon != null && addon->RootNode != null && addon->RootNode->IsVisible())
            return false;
        Chat.ExecuteCommand("/character");
        return true;
    }

    public unsafe bool TryCloseCharacterUi()
    {
        var closed = false;
        var gearSetList = gameGui.GetAddonByName<AtkUnitBase>("GearSetList", 1);
        if (gearSetList != null && gearSetList->RootNode != null && gearSetList->RootNode->IsVisible())
        {
            gearSetList->Close(true);
            closed = true;
        }
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return closed;
        addon->Close(true);
        return true;
    }

    public unsafe bool TryCloseBlockingSelectString()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("SelectString", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return false;
        addon->Close(true);
        return true;
    }

    public unsafe bool TryCloseRetainerUi()
    {
        foreach (var addonName in new[] { "RetainerCharacter", "SelectString", "RetainerList" })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
                continue;
            addon->Close(true);
            return true;
        }
        return false;
    }

    public GearsetChangeCommand? TrySwitchCalibrationJob(string target)
    {
        if (!GearsetChangeCommand.TryCreate(target, out var command))
            return null;
        gatheringStatsStabilizer.Reset();
        Chat.ExecuteCommand(command.Command);
        return command;
    }

    public GearsetChangeCommand? TrySwitchGearsetSlot(string target)
    {
        if (!GearsetChangeCommand.TryCreateSlot(target, out var command))
            return null;
        gatheringStatsStabilizer.Reset();
        Chat.ExecuteCommand(command.Command);
        return command;
    }

    public Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryOpenGearsetList()
    {
        unsafe
        {
            var existing = gameGui.GetAddonByName<AtkUnitBase>("GearSetList", 1);
            if (existing != null && existing->RootNode != null && existing->RootNode->IsVisible() && existing->IsReady)
                return new(true, "RenderedAddonAlreadyOpen", "The rendered Gear Set list is already open.", "GearSetList", null);
        }
        var result = renderedTextActions.TryClickUniqueControlImmediatelyLeftOfText("Character", "Gear Set");
        return result.Success
            ? result
            : result with
            {
                Message = $"{result.Message} Open the rendered Character UI first, then retry.",
            };
    }

    public Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TrySelectCalibrationGearset(string target)
    {
        if (!GearsetChangeCommand.TryCreate(target, out var command))
            return new(false, "InvalidCalibrationJob", "Target must be Miner, Botanist, or Blacksmith.", "GearSetList", null);
        var selected = renderedTextActions.TrySelectUniqueListRowText("GearSetList", command.JobName);
        lastGearsetSelectionDiagnostic = selected.Message;
        return selected;
    }

    public unsafe Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryEquipSelectedGearset()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("GearSetList", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible() || !addon->IsReady)
            return new(false, "RenderedAddonUnavailable", "The rendered Gear Set list changed before equipping.", "GearSetList", null);
        var gearSetList = new AddonMaster.GearSetList(addon);
        if (!gearSetList.EquipSetButton->IsEnabled)
            return new(false, "RenderedEquipSetDisabled", $"The rendered Gear Set list has not enabled Equip Set. Selection evidence: {lastGearsetSelectionDiagnostic ?? "unavailable"}", "GearSetList", null);
        var equipped = renderedTextActions.TryActivateUniqueText("GearSetList", "Equip Set");
        if (equipped.Success)
            gatheringStatsStabilizer.Reset();
        return equipped;
    }

    public unsafe AgentBridgeRenderedUiSnapshot Capture()
    {
        var addons = new List<AgentBridgeRenderedAddonSnapshot>(AddonNames.Length + 4);
        var capturedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addonName in AddonNames)
        {
            addons.Add(CaptureAddon(addonName));
            capturedNames.Add(addonName);
        }

        var character = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        var stage = AtkStage.Instance();
        var unitManager = stage == null ? null : (AtkUnitManager*)stage->RaptureAtkUnitManager;
        if (character != null && unitManager != null)
        {
            var loaded = &unitManager->AllLoadedUnitsList;
            for (var index = 0; index < loaded->Count; index++)
            {
                AtkUnitBase* addon = loaded->Entries[index];
                if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
                    continue;
                var addonName = addon->NameString;
                if (string.IsNullOrWhiteSpace(addonName) || capturedNames.Contains(addonName) ||
                    (addon->HostId != character->Id && !addonName.Contains("Character", StringComparison.Ordinal)))
                    continue;
                addons.Add(CaptureAddon(addonName, addon));
                capturedNames.Add(addonName);
            }
        }
        return new AgentBridgeRenderedUiSnapshot(DateTimeOffset.UtcNow, addons);
    }

    /// <summary>
    /// Captures already-rendered retainer surfaces without opening, closing, selecting, or focusing
    /// anything. This is fixture discovery only; node values are not interpreted here.
    /// </summary>
    public unsafe AgentBridgeRenderedUiSnapshot CaptureRetainerUi()
    {
        var addons = RetainerAddonNames.Select(CaptureAddon).ToList();
        var capturedNames = addons.Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        var stage = AtkStage.Instance();
        var unitManager = stage == null ? null : (AtkUnitManager*)stage->RaptureAtkUnitManager;
        if (unitManager != null)
        {
            var loaded = &unitManager->AllLoadedUnitsList;
            for (var index = 0; index < loaded->Count; index++)
            {
                AtkUnitBase* addon = loaded->Entries[index];
                if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
                    continue;
                var name = addon->NameString;
                if (string.IsNullOrWhiteSpace(name) || capturedNames.Contains(name) ||
                    (!name.Contains("NamePlate", StringComparison.OrdinalIgnoreCase) &&
                     !name.Contains("TargetInfo", StringComparison.OrdinalIgnoreCase)))
                    continue;
                addons.Add(CaptureAddon(name, addon));
                capturedNames.Add(name);
            }
        }
        return new(DateTimeOffset.UtcNow, addons);
    }

    public bool TryActivateRenderedSummoningBell()
        => renderedTextActions.TryConfirmUniqueText("_TargetInfoMainTarget", "Summoning Bell").Success;

    public Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryOpenRenderedRetainer(string retainerName)
    {
        if (string.IsNullOrWhiteSpace(retainerName))
            return new(false, "InvalidRenderedRetainer", "A retainer name is required.", null, null);

        var gear = renderedTextActions.CaptureVisibleText("RetainerCharacter");
        if (gear.Available)
            return new(true, "RenderedRetainerGearVisible", "The rendered retainer attributes and gear surface is already visible.", "RetainerCharacter", null);

        var menu = renderedTextActions.CaptureVisibleText("SelectString");
        if (menu.Available)
        {
            var expectedIdentity = $"Retainer: {retainerName.Trim()}";
            if (!menu.TextNodes.Any(value => value.Text.Contains(expectedIdentity, StringComparison.OrdinalIgnoreCase)))
                return new(false, "RenderedRetainerIdentityMismatch", "The visible retainer menu does not identify the requested retainer.", "SelectString", null);
            return renderedTextActions.TryActivateUniqueSelectStringText("View retainer attributes and gear.");
        }

        return renderedTextActions.TryActivateUniqueRetainerListRowText(retainerName);
    }

    public RenderedGatheringStatsObservation CaptureGatheringStats() =>
        gatheringStatsStabilizer.Observe(RenderedCharacterStatsParser.Parse(Capture()));

    public RenderedEquipmentScanProgress BeginEquipmentScan()
    {
        return equipmentScan.Begin(Capture());
    }

    public RenderedEquipmentScanStepResult AdvanceEquipmentScan()
    {
        var progress = equipmentScan.Snapshot();
        if (progress.Status == RenderedEquipmentScanStatus.ReadyToHover && progress.CurrentTarget is { } target)
        {
            if (!TryRequestEquipmentTooltip(target, out var reason))
                return new(false, progress, reason);
            progress = equipmentScan.MarkHoverStarted(target.NodePath, DateTimeOffset.UtcNow);
            return new(progress.Status == RenderedEquipmentScanStatus.Observing, progress, progress.Diagnostic);
        }
        if (progress.Status == RenderedEquipmentScanStatus.Observing)
        {
            progress = equipmentScan.Observe(Capture(), DateTimeOffset.UtcNow);
            if (progress.Status is RenderedEquipmentScanStatus.Complete or RenderedEquipmentScanStatus.Failed)
                HideEquipmentTooltip();
            return new(true, progress, progress.Diagnostic);
        }
        return new(false, progress, "The rendered equipment scan is not waiting for an advance step.");
    }

    public RenderedEquipmentScanProgress CancelEquipmentScan()
    {
        HideEquipmentTooltip();
        return equipmentScan.Cancel();
    }

    public unsafe bool TryOpenArmouryBoard()
    {
        var existing = gameGui.GetAddonByName<AtkUnitBase>("ArmouryBoard", 1);
        if (existing != null && existing->RootNode != null && existing->RootNode->IsVisible())
            return false;
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.ArmouryBoard);
        if (agent == null)
            return false;
        agent->Show();
        return true;
    }

    public unsafe bool TryOpenInventoryWindow() => TryShowAddon("Inventory", AgentId.Inventory);

    public unsafe bool TryOpenInventoryBuddy() => TryShowAddon("InventoryBuddy", AgentId.InventoryBuddy);

    private unsafe bool TryShowAddon(string addonName, AgentId agentId)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon != null)
        {
            if (addon->RootNode != null && addon->RootNode->IsVisible())
                return false;
            addon->Show(true, 0);
            return true;
        }
        var agent = AgentModule.Instance()->GetAgentByInternalId(agentId);
        if (agent == null)
            return false;
        agent->Show();
        return true;
    }

    private int inventoryContextStableFrames;
    private int inventoryContextRetries;
    private bool inventoryContextOwnerResetRequested;

    /// <summary>
    /// Opens the game's own context menu for an exact inventory slot through
    /// AgentInventoryContext — the same path right-click uses, with raw InventoryType
    /// numbering and no tooltip container mapping. The rendered menu title is the
    /// item identity. The owner agent must be stable first; this advances one step per
    /// call and reports Cycling until the menu can be opened. A stale active-but-hidden
    /// owner is reset with Hide before Show, exactly like the proven opener.
    /// </summary>
    public unsafe Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryOpenBagSlotContext(string container, int slotIndex)
    {
        if (!Enum.TryParse<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>(container, out var inventoryType))
            return new(false, "InvalidContextContainer", $"'{container}' is not a recognized inventory container.", "ContextMenu", null);
        var context = AgentInventoryContext.Instance();
        if (context == null)
            return new(false, "InventoryContextUnavailable", "The inventory context agent is unavailable.", "ContextMenu", null);
        var owner = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        if (owner == null)
            return new(false, "InventoryOwnerUnavailable", "The inventory owner agent is unavailable.", "ContextMenu", null);
        if (inventoryContextRetries++ > 60)
            return new(false, "InventoryOwnerUnavailable", "The inventory owner never stabilized.", "ContextMenu", null);

        if (!owner->IsAgentActive())
        {
            inventoryContextStableFrames = 0;
            inventoryContextOwnerResetRequested = false;
            owner->Show();
            return new(false, "InventoryContextCycling", "Waiting for the inventory owner agent to become active.", "ContextMenu", null);
        }
        var addonId = owner->GetAddonId();
        if (addonId == 0 || !IsAddonPresented(addonId))
        {
            inventoryContextStableFrames = 0;
            // The inventory agent can retain its active bit after its addon was hidden;
            // Show is then a no-op, so Hide must clear the stale ownership first, and
            // only a fully inactive owner may be Shown again.
            if (!inventoryContextOwnerResetRequested)
            {
                inventoryContextOwnerResetRequested = true;
                owner->Hide();
                return new(false, "InventoryContextCycling", "Resetting the stale inventory owner before presenting its window.", "ContextMenu", null);
            }
            return new(false, "InventoryContextCycling", "Waiting for the stale inventory owner to release its hidden addon.", "ContextMenu", null);
        }
        inventoryContextOwnerResetRequested = false;
        if (++inventoryContextStableFrames < 6)
            return new(false, "InventoryContextCycling", $"Verifying inventory owner stability ({inventoryContextStableFrames}/6).", "ContextMenu", null);
        inventoryContextRetries = 0;
        context->OpenForItemSlot(inventoryType, slotIndex, 0, addonId);
        return new(true, "BagSlotContextRequested", $"Requested the rendered context menu for {container}:{slotIndex}.", "ContextMenu", null);
    }

    public void ResetBagSlotContext()
    {
        inventoryContextStableFrames = 0;
        inventoryContextRetries = 0;
        inventoryContextOwnerResetRequested = false;
    }

    /// <summary>
    /// Diagnostic: invoke a named vanilla context-menu entry for the exact slot through
    /// the menu addon's own callback, after proving the menu is bound to that slot.
    /// </summary>
    public unsafe Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryInvokeBagSlotContextAction(string container, int slotIndex, string label)
    {
        if (!Enum.TryParse<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>(container, out var inventoryType))
            return new(false, "InvalidContextContainer", $"'{container}' is not a recognized inventory container.", "ContextMenu", null);
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        var context = AgentInventoryContext.Instance();
        if (menu == null || !menu->IsReady || !menu->IsVisible || context == null)
        {
            var opened = TryOpenBagSlotContext(container, slotIndex);
            if (!opened.Success)
                return opened;
            return new(false, "InventoryContextCycling", "Waiting for the exact slot's context menu.", "ContextMenu", null);
        }
        if (context->TargetInventoryId != inventoryType || context->TargetInventorySlotId != slotIndex)
            return new(false, "UnexpectedContextMenu", "The visible context menu targets a different slot.", "ContextMenu", null);

        var labels = new List<string>();
        foreach (var parameter in context->EventParams)
            if (parameter.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString)
                labels.Add(parameter.GetValueAsString());
        var actionIndex = labels.FindIndex(value => string.Equals(value.Trim(), label, StringComparison.OrdinalIgnoreCase));
        if (actionIndex < 0 || actionIndex >= context->ContextItemCount || context->IsContextItemDisabled(actionIndex))
            return new(false, "ContextActionUnavailable", $"The context menu offered no enabled '{label}' entry.", "ContextMenu", null);

        var values = stackalloc AtkValue[5];
        values[0] = new() { Type = AtkValueType.Int, Int = 0 };
        values[1] = new() { Type = AtkValueType.Int, Int = actionIndex };
        values[2] = new() { Type = AtkValueType.Int, Int = 0 };
        values[3] = new() { Type = AtkValueType.Int, Int = 0 };
        values[4] = new() { Type = AtkValueType.Int, Int = 0 };
        return menu->FireCallback(5, values, true)
            ? new(true, "ContextActionInvoked", $"Invoked '{label}' for {container}:{slotIndex}.", "ContextMenu", null)
            : new(false, "ContextActionRejected", $"The context menu rejected '{label}'.", "ContextMenu", null);
    }

    /// <summary>
    /// Reads the exact bag slot's rendered identity by asking the game itself for it:
    /// open the slot's own context menu, activate its "Copy Name to Clipboard" entry,
    /// and read back the name the game wrote for the user. No numbering, no structs.
    /// </summary>
    public unsafe Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryCopyBagSlotContextName(string container, int slotIndex)
    {
        if (!Enum.TryParse<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>(container, out var inventoryType))
            return new(false, "InvalidContextContainer", $"'{container}' is not a recognized inventory container.", "ContextMenu", null);
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        var context = AgentInventoryContext.Instance();
        if (menu == null || !menu->IsReady || !menu->IsVisible || context == null)
        {
            var opened = TryOpenBagSlotContext(container, slotIndex);
            if (!opened.Success)
                return opened;
            return new(false, "InventoryContextCycling", "Waiting for the exact slot's context menu.", "ContextMenu", null);
        }
        if (context->TargetInventoryId != inventoryType || context->TargetInventorySlotId != slotIndex)
            return new(false, "UnexpectedContextMenu", "The visible context menu targets a different slot.", "ContextMenu", null);

        var labels = new List<string>();
        foreach (var parameter in context->EventParams)
            if (parameter.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString)
                labels.Add(parameter.GetValueAsString());
        var copyIndex = labels.FindIndex(label => string.Equals(label.Trim(), "Copy Name to Clipboard", StringComparison.OrdinalIgnoreCase));
        if (copyIndex < 0 || copyIndex >= context->ContextItemCount || context->IsContextItemDisabled(copyIndex))
            return new(false, "ContextNameCopyUnavailable",
                $"The context menu offered no enabled name-copy entry (count={context->ContextItemCount}; labels=[{string.Join(" | ", labels)}]).", "ContextMenu", null);

        var values = stackalloc AtkValue[5];
        values[0] = new() { Type = AtkValueType.Int, Int = 0 };
        values[1] = new() { Type = AtkValueType.Int, Int = copyIndex };
        values[2] = new() { Type = AtkValueType.Int, Int = 0 };
        values[3] = new() { Type = AtkValueType.Int, Int = 0 };
        values[4] = new() { Type = AtkValueType.Int, Int = 0 };
        if (!menu->FireCallback(5, values, true))
            return new(false, "ContextNameCopyRejected", "The context menu rejected the name-copy entry.", "ContextMenu", null);
        var name = Dalamud.Bindings.ImGui.ImGui.GetClipboardText();
        TryCloseBagSlotContext();
        return string.IsNullOrWhiteSpace(name)
            ? new(false, "ContextNameCopyFailed", "The context menu did not copy an item name.", "ContextMenu", null)
            : new(true, "ContextNameCopied", name, "ContextMenu", null);
    }

    public unsafe bool TryCloseBagSlotContext()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return false;
        addon->Close(true);
        return true;
    }

    private unsafe bool IsContextMenuVisible()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        return addon != null && addon->RootNode != null && addon->RootNode->IsVisible();
    }

    private unsafe bool IsAddonPresented(uint addonId)
    {
        var unitManager = RaptureAtkUnitManager.Instance();
        var addon = unitManager == null ? null : unitManager->GetAddonById((ushort)addonId);
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    public unsafe bool TryCloseInventorySurfaces()
    {
        var closed = false;
        foreach (var addonName in new[] { "Inventory", "InventoryBuddy" })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
                continue;
            Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.HideTooltip(addon->Id);
            addon->Close(true);
            closed = true;
        }
        return closed;
    }

    public unsafe bool TryCloseArmouryBoard()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("ArmouryBoard", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return false;
        HideArmouryTooltip();
        addon->Close(true);
        return true;
    }

    private static readonly (string Keyword, FFXIVClientStructs.FFXIV.Client.Game.InventoryType Type)[] ArmouryTabKeywords =
    [
        ("main hand", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryMainHand),
        ("off-hand", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryOffHand),
        ("off hand", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryOffHand),
        ("head", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryHead),
        ("body", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryBody),
        ("hands", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryHands),
        ("legs", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryLegs),
        ("feet", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryFeets),
        ("ear", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryEar),
        ("neck", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryNeck),
        ("wrist", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryWrist),
        ("bracelet", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryWrist),
        ("ring", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryRings),
        ("soul", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmorySoulCrystal),
    ];

    /// <summary>
    /// AgentItemDetail.TypeOrId values for armoury containers are the raw InventoryType
    /// enum values (proven live on Primary for ArmoryMainHand=3500 and ArmoryHead=3201:
    /// the agent accepts the enum numbering for these containers, unlike the small
    /// 48-51/69-72 scheme documented for player inventory and saddlebags).
    /// Mechanism only; rendered tooltip output is the differential audit comparator.
    /// </summary>
    private static uint ArmouryTooltipTypeOrId(FFXIVClientStructs.FFXIV.Client.Game.InventoryType inventoryType) =>
        (uint)inventoryType;

    public unsafe Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryShowArmourySlotTooltip(string target)
    {
        // Diagnostic experiment override: 'ArmoryMainHand:0:exp=<typeOrId>,<flag1>,<kind>' forces raw tooltip args.
        uint? experimentType = null;
        byte? experimentFlag = null;
        byte? experimentKind = null;
        var targetCore = target ?? string.Empty;
        const string ExpMarker = ":exp=";
        var expIndex = targetCore.IndexOf(ExpMarker, StringComparison.OrdinalIgnoreCase);
        if (expIndex >= 0)
        {
            var parts = targetCore[(expIndex + ExpMarker.Length)..].Split(',');
            if (parts.Length == 3 && uint.TryParse(parts[0], out var parsedType) &&
                byte.TryParse(parts[1], out var parsedFlag) && byte.TryParse(parts[2], out var parsedKind))
            {
                experimentType = parsedType;
                experimentFlag = parsedFlag;
                experimentKind = parsedKind;
            }
            targetCore = targetCore[..expIndex];
        }
        var separatorIndex = targetCore.LastIndexOf(':');
        if (separatorIndex <= 0 || !short.TryParse(targetCore[(separatorIndex + 1)..], out var slotIndex))
            return new(false, "InvalidArmouryTarget", "Target must be '<InventoryType>:<slotIndex>', for example ArmoryMainHand:0.", "ArmouryBoard", null);
        var inventoryTypeName = targetCore[..separatorIndex];
        if (!Enum.TryParse<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>(inventoryTypeName, true, out var inventoryType) ||
            ArmouryTabKeywords.All(value => value.Type != inventoryType))
            return new(false, "InvalidArmouryContainer", $"'{inventoryTypeName}' is not a supported armoury container.", "ArmouryBoard", null);
        if (slotIndex is < 0 or >= 50)
            return new(false, "InvalidArmourySlot", "Armoury slot index must be between 0 and 49.", "ArmouryBoard", null);

        var addon = gameGui.GetAddonByName<AtkUnitBase>("ArmouryBoard", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return new(false, "RenderedAddonUnavailable", "The rendered Armoury Board is unavailable; open it first.", "ArmouryBoard", null);

        var board = (AddonArmouryBoard*)addon;
        if (!TrySelectArmouryTab(board, inventoryType, out var tabDiagnostic))
            return new(false, "ArmouryTabCycling", tabDiagnostic, "ArmouryBoard", null);

        var slotNode = ResolveArmourySlotNode(addon, slotIndex);
        if (slotNode == null)
            return new(false, "ArmourySlotUnavailable", $"Rendered armoury slot {slotIndex} does not resolve on the current tab.", "ArmouryBoard", null);

        Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.HideTooltip(addon->Id);
        if (experimentType is { } rawType)
        {
            if (!TryShowArmourySlotTooltipRaw(addon, slotNode, rawType, experimentFlag ?? 0, experimentKind ?? 2, slotIndex))
                return new(false, "ArmouryTooltipRejected", $"The game rejected the experimental tooltip request (type={rawType}, flag={experimentFlag ?? 0}, kind={experimentKind ?? 2}) for slot {slotIndex}.", "ArmouryBoard", null);
        }
        else if (!Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.TryShowInventoryItemTooltip(addon->Id, slotNode, ArmouryTooltipTypeOrId(inventoryType), slotIndex))
            return new(false, "ArmouryTooltipRejected", $"The game rejected the ItemDetail tooltip request for {inventoryType} slot {slotIndex}.", "ArmouryBoard", null);

        var observation = RenderedItemDetailParser.Parse(Capture());
        return observation.Status == RenderedItemDetailStatus.Complete
            ? new(true, "RenderedArmouryTooltipObserved", $"Rendered Item Detail observed: {observation.Name}.", "ArmouryBoard", null)
            : new(true, "ArmouryTooltipDispatched", "The armoury tooltip request was dispatched; the rendered Item Detail settles on the next frame and can be read with get-item-detail-ui.", "ArmouryBoard", null);
    }

    /// <summary>
    /// Diagnostic dump of the InventoryManager container table: index to inventory type.
    /// The tooltip agent's TypeOrId numbering follows this table's registration order,
    /// which is not the enum order. Mechanism evidence only.
    /// </summary>
    public unsafe string CaptureInventoryContainerTableDiagnostic()
    {
        var manager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (manager == null || manager->Inventories == null)
            return "No InventoryManager.";
        var lines = new List<string>();
        for (var index = 0; index < 256; index++)
        {
            var container = manager->Inventories + index;
            if (container->Type == 0 && container->Size == 0)
                break;
            lines.Add($"[{index}] type={(uint)container->Type} ({container->Type}) size={container->Size} loaded={container->IsLoaded}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Diagnostic: open the classic Inventory window and select a tab (bag page).</summary>
    public unsafe string SetInventoryTabDiagnostic(int tab)
    {
        TryOpenInventoryWindow();
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Inventory", 1);
        if (addon == null)
            return "The classic Inventory addon is not loaded.";
        var inventory = (AddonInventory*)addon;
        inventory->SetTab(tab);
        return $"SetTab({tab}) dispatched; current TabIndex={inventory->TabIndex}.";
    }

    private int buddyOccupancyPhase;
    private int buddyOccupancySaddleIcons = -1;
    private DateTimeOffset buddyOccupancyTabSettledAt;

    /// <summary>
    /// Diagnostic: count the rendered item icons on each saddlebag tab of the game's own
    /// InventoryBuddy window. Stateful across calls — retry while the surface opens and
    /// each tab settles; the final call reports both icon counts. Rendered evidence for
    /// containers the differential has no struct-occupied slots to visit.
    /// </summary>
    public unsafe string CaptureInventoryBuddyOccupancyDiagnostic()
    {
        var addon = ResolveDifferentialAddon(DifferentialSurface.InventoryBuddy);
        if (addon == null)
        {
            EnsureDifferentialSurfaceOpen(DifferentialSurface.InventoryBuddy);
            buddyOccupancyPhase = 0;
            buddyOccupancySaddleIcons = -1;
            return "Opening the rendered saddlebag surface; retry.";
        }
        var buddy = (AddonInventoryBuddy*)addon;
        switch (buddyOccupancyPhase)
        {
            case 0:
                if (buddy->TabIndex != 0)
                {
                    buddy->SetTab(0);
                    return "Selecting the saddlebag tab; retry.";
                }
                buddyOccupancyPhase = 1;
                buddyOccupancyTabSettledAt = DateTimeOffset.UtcNow;
                return "Settling the saddlebag tab; retry.";
            case 1:
                if (DateTimeOffset.UtcNow - buddyOccupancyTabSettledAt < TimeSpan.FromMilliseconds(500))
                    return "Settling the saddlebag tab; retry.";
                buddyOccupancySaddleIcons = CountRenderedIconCells(addon);
                buddy->SetTab(1);
                buddyOccupancyPhase = 2;
                buddyOccupancyTabSettledAt = DateTimeOffset.UtcNow;
                return "Selecting the premium saddlebag tab; retry.";
            case 2:
                if (buddy->TabIndex != 1)
                    return "Selecting the premium saddlebag tab; retry.";
                if (DateTimeOffset.UtcNow - buddyOccupancyTabSettledAt < TimeSpan.FromMilliseconds(500))
                    return "Settling the premium saddlebag tab; retry.";
                var premiumIcons = CountRenderedIconCells(addon);
                buddyOccupancyPhase = 0;
                return $"SaddleBag1-2 rendered icon(s): {buddyOccupancySaddleIcons}; PremiumSaddleBag1-2 rendered icon(s): {premiumIcons}.";
            default:
                buddyOccupancyPhase = 0;
                return "Restarting the saddlebag occupancy count; retry.";
        }
    }

    /// <summary>
    /// Diagnostic dump of the tooltip-manager attachments for a window's item cells:
    /// the exact type/id, slot, and kind values the game itself registered. Mechanism
    /// evidence for agent container numbering — no item data is interpreted here.
    /// </summary>
    public unsafe string CaptureTooltipMapDiagnostic(string addonName)
    {
        var stage = AtkStage.Instance();
        if (stage == null)
            return "No AtkStage.";
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon == null)
            return $"Addon '{addonName}' is not loaded.";
        var lines = new List<string>();
        var map = &stage->TooltipManager.TooltipMap;
        CollectTooltipMapEntries(&addon->UldManager, map, lines, new HashSet<nint>());
        lines.Sort(StringComparer.Ordinal);
        return lines.Count == 0
            ? $"No tooltip attachments found under '{addonName}'."
            : string.Join("\n", lines);
    }

    private static unsafe void CollectTooltipMapEntries(
        AtkUldManager* manager,
        StdMap<Pointer<AtkResNode>, Pointer<AtkTooltipManager.AtkTooltipInfo>>* map,
        List<string> lines,
        HashSet<nint> visited)
    {
        if (manager == null || manager->NodeList == null || !visited.Add((nint)manager))
            return;
        for (var index = 0u; index < manager->NodeListCount; index++)
        {
            var node = manager->NodeList[index];
            if (node == null)
                continue;
            if (map->TryGetValuePointer(node, out var info) && info != null)
                lines.Add($"node={node->NodeId} type={info->Value->Type} parent={info->Value->ParentId} typeOrId={(uint)info->Value->AtkTooltipArgs.ItemArgs.InventoryType} slot={info->Value->AtkTooltipArgs.ItemArgs.Slot} kind={(byte)info->Value->AtkTooltipArgs.ItemArgs.Kind} flag1={info->Value->AtkTooltipArgs.ItemArgs.Flag1}");
            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
                CollectTooltipMapEntries(&componentNode->Component->UldManager, map, lines, visited);
        }
    }

    /// <summary>
    /// Diagnostic bag/saddlebag tooltip request used to prove agent container numbering
    /// before the differential consumes it. Accepts the same ':exp=type,flag,kind'
    /// override as the armoury probe.
    /// </summary>
    public unsafe Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryShowBagSlotTooltip(string target)
    {
        uint? experimentType = null;
        byte? experimentFlag = null;
        byte? experimentKind = null;
        var targetCore = target ?? string.Empty;
        const string ExpMarker = ":exp=";
        var expIndex = targetCore.IndexOf(ExpMarker, StringComparison.OrdinalIgnoreCase);
        if (expIndex >= 0)
        {
            var parts = targetCore[(expIndex + ExpMarker.Length)..].Split(',');
            if (parts.Length == 3 && uint.TryParse(parts[0], out var parsedType) &&
                byte.TryParse(parts[1], out var parsedFlag) && byte.TryParse(parts[2], out var parsedKind))
            {
                experimentType = parsedType;
                experimentFlag = parsedFlag;
                experimentKind = parsedKind;
            }
            targetCore = targetCore[..expIndex];
        }
        string? forcedParentName = null;
        var atIndex = targetCore.IndexOf('@');
        if (atIndex >= 0)
        {
            forcedParentName = targetCore[(atIndex + 1)..];
            targetCore = targetCore[..atIndex];
        }
        var separatorIndex = targetCore.LastIndexOf(':');
        if (separatorIndex <= 0 || !short.TryParse(targetCore[(separatorIndex + 1)..], out var slotIndex) || slotIndex < 0)
            return new(false, "InvalidBagTarget", "Target must be '<Container>:<slotIndex>', for example Inventory1:0.", "Inventory", null);
        var container = targetCore[..separatorIndex];
        var surface = SurfaceFor(container);
        if (surface == DifferentialSurface.ArmouryBoard || BagTooltipTypeOrId(container) is null)
            return new(false, "InvalidBagContainer", $"'{container}' is not a player-inventory or saddlebag container.", "Inventory", null);
        var addonName = SurfaceAddon(surface);
        var addon = ResolveDifferentialAddon(surface);
        if (addon == null)
        {
            EnsureDifferentialSurfaceOpen(surface);
            return new(false, "BagSurfaceOpening", $"Opening the rendered {addonName} surface; retry.", addonName, null);
        }
        // Diagnostic parent override: 'Inventory2:0@InventoryExpansion:exp=...' forces the
        // tooltip parent window, testing which managed interface resolves a container.
        AtkUnitBase* parentAddon = addon;
        if (forcedParentName is not null)
        {
            var forced = gameGui.GetAddonByName<AtkUnitBase>(forcedParentName, 1);
            if (forced != null && (forced->RootNode == null || !forced->RootNode->IsVisible()))
                forced->Show(true, 0);
            if (forced == null || forced->RootNode == null || !forced->RootNode->IsVisible())
                return new(false, "BagParentOpening", $"Opening the forced tooltip parent '{forcedParentName}'; retry.", forcedParentName, null);
            parentAddon = forced;
        }
        if (surface == DifferentialSurface.InventoryBuddy && !SelectInventoryBuddyTab((AddonInventoryBuddy*)addon, container))
            return new(false, "BagTabCycling", "Cycling the rendered saddlebag tab.", addonName, null);
        var slotNode = ResolveFirstDragDropCell(addon);
        if (slotNode == null)
            return new(false, "BagSlotUnavailable", $"The rendered {addonName} surface has no item cells.", addonName, null);
        var typeOrId = experimentType ?? BagTooltipTypeOrId(container)!.Value;
        if (!TryShowArmourySlotTooltipRaw(parentAddon, slotNode, typeOrId, experimentFlag ?? 0, experimentKind ?? 2, slotIndex))
            return new(false, "BagTooltipRejected", $"The game rejected the tooltip request (type={typeOrId}) for {container}:{slotIndex}.", addonName, null);
        var observation = RenderedItemDetailParser.Parse(Capture());
        return observation.Status == RenderedItemDetailStatus.Complete
            ? new(true, "RenderedBagTooltipObserved", $"Rendered Item Detail observed: {observation.Name}.", addonName, null)
            : new(true, "BagTooltipDispatched", "The bag tooltip request was dispatched; the rendered Item Detail settles on the next frame and can be read with get-item-detail-ui.", addonName, null);
    }

    private static unsafe bool TryShowArmourySlotTooltipRaw(AtkUnitBase* addon, AtkResNode* slotNode, uint typeOrId, byte flag1, byte kind, short slotIndex)
    {
        var stage = AtkStage.Instance();
        if (stage == null)
            return false;
        var args = stackalloc AtkTooltipManager.AtkTooltipArgs[1];
        args->Ctor();
        args->ItemArgs.InventoryType = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)typeOrId;
        args->ItemArgs.Flag1 = flag1;
        args->ItemArgs.BuyQuantity = -1;
        args->ItemArgs.Slot = slotIndex;
        args->ItemArgs.Kind = (FFXIVClientStructs.FFXIV.Client.Enums.DetailKind)kind;
        stage->TooltipManager.ShowTooltip(AtkTooltipType.Item, addon->Id, slotNode, args);
        return true;
    }

    private unsafe bool TrySelectArmouryTab(AddonArmouryBoard* board, FFXIVClientStructs.FFXIV.Client.Game.InventoryType target, out string diagnostic)
    {
        // Tab text updates on the frame after NextTab, so this advances at most one tab per
        // call and reports cycling; the caller retries until the target tab is rendered.
        var label = board->CategoryLabelNode != null
            ? board->CategoryLabelNode->NodeText.ExtractText().Trim()
            : string.Empty;
        var matched = ArmouryTabKeywords
            .Where(value => label.Contains(value.Keyword, StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Type)
            .FirstOrDefault();
        if (label.Length > 0 && matched == target)
        {
            diagnostic = string.Empty;
            return true;
        }
        board->NextTab(0);
        diagnostic = $"Cycling armoury tabs; currently on '{label}'.";
        return false;
    }

    private static unsafe AtkResNode* ResolveArmourySlotNode(AtkUnitBase* addon, short slotIndex)
    {
        var cells = new List<(float X, float Y, nint Node)>();
        CollectDragDropCells(&addon->UldManager, cells);
        var ordered = cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();
        return slotIndex < ordered.Length ? (AtkResNode*)ordered[slotIndex].Node : null;
    }

    private static unsafe void CollectDragDropCells(AtkUldManager* manager, List<(float X, float Y, nint Node)> cells)
    {
        if (manager == null || manager->NodeList == null)
            return;
        for (var index = 0u; index < manager->NodeListCount; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !IsEffectivelyVisible(node))
                continue;
            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
            {
                if (componentNode->Component->GetComponentType() == ComponentType.DragDrop)
                {
                    var x = 0f;
                    var y = 0f;
                    node->GetPositionFloat(&x, &y);
                    cells.Add((x, y, (nint)node));
                }
                CollectDragDropCells(&componentNode->Component->UldManager, cells);
            }
        }
    }

    private unsafe void HideArmouryTooltip()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("ArmouryBoard", 1);
        if (addon == null)
            return;
        Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.HideTooltip(addon->Id);
    }

    /// <summary>
    /// Reads only the rendered ItemDetail identity line: name with quality glyph.
    /// Bag items are not equipment, so the full gear tuple is not required here.
    /// </summary>
    private static (string? Name, bool? IsHighQuality) ParseItemDetailIdentity(AgentBridgeRenderedUiSnapshot snapshot)
    {
        var addon = snapshot.Addons.FirstOrDefault(value => string.Equals(value.Name, "ItemDetail", StringComparison.Ordinal));
        if (addon is not { Present: true, Ready: true, Visible: true })
            return (null, null);
        var nameNode = addon.TextNodes.LastOrDefault(value => string.Equals(value.NodePath, "ItemDetail/33", StringComparison.Ordinal));
        if (nameNode is null || string.IsNullOrWhiteSpace(nameNode.Text))
            return (null, null);
        const char hqGlyph = '';
        var text = nameNode.Text.Trim();
        var isHq = text.Contains(hqGlyph);
        // Status glyphs (HQ, collectable, and friends) live in the Unicode private use
        // area; the sheet name carries none of them, so strip the whole block.
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, "[-]", string.Empty);
        var name = System.Text.RegularExpressions.Regex.Replace(stripped, @"\s+", " ").Trim();
        return (name, isHq);
    }

    private readonly RenderedArmouryDifferentialCoordinator armouryDifferential = new();
    private Func<string, RenderedItemDetailObservation, uint?>? armouryNameResolver;
    private Func<string, uint?, uint?>? armouryBagNameResolver;
    private Func<AgentBridgeInventoryStructSnapshot>? inventoryStructRefresher;
    private DateTimeOffset armouryTooltipDispatchedAt;
    private string? armouryCandidateSignature;
    private DateTimeOffset armouryCandidateStartedAt;
    private readonly HashSet<string> occupancyCheckedSurfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<DifferentialSurface, int> surfaceOpenAttempts = new();
    private int armouryDispatchAttempts;

    public RenderedArmouryDifferentialProgress BeginArmouryDifferential(
        IReadOnlyList<AgentBridgeInventoryStructItem> structBaseline,
        Func<string, RenderedItemDetailObservation, uint?> nameResolver,
        Func<string, uint?, uint?>? bagNameResolver = null,
        Func<AgentBridgeInventoryStructSnapshot>? structRefresher = null)
    {
        armouryNameResolver = nameResolver ?? throw new ArgumentNullException(nameof(nameResolver));
        armouryBagNameResolver = bagNameResolver;
        inventoryStructRefresher = structRefresher;
        armouryCandidateSignature = null;
        occupancyCheckedSurfaces.Clear();
        armouryDispatchAttempts = 0;
        surfaceOpenAttempts.Clear();
        ResetBagSlotContext();
        // A previous diagnostic or failed run can leave the game's context menu open;
        // the differential must start from a clean rendered surface.
        TryCloseBagSlotContext();
        armouryDifferential.Begin(structBaseline);
        TryOpenArmouryBoard();
        return armouryDifferential.Snapshot();
    }

    public RenderedArmouryDifferentialProgress CancelArmouryDifferential() => armouryDifferential.Cancel();

    public unsafe RenderedArmouryDifferentialProgress AdvanceArmouryDifferential()
    {
        var current = armouryDifferential.Current;
        var nameResolver = armouryNameResolver;
        if (current is null || nameResolver is null)
            return armouryDifferential.Snapshot();

        var surface = SurfaceFor(current.Value.Container);
        var addonName = SurfaceAddon(surface);
        var addon = ResolveDifferentialAddon(surface);
        if (addon == null)
        {
            // Surfaces open a frame after the show request, so retry a bounded number of
            // times before declaring the container class unproven.
            var attempts = surfaceOpenAttempts.GetValueOrDefault(surface);
            if (attempts < 5)
            {
                surfaceOpenAttempts[surface] = attempts + 1;
                EnsureDifferentialSurfaceOpen(surface);
                return armouryDifferential.Snapshot() with { Diagnostic = $"Opening the rendered {addonName} surface." };
            }
            surfaceOpenAttempts.Remove(surface);
            return armouryDifferential.SkipContainerSlots(current.Value.Container, $"the rendered {addonName} surface is unavailable");
        }
        surfaceOpenAttempts.Remove(surface);

        if (surface == DifferentialSurface.ArmouryBoard)
        {
            var board = (AddonArmouryBoard*)addon;
            if (!Enum.TryParse<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>(current.Value.Container, out var inventoryType))
                return FailArmouryDifferential($"Unsupported armoury container '{current.Value.Container}'.");
            if (!TrySelectArmouryTab(board, inventoryType, out var tabDiagnostic))
                return armouryDifferential.Snapshot() with { Diagnostic = tabDiagnostic };
            var slotNode = ResolveArmourySlotNode(addon, (short)current.Value.SlotIndex);
            if (slotNode == null)
                return FailArmouryDifferential($"Rendered armoury slot {current.Value.SlotIndex} on {current.Value.Container} does not resolve.");
            return AdvanceArmourySlotDifferential(addon, current.Value, slotNode, ArmouryTooltipTypeOrId(inventoryType), nameResolver);
        }

        if (surface == DifferentialSurface.InventoryBuddy && !SelectInventoryBuddyTab((AddonInventoryBuddy*)addon, current.Value.Container))
            return armouryDifferential.Snapshot() with { Diagnostic = "Cycling the rendered saddlebag tab." };
        return AdvanceBagDifferential(addon, current.Value);
    }

    private unsafe RenderedArmouryDifferentialProgress AdvanceArmourySlotDifferential(
        AtkUnitBase* addon,
        (string Container, int SlotIndex, uint ItemId, bool IsHighQuality) current,
        AtkResNode* slotNode,
        uint typeOrId,
        Func<string, RenderedItemDetailObservation, uint?> nameResolver)
    {
        if (armouryCandidateSignature is null)
        {
            // No per-slot Hide: ShowTooltip replaces the previous tooltip, and the
            // equipment scan proves replacement is reliable; Hide+Show races drop requests.
            if (!Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.TryShowInventoryItemTooltip(
                    addon->Id, slotNode, typeOrId, (short)current.SlotIndex))
                return FailArmouryDifferential($"The game rejected the armoury tooltip request for {current.Container}:{current.SlotIndex}.");
            armouryTooltipDispatchedAt = DateTimeOffset.UtcNow;
            armouryCandidateSignature = string.Empty;
            armouryCandidateStartedAt = DateTimeOffset.UtcNow;
            return armouryDifferential.Snapshot();
        }

        var now = DateTimeOffset.UtcNow;
        if (now - armouryTooltipDispatchedAt < TimeSpan.FromMilliseconds(300))
            return armouryDifferential.Snapshot();
        if (now - armouryTooltipDispatchedAt > TimeSpan.FromSeconds(3))
        {
            // A silent dispatch drop must not be misread as a data disagreement: re-dispatch
            // up to three times before declaring that nothing rendered.
            if (armouryDispatchAttempts < 3)
            {
                armouryDispatchAttempts++;
                armouryCandidateSignature = null;
                return armouryDifferential.Snapshot();
            }
            var timedOut = armouryDifferential.RecordRenderedObservation(current.Container, current.SlotIndex, null, null, null);
            armouryCandidateSignature = null;
            armouryDispatchAttempts = 0;
            return timedOut;
        }
        var observation = RenderedItemDetailParser.Parse(Capture());
        string signature;
        if (observation.Status == RenderedItemDetailStatus.Complete)
        {
            var resolvedId = nameResolver(observation.Name!, observation);
            signature = resolvedId is null
                ? $"unresolved:{observation.Name}"
                : $"resolved:{resolvedId}:{observation.Quality}";
        }
        else
        {
            return armouryDifferential.Snapshot();
        }
        if (signature != armouryCandidateSignature)
        {
            armouryCandidateSignature = signature;
            armouryCandidateStartedAt = now;
            return armouryDifferential.Snapshot();
        }
        if (now - armouryCandidateStartedAt < TimeSpan.FromMilliseconds(300))
            return armouryDifferential.Snapshot();

        var renderedId = observation.Status == RenderedItemDetailStatus.Complete
            ? nameResolver(observation.Name!, observation)
            : null;
        var result = RecordDifferentialObservation(
            current,
            renderedId,
            observation.Status == RenderedItemDetailStatus.Complete ? observation.Quality == RenderedItemQuality.High : null,
            observation.Status == RenderedItemDetailStatus.Complete ? observation.Name : null);
        armouryCandidateSignature = null;
        armouryDispatchAttempts = 0;
        return result;
    }

    /// <summary>
    /// One bag/saddlebag differential step through the game's own ItemDetail tooltip:
    /// ShowTooltip with the raw InventoryType enum value (proven live on this build: the
    /// agent accepts enum numbering for player bags, unlike the documented 48-51/69-72
    /// scheme), then the rendered identity line (name with quality glyph) is compared.
    /// Bag items are not equipment, so only the identity line is parsed.
    /// </summary>
    private unsafe RenderedArmouryDifferentialProgress AdvanceBagDifferential(
        AtkUnitBase* addon,
        (string Container, int SlotIndex, uint ItemId, bool IsHighQuality) current)
    {
        if (!Enum.TryParse<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>(current.Container, out var inventoryType))
            return FailArmouryDifferential($"Unsupported bag container '{current.Container}'.");

        if (armouryCandidateSignature is null)
        {
            // Same replace-instead-of-hide discipline as the armoury path: one anchor
            // node on the owning window, the slot travels in the tooltip arguments.
            var anchorNode = ResolveFirstDragDropCell(addon);
            if (anchorNode == null)
                return FailArmouryDifferential($"The rendered inventory surface has no item cells for {current.Container}:{current.SlotIndex}.");
            if (!Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.TryShowInventoryItemTooltip(
                    addon->Id, anchorNode, (uint)inventoryType, (short)current.SlotIndex))
                return FailArmouryDifferential($"The game rejected the bag tooltip request for {current.Container}:{current.SlotIndex}.");
            armouryTooltipDispatchedAt = DateTimeOffset.UtcNow;
            armouryCandidateSignature = string.Empty;
            armouryCandidateStartedAt = DateTimeOffset.UtcNow;
            return armouryDifferential.Snapshot();
        }

        var now = DateTimeOffset.UtcNow;
        if (now - armouryTooltipDispatchedAt < TimeSpan.FromMilliseconds(300))
            return armouryDifferential.Snapshot();
        if (now - armouryTooltipDispatchedAt > TimeSpan.FromSeconds(3))
        {
            // A silent dispatch drop must not be misread as a data disagreement: re-dispatch
            // up to three times before declaring that nothing rendered.
            if (armouryDispatchAttempts < 3)
            {
                armouryDispatchAttempts++;
                armouryCandidateSignature = null;
                return armouryDifferential.Snapshot();
            }
            var timedOut = RecordDifferentialObservation(current, null, null, null);
            armouryCandidateSignature = null;
            armouryDispatchAttempts = 0;
            return timedOut;
        }

        var (renderedName, renderedIsHq) = ParseItemDetailIdentity(Capture());
        if (renderedName is null || renderedIsHq is null)
            return armouryDifferential.Snapshot();
        var resolved = armouryBagNameResolver?.Invoke(renderedName, current.ItemId);
        var signature = resolved is null
            ? $"unresolved:{renderedName}:{renderedIsHq}"
            : $"resolved:{resolved}:{renderedIsHq}";
        if (signature != armouryCandidateSignature)
        {
            armouryCandidateSignature = signature;
            armouryCandidateStartedAt = now;
            return armouryDifferential.Snapshot();
        }
        if (now - armouryCandidateStartedAt < TimeSpan.FromMilliseconds(300))
            return armouryDifferential.Snapshot();

        var result = RecordDifferentialObservation(current, resolved, renderedIsHq, renderedName);
        armouryCandidateSignature = null;
        armouryDispatchAttempts = 0;
        return result;
    }

    private unsafe RenderedArmouryDifferentialProgress RecordDifferentialObservation(
        (string Container, int SlotIndex, uint ItemId, bool IsHighQuality) current,
        uint? renderedId,
        bool? renderedIsHq,
        string? renderedName)
    {
        // Inventories move (AutoRetainer, manual play): before accepting an identity
        // disagreement, re-read the struct state for this slot. A fresh read agreeing
        // with the rendered identity means the baseline went stale, not that the
        // struct read is unreliable.
        if (renderedId is not null && renderedIsHq is not null && inventoryStructRefresher is not null &&
            (renderedId != current.ItemId || renderedIsHq != current.IsHighQuality))
        {
            var fresh = inventoryStructRefresher();
            var freshItem = fresh.Items.FirstOrDefault(item =>
                string.Equals(item.Container, current.Container, StringComparison.Ordinal) && item.SlotIndex == current.SlotIndex);
            if (freshItem is not null && freshItem.ItemId == renderedId && freshItem.IsHighQuality == renderedIsHq)
                armouryDifferential.RefreshBaselineItem(current.Container, current.SlotIndex, freshItem.ItemId, freshItem.IsHighQuality);
        }
        var progress = armouryDifferential.RecordRenderedObservation(current.Container, current.SlotIndex, renderedId, renderedIsHq, renderedName);
        RecordDeferredSurfaceOccupancy(current.Container);
        return progress;
    }

    /// <summary>
    /// Occupancy is counted only after the surface's first slot observation completes:
    /// a freshly opened window reports near-zero icons until its grid finishes loading,
    /// so an eager count at surface resolution races the icon loader.
    /// </summary>
    private unsafe void RecordDeferredSurfaceOccupancy(string container)
    {
        var surface = SurfaceFor(container);
        var addon = ResolveDifferentialAddon(surface);
        if (addon != null)
            RecordSurfaceOccupancy(surface, addon, container);
    }

    private RenderedArmouryDifferentialProgress FailArmouryDifferential(string message)
    {
        armouryCandidateSignature = null;
        return armouryDifferential.Fail(message);
    }

    private enum DifferentialSurface { ArmouryBoard, InventoryWindow, InventoryBuddy }

    private static DifferentialSurface SurfaceFor(string container) => container switch
    {
        "Inventory1" or "Inventory2" or "Inventory3" or "Inventory4" => DifferentialSurface.InventoryWindow,
        "SaddleBag1" or "SaddleBag2" or "PremiumSaddleBag1" or "PremiumSaddleBag2" => DifferentialSurface.InventoryBuddy,
        _ => DifferentialSurface.ArmouryBoard,
    };

    private static string SurfaceAddon(DifferentialSurface surface) => surface switch
    {
        DifferentialSurface.InventoryWindow => "Inventory",
        DifferentialSurface.InventoryBuddy => "InventoryBuddy",
        _ => "ArmouryBoard",
    };

    /// <summary>
    /// AgentItemDetail.TypeOrId values for player inventory and saddlebag containers are
    /// the raw InventoryType enum values, same as the armoury path: proven live on this
    /// build (Inventory1:0 rendered the correct material where the documented 48-51
    /// numbering rendered a soul crystal from a different container). Returns null when
    /// the container name is not a recognized InventoryType.
    /// Mechanism only; rendered tooltip output is the differential audit comparator.
    /// </summary>
    private static uint? BagTooltipTypeOrId(string container) =>
        Enum.TryParse<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>(container, out var inventoryType) &&
        SurfaceFor(container) != DifferentialSurface.ArmouryBoard
            ? (uint)inventoryType
            : null;

    private unsafe bool EnsureDifferentialSurfaceOpen(DifferentialSurface surface)
    {
        if (ResolveDifferentialAddon(surface) != null)
            return true;
        switch (surface)
        {
            case DifferentialSurface.InventoryWindow:
                TryOpenInventoryWindow();
                break;
            case DifferentialSurface.InventoryBuddy:
                TryOpenInventoryBuddy();
                break;
            default:
                TryOpenArmouryBoard();
                break;
        }
        return false;
    }

    private static readonly string[] InventoryWindowCandidates = ["Inventory", "InventoryLarge", "InventoryGrid", "InventoryExpansion"];

    private static readonly string[] InventoryOccupancyCandidates =
    [
        "Inventory", "InventoryLarge", "InventoryGrid", "InventoryExpansion",
        "InventoryGrid0", "InventoryGrid1",
        "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E",
    ];

    /// <summary>
    /// Diagnostic: per-addon visibility and rendered item-icon counts for every known
    /// player-inventory window layout. The inventory display mode decides which addons
    /// carry the 140 bag slots; this dump shows which family is live before the
    /// differential trusts an icon-count comparison.
    /// </summary>
    public unsafe string CaptureInventoryWindowOccupancyDiagnostic()
    {
        var lines = new List<string>();
        foreach (var candidate in InventoryOccupancyCandidates)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(candidate, 1);
            if (addon == null)
            {
                lines.Add($"{candidate}: not loaded");
                continue;
            }
            var visible = addon->RootNode != null && addon->RootNode->IsVisible();
            lines.Add($"{candidate}: visible={visible} icons={(visible ? CountRenderedIconCells(addon) : 0)}");
        }
        return string.Join("\n", lines);
    }


    private unsafe AtkUnitBase* ResolveInventoryWindowAddon()
    {
        foreach (var candidate in InventoryWindowCandidates)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(candidate, 1);
            if (addon != null && addon->RootNode != null && addon->RootNode->IsVisible() && ResolveFirstDragDropCell(addon) != null)
                return addon;
        }
        return null;
    }

    private unsafe AtkUnitBase* ResolveDifferentialAddon(DifferentialSurface surface)
    {
        if (surface == DifferentialSurface.InventoryWindow)
            return ResolveInventoryWindowAddon();
        var addon = gameGui.GetAddonByName<AtkUnitBase>(SurfaceAddon(surface), 1);
        return addon != null && addon->RootNode != null && addon->RootNode->IsVisible() ? addon : null;
    }

    private unsafe bool IsAddonVisible(string addonName)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        return addon != null && addon->RootNode != null && addon->RootNode->IsVisible();
    }

    private static unsafe bool SelectInventoryBuddyTab(AddonInventoryBuddy* buddy, string container)
    {
        var premium = container is "PremiumSaddleBag1" or "PremiumSaddleBag2";
        var wanted = premium ? (byte)1 : (byte)0;
        if (buddy->TabIndex == wanted)
            return true;
        buddy->SetTab(wanted);
        return false;
    }

    private unsafe void RecordSurfaceOccupancy(DifferentialSurface surface, AtkUnitBase* addon, string container)
    {
        var key = surface switch
        {
            DifferentialSurface.InventoryWindow => "inventory-window",
            DifferentialSurface.InventoryBuddy => container is "PremiumSaddleBag1" or "PremiumSaddleBag2"
                ? "inventory-buddy-premium"
                : "inventory-buddy-saddle",
            _ => $"armoury:{container}",
        };
        if (!occupancyCheckedSurfaces.Add(key))
            return;
        var iconCount = CountRenderedIconCells(addon);
        switch (surface)
        {
            case DifferentialSurface.InventoryWindow:
                var bagStructCount = new[] { "Inventory1", "Inventory2", "Inventory3", "Inventory4" }.Sum(armouryDifferential.StructCountFor);
                armouryDifferential.RecordOccupancyCount("Inventory1-4", bagStructCount, iconCount);
                break;
            case DifferentialSurface.InventoryBuddy:
                var premium = container is "PremiumSaddleBag1" or "PremiumSaddleBag2";
                var containers = premium ? new[] { "PremiumSaddleBag1", "PremiumSaddleBag2" } : new[] { "SaddleBag1", "SaddleBag2" };
                var buddyStructCount = containers.Sum(armouryDifferential.StructCountFor);
                armouryDifferential.RecordOccupancyCount(premium ? "PremiumSaddleBag1-2" : "SaddleBag1-2", buddyStructCount, iconCount);
                break;
            default:
                armouryDifferential.RecordOccupancyCount(container, armouryDifferential.StructCountFor(container), iconCount);
                break;
        }
    }

    private static unsafe AtkResNode* ResolveFirstDragDropCell(AtkUnitBase* addon)
    {
        var cells = new List<(float X, float Y, nint Node)>();
        CollectDragDropCells(&addon->UldManager, cells);
        return cells.Count == 0
            ? null
            : (AtkResNode*)cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).First().Node;
    }

    private static unsafe int CountRenderedIconCells(AtkUnitBase* addon)
    {
        var count = 0;
        var manager = &addon->UldManager;
        CountIconCells(manager, ref count);
        return count;
    }

    private static unsafe void CountIconCells(AtkUldManager* manager, ref int count)
    {
        if (manager == null || manager->NodeList == null)
            return;
        for (var index = 0u; index < manager->NodeListCount; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !IsEffectivelyVisible(node))
                continue;
            var dragDrop = node->GetAsAtkComponentDragDrop();
            if (dragDrop != null && dragDrop->GetIconId() != 0)
                count++;
            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
                CountIconCells(&componentNode->Component->UldManager, ref count);
        }
    }

    /// <summary>
    /// Maps a rendered Character slot position to its EquippedItems container index.
    /// The container retains the legacy belt slot at index 5, so later slots shift by one.
    /// Rings are a symmetric pair for scanning purposes; the upper rendered ring maps to
    /// container index 11 and the lower to 12.
    /// </summary>
    private static int EquippedContainerIndex(string positionKey) => positionKey switch
    {
        "main-hand" => 0,
        "off-hand" => 1,
        "head" => 2,
        "body" => 3,
        "hands" => 4,
        "legs" => 6,
        "feet" => 7,
        "ears" => 8,
        "neck" => 9,
        "wrists" => 10,
        "ring-left" => 11,
        "ring-right" => 12,
        _ => -1,
    };

    private unsafe bool TryRequestEquipmentTooltip(RenderedEquipmentSlotTarget target, out string reason)
    {
        reason = string.Empty;
        var containerIndex = EquippedContainerIndex(target.PositionKey);
        if (containerIndex < 0)
        {
            reason = $"Rendered equipment slot {target.PositionKey} has no supported equipped-container index.";
            return false;
        }
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
        {
            reason = "The rendered Character addon is unavailable; the equipment tooltip request fails closed.";
            return false;
        }
        var node = ResolveCharacterNodeByPath(addon, target.NodePath);
        if (node == null || !IsEffectivelyVisible(node))
        {
            reason = $"The rendered node {target.NodePath} no longer resolves; the equipment tooltip request fails closed.";
            return false;
        }
        if (!Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.TryShowEquippedItemTooltip(
                addon->Id, node, (short)containerIndex))
        {
            reason = $"The game rejected the ItemDetail tooltip request for {target.PositionKey}; equipment observation fails closed.";
            return false;
        }
        return true;
    }

    private unsafe void HideEquipmentTooltip()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        if (addon == null)
            return;
        Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.HideTooltip(addon->Id);
    }

    private static unsafe AtkResNode* ResolveCharacterNodeByPath(AtkUnitBase* addon, string nodePath)
    {
        var segments = nodePath.Split('/');
        if (segments.Length < 2 || !string.Equals(segments[0], "Character", StringComparison.Ordinal))
            return null;
        var manager = &addon->UldManager;
        AtkResNode* current = null;
        for (var depth = 1; depth < segments.Length; depth++)
        {
            if (!uint.TryParse(segments[depth], out var nodeId) || manager == null || manager->NodeList == null)
                return null;
            current = null;
            for (var index = 0u; index < manager->NodeListCount; index++)
            {
                var candidate = manager->NodeList[index];
                if (candidate != null && candidate->NodeId == nodeId)
                {
                    current = candidate;
                    break;
                }
            }
            if (current == null)
                return null;
            if (depth + 1 < segments.Length)
            {
                var componentNode = current->GetAsAtkComponentNode();
                manager = componentNode != null && componentNode->Component != null
                    ? &componentNode->Component->UldManager
                    : null;
            }
        }
        return current;
    }

    private unsafe AgentBridgeRenderedAddonSnapshot CaptureAddon(string addonName)
        => CaptureAddon(addonName, gameGui.GetAddonByName<AtkUnitBase>(addonName, 1));

    private static unsafe AgentBridgeRenderedAddonSnapshot CaptureAddon(string addonName, AtkUnitBase* addon)
    {
        try
        {
            if (addon == null)
                return new(addonName, false, false, false, 0, []);

            var visible = addon->RootNode != null && addon->RootNode->IsVisible();
            var nodeCount = addon->UldManager.NodeListCount;
            var textNodes = new List<AgentBridgeRenderedTextNode>();
            var nodes = new List<AgentBridgeRenderedNodeSnapshot>();
            if (visible && addon->UldManager.NodeList != null)
                CaptureManager(&addon->UldManager, addonName, textNodes, nodes, new HashSet<nint>());

            return new(addonName, true, addon->IsReady, visible, nodeCount, textNodes, Nodes: nodes);
        }
        catch (Exception ex)
        {
            return new(addonName, true, false, false, 0, [], ex.Message);
        }
    }

    private static unsafe void CaptureManager(
        AtkUldManager* manager,
        string path,
        ICollection<AgentBridgeRenderedTextNode> textNodes,
        ICollection<AgentBridgeRenderedNodeSnapshot> nodes,
        ISet<nint> visitedManagers)
    {
        if (manager == null || manager->NodeList == null || textNodes.Count >= 512 ||
            !visitedManagers.Add((nint)manager))
            return;

        for (var index = 0u; index < manager->NodeListCount && textNodes.Count < 512; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !IsEffectivelyVisible(node))
                continue;

            var nodePath = $"{path}/{node->NodeId}";
            var componentNode = node->GetAsAtkComponentNode();
            ushort? componentType = null;
            if (componentNode != null && componentNode->Component != null)
            {
                componentType = (ushort)componentNode->Component->GetComponentType();
                CaptureManager(&componentNode->Component->UldManager, nodePath, textNodes, nodes, visitedManagers);
            }

            FFXIVClientStructs.FFXIV.Common.Math.Bounds bounds;
            node->GetBounds(&bounds);
            nodes.Add(new(
                nodePath,
                node->NodeId,
                (ushort)node->Type,
                componentType,
                bounds.Pos1.X,
                bounds.Pos1.Y,
                bounds.Pos2.X,
                bounds.Pos2.Y,
                (node->NodeFlags & NodeFlags.RespondToMouse) != 0,
                RegisteredEvents: GetRegisteredEvents(node)));

            var textNode = node->GetAsAtkTextNode();
            if (textNode != null)
            {
                var text = textNode->NodeText.ExtractText().Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var x = 0f;
                    var y = 0f;
                    node->GetPositionFloat(&x, &y);
                    textNodes.Add(new(
                        nodePath,
                        node->NodeId,
                        (ushort)node->Type,
                        text,
                        x,
                        y,
                        node->Width,
                        node->Height));
                }
            }

        }
    }

    private static unsafe IReadOnlyList<string>? GetRegisteredEvents(AtkResNode* node)
    {
        if ((node->NodeFlags & NodeFlags.RespondToMouse) == 0)
            return null;
        return Enum.GetValues<AtkEventType>()
            .Where(node->IsEventRegistered)
            .Select(value => value.ToString())
            .ToArray();
    }

    private static unsafe bool IsEffectivelyVisible(AtkResNode* node)
    {
        var current = node;
        for (var depth = 0; current != null && depth < 64; depth++, current = current->ParentNode)
        {
            if (!current->IsVisible())
                return false;
        }
        return current == null;
    }
}
