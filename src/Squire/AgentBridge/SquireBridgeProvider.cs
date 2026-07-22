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
    private static readonly IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> ReviewSurfaces =
    [
        new("squire", "Squire", "open-main-window", "squire", 10),
    ];

    private readonly Func<SquireBridgeTruth> createTruth;
    private readonly Action openMainWindow;
    private readonly Action closeMainWindow;

    public SquireBridgeProvider(Func<SquireBridgeTruth> createTruth, Action openMainWindow, Action closeMainWindow)
    {
        this.createTruth = createTruth;
        this.openMainWindow = openMainWindow;
        this.closeMainWindow = closeMainWindow;
    }

    public SquireBridgeTruth CreateTruth() => createTruth();
    public IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> GetReviewSurfaces() => ReviewSurfaces;

    public bool TryOpenMainWindow(string target)
    {
        if (!string.Equals(target, "squire", StringComparison.Ordinal))
            return false;
        openMainWindow();
        return true;
    }

    public void CloseMainWindow() => closeMainWindow();
}

