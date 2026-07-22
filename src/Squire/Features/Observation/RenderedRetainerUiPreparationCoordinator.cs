using System;
using System.Linq;

namespace MarketMafioso.Squire.Observation;

public enum RenderedRetainerUiPreparationStatus
{
    Idle,
    Traveling,
    ClearingMarketBoardUi,
    OpeningRetainerList,
    Complete,
    Failed,
    Cancelled,
}

public sealed record RenderedRetainerUiPreparationProgress(
    RenderedRetainerUiPreparationStatus Status,
    int InteractionAttempts,
    string Diagnostic);

/// <summary>
/// Pure coordinator for a semantic UI workflow. External adapters may issue normal game commands
/// and observe Lifestream progress, but completion is authorized only by the rendered RetainerList.
/// </summary>
public sealed class RenderedRetainerUiPreparationCoordinator
{
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TravelSettleWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MarketBoardCloseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BellInteractionTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RetainerListSettleWindow = TimeSpan.FromSeconds(3);

    private RenderedRetainerUiPreparationStatus status = RenderedRetainerUiPreparationStatus.Idle;
    private DateTimeOffset phaseStartedAt;
    private int interactionAttempts;
    private string diagnostic = "Retainer UI preparation has not started.";

    public RenderedRetainerUiPreparationProgress Begin(
        DateTimeOffset nowUtc,
        bool retainerListVisible,
        bool lifestreamAvailable,
        string ownerHomeWorld,
        Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);
        interactionAttempts = 0;
        if (retainerListVisible)
            return Complete("The rendered Retainer List is already visible.");
        if (!lifestreamAvailable)
            return Fail("Lifestream is unavailable, so the bridge cannot prepare retainer observation without foreground control.");
        var destination = ownerHomeWorld?.Trim() ?? string.Empty;
        if (destination.Length is < 2 or > 32 || destination.Any(value => !char.IsLetter(value) && value is not (' ' or '-' or '\'')))
            return Fail("A valid owner home-world name is required before retainer observation can travel.");
        if (!processCommand($"/li {destination} mb"))
            return Fail("Lifestream did not accept the semantic owner-home-world market-board travel command.");

        status = RenderedRetainerUiPreparationStatus.Traveling;
        phaseStartedAt = nowUtc;
        diagnostic = $"Lifestream travel to {destination}'s market board requested; waiting without taking window focus.";
        return Snapshot();
    }

    public RenderedRetainerUiPreparationProgress Advance(
        DateTimeOffset nowUtc,
        bool retainerListVisible,
        bool lifestreamStateAvailable,
        bool lifestreamBusy,
        bool marketBoardUiVisible,
        Func<string, bool> processCommand)
    {
        ArgumentNullException.ThrowIfNull(processCommand);
        if (retainerListVisible)
            return Complete("The rendered Retainer List is visible and ready for evidence capture.");
        if (status is RenderedRetainerUiPreparationStatus.Complete or RenderedRetainerUiPreparationStatus.Failed or RenderedRetainerUiPreparationStatus.Cancelled)
            return Snapshot();

        switch (status)
        {
            case RenderedRetainerUiPreparationStatus.Traveling:
                if (!lifestreamStateAvailable)
                    return Fail("Lifestream travel began, but its bounded busy state is unavailable.");
                if (nowUtc - phaseStartedAt > TravelTimeout)
                    return Fail("Lifestream did not finish market-board travel within five minutes.");
                if (nowUtc - phaseStartedAt < TravelSettleWindow)
                    return Snapshot();
                // Lifestream remains busy while the market-board addon it opened is visible.
                // Treat the addon as arrival proof, but never target through the UI being closed
                // in this same framework turn.
                if (marketBoardUiVisible)
                {
                    status = RenderedRetainerUiPreparationStatus.ClearingMarketBoardUi;
                    phaseStartedAt = nowUtc;
                    diagnostic = "Market-board arrival confirmed; waiting for its rendered UI to close before targeting the bell.";
                    return Snapshot();
                }
                if (lifestreamBusy)
                    return Snapshot();
                return BeginBellInteraction(nowUtc, processCommand);

            case RenderedRetainerUiPreparationStatus.ClearingMarketBoardUi:
                if (!lifestreamStateAvailable)
                    return Fail("The market board closed, but Lifestream's bounded busy state became unavailable.");
                if (nowUtc - phaseStartedAt > MarketBoardCloseTimeout)
                    return Fail("The market-board UI did not finish closing within ten seconds.");
                if (marketBoardUiVisible || lifestreamBusy)
                    return Snapshot();
                return BeginBellInteraction(nowUtc, processCommand);

            case RenderedRetainerUiPreparationStatus.OpeningRetainerList:
                if (!lifestreamStateAvailable)
                    return Fail("Lifestream began opening the Summoning Bell, but its bounded busy state became unavailable.");
                if (nowUtc - phaseStartedAt > BellInteractionTimeout)
                    return Fail("Lifestream did not open the rendered Retainer List within forty-five seconds.");
                if (lifestreamBusy || nowUtc - phaseStartedAt < RetainerListSettleWindow)
                    return Snapshot();
                if (interactionAttempts == 1)
                {
                    if (!processCommand("rendered-ui:activate-summoning-bell"))
                        return Fail("Lifestream finished the Summoning Bell interaction without opening the Retainer List, and the rendered bell target rejected the bounded fallback activation.");
                    interactionAttempts++;
                    phaseStartedAt = nowUtc;
                    diagnostic = "Lifestream did not open the Retainer List; activated the already-rendered Summoning Bell target and is waiting for rendered confirmation or brief external retainer automation to finish.";
                    return Snapshot();
                }
                return Snapshot();

            default:
                return Fail("Retainer UI preparation is not active.");
        }
    }

    public RenderedRetainerUiPreparationProgress Cancel()
    {
        if (status is RenderedRetainerUiPreparationStatus.Complete or RenderedRetainerUiPreparationStatus.Failed)
            return Snapshot();
        status = RenderedRetainerUiPreparationStatus.Cancelled;
        diagnostic = "Retainer UI preparation was cancelled; no further commands will be issued.";
        return Snapshot();
    }

    public RenderedRetainerUiPreparationProgress Snapshot() => new(status, interactionAttempts, diagnostic);

    private RenderedRetainerUiPreparationProgress BeginBellInteraction(
        DateTimeOffset nowUtc,
        Func<string, bool> processCommand)
    {
        if (!processCommand("lifestream:interact-object:2000401"))
            return Fail("Lifestream did not accept the semantic Summoning Bell interaction.");
        interactionAttempts++;
        status = RenderedRetainerUiPreparationStatus.OpeningRetainerList;
        phaseStartedAt = nowUtc;
        diagnostic = "Summoning Bell interaction requested through Lifestream; waiting for the rendered Retainer List.";
        return Snapshot();
    }

    private RenderedRetainerUiPreparationProgress Complete(string message)
    {
        status = RenderedRetainerUiPreparationStatus.Complete;
        diagnostic = message;
        return Snapshot();
    }

    private RenderedRetainerUiPreparationProgress Fail(string message)
    {
        status = RenderedRetainerUiPreparationStatus.Failed;
        diagnostic = message;
        return Snapshot();
    }
}
