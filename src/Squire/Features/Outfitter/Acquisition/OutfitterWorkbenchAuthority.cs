using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using Newtonsoft.Json;

namespace MarketMafioso.Squire.Outfitter.Acquisition;

public enum OutfitterWorkbenchLineageState
{
    Valid,
    Invalidated,
}

public sealed record OutfitterWorkbenchLineEnvelope(
    uint ItemId,
    string ItemName,
    EquipmentQuality Quality,
    uint RequiredQuantity,
    uint ObservedMaxUnitPriceGil,
    ulong ObservedTotalPriceGil,
    uint MaxUnitPriceGil,
    uint MaxTotalGil,
    string ItemKind = "Equipment");

public sealed record OutfitterExecutionContract(
    string SchemaVersion,
    string ContractId,
    string WorkbenchDocumentId,
    int WorkbenchRevision,
    string CanonicalIntentHash,
    string RecoveryPolicyId,
    string TargetCharacterName,
    string TargetWorld,
    string Region,
    string WorldMode,
    string SweepScope,
    IReadOnlyList<string> SweepDataCenters,
    IReadOnlyList<string> AuthorizedWorlds,
    ulong SquirePlanCapGil,
    OutfitterWorkbenchTransfer Transfer,
    IReadOnlyList<OutfitterWorkbenchLineEnvelope> Lines,
    DateTimeOffset ConfirmedAtUtc)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-outfitter-execution-contract/v2";
}

public sealed record OutfitterWorkbenchAuthority(
    string SchemaVersion,
    OutfitterWorkbenchTransfer Transfer,
    int PriceFlexPercent,
    ulong SquirePlanCapGil,
    string RecoveryPolicyId,
    IReadOnlyList<OutfitterWorkbenchLineEnvelope> Lines,
    OutfitterWorkbenchLineageState LineageState,
    string? InvalidationReason,
    OutfitterExecutionContract? FinalizedContract)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-outfitter-workbench-authority/v1";
    public const string CrossWorldExactQualityV1 = "CrossWorldExactQuality/v1";

    public bool IsLineageValid => LineageState == OutfitterWorkbenchLineageState.Valid;
}

public sealed record OutfitterWorkbenchAuthorityValidation(bool IsValid, string? Error)
{
    public static OutfitterWorkbenchAuthorityValidation Valid { get; } = new(true, null);
}

public static class OutfitterWorkbenchAuthorityService
{
    public const int MinimumPriceFlexPercent = 0;
    public const int MaximumPriceFlexPercent = 500;

    public static MarketAcquisitionRequestDocument Stage(
        MarketAcquisitionRequestDocument document,
        OutfitterWorkbenchTransfer transfer,
        int priceFlexPercent = 0)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(transfer);
        if (!string.Equals(transfer.SchemaVersion, OutfitterWorkbenchTransfer.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(transfer.Origin, OutfitterWorkbenchTransfer.SquireOutfitterOrigin, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Workbench accepts only the current exact-quality Squire Outfitter transfer.");
        }

        var envelopes = BuildEnvelopes(transfer, priceFlexPercent);
        var transferredItemIds = envelopes.Select(line => line.ItemId).ToHashSet();
        var lines = document.Lines
            .Where(line => !transferredItemIds.Contains(line.ItemId))
            .ToList();
        lines.AddRange(envelopes.Select(ToRequestLine));

        var authority = new OutfitterWorkbenchAuthority(
            OutfitterWorkbenchAuthority.CurrentSchemaVersion,
            transfer,
            ClampFlex(priceFlexPercent),
            ApplyFlex(transfer.ObservedMarketTotalGil, priceFlexPercent),
            OutfitterWorkbenchAuthority.CrossWorldExactQualityV1,
            envelopes,
            OutfitterWorkbenchLineageState.Valid,
            null,
            null);
        return MarkEdited(document, document with { Lines = lines, OutfitterAuthority = authority });
    }

    public static MarketAcquisitionRequestDocument UpdatePriceFlex(
        MarketAcquisitionRequestDocument document,
        int priceFlexPercent)
    {
        ArgumentNullException.ThrowIfNull(document);
        var authority = document.OutfitterAuthority ??
            throw new InvalidOperationException("No Squire Outfitter solution is attached to this Workbench.");
        if (!authority.IsLineageValid)
            throw new InvalidOperationException("Return to Advisor before changing caps on an invalidated solution.");

        var envelopes = BuildEnvelopes(authority.Transfer, priceFlexPercent);
        var replacements = envelopes.ToDictionary(LineKey);
        var lines = document.Lines.Select(line =>
        {
            var quality = QualityFromPolicy(line.HqPolicy);
            return quality is { } exact && replacements.TryGetValue((line.ItemId, exact), out var envelope)
                ? ToRequestLine(envelope)
                : line;
        }).ToList();
        var updatedAuthority = authority with
        {
            PriceFlexPercent = ClampFlex(priceFlexPercent),
            SquirePlanCapGil = ApplyFlex(authority.Transfer.ObservedMarketTotalGil, priceFlexPercent),
            Lines = envelopes,
            FinalizedContract = null,
        };
        return MarkEdited(document, document with { Lines = lines, OutfitterAuthority = updatedAuthority });
    }

    public static MarketAcquisitionRequestDocument ReconcileEdit(
        MarketAcquisitionRequestDocument previous,
        MarketAcquisitionRequestDocument updated)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(updated);
        var authority = previous.OutfitterAuthority;
        if (authority is null)
            return updated;

        var validation = ValidateLineage(updated.Lines, authority);
        var reconciled = validation.IsValid
            ? authority with { FinalizedContract = null }
            : authority with
            {
                LineageState = OutfitterWorkbenchLineageState.Invalidated,
                InvalidationReason = validation.Error,
                FinalizedContract = null,
            };
        return updated with { OutfitterAuthority = reconciled };
    }

    public static OutfitterWorkbenchAuthorityValidation ValidateForFinalization(
        MarketAcquisitionRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var authority = document.OutfitterAuthority;
        if (authority is null)
            return OutfitterWorkbenchAuthorityValidation.Valid;
        if (!authority.IsLineageValid)
            return new(false, authority.InvalidationReason ?? "The selected Squire solution changed; return to Advisor.");
        if (!string.Equals(authority.RecoveryPolicyId, OutfitterWorkbenchAuthority.CrossWorldExactQualityV1, StringComparison.Ordinal))
            return new(false, "The Squire recovery policy is not an approved version.");
        var lineage = ValidateLineage(document.Lines, authority);
        if (!lineage.IsValid)
            return lineage;
        foreach (var expected in authority.Lines)
        {
            var line = document.Lines.Single(value =>
                value.ItemId == expected.ItemId && QualityFromPolicy(value.HqPolicy) == expected.Quality);
            if (line.MaxUnitPrice != expected.MaxUnitPriceGil || line.GilCap != expected.MaxTotalGil)
                return new(false, $"{expected.ItemName} fixed ceilings no longer match the visible Squire approval envelope.");
        }
        if (authority.SquirePlanCapGil == 0 || authority.Lines.Any(line => line.MaxUnitPriceGil == 0 || line.MaxTotalGil == 0))
            return new(false, "Every Squire line and the Squire plan require a fixed gil ceiling.");
        return OutfitterWorkbenchAuthorityValidation.Valid;
    }

    public static MarketAcquisitionRequestDocument Finalize(MarketAcquisitionRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var authority = document.OutfitterAuthority;
        if (authority is null)
            return document;
        var validation = ValidateForFinalization(document);
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.Error);

        var intentHash = ComputeCanonicalIntentHash(document);
        if (authority.FinalizedContract is { } existing &&
            existing.SchemaVersion == OutfitterExecutionContract.CurrentSchemaVersion &&
            existing.AuthorizedWorlds is { Count: > 0 } &&
            existing.WorkbenchRevision == document.LocalRevision &&
            string.Equals(existing.CanonicalIntentHash, intentHash, StringComparison.Ordinal))
        {
            return document;
        }

        var contract = new OutfitterExecutionContract(
            OutfitterExecutionContract.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            document.LocalRequestId,
            document.LocalRevision,
            intentHash,
            authority.RecoveryPolicyId,
            document.TargetCharacterName,
            document.TargetWorld,
            document.Region,
            document.WorldMode,
            document.SweepScope,
            document.SweepDataCenters.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            ResolveAuthorizedWorlds(document),
            authority.SquirePlanCapGil,
            authority.Transfer,
            authority.Lines,
            DateTimeOffset.UtcNow);
        return document with
        {
            OutfitterAuthority = authority with { FinalizedContract = contract },
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static string ComputeCanonicalIntentHash(MarketAcquisitionRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var authorityJson = document.OutfitterAuthority is null
            ? string.Empty
            : JsonConvert.SerializeObject(document.OutfitterAuthority with { FinalizedContract = null }, Formatting.None);
        var payload = $"{document.TargetCharacterName.Trim()}\n{document.TargetWorld.Trim()}\n{MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(document)}\n{authorityJson}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static bool IsAuthorityLine(MarketAcquisitionRequestDocument document, int index)
    {
        if (document.OutfitterAuthority is not { } authority || index < 0 || index >= document.Lines.Count)
            return false;
        var line = document.Lines[index];
        var quality = QualityFromPolicy(line.HqPolicy);
        return quality is { } exact && authority.Lines.Any(value => value.ItemId == line.ItemId && value.Quality == exact);
    }

    private static OutfitterWorkbenchAuthorityValidation ValidateLineage(
        IReadOnlyList<MarketAcquisitionRequestLineDocument> requestLines,
        OutfitterWorkbenchAuthority authority)
    {
        foreach (var expected in authority.Lines)
        {
            var matches = requestLines.Where(line =>
                line.ItemId == expected.ItemId && QualityFromPolicy(line.HqPolicy) == expected.Quality).ToArray();
            if (matches.Length != 1)
                return new(false, $"{expected.ItemName} {QualityLabel(expected.Quality)} is missing or duplicated.");
            var line = matches[0];
            if (!line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase) ||
                line.TargetQuantity != expected.RequiredQuantity)
            {
                return new(false, $"{expected.ItemName} {QualityLabel(expected.Quality)} no longer matches the selected quantity.");
            }
        }
        return OutfitterWorkbenchAuthorityValidation.Valid;
    }

    private static IReadOnlyList<OutfitterWorkbenchLineEnvelope> BuildEnvelopes(
        OutfitterWorkbenchTransfer transfer,
        int priceFlexPercent) => transfer.MarketLots
        .GroupBy(lot => (lot.OfferKey.ItemId, lot.OfferKey.Quality))
        .Select(group => new OutfitterWorkbenchLineEnvelope(
            group.Key.ItemId,
            group.Select(lot => lot.ItemName).First(name => !string.IsNullOrWhiteSpace(name)),
            group.Key.Quality,
            group.Aggregate(0u, (sum, lot) => checked(sum + lot.RequiredQuantity)),
            group.Max(lot => lot.ObservedUnitPriceGil),
            group.Aggregate(0ul, (sum, lot) => checked(sum + lot.ObservedTotalPriceGil)),
            ApplyFlex(group.Max(lot => lot.ObservedUnitPriceGil), priceFlexPercent),
            ApplyFlexToUInt(group.Aggregate(0ul, (sum, lot) => checked(sum + lot.ObservedTotalPriceGil)), priceFlexPercent),
            group.Select(lot => lot.ItemKind).Distinct(StringComparer.Ordinal).Single()))
        .OrderBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(line => line.ItemId)
        .ThenBy(line => line.Quality)
        .ToArray();

    private static MarketAcquisitionRequestLineDocument ToRequestLine(OutfitterWorkbenchLineEnvelope envelope) => new()
    {
        ItemId = envelope.ItemId,
        ItemName = envelope.ItemName,
        ItemKind = envelope.ItemKind,
        QuantityMode = "TargetQuantity",
        TargetQuantity = envelope.RequiredQuantity,
        MaxQuantity = 0,
        HqPolicy = envelope.Quality == EquipmentQuality.High ? "HQOnly" : "NQOnly",
        MaxUnitPrice = envelope.MaxUnitPriceGil,
        GilCap = envelope.MaxTotalGil,
    };

    private static int ClampFlex(int value) => Math.Clamp(value, MinimumPriceFlexPercent, MaximumPriceFlexPercent);

    private static uint ApplyFlex(uint value, int percent) => ApplyFlexToUInt(value, percent);

    private static ulong ApplyFlex(ulong value, int percent)
    {
        var factor = (ulong)(100 + ClampFlex(percent));
        return checked((value * factor + 99) / 100);
    }

    private static uint ApplyFlexToUInt(ulong value, int percent)
    {
        var adjusted = ApplyFlex(value, percent);
        if (adjusted > uint.MaxValue)
            throw new InvalidOperationException("The derived Squire gil ceiling exceeds the Workbench limit.");
        return (uint)adjusted;
    }

    private static (uint ItemId, EquipmentQuality Quality) LineKey(OutfitterWorkbenchLineEnvelope line) =>
        (line.ItemId, line.Quality);

    private static EquipmentQuality? QualityFromPolicy(string policy) => policy switch
    {
        "HQOnly" => EquipmentQuality.High,
        "NQOnly" => EquipmentQuality.Normal,
        _ => null,
    };

    private static string QualityLabel(EquipmentQuality quality) =>
        quality == EquipmentQuality.High ? "HQ" : "NQ";

    private static IReadOnlyList<string> ResolveAuthorizedWorlds(MarketAcquisitionRequestDocument document)
    {
        var dataCenters = MarketAcquisitionWorldCatalog.ResolveDataCenters(document.Region);
        IEnumerable<string> worlds = document.WorldMode == "AllWorldSweep"
            ? document.SweepScope switch
            {
                "Region" => dataCenters.Values.SelectMany(value => value),
                "CurrentDataCenter" => MarketAcquisitionWorldCatalog.ResolveWorldsForDataCenters(
                    document.Region,
                    [MarketAcquisitionWorldCatalog.ResolveDataCenter(document.TargetWorld)]),
                "DataCenters" => MarketAcquisitionWorldCatalog.ResolveWorldsForDataCenters(
                    document.Region,
                    document.SweepDataCenters),
                _ => throw new InvalidOperationException($"Unknown all-world sweep scope {document.SweepScope}."),
            }
            : dataCenters.Values.SelectMany(value => value);
        return worlds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MarketAcquisitionRequestDocument MarkEdited(
        MarketAcquisitionRequestDocument previous,
        MarketAcquisitionRequestDocument updated) =>
        (updated with { LocalRevision = previous.LocalRevision }).WithNextRevision(
            string.IsNullOrWhiteSpace(previous.RemoteRequestId) ? "NewDraft" : "LocalEdits");
}

internal static class OutfitterWorkbenchAuthorityPersistence
{
    public static string? Serialize(OutfitterWorkbenchAuthority? authority) =>
        authority is null ? null : JsonConvert.SerializeObject(authority, Formatting.None);

    public static OutfitterWorkbenchAuthority? Restore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var authority = JsonConvert.DeserializeObject<OutfitterWorkbenchAuthority>(json);
            return authority is not null &&
                   string.Equals(authority.SchemaVersion, OutfitterWorkbenchAuthority.CurrentSchemaVersion, StringComparison.Ordinal)
                ? authority
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
