using System;
using System.Collections.Generic;
using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed record CraftAppraisalLineIdentity(
    uint ItemId,
    string ItemName,
    uint Quantity,
    string HqPolicy,
    string Region);

public sealed record CraftAppraisalLineQuoteState(
    CraftAppraisalLineIdentity Identity,
    CraftAppraisalQuote? Quote,
    string? DiagnosticFilePath,
    string Status,
    DateTimeOffset RecordedAtUtc);

public sealed class CraftAppraisalRequestBuilderState
{
    private readonly Dictionary<CraftAppraisalLineIdentity, CraftAppraisalLineQuoteState> lineQuotes = new();

    public CraftAppraisalLineIdentity? SelectedLine { get; private set; }
    public CraftAppraisalQuote? LatestQuote { get; private set; }
    public string? LastCraftQuoteDiagnosticFilePath { get; private set; }
    public uint? LastThresholdUnitPrice { get; private set; }
    public bool WorkshopHostEnabled { get; set; }
    public bool WorkshopHostAvailable { get; set; }
    public DateTimeOffset? CapabilitiesCheckedAtUtc { get; set; }
    public string WorkshopHostStatus { get; set; } = "Workshop Host quote API not checked.";
    public string CraftQuoteStatus { get; set; } = "No craft quote yet.";
    public IReadOnlyDictionary<CraftAppraisalLineIdentity, CraftAppraisalLineQuoteState> LineQuotes => lineQuotes;

    public void UpdateSelectedLine(CraftAppraisalLineIdentity? selectedLine)
    {
        if (Equals(SelectedLine, selectedLine))
            return;

        SelectedLine = selectedLine;
        ClearQuoteEvidence();
    }

    public void RecordQuote(CraftAppraisalQuote? quote, string? diagnosticFilePath)
    {
        LatestQuote = quote;
        LastCraftQuoteDiagnosticFilePath = diagnosticFilePath;
    }

    public CraftAppraisalLineQuoteState? GetLineQuote(CraftAppraisalLineIdentity identity) =>
        lineQuotes.TryGetValue(identity, out var state) ? state : null;

    public void RecordLineQuote(
        CraftAppraisalLineIdentity identity,
        CraftAppraisalQuote? quote,
        string? diagnosticFilePath,
        DateTimeOffset? recordedAtUtc = null)
    {
        lineQuotes[identity] = new CraftAppraisalLineQuoteState(
            identity,
            quote,
            diagnosticFilePath,
            quote is null ? "NoQuote" : "Quoted",
            recordedAtUtc ?? DateTimeOffset.UtcNow);
        RecordQuote(quote, diagnosticFilePath);
    }

    public void ClearLineQuote(CraftAppraisalLineIdentity identity)
    {
        lineQuotes.Remove(identity);
        if (Equals(SelectedLine, identity))
            ClearQuoteEvidence();
    }

    public void ClearAllLineQuotes()
    {
        lineQuotes.Clear();
        ClearQuoteEvidence();
    }

    public void RecordThresholdChanged(uint thresholdUnitPrice)
    {
        LastThresholdUnitPrice = thresholdUnitPrice;
    }

    public uint? TryGetLineQuoteThreshold(CraftAppraisalLineIdentity identity)
    {
        var quote = GetLineQuote(identity)?.Quote;
        if (quote is not { IsComplete: true, EstimatedUnitCost: > 0m })
            return null;

        return (uint)Math.Ceiling(quote.EstimatedUnitCost);
    }

    public void ClearQuoteEvidence()
    {
        LatestQuote = null;
        LastCraftQuoteDiagnosticFilePath = null;
        CraftQuoteStatus = "No craft quote yet.";
    }

    public CraftAppraisalDiagnosticsSnapshot CreateDiagnosticsSnapshot()
    {
        return new CraftAppraisalDiagnosticsSnapshot
        {
            WorkshopHostEnabled = WorkshopHostEnabled,
            WorkshopHostAvailable = WorkshopHostAvailable,
            CapabilitiesCheckedAtUtc = CapabilitiesCheckedAtUtc,
            WorkshopHostStatus = WorkshopHostStatus,
            CraftQuoteStatus = CraftQuoteStatus,
            LastCraftQuoteDiagnosticFilePath = LastCraftQuoteDiagnosticFilePath,
            LastQuoteItemName = LatestQuote?.ItemName,
            LastQuoteItemId = LatestQuote?.ItemId ?? 0,
            LatestQuoteWasLastGood = LatestQuote?.Source.Contains(
                "last-good",
                StringComparison.OrdinalIgnoreCase) == true,
        };
    }
}
