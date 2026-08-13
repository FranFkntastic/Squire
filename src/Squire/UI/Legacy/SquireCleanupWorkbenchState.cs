using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.Squire;

namespace MarketMafioso.Windows.Squire;

internal sealed class SquireCleanupWorkbenchState
{
    public SquireCleanupWorkbenchState(string search = "", bool showProtected = false, bool showNonEquipment = false)
    {
        Search = search;
        ShowProtected = showProtected;
        ShowNonEquipment = showNonEquipment;
        Filter.SetExpression(search);
    }

    public SquireCandidateFilter Filter { get; } = new();
    public SquireReviewState Review { get; } = new();
    public TableSelectionModel<EquipmentInstanceFingerprint> TableSelection { get; } =
        new(EquipmentInstanceFingerprintComparer.Instance);
    public string[] ColumnFilters { get; } = new string[SquireCandidateTableProjection.ColumnCount];
    public string Search { get; set; }
    public bool ShowProtected { get; set; }
    public bool ShowNonEquipment { get; set; }
    public bool SelectionMode { get; set; }
    public bool ShowBatchOnly { get; set; }
    public int HiddenBatchCount { get; set; }
    public EquipmentInstanceFingerprint? FocusedItem { get; set; }
}

internal static class SquireCleanupRunAuthorization
{
    public static bool Resolve(
        bool deterministicReviewActive,
        bool snapshotComplete,
        int selectionCount,
        bool supportedBatch,
        int hiddenSelectionCount,
        bool validationSucceeded,
        bool running) =>
        !deterministicReviewActive &&
        snapshotComplete &&
        selectionCount > 0 &&
        supportedBatch &&
        hiddenSelectionCount == 0 &&
        validationSucceeded &&
        !running;
}

internal readonly record struct SquireCleanupSyntheticReviewEntry(
    bool Enabled,
    bool InvalidateLiveAuthorization,
    string Status);

internal static class SquireCleanupSyntheticReviewAuthority
{
    public static SquireCleanupSyntheticReviewEntry ResolveEntry(
        bool deterministicReviewActive,
        bool liveRunActive,
        bool liveRecoveryActive)
    {
        if (deterministicReviewActive)
            return new(true, false, "synthetic");
        if (liveRunActive)
            return new(false, false, "A cleanup run is active. Cancel it or wait for it to finish before loading deterministic review.");
        if (liveRecoveryActive)
            return new(false, false, "Cleanup interaction recovery is active. Wait for it to finish before loading deterministic review.");
        return new(true, true, "live");
    }

    public static bool AllowsLiveRunMutation(bool deterministicReviewActive) => !deterministicReviewActive;

    public static bool AllowsScenarioMutation(bool deterministicReviewActive) => deterministicReviewActive;
}
