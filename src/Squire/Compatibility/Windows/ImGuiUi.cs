using System.Numerics;
using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows;

internal static class ImGuiUi
{
    public const ImGuiTableFlags InteractiveTableFlags =
        ImGuiTableFlags.Borders |
        ImGuiTableFlags.RowBg |
        ImGuiTableFlags.Resizable |
        ImGuiTableFlags.Reorderable |
        ImGuiTableFlags.Hideable;

    public static void SectionHeader(string text, Vector4 color)
    {
        ImGui.TextColored(color, text);
        ImGui.Separator();
    }

    public static void SectionHeaderWithActions(string text, Vector4 color, Action drawActions, float actionWidth = 0)
    {
        ImGui.TextColored(color, text);
        ImGui.SameLine();
        if (actionWidth > 0)
        {
            var rightAlignedX = ImGui.GetWindowContentRegionMax().X - actionWidth;
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), rightAlignedX));
        }

        drawActions();
        ImGui.Separator();
    }

    public static void SameLineRight(float width)
    {
        ImGui.SameLine();
        var rightAlignedX = ImGui.GetWindowContentRegionMax().X - width;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), rightAlignedX));
    }

    public static bool Button(string label, bool enabled)
    {
        return Button(label, Vector2.Zero, enabled);
    }

    public static bool Button(string label, Vector2 size, bool enabled)
    {
        if (!enabled)
            ImGui.BeginDisabled();

        var clicked = size == Vector2.Zero
            ? ImGui.Button(label)
            : ImGui.Button(label, size);

        if (!enabled)
            ImGui.EndDisabled();

        return clicked;
    }

    public static bool PrimaryButton(string label, bool enabled)
    {
        var color = MarketMafiosoUiTheme.Header;
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(color.X * 0.65f, color.Y * 0.65f, color.Z * 0.65f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(color.X * 0.82f, color.Y * 0.82f, color.Z * 0.82f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
        var clicked = Button(label, enabled);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    public static bool MenuItem(string label, bool enabled)
    {
        if (!enabled)
            ImGui.BeginDisabled();

        var clicked = ImGui.MenuItem(label);

        if (!enabled)
            ImGui.EndDisabled();

        return clicked;
    }

    public static bool MenuButton(string label, bool enabled = true)
    {
        if (!enabled)
            ImGui.BeginDisabled();

        var clicked = ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ChevronDown, label);

        if (!enabled)
            ImGui.EndDisabled();

        return clicked;
    }
}
