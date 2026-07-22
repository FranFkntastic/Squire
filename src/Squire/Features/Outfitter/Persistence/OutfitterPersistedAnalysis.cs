using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.Persistence;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Persistence;

internal sealed record OutfitterPersistedProfile(
    string ProfileId,
    string ProfileVersion,
    AdvisorProfileCalibrationState CalibrationState,
    string ContextId);

internal sealed record OutfitterPersistedEvidenceLineage(
    Guid GenerationId,
    long Revision,
    string SchemaVersion,
    string SourceKey,
    string Region,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset PublishedAtUtc,
    OutfitterMarketCoverage Coverage);

internal sealed record OutfitterPersistedCraftRecipe(
    uint RecipeId,
    uint CraftCount,
    string ItemName,
    int Depth);

internal sealed record OutfitterPersistedCraftMaterial(
    string PlanSha256,
    uint ItemId,
    string ItemName,
    EquipmentQuality Quality,
    uint ConsumedQuantity,
    uint PurchasedQuantity,
    uint SurplusQuantity,
    OutfitterMaterialSourceKind SourceKind,
    uint UnitPriceGil,
    string SourceIdentity);

internal sealed record OutfitterPersistedCraftHandoff(
    string SelectedSolutionId,
    IReadOnlyList<string> PlanSha256s,
    IReadOnlyList<OutfitterPersistedCraftRecipe> Recipes,
    IReadOnlyList<OutfitterPersistedCraftMaterial> Materials,
    DateTimeOffset CapturedAtUtc)
{
    public static OutfitterPersistedCraftHandoff Create(
        OutfitterCraftHandoffProjection projection,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (capturedAtUtc == default)
            throw new ArgumentException("Craft handoff persistence requires an explicit capture time.", nameof(capturedAtUtc));
        return new(
            projection.SelectedSolutionId,
            projection.PlanIdentities.Select(value => value.Sha256).ToArray(),
            projection.Recipes.Select(value => new OutfitterPersistedCraftRecipe(
                value.RecipeId,
                value.CraftCount,
                value.ItemName,
                value.Depth)).ToArray(),
            projection.Materials.Select(value => new OutfitterPersistedCraftMaterial(
                value.PlanIdentity.Sha256,
                value.ItemId,
                value.ItemName,
                value.Quality,
                value.ConsumedQuantity,
                value.PurchasedQuantity,
                value.SurplusQuantity,
                value.Source.Kind,
                value.Source.UnitPriceGil,
                SourceIdentity(value.Source))).ToArray(),
            capturedAtUtc);
    }

    private static string SourceIdentity(OutfitterMaterialSourceIdentity source) => source switch
    {
        OutfitterMarketMaterialSourceIdentity market =>
            $"market:{market.EvidenceGenerationId:N}:{market.EvidenceRevision}:{market.WorldId}:{market.ListingId}",
        OutfitterGilVendorMaterialSourceIdentity vendor =>
            $"vendor:{vendor.CatalogVersion}:{vendor.ShopId}:{vendor.VendorId}:{vendor.TerritoryId}",
        _ => throw new InvalidOperationException($"Unsupported craft material source '{source.GetType().Name}'."),
    };
}

internal sealed record OutfitterPersistedAnalysis(
    Guid AnalysisId,
    long Revision,
    SavedGearsetTargetFingerprint Target,
    OutfitterPersistedProfile Profile,
    string BaselineAuthorityFingerprint,
    OutfitterPersistedEvidenceLineage Evidence,
    IReadOnlyList<EquipmentDecisionSolution> Frontier,
    IReadOnlyDictionary<string, AdvisorAuthorityAssessment> AuthorityBySolutionId,
    string? NominationSolutionId,
    string? SelectedSolutionId,
    IReadOnlyList<EquipmentInstanceFingerprint> RequiredOwnedInstances,
    OutfitterPersistedCraftHandoff? CraftHandoff,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static OutfitterPersistedAnalysis Create(
        SavedGearsetTargetFingerprint target,
        PlayerAdvisorBaseline baseline,
        IAdvisorStatFamily family,
        AdvisorUtilityContextDescriptor context,
        OutfitterMarketEvidenceBook evidence,
        MinerBotanistReadOnlyAdvice advice,
        string? selectedSolutionId = null,
        OutfitterPersistedCraftHandoff? craftHandoff = null,
        DateTimeOffset? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(advice);
        if (baseline is not
            {
                Status: PlayerAdvisorBaselineStatus.Complete,
                Target: { Kind: PlayerAdvisorBaselineTargetKind.SavedGearset } baselineTarget,
            } || !string.Equals(baselineTarget.AuthorityFingerprint, target.Value, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted saved-gearset analysis requires a matching complete target baseline.");
        if (advice is not { Status: MinerBotanistAdvisorStatus.Complete, Frontier: { } exact } || !evidence.IsPublishable)
            throw new InvalidOperationException("Only complete advice with published market evidence may become a persisted analysis.");
        if (!string.Equals(context.Id, family.ResolveContext(context.Id).Id, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted analysis context is outside the selected utility profile.");

        var frontier = exact.Pareto.Frontier.ToArray();
        var nominationId = advice.Nomination?.Candidate.SolutionId;
        selectedSolutionId ??= nominationId;
        var selected = selectedSolutionId is null
            ? null
            : frontier.SingleOrDefault(value => string.Equals(value.Candidate.SolutionId, selectedSolutionId, StringComparison.Ordinal))
              ?? throw new InvalidOperationException("Selected solution is absent from the exact frontier.");
        var requiredOwned = selected?.Candidate.Selections
            .Select(selection => advice.OffersByAllocation.GetValueOrDefault(selection.AllocationKey))
            .Where(offer => offer is { Offer.SourceKind: EquipmentAcquisitionSourceKind.Owned, Offer.Instance: not null })
            .Select(offer => offer!.Offer.Instance!.Fingerprint)
            .Distinct(EquipmentInstanceFingerprintComparer.Instance)
            .ToArray() ?? [];
        var now = createdAtUtc ?? DateTimeOffset.UtcNow;
        if (now == default || evidence.PublishedAtUtc is not { } publishedAtUtc)
            throw new InvalidOperationException("Persisted analysis timestamps and published evidence time are required.");
        var result = new OutfitterPersistedAnalysis(
            Guid.NewGuid(),
            1,
            target,
            new(
                family.ProfileDescriptor.Id,
                family.ProfileDescriptor.Version,
                family.ProfileDescriptor.CalibrationState,
                context.Id),
            PlayerAdvisorAuthorityFingerprint.Capture(baseline).Value,
            new(
                evidence.GenerationId,
                evidence.Revision,
                evidence.SchemaVersion,
                evidence.SourceKey,
                evidence.Region,
                evidence.CreatedAtUtc,
                publishedAtUtc,
                evidence.Coverage),
            frontier,
            new Dictionary<string, AdvisorAuthorityAssessment>(advice.AuthorityBySolutionId, StringComparer.Ordinal),
            nominationId,
            selectedSolutionId,
            requiredOwned,
            craftHandoff,
            now,
            now);
        OutfitterPersistedAnalysisValidation.ValidateDocument(result);
        return result;
    }
}

internal sealed record OutfitterPersistedAnalysisBook(
    string SchemaVersion,
    long Revision,
    IReadOnlyList<OutfitterPersistedAnalysis> Analyses)
{
    public const string CurrentSchemaVersion = "marketmafioso-outfitter-analyses/v1";
    public static OutfitterPersistedAnalysisBook Empty { get; } = new(CurrentSchemaVersion, 0, []);
}

internal sealed class OutfitterPersistedAnalysisStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);

    public OutfitterPersistedAnalysisStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task<OutfitterPersistedAnalysisBook> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(LoadCore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OutfitterPersistedAnalysis> UpsertAsync(
        OutfitterPersistedAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        OutfitterPersistedAnalysisValidation.ValidateDocument(analysis);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var book = LoadCore();
                var existing = book.Analyses.SingleOrDefault(value => value.AnalysisId == analysis.AnalysisId);
                var now = DateTimeOffset.UtcNow;
                var saved = analysis with
                {
                    Revision = existing is null ? Math.Max(1, analysis.Revision) : checked(existing.Revision + 1),
                    CreatedAtUtc = existing?.CreatedAtUtc ?? analysis.CreatedAtUtc,
                    UpdatedAtUtc = now,
                };
                OutfitterPersistedAnalysisValidation.ValidateDocument(saved);
                var analyses = book.Analyses
                    .Where(value => value.AnalysisId != saved.AnalysisId)
                    .Append(saved)
                    .OrderByDescending(value => value.UpdatedAtUtc)
                    .ToArray();
                AtomicJsonFile.Write(path, new OutfitterPersistedAnalysisBook(
                    OutfitterPersistedAnalysisBook.CurrentSchemaVersion,
                    checked(book.Revision + 1),
                    analyses), JsonOptions);
                return saved;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private OutfitterPersistedAnalysisBook LoadCore()
    {
        if (!File.Exists(path))
            return OutfitterPersistedAnalysisBook.Empty;
        var book = AtomicJsonFile.Read<OutfitterPersistedAnalysisBook>(path, JsonOptions)
            ?? throw new InvalidDataException("Persisted Outfitter analysis document is empty.");
        if (!string.Equals(book.SchemaVersion, OutfitterPersistedAnalysisBook.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported persisted Outfitter analysis schema '{book.SchemaVersion}'.");
        if (book.Revision < 0 || book.Analyses is null ||
            book.Analyses.GroupBy(value => value.AnalysisId).Any(group => group.Key == Guid.Empty || group.Count() != 1))
            throw new InvalidDataException("Persisted Outfitter analysis book has invalid revision or duplicate identities.");
        foreach (var analysis in book.Analyses)
            OutfitterPersistedAnalysisValidation.ValidateDocument(analysis);
        return book;
    }
}

internal enum OutfitterPersistedBoundaryKind
{
    Character,
    Target,
    Baseline,
    OwnedAllocations,
    Profile,
    Context,
    SelectedSolution,
    MarketEvidence,
}

internal sealed record OutfitterPersistedBoundary(
    OutfitterPersistedBoundaryKind Kind,
    bool IsCurrent,
    string Diagnostic);

internal sealed record OutfitterPersistedAnalysisRevalidation(
    IReadOnlyList<OutfitterPersistedBoundary> Boundaries)
{
    public bool CanAct => Boundaries.Count > 0 && Boundaries.All(value => value.IsCurrent);
    public IReadOnlyList<OutfitterPersistedBoundary> StaleBoundaries => Boundaries.Where(value => !value.IsCurrent).ToArray();
}

internal static class OutfitterPersistedAnalysisValidation
{
    public static OutfitterPersistedAnalysisRevalidation Revalidate(
        OutfitterPersistedAnalysis analysis,
        PlayerAdvisorBaseline currentBaseline,
        IAdvisorStatFamily? currentFamily,
        AdvisorUtilityContextDescriptor? currentContext,
        OutfitterMarketEvidenceBook? currentEvidence,
        MinerBotanistReadOnlyAdvice? currentAdvice = null)
    {
        ValidateDocument(analysis);
        ArgumentNullException.ThrowIfNull(currentBaseline);

        var characterCurrent = currentBaseline.Status == PlayerAdvisorBaselineStatus.Complete &&
            currentBaseline.Character == analysis.Target.Character;
        var targetCurrent = characterCurrent &&
            currentBaseline.Target is { Kind: PlayerAdvisorBaselineTargetKind.SavedGearset } target &&
            string.Equals(target.AuthorityFingerprint, analysis.Target.Value, StringComparison.Ordinal);
        var baselineCurrent = targetCurrent &&
            string.Equals(
                PlayerAdvisorAuthorityFingerprint.Capture(currentBaseline).Value,
                analysis.BaselineAuthorityFingerprint,
                StringComparison.Ordinal);
        var availableInstances = new HashSet<EquipmentInstanceFingerprint>(
            currentBaseline.EquipmentSnapshot?.Instances.Select(value => value.Fingerprint) ?? [],
            EquipmentInstanceFingerprintComparer.Instance);
        var ownedCurrent = currentBaseline.Status == PlayerAdvisorBaselineStatus.Complete &&
            analysis.RequiredOwnedInstances.All(availableInstances.Contains);
        var profileCurrent = currentFamily is not null &&
            string.Equals(currentFamily.ProfileDescriptor.Id, analysis.Profile.ProfileId, StringComparison.Ordinal) &&
            string.Equals(currentFamily.ProfileDescriptor.Version, analysis.Profile.ProfileVersion, StringComparison.Ordinal) &&
            currentFamily.ProfileDescriptor.CalibrationState == analysis.Profile.CalibrationState;
        var contextCurrent = profileCurrent &&
            string.Equals(currentContext?.Id, analysis.Profile.ContextId, StringComparison.Ordinal);
        var selectedCurrent = analysis.SelectedSolutionId is not null &&
            currentAdvice is { Status: MinerBotanistAdvisorStatus.Complete, Frontier: { } currentFrontier } &&
            currentFrontier.Pareto.Frontier.Any(value =>
                string.Equals(value.Candidate.SolutionId, analysis.SelectedSolutionId, StringComparison.Ordinal));
        var evidenceCurrent = currentEvidence is { IsPublishable: true } &&
            currentEvidence.GenerationId == analysis.Evidence.GenerationId &&
            currentEvidence.Revision == analysis.Evidence.Revision &&
            string.Equals(currentEvidence.SchemaVersion, analysis.Evidence.SchemaVersion, StringComparison.Ordinal);

        return new(
        [
            new(OutfitterPersistedBoundaryKind.Character, characterCurrent,
                characterCurrent ? "Character identity is current." : "Current character does not match the saved analysis."),
            new(OutfitterPersistedBoundaryKind.Target, targetCurrent,
                targetCurrent ? "Saved-gearset fingerprint is current." : "Saved gearset name, job, level, slots, quality, or materia changed."),
            new(OutfitterPersistedBoundaryKind.Baseline, baselineCurrent,
                baselineCurrent ? "Target stat baseline is current." : "Target level, equipment utility, materia, or fixed stats changed."),
            new(OutfitterPersistedBoundaryKind.OwnedAllocations, ownedCurrent,
                ownedCurrent ? "Selected owned allocations remain available." : "One or more selected owned instances moved, changed, or disappeared."),
            new(OutfitterPersistedBoundaryKind.Profile, profileCurrent,
                profileCurrent ? "Utility profile version is current." : "Utility profile identity, version, or calibration changed."),
            new(OutfitterPersistedBoundaryKind.Context, contextCurrent,
                contextCurrent ? "Utility context is current." : "Utility context changed."),
            new(OutfitterPersistedBoundaryKind.SelectedSolution, selectedCurrent,
                selectedCurrent ? "Selected solution remains present in freshly computed advice." : "Selected solution has not been reproduced by current advice."),
            new(OutfitterPersistedBoundaryKind.MarketEvidence, evidenceCurrent,
                evidenceCurrent ? "Market evidence generation is current." : "Market evidence generation changed or is no longer publishable."),
        ]);
    }

    public static void ValidateDocument(OutfitterPersistedAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.AnalysisId == Guid.Empty || analysis.Revision < 1 || analysis.Target is null ||
            analysis.Target.Character.LocalContentId == 0 || analysis.Target.Character.HomeWorldId == 0 ||
            analysis.Target.GearsetId < 0 || string.IsNullOrWhiteSpace(analysis.Target.GearsetName) ||
            analysis.Target.ClassJobId == 0 || analysis.Target.JobLevel is < 1 or > 100 ||
            analysis.Target.Slots is null || analysis.Target.Slots.Count != 12 ||
            analysis.Target.Slots.GroupBy(value => value.Position).Any(group => group.Count() != 1) ||
            analysis.Target.Slots.Any(value =>
                value.MateriaIds is null || value.MateriaGrades is null || value.MateriaIds.Count != value.MateriaGrades.Count ||
                value.ItemId is null && (value.Quality is not null || value.MateriaIds.Count != 0) ||
                value.ItemId is not null && value.Quality is null) ||
            string.IsNullOrWhiteSpace(analysis.Target.Value) || analysis.Target.Value.Length != 64 ||
            analysis.Profile is null || string.IsNullOrWhiteSpace(analysis.Profile.ProfileId) ||
            string.IsNullOrWhiteSpace(analysis.Profile.ProfileVersion) || string.IsNullOrWhiteSpace(analysis.Profile.ContextId) ||
            string.IsNullOrWhiteSpace(analysis.BaselineAuthorityFingerprint) ||
            analysis.Evidence is null || analysis.Evidence.GenerationId == Guid.Empty || analysis.Evidence.Revision < 0 ||
            analysis.Evidence.PublishedAtUtc == default || analysis.Frontier is null || analysis.Frontier.Count == 0 ||
            analysis.AuthorityBySolutionId is null || analysis.RequiredOwnedInstances is null ||
            analysis.CreatedAtUtc == default || analysis.UpdatedAtUtc < analysis.CreatedAtUtc)
        {
            throw new InvalidDataException("Persisted Outfitter analysis is structurally invalid.");
        }
        var solutionIds = analysis.Frontier.Select(value => value.Candidate.SolutionId).ToArray();
        if (solutionIds.Any(string.IsNullOrWhiteSpace) || solutionIds.Distinct(StringComparer.Ordinal).Count() != solutionIds.Length ||
            analysis.AuthorityBySolutionId.Keys.Any(key => !solutionIds.Contains(key, StringComparer.Ordinal)) ||
            analysis.NominationSolutionId is not null && !solutionIds.Contains(analysis.NominationSolutionId, StringComparer.Ordinal) ||
            analysis.SelectedSolutionId is not null && !solutionIds.Contains(analysis.SelectedSolutionId, StringComparer.Ordinal) ||
            analysis.CraftHandoff is not null &&
            (!string.Equals(analysis.CraftHandoff.SelectedSolutionId, analysis.SelectedSolutionId, StringComparison.Ordinal) ||
             analysis.CraftHandoff.CapturedAtUtc == default || analysis.CraftHandoff.PlanSha256s is null ||
             analysis.CraftHandoff.Recipes is null || analysis.CraftHandoff.Materials is null ||
             analysis.CraftHandoff.PlanSha256s.Any(string.IsNullOrWhiteSpace)))
        {
            throw new InvalidDataException("Persisted Outfitter analysis solution lineage is inconsistent.");
        }
    }
}
