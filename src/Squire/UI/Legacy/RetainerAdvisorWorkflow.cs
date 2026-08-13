using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Windows.Squire;

internal sealed record RetainerAdvisorWorkflow(
    Func<OutfitterTarget, RetainerTargetObservationProgress> BeginObservation,
    Func<RetainerTargetObservationProgress> CaptureObservation,
    Action CancelObservation,
    Func<OutfitterTarget, string, RetainerVentureObjectiveResolution> ResolveVenture,
    Action<OutfitterTarget, RetainerVentureObjectiveOption> SelectVenture,
    Func<OutfitterTarget, RetainerVentureOutcomeCalibrationResult> CalibrateVentureOutcome,
    Func<string, string> CaptureVentureItemName);
