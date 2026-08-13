using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RetainerVentureOutcomeCalibrationTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 8, 13, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Battle_definition_must_reproduce_the_rendered_item_level_and_quantity()
    {
        var objective = Objective(RetainerProcurementProfileKind.Battle, 690, (690, 10), (715, 15));

        var result = RetainerVentureOutcomeCalibration.Calibrate(
            objective,
            "Hunting Log",
            "retainer:42", 10, "Fran Example", "Siren", 42, "Venture", 100,
            "session", CapturedAt.AddSeconds(-5), "ASKHASH", 7, "ASKTASKHASH",
            new(715, 0, 0, 0),
            ["Hunting Log", "Items obtained: 15"],
            CapturedAt);

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(RetainerVentureOutcomeCalibration.IsValid(result.Objective!));
        Assert.Equal(715, result.Objective!.RenderedOutcomeEvidence!.EligibilityStat);
        Assert.Equal(15, result.Objective.RenderedOutcomeEvidence.RenderedQuantity);
        Assert.Equal(100u, result.Objective.RenderedOutcomeEvidence.TaskId);
        Assert.Equal(7, result.Objective.RenderedOutcomeEvidence.AskTaskValueIndex);
        Assert.Equal("ASKTASKHASH", result.Objective.RenderedOutcomeEvidence.AskTaskValueSha256);
    }

    [Fact]
    public void Gathering_definition_must_reproduce_the_rendered_gathering_perception_and_quantity()
    {
        var objective = Objective(RetainerProcurementProfileKind.Gathering, 4300, (0, 10), (4900, 20));

        var result = RetainerVentureOutcomeCalibration.Calibrate(
            objective,
            "Dark Rye",
            "retainer:42", 10, "Fran Example", "Siren", 42, "Venture", 101,
            "session", CapturedAt.AddSeconds(-5), "ASKHASH", 7, "ASKTASKHASH",
            new(0, 4500, 4950, 0),
            ["Dark Rye", "Quantity 20"],
            CapturedAt);

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(RetainerVentureOutcomeCalibration.IsValid(result.Objective!));
        Assert.Equal(4500, result.Objective!.RenderedOutcomeEvidence!.EligibilityStat);
        Assert.Equal(4950, result.Objective.RenderedOutcomeEvidence.YieldStat);
    }

    [Fact]
    public void Definition_drift_or_rendered_quantity_mismatch_refuses_calibration()
    {
        var objective = Objective(RetainerProcurementProfileKind.Gathering, 4300, (0, 10), (4900, 20));
        var mismatch = RetainerVentureOutcomeCalibration.Calibrate(
            objective,
            "Dark Rye",
            "retainer:42", 10, "Fran Example", "Siren", 42, "Venture", 101,
            "session", CapturedAt.AddSeconds(-5), "ASKHASH", 7, "ASKTASKHASH",
            new(0, 4500, 4950, 0),
            ["Dark Rye", "Items obtained 10"],
            CapturedAt);
        var valid = RetainerVentureOutcomeCalibration.Calibrate(
            objective,
            "Dark Rye",
            "retainer:42", 10, "Fran Example", "Siren", 42, "Venture", 101,
            "session", CapturedAt.AddSeconds(-5), "ASKHASH", 7, "ASKTASKHASH",
            new(0, 4500, 4950, 0),
            ["Dark Rye", "Items obtained 20"],
            CapturedAt).Objective!;

        Assert.False(mismatch.Success);
        Assert.False(RetainerVentureOutcomeCalibration.IsValid(valid with { EvidenceGenerationId = Guid.NewGuid() }));
        Assert.False(RetainerVentureOutcomeCalibration.IsValid(valid with
        {
            RenderedOutcomeEvidence = valid.RenderedOutcomeEvidence! with { AskTaskValueSha256 = string.Empty },
        }));
    }

    [Fact]
    public void Controlled_session_requires_same_active_retainer_and_expires()
    {
        var identityAt = CapturedAt.AddMinutes(-1);

        Assert.True(RetainerVentureCalibrationContinuity.IsCurrent(42, 42, identityAt, CapturedAt));
        Assert.False(RetainerVentureCalibrationContinuity.IsCurrent(42, 43, identityAt, CapturedAt));
        Assert.False(RetainerVentureCalibrationContinuity.IsCurrent(42, null, identityAt, CapturedAt));
        Assert.False(RetainerVentureCalibrationContinuity.IsCurrent(
            42, 42, CapturedAt.Subtract(RetainerVentureCalibrationContinuity.MaximumSessionAge).AddTicks(-1), CapturedAt));
    }

    private static RetainerProcurementObjective Objective(
        RetainerProcurementProfileKind profile,
        int eligibility,
        params (int Required, int Quantity)[] thresholds) => new(
            "venture:test",
            profile,
            eligibility,
            thresholds.Select(value => new RetainerYieldThreshold(value.Required, value.Quantity)).ToArray(),
            Guid.Parse("fc2723da-0e0d-4ba4-b2c1-d8c6a1691616"),
            CapturedAt.AddMinutes(-1),
            true);
}
