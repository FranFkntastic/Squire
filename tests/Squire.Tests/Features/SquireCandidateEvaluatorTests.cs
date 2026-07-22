using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;
using MarketMafioso.Windows.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireCandidateEvaluatorTests
{
    private static readonly CharacterScope Scope = new(7, "Squire", 21);
    private static readonly SquireDispositionCapabilities DesynthesisUnlocked = new(true, true);
    private readonly SquireCandidateEvaluator evaluator = new();

    [Fact]
    public void CompleteStrictlyWorseItem_BecomesExecutableCandidate()
    {
        var snapshot = Snapshot(
            instances: [Instance(100), Instance(200, equipped: true, slot: 99)],
            definitions: [Definition(100, 20), Definition(200, 30)],
            jobs: [Job(1, 50, true)],
            gearsets: [Gearset(1, 200)]);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates, value => value.Definition.ItemId == 100);
        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.Equal(SquireDisposition.Desynthesize, candidate.RecommendedDisposition);
        Assert.Contains(SquireDisposition.VendorSell, candidate.SupportedDispositions);
    }

    [Fact]
    public void PartialSnapshot_ProducesNoExecutableCandidate()
    {
        var snapshot = Snapshot(
            [Instance(100)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)], complete: false);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);
        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "PartialSnapshot");
    }

    [Fact]
    public void EquippedGearsetAndMateriaItems_ReportRetrievalRiskProtection()
    {
        var instance = Instance(100, equipped: true, materia: [500], crafter: 99);
        var snapshot = Snapshot([instance], [Definition(100, 20)], [Job(1, 50, true)], [Gearset(1, 100)]);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);
        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "CurrentlyEquipped");
        Assert.Contains(candidate.Reasons, reason => reason.Code == "ReferencedByGearset");
        Assert.Contains(candidate.Reasons, reason =>
            reason.Code == "ReferencedByGearset" &&
            reason.Message.Contains("does not yet replace saved gearset assignments", StringComparison.Ordinal));
        Assert.Contains(candidate.Reasons, reason => reason.Code == "MateriaRetrievalRiskNotAuthorized" && reason.Severity == SquireReasonSeverity.Blocking);
        Assert.DoesNotContain(candidate.Reasons, reason => reason.Code == "PlayerSignature");
    }

    [Fact]
    public void SignedGear_IsNotProtectedByDefault()
    {
        var snapshot = Snapshot(
            [Instance(100, crafter: 99), Instance(200, equipped: true, slot: 99)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            [Gearset(1, 200)]);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates, value => value.Definition.ItemId == 100);

        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.DoesNotContain(candidate.Reasons, reason => reason.Code == "PlayerSignature");
    }

    [Fact]
    public void SignedGear_CanBeProtectedByOptInPolicy()
    {
        var snapshot = Snapshot(
            [Instance(100, crafter: 99)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            [Gearset(1, 200)]);

        var candidate = Assert.Single(evaluator.Evaluate(
            snapshot,
            DesynthesisUnlocked,
            new SquireProtectionPolicy(ProtectSignedGear: true)).Candidates);

        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "PlayerSignature");
    }

    [Fact]
    public void ArmoireEligibleItem_IsProtectedByDefault()
    {
        var armoireItem = Definition(100, 20) with { IsArmoireEligible = true };
        var snapshot = Snapshot([Instance(100)], [armoireItem, Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);
        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "ArmoireEligible");
    }

    [Fact]
    public void UnsupportedDispositionCannotEnterPlan()
    {
        var snapshot = Snapshot([Instance(100)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]);
        var analysis = evaluator.Evaluate(snapshot, DesynthesisUnlocked);
        Assert.Throws<InvalidOperationException>(() => new SquireActionPlanner().Create(
            analysis, SquireDisposition.Unsupported, [analysis.Candidates[0].Instance.Fingerprint], DateTimeOffset.UtcNow,
            new SquireProtectionPolicy(), DesynthesisUnlocked));
    }

    [Fact]
    public void RefreshInvalidatesPriorSelectionEvenWhenItemIdMatches()
    {
        var first = evaluator.Evaluate(Snapshot([Instance(100, slot: 1), Instance(200, equipped: true, slot: 99)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]), DesynthesisUnlocked);
        var second = evaluator.Evaluate(Snapshot([Instance(100, slot: 2), Instance(200, equipped: true, slot: 99)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]), DesynthesisUnlocked);
        var review = new SquireReviewState();
        review.Adopt(first);
        var firstCandidate = first.Candidates.Single(value => value.Definition.ItemId == 100);
        Assert.True(review.TrySelect(first, firstCandidate.Instance.Fingerprint, SquireDisposition.Desynthesize));
        review.Adopt(second);
        Assert.Empty(review.Selections);
        Assert.False(review.TrySelect(second, firstCandidate.Instance.Fingerprint, SquireDisposition.Desynthesize));
    }

    [Fact]
    public void ReconcileRefreshPreservesAnExactStillExecutableSelection()
    {
        var first = evaluator.Evaluate(Snapshot([Instance(100, slot: 1), Instance(200, equipped: true, slot: 99)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]), DesynthesisUnlocked);
        var second = evaluator.Evaluate(Snapshot([Instance(100, slot: 1), Instance(200, equipped: true, slot: 99)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]), DesynthesisUnlocked);
        var review = new SquireReviewState();
        review.Adopt(first);
        var candidate = first.Candidates.Single(value => value.Definition.ItemId == 100);
        Assert.True(review.TrySelect(first, candidate.Instance.Fingerprint, candidate.RecommendedDisposition));

        var result = review.Reconcile(second);

        Assert.Equal(1, result.PreservedCount);
        Assert.Empty(result.RemovedReasons);
        Assert.Single(review.Selections);
        Assert.Equal(second.Snapshot.GenerationId, review.GenerationId);
    }

    [Fact]
    public void ReconcileRefreshRemovesAnItemThatMovedSlots()
    {
        var first = evaluator.Evaluate(Snapshot([Instance(100, slot: 1), Instance(200, equipped: true, slot: 99)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]), DesynthesisUnlocked);
        var second = evaluator.Evaluate(Snapshot([Instance(100, slot: 2), Instance(200, equipped: true, slot: 99)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]), DesynthesisUnlocked);
        var review = new SquireReviewState();
        review.Adopt(first);
        var candidate = first.Candidates.Single(value => value.Definition.ItemId == 100);
        Assert.True(review.TrySelect(first, candidate.Instance.Fingerprint, candidate.RecommendedDisposition));

        var result = review.Reconcile(second);

        Assert.Equal(0, result.PreservedCount);
        Assert.Single(result.RemovedReasons);
        Assert.Empty(review.Selections);
    }

    [Fact]
    public void PartialSnapshotCannotProduceActionPlan()
    {
        var analysis = evaluator.Evaluate(Snapshot(
            [Instance(100)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)], complete: false), DesynthesisUnlocked);
        Assert.Throws<InvalidOperationException>(() => new SquireActionPlanner().Create(
            analysis, SquireDisposition.Desynthesize, [analysis.Candidates[0].Instance.Fingerprint], DateTimeOffset.UtcNow,
            new SquireProtectionPolicy(), DesynthesisUnlocked));
    }

    [Fact]
    public void GrandCompanyDeliveryIsNotAPlanningDisposition()
    {
        Assert.DoesNotContain(Enum.GetNames<SquireDisposition>(), name => name.Contains("Grand", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Enum.GetNames<SquireDisposition>(), name => name.Contains("Company", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SurplusGearsetDuplicate_IsIndividuallyEligibleButBatchCannotRemoveRequiredMultiplicity()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(100, slot: 2)],
            [Definition(100, 20)],
            [Job(1, 50, true)],
            [Gearset(1, 100)]);
        var analysis = evaluator.Evaluate(snapshot, DesynthesisUnlocked);
        Assert.All(analysis.Candidates, candidate => Assert.Equal(SquireAssessment.Candidate, candidate.Assessment));
        var removals = analysis.Candidates.ToDictionary(
            candidate => candidate.Instance.Fingerprint,
            _ => SquireDisposition.Desynthesize,
            EquipmentInstanceFingerprintComparer.Instance);
        var result = new SquireCounterfactualBatchValidator().Validate(snapshot, removals, DesynthesisUnlocked, new SquireProtectionPolicy());
        Assert.False(result.Success);
        Assert.Equal("GearsetMultiplicityLost", result.Code);
    }

    [Fact]
    public void ExplicitDuplicateMinimum_ProtectsCopiesAtTheConfiguredFloor()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(100, slot: 2), Instance(200, slot: 3)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            []);
        var policy = new SquireProtectionPolicy(Rules: [RetentionRule(100, false, 2)]);

        var copies = evaluator.Evaluate(snapshot, DesynthesisUnlocked, policy).Candidates
            .Where(candidate => candidate.Definition.ItemId == 100)
            .ToArray();

        Assert.Equal(2, copies.Length);
        Assert.All(copies, candidate =>
        {
            Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
            Assert.Contains(candidate.Reasons, reason => reason.Code == "DuplicateRetentionFloor");
            Assert.Equal(new SquireDuplicateStatus(2, 2, 0), candidate.DuplicateStatus);
        });
    }

    [Fact]
    public void ExplicitDuplicateMinimum_AllowsOnlyCopiesAboveTheFloorInOneBatch()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(100, slot: 2), Instance(100, slot: 3), Instance(200, slot: 4)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            []);
        var policy = new SquireProtectionPolicy(Rules: [RetentionRule(100, false, 2)]);
        var analysis = evaluator.Evaluate(snapshot, DesynthesisUnlocked, policy);
        var copies = analysis.Candidates.Where(candidate => candidate.Definition.ItemId == 100).ToArray();
        Assert.All(copies, candidate =>
        {
            Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
            Assert.Contains(candidate.Reasons, reason => reason.Code == "DuplicateRetentionSurplus");
            Assert.Equal(1, candidate.DuplicateStatus!.CopiesAboveFloor);
        });

        var oneRemoval = new Dictionary<EquipmentInstanceFingerprint, SquireDisposition>(EquipmentInstanceFingerprintComparer.Instance)
        {
            [copies[0].Instance.Fingerprint] = SquireDisposition.Desynthesize,
        };
        Assert.True(new SquireCounterfactualBatchValidator().Validate(snapshot, oneRemoval, DesynthesisUnlocked, policy).Success);

        var twoRemovals = copies.Take(2).ToDictionary(
            candidate => candidate.Instance.Fingerprint,
            _ => SquireDisposition.Desynthesize,
            EquipmentInstanceFingerprintComparer.Instance);
        var rejected = new SquireCounterfactualBatchValidator().Validate(snapshot, twoRemovals, DesynthesisUnlocked, policy);
        Assert.False(rejected.Success);
        Assert.Equal("DuplicateRetentionFloorLost", rejected.Code);
        Assert.Contains("retains at least 2", rejected.Message);
    }

    [Fact]
    public void ExplicitDuplicateMinimum_IsQualityAware()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1, highQuality: true), Instance(100, slot: 2, highQuality: true), Instance(100, slot: 3), Instance(200, slot: 4)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            []);
        var policy = new SquireProtectionPolicy(Rules: [RetentionRule(100, true, 2)]);

        var copies = evaluator.Evaluate(snapshot, DesynthesisUnlocked, policy).Candidates
            .Where(candidate => candidate.Definition.ItemId == 100)
            .ToArray();

        Assert.All(copies.Where(candidate => candidate.Instance.Fingerprint.IsHighQuality), candidate =>
            Assert.Contains(candidate.Reasons, reason => reason.Code == "DuplicateRetentionFloor"));
        Assert.Equal(SquireAssessment.Candidate, Assert.Single(copies, candidate => !candidate.Instance.Fingerprint.IsHighQuality).Assessment);
    }

    [Fact]
    public void DuplicateRetentionMerge_UsesTheConservativeMinimumPerQuality()
    {
        var merged = SquireDuplicateRetention.Merge(
            [RetentionRule(100, false, 1), RetentionRule(100, true, 4)],
            [RetentionRule(100, false, 3), RetentionRule(100, true, 2)]);

        Assert.Equal(3, Assert.Single(merged, rule => rule is { ItemId: 100, Quality: SquireRuleQuality.NormalQuality }).MinimumCopies);
        Assert.Equal(4, Assert.Single(merged, rule => rule is { ItemId: 100, Quality: SquireRuleQuality.HighQuality }).MinimumCopies);
    }

    [Fact]
    public void RuleMerge_PreservesInvalidEnabledRulesForExplicitRevalidationFailure()
    {
        var invalid = new SquireRule(Guid.Empty, SquireRuleKind.RetainCopies, 0,
            SquireRuleQuality.Any, 0, true, "Corrupt rule");

        var merged = SquireDuplicateRetention.Merge([invalid], []);

        Assert.Equal(invalid, Assert.Single(merged));
        Assert.NotEmpty(new SquireProtectionPolicy(Rules: merged).ValidationErrors);
    }

    [Fact]
    public void DuplicateRetentionFloor_DoesNotRequireRepairingAPreexistingDeficit()
    {
        var before = new[] { Instance(100, slot: 1), Instance(100, slot: 2) };
        var policy = new SquireProtectionPolicy(Rules: [RetentionRule(100, false, 3)]);

        Assert.True(SquireDuplicateRetention.DoesNotReduceRequiredMultiplicity(before, before, policy, out _));
        Assert.False(SquireDuplicateRetention.DoesNotReduceRequiredMultiplicity(before, before.Take(1), policy, out var message));
        Assert.Contains("retains at least 2", message);
    }

    [Fact]
    public void CounterfactualBatch_ReevaluatesDynamicRetentionRules()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(100, slot: 2), Instance(100, slot: 3), Instance(200, slot: 4)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            []);
        var dynamicRetention = CleanupRule(
            "user.retain-obsolete",
            900,
            new(
                ItemIds: new HashSet<uint> { 100 },
                UseStatuses: new HashSet<EquipmentUseStatus> { EquipmentUseStatus.Obsolete }),
            new(MinimumCopies: 2));
        var policy = new SquireProtectionPolicy(
            CharacterContentId: Scope.LocalContentId,
            CleanupRules: [.. SquireBuiltInCleanupRules.CreateDefaults(), dynamicRetention]);
        var removal = snapshot.Instances.First(instance => instance.Fingerprint.ItemId == 100).Fingerprint;

        var result = new SquireCounterfactualBatchValidator().Validate(
            snapshot,
            new Dictionary<EquipmentInstanceFingerprint, SquireDisposition>(EquipmentInstanceFingerprintComparer.Instance)
            {
                [removal] = SquireDisposition.Desynthesize,
            },
            DesynthesisUnlocked,
            policy);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void CounterfactualBatch_DoesNotRequireRepairingAnUnrelatedPreexistingGearsetDeficit()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(200, slot: 2)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            [Gearset(1, 999)]);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates,
            value => value.Definition.ItemId == 100);
        var removals = new Dictionary<EquipmentInstanceFingerprint, SquireDisposition>(EquipmentInstanceFingerprintComparer.Instance)
        {
            [candidate.Instance.Fingerprint] = SquireDisposition.Desynthesize,
        };

        var result = new SquireCounterfactualBatchValidator().Validate(snapshot, removals, DesynthesisUnlocked, new SquireProtectionPolicy());

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void DesynthesisLocked_FallsBackWithoutOfferingDesynthesis()
    {
        var snapshot = Snapshot(
            [Instance(100), Instance(200, equipped: true, slot: 99)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            [Gearset(1, 200)]);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, new SquireDispositionCapabilities(false)).Candidates, value => value.Definition.ItemId == 100);

        Assert.Equal(SquireDisposition.VendorSell, candidate.RecommendedDisposition);
        Assert.DoesNotContain(SquireDisposition.Desynthesize, candidate.SupportedDispositions);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "DesynthesisNotUnlocked");
        var displayedReasons = SquireTabPanel.FormatReasons(candidate);
        Assert.Equal(candidate.Reasons.Count, displayedReasons.Split('\n').Length);
        Assert.All(candidate.Reasons, reason => Assert.Contains(reason.Message, displayedReasons));
        var summary = SquireTabPanel.FormatReasonSummary(candidate);
        Assert.StartsWith(SquireTabPanel.ReasonLabel(candidate.Reasons[0].Code), summary);
        Assert.Contains($"(+{candidate.Reasons.Count - 1} rule", summary);
    }

    [Fact]
    public void IncompleteProspectiveBaseline_ExplainsTheEvaluationFailureAndNamesTheJob()
    {
        var incomplete = Definition(200, 30) with { StatProfile = Definition(200, 30).StatProfile! with { IsComplete = false } };
        var snapshot = Snapshot(
            [Instance(100), Instance(200, slot: 2)],
            [Definition(100, 20), incomplete],
            [Job(1, 50, true)],
            []);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates, value => value.Definition.ItemId == 100);
        var reason = Assert.Single(candidate.Reasons, reason => reason.Code == "JobComparisonFailed");

        Assert.Equal(SquireAssessment.EvaluationFailure, candidate.Assessment);
        Assert.Contains("No complete Body witness", reason.Message);
        Assert.Contains("Item 200", reason.Message);
        Assert.Contains("JOB", reason.Message);
    }

    [Fact]
    public void NoOwnedBetterWitness_ProtectsInsteadOfFailingEvaluation()
    {
        var snapshot = Snapshot([Instance(100)], [Definition(100, 20)], [Job(1, 50, true)], []);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates, value => value.Definition.ItemId == 100);

        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "NoRetainedCoverage");
        Assert.DoesNotContain(candidate.Reasons, reason => reason.Code == "JobComparisonFailed");
    }

    [Fact]
    public void SpecialPurposeEquipment_IsProtectedWithoutStatDominance()
    {
        var definition = Definition(100, 20) with { IsSpecialPurpose = true };
        var snapshot = Snapshot([Instance(100)], [definition], [Job(1, 50, true)], []);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);

        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "SpecialPurposeEquipment");
    }

    [Fact]
    public void UnobtainedEligibleJob_DoesNotProtectItem()
    {
        var snapshot = Snapshot(
            [Instance(100)],
            [Definition(100, 20)],
            [new CharacterJobSnapshot(1, "GSM", "Goldsmith", 49, false, 1, "Crafter")],
            []);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);
        var reason = Assert.Single(candidate.Reasons, reason => reason.Code == "NoObtainedEligibleJob");

        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.DoesNotContain("locked", reason.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FutureLevelingGear_IsACandidateByDefault()
    {
        var futureItem = Definition(100, 20) with { EquipLevel = 40 };
        var snapshot = Snapshot([Instance(100)], [futureItem], [Job(1, 30, true)], []);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);

        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "FutureLevelingUseNotProtected");
    }

    [Fact]
    public void UserFacingFields_HideInternalEnumAndContainerNames()
    {
        var fingerprint = Instance(100, slot: 6).Fingerprint with { Container = "ArmoryWrist" };

        Assert.Equal("Armory Chest: Wrists, Slot 6", SquireTabPanel.FormatLocation(fingerprint));
        Assert.Equal("Expert Delivery", SquireTabPanel.FormatDisposition(SquireDisposition.ExpertDelivery));
        Assert.Equal("Vendor Sale", SquireTabPanel.FormatDisposition(SquireDisposition.VendorSell));
        Assert.Equal("Evaluation Failure", SquireTabPanel.FormatAssessment(SquireAssessment.EvaluationFailure));
    }

    [Fact]
    public void MateriaBearingObsoleteGear_RequiresExplicitRiskAuthorization()
    {
        var snapshot = Snapshot(
            [Instance(100, materia: [500]), Instance(200, equipped: true, slot: 99)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            [Gearset(1, 200)]);

        var protectedCandidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates, value => value.Definition.ItemId == 100);
        Assert.Equal(SquireAssessment.Protected, protectedCandidate.Assessment);
        Assert.Contains(protectedCandidate.Reasons, reason => reason.Code == "MateriaRetrievalRiskNotAuthorized");

        var candidate = Assert.Single(evaluator.Evaluate(
            snapshot,
            DesynthesisUnlocked,
            new SquireProtectionPolicy(AllowRiskyMateriaRetrieval: true)).Candidates,
            value => value.Definition.ItemId == 100);
        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "MateriaRetrievalRequired" && reason.Severity == SquireReasonSeverity.Warning);
    }

    [Fact]
    public void MateriaBearingGear_IsProtectedWhenRetrievalQuestIsIncomplete()
    {
        var snapshot = Snapshot(
            [Instance(100, materia: [500]), Instance(200, equipped: true, slot: 99)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            [Gearset(1, 200)]);

        var candidate = Assert.Single(evaluator.Evaluate(
            snapshot,
            new SquireDispositionCapabilities(true, false),
            new SquireProtectionPolicy(AllowRiskyMateriaRetrieval: true)).Candidates,
            value => value.Definition.ItemId == 100);

        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "MateriaRetrievalNotUnlocked");
    }

    [Fact]
    public void NonEquipmentRawMateriaBytes_DoNotProduceMateriaRetrievalReason()
    {
        var definition = Definition(100, 1) with { IsEquipment = false, Slot = EquipmentSlot.Unknown };
        var snapshot = Snapshot([Instance(100, materia: [15])], [definition], [Job(1, 50, true)], []);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);

        Assert.Contains(candidate.Reasons, reason => reason.Code == "NotEquipment");
        Assert.DoesNotContain(candidate.Reasons, reason => reason.Code.StartsWith("MateriaRetrieval", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(EquipmentRarity.Rare)]
    [InlineData(EquipmentRarity.Relic)]
    public void HighRarityEquipment_IsProtectedUntilGlobalProtectionIsDisabled(EquipmentRarity rarity)
    {
        var item = Definition(100, 20) with
        {
            NormalizedRarity = rarity,
            Rarity = rarity == EquipmentRarity.Rare ? (byte)3 : (byte)4,
            ExpertDeliveryEligibility = ExpertDeliveryEligibility.Eligible,
        };
        var baseline = Definition(200, 30);
        var snapshot = Snapshot([Instance(100), Instance(200, equipped: true, slot: 99)], [item, baseline], [Job(1, 50, true)], [Gearset(1, 200)]);

        var protectedCandidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates, value => value.Definition.ItemId == 100);
        Assert.Equal(SquireAssessment.Protected, protectedCandidate.Assessment);
        Assert.Contains(protectedCandidate.Reasons, reason => reason.Code == "HighRarityEquipment");

        var allowed = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked,
            new SquireProtectionPolicy(ProtectBlueAndPurpleGear: false)).Candidates, value => value.Definition.ItemId == 100);
        Assert.Equal(SquireAssessment.Candidate, allowed.Assessment);
        Assert.Equal(SquireDisposition.ExpertDelivery, allowed.RecommendedDisposition);
        Assert.Contains(allowed.Reasons, reason => reason.Code == "HighRarityProtectionDisabled");
    }

    [Fact]
    public void CharacterItemProtectionRule_AlwaysWinsWhenHighRarityProtectionIsDisabled()
    {
        var item = Definition(100, 20) with
        {
            NormalizedRarity = EquipmentRarity.Rare,
            Rarity = 3,
            ExpertDeliveryEligibility = ExpertDeliveryEligibility.Eligible,
        };
        var snapshot = Snapshot([Instance(100)], [item], [Job(1, 50, false)], []);

        var rule = new SquireRule(Guid.NewGuid(), SquireRuleKind.ProtectItem, 100, SquireRuleQuality.Any, 0, true, "Test protection");
        var analysis = evaluator.Evaluate(snapshot, DesynthesisUnlocked,
            new SquireProtectionPolicy(ProtectBlueAndPurpleGear: false, Rules: [rule]));
        var candidate = Assert.Single(analysis.Candidates);

        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        var reason = Assert.Single(candidate.Reasons, reason => reason.Code == "ItemProtectionRule");
        Assert.Contains(rule.Id.ToString("N"), reason.Message);
        Assert.Equal(rule, Assert.Single(analysis.Policy.Rules!));
    }

    [Fact]
    public void InvalidEnabledRule_BlocksEvaluationInsteadOfFallingBack()
    {
        var snapshot = Snapshot([Instance(100)], [Definition(100, 20)], [Job(1, 50, false)], []);
        var invalid = new SquireRule(Guid.Empty, SquireRuleKind.RetainCopies, 0,
            SquireRuleQuality.Any, 0, true, "Corrupt rule");

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked,
            new SquireProtectionPolicy(Rules: [invalid])).Candidates);

        Assert.Equal(SquireAssessment.EvaluationFailure, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "InvalidRuleConfiguration");
    }

    [Fact]
    public void ConfigurableAllowRule_CanAuthorizeHighRarityCleanupAndKeepsTrace()
    {
        var item = Definition(100, 20) with
        {
            NormalizedRarity = EquipmentRarity.Rare,
            Rarity = 3,
            ExpertDeliveryEligibility = ExpertDeliveryEligibility.Eligible,
        };
        var baseline = Definition(200, 30);
        var snapshot = Snapshot([Instance(100), Instance(200, equipped: true, slot: 99)], [item, baseline], [Job(1, 50, true)], [Gearset(1, 200)]);
        var allow = CleanupRule(
            "user.allow-rare-100",
            1_000,
            new(ItemIds: new HashSet<uint> { 100 }),
            new(SquireCleanupDecision.AllowCleanup, Authorizations: SquireCleanupAuthorization.HighRarity));
        var policy = new SquireProtectionPolicy(
            CharacterContentId: Scope.LocalContentId,
            CleanupRules: [.. SquireBuiltInCleanupRules.CreateDefaults(), allow]);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked, policy).Candidates, value => value.Definition.ItemId == 100);

        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.Equal(SquireDisposition.ExpertDelivery, candidate.RecommendedDisposition);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "CleanupRuleAuthorized");
        Assert.Contains(candidate.RuleEvaluation!.MatchedRules, trace => trace.RuleId == allow.Id && trace.WonDecision);
        Assert.Contains(candidate.RuleEvaluation.MatchedRules, trace => trace.RuleId == "builtin.route-expert-delivery" && trace.WonDisposition);
    }

    [Fact]
    public void InvalidConfigurableRule_ProducesEvaluationFailureForEveryObservedItem()
    {
        var snapshot = Snapshot([Instance(100)], [Definition(100, 20)], [Job(1, 50, false)], []);
        var invalid = CleanupRule(
            "user.invalid",
            900,
            new(ItemIds: new HashSet<uint>()),
            new(Decision: SquireCleanupDecision.Protect));
        var policy = new SquireProtectionPolicy(
            CharacterContentId: Scope.LocalContentId,
            CleanupRules: [.. SquireBuiltInCleanupRules.CreateDefaults(), invalid]);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked, policy).Candidates);

        Assert.Equal(SquireAssessment.EvaluationFailure, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "InvalidRuleConfiguration");
        Assert.Contains(candidate.RuleEvaluation!.Errors, error => error.Contains("present but empty", StringComparison.Ordinal));
    }

    [Fact]
    public void UncommonEquipment_EligibleForExpertDelivery_BecomesDeliveryCandidate()
    {
        var item = Definition(100, 20) with
        {
            NormalizedRarity = EquipmentRarity.Uncommon,
            Rarity = 2,
            ExpertDeliveryEligibility = ExpertDeliveryEligibility.Eligible,
            ExpertDeliveryProvenance = "rarity rule",
        };
        var snapshot = Snapshot([Instance(100)], [item], [Job(1, 50, false)], []);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates, value => value.Definition.ItemId == 100);
        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.Equal(SquireDisposition.ExpertDelivery, candidate.RecommendedDisposition);
        Assert.Contains(SquireDisposition.ExpertDelivery, candidate.SupportedDispositions);
    }

    [Fact]
    public void FutureLevelingGear_CanBeProtectedByOptInPolicy()
    {
        var futureItem = Definition(100, 20) with { EquipLevel = 40 };
        var snapshot = Snapshot([Instance(100)], [futureItem], [Job(1, 30, true)], []);

        var candidate = Assert.Single(evaluator.Evaluate(
            snapshot,
            DesynthesisUnlocked,
            new SquireProtectionPolicy(ProtectFutureLevelingGear: true)).Candidates);

        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "FutureUnlockedJobUse");
    }

    [Fact]
    public void ActionPlanRetainsExactLooseWitness()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(200, slot: 2)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            []);
        var analysis = evaluator.Evaluate(snapshot, DesynthesisUnlocked);
        var target = analysis.Candidates.Single(value => value.Definition.ItemId == 100);

        var plan = new SquireActionPlanner().Create(
            analysis, target.RecommendedDisposition, [target.Instance.Fingerprint], DateTimeOffset.UtcNow,
            new SquireProtectionPolicy(), DesynthesisUnlocked);

        var proof = Assert.Single(Assert.Single(plan.Actions).Witnesses!);
        Assert.Equal(200u, Assert.Single(proof.Fingerprints).ItemId);
    }

    [Fact]
    public void ObsoleteReason_NamesTrustedBaselineAndExactOwnedLocation()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(200, slot: 7)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            []);

        var candidate = evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates.Single(value => value.Definition.ItemId == 100);
        var reason = Assert.Single(candidate.Reasons, value => value.Code == "RetainedCoverageForAllUnlockedJobs");

        Assert.Contains("JOB: Item 200", reason.Message);
        Assert.Contains("iLvl 30", reason.Message);
        Assert.Contains("Inventory1 slot 7, NQ", reason.Message);
    }

    [Fact]
    public void ActionPlanRejectsRemovingFinalLooseWitness()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(200, slot: 2)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            []);
        var analysis = evaluator.Evaluate(snapshot, DesynthesisUnlocked);
        var target = analysis.Candidates.Single(value => value.Definition.ItemId == 100);
        var witness = analysis.Candidates.Single(value => value.Definition.ItemId == 200);

        Assert.Throws<InvalidOperationException>(() => new SquireActionPlanner().Create(
            analysis, target.RecommendedDisposition, [target.Instance.Fingerprint, witness.Instance.Fingerprint], DateTimeOffset.UtcNow,
            new SquireProtectionPolicy(), DesynthesisUnlocked));
    }

    [Fact]
    public void CounterfactualBatchValidation_MatchesFreshStructuralFingerprint()
    {
        var target = Instance(100, materia: [7], slot: 1) with
        {
            Fingerprint = Instance(100, materia: [7], slot: 1).Fingerprint with { Stains = [3] },
        };
        var snapshot = Snapshot(
            [target, Instance(200, slot: 2)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            []);
        var freshEquivalent = target.Fingerprint with
        {
            MateriaIds = new List<uint> { 7 },
            Stains = new List<byte> { 3 },
        };
        var removals = new Dictionary<EquipmentInstanceFingerprint, SquireDisposition>
        {
            [freshEquivalent] = SquireDisposition.Desynthesize,
        };

        var result = new SquireCounterfactualBatchValidator().Validate(
            snapshot, removals, DesynthesisUnlocked, new SquireProtectionPolicy(AllowRiskyMateriaRetrieval: true));

        Assert.True(result.Success, result.Message);
        Assert.True(result.UseAnalyses.ContainsKey(freshEquivalent));
    }

    [Fact]
    public void CounterfactualBatch_PreservesRetainedOptionFrontierWithoutRequiringOneEnvelopeItem()
    {
        EquipmentItemDefinition CrafterItem(uint id, int craftsmanship, int control) => Definition(id, (uint)Math.Max(craftsmanship, control)) with
        {
            StatProfile = new EquipmentStatProfile(
                [
                    new(70, EquipmentStatSemantic.Craftsmanship, craftsmanship, false),
                    new(71, EquipmentStatSemantic.Control, control, false),
                    new(11, EquipmentStatSemantic.CraftingPoints, 1, false),
                ], 0, 0, 1, 1, true),
        };
        var firstTarget = CrafterItem(100, 100, 0);
        var secondTarget = CrafterItem(101, 0, 100);
        var craftsmanshipWitness = CrafterItem(200, 110, 0);
        var controlWitness = CrafterItem(201, 0, 110);
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(101, slot: 2), Instance(200, slot: 3), Instance(201, slot: 4)],
            [firstTarget, secondTarget, craftsmanshipWitness, controlWitness],
            [new CharacterJobSnapshot(1, "CRP", "Carpenter", 100, true, 1, "Crafter", EquipmentStatSemantic.Craftsmanship, EquipmentDiscipline.Crafter)],
            []);
        var removals = snapshot.Instances.Where(instance => instance.Fingerprint.ItemId is 100 or 101)
            .ToDictionary(instance => instance.Fingerprint, _ => SquireDisposition.Desynthesize, EquipmentInstanceFingerprintComparer.Instance);

        var result = new SquireCounterfactualBatchValidator().Validate(snapshot, removals, DesynthesisUnlocked, new SquireProtectionPolicy());

        Assert.True(result.Success, result.Message);
    }

    private static CharacterEquipmentSnapshot Snapshot(
        IReadOnlyList<EquipmentInstanceSnapshot> instances,
        IReadOnlyList<EquipmentItemDefinition> definitions,
        IReadOnlyList<CharacterJobSnapshot> jobs,
        IReadOnlyList<GearsetSnapshot> gearsets,
        bool complete = true) =>
        new(
            Guid.NewGuid(),
            new CharacterIdentitySnapshot(Scope, 21, 1, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            jobs,
            gearsets,
            instances,
            definitions.ToDictionary(definition => definition.ItemId),
            new CharacterEquipmentSnapshotDiagnostics(
            [
                new("identity", SnapshotComponentStatus.Complete),
                new("jobs", complete ? SnapshotComponentStatus.Complete : SnapshotComponentStatus.Partial),
                new("gearsets", SnapshotComponentStatus.Complete),
                new("equipped", SnapshotComponentStatus.Complete),
                new("armoury", SnapshotComponentStatus.Complete),
                new("inventory", SnapshotComponentStatus.Complete),
                new("definitions", SnapshotComponentStatus.Complete),
            ]));

    private static EquipmentInstanceSnapshot Instance(
        uint itemId,
        bool equipped = false,
        IReadOnlyList<uint>? materia = null,
        ulong? crafter = null,
        int slot = 1,
        bool highQuality = false) =>
        new(new EquipmentInstanceFingerprint(Scope, "Inventory1", slot, itemId, highQuality, 1, 30000, 0, crafter, materia ?? [], null, []), DateTimeOffset.UtcNow, equipped);

    private static EquipmentItemDefinition Definition(uint itemId, uint itemLevel) =>
        new(itemId, $"Item {itemId}", 1, itemLevel, EquipmentSlot.Body, new HashSet<uint> { 1 }, 1, true, false, true, true, 1, true, false, true, false,
            new EquipmentStatProfile([new(1, EquipmentStatSemantic.Strength, checked((int)itemLevel), false)], 0, 0, checked((int)itemLevel), checked((int)itemLevel), true),
            EquipmentRarity.Normal);

    private static CharacterJobSnapshot Job(uint id, uint level, bool? unlocked) =>
        new(id, "JOB", "Job", level, unlocked, null, "Tank", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat);

    private static SquireRule RetentionRule(uint itemId, bool isHighQuality, int minimumCopies) =>
        new(Guid.NewGuid(), SquireRuleKind.RetainCopies, itemId,
            isHighQuality ? SquireRuleQuality.HighQuality : SquireRuleQuality.NormalQuality,
            minimumCopies, true, "Test retention");

    private static SquireCleanupRule CleanupRule(
        string id,
        int priority,
        SquireCleanupRuleCondition condition,
        SquireCleanupRuleEffect effect) => new(
        id,
        id,
        SquireCleanupRuleOrigin.User,
        SquireCleanupRuleScope.Character,
        Scope.LocalContentId,
        true,
        priority,
        condition,
        effect);

    private static GearsetSnapshot Gearset(int id, uint itemId) =>
        new(id, "Set", 1, [new GearsetItemReference(EquipmentSlot.Body, itemId)], true);
}
