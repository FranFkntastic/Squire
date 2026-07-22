using System.Text.Json;
using MarketMafioso.SquireIntegration;

namespace MarketMafioso.Tests.Squire;

public sealed class StandaloneSquireIpcClientTests
{
    [Fact]
    public void Client_discovers_matching_provider_and_opens_it_through_versioned_ipc()
    {
        var adapter = new FakeAdapter();
        var client = new StandaloneSquireIpcClient(adapter);

        Assert.True(client.TryGetSnapshot(out var snapshot, out var error), error);
        Assert.Equal("provider-1", snapshot!.ProviderInstanceId);
        Assert.Equal("migration-shell", snapshot.FeatureState);
        Assert.True(client.TryOpen(out error), error);
        Assert.True(adapter.Opened);
    }

    [Fact]
    public void Client_rejects_snapshot_from_a_different_provider_instance()
    {
        var adapter = new FakeAdapter { SnapshotProviderInstanceId = "provider-2" };
        var client = new StandaloneSquireIpcClient(adapter);

        Assert.False(client.TryGetSnapshot(out _, out var error));
        Assert.Contains("invalid snapshot", error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeAdapter : IStandaloneSquireIpcAdapter
    {
        public string SnapshotProviderInstanceId { get; init; } = "provider-1";
        public bool Opened { get; private set; }
        public bool HasCapabilities => true;
        public bool HasSnapshot => true;
        public bool HasOpen => true;
        public string GetCapabilities() => JsonSerializer.Serialize(new
        {
            schema = StandaloneSquireIpcClient.CapabilitiesSchema,
            providerInstanceId = "provider-1",
            capabilities = new[] { "standalone-plugin" },
        });
        public string GetSnapshot() => JsonSerializer.Serialize(new
        {
            schema = StandaloneSquireIpcClient.SnapshotSchema,
            providerInstanceId = SnapshotProviderInstanceId,
            featureState = "migration-shell",
            windowOpen = false,
            workspace = "Outfitter",
            cleanupRuleCount = 1,
            characterRuleCount = 2,
        });
        public string Open()
        {
            Opened = true;
            return GetSnapshot();
        }
    }
}
