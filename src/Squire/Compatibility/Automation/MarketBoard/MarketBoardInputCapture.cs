using System.Collections.Generic;

namespace MarketMafioso.Automation.MarketBoard;

public sealed record MarketBoardInputCapture
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Details { get; init; } = new Dictionary<string, string?>();
}
