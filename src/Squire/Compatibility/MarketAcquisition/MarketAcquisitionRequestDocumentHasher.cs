using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRequestDocumentHasher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ComputeIntentHash(MarketAcquisitionRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var normalized = new
        {
            Region = Normalize(document.Region),
            WorldMode = Normalize(document.WorldMode),
            SweepScope = string.IsNullOrWhiteSpace(document.SweepScope) ? "Region" : document.SweepScope.Trim(),
            SweepDataCenters = document.SweepDataCenters
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Lines = document.Lines
                .Select(line => new
                {
                    line.ItemId,
                    ItemName = Normalize(line.ItemName),
                    ItemKind = Normalize(line.ItemKind),
                    QuantityMode = Normalize(line.QuantityMode),
                    line.TargetQuantity,
                    line.MaxQuantity,
                    HqPolicy = Normalize(line.HqPolicy),
                    line.MaxUnitPrice,
                    line.GilCap,
                })
                .ToArray(),
        };

        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
