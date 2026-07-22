using System;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire.Observation;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedItemDetailParserTests
{
    [Fact]
    public void Parse_reads_rendered_hq_identity_and_gathering_stats()
    {
        var result = RenderedItemDetailParser.Parse(Snapshot(
            Text("ItemDetail/33", "Ceremonial Pickaxe\uE03C"),
            Text("ItemDetail/63", "Item Level 740"),
            Text("ItemDetail/65/2", "MIN"),
            Text("ItemDetail/66/2", "Lv. 100\rOwned: 1"),
            Text("ItemDetail/100/2", "Gathering +1,720"),
            Text("ItemDetail/1000101/2", "Perception +983"),
            Text("ItemDetail/100/4", "GP +9"),
            Text("ItemDetail/96/6", "Gathering +25"),
            Text("ItemDetail/960101/6", "Gathering +25"),
            Text("ItemDetail/960102/6", "GP +9")));

        Assert.Equal(RenderedItemDetailStatus.Complete, result.Status);
        Assert.Equal("Ceremonial Pickaxe", result.Name);
        Assert.Equal(RenderedItemQuality.High, result.Quality);
        Assert.Equal(740, result.ItemLevel);
        Assert.Equal(100, result.EquipLevel);
        Assert.Equal("MIN", result.JobCategory);
        Assert.Equal(1720, result.Stats["Gathering"]);
        Assert.Equal(983, result.Stats["Perception"]);
        Assert.Equal(9, result.Stats["GP"]);
        Assert.Equal(50, result.MateriaStats["Gathering"]);
        Assert.Equal(9, result.MateriaStats["GP"]);
    }

    [Fact]
    public void Parse_does_not_treat_materia_or_stale_schema_text_as_item_stats()
    {
        var result = RenderedItemDetailParser.Parse(Snapshot(
            Text("ItemDetail/33", "Ceremonial Pickaxe"),
            Text("ItemDetail/63", "Item Level 740"),
            Text("ItemDetail/65/2", "MIN"),
            Text("ItemDetail/66/2", "Lv. 100"),
            Text("ItemDetail/100/2", "Gathering +1720"),
            Text("ItemDetail/96/6", "Gathering +25"),
            Text("ItemDetail/42", "Stale unrelated item description")));

        Assert.Equal(RenderedItemDetailStatus.Complete, result.Status);
        Assert.Equal(RenderedItemQuality.Normal, result.Quality);
        Assert.Equal(1720, result.Stats["Gathering"]);
        Assert.Single(result.Stats);
        Assert.Equal(25, result.MateriaStats["Gathering"]);
    }

    [Fact]
    public void Parse_abstains_when_rendered_identity_tuple_is_incomplete()
    {
        var result = RenderedItemDetailParser.Parse(Snapshot(
            Text("ItemDetail/33", "Ceremonial Pickaxe"),
            Text("ItemDetail/63", "Item Level 740")));

        Assert.Equal(RenderedItemDetailStatus.Incomplete, result.Status);
        Assert.Null(result.Name);
    }

    [Fact]
    public void Parse_reports_unavailable_when_tooltip_is_not_visible()
    {
        var snapshot = new AgentBridgeRenderedUiSnapshot(DateTimeOffset.UtcNow,
        [
            new("ItemDetail", true, true, false, 117, []),
        ]);

        Assert.Equal(RenderedItemDetailStatus.Unavailable, RenderedItemDetailParser.Parse(snapshot).Status);
    }

    private static AgentBridgeRenderedTextNode Text(string path, string text) =>
        new(path, uint.TryParse(path[(path.LastIndexOf('/') + 1)..], out var nodeId) ? nodeId : 0, 3, text, 0, 0, 100, 20);

    private static AgentBridgeRenderedUiSnapshot Snapshot(params AgentBridgeRenderedTextNode[] nodes) =>
        new(DateTimeOffset.UtcNow,
        [
            new("ItemDetail", true, true, true, 117, nodes),
        ]);
}
