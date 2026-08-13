using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarketMafioso.Squire.Outfitter.Portfolio;

public sealed record PortfolioRetainerEvidenceGeneration(
    string TargetKey,
    string EvidenceGeneration);

public sealed record PortfolioAuthorityLineage(
    string OwnerKey,
    string OwnerEvidenceGeneration,
    string InventoryEvidenceGeneration,
    string ListingEvidenceGeneration,
    IReadOnlyList<PortfolioRetainerEvidenceGeneration> RetainerEvidenceGenerations);

public sealed record PortfolioAuthorityTarget(
    string TargetKey,
    int Priority,
    PortfolioProgressionHorizon ProgressionHorizon,
    string? SelectedCandidateKey,
    PortfolioTargetDispositionKind Disposition,
    string EvidenceGeneration,
    string DispositionReason);

public sealed record PortfolioAuthorityAllocation(
    string TargetKey,
    string CandidateKey,
    PortfolioAllocationKey Allocation,
    uint Quantity);

public sealed record PortfolioAuthorityHandMeDown(
    string UpstreamTargetKey,
    string UpstreamCandidateKey,
    string DownstreamTargetKey,
    string DownstreamCandidateKey,
    string ReleaseEvidenceGeneration,
    PortfolioAllocationKey Allocation,
    PortfolioExactItem ReleasedItem,
    PortfolioExactItem SelectedReplacement);

public sealed record PortfolioAuthorityFingerprint(
    string SchemaVersion,
    string Sha256);

/// <summary>
/// Immutable authority for one global portfolio decision. Its semantic fingerprint binds every
/// input generation and decision that makes later slot execution safe; display labels and runtime
/// timestamps are deliberately absent.
/// </summary>
public sealed record PortfolioAuthorityEnvelope(
    string SchemaVersion,
    PortfolioAuthorityLineage Lineage,
    IReadOnlyList<PortfolioAuthorityTarget> Targets,
    IReadOnlyList<PortfolioAuthorityAllocation> Allocations,
    IReadOnlyList<PortfolioAuthorityHandMeDown> HandMeDownChain,
    PortfolioAuthorityFingerprint Fingerprint)
{
    public const string CurrentSchemaVersion = "squire-outfitter-portfolio-authority/v1";

    public bool HasValidFingerprint() =>
        Fingerprint == PortfolioAuthorityEnvelopeFactory.ComputeFingerprint(
            SchemaVersion,
            Lineage,
            Targets,
            Allocations,
            HandMeDownChain);
}

public static class PortfolioAuthorityEnvelopeFactory
{
    public static PortfolioAuthorityEnvelope Create(
        PortfolioAuthorityLineage lineage,
        OutfitterPortfolioPlan plan)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsComplete)
            throw new InvalidOperationException("Incomplete portfolio planning cannot create execution authority.");
        if (!PortfolioTargetDispositionResolver.Validate(plan, out var dispositionDiagnostic))
            throw new InvalidOperationException($"Portfolio target dispositions are not executable: {dispositionDiagnostic}");
        ValidateLineage(lineage);

        var selected = plan.SelectedCandidates.ToDictionary(candidate => candidate.TargetKey, StringComparer.Ordinal);
        var targets = plan.OrderedTargets
            .Select(target => new PortfolioAuthorityTarget(
                target.TargetKey,
                target.Priority,
                target.ProgressionHorizon,
                selected.GetValueOrDefault(target.TargetKey)?.CandidateKey,
                plan.DispositionFor(target.TargetKey)!.Kind,
                plan.DispositionFor(target.TargetKey)!.EvidenceGeneration!,
                plan.DispositionFor(target.TargetKey)!.Reason))
            .OrderBy(target => target.TargetKey, StringComparer.Ordinal)
            .ToArray();
        var allocations = plan.SelectedCandidates
            .SelectMany(candidate => candidate.Demands.Select(demand => new PortfolioAuthorityAllocation(
                candidate.TargetKey,
                candidate.CandidateKey,
                demand.Allocation,
                demand.Quantity)))
            .OrderBy(allocation => allocation.TargetKey, StringComparer.Ordinal)
            .ThenBy(allocation => allocation.CandidateKey, StringComparer.Ordinal)
            .ThenBy(allocation => allocation.Allocation.SourceKind)
            .ThenBy(allocation => allocation.Allocation.SourceKey, StringComparer.Ordinal)
            .ThenBy(allocation => allocation.Allocation.ItemId)
            .ThenBy(allocation => allocation.Allocation.IsHighQuality)
            .ThenBy(allocation => allocation.Allocation.InstanceId, StringComparer.Ordinal)
            .ToArray();
        var releases = plan.SelectedCandidates
            .SelectMany(candidate => candidate.HandMeDowns.Select(release => (Candidate: candidate, Release: release)))
            .ToDictionary(value => value.Release.ReleasedAllocation, value => value);
        var handMeDowns = allocations
            .Where(allocation => allocation.Allocation.SourceKind == PortfolioAllocationSourceKind.HandMeDown)
            .Select(allocation =>
            {
                if (!releases.TryGetValue(allocation.Allocation, out var release))
                    throw new InvalidOperationException("A selected hand-me-down allocation has no selected upstream release evidence.");
                return new PortfolioAuthorityHandMeDown(
                    release.Candidate.TargetKey,
                    release.Candidate.CandidateKey,
                    allocation.TargetKey,
                    allocation.CandidateKey,
                    release.Release.EvidenceGeneration,
                    allocation.Allocation,
                    release.Release.ReleasedItem,
                    release.Release.SelectedReplacement);
            })
            .OrderBy(value => value.UpstreamTargetKey, StringComparer.Ordinal)
            .ThenBy(value => value.DownstreamTargetKey, StringComparer.Ordinal)
            .ThenBy(value => value.Allocation.SourceKey, StringComparer.Ordinal)
            .ToArray();
        var canonicalLineage = Canonicalize(lineage);
        var fingerprint = ComputeFingerprint(
            PortfolioAuthorityEnvelope.CurrentSchemaVersion,
            canonicalLineage,
            targets,
            allocations,
            handMeDowns);
        return new(
            PortfolioAuthorityEnvelope.CurrentSchemaVersion,
            canonicalLineage,
            targets,
            allocations,
            handMeDowns,
            fingerprint);
    }

    public static PortfolioAuthorityFingerprint ComputeFingerprint(
        string schemaVersion,
        PortfolioAuthorityLineage lineage,
        IReadOnlyList<PortfolioAuthorityTarget> targets,
        IReadOnlyList<PortfolioAuthorityAllocation> allocations,
        IReadOnlyList<PortfolioAuthorityHandMeDown> handMeDowns)
    {
        var payload = new
        {
            SchemaVersion = schemaVersion,
            Lineage = Canonicalize(lineage),
            Targets = targets.OrderBy(value => value.TargetKey, StringComparer.Ordinal).ToArray(),
            Allocations = allocations
                .OrderBy(value => value.TargetKey, StringComparer.Ordinal)
                .ThenBy(value => value.CandidateKey, StringComparer.Ordinal)
                .ThenBy(value => value.Allocation.SourceKind)
                .ThenBy(value => value.Allocation.SourceKey, StringComparer.Ordinal)
                .ThenBy(value => value.Allocation.ItemId)
                .ThenBy(value => value.Allocation.IsHighQuality)
                .ThenBy(value => value.Allocation.InstanceId, StringComparer.Ordinal)
                .ToArray(),
            HandMeDowns = handMeDowns
                .OrderBy(value => value.UpstreamTargetKey, StringComparer.Ordinal)
                .ThenBy(value => value.DownstreamTargetKey, StringComparer.Ordinal)
                .ThenBy(value => value.Allocation.SourceKey, StringComparer.Ordinal)
                .ToArray(),
        };
        var json = JsonSerializer.Serialize(payload);
        return new(
            PortfolioAuthorityEnvelope.CurrentSchemaVersion,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    private static PortfolioAuthorityLineage Canonicalize(PortfolioAuthorityLineage lineage) => lineage with
    {
        RetainerEvidenceGenerations = lineage.RetainerEvidenceGenerations
            .OrderBy(value => value.TargetKey, StringComparer.Ordinal)
            .ThenBy(value => value.EvidenceGeneration, StringComparer.Ordinal)
            .ToArray(),
    };

    private static void ValidateLineage(PortfolioAuthorityLineage lineage)
    {
        if (string.IsNullOrWhiteSpace(lineage.OwnerKey) ||
            string.IsNullOrWhiteSpace(lineage.OwnerEvidenceGeneration) ||
            string.IsNullOrWhiteSpace(lineage.InventoryEvidenceGeneration) ||
            string.IsNullOrWhiteSpace(lineage.ListingEvidenceGeneration))
            throw new ArgumentException("Portfolio authority requires exact owner, inventory, and listing evidence generations.", nameof(lineage));
        if (lineage.RetainerEvidenceGenerations.Any(value =>
                string.IsNullOrWhiteSpace(value.TargetKey) || string.IsNullOrWhiteSpace(value.EvidenceGeneration)) ||
            lineage.RetainerEvidenceGenerations.Select(value => value.TargetKey).Distinct(StringComparer.Ordinal).Count() != lineage.RetainerEvidenceGenerations.Count)
            throw new ArgumentException("Retainer authority requires one exact evidence generation per included retainer target.", nameof(lineage));
    }
}
