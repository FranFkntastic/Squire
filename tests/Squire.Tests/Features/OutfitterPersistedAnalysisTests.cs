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

    [Fact]
    public void Cache_restores_compatible_frontier_and_rebinds_unchanged_market_lineage()
    {
        var fixture = CreateCacheFixture();
        var book = new OutfitterPersistedAnalysisBook(
            OutfitterPersistedAnalysisBook.CurrentSchemaVersion,
            1,
            [fixture.Analysis]);

        var restored = OutfitterPersistedAnalysisCache.TryRestore(
            book,
            fixture.Baseline,
            CrafterAdvisorStatFamily.Instance,
            CrafterAdvisorStatFamily.OrdinaryCraftContext,
            fixture.Evidence,
            out var analysis,
            out var advice);

        Assert.True(restored);
        Assert.Equal(fixture.Analysis.AnalysisId, analysis.AnalysisId);
        Assert.Equal(fixture.Advice.Nomination?.Candidate.SolutionId, advice.Nomination?.Candidate.SolutionId);
        var refreshedEvidence = fixture.Evidence with
        {
            GenerationId = Guid.NewGuid(),
            Revision = fixture.Evidence.Revision + 1,
            CreatedAtUtc = fixture.Evidence.CreatedAtUtc.AddMinutes(10),
            PublishedAtUtc = fixture.Evidence.PublishedAtUtc!.Value.AddMinutes(10),
            Items = fixture.Evidence.Items.Select(item => item with
            {
                CapturedAtUtc = item.CapturedAtUtc.AddMinutes(10),
                Listings = item.Listings.Select(listing => listing with
                {
                    CapturedAtUtc = listing.CapturedAtUtc.AddMinutes(10),
                    ListingReviewedAtUtc = listing.ListingReviewedAtUtc.AddMinutes(10),
                }).ToArray(),
            }).ToArray(),
        };

        Assert.True(OutfitterPersistedAnalysisCache.TryRebindEquivalentMarketEvidence(
            advice,
            fixture.Evidence,
            refreshedEvidence,
            out var rebound));
        var reboundOffer = Assert.Single(rebound.OffersByAllocation.Values);
        Assert.Equal(refreshedEvidence.GenerationId, reboundOffer.Offer.Observation?.EvidenceGenerationId);
    }

    [Fact]
    public void Cache_rejects_changed_player_baseline()
    {
        var fixture = CreateCacheFixture();
        var book = new OutfitterPersistedAnalysisBook(
            OutfitterPersistedAnalysisBook.CurrentSchemaVersion,
            1,
            [fixture.Analysis]);
        var changed = fixture.Baseline with
        {
            TotalStats = fixture.Baseline.TotalStats.ToDictionary(
                value => value.Key,
                value => value.Key == EquipmentStatSemantic.CraftingPoints ? value.Value + 1 : value.Value),
            FixedStats = fixture.Baseline.FixedStats.ToDictionary(
                value => value.Key,
                value => value.Key == EquipmentStatSemantic.CraftingPoints ? value.Value + 1 : value.Value),
        };

        Assert.False(OutfitterPersistedAnalysisCache.TryRestore(
            book,
            changed,
            CrafterAdvisorStatFamily.Instance,
            CrafterAdvisorStatFamily.OrdinaryCraftContext,
            fixture.Evidence,
            out _,
            out _));
    }

    [Fact]
    public async Task Cache_offer_table_round_trips_through_existing_store()
    {
        var fixture = CreateCacheFixture();
        var directory = Path.Combine(Path.GetTempPath(), $"squire-outfitter-cache-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "analyses.json");
        try
        {
            var store = new OutfitterPersistedAnalysisStore(path);
            await store.UpsertAsync(fixture.Analysis);

            var loaded = await store.LoadAsync();
            var header = await File.ReadAllBytesAsync(path);

            var persisted = Assert.Single(loaded.Analyses);
            var offer = Assert.Single(persisted.Offers);
            Assert.Equal(0x1f, header[0]);
            Assert.Equal(0x8b, header[1]);
            Assert.Equal(fixture.Evidence.GenerationId, offer.Offer.Observation?.EvidenceGenerationId);
            Assert.Equal("listing-cache", offer.ObservationId);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cache_store_keeps_one_entry_per_target_profile_and_region()
    {
        var fixture = CreateCacheFixture();
        var directory = Path.Combine(Path.GetTempPath(), $"squire-outfitter-cache-slot-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "analyses.json");
        try
        {
            var store = new OutfitterPersistedAnalysisStore(path);
            await store.UpsertAsync(fixture.Analysis);
            var replacement = fixture.Analysis with { AnalysisId = Guid.NewGuid() };

            await store.UpsertAsync(replacement);

            var persisted = Assert.Single((await store.LoadAsync()).Analyses);
            Assert.Equal(replacement.AnalysisId, persisted.AnalysisId);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static AnalysisFixture CreateCacheFixture()
    {
        var fixture = CreateAnalysisFixture();
        var now = fixture.Evidence.PublishedAtUtc!.Value;
        var definition = new EquipmentItemDefinition(
            99_001,
            "Cached fixture tool",
            1,
            1,
            EquipmentSlot.MainHand,
            new HashSet<uint> { CrafterUtilityProfile.BlacksmithClassJobId },
            1,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            false);
        var key = new EquipmentOfferKey(
            definition.ItemId,
            EquipmentQuality.Normal,
            EquipmentAcquisitionSourceKind.MarketBoard,
            "market:universalis:64:99001:Normal");
        var listing = new OutfitterMarketListingEvidence(
            definition.ItemId,
            EquipmentQuality.Normal,
            "listing-cache",
            "Siren",
            64,
            "Retainer",
            "retainer-cache",
            1,
            100,
            now,
            now,
            "revision-cache");
        var evidence = fixture.Evidence with
        {
            Items =
            [
                new(
                    definition.ItemId,
                    OutfitterMarketEvidenceItemStatus.Fresh,
                    [listing],
                    now,
                    listing.SourceRevision),
            ],
        };
        var observation = new EquipmentOfferObservation(
            key,
            evidence.GenerationId,
            listing.ListingId,
            listing.ListingReviewedAtUtc,
            ObservableMarketRow: new(
                listing.ListingId,
                listing.ItemId,
                listing.Quality,
                listing.Quantity,
                listing.UnitPriceGil,
                listing.WorldName,
                listing.RetainerName),
            World: listing.WorldName,
            AvailableQuantity: listing.Quantity,
            UnitPriceGil: listing.UnitPriceGil);
        var offer = new EquipmentExactSolverOffer(
            new(
                definition,
                EquipmentAcquisitionSourceKind.MarketBoard,
                "Market board · Siren",
                listing.UnitPriceGil,
                Quality: listing.Quality,
                SourceCatalogKey: key.SourceCatalogKey,
                Observation: observation),
            listing.ListingId,
            new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.MainHand },
            1,
            EquipmentSolverUtilityVector.Empty,
            listing.UnitPriceGil,
            listing.WorldName,
            null,
            1,
            new(0, 0, 0),
            ["NQ", listing.WorldName]);
        var originalSolution = fixture.Advice.Frontier!.Pareto.Frontier[0];
        var solution = originalSolution with
        {
            Candidate = new(
                originalSolution.Candidate.SolutionId,
                [new(EquipmentLoadoutPosition.MainHand, key, 1, listing.ListingId)]),
            AcquisitionCostGil = listing.UnitPriceGil,
        };
        var exact = new EquipmentExactFrontierResult(
            new([solution], [], [], []),
            fixture.Advice.Frontier.Diagnostics,
            []);
        var advice = fixture.Advice with
        {
            Frontier = exact,
            Nomination = solution,
            AuthorityBySolutionId = new Dictionary<string, AdvisorAuthorityAssessment>
            {
                [solution.Candidate.SolutionId] = fixture.Advice.AuthorityBySolutionId[originalSolution.Candidate.SolutionId],
            },
            OffersByAllocation = new Dictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer>
            {
                [offer.AllocationKey] = offer,
            },
        };
        var analysis = OutfitterPersistedAnalysis.Create(
            fixture.Target,
            fixture.Baseline,
            CrafterAdvisorStatFamily.Instance,
            CrafterAdvisorStatFamily.OrdinaryCraftContext,
            evidence,
            advice,
            solution.Candidate.SolutionId,
            createdAtUtc: now);
        return new(fixture.Target, fixture.Baseline, evidence, advice, analysis);
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
