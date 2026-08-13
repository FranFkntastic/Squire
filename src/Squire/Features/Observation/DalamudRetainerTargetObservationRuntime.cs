using MarketMafioso.AgentBridge;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudRetainerTargetObservationRuntime : IRetainerTargetObservationRuntime
{
    private readonly DalamudRetainerUiPreparation preparation;
    private readonly DalamudRenderedCharacterUiProbe probe;

    public DalamudRetainerTargetObservationRuntime(
        DalamudRetainerUiPreparation preparation,
        DalamudRenderedCharacterUiProbe probe)
    {
        this.preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public RenderedRetainerUiPreparationProgress BeginPreparation(string ownerHomeWorld) =>
        preparation.Begin(ownerHomeWorld);

    public RenderedRetainerUiPreparationProgress AdvancePreparation() => preparation.Advance();

    public RenderedRetainerUiPreparationProgress CancelPreparation() => preparation.Cancel();

    public RetainerTargetOpenResult TryOpenRetainer(string retainerName)
    {
        var result = probe.TryOpenRenderedRetainer(retainerName);
        return result.Success
            ? new(RetainerTargetOpenStatus.Accepted, result.Message)
            : result.Code is "InvalidRenderedRetainer" or "RenderedRetainerIdentityMismatch"
                ? new(RetainerTargetOpenStatus.Refused, result.Message)
                : new(RetainerTargetOpenStatus.Waiting, result.Message);
    }

    public AgentBridgeRenderedUiSnapshot CaptureRetainerUi() => probe.CaptureRetainerUi();

    public RenderedEquipmentScanProgress BeginRetainerEquipmentScan() => probe.BeginRetainerEquipmentScan();

    public RenderedEquipmentScanStepResult AdvanceRetainerEquipmentScan() => probe.AdvanceRetainerEquipmentScan();

    public RenderedEquipmentScanProgress CancelRetainerEquipmentScan() => probe.CancelRetainerEquipmentScan();
}
