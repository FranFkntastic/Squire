using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketMafioso.Windows;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

internal static class AcquisitionRequestTableStyle
{
    public const float ScrollableBodyRowCount = 5f;

    public const ImGuiTableFlags LineTableFlags =
        ImGuiUi.InteractiveTableFlags |
        ImGuiTableFlags.ScrollX |
        ImGuiTableFlags.ScrollY;

    public const ImGuiTableFlags ClaimedBatchLineTableFlags = LineTableFlags;

    public static Vector2 FiveLineTableSize() =>
        new(0, (ImGui.GetTextLineHeightWithSpacing() * (ScrollableBodyRowCount + 1f)) + 8f);
}
