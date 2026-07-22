using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.Automation.MarketBoard;

public enum MarketBoardAutomationOutcome
{
    Success,
    InProgress,
    ExpectedAlternate,
    Recoverable,
    Fatal,
}

public sealed record MarketBoardAutomationSnapshot
{
    public string Step { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public string Expected { get; init; } = string.Empty;
    public string Observed { get; init; } = string.Empty;
    public MarketBoardAutomationOutcome Outcome { get; init; }
    public string NextAction { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();

    public static MarketBoardAutomationSnapshot Create(
        string step,
        string phase,
        string expected,
        string observed,
        MarketBoardAutomationOutcome outcome,
        string nextAction,
        IReadOnlyDictionary<string, string?>? details = null) =>
        new()
        {
            Step = RequireValue(step, nameof(step)),
            Phase = RequireValue(phase, nameof(phase)),
            Expected = RequireValue(expected, nameof(expected)),
            Observed = RequireValue(observed, nameof(observed)),
            Outcome = outcome,
            NextAction = RequireValue(nextAction, nameof(nextAction)),
            Details = FilterDetails(details),
        };

    public IReadOnlyDictionary<string, string?> ToDetails()
    {
        var details = new Dictionary<string, string?>
        {
            ["step"] = Step,
            ["phase"] = Phase,
            ["expected"] = Expected,
            ["observed"] = Observed,
            ["outcome"] = Outcome.ToString(),
            ["nextAction"] = NextAction,
        };

        foreach (var pair in Details)
            details[pair.Key] = pair.Value;

        return details;
    }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Automation snapshot value is required.", parameterName);

        return value;
    }

    private static IReadOnlyDictionary<string, string> FilterDetails(IReadOnlyDictionary<string, string?>? details)
    {
        if (details == null)
            return new Dictionary<string, string>();

        return details
            .Where(pair => pair.Value != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!);
    }
}
