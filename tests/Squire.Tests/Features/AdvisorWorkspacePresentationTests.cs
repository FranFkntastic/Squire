using MarketMafioso.Squire.Outfitter.Utility;
using MarketMafioso.Windows.Squire;

namespace MarketMafioso.Squire.Tests.Features;

public sealed class AdvisorWorkspacePresentationTests
{
    [Fact]
    public void ReviewedAdvisorControlIdsRemainStableAndUnique()
    {
        Assert.Equal(12, AdvisorReviewedControlIds.Fixed.Count);
        Assert.Equal(
            AdvisorReviewedControlIds.Fixed.Count,
            AdvisorReviewedControlIds.Fixed.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("squire.outfitter.advisor.refresh", AdvisorReviewedControlIds.Refresh);
        Assert.Equal("squire.outfitter.advisor.cancel", AdvisorReviewedControlIds.Cancel);
        Assert.Equal("squire.outfitter.advisor.stage-workbench", AdvisorReviewedControlIds.StageWorkbench);
        Assert.Equal("squire.outfitter.advisor.stage-materials-workbench", AdvisorReviewedControlIds.StageMaterialsWorkbench);
    }

    [Fact]
    public void MissingCharacterOwnsOneRecoveryStateAndCannotEvaluate()
    {
        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            AdvisorCharacterSubject.Unavailable,
            State(MinerBotanistAdvisorSessionStage.Idle),
            hasFrontier: false);

        Assert.True(presentation.ShowRecoveryCallout);
        Assert.Equal("Waiting for a character", presentation.RecoveryTitle);
        Assert.False(presentation.ShowEmptyIntroduction);
        Assert.False(presentation.CanEvaluate);
        Assert.False(presentation.ShowSessionStatus);
    }

    [Fact]
    public void MissingCharacterWhileBusyKeepsCancelAndSuppressesEvaluate()
    {
        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            AdvisorCharacterSubject.Unavailable,
            State(MinerBotanistAdvisorSessionStage.CapturingPlayer),
            hasFrontier: false,
            deterministicReviewAvailable: true);

        Assert.True(presentation.ShowRecoveryCallout);
        Assert.True(presentation.ShowCancel);
        Assert.False(presentation.CanEvaluate);
        Assert.False(presentation.ShowDeterministicReview);
    }

    [Fact]
    public void FisherIsATerminalScopeAbstentionAndCannotEvaluate()
    {
        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            Subject(AdvisorStatFamilies.FisherClassJobId, "FSH"),
            State(MinerBotanistAdvisorSessionStage.Idle),
            hasFrontier: false);

        Assert.True(presentation.ShowRecoveryCallout);
        Assert.Contains("Fisher", presentation.RecoveryTitle);
        Assert.Contains("terminal scope boundary", presentation.RecoveryMessage);
        Assert.False(presentation.CanEvaluate);
    }

    [Theory]
    [InlineData(MinerBotanistUtilityProfile.MinerClassJobId, "MIN", "Gathering", "Ordinary nodes")]
    [InlineData(CrafterUtilityProfile.BlacksmithClassJobId, "BSM", "Crafting", "Ordinary crafts")]
    [InlineData(TankUtilityProfile.MarauderClassJobId, "MRD", "Tank", "General tank combat")]
    [InlineData(PhysicalRangedUtilityProfile.BardClassJobId, "BRD", "Physical ranged DPS", "General physical-ranged combat")]
    public void SupportedFamilyOwnsItsJobContextAndEmptyCopy(
        uint classJobId,
        string job,
        string family,
        string context)
    {
        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            Subject(classJobId, job),
            State(MinerBotanistAdvisorSessionStage.Idle),
            hasFrontier: false);

        Assert.True(presentation.CanEvaluate);
        Assert.True(presentation.ShowEmptyIntroduction);
        Assert.Equal(job, presentation.CharacterLabel);
        Assert.Equal(family, presentation.FamilyLabel);
        Assert.Contains(context, presentation.ContextLabel);
        Assert.Contains(job, presentation.EmptyMessage);
        if (classJobId != MinerBotanistUtilityProfile.MinerClassJobId)
            Assert.DoesNotContain("MIN", presentation.EmptyMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedGearsetReviewActionNamesTheSelectedTargetInsteadOfTheActiveLoadout()
    {
        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            new(true, TankUtilityProfile.MarauderClassJobId, "MRD", 10, "saved gearset 'Marauder' (MRD)"),
            State(MinerBotanistAdvisorSessionStage.Idle),
            hasFrontier: false);

        Assert.Equal(
            "Evaluate gear upgrades for saved gearset 'Marauder' (MRD)",
            presentation.PrimaryActionReviewLabel);
    }

    [Theory]
    [InlineData(MinerBotanistUtilityProfile.BotanistClassJobId, "BTN", "gathering")]
    [InlineData(CrafterUtilityProfile.BlacksmithClassJobId, "BSM", "crafting")]
    [InlineData(TankUtilityProfile.WarriorClassJobId, "WAR", "tank")]
    [InlineData(PhysicalRangedUtilityProfile.MachinistClassJobId, "MCH", "physical ranged")]
    public void CaptureProgressUsesTheActiveJobAndFamily(uint classJobId, string job, string family)
    {
        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            Subject(classJobId, job),
            State(MinerBotanistAdvisorSessionStage.CapturingPlayer),
            hasFrontier: false);

        Assert.True(presentation.ShowSessionStatus);
        Assert.True(presentation.ShowProgress);
        Assert.False(presentation.CanEvaluate);
        Assert.Contains(job, presentation.SessionMessage);
        Assert.Contains(family, presentation.SessionMessage, StringComparison.OrdinalIgnoreCase);
        if (classJobId != MinerBotanistUtilityProfile.BotanistClassJobId)
            Assert.DoesNotContain("BTN", presentation.SessionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteSessionPresentsQuietSuccessWithoutChangingRefreshAuthority()
    {
        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            Subject(CrafterUtilityProfile.BlacksmithClassJobId, "BSM"),
            State(MinerBotanistAdvisorSessionStage.Complete) with { Message = "Complete fixture" },
            hasFrontier: true);

        Assert.True(presentation.ShowSessionStatus);
        Assert.Equal(AdvisorWorkspaceTone.Success, presentation.SessionTone);
        Assert.Equal("Evaluation complete", presentation.SessionLabel);
        Assert.Equal("Complete fixture", presentation.SessionMessage);
        Assert.True(presentation.CanEvaluate);
    }

    [Theory]
    [InlineData(MinerBotanistAdvisorSessionStage.DiscoveringMarket, "Refreshing")]
    [InlineData(MinerBotanistAdvisorSessionStage.Abstained, "abstained")]
    [InlineData(MinerBotanistAdvisorSessionStage.Failed, "failed")]
    [InlineData(MinerBotanistAdvisorSessionStage.Cancelled, "cancelled")]
    public void RetainedFrontierStaysVisibleWithLifecycleTruth(
        MinerBotanistAdvisorSessionStage stage,
        string statusWord)
    {
        var state = State(stage) with { AdviceIsRetained = true };
        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            Subject(MinerBotanistUtilityProfile.MinerClassJobId, "MIN"),
            state,
            hasFrontier: true);

        Assert.True(presentation.ShowRetainedFrontier);
        Assert.True(presentation.ShowSessionStatus);
        Assert.Contains(statusWord, presentation.SessionLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisibleFrontierNeverEscapesAnUnsupportedSubjectBoundary()
    {
        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            Subject(AdvisorStatFamilies.FisherClassJobId, "FSH"),
            State(MinerBotanistAdvisorSessionStage.Abstained) with { AdviceIsRetained = true },
            hasFrontier: true);

        Assert.False(presentation.ShowRetainedFrontier);
        Assert.False(presentation.CanEvaluate);
        Assert.True(presentation.ShowRecoveryCallout);
        Assert.False(presentation.ShowSessionStatus);
    }

    [Fact]
    public void FrontierMustBelongToTheExactActiveJob()
    {
        var miner = Subject(MinerBotanistUtilityProfile.MinerClassJobId, "MIN");

        Assert.True(AdvisorWorkspacePresentationResolver.MayPresentFrontier(
            miner,
            MinerBotanistUtilityProfile.MinerClassJobId));
        Assert.False(AdvisorWorkspacePresentationResolver.MayPresentFrontier(
            miner,
            MinerBotanistUtilityProfile.BotanistClassJobId));
        Assert.False(AdvisorWorkspacePresentationResolver.MayPresentFrontier(
            AdvisorCharacterSubject.Unavailable,
            MinerBotanistUtilityProfile.MinerClassJobId));
    }

#if DEBUG
    [Fact]
    public void SupportedJobChangeNormalizesOldFrontierToCurrentJobEvaluateState()
    {
        var minerAdvice = MinerBotanistAdvisorSyntheticReview.Build(MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark);
        var staleMinerState = State(MinerBotanistAdvisorSessionStage.Complete) with
        {
            Message = "Old MIN evaluation complete",
            Advice = minerAdvice,
            AdviceIsRetained = true,
        };

        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            Subject(CrafterUtilityProfile.BlacksmithClassJobId, "BSM"),
            staleMinerState,
            hasFrontier: false);

        Assert.False(presentation.ShowRetainedFrontier);
        Assert.False(presentation.ShowSessionStatus);
        Assert.True(presentation.ShowEmptyIntroduction);
        Assert.Equal("Evaluate gear upgrades", presentation.PrimaryActionLabel);
        Assert.Equal("Crafting", presentation.FamilyLabel);
        Assert.Equal(CrafterAdvisorStatFamily.OrdinaryCraftContext.Label, presentation.ContextLabel);
        Assert.Contains("BSM", presentation.EmptyTitle);
        Assert.DoesNotContain("Refresh", presentation.PrimaryActionLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("Old MIN", presentation.SessionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCharacterCanReachOnlyTheReviewedDeterministicFixtureAction()
    {
        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            AdvisorCharacterSubject.Unavailable,
            State(MinerBotanistAdvisorSessionStage.Idle),
            hasFrontier: false,
            deterministicReviewAvailable: true);

        Assert.True(presentation.ShowRecoveryCallout);
        Assert.True(presentation.ShowDeterministicReview);
        Assert.False(presentation.ShowCancel);
        Assert.False(presentation.CanEvaluate);
        Assert.Equal("squire.outfitter.advisor.synthetic-review", AdvisorReviewedControlIds.SyntheticReview);
    }

    [Fact]
    public void FrozenScenariosCoverSuccessLoadingStaleIncompleteAndAbstentionWithoutActions()
    {
        var advice = MinerBotanistAdvisorSyntheticReview.Build(MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark);
        var success = MinerBotanistAdvisorSyntheticReview.Present(MinerBotanistAdvisorSyntheticScenarioKind.Success, advice);
        var loading = MinerBotanistAdvisorSyntheticReview.Present(MinerBotanistAdvisorSyntheticScenarioKind.Refreshing, advice);
        var stale = MinerBotanistAdvisorSyntheticReview.Present(MinerBotanistAdvisorSyntheticScenarioKind.StaleEvidence, advice);
        var incomplete = MinerBotanistAdvisorSyntheticReview.Present(MinerBotanistAdvisorSyntheticScenarioKind.IncompleteEvidence, advice);
        var abstention = MinerBotanistAdvisorSyntheticReview.Present(MinerBotanistAdvisorSyntheticScenarioKind.Abstention, advice);

        Assert.Equal(MinerBotanistAdvisorSessionStage.Complete, success.Stage);
        Assert.True(loading.ShowProgress);
        Assert.True(loading.ShowPriorFrontier);
        Assert.True(stale.AdviceIsRetained);
        Assert.True(incomplete.AdviceIsRetained);
        Assert.False(abstention.ShowPriorFrontier);
        Assert.True(abstention.OwnsNoFrontierState);
        Assert.Equal("No authoritative recommendation", abstention.Label);
        Assert.Contains("produced no frontier", abstention.Message, StringComparison.Ordinal);
    }
#endif

    private static AdvisorCharacterSubject Subject(uint classJobId, string job) =>
        new(true, classJobId, job, 100);

    private static MinerBotanistAdvisorSessionState State(MinerBotanistAdvisorSessionStage stage) =>
        new(
            stage,
            $"{stage} fixture",
            "fixture coverage",
            3,
            10,
            GathererAdvisorStatFamily.Instance.ProfileDescriptor.DefaultContext,
            null,
            false,
            DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
}
