using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed record SquireBatchValidationResult(
    bool Success,
    string Code,
    string Message,
    IReadOnlyDictionary<EquipmentInstanceFingerprint, EquipmentUseAnalysis> UseAnalyses)
{
    public static SquireBatchValidationResult Fail(string code, string message) => new(false, code, message,
        new Dictionary<EquipmentInstanceFingerprint, EquipmentUseAnalysis>(EquipmentInstanceFingerprintComparer.Instance));
}

public sealed class SquireCounterfactualBatchValidator
{
    private readonly SquireCandidateEvaluator evaluator = new();
    private readonly EquipmentUseAnalyzer useAnalyzer = new();
    private readonly SquireCleanupRuleEngine ruleEngine = new();

    public SquireBatchValidationResult Validate(
        CharacterEquipmentSnapshot snapshot,
        IReadOnlyDictionary<EquipmentInstanceFingerprint, SquireDisposition> removals,
        SquireDispositionCapabilities capabilities,
        SquireProtectionPolicy policy)
    {
        if (!snapshot.Diagnostics.IsComplete)
            return SquireBatchValidationResult.Fail("PartialSnapshot", "A complete equipment snapshot is required for counterfactual validation.");
        if (removals.Count == 0)
            return SquireBatchValidationResult.Fail("EmptyBatch", "At least one removal is required.");

        var current = evaluator.Evaluate(snapshot, capabilities, policy).Candidates
            .ToDictionary(candidate => candidate.Instance.Fingerprint, EquipmentInstanceFingerprintComparer.Instance);
        foreach (var removal in removals)
        {
            if (!current.TryGetValue(removal.Key, out var candidate) || !candidate.IsExecutable)
                return SquireBatchValidationResult.Fail("CandidateNoLongerExecutable", $"{candidate?.Definition.Name ?? "A selected item"} is no longer an executable cleanup candidate.");
            if (!candidate.SupportedDispositions.Contains(removal.Value))
                return SquireBatchValidationResult.Fail("DispositionUnavailable", $"{candidate.Definition.Name} no longer supports {removal.Value}.");
        }

        var removed = removals.Keys.ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
        var retained = snapshot.Instances.Where(instance => !removed.Contains(instance.Fingerprint)).ToArray();
        if (!GearsetProtectionIndex.Create(snapshot.Gearsets).DoesNotReduceRequiredMultiplicity(snapshot.Instances, retained))
            return SquireBatchValidationResult.Fail(
                "GearsetMultiplicityLost",
                "The selected batch would remove item-ID and quality multiplicity required by a valid saved gearset. Squire does not yet replace saved gearset assignments with better owned equipment.");
        if (!SquireDuplicateRetention.DoesNotReduceRequiredMultiplicity(snapshot.Instances, retained, current.Values, out var duplicateMessage))
            return SquireBatchValidationResult.Fail("DuplicateRetentionFloorLost", duplicateMessage);
        var rules = policy.CleanupRules ?? SquireLegacyCleanupRuleAdapter.Create(policy);
        var dynamicRetention = rules.Any(rule =>
            rule.Enabled &&
            rule.Effect.MinimumCopies > 0 &&
            (rule.Condition.UseStatuses is { Count: > 0 } || rule.Condition.HasFutureLevelingUse is not null));
        var projectedUses = new Dictionary<EquipmentInstanceFingerprint, EquipmentUseAnalysis>(EquipmentInstanceFingerprintComparer.Instance);
        var projectedRules = new Dictionary<EquipmentInstanceFingerprint, SquireCleanupRuleEvaluation>(EquipmentInstanceFingerprintComparer.Instance);
        if (dynamicRetention)
        {
            var projectedCandidates = new List<SquireCandidate>(current.Count);
            foreach (var candidate in current.Values)
            {
                var projectedUse = useAnalyzer.Analyze(candidate.Instance, candidate.Definition, snapshot.Jobs, snapshot.Gearsets, retained, snapshot.Definitions);
                var eligibility = new SquireDispositionEligibilityEvaluator().Evaluate(candidate.Definition, capabilities);
                var projectedRule = ruleEngine.Evaluate(
                    SquireCandidateEvaluator.CreateRuleContext(
                        policy.CharacterContentId,
                        candidate.Instance,
                        candidate.Definition,
                        projectedUse,
                        eligibility.SupportedDispositions),
                    rules);
                if (!projectedRule.IsValid)
                    return SquireBatchValidationResult.Fail("InvalidRuleConfiguration", string.Join(" ", projectedRule.Errors));
                projectedUses[candidate.Instance.Fingerprint] = projectedUse;
                projectedRules[candidate.Instance.Fingerprint] = projectedRule;
                projectedCandidates.Add(candidate with
                {
                    DuplicateStatus = new SquireDuplicateStatus(
                        candidate.DuplicateStatus?.OwnedCopies ?? 1,
                        projectedRule.MinimumCopies,
                        candidate.DuplicateStatus?.GearsetRequiredCopies ?? 0),
                });
            }
            if (!SquireDuplicateRetention.DoesNotReduceRequiredMultiplicity(snapshot.Instances, retained, projectedCandidates, out duplicateMessage))
                return SquireBatchValidationResult.Fail("DynamicDuplicateRetentionFloorLost", duplicateMessage);
        }
        var analyses = new Dictionary<EquipmentInstanceFingerprint, EquipmentUseAnalysis>(EquipmentInstanceFingerprintComparer.Instance);
        foreach (var removal in removals)
        {
            var candidate = current[removal.Key];
            var use = projectedUses.GetValueOrDefault(candidate.Instance.Fingerprint) ??
                      useAnalyzer.Analyze(candidate.Instance, candidate.Definition, snapshot.Jobs, snapshot.Gearsets, retained, snapshot.Definitions);
            if (use.IsEvaluationFailure)
                return SquireBatchValidationResult.Fail(use.FailureCode ?? "CounterfactualEvaluationFailure", use.Diagnostic ?? $"Could not validate removal of {candidate.Definition.Name}.");
            var safe = use.Status is EquipmentUseStatus.NoObtainedEligibleJob or
                           EquipmentUseStatus.LikelyCosmetic or EquipmentUseStatus.SpecialPurpose ||
                       use.IsStrictlyObsolete ||
                       use.Comparisons.Count > 0 &&
                       use.Comparisons.All(comparison => comparison.Status is EquipmentUseStatus.Obsolete or EquipmentUseStatus.FutureUse);
            if (!safe)
                return SquireBatchValidationResult.Fail("RetainedLoadoutInsufficient", $"Removing the selected batch leaves no retained loadout that covers {candidate.Definition.Name} without relevant-stat loss.");
            var eligibility = new SquireDispositionEligibilityEvaluator().Evaluate(candidate.Definition, capabilities);
            var ruleEvaluation = projectedRules.GetValueOrDefault(candidate.Instance.Fingerprint) ??
                                 ruleEngine.Evaluate(
                                     SquireCandidateEvaluator.CreateRuleContext(
                                         policy.CharacterContentId,
                                         candidate.Instance,
                                         candidate.Definition,
                                         use,
                                         eligibility.SupportedDispositions),
                                     rules);
            if (!ruleEvaluation.IsValid)
                return SquireBatchValidationResult.Fail("InvalidRuleConfiguration", string.Join(" ", ruleEvaluation.Errors));
            if (ruleEvaluation.Decision == SquireCleanupDecision.Protect)
                return SquireBatchValidationResult.Fail("CleanupRuleProtected", $"Current cleanup rules protect {candidate.Definition.Name} in the retained-loadout counterfactual.");
            if (ruleEvaluation.PreferredDisposition != removal.Value)
                return SquireBatchValidationResult.Fail("DispositionPolicyChanged", $"Current cleanup rules select {ruleEvaluation.PreferredDisposition?.ToString() ?? "no route"} for {candidate.Definition.Name}, not {removal.Value}.");
            analyses[removal.Key] = use;
        }

        return new(true, "Valid", "The complete selected batch is covered by retained counterfactual loadouts.", analyses);
    }
}
