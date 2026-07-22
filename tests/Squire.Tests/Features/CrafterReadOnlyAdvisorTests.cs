using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class CrafterReadOnlyAdvisorTests
{
    [Fact]
    public void Crafter_family_produces_a_frontier_through_the_shared_advisor()
    {
        var fixture = Fixture();
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.NotNull(advice.Frontier);
        Assert.Contains(advice.OffersByAllocation.Values, value => value.Offer.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard);
    }

    [Fact]
    public void Expanded_material_evidence_is_not_misread_as_equipment_market_scope()
    {
        var fixture = Fixture();
        var material = new OutfitterMarketItemEvidence(
            3_000,
            OutfitterMarketEvidenceItemStatus.Fresh,
            [new(3_000, EquipmentQuality.Normal, "material", "Siren", 1, "Material", "3", 10, 100, fixture.Evidence.CreatedAtUtc, fixture.Evidence.CreatedAtUtc, "r2")],
            fixture.Evidence.CreatedAtUtc,
            "r2");
        var expanded = fixture.Evidence with
        {
            Coverage = new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 2, 2, 100, [fixture.Candidate.ItemId, 3_000]),
            Items = [.. fixture.Evidence.Items, material],
        };

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            expanded,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
            equipmentMarketScope: new HashSet<uint> { fixture.Candidate.ItemId });

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.DoesNotContain(advice.OffersByAllocation.Values, offer => offer.Offer.Definition.ItemId == 3_000);
    }

    [Fact]
    public void Crafter_family_rejects_gatherer_offers_as_cross_discipline_poison()
    {
        var fixture = Fixture();
        var gathererDefinition = Definition(9_000, "Gathering Tool", EquipmentSlot.MainHand, 900, 950, gatheringInsteadOfCrafting: true);
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : itemId == gathererDefinition.ItemId ? [gathererDefinition] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
            vendorOffers: null,
            ownedItems: [new(gathererDefinition.ItemId, true, "Armoury")]);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.DoesNotContain(advice.OffersByAllocation.Values, value => value.Offer.Definition.ItemId == gathererDefinition.ItemId);
    }

    [Fact]
    public void Supported_crafter_calibration_nominates_a_capability_gaining_upgrade()
    {
        var fixture = Fixture();
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.NotNull(advice.Frontier);
        Assert.NotNull(advice.Nomination);
        Assert.Contains(advice.AuthorityBySolutionId.Values, authority => authority.AdvisorMayConsider);
        Assert.DoesNotContain(advice.AuthorityBySolutionId.Values, authority =>
            authority.Reasons.Any(reason => reason.Contains("experimental", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Partial_owned_inventory_coverage_blocks_paid_nomination()
    {
        var fixture = Fixture();
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
            ownedInventoryCoverageComplete: false);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.Null(advice.Nomination);
        Assert.Contains(advice.AuthorityBySolutionId.Values, authority =>
            authority.Reasons.Any(reason => reason.Contains("coverage is partial", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Melded_unequipped_owned_item_with_unproven_utility_blocks_paid_nomination()
    {
        var fixture = Fixture();
        var meldedOwned = Definition(3_000, "Melded owned hammer", EquipmentSlot.MainHand, 399, 401);
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate]
                : itemId == meldedOwned.ItemId ? [meldedOwned]
                : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
            ownedItems: [new(meldedOwned.ItemId, true, "Armoury", UtilityIsExact: false)]);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.Null(advice.Nomination);
        Assert.DoesNotContain(advice.OffersByAllocation.Values, offer => offer.Offer.Definition.ItemId == meldedOwned.ItemId);
        Assert.Contains(advice.AuthorityBySolutionId.Values, authority =>
            authority.Reasons.Any(reason => reason.Contains("melds whose exact effective utility is unproven", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Crafter_family_abstains_when_the_rendered_job_is_not_a_crafter()
    {
        var fixture = Fixture(classJobId: 16);
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId);

        Assert.Equal(MinerBotanistAdvisorStatus.Abstained, advice.Status);
        Assert.Contains("outside the supported", advice.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_non_master_craft_offer_enters_the_existing_advisor_offer_set_passively()
    {
        var fixture = Fixture();
        var definition = fixture.Candidate with { StatProfile = fixture.Candidate.HighQualityStatProfile };
        var response = RecipeResponse(definition, materialQuantity: 3);
        var vendors = OutfitterGilVendorCatalog.FromTrustedSnapshot(
        [
            new(3_000, 20, 30, "Test Merchant", 40, "Test Territory", 100),
        ]);
        var builtAt = fixture.Evidence.PublishedAtUtc!.Value.AddSeconds(30);
        var graphService = new StaticRecipeGraphService(response);
        var provider = new OutfitterPassiveCraftOfferProvider(
            graphService,
            new OutfitterPassiveCraftOfferService(vendors, new FixedTimeProvider(builtAt)));
        var result = await provider.BuildAsync(
            fixture.Baseline,
            fixture.Evidence,
            definition,
            CrafterAdvisorStatFamily.Instance);

        Assert.Equal(OutfitterPassiveCraftOfferStatus.OfferReady, result.Status);
        Assert.NotNull(result.Offer);
        Assert.Equal(300ul, result.Offer!.Source.TotalGil);
        Assert.Equal(EquipmentAcquisitionSourceKind.Craft, result.Offer.SolverOffer.Offer.SourceKind);
        Assert.NotEqual(EquipmentAcquisitionSourceKind.MarketBoard, result.Offer.SolverOffer.Offer.SourceKind);
        Assert.Null(result.Offer.SolverOffer.ObservationId);
        Assert.Equal(definition.ItemId, graphService.Request!.ItemId);
        Assert.Equal(definition.Name, graphService.Request.ItemName);

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == definition.ItemId ? [definition] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
            craftOffers: [result.Offer]);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.NotNull(advice.Nomination);
        Assert.Equal(300ul, advice.Nomination!.AcquisitionCostGil);
        Assert.Contains(advice.OffersByAllocation.Values, value =>
            value.Offer.SourceKind == EquipmentAcquisitionSourceKind.Craft &&
            value.Offer.Definition.ItemId == definition.ItemId);
        Assert.Contains(advice.Nomination.Candidate.Selections, value =>
            value.OfferKey.SourceKind == EquipmentAcquisitionSourceKind.Craft);

        var handoff = OutfitterCraftHandoffProjection.Build(
            advice,
            advice.Nomination.Candidate.SolutionId,
            fixture.Baseline,
            fixture.Evidence,
            builtAt);

        var recipe = Assert.Single(handoff.Recipes);
        Assert.Equal(500u, recipe.RecipeId);
        Assert.Equal(1u, recipe.CraftCount);
        Assert.Equal(definition.Name, recipe.ItemName);
        var vendorMaterial = Assert.Single(handoff.VendorMaterials);
        Assert.Equal("Test Material", vendorMaterial.ItemName);
        Assert.Empty(handoff.MarketMaterials);
        Assert.False(result.Offer.Matches(fixture.Baseline, fixture.Evidence, builtAt.Add(CraftMarketEvidenceFreshness.TimeToLive).AddSeconds(1)));
    }

    [Fact]
    public void Same_raw_listing_id_on_two_worlds_produces_distinct_solver_allocations()
    {
        var fixture = Fixture();
        var original = Assert.Single(fixture.Evidence.Items[0].Listings);
        var evidence = fixture.Evidence with
        {
            Items =
            [
                fixture.Evidence.Items[0] with
                {
                    Listings =
                    [
                        original with { ListingId = "cross-world" },
                        original with { ListingId = "cross-world", WorldId = 74, WorldName = "Coeurl", UnitPriceGil = original.UnitPriceGil + 1 },
                    ],
                },
            ],
        };

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId);

        Assert.Equal(MinerBotanistAdvisorStatus.Complete, advice.Status);
        var offers = advice.OffersByAllocation.Values
            .Where(offer => offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard)
            .ToArray();
        Assert.Equal(2, offers.Length);
        Assert.Equal(2, offers.Select(offer => offer.AllocationKey).Distinct().Count());
    }

    [Fact]
    public void Frontier_solution_reusing_one_material_listing_cannot_be_nominated()
    {
        var fixture = Fixture();
        var publishedAt = fixture.Evidence.PublishedAtUtc!.Value;
        var material = new OutfitterMarketItemEvidence(
            3_000,
            OutfitterMarketEvidenceItemStatus.Fresh,
            [new(3_000, EquipmentQuality.Normal, "shared-lot", "Siren", 1, "Materials", "m1", 10, 7,
                publishedAt, publishedAt, "material-r1")],
            publishedAt,
            "material-r1");
        var evidence = fixture.Evidence with
        {
            Coverage = new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 2, 2, 100, [fixture.Candidate.ItemId, 3_000]),
            Items = [fixture.Evidence.Items[0], material],
        };
        var mainHand = fixture.Candidate with { StatProfile = fixture.Candidate.HighQualityStatProfile };
        var offHand = Definition(2_001, "Threshold File", EquipmentSlot.OffHand, 400, 400);
        var service = new OutfitterPassiveCraftOfferService(
            OutfitterGilVendorCatalog.FromTrustedSnapshot([]),
            new FixedTimeProvider(publishedAt.AddSeconds(30)));
        var mainResult = service.Build(
            RecipeResponse(mainHand, materialQuantity: 3),
            fixture.Baseline,
            evidence,
            mainHand,
            CrafterAdvisorStatFamily.Instance);
        var offResult = service.Build(
            RecipeResponse(offHand, materialQuantity: 3, identityCharacter: 'B'),
            fixture.Baseline,
            evidence,
            offHand,
            CrafterAdvisorStatFamily.Instance);
        Assert.Equal(OutfitterPassiveCraftOfferStatus.OfferReady, mainResult.Status);
        Assert.Equal(OutfitterPassiveCraftOfferStatus.OfferReady, offResult.Status);

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            evidence,
            itemId => itemId == mainHand.ItemId ? [mainHand] : itemId == offHand.ItemId ? [offHand] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
            craftOffers: [mainResult.Offer!, offResult.Offer!],
            equipmentMarketScope: new HashSet<uint>());

        var conflicting = Assert.Single(advice.Frontier!.Pareto.Frontier, solution =>
            solution.Candidate.Selections.Count(selection => selection.OfferKey.SourceKind == EquipmentAcquisitionSourceKind.Craft) == 2);
        var authority = advice.AuthorityBySolutionId[conflicting.Candidate.SolutionId];
        Assert.False(authority.AdvisorMayConsider);
        Assert.Contains(authority.Reasons, reason => reason.Contains("indivisible market listing", StringComparison.Ordinal));
        Assert.NotEqual(conflicting.Candidate.SolutionId, advice.Nomination?.Candidate.SolutionId);
    }

    [Fact]
    public void Master_recipe_response_remains_display_only_and_never_creates_an_advisor_offer()
    {
        var fixture = Fixture();
        var definition = fixture.Candidate with { StatProfile = fixture.Candidate.HighQualityStatProfile };
        var response = RecipeResponse(
            definition,
            materialQuantity: 1,
            unlockEvidence: CraftRecipeUnlockEvidenceV1.UnlockItemRequired,
            unlockItemId: 99);
        var vendors = OutfitterGilVendorCatalog.FromTrustedSnapshot(
        [
            new(3_000, 20, 30, "Test Merchant", 40, "Test Territory", 100),
        ]);

        var result = new OutfitterPassiveCraftOfferService(
            vendors,
            new FixedTimeProvider(fixture.Evidence.PublishedAtUtc!.Value.AddSeconds(30))).Build(
                response,
                fixture.Baseline,
                fixture.Evidence,
                definition,
                CrafterAdvisorStatFamily.Instance);

        Assert.Equal(OutfitterPassiveCraftOfferStatus.DisplayOnly, result.Status);
        Assert.Null(result.Offer);
        Assert.NotNull(result.Plan);
        Assert.Contains(result.Diagnostics, value => value.Contains("master", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_unlock_evidence_abstains_before_plan_or_advisor_offer_creation()
    {
        var fixture = Fixture();
        var definition = fixture.Candidate with { StatProfile = fixture.Candidate.HighQualityStatProfile };
        var response = RecipeResponse(
            definition,
            materialQuantity: 1,
            unlockEvidence: CraftRecipeUnlockEvidenceV1.Unknown);
        var result = new OutfitterPassiveCraftOfferService(
            OutfitterGilVendorCatalog.FromTrustedSnapshot(
            [
                new(3_000, 20, 30, "Test Merchant", 40, "Test Territory", 100),
            ]),
            new FixedTimeProvider(fixture.Evidence.PublishedAtUtc!.Value.AddSeconds(30))).Build(
                response,
                fixture.Baseline,
                fixture.Evidence,
                definition,
                CrafterAdvisorStatFamily.Instance);

        Assert.Equal(OutfitterPassiveCraftOfferStatus.Abstained, result.Status);
        Assert.Null(result.Plan);
        Assert.Null(result.Offer);
        Assert.Contains(result.Diagnostics, value => value.Contains("unlock evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Market_material_craft_offer_charges_the_whole_listing_and_preserves_surplus()
    {
        var fixture = Fixture();
        var definition = fixture.Candidate with { StatProfile = fixture.Candidate.HighQualityStatProfile };
        var publishedAt = fixture.Evidence.PublishedAtUtc!.Value;
        var material = new OutfitterMarketItemEvidence(
            3_000,
            OutfitterMarketEvidenceItemStatus.Fresh,
            [
                new(3_000, EquipmentQuality.Normal, "material-lot", "Siren", 1, "Materials", "m1", 10, 7,
                    publishedAt, publishedAt, "material-r1"),
            ],
            publishedAt,
            "material-r1");
        var evidence = fixture.Evidence with
        {
            Coverage = new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 2, 2, 100, [definition.ItemId, 3_000]),
            Items = [.. fixture.Evidence.Items, material],
        };
        var response = RecipeResponse(definition, materialQuantity: 3);
        var service = new OutfitterPassiveCraftOfferService(
            OutfitterGilVendorCatalog.FromTrustedSnapshot([]),
            new FixedTimeProvider(publishedAt.AddSeconds(30)));

        var result = service.Build(
            response,
            fixture.Baseline,
            evidence,
            definition,
            CrafterAdvisorStatFamily.Instance);

        Assert.Equal(OutfitterPassiveCraftOfferStatus.OfferReady, result.Status);
        Assert.Equal(70ul, result.Offer!.Source.TotalGil);
        var line = Assert.Single(result.Plan!.TerminalMaterials);
        Assert.Equal(3u, line.ConsumedQuantity);
        Assert.Equal(10u, line.PurchasedQuantity);
        Assert.Equal(7u, line.SurplusQuantity);
        Assert.Equal(70ul, result.Offer.SolverOffer.AcquisitionCostGil);

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            evidence,
            itemId => itemId == definition.ItemId ? [definition] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
            craftOffers: [result.Offer],
            equipmentMarketScope: new HashSet<uint> { definition.ItemId });
        Assert.NotNull(advice.Nomination);

        var handoff = OutfitterCraftHandoffProjection.Build(
            advice,
            advice.Nomination!.Candidate.SolutionId,
            fixture.Baseline,
            evidence,
            publishedAt.AddSeconds(30));
        var marketMaterial = Assert.Single(handoff.MarketMaterials);
        Assert.Equal("Test Material", marketMaterial.ItemName);
        Assert.Equal(3u, marketMaterial.ConsumedQuantity);
        Assert.Equal(10u, marketMaterial.PurchasedQuantity);
        Assert.Equal(7u, marketMaterial.SurplusQuantity);

        var fingerprint = PlayerAdvisorAuthorityFingerprint.Capture(fixture.Baseline);
        var validation = OutfitterWorkbenchPlayerValidation.Create(
            advice,
            advice.Nomination.Candidate.SolutionId,
            evidence,
            fingerprint,
            fingerprint) with
        {
            RecapturedBaseline = fixture.Baseline,
        };
        var transfer = OutfitterWorkbenchTransferBuilder.Build(
            advice,
            advice.Nomination.Candidate.SolutionId,
            evidence,
            validation,
            new FixedTimeProvider(publishedAt.AddSeconds(30)));

        var transferred = Assert.Single(transfer.MarketLots);
        Assert.Equal(3_000u, transferred.OfferKey.ItemId);
        Assert.Equal("Test Material", transferred.ItemName);
        Assert.Equal("Crafting material", transferred.ItemKind);
        Assert.Equal(10u, transferred.RequiredQuantity);
        Assert.DoesNotContain(transfer.MarketLots, lot => lot.OfferKey.ItemId == definition.ItemId);
    }

    private static FixtureData Fixture(uint classJobId = 9)
    {
        var now = new DateTimeOffset(2026, 7, 19, 20, 0, 0, TimeSpan.Zero);
        var positions = new[]
        {
            ("main-hand", EquipmentLoadoutPosition.MainHand, EquipmentSlot.MainHand),
            ("off-hand", EquipmentLoadoutPosition.OffHand, EquipmentSlot.OffHand),
            ("head", EquipmentLoadoutPosition.Head, EquipmentSlot.Head),
            ("body", EquipmentLoadoutPosition.Body, EquipmentSlot.Body),
            ("hands", EquipmentLoadoutPosition.Hands, EquipmentSlot.Hands),
            ("legs", EquipmentLoadoutPosition.Legs, EquipmentSlot.Legs),
            ("feet", EquipmentLoadoutPosition.Feet, EquipmentSlot.Feet),
            ("ears", EquipmentLoadoutPosition.Ears, EquipmentSlot.Ears),
            ("neck", EquipmentLoadoutPosition.Neck, EquipmentSlot.Neck),
            ("wrists", EquipmentLoadoutPosition.Wrists, EquipmentSlot.Wrists),
            ("ring-left", EquipmentLoadoutPosition.LeftRing, EquipmentSlot.Ring),
            ("ring-right", EquipmentLoadoutPosition.RightRing, EquipmentSlot.Ring),
        };
        var scope = new CharacterScope(77, "Crafter", 21);
        var equipped = new List<PlayerAdvisorEquippedSlot>();
        var instances = new List<EquipmentInstanceSnapshot>();
        var definitions = new Dictionary<uint, EquipmentItemDefinition>();
        var itemId = 1_000u;
        foreach (var (key, position, slot) in positions)
        {
            var definition = Definition(
                itemId++,
                $"Current {key}",
                slot,
                399,
                399,
                classJobId: classJobId,
                gatheringInsteadOfCrafting: classJobId == 16);
            var index = PlayerAdvisorEquippedSlotMap.All.Single(value => value.Position == position).EquippedIndex;
            var instance = new EquipmentInstanceSnapshot(
                new EquipmentInstanceFingerprint(scope, "EquippedItems", index, definition.ItemId, false, 1, 30_000, 0, null, [], null, []),
                now,
                true);
            var semantics = new Dictionary<EquipmentStatSemantic, int>
            {
                [EquipmentStatSemantic.Craftsmanship] = key == "main-hand" ? 399 : 0,
                [EquipmentStatSemantic.Control] = 0,
                [EquipmentStatSemantic.CraftingPoints] = 0,
            };
            equipped.Add(new(position, key, instance, definition, EquipmentQuality.Normal,
                CrafterAdvisorStatFamily.Instance.VectorFromSemantics(semantics), [], []));
            instances.Add(instance);
            definitions.Add(definition.ItemId, definition);
        }

        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(scope, 21, classJobId, now, true, SnapshotComponentStatus.Complete),
            [],
            [],
            instances,
            definitions,
            new([new("identity", SnapshotComponentStatus.Complete), new("equipped", SnapshotComponentStatus.Complete)]));
        var totalStats = classJobId == 16
            ? new Dictionary<EquipmentStatSemantic, int>
            {
                [EquipmentStatSemantic.Gathering] = 5_399,
                [EquipmentStatSemantic.Perception] = 5_200,
                [EquipmentStatSemantic.GatheringPoints] = 950,
            }
            : new Dictionary<EquipmentStatSemantic, int>
            {
                [EquipmentStatSemantic.Craftsmanship] = 5_399,
                [EquipmentStatSemantic.Control] = 5_200,
                [EquipmentStatSemantic.CraftingPoints] = 950,
            };
        var captures = PlayerAdvisorEquippedSlotMap.All.Select(position =>
        {
            var slot = equipped.Single(value => value.Position == position.Position);
            var stats = classJobId == 16
                ? new Dictionary<EquipmentStatSemantic, int>
                {
                    [EquipmentStatSemantic.Gathering] = position.Position == EquipmentLoadoutPosition.MainHand ? 399 : 0,
                    [EquipmentStatSemantic.Perception] = 0,
                    [EquipmentStatSemantic.GatheringPoints] = 0,
                }
                : new Dictionary<EquipmentStatSemantic, int>
                {
                    [EquipmentStatSemantic.Craftsmanship] = position.Position == EquipmentLoadoutPosition.MainHand ? 399 : 0,
                    [EquipmentStatSemantic.Control] = 0,
                    [EquipmentStatSemantic.CraftingPoints] = 0,
                };
            return new PlayerAdvisorEquippedItemCapture(
                position.EquippedIndex,
                slot.Definition!.ItemId,
                EquipmentQuality.Normal,
                stats,
                [],
                []);
        }).ToArray();
        var baseline = PlayerAdvisorBaselineAssembler.Assemble(
            snapshot,
            new(scope, 21, classJobId, 100, 100, false),
            AdvisorStatFamilies.Resolve(classJobId),
            totalStats,
            captures,
            PlayerAdvisorTrustedCapture.Complete(Guid.NewGuid(), now));
        var candidate = Definition(2_000, "Threshold Hammer", EquipmentSlot.MainHand, 399, 400, classJobId: classJobId);
        var evidence = new OutfitterMarketEvidenceBook(
            Guid.NewGuid(),
            1,
            OutfitterMarketEvidenceBook.CurrentSchemaVersion,
            "fixture",
            "NA",
            now,
            now,
            OutfitterMarketEvidenceGenerationStatus.Complete,
            new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 1, 1, 100, [candidate.ItemId]),
            [
                new(candidate.ItemId, OutfitterMarketEvidenceItemStatus.Fresh,
                [
                    new(candidate.ItemId, EquipmentQuality.High, "hq", "Siren", 1, "HQ", "2", 1, 25_000, now, now, "r1"),
                ], now, "r1"),
            ]);
        return new(baseline, evidence, candidate);
    }

    private static EquipmentItemDefinition Definition(
        uint itemId,
        string name,
        EquipmentSlot slot,
        int normalStat,
        int highStat,
        uint classJobId = 9,
        bool gatheringInsteadOfCrafting = false)
    {
        EquipmentStatProfile Profile(int amount) => gatheringInsteadOfCrafting
            ? new(
                [
                    new((uint)72, EquipmentStatSemantic.Gathering, amount, false, "stat"),
                    new((uint)73, EquipmentStatSemantic.Perception, amount, false, "stat2"),
                ],
                0, 0, 0, 0, true)
            : new(
                [
                    new((uint)74, EquipmentStatSemantic.Craftsmanship, amount, false, "stat"),
                    new((uint)75, EquipmentStatSemantic.Control, amount, false, "stat2"),
                ],
                0, 0, 0, 0, true);
        return new(
            itemId,
            name,
            100,
            700,
            slot,
            new HashSet<uint> { classJobId },
            1,
            true,
            false,
            true,
            true,
            1,
            true,
            null,
            null,
            false,
            StatProfile: Profile(normalStat),
            HighQualityStatProfile: Profile(highStat));
    }

    private static CraftRecipeGraphResponseV1 RecipeResponse(
        EquipmentItemDefinition definition,
        uint materialQuantity,
        CraftRecipeUnlockEvidenceV1 unlockEvidence = CraftRecipeUnlockEvidenceV1.NoUnlockRequired,
        uint unlockItemId = 0,
        char identityCharacter = 'A') => new()
    {
        ProviderVersion = "ca-test-v1",
        RecipeDataIdentity = $"sha256:{new string(identityCharacter, 64)}",
        IsComplete = true,
        RootItemId = definition.ItemId,
        RootItemName = definition.Name,
        Limits = CraftRecipeGraphLimitsV1.Default,
        Recipes =
        [
            new CraftRecipeDefinitionV1
            {
                RecipeId = 500,
                OutputItemId = definition.ItemId,
                OutputItemName = definition.Name,
                OutputQuantity = 1,
                RequiredClassJobId = 9,
                RequiredClassJobName = "Blacksmith",
                RequiredLevel = 90,
                RecipeUnlockItemId = unlockItemId,
                UnlockEvidence = unlockEvidence,
                ResolutionConfidence = CraftRecipeResolutionConfidenceV1.Exact,
                DataSource = CraftRecipeDataSourceV1.GarlandStandardCraft,
                Ingredients =
                [
                    new CraftRecipeIngredientV1
                    {
                        ItemId = 3_000,
                        ItemName = "Test Material",
                        QuantityPerCraft = materialQuantity,
                    },
                ],
                StructuralDiagnostics = [],
            },
        ],
        TerminalMaterialItemIds = [3_000],
        Diagnostics = [],
    };

    private sealed record FixtureData(
        PlayerAdvisorBaseline Baseline,
        OutfitterMarketEvidenceBook Evidence,
        EquipmentItemDefinition Candidate);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StaticRecipeGraphService(CraftRecipeGraphResponseV1 response) : ICraftRecipeGraphService
    {
        public CraftRecipeGraphRequestV1? Request { get; private set; }

        public Task<CraftRecipeGraphResponseV1> BuildAsync(
            CraftRecipeGraphRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(response);
        }
    }
}
