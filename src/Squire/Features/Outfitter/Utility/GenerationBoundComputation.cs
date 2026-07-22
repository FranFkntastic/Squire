using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.Squire.Outfitter.Utility;

internal enum GenerationBoundComputationStatus
{
    None,
    Pending,
    Completed,
    Cancelled,
    Faulted,
    Stale,
}

internal sealed record GenerationBoundComputationResult<T>(
    GenerationBoundComputationStatus Status,
    T? Value = default,
    Exception? Exception = null);

/// <summary>
/// Runs a pure computation away from the framework tick and only releases its result to the
/// session generation that started it. Invalidated work is observed when it eventually exits,
/// but it can never publish late state into a replacement session.
/// </summary>
internal sealed class GenerationBoundComputation<T>
{
    private Task<T>? task;
    private long generation;

    public bool IsActive => task is not null;

    public void Start(
        long sessionGeneration,
        Func<CancellationToken, T> computation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(computation);
        Invalidate();
        generation = sessionGeneration;
        task = Task.Run(() => computation(cancellationToken), cancellationToken);
    }

    public GenerationBoundComputationResult<T> Poll(long sessionGeneration)
    {
        var current = task;
        if (current is null)
            return new(GenerationBoundComputationStatus.None);
        if (generation != sessionGeneration)
        {
            Invalidate();
            return new(GenerationBoundComputationStatus.Stale);
        }
        if (!current.IsCompleted)
            return new(GenerationBoundComputationStatus.Pending);

        task = null;
        generation = 0;
        if (current.IsCanceled)
            return new(GenerationBoundComputationStatus.Cancelled);
        if (current.IsFaulted)
            return new(GenerationBoundComputationStatus.Faulted, Exception: current.Exception?.GetBaseException());
        return new(GenerationBoundComputationStatus.Completed, current.GetAwaiter().GetResult());
    }

    public void Invalidate()
    {
        var invalidated = task;
        task = null;
        generation = 0;
        if (invalidated is { IsCompleted: false })
        {
            _ = invalidated.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        else if (invalidated is { IsFaulted: true })
        {
            _ = invalidated.Exception;
        }
    }
}
