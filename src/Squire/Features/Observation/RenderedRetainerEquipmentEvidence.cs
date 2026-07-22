using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.Squire.Outfitter;

namespace MarketMafioso.Squire.Observation;

public enum RenderedRetainerIdentityStatus
{
    Complete,
    Unavailable,
    Ambiguous,
}

public sealed record RenderedRetainerIdentityObservation(
    RenderedRetainerIdentityStatus Status,
    DateTimeOffset CapturedAtUtc,
    string OwnerCharacterName,
    string OwnerHomeWorld,
    string RetainerName,
    uint ClassJobId,
    uint Level,
    string Diagnostic);

public sealed record RenderedRetainerEquipmentScanObservation(
    RenderedEquipmentScanStatus Status,
    DateTimeOffset CapturedAtUtc,
    string OwnerCharacterName,
    string OwnerHomeWorld,
    string RetainerName,
    int CompletedSlots,
    int TotalSlots,
    IReadOnlyList<RenderedEquipmentSlotObservation> Observations,
    string Diagnostic);

public enum RenderedRetainerEquipmentEvidenceStatus
{
    Complete,
    Unavailable,
    IdentityMismatch,
    Incomplete,
}

public sealed record RenderedRetainerEquipmentEvidence(
    RenderedRetainerEquipmentEvidenceStatus Status,
    string TargetKey,
    DateTimeOffset CapturedAtUtc,
    string OwnerCharacterName,
    string OwnerHomeWorld,
    string RetainerName,
    uint ClassJobId,
    uint Level,
    IReadOnlyList<RenderedEquipmentSlotObservation> Equipment,
    string Diagnostic);

/// <summary>
/// Binds a rendered retainer identity to a complete rendered equipment scan. Cached inventory,
/// AutoRetainer metadata, and a target key select the expected target but cannot authorize the
/// baseline: every identity field and every equipped slot must be proven by rendered UI first.
/// </summary>
public static class RenderedRetainerEquipmentEvidenceAssembler
{
    public static RenderedRetainerEquipmentEvidence Assemble(
        OutfitterTarget target,
        RenderedRetainerIdentityObservation identity,
        RenderedRetainerEquipmentScanObservation equipmentScan)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(equipmentScan);

        if (target.Kind != OutfitterTargetKind.Retainer)
            return Failure(RenderedRetainerEquipmentEvidenceStatus.IdentityMismatch, target, identity,
                "Rendered retainer evidence cannot be attached to a player or gearset target.");
        if (identity.Status != RenderedRetainerIdentityStatus.Complete)
            return Failure(RenderedRetainerEquipmentEvidenceStatus.Unavailable, target, identity,
                $"The rendered retainer identity is not complete: {identity.Diagnostic}");

        var metadata = target.RetainerMetadata;
        if (metadata is null ||
            !Same(identity.OwnerCharacterName, target.OwnerCharacterName) ||
            !Same(identity.OwnerHomeWorld, target.OwnerHomeWorld) ||
            !Same(identity.RetainerName, metadata.RetainerName) ||
            identity.ClassJobId == 0 || identity.ClassJobId != metadata.ClassJobId ||
            identity.Level == 0 || identity.Level != metadata.Level)
        {
            return Failure(RenderedRetainerEquipmentEvidenceStatus.IdentityMismatch, target, identity,
                "The rendered owner, retainer, job, or level does not match the selected retainer target. No cached field will be used to repair the mismatch.");
        }

        if (equipmentScan.Status != RenderedEquipmentScanStatus.Complete ||
            !Same(equipmentScan.OwnerCharacterName, identity.OwnerCharacterName) ||
            !Same(equipmentScan.OwnerHomeWorld, identity.OwnerHomeWorld) ||
            !Same(equipmentScan.RetainerName, identity.RetainerName) ||
            equipmentScan.TotalSlots <= 0 ||
            equipmentScan.CompletedSlots != equipmentScan.TotalSlots ||
            equipmentScan.Observations.Count != equipmentScan.TotalSlots ||
            equipmentScan.Observations.Any(value => value.Item is not { Status: RenderedItemDetailStatus.Complete }) ||
            equipmentScan.Observations.Select(value => value.PositionKey).Distinct(StringComparer.Ordinal).Count() != equipmentScan.TotalSlots)
        {
            return Failure(RenderedRetainerEquipmentEvidenceStatus.Incomplete, target, identity,
                "The rendered retainer equipment scan is incomplete or internally inconsistent. Missing slots are not treated as empty slots.");
        }

        return new(
            RenderedRetainerEquipmentEvidenceStatus.Complete,
            target.Key,
            identity.CapturedAtUtc >= equipmentScan.CapturedAtUtc ? identity.CapturedAtUtc : equipmentScan.CapturedAtUtc,
            identity.OwnerCharacterName,
            identity.OwnerHomeWorld,
            identity.RetainerName,
            identity.ClassJobId,
            identity.Level,
            equipmentScan.Observations.ToArray(),
            "Rendered retainer identity and every observed equipment slot are complete and target-bound.");
    }

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static RenderedRetainerEquipmentEvidence Failure(
        RenderedRetainerEquipmentEvidenceStatus status,
        OutfitterTarget target,
        RenderedRetainerIdentityObservation identity,
        string diagnostic) =>
        new(status, target.Key, identity.CapturedAtUtc, identity.OwnerCharacterName, identity.OwnerHomeWorld,
            identity.RetainerName, identity.ClassJobId, identity.Level, [], diagnostic);
}
