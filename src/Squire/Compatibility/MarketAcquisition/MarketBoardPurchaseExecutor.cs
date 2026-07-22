using System;

namespace MarketMafioso.MarketAcquisition;

public interface IMarketBoardPurchaseAdapter
{
    MarketBoardPurchaseResult ExecutePurchase(
        MarketBoardPurchaseCandidate candidate,
        MarketBoardLiveListing freshListing);
}

public sealed class MarketBoardPurchaseExecutor
{
    private readonly IMarketBoardPurchaseAdapter adapter;

    public MarketBoardPurchaseExecutor(IMarketBoardPurchaseAdapter adapter)
    {
        this.adapter = adapter;
    }

    public MarketBoardPurchaseResult ExecuteFirstCandidate(
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        MarketBoardReadResult freshRead)
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);
        ArgumentNullException.ThrowIfNull(freshRead);

        var candidate = MarketBoardPurchasePlanner.SelectFirstCandidate(candidatePlan);
        if (candidate == null)
        {
            return new MarketBoardPurchaseResult
            {
                Status = "NoCandidate",
                Message = "No safe live purchase candidate is available.",
            };
        }

        var freshCandidate = MarketBoardPurchasePlanner.SelectFirstFreshSafeCandidate(candidatePlan, freshRead);
        if (freshCandidate == null)
        {
            var advisoryRevalidation = MarketBoardPurchasePlanner.RevalidateCandidate(candidate, freshRead);
            return advisoryRevalidation.CanAttemptPurchase
                ? new MarketBoardPurchaseResult
                {
                    Status = "ListingListNotReady",
                    Message = "A safe purchase candidate exists, but no currently visible fresh market-board row matches it yet.",
                    Candidate = candidate,
                }
                : new MarketBoardPurchaseResult
                {
                    Status = advisoryRevalidation.Status,
                    Message = advisoryRevalidation.Message,
                    Candidate = candidate,
                };
        }

        var revalidation = MarketBoardPurchasePlanner.RevalidateCandidate(freshCandidate, freshRead);
        if (!revalidation.CanAttemptPurchase)
        {
            return new MarketBoardPurchaseResult
            {
                Status = revalidation.Status,
                Message = revalidation.Message,
                Candidate = freshCandidate,
            };
        }

        return adapter.ExecutePurchase(freshCandidate, revalidation.FreshListing!);
    }
}
