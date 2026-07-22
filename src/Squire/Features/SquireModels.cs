using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public enum SquireAssessment
{
    Protected,
    Candidate,
    EvaluationFailure,
    Unsupported,
}

public enum SquireDisposition
{
    Keep,
    ExpertDelivery,
    Desynthesize,
    VendorSell,
    Discard,
    Unsupported,
}

public enum SquireReasonSeverity
{
    Information,
    Warning,
    Blocking,
}

public sealed record SquireReason(string Code, string Message, SquireReasonSeverity Severity);

public sealed record SquireDispositionCapabilities(
    bool? DesynthesisUnlocked,
    bool? MateriaRetrievalUnlocked = null);

public enum SquireRuleKind
{
    ProtectItem,
    RetainCopies,
}

public enum SquireRuleQuality
{
    Any,
    NormalQuality,
    HighQuality,
}

public sealed record SquireRule(
    Guid Id,
    SquireRuleKind Kind,
    uint ItemId,
    SquireRuleQuality Quality,
    int MinimumCopies,
    bool Enabled,
    string Note)
{
    public bool Matches(uint itemId, bool isHighQuality) =>
        Enabled && ItemId == itemId && (Quality == SquireRuleQuality.Any ||
            Quality == (isHighQuality ? SquireRuleQuality.HighQuality : SquireRuleQuality.NormalQuality));

    public bool IsValid(out string error)
    {
        if (Id == Guid.Empty)
            error = "The rule has no stable ID.";
        else if (!Enum.IsDefined(Kind))
            error = $"Rule {Id} has unknown kind {(int)Kind}.";
        else if (!Enum.IsDefined(Quality))
            error = $"Rule {Id} has unknown quality scope {(int)Quality}.";
        else if (ItemId == 0)
            error = $"Rule {Id} has no item ID.";
        else if (Kind == SquireRuleKind.ProtectItem && Quality != SquireRuleQuality.Any)
            error = $"Protection rule {Id} must cover every quality.";
        else if (Kind == SquireRuleKind.RetainCopies && Quality == SquireRuleQuality.Any)
            error = $"Retention rule {Id} must select normal or high quality.";
        else if (Kind == SquireRuleKind.RetainCopies && MinimumCopies <= 0)
            error = $"Retention rule {Id} must keep at least one copy.";
        else
        {
            error = string.Empty;
            return true;
        }
        return false;
    }
}

public sealed record SquireDuplicateStatus(
    int OwnedCopies,
    int UserMinimumCopies,
    int GearsetRequiredCopies)
{
    public int EffectiveMinimumCopies => Math.Max(UserMinimumCopies, GearsetRequiredCopies);
    public int CopiesAboveFloor => Math.Max(0, OwnedCopies - EffectiveMinimumCopies);
}

public sealed record SquireProtectionPolicy(
    bool ProtectSignedGear = false,
    bool ProtectFutureLevelingGear = false,
    bool ProtectBlueAndPurpleGear = true,
    bool AllowRiskyMateriaRetrieval = false,
    IReadOnlyList<SquireRule>? Rules = null,
    ulong CharacterContentId = 0,
    IReadOnlyList<SquireCleanupRule>? CleanupRules = null)
{
    public IReadOnlyList<SquireRule> MatchingRules(uint itemId, bool isHighQuality) => Rules?
        .Where(rule => rule.Matches(itemId, isHighQuality))
        .ToArray() ?? [];

    public bool IsItemProtected(uint itemId) => Rules?.Any(rule =>
        rule.Enabled && rule.Kind == SquireRuleKind.ProtectItem && rule.ItemId == itemId) == true;

    public int MinimumCopiesToKeep(uint itemId, bool isHighQuality) => MatchingRules(itemId, isHighQuality)
        .Where(rule => rule.Kind == SquireRuleKind.RetainCopies)
        .Select(rule => Math.Max(0, rule.MinimumCopies))
        .DefaultIfEmpty(0)
        .Max();

    public IReadOnlyList<string> ValidationErrors =>
    [
        .. Rules?
            .Where(rule => rule.Enabled)
            .Select(rule => rule.IsValid(out var error) ? null : error)
            .Where(error => error is not null)
            .Select(error => error!) ?? [],
        .. CleanupRules?
            .Where(rule => rule.Enabled)
            .SelectMany(rule => rule.Validate()) ?? [],
    ];
}

public sealed record SquireCandidate(
    EquipmentInstanceSnapshot Instance,
    EquipmentItemDefinition Definition,
    SquireAssessment Assessment,
    SquireDisposition RecommendedDisposition,
    IReadOnlySet<SquireDisposition> SupportedDispositions,
    IReadOnlyList<SquireReason> Reasons,
    EquipmentUseAnalysis? UseAnalysis,
    SquireDuplicateStatus? DuplicateStatus = null,
    SquireCleanupRuleEvaluation? RuleEvaluation = null)
{
    public bool IsExecutable => Assessment == SquireAssessment.Candidate && SupportedDispositions.Count > 0;
}

public sealed record SquireAnalysis(
    CharacterEquipmentSnapshot Snapshot,
    IReadOnlyList<SquireCandidate> Candidates,
    SquireProtectionPolicy Policy)
{
    public bool IsActionable => Snapshot.Diagnostics.IsComplete;
}
