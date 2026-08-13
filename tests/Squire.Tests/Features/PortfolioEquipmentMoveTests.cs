using MarketMafioso.Squire.Outfitter.Portfolio;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Tests.Squire;

public sealed class PortfolioEquipmentMoveTests
{
    private static readonly PortfolioEquipmentMoveOwner Owner = new(42, "A' Hero", 67);

    [Fact]
    public void Execute_SubmitsExactlyOneMoveForExactActivePlayerAuthorityAndSlots()
    {
        var request = ActiveRequest();
        var runtime = new FakeRuntime(request.Target,
            new(200, true),
            new(100, false));

        var result = PortfolioEquipmentMove.Execute(request, runtime);

        Assert.True(result.WasSubmitted);
        Assert.Equal(1, runtime.MoveCount);
        Assert.Equal(17, result.NativeResult);
        Assert.Equal(request.Source, runtime.MovedSource);
        Assert.Equal(request.Destination, runtime.MovedDestination);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("target")]
    [InlineData("job")]
    [InlineData("source-item")]
    [InlineData("source-quality")]
    [InlineData("source-slot")]
    [InlineData("destination-item")]
    [InlineData("destination-quality")]
    [InlineData("destination-slot")]
    public void Execute_RefusesEveryAuthorityOrExactSlotDriftWithoutCallingMove(string drift)
    {
        var request = ActiveRequest();
        var active = request.Target;
        var source = new PortfolioEquipmentSlotItem(200, true);
        var destination = new PortfolioEquipmentSlotItem(100, false);
        switch (drift)
        {
            case "owner": active = active with { Owner = Owner with { LocalContentId = 99 } }; break;
            case "target": active = active with { TargetKey = "active:other" }; break;
            case "job": active = active with { ActiveClassJobId = 2 }; break;
            case "source-item": source = source with { ItemId = 201 }; break;
            case "source-quality": source = source with { IsHighQuality = false }; break;
            case "source-slot": request = request with { Source = request.Source with { SlotIndex = 8 } }; break;
            case "destination-item": destination = destination with { ItemId = 101 }; break;
            case "destination-quality": destination = destination with { IsHighQuality = true }; break;
            case "destination-slot": request = request with { Destination = request.Destination with { SlotIndex = 4 } }; break;
        }
        var runtime = new FakeRuntime(active, source, destination);

        var result = PortfolioEquipmentMove.Execute(request, runtime);

        Assert.Equal(PortfolioEquipmentMoveResultKind.Refused, result.Kind);
        Assert.Equal(0, runtime.MoveCount);
    }

    [Fact]
    public void Execute_RequiresExactRetainerIdentityAndRetainerEquippedDestination()
    {
        var sourceAddress = new PortfolioEquipmentSlotAddress("RetainerPage1", 2);
        var destinationAddress = new PortfolioEquipmentSlotAddress("RetainerEquippedItems", 3);
        var sourceItem = new PortfolioEquipmentSlotItem(300, false);
        var target = new PortfolioEquipmentMoveTarget(
            "retainer:9001",
            PortfolioEquipmentMoveTargetKind.Retainer,
            Owner,
            RetainerName: "Bobo");
        var request = new PortfolioEquipmentMoveRequest(
            "execution-2",
            "step-1",
            target,
            EquipmentLoadoutPosition.Body,
            sourceAddress,
            destinationAddress,
            Exact(sourceAddress, sourceItem),
            null);
        var runtime = new FakeRuntime(target, sourceItem, null);

        var accepted = PortfolioEquipmentMove.Execute(request, runtime);
        var wrongRetainer = PortfolioEquipmentMove.Execute(
            request,
            new FakeRuntime(target with { RetainerName = "Not Bobo" }, sourceItem, null));
        var wrongDestination = PortfolioEquipmentMove.Execute(
            request with { Destination = new("EquippedItems", 3) },
            new FakeRuntime(target, sourceItem, null));

        Assert.True(accepted.WasSubmitted);
        Assert.Equal(PortfolioEquipmentMoveResultKind.Refused, wrongRetainer.Kind);
        Assert.Equal(PortfolioEquipmentMoveResultKind.Refused, wrongDestination.Kind);
        Assert.Equal(1, runtime.MoveCount);
    }

    [Fact]
    public void Execute_RefusesUnreadableSlotAndNativeFailureRemainsTyped()
    {
        var request = ActiveRequest();
        var unavailable = new FakeRuntime(request.Target, new(200, true), new(100, false)) { SourceReadable = false };
        var throwing = new FakeRuntime(request.Target, new(200, true), new(100, false)) { ThrowOnMove = true };

        var unavailableResult = PortfolioEquipmentMove.Execute(request, unavailable);
        var failedResult = PortfolioEquipmentMove.Execute(request, throwing);

        Assert.Equal(PortfolioEquipmentMoveResultKind.RuntimeUnavailable, unavailableResult.Kind);
        Assert.Equal(0, unavailable.MoveCount);
        Assert.Equal(PortfolioEquipmentMoveResultKind.RuntimeFailed, failedResult.Kind);
        Assert.Equal(1, throwing.MoveCount);
    }

    private static PortfolioEquipmentMoveRequest ActiveRequest()
    {
        var source = new PortfolioEquipmentSlotAddress("ArmoryHead", 7);
        var destination = new PortfolioEquipmentSlotAddress("EquippedItems", 3);
        return new(
            "execution-1",
            "step-head",
            new("active-loadout", PortfolioEquipmentMoveTargetKind.ActivePlayer, Owner, ActiveClassJobId: 1),
            EquipmentLoadoutPosition.Body,
            source,
            destination,
            Exact(source, new(200, true)),
            Exact(destination, new(100, false)));
    }

    private static PortfolioExactItem Exact(
        PortfolioEquipmentSlotAddress address,
        PortfolioEquipmentSlotItem item) =>
        new(item.ItemId, item.IsHighQuality, PortfolioEquipmentMove.ExactInstanceId(Owner, address, item));

    private sealed class FakeRuntime(
        PortfolioEquipmentMoveTarget? activeTarget,
        PortfolioEquipmentSlotItem? source,
        PortfolioEquipmentSlotItem? destination) : IPortfolioEquipmentMoveRuntime
    {
        public bool SourceReadable { get; init; } = true;
        public bool ThrowOnMove { get; init; }
        public int MoveCount { get; private set; }
        public PortfolioEquipmentSlotAddress? MovedSource { get; private set; }
        public PortfolioEquipmentSlotAddress? MovedDestination { get; private set; }

        public bool TryReadActiveTarget(
            PortfolioEquipmentMoveTarget expected,
            out PortfolioEquipmentMoveTarget? target,
            out string diagnostic)
        {
            target = activeTarget;
            diagnostic = activeTarget is null ? "No target." : string.Empty;
            return activeTarget is not null;
        }

        public bool TryReadSlot(
            PortfolioEquipmentSlotAddress address,
            out PortfolioEquipmentSlotItem? item,
            out string diagnostic)
        {
            var isSource = address.Container is "ArmoryHead" or "RetainerPage1";
            if (isSource && !SourceReadable)
            {
                item = null;
                diagnostic = "Source container is not loaded.";
                return false;
            }
            item = isSource ? source : destination;
            diagnostic = string.Empty;
            return true;
        }

        public int MoveItemSlot(
            PortfolioEquipmentSlotAddress sourceAddress,
            PortfolioEquipmentSlotAddress destinationAddress)
        {
            MoveCount++;
            MovedSource = sourceAddress;
            MovedDestination = destinationAddress;
            if (ThrowOnMove)
                throw new InvalidOperationException("synthetic native failure");
            return 17;
        }
    }
}
