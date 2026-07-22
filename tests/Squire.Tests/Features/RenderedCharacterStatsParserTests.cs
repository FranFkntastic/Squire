using MarketMafioso.AgentBridge;
using MarketMafioso.Squire.Observation;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedCharacterStatsParserTests
{
    [Fact]
    public void Parse_PairsVisibleGatheringLabelsWithValuesInTheirRenderedComponentRows()
    {
        var capturedAt = DateTimeOffset.Parse("2026-07-16T09:39:25Z");
        var snapshot = new AgentBridgeRenderedUiSnapshot(capturedAt,
        [
            Addon("Character",
                Node("Character/65", "Miner", 0, 0),
                Node("Character/64", "Level 85", 0, 20)),
            Addon("CharacterStatus",
                Node("CharacterStatus/71/2", "Gathering", 0, 0),
                Node("CharacterStatus/71/3", "2577", 118, 0),
                Node("CharacterStatus/71/2", "excl. Consumables", 0, 20),
                Node("CharacterStatus/71/3", "2577", 118, 20),
                Node("CharacterStatus/72/2", "Perception", 0, 0),
                Node("CharacterStatus/72/3", "2,577", 118, 0),
                Node("CharacterStatus/72/2", "GP", 0, 40),
                Node("CharacterStatus/72/3", "764", 118, 40)),
        ]);

        var result = RenderedCharacterStatsParser.Parse(snapshot);

        Assert.Equal(RenderedCharacterObservationStatus.Complete, result.Status);
        Assert.Equal("Miner", result.JobName);
        Assert.Equal(85, result.Level);
        Assert.Equal(2577, result.Gathering);
        Assert.Equal(2577, result.Perception);
        Assert.Equal(764, result.GatheringPoints);
        Assert.Equal(3, result.Evidence.Count);
        Assert.Equal([0f, 0f, 40f], result.Evidence.Select(item => item.RowY));
        Assert.Equal(capturedAt, result.CapturedAtUtc);
    }

    [Fact]
    public void Parse_ReportsPartialInsteadOfInventingAMissingRenderedValue()
    {
        var snapshot = new AgentBridgeRenderedUiSnapshot(DateTimeOffset.UtcNow,
        [
            Addon("Character",
                Node("Character/65", "Botanist", 0, 0),
                Node("Character/64", "Level 100", 0, 20)),
            Addon("CharacterStatus",
                Node("CharacterStatus/71/2", "Gathering", 0, 0),
                Node("CharacterStatus/71/3", "4,800", 118, 0),
                Node("CharacterStatus/72/2", "Perception", 0, 20),
                Node("CharacterStatus/72/3", "4,600", 118, 20),
                Node("CharacterStatus/72/2", "GP", 0, 40)),
        ]);

        var result = RenderedCharacterStatsParser.Parse(snapshot);

        Assert.Equal(RenderedCharacterObservationStatus.Partial, result.Status);
        Assert.Equal(4800, result.Gathering);
        Assert.Equal(4600, result.Perception);
        Assert.Null(result.GatheringPoints);
        Assert.Contains("could not be paired", result.Diagnostic);
    }

    [Fact]
    public void Parse_AbstainsWhenActiveJobIsOutsideSupportedGatheringScope()
    {
        var snapshot = new AgentBridgeRenderedUiSnapshot(DateTimeOffset.UtcNow,
        [
            Addon("Character", Node("Character/65", "Blacksmith", 0, 0)),
        ]);

        var result = RenderedCharacterStatsParser.Parse(snapshot);

        Assert.Equal(RenderedCharacterObservationStatus.Unavailable, result.Status);
        Assert.Contains("not Miner or Botanist", result.Diagnostic);
    }

    [Fact]
    public void Stabilizer_RequiresACompleteRenderedTupleToRemainUnchanged()
    {
        var stabilizer = new RenderedGatheringStatsStabilizer(TimeSpan.FromSeconds(3));
        var first = CompleteObservation(DateTimeOffset.Parse("2026-07-16T09:00:00Z"), 2577, 2577, 764);
        var earlyRepeat = first with { CapturedAtUtc = first.CapturedAtUtc.AddSeconds(2.9) };
        var stableRepeat = first with { CapturedAtUtc = first.CapturedAtUtc.AddSeconds(3.1) };

        Assert.Equal(RenderedCharacterObservationStatus.Partial, stabilizer.Observe(first).Status);
        Assert.Equal(RenderedCharacterObservationStatus.Partial, stabilizer.Observe(earlyRepeat).Status);
        Assert.Equal(RenderedCharacterObservationStatus.Complete, stabilizer.Observe(stableRepeat).Status);
    }

    [Fact]
    public void Stabilizer_RestartsItsWindowWhenRenderedAttributesChange()
    {
        var stabilizer = new RenderedGatheringStatsStabilizer(TimeSpan.FromSeconds(3));
        var first = CompleteObservation(DateTimeOffset.Parse("2026-07-16T09:00:00Z"), 958, 963, 764);
        var corrected = CompleteObservation(first.CapturedAtUtc.AddSeconds(2), 2577, 2577, 764);

        Assert.Equal(RenderedCharacterObservationStatus.Partial, stabilizer.Observe(first).Status);
        Assert.Equal(RenderedCharacterObservationStatus.Partial, stabilizer.Observe(corrected).Status);
        Assert.Equal(
            RenderedCharacterObservationStatus.Complete,
            stabilizer.Observe(corrected with { CapturedAtUtc = corrected.CapturedAtUtc.AddSeconds(3) }).Status);
    }

    private static RenderedGatheringStatsObservation CompleteObservation(
        DateTimeOffset capturedAt,
        int gathering,
        int perception,
        int gp) =>
        new(
            Guid.NewGuid(),
            capturedAt,
            RenderedCharacterObservationStatus.Complete,
            "Miner",
            85,
            gathering,
            perception,
            gp,
            [],
            "Complete rendered observation.");

    private static AgentBridgeRenderedAddonSnapshot Addon(string name, params AgentBridgeRenderedTextNode[] nodes) =>
        new(name, true, true, true, 100, nodes);

    private static AgentBridgeRenderedTextNode Node(string path, string text, float x, float y) =>
        new(path, 1, 3, text, x, y, 100, 20);
}
