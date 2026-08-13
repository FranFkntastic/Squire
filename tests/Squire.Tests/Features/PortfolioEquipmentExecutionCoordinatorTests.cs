using Franthropy.Dalamud.Equipment;
using MarketMafioso;
using MarketMafioso.Squire;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Portfolio;
using Newtonsoft.Json;

namespace MarketMafioso.Tests.Squire;

public sealed class PortfolioEquipmentExecutionCoordinatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
    private static readonly PortfolioEquipmentMoveOwner Owner = new(42, "A Hero", 67);
    private static readonly PortfolioEquipmentMoveTarget Target = new(
        "active-loadout", PortfolioEquipmentMoveTargetKind.ActivePlayer, Owner, ActiveClassJobId: 1);
    private static readonly PortfolioEquipmentSlotAddress Source = new("ArmoryHead", 4);
    private static readonly PortfolioEquipmentSlotAddress Destination = new("EquippedItems", 2);
    private static readonly PortfolioExactItem SourceExact = new(200, true, "42:ArmoryHead:4:200:True");
    private static readonly PortfolioExactItem BeforeExact = new(100, false, "42:EquippedItems:2:100:False");
    private static readonly PortfolioExactItem AfterExact = new(200, true, "42:EquippedItems:2:200:True");

    [Fact]
    public void Submit_saves_awaiting_intent_before_one_native_move_and_after_evidence_completes()
    {
        var config = new TestConfiguration();
        var runtime = new FakeRuntime();
        var coordinator = new PortfolioEquipmentExecutionCoordinator(config, runtime);
        coordinator.Start(CreateState());
        runtime.BeforeMove = () =>
        {
            var persisted = JsonConvert.DeserializeObject<PortfolioEquipmentExecutionState>(
                config.Squire.OutfitterEquipmentExecutionStateJson!);
            Assert.Equal(PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, persisted!.Status);
        };

        var submitted = coordinator.SubmitNext(Target, Now);

        Assert.True(submitted.Move!.WasSubmitted);
        Assert.Equal(1, runtime.MoveCount);
        runtime.SourceItem = null;
        runtime.DestinationItem = new(200, true);
        var completed = coordinator.ObservePending(Target, Now.AddMilliseconds(300));
        Assert.Equal(PortfolioEquipmentExecutionStatus.Completed, completed.State!.Status);
        Assert.Equal(1, completed.State.CompletedStepCount);
    }

    [Fact]
    public void Reload_reconciles_saved_submission_back_to_ready_when_move_never_happened()
    {
        var config = new TestConfiguration();
        var runtime = new FakeRuntime();
        var state = PortfolioEquipmentExecution.AuthorizeNext(
            CreateState(),
            new("active-loadout", EquipmentLoadoutPosition.Head, BeforeExact, [SourceExact], "before", Now),
            Now,
            TimeSpan.FromSeconds(5));
        config.Squire.OutfitterEquipmentExecutionStateJson = JsonConvert.SerializeObject(state);

        var coordinator = new PortfolioEquipmentExecutionCoordinator(config, runtime);
        var settling = coordinator.ReconcileAfterReload(Target, Now.AddMilliseconds(300));

        Assert.True(coordinator.NeedsRestartReconciliation is false);
        Assert.Equal(PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, settling.State!.Status);
        var expired = coordinator.ObservePending(Target, Now.AddSeconds(6));
        Assert.Equal(PortfolioEquipmentExecutionStatus.Ready, expired.State!.Status);
        Assert.Equal(0, runtime.MoveCount);
    }

    [Fact]
    public void Delayed_exact_after_state_completes_inside_the_bounded_settle_window()
    {
        var config = new TestConfiguration();
        var runtime = new FakeRuntime();
        var coordinator = new PortfolioEquipmentExecutionCoordinator(config, runtime);
        coordinator.Start(CreateState());
        coordinator.SubmitNext(Target, Now);

        var stillBefore = coordinator.ObservePending(Target, Now.AddMilliseconds(300));
        Assert.Equal(PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, stillBefore.State!.Status);

        runtime.SourceItem = null;
        runtime.DestinationItem = new(200, true);
        var completed = coordinator.ObservePending(Target, Now.AddMilliseconds(800));

        Assert.Equal(PortfolioEquipmentExecutionStatus.Completed, completed.State!.Status);
        Assert.Equal(1, runtime.MoveCount);
    }

    [Fact]
    public void Reload_reconciliation_with_persisted_authority_requires_current_lineage_instead_of_self_comparing()
    {
        var config = new TestConfiguration();
        var runtime = new FakeRuntime();
        var authority = Fingerprint("authority-a");
        var state = PortfolioEquipmentExecution.AuthorizeNext(
            CreateState(authority),
            new("active-loadout", EquipmentLoadoutPosition.Head, BeforeExact, [SourceExact], "before", Now),
            Now,
            TimeSpan.FromSeconds(5),
            authority);
        config.Squire.OutfitterEquipmentExecutionStateJson = JsonConvert.SerializeObject(state);

        var coordinator = new PortfolioEquipmentExecutionCoordinator(config, runtime);
        var missing = coordinator.ReconcileAfterReload(Target, Now.AddMilliseconds(300));

        Assert.Equal(PortfolioEquipmentExecutionStatus.StoppedForDrift, missing.State!.Status);
        Assert.Contains("unavailable", missing.State.StopReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runtime.MoveCount);
    }

    [Fact]
    public void After_observation_with_persisted_authority_stops_on_current_lineage_drift()
    {
        var config = new TestConfiguration();
        var runtime = new FakeRuntime();
        var authority = Fingerprint("authority-a");
        var coordinator = new PortfolioEquipmentExecutionCoordinator(config, runtime);
        coordinator.Start(CreateState(authority));
        var submitted = coordinator.SubmitNext(Target, Now, authority);
        Assert.True(submitted.Move!.WasSubmitted);
        runtime.SourceItem = null;
        runtime.DestinationItem = new(200, true);

        var stopped = coordinator.ObservePending(
            Target,
            Now.AddMilliseconds(300),
            Fingerprint("authority-b"));

        Assert.Equal(PortfolioEquipmentExecutionStatus.StoppedForDrift, stopped.State!.Status);
        Assert.Contains("authority changed", stopped.State.StopReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, stopped.State.CompletedStepCount);
    }

    [Fact]
    public void Exact_target_or_slot_drift_stops_without_native_move()
    {
        var config = new TestConfiguration();
        var runtime = new FakeRuntime { SourceItem = new(201, true) };
        var coordinator = new PortfolioEquipmentExecutionCoordinator(config, runtime);
        coordinator.Start(CreateState());

        var result = coordinator.SubmitNext(Target, Now);

        Assert.Equal(PortfolioEquipmentExecutionStatus.StoppedForDrift, result.State!.Status);
        Assert.Equal(0, runtime.MoveCount);
        Assert.NotNull(config.Squire.OutfitterEquipmentExecutionStateJson);
    }

    [Fact]
    public void Saved_gearset_request_then_fresh_proof_submits_one_move_without_duplicate_activation()
    {
        var target = SavedGearsetMoveTarget();
        var activation = SavedGearsetActivation(target, EquipmentTargetActivationStatus.Ready);
        var runtime = new FakeRuntime { ActiveTarget = target };
        var activationRuntime = new FakeActivationRuntime(new(7, "Marauder", 3));
        var proof = SavedGearsetProof(Now);
        var coordinator = new PortfolioEquipmentExecutionCoordinator(
            new TestConfiguration(),
            runtime,
            activationRuntime,
            () => proof);
        coordinator.Start(CreateState(target, activation));

        var requested = coordinator.SubmitNext(target, Now);

        Assert.Null(requested.Move);
        Assert.Equal(1, activationRuntime.ActivationCount);
        Assert.Equal(0, runtime.MoveCount);
        Assert.Equal(EquipmentTargetActivationStatus.AwaitingFreshProof,
            requested.State!.TargetActivations![target.TargetKey].Status);

        proof = SavedGearsetProof(Now.AddMilliseconds(200));
        var submitted = coordinator.AdvanceTargetActivation(target, Now.AddMilliseconds(300));

        Assert.True(submitted.Move!.WasSubmitted);
        Assert.Equal(1, activationRuntime.ActivationCount);
        Assert.Equal(1, runtime.MoveCount);
        Assert.Equal(EquipmentTargetActivationStatus.Proven,
            submitted.State!.TargetActivations![target.TargetKey].Status);

        runtime.SourceItem = null;
        runtime.DestinationItem = new(200, true);
        var completed = coordinator.ObservePending(target, Now.AddMilliseconds(600));

        Assert.Equal(PortfolioEquipmentExecutionStatus.Completed, completed.State!.Status);
        Assert.Equal(1, activationRuntime.UpdateCount);
        Assert.Equal(PlayerAdvisorEquippedSlotMap.All.Count, activationRuntime.ReadItemCount);
    }

    [Fact]
    public void Persisted_interrupted_activation_resumes_fresh_proof_without_replaying_activation()
    {
        var target = SavedGearsetMoveTarget();
        var activation = SavedGearsetActivation(target, EquipmentTargetActivationStatus.AwaitingFreshProof) with
        {
            RequestedAtUtc = Now,
            NativeResult = 23,
        };
        var config = new TestConfiguration();
        config.Squire.OutfitterEquipmentExecutionStateJson = JsonConvert.SerializeObject(CreateState(target, activation));
        var runtime = new FakeRuntime { ActiveTarget = target };
        var activationRuntime = new FakeActivationRuntime(new(7, "Marauder", 3));
        var proof = SavedGearsetProof(Now.AddMilliseconds(200));

        var coordinator = new PortfolioEquipmentExecutionCoordinator(
            config,
            runtime,
            activationRuntime,
            () => proof);

        Assert.Equal(EquipmentTargetActivationStatus.Interrupted,
            coordinator.State!.TargetActivations![target.TargetKey].Status);
        var resumed = coordinator.AdvanceTargetActivation(target, Now.AddMilliseconds(300));

        Assert.Null(resumed.Move);
        Assert.Equal(0, activationRuntime.ActivationCount);
        Assert.Equal(0, runtime.MoveCount);
        Assert.Equal(EquipmentTargetActivationStatus.AwaitingFreshProof,
            resumed.State!.TargetActivations![target.TargetKey].Status);

        proof = SavedGearsetProof(Now.AddMilliseconds(500));
        var proven = coordinator.AdvanceTargetActivation(target, Now.AddMilliseconds(600));

        Assert.Null(proven.Move);
        Assert.Equal("The exact target is active and freshly proven.", proven.Message);
        Assert.Equal(0, activationRuntime.ActivationCount);
        Assert.Equal(0, runtime.MoveCount);
        Assert.Equal(EquipmentTargetActivationStatus.Proven,
            proven.State!.TargetActivations![target.TargetKey].Status);
    }

    [Fact]
    public void Saved_gearset_proof_authority_mismatch_blocks_slot_move()
    {
        var target = SavedGearsetMoveTarget();
        var runtime = new FakeRuntime { ActiveTarget = target };
        var activationRuntime = new FakeActivationRuntime(new(7, "Marauder", 3));
        var proof = SavedGearsetProof(Now);
        var coordinator = new PortfolioEquipmentExecutionCoordinator(
            new TestConfiguration(),
            runtime,
            activationRuntime,
            () => proof);
        coordinator.Start(CreateState(target, SavedGearsetActivation(target, EquipmentTargetActivationStatus.Ready)));
        coordinator.SubmitNext(target, Now);
        proof = SavedGearsetProof(Now.AddMilliseconds(200)) with
        {
            CurrentGearset = new(8, "Not Marauder", 4),
        };

        var blocked = coordinator.AdvanceTargetActivation(target, Now.AddMilliseconds(300));

        Assert.Null(blocked.Move);
        Assert.Equal(1, activationRuntime.ActivationCount);
        Assert.Equal(0, runtime.MoveCount);
        Assert.Equal(EquipmentTargetActivationStatus.Refused,
            blocked.State!.TargetActivations![target.TargetKey].Status);
    }

    [Fact]
    public void Retainer_requires_fresh_rendered_proof_before_slot_move()
    {
        var target = RetainerMoveTarget();
        var activation = RetainerActivation(target, EquipmentTargetActivationStatus.Ready);
        var runtime = new FakeRuntime
        {
            ActiveTarget = target,
            SourceAddress = new("RetainerPage1", 4),
        };
        RenderedRetainerEquipmentEvidence? proof = RetainerProof(target, Now);
        var coordinator = new PortfolioEquipmentExecutionCoordinator(
            new TestConfiguration(),
            runtime,
            captureRetainerProof: _ => proof);
        coordinator.Start(CreateState(target, activation, runtime.SourceAddress));

        var requested = coordinator.SubmitNext(target, Now);

        Assert.Null(requested.Move);
        Assert.Equal(0, runtime.MoveCount);
        Assert.Equal(EquipmentTargetActivationStatus.AwaitingFreshProof,
            requested.State!.TargetActivations![target.TargetKey].Status);

        proof = RetainerProof(target, Now.AddMilliseconds(200));
        var submitted = coordinator.AdvanceTargetActivation(target, Now.AddMilliseconds(300));

        Assert.True(submitted.Move!.WasSubmitted);
        Assert.Equal(1, runtime.MoveCount);
        Assert.Equal(EquipmentTargetActivationStatus.Proven,
            submitted.State!.TargetActivations![target.TargetKey].Status);
    }

    [Fact]
    public void Saved_gearset_update_failure_keeps_physical_after_state_retryable_until_exact_reread_succeeds()
    {
        var target = SavedGearsetMoveTarget();
        var runtime = new FakeRuntime { ActiveTarget = target };
        var activationRuntime = new FakeActivationRuntime(new(7, "Marauder", 3))
        {
            RemainingUpdateFailures = 1,
        };
        var coordinator = CreateProvenSavedCoordinator(target, runtime, activationRuntime);
        coordinator.SubmitNext(target, Now);
        runtime.SourceItem = null;
        runtime.DestinationItem = new(200, true);

        var failed = coordinator.ObservePending(target, Now.AddMilliseconds(300));

        Assert.Equal(PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, failed.State!.Status);
        Assert.Equal(0, failed.State.CompletedStepCount);
        Assert.True(coordinator.NeedsRestartReconciliation);
        Assert.Equal(1, activationRuntime.UpdateCount);

        var retried = coordinator.ObservePending(target, Now.AddMilliseconds(600));

        Assert.Equal(PortfolioEquipmentExecutionStatus.Completed, retried.State!.Status);
        Assert.Equal(1, retried.State.CompletedStepCount);
        Assert.Equal(2, activationRuntime.UpdateCount);
        Assert.Equal(3, activationRuntime.ReadIdentityCount);
        Assert.Equal(PlayerAdvisorEquippedSlotMap.All.Count, activationRuntime.ReadItemCount);
    }

    [Fact]
    public void Saved_gearset_reread_mismatch_never_completes_and_reload_retries_without_another_slot_move()
    {
        var target = SavedGearsetMoveTarget();
        var runtime = new FakeRuntime { ActiveTarget = target };
        var activationRuntime = new FakeActivationRuntime(new(7, "Marauder", 3));
        var config = new TestConfiguration();
        var coordinator = CreateProvenSavedCoordinator(target, runtime, activationRuntime, config);
        coordinator.SubmitNext(target, Now);
        runtime.SourceItem = null;
        runtime.DestinationItem = new(200, true);
        activationRuntime.IdentityAfterUpdate = new(7, "Renamed", 3);

        var mismatched = coordinator.ObservePending(target, Now.AddMilliseconds(300));

        Assert.Equal(PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, mismatched.State!.Status);
        Assert.Equal(0, mismatched.State.CompletedStepCount);
        Assert.Equal(1, runtime.MoveCount);
        Assert.Contains("name", mismatched.Message, StringComparison.OrdinalIgnoreCase);

        activationRuntime.IdentityAfterUpdate = new(7, "Marauder", 3);
        var restored = new PortfolioEquipmentExecutionCoordinator(config, runtime, activationRuntime);
        var recovered = restored.ReconcileAfterReload(target, Now.AddMilliseconds(600));

        Assert.Equal(PortfolioEquipmentExecutionStatus.Completed, recovered.State!.Status);
        Assert.Equal(1, recovered.State.CompletedStepCount);
        Assert.Equal(1, runtime.MoveCount);
        Assert.Equal(2, activationRuntime.UpdateCount);
    }

    [Fact]
    public void Saved_gearset_exact_item_reread_failure_remains_retryable_instead_of_stranding_completed_slot()
    {
        var target = SavedGearsetMoveTarget();
        var runtime = new FakeRuntime { ActiveTarget = target };
        var activationRuntime = new FakeActivationRuntime(new(7, "Marauder", 3))
        {
            ReadItem = new(201, true),
        };
        var coordinator = CreateProvenSavedCoordinator(target, runtime, activationRuntime);
        coordinator.SubmitNext(target, Now);
        runtime.SourceItem = null;
        runtime.DestinationItem = new(200, true);

        var mismatched = coordinator.ObservePending(target, Now.AddMilliseconds(300));

        Assert.Equal(PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, mismatched.State!.Status);
        Assert.Equal(0, mismatched.State.CompletedStepCount);
        activationRuntime.ReadItem = new(200, true);

        var retried = coordinator.ObservePending(target, Now.AddMilliseconds(600));

        Assert.Equal(PortfolioEquipmentExecutionStatus.Completed, retried.State!.Status);
        Assert.Equal(2, activationRuntime.UpdateCount);
        Assert.Equal(PlayerAdvisorEquippedSlotMap.All.Count + 3, activationRuntime.ReadItemCount);
    }

    [Fact]
    public void Unrelated_active_slot_drift_blocks_whole_loadout_update_before_native_call()
    {
        var target = SavedGearsetMoveTarget();
        var runtime = new FakeRuntime
        {
            ActiveTarget = target,
            UnrelatedEquippedDrift = new(999, false),
        };
        var activationRuntime = new FakeActivationRuntime(new(7, "Marauder", 3));
        var coordinator = CreateProvenSavedCoordinator(target, runtime, activationRuntime);
        coordinator.SubmitNext(target, Now);
        runtime.SourceItem = null;
        runtime.DestinationItem = new(200, true);

        var blocked = coordinator.ObservePending(target, Now.AddMilliseconds(300));

        Assert.Equal(PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence, blocked.State!.Status);
        Assert.Equal(0, blocked.State.CompletedStepCount);
        Assert.Contains("drifted", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, activationRuntime.UpdateCount);
        Assert.Equal(0, activationRuntime.ReadItemCount);
    }

    private static PortfolioEquipmentExecutionState CreateState(PortfolioAuthorityFingerprint? fingerprint = null) => PortfolioEquipmentExecution.Create(
        "execution",
        [new("step", "active-loadout", EquipmentLoadoutPosition.Head, SourceExact, BeforeExact, AfterExact)],
        new Dictionary<string, PortfolioEquipmentMoveTarget>(StringComparer.Ordinal) { ["active-loadout"] = Target },
        authorityFingerprint: fingerprint);

    private static PortfolioAuthorityFingerprint Fingerprint(string value) =>
        new(PortfolioAuthorityEnvelope.CurrentSchemaVersion, value);

    private static PortfolioEquipmentExecutionState CreateState(
        PortfolioEquipmentMoveTarget target,
        EquipmentTargetActivationState activation,
        PortfolioEquipmentSlotAddress? source = null)
    {
        source ??= Source;
        var sourceExact = new PortfolioExactItem(
            200,
            true,
            $"{Owner.LocalContentId}:{source.Container}:{source.SlotIndex}:200:True");
        var destinationContainer = target.Kind == PortfolioEquipmentMoveTargetKind.Retainer
            ? "RetainerEquippedItems"
            : "EquippedItems";
        var before = new PortfolioExactItem(100, false,
            $"{Owner.LocalContentId}:{destinationContainer}:2:100:False");
        var after = new PortfolioExactItem(200, true,
            $"{Owner.LocalContentId}:{destinationContainer}:2:200:True");
        return PortfolioEquipmentExecution.Create(
            $"execution-{target.TargetKey}",
            [new("step", target.TargetKey, EquipmentLoadoutPosition.Head, sourceExact, before, after)],
            new Dictionary<string, PortfolioEquipmentMoveTarget>(StringComparer.Ordinal) { [target.TargetKey] = target },
            targetActivations: new Dictionary<string, EquipmentTargetActivationState>(StringComparer.Ordinal)
            {
                [target.TargetKey] = activation,
            },
            savedLoadoutExpectations: target.Kind == PortfolioEquipmentMoveTargetKind.SavedGearset
                ? new Dictionary<string, PortfolioSavedLoadoutExpectation>(StringComparer.Ordinal)
                {
                    [target.TargetKey] = SavedExpectation(target),
                }
                : null);
    }

    private static PortfolioSavedLoadoutExpectation SavedExpectation(PortfolioEquipmentMoveTarget target) => new(
        target.TargetKey,
        target.GearsetId!.Value,
        target.GearsetName!,
        target.ActiveClassJobId!.Value,
        PlayerAdvisorEquippedSlotMap.All.Select(position => new PortfolioSavedLoadoutSlotExpectation(
            position.Position,
            position.Position == EquipmentLoadoutPosition.Head
                ? new(200, true, $"{Owner.LocalContentId}:EquippedItems:{position.EquippedIndex}:200:True")
                : null)).ToArray());

    private static PortfolioEquipmentMoveTarget SavedGearsetMoveTarget() => new(
        "gearset:7",
        PortfolioEquipmentMoveTargetKind.SavedGearset,
        Owner,
        ActiveClassJobId: 3,
        GearsetId: 7,
        GearsetName: "Marauder");

    private static PortfolioEquipmentExecutionCoordinator CreateProvenSavedCoordinator(
        PortfolioEquipmentMoveTarget target,
        FakeRuntime runtime,
        FakeActivationRuntime activationRuntime,
        TestConfiguration? config = null)
    {
        config ??= new TestConfiguration();
        var coordinator = new PortfolioEquipmentExecutionCoordinator(config, runtime, activationRuntime);
        coordinator.Start(CreateState(
            target,
            SavedGearsetActivation(target, EquipmentTargetActivationStatus.Proven)));
        return coordinator;
    }

    private static EquipmentTargetActivationState SavedGearsetActivation(
        PortfolioEquipmentMoveTarget target,
        EquipmentTargetActivationStatus status) =>
        EquipmentTargetActivationCoordinator.Create(
            $"activation-{target.TargetKey}",
            new(
                target.TargetKey,
                EquipmentTargetActivationKind.SavedGearset,
                Owner,
                3,
                GearsetId: 7,
                GearsetName: "Marauder")) with { Status = status };

    private static ActivePlayerActivationProof SavedGearsetProof(DateTimeOffset observedAt) => new(
        Owner,
        3,
        new(7, "Marauder", 3),
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        observedAt,
        true,
        PlayerAdvisorBaselineTargetKind.ActiveLoadout);

    private static PortfolioEquipmentMoveTarget RetainerMoveTarget() => new(
        "retainer:9001",
        PortfolioEquipmentMoveTargetKind.Retainer,
        Owner,
        RetainerName: "Bobo");

    private static EquipmentTargetActivationState RetainerActivation(
        PortfolioEquipmentMoveTarget target,
        EquipmentTargetActivationStatus status) =>
        EquipmentTargetActivationCoordinator.Create(
            $"activation-{target.TargetKey}",
            new(
                target.TargetKey,
                EquipmentTargetActivationKind.Retainer,
                Owner,
                3,
                RetainerId: 9001,
                RetainerName: "Bobo",
                OwnerHomeWorldName: "Siren")) with { Status = status };

    private static RenderedRetainerEquipmentEvidence RetainerProof(
        PortfolioEquipmentMoveTarget target,
        DateTimeOffset observedAt) => new(
            RenderedRetainerEquipmentEvidenceStatus.Complete,
            target.TargetKey,
            observedAt,
            Owner.CharacterName,
            "Siren",
            target.RetainerName!,
            3,
            10,
            PlayerAdvisorEquippedSlotMap.All.Select(position => new RenderedEquipmentSlotObservation(
                position.PositionKey,
                position.Position is EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing
                    ? EquipmentSlot.Ring
                    : position.Position switch
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
                        _ => EquipmentSlot.Unknown,
                    },
                RenderedEquipmentSlotObservationStatus.Empty,
                null)).ToArray(),
            "complete");

    private sealed class FakeRuntime : IPortfolioEquipmentMoveRuntime
    {
        public PortfolioEquipmentMoveTarget ActiveTarget { get; set; } = Target;
        public PortfolioEquipmentSlotAddress SourceAddress { get; set; } = Source;
        public PortfolioEquipmentSlotItem? SourceItem { get; set; } = new(200, true);
        public PortfolioEquipmentSlotItem? DestinationItem { get; set; } = new(100, false);
        public PortfolioEquipmentSlotItem? UnrelatedEquippedDrift { get; set; }
        public int MoveCount { get; private set; }
        public Action? BeforeMove { get; set; }

        public bool TryReadActiveTarget(PortfolioEquipmentMoveTarget expected, out PortfolioEquipmentMoveTarget? target, out string diagnostic)
        {
            target = ActiveTarget;
            diagnostic = string.Empty;
            return true;
        }

        public bool TryReadSlot(PortfolioEquipmentSlotAddress address, out PortfolioEquipmentSlotItem? item, out string diagnostic)
        {
            var destination = new PortfolioEquipmentSlotAddress(
                ActiveTarget.Kind == PortfolioEquipmentMoveTargetKind.Retainer ? "RetainerEquippedItems" : "EquippedItems",
                2);
            var isCanonicalEquipped = address.Container == "EquippedItems" &&
                PlayerAdvisorEquippedSlotMap.All.Any(value => value.EquippedIndex == address.SlotIndex);
            item = address == SourceAddress
                ? SourceItem
                : address == destination
                    ? DestinationItem
                    : isCanonicalEquipped && address.SlotIndex == 3
                        ? UnrelatedEquippedDrift
                        : null;
            diagnostic = string.Empty;
            return address == SourceAddress || address == destination || isCanonicalEquipped;
        }

        public int MoveItemSlot(PortfolioEquipmentSlotAddress source, PortfolioEquipmentSlotAddress destination)
        {
            BeforeMove?.Invoke();
            MoveCount++;
            return 0;
        }
    }

    private sealed class FakeActivationRuntime(SavedGearsetActivationIdentity? identity) : ISavedGearsetActivationRuntime
    {
        public int ActivationCount { get; private set; }
        public int UpdateCount { get; private set; }
        public int ReadIdentityCount { get; private set; }
        public int ReadItemCount { get; private set; }
        public int RemainingUpdateFailures { get; set; }
        public SavedGearsetActivationIdentity? IdentityAfterUpdate { get; set; }
        public PortfolioEquipmentSlotItem? ReadItem { get; set; } = new(200, true);

        public bool TryReadSavedGearset(
            int gearsetId,
            out SavedGearsetActivationIdentity? observed,
            out string diagnostic)
        {
            ReadIdentityCount++;
            observed = UpdateCount > 0 && IdentityAfterUpdate is not null ? IdentityAfterUpdate : identity;
            diagnostic = identity is null ? "Gearset unavailable." : string.Empty;
            return observed is not null;
        }

        public int ActivateSavedGearset(int gearsetId)
        {
            ActivationCount++;
            return 23;
        }

        public int UpdateSavedGearset(int gearsetId)
        {
            UpdateCount++;
            if (RemainingUpdateFailures-- > 0)
                throw new InvalidOperationException("synthetic update failure");
            return gearsetId;
        }

        public bool TryReadSavedGearsetItem(
            int gearsetId,
            EquipmentLoadoutPosition position,
            out PortfolioEquipmentSlotItem? item,
            out string diagnostic)
        {
            ReadItemCount++;
            item = position == EquipmentLoadoutPosition.Head ? ReadItem : null;
            diagnostic = string.Empty;
            return true;
        }
    }

    private sealed class TestConfiguration : ISquireConfigurationStore
    {
        public SquireConfiguration Squire { get; set; } = new();
        public string? OutfitterRouteExecutionStateJson { get; set; }
        public bool EnableMarketAcquisitionDryRunTools { get; set; }
        public PersistedMarketAcquisitionRequestDocument? ActiveMarketAcquisitionRequestDocument { get; set; }
        public PersistedMarketAcquisitionClaim? ActiveMarketAcquisitionClaim { get; set; }
        public int SaveCount { get; private set; }
        public void Save() => SaveCount++;
    }
}
