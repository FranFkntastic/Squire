using System;
using System.Linq;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public static class RequestDocumentMutation
{
    public static MarketAcquisitionRequestDocument ApplyMaxUnitPrice(
        MarketAcquisitionRequestDocument document,
        int selectedLineIndex,
        uint maxUnitPrice)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (selectedLineIndex < 0 || selectedLineIndex >= document.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

        return ApplyPricing(
            document,
            selectedLineIndex,
            maxUnitPrice,
            document.Lines[selectedLineIndex].GilCap);
    }

    public static MarketAcquisitionRequestDocument ApplyPricing(
        MarketAcquisitionRequestDocument document,
        int selectedLineIndex,
        uint maxUnitPrice,
        uint gilCap)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (selectedLineIndex < 0 || selectedLineIndex >= document.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

        var line = document.Lines[selectedLineIndex] with
        {
            MaxUnitPrice = maxUnitPrice,
            GilCap = gilCap,
        };
        return ReplaceLine(document, selectedLineIndex, line);
    }

    public static MarketAcquisitionRequestDocument ApplyLineEdit(
        MarketAcquisitionRequestDocument document,
        int selectedLineIndex,
        string quantityMode,
        uint targetQuantity,
        uint maxQuantity,
        string hqPolicy,
        uint maxUnitPrice,
        uint gilCap)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (selectedLineIndex < 0 || selectedLineIndex >= document.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

        var line = document.Lines[selectedLineIndex] with
        {
            QuantityMode = string.IsNullOrWhiteSpace(quantityMode) ? "AllBelowThreshold" : quantityMode,
            TargetQuantity = targetQuantity,
            MaxQuantity = maxQuantity,
            HqPolicy = string.IsNullOrWhiteSpace(hqPolicy) ? "Either" : hqPolicy,
            MaxUnitPrice = maxUnitPrice,
            GilCap = gilCap,
        };

        return ReplaceLine(document, selectedLineIndex, line);
    }

    public static MarketAcquisitionRequestDocument ApplyRouteScope(
        MarketAcquisitionRequestDocument document,
        RequestRouteScope scope)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(scope);
        return MarkEdited(document, document with
        {
            Region = scope.Region,
            WorldMode = scope.WorldMode,
            SweepScope = scope.SweepScope,
            SweepDataCenters = scope.SweepDataCenters.ToList(),
        });
    }

    public static MarketAcquisitionRequestDocument ReplaceLine(
        MarketAcquisitionRequestDocument document,
        int selectedLineIndex,
        MarketAcquisitionRequestLineDocument line)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(line);
        if (selectedLineIndex < 0 || selectedLineIndex >= document.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

        var lines = document.Lines.ToList();
        lines[selectedLineIndex] = line;
        return MarkEdited(document, document with { Lines = lines });
    }

    public static MarketAcquisitionRequestDocument AddLine(
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionRequestLineDocument line)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(line);
        var lines = document.Lines.ToList();
        lines.Add(line);
        return MarkEdited(document, document with { Lines = lines });
    }

    public static MarketAcquisitionRequestDocument RemoveLine(
        MarketAcquisitionRequestDocument document,
        int selectedLineIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (selectedLineIndex < 0 || selectedLineIndex >= document.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedLineIndex));

        var lines = document.Lines.ToList();
        lines.RemoveAt(selectedLineIndex);
        return MarkEdited(document, document with { Lines = lines });
    }

    private static MarketAcquisitionRequestDocument MarkEdited(
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionRequestDocument updated) =>
        (updated with { LocalRevision = document.LocalRevision }).WithNextRevision(
            string.IsNullOrWhiteSpace(document.RemoteRequestId) ? "NewDraft" : "LocalEdits");
}
