using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class GenerationBoundComputationTests
{
    [Fact]
    public void Poll_DoesNotBlockFrameworkWhileWorkerIsBlocked()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var computation = new GenerationBoundComputation<int>();
        computation.Start(7, _ =>
        {
            started.Set();
            release.Wait();
            return 42;
        }, cancellation.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "Worker did not start.");

        var result = computation.Poll(7);

        Assert.Equal(GenerationBoundComputationStatus.Pending, result.Status);
        release.Set();
    }

    [Fact]
    public async Task Invalidate_IgnoresLateCompletionFromCancelledGeneration()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var computation = new GenerationBoundComputation<int>();
        computation.Start(7, _ =>
        {
            try
            {
                started.Set();
                release.Wait();
                return 42;
            }
            finally
            {
                finished.SetResult();
            }
        }, cancellation.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "Worker did not start.");

        cancellation.Cancel();
        computation.Invalidate();
        release.Set();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(GenerationBoundComputationStatus.None, computation.Poll(8).Status);
    }
}
