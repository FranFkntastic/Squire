using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Diagnostics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using DalamudAddonEventType = Dalamud.Game.Addon.Events.AddonEventType;

namespace MarketMafioso.Diagnostics;

public sealed class UiStateCaptureService : IDisposable
{
    private static readonly HashSet<string> SquireAutomationAddons = new(StringComparer.Ordinal)
    {
        "ContextMenu", "SalvageDialog", "MateriaRetrieveDialog", "SelectYesno", "Shop",
        "GrandCompanySupplyList", "GrandCompanySupplyReward", "Inventory", "InventoryLarge",
        "ArmouryBoard", "SelectString", "SelectIconString", "Talk",
    };
    private static readonly InventoryType[] CapturedInventories =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal,
    ];
    private static readonly AddonEvent[] CapturedAddonEvents =
    [
        AddonEvent.PostSetup,
        AddonEvent.PostRefresh,
        AddonEvent.PostReceiveEvent,
        AddonEvent.PostShow,
        AddonEvent.PostHide,
        AddonEvent.PreFinalize,
    ];

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly string directory;
    private readonly UiStateRecorder recorder = new();
    private readonly HashSet<string> observedAddons = new(StringComparer.Ordinal);
    private CaptureScope captureScope = CaptureScope.Catchall;
    private DateTimeOffset nextStateSampleUtc;

    public UiStateCaptureService(IAddonLifecycle addonLifecycle, IFramework framework, ICondition condition, string directory)
    {
        this.addonLifecycle = addonLifecycle;
        this.framework = framework;
        this.condition = condition;
        this.directory = directory;
    }

    public bool IsRecording => recorder.IsRecording;
    public string Status { get; private set; } = "UI-state recorder is idle.";
    public string? LastCapturePath { get; private set; }
    public int EventCount => recorder.Snapshot().Events.Count;

    public void Start(string name = "manual-ui-transaction")
        => StartCore(name, CaptureScope.Catchall);

    public bool StartSquireProbe(string container, int slotIndex)
    {
        if (IsRecording)
            return false;
        if (!Enum.TryParse<InventoryType>(container, out var inventoryType) || slotIndex < 0)
            throw new ArgumentException($"Squire probe inventory target {container}:{slotIndex} is invalid.");
        StartCore($"squire-probe-{container}-{slotIndex}", new CaptureScope(SquireAutomationAddons, inventoryType, slotIndex));
        return true;
    }

    private void StartCore(string name, CaptureScope scope)
    {
        if (IsRecording)
            return;
        captureScope = scope;
        observedAddons.Clear();
        recorder.Start(name, DateTimeOffset.UtcNow);
        foreach (var addonEvent in CapturedAddonEvents)
            addonLifecycle.RegisterListener(addonEvent, OnAddonEvent);
        framework.Update += OnFrameworkUpdate;
        nextStateSampleUtc = DateTimeOffset.MinValue;
        Status = $"Recording {name}. Perform the UI transaction, then finish capture.";
    }

    public string? Stop()
    {
        if (!IsRecording)
            return LastCapturePath;
        Unregister();
        var session = recorder.Stop(DateTimeOffset.UtcNow);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"ui-state-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.jsonl");
        using (var writer = new StreamWriter(path, false))
        {
            writer.WriteLine(JsonSerializer.Serialize(new { type = "manifest", session.SessionId, session.Name, session.StartedAtUtc, session.StoppedAtUtc, session.Truncated }));
            foreach (var value in session.Events)
                writer.WriteLine(JsonSerializer.Serialize(new { type = "event", value.Sequence, value.TimestampUtc, kind = value.Kind.ToString(), value.Source, value.Name, value.Details }));
        }
        LastCapturePath = path;
        Status = $"Captured {session.Events.Count:N0} event(s): {Path.GetFileName(path)}";
        return path;
    }

    public void Mark(string name, IReadOnlyDictionary<string, string?>? details = null) =>
        recorder.Record(DateTimeOffset.UtcNow, UiStateEventKind.Marker, "plugin", name, details);

    private unsafe void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        if (captureScope.AddonNames is { } addonNames && !addonNames.Contains(args.AddonName))
            return;
        observedAddons.Add(args.AddonName);
        if (args is AddonReceiveEventArgs noisy && IsNoisyReceiveEvent(noisy.AtkEventType))
            return;
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["addon"] = args.AddonName,
            ["addonArgsType"] = args.Type.ToString(),
            ["address"] = FormatPointer(args.Addon.Address),
        };
        var kind = UiStateEventKind.AddonLifecycle;
        if (args is AddonReceiveEventArgs received)
        {
            kind = UiStateEventKind.AddonReceiveEvent;
            details["atkEventType"] = received.AtkEventType.ToString();
            details["eventParam"] = received.EventParam.ToString(CultureInfo.InvariantCulture);
            details["atkEvent"] = FormatPointer(received.AtkEvent);
            details["atkEventData"] = FormatPointer(received.AtkEventData);
        }
        else if (args is AddonRefreshArgs refreshed)
        {
            details["atkValueCount"] = refreshed.AtkValueCount.ToString(CultureInfo.InvariantCulture);
            details["atkValues"] = DescribeAtkValues((AtkValue*)refreshed.AtkValues, refreshed.AtkValueCount);
        }
        recorder.Record(DateTimeOffset.UtcNow, kind, "dalamud-addon-lifecycle", type.ToString(), details);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextStateSampleUtc)
            return;
        nextStateSampleUtc = now.AddMilliseconds(100);
        recorder.RecordStateChange(now, "framework", CaptureState());
    }

    private unsafe IReadOnlyDictionary<string, string?> CaptureState()
    {
        var state = new Dictionary<string, string?>(StringComparer.Ordinal);
        var addonsToCapture = captureScope.AddonNames ?? observedAddons;
        foreach (var addonName in addonsToCapture.Order(StringComparer.Ordinal))
        {
            var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            state[$"addon.{addonName}"] = addon == null
                ? "missing"
                : $"id={addon->Id},ready={addon->IsReady},visible={addon->IsVisible},focus={FormatPointer(addon->FocusNode)}";
        }

        var activeConditions = Enum.GetValues<ConditionFlag>().Where(flag => condition[flag]).Select(flag => flag.ToString());
        state["conditions.active"] = string.Join(",", activeConditions);
        state["client.loggedIn"] = Plugin.ClientState.IsLoggedIn.ToString();
        state["client.territory"] = Plugin.ClientState.TerritoryType.ToString(CultureInfo.InvariantCulture);
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        state["player.local"] = localPlayer is null
            ? "missing"
            : $"object={localPlayer.GameObjectId},position={FormatVector(localPlayer.Position)},rotation={localPlayer.Rotation.ToString("R", CultureInfo.InvariantCulture)}";
        var target = Plugin.TargetManager.Target;
        state["target.current"] = target is null
            ? "none"
            : $"object={target.GameObjectId},kind={target.ObjectKind},position={FormatVector(target.Position)}";
        var actionManager = ActionManager.Instance();
        state["animationLock"] = actionManager == null ? "unavailable" : actionManager->AnimationLock.ToString("R", CultureInfo.InvariantCulture);

        var stage = AtkStage.Instance();
        state["focus.stage"] = stage == null ? "unavailable" : FormatPointer(stage->GetFocus());
        var agentModule = AgentModule.Instance();
        if (agentModule != null)
        {
            var activeAgents = new List<string>();
            foreach (var id in Enum.GetValues<AgentId>())
            {
                var agent = agentModule->GetAgentByInternalId(id);
                if (agent != null && agent->IsAgentActive())
                    activeAgents.Add($"{id}:{agent->GetAddonId()}");
            }
            state["agents.active"] = string.Join(",", activeAgents);
        }
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager != null)
        {
            if (captureScope.InventoryType is { } focusedInventory && captureScope.SlotIndex is { } focusedSlot)
                CaptureInventory(state, inventoryManager, focusedInventory, focusedSlot);
            else
            {
                foreach (var inventoryType in CapturedInventories)
                    CaptureInventory(state, inventoryManager, inventoryType, null);
            }
        }
        return state;
    }

    private static unsafe void CaptureInventory(
        IDictionary<string, string?> state,
        InventoryManager* inventoryManager,
        InventoryType inventoryType,
        int? focusedSlot)
    {
        var container = inventoryManager->GetInventoryContainer(inventoryType);
        if (container == null || !container->IsLoaded)
        {
            state[$"inventory.{inventoryType}"] = "unavailable";
            return;
        }
        state[$"inventory.{inventoryType}"] = $"loaded,size={container->Size}";
        var start = focusedSlot ?? 0;
        var end = focusedSlot is null ? container->Size : Math.Min(container->Size, focusedSlot.Value + 1);
        if (start >= container->Size)
        {
            state[$"inventory.{inventoryType}.{start}"] = "out-of-range";
            return;
        }
        for (var slotIndex = start; slotIndex < end; slotIndex++)
        {
            var item = container->GetInventorySlot(slotIndex);
            state[$"inventory.{inventoryType}.{slotIndex}"] = item == null || item->ItemId == 0
                ? "empty"
                : $"item={item->ItemId},quantity={item->Quantity},flags={(uint)item->Flags},condition={item->Condition},spiritbond={item->SpiritbondOrCollectability}";
        }
    }

    internal static bool IsNoisyReceiveEvent(DalamudAddonEventType eventType) => eventType is
        DalamudAddonEventType.MouseMove or
        DalamudAddonEventType.MouseOver or
        DalamudAddonEventType.MouseOut or
        DalamudAddonEventType.TimerTick or
        DalamudAddonEventType.TimelineActiveLabelChanged or
        DalamudAddonEventType.DragDropRollOver;

    private static unsafe string DescribeAtkValues(AtkValue* values, uint count)
    {
        if (values == null || count == 0)
            return string.Empty;
        var result = new List<string>();
        for (var index = 0u; index < Math.Min(count, 64u); index++)
        {
            var value = values[index];
            result.Add(value.Type switch
            {
                AtkValueType.Int => $"{index}:Int:{value.Int}",
                AtkValueType.UInt => $"{index}:UInt:{value.UInt}",
                AtkValueType.Bool => $"{index}:Bool:{value.Byte != 0}",
                AtkValueType.Float => $"{index}:Float:{value.Float.ToString("R", CultureInfo.InvariantCulture)}",
                AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString => $"{index}:{value.Type}:redacted-length={value.GetValueAsString().Length}",
                _ => $"{index}:{value.Type}",
            });
        }
        return string.Join("|", result);
    }

    private void Unregister()
    {
        framework.Update -= OnFrameworkUpdate;
        foreach (var addonEvent in CapturedAddonEvents)
            addonLifecycle.UnregisterListener(addonEvent, OnAddonEvent);
    }

    public void Dispose()
    {
        if (IsRecording)
            Stop();
        else
            Unregister();
    }

    private static string FormatPointer(nint value) => value == 0 ? "null" : $"0x{value:X}";
    private static unsafe string FormatPointer(void* value) => value == null ? "null" : $"0x{(nuint)value:X}";
    private static string FormatVector(System.Numerics.Vector3 value) =>
        $"{value.X.ToString("R", CultureInfo.InvariantCulture)},{value.Y.ToString("R", CultureInfo.InvariantCulture)},{value.Z.ToString("R", CultureInfo.InvariantCulture)}";

    private sealed record CaptureScope(IReadOnlySet<string>? AddonNames, InventoryType? InventoryType, int? SlotIndex)
    {
        public static CaptureScope Catchall { get; } = new(null, null, null);
    }
}
