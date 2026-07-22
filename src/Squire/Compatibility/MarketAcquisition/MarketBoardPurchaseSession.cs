using System;
using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketBoardPurchaseSession
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public MarketBoardPurchaseCandidate Candidate { get; init; } = new();
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset DeadlineUtc { get; init; }
    public MarketBoardPurchaseSessionPhase Phase => ResolvePhase(Status);
    public bool IsActive =>
        Phase is MarketBoardPurchaseSessionPhase.WaitingForConfirmation or MarketBoardPurchaseSessionPhase.WaitingForListingRemoval;

    public static MarketBoardPurchaseSession Start(
        MarketBoardPurchaseCandidate candidate,
        DateTimeOffset nowUtc,
        TimeSpan confirmationWatchdog) =>
        new()
        {
            Status = "WaitingForConfirmation",
            Message = "Purchase selection was sent; waiting for the market-board confirmation prompt.",
            Candidate = candidate,
            StartedAtUtc = nowUtc,
            DeadlineUtc = nowUtc.Add(confirmationWatchdog),
        };

    public MarketBoardPurchaseSession RecordConfirmationAttempt(
        MarketBoardPurchaseResult result,
        DateTimeOffset nowUtc,
        TimeSpan listingRemovalWatchdog)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!Status.Equals("WaitingForConfirmation", StringComparison.OrdinalIgnoreCase))
            return this;

        if (result.Status.Equals("ConfirmationSubmitted", StringComparison.OrdinalIgnoreCase))
        {
            return this with
            {
                Status = "WaitingForListingRemoval",
                Message = "Purchase confirmation submitted; waiting for the guarded listing to disappear.",
                DeadlineUtc = nowUtc.Add(listingRemovalWatchdog),
            };
        }

        if (result.Status.Equals("ConfirmationPending", StringComparison.OrdinalIgnoreCase) &&
            nowUtc <= DeadlineUtc)
        {
            return this with { Message = result.Message };
        }

        return this with
        {
            Status = nowUtc > DeadlineUtc ? "ConfirmationTimeout" : result.Status,
            Message = nowUtc > DeadlineUtc
                ? "Market-board purchase confirmation did not appear before the watchdog expired."
                : result.Message,
        };
    }

    public MarketBoardPurchaseSession RecordFreshRead(MarketBoardReadResult freshRead, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(freshRead);

        if (!Status.Equals("WaitingForListingRemoval", StringComparison.OrdinalIgnoreCase))
            return this;

        if (freshRead.Status.Equals("MarketBoardNotOpen", StringComparison.OrdinalIgnoreCase))
        {
            return this with
            {
                Status = "Completed",
                Message = "Confirmed purchase: the market-board result window closed after confirmation submission, so the guarded listing is considered removed.",
            };
        }

        if (freshRead.Status.Equals("NoListings", StringComparison.OrdinalIgnoreCase))
        {
            return this with
            {
                Status = "Completed",
                Message = "Confirmed purchase: the market-board search has no remaining live listings.",
            };
        }

        var revalidation = MarketBoardPurchasePlanner.RevalidateCandidate(Candidate, freshRead);
        if (revalidation.Status.Equals("ListingMissing", StringComparison.OrdinalIgnoreCase))
        {
            return this with
            {
                Status = "Completed",
                Message = "Confirmed purchase: the guarded listing is no longer present in live market-board data.",
            };
        }

        if (nowUtc <= DeadlineUtc)
        {
            return this with
            {
                Message = "Purchase confirmation was submitted; waiting for live listings to reflect the purchase.",
            };
        }

        return this with
        {
            Status = "PurchaseOutcomeUnknown",
            Message = $"Purchase confirmation was submitted, but guarded listing {Candidate.ListingId} is still present or unreadable: {revalidation.Message}",
        };
    }

    public MarketBoardAutomationSnapshot CreateFreshReadSnapshot(MarketBoardReadResult freshRead)
    {
        ArgumentNullException.ThrowIfNull(freshRead);

        var revalidation = freshRead.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase)
            ? MarketBoardPurchasePlanner.RevalidateCandidate(Candidate, freshRead)
            : MarketBoardPurchaseRevalidation.Fail(freshRead.Status, freshRead.Message);
        var outcome = ClassifyFreshReadOutcome(freshRead);
        return MarketBoardAutomationSnapshot.Create(
            "BuyListing",
            "AfterConfirmation",
            "ListingRemoved",
            freshRead.Status,
            outcome,
            ChooseFreshReadNextAction(outcome),
            new Dictionary<string, string?>
            {
                ["candidateItemId"] = Candidate.ItemId.ToString(),
                ["candidateWorld"] = Candidate.WorldName,
                ["candidateListingId"] = Candidate.ListingId,
                ["candidateRetainerId"] = Candidate.RetainerId,
                ["candidateQuantity"] = Candidate.Quantity.ToString(),
                ["candidateUnitPrice"] = Candidate.UnitPrice.ToString(),
                ["readItemId"] = freshRead.ItemId == 0 ? null : freshRead.ItemId.ToString(),
                ["readWorld"] = string.IsNullOrWhiteSpace(freshRead.WorldName) ? null : freshRead.WorldName,
                ["readListingCount"] = freshRead.Listings.Count.ToString(),
                ["readReportedListingCount"] = freshRead.ReportedListingCount.ToString(),
                ["readListingCapacity"] = freshRead.ListingCapacity.ToString(),
                ["readIsListingCountTruncated"] = freshRead.IsListingCountTruncated.ToString(),
                ["revalidationStatus"] = revalidation.Status,
                ["revalidationMessage"] = revalidation.Message,
                ["candidateStillPresent"] = ResolveCandidateStillPresent(revalidation.Status),
            });
    }

    private MarketBoardAutomationOutcome ClassifyFreshReadOutcome(MarketBoardReadResult freshRead)
    {
        if (freshRead.Status is "MarketBoardNotOpen" or "NoListings")
            return MarketBoardAutomationOutcome.ExpectedAlternate;

        if (!freshRead.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
            return MarketBoardAutomationOutcome.Recoverable;

        var revalidation = MarketBoardPurchasePlanner.RevalidateCandidate(Candidate, freshRead);
        return revalidation.Status.Equals("ListingMissing", StringComparison.OrdinalIgnoreCase)
            ? MarketBoardAutomationOutcome.Success
            : MarketBoardAutomationOutcome.InProgress;
    }

    private static string ChooseFreshReadNextAction(MarketBoardAutomationOutcome outcome)
    {
        return outcome switch
        {
            MarketBoardAutomationOutcome.Success => "BeginNextPurchase",
            MarketBoardAutomationOutcome.ExpectedAlternate => "TreatListingAsRemoved",
            MarketBoardAutomationOutcome.Recoverable => "ContinueMonitoring",
            MarketBoardAutomationOutcome.InProgress => "ContinueMonitoring",
            MarketBoardAutomationOutcome.Fatal => "CaptureInputState",
            _ => "ContinueMonitoring",
        };
    }

    private static string ResolveCandidateStillPresent(string revalidationStatus)
    {
        if (revalidationStatus.Equals("Ready", StringComparison.OrdinalIgnoreCase))
            return "True";

        if (revalidationStatus.Equals("ListingMissing", StringComparison.OrdinalIgnoreCase))
            return "False";

        return "Unknown";
    }

    private static MarketBoardPurchaseSessionPhase ResolvePhase(string status)
    {
        return status switch
        {
            "WaitingForConfirmation" => MarketBoardPurchaseSessionPhase.WaitingForConfirmation,
            "WaitingForListingRemoval" => MarketBoardPurchaseSessionPhase.WaitingForListingRemoval,
            "Completed" => MarketBoardPurchaseSessionPhase.Completed,
            _ => MarketBoardPurchaseSessionPhase.Failed,
        };
    }
}

public enum MarketBoardPurchaseSessionPhase
{
    WaitingForConfirmation,
    WaitingForListingRemoval,
    Completed,
    Failed,
}
