using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class TankReadOnlyAdvisorTests
{
    [Fact]
    public void Marauder_builds_a_frontier_and_nominates_an_exact_no_loss_weapon_upgrade()
    {
        var fixture = Fixture();

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            TankAdvisorStatFamily.Instance,
            TankUtilityProfile.GeneralCombatContextId);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.NotNull(advice.Frontier);
        Assert.NotNull(advice.Nomination);
        Assert.Contains(advice.Nomination!.Candidate.Selections, selection =>
            advice.OffersByAllocation[selection.AllocationKey].Offer.Definition.ItemId == fixture.Candidate.ItemId);
        Assert.True(advice.AuthorityBySolutionId[advice.Nomination.Candidate.SolutionId].AdvisorMayConsider);
    }

    private static FixtureData Fixture()
    {
        const uint currentItemId = 70_000;
        const uint candidateItemId = 70_001;
        var scope = new CharacterScope(99, "Marauder", 21);
        var currentStats = new TankUtilityStats(40, 50, 20, 0, 0, 2, 2, 0, 2, 1);
        var candidateStats = currentStats with { Strength = 42, PhysicalDamage = 21 };
        var candidate = Definition(candidateItemId, "Better axe", EquipmentSlot.MainHand, candidateStats, occupiesOffHand: true);
        var instances = new List<EquipmentInstanceSnapshot>();
        var definitions = new Dictionary<uint, EquipmentItemDefinition>();
        var slots = new List<PlayerAdvisorEquippedSlot>();
        foreach (var position in PlayerAdvisorEquippedSlotMap.All)
        {
            if (position.Position == EquipmentLoadoutPosition.OffHand)
            {
                slots.Add(new(position.Position, position.PositionKey, null, null, null,
                    TankAdvisorStatFamily.Instance.VectorFromSemantics(new Dictionary<EquipmentStatSemantic, int>()), [], []));
                continue;
            }

            var itemId = position.Position == EquipmentLoadoutPosition.MainHand
                ? currentItemId
                : checked((uint)(currentItemId + position.EquippedIndex + 1));
            var stats = position.Position == EquipmentLoadoutPosition.MainHand
                ? currentStats
                : new TankUtilityStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var definition = Definition(itemId, $"Current {position.PositionKey}", SlotFor(position.Position), stats,
                occupiesOffHand: position.Position == EquipmentLoadoutPosition.MainHand);
            var instance = new EquipmentInstanceSnapshot(
                new(scope, "EquippedItems", position.EquippedIndex, itemId, false, 1, 30_000, 0, null, [], null, []),
                DateTimeOffset.UtcNow,
                true);
            instances.Add(instance);
            definitions.Add(itemId, definition);
            slots.Add(new(position.Position, position.PositionKey, instance, definition, EquipmentQuality.Normal,
                TankUtilityProfile.ToVector(stats), [], []));
        }
        var fixedStats = TankAdvisorStatFamily.Instance.RelevantSemantics.ToDictionary(semantic => semantic, _ => 1_000);
        var equipped = Semantics(currentStats);
        var totals = fixedStats.ToDictionary(value => value.Key, value => checked(value.Value + equipped[value.Key]));
        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(scope, 21, TankUtilityProfile.MarauderClassJobId, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            [],
            [],
            instances,
            definitions,
            new([new("identity", SnapshotComponentStatus.Complete), new("equipped", SnapshotComponentStatus.Complete),
                new("armoury", SnapshotComponentStatus.Complete), new("inventory", SnapshotComponentStatus.Complete)]));
        var baseline = new PlayerAdvisorBaseline(
            PlayerAdvisorBaselineStatus.Complete,
            scope,
            TankUtilityProfile.MarauderClassJobId,
            10,
            10,
            false,
            totals,
            fixedStats,
            slots,
            snapshot,
            "Complete");
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var evidence = new OutfitterMarketEvidenceBook(
            Guid.NewGuid(),
            1,
            OutfitterMarketEvidenceBook.CurrentSchemaVersion,
            "fixture",
            "NA",
            now,
            now,
            OutfitterMarketEvidenceGenerationStatus.Complete,
            new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 1, 1, 100, [candidateItemId]),
            [new(candidateItemId, OutfitterMarketEvidenceItemStatus.Fresh,
                [new(candidateItemId, EquipmentQuality.Normal, "nq", "Siren", 1, "NQ", "2", 1, 500, now, now, "r1")],
                now,
                "r1")]);
        return new(baseline, evidence, candidate);
    }

    private static IReadOnlyDictionary<EquipmentStatSemantic, int> Semantics(TankUtilityStats stats) =>
        new Dictionary<EquipmentStatSemantic, int>
        {
            [EquipmentStatSemantic.Strength] = stats.Strength,
            [EquipmentStatSemantic.Vitality] = stats.Vitality,
            [EquipmentStatSemantic.PhysicalDamage] = stats.PhysicalDamage,
            [EquipmentStatSemantic.PhysicalDefense] = stats.PhysicalDefense,
            [EquipmentStatSemantic.MagicalDefense] = stats.MagicalDefense,
            [EquipmentStatSemantic.CriticalHit] = stats.CriticalHit,
            [EquipmentStatSemantic.Determination] = stats.Determination,
            [EquipmentStatSemantic.DirectHit] = stats.DirectHit,
            [EquipmentStatSemantic.Tenacity] = stats.Tenacity,
            [EquipmentStatSemantic.SkillSpeed] = stats.SkillSpeed,
        };

    private static EquipmentItemDefinition Definition(
        uint itemId,
        string name,
        EquipmentSlot slot,
        TankUtilityStats stats,
        bool occupiesOffHand)
    {
        var profile = new EquipmentStatProfile(
            [
                new(1, EquipmentStatSemantic.Strength, stats.Strength, false),
                new(3, EquipmentStatSemantic.Vitality, stats.Vitality, false),
                new(27, EquipmentStatSemantic.CriticalHit, stats.CriticalHit, false),
                new(44, EquipmentStatSemantic.Determination, stats.Determination, false),
                new(22, EquipmentStatSemantic.DirectHit, stats.DirectHit, false),
                new(19, EquipmentStatSemantic.Tenacity, stats.Tenacity, false),
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
            10,
            10,
            slot,
            new HashSet<uint> { TankUtilityProfile.MarauderClassJobId },
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
            MainHandOccupancy: occupiesOffHand ? (sbyte)1 : (sbyte)0,
            OffHandOccupancy: occupiesOffHand ? (sbyte)-1 : (sbyte)0);
    }

    private static EquipmentSlot SlotFor(EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => EquipmentSlot.MainHand,
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
