#if !DEBUG
using System.Reflection;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Windows;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterDryRunReleaseBoundaryTests
{
    [Fact]
    public void ReleaseArtifactExcludesStateFabricationEntrypoints()
    {
        var assembly = typeof(OutfitterRouteExecutionState).Assembly;
        var forbiddenTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "MarketMafioso.Squire.Outfitter.Acquisition.OutfitterDryRunSunkStateSeeder",
            "MarketMafioso.Squire.Outfitter.Crafting.OutfitterS4GoldenFixture",
            "MarketMafioso.Squire.Outfitter.Crafting.OutfitterS4GoldenFixtureResult",
            "MarketMafioso.Squire.Outfitter.Utility.MinerBotanistAdvisorSyntheticReview",
        };

        Assert.DoesNotContain(assembly.GetTypes(), type => type.FullName is { } name && forbiddenTypes.Contains(name));
        Assert.DoesNotContain(
            typeof(MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name.Contains("SeedOutfitterDryRunSunkState", StringComparison.Ordinal));
        var advisorPanel = assembly.GetType("MarketMafioso.Windows.Squire.MinerBotanistAdvisorPanel")
            ?? throw new InvalidOperationException("Advisor panel type was not present in the Release assembly.");
        Assert.DoesNotContain(
            advisorPanel.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            method => string.Equals(method.Name, "LoadSyntheticReview", StringComparison.Ordinal));
    }
}
#endif
