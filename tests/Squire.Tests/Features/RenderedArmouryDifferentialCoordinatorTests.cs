using System.Collections.Generic;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire.Observation;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedArmouryDifferentialCoordinatorTests
{
    [Fact]
    public void Proof_completes_when_every_struct_slot_matches_the_rendered_identity()
    {
        var coordinator = Coordinator(Baseline(("ArmoryMainHand", 0, 100u, true), ("ArmoryHead", 2, 200u, false)));
        Assert.Equal(2, coordinator.Snapshot().TotalSlots);

        coordinator.RecordRenderedObservation("ArmoryMainHand", 0, 100u, true, "Rendered Tool");
        var done = coordinator.RecordRenderedObservation("ArmoryHead", 2, 200u, false, "Rendered Hat");

        Assert.Equal(RenderedArmouryDifferentialStatus.Complete, done.Status);
        Assert.Equal(2, done.MatchCount);
        Assert.Empty(done.Mismatches);
    }

    [Fact]
    public void Proof_fails_when_a_rendered_identity_disagrees_with_the_struct_read()
    {
        var coordinator = Coordinator(Baseline(("ArmoryMainHand", 0, 100u, true)));
        var done = coordinator.RecordRenderedObservation("ArmoryMainHand", 0, 101u, true, "Wrong Tool");

        Assert.Equal(RenderedArmouryDifferentialStatus.Failed, done.Status);
        var mismatch = Assert.Single(done.Mismatches);
        Assert.Equal("100 HQ", mismatch.StructIdentity);
        Assert.Equal("101 HQ", mismatch.RenderedIdentity);
    }

    [Fact]
    public void Proof_fails_when_quality_disagrees_even_with_the_same_item()
    {
        var coordinator = Coordinator(Baseline(("ArmoryRings", 4, 300u, false)));
        var done = coordinator.RecordRenderedObservation("ArmoryRings", 4, 300u, true, "Same Ring");

        Assert.Equal(RenderedArmouryDifferentialStatus.Failed, done.Status);
        Assert.Single(done.Mismatches);
    }

    [Fact]
    public void Proof_fails_when_a_struct_occupied_slot_renders_nothing()
    {
        var coordinator = Coordinator(Baseline(("ArmoryEar", 1, 400u, true)));
        var done = coordinator.RecordRenderedObservation("ArmoryEar", 1, null, null, null);

        Assert.Equal(RenderedArmouryDifferentialStatus.Failed, done.Status);
        Assert.Contains(done.Mismatches, value => value.RenderedIdentity == "nothing rendered");
    }

    [Fact]
    public void Occupancy_count_conflicts_fail_the_proof()
    {
        var coordinator = Coordinator(Baseline(("ArmoryMainHand", 0, 100u, true)));
        coordinator.RecordOccupancyCount("ArmoryMainHand", 1, 3);
        var done = coordinator.RecordRenderedObservation("ArmoryMainHand", 0, 100u, true, "Rendered Tool");

        Assert.Equal(RenderedArmouryDifferentialStatus.Failed, done.Status);
        Assert.Single(done.OccupancyConflicts);
    }

    [Fact]
    public void Sequence_drift_fails_immediately()
    {
        var coordinator = Coordinator(Baseline(("ArmoryMainHand", 0, 100u, true), ("ArmoryHead", 2, 200u, false)));
        var drifted = coordinator.RecordRenderedObservation("ArmoryHead", 2, 200u, false, "Out of order");

        Assert.Equal(RenderedArmouryDifferentialStatus.Failed, drifted.Status);
        Assert.Contains("drifted", drifted.Diagnostic, System.StringComparison.Ordinal);
    }

    private static RenderedArmouryDifferentialCoordinator Coordinator(IReadOnlyList<AgentBridgeInventoryStructItem> baseline)
    {
        var coordinator = new RenderedArmouryDifferentialCoordinator();
        coordinator.Begin(baseline);
        return coordinator;
    }

    private static AgentBridgeInventoryStructItem[] Baseline(params (string Container, int Slot, uint ItemId, bool Hq)[] slots)
    {
        var result = new List<AgentBridgeInventoryStructItem>();
        foreach (var (container, slot, itemId, hq) in slots)
            result.Add(new(container, slot, itemId, hq, 1, false, []));
        return result.ToArray();
    }
}
