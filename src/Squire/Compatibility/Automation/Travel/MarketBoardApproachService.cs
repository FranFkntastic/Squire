using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MarketMafioso.Automation.Travel;

public sealed class MarketBoardApproachService
{
    private const string ItemSearchAddon = "ItemSearch";
    private const string ItemSearchResultAddon = "ItemSearchResult";
    private const string MarketBoardObjectName = "Market Board";
    private static readonly TimeSpan DirectInteractionCooldown = TimeSpan.FromMilliseconds(750);

    public const float DirectInteractionDistance = 4.25f;
    public const float MaximumApproachDistance = 80f;
    public const float NavigationStopDistance = 3.5f;

    private readonly IGameGui gameGui;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly VNavmeshIpc vnavmesh;
    private readonly IPluginLog log;
    private DateTimeOffset? lastDirectInteractionUtc;

    public MarketBoardApproachService(
        IGameGui gameGui,
        IObjectTable objectTable,
        ITargetManager targetManager,
        VNavmeshIpc vnavmesh,
        IPluginLog log)
    {
        this.gameGui = gameGui;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.vnavmesh = vnavmesh;
        this.log = log;
    }

    public unsafe MarketBoardApproachResult OpenOrApproach()
    {
        if (IsMarketBoardUiOpen())
        {
            lastDirectInteractionUtc = null;
            return MarketBoardApproachResult.Ready("Market board UI is already open.");
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        var board = FindMarketBoard(playerPosition);
        var vnavmeshRunning = vnavmesh.IsRunning;
        var decision = Decide(
            marketBoardUiOpen: false,
            boardDistance: board == null || playerPosition == null
                ? null
                : CalculateHorizontalDistance(playerPosition.Value, board.Position),
            vnavmeshAvailable: vnavmesh.IsReady,
            vnavmeshRunning: vnavmeshRunning);

        return decision.Kind switch
        {
            MarketBoardApproachDecisionKind.InteractDirectly => InteractWithBoard(board, playerPosition),
            MarketBoardApproachDecisionKind.StartNavigation => StartNavigation(board, playerPosition),
            MarketBoardApproachDecisionKind.RequestMarketBoardTravel => MarketBoardApproachResult.MarketBoardTravel(
                DescribeMarketBoardTravelRequest(board, playerPosition)),
            MarketBoardApproachDecisionKind.WaitForMovement => MarketBoardApproachResult.Wait(
                "vnavmesh is moving toward the nearby market board."),
            MarketBoardApproachDecisionKind.ReadyToSearch => MarketBoardApproachResult.Ready(
                "Market board UI is already open."),
            _ => MarketBoardApproachResult.Wait(DescribeManualWait(board, playerPosition, vnavmeshRunning)),
        };
    }

    public VNavmeshStopResult StopNavigation()
    {
        var result = vnavmesh.Stop();
        lastDirectInteractionUtc = null;
        return result;
    }

    internal static MarketBoardApproachDecision Decide(
        bool marketBoardUiOpen,
        float? boardDistance,
        bool vnavmeshAvailable,
        bool vnavmeshRunning)
    {
        if (marketBoardUiOpen)
            return new(MarketBoardApproachDecisionKind.ReadyToSearch);

        if (boardDistance == null || boardDistance > MaximumApproachDistance)
            return new(MarketBoardApproachDecisionKind.RequestMarketBoardTravel);

        if (boardDistance <= DirectInteractionDistance)
            return new(MarketBoardApproachDecisionKind.InteractDirectly);

        if (vnavmeshRunning)
            return new(MarketBoardApproachDecisionKind.WaitForMovement);

        return vnavmeshAvailable
            ? new(MarketBoardApproachDecisionKind.StartNavigation)
            : new(MarketBoardApproachDecisionKind.RequestMarketBoardTravel);
    }

    private unsafe bool IsMarketBoardUiOpen()
    {
        return IsAddonReady(gameGui.GetAddonByName<AtkUnitBase>(ItemSearchAddon, 1)) ||
               IsAddonReady(gameGui.GetAddonByName<AtkUnitBase>(ItemSearchResultAddon, 1));
    }

    private IGameObject? FindMarketBoard(Vector3? playerPosition)
    {
        if (IsMarketBoardObject(targetManager.Target))
            return targetManager.Target;

        var candidates = objectTable.Where(IsMarketBoardObject);
        if (playerPosition == null)
            return candidates.FirstOrDefault();

        return candidates
            .OrderBy(x => CalculateHorizontalDistance(playerPosition.Value, x.Position))
            .FirstOrDefault();
    }

    private static bool IsMarketBoardObject(IGameObject? gameObject)
    {
        if (gameObject == null || !gameObject.IsTargetable)
            return false;

        if (gameObject.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject or ObjectKind.ReactionEventObject))
            return false;

        return gameObject.Name.TextValue.Equals(MarketBoardObjectName, StringComparison.OrdinalIgnoreCase);
    }

    private unsafe MarketBoardApproachResult InteractWithBoard(IGameObject? board, Vector3? playerPosition)
    {
        if (board == null)
            return MarketBoardApproachResult.Wait("No nearby market board target was found.");

        if (lastDirectInteractionUtc is { } lastInteraction &&
            DateTimeOffset.UtcNow - lastInteraction < DirectInteractionCooldown)
        {
            return MarketBoardApproachResult.Wait("Waiting for nearby market board UI to open.");
        }

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return MarketBoardApproachResult.Wait("Target system is unavailable; open the market board manually.");

        targetManager.Target = board;
        // Market boards are large event objects whose collision geometry can reject a valid
        // interaction as "Cannot see target" even when the player is already in range.
        // Distance and targetability are checked above, so do not ask the client for a second,
        // geometry-sensitive line-of-sight check here.
        var result = targetSystem->InteractWithObject((ClientGameObject*)board.Address, false);
        lastDirectInteractionUtc = DateTimeOffset.UtcNow;
        log.Verbose($"[MarketMafioso] Attempted market board interaction {board.Name.TextValue} ({board.GameObjectId:X}).");
        float? distance = playerPosition == null
            ? null
            : CalculateHorizontalDistance(playerPosition.Value, board.Position);
        return MarketBoardApproachResult.Action(MarketBoardApproachActionKind.DirectInteraction,
            distance == null
                ? "Attempted to open nearby market board."
                : $"Attempted to open nearby market board ({distance.Value:0.0}y).",
            new Dictionary<string, string?>
            {
                ["name"] = board.Name.TextValue,
                ["objectKind"] = board.ObjectKind.ToString(),
                ["gameObjectId"] = board.GameObjectId.ToString("X"),
                ["baseId"] = board.BaseId.ToString(),
                ["distance"] = distance?.ToString("0.00") ?? "unavailable",
                ["result"] = result.ToString(),
            });
    }

    private MarketBoardApproachResult StartNavigation(IGameObject? board, Vector3? playerPosition)
    {
        if (board == null)
            return MarketBoardApproachResult.Wait("No nearby market board target was found.");

        var result = vnavmesh.MoveCloseTo(board.Position, NavigationStopDistance);
        if (!result.Success)
            return MarketBoardApproachResult.Wait(result.Message);

        float? distance = playerPosition == null
            ? null
            : CalculateHorizontalDistance(playerPosition.Value, board.Position);
        return MarketBoardApproachResult.Action(MarketBoardApproachActionKind.NavigationStarted,
            distance == null
                ? "vnavmesh is approaching nearby market board."
                : $"vnavmesh is approaching nearby market board ({distance.Value:0.0}y).",
            new Dictionary<string, string?>
            {
                ["name"] = board.Name.TextValue,
                ["gameObjectId"] = board.GameObjectId.ToString("X"),
                ["distance"] = distance?.ToString("0.00") ?? "unavailable",
                ["destination"] = board.Position.ToString(),
            });
    }

    private string DescribeManualWait(IGameObject? board, Vector3? playerPosition, bool vnavmeshRunning)
    {
        if (vnavmeshRunning)
            return "vnavmesh is moving toward the market board.";

        if (board == null)
            return "Open a market board manually; no nearby market board target was found.";

        if (playerPosition == null)
            return "Open a market board manually; player position is unavailable.";

        var distance = CalculateHorizontalDistance(playerPosition.Value, board.Position);
        if (distance > MaximumApproachDistance)
            return $"Open a market board manually; nearest board is {distance:0.0}y away.";

        return "Open a market board manually; vnavmesh is unavailable for approach movement.";
    }

    private string DescribeMarketBoardTravelRequest(IGameObject? board, Vector3? playerPosition)
    {
        if (board == null)
            return "No nearby market board target was found; requesting Lifestream market board travel.";

        if (playerPosition == null)
            return "Player position is unavailable; requesting Lifestream market board travel.";

        var distance = CalculateHorizontalDistance(playerPosition.Value, board.Position);
        if (distance > MaximumApproachDistance)
            return $"Nearest market board is {distance:0.0}y away; requesting Lifestream market board travel.";

        return $"Market board is {distance:0.0}y away and vnavmesh is unavailable; requesting Lifestream market board travel.";
    }

    private static unsafe bool IsAddonReady(AtkUnitBase* addon)
    {
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    internal static float CalculateHorizontalDistance(Vector3 playerPosition, Vector3 objectPosition)
    {
        return MathF.Sqrt(
            MathF.Pow(playerPosition.X - objectPosition.X, 2) +
            MathF.Pow(playerPosition.Z - objectPosition.Z, 2));
    }
}

public enum MarketBoardApproachDecisionKind
{
    ReadyToSearch,
    InteractDirectly,
    StartNavigation,
    RequestMarketBoardTravel,
    WaitForMovement,
    WaitForManualOpen,
}

public sealed record MarketBoardApproachDecision(MarketBoardApproachDecisionKind Kind);

public enum MarketBoardApproachActionKind
{
    None,
    DirectInteraction,
    NavigationStarted,
}

public sealed record MarketBoardApproachResult
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public MarketBoardApproachActionKind ActionKind { get; init; }
    public bool ReadyToSearch => string.Equals(Status, "ReadyToSearch", StringComparison.OrdinalIgnoreCase);
    public bool ActionTaken => string.Equals(Status, "ActionTaken", StringComparison.OrdinalIgnoreCase);
    public bool MarketBoardTravelNeeded => string.Equals(Status, "MarketBoardTravelNeeded", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string?> Details { get; init; } = new Dictionary<string, string?>();

    public static MarketBoardApproachResult Ready(string message)
    {
        return new() { Status = "ReadyToSearch", Message = message };
    }

    public static MarketBoardApproachResult Action(MarketBoardApproachActionKind actionKind, string message, IReadOnlyDictionary<string, string?>? details = null)
    {
        return new()
        {
            Status = "ActionTaken",
            Message = message,
            ActionKind = actionKind,
            Details = details ?? new Dictionary<string, string?>(),
        };
    }

    public static MarketBoardApproachResult MarketBoardTravel(string message)
    {
        return new() { Status = "MarketBoardTravelNeeded", Message = message };
    }

    public static MarketBoardApproachResult Wait(string message)
    {
        return new() { Status = "Waiting", Message = message };
    }
}
