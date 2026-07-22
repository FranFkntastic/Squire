using System;
using System.Collections.Generic;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace MarketMafioso.Windows.Squire;

internal sealed class SquireInventoryChangeMonitor : IDisposable
{
    private static readonly HashSet<GameInventoryType> ObservedContainers =
    [
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
        GameInventoryType.EquippedItems,
        GameInventoryType.ArmoryMainHand,
        GameInventoryType.ArmoryOffHand,
        GameInventoryType.ArmoryHead,
        GameInventoryType.ArmoryBody,
        GameInventoryType.ArmoryHands,
        GameInventoryType.ArmoryLegs,
        GameInventoryType.ArmoryFeets,
        GameInventoryType.ArmoryEar,
        GameInventoryType.ArmoryNeck,
        GameInventoryType.ArmoryWrist,
        GameInventoryType.ArmoryRings,
        GameInventoryType.ArmorySoulCrystal,
    ];

    private readonly IGameInventory gameInventory;
    private readonly IDataManager dataManager;
    private readonly Action equipmentChanged;

    public SquireInventoryChangeMonitor(
        IGameInventory gameInventory,
        IDataManager dataManager,
        Action equipmentChanged)
    {
        this.gameInventory = gameInventory;
        this.dataManager = dataManager;
        this.equipmentChanged = equipmentChanged;
        gameInventory.InventoryChangedRaw += OnInventoryChanged;
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> changes)
    {
        foreach (var change in changes)
        {
            ref readonly var item = ref change.Item;
            if (!IsObservedContainer(item.ContainerType) || item.BaseItemId == 0)
                continue;

            var definition = dataManager.GetExcelSheet<LuminaItem>()?.GetRowOrDefault(item.BaseItemId);
            if (definition is not null && definition.Value.EquipSlotCategory.RowId == 0)
                continue;

            equipmentChanged();
            return;
        }
    }

    internal static bool IsObservedContainer(GameInventoryType type) => ObservedContainers.Contains(type);

    public void Dispose() => gameInventory.InventoryChangedRaw -= OnInventoryChanged;
}
