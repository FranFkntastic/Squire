using System;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRouteEngineTickResult
{
    public bool DidWork { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset? NextTickUtc { get; init; }

    public static MarketAcquisitionRouteEngineTickResult Idle(string message = "") => new()
    {
        Message = message,
    };

    public static MarketAcquisitionRouteEngineTickResult Worked(
        string message,
        DateTimeOffset? nextTickUtc = null) => new()
    {
        DidWork = true,
        Message = message,
        NextTickUtc = nextTickUtc,
    };
}
