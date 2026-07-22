using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed class SquireCandidateEvaluator
{
    private static readonly IReadOnlySet<SquireDisposition> EmptyDispositions = new HashSet<SquireDisposition>();
    private readonly EquipmentUseAnalyzer useAnalyzer = new();
    private readonly SquireDispositionEligibilityEvaluator dispositionEligibility = new();
    private readonly SquireCleanupRuleEngine ruleEngine = new();

    public SquireAnalysis Evaluate(
        CharacterEquipmentSnapshot snapshot,
        SquireDispositionCapabilities? capabilities = null,
        SquireProtectionPolicy? protectionPolicy = null)
    {
        capabilities ??= new SquireDispositionCapabilities(null);
        protectionPolicy ??= new SquireProtectionPolicy();
        var effectiveRules = protectionPolicy.CleanupRules ?? SquireLegacyCleanupRuleAdapter.Create(protectionPolicy);
        var effectivePolicy = protectionPolicy with { CleanupRules = effectiveRules };
        var gearsetProtection = GearsetProtectionIndex.Create(snapshot.Gearsets);
        var candidates = snapshot.Instances
            .Select(instance => EvaluateInstance(snapshot, instance, gearsetProtection, capabilities, effectivePolicy))
            .ToArray();
        return new SquireAnalysis(snapshot, candidates, effectivePolicy);
    }

    private SquireCandidate EvaluateInstance(
        CharacterEquipmentSnapshot snapshot,
        EquipmentInstanceSnapshot instance,
        GearsetProtectionIndex gearsetProtection,
        SquireDispositionCapabilities capabilities,
        SquireProtectionPolicy policy)
    {
        if (!snapshot.Definitions.TryGetValue(instance.Fingerprint.ItemId, out var definition))
            return Unsupported(instance, UnknownDefinition(instance.Fingerprint.ItemId));

        var hardProtections = GetHardProtections(snapshot, instance, definition, gearsetProtection, capabilities);
        var use = useAnalyzer.Analyze(instance, definition, snapshot.Jobs, snapshot.Gearsets, snapshot.Instances, snapshot.Definitions);
        var eligibility = dispositionEligibility.Evaluate(definition, capabilities);
        var ruleContext = CreateRuleContext(policy.CharacterContentId, instance, definition, use, eligibility.SupportedDispositions);
        var ruleEvaluation = ruleEngine.Evaluate(ruleContext, policy.CleanupRules ?? []);
        if (!ruleEvaluation.IsValid)
        {
            return Candidate(
                instance,
                definition,
                SquireAssessment.EvaluationFailure,
                SquireDisposition.Keep,
                EmptyDispositions,
                ruleEvaluation.Errors.Select(error => new SquireReason(
                    "InvalidRuleConfiguration",
                    error,
                    SquireReasonSeverity.Blocking)).ToArray(),
                use,
                ruleEvaluation: ruleEvaluation);
        }

        var exactQualityCount = snapshot.Instances.Count(value =>
            value.Fingerprint.ItemId == definition.ItemId &&
            value.Fingerprint.IsHighQuality == instance.Fingerprint.IsHighQuality);
        var gearsetRequired = gearsetProtection.RequiredCount(definition.ItemId, instance.Fingerprint.IsHighQuality);
        var duplicateStatus = new SquireDuplicateStatus(exactQualityCount, ruleEvaluation.MinimumCopies, gearsetRequired);
        var reasons = new List<SquireReason>(hardProtections);
        if (ruleEvaluation.Decision == SquireCleanupDecision.Protect)
        {
            reasons.AddRange(ruleEvaluation.MatchedRules
                .Where(rule => rule.WonDecision)
                .Select(ProtectionReason));
        }
        if (exactQualityCount <= duplicateStatus.EffectiveMinimumCopies && duplicateStatus.EffectiveMinimumCopies > 0)
        {
            reasons.Add(new(
                "DuplicateRetentionFloor",
                $"Matching rules and saved gearsets retain at least {duplicateStatus.EffectiveMinimumCopies} copies of this quality; {exactQualityCount} are owned.",
                SquireReasonSeverity.Blocking));
        }
        if (reasons.Any(reason => reason.Severity == SquireReasonSeverity.Blocking))
        {
            return Candidate(
                instance,
                definition,
                SquireAssessment.Protected,
                SquireDisposition.Keep,
                EmptyDispositions,
                reasons,
                use,
                duplicateStatus,
                ruleEvaluation);
        }

        if (use.Status == EquipmentUseStatus.EvaluationFailure)
        {
            return Candidate(
                instance,
                definition,
                SquireAssessment.EvaluationFailure,
                SquireDisposition.Keep,
                EmptyDispositions,
                [UseReason(use)],
                use,
                duplicateStatus,
                ruleEvaluation);
        }

        var semanticallyEligible = use.IsStrictlyObsolete ||
                                   use.Status is EquipmentUseStatus.NoObtainedEligibleJob or
                                       EquipmentUseStatus.LikelyCosmetic or EquipmentUseStatus.SpecialPurpose ||
                                   use.Comparisons.Count > 0 && use.Comparisons.All(comparison =>
                                       comparison.Status is EquipmentUseStatus.Obsolete or EquipmentUseStatus.FutureUse);
        if (!semanticallyEligible)
        {
            return Candidate(
                instance,
                definition,
                SquireAssessment.Protected,
                SquireDisposition.Keep,
                EmptyDispositions,
                [UseReason(use)],
                use,
                duplicateStatus,
                ruleEvaluation);
        }

        if (eligibility.SupportedDispositions.Count == 0)
        {
            return Candidate(
                instance,
                definition,
                SquireAssessment.Unsupported,
                SquireDisposition.Unsupported,
                EmptyDispositions,
                [new("NoSupportedDisposition", "No safe cleanup disposition can be proven for this item.", SquireReasonSeverity.Blocking)],
                use,
                duplicateStatus,
                ruleEvaluation);
        }
        if (ruleEvaluation.PreferredDisposition is not { } recommended)
        {
            return Candidate(
                instance,
                definition,
                SquireAssessment.EvaluationFailure,
                SquireDisposition.Keep,
                EmptyDispositions,
                [new("NoDispositionRuleMatched", "No enabled cleanup rule selected a supported disposition for this item.", SquireReasonSeverity.Blocking)],
                use,
                duplicateStatus,
                ruleEvaluation);
        }

        reasons.Add(SemanticCleanupReason(use));
        if (ruleEvaluation.Decision == SquireCleanupDecision.AllowCleanup)
        {
            reasons.AddRange(ruleEvaluation.MatchedRules
                .Where(rule => rule.WonDecision)
                .Select(rule => new SquireReason(
                    "CleanupRuleAuthorized",
                    $"Rule '{rule.RuleName}' ({rule.RuleId}) authorizes cleanup at priority {rule.Priority}.",
                    SquireReasonSeverity.Information)));
        }
        if (duplicateStatus.UserMinimumCopies > 0)
        {
            reasons.Add(new(
                "DuplicateRetentionSurplus",
                $"Rules retain {duplicateStatus.UserMinimumCopies} copies of this quality; {exactQualityCount} are owned, leaving {Math.Max(0, exactQualityCount - duplicateStatus.EffectiveMinimumCopies)} removable.",
                SquireReasonSeverity.Information));
        }
        reasons.Add(new(
            "CleanupRuleDisposition",
            DescribeWinningDispositionRule(ruleEvaluation, recommended),
            SquireReasonSeverity.Information));
        if (definition.NormalizedRarity is EquipmentRarity.Rare or EquipmentRarity.Relic &&
            policy.CleanupRules?.Any(rule => rule.Id == "builtin.protect-high-rarity" && !rule.Enabled) == true)
        {
            reasons.Add(new(
                "HighRarityProtectionDisabled",
                "The built-in blue and purple gear protection rule is disabled; all hard safeguards still apply.",
                SquireReasonSeverity.Warning));
        }
        reasons.AddRange(eligibility.Reasons);
        return Candidate(
            instance,
            definition,
            SquireAssessment.Candidate,
            recommended,
            eligibility.SupportedDispositions,
            reasons,
            use,
            duplicateStatus,
            ruleEvaluation);
    }

    internal static SquireCleanupRuleContext CreateRuleContext(
        ulong characterContentId,
        EquipmentInstanceSnapshot instance,
        EquipmentItemDefinition definition,
        EquipmentUseAnalysis use,
        IReadOnlySet<SquireDisposition> supportedDispositions) => new(
        characterContentId,
        definition.ItemId,
        instance.Fingerprint.IsHighQuality,
        definition.NormalizedRarity,
        use.Status,
        definition.IsEquipment,
        instance.Fingerprint.CrafterContentId is > 0,
        definition.IsArmoireEligible,
        instance.Fingerprint.MateriaIds.Count > 0,
        use.Status == EquipmentUseStatus.FutureUse ||
        use.Comparisons.Any(comparison => comparison.Status == EquipmentUseStatus.FutureUse),
        checked((int)definition.EquipLevel),
        supportedDispositions);

    private static string DescribeWinningDispositionRule(
        SquireCleanupRuleEvaluation evaluation,
        SquireDisposition disposition)
    {
        var winners = evaluation.MatchedRules.Where(rule => rule.WonDisposition).ToArray();
        return winners.Length == 0
            ? $"Cleanup route is {disposition}."
            : $"Rule {string.Join(", ", winners.Select(rule => $"'{rule.RuleName}' ({rule.RuleId})"))} selects {disposition}.";
    }

    private static SquireReason ProtectionReason(SquireCleanupRuleTrace rule)
    {
        var code = rule.RuleId switch
        {
            "builtin.protect-high-rarity" => "HighRarityEquipment",
            "builtin.protect-player-signed" => "PlayerSignature",
            "builtin.protect-future-leveling" => "FutureUnlockedJobUse",
            "builtin.protect-armoire" => "ArmoireEligible",
            "builtin.protect-materia-risk" => "MateriaRetrievalRiskNotAuthorized",
            "builtin.protect-cosmetic" => "StatlessAllClassesEquipment",
            "builtin.protect-special-purpose" => "SpecialPurposeEquipment",
            _ when rule.RuleId.StartsWith("legacy.", StringComparison.Ordinal) => "ItemProtectionRule",
            _ => "CleanupRuleProtected",
        };
        return new SquireReason(
            code,
            $"Rule '{rule.RuleName}' ({rule.RuleId}) protects this item at priority {rule.Priority}.",
            SquireReasonSeverity.Blocking);
    }

    private static SquireReason SemanticCleanupReason(EquipmentUseAnalysis use)
    {
        if (use.Status == EquipmentUseStatus.NoObtainedEligibleJob)
            return new("NoObtainedEligibleJob", "No job obtained by this character can use this item.", SquireReasonSeverity.Information);
        if (use.Status == EquipmentUseStatus.LikelyCosmetic)
            return new("CosmeticCleanupAuthorized", "The item appears cosmetic and policy permits cleanup.", SquireReasonSeverity.Warning);
        if (use.Status == EquipmentUseStatus.SpecialPurpose)
            return new("SpecialPurposeCleanupAuthorized", use.Diagnostic ?? "Policy permits cleanup of this special-purpose item.", SquireReasonSeverity.Warning);
        if (use.Comparisons.Any(comparison => comparison.Status == EquipmentUseStatus.FutureUse))
            return new("FutureLevelingUseNotProtected", "One or more unlocked jobs are below this item's equip level; matching policy permits cleanup.", SquireReasonSeverity.Information);
        return new("RetainedCoverageForAllUnlockedJobs", DescribeTrustedBaselines(use), SquireReasonSeverity.Information);
    }

    private static string DescribeTrustedBaselines(EquipmentUseAnalysis use)
    {
        var comparisons = use.Comparisons
            .Where(comparison => comparison.Status == EquipmentUseStatus.Obsolete && comparison.Baseline is not null)
            .Select(comparison =>
            {
                var baseline = comparison.Baseline!;
                var witness = comparison.WitnessRequirement?.ViableWitnesses
                    .FirstOrDefault(value => value.ItemId == baseline.ItemId);
                var location = witness is null
                    ? "saved gearset"
                    : $"{witness.Fingerprint.Container} slot {witness.Fingerprint.SlotIndex}, {(witness.Fingerprint.IsHighQuality ? "HQ" : "NQ")}";
                return $"{comparison.Job.Abbreviation}: {baseline.Name} (iLvl {baseline.ItemLevel}, {location})";
            })
            .ToArray();
        return comparisons.Length == 0
            ? "Every unlocked eligible job has a trusted baseline that covers this item without relevant-stat loss."
            : $"Trusted retained coverage: {string.Join("; ", comparisons)}.";
    }

    private static List<SquireReason> GetHardProtections(
        CharacterEquipmentSnapshot snapshot,
        EquipmentInstanceSnapshot instance,
        EquipmentItemDefinition definition,
        GearsetProtectionIndex gearsetProtection,
        SquireDispositionCapabilities capabilities)
    {
        var reasons = new List<SquireReason>();
        if (!snapshot.Diagnostics.IsComplete)
            reasons.Add(new("PartialSnapshot", "The equipment snapshot is incomplete.", SquireReasonSeverity.Blocking));
        if (instance.IsEquipped)
            reasons.Add(new("CurrentlyEquipped", "This exact item is currently equipped.", SquireReasonSeverity.Blocking));
        var exactQualityCount = snapshot.Instances.Count(value =>
            value.Fingerprint.ItemId == definition.ItemId &&
            value.Fingerprint.IsHighQuality == instance.Fingerprint.IsHighQuality);
        if (gearsetProtection.IsProtected(definition.ItemId, instance.Fingerprint.IsHighQuality, exactQualityCount))
            reasons.Add(new(
                "ReferencedByGearset",
                "The current item-ID and quality multiplicity is required by an existing valid saved gearset. Squire does not yet replace saved gearset assignments with better owned equipment.",
                SquireReasonSeverity.Blocking));
        if (!definition.IsEquipment)
            reasons.Add(new("NotEquipment", "The item is not equipment.", SquireReasonSeverity.Blocking));
        if (definition.IsSoulCrystal || definition.Slot == EquipmentSlot.SoulCrystal)
            reasons.Add(new("SoulCrystal", "Soul crystals are always protected.", SquireReasonSeverity.Blocking));
        if (definition.IsExplicitlyProtectedFamily)
            reasons.Add(new("ProtectedItemFamily", "The item belongs to a non-configurable protected family.", SquireReasonSeverity.Blocking));
        if (definition.NormalizedRarity == EquipmentRarity.Unknown)
            reasons.Add(new("UnknownItemRarity", $"Item rarity value {definition.Rarity} is not mapped.", SquireReasonSeverity.Blocking));
        else if (definition.NormalizedRarity == EquipmentRarity.Uncommon &&
                 definition.ExpertDeliveryEligibility == ExpertDeliveryEligibility.Unknown)
            reasons.Add(new("ExpertDeliveryEligibilityUnknown", "Expert Delivery eligibility is unknown for this uncommon item.", SquireReasonSeverity.Blocking));
        if (definition.IsEquipment && instance.Fingerprint.MateriaIds.Count > 0)
        {
            if (capabilities.MateriaRetrievalUnlocked != true)
            {
                reasons.Add(new(
                    capabilities.MateriaRetrievalUnlocked == false ? "MateriaRetrievalNotUnlocked" : "MateriaRetrievalUnlockUnknown",
                    capabilities.MateriaRetrievalUnlocked == false
                        ? "Attached materia cannot be handled until Forging the Spirit is complete."
                        : "The Forging the Spirit completion required for materia retrieval could not be proven.",
                    SquireReasonSeverity.Blocking));
            }
            else
            {
                reasons.Add(new(
                    "MateriaRetrievalRequired",
                    $"Squire will attempt to retrieve {instance.Fingerprint.MateriaIds.Count} attached materia before cleanup; failed retrieval can destroy materia.",
                    SquireReasonSeverity.Warning));
            }
        }
        if (definition.IsArmoireEligible is null)
            reasons.Add(new("ArmoireEligibilityUnknown", "Armoire eligibility is unknown.", SquireReasonSeverity.Blocking));
        if (definition.IsRecoverable is null)
            reasons.Add(new("RecoverabilityUnknown", "Recoverability is unknown.", SquireReasonSeverity.Blocking));
        return reasons;
    }

    private static SquireReason UseReason(EquipmentUseAnalysis analysis) => analysis.Status switch
    {
        EquipmentUseStatus.FutureUse => new("FutureUnlockedJobUse", "A lower-level unlocked job could grow into this item.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.BaselineNotBetter => new("NoRetainedCoverage", "No retained owned or saved baseline safely supersedes this item for every relevant obtained job.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.NoObtainedEligibleJob => new("NoObtainedEligibleJob", "No job obtained by this character can use this item.", SquireReasonSeverity.Information),
        EquipmentUseStatus.LikelyCosmetic => new("StatlessAllClassesEquipment", "All Classes equipment has no wearer-defining stats and is likely cosmetic or appearance gear.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.SpecialPurpose => new("SpecialPurposeEquipment", analysis.Diagnostic ?? "Special-purpose equipment is protected.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.EvaluationFailure => new(analysis.FailureCode ?? "EquipmentEvaluationFailure", analysis.Diagnostic ?? "Equipment use evaluation failed.", SquireReasonSeverity.Blocking),
        _ => new("EquipmentUseUnknown", "Equipment use could not be classified safely.", SquireReasonSeverity.Blocking),
    };

    private static SquireCandidate Unsupported(EquipmentInstanceSnapshot instance, SquireReason reason) =>
        Candidate(
            instance,
            new EquipmentItemDefinition(instance.Fingerprint.ItemId, $"Item {instance.Fingerprint.ItemId}", 0, 0, EquipmentSlot.Unknown, new HashSet<uint>(), 0, false, false, null, null, null, null, null, null, false),
            SquireAssessment.Unsupported,
            SquireDisposition.Unsupported,
            EmptyDispositions,
            [reason],
            null);

    private static SquireReason UnknownDefinition(uint itemId) =>
        new("ItemDefinitionMissing", $"Item definition {itemId} is unavailable.", SquireReasonSeverity.Blocking);

    private static SquireCandidate Candidate(
        EquipmentInstanceSnapshot instance,
        EquipmentItemDefinition definition,
        SquireAssessment assessment,
        SquireDisposition recommendation,
        IReadOnlySet<SquireDisposition> supported,
        IReadOnlyList<SquireReason> reasons,
        EquipmentUseAnalysis? use,
        SquireDuplicateStatus? duplicateStatus = null,
        SquireCleanupRuleEvaluation? ruleEvaluation = null) =>
        new(instance, definition, assessment, recommendation, supported, reasons, use, duplicateStatus, ruleEvaluation);
}
