using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Squire.Persistence;
using System.Numerics;
using MarketMafioso.Windows.Squire;
using Franthropy.Dalamud.UI.Styling;

namespace Squire.UI;

internal sealed class SquireWindow : Window
{
    private readonly Action save;
    private readonly LegacyMmfImporter importer;
    private LegacyMmfImportPreview migration;
    private readonly SquireTabPanel featurePanel;
    private readonly Franthropy.Dalamud.AgentBridge.AgentBridgeUiReviewRegistry reviewRegistry;

    public SquireWindow(
        Action save,
        LegacyMmfImporter importer,
        SquireTabPanel featurePanel,
        Franthropy.Dalamud.AgentBridge.AgentBridgeUiReviewRegistry? reviewRegistry = null)
        : base("Squire###SquireStandalone")
    {
        this.save = save;
        this.importer = importer;
        this.featurePanel = featurePanel;
        this.reviewRegistry = reviewRegistry ?? new();
        migration = importer.Preview();
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        reviewRegistry.BeginFrame();
        featurePanel.Draw();
        if (migration.CanImport)
            DrawMigrationRecovery();
        reviewRegistry.EndFrame();
    }

    private void DrawMigrationRecovery()
    {
        ImGui.Separator();
        DalamudUiChrome.DrawCallout(
            "SquireLegacyImport",
            "Previous Squire settings are available",
            migration.Message,
            SquireUiTheme.Current,
            DalamudUiTone.Warning,
            () =>
            {
                if (!DalamudUiControls.Button(
                        "Import previous settings",
                        SquireUiTheme.Current,
                        DalamudUiTone.Warning))
                    return;
                migration = importer.Import();
                save();
            });
    }
}
