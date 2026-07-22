using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public static class RequestRouteScopeSelector
{
    public static void Draw(
        string id,
        RequestRouteScope scope,
        Action<RequestRouteScope> onChanged,
        Vector4 mutedColor,
        Vector4 errorColor)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(onChanged);

        DrawFullWidthCombo(
            $"Region##{id}Region",
            MarketAcquisitionWorldCatalog.SupportedRegions.ToArray(),
            scope.Region,
            region => onChanged(RequestRouteScopePresenter.ApplyRegion(scope, region)),
            mutedColor);

        DrawFullWidthCombo(
            $"World Mode##{id}WorldMode",
            RequestRouteScopePresenter.WorldModes,
            scope.WorldMode,
            worldMode => onChanged(RequestRouteScopePresenter.ApplyWorldMode(scope, worldMode)),
            mutedColor);

        if (scope.WorldMode != "AllWorldSweep")
            return;

        DrawFullWidthCombo(
            $"Sweep Scope##{id}SweepScope",
            RequestRouteScopePresenter.SweepScopes,
            scope.SweepScope,
            sweepScope => onChanged(RequestRouteScopePresenter.ApplySweepScope(scope, sweepScope)),
            mutedColor);

        if (scope.SweepScope == "DataCenters")
            DrawDataCenterSelector(id, scope, onChanged, errorColor);
    }

    public static void DrawCompact(
        string id,
        RequestRouteScope scope,
        Action<RequestRouteScope> onChanged,
        Vector4 mutedColor,
        Vector4 errorColor)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(onChanged);

        if (ImGui.BeginTable($"##{id}RouteScopeCompact", 3, ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Region", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 210f);
            ImGui.TableSetupColumn("Sweep", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawCombo(
                $"Region##{id}Region",
                MarketAcquisitionWorldCatalog.SupportedRegions.ToArray(),
                scope.Region,
                region => onChanged(RequestRouteScopePresenter.ApplyRegion(scope, region)),
                mutedColor);

            ImGui.TableNextColumn();
            DrawCombo(
                $"World Mode##{id}WorldMode",
                RequestRouteScopePresenter.WorldModes,
                scope.WorldMode,
                worldMode => onChanged(RequestRouteScopePresenter.ApplyWorldMode(scope, worldMode)),
                mutedColor);

            ImGui.TableNextColumn();
            if (scope.WorldMode == "AllWorldSweep")
            {
                DrawCombo(
                    $"Sweep Scope##{id}SweepScope",
                    RequestRouteScopePresenter.SweepScopes,
                    scope.SweepScope,
                    sweepScope => onChanged(RequestRouteScopePresenter.ApplySweepScope(scope, sweepScope)),
                    mutedColor);
            }
            else
            {
                ImGui.TextColored(mutedColor, "Sweep Scope");
                ImGui.TextUnformatted("Region");
            }

            ImGui.EndTable();
        }

        if (scope.WorldMode == "AllWorldSweep" && scope.SweepScope == "DataCenters")
            DrawDataCenterSelector(id, scope, onChanged, errorColor);
    }

    private static void DrawDataCenterSelector(
        string id,
        RequestRouteScope scope,
        Action<RequestRouteScope> onChanged,
        Vector4 errorColor)
    {
        IReadOnlyDictionary<string, string[]> dataCenters;
        try
        {
            dataCenters = MarketAcquisitionWorldCatalog.ResolveDataCenters(scope.Region);
        }
        catch (InvalidOperationException ex)
        {
            ImGui.TextColored(errorColor, ex.Message);
            return;
        }

        foreach (var dataCenter in dataCenters.Keys)
        {
            var selected = scope.SweepDataCenters.Contains(dataCenter, StringComparer.OrdinalIgnoreCase);
            if (ImGui.Checkbox($"{dataCenter}##{id}Dc{dataCenter}", ref selected))
                onChanged(RequestRouteScopePresenter.ToggleDataCenter(scope, dataCenter, selected));

            ImGui.SameLine();
        }

        ImGui.NewLine();
    }

    private static void DrawFullWidthCombo(
        string label,
        IReadOnlyList<string> options,
        string current,
        Action<string> onChanged,
        Vector4 mutedColor) =>
        DrawCombo(label, options, current, onChanged, mutedColor);

    private static void DrawCombo(
        string label,
        IReadOnlyList<string> options,
        string current,
        Action<string> onChanged,
        Vector4 mutedColor)
    {
        ImGui.TextColored(mutedColor, label.Split('#')[0]);
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(label, FormatOption(current)))
            return;

        foreach (var option in options)
        {
            var isSelected = option.Equals(current, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(FormatOption(option), isSelected) && !isSelected)
                onChanged(option);
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static string FormatOption(string value) =>
        value switch
        {
            "Recommended" => "Recommended worlds",
            "AllWorldSweep" => "All-world sweep",
            "CurrentDataCenter" => "Current data center",
            "DataCenters" => "Selected data centers",
            _ => string.IsNullOrWhiteSpace(value) ? "-" : value,
        };
}
