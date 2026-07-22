using System;
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketBoardListingReadAccumulator
{
    private const int DefaultMaxContinuationAttempts = 3;

    private readonly int maxContinuationAttempts;
    private MarketBoardAccumulatedReadResult? accumulatedRead;
    private int continuationAttempts;

    public MarketBoardListingReadAccumulator(int maxContinuationAttempts = DefaultMaxContinuationAttempts)
    {
        if (maxContinuationAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxContinuationAttempts));

        this.maxContinuationAttempts = maxContinuationAttempts;
    }

    public MarketBoardReadResult Merge(MarketBoardReadResult readResult)
    {
        ArgumentNullException.ThrowIfNull(readResult);

        if (!readResult.IsFresh || readResult.ItemId == 0 || !readResult.IsListingCountTruncated)
        {
            Clear();
            return readResult;
        }

        if (accumulatedRead == null ||
            accumulatedRead.ItemId != readResult.ItemId ||
            !accumulatedRead.WorldName.Equals(readResult.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            accumulatedRead = MarketBoardAccumulatedReadResult.FromReadResult(readResult);
            continuationAttempts = 0;
            return accumulatedRead.ToReadResult();
        }

        var previousReadableListings = accumulatedRead.ReadableListingCount;
        accumulatedRead = accumulatedRead.Append(readResult);
        if (accumulatedRead.ReadableListingCount > previousReadableListings)
            continuationAttempts = 0;

        return accumulatedRead.ToReadResult();
    }

    public bool TryBeginContinuation(
        MarketBoardReadResult readResult,
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        out MarketBoardListingReadContinuation continuation)
    {
        ArgumentNullException.ThrowIfNull(readResult);
        ArgumentNullException.ThrowIfNull(candidatePlan);

        continuation = default;
        if (!MarketAcquisitionLiveCandidateStatuses.IsIncompleteListingCoverage(candidatePlan.Status) ||
            !readResult.HasIncompleteCoverage ||
            continuationAttempts >= maxContinuationAttempts)
        {
            return false;
        }

        var requestedRow = Math.Min(
            Math.Max(0, readResult.ReportedListingCount - 1),
            Math.Max(0, readResult.ReadableListingCount));
        continuationAttempts++;
        continuation = new MarketBoardListingReadContinuation(
            requestedRow,
            $"Reading deeper market-board listings ({readResult.ReadableListingCount:N0}/{readResult.ReportedListingCount:N0} visible so far; continuation {continuationAttempts:N0}/{maxContinuationAttempts:N0}).");
        return true;
    }

    public void Clear()
    {
        accumulatedRead = null;
        continuationAttempts = 0;
    }
}

public readonly record struct MarketBoardListingReadContinuation(int RequestedRow, string Message);
