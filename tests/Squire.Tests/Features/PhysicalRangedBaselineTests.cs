using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class PhysicalRangedBaselineTests
{
    private static readonly CharacterScope Scope = new(88, "Ranged", 21);
    private static readonly PlayerAdvisorCaptureHeader Header = new(
        Scope,
        21,
        PhysicalRangedUtilityProfile.BardClassJobId,
        100,
        100,
        false);

    [Fact]
    public void Assemble_reconciles_all_nine_physical_ranged_semantics()
    {
        var fixture = Fixture();

        var baseline = PlayerAdvisorBaselineAssembler.Assemble(
            fixture.Snapshot,
            Header,
            PhysicalRangedAdvisorStatFamily.Instance,
            fixture.Totals,
            fixture.Captures);

        Assert.Equal(PlayerAdvisorBaselineStatus.Complete, baseline.Status);
        Assert.Equal(12, baseline.EquippedSlots.Count);
        Assert.Equal(140, baseline.EquippedSlots.Single(value => value.Position == EquipmentLoadoutPosition.MainHand).Utility.Get("physical-damage"));
        Assert.Equal(1_000, baseline.FixedStats[EquipmentStatSemantic.Dexterity]);
        Assert.Equal(500, baseline.FixedStats[EquipmentStatSemantic.PhysicalDefense]);
        Assert.All(PhysicalRangedAdvisorStatFamily.Instance.RelevantSemantics, semantic =>
            Assert.True(baseline.FixedStats.ContainsKey(semantic), $"Missing fixed {semantic}."));
    }

    private static FixtureData Fixture()
    {
        var instances = new List<EquipmentInstanceSnapshot>();
        var definitions = new Dictionary<uint, EquipmentItemDefinition>();
        var captures = new List<PlayerAdvisorEquippedItemCapture>();
        var equippedTotals = PhysicalRangedAdvisorStatFamily.Instance.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0);
        foreach (var position in PlayerAdvisorEquippedSlotMap.All)
        {
            var itemId = checked((uint)(20_000 + position.EquippedIndex));
            var instance = new EquipmentInstanceSnapshot(
                new(Scope, "EquippedItems", position.EquippedIndex, itemId, false, 1, 30_000, 0, null, [], null, []),
                DateTimeOffset.UtcNow,
                true);
            var contributions = PhysicalRangedAdvisorStatFamily.Instance.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0);
            contributions[EquipmentStatSemantic.Dexterity] = position.Position == EquipmentLoadoutPosition.MainHand ? 100 : 20;
            contributions[EquipmentStatSemantic.Vitality] = 20;
            contributions[EquipmentStatSemantic.PhysicalDamage] = position.Position == EquipmentLoadoutPosition.MainHand ? 140 : 0;
            contributions[EquipmentStatSemantic.PhysicalDefense] = position.Position is >= EquipmentLoadoutPosition.Head and <= EquipmentLoadoutPosition.Feet ? 200 : 0;
            contributions[EquipmentStatSemantic.MagicalDefense] = position.Position is >= EquipmentLoadoutPosition.Head and <= EquipmentLoadoutPosition.Feet ? 180 : 0;
            contributions[EquipmentStatSemantic.CriticalHit] = 10;
            contributions[EquipmentStatSemantic.Determination] = 8;
            contributions[EquipmentStatSemantic.DirectHit] = 6;
            contributions[EquipmentStatSemantic.SkillSpeed] = 4;
            foreach (var semantic in contributions.Keys)
                equippedTotals[semantic] += contributions[semantic];
            instances.Add(instance);
            definitions.Add(itemId, Definition(itemId, SlotFor(position.Position)));
            captures.Add(new(position.EquippedIndex, itemId, EquipmentQuality.Normal, contributions, [], []));
        }

        var fixedStats = PhysicalRangedAdvisorStatFamily.Instance.RelevantSemantics.ToDictionary(semantic => semantic, _ => 500);
        fixedStats[EquipmentStatSemantic.Dexterity] = 1_000;
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

    private static EquipmentItemDefinition Definition(uint itemId, EquipmentSlot slot) => new(
        itemId,
        $"Physical ranged {slot}",
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
        StatProfile: Profile(slot == EquipmentSlot.MainHand ? 140 : 0, 0, 0));

    private static EquipmentStatProfile Profile(int damage, int physicalDefense, int magicalDefense) =>
        new([], damage, 0, physicalDefense, magicalDefense, true);

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
