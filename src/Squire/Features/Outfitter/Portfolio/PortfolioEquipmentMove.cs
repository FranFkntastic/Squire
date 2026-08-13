using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Portfolio;

public enum PortfolioEquipmentMoveTargetKind
{
    ActivePlayer,
    SavedGearset,
    Retainer,
}

public enum PortfolioEquipmentMoveResultKind
{
    Refused,
    Submitted,
    RuntimeUnavailable,
    RuntimeFailed,
}

public sealed record PortfolioEquipmentMoveOwner(
    ulong LocalContentId,
    string CharacterName,
    uint HomeWorldId);

public sealed record PortfolioEquipmentMoveTarget(
    string TargetKey,
    PortfolioEquipmentMoveTargetKind Kind,
    PortfolioEquipmentMoveOwner Owner,
    uint? ActiveClassJobId = null,
    string? RetainerName = null,
    int? GearsetId = null,
    string? GearsetName = null);

public sealed record PortfolioEquipmentSlotAddress(
    string Container,
    int SlotIndex);

public sealed record PortfolioEquipmentSlotItem(
    uint ItemId,
    bool IsHighQuality);

public sealed record PortfolioEquipmentMoveRequest(
    string ExecutionId,
    string StepId,
    PortfolioEquipmentMoveTarget Target,
    EquipmentLoadoutPosition Position,
    PortfolioEquipmentSlotAddress Source,
    PortfolioEquipmentSlotAddress Destination,
    PortfolioExactItem ExpectedSource,
    PortfolioExactItem? ExpectedDestination);

public sealed record PortfolioEquipmentMoveObservation(
    PortfolioEquipmentMoveTarget ActiveTarget,
    PortfolioEquipmentSlotAddress Source,
    PortfolioEquipmentSlotItem? SourceItem,
    PortfolioEquipmentSlotAddress Destination,
    PortfolioEquipmentSlotItem? DestinationItem);

public sealed record PortfolioEquipmentMoveResult(
    PortfolioEquipmentMoveResultKind Kind,
    string Code,
    string Message,
    int? NativeResult = null)
{
    public bool WasSubmitted => Kind == PortfolioEquipmentMoveResultKind.Submitted;
}

public interface IPortfolioEquipmentMoveRuntime
{
    bool TryReadActiveTarget(
        PortfolioEquipmentMoveTarget expected,
        out PortfolioEquipmentMoveTarget? target,
        out string diagnostic);

    bool TryReadSlot(
        PortfolioEquipmentSlotAddress address,
        out PortfolioEquipmentSlotItem? item,
        out string diagnostic);

    int MoveItemSlot(
        PortfolioEquipmentSlotAddress source,
        PortfolioEquipmentSlotAddress destination);
}

/// <summary>
/// The fail-closed boundary immediately in front of the native inventory move. Validation binds
/// the active owner and target, both exact slot addresses, and the item/quality identities observed
/// in those slots. The caller must still obtain fresh after-evidence through
/// <see cref="PortfolioEquipmentExecution.AcceptAfter"/>; a submitted native call is not proof that
/// gameplay changed.
/// </summary>
public static class PortfolioEquipmentMove
{
    private static readonly HashSet<string> PlayerSourceContainers = new(StringComparer.Ordinal)
    {
        "Inventory1", "Inventory2", "Inventory3", "Inventory4",
        "ArmoryMainHand", "ArmoryOffHand", "ArmoryHead", "ArmoryBody", "ArmoryHands",
        "ArmoryLegs", "ArmoryFeets", "ArmoryEar", "ArmoryNeck", "ArmoryWrist",
        "ArmoryRings", "ArmorySoulCrystal", "EquippedItems",
    };

    private static readonly HashSet<string> RetainerSourceContainers = new(StringComparer.Ordinal)
    {
        "Inventory1", "Inventory2", "Inventory3", "Inventory4",
        "RetainerPage1", "RetainerPage2", "RetainerPage3", "RetainerPage4",
        "RetainerPage5", "RetainerPage6", "RetainerPage7", "RetainerEquippedItems",
    };

    public static PortfolioEquipmentMoveResult Execute(
        PortfolioEquipmentMoveRequest request,
        IPortfolioEquipmentMoveRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);

        if (!runtime.TryReadActiveTarget(request.Target, out var activeTarget, out var targetDiagnostic) || activeTarget is null)
            return Unavailable("TargetAuthorityUnavailable", targetDiagnostic);
        if (!runtime.TryReadSlot(request.Source, out var sourceItem, out var sourceDiagnostic))
            return Unavailable("SourceSlotUnavailable", sourceDiagnostic);
        if (!runtime.TryReadSlot(request.Destination, out var destinationItem, out var destinationDiagnostic))
            return Unavailable("DestinationSlotUnavailable", destinationDiagnostic);

        var refusal = Validate(request, new(
            activeTarget,
            request.Source,
            sourceItem,
            request.Destination,
            destinationItem));
        if (refusal is not null)
            return refusal;

        try
        {
            var nativeResult = runtime.MoveItemSlot(request.Source, request.Destination);
            return new(
                PortfolioEquipmentMoveResultKind.Submitted,
                "MoveSubmitted",
                "The exact slot move was submitted. Fresh after-evidence is required before this step can complete.",
                nativeResult);
        }
        catch (Exception exception)
        {
            return new(
                PortfolioEquipmentMoveResultKind.RuntimeFailed,
                "MoveInvocationFailed",
                $"The native slot move failed before Squire could observe a result: {exception.Message}");
        }
    }

    public static PortfolioEquipmentMoveResult? Validate(
        PortfolioEquipmentMoveRequest request,
        PortfolioEquipmentMoveObservation observation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observation);

        if (string.IsNullOrWhiteSpace(request.ExecutionId) || string.IsNullOrWhiteSpace(request.StepId))
            return Refuse("MoveIdentityMissing", "The equipment execution or slot step has no stable identity.");
        if (!ExactTarget(request.Target, observation.ActiveTarget))
            return Refuse("TargetChanged", "The active owner, target, job, or retainer changed before the slot move.");
        if (!HasCoherentTargetIdentity(request.Target))
            return Refuse("TargetIdentityInvalid", "The equipment target identity is malformed for its authority kind.");
        if (request.Source != observation.Source || request.Destination != observation.Destination)
            return Refuse("SlotAddressChanged", "The observed source or destination address does not match the authorized slot move.");
        if (!IsValidAddress(request.Source) || !IsValidAddress(request.Destination))
            return Refuse("InvalidSlotAddress", "The source or destination container and slot are invalid.");
        if (request.Source == request.Destination)
            return Refuse("SameSlot", "The source and destination must be different exact slots.");

        var expectedDestination = request.Target.Kind == PortfolioEquipmentMoveTargetKind.Retainer
            ? "RetainerEquippedItems"
            : "EquippedItems";
        if (!string.Equals(request.Destination.Container, expectedDestination, StringComparison.Ordinal))
            return Refuse("WrongDestinationContainer", $"The selected target requires destination container {expectedDestination}.");
        if (DestinationSlot(request.Position) != request.Destination.SlotIndex)
            return Refuse("WrongDestinationSlot", "The destination slot does not match the authorized equipment position.");
        var validSources = request.Target.Kind == PortfolioEquipmentMoveTargetKind.Retainer
            ? RetainerSourceContainers
            : PlayerSourceContainers;
        if (!validSources.Contains(request.Source.Container))
            return Refuse("WrongSourceAuthority", "The source container does not belong to the selected target's supported equipment authority.");

        if (!ExactItem(request.Target.Owner, request.Source, request.ExpectedSource, observation.SourceItem))
            return Refuse("SourceItemChanged", "The exact source item instance or quality changed before the slot move.");
        if (!ExactItem(request.Target.Owner, request.Destination, request.ExpectedDestination, observation.DestinationItem))
            return Refuse("DestinationItemChanged", "The exact destination item instance or quality changed before the slot move.");

        return null;
    }

    public static string ExactInstanceId(
        PortfolioEquipmentMoveOwner owner,
        PortfolioEquipmentSlotAddress address,
        PortfolioEquipmentSlotItem item) =>
        $"{owner.LocalContentId}:{address.Container}:{address.SlotIndex}:{item.ItemId}:{item.IsHighQuality}";

    private static bool ExactTarget(PortfolioEquipmentMoveTarget expected, PortfolioEquipmentMoveTarget observed) =>
        expected.Kind == observed.Kind &&
        string.Equals(expected.TargetKey, observed.TargetKey, StringComparison.Ordinal) &&
        expected.Owner.LocalContentId != 0 &&
        expected.Owner.LocalContentId == observed.Owner.LocalContentId &&
        expected.Owner.HomeWorldId != 0 &&
        expected.Owner.HomeWorldId == observed.Owner.HomeWorldId &&
        Same(expected.Owner.CharacterName, observed.Owner.CharacterName) &&
        expected.Kind switch
        {
            PortfolioEquipmentMoveTargetKind.ActivePlayer =>
                expected.ActiveClassJobId is > 0 && expected.ActiveClassJobId == observed.ActiveClassJobId,
            PortfolioEquipmentMoveTargetKind.SavedGearset =>
                expected.ActiveClassJobId is > 0 && expected.ActiveClassJobId == observed.ActiveClassJobId &&
                expected.GearsetId is >= 0 && expected.GearsetId == observed.GearsetId &&
                Same(expected.GearsetName, observed.GearsetName),
            PortfolioEquipmentMoveTargetKind.Retainer =>
                !string.IsNullOrWhiteSpace(expected.RetainerName) &&
                Same(expected.RetainerName, observed.RetainerName),
            _ => false,
        };

    private static bool HasCoherentTargetIdentity(PortfolioEquipmentMoveTarget target) => target.Kind switch
    {
        PortfolioEquipmentMoveTargetKind.ActivePlayer =>
            string.Equals(target.TargetKey, "active-loadout", StringComparison.Ordinal) &&
            target.ActiveClassJobId is > 0 &&
            string.IsNullOrWhiteSpace(target.RetainerName) && target.GearsetId is null,
        PortfolioEquipmentMoveTargetKind.SavedGearset =>
            target.TargetKey.StartsWith("gearset:", StringComparison.Ordinal) &&
            target.ActiveClassJobId is > 0 && target.GearsetId is >= 0 and < 100 &&
            !string.IsNullOrWhiteSpace(target.GearsetName) && string.IsNullOrWhiteSpace(target.RetainerName),
        PortfolioEquipmentMoveTargetKind.Retainer =>
            target.TargetKey.StartsWith("retainer:", StringComparison.Ordinal) &&
            target.ActiveClassJobId is null &&
            !string.IsNullOrWhiteSpace(target.RetainerName) && target.GearsetId is null,
        _ => false,
    };

    private static int DestinationSlot(EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => 0,
        EquipmentLoadoutPosition.OffHand => 1,
        EquipmentLoadoutPosition.Head => 2,
        EquipmentLoadoutPosition.Body => 3,
        EquipmentLoadoutPosition.Hands => 4,
        EquipmentLoadoutPosition.Legs => 6,
        EquipmentLoadoutPosition.Feet => 7,
        EquipmentLoadoutPosition.Ears => 8,
        EquipmentLoadoutPosition.Neck => 9,
        EquipmentLoadoutPosition.Wrists => 10,
        EquipmentLoadoutPosition.RightRing => 11,
        EquipmentLoadoutPosition.LeftRing => 12,
        _ => -1,
    };

    private static bool ExactItem(
        PortfolioEquipmentMoveOwner owner,
        PortfolioEquipmentSlotAddress address,
        PortfolioExactItem? expected,
        PortfolioEquipmentSlotItem? observed)
    {
        if (expected is null || observed is null)
            return expected is null && observed is null;
        return expected.ItemId == observed.ItemId &&
            expected.IsHighQuality == observed.IsHighQuality &&
            string.Equals(expected.InstanceId, ExactInstanceId(owner, address, observed), StringComparison.Ordinal);
    }

    private static bool IsValidAddress(PortfolioEquipmentSlotAddress address) =>
        !string.IsNullOrWhiteSpace(address.Container) && address.SlotIndex is >= 0 and <= ushort.MaxValue;

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static PortfolioEquipmentMoveResult Refuse(string code, string message) =>
        new(PortfolioEquipmentMoveResultKind.Refused, code, message);

    private static PortfolioEquipmentMoveResult Unavailable(string code, string message) =>
        new(PortfolioEquipmentMoveResultKind.RuntimeUnavailable, code, message);
}
