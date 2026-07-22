using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.AgentBridge;

namespace MarketMafioso.Squire.Observation;

public enum RenderedEquipmentLayoutStatus
{
    Complete,
    Unavailable,
    Ambiguous,
}

public sealed record RenderedEquipmentSlotTarget(
    string PositionKey,
    EquipmentSlot Slot,
    string NodePath,
    int Left,
    int Top,
    int Right,
    int Bottom);

public sealed record RenderedCharacterEquipmentLayout(
    RenderedEquipmentLayoutStatus Status,
    IReadOnlyList<RenderedEquipmentSlotTarget> Slots,
    string Diagnostic);

/// <summary>
/// Discovers the visible Character equipment grid from rendered drag/drop geometry.
/// Rendered icon values are deliberately ignored because they are not item identity authority.
/// </summary>
public static class RenderedCharacterEquipmentLayoutParser
{
    public static RenderedCharacterEquipmentLayout Parse(AgentBridgeRenderedUiSnapshot snapshot)
    {
        var addon = snapshot.Addons.FirstOrDefault(value => string.Equals(value.Name, "Character", StringComparison.Ordinal));
        if (addon is not { Present: true, Ready: true, Visible: true } || addon.Nodes == null)
            return Unavailable("The rendered Character equipment pane is unavailable.");

        var candidates = addon.Nodes
            .Where(IsEquipmentDragDrop)
            .GroupBy(value => (value.Left + value.Right) / 2)
            .Select(group => group.OrderBy(value => value.Top).ToArray())
            .Where(IsRegularEquipmentColumn)
            .OrderBy(column => (column[0].Left + column[0].Right) / 2)
            .ToArray();

        if (candidates.Length != 2)
            return Ambiguous($"Expected two rendered equipment columns; observed {candidates.Length}.");

        var left = candidates[0];
        var right = candidates[1];
        var horizontalGap = CenterX(right[0]) - CenterX(left[0]);
        if (left.Length is not (6 or 7) || right.Length != 7 ||
            horizontalGap is < 140 or > 420 ||
            left[0].Top >= right[0].Top ||
            !Aligned(left[1], right[0]) ||
            !Aligned(left[5], right[4]) ||
            right[6].Top <= left[5].Top)
            return Ambiguous("Rendered Character slot columns do not match the supported equipment-pane topology.");

        // When present, Left[6] is facewear: it is a cosmetic Character control, not a stat-bearing equipment slot.
        var mapped = new[]
        {
            Target("main-hand", EquipmentSlot.MainHand, left[0]),
            Target("head", EquipmentSlot.Head, left[1]),
            Target("body", EquipmentSlot.Body, left[2]),
            Target("hands", EquipmentSlot.Hands, left[3]),
            Target("legs", EquipmentSlot.Legs, left[4]),
            Target("feet", EquipmentSlot.Feet, left[5]),
            Target("off-hand", EquipmentSlot.OffHand, right[0]),
            Target("ears", EquipmentSlot.Ears, right[1]),
            Target("neck", EquipmentSlot.Neck, right[2]),
            Target("wrists", EquipmentSlot.Wrists, right[3]),
            Target("ring-left", EquipmentSlot.Ring, right[4]),
            Target("ring-right", EquipmentSlot.Ring, right[5]),
            Target("soul-crystal", EquipmentSlot.SoulCrystal, right[6]),
        };
        return new(RenderedEquipmentLayoutStatus.Complete, mapped, "Rendered Character equipment layout is complete.");
    }

    private static bool IsEquipmentDragDrop(AgentBridgeRenderedNodeSnapshot node)
    {
        var width = node.Right - node.Left;
        var height = node.Bottom - node.Top;
        return node.ComponentType == 17 && node.RespondsToMouse &&
               node.NodePath.StartsWith("Character/", StringComparison.Ordinal) &&
               node.NodePath.EndsWith("/5", StringComparison.Ordinal) &&
               width is >= 36 and <= 60 && height is >= 36 and <= 60;
    }

    private static bool IsRegularEquipmentColumn(AgentBridgeRenderedNodeSnapshot[] column)
    {
        if (column.Length is not (6 or 7))
            return false;
        for (var index = 1; index < column.Length; index++)
        {
            var gap = column[index].Top - column[index - 1].Top;
            if (gap is < 35 or > 75)
                return false;
        }
        return true;
    }

    private static bool Aligned(AgentBridgeRenderedNodeSnapshot left, AgentBridgeRenderedNodeSnapshot right) =>
        Math.Abs(left.Top - right.Top) <= 8;

    private static int CenterX(AgentBridgeRenderedNodeSnapshot value) => (value.Left + value.Right) / 2;

    private static RenderedEquipmentSlotTarget Target(string positionKey, EquipmentSlot slot, AgentBridgeRenderedNodeSnapshot node) =>
        new(positionKey, slot, node.NodePath, node.Left, node.Top, node.Right, node.Bottom);

    private static RenderedCharacterEquipmentLayout Unavailable(string diagnostic) =>
        new(RenderedEquipmentLayoutStatus.Unavailable, [], diagnostic);

    private static RenderedCharacterEquipmentLayout Ambiguous(string diagnostic) =>
        new(RenderedEquipmentLayoutStatus.Ambiguous, [], diagnostic);
}
