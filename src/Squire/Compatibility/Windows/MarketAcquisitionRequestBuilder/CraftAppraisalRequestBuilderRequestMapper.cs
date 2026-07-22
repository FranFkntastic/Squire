using System;
using System.Linq;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public static class CraftAppraisalRequestMapper
{
    public static MarketAppraisalRequest Build(
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionRequestLineDocument line)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(line);
        if (line.ItemId == 0)
            throw new InvalidOperationException("Selected line must have an item id before craft appraisal.");

        return new MarketAppraisalRequest
        {
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            Quantity = ResolveQuoteQuantity(line),
            HqPolicy = string.IsNullOrWhiteSpace(line.HqPolicy) ? "Either" : line.HqPolicy,
            BuyThresholdUnitPrice = line.MaxUnitPrice,
            GilCap = line.GilCap,
            Region = MarketAcquisitionWorldCatalog.NormalizeRegion(document.Region),
            WorldMode = string.IsNullOrWhiteSpace(document.WorldMode) ? "Recommended" : document.WorldMode,
            SweepScope = string.IsNullOrWhiteSpace(document.SweepScope) ? "Region" : document.SweepScope,
            SweepDataCenters = document.SweepDataCenters
                .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
                .ToArray(),
        };
    }

    public static CraftAppraisalLineIdentity BuildLineIdentity(
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionRequestLineDocument line)
    {
        var request = Build(document, line);
        return new CraftAppraisalLineIdentity(
            request.ItemId,
            request.ItemName,
            request.Quantity,
            request.HqPolicy,
            request.Region);
    }

    public static int FindMatchingLineIndex(
        MarketAcquisitionRequestDocument document,
        CraftAppraisalLineIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(identity);
        for (var index = 0; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            if (line.ItemId == identity.ItemId && BuildLineIdentity(document, line) == identity)
                return index;
        }

        return -1;
    }

    private static uint ResolveQuoteQuantity(MarketAcquisitionRequestLineDocument line)
    {
        if (line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase))
            return Math.Max(1, line.TargetQuantity);

        return line.MaxQuantity > 0 ? line.MaxQuantity : 1;
    }
}
