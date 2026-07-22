using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed record RequestRouteScope(
    string Region,
    string WorldMode,
    string SweepScope,
    IReadOnlyList<string> SweepDataCenters)
{
    public static RequestRouteScope Default { get; } = new(
        "North America",
        "Recommended",
        "Region",
        []);

    public static RequestRouteScope FromDocument(MarketAcquisitionRequestDocument document) =>
        new(
            document.Region,
            document.WorldMode,
            document.SweepScope,
            document.SweepDataCenters.ToArray());
}

public static class RequestRouteScopePresenter
{
    public static readonly IReadOnlyList<string> WorldModes = ["Recommended", "AllWorldSweep"];
    public static readonly IReadOnlyList<string> SweepScopes = ["Region", "CurrentDataCenter", "DataCenters"];

    public static RequestRouteScope ApplyRegion(RequestRouteScope scope, string region) =>
        scope with
        {
            Region = region,
            SweepDataCenters = [],
        };

    public static RequestRouteScope ApplyWorldMode(RequestRouteScope scope, string worldMode) =>
        scope with
        {
            WorldMode = worldMode,
            SweepScope = worldMode == "AllWorldSweep" ? scope.SweepScope : "Region",
            SweepDataCenters = worldMode == "AllWorldSweep" ? scope.SweepDataCenters : [],
        };

    public static RequestRouteScope ApplySweepScope(RequestRouteScope scope, string sweepScope) =>
        scope with
        {
            SweepScope = sweepScope,
            SweepDataCenters = sweepScope == "DataCenters" ? scope.SweepDataCenters : [],
        };

    public static RequestRouteScope ToggleDataCenter(RequestRouteScope scope, string dataCenter, bool selected)
    {
        var selectedDataCenters = scope.SweepDataCenters
            .Where(existing => !existing.Equals(dataCenter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (selected)
            selectedDataCenters.Add(dataCenter);

        return scope with { SweepDataCenters = selectedDataCenters };
    }
}
