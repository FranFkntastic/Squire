namespace MarketMafioso.Squire.Outfitter.Portfolio;

public enum PortfolioAcquisitionRecoveryTriggerKind
{
    WindowOpened,
    WorkspaceReturned,
    InventoryEvidenceChanged,
}

public sealed record PortfolioAcquisitionRecoveryTriggerState(
    string? LastInventoryEvidenceGeneration,
    DateTimeOffset? DueAtUtc,
    bool AttemptInFlight);

public static class PortfolioAcquisitionRecoveryTrigger
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    public static PortfolioAcquisitionRecoveryTriggerState Empty { get; } = new(null, null, false);

    public static PortfolioAcquisitionRecoveryTriggerState Schedule(
        PortfolioAcquisitionRecoveryTriggerState state,
        PortfolioAcquisitionRecoveryTriggerKind kind,
        DateTimeOffset nowUtc,
        string? inventoryEvidenceGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (kind == PortfolioAcquisitionRecoveryTriggerKind.InventoryEvidenceChanged &&
            (string.IsNullOrWhiteSpace(inventoryEvidenceGeneration) ||
             string.Equals(state.LastInventoryEvidenceGeneration, inventoryEvidenceGeneration, StringComparison.Ordinal)))
            return state;
        return state with
        {
            LastInventoryEvidenceGeneration = kind == PortfolioAcquisitionRecoveryTriggerKind.InventoryEvidenceChanged
                ? inventoryEvidenceGeneration
                : state.LastInventoryEvidenceGeneration,
            DueAtUtc = nowUtc + Debounce,
        };
    }

    public static bool IsDue(PortfolioAcquisitionRecoveryTriggerState state, DateTimeOffset nowUtc) =>
        !state.AttemptInFlight && state.DueAtUtc is { } due && nowUtc >= due;

    public static PortfolioAcquisitionRecoveryTriggerState Begin(PortfolioAcquisitionRecoveryTriggerState state) =>
        state with { DueAtUtc = null, AttemptInFlight = true };

    public static PortfolioAcquisitionRecoveryTriggerState Complete(PortfolioAcquisitionRecoveryTriggerState state) =>
        state with { AttemptInFlight = false };
}
