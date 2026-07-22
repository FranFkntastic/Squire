using System;
using System.Collections.Generic;

namespace MarketMafioso.AgentBridge;

public sealed record AgentBridgeRenderedUiSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<AgentBridgeRenderedAddonSnapshot> Addons);

public sealed record AgentBridgeRenderedAddonSnapshot(
    string Name,
    bool Present,
    bool Ready,
    bool Visible,
    uint NodeCount,
    IReadOnlyList<AgentBridgeRenderedTextNode> TextNodes,
    string? Diagnostic = null,
    IReadOnlyList<AgentBridgeRenderedNodeSnapshot>? Nodes = null);

public sealed record AgentBridgeRenderedNodeSnapshot(
    string NodePath,
    uint NodeId,
    ushort NodeType,
    ushort? ComponentType,
    int Left,
    int Top,
    int Right,
    int Bottom,
    bool RespondsToMouse,
    IReadOnlyList<string>? RegisteredEvents = null);

public sealed record AgentBridgeRenderedTextNode(
    string NodePath,
    uint NodeId,
    ushort NodeType,
    string Text,
    float X,
    float Y,
    ushort Width,
    ushort Height);

public sealed record AgentBridgeUiAutomationCapabilities(
    string EquipmentObservationMode,
    bool MovesOperatingSystemCursor,
    bool ActivatesGameWindow,
    bool RequiresGameForeground,
    bool RequiresVisibleCharacterAddon,
    bool UsesRenderedTooltipAsAuthority,
    bool SupportsDeterministicReplay,
    string Diagnostic);
