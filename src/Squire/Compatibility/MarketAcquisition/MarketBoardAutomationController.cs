using System;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketBoardAutomationController : IDisposable
{
    public MarketBoardPurchaseSession? PurchaseSession { get; private set; }
    public MarketBoardPurchaseResult? LastPurchaseResult { get; private set; }
    public DateTimeOffset NextMonitorUtc { get; private set; } = DateTimeOffset.MinValue;

    public bool IsBusy => PurchaseSession?.IsActive == true;

    public string Status =>
        PurchaseSession?.Status ??
        LastPurchaseResult?.Status ??
        "Idle";

    public string Message =>
        PurchaseSession?.Message ??
        LastPurchaseResult?.Message ??
        "No market-board automation is active.";

    public bool IsMonitorDue(DateTimeOffset nowUtc) =>
        IsBusy &&
        nowUtc >= NextMonitorUtc;

    public void ScheduleNextMonitor(DateTimeOffset nowUtc, TimeSpan delay)
    {
        NextMonitorUtc = nowUtc.Add(delay);
    }

    public MarketBoardPurchaseMonitorTick MonitorPurchase(
        DateTimeOffset nowUtc,
        TimeSpan monitorInterval,
        TimeSpan listingRemovalWatchdog,
        Func<MarketBoardPurchaseCandidate, MarketBoardPurchaseResult> confirmPurchase,
        Func<MarketBoardReadResult> readFreshListings,
        bool monitorListingRemoval = true)
    {
        ArgumentNullException.ThrowIfNull(confirmPurchase);
        ArgumentNullException.ThrowIfNull(readFreshListings);

        var session = PurchaseSession;
        if (session?.IsActive != true)
            return MarketBoardPurchaseMonitorTick.Idle(session, LastPurchaseResult);

        if (!IsMonitorDue(nowUtc))
            return MarketBoardPurchaseMonitorTick.Waiting(session);

        ScheduleNextMonitor(nowUtc, monitorInterval);

        MarketBoardPurchaseResult? confirmationResult = null;
        MarketBoardReadResult? freshRead = null;
        MarketBoardPurchaseSession? freshReadSession = null;

        if (session.Status.Equals("WaitingForConfirmation", StringComparison.OrdinalIgnoreCase))
        {
            confirmationResult = confirmPurchase(session.Candidate);
            RecordConfirmationAttempt(confirmationResult, nowUtc, listingRemovalWatchdog);
            session = PurchaseSession ?? session;
        }

        if (monitorListingRemoval &&
            session.Status.Equals("WaitingForListingRemoval", StringComparison.OrdinalIgnoreCase))
        {
            freshReadSession = session;
            freshRead = readFreshListings();
            RecordFreshRead(freshRead, nowUtc);
            session = PurchaseSession ?? session;
        }

        return MarketBoardPurchaseMonitorTick.Worked(
            session,
            confirmationResult,
            freshRead,
            freshReadSession);
    }

    public void RecordPurchaseSelection(
        MarketBoardPurchaseResult result,
        DateTimeOffset nowUtc,
        TimeSpan confirmationWatchdog)
    {
        ArgumentNullException.ThrowIfNull(result);

        LastPurchaseResult = result;
        PurchaseSession = result.Status.Equals("PurchaseSelectionSent", StringComparison.OrdinalIgnoreCase) &&
                          result.Candidate != null
            ? MarketBoardPurchaseSession.Start(result.Candidate, nowUtc, confirmationWatchdog)
            : null;
    }

    public void RecordConfirmationAttempt(
        MarketBoardPurchaseResult result,
        DateTimeOffset nowUtc,
        TimeSpan listingRemovalWatchdog)
    {
        ArgumentNullException.ThrowIfNull(result);

        LastPurchaseResult = result;
        if (PurchaseSession != null)
            PurchaseSession = PurchaseSession.RecordConfirmationAttempt(result, nowUtc, listingRemovalWatchdog);
    }

    public void RecordFreshRead(MarketBoardReadResult readResult, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(readResult);

        if (PurchaseSession != null)
            PurchaseSession = PurchaseSession.RecordFreshRead(readResult, nowUtc);
    }

    public void RecordMonitorFailure(string status, string message)
    {
        if (PurchaseSession != null)
        {
            PurchaseSession = PurchaseSession with
            {
                Status = status,
                Message = message,
            };
            return;
        }

        LastPurchaseResult = new MarketBoardPurchaseResult
        {
            Status = status,
            Message = message,
        };
    }

    public void Abort(string message)
    {
        PurchaseSession = null;
        LastPurchaseResult = new MarketBoardPurchaseResult
        {
            Status = "Aborted",
            Message = message,
        };
    }

    public void Clear()
    {
        PurchaseSession = null;
        LastPurchaseResult = null;
        NextMonitorUtc = DateTimeOffset.MinValue;
    }

    public void Dispose()
    {
        Clear();
    }
}

public sealed record MarketBoardPurchaseMonitorTick
{
    public bool DidWork { get; init; }
    public MarketBoardPurchaseSession? Session { get; init; }
    public MarketBoardPurchaseResult? LastPurchaseResult { get; init; }
    public MarketBoardPurchaseResult? ConfirmationResult { get; init; }
    public MarketBoardReadResult? FreshRead { get; init; }
    public MarketBoardPurchaseSession? FreshReadSession { get; init; }

    public static MarketBoardPurchaseMonitorTick Idle(
        MarketBoardPurchaseSession? session,
        MarketBoardPurchaseResult? lastPurchaseResult) =>
        new()
        {
            Session = session,
            LastPurchaseResult = lastPurchaseResult,
        };

    public static MarketBoardPurchaseMonitorTick Waiting(MarketBoardPurchaseSession session) =>
        new()
        {
            Session = session,
        };

    public static MarketBoardPurchaseMonitorTick Worked(
        MarketBoardPurchaseSession session,
        MarketBoardPurchaseResult? confirmationResult,
        MarketBoardReadResult? freshRead,
        MarketBoardPurchaseSession? freshReadSession) =>
        new()
        {
            DidWork = true,
            Session = session,
            ConfirmationResult = confirmationResult,
            FreshRead = freshRead,
            FreshReadSession = freshReadSession,
        };
}
