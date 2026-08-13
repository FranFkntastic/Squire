using System;
using System.Linq;

namespace MarketMafioso.Squire.Outfitter.Utility;

public sealed record AdvisorCharacterSubject(
    bool IsAvailable,
    uint? ClassJobId,
    string JobLabel,
    short? Level)
{
    public static AdvisorCharacterSubject Unavailable { get; } = new(false, null, "No active character", null);
}

public enum AdvisorWorkspaceTone
{
    Quiet,
    Progress,
    Success,
    Warning,
    Error,
}

public sealed record AdvisorWorkspacePresentation(
    bool CharacterAvailable,
    string CharacterLabel,
    string FamilyLabel,
    string ContextLabel,
    bool CanEvaluate,
    string PrimaryActionLabel,
    string PrimaryActionReviewLabel,
    bool ShowRecoveryCallout,
    string? RecoveryTitle,
    string? RecoveryMessage,
    bool ShowEmptyIntroduction,
    string EmptyTitle,
    string EmptyMessage,
    bool ShowSessionStatus,
    AdvisorWorkspaceTone SessionTone,
    string SessionLabel,
    string SessionMessage,
    bool ShowProgress,
    bool ShowRetainedFrontier,
    bool ShowCancel,
    bool ShowDeterministicReview);

public static class AdvisorWorkspacePresentationResolver
{
    public static bool MayPresentFrontier(AdvisorCharacterSubject subject, uint? evaluatedClassJobId) =>
        subject.IsAvailable &&
        subject.ClassJobId is { } activeClassJobId &&
        evaluatedClassJobId == activeClassJobId &&
        AdvisorStatFamilies.Resolve(activeClassJobId) is not null;

    public static AdvisorWorkspacePresentation Resolve(
        AdvisorCharacterSubject subject,
        MinerBotanistAdvisorSessionState state,
        bool hasFrontier,
        bool deterministicReviewAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(state);

        var family = subject.ClassJobId is { } classJobId ? AdvisorStatFamilies.Resolve(classJobId) : null;
        var isFisher = subject.ClassJobId == AdvisorStatFamilies.FisherClassJobId;
        var characterAvailable = subject.IsAvailable && subject.ClassJobId is not null;
        var jobLabel = characterAvailable && !string.IsNullOrWhiteSpace(subject.JobLabel)
            ? subject.JobLabel.Trim()
            : "No active character";
        var familyLabel = FamilyLabel(family, isFisher);
        var evaluatedClassJobId = state.Advice?.Frontier?.Pareto.Frontier.FirstOrDefault()?.Utility.Context.ClassJobId;
        var staleJobEvidence = characterAvailable && family is not null && evaluatedClassJobId is { } evaluated &&
            evaluated != subject.ClassJobId;
        var context = ResolveContext(family, state, staleJobEvidence);
        var canEvaluate = characterAvailable && family is not null && !state.IsBusy;
        var primaryLabel = state.Advice is null || staleJobEvidence ? "Evaluate gear upgrades" : "Refresh evaluation";
        var supportedFrontier = hasFrontier && characterAvailable && family is not null;
        var showRetained = state.AdviceIsRetained && supportedFrontier;
        var showRecovery = !supportedFrontier && (!characterAvailable || family is null);
        var recovery = Recovery(characterAvailable, isFisher, jobLabel);
        var showIntroduction = !supportedFrontier && !showRecovery &&
            (state.Stage == MinerBotanistAdvisorSessionStage.Idle || staleJobEvidence);
        var status = showRecovery || staleJobEvidence
            ? (Show: false, Tone: AdvisorWorkspaceTone.Quiet, Label: string.Empty, Message: string.Empty)
            : SessionStatus(state, jobLabel, familyLabel, showRetained);

        return new(
            characterAvailable,
            jobLabel,
            familyLabel,
            context.Label,
            canEvaluate,
            primaryLabel,
            state.Advice is null || staleJobEvidence
                ? $"Evaluate gear upgrades for the active {jobLabel}"
                : $"Refresh gear upgrades for the active {jobLabel}",
            showRecovery,
            recovery.Title,
            recovery.Message,
            showIntroduction,
            $"Find {jobLabel} gear upgrades",
            EmptyMessage(familyLabel, jobLabel),
            status.Show,
            status.Tone,
            status.Label,
            status.Message,
            state.IsBusy,
            showRetained,
            state.IsBusy,
            showRecovery && deterministicReviewAvailable && !state.IsBusy);
    }

    private static AdvisorUtilityContextDescriptor ResolveContext(
        IAdvisorStatFamily? family,
        MinerBotanistAdvisorSessionState state,
        bool normalizeForCurrentJob)
    {
        if (family is null)
            return state.Context;
        if (normalizeForCurrentJob || state.Stage == MinerBotanistAdvisorSessionStage.Idle && state.Advice is null)
            return family.ProfileDescriptor.DefaultContext;
        return family.ResolveContext(state.Context.Id);
    }

    private static string FamilyLabel(IAdvisorStatFamily? family, bool isFisher) => family switch
    {
        GathererAdvisorStatFamily => "Gathering",
        CrafterAdvisorStatFamily => "Crafting",
        PhysicalRangedAdvisorStatFamily => "Physical ranged DPS",
        _ when isFisher => "Fisher",
        _ => "Unsupported job",
    };

    private static (string? Title, string? Message) Recovery(bool available, bool isFisher, string jobLabel)
    {
        if (!available)
        {
            return (
                "Waiting for a character",
                "Squire can evaluate gear upgrades as soon as an active character is available.");
        }
        if (isFisher)
        {
            return (
                "Fisher is outside the supported scope",
                "Squire does not evaluate Fisher equipment. This is a terminal scope boundary, not an incomplete scan.");
        }
        return (
            $"{jobLabel} is not supported yet",
            "Squire has no authoritative equipment model for this job, so it will not start an evaluation.");
    }

    private static string EmptyMessage(string familyLabel, string jobLabel) => familyLabel switch
    {
        "Gathering" => $"Compare equipped {jobLabel} gear with owned, vendor, crafted, and market options.",
        "Crafting" => $"Compare equipped {jobLabel} gear with owned, vendor, crafted, and market options using the crafting stat model.",
        "Physical ranged DPS" => $"Compare equipped {jobLabel} gear with owned, vendor, crafted, and market options using the physical-ranged role model.",
        _ => "Squire will evaluate the active job only when an authoritative model is available.",
    };

    private static (bool Show, AdvisorWorkspaceTone Tone, string Label, string Message) SessionStatus(
        MinerBotanistAdvisorSessionState state,
        string jobLabel,
        string familyLabel,
        bool retained)
    {
        if (state.IsBusy)
        {
            var message = state.Stage == MinerBotanistAdvisorSessionStage.CapturingPlayer
                ? $"Reading current {jobLabel} equipment and {familyLabel.ToLowerInvariant()} stats."
                : $"Comparing {jobLabel} options from owned, vendor, crafted, and market sources.";
            return (true, AdvisorWorkspaceTone.Progress,
                retained ? "Refreshing · last valid frontier retained" : "Evaluation in progress", message);
        }

        return state.Stage switch
        {
            MinerBotanistAdvisorSessionStage.Complete =>
                (true, AdvisorWorkspaceTone.Success, "Evaluation complete", state.Message),
            MinerBotanistAdvisorSessionStage.Abstained =>
                (true, AdvisorWorkspaceTone.Warning,
                    retained ? "Refresh abstained · last valid frontier retained" : "No authoritative recommendation",
                    state.Message),
            MinerBotanistAdvisorSessionStage.Cancelled =>
                (true, AdvisorWorkspaceTone.Quiet,
                    retained ? "Refresh cancelled · last valid frontier retained" : "Evaluation cancelled",
                    state.Message),
            MinerBotanistAdvisorSessionStage.Failed =>
                (true, AdvisorWorkspaceTone.Error,
                    retained ? "Refresh failed · last valid frontier retained" : "Evaluation failed safely",
                    state.Message),
            _ => (false, AdvisorWorkspaceTone.Quiet, string.Empty, string.Empty),
        };
    }
}
