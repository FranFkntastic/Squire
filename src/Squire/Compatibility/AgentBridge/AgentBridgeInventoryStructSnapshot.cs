using System;
using System.Collections.Generic;

namespace MarketMafioso.AgentBridge;

public sealed record AgentBridgeInventoryStructItem(
    string Container,
    int SlotIndex,
    uint ItemId,
    bool IsHighQuality,
    uint Quantity,
    bool IsEquipped,
    IReadOnlyList<uint> MateriaIds);

/// <summary>
/// Diagnostic projection of the direct container read. This is explicitly NOT
/// ownership authority: per the amended roadmap gate it becomes production
/// evidence only after a differential proof against rendered observation passes.
/// </summary>
public sealed record AgentBridgeInventoryStructSnapshot(
    string CharacterName,
    uint HomeWorldId,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<AgentBridgeInventoryStructItem> Items,
    IReadOnlyList<string> ComponentDiagnostics,
    string AuthorityNotice);
