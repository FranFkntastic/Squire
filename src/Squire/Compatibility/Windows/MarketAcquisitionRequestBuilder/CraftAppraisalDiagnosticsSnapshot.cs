using System;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed record CraftAppraisalDiagnosticsSnapshot
{
    public bool WorkshopHostEnabled { get; init; }
    public bool WorkshopHostAvailable { get; init; }
    public DateTimeOffset? CapabilitiesCheckedAtUtc { get; init; }
    public string WorkshopHostStatus { get; init; } = "Workshop Host quote API not checked.";
    public string CraftQuoteStatus { get; init; } = "No craft quote yet.";
    public string? LastCraftQuoteDiagnosticFilePath { get; init; }
    public string? LastQuoteItemName { get; init; }
    public uint LastQuoteItemId { get; init; }
    public bool LatestQuoteWasLastGood { get; init; }
}
