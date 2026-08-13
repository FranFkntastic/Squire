using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Settings;
using Franthropy.Dalamud.UI.Styling;
using Squire.AgentBridge;

namespace Squire.UI;

internal sealed class SquireSettingsPanel
{
    private const string SafetyPageId = "safety";
    private const string CleanupPageId = "cleanup";
    private const string RecoveryPageId = "recovery";
    private const string IntegrationsPageId = "integrations";
    private const string DeveloperPageId = "developer";
    private readonly SquireSettingsState state;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Action openCleanupRules;
    private readonly Action drawRouteDiagnostics;
    private readonly SettingsNavigationState navigation = new(SafetyPageId, ["Settings"]);
    private readonly DalamudSettingsTreeRenderer renderer = new("Squire");
    private readonly SettingsNavigationCatalog catalog;

    public SquireSettingsPanel(
        SquireSettingsState state,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Action openCleanupRules,
        Action drawRouteDiagnostics)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        this.openCleanupRules = openCleanupRules ?? throw new ArgumentNullException(nameof(openCleanupRules));
        this.drawRouteDiagnostics = drawRouteDiagnostics ?? throw new ArgumentNullException(nameof(drawRouteDiagnostics));
        catalog = new SettingsNavigationCatalog(
        [
            new(SafetyPageId, "Settings / Safety", _ => DrawSafety(), 10, searchTerms: ["protection", "rarity", "materia", "armoire", "audit"]),
            new(CleanupPageId, "Settings / Cleanup", _ => DrawCleanup(), 20, searchTerms: ["leveling", "rules", "materia retrieval"]),
            new(RecoveryPageId, "Settings / Recovery", _ => DrawRecovery(), 30, searchTerms: ["knockout", "combat", "duty"]),
            new(IntegrationsPageId, "Settings / Integrations", _ => DrawIntegrations(), 40, searchTerms: ["GatherBuddy", "Questionable", "Artisan", "menus"]),
            new(DeveloperPageId, "Settings / Developer", _ => DrawDeveloper(), 50, searchTerms: ["diagnostics", "fixtures", "audit"]),
        ]);
    }

    public string SelectedPageId => catalog.ResolveSelectedPage(navigation)?.Id ?? SafetyPageId;

    public SquireBridgeSettingsTruth CreateBridgeTruth() => new(
        SelectedPageId,
        state.Policy.ProtectBlueAndPurpleGear,
        state.Policy.ProtectMateria,
        state.Policy.ProtectPlayerSignedGear,
        state.Policy.ProtectArmoireEligible,
        state.Policy.AuditRetentionDays,
        state.Policy.ProtectFutureLevelingGearOptIn,
        state.Policy.AllowRiskyMateriaRetrieval,
        state.Policy.RecoverFromKnockout,
        state.Policy.WaitForCombatToEnd,
        state.Policy.CombatRecoveryTimeoutSeconds,
        state.Policy.LeaveDutyToExecute,
        state.Policy.PauseGatherBuddyReborn,
        state.Policy.PauseQuestionable,
        state.Policy.PauseArtisan,
        state.Policy.CloseSafeUserMenus,
        state.RouteDiagnosticsVisible,
        state.AdvisorFixturesEnabled,
        state.AgentBridgeAuditEnabled);

    public void Draw()
    {
        renderer.Draw(catalog, navigation, SquireUiTheme.Current);
        foreach (var control in renderer.RenderedPageControls)
        {
            var pageId = control.Id;
            reviewRegistry.Register(
                PageControlId(pageId),
                $"Open {control.Label}",
                AgentBridgeUiControlKind.Select,
                control.Min,
                control.Max,
                true,
                control.Selected,
                pageId,
                () => navigation.SelectPage(pageId));
        }
    }

    private void DrawSafety()
    {
        DalamudUiChrome.DrawCallout(
            "SquireSettingsSafety",
            "Protected unless you choose otherwise",
            "High-rarity, melded, signed, and armoire-eligible equipment stays protected under the choices below.",
            SquireUiTheme.Current,
            DalamudUiTone.Warning);
        ImGui.Spacing();
        if (!BeginRows("Safety"))
            return;
        DrawPolicyToggle(SquireSettingsControlIds.ProtectBlueAndPurple, "Protect blue and purple equipment", "Prevent cleanup unless an item rule explicitly allows it.", state.Policy.ProtectBlueAndPurpleGear, value => state.UpdatePolicy(settings => settings.ProtectBlueAndPurpleGear = value));
        DrawPolicyToggle(SquireSettingsControlIds.ProtectMateria, "Protect equipment with materia", "Require an explicit retrieval-risk decision before cleanup.", state.Policy.ProtectMateria, value => state.UpdatePolicy(settings => settings.ProtectMateria = value));
        DrawPolicyToggle(SquireSettingsControlIds.ProtectPlayerSigned, "Protect player-signed equipment", "Keep crafted items carrying a player signature.", state.Policy.ProtectPlayerSignedGear, value => state.UpdatePolicy(settings => settings.ProtectPlayerSignedGear = value));
        DrawPolicyToggle(SquireSettingsControlIds.ProtectArmoireEligible, "Protect armoire-eligible equipment", "Keep items that can be stored in the armoire.", state.Policy.ProtectArmoireEligible, value => state.UpdatePolicy(settings => settings.ProtectArmoireEligible = value));
        DrawChoice(SquireSettingsControlIds.AuditRetentionDays, "Audit retention", "How long completed cleanup receipts remain available.", state.Policy.AuditRetentionDays, [14, 30, 90], "days", value => state.UpdatePolicy(settings => settings.AuditRetentionDays = value));
        ImGui.EndTable();
    }

    private void DrawCleanup()
    {
        if (!BeginRows("Cleanup"))
            return;
        DrawPolicyToggle(SquireSettingsControlIds.ProtectFutureLeveling, "Protect possible future leveling gear", "Keep equipment that may suit an unlocked lower-level job.", state.Policy.ProtectFutureLevelingGearOptIn, value => state.UpdatePolicy(settings => settings.ProtectFutureLevelingGearOptIn = value));
        DrawPolicyToggle(SquireSettingsControlIds.AllowRiskyMateriaRetrieval, "Allow risky materia retrieval", "Permit cleanup plans that may require removing materia first.", state.Policy.AllowRiskyMateriaRetrieval, value => state.UpdatePolicy(settings => settings.AllowRiskyMateriaRetrieval = value));
        DrawAction(SquireSettingsControlIds.ReviewRules, "Item-specific rules", "Review direct protections and retained-copy floors in Cleanup.", "Go to cleanup", openCleanupRules);
        ImGui.EndTable();
    }

    private void DrawRecovery()
    {
        if (!BeginRows("Recovery"))
            return;
        DrawPolicyToggle(SquireSettingsControlIds.RecoverFromKnockout, "Recover after knockout", "Wait for recovery and resume only after the route can be revalidated.", state.Policy.RecoverFromKnockout, value => state.UpdatePolicy(settings => settings.RecoverFromKnockout = value));
        DrawPolicyToggle(SquireSettingsControlIds.WaitForCombat, "Wait for combat to end", "Pause instead of fighting for control of the character.", state.Policy.WaitForCombatToEnd, value => state.UpdatePolicy(settings => settings.WaitForCombatToEnd = value));
        DrawChoice(SquireSettingsControlIds.CombatTimeout, "Combat wait limit", "Stop safely if combat lasts longer than this interval.", state.Policy.CombatRecoveryTimeoutSeconds, [60, 90, 120], "seconds", value => state.UpdatePolicy(settings => settings.CombatRecoveryTimeoutSeconds = value));
        DrawPolicyToggle(SquireSettingsControlIds.LeaveDuty, "Leave duties to execute cleanup", "Allow a confirmed batch to exit the current duty before continuing.", state.Policy.LeaveDutyToExecute, value => state.UpdatePolicy(settings => settings.LeaveDutyToExecute = value));
        ImGui.EndTable();
    }

    private void DrawIntegrations()
    {
        if (!BeginRows("Integrations"))
            return;
        DrawPolicyToggle(SquireSettingsControlIds.PauseGatherBuddy, "Pause GatherBuddyReborn", "Yield automation ownership before a confirmed cleanup route begins.", state.Policy.PauseGatherBuddyReborn, value => state.UpdatePolicy(settings => settings.PauseGatherBuddyReborn = value));
        DrawPolicyToggle(SquireSettingsControlIds.PauseQuestionable, "Pause Questionable", "Yield quest automation ownership before cleanup.", state.Policy.PauseQuestionable, value => state.UpdatePolicy(settings => settings.PauseQuestionable = value));
        DrawPolicyToggle(SquireSettingsControlIds.PauseArtisan, "Pause Artisan", "Yield crafting automation ownership before cleanup.", state.Policy.PauseArtisan, value => state.UpdatePolicy(settings => settings.PauseArtisan = value));
        DrawPolicyToggle(SquireSettingsControlIds.CloseSafeMenus, "Close safe user menus", "Dismiss only known recoverable menus that block an authorized route.", state.Policy.CloseSafeUserMenus, value => state.UpdatePolicy(settings => settings.CloseSafeUserMenus = value));
        ImGui.EndTable();
    }

    private void DrawDeveloper()
    {
        DalamudUiChrome.DrawCallout(
            "SquireSettingsDeveloper",
            "Developer tools are isolated",
            "Diagnostics and deterministic fixtures never grant cleanup, purchase, travel, or inventory authority.",
            SquireUiTheme.Current,
            DalamudUiTone.Neutral);
        ImGui.Spacing();
        if (BeginRows("Developer"))
        {
            DrawToggle(SquireSettingsControlIds.RouteDiagnostics, "Route diagnostics", "Inspect safe probes and recent route evidence.", state.RouteDiagnosticsVisible, state.SetRouteDiagnosticsVisible);
            DrawToggle(SquireSettingsControlIds.AdvisorFixtures, "Deterministic advisor fixtures", "Expose reviewed success, stale, incomplete, and abstention states.", state.AdvisorFixturesEnabled, state.SetAdvisorFixturesEnabled);
            DrawToggle(SquireSettingsControlIds.AgentBridgeAudit, "Agent Bridge audit log", "Record authenticated development-control receipts.", state.AgentBridgeAuditEnabled, state.SetAgentBridgeAuditEnabled);
            ImGui.EndTable();
        }
        if (state.RouteDiagnosticsVisible)
        {
            ImGui.Spacing();
            drawRouteDiagnostics();
        }
    }

    private static bool BeginRows(string id)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.TableBorderStrong, SquireUiTheme.Current.Palette.Border)
            .Push(ImGuiCol.TableBorderLight, SquireUiTheme.Current.Palette.Border);
        if (!ImGui.BeginTable($"##SquireSettings{id}", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
            return false;
        ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthStretch, 4f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 170f);
        return true;
    }

    private void DrawPolicyToggle(string id, string label, string description, bool value, Action<bool> update) =>
        DrawToggle(id, label, description, value, update);

    private void DrawToggle(string id, string label, string description, bool value, Action<bool> update)
    {
        BeginRow(label, description);
        ImGui.TableNextColumn();
        var edited = value;
        if (ImGui.Checkbox($"##{id}", ref edited))
            update(edited);
        reviewRegistry.RegisterLastItem(id, label, AgentBridgeUiControlKind.Toggle, true, edited, edited ? "on" : "off", () => update(!edited));
        ImGui.SameLine();
        ImGui.TextUnformatted(edited ? "On" : "Off");
    }

    private void DrawChoice(string id, string label, string description, int value, IReadOnlyList<int> choices, string unit, Action<int> update)
    {
        BeginRow(label, description);
        ImGui.TableNextColumn();
        var displayedValue = value;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo($"##{id}", $"{displayedValue} {unit}"))
        {
            foreach (var choice in choices)
            {
                if (ImGui.Selectable($"{choice} {unit}", choice == displayedValue))
                {
                    update(choice);
                    displayedValue = choice;
                }
            }
            ImGui.EndCombo();
        }
        var currentIndex = choices.IndexOf(displayedValue);
        var next = currentIndex < 0 ? choices[0] : choices[(currentIndex + 1) % choices.Count];
        reviewRegistry.RegisterLastItem(id, label, AgentBridgeUiControlKind.Select, true, true, displayedValue.ToString(), () => update(next));
    }

    private void DrawAction(string id, string label, string description, string actionLabel, Action action)
    {
        BeginRow(label, description);
        ImGui.TableNextColumn();
        if (DalamudUiControls.Button($"{actionLabel}##{id}", SquireUiTheme.Current, DalamudUiTone.Neutral, quiet: true, size: new(-1f, 0f)))
            action();
        reviewRegistry.RegisterLastItem(id, label, AgentBridgeUiControlKind.Button, true, false, null, action);
    }

    private static void BeginRow(string label, string description)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        using var muted = ImRaii.PushColor(ImGuiCol.Text, SquireUiTheme.Current.Palette.Muted);
        ImGui.TextWrapped(description);
    }

    private static string PageControlId(string pageId) => pageId switch
    {
        SafetyPageId => SquireSettingsControlIds.SafetyPage,
        CleanupPageId => SquireSettingsControlIds.CleanupPage,
        RecoveryPageId => SquireSettingsControlIds.RecoveryPage,
        IntegrationsPageId => SquireSettingsControlIds.IntegrationsPage,
        DeveloperPageId => SquireSettingsControlIds.DeveloperPage,
        _ => throw new ArgumentOutOfRangeException(nameof(pageId), pageId, "Unknown settings page."),
    };
}

internal static class ReadOnlyListIndexExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> values, T value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(values[index], value))
                return index;
        }
        return -1;
    }
}
