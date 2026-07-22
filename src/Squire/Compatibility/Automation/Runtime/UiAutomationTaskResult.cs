using System.Collections.Generic;

namespace MarketMafioso.Automation.Runtime;

public enum UiAutomationTaskOutcome
{
    Waiting,
    Complete,
    Abort,
}

public sealed record UiAutomationTaskResult(
    UiAutomationTaskOutcome Outcome,
    string Message,
    IReadOnlyDictionary<string, string>? Diagnostics = null)
{
    public static UiAutomationTaskResult Waiting(
        string message,
        IReadOnlyDictionary<string, string>? diagnostics = null) =>
        new(UiAutomationTaskOutcome.Waiting, message, diagnostics);

    public static UiAutomationTaskResult Complete(
        string message,
        IReadOnlyDictionary<string, string>? diagnostics = null) =>
        new(UiAutomationTaskOutcome.Complete, message, diagnostics);

    public static UiAutomationTaskResult Abort(
        string message,
        IReadOnlyDictionary<string, string>? diagnostics = null) =>
        new(UiAutomationTaskOutcome.Abort, message, diagnostics);
}
