using System;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRouteProgressReporter
{
    public const string ProgressAction = "progress";
    public const string CompleteAction = "complete";
    public const string FailAction = "fail";

    public static bool CanReportForRequestStatus(string? requestStatus) =>
        requestStatus != null &&
        (requestStatus.Equals("AcceptedInPlugin", StringComparison.OrdinalIgnoreCase) ||
         requestStatus.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
         requestStatus.Equals("RecoveryRequired", StringComparison.OrdinalIgnoreCase));

    public static bool CanReportForRouteState(string? routeState) =>
        routeState != null &&
        (routeState.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
         routeState.Equals("Paused", StringComparison.OrdinalIgnoreCase) ||
         routeState.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
         routeState.Equals("Failed", StringComparison.OrdinalIgnoreCase));

    public static string CreateIdempotencyKey(
        string pluginInstanceId,
        string routeNonce,
        long sequence) =>
        $"{pluginInstanceId}-route-{routeNonce}-{sequence}";

    public static string ResolveAction(string runnerState)
    {
        if (runnerState.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            return CompleteAction;

        return runnerState.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            ? FailAction
            : ProgressAction;
    }
}
