using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using MarketMafioso.Squire.Outfitter.Acquisition;

namespace Squire.Interop;

public interface IMarketMafiosoAcquisitionIpcAdapter
{
    bool HasFunction { get; }
    string Invoke(string requestJson);
}

public sealed class DalamudMarketMafiosoAcquisitionIpcAdapter : IMarketMafiosoAcquisitionIpcAdapter
{
    private readonly ICallGateSubscriber<string, string> stage;

    public DalamudMarketMafiosoAcquisitionIpcAdapter(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        stage = pluginInterface.GetIpcSubscriber<string, string>(MarketMafiosoAcquisitionIpcClient.StageChannel);
    }

    public bool HasFunction => stage.HasFunction;
    public string Invoke(string requestJson) => stage.InvokeFunc(requestJson);
}

public sealed class MarketMafiosoAcquisitionIpcClient
{
    public const string StageChannel = "MarketMafioso.v1.StageExactAcquisition";
    public const string TransferSchema = "gooseworks-exact-acquisition-workbench-transfer/v1";
    public const string ResponseSchema = "gooseworks-exact-acquisition-stage-response/v1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IMarketMafiosoAcquisitionIpcAdapter adapter;

    public MarketMafiosoAcquisitionIpcClient(IDalamudPluginInterface pluginInterface)
        : this(new DalamudMarketMafiosoAcquisitionIpcAdapter(pluginInterface))
    {
    }

    public MarketMafiosoAcquisitionIpcClient(IMarketMafiosoAcquisitionIpcAdapter adapter)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public void Stage(OutfitterWorkbenchTransfer transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (!adapter.HasFunction)
            throw new InvalidOperationException("MarketMafioso is not loaded or does not expose exact-acquisition IPC.");

        var wire = new
        {
            SchemaVersion = TransferSchema,
            Origin = "ExternalPlanningPlugin",
            transfer.SelectedSolutionId,
            transfer.AdvisorNominationSolutionId,
            transfer.Profile,
            transfer.Context,
            Evidence = new
            {
                transfer.Evidence.GenerationId,
                transfer.Evidence.Revision,
                transfer.Evidence.SchemaVersion,
                transfer.Evidence.SourceKey,
                transfer.Evidence.Region,
                CoverageMode = transfer.Evidence.CoverageMode.ToString(),
                transfer.Evidence.PublishedAtUtc,
            },
            transfer.SelectedLoadout,
            transfer.MarketLots,
            transfer.ObservedMarketTotalGil,
            transfer.DryRunOnly,
        };
        var responseJson = adapter.Invoke(JsonSerializer.Serialize(wire));
        var response = JsonSerializer.Deserialize<StageResponse>(responseJson, JsonOptions);
        if (response is null || response.Schema != ResponseSchema || !response.Accepted)
            throw new InvalidOperationException(response?.Error ?? "MarketMafioso rejected the exact-acquisition transfer.");
    }

    private sealed class StageResponse
    {
        public string? Schema { get; init; }
        public bool Accepted { get; init; }
        public string? Error { get; init; }
    }
}
