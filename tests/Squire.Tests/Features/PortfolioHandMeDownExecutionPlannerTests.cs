using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Portfolio;

namespace MarketMafioso.Tests.Squire;

public sealed class PortfolioHandMeDownExecutionPlannerTests
{
    [Fact]
    public void Full_loadout_with_one_changed_slot_emits_only_that_physical_swap()
    {
        var owned = new PortfolioAllocationKey(PortfolioAllocationSourceKind.OwnedInstance, "owned:changed", 300, false, "10:Inventory1:0:300:False");
        var unchanged = new PortfolioExactItem(100, false, "10:EquippedItems:2:100:False");
        var changed = new PortfolioExactItem(300, false, owned.InstanceId!);
        var candidate = new PortfolioCandidate(
            "active-loadout",
            "candidate",
            10,
            [new(owned, 1)],
            [new(EquipmentLoadoutPosition.Head, unchanged), new(EquipmentLoadoutPosition.Body, changed)],
            []);
        var plan = new OutfitterPortfolioPlan(
            [new("active-loadout", "Active", 100, PortfolioProgressionHorizon.Immediate)],
            [candidate],
            []);
        plan = Complete(plan, "active-loadout");
        var lineage = new PortfolioAuthorityLineage("owner", "owner-gen", "inventory-gen", "listing-gen", []);
        var authority = PortfolioAuthorityEnvelopeFactory.Create(lineage, plan);
        var owner = new PortfolioEquipmentMoveOwner(10, "Fran Example", 57);
        var target = new PortfolioEquipmentMoveTarget("active-loadout", PortfolioEquipmentMoveTargetKind.ActivePlayer, owner, 3);
        var steps = PortfolioHandMeDownExecutionPlanner.Build(
            authority,
            plan,
            [new(owned, owner, new("Inventory1", 0), changed, "inventory-gen")],
            [
                new(target, EquipmentLoadoutPosition.Head, new("EquippedItems", 2), unchanged, "equipment-gen"),
                new(target, EquipmentLoadoutPosition.Body, new("EquippedItems", 3), new(200, false, "10:EquippedItems:3:200:False"), "equipment-gen"),
            ]);

        var step = Assert.Single(steps);
        Assert.Equal(EquipmentLoadoutPosition.Body, step.Step.Position);
        Assert.Equal(300u, step.Step.ExpectedAfter.ItemId);
    }

    private static readonly PortfolioEquipmentMoveOwner Owner = new(42, "A Hero", 67);

    [Fact]
    public void Build_OrdersUpstreamSwapBeforeHandDownAndRebindsReleasedItemToFreshSourceAddress()
    {
        var newHatSource = new PortfolioEquipmentSlotAddress("Inventory1", 5);
        var playerWorn = new PortfolioEquipmentSlotAddress("EquippedItems", 2);
        var retainerWorn = new PortfolioEquipmentSlotAddress("RetainerEquippedItems", 2);
        var newHat = Exact(newHatSource, 200, highQuality: true);
        var oldHatAtStaleWornAddress = Exact(playerWorn, 100, highQuality: false);
        var newHatAllocation = new PortfolioAllocationKey(
            PortfolioAllocationSourceKind.OwnedInstance,
            "owned:new-hat",
            newHat.ItemId,
            newHat.IsHighQuality,
            newHat.InstanceId);
        var handDownAllocation = new PortfolioAllocationKey(
            PortfolioAllocationSourceKind.HandMeDown,
            "hand-down:old-hat",
            oldHatAtStaleWornAddress.ItemId,
            oldHatAtStaleWornAddress.IsHighQuality,
            oldHatAtStaleWornAddress.InstanceId);
        var player = new PortfolioCandidate(
            "active-loadout",
            "upgrade-player",
            10,
            [new(newHatAllocation, 1)],
            [new(EquipmentLoadoutPosition.Head, newHat)],
            [new(
                "player-worn-7",
                EquipmentLoadoutPosition.Head,
                oldHatAtStaleWornAddress,
                newHat,
                handDownAllocation)]);
        var retainer = new PortfolioCandidate(
            "retainer:9001",
            "upgrade-retainer",
            5,
            [new(handDownAllocation, 1)],
            [new(EquipmentLoadoutPosition.Head, oldHatAtStaleWornAddress)],
            []);
        var plan = OutfitterPortfolioPlanner.Plan(
            [
                new("active-loadout", "Player", 100, PortfolioProgressionHorizon.Immediate),
                new("retainer:9001", "Bobo", 10, PortfolioProgressionHorizon.NearTerm),
            ],
            [player, retainer],
            [new(newHatAllocation, 1)]);
        plan = Complete(plan, "active-loadout", "retainer:9001");
        var authority = PortfolioAuthorityEnvelopeFactory.Create(
            new("owner-42", "owner-3", "inventory-7", "listing-9", [new("retainer:9001", "retainer-worn-4")]),
            plan);
        var playerTarget = new PortfolioEquipmentMoveTarget(
            "active-loadout",
            PortfolioEquipmentMoveTargetKind.ActivePlayer,
            Owner,
            ActiveClassJobId: 1);
        var retainerTarget = new PortfolioEquipmentMoveTarget(
            "retainer:9001",
            PortfolioEquipmentMoveTargetKind.Retainer,
            Owner,
            RetainerName: "Bobo");

        var swaps = PortfolioHandMeDownExecutionPlanner.Build(
            authority,
            plan,
            [new(newHatAllocation, Owner, newHatSource, newHat, "inventory-7")],
            [
                new(playerTarget, EquipmentLoadoutPosition.Head, playerWorn, oldHatAtStaleWornAddress, "player-worn-7"),
                new(retainerTarget, EquipmentLoadoutPosition.Head, retainerWorn, null, "retainer-worn-4"),
            ]);

        Assert.Collection(
            swaps,
            upstream =>
            {
                Assert.Equal(0, upstream.Order);
                Assert.Equal(newHatSource, upstream.SourceAddress);
                Assert.Null(upstream.RequiresFreshSourceEvidenceAfterStepId);
                Assert.Equal(oldHatAtStaleWornAddress, upstream.Step.ExpectedBefore);
            },
            downstream =>
            {
                Assert.Equal(1, downstream.Order);
                Assert.Equal(newHatSource, downstream.SourceAddress);
                Assert.Equal(swaps[0].Step.StepId, downstream.RequiresFreshSourceEvidenceAfterStepId);
                Assert.Null(downstream.SourceEvidenceGeneration);
                Assert.NotEqual(oldHatAtStaleWornAddress.InstanceId, downstream.Step.SourceItem.InstanceId);
                Assert.Equal(
                    PortfolioEquipmentMove.ExactInstanceId(Owner, newHatSource, new(100, false)),
                    downstream.Step.SourceItem.InstanceId);
                Assert.DoesNotContain("EquippedItems:2", downstream.Step.SourceItem.InstanceId, StringComparison.Ordinal);
                Assert.Equal(retainerWorn, downstream.DestinationAddress);
                Assert.Equal(
                    PortfolioEquipmentMove.ExactInstanceId(Owner, retainerWorn, new(100, false)),
                    downstream.Step.ExpectedAfter.InstanceId);
            });

        var chain = Assert.Single(authority.HandMeDownChain);
        Assert.Equal("active-loadout", chain.UpstreamTargetKey);
        Assert.Equal("retainer:9001", chain.DownstreamTargetKey);
        Assert.Equal(handDownAllocation, chain.Allocation);
    }

    private static PortfolioExactItem Exact(
        PortfolioEquipmentSlotAddress address,
        uint itemId,
        bool highQuality) =>
        new(
            itemId,
            highQuality,
            PortfolioEquipmentMove.ExactInstanceId(Owner, address, new(itemId, highQuality)));

    private static OutfitterPortfolioPlan Complete(OutfitterPortfolioPlan plan, params string[] targetKeys) =>
        PortfolioTargetDispositionResolver.Apply(
            plan,
            targetKeys.ToDictionary(
                key => key,
                key => ($"evidence:{key}", (string?)null, PortfolioTargetDispositionKind.TerminalNoUpgrade),
                StringComparer.Ordinal),
            new Dictionary<string, string>());
}
