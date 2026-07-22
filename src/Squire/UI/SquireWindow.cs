using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Squire.Persistence;
using System.Numerics;

namespace Squire.UI;

public sealed class SquireWindow : Window
{
    private readonly PluginConfiguration configuration;
    private readonly Action save;
    private readonly LegacyMmfImporter importer;
    private LegacyMmfImportPreview migration;

    public SquireWindow(PluginConfiguration configuration, Action save, LegacyMmfImporter importer)
        : base("Squire###SquireStandalone")
    {
        this.configuration = configuration;
        this.save = save;
        this.importer = importer;
        migration = importer.Preview();
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("SquireWorkspaces"))
            return;
        if (ImGui.BeginTabItem("Overview"))
        {
            DrawOverview();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Cleanup policy"))
        {
            DrawCleanupPolicy();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Integration"))
        {
            DrawIntegration();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawOverview()
    {
        ImGui.TextUnformatted("Standalone Squire");
        ImGui.Separator();
        ImGui.TextWrapped("Squire now has its own plugin identity, configuration, window, and IPC provider.");
        ImGui.Spacing();
        ImGui.TextUnformatted("MarketMafioso migration");
        ImGui.TextWrapped(migration.Message);
        ImGui.TextDisabled($"Cleanup rules: {migration.CleanupRuleCount}   Character rules: {migration.CharacterRuleCount}");
        if (migration.CanImport && ImGui.Button("Import settings from MarketMafioso"))
        {
            migration = importer.Import();
            save();
        }
        if (ImGui.Button("Refresh migration status"))
            migration = importer.Preview();
    }

    private void DrawCleanupPolicy()
    {
        DrawCheckbox("Protect blue and purple gear", configuration.Settings.ProtectBlueAndPurpleGear,
            value => configuration.Settings.ProtectBlueAndPurpleGear = value);
        DrawCheckbox("Protect gear with materia", configuration.Settings.ProtectMateria,
            value => configuration.Settings.ProtectMateria = value);
        DrawCheckbox("Protect player-signed gear", configuration.Settings.ProtectPlayerSignedGear,
            value => configuration.Settings.ProtectPlayerSignedGear = value);
        DrawCheckbox("Protect armoire-eligible gear", configuration.Settings.ProtectArmoireEligible,
            value => configuration.Settings.ProtectArmoireEligible = value);
        DrawCheckbox("Protect future leveling gear", configuration.Settings.ProtectFutureLevelingGearOptIn,
            value => configuration.Settings.ProtectFutureLevelingGearOptIn = value);
        ImGui.Spacing();
        ImGui.TextDisabled($"{configuration.Settings.CleanupRules.Count} configurable cleanup rules; " +
                           $"{configuration.Settings.RulesByCharacter.Values.Sum(rules => rules.Count)} character rules.");
    }

    private void DrawIntegration()
    {
        ImGui.TextWrapped("Other plugins communicate with Squire through versioned IPC channels; they do not load Squire's implementation assembly.");
        ImGui.BulletText("Squire.v1.GetCapabilities");
        ImGui.BulletText("Squire.v1.GetSnapshot");
        ImGui.BulletText("Squire.v1.Open");
        ImGui.BulletText("Squire.v1.Changed");
    }

    private void DrawCheckbox(string label, bool current, Action<bool> update)
    {
        var value = current;
        if (!ImGui.Checkbox(label, ref value))
            return;
        update(value);
        save();
    }
}

