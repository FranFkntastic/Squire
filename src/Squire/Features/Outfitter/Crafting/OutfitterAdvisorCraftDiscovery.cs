using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Crafting;

internal sealed record OutfitterAdvisorCraftDiscoveryProgress(
    int Completed,
    int Total,
    int OfferReadyCount,
    int DisplayOnlyCount,
    int UnavailableCount);

internal sealed record OutfitterAdvisorCraftDiscoveryResult(
    IReadOnlyList<OutfitterPassiveCraftOfferPreparation> Preparations,
    IReadOnlyList<OutfitterCraftAdvisorOffer> Offers,
    IReadOnlyList<uint> RequiredMaterialItemIds,
    int EligibleCandidateCount,
    int RequestedCandidateCount,
    int DeferredCandidateCount,
    int PreparedCount,
    int OfferReadyCount,
    int DisplayOnlyCount,
    int UnavailableCount,
    string Diagnostic);

internal enum OutfitterAdvisorCraftDiscoveryPollStatus
{
    Pending,
    Completed,
    Cancelled,
    Stale,
    Faulted,
}

internal sealed record OutfitterAdvisorCraftDiscoveryPoll(
    OutfitterAdvisorCraftDiscoveryPollStatus Status,
    OutfitterAdvisorCraftDiscoveryResult? Result = null,
    Exception? Exception = null);

internal sealed class OutfitterAdvisorCraftDiscoveryOperation
{
    private Task<OutfitterAdvisorCraftDiscoveryResult>? task;

    internal OutfitterAdvisorCraftDiscoveryOperation(
        long generation,
        int eligibleCandidateCount,
        int requestedCandidateCount,
        Task<OutfitterAdvisorCraftDiscoveryResult> task)
    {
        Generation = generation;
        EligibleCandidateCount = eligibleCandidateCount;
        RequestedCandidateCount = requestedCandidateCount;
        this.task = task;
    }

    public long Generation { get; }
    public int EligibleCandidateCount { get; }
    public int RequestedCandidateCount { get; }

    public OutfitterAdvisorCraftDiscoveryPoll Poll(long currentGeneration)
    {
        var current = Volatile.Read(ref task);
        if (current is null || currentGeneration != Generation)
        {
            Invalidate();
            return new(OutfitterAdvisorCraftDiscoveryPollStatus.Stale);
        }
        if (!current.IsCompleted)
            return new(OutfitterAdvisorCraftDiscoveryPollStatus.Pending);
        Interlocked.CompareExchange(ref task, null, current);
        if (current.IsCanceled)
            return new(OutfitterAdvisorCraftDiscoveryPollStatus.Cancelled);
        if (current.IsFaulted)
            return new(OutfitterAdvisorCraftDiscoveryPollStatus.Faulted, Exception: current.Exception?.GetBaseException());
        return new(OutfitterAdvisorCraftDiscoveryPollStatus.Completed, current.GetAwaiter().GetResult());
    }

    public void Invalidate() => Interlocked.Exchange(ref task, null);
}

/// <summary>
/// Bounded worker orchestration for passive Craft Architect evidence. Recipe preparation runs first
/// so the session can publish one equipment-plus-material evidence generation before offer building.
/// </summary>
internal sealed class OutfitterAdvisorCraftDiscovery
{
    public const int MaximumCandidateRequests = 16;
    public const int MaximumConcurrentRequests = 2;
    public const int MaximumMaterialItemRequests = 128;

    private readonly IOutfitterPassiveCraftOfferProvider provider;

    public OutfitterAdvisorCraftDiscovery(IOutfitterPassiveCraftOfferProvider provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public OutfitterAdvisorCraftDiscoveryOperation StartPreparation(
        long generation,
        PlayerAdvisorBaseline baseline,
        MinerBotanistAdvisorCatalogResult catalog,
        IAdvisorStatFamily family,
        Action<OutfitterAdvisorCraftDiscoveryProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(family);
        var eligible = SelectCandidates(baseline, catalog, family);
        var requested = eligible.Take(MaximumCandidateRequests).ToArray();
        var task = Task.Run(
            () => PrepareAsync(eligible.Count, requested, reportProgress, cancellationToken),
            cancellationToken);
        return new(generation, eligible.Count, requested.Length, task);
    }

    public OutfitterAdvisorCraftDiscoveryOperation StartFinalization(
        long generation,
        PlayerAdvisorBaseline baseline,
        OutfitterMarketEvidenceBook evidence,
        OutfitterAdvisorCraftDiscoveryResult preparation,
        IAdvisorStatFamily family,
        Action<OutfitterAdvisorCraftDiscoveryProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(family);
        var task = Task.Run(
            () => Finalize(
                baseline,
                evidence,
                preparation,
                family,
                reportProgress,
                cancellationToken),
            cancellationToken);
        return new(generation, preparation.EligibleCandidateCount, preparation.Preparations.Count, task);
    }

    private static IReadOnlyList<EquipmentItemDefinition> SelectCandidates(
        PlayerAdvisorBaseline baseline,
        MinerBotanistAdvisorCatalogResult catalog,
        IAdvisorStatFamily family)
    {
        if (family is not CrafterAdvisorStatFamily ||
            baseline.Status != PlayerAdvisorBaselineStatus.Complete ||
            baseline.ClassJobId is not { } classJobId ||
            baseline.Level is not { } level ||
            !family.SupportedClassJobIds.Contains(classJobId))
        {
            return [];
        }

        return catalog.Definitions.Values
            .Where(definition =>
                definition.ItemId != 0 &&
                definition.EquipLevel <= level &&
                definition.EligibleClassJobIds.Contains(classJobId) &&
                definition.ResolveStatProfile(EquipmentQuality.Normal) is { IsComplete: true } profile &&
                profile.Parameters.Any(parameter => parameter.Value > 0 && family.IsRelevantSemantic(parameter.Semantic)) &&
                !AdvisorEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(definition) &&
                MinerBotanistReadOnlyAdvisor.Positions(definition).Count > 0)
            .GroupBy(definition => definition.ItemId)
            .Select(group => group
                .OrderByDescending(definition => definition.ItemLevel)
                .ThenBy(definition => definition.Name, StringComparer.Ordinal)
                .First())
            .OrderByDescending(definition => definition.EquipLevel)
            .ThenByDescending(definition => definition.ItemLevel)
            .ThenBy(definition => definition.ItemId)
            .ToArray();
    }

    private async Task<OutfitterAdvisorCraftDiscoveryResult> PrepareAsync(
        int eligibleCandidateCount,
        IReadOnlyList<EquipmentItemDefinition> requested,
        Action<OutfitterAdvisorCraftDiscoveryProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        using var concurrency = new SemaphoreSlim(MaximumConcurrentRequests, MaximumConcurrentRequests);
        var completed = 0;
        var unavailable = 0;
        reportProgress?.Invoke(new(0, requested.Count, 0, 0, 0));
        var tasks = requested.Select(async definition =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    var result = await provider.PrepareAsync(definition, cancellationToken).ConfigureAwait(false);
                    if (!result.IsPrepared)
                        Interlocked.Increment(ref unavailable);
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref unavailable);
                    return new OutfitterPassiveCraftOfferPreparationResult(
                        null,
                        [$"Craft provider failed safely for {definition.Name}: {exception.Message}"]);
                }
            }
            finally
            {
                concurrency.Release();
                var done = Interlocked.Increment(ref completed);
                reportProgress?.Invoke(new(
                    done,
                    requested.Count,
                    0,
                    0,
                    Volatile.Read(ref unavailable)));
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = new List<OutfitterPassiveCraftOfferPreparation>();
        var materialIds = new HashSet<uint>();
        var materialDeferred = 0;
        foreach (var result in results)
        {
            if (result.Preparation is not { } preparation)
                continue;
            var additionalMaterials = preparation.RecipeGraph.TerminalMaterialItemIds.Count(id => !materialIds.Contains(id));
            if (materialIds.Count + additionalMaterials > MaximumMaterialItemRequests)
            {
                materialDeferred++;
                continue;
            }
            accepted.Add(preparation);
            materialIds.UnionWith(preparation.RecipeGraph.TerminalMaterialItemIds);
        }

        var deferred = Math.Max(0, eligibleCandidateCount - requested.Count) + materialDeferred;
        var firstUnavailable = results
            .Select((result, index) => (Result: result, Definition: requested[index]))
            .FirstOrDefault(value => !value.Result.IsPrepared && value.Result.Diagnostics.Length != 0);
        var firstUnavailableReason = firstUnavailable.Result?.Diagnostics.FirstOrDefault(value =>
            value.StartsWith("Craft Architect ", StringComparison.Ordinal) && value.Contains(':')) ??
            firstUnavailable.Result?.Diagnostics.FirstOrDefault();
        var unavailableDetail = firstUnavailableReason is null
            ? string.Empty
            : $" First unavailable: {firstUnavailable.Definition.Name}: {firstUnavailableReason}";
        var diagnostic =
            $"Craft preparation: {accepted.Count:N0} exact graph(s) prepared from {requested.Count:N0}/{eligibleCandidateCount:N0} eligible catalog items; " +
            $"{materialIds.Count:N0}/{MaximumMaterialItemRequests:N0} terminal material IDs requested, {unavailable:N0} unavailable, {deferred:N0} deferred by runtime bounds." +
            unavailableDetail;
        return new(
            accepted,
            [],
            materialIds.Order().ToArray(),
            eligibleCandidateCount,
            requested.Count,
            deferred,
            accepted.Count,
            0,
            0,
            unavailable,
            diagnostic);
    }

    private OutfitterAdvisorCraftDiscoveryResult Finalize(
        PlayerAdvisorBaseline baseline,
        OutfitterMarketEvidenceBook evidence,
        OutfitterAdvisorCraftDiscoveryResult preparation,
        IAdvisorStatFamily family,
        Action<OutfitterAdvisorCraftDiscoveryProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        var offers = new List<OutfitterCraftAdvisorOffer>();
        var displayOnly = 0;
        var unavailable = preparation.UnavailableCount;
        string? firstUnavailable = null;
        reportProgress?.Invoke(new(0, preparation.Preparations.Count, 0, 0, unavailable));
        for (var index = 0; index < preparation.Preparations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OutfitterPassiveCraftOfferResult result;
            try
            {
                result = provider.Build(preparation.Preparations[index], baseline, evidence, family);
            }
            catch (Exception exception)
            {
                result = new(
                    OutfitterPassiveCraftOfferStatus.Abstained,
                    null,
                    null,
                    [$"Craft offer construction failed safely: {exception.Message}"]);
            }
            switch (result.Status)
            {
                case OutfitterPassiveCraftOfferStatus.OfferReady when result.Offer is not null:
                    offers.Add(result.Offer);
                    break;
                case OutfitterPassiveCraftOfferStatus.DisplayOnly:
                    displayOnly++;
                    firstUnavailable ??= FormatUnavailable(preparation.Preparations[index], result);
                    break;
                default:
                    unavailable++;
                    firstUnavailable ??= FormatUnavailable(preparation.Preparations[index], result);
                    break;
            }
            reportProgress?.Invoke(new(index + 1, preparation.Preparations.Count, offers.Count, displayOnly, unavailable));
        }

        var diagnostic =
            $"Craft coverage: {offers.Count:N0} ready, {displayOnly:N0} display-only, {unavailable:N0} unavailable, " +
            $"{preparation.DeferredCandidateCount:N0} deferred from {preparation.RequestedCandidateCount:N0}/{preparation.EligibleCandidateCount:N0} eligible; " +
            $"{preparation.PreparedCount:N0} exact graph(s) and " +
            $"{preparation.RequiredMaterialItemIds.Count:N0} terminal material item(s) evaluated from one published evidence generation." +
            (firstUnavailable is null ? string.Empty : $" First non-ready: {firstUnavailable}");
        return preparation with
        {
            Offers = offers,
            OfferReadyCount = offers.Count,
            DisplayOnlyCount = displayOnly,
            UnavailableCount = unavailable,
            Diagnostic = diagnostic,
        };

        static string? FormatUnavailable(
            OutfitterPassiveCraftOfferPreparation candidate,
            OutfitterPassiveCraftOfferResult result)
        {
            var reason = result.Diagnostics.FirstOrDefault();
            return string.IsNullOrWhiteSpace(reason) ? null : $"{candidate.Definition.Name}: {reason}";
        }
    }
}
