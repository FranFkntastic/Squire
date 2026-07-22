using System.Text.Json;
using MarketMafioso.Squire.Outfitter.Acquisition;
using Squire.Interop;

namespace Squire.Tests;

public sealed class MarketMafiosoAcquisitionIpcClientTests
{
    [Fact]
    public void Stage_MapsTheSquireTransferOntoTheProductNeutralWireContract()
    {
        var adapter = new Adapter();
        var client = new MarketMafiosoAcquisitionIpcClient(adapter);

        client.Stage(MarketMafioso.Tests.Squire.OutfitterWorkbenchAuthorityTests.Transfer());

        using var document = JsonDocument.Parse(adapter.Request!);
        var root = document.RootElement;
        Assert.Equal(MarketMafiosoAcquisitionIpcClient.TransferSchema, root.GetProperty("SchemaVersion").GetString());
        Assert.Equal("ExternalPlanningPlugin", root.GetProperty("Origin").GetString());
        Assert.Equal("selected-solution", root.GetProperty("SelectedSolutionId").GetString());
        Assert.Equal("ExhaustiveWithinScope", root.GetProperty("Evidence").GetProperty("CoverageMode").GetString());
        Assert.Single(root.GetProperty("MarketLots").EnumerateArray());
    }

    [Fact]
    public void Stage_FailsClosedWhenMarketMafiosoRejectsTheTransfer()
    {
        var adapter = new Adapter { Accepted = false };
        var client = new MarketMafiosoAcquisitionIpcClient(adapter);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            client.Stage(MarketMafioso.Tests.Squire.OutfitterWorkbenchAuthorityTests.Transfer()));

        Assert.Contains("rejected for test", exception.Message, StringComparison.Ordinal);
    }

    private sealed class Adapter : IMarketMafiosoAcquisitionIpcAdapter
    {
        public bool HasFunction { get; init; } = true;
        public bool Accepted { get; init; } = true;
        public string? Request { get; private set; }

        public string Invoke(string requestJson)
        {
            Request = requestJson;
            return JsonSerializer.Serialize(new
            {
                Schema = MarketMafiosoAcquisitionIpcClient.ResponseSchema,
                Accepted,
                Error = Accepted ? null : "rejected for test",
            });
        }
    }
}
