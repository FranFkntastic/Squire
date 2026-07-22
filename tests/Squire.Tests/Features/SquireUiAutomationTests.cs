using MarketMafioso.Squire.Observation;
using Franthropy.Dalamud.Automation.Inventory;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireUiAutomationTests
{
    [Fact]
    public void FindDesynthesizeEntry_FindsExactEntryCaseInsensitively()
    {
        var option = new DalamudContextMenuOptionSpec("Desynthesis", new HashSet<string> { "Desynthesis", "Desynthesize" });
        Assert.Equal(1, DalamudContextMenuOptionParser.Find(["Try On", "DESYNTHESIZE", "Discard"], option).Index);
        Assert.Equal(1, DalamudContextMenuOptionParser.Find(["Try On", "DESYNTHESIS", "Discard"], option).Index);
    }

    [Fact]
    public void FindDesynthesizeEntry_DoesNotAcceptSimilarDestructiveLabels()
    {
        var option = new DalamudContextMenuOptionSpec("Desynthesis", new HashSet<string> { "Desynthesis", "Desynthesize" });
        Assert.False(DalamudContextMenuOptionParser.Find(["Discard", "Search for Item"], option).Success);
    }

    [Theory]
    [InlineData("Discard")]
    [InlineData("捨てる")]
    [InlineData("Wegwerfen")]
    [InlineData("Jeter")]
    public void FindDiscardEntry_RequiresAnExactLocalizedSemanticLabel(string label)
    {
        var option = new DalamudContextMenuOptionSpec("Discard", new HashSet<string> { "Discard", "捨てる", "Wegwerfen", "Jeter" });
        Assert.True(DalamudContextMenuOptionParser.Find(["Try On", label], option).Success);
        Assert.False(DalamudContextMenuOptionParser.Find([$"{label} all"], option).Success);
    }

    [Fact]
    public void FindSellEntry_DoesNotConfuseMarketOrRetainerActions()
    {
        var option = new DalamudContextMenuOptionSpec("Sell", new HashSet<string> { "Sell", "売却する", "Verkaufen", "Vendre" });
        Assert.Equal(1, DalamudContextMenuOptionParser.Find(["Search for Item", "Sell", "Have Retainer Sell Item"], option).Index);
    }
}
