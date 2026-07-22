using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using ECommons.ExcelServices.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.Travel;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MarketMafioso.Squire.Observation;

internal sealed class DalamudExpertDeliveryPreparation
{
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TravelIdleFailureDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MovementTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InteractionTimeout = TimeSpan.FromSeconds(30);

    private readonly ICommandManager commandManager;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly LifestreamIpc lifestream;
    private readonly VNavmeshIpc vnavmesh;
    private readonly string showAllItemsText;
    private bool ownsSupplyUi;
    private bool ownsNavigation;
    public string Status { get; private set; } = "Expert Delivery preparation is idle.";
    public bool OwnsUi => ownsSupplyUi;

    public DalamudExpertDeliveryPreparation(
        ICommandManager commandManager,
        ICondition condition,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IGameGui gameGui,
        IFramework framework,
        IDataManager dataManager,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log)
    {
        this.commandManager = commandManager;
        this.condition = condition;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.gameGui = gameGui;
        this.framework = framework;
        this.dataManager = dataManager;
        showAllItemsText = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(4617).Text.ExtractText().Trim();
        lifestream = new LifestreamIpc(pluginInterface, log);
        vnavmesh = new VNavmeshIpc(new DalamudVNavmeshIpcAdapter(pluginInterface, log));
    }

    public async Task<SquireActionResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        Status = "Checking for an already-open Expert Delivery list.";
        if (await framework.RunOnTick(IsExpertDeliveryListReady).ConfigureAwait(false))
            return await EnsureShowAllItemsAsync(cancellationToken).ConfigureAwait(false);

        if (await framework.RunOnTick(IsSupplyListReady).ConfigureAwait(false))
            return await SelectExpertDeliveryTabAsync(cancellationToken).ConfigureAwait(false);

        if (condition[ConditionFlag.Unconscious])
            return SquireActionResult.Fail("PlayerUnconscious", "The character is unconscious, so Squire cannot begin Grand Company travel safely.");

        var company = await framework.RunOnTick(GetGrandCompany).ConfigureAwait(false);
        if (!TryGetOfficerDataId(company, out var officerDataId))
            return SquireActionResult.Fail("GrandCompanyUnavailable", "The active character is not employed by a Grand Company.");

        var officer = await framework.RunOnTick(() => FindOfficer(officerDataId)).ConfigureAwait(false);
        if (officer is null)
        {
            Status = $"Grand Company officer {officerDataId} is not loaded; requesting /li gc.";
            if (!lifestream.IsAvailable)
                return SquireActionResult.Fail("LifestreamUnavailable", "Lifestream is not loaded, so Squire could not travel to the Grand Company.");
            if (!commandManager.ProcessCommand("/li gc"))
                return SquireActionResult.Fail("LifestreamUnavailable", "Lifestream did not accept /li gc, so Squire could not travel to the Grand Company.");

            var ipcFailed = false;
            var travelEndedWithoutArrival = false;
            Stopwatch? idleStopwatch = null;
            var arrived = await WaitUntilAsync(
                () =>
                {
                    if (!lifestream.TryIsBusy(out var isBusy))
                    {
                        ipcFailed = true;
                        return false;
                    }
                    var observedOfficer = FindOfficer(officerDataId);
                    var observed = observedOfficer is null
                        ? "not loaded"
                        : $"distance X={observedOfficer.YalmDistanceX:0.0}, Z={observedOfficer.YalmDistanceZ:0.0}";
                    Status = $"Waiting for /li gc to finish: Lifestream busy={isBusy}; officer {observed}.";
                    if (isBusy)
                    {
                        idleStopwatch = null;
                        return false;
                    }
                    if (observedOfficer is not null)
                        return true;
                    idleStopwatch ??= Stopwatch.StartNew();
                    if (idleStopwatch.Elapsed < TravelIdleFailureDelay)
                        return false;
                    travelEndedWithoutArrival = true;
                    return true;
                },
                TravelTimeout,
                cancellationToken).ConfigureAwait(false);
            if (ipcFailed)
                return SquireActionResult.Fail("LifestreamStateUnavailable", "Lifestream accepted travel, but its busy-state IPC could not be observed safely.");
            if (travelEndedWithoutArrival)
                return SquireActionResult.Fail("GrandCompanyTravelFailed", "Lifestream stopped before the Grand Company delivery officer became available.");
            if (!arrived)
                return SquireActionResult.Fail("GrandCompanyTravelTimeout", "Lifestream did not finish where the Grand Company delivery officer could be discovered.");
            officer = await framework.RunOnTick(() => FindOfficer(officerDataId)).ConfigureAwait(false);
        }

        if (officer is null)
            return SquireActionResult.Fail("GrandCompanyOfficerUnavailable", "The Grand Company delivery officer was not loaded after travel.");
        if (!IsInInteractionRange(officer))
        {
            Status = $"Approaching Grand Company officer {officerDataId} with vnavmesh from X={officer.YalmDistanceX:0.0}, Z={officer.YalmDistanceZ:0.0}.";
            var move = vnavmesh.MoveCloseTo(officer.Position, 4f);
            if (!move.Success)
                return SquireActionResult.Fail("GrandCompanyNavigationUnavailable", move.Message.Replace("market board", "Grand Company officer", StringComparison.OrdinalIgnoreCase));
            ownsNavigation = true;
            var reached = await WaitUntilAsync(() =>
            {
                var current = FindOfficer(officerDataId);
                Status = current is null
                    ? "vnavmesh is approaching the Grand Company officer; the officer is temporarily not loaded."
                    : $"vnavmesh approach: running={vnavmesh.IsRunning}; officer distance X={current.YalmDistanceX:0.0}, Z={current.YalmDistanceZ:0.0}.";
                return current is not null && IsInInteractionRange(current) && !vnavmesh.IsRunning;
            }, MovementTimeout, cancellationToken).ConfigureAwait(false);
            ownsNavigation = false;
            if (!reached)
            {
                _ = vnavmesh.Stop();
                return SquireActionResult.Fail("GrandCompanyNavigationTimeout", "vnavmesh did not finish within interaction range of the Grand Company delivery officer.");
            }
        }

        Status = $"Interacting with Grand Company officer {officerDataId}.";
        var interactionStarted = await framework.RunOnTick(() => TryInteract(officerDataId)).ConfigureAwait(false);
        if (!interactionStarted)
            return SquireActionResult.Fail("GrandCompanyOfficerUnavailable", "The Grand Company delivery officer was present but could not be targeted and interacted with.");
        ownsSupplyUi = true;

        var supplyEntry = dataManager
            .GetExcelSheet<QuestDialogueText>(name: "custom/000/ComDefGrandCompanyOfficer_00073")
            .GetRow(69)
            .Value
            .ExtractText()
            .Trim();
        if (string.IsNullOrWhiteSpace(supplyEntry))
            return SquireActionResult.Fail("GrandCompanyMenuTextUnavailable", "The localized supply and provisioning menu text could not be resolved.");

        var menuSelected = await WaitUntilAsync(
            () =>
            {
                Status = "Waiting for the normal Supply and Provisioning menu.";
                return TrySelectSupplyEntry(supplyEntry) || IsSupplyListReady();
            },
            InteractionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!menuSelected)
            return SquireActionResult.Fail("GrandCompanyMenuTimeout", "The Grand Company supply and provisioning menu did not become ready.");

        return await SelectExpertDeliveryTabAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SquireActionResult> WaitForListReturnAsync(CancellationToken cancellationToken)
    {
        Status = "Waiting for the Expert Delivery list to return after confirmation.";
        var returned = await WaitUntilAsync(IsExpertDeliveryListReady, InteractionTimeout, cancellationToken).ConfigureAwait(false);
        if (!returned)
        {
            Status = "The Expert Delivery list did not return after confirmation.";
            return SquireActionResult.Fail(
                "ExpertDeliveryListReturnTimeout",
                "The confirmed item left its exact slot, but the Expert Delivery list did not return within 30 seconds.");
        }

        return await EnsureShowAllItemsAsync(cancellationToken).ConfigureAwait(false);
    }

    public unsafe void CloseOwnedUi()
    {
        if (ownsNavigation)
        {
            _ = vnavmesh.Stop();
            ownsNavigation = false;
        }
        if (!ownsSupplyUi)
            return;
        FirePresentedCloseCallbacks("GrandCompanySupplyList");
        FirePresentedCloseCallbacks("SelectString");
    }

    public unsafe void CloseVisibleUi()
    {
        FirePresentedCloseCallbacks("GrandCompanySupplyList");
        FirePresentedCloseCallbacks("SelectString");
        ownsSupplyUi = false;
    }

    public void CompleteOwnedUiClose() => ownsSupplyUi = false;

    public unsafe void RecoverDiagnosticUi()
    {
        FireAllCloseCallbacks("GrandCompanySupplyList");
        FireAllCloseCallbacks("SelectString");
        ownsSupplyUi = false;
    }

    private unsafe void FirePresentedCloseCallbacks(string addonName)
    {
        for (var index = 1; index <= 8; index++)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, index);
            if (addon != null && addon->RootNode != null && addon->RootNode->IsVisible())
                addon->FireCallbackInt(-1);
        }
    }

    private unsafe void FireAllCloseCallbacks(string addonName)
    {
        for (var index = 1; index <= 8; index++)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, index);
            if (addon != null)
                addon->FireCallbackInt(-1);
        }
    }

    private async Task<SquireActionResult> SelectExpertDeliveryTabAsync(CancellationToken cancellationToken)
    {
        Status = "Selecting the Expert Delivery tab in the normal Grand Company supply window.";
        var tabSelected = await WaitUntilAsync(TrySelectExpertDeliveryTab, InteractionTimeout, cancellationToken).ConfigureAwait(false);
        if (!tabSelected)
            return SquireActionResult.Fail("ExpertDeliveryTabTimeout", "The Expert Delivery tab did not become ready.");

        var ready = await WaitUntilAsync(IsExpertDeliveryListReady, InteractionTimeout, cancellationToken).ConfigureAwait(false);
        Status = ready ? "The Expert Delivery list is ready." : "The Expert Delivery list did not become ready.";
        if (!ready)
            return SquireActionResult.Fail("ExpertDeliveryListTimeout", "The Expert Delivery item list did not become ready after selecting its tab.");
        return await EnsureShowAllItemsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SquireActionResult> EnsureShowAllItemsAsync(CancellationToken cancellationToken)
    {
        Status = "Checking the Expert Delivery item-visibility filter.";
        var selectedFilter = await framework.RunOnTick(ReadExpertDeliveryFilter).ConfigureAwait(false);
        if (IsShowAllItemsLabel(selectedFilter))
        {
            Status = "The Expert Delivery list is ready with Show All Items selected.";
            return SquireActionResult.Completed("The Expert Delivery item list is ready with Show All Items selected.");
        }
        if (string.IsNullOrWhiteSpace(showAllItemsText))
            return SquireActionResult.Fail(
                "ExpertDeliveryShowAllTextUnavailable",
                "Squire could not resolve the localized Show All Items label and will not guess which Expert Delivery filter is selected.");
        if (string.IsNullOrWhiteSpace(selectedFilter))
            return SquireActionResult.Fail(
                "ExpertDeliveryFilterUnavailable",
                "Squire could not read the visible Expert Delivery item-filter label. Select Show All Items in the Expert Delivery window and retry.");

        Status = $"Switching the Expert Delivery item-visibility filter from '{selectedFilter}' to Show All Items.";
        var submitted = await framework.RunOnTick(TrySelectShowAllItems).ConfigureAwait(false);
        if (!submitted)
            return SquireActionResult.Fail(
                "ExpertDeliveryShowAllRejected",
                "The Expert Delivery window did not accept the Show All Items visibility setting. Select Show All Items manually and retry.");

        var ready = await WaitUntilAsync(IsShowAllExpertDeliveryListReady, InteractionTimeout, cancellationToken).ConfigureAwait(false);
        Status = ready
            ? "The Expert Delivery list is ready with Show All Items selected."
            : "The Expert Delivery list did not settle with Show All Items selected.";
        return ready
            ? SquireActionResult.Completed("The Expert Delivery item list is ready with Show All Items selected.")
            : SquireActionResult.Fail(
                "ExpertDeliveryShowAllTimeout",
                "Squire selected Show All Items, but the Expert Delivery list did not reload with that visibility setting. Select Show All Items manually and retry.");
    }

    private async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await framework.RunOnTick(predicate).ConfigureAwait(false))
                return true;
            await framework.DelayTicks(6).ConfigureAwait(false);
        }
        return false;
    }

    private static unsafe byte GetGrandCompany() =>
        FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance()->GrandCompany;

    private static bool TryGetOfficerDataId(byte company, out uint dataId)
    {
        dataId = company switch
        {
            1 => 1002388,
            2 => 1002394,
            3 => 1002391,
            _ => 0,
        };
        return dataId != 0;
    }

    private IGameObject? FindOfficer(uint dataId) => objectTable
        .Where(gameObject => gameObject.BaseId == dataId && gameObject.IsTargetable)
        .OrderBy(gameObject => gameObject.YalmDistanceX + gameObject.YalmDistanceZ)
        .FirstOrDefault();

    private bool IsOfficerInRange(uint dataId)
    {
        var officer = FindOfficer(dataId);
        return officer is not null && officer.YalmDistanceX <= 7 && officer.YalmDistanceZ <= 7;
    }

    private static bool IsInInteractionRange(IGameObject officer) => officer.YalmDistanceX <= 7 && officer.YalmDistanceZ <= 7;

    private unsafe bool TryInteract(uint dataId)
    {
        var officer = FindOfficer(dataId);
        var targetSystem = TargetSystem.Instance();
        if (officer is null || targetSystem is null || officer.YalmDistanceX > 7 || officer.YalmDistanceZ > 7)
            return false;

        targetManager.Target = officer;
        targetSystem->InteractWithObject((ClientGameObject*)officer.Address, false);
        return true;
    }

    private unsafe bool TrySelectSupplyEntry(string targetText)
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>("SelectString", 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return false;

        var popup = addon->PopupMenu.PopupMenu;
        for (var index = 0; index < popup.EntryCount; index++)
        {
            var text = popup.EntryNames[index].ToString();
            if (!Automation.Retainers.RetainerUiAutomationText.IsSelectStringEntryMatch(text, targetText))
                continue;
            addon->AtkUnitBase.FireCallbackInt(index);
            return true;
        }
        return false;
    }

    private unsafe bool IsSupplyListReady()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyList", 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private unsafe bool TrySelectExpertDeliveryTab()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyList", 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return false;
        if (IsExpertDeliveryListReady())
            return true;

        var node = addon->GetNodeById(13);
        if (node == null)
            return false;
        var button = node->GetAsAtkComponentRadioButton();
        if (button == null)
            return false;
        button->ClickRadioButton(addon);
        return true;
    }

    private unsafe bool IsExpertDeliveryListReady()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyList", 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible || addon->UldManager.NodeListCount <= 24)
            return false;
        var listNode = addon->UldManager.SearchNodeById(24);
        return listNode != null && listNode->IsVisible();
    }

    private unsafe string? ReadExpertDeliveryFilter()
    {
        // The rendered label is the UI truth. Do not infer the active option from
        // AddonGrandCompanySupplyList fields; those can be stale while the list reloads.
        var addon = gameGui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyList", 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return null;
        var filterNode = addon->UldManager.SearchNodeById(14);
        if (filterNode == null)
            return null;
        var filterComponentNode = filterNode->GetAsAtkComponentNode();
        if (filterComponentNode == null || filterComponentNode->Component == null ||
            filterComponentNode->Component->UldManager.NodeListCount <= 1)
            return null;
        var labelComponentRoot = filterComponentNode->Component->UldManager.NodeList[1];
        var labelComponentNode = labelComponentRoot == null ? null : labelComponentRoot->GetAsAtkComponentNode();
        if (labelComponentNode == null || labelComponentNode->Component == null ||
            labelComponentNode->Component->UldManager.NodeListCount <= 2)
            return null;
        var labelRoot = labelComponentNode->Component->UldManager.NodeList[2];
        var labelNode = labelRoot == null ? null : labelRoot->GetAsAtkTextNode();
        return labelNode == null ? null : labelNode->NodeText.ExtractText().Trim();
    }

    private unsafe bool TrySelectShowAllItems()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyList", 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return false;
        if (IsShowAllItemsLabel(ReadExpertDeliveryFilter()))
            return true;

        var values = stackalloc AtkValue[3];
        values[0] = new() { Type = AtkValueType.Int, Int = 5 };
        values[1] = new() { Type = AtkValueType.Int, Int = 0 };
        values[2] = new() { Type = AtkValueType.Int, Int = 0 };
        return addon->FireCallback(3, values, true);
    }

    private bool IsShowAllExpertDeliveryListReady() =>
        IsExpertDeliveryListReady() && IsShowAllItemsLabel(ReadExpertDeliveryFilter());

    private bool IsShowAllItemsLabel(string? observed) =>
        !string.IsNullOrWhiteSpace(showAllItemsText) &&
        !string.IsNullOrWhiteSpace(observed) &&
        Automation.Retainers.RetainerUiAutomationText.IsSelectStringEntryMatch(observed, showAllItemsText);

}
