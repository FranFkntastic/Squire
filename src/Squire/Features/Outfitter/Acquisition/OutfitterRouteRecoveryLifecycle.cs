using System;

namespace MarketMafioso.Squire.Outfitter.Acquisition;

public static class OutfitterRouteRecoveryLifecycle
{
    public static bool ClearOrphanedState(
        bool isRouteActive,
        OutfitterRouteExecutionState? persisted,
        OutfitterExecutionContract? finalizedContract,
        IOutfitterRouteExecutionStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (isRouteActive || persisted is null ||
            (finalizedContract is not null &&
             finalizedContract.ContractId == persisted.ContractId &&
             finalizedContract.CanonicalIntentHash == persisted.CanonicalIntentHash))
            return false;

        store.Clear();
        return true;
    }

    public static bool CanAutoResume(OutfitterRouteExecutionState? state) =>
        state?.Phase is OutfitterRouteAuthorityPhase.Active or
            OutfitterRouteAuthorityPhase.Preparing or
            OutfitterRouteAuthorityPhase.RecoveryNeeded;

    public static OutfitterRouteExecutionState PauseUnavailable(
        OutfitterRouteExecutionState state,
        string message,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return state with
        {
            Phase = OutfitterRouteAuthorityPhase.Paused,
            Message = message,
            UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow,
        };
    }
}
