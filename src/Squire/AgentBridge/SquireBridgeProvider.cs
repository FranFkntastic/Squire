using Franthropy.Dalamud.AgentBridge;
using System.Text.Json;

namespace Squire.AgentBridge;

internal static class SquireBridgeSchema
{
    public const int CurrentVersion = 12;
}

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
    SquireBridgeSettingsTruth Settings,
    string RetainerObservationStage = "Idle",
    string? RetainerObservationTargetKey = null,
    int RetainerObservedSlotCount = 0,
    int RetainerTotalSlotCount = 0,
    bool RetainerEvidenceComplete = false,
    string? RetainerVentureKey = null,
    string? RetainerVentureItemName = null,
    bool PortfolioMode = false,
    string PortfolioBuildStage = "Idle",
    int PortfolioEvaluatedTargetCount = 0,
    bool? PortfolioPlanComplete = null,
    int PortfolioSelectedCandidateCount = 0,
    int PortfolioAllocationConsequenceCount = 0,
    long PortfolioExploredStateCount = 0,
    string? PortfolioDiagnostic = null,
    string? EquipmentExecutionId = null,
    string? EquipmentExecutionStatus = null,
    int EquipmentCompletedStepCount = 0,
    int EquipmentTotalStepCount = 0,
    string? EquipmentNextTargetKey = null,
    string? EquipmentNextPosition = null,
    string? EquipmentStopReason = null,
    string? PortfolioAuthoritySha256 = null,
    IReadOnlyList<SquireBridgePortfolioTargetTruth>? PortfolioTargets = null,
    IReadOnlyList<SquireBridgePortfolioAllocationTruth>? PortfolioAllocations = null,
    IReadOnlyList<SquireBridgePortfolioHandMeDownTruth>? PortfolioHandMeDowns = null,
    string? EquipmentNextItemName = null,
    uint? EquipmentNextItemId = null,
    bool? EquipmentNextItemHighQuality = null,
    string? EquipmentNextSourceInstance = null,
    string? EquipmentTargetActivationStatus = null,
    bool RetainerVentureOutcomeCalibrated = false,
    string? RetainerVentureOutcomeTextSha256 = null,
    uint? RetainerVentureTaskId = null,
    string? RetainerVentureTaskLabel = null,
    string? PortfolioAcquisitionStatus = null,
    int PortfolioAcquisitionLineCount = 0,
    int PortfolioMarketLineCount = 0,
    int PortfolioVendorActionCount = 0,
    int PortfolioCraftHandoffCount = 0,
    string? PortfolioAcquisitionAuthoritySha256 = null,
    string? PortfolioAcquisitionDiagnostic = null,
    IReadOnlyList<SquireBridgePortfolioAcquisitionLineTruth>? PortfolioAcquisitionLines = null,
    bool PortfolioAcquisitionStageReachable = false,
    bool PortfolioAcquisitionResumeReachable = false,
    bool PortfolioVendorConfirmReachable = false,
    bool PortfolioArtisanExportReachable = false);

public sealed record SquireBridgePortfolioAcquisitionLineTruth(
    string LineageKey,
    string SourceKind,
    string Status,
    string TargetKey,
    string CandidateKey,
    uint ItemId,
    string ItemName,
    uint Quantity,
    string NextAction,
    string? Receipt);

public sealed record SquireBridgePortfolioTargetTruth(
    string TargetKey,
    bool Included,
    int Priority,
    string ProgressionHorizon,
    string? SelectedCandidateKey,
    bool EvidenceReady,
    string? EvidenceDiagnostic,
    string? Disposition = null,
    string? DispositionReason = null,
    string? EvidenceGeneration = null);

public sealed record SquireBridgePortfolioAllocationTruth(
    string TargetKey,
    string CandidateKey,
    string SourceKind,
    string SourceKey,
    uint ItemId,
    bool IsHighQuality,
    string? InstanceId,
    uint Quantity);

public sealed record SquireBridgePortfolioHandMeDownTruth(
    string UpstreamTargetKey,
    string DownstreamTargetKey,
    string ReleaseEvidenceGeneration,
    uint ItemId,
    bool IsHighQuality,
    string ReleasedInstanceId);

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
