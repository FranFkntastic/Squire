using System.Text.Json;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Portfolio;

namespace MarketMafioso.Tests.Squire;

public sealed class EquipmentTargetActivationCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);
    private static readonly PortfolioEquipmentMoveOwner Owner = new(42, "A' Hero", 67);

    [Fact]
    public void SavedGearset_SubmitsExactlyOnceAndWaitsForFreshExactProof()
    {
        var target = SavedTarget();
        var runtime = new FakeRuntime(new(7, "Marauder", 3));
        var state = EquipmentTargetActivationCoordinator.Create("activation-1", target);

        state = EquipmentTargetActivationCoordinator.Request(state, runtime, Now);

        Assert.Equal(1, runtime.ActivationCount);
        Assert.Equal(EquipmentTargetActivationStatus.AwaitingFreshProof, state.Status);
        Assert.False(state.CanMoveSlots);

        state = EquipmentTargetActivationCoordinator.AcceptActivePlayerProof(
            state,
            Proof(Now + TimeSpan.FromSeconds(1)),
            Now + TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10));

        Assert.Equal(EquipmentTargetActivationStatus.Proven, state.Status);
        Assert.True(state.CanMoveSlots);
    }

    [Theory]
    [InlineData("gearset-id")]
    [InlineData("gearset-name")]
    [InlineData("gearset-job")]
    public void SavedGearset_RefusesLiveIdentityMismatchBeforeActivation(string mismatch)
    {
        var observed = mismatch switch
        {
            "gearset-id" => new SavedGearsetActivationIdentity(8, "Marauder", 3),
            "gearset-name" => new SavedGearsetActivationIdentity(7, "Wrong", 3),
            _ => new SavedGearsetActivationIdentity(7, "Marauder", 4),
        };
        var runtime = new FakeRuntime(observed);
        var state = EquipmentTargetActivationCoordinator.Create("activation-1", SavedTarget());

        state = EquipmentTargetActivationCoordinator.Request(state, runtime, Now);

        Assert.Equal(EquipmentTargetActivationStatus.Refused, state.Status);
        Assert.Equal(0, runtime.ActivationCount);
        Assert.False(state.CanMoveSlots);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("job")]
    [InlineData("gearset")]
    [InlineData("stale")]
    [InlineData("saved-baseline")]
    public void SavedGearset_RefusesMismatchedOrNonFreshActivationProof(string mismatch)
    {
        var runtime = new FakeRuntime(new(7, "Marauder", 3));
        var state = EquipmentTargetActivationCoordinator.Request(
            EquipmentTargetActivationCoordinator.Create("activation-1", SavedTarget()),
            runtime,
            Now);
        var proof = Proof(Now + TimeSpan.FromSeconds(1));
        proof = mismatch switch
        {
            "owner" => proof with { Owner = Owner with { LocalContentId = 99 } },
            "job" => proof with { ClassJobId = 4 },
            "gearset" => proof with { CurrentGearset = new(8, "Marauder", 3) },
            "stale" => proof with { BaselineCompletedAtUtc = Now },
            _ => proof with { BaselineTargetKind = PlayerAdvisorBaselineTargetKind.SavedGearset },
        };

        state = EquipmentTargetActivationCoordinator.AcceptActivePlayerProof(
            state,
            proof,
            Now + TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10));

        Assert.Equal(EquipmentTargetActivationStatus.Refused, state.Status);
        Assert.False(state.CanMoveSlots);
    }

    [Fact]
    public void InterruptedPersistedActivation_ResumesProofWaitWithoutReplayingNativeActivation()
    {
        var runtime = new FakeRuntime(new(7, "Marauder", 3));
        var state = EquipmentTargetActivationCoordinator.Request(
            EquipmentTargetActivationCoordinator.Create("activation-1", SavedTarget()),
            runtime,
            Now);
        state = EquipmentTargetActivationCoordinator.Interrupt(state, "Plugin reload interrupted proof capture.");
        var persisted = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<EquipmentTargetActivationState>(persisted)!;

        restored = EquipmentTargetActivationCoordinator.ResumeProofWaitAfterReload(
            restored,
            Now + TimeSpan.FromSeconds(5));

        Assert.Equal(1, runtime.ActivationCount);
        Assert.Equal(EquipmentTargetActivationStatus.AwaitingFreshProof, restored.Status);
        Assert.Null(restored.NativeResult);
        Assert.False(restored.CanMoveSlots);

        restored = EquipmentTargetActivationCoordinator.AcceptActivePlayerProof(
            restored,
            Proof(Now + TimeSpan.FromSeconds(6)),
            Now + TimeSpan.FromSeconds(7),
            TimeSpan.FromSeconds(10));

        Assert.True(restored.CanMoveSlots);
        Assert.Equal(1, runtime.ActivationCount);
    }

    [Fact]
    public void ActivePlayer_RequiresFreshBaselineButDoesNotInvokeGearsetActivation()
    {
        var target = SavedTarget() with
        {
            TargetKey = "active:3",
            Kind = EquipmentTargetActivationKind.ActivePlayer,
            GearsetId = null,
            GearsetName = null,
        };
        var runtime = new FakeRuntime(null);
        var state = EquipmentTargetActivationCoordinator.Create("activation-2", target);

        state = EquipmentTargetActivationCoordinator.Request(state, runtime, Now);

        Assert.Equal(0, runtime.ActivationCount);
        Assert.False(state.CanMoveSlots);
        state = EquipmentTargetActivationCoordinator.AcceptActivePlayerProof(
            state,
            Proof(Now + TimeSpan.FromSeconds(1)),
            Now + TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10));
        Assert.True(state.CanMoveSlots);
    }

    [Fact]
    public void Retainer_RequiresFreshExactRenderedOwnerIdentityAndEverySlot()
    {
        var target = new EquipmentTargetActivationTarget(
            "retainer:9001",
            EquipmentTargetActivationKind.Retainer,
            Owner,
            3,
            RetainerId: 9001,
            RetainerName: "Bobo",
            OwnerHomeWorldName: "Siren");
        var runtime = new FakeRuntime(null);
        var state = EquipmentTargetActivationCoordinator.Request(
            EquipmentTargetActivationCoordinator.Create("activation-3", target),
            runtime,
            Now);
        var evidence = new RenderedRetainerEquipmentEvidence(
            RenderedRetainerEquipmentEvidenceStatus.Complete,
            target.TargetKey,
            Now + TimeSpan.FromSeconds(1),
            Owner.CharacterName,
            "Siren",
            "Bobo",
            3,
            10,
            EmptyCanonicalSlots(),
            "complete");

        var proven = EquipmentTargetActivationCoordinator.AcceptRetainerProof(
            state,
            evidence,
            Now + TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10));
        var wrongRetainer = EquipmentTargetActivationCoordinator.AcceptRetainerProof(
            state,
            evidence with { RetainerName = "Not Bobo" },
            Now + TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10));

        Assert.True(proven.CanMoveSlots);
        Assert.Equal(EquipmentTargetActivationStatus.Refused, wrongRetainer.Status);
        Assert.Equal(0, runtime.ActivationCount);
    }

    private static EquipmentTargetActivationTarget SavedTarget() => new(
        "gearset:7",
        EquipmentTargetActivationKind.SavedGearset,
        Owner,
        3,
        GearsetId: 7,
        GearsetName: "Marauder");

    private static ActivePlayerActivationProof Proof(DateTimeOffset completedAt) => new(
        Owner,
        3,
        new(7, "Marauder", 3),
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        completedAt,
        true,
        PlayerAdvisorBaselineTargetKind.ActiveLoadout);

    private static IReadOnlyList<RenderedEquipmentSlotObservation> EmptyCanonicalSlots() =>
    [
        new("main-hand", EquipmentSlot.MainHand, RenderedEquipmentSlotObservationStatus.Empty, null),
        new("off-hand", EquipmentSlot.OffHand, RenderedEquipmentSlotObservationStatus.Empty, null),
        new("head", EquipmentSlot.Head, RenderedEquipmentSlotObservationStatus.Empty, null),
        new("body", EquipmentSlot.Body, RenderedEquipmentSlotObservationStatus.Empty, null),
        new("hands", EquipmentSlot.Hands, RenderedEquipmentSlotObservationStatus.Empty, null),
        new("legs", EquipmentSlot.Legs, RenderedEquipmentSlotObservationStatus.Empty, null),
        new("feet", EquipmentSlot.Feet, RenderedEquipmentSlotObservationStatus.Empty, null),
        new("ears", EquipmentSlot.Ears, RenderedEquipmentSlotObservationStatus.Empty, null),
        new("neck", EquipmentSlot.Neck, RenderedEquipmentSlotObservationStatus.Empty, null),
        new("wrists", EquipmentSlot.Wrists, RenderedEquipmentSlotObservationStatus.Empty, null),
        new("ring-right", EquipmentSlot.Ring, RenderedEquipmentSlotObservationStatus.Empty, null),
        new("ring-left", EquipmentSlot.Ring, RenderedEquipmentSlotObservationStatus.Empty, null),
    ];

    private sealed class FakeRuntime(SavedGearsetActivationIdentity? identity) : ISavedGearsetActivationRuntime
    {
        public int ActivationCount { get; private set; }

        public bool TryReadSavedGearset(
            int gearsetId,
            out SavedGearsetActivationIdentity? observed,
            out string diagnostic)
        {
            observed = identity;
            diagnostic = identity is null ? "Gearset unavailable." : string.Empty;
            return identity is not null;
        }

        public int ActivateSavedGearset(int gearsetId)
        {
            ActivationCount++;
            return 23;
        }

        public int UpdateSavedGearset(int gearsetId) => gearsetId;

        public bool TryReadSavedGearsetItem(
            int gearsetId,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition position,
            out PortfolioEquipmentSlotItem? item,
            out string diagnostic)
        {
            item = new(200, true);
            diagnostic = string.Empty;
            return true;
        }
    }
}
