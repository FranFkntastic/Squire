using Squire.AgentBridge;
using Franthropy.Dalamud.AgentBridge;
using System.Numerics;
using System.Text.Json;
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
                new SquireBridgeProductTruth(
                    "WaitingForAnalysis", null, false, 0, 0, 0, 0, false, false,
                    "Idle", "Ready to evaluate.", 3, 7, DateTimeOffset.UnixEpoch,
                    "active-loadout", "ActiveLoadout", "Current equipped job", 4, 3,
                    null, null, null, null, null,
                    "quality:hq", true, 2,
                    new SquireBridgeSettingsTruth("safety", true, true, false, true, 30, false, false, true, true, 90, false, true, true, true, true, false, false, false))),
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
        Assert.Equal("Ready to evaluate.", truth.Product.AdvisorMessage);
        Assert.Equal(3, truth.Product.AdvisorCompleted);
        Assert.Equal(7, truth.Product.AdvisorTotal);
        Assert.Equal(DateTimeOffset.UnixEpoch, truth.Product.AdvisorUpdatedAtUtc);
        Assert.Equal("active-loadout", truth.Product.AdvisorTargetKey);
        Assert.Equal("ActiveLoadout", truth.Product.AdvisorTargetKind);
        Assert.Equal("Current equipped job", truth.Product.AdvisorTargetLabel);
        Assert.Equal(4, truth.Product.AdvisorTargetCount);
        Assert.Equal(3, truth.Product.AdvisorReadyTargetCount);
        Assert.Null(truth.Product.OperationalStatusKind);
        Assert.Equal("quality:hq", truth.Product.CandidateFilterExpression);
        Assert.True(truth.Product.CandidateFilterValid);
        Assert.Equal(2, truth.Product.VisibleCandidateCount);
    }

    [Fact]
    public void Provider_forwards_typed_reviewed_control_arguments()
    {
        var expression = string.Empty;
        var registry = new AgentBridgeUiReviewRegistry();
        registry.BeginFrame();
        registry.Register(
            "squire.cleanup.filter",
            "Filter Cleanup candidates",
            AgentBridgeUiControlKind.Input,
            Vector2.Zero,
            Vector2.One,
            enabled: true,
            selected: false,
            value: string.Empty,
            new AgentBridgeActionArgumentSchema(
                [new("expression", AgentBridgeActionArgumentKind.String, Required: false)]),
            arguments =>
            {
                expression = arguments?.GetProperty("expression").GetString() ?? string.Empty;
                return AgentBridgeUiActionResult.Ok("Cleanup filter updated.");
            });
        var frame = registry.EndFrame();
        var provider = new SquireBridgeProvider(
            () => throw new InvalidOperationException(),
            () => { },
            () => { },
            registry);
        using var document = JsonDocument.Parse("{\"expression\":\"name:copper\"}");

        var result = provider.InvokeControl(
            "squire.cleanup.filter",
            frame.FrameId,
            document.RootElement.Clone());

        Assert.True(result.Success);
        Assert.Equal("name:copper", expression);
    }
}
