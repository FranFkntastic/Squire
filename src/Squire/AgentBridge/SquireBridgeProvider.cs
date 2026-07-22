using Franthropy.Dalamud.AgentBridge;

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
    DateTimeOffset? LegacyMmfImportedAtUtc);

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
    public AgentBridgeUiControlInvocation InvokeControl(string controlId, long frameId) => reviewRegistry.Invoke(controlId, frameId);

    public bool TryOpenMainWindow(string target)
    {
        if (!string.Equals(target, "squire", StringComparison.Ordinal))
            return false;
        openMainWindow();
        return true;
    }

    public void CloseMainWindow() => closeMainWindow();
}
