using Squire.AgentBridge;
using Franthropy.Dalamud.AgentBridge;
using Xunit;

namespace Squire.Tests;

public sealed class AgentBridgeProviderTests
{
    [Fact]
    public void Provider_advertises_only_its_registered_review_surface()
    {
        var opened = false;
        var provider = new SquireBridgeProvider(
            () => new SquireBridgeTruth(
                1,
                "provider",
                1,
                "test",
                false,
                "standalone",
                "Outfitter",
                0,
                0,
                null,
                new SquireBridgeProductTruth("WaitingForAnalysis", null, false, 0, 0, 0, 0, false, false, "Idle", null, null, null, null, null)),
            () => opened = true,
            () => { },
            new AgentBridgeUiReviewRegistry());

        var surface = Assert.Single(provider.GetReviewSurfaces());

        Assert.Equal("squire", surface.Id);
        Assert.False(provider.TryOpenMainWindow("unknown"));
        Assert.False(opened);
        Assert.True(provider.TryOpenMainWindow(surface.Target));
        Assert.True(opened);
        var truth = provider.CreateTruth();
        Assert.Equal("WaitingForAnalysis", truth.Product.CleanupSurfaceState);
        Assert.Equal("Idle", truth.Product.AdvisorStage);
        Assert.Null(truth.Product.OperationalStatusKind);
    }
}
