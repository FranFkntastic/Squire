namespace MarketMafioso.Squire.Outfitter.Portfolio;

public static class PortfolioAcquisitionBridgeProjection
{
    public static string? Status(
        PortfolioAcquisitionRecoveryState? recovery,
        PortfolioAcquisitionTransfer? transfer,
        bool compositionFailed) =>
        recovery?.Status.ToString() ??
        (compositionFailed ? "CompositionFailed" : transfer is { Lines.Count: > 0 } ? "ReadyForReview" : null);

    public static string? Diagnostic(
        PortfolioAcquisitionRecoveryState? recovery,
        PortfolioAcquisitionTransfer? transfer,
        string? operationalStatus) =>
        recovery?.Diagnostic ??
        operationalStatus ??
        (transfer is { Lines.Count: > 0 }
            ? "Exact portfolio acquisitions are ready for reviewed staging."
            : null);

    public static bool ResumeReachable(
        PortfolioAcquisitionRecoveryState? recovery,
        bool buildQueueIdle,
        bool sessionBusy) =>
        recovery?.Status == PortfolioAcquisitionRecoveryStatus.Failed && buildQueueIdle && !sessionBusy;
}
