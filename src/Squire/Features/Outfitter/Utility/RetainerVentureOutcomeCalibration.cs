using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.AgentBridge;

namespace MarketMafioso.Squire.Outfitter.Utility;

public sealed record RenderedRetainerVentureOutcomeEvidence(
    Guid DefinitionGenerationId,
    string VentureKey,
    RetainerProcurementProfileKind Profile,
    string TargetKey,
    ulong OwnerContentId,
    string OwnerCharacterName,
    string OwnerHomeWorld,
    ulong RetainerId,
    string RetainerName,
    uint TaskId,
    string CalibrationSessionId,
    DateTimeOffset IdentityObservedAtUtc,
    string AskTextSha256,
    int AskTaskValueIndex,
    string AskTaskValueSha256,
    int EligibilityStat,
    int YieldStat,
    int RenderedQuantity,
    string RenderedTextSha256,
    DateTimeOffset CapturedAtUtc);

public sealed record RetainerVentureOutcomeCalibrationResult(
    bool Success,
    RetainerProcurementObjective? Objective,
    string Diagnostic);

public static partial class RetainerVentureOutcomeCalibration
{
    public static RetainerVentureOutcomeCalibrationResult Calibrate(
        RetainerProcurementObjective objective,
        string exactItemName,
        string targetKey,
        ulong ownerContentId,
        string ownerCharacterName,
        string ownerHomeWorld,
        ulong retainerId,
        string retainerName,
        uint taskId,
        string calibrationSessionId,
        DateTimeOffset identityObservedAtUtc,
        string askTextSha256,
        int askTaskValueIndex,
        string askTaskValueSha256,
        RetainerProcurementStats observedStats,
        IReadOnlyList<string> renderedText,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(objective);
        ArgumentNullException.ThrowIfNull(renderedText);
        if (!objective.IsDefinitionComplete || objective.EvidenceGenerationId == Guid.Empty)
            return Fail("The installed venture definition is incomplete.");
        if (capturedAtUtc == default || string.IsNullOrWhiteSpace(exactItemName) ||
            string.IsNullOrWhiteSpace(targetKey) || ownerContentId == 0 ||
            string.IsNullOrWhiteSpace(ownerCharacterName) || string.IsNullOrWhiteSpace(ownerHomeWorld) ||
            retainerId == 0 || string.IsNullOrWhiteSpace(retainerName) || taskId == 0)
            return Fail("A current owner-bound retainer, task, capture time, and exact venture item are required.");
        if (string.IsNullOrWhiteSpace(calibrationSessionId) || identityObservedAtUtc == default ||
            string.IsNullOrWhiteSpace(askTextSha256) || askTaskValueIndex < 0 ||
            string.IsNullOrWhiteSpace(askTaskValueSha256) || capturedAtUtc <= identityObservedAtUtc)
            return Fail("A controlled identity-to-ask-to-result venture session is required.");
        var lines = renderedText.Select(Normalize).Where(value => value.Length > 0).ToArray();
        if (!lines.Any(value => value.Contains(exactItemName.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Fail("The visible venture outcome does not identify the selected exact item.");

        var quantity = FindStat(lines, "items obtained", "quantity", "yield");
        var eligibility = objective.Profile == RetainerProcurementProfileKind.Battle
            ? observedStats.AverageItemLevel
            : observedStats.Gathering;
        var yield = objective.Profile == RetainerProcurementProfileKind.Battle
            ? observedStats.AverageItemLevel
            : observedStats.Perception;
        if (eligibility <= 0 || yield <= 0 || quantity is null)
            return Fail(objective.Profile == RetainerProcurementProfileKind.Battle
                ? "The controlled rendered retainer identity must include item level, and the completed result must include obtained quantity."
                : "The controlled rendered retainer identity must include gathering and perception, and the completed result must include obtained quantity.");

        var predicted = RetainerProcurementOutcomeEvaluator.Evaluate(
            objective,
            objective.Profile == RetainerProcurementProfileKind.Battle
                ? new(eligibility, 0, 0, 0)
                : new(0, eligibility, yield, observedStats.GatheringPoints));
        if (predicted.Status != RetainerProcurementOutcomeStatus.Complete || predicted.Quantity != quantity.Value)
            return Fail($"Installed thresholds predict {predicted.Quantity:N0}, but the rendered venture outcome shows {quantity.Value:N0}; calibration stopped.");

        var canonical = string.Join("\n", lines.OrderBy(value => value, StringComparer.Ordinal));
        var evidence = new RenderedRetainerVentureOutcomeEvidence(
            objective.EvidenceGenerationId,
            objective.VentureKey,
            objective.Profile,
            targetKey.Trim(),
            ownerContentId,
            ownerCharacterName.Trim(),
            ownerHomeWorld.Trim(),
            retainerId,
            retainerName.Trim(),
            taskId,
            calibrationSessionId,
            identityObservedAtUtc,
            askTextSha256,
            askTaskValueIndex,
            askTaskValueSha256,
            eligibility,
            yield,
            quantity.Value,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
            capturedAtUtc);
        return new(true, objective with { RenderedOutcomeEvidence = evidence },
            "The installed thresholds reproduce the current rendered venture outcome exactly.");
    }

    public static bool IsValid(RetainerProcurementObjective objective) =>
        objective.RenderedOutcomeEvidence is { } evidence &&
        evidence.DefinitionGenerationId == objective.EvidenceGenerationId &&
        string.Equals(evidence.VentureKey, objective.VentureKey, StringComparison.Ordinal) &&
        evidence.Profile == objective.Profile &&
        !string.IsNullOrWhiteSpace(evidence.TargetKey) && evidence.OwnerContentId != 0 &&
        !string.IsNullOrWhiteSpace(evidence.OwnerCharacterName) && !string.IsNullOrWhiteSpace(evidence.OwnerHomeWorld) &&
        evidence.RetainerId != 0 && !string.IsNullOrWhiteSpace(evidence.RetainerName) && evidence.TaskId != 0 &&
        !string.IsNullOrWhiteSpace(evidence.CalibrationSessionId) && evidence.IdentityObservedAtUtc != default &&
        !string.IsNullOrWhiteSpace(evidence.AskTextSha256) && evidence.AskTaskValueIndex >= 0 &&
        !string.IsNullOrWhiteSpace(evidence.AskTaskValueSha256) && evidence.CapturedAtUtc > evidence.IdentityObservedAtUtc &&
        evidence.CapturedAtUtc != default &&
        evidence.RenderedQuantity > 0 &&
        !string.IsNullOrWhiteSpace(evidence.RenderedTextSha256) &&
        RetainerProcurementOutcomeEvaluator.Evaluate(
            objective,
            objective.Profile == RetainerProcurementProfileKind.Battle
                ? new(evidence.EligibilityStat, 0, 0, 0)
                : new(0, evidence.EligibilityStat, evidence.YieldStat, 0)) is
            { Status: RetainerProcurementOutcomeStatus.Complete } predicted &&
        predicted.Quantity == evidence.RenderedQuantity;

    private static int? FindStat(IReadOnlyList<string> lines, params string[] labels)
    {
        foreach (var line in lines)
        foreach (var label in labels)
        {
            var index = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;
            var match = Number().Match(line[(index + label.Length)..]);
            if (match.Success && int.TryParse(match.Value.Replace(",", string.Empty), out var value))
                return value;
        }
        return null;
    }

    private static string Normalize(string value) =>
        Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();

    private static RetainerVentureOutcomeCalibrationResult Fail(string diagnostic) => new(false, null, diagnostic);

    [GeneratedRegex(@"\d[\d,]*", RegexOptions.CultureInvariant)]
    private static partial Regex Number();
}

public sealed class DalamudRetainerVentureOutcomeProbe
{
    private readonly IGameGui gameGui;
    private readonly DalamudRenderedUiTextActionDispatcher dispatcher;
    private CalibrationSession? session;

    public DalamudRetainerVentureOutcomeProbe(IGameGui gameGui)
    {
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        dispatcher = new(gameGui);
    }

    public RetainerVentureOutcomeCalibrationResult Capture(
        RetainerProcurementObjective objective,
        string exactItemName,
        string targetKey,
        ulong ownerContentId,
        string ownerCharacterName,
        string ownerHomeWorld,
        ulong retainerId,
        string retainerName,
        uint taskId,
        bool renderedRetainerIdentityProven,
        DateTimeOffset renderedIdentityObservedAtUtc,
        RetainerProcurementStats renderedStats,
        DateTimeOffset capturedAtUtc)
    {
        var expected = new CalibrationIdentity(
            objective.EvidenceGenerationId, targetKey, ownerContentId, ownerCharacterName, ownerHomeWorld,
            retainerId, retainerName, taskId, exactItemName);
        if (session is not null && session.Identity != expected)
            session = null;
        if (session is not null && !RetainerVentureCalibrationContinuity.IsCurrent(
                retainerId,
                CurrentRetainerId(),
                session.IdentityObservedAtUtc,
                capturedAtUtc))
            session = null;

        var ask = dispatcher.CaptureVisibleText("RetainerTaskAsk");
        var result = dispatcher.CaptureVisibleText("RetainerTaskResult");
        if (result.Available)
        {
            if (session is not
                {
                    AskTextSha256: not null,
                    AskTaskValueIndex: not null,
                    AskTaskValueSha256: not null,
                } active)
                return new(false, null, "This completed result has no controlled exact-retainer ask session; it cannot calibrate the selected target.");
            if (!RetainerVentureCalibrationContinuity.IsCurrent(
                    retainerId, CurrentRetainerId(), active.IdentityObservedAtUtc, capturedAtUtc))
            {
                session = null;
                return new(false, null, "The active retainer changed before the completed result; the controlled session was discarded.");
            }
            var calibrated = RetainerVentureOutcomeCalibration.Calibrate(
                objective,
                exactItemName,
                targetKey,
                ownerContentId,
                ownerCharacterName,
                ownerHomeWorld,
                retainerId,
                retainerName,
                taskId,
                active.SessionId,
                active.IdentityObservedAtUtc,
                active.AskTextSha256,
                active.AskTaskValueIndex!.Value,
                active.AskTaskValueSha256!,
                active.RenderedStats,
                result.TextNodes.Select(value => value.Text).ToArray(),
                capturedAtUtc);
            if (calibrated.Success)
                session = null;
            return calibrated;
        }

        if (ask.Available)
        {
            if (session is null)
                return new(false, null, "Observe the exact rendered retainer identity before opening the venture ask screen.");
            if (!RetainerVentureCalibrationContinuity.IsCurrent(
                    retainerId, CurrentRetainerId(), session.IdentityObservedAtUtc, capturedAtUtc))
            {
                session = null;
                return new(false, null, "The active retainer changed before the venture ask; the controlled session was discarded.");
            }
            var askLines = ask.TextNodes.Select(value => value.Text.Trim()).Where(value => value.Length > 0).ToArray();
            if (!askLines.Any(value => value.Contains(exactItemName, StringComparison.OrdinalIgnoreCase)))
            {
                session = null;
                return new(false, null, "The rendered venture ask does not identify the selected exact item; the controlled session was discarded.");
            }
            if (!TryCaptureRenderedTaskValue("RetainerTaskAsk", taskId, out var taskValueIndex, out var taskValueHash))
            {
                session = null;
                return new(false, null, "The rendered venture ask does not expose the selected exact task identity; the controlled session was discarded.");
            }
            session = session with
            {
                AskTextSha256 = HashLines(askLines),
                AskTaskValueIndex = taskValueIndex,
                AskTaskValueSha256 = taskValueHash,
            };
            return new(false, null, "The exact venture ask is bound. Complete it, then verify the rendered result.");
        }

        if (!renderedRetainerIdentityProven || renderedIdentityObservedAtUtc == default)
        {
            session = null;
            return new(false, null, "Open the exact selected retainer's rendered attributes and gear before starting outcome verification.");
        }
        if (!RetainerVentureCalibrationContinuity.IsCurrent(
                retainerId, CurrentRetainerId(), renderedIdentityObservedAtUtc, capturedAtUtc))
        {
            session = null;
            return new(false, null, "The active retainer manager identity does not match the rendered selected retainer.");
        }
        var renderedStatsComplete = objective.Profile == RetainerProcurementProfileKind.Battle
            ? renderedStats.AverageItemLevel > 0
            : renderedStats.Gathering > 0 && renderedStats.Perception > 0;
        if (!renderedStatsComplete)
        {
            session = null;
            return new(false, null, objective.Profile == RetainerProcurementProfileKind.Battle
                ? "The exact rendered retainer identity does not expose a current average item level."
                : "The exact rendered retainer identity does not expose current gathering and perception.");
        }
        session = new(Guid.NewGuid().ToString("N"), expected, renderedIdentityObservedAtUtc, renderedStats, null, null, null);
        return new(false, null, "The exact retainer identity is bound. Open the selected venture ask to continue verification.");
    }

    public void Invalidate() => session = null;

    private static unsafe ulong? CurrentRetainerId()
    {
        var manager = RetainerManager.Instance();
        var active = manager is null ? null : manager->GetActiveRetainer();
        return active == null || active->RetainerId == 0 ? null : active->RetainerId;
    }

    private static string HashLines(IEnumerable<string> lines) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", lines.OrderBy(value => value, StringComparer.Ordinal)))));

    private unsafe bool TryCaptureRenderedTaskValue(
        string addonName,
        uint expectedTaskId,
        out int valueIndex,
        out string valueHash)
    {
        valueIndex = -1;
        valueHash = string.Empty;
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible || addon->AtkValues == null)
            return false;
        var matches = new List<int>();
        for (var index = 0; index < addon->AtkValuesCount; index++)
        {
            var value = addon->AtkValues[index];
            var numeric = value.Type switch
            {
                AtkValueType.UInt => value.UInt,
                AtkValueType.Int when value.Int >= 0 => (uint)value.Int,
                _ => uint.MaxValue,
            };
            if (numeric == expectedTaskId)
                matches.Add(index);
        }
        if (matches.Count != 1)
            return false;
        valueIndex = matches[0];
        valueHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{addonName}|{valueIndex}|{expectedTaskId}")));
        return true;
    }

    private sealed record CalibrationIdentity(
        Guid DefinitionGenerationId,
        string TargetKey,
        ulong OwnerContentId,
        string OwnerCharacterName,
        string OwnerHomeWorld,
        ulong RetainerId,
        string RetainerName,
        uint TaskId,
        string ItemName);

    private sealed record CalibrationSession(
        string SessionId,
        CalibrationIdentity Identity,
        DateTimeOffset IdentityObservedAtUtc,
        RetainerProcurementStats RenderedStats,
        string? AskTextSha256,
        int? AskTaskValueIndex,
        string? AskTaskValueSha256);
}

public static class RetainerVentureCalibrationContinuity
{
    public static readonly TimeSpan MaximumSessionAge = TimeSpan.FromMinutes(5);

    public static bool IsCurrent(
        ulong expectedRetainerId,
        ulong? activeRetainerId,
        DateTimeOffset identityObservedAtUtc,
        DateTimeOffset observedAtUtc) =>
        expectedRetainerId != 0 && activeRetainerId == expectedRetainerId &&
        identityObservedAtUtc != default && observedAtUtc >= identityObservedAtUtc &&
        observedAtUtc - identityObservedAtUtc <= MaximumSessionAge;
}
