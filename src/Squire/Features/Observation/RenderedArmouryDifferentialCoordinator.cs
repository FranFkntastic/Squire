using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.AgentBridge;

namespace MarketMafioso.Squire.Observation;

public enum RenderedArmouryDifferentialStatus
{
    Idle,
    Working,
    Complete,
    Failed,
    Cancelled,
}

public sealed record RenderedArmouryDifferentialMismatch(
    string Container,
    int SlotIndex,
    string StructIdentity,
    string RenderedIdentity,
    string Reason);

public sealed record RenderedArmouryDifferentialProgress(
    RenderedArmouryDifferentialStatus Status,
    int CompletedSlots,
    int TotalSlots,
    string CurrentContainer,
    int MatchCount,
    IReadOnlyList<RenderedArmouryDifferentialMismatch> Mismatches,
    IReadOnlyList<string> OccupancyConflicts,
    IReadOnlyList<string> UnprovenContainers,
    string Diagnostic);

/// <summary>
/// Post-patch differential-audit bookkeeping. For every direct-read occupied slot, the
/// rendered tooltip identity must match item id and quality. Any disagreement fails the
/// audit and identifies a decode or observation regression for investigation.
/// </summary>
public sealed class RenderedArmouryDifferentialCoordinator
{
    private static readonly string[] ContainerOrder =
    [
        "ArmoryMainHand",
        "ArmoryOffHand",
        "ArmoryHead",
        "ArmoryBody",
        "ArmoryHands",
        "ArmoryLegs",
        "ArmoryFeets",
        "ArmoryEar",
        "ArmoryNeck",
        "ArmoryWrist",
        "ArmoryRings",
        "ArmorySoulCrystal",
        "Inventory1",
        "Inventory2",
        "Inventory3",
        "Inventory4",
        "SaddleBag1",
        "SaddleBag2",
        "PremiumSaddleBag1",
        "PremiumSaddleBag2",
    ];

    private readonly List<(string Container, int SlotIndex, uint ItemId, bool IsHighQuality)> structSlots = [];
    private readonly List<RenderedArmouryDifferentialMismatch> mismatches = [];
    private readonly List<string> occupancyConflicts = [];
    private readonly List<string> unprovenContainers = [];
    private readonly HashSet<string> comparedKeys = new(StringComparer.Ordinal);
    private RenderedArmouryDifferentialStatus status = RenderedArmouryDifferentialStatus.Idle;
    private string diagnostic = "The armoury differential proof has not started.";
    private int cursor;
    private int matchCount;

    public void Begin(IReadOnlyList<AgentBridgeInventoryStructItem> structBaseline)
    {
        ArgumentNullException.ThrowIfNull(structBaseline);
        structSlots.Clear();
        mismatches.Clear();
        occupancyConflicts.Clear();
        unprovenContainers.Clear();
        comparedKeys.Clear();
        structSlots.AddRange(structBaseline
            .Where(item => ContainerOrder.Contains(item.Container, StringComparer.Ordinal))
            .Select(item => (item.Container, item.SlotIndex, item.ItemId, item.IsHighQuality))
            .OrderBy(item => Array.IndexOf(ContainerOrder, item.Container))
            .ThenBy(item => item.SlotIndex));
        cursor = 0;
        matchCount = 0;
        status = structSlots.Count == 0 ? RenderedArmouryDifferentialStatus.Failed : RenderedArmouryDifferentialStatus.Working;
        diagnostic = structSlots.Count == 0
            ? "The struct baseline contains no armoury items; the differential proof cannot run."
            : $"Comparing {structSlots.Count} struct-occupied armoury slots against rendered tooltips.";
    }

    public (string Container, int SlotIndex, uint ItemId, bool IsHighQuality)? Current =>
        status == RenderedArmouryDifferentialStatus.Working && cursor < structSlots.Count
            ? structSlots[cursor]
            : null;

    public RenderedArmouryDifferentialProgress RecordRenderedObservation(
        string container, int slotIndex, uint? renderedItemId, bool? renderedIsHighQuality, string? renderedName)
    {
        if (Current is not { } current || current.Container != container || current.SlotIndex != slotIndex)
            return Fail($"The differential observation sequence drifted at {container}:{slotIndex}.");
        if (renderedItemId is null || renderedIsHighQuality is null || string.IsNullOrWhiteSpace(renderedName))
            mismatches.Add(new(container, slotIndex, Identity(current.ItemId, current.IsHighQuality), "nothing rendered",
                "No rendered tooltip identity was produced for a struct-occupied slot."));
        else if (renderedItemId != current.ItemId || renderedIsHighQuality != current.IsHighQuality)
            mismatches.Add(new(container, slotIndex, Identity(current.ItemId, current.IsHighQuality), Identity(renderedItemId.Value, renderedIsHighQuality.Value),
                $"Rendered identity '{renderedName}' disagrees with the struct read."));
        else
            matchCount++;
        comparedKeys.Add($"{container}:{slotIndex}");
        cursor++;
        return CompleteOrContinue();
    }

    /// <summary>
    /// Skips every remaining struct slot of a container whose rendered surface is
    /// unavailable. Skipped slots are disclosed as unproven — they are not matches,
    /// mismatches, or authorization for that container class.
    /// </summary>
    public RenderedArmouryDifferentialProgress SkipContainerSlots(string container, string reason)
    {
        var skipped = 0;
        while (Current is { } current && string.Equals(current.Container, container, StringComparison.Ordinal))
        {
            cursor++;
            skipped++;
        }
        if (skipped > 0)
            unprovenContainers.Add($"{container}: {skipped} struct slot(s) unproven ({reason}).");
        return CompleteOrContinue();
    }

    private RenderedArmouryDifferentialProgress CompleteOrContinue()
    {
        if (cursor < structSlots.Count)
        {
            status = RenderedArmouryDifferentialStatus.Working;
            diagnostic = $"Compared {cursor} of {structSlots.Count} armoury slots.";
            return Snapshot();
        }
        status = mismatches.Count == 0 && occupancyConflicts.Count == 0
            ? RenderedArmouryDifferentialStatus.Complete
            : RenderedArmouryDifferentialStatus.Failed;
        diagnostic = mismatches.Count == 0 && occupancyConflicts.Count == 0
            ? $"Differential proof passed: all {matchCount} struct-occupied armoury slots render the same identity and quality."
            : $"Differential proof failed with {mismatches.Count} identity mismatch(es) and {occupancyConflicts.Count} occupancy conflict(s).";
        if (unprovenContainers.Count > 0)
            diagnostic += $" Unproven: {string.Join("; ", unprovenContainers)}";
        return Snapshot();
    }

    public RenderedArmouryDifferentialProgress RecordOccupancyCount(string container, int structCount, int renderedIconCount)
    {
        if (structCount != renderedIconCount)
            occupancyConflicts.Add($"{container}: struct shows {structCount} occupied slot(s) but the rendered tab shows {renderedIconCount} icon(s).");
        return Snapshot();
    }

    public int StructCountFor(string container) =>
        structSlots.Count(value => value.Container == container);

    /// <summary>
    /// Replaces the baseline entry for one slot with a fresh struct read. Used when the
    /// inventory moved mid-run: a fresh read that agrees with the rendered identity
    /// proves the old baseline went stale rather than the struct read being wrong.
    /// </summary>
    public bool RefreshBaselineItem(string container, int slotIndex, uint itemId, bool isHighQuality)
    {
        for (var index = 0; index < structSlots.Count; index++)
        {
            var slot = structSlots[index];
            if (slot.Container == container && slot.SlotIndex == slotIndex)
            {
                structSlots[index] = (slot.Container, slot.SlotIndex, itemId, isHighQuality);
                return true;
            }
        }
        return false;
    }

    public RenderedArmouryDifferentialProgress Fail(string message) => FailCore(message);

    public RenderedArmouryDifferentialProgress Cancel()
    {
        if (status is RenderedArmouryDifferentialStatus.Complete or RenderedArmouryDifferentialStatus.Failed)
            return Snapshot();
        status = RenderedArmouryDifferentialStatus.Cancelled;
        diagnostic = "The armoury differential proof was cancelled.";
        return Snapshot();
    }

    public RenderedArmouryDifferentialProgress Snapshot() => new(
        status,
        cursor,
        structSlots.Count,
        Current?.Container ?? string.Empty,
        matchCount,
        mismatches.ToArray(),
        occupancyConflicts.ToArray(),
        unprovenContainers.ToArray(),
        diagnostic);

    private RenderedArmouryDifferentialProgress FailCore(string message)
    {
        status = RenderedArmouryDifferentialStatus.Failed;
        diagnostic = message;
        return Snapshot();
    }

    private static string Identity(uint itemId, bool isHighQuality) => $"{itemId}{(isHighQuality ? " HQ" : " NQ")}";
}
