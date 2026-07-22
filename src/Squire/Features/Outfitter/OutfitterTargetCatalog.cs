using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;

namespace MarketMafioso.Squire.Outfitter;

public sealed class OutfitterTargetCatalog
{
    public IReadOnlyList<OutfitterTarget> Build(
        CharacterEquipmentSnapshot snapshot,
        IReadOnlyDictionary<ulong, CachedRetainer> retainers,
        IReadOnlyList<OutfitterRetainerMetadata>? autoRetainerMetadata = null,
        IReadOnlyDictionary<string, RenderedRetainerEquipmentEvidence>? renderedRetainerEquipment = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(retainers);

        var targets = new List<OutfitterTarget>();
        foreach (var job in snapshot.Jobs
                     .Where(job => job.IsUnlocked == true && job.Level > 0)
                     .Where(job => !HasUnlockedUpgrade(snapshot, job))
                     .OrderBy(job => DisciplineOrder(job.Discipline))
                     .ThenBy(job => job.Role, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(job => job.Name, StringComparer.OrdinalIgnoreCase))
        {
            var gearsets = snapshot.Gearsets
                .Where(gearset => gearset.IsValid && gearset.ClassJobId == job.ClassJobId)
                .OrderBy(gearset => gearset.GearsetId)
                .ToArray();
            targets.Add(new(
                $"job:{job.ClassJobId}",
                OutfitterTargetKind.Job,
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(job.Name),
                $"{job.Abbreviation} · Lv. {job.Level:N0} · {gearsets.Length:N0} gearset{(gearsets.Length == 1 ? string.Empty : "s")}",
                Job: job,
                Gearset: gearsets.FirstOrDefault()));
            foreach (var gearset in gearsets)
            {
                targets.Add(new(
                    $"gearset:{gearset.GearsetId}",
                    OutfitterTargetKind.Gearset,
                    gearset.Name,
                    $"Gearset {gearset.GearsetId + 1:N0}",
                    Job: job,
                    Gearset: gearset));
            }
        }

        var metadataByRetainer = (autoRetainerMetadata ?? [])
            .Where(value => value.RetainerId != 0)
            .GroupBy(value => value.RetainerId)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var retainerId in retainers.Keys.Concat(metadataByRetainer.Keys).Distinct())
        {
            retainers.TryGetValue(retainerId, out var retainer);
            metadataByRetainer.TryGetValue(retainerId, out var metadata);
            var ownerName = metadata?.OwnerCharacterName ?? retainer?.OwnerCharacterName;
            var ownerWorld = metadata?.OwnerHomeWorld ?? retainer?.OwnerHomeWorld;
            var isCurrentCharacter = metadata?.OwnerContentId == snapshot.Identity.Scope?.LocalContentId ||
                                     (!string.IsNullOrWhiteSpace(ownerName) && string.Equals(
                                         ownerName,
                                         snapshot.Identity.Scope?.Name,
                                         StringComparison.OrdinalIgnoreCase));
            var job = metadata is { ClassJobId: > 0 }
                ? snapshot.Jobs.FirstOrDefault(value => value.ClassJobId == metadata.ClassJobId)
                : null;
            if (job is not null && metadata is { Level: > 0 })
                job = job with { Level = metadata.Level, IsUnlocked = true };
            var freshness = retainer is null ? "inventory not cached" : FormatFreshness(retainer.LastUpdated);
            var jobSummary = job is not null && metadata is { Level: > 0 }
                ? $"{job.Abbreviation} · Lv. {metadata.Level:N0}"
                : "Job unavailable";
            var targetKey = $"retainer:{retainerId}";
            RenderedRetainerEquipmentEvidence? renderedEvidence = null;
            renderedRetainerEquipment?.TryGetValue(targetKey, out renderedEvidence);
            var diagnostic = renderedEvidence?.Status == RenderedRetainerEquipmentEvidenceStatus.Complete
                ? "The rendered equipment baseline is proven. Squire still needs a supported retainer outcome profile before this target can be advised."
                : (retainer, metadata) switch
            {
                (null, not null) => "AutoRetainer knows this retainer's job and level, but Squire has no inventory snapshot. Visit the retainer or run a retainer refresh to cache its bags.",
                (not null, not null) => "The retainer's inventory, job, and level are known, but AutoRetainer does not expose worn equipment slots. Squire will not claim a complete loadout until that baseline can be proven.",
                _ => "The retainer inventory is cached, but its job, level, and worn equipment slots are unavailable. Load the owner in AutoRetainer to refresh this identity.",
            };
            targets.Add(new(
                targetKey,
                OutfitterTargetKind.Retainer,
                metadata?.RetainerName ?? retainer?.RetainerName ?? $"Retainer {retainerId}",
                $"{jobSummary} · {freshness}",
                Job: job,
                Retainer: retainer,
                RetainerMetadata: metadata,
                OwnerCharacterName: ownerName,
                OwnerHomeWorld: ownerWorld,
                IsCurrentCharacter: isCurrentCharacter,
                IsReady: false,
                Diagnostic: diagnostic,
                RetainerEquipmentEvidence: renderedEvidence));
        }

        return targets;
    }

    private static string FormatFreshness(DateTime capturedAt)
    {
        var age = DateTime.UtcNow - capturedAt.ToUniversalTime();
        return age.TotalHours < 1
            ? $"updated {Math.Max(1, (int)age.TotalMinutes):N0}m ago"
            : age.TotalDays < 2
                ? $"updated {Math.Max(1, (int)age.TotalHours):N0}h ago"
                : $"updated {Math.Max(1, (int)age.TotalDays):N0}d ago";
    }

    private static int DisciplineOrder(EquipmentDiscipline discipline) => discipline switch
    {
        EquipmentDiscipline.Combat => 0,
        EquipmentDiscipline.Crafter => 1,
        EquipmentDiscipline.Gatherer => 2,
        _ => 3,
    };

    private static bool HasUnlockedUpgrade(CharacterEquipmentSnapshot snapshot, Franthropy.Dalamud.Characters.CharacterJobSnapshot job) =>
        snapshot.Jobs.Any(candidate =>
            candidate.IsUnlocked == true &&
            candidate.Level > 0 &&
            candidate.ClassJobId != job.ClassJobId &&
            candidate.ParentClassJobId == job.ClassJobId);
}
