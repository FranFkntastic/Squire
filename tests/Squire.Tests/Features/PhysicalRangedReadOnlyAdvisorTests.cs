using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class PhysicalRangedReadOnlyAdvisorTests
{
    [Theory]
    [InlineData(PhysicalRangedUtilityProfile.BardClassJobId)]
    [InlineData(PhysicalRangedUtilityProfile.MachinistClassJobId)]
    [InlineData(PhysicalRangedUtilityProfile.DancerClassJobId)]
    public void Shared_family_builds_frontier_but_experimental_gate_blocks_nomination(uint classJobId)
    {
        var fixture = Fixture(classJobId);

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            PhysicalRangedAdvisorStatFamily.Instance,
            PhysicalRangedUtilityProfile.GeneralCombatContextId);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.NotNull(advice.Frontier);
        Assert.Null(advice.Nomination);
        Assert.Contains(advice.OffersByAllocation.Values, offer =>
            offer.Offer.Definition.ItemId == fixture.Candidate.ItemId &&
            offer.Utility.Get("physical-damage") == 141);
        Assert.Contains(advice.AuthorityBySolutionId.Values, authority =>
            authority.Reasons.Any(reason => reason.Contains("experimental", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(PhysicalRangedUtilityProfile.BardClassJobId)]
    [InlineData(PhysicalRangedUtilityProfile.MachinistClassJobId)]
    [InlineData(PhysicalRangedUtilityProfile.DancerClassJobId)]
    public void Raw_dexterity_only_offer_stays_visible_without_nomination(uint classJobId)
    {
        var fixture = Fixture(classJobId, dexterityOnlyCandidate: true);

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            PhysicalRangedAdvisorStatFamily.Instance,
            PhysicalRangedUtilityProfile.GeneralCombatContextId);

        Assert.Equal(MinerBotanistAdvisorStatus.Complete, advice.Status);
        Assert.Null(advice.Nomination);
        Assert.Contains(advice.OffersByAllocation.Values, offer => offer.Offer.Definition.ItemId == fixture.Candidate.ItemId);
        Assert.Contains(advice.AuthorityBySolutionId.Values, authority =>
            authority.Reasons.Any(reason => reason.Contains("effective damage tiers", StringComparison.Ordinal)));
    }

    [Fact]
    public void Market_item_with_unmodeled_effect_fails_closed()
    {
        var fixture = Fixture(PhysicalRangedUtilityProfile.BardClassJobId, unmodeledCandidate: true);

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            PhysicalRangedAdvisorStatFamily.Instance,
            PhysicalRangedUtilityProfile.GeneralCombatContextId);

        Assert.Equal(MinerBotanistAdvisorStatus.Abstained, advice.Status);
        Assert.Contains("unmodeled effect", advice.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private static FixtureData Fixture(uint classJobId, bool unmodeledCandidate = false, bool dexterityOnlyCandidate = false)
    {
        var scope = new CharacterScope(99, "Physical Ranged", 21);
        var instances = new List<EquipmentInstanceSnapshot>();
        var definitions = new Dictionary<uint, EquipmentItemDefinition>();
        var slots = new List<PlayerAdvisorEquippedSlot>();
        var totals = PhysicalRangedAdvisorStatFamily.Instance.RelevantSemantics.ToDictionary(semantic => semantic, _ => 1_000);
        var fixedStats = PhysicalRangedAdvisorStatFamily.Instance.RelevantSemantics.ToDictionary(semantic => semantic, _ => 1_000);
        foreach (var position in PlayerAdvisorEquippedSlotMap.All)
        {
            var itemId = checked((uint)(30_000 + position.EquippedIndex));
            var utilityStats = new PhysicalRangedUtilityStats(
                position.Position == EquipmentLoadoutPosition.MainHand ? 400 : 100,
                100,
                position.Position == EquipmentLoadoutPosition.MainHand ? 140 : 0,
                position.Position is EquipmentLoadoutPosition.Head or EquipmentLoadoutPosition.Body or EquipmentLoadoutPosition.Hands or EquipmentLoadoutPosition.Legs or EquipmentLoadoutPosition.Feet ? 200 : 0,
                position.Position is EquipmentLoadoutPosition.Head or EquipmentLoadoutPosition.Body or EquipmentLoadoutPosition.Hands or EquipmentLoadoutPosition.Legs or EquipmentLoadoutPosition.Feet ? 180 : 0,
                100,
                80,
                60,
                40);
            var definition = Definition(itemId, $"Current {position.PositionKey}", SlotFor(position.Position), classJobId, utilityStats);
            var instance = new EquipmentInstanceSnapshot(
                new(scope, "EquippedItems", position.EquippedIndex, itemId, false, 1, 30_000, 0, null, [], null, []),
                DateTimeOffset.UtcNow,
                true);
            var vector = PhysicalRangedUtilityProfile.ToVector(utilityStats);
            foreach (var semantic in PhysicalRangedAdvisorStatFamily.Instance.RelevantSemantics)
            {
                var key = ComponentKey(semantic);
                totals[semantic] += checked((int)vector.Get(key));
            }
            definitions.Add(itemId, definition);
            instances.Add(instance);
            slots.Add(new(position.Position, position.PositionKey, instance, definition, EquipmentQuality.Normal, vector, [], []));
        }

        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(scope, 21, classJobId, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            [],
            [],
            instances,
            definitions,
            new([new("identity", SnapshotComponentStatus.Complete), new("equipped", SnapshotComponentStatus.Complete)]));
        var baseline = new PlayerAdvisorBaseline(
            PlayerAdvisorBaselineStatus.Complete,
            scope,
            classJobId,
            100,
            100,
            false,
            totals,
            fixedStats,
            slots,
            snapshot,
            "Complete");
        var candidateStats = new PhysicalRangedUtilityStats(
            dexterityOnlyCandidate ? 401 : 400,
            100,
            dexterityOnlyCandidate ? 140 : 141,
            0,
            0,
            100,
            80,
            60,
            40);
        var candidate = Definition(
            40_000,
            dexterityOnlyCandidate ? "No-loss raw Dexterity offer" : "No-loss weapon upgrade",
            EquipmentSlot.MainHand,
            classJobId,
            candidateStats,
            unmodeledCandidate ? 9u : 0u);
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
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
            [new(candidate.ItemId, OutfitterMarketEvidenceItemStatus.Fresh,
                [new(candidate.ItemId, EquipmentQuality.Normal, "nq", "Siren", 1, "NQ", "2", 1, 10_000, now, now, "r1")],
                now,
                "r1")]);
        return new(baseline, evidence, candidate);
    }

    private static EquipmentItemDefinition Definition(
        uint itemId,
        string name,
        EquipmentSlot slot,
        uint classJobId,
        PhysicalRangedUtilityStats stats,
        uint itemActionId = 0)
    {
        var profile = new EquipmentStatProfile(
            [
                new(2, EquipmentStatSemantic.Dexterity, stats.Dexterity, false),
                new(3, EquipmentStatSemantic.Vitality, stats.Vitality, false),
                new(27, EquipmentStatSemantic.CriticalHit, stats.CriticalHit, false),
                new(44, EquipmentStatSemantic.Determination, stats.Determination, false),
                new(22, EquipmentStatSemantic.DirectHit, stats.DirectHit, false),
                new(45, EquipmentStatSemantic.SkillSpeed, stats.SkillSpeed, false),
            ],
            stats.PhysicalDamage,
            0,
            stats.PhysicalDefense,
            stats.MagicalDefense,
            true);
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
            false,
            true,
            false,
            StatProfile: profile,
            HighQualityStatProfile: profile,
            ItemActionId: itemActionId);
    }

    private static string ComponentKey(EquipmentStatSemantic semantic) => semantic switch
    {
        EquipmentStatSemantic.Dexterity => "dexterity",
        EquipmentStatSemantic.Vitality => "vitality",
        EquipmentStatSemantic.PhysicalDamage => "physical-damage",
        EquipmentStatSemantic.PhysicalDefense => "physical-defense",
        EquipmentStatSemantic.MagicalDefense => "magical-defense",
        EquipmentStatSemantic.CriticalHit => "critical-hit",
        EquipmentStatSemantic.Determination => "determination",
        EquipmentStatSemantic.DirectHit => "direct-hit",
        EquipmentStatSemantic.SkillSpeed => "skill-speed",
        _ => throw new ArgumentOutOfRangeException(nameof(semantic)),
    };

    private static EquipmentSlot SlotFor(EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => EquipmentSlot.MainHand,
        EquipmentLoadoutPosition.OffHand => EquipmentSlot.OffHand,
        EquipmentLoadoutPosition.Head => EquipmentSlot.Head,
        EquipmentLoadoutPosition.Body => EquipmentSlot.Body,
        EquipmentLoadoutPosition.Hands => EquipmentSlot.Hands,
        EquipmentLoadoutPosition.Legs => EquipmentSlot.Legs,
        EquipmentLoadoutPosition.Feet => EquipmentSlot.Feet,
        EquipmentLoadoutPosition.Ears => EquipmentSlot.Ears,
        EquipmentLoadoutPosition.Neck => EquipmentSlot.Neck,
        EquipmentLoadoutPosition.Wrists => EquipmentSlot.Wrists,
        EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing => EquipmentSlot.Ring,
        _ => throw new ArgumentOutOfRangeException(nameof(position)),
    };

    private sealed record FixtureData(
        PlayerAdvisorBaseline Baseline,
        OutfitterMarketEvidenceBook Evidence,
        EquipmentItemDefinition Candidate);
}
