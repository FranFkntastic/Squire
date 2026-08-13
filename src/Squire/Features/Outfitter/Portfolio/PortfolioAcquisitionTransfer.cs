using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using System.Text;
using System.Text.Json;

namespace MarketMafioso.Squire.Outfitter.Portfolio;

public sealed record PortfolioAcquisitionEvidenceLine(
    string TargetKey,
    string CandidateKey,
    PortfolioAllocationKey Allocation,
    string ItemName,
    string SourceLabel,
    IReadOnlyList<PortfolioAcquisitionPositionDemand> PositionDemands,
    uint ObservedAvailableQuantity,
    uint? UnitPriceGil,
    string EvidenceGeneration,
    DateTimeOffset ReviewedAtUtc,
    string? WorldName = null,
    string? ObservationId = null,
    string? SourceRevision = null,
    string? RetainerName = null,
    string? RetainerId = null)
{
    public PortfolioVendorIdentity? Vendor { get; init; }
}

public sealed record PortfolioAcquisitionPositionDemand(
    EquipmentLoadoutPosition Position,
    uint Quantity);

public sealed record PortfolioAcquisitionLine(
    string TargetKey,
    int Priority,
    PortfolioProgressionHorizon ProgressionHorizon,
    string CandidateKey,
    PortfolioAllocationKey Allocation,
    uint Quantity,
    string ItemName,
    string SourceLabel,
    IReadOnlyList<PortfolioAcquisitionPositionDemand> PositionDemands,
    uint ObservedAvailableQuantity,
    uint? UnitPriceGil,
    string EvidenceGeneration,
    DateTimeOffset ReviewedAtUtc,
    string? WorldName,
    string? ObservationId,
    string? SourceRevision,
    string? RetainerName,
    string? RetainerId)
{
    public PortfolioVendorIdentity? Vendor { get; init; }
}

public sealed record PortfolioAcquisitionTransfer(
    string SchemaVersion,
    PortfolioAuthorityFingerprint AuthorityFingerprint,
    IReadOnlyList<PortfolioAuthorityTarget> Targets,
    IReadOnlyList<PortfolioAcquisitionLine> Lines,
    ulong ObservedMarketTotalGil)
{
    public const string CurrentSchemaVersion = "squire-outfitter-portfolio-acquisition/v1";
    public IReadOnlyList<PortfolioAcquisitionMarketLot> MarketLots { get; init; } = [];
    public IReadOnlyList<PortfolioVendorAcquisitionAction> VendorActions { get; init; } = [];
    public IReadOnlyList<PortfolioArtisanRecipeLine> ArtisanRecipes { get; init; } = [];
}

public sealed record PortfolioAcquisitionLineageKey(
    PortfolioAuthorityFingerprint AuthorityFingerprint,
    string TargetKey,
    string CandidateKey,
    PortfolioAllocationKey Allocation,
    string? ComponentKind = null,
    string? ComponentKey = null);

/// <summary>
/// Carries portfolio lineage through the existing product-neutral Workbench wire without asking
/// MarketMafioso to understand Squire's portfolio schema. The Workbench preserves this exact
/// source catalog key in its reviewed transfer and execution contract; it never turns the key
/// into purchase authority by itself.
/// </summary>
public static class PortfolioAcquisitionLineage
{
    private const string Prefix = "squire-portfolio/v1/";

    public static string Encode(PortfolioAcquisitionLineageKey lineage)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lineage));
        return Prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string value, out PortfolioAcquisitionLineageKey? lineage)
    {
        lineage = null;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        try
        {
            var payload = value[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            lineage = JsonSerializer.Deserialize<PortfolioAcquisitionLineageKey>(
                Convert.FromBase64String(payload));
            return lineage is not null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }
}

public static class PortfolioAcquisitionTransferBuilder
{
    public static PortfolioAcquisitionTransfer Build(
        PortfolioAuthorityEnvelope authority,
        OutfitterPortfolioPlan plan,
        IReadOnlyList<PortfolioAcquisitionEvidenceLine> evidence,
        IReadOnlyList<PortfolioCraftAcquisitionEvidence>? craftEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!authority.HasValidFingerprint() ||
            PortfolioAuthorityEnvelopeFactory.Create(authority.Lineage, plan).Fingerprint != authority.Fingerprint)
            throw new InvalidOperationException("Portfolio acquisition requires the current fingerprinted portfolio authority.");

        var targetByKey = authority.Targets.ToDictionary(target => target.TargetKey, StringComparer.Ordinal);
        var evidenceByAllocation = evidence
            .GroupBy(value => (value.TargetKey, value.CandidateKey, value.Allocation))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var craftMaterialDemands = (craftEvidence ?? [])
            .SelectMany(value => value.Materials.Select(material => (
                value.TargetKey,
                value.CandidateKey,
                Allocation: PortfolioAcquisitionComposition.MaterialAllocation(material),
                material.RequiredQuantity)))
            .ToArray();
        foreach (var demand in craftMaterialDemands)
        {
            var authoritative = authority.Allocations.SingleOrDefault(value =>
                value.TargetKey == demand.TargetKey && value.CandidateKey == demand.CandidateKey &&
                value.Allocation == demand.Allocation);
            if (authoritative is null || authoritative.Quantity != demand.RequiredQuantity)
                throw new InvalidOperationException("A craft terminal-material demand is absent from the fingerprinted portfolio decision.");
        }
        var craftMaterialKeys = craftMaterialDemands
            .Select(value => (value.TargetKey, value.CandidateKey, value.Allocation))
            .ToHashSet();
        var requiredAllocations = authority.Allocations
            .Where(value => value.Allocation.SourceKind is not (PortfolioAllocationSourceKind.OwnedInstance or PortfolioAllocationSourceKind.HandMeDown))
            .Where(value => !craftMaterialKeys.Contains((value.TargetKey, value.CandidateKey, value.Allocation)))
            .Select(value => (value.TargetKey, value.CandidateKey, value.Allocation))
            .ToHashSet();
        if (evidence.Any(value => !requiredAllocations.Contains((value.TargetKey, value.CandidateKey, value.Allocation))))
            throw new InvalidOperationException("Portfolio acquisition evidence contains an allocation outside the fingerprinted decision.");
        var lines = new List<PortfolioAcquisitionLine>();
        foreach (var allocation in authority.Allocations.Where(value =>
                     value.Allocation.SourceKind is not (PortfolioAllocationSourceKind.OwnedInstance or PortfolioAllocationSourceKind.HandMeDown) &&
                     !craftMaterialKeys.Contains((value.TargetKey, value.CandidateKey, value.Allocation))))
        {
            if (!targetByKey.TryGetValue(allocation.TargetKey, out var target) ||
                !string.Equals(target.SelectedCandidateKey, allocation.CandidateKey, StringComparison.Ordinal))
                throw new InvalidOperationException("A portfolio acquisition allocation is outside its selected target authority.");
            if (!evidenceByAllocation.TryGetValue((allocation.TargetKey, allocation.CandidateKey, allocation.Allocation), out var matches) || matches.Length != 1)
                throw new InvalidOperationException($"Allocation {allocation.Allocation.SourceKey} requires one exact acquisition evidence line.");
            var observed = matches[0];
            ValidateEvidence(allocation, target, observed);
            lines.Add(new PortfolioAcquisitionLine(
                allocation.TargetKey,
                target.Priority,
                target.ProgressionHorizon,
                allocation.CandidateKey,
                allocation.Allocation,
                allocation.Quantity,
                observed.ItemName,
                observed.SourceLabel,
                observed.PositionDemands.OrderBy(value => value.Position).ToArray(),
                observed.ObservedAvailableQuantity,
                observed.UnitPriceGil,
                observed.EvidenceGeneration,
                observed.ReviewedAtUtc,
                observed.WorldName,
                observed.ObservationId,
                observed.SourceRevision,
                observed.RetainerName,
                observed.RetainerId)
            {
                Vendor = observed.Vendor,
            });
        }

        foreach (var shared in lines.GroupBy(value => value.Allocation))
        {
            var observedCapacities = shared.Select(value => value.ObservedAvailableQuantity).Distinct().ToArray();
            var required = shared.Aggregate(0u, (sum, value) => checked(sum + value.Quantity));
            if (observedCapacities.Length != 1 || required > observedCapacities[0])
                throw new InvalidOperationException("Portfolio acquisition lines exceed or disagree about one shared source capacity.");
            if (shared.Key.SourceKind == PortfolioAllocationSourceKind.MarketListing &&
                shared.Select(value => (value.WorldName, value.ObservationId, value.SourceRevision, value.UnitPriceGil))
                    .Distinct()
                    .Count() != 1)
                throw new InvalidOperationException("Shared market allocation lines disagree about the exact reviewed listing.");
        }

        var ordered = lines
            .OrderByDescending(value => value.Priority)
            .ThenBy(value => value.ProgressionHorizon)
            .ThenBy(value => value.TargetKey, StringComparer.Ordinal)
            .ThenBy(value => value.CandidateKey, StringComparer.Ordinal)
            .ThenBy(value => value.Allocation.SourceKind)
            .ThenBy(value => value.Allocation.SourceKey, StringComparer.Ordinal)
            .ToArray();
        var selectedCraftAllocations = ordered
            .Where(value => value.Allocation.SourceKind == PortfolioAllocationSourceKind.Craft)
            .GroupBy(value => (value.TargetKey, value.CandidateKey))
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => value.Allocation).OrderBy(value => value.SourceKey, StringComparer.Ordinal).ToArray());
        var craftByTarget = (craftEvidence ?? [])
            .GroupBy(value => (value.TargetKey, value.CandidateKey))
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (craftByTarget.Any(value => !selectedCraftAllocations.ContainsKey(value.Key)) ||
            selectedCraftAllocations.Any(value =>
                !craftByTarget.TryGetValue(value.Key, out var matches) || matches.Length != 1 ||
                !matches[0].ParentAllocations.OrderBy(allocation => allocation.SourceKey, StringComparer.Ordinal)
                    .SequenceEqual(value.Value)))
            throw new InvalidOperationException("Every selected craft allocation requires one exact full craft handoff projection.");
        var composition = PortfolioAcquisitionComposition.Build(
            authority.Fingerprint,
            ordered,
            craftEvidence ?? []);
        return new(
            PortfolioAcquisitionTransfer.CurrentSchemaVersion,
            authority.Fingerprint,
            authority.Targets,
            ordered,
            composition.MarketTotalGil)
        {
            MarketLots = composition.MarketLots,
            VendorActions = composition.VendorActions,
            ArtisanRecipes = composition.ArtisanRecipes,
        };
    }

    public static OutfitterWorkbenchTransfer ToWorkbenchTransfer(
        PortfolioAcquisitionTransfer portfolio,
        EquipmentUtilityProfileKey profile,
        EquipmentUtilityContext context,
        string acquisitionRegion,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(portfolio);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(acquisitionRegion);
        if (portfolio.Lines.Count == 0)
            throw new InvalidOperationException("The portfolio has no non-owned acquisitions to review.");
        if (portfolio.MarketLots.Count == 0)
            throw new InvalidOperationException("The portfolio has no exact market lots for the Market Workbench.");
        var selected = portfolio.Lines.SelectMany(line => line.PositionDemands.Select(position =>
            new OutfitterWorkbenchSelectionLineage(
                position.Position,
                OfferKey(portfolio, line),
                position.Quantity,
                line.ObservationId,
                $"{line.TargetKey} / {line.CandidateKey} / {line.SourceLabel}"))).ToArray();
        var marketLots = portfolio.MarketLots
            .Select(line => new OutfitterWorkbenchMarketLot(
                new(
                    line.ItemId,
                    line.IsHighQuality ? EquipmentQuality.High : EquipmentQuality.Normal,
                    EquipmentAcquisitionSourceKind.MarketBoard,
                    line.LineageKey),
                line.ItemName,
                line.RequiredQuantity,
                line.ObservedAvailableQuantity,
                line.WorldName,
                line.UnitPriceGil,
                checked((ulong)line.UnitPriceGil * line.RequiredQuantity),
                line.ObservationId,
                line.SourceRevision,
                line.ReviewedAtUtc,
                line.RetainerName,
                line.RetainerId,
                ItemKind: line.IsCraftMaterial ? "CraftMaterial" : "PortfolioEquipment"))
            .ToArray();
        var generation = new Guid(Convert.FromHexString(portfolio.AuthorityFingerprint.Sha256[..32]));
        return new(
            OutfitterWorkbenchTransfer.CurrentSchemaVersion,
            OutfitterWorkbenchTransfer.SquireOutfitterOrigin,
            $"portfolio:{portfolio.AuthorityFingerprint.Sha256}",
            null,
            profile,
            context,
            new(
                generation,
                1,
                portfolio.SchemaVersion,
                portfolio.AuthorityFingerprint.Sha256,
                acquisitionRegion,
                OutfitterMarketCoverageMode.ExhaustiveWithinScope,
                createdAtUtc),
            selected,
            marketLots,
            portfolio.ObservedMarketTotalGil,
            DryRunOnly: false);
    }

    private static void ValidateEvidence(
        PortfolioAuthorityAllocation allocation,
        PortfolioAuthorityTarget target,
        PortfolioAcquisitionEvidenceLine evidence)
    {
        if (allocation.Quantity == 0 || allocation.Quantity > evidence.ObservedAvailableQuantity ||
            string.IsNullOrWhiteSpace(evidence.ItemName) || string.IsNullOrWhiteSpace(evidence.SourceLabel) ||
            string.IsNullOrWhiteSpace(evidence.EvidenceGeneration) || evidence.ReviewedAtUtc == default ||
            evidence.PositionDemands.Count == 0 ||
            evidence.PositionDemands.Any(value => value.Quantity == 0) ||
            evidence.PositionDemands.Aggregate(0u, (sum, value) => checked(sum + value.Quantity)) != allocation.Quantity)
            throw new InvalidOperationException("Portfolio acquisition evidence is incomplete or cannot satisfy the exact selected quantity.");
        if (!string.Equals(evidence.EvidenceGeneration, target.EvidenceGeneration, StringComparison.Ordinal))
            throw new InvalidOperationException("Portfolio acquisition evidence does not match the selected target evidence generation.");
        if (allocation.Allocation.SourceKind == PortfolioAllocationSourceKind.MarketListing &&
            (evidence.UnitPriceGil is null || string.IsNullOrWhiteSpace(evidence.WorldName) ||
             string.IsNullOrWhiteSpace(evidence.ObservationId) || string.IsNullOrWhiteSpace(evidence.SourceRevision)))
            throw new InvalidOperationException("Market allocation evidence requires one exact listing, world, price, and source revision.");
        if (allocation.Allocation.SourceKind == PortfolioAllocationSourceKind.GilVendor && evidence.Vendor is null)
            throw new InvalidOperationException("Vendor allocation evidence requires exact shop, NPC, territory, price, and catalog identity.");
        if (allocation.Allocation.SourceKind == PortfolioAllocationSourceKind.GilVendor &&
            evidence.Vendor is { } vendor && evidence.UnitPriceGil != vendor.UnitPriceGil)
            throw new InvalidOperationException("Vendor allocation evidence disagrees with the exact catalog price.");
    }

    private static EquipmentOfferKey OfferKey(PortfolioAcquisitionTransfer portfolio, PortfolioAcquisitionLine line) => new(
        line.Allocation.ItemId,
        line.Allocation.IsHighQuality ? EquipmentQuality.High : EquipmentQuality.Normal,
        line.Allocation.SourceKind switch
        {
            PortfolioAllocationSourceKind.MarketListing => EquipmentAcquisitionSourceKind.MarketBoard,
            PortfolioAllocationSourceKind.GilVendor => EquipmentAcquisitionSourceKind.GilVendor,
            PortfolioAllocationSourceKind.Craft => EquipmentAcquisitionSourceKind.Craft,
            _ => throw new InvalidOperationException("Owned and hand-me-down allocations do not belong in acquisition transfer."),
        },
        PortfolioAcquisitionLineage.Encode(new(
            portfolio.AuthorityFingerprint,
            line.TargetKey,
            line.CandidateKey,
            line.Allocation)));
}

public enum PortfolioAcquisitionRecoveryStatus
{
    AwaitingAcquisition,
    Rebuilding,
    AwaitingUpdatedReview,
    ReadyToEquip,
    Failed,
}

public sealed record PortfolioAcquisitionRecoveryState(
    string SchemaVersion,
    PortfolioAcquisitionRecoveryStatus Status,
    PortfolioAuthorityFingerprint StagedAuthorityFingerprint,
    PortfolioAcquisitionTransfer Transfer,
    DateTimeOffset UpdatedAtUtc,
    string Diagnostic)
{
    public const string CurrentSchemaVersion = "squire-outfitter-portfolio-acquisition-recovery/v2";

    public PortfolioAuthorityFingerprint AuthorityFingerprint => Transfer.AuthorityFingerprint;
    public IReadOnlyList<PortfolioAuthorityTarget> Targets => Transfer.Targets;
    public IReadOnlyList<PortfolioAcquisitionActionProgress> Progress { get; init; } = [];
}

public static class PortfolioAcquisitionRecovery
{
    public static PortfolioAcquisitionRecoveryState Staged(
        PortfolioAcquisitionTransfer transfer,
        DateTimeOffset nowUtc,
        bool marketStaged = false) =>
        new(
            PortfolioAcquisitionRecoveryState.CurrentSchemaVersion,
            PortfolioAcquisitionRecoveryStatus.AwaitingAcquisition,
            transfer.AuthorityFingerprint,
            transfer,
            nowUtc,
            transfer.MarketLots.Count > 0 && marketStaged
                ? "Exact market lots are staged in Market Workbench; vendor and craft actions remain explicit in Squire."
                : transfer.MarketLots.Count > 0
                    ? "Exact market lots are ready for reviewed Market Workbench staging; vendor and craft actions remain explicit in Squire."
                : "The vendor and craft acquisition checklist is ready; no Market Workbench purchase was implied.")
        {
            Progress = PortfolioAcquisitionComposition.InitialProgress(transfer, nowUtc, marketStaged),
        };

    public static PortfolioAcquisitionRecoveryState BeginRebuild(PortfolioAcquisitionRecoveryState state, DateTimeOffset nowUtc) =>
        state with
        {
            Status = PortfolioAcquisitionRecoveryStatus.Rebuilding,
            UpdatedAtUtc = nowUtc,
            Diagnostic = "Re-observing every included target and rebuilding the global allocation from acquired inventory.",
        };

    public static PortfolioAcquisitionRecoveryState Reconciled(
        PortfolioAcquisitionRecoveryState state,
        PortfolioAuthorityEnvelope authority,
        PortfolioAcquisitionTransfer transfer,
        DateTimeOffset nowUtc)
    {
        if (state.Status != PortfolioAcquisitionRecoveryStatus.Rebuilding)
            return state with
            {
                Status = PortfolioAcquisitionRecoveryStatus.Failed,
                UpdatedAtUtc = nowUtc,
                Diagnostic = "Portfolio acquisition recovery was not in its explicit rebuilding state.",
            };
        var expectedTargets = state.Targets.OrderBy(value => value.TargetKey, StringComparer.Ordinal)
            .Select(value => (value.TargetKey, value.Priority, value.ProgressionHorizon))
            .ToArray();
        var currentTargets = authority.Targets.OrderBy(value => value.TargetKey, StringComparer.Ordinal)
            .Select(value => (value.TargetKey, value.Priority, value.ProgressionHorizon))
            .ToArray();
        if (!expectedTargets.SequenceEqual(currentTargets))
            return state with
            {
                Status = PortfolioAcquisitionRecoveryStatus.Failed,
                UpdatedAtUtc = nowUtc,
                Diagnostic = "Portfolio target inclusion, priority, or progression horizon changed during acquisition recovery.",
            };
        return state with
        {
            Status = transfer.Lines.Count == 0
                ? PortfolioAcquisitionRecoveryStatus.ReadyToEquip
                : PortfolioAcquisitionRecoveryStatus.AwaitingUpdatedReview,
            Transfer = transfer,
            Progress = ReconcileProgress(state.Progress, transfer, nowUtc),
            UpdatedAtUtc = nowUtc,
            Diagnostic = transfer.Lines.Count == 0
                ? "Acquired inventory satisfies the portfolio; exact equipment preparation may continue."
                : "Fresh evidence changed or retained portfolio acquisitions; the reviewed Workbench handoff was rebuilt.",
        };
    }

    public static bool NeedsAutomaticRebuild(PortfolioAcquisitionRecoveryState state) =>
        state.Status is PortfolioAcquisitionRecoveryStatus.AwaitingAcquisition or
            PortfolioAcquisitionRecoveryStatus.AwaitingUpdatedReview or
            PortfolioAcquisitionRecoveryStatus.Rebuilding;

    public static PortfolioAcquisitionRecoveryState MarkVendorConfirmed(
        PortfolioAcquisitionRecoveryState state,
        string lineageKey,
        DateTimeOffset nowUtc) =>
        UpdateProgress(state, lineageKey, PortfolioAcquisitionActionKind.VendorChecklist,
            PortfolioAcquisitionActionStatus.UserConfirmed, nowUtc, "User confirmed this exact vendor checklist line; inventory proof is still required.");

    public static PortfolioAcquisitionRecoveryState MarkArtisanExported(
        PortfolioAcquisitionRecoveryState state,
        string lineageKey,
        string exportSha256,
        DateTimeOffset nowUtc) =>
        UpdateProgress(state, lineageKey, PortfolioAcquisitionActionKind.ArtisanExport,
            PortfolioAcquisitionActionStatus.Exported, nowUtc, exportSha256);

    private static PortfolioAcquisitionRecoveryState UpdateProgress(
        PortfolioAcquisitionRecoveryState state,
        string lineageKey,
        PortfolioAcquisitionActionKind kind,
        PortfolioAcquisitionActionStatus status,
        DateTimeOffset nowUtc,
        string receipt)
    {
        var matches = state.Progress.Count(value => value.LineageKey == lineageKey && value.Kind == kind);
        if (matches != 1)
            return state with
            {
                Status = PortfolioAcquisitionRecoveryStatus.Failed,
                UpdatedAtUtc = nowUtc,
                Diagnostic = "The reviewed acquisition action no longer matches one exact persisted portfolio line.",
            };
        return state with
        {
            Progress = state.Progress.Select(value => value.LineageKey == lineageKey && value.Kind == kind
                ? value with { Status = status, UpdatedAtUtc = nowUtc, Receipt = receipt }
                : value).ToArray(),
            UpdatedAtUtc = nowUtc,
        };
    }

    private static IReadOnlyList<PortfolioAcquisitionActionProgress> ReconcileProgress(
        IReadOnlyList<PortfolioAcquisitionActionProgress> previous,
        PortfolioAcquisitionTransfer transfer,
        DateTimeOffset nowUtc)
    {
        var fresh = PortfolioAcquisitionComposition.InitialProgress(transfer, nowUtc, marketStaged: false);
        var previousByKey = previous.ToDictionary(value => (value.LineageKey, value.Kind));
        return fresh.Select(value => previousByKey.TryGetValue((value.LineageKey, value.Kind), out var retained)
            ? retained
            : value).ToArray();
    }
}
