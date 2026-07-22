using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Plugin.Services;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.Automation.MarketBoard;

public sealed class MarketBoardItemSearchDriver
{
    private const string ItemSearchAddon = "ItemSearch";
    private const string ItemSearchResultAddon = "ItemSearchResult";

    private readonly IGameGui gameGui;

    public MarketBoardItemSearchDriver(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public unsafe MarketBoardItemSearchResult Search(uint itemId, string? itemName)
    {
        if (itemId == 0)
            throw new InvalidOperationException("Item id is required before searching the market board.");

        if (string.IsNullOrWhiteSpace(itemName))
            throw new InvalidOperationException($"Item name is required before searching the market board for item {itemId}.");

        var itemSearchResult = gameGui.GetAddonByName<AddonItemSearchResult>(ItemSearchResultAddon, 1);
        if (itemSearchResult != null && IsAddonReady(&itemSearchResult->AtkUnitBase))
        {
            var infoProxy = InfoProxyItemSearch.Instance();
            var openResultItemId = infoProxy == null ? 0 : infoProxy->SearchItemId;
            if (IsOpenListingResultForRequestedItem(itemId, openResultItemId))
            {
                return new MarketBoardItemSearchResult
                {
                    Status = "ListingsReady",
                    Message = $"Market board listings are open for {itemName.Trim()} ({itemId}).",
                    Details = new Dictionary<string, string?>
                    {
                        ["itemSearchResultVisible"] = true.ToString(),
                        ["openResultItemId"] = openResultItemId.ToString(CultureInfo.InvariantCulture),
                    },
                };
            }

            itemSearchResult->AtkUnitBase.Close(true);
            return new MarketBoardItemSearchResult
            {
                Status = "StaleListingsClosed",
                Message = $"Closed stale market board listings for item {openResultItemId}; preparing to search {itemName.Trim()} ({itemId}).",
                Details = new Dictionary<string, string?>
                {
                    ["itemSearchResultVisible"] = true.ToString(),
                    ["openResultItemId"] = openResultItemId.ToString(CultureInfo.InvariantCulture),
                    ["requestedItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        var addon = gameGui.GetAddonByName<AddonItemSearch>(ItemSearchAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
        {
            return new MarketBoardItemSearchResult
            {
                Status = "MarketBoardNotOpen",
                Message = "Waiting for the market board item search window to open.",
            };
        }

        var searchText = itemName.Trim();
        var mode = (uint)addon->Mode;
        var partialSearchWasEnabled = addon->PartialMatch;
        var agent = AgentItemSearch.Instance();
        var details = new Dictionary<string, string?>
        {
            ["mode"] = FormatMode(mode),
            ["modeRaw"] = mode.ToString(),
            ["partialSearchBefore"] = partialSearchWasEnabled.ToString(),
            ["itemSearchResultVisible"] = false.ToString(),
        };

        if (ChooseAction(mode) == MarketBoardItemSearchAction.ResetMode)
        {
            addon->SetModeFilter(AddonItemSearch.SearchMode.Normal, 0);
            return new MarketBoardItemSearchResult
            {
                Status = "ModeReset",
                Message = $"Resetting market board item search mode from {FormatMode(mode)} before searching for {searchText} ({itemId}).",
                Details = details,
            };
        }

        if (ShouldDisablePartialSearch(partialSearchWasEnabled))
        {
            addon->PartialMatch = false;
            if (addon->PartialSearchCheckBox != null)
                addon->PartialSearchCheckBox->AtkComponentButton.SetChecked(false);
        }

        AddAgentDetails(details, agent);
        if (TryOpenExactItemResult(addon, agent, itemId, details))
        {
            return new MarketBoardItemSearchResult
            {
                Status = "ItemOpenSent",
                Message = $"Opening exact market board item result for {searchText} ({itemId}); waiting for market listings.",
                Details = details,
            };
        }

        if (IsSearchAgentStillWorking(agent))
        {
            var agentIsPartialSearching = agent != null && agent->IsPartialSearching;
            var agentIsItemPushPending = agent != null && agent->IsItemPushPending;
            details["searchAgentStillWorking"] = true.ToString();
            details["searchAgentExactItemVisible"] = AgentContainsItem(agent, itemId).ToString();
            details["submitAgentIsPartialSearching"] = agentIsPartialSearching.ToString();
            details["submitAgentIsItemPushPending"] = agentIsItemPushPending.ToString();
            details["searchSource"] = "AgentPredicate";
            details["partialSearchAfter"] = addon->PartialMatch.ToString();

            return new MarketBoardItemSearchResult
            {
                Status = "SearchSent",
                Message = $"Waiting for market board item search results for {searchText} ({itemId}).",
                Details = details,
            };
        }

        if (!TrySubmitSearchWithTextInputEnter(addon, agent, itemId, searchText, details))
        {
            return new MarketBoardItemSearchResult
            {
                Status = "SearchSubmitFailed",
                Message = $"Could not submit market board item search for {searchText} ({itemId}); see diagnostics.",
                Details = details,
            };
        }

        details["partialSearchAfter"] = addon->PartialMatch.ToString();
        AddAgentDetails(details, agent);
        details["searchAgentStillWorking"] = false.ToString();

        return new MarketBoardItemSearchResult
        {
            Status = "SearchSent",
            Message = $"Searching market board item list for {searchText} ({itemId}).",
            Details = details,
        };
    }

    public unsafe MarketBoardItemSearchResult Observe(uint itemId, string? itemName)
    {
        if (itemId == 0)
            throw new InvalidOperationException("Item id is required before observing the market board search state.");

        if (string.IsNullOrWhiteSpace(itemName))
            throw new InvalidOperationException($"Item name is required before observing the market board search state for item {itemId}.");

        var searchText = itemName.Trim();
        var itemSearchResult = gameGui.GetAddonByName<AtkUnitBase>(ItemSearchResultAddon, 1);
        if (IsAddonReady(itemSearchResult))
        {
            return new MarketBoardItemSearchResult
            {
                Status = "ListingsReady",
                Message = $"Market board listings are open for {searchText} ({itemId}).",
                Details = new Dictionary<string, string?>
                {
                    ["itemSearchResultVisible"] = true.ToString(),
                },
            };
        }

        var addon = gameGui.GetAddonByName<AddonItemSearch>(ItemSearchAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
        {
            return new MarketBoardItemSearchResult
            {
                Status = "MarketBoardNotOpen",
                Message = "Waiting for the market board item search window to open.",
            };
        }

        var mode = (uint)addon->Mode;
        var agent = AgentItemSearch.Instance();
        var resultListItemCount = addon->ResultsList == null ? 0 : addon->ResultsList->GetItemCount();
        var details = new Dictionary<string, string?>
        {
            ["mode"] = FormatMode(mode),
            ["modeRaw"] = mode.ToString(),
            ["partialSearch"] = addon->PartialMatch.ToString(),
            ["searchButtonAvailable"] = (addon->SearchButton != null).ToString(),
            ["searchTextInputAvailable"] = (addon->SearchTextInput != null).ToString(),
            ["resultsListAvailable"] = (addon->ResultsList != null).ToString(),
            ["resultListItemCount"] = resultListItemCount.ToString(),
            ["itemSearchResultVisible"] = false.ToString(),
        };
        AddAgentDetails(details, agent);

        var exactItemVisible = AgentContainsItem(agent, itemId);
        details["exactItemVisibleInAgent"] = exactItemVisible.ToString();
        if (resultListItemCount > 0 || exactItemVisible)
        {
            return new MarketBoardItemSearchResult
            {
                Status = "ItemResultsReady",
                Message = $"Market board item search results are visible for {searchText} ({itemId}); select the exact item result.",
                Details = details,
            };
        }

        return new MarketBoardItemSearchResult
        {
            Status = "AwaitingManualSearch",
            Message = $"Waiting for manual market board search for {searchText} ({itemId}).",
            Details = details,
        };
    }

    internal static bool ShouldResetToNormalSearch(uint mode)
    {
        return mode != (uint)AddonItemSearch.SearchMode.Normal;
    }

    internal static MarketBoardItemSearchAction ChooseAction(uint mode)
    {
        return ShouldResetToNormalSearch(mode)
            ? MarketBoardItemSearchAction.ResetMode
            : MarketBoardItemSearchAction.SubmitSearch;
    }

    internal static bool ShouldDisablePartialSearch(bool partialSearchEnabled)
    {
        return false;
    }

    internal static unsafe bool IsSearchAgentStillWorking(AgentItemSearch* agent)
    {
        return agent != null && (agent->IsPartialSearching || agent->IsItemPushPending);
    }

    internal static bool IsOpenListingResultForRequestedItem(uint requestedItemId, uint openResultItemId)
    {
        return requestedItemId != 0 && openResultItemId == requestedItemId;
    }

    internal static IReadOnlyList<MarketBoardItemSearchSubmitCallback> GetSearchSubmitCallbackSequence()
    {
        return
        [
            MarketBoardItemSearchSubmitCallback.TextChanged,
            MarketBoardItemSearchSubmitCallback.Enter,
        ];
    }

    internal static MarketBoardItemSearchSubmitStrategy ChooseSearchSubmitStrategy(
        bool textInputWasActive,
        bool searchButtonWasEnabled,
        bool exactItemVisible,
        bool agentIsPartialSearching,
        bool agentIsItemPushPending)
    {
        if (textInputWasActive
            && !searchButtonWasEnabled
            && !exactItemVisible
            && !agentIsPartialSearching
            && !agentIsItemPushPending)
        {
            return MarketBoardItemSearchSubmitStrategy.AutofocusedTextInputRewrite;
        }

        return MarketBoardItemSearchSubmitStrategy.TextInputEnterCallback;
    }

    internal static IReadOnlyList<MarketBoardItemSearchSubmitStep> GetAutofocusedSubmitStepSequence()
    {
        return
        [
            MarketBoardItemSearchSubmitStep.ClearSearchText,
            MarketBoardItemSearchSubmitStep.TextChanged,
            MarketBoardItemSearchSubmitStep.SetSearchText,
            MarketBoardItemSearchSubmitStep.TextChanged,
            MarketBoardItemSearchSubmitStep.Enter,
        ];
    }

    internal static bool ShouldMirrorSubmitTextToAddonSearchStrings(MarketBoardItemSearchSubmitStrategy strategy)
    {
        return strategy switch
        {
            MarketBoardItemSearchSubmitStrategy.TextInputEnterCallback => false,
            MarketBoardItemSearchSubmitStrategy.AutofocusedTextInputRewrite => false,
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null),
        };
    }

    internal static bool IsSearchSubmitAccepted(
        bool exactItemVisible,
        bool agentIsPartialSearching,
        bool agentIsItemPushPending,
        bool searchButtonClickSent,
        bool itemSearchResultVisible)
    {
        return AtkTextInputAutomation.IsSubmitAccepted(new UiTextInputSubmitEvidence(
            TargetVisible: exactItemVisible,
            ResultVisible: itemSearchResultVisible,
            WorkInProgress: agentIsPartialSearching || agentIsItemPushPending,
            ActivationSent: searchButtonClickSent));
    }

    internal static IReadOnlyList<MarketBoardItemSearchResultActivationEvent> GetResultActivationEventSequence()
    {
        return
        [
            MarketBoardItemSearchResultActivationEvent.ListItemClick,
            MarketBoardItemSearchResultActivationEvent.ListItemDoubleClick,
        ];
    }

    internal static MarketBoardItemSearchFocusTarget ChooseTextInputFocusTarget(bool hasCollisionNode, bool hasOwnerNode)
    {
        if (hasCollisionNode)
            return MarketBoardItemSearchFocusTarget.CollisionNode;

        return hasOwnerNode
            ? MarketBoardItemSearchFocusTarget.OwnerNode
            : MarketBoardItemSearchFocusTarget.None;
    }

    private static unsafe bool IsAddonReady(AtkUnitBase* addon)
    {
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private unsafe bool TrySubmitSearchWithTextInputEnter(
        AddonItemSearch* addon,
        AgentItemSearch* agent,
        uint itemId,
        string searchText,
        IDictionary<string, string?> details)
    {
        var input = addon->SearchTextInput;
        details["searchTextInputAvailable"] = (input != null).ToString();
        if (input == null)
        {
            details["searchSubmitStatus"] = "TextInputUnavailable";
            return false;
        }

        var inputBase = &input->AtkComponentInputBase;
        var ownerNode = inputBase->AtkComponentBase.OwnerNode;
        var collisionNode = inputBase->CollisionNode;
        var focusTargetKind = ChooseTextInputFocusTarget(collisionNode != null, ownerNode != null);
        var focusNode = focusTargetKind switch
        {
            MarketBoardItemSearchFocusTarget.CollisionNode => &collisionNode->AtkResNode,
            MarketBoardItemSearchFocusTarget.OwnerNode => &ownerNode->AtkResNode,
            _ => null,
        };
        var textInputWasActive = inputBase->IsActive;
        var searchButtonWasEnabled = addon->SearchButton != null && addon->SearchButton->IsEnabled;
        var exactItemVisible = AgentContainsItem(agent, itemId);
        var agentIsPartialSearching = agent != null && agent->IsPartialSearching;
        var agentIsItemPushPending = agent != null && agent->IsItemPushPending;
        var submitStrategy = ChooseSearchSubmitStrategy(
            textInputWasActive,
            searchButtonWasEnabled,
            exactItemVisible,
            agentIsPartialSearching,
            agentIsItemPushPending);
        details["searchSource"] = submitStrategy.ToString();
        details["submitExactItemVisibleBefore"] = exactItemVisible.ToString();
        details["submitAgentIsPartialSearchingBefore"] = agentIsPartialSearching.ToString();
        details["submitAgentIsItemPushPendingBefore"] = agentIsItemPushPending.ToString();
        details["textInputCallbackAvailable"] = (inputBase->Callback != null).ToString();
        details["textInputCallbackEventKind"] = inputBase->CallbackEventKind.ToString();
        details["textInputWasActive"] = textInputWasActive.ToString();
        details["searchButtonWasEnabled"] = searchButtonWasEnabled.ToString();
        details["textInputFocusTarget"] = focusTargetKind.ToString();
        details["textInputFocusTargetNode"] = FormatNode(focusNode);
        if (inputBase->Callback == null)
        {
            details["searchSubmitStatus"] = "TextInputCallbackUnavailable";
            return false;
        }

        var focusResult = AtkTextInputAutomation.FocusTextInput(&addon->AtkUnitBase, inputBase, focusNode);
        inputBase->SelectionStart = searchText.Length;
        inputBase->SelectionEnd = searchText.Length;
        inputBase->CursorPos = searchText.Length;
        details["textInputFocusSet"] = focusResult.FocusSet.ToString();
        details["textInputIsActiveAfterFocus"] = focusResult.IsActive.ToString();

        var callbackResults = new List<string>();
        var searchButtonEnabledBeforeEnter = addon->SearchButton != null && addon->SearchButton->IsEnabled;
        if (submitStrategy == MarketBoardItemSearchSubmitStrategy.AutofocusedTextInputRewrite)
        {
            var submitSteps = GetAutofocusedSubmitStepSequence();
            foreach (var step in submitSteps)
            {
                switch (step)
                {
                    case MarketBoardItemSearchSubmitStep.ClearSearchText:
                        SetSearchInputText(
                            addon,
                            input,
                            inputBase,
                            string.Empty,
                            updateAddonSearchStrings: true);
                        callbackResults.Add(step.ToString());
                        break;
                    case MarketBoardItemSearchSubmitStep.SetSearchText:
                        SetSearchInputText(
                            addon,
                            input,
                            inputBase,
                            searchText,
                            updateAddonSearchStrings: ShouldMirrorSubmitTextToAddonSearchStrings(submitStrategy));
                        callbackResults.Add(step.ToString());
                        break;
                    case MarketBoardItemSearchSubmitStep.TextChanged:
                        callbackResults.Add(InvokeInputCallback(addon, inputBase, MarketBoardItemSearchSubmitCallback.TextChanged));
                        searchButtonEnabledBeforeEnter = addon->SearchButton != null && addon->SearchButton->IsEnabled;
                        break;
                    case MarketBoardItemSearchSubmitStep.Enter:
                        callbackResults.Add(InvokeInputCallback(addon, inputBase, MarketBoardItemSearchSubmitCallback.Enter));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(step), step, null);
                }
            }

            details["textInputCallbackSequence"] = string.Join(",", submitSteps);
        }
        else
        {
            SetSearchInputText(
                addon,
                input,
                inputBase,
                searchText,
                updateAddonSearchStrings: ShouldMirrorSubmitTextToAddonSearchStrings(submitStrategy));
            foreach (var callback in GetSearchSubmitCallbackSequence())
                callbackResults.Add(InvokeInputCallback(addon, inputBase, callback));

            details["textInputCallbackSequence"] = string.Join(",", GetSearchSubmitCallbackSequence());
        }

        details["textInputCallbackResults"] = string.Join(",", callbackResults);
        var searchButtonEnabledAfterCallbacks = addon->SearchButton != null && addon->SearchButton->IsEnabled;
        var searchButtonClickSent = false;
        if (searchButtonEnabledAfterCallbacks)
            searchButtonClickSent = TryClickSearchButton(addon, details);

        var itemSearchResultVisible = IsAddonReady(gameGui.GetAddonByName<AtkUnitBase>(ItemSearchResultAddon, 1));
        exactItemVisible = AgentContainsItem(agent, itemId);
        agentIsPartialSearching = agent != null && agent->IsPartialSearching;
        agentIsItemPushPending = agent != null && agent->IsItemPushPending;
        details["searchButtonEnabledBeforeEnter"] = searchButtonEnabledBeforeEnter.ToString();
        details["searchButtonEnabledAfterCallbacks"] = searchButtonEnabledAfterCallbacks.ToString();
        details["searchButtonClickSent"] = searchButtonClickSent.ToString();
        details["submitExactItemVisibleAfter"] = exactItemVisible.ToString();
        details["submitAgentIsPartialSearchingAfter"] = agentIsPartialSearching.ToString();
        details["submitAgentIsItemPushPendingAfter"] = agentIsItemPushPending.ToString();
        details["submitItemSearchResultVisibleAfter"] = itemSearchResultVisible.ToString();
        var submitAccepted = IsSearchSubmitAccepted(
            exactItemVisible,
            agentIsPartialSearching,
            agentIsItemPushPending,
            searchButtonClickSent,
            itemSearchResultVisible);
        details["searchSubmitAccepted"] = submitAccepted.ToString();
        details["searchSubmitStatus"] = submitAccepted ? "Submitted" : "InputRejected";
        return submitAccepted;
    }

    private static unsafe void SetSearchInputText(
        AddonItemSearch* addon,
        AtkComponentTextInput* input,
        AtkComponentInputBase* inputBase,
        string text,
        bool updateAddonSearchStrings)
    {
        if (updateAddonSearchStrings)
        {
            addon->SearchText.SetString(text);
            addon->SearchText2.SetString(text);
        }

        AtkTextInputAutomation.SetEditableText(input, inputBase, text);
    }

    private static unsafe bool TryClickSearchButton(
        AddonItemSearch* addon,
        IDictionary<string, string?> details)
    {
        if (addon->SearchButton == null)
        {
            details["searchButtonClickStatus"] = "Unavailable";
            return false;
        }

        if (!addon->SearchButton->IsEnabled)
        {
            details["searchButtonClickStatus"] = "Disabled";
            return false;
        }

        try
        {
            (*addon->SearchButton).ClickAddonButton(&addon->AtkUnitBase);
            details["searchButtonClickStatus"] = "Clicked";
            return true;
        }
        catch (Exception ex)
        {
            details["searchButtonClickStatus"] = "Failed";
            details["searchButtonClickException"] = ex.GetType().Name;
            details["searchButtonClickMessage"] = ex.Message;
            return false;
        }
    }

    private static unsafe string InvokeInputCallback(
        AddonItemSearch* addon,
        AtkComponentInputBase* inputBase,
        MarketBoardItemSearchSubmitCallback callback)
    {
        return callback switch
        {
            MarketBoardItemSearchSubmitCallback.TextChanged =>
                AtkTextInputAutomation.InvokeCallback(&addon->AtkUnitBase, inputBase, UiTextInputCallbackKind.TextChanged),
            MarketBoardItemSearchSubmitCallback.Enter =>
                AtkTextInputAutomation.InvokeCallback(&addon->AtkUnitBase, inputBase, UiTextInputCallbackKind.Enter),
            _ => throw new ArgumentOutOfRangeException(nameof(callback), callback, null),
        };
    }

    private static unsafe bool TryOpenExactItemResult(
        AddonItemSearch* addon,
        AgentItemSearch* agent,
        uint itemId,
        IDictionary<string, string?> details)
    {
        if (agent == null)
        {
            details["itemResultSelectStatus"] = "AgentUnavailable";
            return false;
        }

        var itemCount = Math.Min((int)agent->ItemCount, 100);
        if (itemCount <= 0)
        {
            details["itemResultSelectStatus"] = "NoAgentItems";
            return false;
        }

        for (var index = 0; index < itemCount; index++)
        {
            var candidateItemId = agent->ItemBuffer[index];
            if (candidateItemId != itemId)
                continue;

            details["itemResultSelectStatus"] = "ExactMatch";
            details["itemResultIndex"] = index.ToString();
            details["itemResultId"] = candidateItemId.ToString();
            details["resultListItemCount"] = addon->ResultsList == null
                ? null
                : addon->ResultsList->GetItemCount().ToString();

            if (addon->ResultsList == null)
            {
                details["itemResultSelectStatus"] = "ResultsListUnavailable";
                return false;
            }

            details["listIsItemInteractionEnabled"] = addon->ResultsList->IsItemInteractionEnabled.ToString();
            details["listIsItemClickEnabled"] = addon->ResultsList->IsItemClickEnabled.ToString();
            details["listSelectedItemIndexBefore"] = addon->ResultsList->SelectedItemIndex.ToString(CultureInfo.InvariantCulture);
            details["agentResultSelectedIndexBefore"] = agent->ResultSelectedIndex.ToString(CultureInfo.InvariantCulture);

            addon->ResultsList->SelectItem(index, true);
            foreach (var activationEvent in GetResultActivationEventSequence())
                addon->ResultsList->DispatchItemEvent(index, ToAtkEventType(activationEvent));

            details["itemOpenSource"] = "ResultListActivationSequence";
            details["itemOpenEventSequence"] = string.Join(",", GetResultActivationEventSequence());
            details["listSelectedItemIndexAfter"] = addon->ResultsList->SelectedItemIndex.ToString(CultureInfo.InvariantCulture);
            details["agentResultSelectedIndexAfter"] = agent->ResultSelectedIndex.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        details["itemResultSelectStatus"] = "ExactItemNotFound";
        details["agentItemPreview"] = FormatAgentItemPreview(agent, itemCount);
        return false;
    }

    private static AtkEventType ToAtkEventType(MarketBoardItemSearchResultActivationEvent activationEvent)
    {
        return activationEvent switch
        {
            MarketBoardItemSearchResultActivationEvent.ListItemClick => AtkEventType.ListItemClick,
            MarketBoardItemSearchResultActivationEvent.ListItemDoubleClick => AtkEventType.ListItemDoubleClick,
            _ => throw new ArgumentOutOfRangeException(nameof(activationEvent), activationEvent, null),
        };
    }

    private static unsafe void AddAgentDetails(IDictionary<string, string?> details, AgentItemSearch* agent)
    {
        if (agent == null)
        {
            details["agentAvailable"] = false.ToString();
            return;
        }

        var itemCount = Math.Min((int)agent->ItemCount, 100);
        details["agentAvailable"] = true.ToString();
        details["agentItemCount"] = itemCount.ToString();
        details["agentIsPartialSearching"] = agent->IsPartialSearching.ToString();
        details["agentIsItemPushPending"] = agent->IsItemPushPending.ToString();
        details["agentResultItemId"] = agent->ResultItemId.ToString();
        details["agentResultSelectedIndex"] = agent->ResultSelectedIndex.ToString();
        details["agentItemPreview"] = FormatAgentItemPreview(agent, itemCount);
    }

    private static unsafe bool AgentContainsItem(AgentItemSearch* agent, uint itemId)
    {
        if (agent == null)
            return false;

        var itemCount = Math.Min((int)agent->ItemCount, 100);
        if (itemCount <= 0)
            return false;

        for (var index = 0; index < itemCount; index++)
        {
            if (agent->ItemBuffer[index] == itemId)
                return true;
        }

        return false;
    }

    private static unsafe string FormatAgentItemPreview(AgentItemSearch* agent, int itemCount)
    {
        if (agent == null || itemCount <= 0)
            return string.Empty;

        var previewCount = Math.Min(itemCount, 8);
        var values = new string[previewCount];
        for (var index = 0; index < previewCount; index++)
            values[index] = agent->ItemBuffer[index].ToString();

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
            : mode.ToString();
    }
}

public enum MarketBoardItemSearchAction
{
    ResetMode,
    SubmitSearch,
}

public enum MarketBoardItemSearchSubmitCallback
{
    TextChanged,
    Enter,
}

public enum MarketBoardItemSearchSubmitStrategy
{
    TextInputEnterCallback,
    AutofocusedTextInputRewrite,
}

public enum MarketBoardItemSearchSubmitStep
{
    ClearSearchText,
    SetSearchText,
    TextChanged,
    Enter,
}

public enum MarketBoardItemSearchResultActivationEvent
{
    ListItemClick,
    ListItemDoubleClick,
}

public enum MarketBoardItemSearchFocusTarget
{
    None,
    OwnerNode,
    CollisionNode,
}

public sealed record MarketBoardItemSearchResult
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Details { get; init; } = new Dictionary<string, string?>();
    public bool SearchSent => string.Equals(Status, "SearchSent", StringComparison.OrdinalIgnoreCase);
    public bool ReadyForListings => string.Equals(Status, "ListingsReady", StringComparison.OrdinalIgnoreCase);
    public bool IsInProgress =>
        Status is "MarketBoardNotOpen" or "ModeReset" or "StaleListingsClosed" or "SearchSent" or "ItemSelectionSent" or "ItemOpenSent";
}
