using System.Linq;

namespace MarketMafioso.MarketAcquisition;

internal static class MarketAcquisitionClaimPersistence
{
    public static (MarketAcquisitionClaimView Claim, string? AcceptIdempotencyKey, string? RejectIdempotencyKey)? Restore(
        Configuration config)
    {
        var stored = config.ActiveMarketAcquisitionClaim;
        if (stored == null ||
            string.IsNullOrWhiteSpace(stored.Id) ||
            string.IsNullOrWhiteSpace(stored.ClaimToken))
        {
            return null;
        }

        return (
            new MarketAcquisitionClaimView
            {
                Id = stored.Id,
                Revision = stored.Revision,
                Status = stored.Status,
                Origin = string.IsNullOrWhiteSpace(stored.Origin)
                    ? MarketAcquisitionOrigins.DashboardCreated
                    : stored.Origin,
                CreatedByPluginInstanceId = stored.CreatedByPluginInstanceId,
                TargetCharacterName = stored.TargetCharacterName,
                TargetWorld = stored.TargetWorld,
                Region = stored.Region,
                ItemId = stored.ItemId,
                ItemName = stored.ItemName,
                QuantityMode = stored.QuantityMode,
                Quantity = stored.Quantity,
                HqPolicy = stored.HqPolicy,
                MaxUnitPrice = stored.MaxUnitPrice,
                MaxTotalGil = stored.MaxTotalGil,
                WorldMode = stored.WorldMode,
                SelectedWorlds = stored.SelectedWorlds.ToList(),
                ClaimToken = stored.ClaimToken,
                Lines = stored.Lines.Count == 0
                    ? [CreateFallbackLine(stored)]
                    : stored.Lines
                        .Select(line => new MarketAcquisitionBatchLineView
                        {
                            LineId = line.LineId,
                            BatchId = line.BatchId,
                            Ordinal = line.Ordinal,
                            ItemId = line.ItemId,
                            ItemName = line.ItemName,
                            ItemKind = line.ItemKind,
                            QuantityMode = line.QuantityMode,
                            TargetQuantity = line.TargetQuantity,
                            MaxQuantity = line.MaxQuantity,
                            HqPolicy = line.HqPolicy,
                            MaxUnitPrice = line.MaxUnitPrice,
                            GilCap = line.GilCap,
                            Status = line.Status,
                            PurchasedQuantity = line.PurchasedQuantity,
                            SpentGil = line.SpentGil,
                            LatestMessage = line.LatestMessage,
                        })
                        .ToList(),
            },
            stored.AcceptIdempotencyKey,
            stored.RejectIdempotencyKey);
    }

    public static void Save(
        Configuration config,
        MarketAcquisitionClaimView claim,
        string? acceptIdempotencyKey,
        string? rejectIdempotencyKey)
    {
        config.ActiveMarketAcquisitionClaim = new PersistedMarketAcquisitionClaim
        {
            Id = claim.Id,
            Revision = claim.Revision,
            Status = claim.Status,
            Origin = claim.Origin,
            CreatedByPluginInstanceId = claim.CreatedByPluginInstanceId,
            TargetCharacterName = claim.TargetCharacterName,
            TargetWorld = claim.TargetWorld,
            Region = claim.Region,
            ItemId = claim.ItemId,
            ItemName = claim.ItemName,
            QuantityMode = claim.QuantityMode,
            Quantity = claim.Quantity,
            HqPolicy = claim.HqPolicy,
            MaxUnitPrice = claim.MaxUnitPrice,
            MaxTotalGil = claim.MaxTotalGil,
            WorldMode = claim.WorldMode,
            SelectedWorlds = claim.SelectedWorlds.ToList(),
            ClaimToken = claim.ClaimToken,
            AcceptIdempotencyKey = acceptIdempotencyKey,
            RejectIdempotencyKey = rejectIdempotencyKey,
            Lines = claim.Lines
                .Select(line => new PersistedMarketAcquisitionLine
                {
                    LineId = line.LineId,
                    BatchId = line.BatchId,
                    Ordinal = line.Ordinal,
                    ItemId = line.ItemId,
                    ItemName = line.ItemName,
                    ItemKind = line.ItemKind,
                    QuantityMode = line.QuantityMode,
                    TargetQuantity = line.TargetQuantity,
                    MaxQuantity = line.MaxQuantity,
                    HqPolicy = line.HqPolicy,
                    MaxUnitPrice = line.MaxUnitPrice,
                    GilCap = line.GilCap,
                    Status = line.Status,
                    PurchasedQuantity = line.PurchasedQuantity,
                    SpentGil = line.SpentGil,
                    LatestMessage = line.LatestMessage,
                })
                .ToList(),
        };
    }

    public static void Clear(Configuration config)
    {
        config.ActiveMarketAcquisitionClaim = null;
    }

    private static MarketAcquisitionBatchLineView CreateFallbackLine(PersistedMarketAcquisitionClaim stored) =>
        new()
        {
            LineId = $"{stored.Id}-line-1",
            BatchId = stored.Id,
            Ordinal = 0,
            ItemId = stored.ItemId,
            ItemName = stored.ItemName,
            QuantityMode = stored.QuantityMode,
            TargetQuantity = stored.QuantityMode == "TargetQuantity" ? stored.Quantity : 0,
            MaxQuantity = stored.QuantityMode == "AllBelowThreshold" ? stored.Quantity : 0,
            HqPolicy = stored.HqPolicy,
            MaxUnitPrice = stored.MaxUnitPrice,
            GilCap = stored.MaxTotalGil,
            Status = stored.Status,
        };
}
