using System.Text.Json;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Portfolio;
using JsonConvert = Newtonsoft.Json.JsonConvert;

namespace MarketMafioso.Tests.Squire;

public sealed class PortfolioAuthorityEnvelopeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fingerprint_IsStableAcrossSerializationAndCanonicalRetainerEvidenceOrder()
    {
        var plan = SingleTargetPlan(priority: 100);
        var first = PortfolioAuthorityEnvelopeFactory.Create(
            Lineage(retainers: [new("retainer:b", "b-7"), new("retainer:a", "a-4")]),
            plan);
        var second = PortfolioAuthorityEnvelopeFactory.Create(
            Lineage(retainers: [new("retainer:a", "a-4"), new("retainer:b", "b-7")]),
            plan);
        var restored = JsonSerializer.Deserialize<PortfolioAuthorityEnvelope>(JsonSerializer.Serialize(first))!;

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Fingerprint, restored.Fingerprint);
        Assert.True(restored.HasValidFingerprint());
        Assert.Equal(["retainer:a", "retainer:b"], restored.Lineage.RetainerEvidenceGenerations.Select(value => value.TargetKey));
    }

    [Fact]
    public void AuthorizeNext_StopsWhenTargetPriorityAuthorityDrifts()
    {
        var original = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), SingleTargetPlan(priority: 100));
        var changed = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), SingleTargetPlan(priority: 99));
        var state = State(original.Fingerprint);

        var stopped = PortfolioEquipmentExecution.AuthorizeNext(
            state,
            Before(),
            Now,
            TimeSpan.FromSeconds(5),
            changed.Fingerprint);

        Assert.Equal(PortfolioEquipmentExecutionStatus.StoppedForDrift, stopped.Status);
        Assert.Contains("priority", stopped.StopReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, stopped.CompletedStepCount);
    }

    [Fact]
    public void AcceptAfter_StopsWhenInventoryEvidenceGenerationDriftsAfterAuthorization()
    {
        var original = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), SingleTargetPlan(priority: 100));
        var changed = PortfolioAuthorityEnvelopeFactory.Create(
            Lineage() with { InventoryEvidenceGeneration = "inventory-8" },
            SingleTargetPlan(priority: 100));
        var state = PortfolioEquipmentExecution.AuthorizeNext(
            State(original.Fingerprint),
            Before(),
            Now,
            TimeSpan.FromSeconds(5),
            original.Fingerprint);

        var stopped = PortfolioEquipmentExecution.AcceptAfter(
            state,
            new(
                "active-loadout",
                EquipmentLoadoutPosition.Head,
                Item(200, true, "owner:EquippedItems:2:200:True"),
                [],
                "after-2",
                Now.AddMilliseconds(100)),
            Now.AddMilliseconds(200),
            TimeSpan.FromSeconds(5),
            changed.Fingerprint);

        Assert.Equal(PortfolioEquipmentExecutionStatus.StoppedForDrift, stopped.Status);
        Assert.Equal(0, stopped.CompletedStepCount);
        Assert.Contains("evidence lineage", stopped.StopReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PersistedExecution_RetainsExactPortfolioFingerprint()
    {
        var authority = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), SingleTargetPlan(priority: 100));
        var state = State(authority.Fingerprint);

        var restored = JsonSerializer.Deserialize<PortfolioEquipmentExecutionState>(JsonSerializer.Serialize(state))!;

        Assert.Equal(authority.Fingerprint, restored.AuthorityFingerprint);
        var authorized = PortfolioEquipmentExecution.AuthorizeNext(
            restored,
            Before(),
            Now,
            TimeSpan.FromSeconds(5),
            authority.Fingerprint);
        Assert.Equal(PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, authorized.Status);
    }

    [Fact]
    public void Version2Execution_NewtonsoftRoundTripRetainsTargetsActivationsAndReconstructibleEnvelope()
    {
        var plan = SingleTargetPlan(priority: 100);
        var authority = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), plan);
        var owner = new PortfolioEquipmentMoveOwner(42, "A Hero", 67);
        var moveTarget = new PortfolioEquipmentMoveTarget(
            "active-loadout",
            PortfolioEquipmentMoveTargetKind.ActivePlayer,
            owner,
            ActiveClassJobId: 1);
        var activation = EquipmentTargetActivationCoordinator.Create(
            "activate-player",
            new(
                "active-loadout",
                EquipmentTargetActivationKind.ActivePlayer,
                owner,
                ClassJobId: 1));
        var state = PortfolioEquipmentExecution.Create(
            "execution-v2",
            [new(
                "head",
                "active-loadout",
                EquipmentLoadoutPosition.Head,
                Item(200, true, "owner:Inventory1:4:200:True"),
                Item(100, false, "owner:EquippedItems:2:100:False"),
                Item(200, true, "owner:EquippedItems:2:200:True"))],
            new Dictionary<string, PortfolioEquipmentMoveTarget>(StringComparer.Ordinal)
            {
                ["active-loadout"] = moveTarget,
            },
            authority.Fingerprint,
            authority,
            new Dictionary<string, EquipmentTargetActivationState>(StringComparer.Ordinal)
            {
                ["active-loadout"] = activation,
            });

        var restored = JsonConvert.DeserializeObject<PortfolioEquipmentExecutionState>(
            JsonConvert.SerializeObject(state))!;

        Assert.Equal(PortfolioEquipmentExecution.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal(authority.SchemaVersion, restored.AuthorityEnvelope!.SchemaVersion);
        Assert.Equal(authority.Lineage with { RetainerEvidenceGenerations = [] }, restored.AuthorityEnvelope.Lineage with { RetainerEvidenceGenerations = [] });
        Assert.Equal(authority.Lineage.RetainerEvidenceGenerations, restored.AuthorityEnvelope.Lineage.RetainerEvidenceGenerations);
        Assert.Equal(authority.Targets, restored.AuthorityEnvelope.Targets);
        Assert.Equal(authority.Allocations, restored.AuthorityEnvelope.Allocations);
        Assert.Equal(authority.HandMeDownChain, restored.AuthorityEnvelope.HandMeDownChain);
        Assert.Equal(authority.Fingerprint, restored.AuthorityFingerprint);
        Assert.True(restored.AuthorityEnvelope.HasValidFingerprint());
        Assert.Equal(moveTarget, Assert.Single(restored.Targets!).Value);
        Assert.Equal(activation, Assert.Single(restored.TargetActivations!).Value);
        Assert.Equal(
            authority.Fingerprint,
            PortfolioAuthorityEnvelopeFactory.Create(restored.AuthorityEnvelope.Lineage, plan).Fingerprint);
    }

    [Fact]
    public void Version2Execution_RejectsActivationStoredUnderAnotherTargetKey()
    {
        var plan = SingleTargetPlan(priority: 100);
        var authority = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), plan);
        var owner = new PortfolioEquipmentMoveOwner(42, "A Hero", 67);
        var mismatched = EquipmentTargetActivationCoordinator.Create(
            "activate-retainer",
            new(
                "retainer:9001",
                EquipmentTargetActivationKind.Retainer,
                owner,
                ClassJobId: 1,
                RetainerId: 9001,
                RetainerName: "Bobo"));

        var error = Assert.Throws<ArgumentException>(() => PortfolioEquipmentExecution.Create(
            "execution-v2",
            [new(
                "head",
                "active-loadout",
                EquipmentLoadoutPosition.Head,
                Item(200, true, "owner:Inventory1:4:200:True"),
                Item(100, false, "owner:EquippedItems:2:100:False"),
                Item(200, true, "owner:EquippedItems:2:200:True"))],
            authorityFingerprint: authority.Fingerprint,
            authorityEnvelope: authority,
            targetActivations: new Dictionary<string, EquipmentTargetActivationState>(StringComparer.Ordinal)
            {
                ["active-loadout"] = mismatched,
            }));

        Assert.Contains("activation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoTargetEnvelope_JsonRoundTripPreservesOrderedExactHandDownChain()
    {
        var fixture = TwoTargetHandDownPlan();
        var authority = PortfolioAuthorityEnvelopeFactory.Create(
            Lineage([new("retainer:9001", "retainer-4")]),
            fixture.Plan);

        var restored = JsonConvert.DeserializeObject<PortfolioAuthorityEnvelope>(
            JsonConvert.SerializeObject(authority))!;

        Assert.True(restored.HasValidFingerprint());
        Assert.Equal(authority.Fingerprint, restored.Fingerprint);
        var chain = Assert.Single(restored.HandMeDownChain);
        Assert.Equal("active-loadout", chain.UpstreamTargetKey);
        Assert.Equal("retainer:9001", chain.DownstreamTargetKey);
        Assert.Equal(fixture.HandDownAllocation, chain.Allocation);
        Assert.Equal(fixture.ReleasedItem, chain.ReleasedItem);
        Assert.Equal(fixture.ReplacementItem, chain.SelectedReplacement);
    }

    private static PortfolioAuthorityLineage Lineage(
        IReadOnlyList<PortfolioRetainerEvidenceGeneration>? retainers = null) =>
        new("owner-42", "owner-3", "inventory-7", "listing-9", retainers ?? []);

    private static OutfitterPortfolioPlan SingleTargetPlan(int priority)
    {
        var allocation = new PortfolioAllocationKey(
            PortfolioAllocationSourceKind.OwnedInstance,
            "owned:source-head",
            200,
            true,
            "owner:Inventory1:4:200:True");
        var candidate = new PortfolioCandidate(
            "active-loadout",
            "best",
            10,
            [new(allocation, 1)],
            [new(EquipmentLoadoutPosition.Head, Item(200, true, allocation.InstanceId!))],
            []);
        var plan = OutfitterPortfolioPlanner.Plan(
            [new("active-loadout", "Player", priority, PortfolioProgressionHorizon.Immediate)],
            [candidate],
            [new(allocation, 1)]);
        return PortfolioTargetDispositionResolver.Apply(
            plan,
            new Dictionary<string, (string, string?, PortfolioTargetDispositionKind)>
            {
                ["active-loadout"] = ("active-evidence-7", null, PortfolioTargetDispositionKind.TerminalNoUpgrade),
            },
            new Dictionary<string, string>());
    }

    private static PortfolioEquipmentExecutionState State(PortfolioAuthorityFingerprint fingerprint) =>
        PortfolioEquipmentExecution.Create(
            "execution",
            [new(
                "head",
                "active-loadout",
                EquipmentLoadoutPosition.Head,
                Item(200, true, "owner:Inventory1:4:200:True"),
                Item(100, false, "owner:EquippedItems:2:100:False"),
                Item(200, true, "owner:EquippedItems:2:200:True"))],
            authorityFingerprint: fingerprint);

    private static PortfolioEquipmentObservation Before() => new(
        "active-loadout",
        EquipmentLoadoutPosition.Head,
        Item(100, false, "owner:EquippedItems:2:100:False"),
        [Item(200, true, "owner:Inventory1:4:200:True")],
        "before-1",
        Now - TimeSpan.FromMilliseconds(100));

    private static PortfolioExactItem Item(uint itemId, bool highQuality, string instanceId) =>
        new(itemId, highQuality, instanceId);

    private static (OutfitterPortfolioPlan Plan, PortfolioAllocationKey HandDownAllocation, PortfolioExactItem ReleasedItem, PortfolioExactItem ReplacementItem) TwoTargetHandDownPlan()
    {
        var replacement = Item(200, true, "owner:Inventory1:5:200:True");
        var released = Item(100, false, "owner:EquippedItems:2:100:False");
        var replacementAllocation = new PortfolioAllocationKey(
            PortfolioAllocationSourceKind.OwnedInstance,
            "owned:new-hat",
            replacement.ItemId,
            replacement.IsHighQuality,
            replacement.InstanceId);
        var handDown = new PortfolioAllocationKey(
            PortfolioAllocationSourceKind.HandMeDown,
            "hand-down:old-hat",
            released.ItemId,
            released.IsHighQuality,
            released.InstanceId);
        var upstream = new PortfolioCandidate(
            "active-loadout",
            "player-upgrade",
            10,
            [new(replacementAllocation, 1)],
            [new(EquipmentLoadoutPosition.Head, replacement)],
            [new("player-worn-3", EquipmentLoadoutPosition.Head, released, replacement, handDown)]);
        var downstream = new PortfolioCandidate(
            "retainer:9001",
            "retainer-upgrade",
            5,
            [new(handDown, 1)],
            [new(EquipmentLoadoutPosition.Head, released)],
            []);
        var plan = OutfitterPortfolioPlanner.Plan(
            [
                new("active-loadout", "Player", 100, PortfolioProgressionHorizon.Immediate),
                new("retainer:9001", "Bobo", 10, PortfolioProgressionHorizon.NearTerm),
            ],
            [upstream, downstream],
            [new(replacementAllocation, 1)]);
        plan = PortfolioTargetDispositionResolver.Apply(
            plan,
            new Dictionary<string, (string, string?, PortfolioTargetDispositionKind)>
            {
                ["active-loadout"] = ("player-worn-3", null, PortfolioTargetDispositionKind.TerminalNoUpgrade),
                ["retainer:9001"] = ("retainer-worn-4", null, PortfolioTargetDispositionKind.TerminalNoUpgrade),
            },
            new Dictionary<string, string>());
        return (plan, handDown, released, replacement);
    }
}
