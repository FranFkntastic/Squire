using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Persistence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterPersistedAnalysisTests
{
    [Fact]
    public async Task Store_round_trips_versioned_decision_lineage_and_atomically_revises_snapshot()
    {
        var fixture = CreateAnalysisFixture();
        var directory = Path.Combine(Path.GetTempPath(), $"mmf-outfitter-analysis-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "analyses.json");
        try
        {
            var store = new OutfitterPersistedAnalysisStore(path);

            var first = await store.UpsertAsync(fixture.Analysis);
            var updated = await store.UpsertAsync(first with { SelectedSolutionId = first.NominationSolutionId });
            var restartedStore = new OutfitterPersistedAnalysisStore(path);
            var loaded = await restartedStore.LoadAsync();

            Assert.Equal(2, updated.Revision);
            Assert.Equal(2, loaded.Revision);
            var persisted = Assert.Single(loaded.Analyses);
            Assert.Equal(fixture.Target.Value, persisted.Target.Value);
            Assert.Equal(fixture.Analysis.Profile, persisted.Profile);
            Assert.Equal(fixture.Analysis.Evidence.GenerationId, persisted.Evidence.GenerationId);
            Assert.Equal(fixture.Analysis.Frontier.Count, persisted.Frontier.Count);
            Assert.Equal(fixture.Analysis.NominationSolutionId, persisted.NominationSolutionId);
            Assert.Equal(fixture.Analysis.SelectedSolutionId, persisted.SelectedSolutionId);
            Assert.NotNull(persisted.CraftHandoff);
            Assert.NotEmpty(persisted.CraftHandoff.PlanSha256s);
            Assert.NotEmpty(persisted.CraftHandoff.Materials);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Revalidation_names_exact_stale_boundaries_without_destroying_saved_decision()
    {
        var fixture = CreateAnalysisFixture();
        var current = OutfitterPersistedAnalysisValidation.Revalidate(
            fixture.Analysis,
            fixture.Baseline,
            CrafterAdvisorStatFamily.Instance,
            CrafterAdvisorStatFamily.OrdinaryCraftContext,
            fixture.Evidence,
            fixture.Advice);

        var changedBaseline = fixture.Baseline with
        {
            Target = fixture.Baseline.Target! with { AuthorityFingerprint = new string('b', 64) },
        };
        var changedEvidence = fixture.Evidence with { GenerationId = Guid.NewGuid() };
        var stale = OutfitterPersistedAnalysisValidation.Revalidate(
            fixture.Analysis,
            changedBaseline,
            CrafterAdvisorStatFamily.Instance,
            new("different-context", "Different", "Different context"),
            changedEvidence,
            fixture.Advice);

        Assert.True(current.CanAct);
        Assert.False(stale.CanAct);
        Assert.Contains(stale.StaleBoundaries, value => value.Kind == OutfitterPersistedBoundaryKind.Target);
        Assert.Contains(stale.StaleBoundaries, value => value.Kind == OutfitterPersistedBoundaryKind.Baseline);
        Assert.Contains(stale.StaleBoundaries, value => value.Kind == OutfitterPersistedBoundaryKind.Context);
        Assert.Contains(stale.StaleBoundaries, value => value.Kind == OutfitterPersistedBoundaryKind.MarketEvidence);
        Assert.Equal(fixture.Analysis.SelectedSolutionId, fixture.Analysis.CraftHandoff?.SelectedSolutionId);
    }

    [Fact]
    public async Task Store_rejects_unknown_schema_instead_of_guessing_migration()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mmf-outfitter-schema-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "analyses.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":\"future/v9\",\"revision\":1,\"analyses\":[]}");
            var store = new OutfitterPersistedAnalysisStore(path);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

            Assert.Contains("Unsupported", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static AnalysisFixture CreateAnalysisFixture()
    {
        var now = DateTimeOffset.Parse("2026-07-21T18:00:00Z");
        var character = new CharacterScope(7_777, "Myrna Smith", 64);
        var slots = PlayerAdvisorEquippedSlotMap.All.Select(slot => new SavedGearsetSlotFingerprint(
            slot.Position,
            null,
            null,
            [],
            [])).ToArray();
        var target = new SavedGearsetTargetFingerprint(
            character,
            4,
            "Blacksmith",
            CrafterUtilityProfile.BlacksmithClassJobId,
            100,
            slots,
            new string('a', 64));
        var fixedStats = new Dictionary<EquipmentStatSemantic, int>
        {
            [EquipmentStatSemantic.Craftsmanship] = 0,
            [EquipmentStatSemantic.Control] = 0,
            [EquipmentStatSemantic.CraftingPoints] = 180,
        };
        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(character, 64, 16, now.AddSeconds(-2), true, SnapshotComponentStatus.Complete),
            [],
            [],
            [],
            new Dictionary<uint, EquipmentItemDefinition>(),
            new([new("identity", SnapshotComponentStatus.Complete), new("equipped", SnapshotComponentStatus.Complete)]));
        var baseline = new PlayerAdvisorBaseline(
            PlayerAdvisorBaselineStatus.Complete,
            character,
            CrafterUtilityProfile.BlacksmithClassJobId,
            100,
            100,
            false,
            fixedStats,
            fixedStats,
            PlayerAdvisorEquippedSlotMap.All.Select(slot => new PlayerAdvisorEquippedSlot(
                slot.Position,
                slot.PositionKey,
                null,
                null,
                null,
                EquipmentSolverUtilityVector.Empty,
                [],
                [])).ToArray(),
            snapshot,
            "Synthetic saved-gearset persistence baseline.",
            new(PlayerAdvisorBaselineTargetKind.SavedGearset, "gearset:4", target.Value, target));
        var context = new EquipmentUtilityContext(
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
            CrafterUtilityProfile.BlacksmithClassJobId,
            100,
            "Ordinary craft",
            []);
        var solution = new EquipmentDecisionSolution(
            new("solution-a", []),
            new(
                new(CrafterUtilityProfile.ProfileId, CrafterUtilityProfile.ProfileVersion),
                context,
                0,
                new(0, 0, []),
                UpgradeAssessment.Equivalent,
                [],
                [],
                [],
                EquipmentEvaluationConfidence.High,
                []),
            0,
            new(0, 0, 0),
            new(0, 0, 0),
            ["Keep current"]);
        var pareto = new EquipmentParetoResult([solution], [], [], []);
        var exact = new EquipmentExactFrontierResult(
            pareto,
            new(0, 0, 0, 0, 1, 1, 1, 16, solution.Candidate.SolutionId, TimeSpan.Zero),
            []);
        var authority = new AdvisorAuthorityAssessment(true, UpgradeAssessment.Equivalent, [], ["No change required."]);
        var advice = new MinerBotanistReadOnlyAdvice(
            MinerBotanistAdvisorStatus.Complete,
            MinerBotanistReadOnlyAdvisor.AdvisoryRule,
            exact,
            solution,
            new Dictionary<string, AdvisorAuthorityAssessment> { [solution.Candidate.SolutionId] = authority },
            new Dictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer>(),
            "Synthetic complete advice.");
        var evidence = new OutfitterMarketEvidenceBook(
            Guid.NewGuid(),
            3,
            OutfitterMarketEvidenceBook.CurrentSchemaVersion,
            "universalis",
            "North America",
            now.AddMinutes(-1),
            now,
            OutfitterMarketEvidenceGenerationStatus.Complete,
            new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 1, 1, 100, [99_001]),
            [new(99_001, OutfitterMarketEvidenceItemStatus.Missing, [], now, "fixture")]);
        var craftHandoff = new OutfitterPersistedCraftHandoff(
            solution.Candidate.SolutionId,
            [new string('c', 64)],
            [new(55_001, 1, "Fixture recipe", 0)],
            [new(
                new string('c', 64),
                99_001,
                "Fixture material",
                EquipmentQuality.Normal,
                1,
                1,
                0,
                OutfitterMaterialSourceKind.MarketListing,
                100,
                $"market:{evidence.GenerationId:N}:{evidence.Revision}:64:listing-a")],
            now);
        var analysis = OutfitterPersistedAnalysis.Create(
            target,
            baseline,
            CrafterAdvisorStatFamily.Instance,
            CrafterAdvisorStatFamily.OrdinaryCraftContext,
            evidence,
            advice,
            solution.Candidate.SolutionId,
            craftHandoff,
            now);
        return new(target, baseline, evidence, advice, analysis);
    }

    private sealed record AnalysisFixture(
        SavedGearsetTargetFingerprint Target,
        PlayerAdvisorBaseline Baseline,
        OutfitterMarketEvidenceBook Evidence,
        MinerBotanistReadOnlyAdvice Advice,
        OutfitterPersistedAnalysis Analysis);
}
