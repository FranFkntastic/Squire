using Franthropy.Dalamud.AgentBridge;
using System.Text.Json;

namespace Squire.AgentBridge;

public sealed record SquireBridgeTruth(
    int SchemaVersion,
    string PluginInstanceId,
    int ProcessId,
    string PluginVersion,
    bool MainWindowOpen,
    string FeatureState,
    string Workspace,
    int CleanupRuleCount,
    int CharacterRuleCount,
    DateTimeOffset? LegacyMmfImportedAtUtc,
    SquireBridgeProductTruth Product);

public sealed record SquireBridgeProductTruth(
    string CleanupSurfaceState,
    DateTimeOffset? SnapshotCapturedAtUtc,
    bool SnapshotComplete,
    int CandidateCount,
    int ExecutableCandidateCount,
    int SelectedBatchCount,
    int HiddenSelectedBatchCount,
    bool RunConfirmed,
    bool RunActive,
    string AdvisorStage,
    string AdvisorMessage,
    int AdvisorCompleted,
    int? AdvisorTotal,
    DateTimeOffset AdvisorUpdatedAtUtc,
    string AdvisorTargetKey,
    string AdvisorTargetKind,
    string AdvisorTargetLabel,
    int AdvisorTargetCount,
    int AdvisorReadyTargetCount,
    string? OperationalStatusKind,
    string? OperationalStatusSource,
    string? OperationalStatusMessage,
    DateTimeOffset? OperationalStatusCreatedAtUtc,
    DateTimeOffset? OperationalStatusExpiresAtUtc,
    string CandidateFilterExpression,
    bool CandidateFilterValid,
    int VisibleCandidateCount,
    SquireBridgeSettingsTruth Settings);

public sealed record SquireBridgeSettingsTruth(
    string SelectedPage,
    bool ProtectBlueAndPurpleGear,
    bool ProtectMateria,
    bool ProtectPlayerSignedGear,
    bool ProtectArmoireEligible,
    int AuditRetentionDays,
    bool ProtectFutureLevelingGearOptIn,
    bool AllowRiskyMateriaRetrieval,
    bool RecoverFromKnockout,
    bool WaitForCombatToEnd,
    int CombatRecoveryTimeoutSeconds,
    bool LeaveDutyToExecute,
    bool PauseGatherBuddyReborn,
    bool PauseQuestionable,
    bool PauseArtisan,
    bool CloseSafeUserMenus,
    bool RouteDiagnosticsVisible,
    bool AdvisorFixturesEnabled,
    bool AgentBridgeAuditEnabled);

public sealed class SquireBridgeProvider
{
    private static readonly IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor> CaptureSurfaces =
    [
        new("squire.main-window", "Squire window", 10, IsDefault: true),
    ];
    private static readonly IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> ReviewSurfaces =
    [
        new("squire", "Squire", "open-main-window", "squire", 10),
    ];

    private readonly Func<SquireBridgeTruth> createTruth;
    private readonly Action openMainWindow;
    private readonly Action closeMainWindow;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    public SquireBridgeProvider(
        Func<SquireBridgeTruth> createTruth,
        Action openMainWindow,
        Action closeMainWindow,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.createTruth = createTruth;
        this.openMainWindow = openMainWindow;
        this.closeMainWindow = closeMainWindow;
        this.reviewRegistry = reviewRegistry;
    }

    public SquireBridgeTruth CreateTruth() => createTruth();
    public IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> GetReviewSurfaces() => ReviewSurfaces;
    public IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor> GetCaptureSurfaces() => CaptureSurfaces;
    public AgentBridgeUiReviewFrame GetControlSurface() => reviewRegistry.Snapshot();
    public AgentBridgeUiControlReview ReviewControl(string controlId) => reviewRegistry.Review(controlId);
    public AgentBridgeUiControlInvocation InvokeControl(
        string controlId,
        long frameId,
        JsonElement? arguments = null) =>
        reviewRegistry.Invoke(controlId, frameId, arguments);

    public bool TryOpenMainWindow(string target)
    {
        if (!string.Equals(target, "squire", StringComparison.Ordinal))
            return false;
        openMainWindow();
        return true;
    }

    public void CloseMainWindow() => closeMainWindow();
}
