using System;
using ECommons.Automation.NeoTaskManager;

namespace MarketMafioso.Automation.Runtime;

public sealed class UiAutomationTaskQueue : IDisposable
{
    private readonly TaskManager _taskManager;

    public UiAutomationTaskQueue(int timeLimitMs = 15000)
    {
        _taskManager = new TaskManager(new TaskManagerConfiguration(
            abortOnTimeout: true,
            abortOnError: true,
            showDebug: false,
            showError: false,
            timeLimitMS: timeLimitMs,
            timeoutSilently: true));
    }

    public bool IsBusy => _taskManager.IsBusy;

    public int Count => _taskManager.NumQueuedTasks;

    public UiAutomationTaskResult? LastResult { get; private set; }

    public void Enqueue(string name, Func<UiAutomationTaskResult> step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(step);

        _taskManager.Enqueue(
            () =>
            {
                LastResult = step();
                return LastResult.Outcome switch
                {
                    UiAutomationTaskOutcome.Waiting => false,
                    UiAutomationTaskOutcome.Complete => true,
                    UiAutomationTaskOutcome.Abort => null,
                    _ => null,
                };
            },
            name);
    }

    public void Abort()
    {
        _taskManager.Abort();
    }

    public void Dispose()
    {
        _taskManager.Dispose();
    }
}
