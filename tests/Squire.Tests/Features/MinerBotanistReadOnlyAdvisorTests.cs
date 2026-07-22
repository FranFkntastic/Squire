using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class MinerBotanistReadOnlyAdvisorTests
{
    [Fact]
    public void Build_nominates_least_cost_exact_quality_solution_that_crosses_capability()
    {
        var fixture = Fixture();
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.NotNull(advice.Frontier);
        Assert.NotNull(advice.Nomination);
        Assert.Contains(
            advice.Nomination!.Candidate.Selections,
            value => value.Position == EquipmentLoadoutPosition.MainHand && value.OfferKey.Quality == EquipmentQuality.High);
        Assert.Equal((ulong)25_000, advice.Nomination.AcquisitionCostGil);
        Assert.Contains("least-cost", advice.AdvisoryRule, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_abstains_before_solver_when_market_generation_is_partial()
    {
        var fixture = Fixture();
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence with { Status = OutfitterMarketEvidenceGenerationStatus.Partial },
            _ => [fixture.Candidate],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId);

        Assert.Equal(MinerBotanistAdvisorStatus.Abstained, advice.Status);
        Assert.Null(advice.Frontier);
        Assert.Null(advice.Nomination);
    }

    [Fact]
    public void Build_abstains_on_unmodeled_item_effects()
    {
        var fixture = Fixture();
        var special = fixture.Candidate with { ItemSpecialBonusId = 99 };
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            _ => [special],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId);

        Assert.Equal(MinerBotanistAdvisorStatus.Abstained, advice.Status);
        Assert.Contains("unmodeled effect", advice.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_accepts_empty_row1_crafted_gatherer_marker_but_not_a_parameterized_bonus()
    {
        var fixture = Fixture();
        var modeled = fixture.Candidate with { ItemSpecialBonusId = 1, ItemSpecialBonusParam = 0 };
        var unmodeled = modeled with { ItemSpecialBonusParam = 1 };

        var accepted = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            _ => [modeled],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId);
        var rejected = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            _ => [unmodeled],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId);

        Assert.Equal(MinerBotanistAdvisorStatus.Complete, accepted.Status);
        Assert.NotNull(accepted.Nomination);
        Assert.Equal(MinerBotanistAdvisorStatus.Abstained, rejected.Status);
        Assert.Contains("unmodeled effect", rejected.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_considers_vendor_and_market_costs_on_the_same_frontier()
    {
        var fixture = Fixture();
        var vendorDefinition = Definition(3_000, "Vendor Pickaxe", EquipmentSlot.MainHand, 400, 400);
        var vendor = new EquipmentLoadoutOffer(
            vendorDefinition,
            EquipmentAcquisitionSourceKind.GilVendor,
            "Vendor · Old Gridania",
            15_000,
            Quality: EquipmentQuality.Normal,
            SourceCatalogKey: "vendor:old-gridania:3000");
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId,
            [vendor]);

        Assert.NotNull(advice.Nomination);
        Assert.Equal((ulong)15_000, advice.Nomination!.AcquisitionCostGil);
        Assert.Contains(advice.Nomination.Candidate.Selections, value => value.OfferKey.SourceKind == EquipmentAcquisitionSourceKind.GilVendor);
    }

    [Fact]
    public void Build_supports_level85_player_in_default_ordinary_context()
    {
        var fixture = Fixture(characterLevel: 85, candidatePerception: 1);

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.OrdinaryResourceBenchmarkContextId);

        Assert.Equal(MinerBotanistAdvisorStatus.Complete, advice.Status);
        Assert.NotNull(advice.Nomination);
        Assert.Contains(
            advice.Nomination!.Candidate.Selections,
            value => value.Position == EquipmentLoadoutPosition.MainHand && value.OfferKey.Quality == EquipmentQuality.High);
        Assert.Contains(
            advice.AuthorityBySolutionId[advice.Nomination.Candidate.SolutionId].GainedCapabilityIds,
            value => value == "ordinary-balanced-stat-dominance");
    }

    [Fact]
    public void Build_prunes_higher_priced_duplicate_listings_but_keeps_two_units_for_rings()
    {
        var fixture = Fixture();
        var ring = Definition(4_000, "Threshold Ring", EquipmentSlot.Ring, 0, 1);
        var now = fixture.Evidence.CreatedAtUtc;
        var listings = new[]
        {
            new OutfitterMarketListingEvidence(ring.ItemId, EquipmentQuality.High, "first", "Siren", 1, "A", "1", 1, 10_000, now, now, "r1"),
            new OutfitterMarketListingEvidence(ring.ItemId, EquipmentQuality.High, "second", "Siren", 1, "B", "2", 1, 11_000, now, now, "r1"),
            new OutfitterMarketListingEvidence(ring.ItemId, EquipmentQuality.High, "dominated", "Siren", 1, "C", "3", 1, 99_000, now, now, "r1"),
        };
        var evidence = fixture.Evidence with
        {
            Coverage = new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 1, 1, 100, [ring.ItemId]),
            Items = [new(ring.ItemId, OutfitterMarketEvidenceItemStatus.Fresh, listings, now, "r1")],
        };

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            evidence,
            _ => [ring],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId);

        Assert.Equal(2, advice.OffersByAllocation.Values.Count(value => value.Offer.Definition.ItemId == ring.ItemId));
        Assert.DoesNotContain(advice.OffersByAllocation.Keys, value => value.ObservationId == "dominated");
    }

    [Fact]
    public void Build_accepts_two_handed_main_hand_with_authoritative_empty_offhand()
    {
        var fixture = Fixture();
        var slots = fixture.Baseline.EquippedSlots.Select(slot => slot.Position switch
        {
            EquipmentLoadoutPosition.MainHand => slot with
            {
                Definition = slot.Definition! with { OffHandOccupancy = -1 },
            },
            EquipmentLoadoutPosition.OffHand => slot with
            {
                Instance = null,
                Definition = null,
                Quality = null,
                Utility = EquipmentSolverUtilityVector.Empty,
                MateriaIds = [],
                MateriaGrades = [],
            },
            _ => slot,
        }).ToArray();
        var baseline = fixture.Baseline with { EquippedSlots = slots };

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            GathererAdvisorStatFamily.Instance,
            MinerBotanistUtilityProfile.LegendaryContextId);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.NotNull(advice.Frontier);
    }

    [Fact]
    public void Build_prefers_a_capable_owned_item_over_any_paid_alternative()
    {
        var fixture = Fixture();
        var ownedDefinition = Definition(4_000, "Owned Hatchet", EquipmentSlot.MainHand, 399, 420, equipLevel: 100);
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : itemId == ownedDefinition.ItemId ? [ownedDefinition] : [],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId,
            vendorOffers: null,
            ownedItems: [new(ownedDefinition.ItemId, true, "Armoury")]);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.NotNull(advice.Nomination);
        Assert.Equal(0UL, advice.Nomination!.AcquisitionCostGil);
        Assert.Contains(
            advice.Nomination.Candidate.Selections,
            value => value.Position == EquipmentLoadoutPosition.MainHand &&
                     value.OfferKey.SourceKind == EquipmentAcquisitionSourceKind.Owned &&
                     value.OfferKey.Quality == EquipmentQuality.High);
    }

    [Fact]
    public void Build_preserves_distinct_identity_for_duplicate_owned_instances()
    {
        var fixture = Fixture();
        var ownedDefinition = Definition(4_010, "Twin Hatchet", EquipmentSlot.MainHand, 399, 420, equipLevel: 100);
        EquipmentInstanceSnapshot Instance(int slot) => new(
            new(
                fixture.Baseline.Character!,
                "ArmoryMainHand",
                slot,
                ownedDefinition.ItemId,
                true,
                1,
                30_000,
                0,
                null,
                [],
                null,
                []),
            DateTimeOffset.UtcNow,
            false);
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : itemId == ownedDefinition.ItemId ? [ownedDefinition] : [],
            GathererAdvisorStatFamily.Instance,
            MinerBotanistUtilityProfile.LegendaryContextId,
            ownedItems:
            [
                new(ownedDefinition.ItemId, true, "Armoury", Instance(1)),
                new(ownedDefinition.ItemId, true, "Armoury", Instance(2)),
            ]);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        var allocations = advice.OffersByAllocation.Values
            .Where(value => value.Offer.Definition.ItemId == ownedDefinition.ItemId)
            .ToArray();
        Assert.Equal(2, allocations.Length);
        Assert.Equal(2, allocations.Select(value => value.AllocationKey).Distinct().Count());
    }

    [Fact]
    public void Build_skips_owned_cross_discipline_poison()
    {
        var fixture = Fixture();
        var craftingOnly = Definition(4_001, "Crafting Vest", EquipmentSlot.Body, 0, 0, equipLevel: 100);
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : itemId == craftingOnly.ItemId ? [craftingOnly] : [],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId,
            vendorOffers: null,
            ownedItems: [new(craftingOnly.ItemId, true, "Armoury")]);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.DoesNotContain(advice.OffersByAllocation.Values, value => value.Offer.Definition.ItemId == craftingOnly.ItemId);
        Assert.Equal((ulong)25_000, advice.Nomination!.AcquisitionCostGil);
    }

    [Fact]
    public void Build_skips_owned_items_ineligible_for_the_current_job_without_abstaining()
    {
        var fixture = Fixture();
        var wrongJob = Definition(4_002, "Other Job Tool", EquipmentSlot.MainHand, 399, 900, equipLevel: 100) with
        {
            EligibleClassJobIds = new HashSet<uint> { 99 },
        };
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : itemId == wrongJob.ItemId ? [wrongJob] : [],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId,
            vendorOffers: null,
            ownedItems: [new(wrongJob.ItemId, true, "Armoury")]);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.DoesNotContain(advice.OffersByAllocation.Values, value => value.Offer.Definition.ItemId == wrongJob.ItemId);
        Assert.Equal((ulong)25_000, advice.Nomination!.AcquisitionCostGil);
    }

    [Fact]
    public void Build_evaluates_owned_items_at_their_exact_owned_quality()
    {
        var fixture = Fixture();
        var ownedDefinition = Definition(4_003, "Owned Pick", EquipmentSlot.MainHand, 399, 410, equipLevel: 100);
        var nqAdvice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : itemId == ownedDefinition.ItemId ? [ownedDefinition] : [],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId,
            vendorOffers: null,
            ownedItems: [new(ownedDefinition.ItemId, false, "Armoury")]);
        var hqAdvice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : itemId == ownedDefinition.ItemId ? [ownedDefinition] : [],
            GathererAdvisorStatFamily.Instance, MinerBotanistUtilityProfile.LegendaryContextId,
            vendorOffers: null,
            ownedItems: [new(ownedDefinition.ItemId, true, "Armoury")]);

        Assert.Contains(nqAdvice.OffersByAllocation.Values, value =>
            value.Offer.SourceKind == EquipmentAcquisitionSourceKind.Owned && value.Offer.ResolvedQuality == EquipmentQuality.Normal);
        Assert.Contains(hqAdvice.OffersByAllocation.Values, value =>
            value.Offer.SourceKind == EquipmentAcquisitionSourceKind.Owned && value.Offer.ResolvedQuality == EquipmentQuality.High);
        Assert.True(
            hqAdvice.Nomination!.Utility.UtilityScore > nqAdvice.Nomination!.Utility.UtilityScore ||
            hqAdvice.Nomination.AcquisitionCostGil < nqAdvice.Nomination.AcquisitionCostGil,
            "The HQ-owned evaluation must be at least as attractive as the NQ-owned one.");
    }

    private static FixtureData Fixture(uint characterLevel = 100, int candidatePerception = 0)
    {
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
        var scope = new CharacterScope(77, "Advisor", 21);
        var equipped = new List<PlayerAdvisorEquippedSlot>();
        var instances = new List<EquipmentInstanceSnapshot>();
        var definitions = new Dictionary<uint, EquipmentItemDefinition>();
        var itemId = 1_000u;
        foreach (var (key, position, slot) in positions)
        {
            var definition = Definition(itemId++, $"Current {key}", slot, 399, 399, equipLevel: characterLevel);
            var index = PlayerAdvisorEquippedSlotMap.All.Single(value => value.Position == position).EquippedIndex;
            var instance = new EquipmentInstanceSnapshot(
                new EquipmentInstanceFingerprint(scope, "EquippedItems", index, definition.ItemId, false, 1, 30_000, 0, null, [], null, []),
                DateTimeOffset.UtcNow,
                true);
            var semantics = new Dictionary<EquipmentStatSemantic, int>
            {
                [EquipmentStatSemantic.Gathering] = key == "main-hand" ? 399 : 0,
                [EquipmentStatSemantic.Perception] = 0,
                [EquipmentStatSemantic.GatheringPoints] = 0,
            };
            equipped.Add(new(position, key, instance, definition, EquipmentQuality.Normal,
                GathererAdvisorStatFamily.Instance.VectorFromSemantics(semantics), [], []));
            instances.Add(instance);
            definitions.Add(definition.ItemId, definition);
        }

        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(scope, 21, 16, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            [],
            [],
            instances,
            definitions,
            new([new("identity", SnapshotComponentStatus.Complete), new("equipped", SnapshotComponentStatus.Complete)]));
        var baseline = new PlayerAdvisorBaseline(
            PlayerAdvisorBaselineStatus.Complete,
            scope,
            16,
            checked((short)characterLevel),
            checked((short)characterLevel),
            false,
            new Dictionary<EquipmentStatSemantic, int>
            {
                [EquipmentStatSemantic.Gathering] = 5_399,
                [EquipmentStatSemantic.Perception] = 5_200,
                [EquipmentStatSemantic.GatheringPoints] = 950,
            },
            new Dictionary<EquipmentStatSemantic, int>
            {
                [EquipmentStatSemantic.Gathering] = 5_000,
                [EquipmentStatSemantic.Perception] = 5_200,
                [EquipmentStatSemantic.GatheringPoints] = 950,
            },
            equipped,
            snapshot,
            "Complete");
        var candidate = Definition(
            2_000,
            "Threshold Pickaxe",
            EquipmentSlot.MainHand,
            399,
            400,
            highPerception: candidatePerception,
            equipLevel: characterLevel);
        var now = new DateTimeOffset(2026, 7, 16, 20, 0, 0, TimeSpan.Zero);
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
                    new(candidate.ItemId, EquipmentQuality.Normal, "nq", "Siren", 1, "NQ", "1", 1, 10_000, now, now, "r1"),
                    new(candidate.ItemId, EquipmentQuality.High, "hq", "Siren", 1, "HQ", "2", 1, 25_000, now, now, "r1"),
                ], now, "r1"),
            ]);
        return new(baseline, evidence, candidate);
    }

    private static EquipmentItemDefinition Definition(
        uint itemId,
        string name,
        EquipmentSlot slot,
        int normalGathering,
        int highGathering,
        int normalPerception = 0,
        int highPerception = 0,
        uint equipLevel = 100)
    {
        EquipmentStatProfile Profile(int gathering, int perception) => new(
            [
                ..gathering == 0 ? [] : new EquipmentStatValue[] { new(72, EquipmentStatSemantic.Gathering, gathering, false, "Gathering") },
                ..perception == 0 ? [] : new EquipmentStatValue[] { new(73, EquipmentStatSemantic.Perception, perception, false, "Perception") },
            ],
            0, 0, 0, 0, true);
        return new(
            itemId,
            name,
            equipLevel,
            700,
            slot,
            new HashSet<uint> { 16, 17 },
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
            StatProfile: Profile(normalGathering, normalPerception),
            HighQualityStatProfile: Profile(highGathering, highPerception));
    }

    private sealed record FixtureData(
        PlayerAdvisorBaseline Baseline,
        OutfitterMarketEvidenceBook Evidence,
        EquipmentItemDefinition Candidate);
}
