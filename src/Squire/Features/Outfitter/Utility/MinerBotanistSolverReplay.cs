using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

public interface IAdvisorSolverReplay
{
    EquipmentExactFrontierRequest ToRequest();
}

public sealed record MinerBotanistSolverReplayProfile(
    MinerBotanistUtilityContextKind Context,
    uint ClassJobId,
    uint CharacterLevel,
    MinerBotanistUtilityStats OfferBaseline,
    MinerBotanistUtilityStats FixedStats);

public sealed record MinerBotanistSolverReplayBaseline(
    EquipmentLoadoutPosition Position,
    int? OfferIndex);

public sealed record MinerBotanistSolverReplayRisk(
    int FreshnessBucket,
    int IncompleteCoverageCount,
    int ConfidencePenalty);

public sealed record MinerBotanistSolverReplayOffer(
    int Index,
    uint ItemId,
    EquipmentQuality Quality,
    EquipmentAcquisitionSourceKind SourceKind,
    string CatalogKey,
    string? ObservationId,
    EquipmentSlot Slot,
    IReadOnlyList<EquipmentLoadoutPosition> Positions,
    uint AvailableQuantity,
    IReadOnlyList<EquipmentSolverUtilityComponent> Utility,
    ulong AcquisitionCostGil,
    string? WorldVisitKey,
    string? VendorStopKey,
    int PurchaseTransactions,
    MinerBotanistSolverReplayRisk EvidenceRisk,
    bool IsUnique,
    sbyte OffHandOccupancy);

public sealed record MinerBotanistSolverReplay(
    string SchemaVersion,
    MinerBotanistSolverReplayProfile Profile,
    IReadOnlyList<EquipmentLoadoutPosition> RequiredPositions,
    IReadOnlyList<MinerBotanistSolverReplayBaseline> Baseline,
    IReadOnlyList<MinerBotanistSolverReplayOffer> Offers) : IAdvisorSolverReplay
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-min-btn-solver-replay/v1";

    public static MinerBotanistSolverReplay Capture(
        EquipmentExactFrontierRequest request,
        MinerBotanistUtilityContextKind context,
        uint classJobId,
        uint characterLevel,
        MinerBotanistUtilityStats offerBaseline,
        MinerBotanistUtilityStats fixedStats)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ordered = request.Offers
            .OrderBy(offer => offer.Offer.Key.ItemId)
            .ThenBy(offer => offer.Offer.Key.Quality)
            .ThenBy(offer => offer.Offer.Key.SourceKind)
            .ThenBy(offer => offer.Offer.Key.SourceCatalogKey, StringComparer.Ordinal)
            .ThenBy(offer => offer.ObservationId, StringComparer.Ordinal)
            .ToArray();
        var itemIds = Map(ordered.Select(value => value.Offer.Key.ItemId), Comparer<uint>.Default);
        var catalogKeys = Map(ordered.Select(value => value.Offer.Key.SourceCatalogKey), StringComparer.Ordinal);
        var observations = Map(
            ordered.Select(value => value.ObservationId).Where(value => value is not null).Cast<string>(),
            StringComparer.Ordinal);
        var worlds = Map(
            ordered.Select(value => value.WorldVisitKey).Where(value => value is not null).Cast<string>(),
            StringComparer.Ordinal);
        var vendors = Map(
            ordered.Select(value => value.VendorStopKey).Where(value => value is not null).Cast<string>(),
            StringComparer.Ordinal);

        string Catalog(string value) => $"catalog-{catalogKeys[value]:D4}";
        string? Observation(string? value) => value is null ? null : $"observation-{observations[value]:D4}";
        string? World(string? value) => value is null ? null : $"world-{worlds[value]:D2}";
        string? Vendor(string? value) => value is null ? null : $"vendor-{vendors[value]:D2}";

        var offers = ordered.Select((value, index) => new MinerBotanistSolverReplayOffer(
            index,
            checked((uint)itemIds[value.Offer.Key.ItemId]),
            value.Offer.Key.Quality,
            value.Offer.Key.SourceKind,
            Catalog(value.Offer.Key.SourceCatalogKey),
            Observation(value.ObservationId),
            value.Offer.Definition.Slot,
            value.Positions.Order().ToArray(),
            value.AvailableQuantity,
            value.Utility.Normalize().Components,
            value.AcquisitionCostGil,
            World(value.WorldVisitKey),
            Vendor(value.VendorStopKey),
            value.PurchaseTransactions,
            new(
                value.EvidenceRisk.FreshnessBucket,
                value.EvidenceRisk.IncompleteCoverageCount,
                value.EvidenceRisk.ConfidencePenalty),
            value.Offer.Definition.IsUnique,
            value.Offer.Definition.OffHandOccupancy)).ToArray();
        var offerIndexByAllocation = ordered
            .Select((value, index) => (value.AllocationKey, index))
            .ToDictionary(value => value.AllocationKey, value => value.index);
        var baseline = request.RequiredPositions.Order().Select(position => new MinerBotanistSolverReplayBaseline(
            position,
            request.Baseline[position] is { } allocation ? offerIndexByAllocation[allocation] : null)).ToArray();
        return new(
            CurrentSchemaVersion,
            new(context, classJobId, characterLevel, offerBaseline, fixedStats),
            request.RequiredPositions.Order().ToArray(),
            baseline,
            offers);
    }

    public EquipmentExactFrontierRequest ToRequest()
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported solver replay schema '{SchemaVersion}'.");
        var offers = Offers.OrderBy(value => value.Index).Select(value => value.ToOffer(Profile.ClassJobId)).ToArray();
        var byIndex = Offers.OrderBy(value => value.Index)
            .Select((value, index) => (value.Index, Offer: offers[index]))
            .ToDictionary(value => value.Index, value => value.Offer);
        var baseline = Baseline.ToDictionary(
            value => value.Position,
            value => value.OfferIndex is { } index
                ? (EquipmentOfferAllocationKey?)byIndex[index].AllocationKey
                : null);
        return new(
            offers,
            RequiredPositions.ToHashSet(),
            baseline,
            new MinerBotanistUtilityProfile(
                Profile.Context,
                Profile.OfferBaseline,
                Profile.ClassJobId,
                Profile.CharacterLevel,
                Profile.FixedStats));
    }

    private static Dictionary<T, int> Map<T>(IEnumerable<T> values, IComparer<T> comparer) where T : notnull =>
        values.Distinct().OrderBy(value => value, comparer).Select((value, index) => (value, index: index + 1))
            .ToDictionary(value => value.value, value => value.index);
}

internal static class MinerBotanistSolverReplayOfferExtensions
{
    public static EquipmentExactSolverOffer ToOffer(this MinerBotanistSolverReplayOffer value, uint classJobId)
    {
        var definition = new EquipmentItemDefinition(
            value.ItemId,
            $"Replay item {value.ItemId}",
            1,
            1,
            value.Slot,
            new HashSet<uint> { classJobId },
            1,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            IsUnique: value.IsUnique,
            OffHandOccupancy: value.OffHandOccupancy);
        var offer = new EquipmentLoadoutOffer(
            definition,
            value.SourceKind,
            $"Replay {value.SourceKind}",
            value.AcquisitionCostGil > uint.MaxValue ? uint.MaxValue : checked((uint)value.AcquisitionCostGil),
            Quality: value.Quality,
            SourceCatalogKey: value.CatalogKey);
        return new(
            offer,
            value.ObservationId,
            value.Positions.ToHashSet(),
            value.AvailableQuantity,
            new(value.Utility),
            value.AcquisitionCostGil,
            value.WorldVisitKey,
            value.VendorStopKey,
            value.PurchaseTransactions,
            new(
                value.EvidenceRisk.FreshnessBucket,
                value.EvidenceRisk.IncompleteCoverageCount,
                value.EvidenceRisk.ConfidencePenalty),
            [value.Quality.ToString(), value.SourceKind.ToString()]);
    }
}

internal static class AdvisorSolverReplayFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static void Write(string path, IAdvisorSolverReplay replay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(replay);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(replay, replay.GetType(), JsonOptions));
        File.Move(temporaryPath, path, true);
    }
}
