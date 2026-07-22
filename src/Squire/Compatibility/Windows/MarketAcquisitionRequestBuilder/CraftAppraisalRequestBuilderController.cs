using System;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed class CraftAppraisalRequestBuilderController
{
    private static readonly TimeSpan DefaultCapabilityTtl = TimeSpan.FromMinutes(5);
    private readonly ICraftQuoteProvider quoteProvider;
    private readonly Func<CancellationToken, Task<bool>> refreshCapabilities;
    private readonly string craftQuoteDiagnosticsDirectory;
    private readonly Func<DateTimeOffset> getUtcNow;
    private readonly TimeSpan capabilityTtl;

    public CraftAppraisalRequestBuilderController(
        ICraftQuoteProvider quoteProvider,
        Func<CancellationToken, Task<bool>> refreshCapabilities,
        string craftQuoteDiagnosticsDirectory,
        Func<DateTimeOffset>? getUtcNow = null,
        TimeSpan? capabilityTtl = null)
    {
        this.quoteProvider = quoteProvider ?? throw new ArgumentNullException(nameof(quoteProvider));
        this.refreshCapabilities = refreshCapabilities ?? throw new ArgumentNullException(nameof(refreshCapabilities));
        ArgumentException.ThrowIfNullOrWhiteSpace(craftQuoteDiagnosticsDirectory);
        this.craftQuoteDiagnosticsDirectory = craftQuoteDiagnosticsDirectory;
        this.getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        this.capabilityTtl = capabilityTtl ?? DefaultCapabilityTtl;
    }

    public CraftAppraisalRequestBuilderState State { get; } = new();
    public bool IsFetchingCraftQuote { get; private set; }
    public bool IsCheckingWorkshopHostCapabilities { get; private set; }

    public async Task<CraftAppraisalQuote?> FetchQuoteAsync(
        MarketAppraisalRequest request,
        CancellationToken cancellationToken = default)
    {
        if (IsFetchingCraftQuote)
            return State.LatestQuote;

        IsFetchingCraftQuote = true;
        State.CraftQuoteStatus = "Fetching craft quote...";

        try
        {
            await EnsureWorkshopHostCapabilitiesFreshAsync(cancellationToken).ConfigureAwait(false);
            var quote = await quoteProvider.GetQuoteAsync(request, cancellationToken).ConfigureAwait(false);
            var diagnosticPath = WriteCraftQuoteDiagnosticPrintout(request, quote);
            State.RecordQuote(quote, diagnosticPath);
            State.CraftQuoteStatus = quote is null
                ? "No craft quote source returned evidence."
                : "Craft quote refreshed.";
            return quote;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            State.ClearQuoteEvidence();
            State.CraftQuoteStatus = $"Craft quote failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsFetchingCraftQuote = false;
        }
    }

    public async Task RefreshWorkshopHostCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (IsCheckingWorkshopHostCapabilities)
            return;

        State.WorkshopHostAvailable = false;
        IsCheckingWorkshopHostCapabilities = true;
        State.WorkshopHostStatus = "Checking Workshop Host capabilities...";

        try
        {
            State.WorkshopHostAvailable = await refreshCapabilities(cancellationToken).ConfigureAwait(false);
            State.CapabilitiesCheckedAtUtc = getUtcNow();
            State.WorkshopHostStatus = State.WorkshopHostAvailable
                ? "Workshop Host craft quotes available."
                : "Workshop Host does not advertise craft quote support.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            State.WorkshopHostAvailable = false;
            State.CapabilitiesCheckedAtUtc = getUtcNow();
            State.WorkshopHostStatus = $"Workshop Host capability check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingWorkshopHostCapabilities = false;
        }
    }

    public async Task EnsureWorkshopHostCapabilitiesFreshAsync(CancellationToken cancellationToken = default)
    {
        if (!State.WorkshopHostEnabled)
            return;

        if (State.CapabilitiesCheckedAtUtc is { } checkedAt &&
            getUtcNow() - checkedAt < capabilityTtl)
        {
            return;
        }

        await RefreshWorkshopHostCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
    }

    public uint? TryGetQuoteThreshold()
    {
        var quote = State.LatestQuote;
        if (quote is not { IsComplete: true, EstimatedUnitCost: > 0m })
            return null;

        return (uint)Math.Ceiling(quote.EstimatedUnitCost);
    }

    private string? WriteCraftQuoteDiagnosticPrintout(
        MarketAppraisalRequest request,
        CraftAppraisalQuote? quote)
    {
        if (quote is null)
            return null;

        return CraftQuoteDiagnosticPrintout.Write(
            craftQuoteDiagnosticsDirectory,
            request,
            quote,
            getUtcNow());
    }
}
