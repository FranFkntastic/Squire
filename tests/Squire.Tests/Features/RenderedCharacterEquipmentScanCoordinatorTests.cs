using System;
using System.Collections.Generic;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire.Observation;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedCharacterEquipmentScanCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 16, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Begin_exposes_progress_and_requires_the_exact_rendered_target()
    {
        var coordinator = Coordinator();
        var begun = coordinator.Begin(Snapshot());

        Assert.Equal(RenderedEquipmentScanStatus.ReadyToHover, begun.Status);
        Assert.Equal(12, begun.TotalSlots);
        Assert.Equal("main-hand", begun.CurrentTarget?.PositionKey);
        Assert.Equal(RenderedEquipmentScanStatus.Failed, coordinator.MarkHoverStarted("Character/999/5", Start).Status);
    }

    [Fact]
    public void Observe_requires_a_stable_rendered_item_tuple_before_advancing()
    {
        var coordinator = Coordinator();
        var begun = coordinator.Begin(Snapshot());
        coordinator.MarkHoverStarted(begun.CurrentTarget!.NodePath, Start);

        Assert.Equal(RenderedEquipmentScanStatus.Observing, coordinator.Observe(Snapshot(ItemTexts("Hatchet\uE03C")), Start.AddMilliseconds(350)).Status);
        var advanced = coordinator.Observe(Snapshot(ItemTexts("Hatchet\uE03C")), Start.AddMilliseconds(700));

        Assert.Equal(RenderedEquipmentScanStatus.ReadyToHover, advanced.Status);
        Assert.Equal(1, advanced.CompletedSlots);
        Assert.Equal("Hatchet", Assert.Single(advanced.Observations).Item?.Name);
        Assert.Equal(RenderedItemQuality.High, advanced.Observations[0].Item?.Quality);
        Assert.Equal("head", advanced.CurrentTarget?.PositionKey);
    }

    [Fact]
    public void Observe_rejects_a_tooltip_category_that_contradicts_the_scanned_position()
    {
        var coordinator = Coordinator();
        var begun = coordinator.Begin(Snapshot());
        coordinator.MarkHoverStarted(begun.CurrentTarget!.NodePath, Start);

        var failed = coordinator.Observe(Snapshot(ItemTexts("Hatchet", "Feet")), Start.AddMilliseconds(350));

        Assert.Equal(RenderedEquipmentScanStatus.Failed, failed.Status);
        Assert.Contains("inconsistent with the rendered main-hand slot", failed.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(failed.Observations);
    }

    [Fact]
    public void Observe_rejects_an_armor_position_when_the_tooltip_category_names_another_slot()
    {
        var coordinator = Coordinator();
        var begun = coordinator.Begin(Snapshot());
        coordinator.MarkHoverStarted(begun.CurrentTarget!.NodePath, Start);
        coordinator.Observe(Snapshot(ItemTexts("Hatchet")), Start.AddMilliseconds(350));
        var advanced = coordinator.Observe(Snapshot(ItemTexts("Hatchet")), Start.AddMilliseconds(700));
        Assert.Equal("head", advanced.CurrentTarget?.PositionKey);
        coordinator.MarkHoverStarted(advanced.CurrentTarget!.NodePath, Start.AddMilliseconds(700));

        var failed = coordinator.Observe(Snapshot(ItemTexts("Leather Jacket", "Body")), Start.AddMilliseconds(1050));

        Assert.Equal(RenderedEquipmentScanStatus.Failed, failed.Status);
        Assert.Contains("inconsistent with the rendered head slot", failed.Diagnostic, StringComparison.Ordinal);
        Assert.Single(failed.Observations);
    }

    [Fact]
    public void Observe_rejects_a_tooltip_with_no_rendered_category()
    {
        var coordinator = Coordinator();
        var begun = coordinator.Begin(Snapshot());
        coordinator.MarkHoverStarted(begun.CurrentTarget!.NodePath, Start);

        var texts = ItemTexts("Hatchet").Where(node => node.NodePath != "ItemDetail/35").ToArray();
        var failed = coordinator.Observe(Snapshot(texts), Start.AddMilliseconds(350));

        Assert.Equal(RenderedEquipmentScanStatus.Failed, failed.Status);
        Assert.Contains("inconsistent with the rendered main-hand slot", failed.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_does_not_infer_empty_from_a_missing_tooltip()
    {
        var coordinator = Coordinator();
        var begun = coordinator.Begin(Snapshot());
        coordinator.MarkHoverStarted(begun.CurrentTarget!.NodePath, Start);

        coordinator.Observe(Snapshot(), Start.AddMilliseconds(350));
        var failed = coordinator.Observe(Snapshot(), Start.AddMilliseconds(1100));

        Assert.Equal(RenderedEquipmentScanStatus.Failed, failed.Status);
        Assert.Empty(failed.Observations);
        Assert.Contains("will not infer an empty slot", failed.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_fails_if_character_layout_changes_mid_scan()
    {
        var coordinator = Coordinator();
        var begun = coordinator.Begin(Snapshot());
        coordinator.MarkHoverStarted(begun.CurrentTarget!.NodePath, Start);
        var broken = Snapshot(characterNodes: CharacterNodes()[1..]);

        Assert.Equal(RenderedEquipmentScanStatus.Failed, coordinator.Observe(broken, Start.AddMilliseconds(350)).Status);
    }

    private static RenderedCharacterEquipmentScanCoordinator Coordinator() =>
        new(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1));

    private static AgentBridgeRenderedTextNode[] ItemTexts(string name, string slotCategory = "Botanist's Primary Tool") =>
    [
        Text("ItemDetail/33", name),
        Text("ItemDetail/35", slotCategory),
        Text("ItemDetail/63", "Item Level 750"),
        Text("ItemDetail/65/2", "MIN BTN"),
        Text("ItemDetail/66/2", "Lv. 100"),
        Text("ItemDetail/100/2", "Gathering +1720"),
    ];

    private static AgentBridgeRenderedUiSnapshot Snapshot(
        AgentBridgeRenderedTextNode[]? itemTexts = null,
        IReadOnlyList<AgentBridgeRenderedNodeSnapshot>? characterNodes = null) =>
        new(Start,
        [
            new("Character", true, true, true, 200, [], Nodes: characterNodes ?? CharacterNodes()),
            new("ItemDetail", true, true, itemTexts is { Length: > 0 }, 117, itemTexts ?? []),
        ]);

    private static AgentBridgeRenderedTextNode Text(string path, string text) =>
        new(path, 0, 3, text, 0, 0, 100, 20);

    private static AgentBridgeRenderedNodeSnapshot[] CharacterNodes()
    {
        var result = new List<AgentBridgeRenderedNodeSnapshot>();
        var leftIds = new[] { 49u, 51u, 52u, 54u, 53u, 55u, 62u };
        var rightIds = new[] { 50u, 56u, 57u, 58u, 59u, 60u, 61u };
        for (var index = 0; index < leftIds.Length; index++)
            result.Add(Node(leftIds[index], 1369, index == 0 ? 157 : 218 + ((index - 1) * 47)));
        for (var index = 0; index < rightIds.Length; index++)
            result.Add(Node(rightIds[index], 1631, 218 + (index * 47)));
        return result.ToArray();
    }

    private static AgentBridgeRenderedNodeSnapshot Node(uint parentId, int left, int top) =>
        new($"Character/{parentId}/5", 5, 1007, 17, left, top, left + 44, top + 44, true);
}
