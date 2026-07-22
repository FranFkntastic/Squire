using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.Automation.MarketBoard;

public sealed class MarketBoardInputCaptureReader
{
    private static readonly string[] AddonsToCapture =
    [
        "ItemSearch",
        "ItemSearchResult",
        "SelectYesno",
        "ContextMenu",
        "InputNumeric",
    ];

    private readonly IGameGui gameGui;

    public MarketBoardInputCaptureReader(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public unsafe MarketBoardInputCapture Capture()
    {
        var details = new Dictionary<string, string?>();
        foreach (var addonName in AddonsToCapture)
            AddAddonState(details, addonName);

        AddItemSearchDetails(details);
        AddInfoProxyDetails(details);
        AddAgentDetails(details, AgentItemSearch.Instance());
        AddStageDetails(details);

        return new MarketBoardInputCapture
        {
            Status = "Captured",
            Message = "Captured current market board UI/input state.",
            Details = details,
        };
    }

    private unsafe void AddAddonState(IDictionary<string, string?> details, string addonName)
    {
        var key = ToDetailPrefix(addonName);
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        details[$"{key}Available"] = (addon != null).ToString();
        details[$"{key}Ready"] = (addon != null && addon->IsReady).ToString();
        details[$"{key}Visible"] = (addon != null && addon->IsVisible).ToString();
        if (addon == null)
            return;

        details[$"{key}FocusNode"] = FormatNode(addon->FocusNode);
        details[$"{key}ComponentFocusNode"] = FormatNode(addon->ComponentFocusNode);
    }

    private unsafe void AddItemSearchDetails(IDictionary<string, string?> details)
    {
        var addon = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch", 1);
        if (addon == null)
            return;

        details["itemSearchMode"] = FormatMode((uint)addon->Mode);
        details["itemSearchModeRaw"] = ((uint)addon->Mode).ToString(CultureInfo.InvariantCulture);
        details["itemSearchPartialMatch"] = addon->PartialMatch.ToString();
        details["itemSearchText"] = addon->SearchText.ToString();
        details["itemSearchText2"] = addon->SearchText2.ToString();
        details["itemSearchSearchButtonAvailable"] = (addon->SearchButton != null).ToString();
        details["itemSearchSearchButtonEnabled"] = (addon->SearchButton != null && addon->SearchButton->IsEnabled).ToString();
        details["itemSearchTextInputAvailable"] = (addon->SearchTextInput != null).ToString();
        details["itemSearchResultsListAvailable"] = (addon->ResultsList != null).ToString();
        details["itemSearchResultsListItemCount"] = addon->ResultsList == null
            ? null
            : addon->ResultsList->GetItemCount().ToString(CultureInfo.InvariantCulture);

        var input = addon->SearchTextInput;
        if (input == null)
            return;

        var inputBase = &input->AtkComponentInputBase;
        details["itemSearchTextInputIsActive"] = inputBase->IsActive.ToString();
        details["itemSearchTextInputCursorPos"] = inputBase->CursorPos.ToString(CultureInfo.InvariantCulture);
        details["itemSearchTextInputSelectionStart"] = inputBase->SelectionStart.ToString(CultureInfo.InvariantCulture);
        details["itemSearchTextInputSelectionEnd"] = inputBase->SelectionEnd.ToString(CultureInfo.InvariantCulture);
        details["itemSearchTextInputCallbackAvailable"] = (inputBase->Callback != null).ToString();
        details["itemSearchTextInputCallbackEventKind"] = inputBase->CallbackEventKind.ToString(CultureInfo.InvariantCulture);
        details["itemSearchTextInputRawString"] = inputBase->RawString.ToString();
        details["itemSearchTextInputEvaluatedString"] = inputBase->EvaluatedString.ToString();
        details["itemSearchTextInputFocusNode"] = FormatNode(inputBase->AtkComponentBase.GetFocusNode());
        details["itemSearchTextInputOwnerNode"] = inputBase->AtkComponentBase.OwnerNode == null
            ? "null"
            : FormatNode(&inputBase->AtkComponentBase.OwnerNode->AtkResNode);
        details["itemSearchTextInputCollisionNode"] = inputBase->CollisionNode == null
            ? "null"
            : FormatNode(&inputBase->CollisionNode->AtkResNode);
    }

    private static unsafe void AddInfoProxyDetails(IDictionary<string, string?> details)
    {
        var infoProxy = InfoProxyItemSearch.Instance();
        details["infoProxyItemSearchAvailable"] = (infoProxy != null).ToString();
        if (infoProxy == null)
            return;

        var reportedListingCount = (int)infoProxy->ListingCount;
        var listingCapacity = infoProxy->Listings.Length;
        var listingCount = Math.Min(reportedListingCount, listingCapacity);
        details["infoProxySearchItemId"] = infoProxy->SearchItemId.ToString(CultureInfo.InvariantCulture);
        details["infoProxyEntryCount"] = infoProxy->InfoProxyPageInterface.InfoProxyInterface.EntryCount.ToString(CultureInfo.InvariantCulture);
        details["infoProxyCurrentRequestId"] = infoProxy->InfoProxyPageInterface.CurrentRequestId.ToString(CultureInfo.InvariantCulture);
        details["infoProxyNextRequestId"] = infoProxy->InfoProxyPageInterface.NextRequestId.ToString(CultureInfo.InvariantCulture);
        details["infoProxyListingCount"] = listingCount.ToString(CultureInfo.InvariantCulture);
        details["infoProxyReportedListingCount"] = reportedListingCount.ToString(CultureInfo.InvariantCulture);
        details["infoProxyListingCapacity"] = listingCapacity.ToString(CultureInfo.InvariantCulture);
        details["infoProxyListingCountTruncated"] = (reportedListingCount > listingCount).ToString();
        details["infoProxyWaitingForListings"] = infoProxy->WaitingForListings.ToString();
        details["infoProxyListingPreview"] = FormatListingPreview(infoProxy, listingCount);
    }

    private static unsafe void AddAgentDetails(IDictionary<string, string?> details, AgentItemSearch* agent)
    {
        details["agentItemSearchAvailable"] = (agent != null).ToString();
        if (agent == null)
            return;

        var itemCount = Math.Min((int)agent->ItemCount, 100);
        details["agentItemSearchItemCount"] = itemCount.ToString(CultureInfo.InvariantCulture);
        details["agentItemSearchIsPartialSearching"] = agent->IsPartialSearching.ToString();
        details["agentItemSearchIsItemPushPending"] = agent->IsItemPushPending.ToString();
        details["agentItemSearchResultItemId"] = agent->ResultItemId.ToString(CultureInfo.InvariantCulture);
        details["agentItemSearchResultSelectedIndex"] = agent->ResultSelectedIndex.ToString(CultureInfo.InvariantCulture);
        details["agentItemSearchItemPreview"] = FormatAgentItemPreview(agent, itemCount);
    }

    private static unsafe void AddStageDetails(IDictionary<string, string?> details)
    {
        var stage = AtkStage.Instance();
        details["stageAvailable"] = (stage != null).ToString();
        if (stage == null)
            return;

        details["stageFocus"] = FormatNode(stage->GetFocus());
        details["stageInputManagerAvailable"] = (stage->AtkInputManager != null).ToString();
        if (stage->AtkInputManager == null)
            return;

        details["stageInputManagerFocusedNode"] = FormatNode(stage->AtkInputManager->FocusedNode);
        details["stageInputManagerTextInput"] = FormatPointer(stage->AtkInputManager->TextInput);
    }

    private static unsafe string FormatListingPreview(InfoProxyItemSearch* infoProxy, int listingCount)
    {
        if (infoProxy == null || listingCount <= 0)
            return string.Empty;

        var previewCount = Math.Min(listingCount, 6);
        var values = new string[previewCount];
        for (var index = 0; index < previewCount; index++)
        {
            var listing = infoProxy->Listings[index];
            values[index] = string.Create(
                CultureInfo.InvariantCulture,
                $"{listing.ListingId}/{listing.ItemId}/{listing.UnitPrice}/{listing.Quantity}/{(listing.IsHqItem ? "HQ" : "NQ")}");
        }

        return string.Join(",", values);
    }

    private static unsafe string FormatAgentItemPreview(AgentItemSearch* agent, int itemCount)
    {
        if (agent == null || itemCount <= 0)
            return string.Empty;

        var previewCount = Math.Min(itemCount, 8);
        var values = new string[previewCount];
        for (var index = 0; index < previewCount; index++)
            values[index] = agent->ItemBuffer[index].ToString(CultureInfo.InvariantCulture);

        return string.Join(",", values);
    }

    private static unsafe string FormatNode(AtkResNode* node)
    {
        return node == null
            ? "null"
            : $"0x{FormatPointerValue(node)}#{node->NodeId.ToString(CultureInfo.InvariantCulture)}";
    }

    private static unsafe string FormatPointer(void* pointer)
    {
        return pointer == null ? "null" : $"0x{FormatPointerValue(pointer)}";
    }

    private static unsafe string FormatPointerValue(void* pointer)
    {
        return ((nuint)pointer).ToString("X", CultureInfo.InvariantCulture);
    }

    private static string FormatMode(uint mode)
    {
        return Enum.IsDefined(typeof(AddonItemSearch.SearchMode), mode)
            ? ((AddonItemSearch.SearchMode)mode).ToString()
            : mode.ToString(CultureInfo.InvariantCulture);
    }

    private static string ToDetailPrefix(string addonName)
    {
        if (string.IsNullOrWhiteSpace(addonName))
            return string.Empty;

        return char.ToLowerInvariant(addonName[0]) + addonName[1..];
    }
}
