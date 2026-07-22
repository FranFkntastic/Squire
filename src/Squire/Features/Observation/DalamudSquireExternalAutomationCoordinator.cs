using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MarketMafioso.Squire.Observation;

internal sealed class DalamudSquireExternalAutomationCoordinator
{
    private const string Owner = "MarketMafioso.Squire";
    private const string GatherBuddyRunning = "GatherBuddyReborn.IsAutoGatherEnabled";
    private const string GatherBuddyPause = "GatherBuddyReborn.SetAutoGatherPauseRequest";
    private const string GatherBuddyPauseEffective = "GatherBuddyReborn.IsAutoGatherPauseRequestEffective";
    private const string QuestionableRunning = "Questionable.IsRunning";
    private const string QuestionablePause = "Questionable.SetPauseRequest";
    private const string QuestionablePauseEffective = "Questionable.IsPauseRequestEffective";
    private const string ArtisanBusy = "Artisan.IsBusy";
    private const string ArtisanListRunning = "Artisan.IsListRunning";
    private const string ArtisanEndurance = "Artisan.GetEnduranceStatus";
    private const string ArtisanStopRequest = "Artisan.GetStopRequest";
    private const string ArtisanSetStopRequest = "Artisan.SetStopRequest";
    private static readonly TimeSpan PauseTimeout = TimeSpan.FromSeconds(30);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private bool gatherBuddyOwned;
    private bool questionableOwned;
    private bool artisanOwned;

    public DalamudSquireExternalAutomationCoordinator(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        ICondition condition,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.condition = condition;
        this.log = log;
    }

    public async Task<SquireActionResult> EnsurePausedAsync(
        SquireExecutionRecoveryPolicy policy,
        CancellationToken cancellationToken)
    {
        var gatherBuddy = await EnsureCooperativePauseAsync(
            "GatherBuddyReborn",
            "GatherBuddy Reborn",
            GatherBuddyRunning,
            GatherBuddyPause,
            GatherBuddyPauseEffective,
            policy.PauseGatherBuddyReborn,
            () => gatherBuddyOwned,
            value => gatherBuddyOwned = value,
            cancellationToken).ConfigureAwait(false);
        if (!gatherBuddy.Success)
            return gatherBuddy;

        var questionable = await EnsureCooperativePauseAsync(
            "Questionable",
            "Questionable",
            QuestionableRunning,
            QuestionablePause,
            QuestionablePauseEffective,
            policy.PauseQuestionable,
            () => questionableOwned,
            value => questionableOwned = value,
            cancellationToken).ConfigureAwait(false);
        if (!questionable.Success)
            return questionable;

        var artisan = await EnsureArtisanPausedAsync(policy.PauseArtisan, cancellationToken).ConfigureAwait(false);
        if (!artisan.Success)
            return artisan;

        var paused = new[]
        {
            gatherBuddyOwned ? "GatherBuddy Reborn" : null,
            questionableOwned ? "Questionable" : null,
            artisanOwned ? "Artisan" : null,
        }.Where(value => value is not null).ToArray();
        return SquireActionResult.Completed(paused.Length == 0
            ? "No compatible external automation required a pause."
            : $"Paused {string.Join(", ", paused!)} without discarding their active plans.");
    }

    public void ReleaseOwnedPauses()
    {
        if (artisanOwned)
        {
            try
            {
                pluginInterface.GetIpcSubscriber<bool, object>(ArtisanSetStopRequest).InvokeAction(false);
                log.Info("[MarketMafioso] Released Squire's Artisan stop request.");
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[MarketMafioso] Could not release Squire's Artisan stop request.");
            }
            finally
            {
                artisanOwned = false;
            }
        }
        ReleaseCooperativePause("Questionable", QuestionablePause, ref questionableOwned);
        ReleaseCooperativePause("GatherBuddy Reborn", GatherBuddyPause, ref gatherBuddyOwned);
    }

    private async Task<SquireActionResult> EnsureCooperativePauseAsync(
        string internalName,
        string displayName,
        string runningChannel,
        string pauseChannel,
        string effectiveChannel,
        bool mayPause,
        Func<bool> isOwned,
        Action<bool> setOwned,
        CancellationToken cancellationToken)
    {
        if (!IsLoaded(internalName))
            return SquireActionResult.Completed($"{displayName} does not require another pause request.");

        if (isOwned())
        {
            try
            {
                pluginInterface.GetIpcSubscriber<string, bool, object>(pauseChannel).InvokeAction(Owner, true);
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"[MarketMafioso] Could not renew Squire's {displayName} pause lease.");
                return SquireActionResult.Fail($"{internalName}PauseRenewalFailed", $"{displayName} did not accept renewal of Squire's owned pause lease.");
            }
            var stillEffective = await WaitUntilAsync(
                () => pluginInterface.GetIpcSubscriber<string, bool>(effectiveChannel).InvokeFunc(Owner),
                PauseTimeout,
                cancellationToken).ConfigureAwait(false);
            return stillEffective
                ? SquireActionResult.Completed($"{displayName} remains paused by Squire's owned request.")
                : SquireActionResult.Fail($"{internalName}PauseLost", $"{displayName} no longer reports Squire's owned pause request as effective.");
        }

        bool running;
        try
        {
            running = pluginInterface.GetIpcSubscriber<bool>(runningChannel).InvokeFunc();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[MarketMafioso] Could not read {displayName} running state.");
            return SquireActionResult.Fail($"{internalName}StateUnavailable", $"{displayName} is loaded, but Squire could not verify whether it owns automation.");
        }

        if (!running)
            return SquireActionResult.Completed($"{displayName} is idle.");
        if (!mayPause)
            return SquireActionResult.Fail($"{internalName}Running", $"{displayName} is running and its Squire pause policy is disabled.");

        try
        {
            pluginInterface.GetIpcSubscriber<string, bool, object>(pauseChannel).InvokeAction(Owner, true);
            setOwned(true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[MarketMafioso] {displayName} does not expose cooperative pause support.");
            return SquireActionResult.Fail(
                $"{internalName}CooperativePauseUnavailable",
                $"{displayName} is running, but this build cannot pause and resume its plan safely. Squire did not disable or abort it.");
        }

        var effective = await WaitUntilAsync(
            () => pluginInterface.GetIpcSubscriber<string, bool>(effectiveChannel).InvokeFunc(Owner),
            PauseTimeout,
            cancellationToken).ConfigureAwait(false);
        if (effective)
            return SquireActionResult.Completed($"{displayName} reached a cooperative pause boundary.");

        ReleaseCooperativePause(displayName, pauseChannel, setOwned, isOwned);
        return SquireActionResult.Fail($"{internalName}PauseTimeout", $"{displayName} did not reach a safe pause boundary within {PauseTimeout.TotalSeconds:0} seconds.");
    }

    private async Task<SquireActionResult> EnsureArtisanPausedAsync(bool mayPause, CancellationToken cancellationToken)
    {
        if (!IsLoaded("Artisan"))
            return SquireActionResult.Completed("Artisan does not require another stop request.");

        try
        {
            if (artisanOwned)
            {
                var stillRequested = pluginInterface.GetIpcSubscriber<bool>(ArtisanStopRequest).InvokeFunc();
                return stillRequested
                    ? SquireActionResult.Completed("Artisan remains paused by Squire's owned stop request.")
                    : SquireActionResult.Fail("ArtisanPauseLost", "Artisan no longer reports Squire's owned stop request as active.");
            }

            var busy = pluginInterface.GetIpcSubscriber<bool>(ArtisanBusy).InvokeFunc();
            var listRunning = pluginInterface.GetIpcSubscriber<bool>(ArtisanListRunning).InvokeFunc();
            var endurance = pluginInterface.GetIpcSubscriber<bool>(ArtisanEndurance).InvokeFunc();
            if (!busy && !listRunning && !endurance)
                return SquireActionResult.Completed("Artisan is idle.");
            if (!mayPause)
                return SquireActionResult.Fail("ArtisanRunning", "Artisan is processing and its Squire pause policy is disabled.");

            var alreadyRequested = pluginInterface.GetIpcSubscriber<bool>(ArtisanStopRequest).InvokeFunc();
            if (!alreadyRequested)
            {
                pluginInterface.GetIpcSubscriber<bool, object>(ArtisanSetStopRequest).InvokeAction(true);
                artisanOwned = true;
            }

            var settled = await WaitUntilAsync(
                () => !condition[ConditionFlag.Crafting] &&
                      !condition[ConditionFlag.PreparingToCraft] &&
                      !condition[ConditionFlag.ExecutingCraftingAction],
                PauseTimeout,
                cancellationToken).ConfigureAwait(false);
            return settled
                ? SquireActionResult.Completed(alreadyRequested
                    ? "Artisan already had an external stop request and is settled."
                    : "Artisan accepted Squire's stop request and is settled.")
                : SquireActionResult.Fail("ArtisanPauseTimeout", "Artisan accepted the stop request but did not leave crafting state within 30 seconds.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] Artisan stop-request coordination failed.");
            return SquireActionResult.Fail("ArtisanPauseUnavailable", "Artisan is loaded, but Squire could not safely pause its processing plan.");
        }
    }

    private void ReleaseCooperativePause(string displayName, string pauseChannel, ref bool owned)
    {
        if (!owned)
            return;
        try
        {
            pluginInterface.GetIpcSubscriber<string, bool, object>(pauseChannel).InvokeAction(Owner, false);
            log.Info($"[MarketMafioso] Released Squire's {displayName} pause request.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[MarketMafioso] Could not release Squire's {displayName} pause request.");
        }
        finally
        {
            owned = false;
        }
    }

    private void ReleaseCooperativePause(string displayName, string pauseChannel, Action<bool> setOwned, Func<bool> isOwned)
    {
        if (!isOwned())
            return;
        try
        {
            pluginInterface.GetIpcSubscriber<string, bool, object>(pauseChannel).InvokeAction(Owner, false);
            log.Info($"[MarketMafioso] Released Squire's {displayName} pause request.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[MarketMafioso] Could not release Squire's {displayName} pause request.");
        }
        finally
        {
            setOwned(false);
        }
    }

    private bool IsLoaded(string internalName) => pluginInterface.InstalledPlugins.Any(plugin =>
        plugin.IsLoaded && string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (predicate())
                    return true;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[MarketMafioso] External automation pause verification failed.");
                return false;
            }
            await framework.DelayTicks(6).ConfigureAwait(false);
        }
        return false;
    }
}
