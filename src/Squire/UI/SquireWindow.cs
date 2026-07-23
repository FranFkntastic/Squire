using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Squire.Persistence;
using System.Numerics;
using MarketMafioso.Windows.Squire;

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
        ImGui.TextColored(new Vector4(0.88f, 0.69f, 0.35f, 1f), "Previous Squire settings are available");
        ImGui.TextWrapped(migration.Message);
        if (ImGui.Button("Import previous settings"))
        {
            migration = importer.Import();
            save();
        }
    }
}
