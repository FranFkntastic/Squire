using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Styling;
using MarketMafioso.Squire.Outfitter.Utility;
using MarketMafioso.Windows.Main;
using Squire.UI;

namespace MarketMafioso.Windows.Squire;

internal static class AdvisorWorkspaceComponentRenderer
{
    public static void DrawHeader(AdvisorWorkspacePresentation presentation) =>
        DalamudUiChrome.DrawSectionHeading(
            "Gear upgrades",
            presentation.CharacterAvailable
                ? $"{presentation.CharacterLabel} · {presentation.FamilyLabel} · {presentation.ContextLabel}"
                : "Equipment advice from current character evidence",
            SquireUiTheme.Current.Palette);

    public static void DrawSessionStatus(
        MinerBotanistAdvisorSessionState state,
        AdvisorWorkspacePresentation presentation)
    {
        if (!presentation.ShowSessionStatus)
            return;

        DalamudUiChrome.DrawStatusFact(
            "Advisor",
            presentation.SessionLabel,
            SquireUiTheme.Current.Palette,
            Tone(presentation.SessionTone));
        if (!string.IsNullOrWhiteSpace(presentation.SessionMessage))
            ImGui.TextWrapped(presentation.SessionMessage);
        if (state.Stage == MinerBotanistAdvisorSessionStage.Complete && !string.IsNullOrWhiteSpace(state.CoverageLabel))
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, state.CoverageLabel);
        if (!presentation.ShowProgress)
            return;

        var fraction = state.Total is > 0 ? Math.Clamp((float)state.Completed / state.Total.Value, 0f, 1f) : 0f;
        ImGui.ProgressBar(fraction, new Vector2(-1, 0), state.Total is > 0
            ? $"{state.Completed:N0} / {state.Total:N0}"
            : string.Empty);
    }

    public static bool DrawEmptyState(
        AdvisorWorkspacePresentation presentation,
        Action? drawRecoveryActions = null)
    {
        if (presentation.ShowRecoveryCallout)
        {
            DalamudUiChrome.DrawCallout(
                "SquireAdvisorAvailability",
                presentation.RecoveryTitle!,
                presentation.RecoveryMessage,
                SquireUiTheme.Current,
                presentation.CharacterAvailable ? DalamudUiTone.Warning : DalamudUiTone.Neutral,
                drawRecoveryActions);
            return false;
        }

        if (!presentation.ShowEmptyIntroduction)
            return false;

        ImGui.Dummy(new Vector2(0, 34f));
        ImGui.TextColored(MarketMafiosoUiTheme.Header, presentation.EmptyTitle);
        ImGui.TextWrapped(presentation.EmptyMessage);
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Nothing is purchased or equipped unless you explicitly continue with an upgrade list.");
        ImGui.Spacing();
        return ImGuiUi.PrimaryButton($"{presentation.PrimaryActionLabel}##SquireAdvisor", presentation.CanEvaluate);
    }

    private static DalamudUiTone Tone(AdvisorWorkspaceTone tone) => tone switch
    {
        AdvisorWorkspaceTone.Progress => DalamudUiTone.Accent,
        AdvisorWorkspaceTone.Success => DalamudUiTone.Success,
        AdvisorWorkspaceTone.Warning => DalamudUiTone.Warning,
        AdvisorWorkspaceTone.Error => DalamudUiTone.Error,
        _ => DalamudUiTone.Neutral,
    };
}
