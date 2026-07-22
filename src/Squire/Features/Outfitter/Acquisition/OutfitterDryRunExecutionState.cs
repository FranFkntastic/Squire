using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Squire.Outfitter.Acquisition;

public sealed record OutfitterRouteSunkPurchase
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-outfitter-sunk-purchase/v1";

    public required string SchemaVersion { get; init; }
    public required string ReceiptId { get; init; }
    public required string ContractId { get; init; }
    public required string CanonicalIntentHash { get; init; }
    public required string WorkbenchDocumentId { get; init; }
    public required long WorkbenchRevision { get; init; }
    public required string PlanRequestId { get; init; }
    public required DateTimeOffset PlanPreparedAtUtc { get; init; }
    public required string WorldName { get; init; }
    public required string LineId { get; init; }
    public required uint ItemId { get; init; }
    public required EquipmentQuality Quality { get; init; }
    public required string ListingId { get; init; }
    public required string RetainerId { get; init; }
    public required uint Quantity { get; init; }
    public required uint UnitPriceGil { get; init; }
    public required uint TotalGil { get; init; }
}

public sealed class RestoreOnlyOutfitterRouteExecutionStateStore : IOutfitterRouteExecutionStateStore
{
    private readonly IOutfitterRouteExecutionStateStore source;

    public RestoreOnlyOutfitterRouteExecutionStateStore(IOutfitterRouteExecutionStateStore source) =>
        this.source = source ?? throw new ArgumentNullException(nameof(source));

    public OutfitterRouteExecutionState? Restore() => source.Restore();
    public void Save(OutfitterRouteExecutionState state) { }
    public void Clear() { }
}

public static class OutfitterDryRunPreparedPlanRestorer
{
    private const int MaximumRestoredLines = 64;
    private const int MaximumRestoredListings = 128;

    public static MarketAcquisitionPlan Prepare(
        OutfitterExecutionContract contract,
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionClaimView claim,
        DateTimeOffset preparedAt)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(claim);
        ValidateContractIdentity(contract, document, claim);
        return BuildPinnedPlan(contract, claim, preparedAt, legacyReceipts: null);
    }

    public static MarketAcquisitionPlan Restore(
        OutfitterExecutionContract contract,
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionClaimView claim,
        OutfitterRouteExecutionState persisted)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(persisted);
        ValidateIdentity(contract, document, claim, persisted);

        var preparedAt = persisted.SunkPurchases
            .Select(receipt => receipt.PlanPreparedAtUtc)
            .Distinct()
            .SingleOrDefault();
        if (preparedAt == default)
            throw new InvalidOperationException("Persisted Squire sunk receipts do not identify one prepared plan generation.");
        if (contract.Transfer.Evidence.PublishedAtUtc > preparedAt)
            throw new InvalidOperationException("Persisted Squire listing evidence was published after the identified plan generation.");

        var fullPlan = BuildPinnedPlan(contract, claim, preparedAt, persisted.SunkPurchases);
        return OutfitterDryRunExecutionStateRestorer.RestoreRemainingPlan(
            contract,
            document,
            claim,
            fullPlan,
            persisted);
    }

    private static MarketAcquisitionPlan BuildPinnedPlan(
        OutfitterExecutionContract contract,
        MarketAcquisitionClaimView claim,
        DateTimeOffset preparedAt,
        IReadOnlyList<OutfitterRouteSunkPurchase>? legacyReceipts)
    {
        if (preparedAt == default || contract.Transfer.Evidence.PublishedAtUtc > preparedAt)
            throw new InvalidOperationException("Finalized Squire listing evidence does not precede one valid plan generation.");
        var contractLines = contract.Lines.ToDictionary(
            line => (line.ItemId, line.Quality),
            line => line);
        var claimLines = claim.Lines.ToDictionary(
            line => (line.ItemId, QualityFromPolicy(line.HqPolicy)),
            line => line);
        if (contractLines.Count != contract.Lines.Count || claimLines.Count != claim.Lines.Count ||
            contractLines.Count != claimLines.Count)
            throw new InvalidOperationException("The finalized Squire contract and accepted claim do not have one exact line mapping.");

        foreach (var pair in contractLines)
        {
            if (!claimLines.TryGetValue(pair.Key, out var line) ||
                !line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase) ||
                line.TargetQuantity != pair.Value.RequiredQuantity || line.MaxQuantity != 0 ||
                line.MaxUnitPrice != pair.Value.MaxUnitPriceGil || line.GilCap != pair.Value.MaxTotalGil ||
                string.IsNullOrWhiteSpace(line.LineId))
                throw new InvalidOperationException($"The accepted claim no longer exactly matches {pair.Value.ItemName} authority.");
        }

        var resolved = contract.Transfer.MarketLots.Select(lot =>
        {
            if (lot.OfferKey.SourceKind != EquipmentAcquisitionSourceKind.MarketBoard ||
                !contractLines.TryGetValue((lot.OfferKey.ItemId, lot.OfferKey.Quality), out var envelope) ||
                string.IsNullOrWhiteSpace(lot.DiscoveryObservationId) || string.IsNullOrWhiteSpace(lot.WorldName) ||
                string.IsNullOrWhiteSpace(lot.SourceRevision) || lot.ReviewedAtUtc == default ||
                lot.ReviewedAtUtc > contract.Transfer.Evidence.PublishedAtUtc ||
                lot.RequiredQuantity == 0 || lot.RequiredQuantity != lot.ObservedAvailableQuantity ||
                lot.ObservedUnitPriceGil == 0 || lot.ObservedUnitPriceGil > envelope.MaxUnitPriceGil ||
                lot.ObservedTotalPriceGil != checked((ulong)lot.RequiredQuantity * lot.ObservedUnitPriceGil) ||
                lot.ObservedTotalPriceGil > uint.MaxValue ||
                !contract.AuthorizedWorlds.Contains(lot.WorldName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Persisted Squire listing authority is incomplete or outside its finalized envelope.");

            var retainerId = lot.RetainerId;
            if (string.IsNullOrWhiteSpace(retainerId))
            {
                var receiptRetainers = (legacyReceipts ?? [])
                    .Where(receipt => receipt.ItemId == lot.OfferKey.ItemId && receipt.Quality == lot.OfferKey.Quality &&
                        receipt.WorldName.Equals(lot.WorldName, StringComparison.OrdinalIgnoreCase) &&
                        receipt.ListingId == lot.DiscoveryObservationId && receipt.UnitPriceGil == lot.ObservedUnitPriceGil)
                    .Select(receipt => receipt.RetainerId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (receiptRetainers.Length != 1)
                    throw new InvalidOperationException("Persisted Squire listing authority lacks an exact retainer identity; prepare a new dry-run plan.");
                retainerId = receiptRetainers[0];
            }

            var claimLine = claimLines[(lot.OfferKey.ItemId, lot.OfferKey.Quality)];
            return new ResolvedLot(
                claimLine.LineId,
                lot,
                lot.RetainerName ?? string.Empty,
                retainerId);
        }).ToArray();

        if (resolved.Select(LotIdentity).Distinct(StringComparer.Ordinal).Count() != resolved.Length)
            throw new InvalidOperationException("Persisted Squire listing authority contains duplicate listing identities.");
        foreach (var pair in contractLines)
        {
            var quantity = resolved
                .Where(value => value.Lot.OfferKey.ItemId == pair.Key.ItemId && value.Lot.OfferKey.Quality == pair.Key.Quality)
                .Aggregate(0ul, (sum, value) => checked(sum + value.Lot.RequiredQuantity));
            var gil = resolved
                .Where(value => value.Lot.OfferKey.ItemId == pair.Key.ItemId && value.Lot.OfferKey.Quality == pair.Key.Quality)
                .Aggregate(0ul, (sum, value) => checked(sum + value.Lot.ObservedTotalPriceGil));
            if (quantity != pair.Value.RequiredQuantity || gil > pair.Value.MaxTotalGil)
                throw new InvalidOperationException($"Persisted Squire listing authority does not exactly fulfill {pair.Value.ItemName} within its gil cap.");
        }
        var observedTotal = resolved.Aggregate(0ul, (sum, value) => checked(sum + value.Lot.ObservedTotalPriceGil));
        if (observedTotal != contract.Transfer.ObservedMarketTotalGil || observedTotal > contract.SquirePlanCapGil)
            throw new InvalidOperationException("Persisted Squire listing totals disagree with the finalized transfer or exceed its plan cap.");

        var listings = resolved.Select(value => new MarketAcquisitionListing
        {
            ItemId = value.Lot.OfferKey.ItemId,
            ItemName = value.Lot.ItemName,
            ListingId = value.Lot.DiscoveryObservationId,
            WorldName = value.Lot.WorldName,
            RetainerName = value.RetainerName,
            RetainerId = value.RetainerId,
            Quantity = value.Lot.RequiredQuantity,
            UnitPrice = value.Lot.ObservedUnitPriceGil,
            IsHq = value.Lot.OfferKey.Quality == EquipmentQuality.High,
            LastReviewTimeUtc = value.Lot.ReviewedAtUtc,
        }).ToArray();
        var fullPlan = MarketAcquisitionPlanner.BuildPlan(claim, listings, preparedAt, contract.TargetWorld);
        var plannedIdentities = fullPlan.WorldBatches
            .SelectMany(batch => batch.ItemSubtasks.SelectMany(subtask => subtask.Listings.Select(listing =>
                $"{batch.WorldName.ToUpperInvariant()}|{subtask.LineId}|{listing.ListingId}|{listing.RetainerId}|{listing.Quantity}|{listing.UnitPrice}|{listing.IsHq}")))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var expectedIdentities = resolved.Select(PlanIdentity).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!plannedIdentities.SequenceEqual(expectedIdentities, StringComparer.Ordinal))
            throw new InvalidOperationException("Reconstructed Squire plan does not preserve every exact finalized listing row.");
        return fullPlan;
    }

    private static void ValidateIdentity(
        OutfitterExecutionContract contract,
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionClaimView claim,
        OutfitterRouteExecutionState persisted)
    {
        if (persisted.SunkPurchases is not { Count: > 0 })
            throw new InvalidOperationException("Persisted Squire sunk receipts are unavailable.");
        ValidateContractIdentity(contract, document, claim);
        var intentHash = MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(document);
        if (string.IsNullOrWhiteSpace(document.LastPlanHash) || document.LastPlanHash != intentHash ||
            claim.Id != persisted.SunkPurchases.FirstOrDefault()?.PlanRequestId)
            throw new InvalidOperationException("Finalized Workbench, claim, contract, and prepared-plan identity no longer agree.");
        if (persisted.Phase != OutfitterRouteAuthorityPhase.Paused ||
            contract.Lines.Count == 0 || contract.Lines.Count > MaximumRestoredLines ||
            contract.Transfer.MarketLots.Count == 0 || contract.Transfer.MarketLots.Count > MaximumRestoredListings ||
            contract.Transfer.Evidence.GenerationId == Guid.Empty || contract.Transfer.Evidence.Revision <= 0 ||
            contract.Transfer.Evidence.PublishedAtUtc == default ||
            !OutfitterRouteAuthoritySession.IsRestoredStateSane(persisted, contract, claim))
            throw new InvalidOperationException("Persisted Squire sunk state or listing evidence is unavailable, unbounded, active, or mismatched.");
    }

    private static void ValidateContractIdentity(
        OutfitterExecutionContract contract,
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionClaimView claim)
    {
        var finalized = document.OutfitterAuthority?.FinalizedContract;
        if (!contract.Transfer.DryRunOnly ||
            contract.SchemaVersion != OutfitterExecutionContract.CurrentSchemaVersion ||
            contract.Transfer.SchemaVersion != OutfitterWorkbenchTransfer.CurrentSchemaVersion ||
            contract.Transfer.Origin != OutfitterWorkbenchTransfer.SquireOutfitterOrigin ||
            finalized?.ContractId != contract.ContractId || finalized.CanonicalIntentHash != contract.CanonicalIntentHash ||
            document.LocalRequestId != contract.WorkbenchDocumentId || document.LocalRevision != contract.WorkbenchRevision ||
            OutfitterWorkbenchAuthorityService.ComputeCanonicalIntentHash(document) != contract.CanonicalIntentHash ||
            claim.TargetCharacterName != contract.TargetCharacterName || claim.TargetWorld != contract.TargetWorld ||
            claim.Region != contract.Region || claim.WorldMode != contract.WorldMode ||
            contract.Transfer.Evidence.Region != contract.Region ||
            contract.Lines.Count == 0 || contract.Lines.Count > MaximumRestoredLines ||
            contract.Transfer.MarketLots.Count == 0 || contract.Transfer.MarketLots.Count > MaximumRestoredListings ||
            contract.Transfer.Evidence.GenerationId == Guid.Empty || contract.Transfer.Evidence.Revision <= 0 ||
            contract.Transfer.Evidence.PublishedAtUtc == default)
            throw new InvalidOperationException("Finalized Workbench, claim, contract, and exact listing authority no longer agree.");
    }

    private static EquipmentQuality QualityFromPolicy(string policy) =>
        MarketAcquisitionPolicy.NormalizeHqPolicy(policy) switch
        {
            "HqOnly" => EquipmentQuality.High,
            "NqOnly" => EquipmentQuality.Normal,
            _ => throw new InvalidOperationException("Squire startup restoration requires exact NQ or HQ claim authority."),
        };

    private static string LotIdentity(ResolvedLot value) =>
        $"{value.Lot.WorldName.ToUpperInvariant()}|{value.LineId}|{value.Lot.DiscoveryObservationId}|{value.RetainerId}";

    private static string PlanIdentity(ResolvedLot value) =>
        $"{LotIdentity(value)}|{value.Lot.RequiredQuantity}|{value.Lot.ObservedUnitPriceGil}|{value.Lot.OfferKey.Quality == EquipmentQuality.High}";

    private sealed record ResolvedLot(
        string LineId,
        OutfitterWorkbenchMarketLot Lot,
        string RetainerName,
        string RetainerId);
}

public static class OutfitterDryRunExecutionStateRestorer
{
    internal static bool MatchesFinalizedMarketLot(
        OutfitterExecutionContract contract,
        string worldName,
        MarketAcquisitionPlannedListing listing)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(listing);
        return contract.Transfer.MarketLots.Count(lot =>
            lot.OfferKey.ItemId == listing.ItemId &&
            lot.OfferKey.Quality == (listing.IsHq ? EquipmentQuality.High : EquipmentQuality.Normal) &&
            lot.WorldName.Equals(worldName, StringComparison.OrdinalIgnoreCase) &&
            lot.DiscoveryObservationId == listing.ListingId && lot.RetainerId == listing.RetainerId &&
            lot.RequiredQuantity == listing.Quantity && lot.ObservedAvailableQuantity == listing.Quantity &&
            lot.ObservedUnitPriceGil == listing.UnitPrice && lot.ObservedTotalPriceGil == listing.TotalGil &&
            lot.ReviewedAtUtc == listing.LastReviewTimeUtc) == 1;
    }

    public static MarketAcquisitionPlan RestoreRemainingPlan(
        OutfitterExecutionContract contract,
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionClaimView claim,
        MarketAcquisitionPlan plan,
        OutfitterRouteExecutionState? persisted)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(plan);
        if (persisted is null)
            return plan;
        if (!OutfitterRouteAuthoritySession.IsRestoredStateSane(persisted, contract, claim))
            throw new InvalidOperationException("Persisted Squire sunk state does not exactly match the finalized contract and line envelopes.");
        if (persisted.SunkPurchases is null)
            throw new InvalidOperationException("Persisted Squire sunk receipt collection is unavailable.");

        var purchasedQuantity = persisted.Lines.Aggregate(0ul, (sum, line) => checked(sum + line.PurchasedQuantity));
        if (purchasedQuantity == 0 && persisted.TotalSpentGil == 0)
        {
            if (persisted.SunkPurchases.Count != 0)
                throw new InvalidOperationException("Persisted Squire sunk receipts exist without matching quantity and gil totals.");
            return plan;
        }
        if (persisted.SunkPurchases.Count == 0)
            throw new InvalidOperationException("Persisted Squire sunk totals have no world/listing receipts and cannot authorize dry-run restoration.");

        foreach (var receipt in persisted.SunkPurchases)
            ValidateReceiptIdentity(contract, document, claim, receipt);
        ValidateReceiptTotals(persisted);
        if (IsAlreadyRemainingPlan(plan, persisted))
        {
            ValidateRemainingPlan(contract, persisted, plan);
            return plan;
        }
        foreach (var receipt in persisted.SunkPurchases)
            ValidateReceipt(contract, document, claim, plan, receipt);

        var deductions = persisted.SunkPurchases
            .GroupBy(ReceiptKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new ReceiptDeduction(
                    checked((uint)group.Aggregate(0ul, (sum, receipt) => checked(sum + receipt.Quantity))),
                    checked((uint)group.Aggregate(0ul, (sum, receipt) => checked(sum + receipt.TotalGil)))),
                StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var remainingByLine = persisted.Lines.ToDictionary(line => line.LineId, StringComparer.Ordinal);
        var batches = plan.WorldBatches.Select(batch =>
        {
            var subtasks = batch.ItemSubtasks.Select(subtask =>
            {
                var listings = subtask.Listings.Select(listing =>
                {
                    var key = ReceiptKey(batch.WorldName, subtask.LineId, listing.ListingId, listing.RetainerId);
                    if (!deductions.TryGetValue(key, out var deduction))
                        return listing;
                    if (!matched.Add(key) || deduction.Quantity > listing.Quantity ||
                        deduction.TotalGil != checked(deduction.Quantity * listing.UnitPrice))
                        throw new InvalidOperationException("Persisted Squire sunk receipt exceeds or ambiguously matches its exact planned listing.");
                    var quantity = listing.Quantity - deduction.Quantity;
                    return quantity == 0 ? null : listing with
                    {
                        Quantity = quantity,
                        TotalGil = checked(quantity * listing.UnitPrice),
                    };
                }).Where(listing => listing is not null).Cast<MarketAcquisitionPlannedListing>().ToArray();
                var line = remainingByLine[subtask.LineId];
                return subtask with
                {
                    RequestedQuantity = line.RequiredQuantity - line.PurchasedQuantity,
                    GilCap = line.MaxTotalGil - line.SpentGil,
                    PlannedQuantity = (uint)listings.Sum(listing => listing.Quantity),
                    PlannedGil = (uint)listings.Sum(listing => listing.TotalGil),
                    Listings = listings,
                };
            }).Where(subtask => subtask.PlannedQuantity > 0).ToArray();
            var listings = subtasks.SelectMany(subtask => subtask.Listings).ToArray();
            return batch with
            {
                PlannedQuantity = (uint)subtasks.Sum(subtask => subtask.PlannedQuantity),
                PlannedGil = (uint)subtasks.Sum(subtask => subtask.PlannedGil),
                ItemSubtasks = subtasks,
                Listings = listings,
            };
        }).Where(batch => batch.PlannedQuantity > 0).ToArray();
        if (matched.Count != deductions.Count)
            throw new InvalidOperationException("Persisted Squire sunk receipt does not identify one exact listing in the prepared route.");

        var subtasksByLine = batches.SelectMany(batch => batch.ItemSubtasks)
            .GroupBy(subtask => subtask.LineId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var lines = plan.Lines.Select(line =>
        {
            var state = remainingByLine[line.LineId];
            subtasksByLine.TryGetValue(line.LineId, out var subtasks);
            subtasks ??= [];
            return line with
            {
                RequestedQuantity = state.RequiredQuantity - state.PurchasedQuantity,
                GilCap = state.MaxTotalGil - state.SpentGil,
                Status = subtasks.Length == 0 ? "NoSupportedListings" : "Ready",
                PlannedQuantity = (uint)subtasks.Sum(subtask => subtask.PlannedQuantity),
                PlannedGil = (uint)subtasks.Sum(subtask => subtask.PlannedGil),
            };
        }).ToArray();
        var remaining = plan with
        {
            Status = batches.Length == 0 ? "NoSupportedListings" : "Ready",
            PlannedQuantity = (uint)batches.Sum(batch => batch.PlannedQuantity),
            PlannedGil = (uint)batches.Sum(batch => batch.PlannedGil),
            Diagnostics = plan.Diagnostics with { PlannedListingCount = batches.Sum(batch => batch.Listings.Count) },
            Lines = lines,
            WorldBatches = batches,
        };
        ValidateRemainingPlan(contract, persisted, remaining);
        return remaining;
    }

    public static string ComputeReceiptId(OutfitterRouteSunkPurchase receipt)
    {
        var identity = string.Join("|",
            receipt.ContractId,
            receipt.CanonicalIntentHash,
            receipt.WorkbenchDocumentId,
            receipt.WorkbenchRevision,
            receipt.PlanRequestId,
            receipt.PlanPreparedAtUtc.ToUniversalTime().ToString("O"),
            receipt.WorldName,
            receipt.LineId,
            receipt.ItemId,
            receipt.Quality,
            receipt.ListingId,
            receipt.RetainerId,
            receipt.Quantity,
            receipt.UnitPriceGil,
            receipt.TotalGil);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    internal static void ValidateReceipt(
        OutfitterExecutionContract contract,
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionClaimView claim,
        MarketAcquisitionPlan plan,
        OutfitterRouteSunkPurchase receipt)
    {
        ValidateReceiptIdentity(contract, document, claim, receipt);
        if (plan.RequestId != claim.Id || receipt.PlanRequestId != plan.RequestId || receipt.PlanPreparedAtUtc != plan.PreparedAtUtc)
            throw new InvalidOperationException("Persisted Squire sunk receipt is stale or bound to different contract, Workbench, or plan evidence.");

        var contractLine = contract.Lines.SingleOrDefault(line =>
            line.ItemId == receipt.ItemId && line.Quality == receipt.Quality);
        var planLine = plan.Lines.SingleOrDefault(line => line.LineId == receipt.LineId);
        var listing = plan.WorldBatches
            .Where(batch => batch.WorldName.Equals(receipt.WorldName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(batch => batch.ItemSubtasks)
            .Where(subtask => subtask.LineId == receipt.LineId)
            .SelectMany(subtask => subtask.Listings)
            .SingleOrDefault(value => value.ListingId == receipt.ListingId && value.RetainerId == receipt.RetainerId);
        var expectedPolicy = receipt.Quality == EquipmentQuality.High ? "HqOnly" : "NqOnly";
        if (contractLine is null || planLine is null || listing is null ||
            !MatchesFinalizedMarketLot(contract, receipt.WorldName, listing) ||
            planLine.ItemId != receipt.ItemId || listing.ItemId != receipt.ItemId ||
            MarketAcquisitionPolicy.NormalizeHqPolicy(planLine.HqPolicy) != expectedPolicy || listing.IsHq != (receipt.Quality == EquipmentQuality.High) ||
            receipt.Quantity == 0 || receipt.Quantity != listing.Quantity || receipt.UnitPriceGil != listing.UnitPrice ||
            listing.TotalGil != checked(listing.Quantity * listing.UnitPrice) ||
            receipt.TotalGil != listing.TotalGil || receipt.TotalGil != checked(receipt.Quantity * receipt.UnitPriceGil) ||
            receipt.UnitPriceGil > contractLine.MaxUnitPriceGil || receipt.TotalGil > contractLine.MaxTotalGil ||
            receipt.TotalGil > contract.SquirePlanCapGil)
            throw new InvalidOperationException("Persisted Squire sunk receipt does not match one exact line, listing, quantity, quality, and gil envelope.");
    }

    private static void ValidateReceiptIdentity(
        OutfitterExecutionContract contract,
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionClaimView claim,
        OutfitterRouteSunkPurchase receipt)
    {
        if (receipt.SchemaVersion != OutfitterRouteSunkPurchase.CurrentSchemaVersion ||
            receipt.ReceiptId != ComputeReceiptId(receipt) ||
            contract.WorkbenchDocumentId != document.LocalRequestId || contract.WorkbenchRevision != document.LocalRevision ||
            receipt.ContractId != contract.ContractId || receipt.CanonicalIntentHash != contract.CanonicalIntentHash ||
            receipt.WorkbenchDocumentId != document.LocalRequestId || receipt.WorkbenchRevision != document.LocalRevision ||
            receipt.PlanRequestId != claim.Id ||
            OutfitterWorkbenchAuthorityService.ComputeCanonicalIntentHash(document) != contract.CanonicalIntentHash)
            throw new InvalidOperationException("Persisted Squire sunk receipt is stale or bound to different contract or Workbench evidence.");
        if (!contract.AuthorizedWorlds.Contains(receipt.WorldName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Persisted Squire sunk receipt names a world outside the finalized contract.");

        var claimLine = claim.Lines.SingleOrDefault(line => line.LineId == receipt.LineId);
        var contractLine = contract.Lines.SingleOrDefault(line => line.ItemId == receipt.ItemId && line.Quality == receipt.Quality);
        var expectedPolicy = receipt.Quality == EquipmentQuality.High ? "HqOnly" : "NqOnly";
        if (claimLine is null || contractLine is null || claimLine.ItemId != receipt.ItemId ||
            MarketAcquisitionPolicy.NormalizeHqPolicy(claimLine.HqPolicy) != expectedPolicy ||
            receipt.Quantity == 0 || receipt.UnitPriceGil == 0 ||
            receipt.TotalGil != checked(receipt.Quantity * receipt.UnitPriceGil) ||
            receipt.UnitPriceGil > contractLine.MaxUnitPriceGil || receipt.Quantity > contractLine.RequiredQuantity ||
            receipt.TotalGil > contractLine.MaxTotalGil || receipt.TotalGil > contract.SquirePlanCapGil)
            throw new InvalidOperationException("Persisted Squire sunk receipt does not match one exact contract line, quantity, quality, and gil envelope.");
    }

    private static bool IsAlreadyRemainingPlan(
        MarketAcquisitionPlan plan,
        OutfitterRouteExecutionState state) => state.Lines.All(line =>
    {
        var planned = plan.Lines.SingleOrDefault(value => value.LineId == line.LineId);
        return planned is not null && planned.ItemId == line.ItemId &&
               planned.RequestedQuantity == line.RequiredQuantity - line.PurchasedQuantity &&
               planned.GilCap == line.MaxTotalGil - line.SpentGil &&
               planned.PlannedQuantity <= planned.RequestedQuantity && planned.PlannedGil <= planned.GilCap;
    });

    private static void ValidateRemainingPlan(
        OutfitterExecutionContract contract,
        OutfitterRouteExecutionState state,
        MarketAcquisitionPlan plan)
    {
        ulong plannedSquireGil = 0;
        foreach (var line in state.Lines)
        {
            var plannedLine = plan.Lines.SingleOrDefault(value => value.LineId == line.LineId);
            var listings = plan.WorldBatches
                .Where(batch => contract.AuthorizedWorlds.Contains(batch.WorldName, StringComparer.OrdinalIgnoreCase))
                .SelectMany(batch => batch.ItemSubtasks)
                .Where(subtask => subtask.LineId == line.LineId)
                .SelectMany(subtask => subtask.Listings)
                .ToArray();
            var plannedQuantity = listings.Aggregate(0ul, (sum, listing) => checked(sum + listing.Quantity));
            var plannedGil = listings.Aggregate(0ul, (sum, listing) => checked(sum + listing.TotalGil));
            var remainingQuantity = line.RequiredQuantity - line.PurchasedQuantity;
            var remainingGil = line.MaxTotalGil - line.SpentGil;
            if (plannedLine is null || plannedLine.ItemId != line.ItemId ||
                MarketAcquisitionPolicy.NormalizeHqPolicy(plannedLine.HqPolicy) !=
                    (line.Quality == EquipmentQuality.High ? "HqOnly" : "NqOnly") ||
                plannedLine.RequestedQuantity != remainingQuantity || plannedLine.GilCap != remainingGil ||
                plannedLine.PlannedQuantity != plannedQuantity || plannedLine.PlannedGil != plannedGil ||
                plannedQuantity > remainingQuantity || plannedGil > remainingGil)
                throw new InvalidOperationException($"Restored Squire route exceeds the exact remaining quantity or gil envelope for {line.ItemName}.");
            if (listings.Any(listing =>
                    string.IsNullOrWhiteSpace(listing.ListingId) || string.IsNullOrWhiteSpace(listing.RetainerId) ||
                    listing.ItemId != line.ItemId || listing.IsHq != (line.Quality == EquipmentQuality.High) ||
                    listing.Quantity == 0 || listing.UnitPrice == 0 || listing.UnitPrice > line.MaxUnitPriceGil ||
                    listing.TotalGil != checked(listing.Quantity * listing.UnitPrice)))
                throw new InvalidOperationException($"Restored Squire route changed the exact listing, quality, quantity, or gil identity for {line.ItemName}.");
            plannedSquireGil = checked(plannedSquireGil + plannedGil);
        }
        if (plannedSquireGil > contract.SquirePlanCapGil - state.TotalSpentGil)
            throw new InvalidOperationException("Restored Squire route exceeds the exact remaining finalized plan envelope.");
    }

    private static void ValidateReceiptTotals(OutfitterRouteExecutionState state)
    {
        if (state.SunkPurchases.Select(receipt => receipt.ReceiptId).Distinct(StringComparer.Ordinal).Count() != state.SunkPurchases.Count)
            throw new InvalidOperationException("Persisted Squire sunk receipts contain duplicate evidence identities.");
        foreach (var line in state.Lines)
        {
            var receipts = state.SunkPurchases.Where(receipt => receipt.LineId == line.LineId).ToArray();
            if (receipts.Aggregate(0ul, (sum, receipt) => checked(sum + receipt.Quantity)) != line.PurchasedQuantity ||
                receipts.Aggregate(0ul, (sum, receipt) => checked(sum + receipt.TotalGil)) != line.SpentGil)
                throw new InvalidOperationException("Persisted Squire sunk receipts disagree with their line quantity or gil totals.");
        }
        if (state.SunkPurchases.Aggregate(0ul, (sum, receipt) => checked(sum + receipt.TotalGil)) != state.TotalSpentGil)
            throw new InvalidOperationException("Persisted Squire sunk receipts disagree with the route gil total.");
    }

    private static string ReceiptKey(OutfitterRouteSunkPurchase receipt) =>
        ReceiptKey(receipt.WorldName, receipt.LineId, receipt.ListingId, receipt.RetainerId);

    private static string ReceiptKey(string worldName, string lineId, string listingId, string retainerId) =>
        $"{worldName.ToUpperInvariant()}|{lineId}|{listingId}|{retainerId}";

    private sealed record ReceiptDeduction(uint Quantity, uint TotalGil);
}
