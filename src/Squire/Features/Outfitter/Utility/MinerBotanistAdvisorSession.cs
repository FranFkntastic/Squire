using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Persistence;

namespace MarketMafioso.Squire.Outfitter.Utility;

public enum MinerBotanistAdvisorSessionStage
{
    Idle,
    CapturingPlayer,
    DiscoveringMarket,
    Complete,
    Abstained,
    Failed,
    Cancelled,
}

public sealed record MinerBotanistAdvisorSessionState(
    MinerBotanistAdvisorSessionStage Stage,
    string Message,
    string CoverageLabel,
    int Completed,
    int? Total,
    AdvisorUtilityContextDescriptor Context,
    MinerBotanistReadOnlyAdvice? Advice,
    bool AdviceIsRetained,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsBusy => Stage is MinerBotanistAdvisorSessionStage.CapturingPlayer or
        MinerBotanistAdvisorSessionStage.DiscoveringMarket;
}

internal static class MinerBotanistAdvisorSessionEvidencePolicy
{
    public static OutfitterMarketEvidenceBook? SelectCurrent(
        OutfitterMarketDiscoveryResult result,
        OutfitterMarketEvidenceRequest request) =>
        result.WorkingBook.IsPublishable &&
        result.PublishedBook is { } published && published.Matches(request)
            ? published
            : null;
}

/// <summary>
/// Framework-ticked orchestration for the player advisor. One windowless player baseline is
/// captured from PlayerState and equipped inventory on the framework thread; immutable market
/// discovery and exact solving proceed from that frozen generation.
/// </summary>
public sealed class MinerBotanistAdvisorSession : IDisposable
{
    private readonly IPlayerAdvisorBaselineSource baselineSource;
    private readonly MinerBotanistAdvisorCatalog catalog;
    private readonly OutfitterMarketEvidenceDiscoveryService marketDiscovery;
    private readonly OutfitterMarketEvidenceFileStore marketEvidenceStore;
    private readonly OutfitterPersistedAnalysisStore persistedAnalysisStore;
    private readonly OutfitterAdvisorCraftDiscovery? craftDiscovery;
#if DEBUG
    private readonly string solverReplayPath;
#endif
    private readonly MinerBotanistReadOnlyAdvisor advisor = new();
    private readonly GenerationBoundComputation<MinerBotanistReadOnlyAdvice> solving = new();
    private CancellationTokenSource? cancellation;
    private Task<OutfitterMarketDiscoveryResult>? discoveryTask;
    private OutfitterMarketEvidenceRequest? discoveryRequest;
    private PlayerAdvisorBaseline? baseline;
    private IAdvisorStatFamily? resolvedFamily;
    private MinerBotanistAdvisorCatalogResult? offers;
    private IReadOnlyList<MinerBotanistOwnedItemEvidence>? ownedItemsEvidence;
    private bool ownedInventoryCoverageComplete;
    private OutfitterMarketEvidenceBook? pendingCurrentEvidence;
    private OutfitterMarketEvidenceBook? pendingSolvingEvidence;
    private OutfitterAdvisorCraftDiscoveryOperation? craftDiscoveryOperation;
    private OutfitterAdvisorCraftDiscoveryResult? pendingCraftPreparation;
    private CraftProgressSnapshot? craftProgress;
    private string? pendingCraftDiagnostic;
    private PlayerAdvisorAuthorityFingerprint? advicePlayerFingerprint;
    private WorkbenchValidationRequest? workbenchValidationRequest;
    private OutfitterWorkbenchPlayerValidation? completedWorkbenchValidation;
    private IReadOnlyList<EquipmentInstanceFingerprint> completedValidationOwnedInstances = [];
    private SolverProgressSnapshot? solverProgress;
    private long sessionGeneration;
    private DateTimeOffset solvingStartedAtUtc;
    private string requestedContextId = GathererAdvisorStatFamily.OrdinaryResourceContext.Id;
    private OutfitterTarget? requestedTarget;
    private string? adviceTargetKey;

    public MinerBotanistAdvisorSession(
        IPlayerAdvisorBaselineSource baselineSource,
        IDataManager dataManager,
        IMarketAcquisitionListingSource listingSource,
        string evidencePath)
        : this(baselineSource, dataManager, listingSource, evidencePath, null)
    {
    }

    internal MinerBotanistAdvisorSession(
        IPlayerAdvisorBaselineSource baselineSource,
        IDataManager dataManager,
        IMarketAcquisitionListingSource listingSource,
        string evidencePath,
        OutfitterAdvisorCraftDiscovery? craftDiscovery)
    {
        this.baselineSource = baselineSource ?? throw new ArgumentNullException(nameof(baselineSource));
        ArgumentNullException.ThrowIfNull(dataManager);
        ArgumentNullException.ThrowIfNull(listingSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);
        this.craftDiscovery = craftDiscovery;
        catalog = new(dataManager);
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
#if DEBUG
        solverReplayPath = Path.Combine(Path.GetDirectoryName(evidencePath)!, "outfitter-solver-replay.json");
#endif
        marketEvidenceStore = new(evidencePath);
        persistedAnalysisStore = new(Path.Combine(Path.GetDirectoryName(evidencePath)!, "outfitter-analyses.json"));
        marketDiscovery = new(
            listingSource,
            new(TimeSpan.FromMinutes(15), TimeSpan.FromHours(6), maxEntries: 4096),
            marketEvidenceStore);
        State = Idle(GathererAdvisorStatFamily.Instance.ProfileDescriptor.DefaultContext);
    }

    public MinerBotanistAdvisorSessionState State { get; private set; }

    public OutfitterMarketEvidenceBook? CurrentEvidence { get; private set; }

    public string Region { get; private set; } = "North America";

    internal Task<OutfitterPersistedAnalysisBook> LoadPersistedAnalysesAsync(CancellationToken cancellationToken = default) =>
        persistedAnalysisStore.LoadAsync(cancellationToken);

    internal Task<OutfitterMarketEvidenceBook?> LoadPersistedMarketEvidenceAsync(CancellationToken cancellationToken = default) =>
        marketEvidenceStore.LoadAsync(cancellationToken);

    internal Task<OutfitterPersistedAnalysis> PersistCurrentAnalysisAsync(
        string? selectedSolutionId = null,
        Guid? existingAnalysisId = null,
        CancellationToken cancellationToken = default)
    {
        if (State is not
            {
                Stage: MinerBotanistAdvisorSessionStage.Complete,
                AdviceIsRetained: false,
                Advice: { } advice,
            } || baseline is not
            {
                Status: PlayerAdvisorBaselineStatus.Complete,
                Target: { Kind: PlayerAdvisorBaselineTargetKind.SavedGearset, SavedGearset: { } target },
            } || resolvedFamily is null || CurrentEvidence is null)
        {
            throw new InvalidOperationException("Current complete saved-gearset advice is required before persistence.");
        }

        selectedSolutionId ??= advice.Nomination?.Candidate.SolutionId;
        OutfitterPersistedCraftHandoff? craftHandoff = null;
        if (selectedSolutionId is not null && advice.Frontier?.Pareto.Frontier.SingleOrDefault(value =>
                string.Equals(value.Candidate.SolutionId, selectedSolutionId, StringComparison.Ordinal)) is { } selected)
        {
            var includesCraft = selected.Candidate.Selections.Any(selection =>
                advice.OffersByAllocation.TryGetValue(selection.AllocationKey, out var offer) &&
                offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.Craft);
            if (includesCraft)
            {
                var projection = OutfitterCraftHandoffProjection.Build(
                    advice,
                    selectedSolutionId,
                    baseline,
                    CurrentEvidence,
                    DateTimeOffset.UtcNow);
                craftHandoff = OutfitterPersistedCraftHandoff.Create(projection, DateTimeOffset.UtcNow);
            }
        }

        var analysis = OutfitterPersistedAnalysis.Create(
            target,
            baseline,
            resolvedFamily,
            State.Context,
            CurrentEvidence,
            advice,
            selectedSolutionId,
            craftHandoff);
        if (existingAnalysisId is { } analysisId)
            analysis = analysis with { AnalysisId = analysisId };
        return persistedAnalysisStore.UpsertAsync(analysis, cancellationToken);
    }

    internal OutfitterPersistedAnalysisRevalidation RevalidatePersistedAnalysis(
        OutfitterPersistedAnalysis analysis,
        OutfitterMarketEvidenceBook? currentEvidence)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var target = RehydrateTarget(analysis.Target);
        var currentBaseline = baselineSource is IOutfitterTargetAdvisorBaselineSource targetSource
            ? targetSource.Capture(target)
            : PlayerAdvisorBaselineAssembler.Failure(
                PlayerAdvisorBaselineStatus.Unsupported,
                "This Advisor baseline source cannot revalidate saved-gearset targets.");
        var family = AdvisorStatFamilies.Resolve(analysis.Target.ClassJobId);
        var context = family?.ResolveContext(analysis.Profile.ContextId);
        return OutfitterPersistedAnalysisValidation.Revalidate(
            analysis,
            currentBaseline,
            family,
            context,
            currentEvidence,
            State.Advice);
    }

    internal bool TryGetCraftHandoffPresentation(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        OutfitterMarketEvidenceBook evidence,
        out OutfitterCraftHandoffProjection projection)
    {
        projection = null!;
        if (!ReferenceEquals(advice, State.Advice) || !ReferenceEquals(evidence, CurrentEvidence) || baseline is null)
            return false;
        try
        {
            var reviewAt = advice.CraftOffersByAllocation.Values
                .Select(craft => craft.Source.Plan.BuiltAtUtc)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();
            if (reviewAt == DateTimeOffset.MinValue)
                return false;
            projection = OutfitterCraftHandoffProjection.Build(
                advice,
                selectedSolutionId,
                baseline,
                evidence,
                reviewAt);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    internal bool TryBuildCraftHandoff(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        OutfitterMarketEvidenceBook evidence,
        out OutfitterCraftHandoffProjection projection,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(advice);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedSolutionId);
        ArgumentNullException.ThrowIfNull(evidence);
        projection = null!;
        if (State.Stage != MinerBotanistAdvisorSessionStage.Complete || State.AdviceIsRetained ||
            !ReferenceEquals(advice, State.Advice) || !ReferenceEquals(evidence, CurrentEvidence) ||
            advicePlayerFingerprint is not { } capturedPlayer)
        {
            diagnostic = "Craft handoff requires current, non-retained Advisor evidence.";
            return false;
        }

        try
        {
            var currentBaseline = CaptureRequestedBaseline();
            if (currentBaseline.Status != PlayerAdvisorBaselineStatus.Complete ||
                PlayerAdvisorAuthorityFingerprint.Capture(currentBaseline) != capturedPlayer)
            {
                diagnostic = "Player job, equipment, materia, or relevant stats changed; refresh Advisor before craft handoff.";
                return false;
            }
            projection = OutfitterCraftHandoffProjection.Build(
                advice,
                selectedSolutionId,
                currentBaseline,
                evidence,
                DateTimeOffset.UtcNow);
            diagnostic = "Craft handoff reviewed against current player and market evidence.";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            diagnostic = $"Craft handoff stopped safely: {exception.Message}";
            return false;
        }
    }

    public void Begin(AdvisorUtilityContextDescriptor context, string region)
    {
        BeginCore(null, context, region);
    }

    public void Begin(OutfitterTarget target, AdvisorUtilityContextDescriptor context, string region)
    {
        ArgumentNullException.ThrowIfNull(target);
        BeginCore(target, context, region);
    }

    private void BeginCore(OutfitterTarget? target, AdvisorUtilityContextDescriptor context, string region)
    {
        ArgumentNullException.ThrowIfNull(context);
        CancelCore(MinerBotanistAdvisorSessionStage.Cancelled, "Superseded by a new advisor refresh.");
        sessionGeneration++;
        var targetKey = target?.Key ?? "active-loadout";
        var retainedAdvice = string.Equals(requestedContextId, context.Id, StringComparison.Ordinal) &&
            string.Equals(adviceTargetKey, targetKey, StringComparison.Ordinal) &&
            State.Advice is { Frontier: not null }
            ? State.Advice
            : null;
        requestedTarget = target;
        requestedContextId = context.Id;
        if (retainedAdvice is null)
            CurrentEvidence = null;
        var retainedCoverage = retainedAdvice is null
            ? "Coverage is declared after the active job is captured."
            : State.CoverageLabel;
        cancellation = new();
        ResetPendingCapture();
        State = new(
            MinerBotanistAdvisorSessionStage.CapturingPlayer,
            "Capturing current player stats, equipped items, quality, and materia on the next framework tick.",
            retainedCoverage,
            0,
            null,
            context,
            retainedAdvice,
            retainedAdvice is not null,
            DateTimeOffset.UtcNow);
        Region = string.IsNullOrWhiteSpace(region) ? "North America" : region.Trim();
    }

    public void Tick()
    {
        if (!State.IsBusy)
            return;
        try
        {
            switch (State.Stage)
            {
                case MinerBotanistAdvisorSessionStage.CapturingPlayer:
                    TickPlayerCapture();
                    break;
                case MinerBotanistAdvisorSessionStage.DiscoveringMarket:
                    TickMarket();
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            CancelCore(MinerBotanistAdvisorSessionStage.Cancelled, "Advisor refresh was cancelled.");
        }
        catch (Exception exception)
        {
            CancelCore(MinerBotanistAdvisorSessionStage.Failed, $"Advisor refresh failed safely: {exception.Message}");
        }
    }

    public void Cancel() => CancelCore(MinerBotanistAdvisorSessionStage.Cancelled, "Advisor refresh was cancelled.");

    public void InvalidateForPlayerStateChange()
    {
        cancellation?.Cancel();
        sessionGeneration++;
        solving.Invalidate();
        InvalidateCraftDiscovery();
        pendingCurrentEvidence = null;
        pendingSolvingEvidence = null;
        pendingCraftDiagnostic = null;
        advicePlayerFingerprint = null;
        adviceTargetKey = null;
        workbenchValidationRequest = null;
        completedWorkbenchValidation = null;
        completedValidationOwnedInstances = [];
        Volatile.Write(ref solverProgress, null);
        CurrentEvidence = null;
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.Cancelled,
            Message = "The player job or equipped inventory changed; refresh before using Advisor evidence.",
            Advice = null,
            AdviceIsRetained = false,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DisposeCancellation();
    }

    internal bool RequestWorkbenchValidation(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        OutfitterMarketEvidenceBook evidence)
    {
        ArgumentNullException.ThrowIfNull(advice);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedSolutionId);
        ArgumentNullException.ThrowIfNull(evidence);
        if (State.Stage != MinerBotanistAdvisorSessionStage.Complete || State.AdviceIsRetained ||
            !ReferenceEquals(advice, State.Advice) || !ReferenceEquals(evidence, CurrentEvidence) ||
            advicePlayerFingerprint is not { } capturedPlayer)
        {
            return false;
        }

        var selected = advice.Frontier!.Pareto.Frontier.SingleOrDefault(value =>
            string.Equals(value.Candidate.SolutionId, selectedSolutionId, StringComparison.Ordinal));
        if (selected is null)
            return false;
        var requiredOwnedInstances = selected.Candidate.Selections
            .Select(selection => advice.OffersByAllocation.TryGetValue(selection.AllocationKey, out var offer) ? offer.Offer.Instance : null)
            .Where(instance => instance is not null && !instance.IsEquipped)
            .Select(instance => instance!.Fingerprint)
            .Distinct(EquipmentInstanceFingerprintComparer.Instance)
            .ToArray();
        workbenchValidationRequest = new(advice, selectedSolutionId, evidence, capturedPlayer, requiredOwnedInstances);
        completedWorkbenchValidation = null;
        completedValidationOwnedInstances = [];
        cancellation = new();
        baseline = null;
        resolvedFamily = null;
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.CapturingPlayer,
            Message = "Revalidating the current player baseline before Workbench review.",
            Completed = 0,
            Total = null,
            AdviceIsRetained = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        return true;
    }

    internal bool TryTakeWorkbenchValidation(out OutfitterWorkbenchPlayerValidation validation)
    {
        if (completedWorkbenchValidation is null)
        {
            validation = null!;
            return false;
        }

        var latest = CaptureRequestedBaseline();
        var latestInstances = new HashSet<EquipmentInstanceFingerprint>(
            latest.EquipmentSnapshot?.Instances.Select(value => value.Fingerprint) ?? [],
            EquipmentInstanceFingerprintComparer.Instance);
        if (latest.Status != PlayerAdvisorBaselineStatus.Complete ||
            PlayerAdvisorAuthorityFingerprint.Capture(latest) != completedWorkbenchValidation.RecapturedPlayer ||
            completedValidationOwnedInstances.Any(value => !latestInstances.Contains(value)))
        {
            completedWorkbenchValidation = null;
            completedValidationOwnedInstances = [];
            CurrentEvidence = null;
            advicePlayerFingerprint = null;
            State = State with
            {
                Stage = MinerBotanistAdvisorSessionStage.Abstained,
                Message = "The player baseline changed after Workbench validation completed; refresh before staging the solution.",
                AdviceIsRetained = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            validation = null!;
            return false;
        }

        validation = completedWorkbenchValidation with { RecapturedBaseline = latest };
        completedWorkbenchValidation = null;
        completedValidationOwnedInstances = [];
        return true;
    }

    private void TickPlayerCapture()
    {
        baseline = CaptureRequestedBaseline();
        State = State with { Message = baseline.Diagnostic, UpdatedAtUtc = DateTimeOffset.UtcNow };
        if (baseline.Status != PlayerAdvisorBaselineStatus.Complete || baseline.ClassJobId is not { } classJobId ||
            baseline.Level is not { } level)
        {
            Abstain(baseline.Diagnostic);
            return;
        }

        resolvedFamily = AdvisorStatFamilies.Resolve(classJobId);
        if (resolvedFamily is null)
        {
            Abstain(AdvisorStatFamilies.UnsupportedDiagnostic(classJobId));
            return;
        }
        State = State with { Context = resolvedFamily.ResolveContext(requestedContextId) };

        if (workbenchValidationRequest is { } validationRequest)
        {
            CompleteWorkbenchValidation(validationRequest);
            return;
        }

        offers = catalog.Build(classJobId, checked((uint)level), resolvedFamily);
        if (offers.MarketItemIds.Count == 0)
        {
            Abstain($"The declared market scope contains no eligible items in this game-data version. {offers.Diagnostic}");
            return;
        }

        ownedInventoryCoverageComplete = ComponentIsComplete(baseline, "armoury") && ComponentIsComplete(baseline, "inventory");
        ownedItemsEvidence = CaptureOwnedItems(baseline);
        var ownershipCoverage = ownedInventoryCoverageComplete
            ? "owned armoury, bag, and saddlebag inventory observed via direct container reads (unmelded items use exact NQ/HQ definitions; relevant melded items block paid nomination)"
            : "owned inventory coverage is partial; observed unmelded items use exact NQ/HQ definitions, but paid nomination is disabled";
        var coverageLabel = offers.CoverageLabel.Replace(
            "owned inventory is not yet observed",
            ownershipCoverage,
            StringComparison.Ordinal);
        discoveryRequest = new(
            "universalis",
            Region,
            offers.MarketItemIds,
            ListingLimit: 100,
            CoverageMode: OutfitterMarketCoverageMode.ExhaustiveWithinScope,
            MaxConcurrency: 4);
        discoveryTask = marketDiscovery.DiscoverAsync(discoveryRequest, cancellation!.Token);
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.DiscoveringMarket,
            Message = $"Player baseline captured in one framework tick; discovering exact NQ/HQ listings for {offers.MarketItemIds.Count:N0} scoped items.",
            CoverageLabel = coverageLabel,
            Completed = 0,
            Total = offers.MarketItemIds.Count,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private void TickMarket()
    {
        if (craftDiscoveryOperation is not null)
        {
            TickCraftDiscovery();
            return;
        }
        if (solving.IsActive)
        {
            TickSolving();
            return;
        }
        if (discoveryRequest is not null && marketDiscovery.GetLiveState(discoveryRequest) is { } live)
        {
            var retainedAdvice = State.Advice;
            State = State with
            {
                Message = live.Progress.Message,
                Completed = live.Progress.Completed,
                Total = live.Progress.Total,
                Advice = retainedAdvice is { Frontier: not null } ? retainedAdvice : State.Advice,
                AdviceIsRetained = retainedAdvice is { Frontier: not null },
                UpdatedAtUtc = live.Progress.UpdatedAtUtc,
            };
        }
        if (discoveryTask is not { IsCompleted: true })
            return;

        var result = discoveryTask.GetAwaiter().GetResult();
        var evidence = result.WorkingBook;
        var currentEvidence = MinerBotanistAdvisorSessionEvidencePolicy.SelectCurrent(result, discoveryRequest!);
        discoveryTask = null;
        discoveryRequest = null;
        if (pendingCraftPreparation is { } preparation)
        {
            if (currentEvidence is null)
            {
                pendingCraftPreparation = null;
                StartSolving(
                    [],
                    "Craft coverage: terminal-material evidence did not publish completely; ordinary advice uses the prior equipment evidence generation.");
                return;
            }

            var revalidatedBaseline = CaptureRequestedBaseline();
            if (revalidatedBaseline.Status != PlayerAdvisorBaselineStatus.Complete ||
                advicePlayerFingerprint is not { } capturedPlayer ||
                PlayerAdvisorAuthorityFingerprint.Capture(revalidatedBaseline) != capturedPlayer)
            {
                CancelCore(
                    MinerBotanistAdvisorSessionStage.Cancelled,
                    "The player baseline changed while terminal-material evidence was publishing; refresh before using Advisor evidence.");
                return;
            }

            baseline = revalidatedBaseline;
            pendingCurrentEvidence = currentEvidence;
            pendingSolvingEvidence = currentEvidence;
            StartCraftFinalization(preparation, currentEvidence);
            return;
        }

        var solvingEvidence = currentEvidence ?? evidence;
        var capturedBaseline = baseline!;
        var capturedOffers = offers!;
        var capturedFamily = resolvedFamily!;
        advicePlayerFingerprint = PlayerAdvisorAuthorityFingerprint.Capture(capturedBaseline);
        pendingCurrentEvidence = currentEvidence;
        pendingSolvingEvidence = solvingEvidence;

        if (craftDiscovery is not null && currentEvidence is not null)
        {
            var craftGeneration = sessionGeneration;
            Volatile.Write(ref craftProgress, null);
            craftDiscoveryOperation = craftDiscovery.StartPreparation(
                craftGeneration,
                capturedBaseline,
                capturedOffers,
                capturedFamily,
                progress => Volatile.Write(ref craftProgress, new(craftGeneration, progress)),
                cancellation!.Token);
            State = State with
            {
                Message = $"Market evidence is published; resolving exact NQ craft graphs for {craftDiscoveryOperation.RequestedCandidateCount:N0}/{craftDiscoveryOperation.EligibleCandidateCount:N0} eligible catalog items off the framework tick. Cancel remains available.",
                Completed = 0,
                Total = craftDiscoveryOperation.RequestedCandidateCount,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return;
        }

        StartSolving(
            [],
            craftDiscovery is null
                ? "Craft coverage: process-local provider unavailable; ordinary advice remains complete."
                : "Craft coverage: no current published market evidence; no craft claim was admitted.");
    }

    private void TickCraftDiscovery()
    {
        var operation = craftDiscoveryOperation!;
        var poll = operation.Poll(sessionGeneration);
        if (poll.Status == OutfitterAdvisorCraftDiscoveryPollStatus.Pending)
        {
            var progress = Volatile.Read(ref craftProgress);
            State = State with
            {
                Message = progress is { Generation: var generation, Progress: var value } && generation == sessionGeneration
                    ? pendingCraftPreparation is null
                        ? $"Resolving exact NQ craft graphs {value.Completed:N0}/{value.Total:N0}: {value.UnavailableCount:N0} unavailable. Cancel remains available."
                        : $"Constructing passive NQ craft offers {value.Completed:N0}/{value.Total:N0}: {value.OfferReadyCount:N0} ready, {value.DisplayOnlyCount:N0} display-only, {value.UnavailableCount:N0} unavailable. Cancel remains available."
                    : $"Preparing bounded passive craft evaluation for {operation.RequestedCandidateCount:N0} catalog items off the framework tick. Cancel remains available.",
                Completed = progress?.Progress.Completed ?? 0,
                Total = operation.RequestedCandidateCount,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return;
        }

        craftDiscoveryOperation = null;
        Volatile.Write(ref craftProgress, null);
        switch (poll.Status)
        {
            case OutfitterAdvisorCraftDiscoveryPollStatus.Stale:
                pendingSolvingEvidence = null;
                pendingCurrentEvidence = null;
                return;
            case OutfitterAdvisorCraftDiscoveryPollStatus.Cancelled:
                CancelCore(MinerBotanistAdvisorSessionStage.Cancelled, "Passive craft evaluation was cancelled.");
                return;
            case OutfitterAdvisorCraftDiscoveryPollStatus.Faulted:
                StartSolving(
                    [],
                    $"Craft coverage: provider phase failed safely ({poll.Exception?.Message ?? "unknown failure"}); ordinary advice remains available.");
                return;
            case OutfitterAdvisorCraftDiscoveryPollStatus.Completed:
                if (pendingCraftPreparation is null)
                    CompleteCraftPreparation(poll.Result!);
                else
                {
                    pendingCraftPreparation = null;
                    StartSolving(poll.Result!.Offers, poll.Result.Diagnostic);
                }
                return;
        }
    }

    private void CompleteCraftPreparation(OutfitterAdvisorCraftDiscoveryResult preparation)
    {
        pendingCraftPreparation = preparation;
        if (preparation.Preparations.Count == 0)
        {
            pendingCraftPreparation = null;
            StartSolving([], preparation.Diagnostic);
            return;
        }

        if (preparation.RequiredMaterialItemIds.Count == 0)
        {
            StartCraftFinalization(preparation, pendingCurrentEvidence!);
            return;
        }

        var itemIds = offers!.MarketItemIds
            .Concat(preparation.RequiredMaterialItemIds)
            .Where(itemId => itemId != 0)
            .Distinct()
            .Order()
            .ToArray();
        discoveryRequest = new(
            "universalis",
            Region,
            itemIds,
            ListingLimit: 100,
            CoverageMode: OutfitterMarketCoverageMode.ExhaustiveWithinScope,
            MaxConcurrency: 4);
        discoveryTask = marketDiscovery.DiscoverAsync(discoveryRequest, cancellation!.Token);
        State = State with
        {
            Message = $"Exact craft graphs require {preparation.RequiredMaterialItemIds.Count:N0} terminal material item(s); publishing one bounded equipment-plus-material evidence generation. Cancel remains available.",
            Completed = 0,
            Total = itemIds.Length,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private void StartCraftFinalization(
        OutfitterAdvisorCraftDiscoveryResult preparation,
        OutfitterMarketEvidenceBook evidence)
    {
        var craftGeneration = sessionGeneration;
        Volatile.Write(ref craftProgress, null);
        craftDiscoveryOperation = craftDiscovery!.StartFinalization(
            craftGeneration,
            baseline!,
            evidence,
            preparation,
            resolvedFamily!,
            progress => Volatile.Write(ref craftProgress, new(craftGeneration, progress)),
            cancellation!.Token);
        State = State with
        {
            Message = $"Material evidence is published; constructing {preparation.Preparations.Count:N0} passive NQ craft offer(s) off the framework tick. Cancel remains available.",
            Completed = 0,
            Total = preparation.Preparations.Count,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private void StartSolving(
        IReadOnlyList<OutfitterCraftAdvisorOffer> craftOffers,
        string craftDiagnostic)
    {
        var capturedBaseline = baseline!;
        var capturedOffers = offers!;
        var capturedFamily = resolvedFamily!;
        var capturedOwnedItems = ownedItemsEvidence;
        var capturedOwnedCoverageComplete = ownedInventoryCoverageComplete;
        var capturedContext = State.Context;
        var solvingEvidence = pendingSolvingEvidence!;
        var capturedGeneration = sessionGeneration;
        solvingStartedAtUtc = DateTimeOffset.UtcNow;
        pendingCraftDiagnostic = craftDiagnostic;
        Volatile.Write(ref solverProgress, null);
        solving.Start(
            sessionGeneration,
            token => advisor.Build(
                capturedBaseline,
                solvingEvidence,
                itemId => capturedOffers.Definitions.TryGetValue(itemId, out var definition) ? [definition] : [],
                capturedFamily,
                capturedContext.Id,
                capturedOffers.VendorOffers,
                capturedOwnedItems,
                token,
                progress => Volatile.Write(ref solverProgress, new(capturedGeneration, progress)),
#if DEBUG
                replay => AdvisorSolverReplayFileStore.Write(solverReplayPath, replay),
#else
                null,
#endif
                capturedOwnedCoverageComplete,
                craftOffers,
                capturedOffers.MarketItemIds.ToHashSet()),
            cancellation!.Token);
        pendingSolvingEvidence = null;
        State = State with
        {
            Message = $"{craftDiagnostic} Solving the exact frontier off the framework tick. Cancel remains available.",
            CoverageLabel = $"{State.CoverageLabel} {craftDiagnostic}",
            Completed = craftOffers.Count,
            Total = craftOffers.Count,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private void TickSolving()
    {
        var result = solving.Poll(sessionGeneration);
        if (result.Status == GenerationBoundComputationStatus.Pending)
        {
            var progress = Volatile.Read(ref solverProgress);
            var elapsed = DateTimeOffset.UtcNow - solvingStartedAtUtc;
            State = State with
            {
                Message = progress is { Generation: var generation, Progress: var value } && generation == sessionGeneration
                    ? $"Exact frontier {value.CompletedPositionCount:N0}/{value.TotalPositionCount:N0} through {value.Position}: {value.CandidateStateCount:N0} candidates, {value.RetainedStateCount:N0} exact retained states, {value.ExpandedStateCount:N0} expanded ({elapsed.TotalSeconds:N0}s). Cancel remains available."
                    : $"Preparing the exact frontier off the framework tick ({elapsed.TotalSeconds:N0}s). Cancel remains available.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return;
        }
        if (result.Status is GenerationBoundComputationStatus.None or GenerationBoundComputationStatus.Stale)
            return;
        if (result.Status == GenerationBoundComputationStatus.Cancelled)
        {
            CancelCore(MinerBotanistAdvisorSessionStage.Cancelled, "Advisor frontier construction was cancelled.");
            return;
        }
        if (result.Status == GenerationBoundComputationStatus.Faulted)
        {
            CancelCore(MinerBotanistAdvisorSessionStage.Failed,
                $"Advisor frontier construction failed safely: {result.Exception?.Message ?? "Unknown worker failure."}");
            return;
        }

        var advice = result.Value!;
        var stage = advice.Status == MinerBotanistAdvisorStatus.Complete
            ? MinerBotanistAdvisorSessionStage.Complete
            : MinerBotanistAdvisorSessionStage.Abstained;
        var retainPrevious = advice.Status != MinerBotanistAdvisorStatus.Complete && State.Advice is { Frontier: not null };
        if (advice.Status == MinerBotanistAdvisorStatus.Complete)
        {
            CurrentEvidence = pendingCurrentEvidence;
            adviceTargetKey = requestedTarget?.Key ?? "active-loadout";
        }
        var craftDiagnostic = pendingCraftDiagnostic;
        State = State with
        {
            Stage = stage,
            Message = retainPrevious
                ? $"Refresh abstained: {advice.Diagnostic} The last valid frontier remains visible. {craftDiagnostic}"
                : string.IsNullOrWhiteSpace(craftDiagnostic) ? advice.Diagnostic : $"{advice.Diagnostic} {craftDiagnostic}",
            Advice = retainPrevious ? State.Advice : advice,
            AdviceIsRetained = retainPrevious,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        pendingCurrentEvidence = null;
        pendingCraftDiagnostic = null;
        pendingCraftPreparation = null;
        Volatile.Write(ref solverProgress, null);
        DisposeCancellation();
    }

    private void CompleteWorkbenchValidation(WorkbenchValidationRequest request)
    {
        var recapturedPlayer = PlayerAdvisorAuthorityFingerprint.Capture(baseline!);
        var availableInstances = new HashSet<EquipmentInstanceFingerprint>(
            baseline!.EquipmentSnapshot?.Instances.Select(value => value.Fingerprint) ?? [],
            EquipmentInstanceFingerprintComparer.Instance);
        var ownedAllocationsRemainAvailable = request.RequiredOwnedInstances.All(availableInstances.Contains);
        workbenchValidationRequest = null;
        if (recapturedPlayer != request.CapturedPlayer || !ownedAllocationsRemainAvailable)
        {
            CurrentEvidence = null;
            advicePlayerFingerprint = null;
            State = State with
            {
                Stage = MinerBotanistAdvisorSessionStage.Abstained,
                Message = ownedAllocationsRemainAvailable
                    ? "The player job, level, equipment, quality, materia, or relevant stat totals changed. The prior frontier is read-only; refresh it before Workbench review."
                    : "An owned item selected by the prior frontier is no longer available. Refresh before Workbench review.",
                AdviceIsRetained = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            DisposeCancellation();
            return;
        }

        completedValidationOwnedInstances = request.RequiredOwnedInstances;
        completedWorkbenchValidation = OutfitterWorkbenchPlayerValidation.Create(
            request.Advice,
            request.SelectedSolutionId,
            request.Evidence,
            request.CapturedPlayer,
            recapturedPlayer);
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.Complete,
            Message = "Current player baseline revalidated for Workbench review.",
            AdviceIsRetained = false,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DisposeCancellation();
    }

    private void Abstain(string message)
    {
        State = State with
        {
            Stage = MinerBotanistAdvisorSessionStage.Abstained,
            Message = message,
            AdviceIsRetained = State.Advice is { Frontier: not null },
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DisposeCancellation();
    }

    private void CancelCore(MinerBotanistAdvisorSessionStage terminalStage, string message)
    {
        if (cancellation is null && !State.IsBusy)
            return;
        cancellation?.Cancel();
        sessionGeneration++;
        solving.Invalidate();
        InvalidateCraftDiscovery();
        pendingCurrentEvidence = null;
        pendingSolvingEvidence = null;
        pendingCraftDiagnostic = null;
        pendingCraftPreparation = null;
        workbenchValidationRequest = null;
        completedWorkbenchValidation = null;
        completedValidationOwnedInstances = [];
        Volatile.Write(ref solverProgress, null);
        State = State with
        {
            Stage = terminalStage,
            Message = message,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DisposeCancellation();
    }

    private void ResetPendingCapture()
    {
        discoveryTask = null;
        discoveryRequest = null;
        InvalidateCraftDiscovery();
        baseline = null;
        resolvedFamily = null;
        offers = null;
        ownedItemsEvidence = null;
        ownedInventoryCoverageComplete = false;
        pendingCurrentEvidence = null;
        pendingSolvingEvidence = null;
        pendingCraftDiagnostic = null;
        advicePlayerFingerprint = null;
        workbenchValidationRequest = null;
        completedWorkbenchValidation = null;
        completedValidationOwnedInstances = [];
        Volatile.Write(ref solverProgress, null);
        Volatile.Write(ref craftProgress, null);
    }

    private void InvalidateCraftDiscovery()
    {
        craftDiscoveryOperation?.Invalidate();
        craftDiscoveryOperation = null;
        pendingCraftPreparation = null;
        Volatile.Write(ref craftProgress, null);
    }

    private void DisposeCancellation()
    {
        cancellation?.Dispose();
        cancellation = null;
    }

    private static IReadOnlyList<MinerBotanistOwnedItemEvidence> CaptureOwnedItems(PlayerAdvisorBaseline baseline)
    {
        var baselineInstances = new HashSet<EquipmentInstanceFingerprint>(
            baseline.EquippedSlots.Where(value => value.Instance is not null).Select(value => value.Instance!.Fingerprint),
            EquipmentInstanceFingerprintComparer.Instance);
        return (baseline.EquipmentSnapshot?.Instances ?? [])
            .Where(instance => !instance.IsEquipped &&
                !baselineInstances.Contains(instance.Fingerprint) &&
                IsOwnedGearContainer(instance.Fingerprint.Container))
            .Select(instance => new MinerBotanistOwnedItemEvidence(
                instance.Fingerprint.ItemId,
                instance.Fingerprint.IsHighQuality,
                OwnedContainerLabel(instance.Fingerprint.Container),
                instance,
                UtilityIsExact: instance.Fingerprint.MateriaIds.Count == 0))
            .ToArray();
    }

    private static bool ComponentIsComplete(PlayerAdvisorBaseline baseline, string component) =>
        baseline.EquipmentSnapshot?.Diagnostics.Components.Any(value =>
            string.Equals(value.Component, component, StringComparison.Ordinal) &&
            value.Status == Franthropy.Dalamud.Characters.SnapshotComponentStatus.Complete) == true;

    private static bool IsOwnedGearContainer(string container) =>
        container.StartsWith("Armory", StringComparison.Ordinal) ||
        container.StartsWith("Inventory", StringComparison.Ordinal) ||
        container.Contains("SaddleBag", StringComparison.Ordinal);

    private static string OwnedContainerLabel(string container) =>
        container.StartsWith("Armory", StringComparison.Ordinal) ? "Armoury"
            : container.Contains("SaddleBag", StringComparison.Ordinal) ? "Saddlebag"
            : "Inventory";

    private PlayerAdvisorBaseline CaptureRequestedBaseline()
    {
        if (requestedTarget is null)
            return baselineSource.Capture();
        if (baselineSource is IOutfitterTargetAdvisorBaselineSource targetSource)
            return targetSource.Capture(requestedTarget);
        return PlayerAdvisorBaselineAssembler.Failure(
            PlayerAdvisorBaselineStatus.Unsupported,
            "This Advisor baseline source cannot capture saved-gearset targets.");
    }

    private static OutfitterTarget RehydrateTarget(SavedGearsetTargetFingerprint fingerprint)
    {
        var job = new Franthropy.Dalamud.Characters.CharacterJobSnapshot(
            fingerprint.ClassJobId,
            string.Empty,
            string.Empty,
            fingerprint.JobLevel,
            true,
            null,
            string.Empty,
            EquipmentStatSemantic.Unknown,
            EquipmentDiscipline.Unknown);
        var gearset = new GearsetSnapshot(
            fingerprint.GearsetId,
            fingerprint.GearsetName,
            fingerprint.ClassJobId,
            [],
            true);
        return new(
            $"gearset:{fingerprint.GearsetId}",
            OutfitterTargetKind.Gearset,
            fingerprint.GearsetName,
            $"Gearset {fingerprint.GearsetId + 1:N0}",
            job,
            gearset);
    }

    private static MinerBotanistAdvisorSessionState Idle(AdvisorUtilityContextDescriptor context) => new(
        MinerBotanistAdvisorSessionStage.Idle,
        "Ready to evaluate the active player's gear.",
        "No evaluation yet.",
        0,
        null,
        context,
        null,
        false,
        DateTimeOffset.UtcNow);

    public void Dispose()
    {
        Cancel();
        GC.SuppressFinalize(this);
    }

    private sealed record WorkbenchValidationRequest(
        MinerBotanistReadOnlyAdvice Advice,
        string SelectedSolutionId,
        OutfitterMarketEvidenceBook Evidence,
        PlayerAdvisorAuthorityFingerprint CapturedPlayer,
        IReadOnlyList<EquipmentInstanceFingerprint> RequiredOwnedInstances);

    private sealed record SolverProgressSnapshot(long Generation, EquipmentExactFrontierProgress Progress);
    private sealed record CraftProgressSnapshot(long Generation, OutfitterAdvisorCraftDiscoveryProgress Progress);
}
