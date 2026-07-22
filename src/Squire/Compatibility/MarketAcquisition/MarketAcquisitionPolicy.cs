using System;

namespace MarketMafioso.MarketAcquisition;

internal static class MarketAcquisitionPolicy
{
    public static string NormalizeHqPolicy(string hqPolicy) =>
        hqPolicy switch
        {
            "Either" => "Either",
            "HqOnly" or "HQOnly" => "HqOnly",
            "NqOnly" or "NQOnly" => "NqOnly",
            _ => throw new InvalidOperationException($"Unknown HQ policy {hqPolicy}."),
        };

    public static bool HqMatches(string hqPolicy, bool isHq) =>
        NormalizeHqPolicy(hqPolicy) switch
        {
            "HqOnly" => isHq,
            "NqOnly" => !isHq,
            "Either" => true,
            _ => throw new InvalidOperationException($"Unknown HQ policy {hqPolicy}."),
        };
}
