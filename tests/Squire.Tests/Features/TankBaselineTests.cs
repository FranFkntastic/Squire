using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class TankBaselineTests
{
    private static readonly CharacterScope Scope = new(88, "Tank", 21);
    private static readonly PlayerAdvisorCaptureHeader Header = new(
        Scope,
        21,
        TankUtilityProfile.MarauderClassJobId,
        10,
        10,
        false);

    [Fact]
    public void Marauder_baseline_reconciles_all_tank_semantics()
    {
        var fixture = Fixture();

        var baseline = PlayerAdvisorBaselineAssembler.Assemble(
            fixture.Snapshot,
            Header,
            TankAdvisorStatFamily.Instance,
            fixture.Totals,
            fixture.Captures);

        Assert.Equal(PlayerAdvisorBaselineStatus.Complete, baseline.Status);
        Assert.Equal(12, baseline.EquippedSlots.Count);
        Assert.Equal(20, baseline.EquippedSlots.Single(value => value.Position == EquipmentLoadoutPosition.MainHand).Utility.Get("physical-damage"));
        Assert.Equal(500, baseline.FixedStats[EquipmentStatSemantic.Strength]);
        Assert.Equal(300, baseline.FixedStats[EquipmentStatSemantic.Tenacity]);
        Assert.All(TankAdvisorStatFamily.Instance.RelevantSemantics, semantic =>
            Assert.True(baseline.FixedStats.ContainsKey(semantic), $"Missing fixed {semantic}."));
    }

    private static FixtureData Fixture()
    {
        var instances = new List<EquipmentInstanceSnapshot>();
        var definitions = new Dictionary<uint, EquipmentItemDefinition>();
        var captures = new List<PlayerAdvisorEquippedItemCapture>();
        var equippedTotals = TankAdvisorStatFamily.Instance.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0);
        foreach (var position in PlayerAdvisorEquippedSlotMap.All)
        {
            var itemId = checked((uint)(60_000 + position.EquippedIndex));
            var contributions = TankAdvisorStatFamily.Instance.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0);
            contributions[EquipmentStatSemantic.Strength] = position.Position == EquipmentLoadoutPosition.MainHand ? 40 : 4;
            contributions[EquipmentStatSemantic.Vitality] = 5;
            contributions[EquipmentStatSemantic.PhysicalDamage] = position.Position == EquipmentLoadoutPosition.MainHand ? 20 : 0;
            contributions[EquipmentStatSemantic.PhysicalDefense] = IsArmor(position.Position) ? 20 : 0;
            contributions[EquipmentStatSemantic.MagicalDefense] = IsArmor(position.Position) ? 18 : 0;
            contributions[EquipmentStatSemantic.CriticalHit] = 2;
            contributions[EquipmentStatSemantic.Determination] = 2;
            contributions[EquipmentStatSemantic.DirectHit] = 0;
            contributions[EquipmentStatSemantic.Tenacity] = 2;
            contributions[EquipmentStatSemantic.SkillSpeed] = 1;
            foreach (var semantic in contributions.Keys)
                equippedTotals[semantic] += contributions[semantic];

            var instance = new EquipmentInstanceSnapshot(
                new(Scope, "EquippedItems", position.EquippedIndex, itemId, false, 1, 30_000, 0, null, [], null, []),
                DateTimeOffset.UtcNow,
                true);
            instances.Add(instance);
            definitions.Add(itemId, Definition(itemId, SlotFor(position.Position)));
            captures.Add(new(position.EquippedIndex, itemId, EquipmentQuality.Normal, contributions, [], []));
        }

        var fixedStats = TankAdvisorStatFamily.Instance.RelevantSemantics.ToDictionary(semantic => semantic, _ => 300);
        fixedStats[EquipmentStatSemantic.Strength] = 500;
        var totals = equippedTotals.ToDictionary(value => value.Key, value => value.Value + fixedStats[value.Key]);
        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(Scope, 21, Header.ClassJobId, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            [],
            [],
            instances,
            definitions,
            new([new("identity", SnapshotComponentStatus.Complete), new("equipped", SnapshotComponentStatus.Complete)]));
        return new(snapshot, totals, captures);
    }

    private static bool IsArmor(EquipmentLoadoutPosition position) => position is
        EquipmentLoadoutPosition.Head or EquipmentLoadoutPosition.Body or EquipmentLoadoutPosition.Hands or
        EquipmentLoadoutPosition.Legs or EquipmentLoadoutPosition.Feet;

    private static EquipmentItemDefinition Definition(uint itemId, EquipmentSlot slot) => new(
        itemId,
        $"Tank {slot}",
        1,
        1,
        slot,
        new HashSet<uint> { Header.ClassJobId },
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
        StatProfile: new([], slot == EquipmentSlot.MainHand ? 20 : 0, 0, 0, 0, true));

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
        CharacterEquipmentSnapshot Snapshot,
        IReadOnlyDictionary<EquipmentStatSemantic, int> Totals,
        IReadOnlyList<PlayerAdvisorEquippedItemCapture> Captures);
}
