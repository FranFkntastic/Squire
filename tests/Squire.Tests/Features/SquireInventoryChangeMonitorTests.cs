using Dalamud.Game.Inventory;
using MarketMafioso.Windows.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireInventoryChangeMonitorTests
{
    [Theory]
    [InlineData(GameInventoryType.Inventory1)]
    [InlineData(GameInventoryType.Inventory4)]
    [InlineData(GameInventoryType.EquippedItems)]
    [InlineData(GameInventoryType.ArmoryMainHand)]
    [InlineData(GameInventoryType.ArmoryRings)]
    public void IsObservedContainer_AcceptsPlayerEquipmentStorage(GameInventoryType type) =>
        Assert.True(SquireInventoryChangeMonitor.IsObservedContainer(type));

    [Theory]
    [InlineData(GameInventoryType.RetainerPage1)]
    [InlineData(GameInventoryType.SaddleBag1)]
    [InlineData(GameInventoryType.Currency)]
    [InlineData(GameInventoryType.Crystals)]
    public void IsObservedContainer_RejectsStorageOutsideSquireSnapshot(GameInventoryType type) =>
        Assert.False(SquireInventoryChangeMonitor.IsObservedContainer(type));
}
