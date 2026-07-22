using System.Linq;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire;
using MarketMafioso.Squire.Observation;

namespace MarketMafioso.Windows.Squire;

internal static class SquireBridgeTruthFactory
{
    public static AgentBridgeSquireTruth Create(
        SquireAnalysis? analysis,
        string status,
        ISquireActionGameAdapter actionAdapter)
    {
        if (analysis is null)
        {
            return new AgentBridgeSquireTruth
            {
                HasSnapshot = false,
                Status = status,
                CharacterName = null,
                HomeWorldId = null,
                CapturedAtUtc = null,
                IsComplete = false,
                UnlockedJobCount = 0,
                ValidGearsetCount = 0,
                InstanceCount = 0,
                CandidateCount = 0,
                ProtectedCount = 0,
                EvaluationFailureCount = 0,
                UnsupportedCount = 0,
                BlockingDiagnostics = [],
                EvaluationFailureGroups = [],
                ProtectionGroups = [],
                ExecutableCandidates = [],
                ApplicableRuleCount = 0,
                EnabledRuleCount = 0,
                RuleValidationErrors = [],
            };
        }

        var snapshot = analysis.Snapshot;
        return new AgentBridgeSquireTruth
        {
            HasSnapshot = true,
            Status = status,
            CharacterName = snapshot.Identity.Scope?.Name,
            HomeWorldId = snapshot.Identity.Scope?.HomeWorldId.ToString(),
            CapturedAtUtc = snapshot.Identity.CapturedAt,
            IsComplete = snapshot.Diagnostics.IsComplete,
            UnlockedJobCount = snapshot.Jobs.Count(job => job.IsUnlocked == true),
            ValidGearsetCount = snapshot.Gearsets.Count(gearset => gearset.IsValid),
            InstanceCount = snapshot.Instances.Count,
            CandidateCount = analysis.Candidates.Count(candidate => candidate.Assessment == SquireAssessment.Candidate),
            ProtectedCount = analysis.Candidates.Count(candidate => candidate.Assessment == SquireAssessment.Protected),
            EvaluationFailureCount = analysis.Candidates.Count(candidate => candidate.Assessment == SquireAssessment.EvaluationFailure),
            UnsupportedCount = analysis.Candidates.Count(candidate => candidate.Assessment == SquireAssessment.Unsupported),
            BlockingDiagnostics = snapshot.Diagnostics.Blocking.Select(value => $"{value.Component}:{value.Status}:{value.Message}").ToArray(),
            EvaluationFailureGroups = analysis.Candidates
                .Where(candidate => candidate.Assessment == SquireAssessment.EvaluationFailure)
                .SelectMany(candidate => candidate.Reasons.Select(reason => new { candidate.Definition.Name, reason.Code, reason.Message }))
                .GroupBy(value => new { value.Code, value.Message })
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Key.Code}:{group.Count()}:{group.First().Name}:{group.Key.Message}")
                .ToArray(),
            ProtectionGroups = analysis.Candidates
                .Where(candidate => candidate.Assessment == SquireAssessment.Protected)
                .SelectMany(candidate => candidate.Reasons.Select(reason => new { reason.Code, candidate.Definition.NormalizedRarity }))
                .GroupBy(value => new { value.Code, value.NormalizedRarity })
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Key.Code}:{group.Key.NormalizedRarity}:{group.Count()}")
                .ToArray(),
            ApplicableRuleCount = analysis.Policy.CleanupRules?.Count ?? 0,
            EnabledRuleCount = analysis.Policy.CleanupRules?.Count(rule => rule.Enabled) ?? 0,
            RuleValidationErrors = analysis.Policy.ValidationErrors,
            ExecutableCandidates = analysis.Candidates
                .Where(candidate => candidate.IsExecutable)
                .Select(candidate =>
                {
                    var revalidation = actionAdapter.Revalidate(candidate.Instance.Fingerprint, candidate.RecommendedDisposition);
                    return new AgentBridgeSquireCandidateTruth
                    {
                        ItemId = candidate.Definition.ItemId,
                        ItemName = candidate.Definition.Name,
                        Container = candidate.Instance.Fingerprint.Container,
                        SlotIndex = candidate.Instance.Fingerprint.SlotIndex,
                        EquipLevel = candidate.Definition.EquipLevel,
                        ItemLevel = candidate.Definition.ItemLevel,
                        OwnedCopies = candidate.DuplicateStatus?.OwnedCopies ?? 1,
                        ExplicitMinimumCopies = candidate.DuplicateStatus?.UserMinimumCopies ?? 0,
                        EffectiveMinimumCopies = candidate.DuplicateStatus?.EffectiveMinimumCopies ?? 0,
                        RecommendedDisposition = candidate.RecommendedDisposition.ToString(),
                        ReasonCodes = candidate.Reasons.Select(reason => reason.Code).ToArray(),
                        JobComparisons = candidate.UseAnalysis?.Comparisons
                            .Select(comparison => $"{comparison.Job.Abbreviation}:{comparison.Job.Level}:{comparison.Status}:{comparison.Baseline?.Name ?? "none"}:{comparison.Baseline?.ItemLevel.ToString() ?? "none"}:basis={comparison.Basis}:witnesses={string.Join(",", comparison.WitnessRequirement?.ViableWitnesses.Select(witness => $"{witness.ItemName}@{witness.Fingerprint.Container}:{witness.Fingerprint.SlotIndex}:{(witness.IsGearsetReferenced ? "saved" : "loose")}{(witness.Fingerprint.IsHighQuality ? ":HQ" : string.Empty)}") ?? [])}")
                            .ToArray() ?? [],
                        RevalidationCode = revalidation.Code,
                        RevalidationSucceeded = revalidation.Success,
                        RuleTrace = candidate.RuleEvaluation?.MatchedRules
                            .Select(trace => $"{trace.Priority}:{trace.RuleId}:{trace.RuleName}:decision={trace.Effect.Decision}:route={trace.Effect.PreferredDisposition?.ToString() ?? "none"}:minimum={trace.Effect.MinimumCopies}:authorizations={trace.Effect.Authorizations}:wonDecision={trace.WonDecision}:wonRoute={trace.WonDisposition}")
                            .ToArray() ?? [],
                    };
                })
                .ToArray(),
        };
    }
}
