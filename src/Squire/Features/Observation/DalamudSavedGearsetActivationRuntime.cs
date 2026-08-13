using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using MarketMafioso.Squire.Outfitter.Portfolio;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudSavedGearsetActivationRuntime : ISavedGearsetActivationRuntime
{
    public unsafe bool TryReadSavedGearset(
        int gearsetId,
        out SavedGearsetActivationIdentity? identity,
        out string diagnostic)
    {
        identity = null;
        diagnostic = string.Empty;
        var module = RaptureGearsetModule.Instance();
        if (module == null)
        {
            diagnostic = "RaptureGearsetModule is unavailable.";
            return false;
        }
        if (gearsetId is < 0 or >= 100 || !module->IsValidGearset(gearsetId))
        {
            diagnostic = $"Saved gearset {gearsetId} is no longer valid.";
            return false;
        }
        var entry = module->GetGearset(gearsetId);
        if (entry == null ||
            !entry->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists) ||
            string.IsNullOrWhiteSpace(entry->NameString) ||
            entry->ClassJob == 0)
        {
            diagnostic = $"Saved gearset {gearsetId} has incomplete live identity.";
            return false;
        }
        identity = new(gearsetId, entry->NameString, entry->ClassJob);
        return true;
    }

    public unsafe int ActivateSavedGearset(int gearsetId)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            throw new InvalidOperationException("RaptureGearsetModule is unavailable.");
        return module->EquipGearset(gearsetId);
    }

    public unsafe int UpdateSavedGearset(int gearsetId)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            throw new InvalidOperationException("RaptureGearsetModule is unavailable.");
        return module->UpdateGearset(gearsetId);
    }

    public unsafe bool TryReadSavedGearsetItem(
        int gearsetId,
        Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition position,
        out PortfolioEquipmentSlotItem? item,
        out string diagnostic)
    {
        item = null;
        diagnostic = string.Empty;
        var module = RaptureGearsetModule.Instance();
        if (module == null || gearsetId is < 0 or >= 100 || !module->IsValidGearset(gearsetId))
        {
            diagnostic = "The saved gearset is unavailable for update proof.";
            return false;
        }
        var entry = module->GetGearset(gearsetId);
        if (entry == null || !entry->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
        {
            diagnostic = "The saved gearset entry is unavailable for update proof.";
            return false;
        }
        var index = GearsetItemIndex(position);
        if (index is null)
        {
            diagnostic = "The equipment position has no saved-gearset slot.";
            return false;
        }
        var observed = entry->GetItem(index.Value).ItemId;
        if (observed != 0)
            item = new(observed % 1_000_000, observed >= 1_000_000);
        return true;
    }

    private static RaptureGearsetModule.GearsetItemIndex? GearsetItemIndex(
        Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition position) => position switch
        {
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.MainHand => RaptureGearsetModule.GearsetItemIndex.MainHand,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.OffHand => RaptureGearsetModule.GearsetItemIndex.OffHand,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.Head => RaptureGearsetModule.GearsetItemIndex.Head,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.Body => RaptureGearsetModule.GearsetItemIndex.Body,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.Hands => RaptureGearsetModule.GearsetItemIndex.Hands,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.Legs => RaptureGearsetModule.GearsetItemIndex.Legs,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.Feet => RaptureGearsetModule.GearsetItemIndex.Feet,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.Ears => RaptureGearsetModule.GearsetItemIndex.Ears,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.Neck => RaptureGearsetModule.GearsetItemIndex.Neck,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.Wrists => RaptureGearsetModule.GearsetItemIndex.Wrists,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.RightRing => RaptureGearsetModule.GearsetItemIndex.RingRight,
            Franthropy.Dalamud.Equipment.EquipmentLoadoutPosition.LeftRing => RaptureGearsetModule.GearsetItemIndex.RingLeft,
            _ => null,
        };
}

/// <summary>
/// Captures the proof consumed by <see cref="EquipmentTargetActivationCoordinator"/>. It does not
/// infer activation from the native return: the current gearset entry, active class/job, and a
/// separately completed active-player baseline must agree in the same post-request observation.
/// </summary>
public sealed class DalamudActivePlayerActivationProofSource
{
    private readonly IPlayerState playerState;
    private readonly IPlayerAdvisorBaselineSource baselineSource;

    public DalamudActivePlayerActivationProofSource(
        IPlayerState playerState,
        IPlayerAdvisorBaselineSource baselineSource)
    {
        this.playerState = playerState ?? throw new ArgumentNullException(nameof(playerState));
        this.baselineSource = baselineSource ?? throw new ArgumentNullException(nameof(baselineSource));
    }

    public unsafe ActivePlayerActivationProof Capture()
    {
        var baseline = baselineSource.Capture();
        var family = baseline.ClassJobId is { } classJobId ? AdvisorStatFamilies.Resolve(classJobId) : null;
        var complete = PlayerAdvisorBaselineAssembler.IsCompleteAndConsistent(baseline, family, out _);
        var module = RaptureGearsetModule.Instance();
        SavedGearsetActivationIdentity? gearset = null;
        if (module != null && module->CurrentGearsetIndex is >= 0 and < 100)
        {
            var entry = module->GetGearset(module->CurrentGearsetIndex);
            if (entry != null &&
                entry->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists) &&
                !string.IsNullOrWhiteSpace(entry->NameString) &&
                entry->ClassJob != 0)
            {
                gearset = new(module->CurrentGearsetIndex, entry->NameString, entry->ClassJob);
            }
        }

        var provenance = baseline.CaptureProvenance;
        var owner = new PortfolioEquipmentMoveOwner(
            playerState.ContentId,
            playerState.CharacterName.ToString(),
            playerState.HomeWorld.RowId);
        return new(
            owner,
            playerState.ClassJob.RowId,
            gearset,
            baseline.EquipmentSnapshot?.GenerationId ?? Guid.Empty,
            provenance?.CompletedAtUtc ?? default,
            complete,
            baseline.Target?.Kind ?? PlayerAdvisorBaselineTargetKind.ActiveLoadout);
    }
}
