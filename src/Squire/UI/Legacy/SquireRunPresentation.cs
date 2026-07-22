using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.Squire;

internal sealed record SquireRunPresentation(
    SquireActionPlan Plan,
    SquireRunResult Result,
    string AuditPath,
    IReadOnlyList<SquireReviewedSelection> Completed,
    IReadOnlyList<SquireReviewedSelection> Failed,
    IReadOnlyList<SquireReviewedSelection> Remaining,
    string? FailureMessage)
{
    public static SquireRunPresentation Create(SquireActionPlan plan, SquireRunResult result, string auditPath)
    {
        var stopped = result.Events.LastOrDefault(value => value.Kind == "RunStopped");
        var failedFingerprint = stopped?.Item;
        var completedFingerprints = result.Events
            .Where(value => value.Kind is "ActionResult" or "DiagnosticActionResult")
            .Where(value => failedFingerprint is null || value.Item != failedFingerprint)
            .Select(value => value.Item)
            .OfType<EquipmentInstanceFingerprint>()
            .ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
        var completed = plan.Actions.Where(action => completedFingerprints.Contains(action.Fingerprint)).ToArray();
        var failed = failedFingerprint is { } failedItem
            ? plan.Actions.Where(action => EquipmentInstanceFingerprintComparer.Instance.Equals(action.Fingerprint, failedItem)).ToArray()
            : [];
        var remaining = plan.Actions
            .Where(action => !completedFingerprints.Contains(action.Fingerprint))
            .Where(action => failedFingerprint is null || !EquipmentInstanceFingerprintComparer.Instance.Equals(action.Fingerprint, failedFingerprint))
            .ToArray();
        return new SquireRunPresentation(plan, result, auditPath, completed, failed, remaining, stopped?.Message);
    }

    public IReadOnlyList<SquireReviewedSelection> Retryable => Failed.Concat(Remaining).ToArray();
    public bool NeedsInteractionRecovery => !Result.Success && Completed.Count == Plan.Actions.Count && Retryable.Count == 0;
    public bool WasDiagnostic => Result.Events.Any(value => value.Kind.StartsWith("Diagnostic", StringComparison.Ordinal));
    public SquireActionPlan CreateCheckpointPlan() => Plan with { Actions = Retryable };
}

internal sealed class SquireRunResultPanel
{
    public void Draw(
        SquireRunPresentation presentation,
        Func<uint, string> resolveItemName,
        Action recoverInteraction,
        bool recoveryActive,
        Action retryFromCheckpoint,
        bool retryActive,
        Action dismiss,
        Action openAuditLocation)
    {
        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Header,
            presentation.Result.Success
                ? "Last cleanup run completed"
                : presentation.NeedsInteractionRecovery
                    ? "Cleanup completed; interaction recovery needed"
                    : "Last cleanup run stopped");
        ImGui.TextUnformatted(
            $"Completed {presentation.Completed.Count} | Failed {presentation.Failed.Count} | Remaining {presentation.Remaining.Count} | Audit {Path.GetFileName(presentation.AuditPath)}");
        if (presentation.NeedsInteractionRecovery)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Squire handled every item but could not confirm that the game released its NPC interaction.");
        else if (!string.IsNullOrWhiteSpace(presentation.FailureMessage))
            ImGui.TextColored(MarketMafiosoUiTheme.Error, presentation.FailureMessage.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0]);

        if (presentation.Retryable.Count > 0 &&
            ImGui.BeginTable("##SquireRunRecovery", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 75);
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Location");
            ImGui.TableSetupColumn("Disposition");
            ImGui.TableHeadersRow();
            foreach (var action in presentation.Failed)
                DrawAction("Failed", action, resolveItemName);
            foreach (var action in presentation.Remaining)
                DrawAction("Remaining", action, resolveItemName);
            ImGui.EndTable();
        }

        if (presentation.NeedsInteractionRecovery)
        {
            if (recoveryActive)
                ImGui.BeginDisabled();
            if (ImGui.Button(recoveryActive ? "Recovering interaction..." : "Recover interaction"))
                recoverInteraction();
            if (recoveryActive)
                ImGui.EndDisabled();
            ImGui.SameLine();
        }
        else if (presentation.Retryable.Count > 0)
        {
            if (retryActive)
                ImGui.BeginDisabled();
            if (ImGui.Button(retryActive
                    ? "Retrying from checkpoint..."
                    : $"Retry from checkpoint ({presentation.Retryable.Count} item(s))"))
                retryFromCheckpoint();
            if (retryActive)
                ImGui.EndDisabled();
            ImGui.SameLine();
        }
        if (ImGui.Button("Open audit location"))
            openAuditLocation();
        ImGui.SameLine();
        if (ImGui.Button("Dismiss"))
            dismiss();
    }

    private static void DrawAction(string state, SquireReviewedSelection action, Func<uint, string> resolveItemName)
    {
        ImGui.TableNextRow();
        Cell(state);
        Cell(resolveItemName(action.Fingerprint.ItemId));
        Cell(SquirePresentation.FormatLocation(action.Fingerprint));
        Cell(SquirePresentation.FormatDisposition(action.Disposition));
    }

    private static void Cell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextWrapped(value);
    }
}
