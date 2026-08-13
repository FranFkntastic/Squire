using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using MarketMafioso.Squire.Outfitter.Portfolio;

namespace MarketMafioso.Squire.Observation;

/// <summary>
/// The only native gameplay seam for portfolio equipping. Container/slot and exact item identity
/// validation stay in <see cref="PortfolioEquipmentMove"/>; this runtime reads the current slots
/// and submits one InventoryManager move when that validator authorizes it.
/// </summary>
public sealed class DalamudPortfolioEquipmentMoveRuntime : IPortfolioEquipmentMoveRuntime
{
    private readonly IPlayerState playerState;
    private readonly Func<RenderedRetainerEquipmentEvidence?> renderedRetainerProvider;
    private readonly Func<string, bool> liveRetainerIdentityMatches;

    public DalamudPortfolioEquipmentMoveRuntime(
        IPlayerState playerState,
        Func<RenderedRetainerEquipmentEvidence?> renderedRetainerProvider,
        Func<string, bool>? liveRetainerIdentityMatches = null)
    {
        this.playerState = playerState ?? throw new ArgumentNullException(nameof(playerState));
        this.renderedRetainerProvider = renderedRetainerProvider ?? throw new ArgumentNullException(nameof(renderedRetainerProvider));
        this.liveRetainerIdentityMatches = liveRetainerIdentityMatches ?? (_ => true);
    }

    public unsafe bool TryReadActiveTarget(
        PortfolioEquipmentMoveTarget expected,
        out PortfolioEquipmentMoveTarget? target,
        out string diagnostic)
    {
        target = null;
        diagnostic = string.Empty;
        if (!playerState.IsLoaded || playerState.ContentId == 0 || playerState.HomeWorld.RowId == 0)
        {
            diagnostic = "The active character owner is unavailable.";
            return false;
        }
        var owner = new PortfolioEquipmentMoveOwner(
            playerState.ContentId,
            playerState.CharacterName.ToString(),
            playerState.HomeWorld.RowId);
        if (expected.Kind is PortfolioEquipmentMoveTargetKind.ActivePlayer or PortfolioEquipmentMoveTargetKind.SavedGearset)
        {
            int? gearsetId = null;
            string? gearsetName = null;
            if (expected.Kind == PortfolioEquipmentMoveTargetKind.SavedGearset)
            {
                var module = RaptureGearsetModule.Instance();
                if (module == null || module->CurrentGearsetIndex is < 0 or >= 100)
                {
                    diagnostic = "The current saved gearset identity is unavailable.";
                    return false;
                }
                var entry = module->GetGearset(module->CurrentGearsetIndex);
                if (entry == null || !entry->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                {
                    diagnostic = "The current saved gearset is no longer valid.";
                    return false;
                }
                gearsetId = module->CurrentGearsetIndex;
                gearsetName = entry->NameString;
            }
            target = expected with
            {
                Owner = owner,
                ActiveClassJobId = playerState.ClassJob.RowId,
                GearsetId = gearsetId,
                GearsetName = gearsetName,
            };
            return true;
        }

        var retainer = renderedRetainerProvider();
        if (retainer is not { Status: RenderedRetainerEquipmentEvidenceStatus.Complete } ||
            !liveRetainerIdentityMatches(expected.TargetKey) ||
            !string.Equals(retainer.TargetKey, expected.TargetKey, StringComparison.Ordinal) ||
            !string.Equals(retainer.RetainerName, expected.RetainerName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(retainer.OwnerCharacterName, owner.CharacterName, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = "The visible rendered retainer identity does not match the authorized equipment target.";
            return false;
        }
        target = expected with { Owner = owner, ActiveClassJobId = null, RetainerName = retainer.RetainerName };
        return true;
    }

    public unsafe bool TryReadSlot(
        PortfolioEquipmentSlotAddress address,
        out PortfolioEquipmentSlotItem? item,
        out string diagnostic)
    {
        item = null;
        diagnostic = string.Empty;
        if (!TryResolveAddress(address, out var containerType, out var slot, out diagnostic))
            return false;

        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            diagnostic = "InventoryManager is unavailable.";
            return false;
        }
        var container = manager->GetInventoryContainer(containerType);
        if (container == null || !container->IsLoaded)
        {
            diagnostic = $"Inventory container {address.Container} is not loaded.";
            return false;
        }
        if (slot >= container->Size)
        {
            diagnostic = $"Slot {slot} is outside loaded container {address.Container}.";
            return false;
        }
        var inventoryItem = container->GetInventorySlot(slot);
        if (inventoryItem == null)
        {
            diagnostic = $"Slot {address.Container}:{slot} is unavailable.";
            return false;
        }
        if (inventoryItem->ItemId != 0)
        {
            item = new(
                NormalizeItemId(inventoryItem->ItemId),
                inventoryItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
        }
        return true;
    }

    public unsafe int MoveItemSlot(
        PortfolioEquipmentSlotAddress source,
        PortfolioEquipmentSlotAddress destination)
    {
        if (!TryResolveAddress(source, out var sourceContainer, out var sourceSlot, out var sourceError))
            throw new InvalidOperationException(sourceError);
        if (!TryResolveAddress(destination, out var destinationContainer, out var destinationSlot, out var destinationError))
            throw new InvalidOperationException(destinationError);
        var manager = InventoryManager.Instance();
        if (manager == null)
            throw new InvalidOperationException("InventoryManager is unavailable.");
        return manager->MoveItemSlot(sourceContainer, sourceSlot, destinationContainer, destinationSlot);
    }

    private static bool TryResolveAddress(
        PortfolioEquipmentSlotAddress address,
        out InventoryType container,
        out ushort slot,
        out string diagnostic)
    {
        container = default;
        slot = default;
        diagnostic = string.Empty;
        if (address.SlotIndex is < 0 or > ushort.MaxValue ||
            !Enum.TryParse(address.Container, ignoreCase: false, out container) ||
            !Enum.IsDefined(container))
        {
            diagnostic = $"Inventory address {address.Container}:{address.SlotIndex} is invalid.";
            return false;
        }
        slot = checked((ushort)address.SlotIndex);
        return true;
    }

    private static uint NormalizeItemId(uint itemId) => itemId >= 1_000_000 ? itemId % 1_000_000 : itemId;
}
