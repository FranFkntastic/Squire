using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.AgentBridge;
using MarketMafioso.Diagnostics;
using MarketMafioso.Squire;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.Squire;

internal sealed class SquireRouteDiagnosticsPanel(
    ISquireActionGameAdapter actionAdapter,
    AgentBridgeUiReviewRegistry reviewRegistry,
    UiStateCaptureService uiStateCapture) : IDisposable
{
    private CancellationTokenSource? cancellation;
    private Task<SquireActionResult>? probe;
    private bool ownsCapture;
    private string status = "Focus one Squire row to inspect its non-destructive route probes.";

    public void Draw(SquireAnalysis? analysis, EquipmentInstanceFingerprint? focusedItem)
    {
        ObserveCompletedProbe();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Squire route diagnostics");
        var running = probe is { IsCompleted: false };
        var displayedStatus = running && !string.IsNullOrWhiteSpace(actionAdapter.DiagnosticStatus)
            ? actionAdapter.DiagnosticStatus
            : status;
        ImGui.TextWrapped(displayedStatus);

        if (analysis is null || focusedItem is not { } fingerprint)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted,
                "Focus one row in Squire, then return here. Probes open and inspect normal game UI but never confirm a destructive action.");
            return;
        }

        var candidate = analysis.Candidates.FirstOrDefault(value =>
            EquipmentInstanceFingerprintComparer.Instance.Equals(value.Instance.Fingerprint, fingerprint));
        if (candidate is null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Error,
                "The focused Squire row is no longer present in the current snapshot.");
            return;
        }

        ImGui.TextUnformatted($"{candidate.Definition.Name} — {SquirePresentation.FormatLocation(fingerprint)}");
        foreach (var disposition in candidate.SupportedDispositions.OrderBy(SquireDispositionBatching.ExecutionOrder))
        {
            if (disposition is SquireDisposition.Keep or SquireDisposition.Unsupported)
                continue;
            if (running)
                ImGui.BeginDisabled();
            if (ImGui.Button($"Probe {SquirePresentation.FormatDisposition(disposition)}##SquireDiagnostic{disposition}"))
                Start(candidate, disposition);
            RegisterLastControl(
                $"squire.diagnostics.probe.{disposition}",
                $"Probe {SquirePresentation.FormatDisposition(disposition)} for {candidate.Definition.Name}",
                AgentBridgeUiControlKind.Button,
                !running,
                false,
                displayedStatus,
                () => Start(candidate, disposition));
            if (running)
                ImGui.EndDisabled();
            ImGui.SameLine();
        }
        ImGui.NewLine();

        if (running)
        {
            if (ImGui.Button("Cancel Squire route probe"))
                cancellation?.Cancel();
            RegisterLastControl(
                "squire.diagnostics.cancel-probe",
                "Cancel the active Squire route probe",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                displayedStatus,
                () => cancellation?.Cancel());
            return;
        }

        if (ImGui.Button("Close visible Squire route UI"))
            CloseVisibleUi();
        RegisterLastControl(
            "squire.diagnostics.close-ui",
            "Close visible game UI used by Squire route diagnostics",
            AgentBridgeUiControlKind.Button,
            true,
            false,
            status,
            CloseVisibleUi);
    }

    private void ObserveCompletedProbe()
    {
        if (probe is not { IsCompleted: true })
            return;
        string resultCode;
        string resultMessage;
        try
        {
            var result = probe.GetAwaiter().GetResult();
            resultCode = result.Code;
            resultMessage = result.Message;
        }
        catch (Exception ex)
        {
            resultCode = "ProbeFailed";
            resultMessage = ex.Message;
        }
        status = $"{resultCode}: {resultMessage}";
        if (ownsCapture)
        {
            uiStateCapture.Mark("squire-probe-result", new Dictionary<string, string?>
            {
                ["code"] = resultCode,
                ["message"] = resultMessage,
            });
            var capturePath = uiStateCapture.Stop();
            if (!string.IsNullOrWhiteSpace(capturePath))
                status += $" Capture: {Path.GetFileName(capturePath)}";
            ownsCapture = false;
        }
        probe = null;
        cancellation?.Dispose();
        cancellation = null;
    }

    private void Start(SquireCandidate candidate, SquireDisposition disposition)
    {
        if (probe is { IsCompleted: false })
            return;
        var fingerprint = candidate.Instance.Fingerprint;
        cancellation = new CancellationTokenSource();
        status = $"Probing {SquirePresentation.FormatDisposition(disposition)} through normal game UI...";
        ownsCapture = uiStateCapture.StartSquireProbe(fingerprint.Container, fingerprint.SlotIndex);
        if (ownsCapture)
            uiStateCapture.Mark("squire-probe-start", new Dictionary<string, string?>
            {
                ["disposition"] = disposition.ToString(),
                ["container"] = fingerprint.Container,
                ["slotIndex"] = fingerprint.SlotIndex.ToString(),
                ["itemId"] = fingerprint.ItemId.ToString(),
                ["itemName"] = candidate.Definition.Name,
                ["assessment"] = candidate.Assessment.ToString(),
                ["recommendedDisposition"] = candidate.RecommendedDisposition.ToString(),
                ["supportedDispositions"] = string.Join(",", candidate.SupportedDispositions.OrderBy(SquireDispositionBatching.ExecutionOrder)),
                ["equipmentSlot"] = candidate.Definition.Slot.ToString(),
                ["equipLevel"] = candidate.Definition.EquipLevel.ToString(),
                ["itemLevel"] = candidate.Definition.ItemLevel.ToString(),
                ["rarity"] = candidate.Definition.NormalizedRarity.ToString(),
                ["highQuality"] = fingerprint.IsHighQuality.ToString(),
                ["condition"] = fingerprint.Condition.ToString(),
                ["spiritbond"] = fingerprint.Spiritbond.ToString(),
                ["materiaIds"] = string.Join(",", fingerprint.MateriaIds),
                ["glamourId"] = fingerprint.GlamourId?.ToString(),
            });
        probe = RunProbeAsync(fingerprint, disposition, cancellation.Token);
    }

    private async Task<SquireActionResult> RunProbeAsync(
        EquipmentInstanceFingerprint fingerprint,
        SquireDisposition disposition,
        CancellationToken cancellationToken)
    {
        try
        {
            var recovery = await actionAdapter.RecoverExecutionStateAsync(cancellationToken).ConfigureAwait(false);
            if (!recovery.Success)
                return recovery;
            return await actionAdapter.ProbeAsync(fingerprint, disposition, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            actionAdapter.ReleaseOwnedState();
        }
    }

    private void CloseVisibleUi()
    {
        actionAdapter.CloseDiagnosticUi();
        status = "Requested closure of visible Squire route UI.";
    }

    private void RegisterLastControl(
        string id,
        string label,
        AgentBridgeUiControlKind kind,
        bool enabled,
        bool selected,
        string? value,
        Action invoke) =>
        reviewRegistry.RegisterLastItem(id, label, kind, enabled, selected, value, invoke);

    public void Dispose()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (ownsCapture)
            uiStateCapture.Stop();
    }
}
