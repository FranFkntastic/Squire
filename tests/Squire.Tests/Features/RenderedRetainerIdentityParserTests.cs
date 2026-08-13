using System;
using System.Linq;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedRetainerIdentityParserTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 8, 13, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_binds_active_owner_and_exact_rendered_retainer_tuple()
    {
        var result = RenderedRetainerIdentityParser.Parse(
            Snapshot("Venture", "Miner", "Level 100", "Gathering 4,321", "Perception: 3,210"),
            Target(),
            Owner());

        Assert.Equal(RenderedRetainerIdentityStatus.Complete, result.Status);
        Assert.Equal("Fran Example", result.OwnerCharacterName);
        Assert.Equal("Venture", result.RetainerName);
        Assert.Equal(16u, result.ClassJobId);
        Assert.Equal(100u, result.Level);
        Assert.Equal(4321, result.Gathering);
        Assert.Equal(3210, result.Perception);
    }

    [Theory]
    [InlineData("Other Retainer", "Miner", "Level 100")]
    [InlineData("Venture", "Botanist", "Level 100")]
    [InlineData("Venture", "Miner", "Level 99")]
    public void Parse_refuses_missing_or_contradictory_rendered_identity(
        string retainer,
        string job,
        string level)
    {
        var result = RenderedRetainerIdentityParser.Parse(Snapshot(retainer, job, level), Target(), Owner());

        Assert.Equal(RenderedRetainerIdentityStatus.Ambiguous, result.Status);
        Assert.Equal(0u, result.ClassJobId);
        Assert.Contains("cached metadata will not fill it in", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_refuses_a_different_active_owner_even_when_rendered_text_matches()
    {
        var result = RenderedRetainerIdentityParser.Parse(
            Snapshot("Venture", "MIN · Lv. 100"),
            Target(),
            new(2, "Other Owner", "Gilgamesh"));

        Assert.Equal(RenderedRetainerIdentityStatus.Ambiguous, result.Status);
        Assert.Contains("does not exactly own", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_reports_unavailable_when_the_exact_retainer_surface_is_not_visible()
    {
        var result = RenderedRetainerIdentityParser.Parse(
            new(CapturedAt, [new("RetainerCharacter", true, true, false, 0, [])]),
            Target(),
            Owner());

        Assert.Equal(RenderedRetainerIdentityStatus.Unavailable, result.Status);
    }

    internal static OutfitterTarget Target() => new(
        "retainer:42",
        OutfitterTargetKind.Retainer,
        "Venture",
        "MIN · Lv. 100",
        Job: new CharacterJobSnapshot(16, "MIN", "Miner", 100, true, null, "Gatherer",
            EquipmentStatSemantic.Gathering, EquipmentDiscipline.Gatherer),
        RetainerMetadata: new(1, "Fran Example", "Gilgamesh", 42, "Venture", 16, 100),
        OwnerCharacterName: "Fran Example",
        OwnerHomeWorld: "Gilgamesh",
        IsCurrentCharacter: true,
        IsReady: false);

    internal static RetainerObservationOwnerScope Owner() => new(1, "Fran Example", "Gilgamesh");

    internal static AgentBridgeRenderedUiSnapshot Snapshot(params string[] texts) => new(
        CapturedAt,
        [
            new("RetainerCharacter", true, true, true, 100,
                texts.Select((text, index) => new AgentBridgeRenderedTextNode(
                    $"RetainerCharacter/{index + 1}", (uint)(index + 1), 3, text, 0, index * 20, 120, 20)).ToArray()),
        ]);
}
