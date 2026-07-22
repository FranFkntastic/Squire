using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.Squire.Observation;

internal sealed class DalamudSquireRecoveryCoordinator
{
    private static readonly TimeSpan KnockoutRecoveryTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AreaSettleTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DutyMenuTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DutyExitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MenuSettleTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] KnockoutReturnTerms = ["Return", "帰還", "zurückkehren", "Rapatriement", "返回", "返回"];
    private static readonly string[] DutyExitTerms = ["Leave", "退出", "verlassen", "Quitter", "退出", "離開"];
    private static readonly string[] SafeMenuAddons =
    [
        "SelectString", "SelectIconString", "Shop", "Repair", "MateriaAttach", "MateriaAttachDialog",
        "MateriaRetrieve", "MateriaRetrieveDialog", "SalvageDialog", "GrandCompanySupplyList",
        "RetainerList", "Inventory", "InventoryLarge", "InventoryExpansion", "ArmouryBoard", "Character",
        "RecipeNote", "GatheringNote", "Teleport", "Map", "ContentsFinder", "ContentsFinderMenu", "Journal",
        "ItemSearch", "ItemSearchResult", "ContextMenu", "InputNumeric",
    ];
    private static readonly string[] UnknownModalAddons =
    [
        "SelectYesno", "Talk", "RetainerTaskAsk", "RetainerTaskResult", "InventoryRetainer",
        "InventoryRetainerLarge", "InventoryRetainerSmall",
    ];

    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly DalamudSquireExternalAutomationCoordinator externalAutomation;

    public DalamudSquireRecoveryCoordinator(
        ICondition condition,
        IGameGui gameGui,
        IFramework framework,
        IPluginLog log,
        DalamudSquireExternalAutomationCoordinator externalAutomation)
    {
        this.condition = condition;
        this.gameGui = gameGui;
        this.framework = framework;
        this.log = log;
        this.externalAutomation = externalAutomation;
    }

    public async Task<SquireActionResult> EnsureReadyAsync(
        SquireExecutionRecoveryPolicy policy,
        CancellationToken cancellationToken)
    {
        var pause = await externalAutomation.EnsurePausedAsync(policy, cancellationToken).ConfigureAwait(false);
        if (!pause.Success)
            return pause;

        if (condition[ConditionFlag.Unconscious])
        {
            if (!policy.RecoverFromKnockout)
                return SquireActionResult.Fail("PlayerUnconscious", "The character is unconscious, and knockout recovery is disabled.");
            var knockout = await RecoverFromKnockoutAsync(cancellationToken).ConfigureAwait(false);
            if (!knockout.Success)
                return knockout;
        }

        if (condition[ConditionFlag.InCombat])
        {
            if (!policy.WaitForCombatToEnd)
                return SquireActionResult.Fail("PlayerInCombat", "The character is in combat, and combat waiting is disabled.");
            var combatEnded = await WaitUntilAsync(
                () => !condition[ConditionFlag.InCombat],
                policy.CombatRecoveryTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!combatEnded)
                return SquireActionResult.Fail("CombatRecoveryTimeout", $"Combat did not end within {policy.CombatRecoveryTimeout.TotalSeconds:0} seconds.");
        }

        if (IsInDuty())
        {
            if (!policy.LeaveDutyToExecute)
                return SquireActionResult.Fail("DutyExitNotAuthorized", "The character is in a duty, and leaving duties for Squire is disabled.");
            var dutyExit = await LeaveDutyAsync(cancellationToken).ConfigureAwait(false);
            if (!dutyExit.Success)
                return dutyExit;
        }

        var menuRecovery = policy.CloseSafeUserMenus
            ? await CloseSafeMenusAsync(cancellationToken).ConfigureAwait(false)
            : (Closed: (IReadOnlyList<string>)[], Remaining: (IReadOnlyList<string>)[]);
        if (menuRecovery.Remaining.Count > 0)
            return SquireActionResult.Fail(
                "MenuCloseTimeout",
                $"Compatible menu(s) did not close through their normal callbacks: {string.Join(", ", menuRecovery.Remaining)}.");

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            var settled = await WaitUntilAsync(
                () => !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51],
                AreaSettleTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!settled)
                return SquireActionResult.Fail("AreaTransitionTimeout", "The character did not finish changing areas within one minute.");
        }

        if (condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.WatchingCutscene78])
            return SquireActionResult.Fail("CutsceneOwnsUi", "A cutscene owns the game UI; Squire did not dismiss or skip it.");
        if (condition[ConditionFlag.OccupiedInQuestEvent])
            return SquireActionResult.Fail("UnknownUiOwner", "A quest or NPC interaction still owns the game UI after compatible menus were closed.");
        var occupied = GetBlockingOccupiedConditions();
        if (occupied.Count > 0)
            return SquireActionResult.Fail("UnknownUiOwner", $"The game still reports an occupied UI state after compatible menus were closed: {string.Join(", ", occupied)}.");
        var unknownModals = await framework.RunOnTick(GetVisibleUnknownModals).ConfigureAwait(false);
        if (unknownModals.Count > 0)
            return SquireActionResult.Fail(
                "UnknownModalOpen",
                $"Squire left unrelated confirmation, dialogue, or retainer UI untouched: {string.Join(", ", unknownModals)}.");
        if (condition[ConditionFlag.Crafting] ||
            condition[ConditionFlag.PreparingToCraft] ||
            condition[ConditionFlag.ExecutingCraftingAction])
            return SquireActionResult.Fail("CraftingStateActive", "The character remains in a crafting state after external processing was paused.");

        var recovery = menuRecovery.Closed.Count == 0
            ? "The character is ready for Squire execution."
            : $"Closed compatible menu(s): {string.Join(", ", menuRecovery.Closed)}. The character is ready for Squire execution.";
        return SquireActionResult.Completed($"{pause.Message} {recovery}");
    }

    public void ReleaseOwnedState() => externalAutomation.ReleaseOwnedPauses();

    private async Task<SquireActionResult> RecoverFromKnockoutAsync(CancellationToken cancellationToken)
    {
        log.Info("[MarketMafioso] Squire is recovering the character from a knockout through the Return confirmation UI.");
        string? unexpectedPrompt = null;
        var returned = await WaitUntilAsync(
            () =>
            {
                if (!condition[ConditionFlag.Unconscious])
                    return true;
                unexpectedPrompt = TryConfirmReturnPrompt();
                if (unexpectedPrompt is not null)
                    return true;
                return false;
            },
            KnockoutRecoveryTimeout,
            cancellationToken).ConfigureAwait(false);
        if (unexpectedPrompt is not null)
            return SquireActionResult.Fail(
                "KnockoutReturnPromptUnexpected",
                $"The character is unconscious, but the visible confirmation was not a recognized Return prompt: {unexpectedPrompt}");
        if (!returned)
            return SquireActionResult.Fail("KnockoutRecoveryTimeout", "The character did not return from the knockout within two minutes.");

        var stableSince = Stopwatch.StartNew();
        var settled = await WaitUntilAsync(
            () =>
            {
                if (condition[ConditionFlag.Unconscious] ||
                    condition[ConditionFlag.BetweenAreas] ||
                    condition[ConditionFlag.BetweenAreas51])
                {
                    stableSince.Restart();
                    return false;
                }
                return stableSince.Elapsed >= TimeSpan.FromSeconds(3);
            },
            AreaSettleTimeout,
            cancellationToken).ConfigureAwait(false);
        return settled
            ? SquireActionResult.Completed("Returned from the knockout and observed three seconds of stable game state.")
            : SquireActionResult.Fail("KnockoutAreaSettleTimeout", "The character returned, but the new area did not settle within one minute.");
    }

    private unsafe string? TryConfirmReturnPrompt()
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno", 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return null;
        var prompt = addon->PromptText->NodeText.ExtractText().Trim();
        if (!ContainsAny(prompt, KnockoutReturnTerms))
            return prompt;
        addon->AtkUnitBase.FireCallbackInt(0);
        return null;
    }

    private async Task<SquireActionResult> LeaveDutyAsync(CancellationToken cancellationToken)
    {
        log.Warning("[MarketMafioso] Squire is leaving the current duty through the Contents Finder menu because the user explicitly enabled duty-exit recovery.");
        var leaveSubmitted = await WaitUntilAsync(
            () =>
            {
                if (!IsInDuty())
                    return true;
                return TrySubmitDutyExitCommand();
            },
            DutyMenuTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!leaveSubmitted)
            return SquireActionResult.Fail("DutyExitMenuTimeout", "The Contents Finder menu did not expose its Leave command within ten seconds.");
        if (!IsInDuty())
            return await ObserveAreaSettledAsync("Left the duty without a confirmation prompt.", cancellationToken).ConfigureAwait(false);

        string? unexpectedPrompt = null;
        var confirmed = await WaitUntilAsync(
            () =>
            {
                var promptState = TryConfirmDutyExitPrompt();
                if (!promptState.Visible)
                    return false;
                if (!promptState.Expected)
                {
                    unexpectedPrompt = promptState.Prompt;
                    return true;
                }
                return true;
            },
            DutyMenuTimeout,
            cancellationToken).ConfigureAwait(false);
        if (unexpectedPrompt is not null)
            return SquireActionResult.Fail("DutyExitPromptUnexpected", $"Squire opened the Leave command, but did not accept an unrecognized confirmation: {unexpectedPrompt}");
        if (!confirmed)
            return SquireActionResult.Fail("DutyExitPromptTimeout", "The Leave command did not produce a confirmation prompt within ten seconds.");

        var exited = await WaitUntilAsync(() => !IsInDuty(), DutyExitTimeout, cancellationToken).ConfigureAwait(false);
        if (!exited)
            return SquireActionResult.Fail("DutyExitTimeout", "The character remained bound by duty for two minutes after confirming Leave.");
        return await ObserveAreaSettledAsync("Left the duty through its normal UI.", cancellationToken).ConfigureAwait(false);
    }

    private unsafe bool TrySubmitDutyExitCommand()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return false;
        var agent = agentModule->GetAgentByInternalId(AgentId.ContentsFinderMenu);
        if (agent == null)
            return false;
        agent->Show();
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContentsFinderMenu", 1);
        if (menu == null || !menu->IsReady || !menu->IsVisible)
            return false;
        menu->FireCallbackInt(0);
        return true;
    }

    private unsafe (bool Visible, bool Expected, string Prompt) TryConfirmDutyExitPrompt()
    {
        var yesNo = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno", 1);
        if (yesNo == null || !yesNo->AtkUnitBase.IsReady || !yesNo->AtkUnitBase.IsVisible)
            return (false, false, string.Empty);
        var prompt = yesNo->PromptText->NodeText.ExtractText().Trim();
        if (!IsExpectedDutyExitPrompt(prompt))
            return (true, false, prompt);
        yesNo->AtkUnitBase.FireCallbackInt(0);
        return (true, true, prompt);
    }

    private async Task<SquireActionResult> ObserveAreaSettledAsync(string completedMessage, CancellationToken cancellationToken)
    {
        var stableSince = Stopwatch.StartNew();
        var settled = await WaitUntilAsync(
            () =>
            {
                if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
                {
                    stableSince.Restart();
                    return false;
                }
                return stableSince.Elapsed >= TimeSpan.FromSeconds(3);
            },
            AreaSettleTimeout,
            cancellationToken).ConfigureAwait(false);
        return settled
            ? SquireActionResult.Completed(completedMessage)
            : SquireActionResult.Fail("DutyExitAreaSettleTimeout", "The character left the duty, but the destination area did not settle within one minute.");
    }

    internal static bool IsExpectedDutyExitPrompt(string prompt) => ContainsAny(prompt, DutyExitTerms);

    private static bool ContainsAny(string text, IEnumerable<string> terms) =>
        terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private async Task<(IReadOnlyList<string> Closed, IReadOnlyList<string> Remaining)> CloseSafeMenusAsync(CancellationToken cancellationToken)
    {
        var closed = new HashSet<string>(StringComparer.Ordinal);
        var deadline = DateTime.UtcNow + MenuSettleTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var visible = await framework.RunOnTick(() => CloseOnePass(closed)).ConfigureAwait(false);
            if (!visible)
                break;
            await framework.DelayTicks(2).ConfigureAwait(false);
        }
        var remaining = await framework.RunOnTick(GetVisibleSafeMenus).ConfigureAwait(false);
        return (
            closed.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            remaining);
    }

    private unsafe bool CloseOnePass(HashSet<string> closed)
    {
        var foundVisible = false;
        foreach (var addonName in SafeMenuAddons)
        {
            for (var index = 1; index <= 8; index++)
            {
                var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, index);
                if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
                    continue;
                foundVisible = true;
                closed.Add(addonName);
                addon->FireCallbackInt(-1);
            }
        }
        return foundVisible;
    }

    private unsafe IReadOnlyList<string> GetVisibleSafeMenus()
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addonName in SafeMenuAddons)
        {
            for (var index = 1; index <= 8; index++)
            {
                var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, index);
                if (addon != null && addon->RootNode != null && addon->RootNode->IsVisible())
                    visible.Add(addonName);
            }
        }
        return visible.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private unsafe IReadOnlyList<string> GetVisibleUnknownModals() => UnknownModalAddons
        .Where(addonName =>
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            return addon != null && addon->RootNode != null && addon->RootNode->IsVisible();
        })
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

    private IReadOnlyList<ConditionFlag> GetBlockingOccupiedConditions()
    {
        var flags = new[]
        {
            ConditionFlag.Occupied,
            ConditionFlag.Occupied30,
            ConditionFlag.Occupied33,
            ConditionFlag.Occupied38,
            ConditionFlag.Occupied39,
            ConditionFlag.OccupiedInEvent,
            ConditionFlag.OccupiedSummoningBell,
        };
        return flags.Where(flag => condition[flag]).ToArray();
    }

    private bool IsInDuty() => condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56];

    private async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await framework.RunOnTick(predicate).ConfigureAwait(false))
                return true;
            await framework.DelayTicks(6).ConfigureAwait(false);
        }
        return false;
    }
}
