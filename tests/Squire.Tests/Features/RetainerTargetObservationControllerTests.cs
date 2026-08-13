using System;
using System.Collections.Generic;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire.Observation;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RetainerTargetObservationControllerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Complete_requires_preparation_identity_and_complete_equipment_from_one_owner()
    {
        var runtime = new FakeRuntime
        {
            Preparation = Preparation(RenderedRetainerUiPreparationStatus.Complete),
            Snapshot = RenderedRetainerIdentityParserTests.Snapshot("Venture", "MIN", "Lv. 100"),
            BeginScan = Scan(RenderedEquipmentScanStatus.ReadyToHover, [], 0, 2),
            Steps = new Queue<RenderedEquipmentScanProgress>(
            [
                Scan(RenderedEquipmentScanStatus.Complete,
                [
                    Equipped("main-hand", EquipmentSlot.MainHand),
                    new("head", EquipmentSlot.Head, RenderedEquipmentSlotObservationStatus.Empty, null),
                ], 2, 2),
            ]),
        };
        var controller = new RetainerTargetObservationController(runtime);

        Assert.Equal(RetainerTargetObservationStatus.OpeningTarget,
            controller.Begin(RenderedRetainerIdentityParserTests.Target(), RenderedRetainerIdentityParserTests.Owner(), Start).Status);
        Assert.Equal(RetainerTargetObservationStatus.ScanningEquipment,
            controller.Advance(RenderedRetainerIdentityParserTests.Owner(), Start.AddSeconds(1)).Status);
        var completed = controller.Advance(RenderedRetainerIdentityParserTests.Owner(), Start.AddSeconds(2));

        Assert.Equal(RetainerTargetObservationStatus.Complete, completed.Status);
        Assert.Equal(RenderedRetainerEquipmentEvidenceStatus.Complete, completed.Evidence?.Status);
        Assert.Contains(completed.Evidence!.Equipment, value => value.Status == RenderedEquipmentSlotObservationStatus.Empty);
    }

    [Fact]
    public void Owner_drift_cancels_scan_and_discards_partial_evidence()
    {
        var runtime = ReadyRuntime();
        var controller = new RetainerTargetObservationController(runtime);
        controller.Begin(RenderedRetainerIdentityParserTests.Target(), RenderedRetainerIdentityParserTests.Owner(), Start);
        controller.Advance(RenderedRetainerIdentityParserTests.Owner(), Start.AddSeconds(1));

        var failed = controller.Advance(new(2, "Other Owner", "Gilgamesh"), Start.AddSeconds(2));

        Assert.Equal(RetainerTargetObservationStatus.Failed, failed.Status);
        Assert.Null(failed.Identity);
        Assert.Null(failed.Evidence);
        Assert.True(runtime.ScanCancelled);
    }

    [Fact]
    public void Rendered_retainer_drift_cancels_scan_and_discards_partial_evidence()
    {
        var runtime = ReadyRuntime();
        var controller = new RetainerTargetObservationController(runtime);
        controller.Begin(RenderedRetainerIdentityParserTests.Target(), RenderedRetainerIdentityParserTests.Owner(), Start);
        controller.Advance(RenderedRetainerIdentityParserTests.Owner(), Start.AddSeconds(1));
        runtime.Snapshot = RenderedRetainerIdentityParserTests.Snapshot("Someone Else", "MIN", "Lv. 100");

        var failed = controller.Advance(RenderedRetainerIdentityParserTests.Owner(), Start.AddSeconds(2));

        Assert.Equal(RetainerTargetObservationStatus.Failed, failed.Status);
        Assert.Null(failed.Evidence);
        Assert.True(runtime.ScanCancelled);
    }

    [Fact]
    public void Opening_timeout_fails_without_starting_an_equipment_scan()
    {
        var runtime = new FakeRuntime
        {
            Preparation = Preparation(RenderedRetainerUiPreparationStatus.Complete),
            Snapshot = new(Start, []),
            OpenResult = new(RetainerTargetOpenStatus.Waiting, "waiting"),
        };
        var controller = new RetainerTargetObservationController(runtime, TimeSpan.FromSeconds(2));
        controller.Begin(RenderedRetainerIdentityParserTests.Target(), RenderedRetainerIdentityParserTests.Owner(), Start);

        var failed = controller.Advance(RenderedRetainerIdentityParserTests.Owner(), Start.AddSeconds(3));

        Assert.Equal(RetainerTargetObservationStatus.Failed, failed.Status);
        Assert.False(runtime.ScanBegan);
    }

    [Fact]
    public void Explicit_cancel_stops_preparation_and_scan_without_retaining_identity()
    {
        var runtime = ReadyRuntime();
        var controller = new RetainerTargetObservationController(runtime);
        controller.Begin(RenderedRetainerIdentityParserTests.Target(), RenderedRetainerIdentityParserTests.Owner(), Start);
        controller.Advance(RenderedRetainerIdentityParserTests.Owner(), Start.AddSeconds(1));

        var cancelled = controller.Cancel();

        Assert.Equal(RetainerTargetObservationStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.Identity);
        Assert.Null(cancelled.Evidence);
        Assert.True(runtime.PreparationCancelled);
        Assert.True(runtime.ScanCancelled);
    }

    private static FakeRuntime ReadyRuntime() => new()
    {
        Preparation = Preparation(RenderedRetainerUiPreparationStatus.Complete),
        Snapshot = RenderedRetainerIdentityParserTests.Snapshot("Venture", "MIN", "Lv. 100"),
        BeginScan = Scan(RenderedEquipmentScanStatus.ReadyToHover, [], 0, 2),
        Steps = new Queue<RenderedEquipmentScanProgress>(
        [
            Scan(RenderedEquipmentScanStatus.ReadyToHover, [Equipped("main-hand", EquipmentSlot.MainHand)], 1, 2),
        ]),
    };

    private static RenderedRetainerUiPreparationProgress Preparation(RenderedRetainerUiPreparationStatus status) =>
        new(status, 0, status.ToString());

    private static RenderedEquipmentScanProgress Scan(
        RenderedEquipmentScanStatus status,
        IReadOnlyList<RenderedEquipmentSlotObservation> observations,
        int complete,
        int total) =>
        new(status, complete, total, null, observations, status.ToString());

    private static RenderedEquipmentSlotObservation Equipped(string key, EquipmentSlot slot) => new(
        key,
        slot,
        RenderedEquipmentSlotObservationStatus.Equipped,
        new(RenderedItemDetailStatus.Complete, "Synthetic Gear", RenderedItemQuality.Normal, 1, 1,
            "MIN BTN", "Botanist's Primary Tool", new Dictionary<string, int>(), new Dictionary<string, int>(), "complete"));

    private sealed class FakeRuntime : IRetainerTargetObservationRuntime
    {
        public RenderedRetainerUiPreparationProgress Preparation { get; set; } = Preparation(RenderedRetainerUiPreparationStatus.Traveling);
        public AgentBridgeRenderedUiSnapshot Snapshot { get; set; } = new(Start, []);
        public RetainerTargetOpenResult OpenResult { get; set; } = new(RetainerTargetOpenStatus.Accepted, "accepted");
        public RenderedEquipmentScanProgress BeginScan { get; set; } = Scan(RenderedEquipmentScanStatus.Failed, [], 0, 0);
        public Queue<RenderedEquipmentScanProgress> Steps { get; set; } = new();
        public bool PreparationCancelled { get; private set; }
        public bool ScanCancelled { get; private set; }
        public bool ScanBegan { get; private set; }

        public RenderedRetainerUiPreparationProgress BeginPreparation(string ownerHomeWorld) => Preparation;
        public RenderedRetainerUiPreparationProgress AdvancePreparation() => Preparation;
        public RenderedRetainerUiPreparationProgress CancelPreparation()
        {
            PreparationCancelled = true;
            return Preparation(RenderedRetainerUiPreparationStatus.Cancelled);
        }
        public RetainerTargetOpenResult TryOpenRetainer(string retainerName) => OpenResult;
        public AgentBridgeRenderedUiSnapshot CaptureRetainerUi() => Snapshot;
        public RenderedEquipmentScanProgress BeginRetainerEquipmentScan()
        {
            ScanBegan = true;
            return BeginScan;
        }
        public RenderedEquipmentScanStepResult AdvanceRetainerEquipmentScan()
        {
            var progress = Steps.Count > 0 ? Steps.Dequeue() : BeginScan;
            return new(true, progress, progress.Diagnostic);
        }
        public RenderedEquipmentScanProgress CancelRetainerEquipmentScan()
        {
            ScanCancelled = true;
            return Scan(RenderedEquipmentScanStatus.Cancelled, [], 0, 0);
        }
    }
}
