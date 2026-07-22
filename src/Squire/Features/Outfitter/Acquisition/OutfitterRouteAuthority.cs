using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;
using Newtonsoft.Json;

namespace MarketMafioso.Squire.Outfitter.Acquisition;

public enum OutfitterRouteAuthorityPhase
{
    Preparing,
    Active,
    RecoveryNeeded,
    Paused,
    Complete,
}

public sealed record OutfitterRouteLineProgress(
    string LineId,
    uint ItemId,
    string ItemName,
    EquipmentQuality Quality,
    uint RequiredQuantity,
    uint PurchasedQuantity,
    uint SpentGil,
    uint MaxUnitPriceGil,
    uint MaxTotalGil);

public sealed record OutfitterRouteExecutionState(
    string SchemaVersion,
    string ContractId,
    string CanonicalIntentHash,
    OutfitterRouteAuthorityPhase Phase,
    IReadOnlyList<OutfitterRouteLineProgress> Lines,
    ulong TotalSpentGil,
    int RecoveryAttempt,
    string? LastRecoveryPlanSignature,
    ulong SpendAtLastRecovery,
    string Message,
    DateTimeOffset UpdatedAtUtc)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-outfitter-route-state/v1";
    public bool NeedsRecovery => Phase == OutfitterRouteAuthorityPhase.RecoveryNeeded;
    public IReadOnlyList<OutfitterRouteSunkPurchase> SunkPurchases { get; init; } = [];
}

public interface IOutfitterRouteExecutionStateStore
{
    OutfitterRouteExecutionState? Restore();
    void Save(OutfitterRouteExecutionState state);
    void Clear();
}

public sealed class ConfigurationOutfitterRouteExecutionStateStore : IOutfitterRouteExecutionStateStore
{
    private readonly ISquireConfigurationStore config;
    private readonly Action save;

    public ConfigurationOutfitterRouteExecutionStateStore(ISquireConfigurationStore config, Action? save = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.save = save ?? config.Save;
    }

    public OutfitterRouteExecutionState? Restore()
    {
        if (string.IsNullOrWhiteSpace(config.OutfitterRouteExecutionStateJson))
            return null;
        try
        {
            var state = JsonConvert.DeserializeObject<OutfitterRouteExecutionState>(config.OutfitterRouteExecutionStateJson);
            return state?.SchemaVersion == OutfitterRouteExecutionState.CurrentSchemaVersion ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(OutfitterRouteExecutionState state)
    {
        config.OutfitterRouteExecutionStateJson = JsonConvert.SerializeObject(state, Formatting.None);
        save();
    }

    public void Clear()
    {
        config.OutfitterRouteExecutionStateJson = null;
        save();
    }
}

public sealed class OutfitterRouteAuthoritySession
{
    private readonly OutfitterExecutionContract contract;
    private readonly IOutfitterRouteExecutionStateStore? store;

    private OutfitterRouteAuthoritySession(
        OutfitterExecutionContract contract,
        OutfitterRouteExecutionState state,
        IOutfitterRouteExecutionStateStore? store)
    {
        this.contract = contract;
        State = state;
        this.store = store;
    }

    public OutfitterRouteExecutionState State { get; private set; }
    public OutfitterExecutionContract Contract => contract;

    public static OutfitterRouteAuthoritySession Consume(
        OutfitterExecutionContract contract,
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionPlan plan,
        MarketAcquisitionClaimView claim,
        IOutfitterRouteExecutionStateStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(claim);
        ValidateContractBinding(contract, document, claim);
        var bindings = BindLines(contract, claim);
        var restored = store?.Restore();
        var state = restored is not null &&
                    restored.ContractId == contract.ContractId &&
                    restored.CanonicalIntentHash == contract.CanonicalIntentHash &&
                    IsRestoredStateSane(restored, bindings, contract.SquirePlanCapGil)
            ? restored with
            {
                Phase = OutfitterRouteAuthorityPhase.Preparing,
                Message = "Reconciled persisted purchases; preparing the remaining exact-quality route.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            }
            : new OutfitterRouteExecutionState(
                OutfitterRouteExecutionState.CurrentSchemaVersion,
                contract.ContractId,
                contract.CanonicalIntentHash,
                OutfitterRouteAuthorityPhase.Preparing,
                bindings,
                0,
                0,
                null,
                0,
                "Contract consumed; validating the exact-quality route before spending.",
                DateTimeOffset.UtcNow);
        try
        {
            ValidatePlan(contract, plan, state.Lines);
        }
        catch (Exception exception)
        {
            store?.Save(state with
            {
                Phase = OutfitterRouteAuthorityPhase.Paused,
                Message = $"Squire preflight stopped before travel or purchase: {exception.Message}",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            throw;
        }
        var session = new OutfitterRouteAuthoritySession(contract, state, store);
        session.Persist();
        return session;
    }

    public void CompletePreflight(MarketAcquisitionPlan plan)
    {
        ValidatePlan(contract, plan, State.Lines);
        SetState(State with
        {
            Phase = OutfitterRouteAuthorityPhase.Active,
            Message = "Preflight complete; immediate visible-row checks remain required before each purchase.",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    public void ValidateCurrentDocument(MarketAcquisitionRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.LocalRequestId != contract.WorkbenchDocumentId ||
            document.LocalRevision != contract.WorkbenchRevision ||
            OutfitterWorkbenchAuthorityService.ComputeCanonicalIntentHash(document) != contract.CanonicalIntentHash)
            throw new InvalidOperationException("Workbench changed after Squire confirmation; return through Advisor and finalize a new contract.");
    }

    internal bool IsSunkListing(MarketBoardLiveListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return State.SunkPurchases.Any(receipt =>
            receipt.ItemId == listing.ItemId &&
            receipt.Quality == (listing.IsHq ? EquipmentQuality.High : EquipmentQuality.Normal) &&
            string.Equals(receipt.WorldName, listing.WorldName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(receipt.ListingId, listing.ListingId, StringComparison.Ordinal) &&
            string.Equals(receipt.RetainerId, listing.RetainerId, StringComparison.Ordinal));
    }

    public OutfitterWorkbenchAuthorityValidation AuthorizeCandidate(
        MarketAcquisitionWorldItemSubtask subtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        ArgumentNullException.ThrowIfNull(subtask);
        ArgumentNullException.ThrowIfNull(candidatePlan);
        if (State.Phase != OutfitterRouteAuthorityPhase.Active)
            return new(false, $"Squire authority is {State.Phase}; purchases are disabled.");
        if (!candidatePlan.IsListingReadFresh)
            return new(false, "Visible market-board evidence is not fresh.");
        if (MarketAcquisitionLiveCandidateStatuses.IsIncompleteListingCoverage(candidatePlan.Status))
            return new(false, "Visible listing coverage is incomplete; Squire paused instead of treating hidden rows as absent.");
        if (candidatePlan.Rows.Any(row => row.Decision == "WouldBuy" && IsSunkListing(row.LiveListing)))
            return new(false, "A selected visible row is already represented by a persisted purchase receipt.");
        var line = State.Lines.SingleOrDefault(value => value.LineId == subtask.LineId);
        if (line is null)
            return OutfitterWorkbenchAuthorityValidation.Valid;
        if (line.ItemId != subtask.ItemId || !PolicyMatches(line.Quality, subtask.HqPolicy))
            return new(false, "The active route row no longer matches the confirmed item and exact quality.");
        var remainingQuantity = line.RequiredQuantity - line.PurchasedQuantity;
        var remainingLineGil = line.MaxTotalGil - line.SpentGil;
        var candidateGil = (ulong)candidatePlan.WouldSpendGil;
        if (candidatePlan.WouldBuyQuantity > remainingQuantity)
            return new(false, "The visible listing would exceed the confirmed remaining quantity.");
        if (candidateGil > remainingLineGil)
            return new(false, "The visible listing would exceed the confirmed line ceiling.");
        if (candidateGil > contract.SquirePlanCapGil - State.TotalSpentGil)
            return new(false, "The visible listing would exceed the confirmed Squire plan ceiling.");
        foreach (var row in candidatePlan.Rows.Where(row => row.Decision == "WouldBuy"))
        {
            if (row.LiveListing.ItemId != line.ItemId || row.LiveListing.IsHq != (line.Quality == EquipmentQuality.High))
                return new(false, "A selected visible row changed item or exact NQ/HQ identity.");
            if (row.LiveListing.UnitPrice > line.MaxUnitPriceGil)
                return new(false, "A selected visible row exceeds its fixed unit ceiling.");
        }
        return OutfitterWorkbenchAuthorityValidation.Valid;
    }

    public void RecordPurchase(
        string lineId,
        MarketBoardPurchaseCandidate candidate,
        MarketAcquisitionPlan? plan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lineId);
        ArgumentNullException.ThrowIfNull(candidate);
        var lines = State.Lines.ToArray();
        var index = Array.FindIndex(lines, line => line.LineId == lineId);
        if (index < 0)
            return;
        var line = lines[index];
        var existingReceipts = State.SunkPurchases ?? [];
        OutfitterRouteSunkPurchase? receipt = null;
        if (plan is not null)
        {
            if (string.IsNullOrWhiteSpace(plan.RequestId) || plan.PreparedAtUtc == default)
                throw new InvalidOperationException("Confirmed purchase plan identity is incomplete.");
            var draft = new OutfitterRouteSunkPurchase
            {
                SchemaVersion = OutfitterRouteSunkPurchase.CurrentSchemaVersion,
                ReceiptId = string.Empty,
                ContractId = contract.ContractId,
                CanonicalIntentHash = contract.CanonicalIntentHash,
                WorkbenchDocumentId = contract.WorkbenchDocumentId,
                WorkbenchRevision = contract.WorkbenchRevision,
                PlanRequestId = plan.RequestId,
                PlanPreparedAtUtc = plan.PreparedAtUtc,
                WorldName = candidate.WorldName,
                LineId = lineId,
                ItemId = candidate.ItemId,
                Quality = candidate.IsHq ? EquipmentQuality.High : EquipmentQuality.Normal,
                ListingId = candidate.ListingId,
                RetainerId = candidate.RetainerId,
                Quantity = candidate.Quantity,
                UnitPriceGil = candidate.UnitPrice,
                TotalGil = candidate.TotalGil,
            };
            receipt = draft with { ReceiptId = OutfitterDryRunExecutionStateRestorer.ComputeReceiptId(draft) };
            if (existingReceipts.Any(value => value.ReceiptId == receipt.ReceiptId))
                return;
            if (existingReceipts.Any(value =>
                    value.WorldName.Equals(receipt.WorldName, StringComparison.OrdinalIgnoreCase) &&
                    value.ListingId == receipt.ListingId && value.RetainerId == receipt.RetainerId))
                throw new InvalidOperationException("Confirmed listing already has a different persisted Squire receipt.");
        }
        if (candidate.ItemId != line.ItemId || candidate.IsHq != (line.Quality == EquipmentQuality.High) ||
            candidate.UnitPrice > line.MaxUnitPriceGil ||
            candidate.Quantity > line.RequiredQuantity - line.PurchasedQuantity ||
            candidate.TotalGil > line.MaxTotalGil - line.SpentGil ||
            candidate.TotalGil > contract.SquirePlanCapGil - State.TotalSpentGil)
        {
            throw new InvalidOperationException("Confirmed purchase is outside the finalized Squire execution contract.");
        }
        lines[index] = line with
        {
            PurchasedQuantity = checked(line.PurchasedQuantity + candidate.Quantity),
            SpentGil = checked(line.SpentGil + candidate.TotalGil),
        };
        SetState(State with
        {
            Lines = lines,
            TotalSpentGil = checked(State.TotalSpentGil + candidate.TotalGil),
            Message = $"Verified purchase recorded for {line.ItemName}; remaining need and cap reconciled.",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            SunkPurchases = receipt is null ? existingReceipts : [.. existingReceipts, receipt],
        });
    }

    public void EvaluateRouteEnd(MarketAcquisitionPlan completedPlan)
    {
        if (State.Phase != OutfitterRouteAuthorityPhase.Active)
            return;
        if (State.Lines.All(line => line.PurchasedQuantity >= line.RequiredQuantity))
        {
            SetState(State with
            {
                Phase = OutfitterRouteAuthorityPhase.Complete,
                Message = "The finalized exact-quality Squire solution is fully acquired.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            return;
        }

        var signature = ComputePlanSignature(completedPlan);
        var repeatedWithoutProgress = State.LastRecoveryPlanSignature == signature &&
                                      State.SpendAtLastRecovery == State.TotalSpentGil;
        SetState(State with
        {
            Phase = repeatedWithoutProgress ? OutfitterRouteAuthorityPhase.Paused : OutfitterRouteAuthorityPhase.RecoveryNeeded,
            RecoveryAttempt = State.RecoveryAttempt + 1,
            LastRecoveryPlanSignature = signature,
            SpendAtLastRecovery = State.TotalSpentGil,
            Message = repeatedWithoutProgress
                ? "Recovery produced the same exhausted route without progress; review or return to Advisor."
                : "The planned rows were exhausted; refreshing and optimizing the complete remaining exact-quality route.",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    public MarketAcquisitionClaimView CreateRecoveryClaim(MarketAcquisitionClaimView claim)
        => CreateRecoveryClaim(claim, State);

    public static MarketAcquisitionClaimView CreateRecoveryClaim(
        MarketAcquisitionClaimView claim,
        OutfitterRouteExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(state);
        var remaining = state.Lines
            .Where(line => line.PurchasedQuantity < line.RequiredQuantity)
            .Select(line => claim.Lines.Single(source => source.LineId == line.LineId) with
            {
                QuantityMode = "TargetQuantity",
                TargetQuantity = line.RequiredQuantity - line.PurchasedQuantity,
                MaxQuantity = 0,
                MaxUnitPrice = line.MaxUnitPriceGil,
                GilCap = line.MaxTotalGil - line.SpentGil,
            })
            .ToList();
        return claim with { Lines = remaining };
    }

    public void BeginRecovery(MarketAcquisitionPlan plan)
    {
        ValidatePlan(contract, plan, State.Lines, remainingOnly: true);
        SetState(State with
        {
            Phase = OutfitterRouteAuthorityPhase.Active,
            Message = "Recovered remaining route passed preflight; visible rows will be revalidated before purchase.",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    public void Pause(string message) => SetState(State with
    {
        Phase = OutfitterRouteAuthorityPhase.Paused,
        Message = message,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    });

    public void RequestRecovery(string? message = null) => SetState(State with
    {
        Phase = OutfitterRouteAuthorityPhase.RecoveryNeeded,
        Message = message ?? "Refreshing the complete remaining exact-quality route.",
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    });

    private void SetState(OutfitterRouteExecutionState state)
    {
        State = state;
        Persist();
    }

    private void Persist() => store?.Save(State);

    private static void ValidateContractBinding(
        OutfitterExecutionContract contract,
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionClaimView claim)
    {
        if (contract.SchemaVersion != OutfitterExecutionContract.CurrentSchemaVersion ||
            contract.RecoveryPolicyId != OutfitterWorkbenchAuthority.CrossWorldExactQualityV1)
            throw new InvalidOperationException("Route cannot consume an unknown Squire contract or recovery policy version.");
        if (document.LocalRequestId != contract.WorkbenchDocumentId ||
            document.LocalRevision != contract.WorkbenchRevision ||
            OutfitterWorkbenchAuthorityService.ComputeCanonicalIntentHash(document) != contract.CanonicalIntentHash)
            throw new InvalidOperationException("The Workbench no longer matches the finalized Squire contract.");
        if (!string.Equals(claim.TargetCharacterName, contract.TargetCharacterName, StringComparison.Ordinal) ||
            !string.Equals(claim.TargetWorld, contract.TargetWorld, StringComparison.Ordinal))
            throw new InvalidOperationException("The accepted work order targets a different character or world than the Squire contract.");
    }

    private static IReadOnlyList<OutfitterRouteLineProgress> BindLines(
        OutfitterExecutionContract contract,
        MarketAcquisitionClaimView claim) => contract.Lines.Select(envelope =>
    {
        var line = claim.Lines.SingleOrDefault(value =>
            value.ItemId == envelope.ItemId && PolicyMatches(envelope.Quality, value.HqPolicy)) ??
            throw new InvalidOperationException($"Accepted work order is missing {envelope.ItemName} exact-quality lineage.");
        return new OutfitterRouteLineProgress(
            line.LineId,
            envelope.ItemId,
            envelope.ItemName,
            envelope.Quality,
            envelope.RequiredQuantity,
            0,
            0,
            envelope.MaxUnitPriceGil,
            envelope.MaxTotalGil);
    }).ToArray();

    private static void ValidatePlan(
        OutfitterExecutionContract contract,
        MarketAcquisitionPlan plan,
        IReadOnlyList<OutfitterRouteLineProgress> lines,
        bool remainingOnly = false)
    {
        if (plan.Status != "Ready")
            throw new InvalidOperationException("Squire preflight requires a ready route plan.");
        if (plan.WorldBatches.Count == 0)
            throw new InvalidOperationException("Squire preflight requires at least one executable world batch.");
        var allowedWorlds = ResolveAllowedWorlds(contract);
        if (plan.WorldBatches.Any(batch => !allowedWorlds.Contains(batch.WorldName)))
            throw new InvalidOperationException("The route contains a world outside the visibly confirmed scope.");
        ulong plannedSquireGil = 0;
        foreach (var line in lines)
        {
            var planned = plan.Lines.SingleOrDefault(value => value.LineId == line.LineId);
            if (remainingOnly && line.PurchasedQuantity >= line.RequiredQuantity)
                continue;
            if (planned is null || planned.ItemId != line.ItemId || !PolicyMatches(line.Quality, planned.HqPolicy) ||
                planned.MaxUnitPrice > line.MaxUnitPriceGil || planned.GilCap > line.MaxTotalGil - line.SpentGil)
                throw new InvalidOperationException($"Prepared route does not preserve {line.ItemName} exact identity and remaining caps.");
            if (planned.PlannedQuantity > line.RequiredQuantity - line.PurchasedQuantity)
                throw new InvalidOperationException($"Prepared route would exceed the remaining required quantity for {line.ItemName}.");
            plannedSquireGil = checked(plannedSquireGil + planned.PlannedGil);
        }
        if (plannedSquireGil > contract.SquirePlanCapGil - lines.Aggregate(0ul, (sum, line) => checked(sum + line.SpentGil)))
            throw new InvalidOperationException("Prepared route exceeds the remaining confirmed Squire plan ceiling.");
    }

    private static HashSet<string> ResolveAllowedWorlds(OutfitterExecutionContract contract)
    {
        if (contract.AuthorizedWorlds.Count == 0)
            throw new InvalidOperationException("The finalized Squire contract contains no authorized worlds.");
        var regionalWorlds = MarketAcquisitionWorldCatalog.ResolveDataCenters(contract.Region)
            .Values
            .SelectMany(value => value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (contract.AuthorizedWorlds.Any(world => !regionalWorlds.Contains(world)))
            throw new InvalidOperationException("The finalized Squire contract contains a world outside its region.");
        return contract.AuthorizedWorlds.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsRestoredStateSane(
        OutfitterRouteExecutionState state,
        IReadOnlyList<OutfitterRouteLineProgress> bindings,
        ulong planCap)
    {
        if (state.SchemaVersion != OutfitterRouteExecutionState.CurrentSchemaVersion ||
            state.TotalSpentGil > planCap || state.Lines.Count != bindings.Count ||
            state.Lines.Select(line => line.LineId).Distinct(StringComparer.Ordinal).Count() != state.Lines.Count ||
            state.TotalSpentGil != state.Lines.Aggregate(0ul, (sum, line) => checked(sum + line.SpentGil)))
            return false;
        return state.Lines.All(line => bindings.Any(binding =>
            binding.LineId == line.LineId && binding.ItemId == line.ItemId && binding.Quality == line.Quality &&
            line.RequiredQuantity == binding.RequiredQuantity && line.MaxUnitPriceGil == binding.MaxUnitPriceGil &&
            line.MaxTotalGil == binding.MaxTotalGil && line.PurchasedQuantity <= binding.RequiredQuantity &&
            line.SpentGil <= binding.MaxTotalGil));
    }

    internal static bool IsRestoredStateSane(
        OutfitterRouteExecutionState state,
        OutfitterExecutionContract contract,
        MarketAcquisitionClaimView claim) =>
        state.ContractId == contract.ContractId &&
        state.CanonicalIntentHash == contract.CanonicalIntentHash &&
        IsRestoredStateSane(state, BindLines(contract, claim), contract.SquirePlanCapGil);

    private static bool PolicyMatches(EquipmentQuality quality, string policy) =>
        MarketAcquisitionPolicy.NormalizeHqPolicy(policy) ==
        (quality == EquipmentQuality.High ? "HqOnly" : "NqOnly");

    private static string ComputePlanSignature(MarketAcquisitionPlan plan)
    {
        var value = string.Join("|", plan.WorldBatches.SelectMany(batch =>
            batch.ItemSubtasks.SelectMany(subtask => subtask.Listings.Select(listing =>
                $"{subtask.LineId}:{batch.WorldName}:{listing.ListingId}:{listing.Quantity}:{listing.UnitPrice}:{listing.IsHq}"))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
