using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire.Observation;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedCharacterEquipmentLayoutParserTests
{
    [Fact]
    public void Parse_maps_rendered_grid_and_excludes_facewear()
    {
        var result = RenderedCharacterEquipmentLayoutParser.Parse(Snapshot(CharacterNodes()));

        Assert.Equal(RenderedEquipmentLayoutStatus.Complete, result.Status);
        Assert.Equal(13, result.Slots.Count);
        Assert.Equal("Character/49/5", Assert.Single(result.Slots, value => value.Slot == EquipmentSlot.MainHand).NodePath);
        Assert.Equal("Character/50/5", Assert.Single(result.Slots, value => value.Slot == EquipmentSlot.OffHand).NodePath);
        Assert.DoesNotContain(result.Slots, value => value.NodePath == "Character/62/5");
        Assert.Equal(2, result.Slots.Count(value => value.Slot == EquipmentSlot.Ring));
        Assert.Equal("Character/61/5", Assert.Single(result.Slots, value => value.Slot == EquipmentSlot.SoulCrystal).NodePath);
    }

    [Fact]
    public void Parse_uses_geometry_and_not_item_identity_values()
    {
        var first = RenderedCharacterEquipmentLayoutParser.Parse(Snapshot(CharacterNodes()));
        var second = RenderedCharacterEquipmentLayoutParser.Parse(Snapshot(CharacterNodes(offsetX: 20)));

        Assert.Equal(
            first.Slots.Select(value => (value.Slot, value.NodePath)),
            second.Slots.Select(value => (value.Slot, value.NodePath)));
    }

    [Fact]
    public void Parse_accepts_current_layout_when_optional_facewear_control_is_absent()
    {
        var nodes = CharacterNodes().Where(value => value.NodePath != "Character/62/5").ToArray();

        var result = RenderedCharacterEquipmentLayoutParser.Parse(Snapshot(nodes));

        Assert.Equal(RenderedEquipmentLayoutStatus.Complete, result.Status);
        Assert.Equal(13, result.Slots.Count);
        Assert.DoesNotContain(result.Slots, value => value.NodePath == "Character/62/5");
    }

    [Fact]
    public void Parse_maps_the_same_rendered_topology_for_retainer_character()
    {
        var nodes = CharacterNodes("RetainerCharacter");

        var result = RenderedCharacterEquipmentLayoutParser.Parse(
            Snapshot(nodes, "RetainerCharacter"),
            "RetainerCharacter");

        Assert.Equal(RenderedEquipmentLayoutStatus.Complete, result.Status);
        Assert.Equal("RetainerCharacter/49/5", Assert.Single(result.Slots, value => value.Slot == EquipmentSlot.MainHand).NodePath);
        Assert.Contains("RetainerCharacter", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_abstains_when_column_topology_is_incomplete()
    {
        var nodes = CharacterNodes().Where(value => value.NodePath != "Character/57/5").ToArray();
        var result = RenderedCharacterEquipmentLayoutParser.Parse(Snapshot(nodes));

        Assert.Equal(RenderedEquipmentLayoutStatus.Ambiguous, result.Status);
        Assert.Empty(result.Slots);
    }

    private static AgentBridgeRenderedUiSnapshot Snapshot(
        IReadOnlyList<AgentBridgeRenderedNodeSnapshot> nodes,
        string addonName = "Character") =>
        new(DateTimeOffset.UtcNow,
        [
            new(addonName, true, true, true, 200, [], Nodes: nodes),
        ]);

    private static IReadOnlyList<AgentBridgeRenderedNodeSnapshot> CharacterNodes(
        string addonName = "Character",
        int offsetX = 0)
    {
        var result = new List<AgentBridgeRenderedNodeSnapshot>();
        var leftIds = new[] { 49u, 51u, 52u, 54u, 53u, 55u, 62u };
        var rightIds = new[] { 50u, 56u, 57u, 58u, 59u, 60u, 61u };
        for (var index = 0; index < leftIds.Length; index++)
            result.Add(Node(addonName, leftIds[index], 1369 + offsetX, index == 0 ? 157 : 218 + ((index - 1) * 47)));
        for (var index = 0; index < rightIds.Length; index++)
            result.Add(Node(addonName, rightIds[index], 1631 + offsetX, 218 + (index * 47)));
        result.Add(new($"{addonName}/999/5", 5, 1007, 17, 1100, 100, 1120, 120, true));
        return result;
    }

    private static AgentBridgeRenderedNodeSnapshot Node(string addonName, uint parentId, int left, int top) =>
        new($"{addonName}/{parentId}/5", 5, 1007, 17, left, top, left + 44, top + 44, true);
}
