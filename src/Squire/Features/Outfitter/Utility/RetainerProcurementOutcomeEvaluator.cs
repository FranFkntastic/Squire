using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.Squire.Outfitter.Utility;

public enum RetainerProcurementProfileKind
{
    Battle,
    Gathering,
}

public sealed record RetainerProcurementStats(
    int AverageItemLevel,
    int Gathering,
    int Perception,
    int GatheringPoints);

public sealed record RetainerYieldThreshold(int RequiredStat, int Quantity);

public sealed record RetainerProcurementObjective(
    string VentureKey,
    RetainerProcurementProfileKind Profile,
    int RequiredEligibilityStat,
    IReadOnlyList<RetainerYieldThreshold> YieldThresholds,
    Guid EvidenceGenerationId,
    DateTimeOffset CapturedAtUtc,
    bool IsRenderedUiComplete);

public enum RetainerProcurementOutcomeStatus
{
    Complete,
    Ineligible,
    Unsupported,
    InvalidEvidence,
}

public sealed record RetainerProcurementOutcome(
    RetainerProcurementOutcomeStatus Status,
    bool IsEligible,
    int Quantity,
    int EligibilityStat,
    int YieldStat,
    int? NextYieldThreshold,
    string Diagnostic);

public sealed record RetainerQualityOutcomeComparison(
    RetainerProcurementOutcome NormalQuality,
    RetainerProcurementOutcome HighQuality,
    bool HighQualityChangesOutcome,
    string Diagnostic);

/// <summary>
/// Evaluates only deterministic targeted-procurement outcomes. Battle retainers use average item
/// level for both eligibility and yield. Gathering retainers use Gathering for eligibility and
/// Perception for yield; GP is intentionally ignored. Thresholds are observed venture evidence,
/// not patch-specific constants embedded in the profile.
/// </summary>
public static class RetainerProcurementOutcomeEvaluator
{
    public static RetainerProcurementOutcome Evaluate(
        RetainerProcurementObjective objective,
        RetainerProcurementStats stats)
    {
        ArgumentNullException.ThrowIfNull(objective);
        ArgumentNullException.ThrowIfNull(stats);

        if (string.IsNullOrWhiteSpace(objective.VentureKey) || objective.EvidenceGenerationId == Guid.Empty ||
            objective.CapturedAtUtc == default || !objective.IsRenderedUiComplete ||
            objective.RequiredEligibilityStat < 0 ||
            objective.YieldThresholds.Count == 0 ||
            objective.YieldThresholds.Any(value => value.RequiredStat < 0 || value.Quantity <= 0) ||
            objective.YieldThresholds.GroupBy(value => value.RequiredStat).Any(group => group.Count() > 1))
        {
            return new(RetainerProcurementOutcomeStatus.InvalidEvidence, false, 0, 0, 0, null,
                "The rendered venture requirement or yield thresholds are incomplete or inconsistent.");
        }

        var eligibilityStat = objective.Profile switch
        {
            RetainerProcurementProfileKind.Battle => stats.AverageItemLevel,
            RetainerProcurementProfileKind.Gathering => stats.Gathering,
            _ => -1,
        };
        var yieldStat = objective.Profile switch
        {
            RetainerProcurementProfileKind.Battle => stats.AverageItemLevel,
            RetainerProcurementProfileKind.Gathering => stats.Perception,
            _ => -1,
        };
        if (eligibilityStat < 0 || yieldStat < 0)
            return new(RetainerProcurementOutcomeStatus.Unsupported, false, 0, eligibilityStat, yieldStat, null,
                "The retainer profile is unsupported.");

        var ordered = objective.YieldThresholds.OrderBy(value => value.RequiredStat).ToArray();
        var nextThreshold = ordered.FirstOrDefault(value => value.RequiredStat > yieldStat)?.RequiredStat;
        if (eligibilityStat < objective.RequiredEligibilityStat)
        {
            return new(RetainerProcurementOutcomeStatus.Ineligible, false, 0, eligibilityStat, yieldStat, nextThreshold,
                $"The retainer is below the observed eligibility threshold by {objective.RequiredEligibilityStat - eligibilityStat:N0}.");
        }

        var reached = ordered.LastOrDefault(value => value.RequiredStat <= yieldStat);
        if (reached is null)
            return new(RetainerProcurementOutcomeStatus.InvalidEvidence, false, 0, eligibilityStat, yieldStat, nextThreshold,
                "No observed yield tier covers an otherwise eligible retainer.");

        return new(RetainerProcurementOutcomeStatus.Complete, true, reached.Quantity, eligibilityStat, yieldStat, nextThreshold,
            nextThreshold is null
                ? "The retainer reaches the highest observed yield tier."
                : $"The next observed yield tier requires {nextThreshold.Value - yieldStat:N0} more relevant stat.");
    }

    public static RetainerQualityOutcomeComparison CompareQuality(
        RetainerProcurementObjective objective,
        RetainerProcurementStats normalQuality,
        RetainerProcurementStats highQuality)
    {
        var nq = Evaluate(objective, normalQuality);
        var hq = Evaluate(objective, highQuality);
        var changesOutcome = nq.Status != hq.Status || nq.IsEligible != hq.IsEligible || nq.Quantity != hq.Quantity;
        return new(
            nq,
            hq,
            changesOutcome,
            changesOutcome
                ? "HQ changes a supported eligibility or yield outcome for this venture."
                : "HQ changes no supported eligibility or yield outcome for this venture.");
    }
}
