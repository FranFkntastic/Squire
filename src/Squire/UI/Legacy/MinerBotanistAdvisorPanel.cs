using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Plots;
using Franthropy.Dalamud.UI.Styling;
using MarketMafioso.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.Utility;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Portfolio;
using MarketMafioso.WorkshopPrep;
using MarketMafioso.Windows.Main;
using Squire.UI;
using Squire.AgentBridge;

namespace MarketMafioso.Windows.Squire;

internal sealed class MinerBotanistAdvisorPanel
{
    private static readonly IReadOnlyList<AdvisorUtilityContextDescriptor> ContextOrder =
        GathererAdvisorStatFamily.Instance.ProfileDescriptor.Contexts;

    private readonly ISquireConfigurationStore config;
    private readonly MinerBotanistAdvisorSession session;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Action<OutfitterWorkbenchTransfer> stageTransfer;
    private readonly IMarketAcquisitionListingSource listingSource;
    private readonly Func<IReadOnlyList<OutfitterTarget>> captureTargets;
    private readonly Func<AdvisorCharacterSubject> captureCharacter;
    private readonly Func<string> resolveRegion;
    private readonly PortfolioEquipmentExecutionCoordinator equipmentExecution;
    private readonly RetainerAdvisorWorkflow retainerWorkflow;
    private readonly ParetoFrontierPlotBuilder plotBuilder = new();
    private readonly DalamudPlotContainer plotContainer = new();
    private AdvisorUtilityContextDescriptor context = GathererAdvisorStatFamily.Instance.ProfileDescriptor.DefaultContext;
    private MinerBotanistReadOnlyAdvice? lastAdvice;
    private AdvisorFrontierPresentation? frontierPresentation;
    private AdvisorFrontierWindow? frontierWindow;
    private ParetoFrontierPlotModel? frontierPlot;
    private HashSet<string> frontierWarningIds = new(StringComparer.Ordinal);
    private IReadOnlyList<AdvisorAdjacentTradeoff> adjacentTradeoffs = [];
    private string? selectedSolutionId;
    private string? handoffStatus;
    private AdvisorFrontierView frontierView = AdvisorFrontierView.Solutions;
    private IReadOnlyList<OutfitterTarget>? targets;
    private OutfitterTarget? selectedTarget;
    private string? targetStatus;
    private string retainerVentureItemName = string.Empty;
    private RetainerVentureObjectiveResolution? retainerVentureResolution;
    private string? retainerVentureResolutionKey;
    private readonly Dictionary<string, PortfolioEvaluatedTarget> portfolioEvidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> portfolioTargetDiagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PortfolioTerminalTargetEvidence> portfolioTerminalEvidence = new(StringComparer.Ordinal);
    private Queue<OutfitterTarget?>? portfolioBuildQueue;
    private IReadOnlyList<MinerBotanistOwnedItemEvidence> portfolioSharedWornItems = [];
    private string? portfolioActiveTargetKey;
    private bool showPortfolio;
    private Task<OutfitterPortfolioPlan>? portfolioPlanTask;
    private CancellationTokenSource? portfolioPlanCancellation;
    private OutfitterPortfolioPlan? portfolioPlan;
    private PortfolioAuthorityEnvelope? portfolioAuthority;
    private string? portfolioPlanRevision;
    private string? pendingPortfolioPlanRevision;
    private string? portfolioPlanFailure;
    private string? equipmentExecutionMessage;
    private PortfolioAcquisitionRecoveryState? portfolioAcquisitionRecovery;
    private PortfolioAcquisitionTransfer? portfolioAcquisitionTransfer;
    private string? portfolioAcquisitionStatus;
    private bool portfolioAcquisitionCompositionFailed;
    private bool showPortfolioConsequences;
    private DateTimeOffset nextEquipmentAfterObservationAtUtc;
    private bool portfolioRecoveryBuildRequested;
    private PortfolioAcquisitionRecoveryTriggerState portfolioRecoveryTrigger = PortfolioAcquisitionRecoveryTrigger.Empty;
    private PortfolioEquipmentExecutionState? preparedEquipmentExecution;
    private string? preparedEquipmentAuthoritySha256;
#if DEBUG
    private static readonly MinerBotanistAdvisorSyntheticScenarioKind[] SyntheticScenarioOrder =
    [
        MinerBotanistAdvisorSyntheticScenarioKind.Success,
        MinerBotanistAdvisorSyntheticScenarioKind.Refreshing,
        MinerBotanistAdvisorSyntheticScenarioKind.StaleEvidence,
        MinerBotanistAdvisorSyntheticScenarioKind.IncompleteEvidence,
        MinerBotanistAdvisorSyntheticScenarioKind.Abstention,
    ];
    private MinerBotanistReadOnlyAdvice? syntheticReviewAdvice;
    private OutfitterS4GoldenFixtureResult? s4GoldenFixture;
    private MinerBotanistAdvisorDryRunFixture? dryRunFixture;
    private Task<MinerBotanistAdvisorDryRunFixture>? dryRunFixtureTask;
    private string? dryRunFixtureStatus;
    private MinerBotanistAdvisorSyntheticScenarioKind syntheticScenarioKind;
    private readonly HashSet<AdvisorUtilityContextDescriptor> visibleSyntheticContexts =
        [GathererAdvisorStatFamily.Instance.ProfileDescriptor.DefaultContext];
#endif

    public MinerBotanistAdvisorPanel(
        ISquireConfigurationStore config,
        MinerBotanistAdvisorSession session,
        AgentBridgeUiReviewRegistry reviewRegistry,
        IMarketAcquisitionListingSource listingSource,
        Func<IReadOnlyList<OutfitterTarget>> captureTargets,
        Func<AdvisorCharacterSubject> captureCharacter,
        Func<string> resolveRegion,
        PortfolioEquipmentExecutionCoordinator equipmentExecution,
        RetainerAdvisorWorkflow retainerWorkflow,
        Action<OutfitterWorkbenchTransfer> stageTransfer)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        this.listingSource = listingSource ?? throw new ArgumentNullException(nameof(listingSource));
        this.captureTargets = captureTargets ?? throw new ArgumentNullException(nameof(captureTargets));
        this.captureCharacter = captureCharacter ?? throw new ArgumentNullException(nameof(captureCharacter));
        this.resolveRegion = resolveRegion ?? throw new ArgumentNullException(nameof(resolveRegion));
        this.equipmentExecution = equipmentExecution ?? throw new ArgumentNullException(nameof(equipmentExecution));
        this.retainerWorkflow = retainerWorkflow ?? throw new ArgumentNullException(nameof(retainerWorkflow));
        this.stageTransfer = stageTransfer ?? throw new ArgumentNullException(nameof(stageTransfer));
        portfolioAcquisitionRecovery = LoadPortfolioAcquisitionRecovery(config.Squire.OutfitterPortfolioAcquisitionRecoveryStateJson);
        if (portfolioAcquisitionRecovery is not null)
            portfolioRecoveryTrigger = PortfolioAcquisitionRecoveryTrigger.Schedule(
                portfolioRecoveryTrigger,
                PortfolioAcquisitionRecoveryTriggerKind.WindowOpened,
                DateTimeOffset.UtcNow);
        context = GathererAdvisorStatFamily.Instance.ResolveContext(config.Squire.OutfitterAdvisorContext);
        showPortfolio = portfolioAcquisitionRecovery is not null ||
            equipmentExecution.State is { Status: not (PortfolioEquipmentExecutionStatus.Completed or PortfolioEquipmentExecutionStatus.UnexecutedSuffixRolledBack) };
    }

    public void Draw()
    {
#if DEBUG
        PumpDryRunFixture();
#endif
        CompletePendingWorkbenchTransfer();
        PumpPortfolioBuild();
        DrawPlanningModeToolbar();
        if (showPortfolio)
        {
            DrawPortfolioWorkspace();
            return;
        }
        var state = session.State;
        MinerBotanistReadOnlyAdvice? displayedAdvice = state.Advice;
#if DEBUG
        var syntheticReviewActive = syntheticReviewAdvice is not null;
        var syntheticPresentation = syntheticReviewAdvice is null
            ? null
            : MinerBotanistAdvisorSyntheticReview.Present(syntheticScenarioKind, syntheticReviewAdvice);
        displayedAdvice = syntheticPresentation is { ShowPriorFrontier: true }
            ? syntheticReviewAdvice
            : syntheticReviewActive ? null : displayedAdvice;
#endif
        var subject = SubjectForSelectedTarget(captureCharacter());
#if DEBUG
        if (syntheticReviewActive)
            subject = new(true, MinerBotanistUtilityProfile.MinerClassJobId, "MIN", 100);
#endif
        var evaluatedClassJobId = displayedAdvice?.Frontier?.Pareto.Frontier.FirstOrDefault()?.Utility.Context.ClassJobId;
        if (displayedAdvice is not null &&
            !AdvisorWorkspacePresentationResolver.MayPresentFrontier(subject, evaluatedClassJobId, selectedTarget))
        {
            displayedAdvice = null;
        }
        var hasFrontier = displayedAdvice?.Frontier?.Pareto.Frontier.Count > 0;
#if DEBUG
        var deterministicReviewAvailable = config.EnableMarketAcquisitionDryRunTools;
#else
        const bool deterministicReviewAvailable = false;
#endif
        var presentation = AdvisorWorkspacePresentationResolver.Resolve(
            subject,
            state,
            hasFrontier,
            deterministicReviewAvailable,
            selectedTarget);
        AdvisorWorkspaceComponentRenderer.DrawHeader(presentation);
        if (selectedTarget is { Kind: OutfitterTargetKind.Retainer } retainerTarget)
            DrawRetainerWorkflow(retainerTarget, state);
        if (!presentation.ShowRecoveryCallout)
            DrawControls(state, presentation, subject);
#if DEBUG
        if (syntheticReviewActive)
        {
            DrawSyntheticReviewStatus(syntheticPresentation!);
        }
        else
#endif
        {
            AdvisorWorkspaceComponentRenderer.DrawSessionStatus(state, presentation);
        }
        ImGui.Separator();

        if (displayedAdvice is not { Frontier: { } frontier } advice || frontier.Pareto.Frontier.Count == 0)
        {
#if DEBUG
            if (syntheticReviewActive && syntheticPresentation!.OwnsNoFrontierState)
                return;
#endif
            DrawEmptyState(presentation);
            return;
        }
        EnsureSelection(advice);
        var selected = frontierPresentation!.TryGet(selectedSolutionId, out var selectedSolution)
            ? selectedSolution
            : frontierPresentation.First;
#if DEBUG
        if (syntheticReviewActive)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Header, selected.VariantLabels.FirstOrDefault() ?? selected.Candidate.SolutionId);
            var decisionDetail = string.Join(" · ", selected.VariantLabels.Skip(2));
            if (!string.IsNullOrWhiteSpace(decisionDetail))
            {
                using var muted = ImRaii.PushColor(ImGuiCol.Text, MarketMafiosoUiTheme.Muted);
                ImGui.TextWrapped(decisionDetail);
            }
        }
#endif
        DrawAdvisorWorkspace(advice, selected);
    }

    private void DrawAdvisorWorkspace(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        const float wideLayoutMinimum = 980f;
        if (ImGui.GetContentRegionAvail().X < wideLayoutMinimum ||
            !ImGui.BeginTable("##SquireAdvisorWorkspace", 2,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            DrawSelectedDecisionComponent(advice, selected);
            ImGui.Separator();
            DrawFrontierComponent(advice, selected);
            return;
        }

        ImGui.TableSetupColumn("Selected decision", ImGuiTableColumnFlags.WidthStretch, 1.65f);
        ImGui.TableSetupColumn("Exact frontier", ImGuiTableColumnFlags.WidthStretch, 0.85f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawSelectedDecisionComponent(advice, selected);
        ImGui.TableNextColumn();
        DrawFrontierComponent(advice, selected);
        ImGui.EndTable();
    }

    private void DrawSelectedDecisionComponent(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        var selectedOffers = selected.Candidate.Selections
            .Select(selection => advice.OffersByAllocation.GetValueOrDefault(selection.AllocationKey))
            .Where(offer => offer is not null)
            .DistinctBy(offer => offer!.AllocationKey)
            .Cast<EquipmentExactSolverOffer>()
            .ToArray();
        var changedOffers = selectedOffers
            .Where(offer => offer.Offer.SourceKind != EquipmentAcquisitionSourceKind.Owned)
            .ToArray();
        var changedSlotCount = selected.Candidate.Selections.Count(selection =>
            advice.OffersByAllocation.TryGetValue(selection.AllocationKey, out var offer) &&
            offer.Offer.SourceKind != EquipmentAcquisitionSourceKind.Owned);
        var primary = changedOffers.FirstOrDefault(offer => offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.Craft)
            ?? changedOffers.FirstOrDefault();
        var title = primary is null
            ? "Keep current equipped loadout"
            : $"{AcquisitionVerb(primary.Offer.SourceKind)} {primary.Offer.Definition.Name}";

        ImGui.TextColored(MarketMafiosoUiTheme.Muted,
            advice.Nomination?.Candidate.SolutionId == selected.Candidate.SolutionId
                ? "RECOMMENDED UPGRADE"
                : "ALTERNATIVE OPTION");
        ImGui.TextColored(MarketMafiosoUiTheme.Header, title);
        ImGui.SameLine();
        ImGui.TextDisabled($"{changedSlotCount:N0} changed slot{(changedSlotCount == 1 ? string.Empty : "s")}");
        DrawDecisionSummary(advice, selected);
        DrawAcquisitionHandoffComponent(advice, selected);

        if (ImGui.CollapsingHeader($"Full selected loadout ({selected.Candidate.Selections.Count:N0} slots, {changedSlotCount:N0} changes)##SquireAdvisorLoadoutDisclosure"))
            DrawSelectedLoadout(advice, selected);
        if (adjacentTradeoffs.Count > 0 && ImGui.CollapsingHeader("Adjacent tradeoffs##SquireAdvisorTradeoffsDisclosure"))
            DrawAdjacentTradeoffs(advice);
    }

    private void DrawFrontierComponent(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "OTHER GOOD OPTIONS");
        ImGui.SameLine();
        DrawFrontierViewButton("List", AdvisorFrontierView.Solutions);
        ImGui.SameLine();
        DrawFrontierViewButton("Chart", AdvisorFrontierView.Plot);

        if (frontierView == AdvisorFrontierView.Plot)
            DrawFrontier(advice, selected);
        else
            DrawSolutionRail(advice, selected);
    }

    private void DrawFrontierViewButton(string label, AdvisorFrontierView view)
    {
        var selected = frontierView == view;
        if (ImGui.SmallButton($"{label}##SquireAdvisorFrontierView{view}"))
            frontierView = view;
        RegisterLastControl(
            view == AdvisorFrontierView.Solutions ? AdvisorReviewedControlIds.FrontierList : AdvisorReviewedControlIds.FrontierPlot,
            $"Show exact frontier as {label.ToLowerInvariant()}",
            AgentBridgeUiControlKind.Select,
            true,
            selected,
            view.ToString(),
            () => frontierView = view);
    }

    public void Dispose()
    {
        portfolioPlanCancellation?.Cancel();
        portfolioPlanCancellation?.Dispose();
    }

    private void DrawRetainerWorkflow(OutfitterTarget target, MinerBotanistAdvisorSessionState state)
    {
        EnsureRetainerVentureResolution(target);
        var observation = retainerWorkflow.CaptureObservation();
        var observingThisTarget = string.Equals(observation.TargetKey, target.Key, StringComparison.Ordinal) &&
            observation.Status is not (RetainerTargetObservationStatus.Idle or RetainerTargetObservationStatus.Complete or
                RetainerTargetObservationStatus.Failed or RetainerTargetObservationStatus.Cancelled);
        var evidenceComplete = target.RetainerEquipmentEvidence?.Status == RenderedRetainerEquipmentEvidenceStatus.Complete;
        var objectiveComplete = target.RetainerObjective?.IsDefinitionComplete == true;
        var outcomeCalibrated = target.RetainerObjective is { } objective && RetainerVentureOutcomeCalibration.IsValid(objective);
        DalamudUiChrome.DrawCallout(
            $"SquireRetainerEvidence{target.Key}",
            target.IsReady ? "Retainer evidence is ready" : $"Prepare {target.Name} for evaluation",
            target.IsReady
                ? "Owner, rendered retainer identity, worn slots, and venture outcome thresholds are bound to this target."
                : target.Diagnostic ?? "Observe the exact worn loadout and choose a targeted-procurement venture.",
            SquireUiTheme.Current,
            target.IsReady ? DalamudUiTone.Success : DalamudUiTone.Warning);
        ImGui.Spacing();
        DalamudUiChrome.DrawStatusFact(
            "Worn equipment",
            evidenceComplete ? $"{target.RetainerEquipmentEvidence!.Equipment.Count:N0} / {PlayerAdvisorEquippedSlotMap.All.Count:N0} slots proven" :
                observingThisTarget ? $"{observation.EquipmentScan?.CompletedSlots ?? 0:N0} / {observation.EquipmentScan?.TotalSlots ?? PlayerAdvisorEquippedSlotMap.All.Count:N0} observing" : "Not proven",
            SquireUiTheme.Current.Palette,
            evidenceComplete ? DalamudUiTone.Success : DalamudUiTone.Warning);
        DalamudUiChrome.DrawStatusFact(
            "Venture objective",
            objectiveComplete
                ? $"{retainerVentureItemName} · {(outcomeCalibrated ? "rendered outcome verified" : "verification pending")}"
                : "Choose an exact output item",
            SquireUiTheme.Current.Palette,
            outcomeCalibrated ? DalamudUiTone.Success : DalamudUiTone.Warning);

        var canObserve = !state.IsBusy && target.IsCurrentCharacter && target.RetainerMetadata is not null && !observingThisTarget;
        if (DalamudUiControls.Button(
                evidenceComplete ? "Observe worn gear again##SquireRetainerObserve" : "Observe worn gear##SquireRetainerObserve",
                SquireUiTheme.Current,
                DalamudUiTone.Neutral,
                quiet: true,
                enabled: canObserve))
            retainerWorkflow.BeginObservation(target);
        RegisterLastControl(
            "squire.outfitter.retainer.observe",
            $"Observe {target.Name}'s rendered worn equipment",
            AgentBridgeUiControlKind.Button,
            canObserve,
            observingThisTarget,
            observation.Status.ToString(),
            () => retainerWorkflow.BeginObservation(target));
        if (observingThisTarget)
        {
            ImGui.SameLine();
            if (DalamudUiControls.Button("Cancel observation##SquireRetainerObserveCancel", SquireUiTheme.Current, DalamudUiTone.Warning, quiet: true))
                retainerWorkflow.CancelObservation();
            RegisterLastControl(
                "squire.outfitter.retainer.observe.cancel",
                "Cancel the active retainer observation",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                observation.Status.ToString(),
                retainerWorkflow.CancelObservation);
        }
        if (observation.Status is RetainerTargetObservationStatus.Failed or RetainerTargetObservationStatus.Cancelled || observingThisTarget)
            ImGui.TextColored(observation.Status == RetainerTargetObservationStatus.Failed ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Muted, observation.Diagnostic);

        if (objectiveComplete)
        {
            var canCalibrate = !state.IsBusy;
            if (DalamudUiControls.Button(
                    outcomeCalibrated ? "Verify visible outcome again##SquireRetainerOutcome" : "Verify visible venture outcome##SquireRetainerOutcome",
                    SquireUiTheme.Current,
                    DalamudUiTone.Neutral,
                    quiet: true,
                    enabled: canCalibrate))
                targetStatus = retainerWorkflow.CalibrateVentureOutcome(target).Diagnostic;
            RegisterLastControl(
                "squire.outfitter.retainer.venture.verify-outcome",
                $"Verify {target.Name}'s selected rendered venture outcome",
                AgentBridgeUiControlKind.Button,
                canCalibrate,
                false,
                outcomeCalibrated.ToString(),
                () => targetStatus = retainerWorkflow.CalibrateVentureOutcome(target).Diagnostic);
            if (!string.IsNullOrWhiteSpace(targetStatus))
                ImGui.TextColored(outcomeCalibrated ? MarketMafiosoUiTheme.Muted : MarketMafiosoUiTheme.Warning, targetStatus);
        }

        ImGui.SetNextItemWidth(330f);
        var editedName = retainerVentureItemName;
        using (ImRaii.Disabled(state.IsBusy))
        {
            if (ImGui.InputTextWithHint("##SquireRetainerVentureItem", "Exact venture output item name", ref editedName, 160))
                ResolveRetainerVenture(target, editedName);
        }
        reviewRegistry.RegisterLastAction(
            "squire.outfitter.retainer.venture-item",
            $"Choose {target.Name}'s targeted-procurement venture by exact output item name",
            AgentBridgeUiControlKind.Input,
            !state.IsBusy,
            false,
            retainerVentureItemName,
            new AgentBridgeActionArgumentSchema([new("itemName", AgentBridgeActionArgumentKind.String, Required: true)]),
            arguments =>
            {
                var itemName = arguments is { ValueKind: System.Text.Json.JsonValueKind.Object } value &&
                               value.TryGetProperty("itemName", out var itemNameValue)
                    ? itemNameValue.GetString() ?? string.Empty
                    : string.Empty;
                return ResolveRetainerVenture(target, itemName)
                    ? AgentBridgeUiActionResult.Ok("Retainer venture objective selected from current installed-game data.")
                    : AgentBridgeUiActionResult.Fail(retainerVentureResolution?.Diagnostic ?? "No compatible venture was resolved.");
            });
        if (retainerVentureResolution is { } resolution)
        {
            if (resolution.Options.Count > 1)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(260f);
                var selectedOption = resolution.Options.SingleOrDefault(option =>
                    string.Equals(target.RetainerObjective?.VentureKey, option.Objective.VentureKey, StringComparison.Ordinal));
                if (ImGui.BeginCombo("##SquireRetainerVentureDefinition", selectedOption?.Label ?? "Choose one exact venture"))
                {
                    foreach (var option in resolution.Options)
                    {
                        var captured = option;
                        if (ImGui.Selectable(option.Label,
                                string.Equals(target.RetainerObjective?.VentureKey, option.Objective.VentureKey, StringComparison.Ordinal)))
                            SelectRetainerVenture(target, captured);
                    }
                    ImGui.EndCombo();
                }
                reviewRegistry.RegisterLastAction(
                    "squire.outfitter.retainer.venture-task",
                    $"Choose {target.Name}'s exact installed-game venture definition",
                    AgentBridgeUiControlKind.Select,
                    !state.IsBusy,
                    false,
                    selectedOption?.TaskId.ToString(),
                    new AgentBridgeActionArgumentSchema([new(
                        "taskId",
                        AgentBridgeActionArgumentKind.Integer,
                        true,
                        Minimum: 1,
                        Maximum: int.MaxValue)]),
                    arguments =>
                    {
                        if (arguments is not { } value || !value.TryGetProperty("taskId", out var taskValue) ||
                            !taskValue.TryGetUInt32(out var requestedTaskId))
                            return AgentBridgeUiActionResult.Fail("A positive whole-number venture task ID is required.");
                        var requested = resolution.Options.SingleOrDefault(option => option.TaskId == requestedTaskId);
                        if (requested is null)
                            return AgentBridgeUiActionResult.Fail("That venture task is not one of the currently rendered compatible choices.");
                        SelectRetainerVenture(target, requested);
                        return AgentBridgeUiActionResult.Ok($"Selected {requested.Label}.");
                    });
            }
            ImGui.TextColored(resolution.Options.Count > 0 ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Warning, resolution.Diagnostic);
        }
        ImGui.Separator();
    }

    public uint? SelectedRetainerVentureTaskId => selectedTarget is { Kind: OutfitterTargetKind.Retainer } target &&
        config.Squire.OutfitterRetainerVentureTaskIds.TryGetValue(target.Key, out var taskId)
            ? taskId
            : null;

    public string? SelectedRetainerVentureTaskLabel => SelectedRetainerVentureTaskId is { } taskId
        ? retainerVentureResolution?.Options.SingleOrDefault(option => option.TaskId == taskId)?.Label ??
          (selectedTarget is { } target && config.Squire.OutfitterRetainerVentureItemNames.GetValueOrDefault(target.Key) is { } itemName
              ? $"{itemName} / task {taskId}"
              : $"Venture task {taskId}")
        : null;

    private void DrawPlanningModeToolbar()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "PLANNING MODE");
        ImGui.SameLine();
        if (DalamudUiControls.SegmentedOption("One target##SquireAdvisorModeSingle", !showPortfolio, SquireUiTheme.Current))
            SelectPlanningMode(portfolio: false);
        ImGui.SameLine();
        if (DalamudUiControls.SegmentedOption("Portfolio##SquireAdvisorModePortfolio", showPortfolio, SquireUiTheme.Current))
            SelectPlanningMode(portfolio: true);
        RegisterLastControl(
            "squire.outfitter.mode.single",
            "Show one exact upgrade target",
            AgentBridgeUiControlKind.Select,
            portfolioBuildQueue is null,
            !showPortfolio,
            "single",
            () => SelectPlanningMode(portfolio: false));
        RegisterLastControl(
            "squire.outfitter.mode.portfolio",
            "Show the global upgrade portfolio",
            AgentBridgeUiControlKind.Select,
            true,
            showPortfolio,
            "portfolio",
            () => SelectPlanningMode(portfolio: true));
        ImGui.Separator();
    }

    private void SelectPlanningMode(bool portfolio)
    {
        var returningToRecovery = portfolio && !showPortfolio &&
            portfolioAcquisitionRecovery is { } recovery &&
            PortfolioAcquisitionRecovery.NeedsAutomaticRebuild(recovery);
        showPortfolio = portfolio;
        if (returningToRecovery)
        {
            portfolioRecoveryTrigger = PortfolioAcquisitionRecoveryTrigger.Schedule(
                portfolioRecoveryTrigger,
                PortfolioAcquisitionRecoveryTriggerKind.WorkspaceReturned,
                DateTimeOffset.UtcNow);
        }
    }

    internal bool NotifyWindowOpened()
    {
        if (portfolioAcquisitionRecovery is null)
            return false;
        showPortfolio = true;
        portfolioRecoveryTrigger = PortfolioAcquisitionRecoveryTrigger.Schedule(
            portfolioRecoveryTrigger,
            PortfolioAcquisitionRecoveryTriggerKind.WindowOpened,
            DateTimeOffset.UtcNow);
        return true;
    }

    internal void NotifyInventoryEvidenceChanged(string generation)
    {
        if (portfolioAcquisitionRecovery is null)
            return;
        portfolioRecoveryTrigger = PortfolioAcquisitionRecoveryTrigger.Schedule(
            portfolioRecoveryTrigger,
            PortfolioAcquisitionRecoveryTriggerKind.InventoryEvidenceChanged,
            DateTimeOffset.UtcNow,
            generation);
    }

    private void DrawPortfolioWorkspace()
    {
        EnsureTargets();
        var nowUtc = DateTimeOffset.UtcNow;
        if (portfolioAcquisitionRecovery is { } scheduledRecovery &&
            PortfolioAcquisitionRecovery.NeedsAutomaticRebuild(scheduledRecovery) &&
            PortfolioAcquisitionRecoveryTrigger.IsDue(portfolioRecoveryTrigger, nowUtc) &&
            portfolioBuildQueue is null && !session.State.IsBusy)
        {
            portfolioRecoveryTrigger = PortfolioAcquisitionRecoveryTrigger.Begin(portfolioRecoveryTrigger);
            if (scheduledRecovery.Status != PortfolioAcquisitionRecoveryStatus.Rebuilding)
            {
                portfolioAcquisitionRecovery = PortfolioAcquisitionRecovery.BeginRebuild(scheduledRecovery, nowUtc);
                portfolioAcquisitionStatus = portfolioAcquisitionRecovery.Diagnostic;
                SavePortfolioAcquisitionRecovery();
            }
            portfolioRecoveryBuildRequested = BeginPortfolioBuild();
            if (!portfolioRecoveryBuildRequested)
            {
                portfolioRecoveryTrigger = PortfolioAcquisitionRecoveryTrigger.Complete(portfolioRecoveryTrigger);
                portfolioAcquisitionRecovery = scheduledRecovery with
                {
                    Status = PortfolioAcquisitionRecoveryStatus.Failed,
                    UpdatedAtUtc = nowUtc,
                    Diagnostic = "Automatic portfolio recovery could not start because no included target has complete current evidence.",
                };
                portfolioAcquisitionStatus = portfolioAcquisitionRecovery.Diagnostic;
                SavePortfolioAcquisitionRecovery();
            }
        }
        if (!portfolioRecoveryBuildRequested &&
            equipmentExecution.State is { Status: not (PortfolioEquipmentExecutionStatus.Completed or PortfolioEquipmentExecutionStatus.UnexecutedSuffixRolledBack) } &&
            portfolioEvidence.Count == 0 && portfolioBuildQueue is null)
        {
            portfolioRecoveryBuildRequested = BeginPortfolioBuild();
        }
        PumpEquipmentAfterObservation();
        var busy = portfolioBuildQueue is not null;
        DalamudUiChrome.DrawSectionHeading(
            "Upgrade portfolio",
            "One allocation across jobs, gearsets, and retainers",
            SquireUiTheme.Current.Palette,
            () =>
            {
                if (busy)
                {
                    if (DalamudUiControls.Button("Cancel portfolio##SquirePortfolioCancel", SquireUiTheme.Current, DalamudUiTone.Warning, quiet: true))
                        CancelPortfolioBuild();
                    RegisterLastControl("squire.outfitter.portfolio.cancel", "Cancel portfolio evaluation", AgentBridgeUiControlKind.Button, true, false, portfolioActiveTargetKey, CancelPortfolioBuild);
                    return;
                }
                if (DalamudUiControls.Button("Evaluate ready targets##SquirePortfolioBuild", SquireUiTheme.Current, DalamudUiTone.Neutral, quiet: true))
                    BeginPortfolioBuild();
                RegisterLastControl("squire.outfitter.portfolio.evaluate", "Evaluate all ready portfolio targets", AgentBridgeUiControlKind.Button, true, false, null, () => BeginPortfolioBuild());
            },
            164f);
        ImGui.Spacing();
        DrawPortfolioRecoveryControls();
        if (busy)
        {
            DalamudUiChrome.DrawCallout(
                "SquirePortfolioBuildProgress",
                "Building the exact portfolio",
                $"Evaluating {portfolioActiveTargetKey ?? "the next target"}. Completed evidence remains visible and can be cancelled without losing it.",
                SquireUiTheme.Current,
                DalamudUiTone.Neutral);
        }

        var priorities = PortfolioPriorities();
        if (ImGui.BeginTable("##SquirePortfolioTargets", 5, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 42f);
            ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 105f);
            ImGui.TableSetupColumn("Horizon", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("Evidence", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();
            foreach (var priority in priorities)
                DrawPortfolioTargetRow(priority);
            ImGui.EndTable();
        }

        var hasActiveExecution = equipmentExecution.State is { Status: not (PortfolioEquipmentExecutionStatus.Completed or PortfolioEquipmentExecutionStatus.UnexecutedSuffixRolledBack) };
        if (hasActiveExecution)
            DrawPortfolioEquipmentExecution(null);

        var plan = ResolvePortfolioPlan(priorities);
        ImGui.Spacing();
        if (plan is null)
        {
            DalamudUiChrome.DrawCallout(
                "SquirePortfolioPlanning",
                "Planning the exact allocation",
                portfolioPlanFailure ?? "Squire is reconciling the evaluated frontiers away from the render thread. Existing evidence remains unchanged.",
                SquireUiTheme.Current,
                portfolioPlanFailure is null ? DalamudUiTone.Neutral : DalamudUiTone.Warning);
            return;
        }
        if (!plan.IsComplete)
        {
            DalamudUiChrome.DrawCallout(
                "SquirePortfolioPlanningBound",
                "Portfolio allocation stopped safely",
                plan.Diagnostic ?? "The exact allocation could not be completed within its deterministic work bound.",
                SquireUiTheme.Current,
                DalamudUiTone.Warning);
            return;
        }
        if (plan.SelectedCandidates.Count == 0)
        {
            DalamudUiChrome.DrawCallout(
                "SquirePortfolioEmpty",
                portfolioEvidence.Count == 0 ? "No portfolio evidence yet" : "No globally feasible upgrade",
                portfolioEvidence.Count == 0
                    ? "Evaluate the ready targets. Squire will preserve each exact frontier, then allocate shared items once across the portfolio."
                    : "The evaluated target recommendations cannot be combined without violating exact allocation evidence.",
                SquireUiTheme.Current,
                DalamudUiTone.Warning);
            return;
        }

        DalamudUiChrome.DrawCallout(
            "SquirePortfolioExact",
            "Every scarce item has one owner",
            $"{plan.SelectedCandidates.Count:N0} target recommendation{(plan.SelectedCandidates.Count == 1 ? string.Empty : "s")} fit the current exact allocation. {plan.AllocationConsequences.Count:N0} lower-priority conflict{(plan.AllocationConsequences.Count == 1 ? string.Empty : "s")} remain reviewable.",
            SquireUiTheme.Current,
            DalamudUiTone.Success);
        if (ImGui.BeginTable("##SquirePortfolioAllocation", 4, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("Selected changes", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("Source mix", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 82f);
            ImGui.TableHeadersRow();
            foreach (var candidate in plan.SelectedCandidates)
                DrawPortfolioAllocationRow(candidate);
            if (showPortfolioConsequences)
            {
                var targetLabels = plan.OrderedTargets.ToDictionary(value => value.TargetKey, value => value.TargetLabel, StringComparer.Ordinal);
                foreach (var consequence in plan.AllocationConsequences)
                    DrawPortfolioConsequenceRow(consequence, targetLabels);
            }
            ImGui.EndTable();
        }
        DrawPortfolioConsequenceDisclosure(plan.AllocationConsequences.Count);
        if (!hasActiveExecution)
        {
            DrawPortfolioAcquisition(plan);
            DrawPortfolioEquipmentExecution(plan);
        }
    }

    private void DrawPortfolioConsequenceDisclosure(int count)
    {
        var presentation = PortfolioConsequenceDisclosurePresentationResolver.Resolve(count, showPortfolioConsequences);
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, presentation.Summary);
        if (count == 0)
            return;
        void Toggle() => showPortfolioConsequences = !showPortfolioConsequences;
        if (DalamudUiControls.Button(
                $"{presentation.ActionLabel}##SquirePortfolioConsequencesToggle",
                SquireUiTheme.Current,
                DalamudUiTone.Neutral,
                quiet: true))
            Toggle();
        RegisterLastControl(
            PortfolioPresentationReviewedControlIds.ConsequencesToggle,
            showPortfolioConsequences ? "Hide lower-priority allocation alternatives" : "Show lower-priority allocation alternatives",
            AgentBridgeUiControlKind.Toggle,
            true,
            showPortfolioConsequences,
            count.ToString(),
            Toggle);
    }

    private void DrawPortfolioRecoveryControls()
    {
        if (portfolioAcquisitionRecovery is not { } recovery)
            return;
        DalamudUiChrome.DrawCallout(
            "SquirePortfolioAcquisitionRecovery",
            recovery.Status == PortfolioAcquisitionRecoveryStatus.ReadyToEquip
                ? "Acquisition evidence reconciled"
                : "Resuming the reviewed acquisition",
            recovery.Diagnostic,
            SquireUiTheme.Current,
            recovery.Status is PortfolioAcquisitionRecoveryStatus.Failed
                ? DalamudUiTone.Warning
                : recovery.Status == PortfolioAcquisitionRecoveryStatus.ReadyToEquip
                    ? DalamudUiTone.Success
                    : DalamudUiTone.Neutral);
        var canRetry = portfolioBuildQueue is null && !session.State.IsBusy &&
                       recovery.Status == PortfolioAcquisitionRecoveryStatus.Failed;
        void Retry()
        {
            if (!canRetry)
                return;
            portfolioRecoveryBuildRequested = false;
            portfolioAcquisitionRecovery = PortfolioAcquisitionRecovery.BeginRebuild(recovery, DateTimeOffset.UtcNow);
            portfolioAcquisitionStatus = portfolioAcquisitionRecovery.Diagnostic;
            SavePortfolioAcquisitionRecovery();
            portfolioRecoveryBuildRequested = BeginPortfolioBuild();
            if (!portfolioRecoveryBuildRequested)
            {
                portfolioAcquisitionRecovery = recovery with
                {
                    Status = PortfolioAcquisitionRecoveryStatus.Failed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Diagnostic = "Portfolio recovery still cannot start because no included target has complete current evidence.",
                };
                SavePortfolioAcquisitionRecovery();
            }
        }
        if (DalamudUiControls.Button(
                "Retry reconciliation##SquirePortfolioAcquisitionResume",
                SquireUiTheme.Current,
                DalamudUiTone.Warning,
                quiet: true,
                enabled: canRetry))
            Retry();
        RegisterLastControl(
            PortfolioAcquisitionReviewedControlIds.Resume,
            "Retry automatic portfolio re-observation and acquisition reconciliation",
            AgentBridgeUiControlKind.Button,
            canRetry,
            false,
            recovery.AuthorityFingerprint.Sha256,
            Retry);
        ImGui.Spacing();
    }

    private void DrawPortfolioEquipmentExecution(OutfitterPortfolioPlan? plan)
    {
        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "EQUIP CHECKLIST");
        var state = equipmentExecution.State;
        if (state is null || state.Status is PortfolioEquipmentExecutionStatus.Completed or PortfolioEquipmentExecutionStatus.UnexecutedSuffixRolledBack)
        {
            var prepared = plan is null || portfolioAuthority is null ? null : ResolvePreparedEquipmentExecution(plan, portfolioAuthority);
            var canPrepare = prepared is not null;
            if (DalamudUiControls.Button("Prepare equip checklist##SquirePortfolioEquipPrepare", SquireUiTheme.Current, DalamudUiTone.Neutral, quiet: true, enabled: canPrepare))
            {
                equipmentExecution.Start(prepared!);
                equipmentExecutionMessage = "Exact owned-item slot checklist prepared. Each slot remains a separate user action.";
            }
            RegisterLastControl(
                "squire.outfitter.portfolio.equip.prepare",
                "Prepare exact owned-item equipment checklist",
                AgentBridgeUiControlKind.Button,
                canPrepare,
                false,
                prepared?.ExecutionId,
                () =>
                {
                    if (prepared is not null)
                    {
                        equipmentExecution.Start(prepared);
                        equipmentExecutionMessage = "Exact owned-item slot checklist prepared. Each slot remains a separate user action.";
                    }
                });
            if (!canPrepare)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted,
                    "No selected owned instance can be equipped yet. Acquire the selected market, vendor, or crafted gear, then reevaluate.");
            return;
        }

        DalamudUiChrome.DrawStatusFact(
            "Checklist",
            $"{state.CompletedStepCount:N0} / {state.Steps.Count:N0} slots · {state.Status}",
            SquireUiTheme.Current.Palette,
            state.Status == PortfolioEquipmentExecutionStatus.StoppedForDrift ? DalamudUiTone.Warning : DalamudUiTone.Neutral);
        if (state.NextStep is { } step)
        {
            ImGui.TextWrapped($"Next: {step.TargetKey} · {step.Position} · {PortfolioItemLabel(step.ExpectedAfter.ItemId)} {(step.ExpectedAfter.IsHighQuality ? "HQ" : "NQ")}");
            var target = state.Targets?.GetValueOrDefault(step.TargetKey);
            if (state.TargetActivations?.GetValueOrDefault(step.TargetKey) is { } activation)
                DalamudUiChrome.DrawStatusFact(
                    "Target proof",
                    activation.Status.ToString(),
                    SquireUiTheme.Current.Palette,
                    activation.Status == EquipmentTargetActivationStatus.Refused ? DalamudUiTone.Warning : DalamudUiTone.Neutral);
            var authorityCurrent = state.AuthorityFingerprint is null ||
                state.AuthorityFingerprint == portfolioAuthority?.Fingerprint;
            var canSubmit = target is not null && authorityCurrent && state.Status is
                (PortfolioEquipmentExecutionStatus.Ready or PortfolioEquipmentExecutionStatus.StoppedForDrift);
            if (DalamudUiControls.Button("Equip this slot##SquirePortfolioEquipNext", SquireUiTheme.Current, DalamudUiTone.Warning, quiet: true, enabled: canSubmit))
                SubmitEquipmentStep(target!);
            RegisterLastControl(
                "squire.outfitter.portfolio.equip.next",
                $"Equip the exact next slot for {step.TargetKey}",
                AgentBridgeUiControlKind.Button,
                canSubmit,
                state.Status == PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence,
                step.StepId,
                () =>
                {
                    if (target is not null)
                        SubmitEquipmentStep(target);
                });
        }
        if (state.Status != PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence)
        {
            ImGui.SameLine();
            if (DalamudUiControls.Button("Remove remaining##SquirePortfolioEquipRollback", SquireUiTheme.Current, DalamudUiTone.Neutral, quiet: true))
                equipmentExecution.RollbackRemaining();
            RegisterLastControl(
                "squire.outfitter.portfolio.equip.rollback",
                "Remove only the unexecuted equipment checklist steps",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                null,
                equipmentExecution.RollbackRemaining);
        }
        if (!string.IsNullOrWhiteSpace(state.StopReason))
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, state.StopReason);
        else if (!string.IsNullOrWhiteSpace(equipmentExecutionMessage))
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, equipmentExecutionMessage);
    }

    private PortfolioEquipmentExecutionState? BuildEquipmentExecution(
        OutfitterPortfolioPlan plan,
        PortfolioAuthorityEnvelope authority)
    {
        if (!authority.HasValidFingerprint() || authority.Fingerprint != PortfolioAuthorityEnvelopeFactory.Create(authority.Lineage, plan).Fingerprint)
            return null;
        var sources = new List<PortfolioEquipmentSourceEvidence>();
        var destinations = new List<PortfolioEquipmentDestinationEvidence>();
        var targetsByKey = new Dictionary<string, PortfolioEquipmentMoveTarget>(StringComparer.Ordinal);
        var activations = new Dictionary<string, EquipmentTargetActivationState>(StringComparer.Ordinal);
        var savedLoadoutExpectations = new Dictionary<string, PortfolioSavedLoadoutExpectation>(StringComparer.Ordinal);
        foreach (var candidate in plan.SelectedCandidates)
        {
            if (!portfolioEvidence.TryGetValue(candidate.TargetKey, out var evaluated) ||
                evaluated.Advice.Baseline is not { Character: { } character, ClassJobId: { } classJobId } baseline ||
                evaluated.Advice.Frontier?.Pareto.Frontier.SingleOrDefault(solution =>
                    string.Equals(solution.Candidate.SolutionId, candidate.CandidateKey, StringComparison.Ordinal)) is not { } solution)
                continue;
            var targetModel = (targets ?? []).SingleOrDefault(value => value.Key == candidate.TargetKey);
            var target = baseline.Target?.Kind switch
            {
                PlayerAdvisorBaselineTargetKind.Retainer => new PortfolioEquipmentMoveTarget(
                    candidate.TargetKey,
                    PortfolioEquipmentMoveTargetKind.Retainer,
                    new(character.LocalContentId, character.Name, character.HomeWorldId),
                    RetainerName: targetModel?.RetainerMetadata?.RetainerName),
                PlayerAdvisorBaselineTargetKind.SavedGearset when baseline.Target.SavedGearset is { } gearset => new PortfolioEquipmentMoveTarget(
                    candidate.TargetKey,
                    PortfolioEquipmentMoveTargetKind.SavedGearset,
                    new(character.LocalContentId, character.Name, character.HomeWorldId),
                    ActiveClassJobId: classJobId,
                    GearsetId: gearset.GearsetId,
                    GearsetName: gearset.GearsetName),
                null => new PortfolioEquipmentMoveTarget(
                    "active-loadout",
                    PortfolioEquipmentMoveTargetKind.ActivePlayer,
                    new(character.LocalContentId, character.Name, character.HomeWorldId),
                    ActiveClassJobId: classJobId),
                _ => null,
            };
            if (target is null)
                continue;
            if (baseline.EquippedSlots.Count != PlayerAdvisorEquippedSlotMap.All.Count ||
                baseline.EquippedSlots.GroupBy(value => value.Position).Any(group => group.Count() != 1) ||
                PlayerAdvisorEquippedSlotMap.All.Any(canonical =>
                    baseline.EquippedSlots.All(value => value.Position != canonical.Position)))
                return null;
            var baselineSlotsByPosition = baseline.EquippedSlots.ToDictionary(value => value.Position);
            targetsByKey[target.TargetKey] = target;
            var activationTarget = target.Kind switch
            {
                PortfolioEquipmentMoveTargetKind.ActivePlayer => new EquipmentTargetActivationTarget(
                    target.TargetKey, EquipmentTargetActivationKind.ActivePlayer, target.Owner, classJobId),
                PortfolioEquipmentMoveTargetKind.SavedGearset => new EquipmentTargetActivationTarget(
                    target.TargetKey, EquipmentTargetActivationKind.SavedGearset, target.Owner, classJobId,
                    target.GearsetId, target.GearsetName),
                _ => new EquipmentTargetActivationTarget(
                    target.TargetKey, EquipmentTargetActivationKind.Retainer, target.Owner, classJobId,
                    RetainerId: targetModel?.RetainerMetadata?.RetainerId,
                    RetainerName: target.RetainerName,
                    OwnerHomeWorldName: targetModel?.RetainerMetadata?.OwnerHomeWorld),
            };
            activations[target.TargetKey] = EquipmentTargetActivationCoordinator.Create(
                $"{Guid.NewGuid():N}:{target.TargetKey}", activationTarget);

            if (target.Kind == PortfolioEquipmentMoveTargetKind.SavedGearset &&
                target.GearsetId is { } gearsetId &&
                target.GearsetName is { } gearsetName)
            {
                var replacements = candidate.SelectedReplacements.ToDictionary(value => value.Position);
                var slots = new List<PortfolioSavedLoadoutSlotExpectation>(PlayerAdvisorEquippedSlotMap.All.Count);
                foreach (var canonical in PlayerAdvisorEquippedSlotMap.All)
                {
                    var address = new PortfolioEquipmentSlotAddress("EquippedItems", canonical.EquippedIndex);
                    PortfolioExactItem? expected;
                    if (replacements.TryGetValue(canonical.Position, out var replacement))
                    {
                        expected = new(
                            replacement.Item.ItemId,
                            replacement.Item.IsHighQuality,
                            PortfolioEquipmentMove.ExactInstanceId(target.Owner, address,
                                new(replacement.Item.ItemId, replacement.Item.IsHighQuality)));
                    }
                    else
                    {
                        var baselineSlot = baselineSlotsByPosition[canonical.Position];
                        if ((baselineSlot.Instance is null) != (baselineSlot.Quality is null))
                            return null;
                        expected = baselineSlot.Instance is null
                            ? null
                            : new(
                                baselineSlot.Instance.Fingerprint.ItemId,
                                baselineSlot.Quality == EquipmentQuality.High,
                                PortfolioEquipmentMove.ExactInstanceId(target.Owner, address,
                                    new(baselineSlot.Instance.Fingerprint.ItemId, baselineSlot.Quality == EquipmentQuality.High)));
                    }
                    slots.Add(new(canonical.Position, expected));
                }
                savedLoadoutExpectations[target.TargetKey] = new(
                    target.TargetKey,
                    gearsetId,
                    gearsetName,
                    classJobId,
                    slots);
            }

            foreach (var replacement in candidate.SelectedReplacements)
            {
                var destinationContainer = target.Kind == PortfolioEquipmentMoveTargetKind.Retainer ? "RetainerEquippedItems" : "EquippedItems";
                var destinationIndex = PlayerAdvisorEquippedSlotMap.All.Single(value => value.Position == replacement.Position).EquippedIndex;
                var address = new PortfolioEquipmentSlotAddress(destinationContainer, destinationIndex);
                var before = baselineSlotsByPosition[replacement.Position];
                var expectedBefore = before?.Instance is null || before.Quality is null
                    ? null
                    : new PortfolioExactItem(
                        before.Instance.Fingerprint.ItemId,
                        before.Quality == EquipmentQuality.High,
                        PortfolioEquipmentMove.ExactInstanceId(target.Owner, address,
                            new(before.Instance.Fingerprint.ItemId, before.Quality == EquipmentQuality.High)));
                destinations.Add(new(target, replacement.Position, address, expectedBefore,
                    baseline.Target?.AuthorityFingerprint ?? baseline.EquipmentSnapshot?.GenerationId.ToString("N") ?? candidate.TargetKey));
            }

            foreach (var demand in candidate.Demands.Where(value => value.Allocation.SourceKind != PortfolioAllocationSourceKind.HandMeDown))
            {
                if (demand.Allocation.SourceKind != PortfolioAllocationSourceKind.OwnedInstance ||
                    demand.Allocation.InstanceId is not { } instanceId ||
                    !TryParseEquipmentInstanceAddress(instanceId, out var address))
                    return null; // Acquisition must complete before any physical equip checklist is prepared.
                sources.Add(new(
                    demand.Allocation,
                    target.Owner,
                    address,
                    new(demand.Allocation.ItemId, demand.Allocation.IsHighQuality, instanceId),
                    demand.Allocation.SourceKey));
            }
        }
        IReadOnlyList<PortfolioOrderedEquipmentSwap> ordered;
        try
        {
            ordered = PortfolioHandMeDownExecutionPlanner.Build(authority, plan, sources, destinations);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        return ordered.Count == 0
            ? null
            : PortfolioEquipmentExecution.Create(
                Guid.NewGuid().ToString("N"),
                ordered.Select(value => value.Step).ToArray(),
                targetsByKey,
                authority.Fingerprint,
                authority,
                activations,
                savedLoadoutExpectations);
    }

    private PortfolioEquipmentExecutionState? ResolvePreparedEquipmentExecution(
        OutfitterPortfolioPlan plan,
        PortfolioAuthorityEnvelope authority)
    {
        if (string.Equals(preparedEquipmentAuthoritySha256, authority.Fingerprint.Sha256, StringComparison.Ordinal))
            return preparedEquipmentExecution;
        preparedEquipmentExecution = BuildEquipmentExecution(plan, authority);
        preparedEquipmentAuthoritySha256 = authority.Fingerprint.Sha256;
        return preparedEquipmentExecution;
    }

    private static bool TryParseEquipmentInstanceAddress(string instanceId, out PortfolioEquipmentSlotAddress address)
    {
        address = null!;
        var parts = instanceId.Split(':');
        if (parts.Length != 5 || !int.TryParse(parts[2], out var slot) || !(parts[1].StartsWith("Inventory", StringComparison.Ordinal) ||
            parts[1].StartsWith("Armory", StringComparison.Ordinal) ||
            string.Equals(parts[1], "EquippedItems", StringComparison.Ordinal) ||
            parts[1].StartsWith("RetainerPage", StringComparison.Ordinal) ||
            string.Equals(parts[1], "RetainerEquippedItems", StringComparison.Ordinal)))
            return false;
        address = new(parts[1], slot);
        return true;
    }

    private void SubmitEquipmentStep(PortfolioEquipmentMoveTarget target)
    {
        var result = equipmentExecution.SubmitNext(target, DateTimeOffset.UtcNow, portfolioAuthority?.Fingerprint);
        equipmentExecutionMessage = result.Message;
        if (target.Kind == PortfolioEquipmentMoveTargetKind.Retainer &&
            result.State?.TargetActivations?.GetValueOrDefault(target.TargetKey)?.Status == EquipmentTargetActivationStatus.AwaitingFreshProof &&
            (targets ?? []).SingleOrDefault(value => value.Key == target.TargetKey) is { } retainerTarget)
            retainerWorkflow.BeginObservation(retainerTarget);
        if (result.Move?.WasSubmitted == true)
            nextEquipmentAfterObservationAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250);
    }

    private void PumpEquipmentAfterObservation()
    {
        var state = equipmentExecution.State;
        if (state?.NextStep is not { } step ||
            state.Targets?.GetValueOrDefault(step.TargetKey) is not { } target)
            return;
        if (state.AuthorityFingerprint is { } persisted && portfolioAuthority?.Fingerprint != persisted)
        {
            equipmentExecutionMessage = portfolioPlanTask is not null || portfolioBuildQueue is not null
                ? "Rebuilding the exact portfolio lineage before interrupted equipment recovery."
                : "The current portfolio lineage is unavailable; equipment recovery remains paused.";
            return;
        }
        if (state.TargetActivations?.GetValueOrDefault(step.TargetKey)?.Status is
            EquipmentTargetActivationStatus.AwaitingFreshProof or EquipmentTargetActivationStatus.Interrupted)
        {
            var activation = equipmentExecution.AdvanceTargetActivation(target, DateTimeOffset.UtcNow);
            equipmentExecutionMessage = activation.Message;
            if (activation.Move?.WasSubmitted == true)
                nextEquipmentAfterObservationAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250);
            return;
        }
        if (state.Status != PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence ||
            DateTimeOffset.UtcNow < nextEquipmentAfterObservationAtUtc)
            return;
        var result = equipmentExecution.NeedsRestartReconciliation
            ? equipmentExecution.ReconcileAfterReload(target, DateTimeOffset.UtcNow, portfolioAuthority?.Fingerprint)
            : equipmentExecution.ObservePending(target, DateTimeOffset.UtcNow, portfolioAuthority?.Fingerprint);
        equipmentExecutionMessage = result.Message;
        nextEquipmentAfterObservationAtUtc = result.State?.Status == PortfolioEquipmentExecutionStatus.AwaitingAfterEvidence
            ? DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100)
            : DateTimeOffset.MaxValue;
    }

    private void DrawPortfolioTargetRow(PortfolioTargetPriority priority)
    {
        var setting = config.Squire.OutfitterPortfolioTargets.Single(value => value.TargetKey == priority.TargetKey);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(priority.TargetLabel);
        ImGui.TableNextColumn();
        var included = setting.Included;
        if (ImGui.Checkbox($"##SquirePortfolioIncluded{priority.TargetKey}", ref included))
        {
            setting.Included = included;
            config.Save();
            InvalidatePortfolioDecision();
        }
        RegisterLastControl(
            $"squire.outfitter.portfolio.target.{priority.TargetKey.Replace(':', '.')}.included",
            $"Include {priority.TargetLabel} in the portfolio",
            AgentBridgeUiControlKind.Toggle,
            true,
            setting.Included,
            setting.Included.ToString(),
            () =>
            {
                setting.Included = !setting.Included;
                config.Save();
                InvalidatePortfolioDecision();
            });
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1f);
        var editedPriority = setting.Priority;
        if (ImGui.InputInt($"##SquirePortfolioPriority{priority.TargetKey}", ref editedPriority, 1, 10))
            SetPortfolioPriority(setting, editedPriority);
        reviewRegistry.RegisterLastAction(
            $"squire.outfitter.portfolio.target.{priority.TargetKey.Replace(':', '.')}.priority",
            $"Set {priority.TargetLabel}'s explicit portfolio priority",
            AgentBridgeUiControlKind.Input,
            true,
            false,
            setting.Priority.ToString(),
            new AgentBridgeActionArgumentSchema([new("priority", AgentBridgeActionArgumentKind.Integer, true, Minimum: 0, Maximum: 10000)]),
            arguments =>
            {
                if (arguments is not { } value || !value.TryGetProperty("priority", out var priorityValue) ||
                    !priorityValue.TryGetInt32(out var requested))
                    return AgentBridgeUiActionResult.Fail("A whole-number priority is required.");
                SetPortfolioPriority(setting, requested);
                return AgentBridgeUiActionResult.Ok($"Priority is now {setting.Priority:N0}.");
            });
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1f);
        var horizon = Enum.TryParse<PortfolioProgressionHorizon>(setting.ProgressionHorizon, out var parsed)
            ? parsed
            : PortfolioProgressionHorizon.NearTerm;
        if (ImGui.BeginCombo($"##SquirePortfolioHorizon{priority.TargetKey}", HorizonLabel(horizon)))
        {
            foreach (var option in Enum.GetValues<PortfolioProgressionHorizon>())
            {
                if (ImGui.Selectable(HorizonLabel(option), option == horizon))
                    SetPortfolioHorizon(setting, option);
            }
            ImGui.EndCombo();
        }
        reviewRegistry.RegisterLastAction(
            $"squire.outfitter.portfolio.target.{priority.TargetKey.Replace(':', '.')}.horizon",
            $"Set {priority.TargetLabel}'s progression horizon",
            AgentBridgeUiControlKind.Select,
            true,
            false,
            setting.ProgressionHorizon,
            new AgentBridgeActionArgumentSchema([new(
                "horizon",
                AgentBridgeActionArgumentKind.Enum,
                true,
                Enum.GetNames<PortfolioProgressionHorizon>())]),
            arguments =>
            {
                var value = arguments is { } root && root.TryGetProperty("horizon", out var horizonValue)
                    ? horizonValue.GetString()
                    : null;
                if (!Enum.TryParse<PortfolioProgressionHorizon>(value, true, out var requested))
                    return AgentBridgeUiActionResult.Fail("Choose Immediate, NearTerm, or LongTerm.");
                SetPortfolioHorizon(setting, requested);
                return AgentBridgeUiActionResult.Ok($"Progression horizon is now {HorizonLabel(requested)}.");
            });
        ImGui.TableNextColumn();
        var target = (targets ?? []).SingleOrDefault(value => value.Key == priority.TargetKey);
        var evidenceReady = portfolioEvidence.ContainsKey(priority.TargetKey);
        var terminal = portfolioTerminalEvidence.GetValueOrDefault(priority.TargetKey);
        var evaluationDiagnostic = portfolioTargetDiagnostics.GetValueOrDefault(priority.TargetKey) ?? terminal?.Reason;
        var evidenceLabel = evidenceReady
            ? "Exact frontier ready"
            : terminal?.Kind switch
            {
                PortfolioTargetDispositionKind.TerminalNoUpgrade => "No upgrade found",
                PortfolioTargetDispositionKind.TerminalAbstention => "Abstained",
                _ when evaluationDiagnostic is not null => "Stopped safely",
                _ when target is { IsReady: false } => "Needs evidence",
                _ => "Not evaluated",
            };
        ImGui.TextColored(
            evidenceReady ? MarketMafiosoUiTheme.Success :
            target is { IsReady: false } || evaluationDiagnostic is not null ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Muted,
            evidenceLabel);
        var diagnostic = evaluationDiagnostic ?? (target is { IsReady: false } ? target.Diagnostic : null);
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(diagnostic))
            ImGui.SetTooltip(diagnostic);
    }

    private void SetPortfolioPriority(OutfitterPortfolioTargetConfiguration setting, int priority)
    {
        setting.Priority = Math.Max(0, priority);
        config.Save();
        InvalidatePortfolioDecision();
    }

    private void SetPortfolioHorizon(
        OutfitterPortfolioTargetConfiguration setting,
        PortfolioProgressionHorizon horizon)
    {
        setting.ProgressionHorizon = horizon.ToString();
        config.Save();
        InvalidatePortfolioDecision();
    }

    private IReadOnlyList<PortfolioTargetPriority> PortfolioPriorities()
    {
        var targetList = new List<(string Key, string Label)> { ("active-loadout", "Current equipped job") };
        targetList.AddRange((targets ?? []).Where(target => target.Kind != OutfitterTargetKind.Job)
            .Select(target => (target.Key, TargetLabel(target))));
        var changed = false;
        var normalized = config.Squire.OutfitterPortfolioTargets
            .Where(value => !string.IsNullOrWhiteSpace(value.TargetKey))
            .GroupBy(value => value.TargetKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (normalized.Count != config.Squire.OutfitterPortfolioTargets.Count)
        {
            config.Squire.OutfitterPortfolioTargets = normalized;
            changed = true;
        }
        foreach (var (key, _) in targetList)
        {
            if (config.Squire.OutfitterPortfolioTargets.All(value => !string.Equals(value.TargetKey, key, StringComparison.Ordinal)))
            {
                config.Squire.OutfitterPortfolioTargets.Add(new()
                {
                    TargetKey = key,
                    Priority = Math.Max(1, 100 - config.Squire.OutfitterPortfolioTargets.Count),
                    ProgressionHorizon = PortfolioProgressionHorizon.NearTerm.ToString(),
                });
                changed = true;
            }
        }
        if (changed)
            config.Save();
        return targetList.Select(value =>
        {
            var setting = config.Squire.OutfitterPortfolioTargets.Single(item => item.TargetKey == value.Key);
            var horizon = Enum.TryParse<PortfolioProgressionHorizon>(setting.ProgressionHorizon, out var parsed)
                ? parsed
                : PortfolioProgressionHorizon.NearTerm;
            return new PortfolioTargetPriority(value.Key, value.Label, setting.Priority, horizon);
        }).ToArray();
    }

    private bool BeginPortfolioBuild()
    {
        if (session.State.IsBusy || portfolioBuildQueue is not null)
            return false;
        EnsureTargets();
        var included = PortfolioPriorities().Where(priority =>
            config.Squire.OutfitterPortfolioTargets.Single(value => value.TargetKey == priority.TargetKey).Included)
            .Select(priority => priority.TargetKey)
            .ToHashSet(StringComparer.Ordinal);
        var uniqueTargets = (included.Contains("active-loadout") ? new OutfitterTarget?[] { null } : [])
            .Concat((targets ?? []).Where(target => target.IsReady && target.Kind != OutfitterTargetKind.Job && included.Contains(target.Key)))
            .ToArray();
        if (uniqueTargets.Length == 0)
            return false;
        portfolioSharedWornItems = session.CapturePortfolioWornItems(uniqueTargets);
        portfolioBuildQueue = new Queue<OutfitterTarget?>(uniqueTargets);
        portfolioEvidence.Clear();
        portfolioTargetDiagnostics.Clear();
        portfolioTerminalEvidence.Clear();
        foreach (var target in (targets ?? []).Where(target => included.Contains(target.Key) && !target.IsReady))
            portfolioTargetDiagnostics[target.Key] = target.Diagnostic ?? "The included target is not ready for exact evaluation.";
        InvalidatePortfolioPlan();
        BeginNextPortfolioTarget();
        return true;
    }

    private void PumpPortfolioBuild()
    {
        if (portfolioBuildQueue is null || portfolioActiveTargetKey is null)
            return;
        var state = session.State;
        if (state.Stage is not (MinerBotanistAdvisorSessionStage.Complete or MinerBotanistAdvisorSessionStage.Abstained or
            MinerBotanistAdvisorSessionStage.Failed or MinerBotanistAdvisorSessionStage.Cancelled))
            return;
        if (state.Stage == MinerBotanistAdvisorSessionStage.Complete && state.Advice is { } advice)
        {
            var adviceTargetKey = advice.Baseline?.Target?.Key ?? "active-loadout";
            if (string.Equals(adviceTargetKey, portfolioActiveTargetKey, StringComparison.Ordinal))
            {
                portfolioEvidence[portfolioActiveTargetKey] = new(
                    portfolioActiveTargetKey,
                    TargetLabel(selectedTarget),
                    advice,
                    session.CurrentEvidence);
                portfolioTerminalEvidence.Remove(portfolioActiveTargetKey);
                portfolioTargetDiagnostics.Remove(portfolioActiveTargetKey);
                InvalidatePortfolioPlan();
            }
            else
            {
                portfolioTargetDiagnostics[portfolioActiveTargetKey] =
                    $"Evaluation returned evidence for {adviceTargetKey}; {portfolioActiveTargetKey} was not published.";
            }
        }
        else if (state.Stage == MinerBotanistAdvisorSessionStage.Abstained)
        {
            portfolioTerminalEvidence[portfolioActiveTargetKey] = new(
                AggregateGeneration("abstention", [portfolioActiveTargetKey, state.UpdatedAtUtc.UtcTicks.ToString(), state.Message]),
                state.Message,
                PortfolioTargetDispositionKind.TerminalAbstention);
            portfolioTargetDiagnostics.Remove(portfolioActiveTargetKey);
        }
        else
            portfolioTargetDiagnostics[portfolioActiveTargetKey] = state.Message;
        BeginNextPortfolioTarget();
    }

    private void BeginNextPortfolioTarget()
    {
        if (portfolioBuildQueue is null)
            return;
        while (portfolioBuildQueue.Count > 0)
        {
            var target = portfolioBuildQueue.Dequeue();
            SelectTarget(target);
            var targetKey = target?.Key ?? "active-loadout";
            if (TryBeginPortfolioTarget(target, out var diagnostic))
            {
                portfolioActiveTargetKey = targetKey;
                return;
            }
            portfolioTargetDiagnostics[targetKey] = diagnostic;
        }
        portfolioBuildQueue = null;
        portfolioActiveTargetKey = null;
        InvalidatePortfolioPlan();
    }

    private bool TryBeginPortfolioTarget(OutfitterTarget? target, out string diagnostic)
    {
        var subject = SubjectForSelectedTarget(captureCharacter());
        var family = subject.IsAvailable && subject.ClassJobId is { } classJobId
            ? AdvisorStatFamilies.Resolve(selectedTarget, classJobId)
            : null;
        if (family is null)
        {
            diagnostic = subject.ClassJobId is { } unsupported
                ? AdvisorStatFamilies.UnsupportedDiagnostic(unsupported)
                : "The target has no current class/job identity; portfolio evaluation abstained.";
            return false;
        }
        var region = resolveRegion();
        session.BeginPortfolio(
            target,
            family.ResolveContext(context.Id),
            string.IsNullOrWhiteSpace(region) ? "North America" : region,
            portfolioSharedWornItems);
        diagnostic = string.Empty;
        return true;
    }

    private void CancelPortfolioBuild()
    {
        portfolioBuildQueue = null;
        portfolioActiveTargetKey = null;
        if (session.State.IsBusy)
            session.Cancel();
    }

    private OutfitterPortfolioPlan? ResolvePortfolioPlan(IReadOnlyList<PortfolioTargetPriority> priorities)
    {
        PumpPortfolioPlan();
        var revision = PortfolioPlanRevision(priorities);
        if (portfolioPlan is not null && string.Equals(portfolioPlanRevision, revision, StringComparison.Ordinal))
            return portfolioPlan;
        if (portfolioPlanTask is not null && string.Equals(pendingPortfolioPlanRevision, revision, StringComparison.Ordinal))
            return null;

        portfolioPlanCancellation?.Cancel();
        portfolioPlanCancellation?.Dispose();
        portfolioPlanCancellation = new();
        pendingPortfolioPlanRevision = revision;
        portfolioPlanFailure = null;
        var frozenPriorities = priorities.Where(priority =>
            config.Squire.OutfitterPortfolioTargets.Single(value => value.TargetKey == priority.TargetKey).Included).ToArray();
        var includedKeys = frozenPriorities.Select(value => value.TargetKey).ToHashSet(StringComparer.Ordinal);
        var frozenEvidence = portfolioEvidence.Values.Where(value => includedKeys.Contains(value.TargetKey)).ToArray();
        var frozenTerminalEvidence = portfolioTerminalEvidence
            .Where(value => includedKeys.Contains(value.Key))
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        var frozenDiagnostics = portfolioTargetDiagnostics
            .Where(value => includedKeys.Contains(value.Key))
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        var token = portfolioPlanCancellation.Token;
        portfolioPlanTask = Task.Run(() => BuildPortfolioPlan(
            frozenPriorities,
            frozenEvidence,
            frozenTerminalEvidence,
            frozenDiagnostics,
            token), token);
        return null;
    }

    private void PumpPortfolioPlan()
    {
        if (portfolioPlanTask is not { IsCompleted: true } completed)
            return;
        portfolioPlanTask = null;
        try
        {
            portfolioPlan = completed.GetAwaiter().GetResult();
            portfolioPlanRevision = pendingPortfolioPlanRevision;
            portfolioPlanFailure = null;
            portfolioAuthority = portfolioPlan.IsComplete ? BuildPortfolioAuthority(portfolioPlan) : null;
            portfolioAcquisitionTransfer = portfolioAuthority is null
                ? null
                : TryBuildPortfolioAcquisition(portfolioPlan, portfolioAuthority);
            if (equipmentExecution.State is { AuthorityFingerprint: { } persisted } &&
                portfolioAuthority?.Fingerprint != persisted)
                equipmentExecution.InvalidateAuthority("The current portfolio evidence or decision no longer matches this equip checklist.");
            CompletePortfolioAcquisitionRecovery();
        }
        catch (OperationCanceledException)
        {
            portfolioPlanFailure = null;
        }
        catch (Exception exception)
        {
            portfolioPlan = null;
            portfolioPlanFailure = $"Exact portfolio planning stopped safely: {exception.Message}";
        }
    }

    private string PortfolioPlanRevision(IReadOnlyList<PortfolioTargetPriority> priorities) => string.Join("|",
        priorities.Select(value => $"{value.TargetKey}:{value.Priority}:{value.ProgressionHorizon}")
            .Concat(portfolioEvidence.OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => $"{value.Key}:{PortfolioEvidenceGeneration(value.Value)}"))
            .Concat(portfolioTerminalEvidence.OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => $"terminal:{value.Key}:{value.Value.EvidenceGeneration}:{value.Value.Kind}"))
            .Concat(portfolioTargetDiagnostics.OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => $"incomplete:{value.Key}:{value.Value}")));

    private void InvalidatePortfolioDecision()
    {
        equipmentExecution.InvalidateAuthority("Portfolio inclusion, priority, or progression horizon changed; the old equip checklist was retired.");
        InvalidatePortfolioPlan();
    }

    private void InvalidatePortfolioPlan()
    {
        portfolioPlanCancellation?.Cancel();
        portfolioPlan = null;
        portfolioAuthority = null;
        portfolioAcquisitionTransfer = null;
        preparedEquipmentExecution = null;
        preparedEquipmentAuthoritySha256 = null;
        portfolioPlanRevision = null;
        pendingPortfolioPlanRevision = null;
    }

    private PortfolioAuthorityEnvelope BuildPortfolioAuthority(OutfitterPortfolioPlan plan)
    {
        var selectedEvidence = plan.OrderedTargets
            .Where(target => portfolioEvidence.ContainsKey(target.TargetKey))
            .Select(target => portfolioEvidence.GetValueOrDefault(target.TargetKey) ??
                throw new InvalidOperationException($"Selected portfolio target {target.TargetKey} has no frozen evidence."))
            .ToArray();
        var owners = selectedEvidence.Select(value => value.Advice.Baseline?.Character)
            .Where(value => value is not null)
            .Distinct()
            .ToArray();
        if (owners.Length != 1 || owners[0] is not { } owner)
            throw new InvalidOperationException("Portfolio authority requires one exact current-character owner.");
        var retainerGenerations = selectedEvidence
            .Where(value => value.Advice.Baseline?.Target?.Kind == PlayerAdvisorBaselineTargetKind.Retainer)
            .Select(value => new PortfolioRetainerEvidenceGeneration(
                value.TargetKey,
                value.Advice.Baseline!.Target!.AuthorityFingerprint))
            .ToArray();
        var lineage = new PortfolioAuthorityLineage(
            $"{owner.LocalContentId}:{owner.HomeWorldId}:{owner.Name}",
            AggregateGeneration("owner", selectedEvidence.Select(PortfolioEvidenceGeneration)),
            AggregateGeneration("inventory", selectedEvidence.Select(value =>
                value.Advice.Baseline?.EquipmentSnapshot?.GenerationId.ToString("N") ?? value.TargetKey)),
            AggregateGeneration("listings", selectedEvidence.SelectMany(value =>
                value.Advice.OffersByAllocation.Values.Select(offer =>
                    offer.ObservationId ?? offer.Offer.SourceCatalogKey ?? "non-listing"))),
            retainerGenerations);
        return PortfolioAuthorityEnvelopeFactory.Create(lineage, plan);
    }

    private static string PortfolioEvidenceGeneration(PortfolioEvaluatedTarget value) => AggregateGeneration(
        value.TargetKey,
        [
            value.Advice.Baseline?.Target?.AuthorityFingerprint ?? string.Empty,
            value.Advice.Baseline?.EquipmentSnapshot?.GenerationId.ToString("N") ?? string.Empty,
            string.Join(",", value.Advice.Frontier?.Pareto.Frontier
                .Select(solution => solution.Candidate.SolutionId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray() ?? []),
        ]);

    private static string AggregateGeneration(string scope, IEnumerable<string> values)
    {
        var canonical = string.Join("\n", values.Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderBy(value => value, StringComparer.Ordinal));
        return $"{scope}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
    }

    private OutfitterPortfolioPlan BuildPortfolioPlan(
        IReadOnlyList<PortfolioTargetPriority> priorities,
        IReadOnlyList<PortfolioEvaluatedTarget> evaluatedTargets,
        IReadOnlyDictionary<string, PortfolioTerminalTargetEvidence> terminalEvidence,
        IReadOnlyDictionary<string, string> incompleteDiagnostics,
        CancellationToken cancellationToken)
    {
        if (priorities.Count == 0)
            return new([], [], [], true, "No targets are included in this portfolio.");
        var candidates = new List<PortfolioCandidate>();
        var planningIncomplete = incompleteDiagnostics.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        var targetsWithValidCandidate = new HashSet<string>(StringComparer.Ordinal);
        var craftProjectionFailures = new Dictionary<string, string>(StringComparer.Ordinal);
        var capacities = new Dictionary<PortfolioAllocationKey, (uint Quantity, DateTimeOffset ObservedAtUtc)>();
        foreach (var evaluated in evaluatedTargets)
        {
            var advice = evaluated.Advice;
            if (advice.Frontier is not { } frontier || advice.Baseline is not { } baseline)
                continue;

            foreach (var solution in frontier.Pareto.Frontier.Where(solution =>
                         advice.AuthorityBySolutionId.TryGetValue(solution.Candidate.SolutionId, out var authority) &&
                         authority.AdvisorMayConsider))
            {
                var demands = new List<PortfolioAllocationDemand>();
                var replacements = new List<PortfolioSelectedReplacement>();
                var handMeDowns = new List<PortfolioHandMeDownEvidence>();
                foreach (var selection in solution.Candidate.Selections)
                {
                    if (!advice.OffersByAllocation.TryGetValue(selection.AllocationKey, out var offer))
                        continue;
                    var instanceId = ExactOfferInstanceId(offer);
                    var replacement = new PortfolioExactItem(
                        offer.Offer.Definition.ItemId,
                        offer.Offer.ResolvedQuality == EquipmentQuality.High,
                        instanceId);
                    replacements.Add(new(selection.Position, replacement));

                    var allocation = PortfolioAllocationFor(offer, instanceId);
                    if (allocation is not null)
                    {
                        demands.Add(new(allocation, selection.Quantity));
                        var observedAt = offer.Offer.GetValidatedObservation()?.ReviewedAt ??
                            baseline.CaptureProvenance?.CompletedAtUtc ?? DateTimeOffset.MinValue;
                        if (!capacities.TryGetValue(allocation, out var current) || observedAt > current.ObservedAtUtc)
                            capacities[allocation] = (offer.AvailableQuantity, observedAt);
                    }

                    var worn = baseline.EquippedSlots.SingleOrDefault(slot => slot.Position == selection.Position);
                    if (worn?.Instance is not { } wornInstance || worn.Quality is not { } wornQuality ||
                        worn.Instance.Fingerprint.ItemId == replacement.ItemId &&
                        wornQuality == (replacement.IsHighQuality ? EquipmentQuality.High : EquipmentQuality.Normal) &&
                        string.Equals(ExactFingerprintInstanceId(wornInstance.Fingerprint), replacement.InstanceId, StringComparison.Ordinal))
                        continue;

                    var released = new PortfolioExactItem(
                        wornInstance.Fingerprint.ItemId,
                        wornQuality == EquipmentQuality.High,
                        ExactFingerprintInstanceId(wornInstance.Fingerprint));
                    var releasedAllocation = new PortfolioAllocationKey(
                        PortfolioAllocationSourceKind.HandMeDown,
                        $"hand-me-down:{evaluated.TargetKey}:{selection.Position}:{released.InstanceId}",
                        released.ItemId,
                        released.IsHighQuality,
                        released.InstanceId);
                    handMeDowns.Add(new(
                        baseline.Target?.AuthorityFingerprint ?? baseline.EquipmentSnapshot?.GenerationId.ToString("N") ?? evaluated.TargetKey,
                        selection.Position,
                        released,
                        replacement,
                        releasedAllocation));
                }
                if (!TryAddPortfolioCraftMaterialDemands(
                        evaluated,
                        solution.Candidate.SolutionId,
                        demands,
                        capacities,
                        out var craftDiagnostic))
                {
                    craftProjectionFailures[evaluated.TargetKey] = craftDiagnostic;
                    continue;
                }
                demands = demands.GroupBy(value => value.Allocation)
                    .Select(group => new PortfolioAllocationDemand(group.Key, group.Aggregate(0u, (sum, value) => checked(sum + value.Quantity))))
                    .ToList();
                candidates.Add(new(
                    evaluated.TargetKey,
                    solution.Candidate.SolutionId,
                    Math.Max(1, checked((long)Math.Round(Math.Max(0.001, solution.Utility.UtilityScore) * 1_000d))),
                    demands,
                    replacements,
                    handMeDowns));
                targetsWithValidCandidate.Add(evaluated.TargetKey);
            }
        }

        foreach (var failure in craftProjectionFailures.Where(value => !targetsWithValidCandidate.Contains(value.Key)))
            planningIncomplete[failure.Key] = failure.Value;

        RemapReleasedOwnedDemands(candidates, priorities, capacities);
        var plan = candidates.Count == 0
            ? new(priorities.OrderByDescending(value => value.Priority).ThenBy(value => value.ProgressionHorizon).ToArray(), [], [])
            : OutfitterPortfolioPlanner.Plan(priorities, candidates,
                capacities.Select(value => new PortfolioAllocationCapacity(value.Key, value.Value.Quantity)).ToArray(),
                cancellationToken);
        var evaluatedDispositions = evaluatedTargets.ToDictionary(
            value => value.TargetKey,
            value => (
                PortfolioEvidenceGeneration(value),
                value.Advice.Nomination is null ? value.Advice.Diagnostic : null,
                PortfolioTargetDispositionKind.TerminalNoUpgrade),
            StringComparer.Ordinal);
        foreach (var terminal in terminalEvidence)
            evaluatedDispositions[terminal.Key] = (terminal.Value.EvidenceGeneration, terminal.Value.Reason, terminal.Value.Kind);
        return PortfolioTargetDispositionResolver.Apply(plan, evaluatedDispositions, planningIncomplete);
    }

    private static bool TryAddPortfolioCraftMaterialDemands(
        PortfolioEvaluatedTarget evaluated,
        string candidateKey,
        List<PortfolioAllocationDemand> demands,
        Dictionary<PortfolioAllocationKey, (uint Quantity, DateTimeOffset ObservedAtUtc)> capacities,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        var advice = evaluated.Advice;
        var hasCraft = advice.Frontier?.Pareto.Frontier.SingleOrDefault(value => value.Candidate.SolutionId == candidateKey)?
            .Candidate.Selections.Any(selection => advice.OffersByAllocation.TryGetValue(selection.AllocationKey, out var offer) &&
                                                   offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.Craft) == true;
        if (!hasCraft)
            return true;
        if (advice.Baseline is null || evaluated.MarketEvidence is null)
        {
            diagnostic = "Craft candidate composition is incomplete because current baseline or market evidence is unavailable.";
            return false;
        }
        try
        {
            var reviewAt = advice.CraftOffersByAllocation.Values
                .Select(value => value.Source.Plan.BuiltAtUtc)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();
            var projection = OutfitterCraftHandoffProjection.Build(
                advice,
                candidateKey,
                advice.Baseline,
                evaluated.MarketEvidence,
                reviewAt);
            foreach (var material in projection.Materials.Where(value => value.ConsumedQuantity > 0))
            {
                var allocation = material.Source switch
                {
                    OutfitterMarketMaterialSourceIdentity market => new PortfolioAllocationKey(
                        PortfolioAllocationSourceKind.MarketListing,
                        $"craft-material-market:{market.WorldName}:{market.ListingId}:{market.SourceRevision}",
                        material.ItemId,
                        material.Quality == EquipmentQuality.High),
                    OutfitterGilVendorMaterialSourceIdentity vendor => new PortfolioAllocationKey(
                        PortfolioAllocationSourceKind.GilVendor,
                        $"craft-material-vendor:{vendor.CatalogVersion}:{vendor.ShopId}:{vendor.VendorId}:{vendor.TerritoryId}:{material.ItemId}",
                        material.ItemId,
                        false),
                    _ => throw new InvalidOperationException("Craft terminal material has no exact portfolio allocation identity."),
                };
                demands.Add(new(allocation, material.ConsumedQuantity));
                var capacity = material.Source switch
                {
                    OutfitterMarketMaterialSourceIdentity market => market.AvailableQuantity,
                    OutfitterGilVendorMaterialSourceIdentity => uint.MaxValue,
                    _ => 0u,
                };
                var observedAt = material.Source is OutfitterMarketMaterialSourceIdentity source
                    ? source.ReviewedAtUtc
                    : reviewAt;
                if (!capacities.TryGetValue(allocation, out var current) || observedAt > current.ObservedAtUtc)
                    capacities[allocation] = (capacity, observedAt);
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            diagnostic = $"Craft candidate composition is incomplete: {exception.Message}";
            return false;
        }
    }

    private static PortfolioAllocationKey? PortfolioAllocationFor(EquipmentExactSolverOffer offer, string instanceId) =>
        offer.Offer.SourceKind switch
        {
            EquipmentAcquisitionSourceKind.Owned => new(
                PortfolioAllocationSourceKind.OwnedInstance,
                offer.Offer.SourceCatalogKey ?? instanceId,
                offer.Offer.Definition.ItemId,
                offer.Offer.ResolvedQuality == EquipmentQuality.High,
                instanceId),
            EquipmentAcquisitionSourceKind.MarketBoard => new(
                PortfolioAllocationSourceKind.MarketListing,
                offer.ObservationId ?? offer.Offer.SourceCatalogKey ?? "missing-market-observation",
                offer.Offer.Definition.ItemId,
                offer.Offer.ResolvedQuality == EquipmentQuality.High),
            EquipmentAcquisitionSourceKind.GilVendor => new(
                PortfolioAllocationSourceKind.GilVendor,
                offer.Offer.SourceCatalogKey ?? offer.Offer.SourceLabel,
                offer.Offer.Definition.ItemId,
                offer.Offer.ResolvedQuality == EquipmentQuality.High),
            EquipmentAcquisitionSourceKind.Craft => new(
                PortfolioAllocationSourceKind.Craft,
                offer.Offer.SourceCatalogKey ?? offer.Offer.SourceLabel,
                offer.Offer.Definition.ItemId,
                offer.Offer.ResolvedQuality == EquipmentQuality.High),
            _ => null,
        };

    private string PortfolioItemLabel(uint itemId) => portfolioEvidence.Values
        .SelectMany(value => value.Advice.OffersByAllocation.Values)
        .Select(value => value.Offer.Definition)
        .FirstOrDefault(value => value.ItemId == itemId)?.Name ?? $"Item {itemId}";

    private static void RemapReleasedOwnedDemands(
        List<PortfolioCandidate> candidates,
        IReadOnlyList<PortfolioTargetPriority> priorities,
        Dictionary<PortfolioAllocationKey, (uint Quantity, DateTimeOffset ObservedAtUtc)> capacities)
    {
        var rank = priorities.OrderByDescending(value => value.Priority)
            .ThenBy(value => value.ProgressionHorizon)
            .ThenBy(value => value.TargetKey, StringComparer.Ordinal)
            .Select((value, index) => (value.TargetKey, Index: index))
            .ToDictionary(value => value.TargetKey, value => value.Index, StringComparer.Ordinal);
        var releases = candidates.SelectMany(candidate => candidate.HandMeDowns.Select(release => (candidate.TargetKey, Release: release)))
            .GroupBy(value => value.Release.ReleasedItem.InstanceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var owned in capacities.Keys.Where(key =>
                     key.SourceKind == PortfolioAllocationSourceKind.OwnedInstance &&
                     key.InstanceId is { } instanceId && releases.ContainsKey(instanceId)).ToArray())
            capacities.Remove(owned);

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            var remapped = candidate.Demands.Select(demand =>
            {
                if (demand.Allocation.SourceKind != PortfolioAllocationSourceKind.OwnedInstance ||
                    demand.Allocation.InstanceId is not { } instanceId ||
                    !releases.TryGetValue(instanceId, out var owners))
                    return demand;
                var upstream = owners.FirstOrDefault(owner => rank[owner.TargetKey] < rank[candidate.TargetKey]);
                return upstream == default ? demand : new PortfolioAllocationDemand(upstream.Release.ReleasedAllocation, demand.Quantity);
            }).ToArray();
            candidates[candidateIndex] = candidate with { Demands = remapped };
        }
    }

    private void DrawPortfolioAllocationRow(PortfolioCandidate candidate)
    {
        var evaluated = portfolioEvidence[candidate.TargetKey];
        string ItemLabel(uint itemId) => evaluated.Advice.OffersByAllocation.Values
            .FirstOrDefault(offer => offer.Offer.Definition.ItemId == itemId)?.Offer.Definition.Name ?? "Exact observed item";
        var presentationConsumers = candidate.HandMeDowns.ToDictionary(
            value => value.ReleasedAllocation,
            value => (IReadOnlyList<string>)(portfolioAuthority?.HandMeDownChain.Where(link =>
                    string.Equals(link.UpstreamTargetKey, candidate.TargetKey, StringComparison.Ordinal) &&
                    link.Allocation == value.ReleasedAllocation)
                .Select(link => portfolioEvidence.TryGetValue(link.DownstreamTargetKey, out var downstream)
                    ? downstream.TargetLabel
                    : "Downstream target")
                .ToArray() ?? []));
        var baseline = evaluated.Advice.Baseline?.EquippedSlots.ToDictionary(
            value => value.Position,
            value => value.Instance is { } instance && value.Quality is { } quality
                ? new PortfolioExactItem(
                    instance.Fingerprint.ItemId,
                    quality == EquipmentQuality.High,
                    ExactFingerprintInstanceId(instance.Fingerprint))
                : null) ?? new Dictionary<EquipmentLoadoutPosition, PortfolioExactItem?>();
        var presentation = PortfolioAllocationPresentationResolver.Resolve(candidate, baseline, ItemLabel, presentationConsumers);
        ImGui.TableNextRow();
        ImGui.TableNextColumn(); ImGui.TextUnformatted(evaluated.TargetLabel);
        ImGui.TableNextColumn(); ImGui.TextColored(MarketMafiosoUiTheme.Header, presentation.SelectedUpgradeText);
        ImGui.TableNextColumn(); ImGui.TextWrapped(presentation.ExactAllocationText);
        ImGui.TableNextColumn(); ImGui.TextColored(MarketMafiosoUiTheme.Success, "Allocated");
    }

    private void DrawPortfolioAcquisition(OutfitterPortfolioPlan plan)
    {
        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "ACQUISITION REVIEW");
        var transfer = portfolioAcquisitionTransfer ??
            (portfolioAuthority is null ? null : TryBuildPortfolioAcquisition(plan, portfolioAuthority));
        if (transfer is null)
        {
            DalamudUiChrome.DrawCallout(
                "SquirePortfolioAcquisitionIncomplete",
                "Acquisition composition incomplete",
                portfolioAcquisitionStatus ?? "Squire could not bind every selected market, vendor, or craft-material demand to current exact evidence. Equipment authority remains blocked.",
                SquireUiTheme.Current,
                DalamudUiTone.Warning);
            return;
        }
        if (transfer.Lines.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted,
                "Every selected allocation is already owned or comes from an explicit hand-me-down.");
            return;
        }

        DalamudUiChrome.DrawStatusFact(
            "Portfolio handoff",
            $"{transfer.Lines.Count:N0} exact non-owned allocation{(transfer.Lines.Count == 1 ? string.Empty : "s")} / {transfer.ObservedMarketTotalGil:N0} gil observed market total",
            SquireUiTheme.Current.Palette,
            DalamudUiTone.Neutral);
        var marketCount = transfer.MarketLots.Count;
        var vendorCount = transfer.VendorActions.Count;
        var craftCount = transfer.ArtisanRecipes.Select(value => (value.TargetKey, value.CandidateKey)).Distinct().Count();
        var hasMarketLots = marketCount > 0;
        var targetLabels = PortfolioPriorities().ToDictionary(
            value => value.TargetKey,
            value => value.TargetLabel,
            StringComparer.Ordinal);
        ImGui.TextWrapped($"Market review: {marketCount:N0} / Vendor actions: {vendorCount:N0} / Craft handoffs: {craftCount:N0}. The Market Workbench reviews exact market lots only; vendor purchases and crafting remain separate explicit actions.");
        if (ImGui.BeginTable(
                "##SquirePortfolioAcquisitionLines",
                4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 76f);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.8f);
            ImGui.TableSetupColumn("Next action", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableHeadersRow();
            foreach (var line in transfer.Lines)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(targetLabels.GetValueOrDefault(line.TargetKey) ?? "Portfolio target");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{line.Priority:N0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{line.ItemName} {(line.Allocation.IsHighQuality ? "HQ" : "NQ")} x{line.Quantity:N0}");
                ImGui.TableNextColumn();
                ImGui.TextWrapped(PortfolioAcquisitionActionLabel(line));
            }
            ImGui.EndTable();
        }
        void Stage()
        {
            try
            {
                if (hasMarketLots)
                    StagePortfolioAcquisition(transfer);
                portfolioAcquisitionRecovery = PortfolioAcquisitionRecovery.Staged(
                    transfer,
                    DateTimeOffset.UtcNow,
                    marketStaged: hasMarketLots);
                // The reviewed handoff has only just begun. Reconcile automatically on a later
                // Portfolio return or plugin restart, not in a hot loop before acquisition occurs.
                portfolioRecoveryBuildRequested = true;
                portfolioAcquisitionStatus = portfolioAcquisitionRecovery.Diagnostic;
                SavePortfolioAcquisitionRecovery();
            }
            catch (Exception exception)
            {
                portfolioAcquisitionStatus = $"Portfolio acquisition stopped safely: {exception.Message}";
            }
        }
        if (DalamudUiControls.Button(
                $"{(hasMarketLots ? "Stage market review" : "Start acquisition checklist")}##SquirePortfolioAcquisitionStage",
                SquireUiTheme.Current,
                DalamudUiTone.Neutral,
                quiet: true))
            Stage();
        RegisterLastControl(
            PortfolioAcquisitionReviewedControlIds.Stage,
            hasMarketLots
                ? "Stage exact market lots in Market Workbench and retain vendor and craft actions in Squire"
                : "Start the reviewed vendor and craft acquisition checklist without opening Market Workbench",
            AgentBridgeUiControlKind.Button,
            true,
            false,
            transfer.AuthorityFingerprint.Sha256,
            Stage);

        DrawPortfolioVendorChecklist(transfer);
        DrawPortfolioArtisanExports(transfer);
        if (!string.IsNullOrWhiteSpace(portfolioAcquisitionStatus ?? portfolioAcquisitionRecovery?.Diagnostic))
            ImGui.TextColored(MarketMafiosoUiTheme.Muted,
                portfolioAcquisitionStatus ?? portfolioAcquisitionRecovery!.Diagnostic);
    }

    private void DrawPortfolioVendorChecklist(PortfolioAcquisitionTransfer transfer)
    {
        if (transfer.VendorActions.Count == 0)
            return;
        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "VENDOR CHECKLIST");
        foreach (var action in transfer.VendorActions)
        {
            var progress = portfolioAcquisitionRecovery?.Progress.SingleOrDefault(value =>
                value.LineageKey == action.LineageKey && value.Kind == PortfolioAcquisitionActionKind.VendorChecklist);
            var confirmed = progress?.Status == PortfolioAcquisitionActionStatus.UserConfirmed;
            ImGui.TextWrapped($"{action.ItemName} NQ x{action.Quantity:N0} / {action.Vendor.VendorName} / {action.Vendor.TerritoryName} / {action.Vendor.UnitPriceGil:N0} gil each");
            ImGui.SameLine();
            var enabled = portfolioAcquisitionRecovery is not null && !confirmed;
            void Confirm()
            {
                if (!enabled || portfolioAcquisitionRecovery is null)
                    return;
                portfolioAcquisitionRecovery = PortfolioAcquisitionRecovery.MarkVendorConfirmed(
                    portfolioAcquisitionRecovery,
                    action.LineageKey,
                    DateTimeOffset.UtcNow);
                portfolioAcquisitionStatus = "Vendor checklist line confirmed; fresh inventory evidence will still decide completion.";
                SavePortfolioAcquisitionRecovery();
            }
            if (DalamudUiControls.Button(
                    $"{(confirmed ? "Confirmed" : "Confirm acquired")}##SquirePortfolioVendor{PortfolioAcquisitionReviewedControlIds.VendorConfirm(action.LineageKey)}",
                    SquireUiTheme.Current,
                    confirmed ? DalamudUiTone.Success : DalamudUiTone.Neutral,
                    quiet: true,
                    enabled: enabled))
                Confirm();
            RegisterLastControl(
                PortfolioAcquisitionReviewedControlIds.VendorConfirm(action.LineageKey),
                $"Confirm user-directed acquisition of exact vendor line {action.ItemName}",
                AgentBridgeUiControlKind.Button,
                enabled,
                confirmed,
                action.LineageKey,
                Confirm);
        }
    }

    private void DrawPortfolioArtisanExports(PortfolioAcquisitionTransfer transfer)
    {
        if (transfer.ArtisanRecipes.Count == 0)
            return;
        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "ARTISAN EXPORTS");
        foreach (var group in transfer.ArtisanRecipes.GroupBy(value => (value.TargetKey, value.CandidateKey)))
        {
            var lineageKey = PortfolioAcquisitionComposition.ArtisanExportLineageKey(
                transfer.AuthorityFingerprint,
                group.Key.TargetKey,
                group.Key.CandidateKey);
            var progress = portfolioAcquisitionRecovery?.Progress.SingleOrDefault(value =>
                value.LineageKey == lineageKey && value.Kind == PortfolioAcquisitionActionKind.ArtisanExport);
            var exported = progress?.Status == PortfolioAcquisitionActionStatus.Exported;
            void Export()
            {
                if (portfolioAcquisitionRecovery is null)
                    return;
                try
                {
                    var result = ArtisanCraftingListExport.Create(
                        $"Squire Portfolio - {group.Key.TargetKey}",
                        group.Select(value => new ArtisanCraftingListRecipeRequest(
                            value.RecipeId,
                            checked((int)value.CraftCount))));
                    ImGui.SetClipboardText(result.Json);
                    var receipt = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Json)));
                    portfolioAcquisitionRecovery = PortfolioAcquisitionRecovery.MarkArtisanExported(
                        portfolioAcquisitionRecovery,
                        lineageKey,
                        receipt,
                        DateTimeOffset.UtcNow);
                    portfolioAcquisitionStatus = $"Copied {result.RecipeCount:N0} reviewed recipes for user-directed Artisan import; crafting was not started.";
                    SavePortfolioAcquisitionRecovery();
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
                {
                    portfolioAcquisitionStatus = $"Portfolio Artisan export stopped safely: {exception.Message}";
                }
            }
            if (DalamudUiControls.Button(
                    $"{(exported ? "Copy Artisan list again" : "Copy Artisan list")}##SquirePortfolioArtisan{PortfolioAcquisitionReviewedControlIds.ArtisanExport(lineageKey)}",
                    SquireUiTheme.Current,
                    DalamudUiTone.Neutral,
                    quiet: true,
                    enabled: portfolioAcquisitionRecovery is not null))
                Export();
            RegisterLastControl(
                PortfolioAcquisitionReviewedControlIds.ArtisanExport(lineageKey),
                $"Copy the full reviewed recipe and subcraft list for {group.Key.TargetKey}",
                AgentBridgeUiControlKind.Button,
                portfolioAcquisitionRecovery is not null,
                exported,
                lineageKey,
                Export);
        }
        ImGui.TextColored(MarketMafiosoUiTheme.Muted,
            "This exports reviewed recipes to the clipboard. It never starts Artisan or claims the crafts completed.");
    }

    private PortfolioAcquisitionTransfer? TryBuildPortfolioAcquisition(
        OutfitterPortfolioPlan plan,
        PortfolioAuthorityEnvelope authority)
    {
        portfolioAcquisitionCompositionFailed = false;
        try
        {
            var evidence = new List<PortfolioAcquisitionEvidenceLine>();
            var craftEvidence = new List<PortfolioCraftAcquisitionEvidence>();
            foreach (var selectedCraft in authority.Allocations
                         .Where(value => value.Allocation.SourceKind == PortfolioAllocationSourceKind.Craft)
                         .Select(value => (value.TargetKey, value.CandidateKey))
                         .Distinct())
            {
                if (!portfolioEvidence.TryGetValue(selectedCraft.TargetKey, out var evaluated))
                    return null;
                var projected = BuildPortfolioCraftEvidence(evaluated, selectedCraft.CandidateKey, authority);
                if (projected is null)
                    return null;
                craftEvidence.Add(projected);
            }
            var craftMaterialAllocations = craftEvidence
                .SelectMany(value => value.Materials.Select(material => (
                    value.TargetKey,
                    value.CandidateKey,
                    Allocation: PortfolioAcquisitionComposition.MaterialAllocation(material))))
                .ToHashSet();
            foreach (var allocation in authority.Allocations.Where(value =>
                         value.Allocation.SourceKind is not (PortfolioAllocationSourceKind.OwnedInstance or PortfolioAllocationSourceKind.HandMeDown) &&
                         !craftMaterialAllocations.Contains((value.TargetKey, value.CandidateKey, value.Allocation))))
            {
                if (!portfolioEvidence.TryGetValue(allocation.TargetKey, out var evaluated) ||
                    evaluated.Advice.Frontier?.Pareto.Frontier.SingleOrDefault(solution =>
                        string.Equals(solution.Candidate.SolutionId, allocation.CandidateKey, StringComparison.Ordinal)) is not { } solution)
                    return null;
                var offers = solution.Candidate.Selections
                    .Select(selection => evaluated.Advice.OffersByAllocation.GetValueOrDefault(selection.AllocationKey))
                    .Where(offer => offer is not null && PortfolioAllocationFor(offer, ExactOfferInstanceId(offer)) == allocation.Allocation)
                    .DistinctBy(offer => offer!.AllocationKey)
                    .Cast<EquipmentExactSolverOffer>()
                    .ToArray();
                if (offers.Length != 1)
                    return null;
                var offer = offers[0];
                var positionDemands = solution.Candidate.Selections
                    .Where(selection => selection.AllocationKey == offer.AllocationKey)
                    .GroupBy(selection => selection.Position)
                    .Select(group => new PortfolioAcquisitionPositionDemand(
                        group.Key,
                        group.Aggregate(0u, (sum, selection) => checked(sum + selection.Quantity))))
                    .ToArray();
                var observation = offer.Offer.GetValidatedObservation();
                var row = observation?.ObservableMarketRow;
                var listing = allocation.Allocation.SourceKind == PortfolioAllocationSourceKind.MarketListing
                    ? evaluated.MarketEvidence?.Items.SingleOrDefault(item => item.ItemId == allocation.Allocation.ItemId)?
                        .Listings.SingleOrDefault(value => string.Equals(value.ListingId, observation?.ObservationId, StringComparison.Ordinal))
                    : null;
                var acquisitionEvidence = new PortfolioAcquisitionEvidenceLine(
                    allocation.TargetKey,
                    allocation.CandidateKey,
                    allocation.Allocation,
                    offer.Offer.Definition.Name,
                    offer.Offer.SourceLabel,
                    positionDemands,
                    offer.AvailableQuantity,
                    row?.UnitPriceGil ?? offer.Offer.UnitPriceGil,
                    authority.Targets.Single(target => target.TargetKey == allocation.TargetKey).EvidenceGeneration,
                    listing?.ListingReviewedAtUtc ?? observation?.ReviewedAt ??
                    evaluated.Advice.Baseline?.CaptureProvenance?.CompletedAtUtc ?? DateTimeOffset.MinValue,
                    observation?.World,
                    observation?.ObservationId,
                    listing?.SourceRevision,
                    listing?.RetainerName,
                    listing?.RetainerId);
                if (allocation.Allocation.SourceKind == PortfolioAllocationSourceKind.GilVendor)
                {
                    if (!OutfitterGilVendorSelectionIdentity.TryDecode(offer.Offer.SourceCatalogKey, out var vendor) || vendor is null)
                        return null;
                    acquisitionEvidence = acquisitionEvidence with
                    {
                        Vendor = new(
                            vendor.ShopId,
                            vendor.VendorId,
                            vendor.TerritoryId,
                            vendor.VendorName,
                            vendor.TerritoryName,
                            vendor.UnitPriceGil,
                            vendor.CatalogVersion),
                    };
                }
                evidence.Add(acquisitionEvidence);
            }
            return PortfolioAcquisitionTransferBuilder.Build(authority, plan, evidence, craftEvidence);
        }
        catch (InvalidOperationException exception)
        {
            portfolioAcquisitionCompositionFailed = true;
            portfolioAcquisitionStatus = $"Portfolio acquisition composition stopped safely: {exception.Message}";
            return null;
        }
    }

    private static PortfolioCraftAcquisitionEvidence? BuildPortfolioCraftEvidence(
        PortfolioEvaluatedTarget evaluated,
        string candidateKey,
        PortfolioAuthorityEnvelope authority)
    {
        if (evaluated.Advice.Baseline is null || evaluated.MarketEvidence is null)
            return null;
        try
        {
            var reviewAt = evaluated.Advice.CraftOffersByAllocation.Values
                .Select(value => value.Source.Plan.BuiltAtUtc)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();
            if (reviewAt == DateTimeOffset.MinValue)
                return null;
            var projection = OutfitterCraftHandoffProjection.Build(
                evaluated.Advice,
                candidateKey,
                evaluated.Advice.Baseline,
                evaluated.MarketEvidence,
                reviewAt);
            var parents = authority.Allocations.Where(value =>
                    string.Equals(value.TargetKey, evaluated.TargetKey, StringComparison.Ordinal) &&
                    string.Equals(value.CandidateKey, candidateKey, StringComparison.Ordinal) &&
                    value.Allocation.SourceKind == PortfolioAllocationSourceKind.Craft)
                .Select(value => value.Allocation)
                .ToArray();
            var recipeSetIdentity = AggregateGeneration("craft-plans", projection.PlanIdentities.Select(value => value.Sha256));
            var recipes = projection.Recipes.Select(recipe => new PortfolioCraftRecipeEvidence(
                recipeSetIdentity,
                recipe.RecipeId,
                recipe.CraftCount,
                recipe.ItemName,
                recipe.Depth)).ToArray();
            var materials = projection.Materials.Select(material =>
            {
                var generation = authority.Targets.Single(value => value.TargetKey == evaluated.TargetKey).EvidenceGeneration;
                return material.Source switch
                {
                    OutfitterMarketMaterialSourceIdentity market => new PortfolioCraftMaterialEvidence(
                        material.PlanIdentity.Sha256, material.ItemId, material.ItemName,
                        material.Quality == EquipmentQuality.High, material.ConsumedQuantity,
                        market.ListingId, PortfolioAllocationSourceKind.MarketListing, market.UnitPriceGil,
                        market.AvailableQuantity, generation, market.ReviewedAtUtc, market.WorldName,
                        market.ListingId, market.SourceRevision,
                        RetainerName: evaluated.MarketEvidence.Items.Single(value => value.ItemId == material.ItemId)
                            .Listings.Single(value => value.ListingId == market.ListingId).RetainerName,
                        RetainerId: evaluated.MarketEvidence.Items.Single(value => value.ItemId == material.ItemId)
                            .Listings.Single(value => value.ListingId == market.ListingId).RetainerId),
                    OutfitterGilVendorMaterialSourceIdentity vendor => new PortfolioCraftMaterialEvidence(
                        material.PlanIdentity.Sha256, material.ItemId, material.ItemName,
                        material.Quality == EquipmentQuality.High, material.ConsumedQuantity,
                        $"vendor:{vendor.ShopId}:{vendor.VendorId}:{vendor.TerritoryId}",
                        PortfolioAllocationSourceKind.GilVendor, vendor.UnitPriceGil, uint.MaxValue,
                        generation, reviewAt,
                        Vendor: new(vendor.ShopId, vendor.VendorId, vendor.TerritoryId, vendor.VendorName,
                            vendor.TerritoryName, vendor.UnitPriceGil, vendor.CatalogVersion)),
                    _ => throw new InvalidOperationException("Portfolio craft material source is unsupported."),
                };
            }).ToArray();
            return new(evaluated.TargetKey, candidateKey, parents, recipes, materials)
            {
                PlanIdentities = projection.PlanIdentities.Select(value => value.Sha256).ToArray(),
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    private void StagePortfolioAcquisition(PortfolioAcquisitionTransfer transfer)
    {
        stageTransfer(PortfolioAcquisitionTransferBuilder.ToWorkbenchTransfer(
            transfer,
            new("SquirePortfolio", "v1"),
            new("GlobalPortfolio", 0, 0, "Global portfolio", []),
            resolveRegion(),
            DateTimeOffset.UtcNow));
    }

    private static string PortfolioAcquisitionActionLabel(PortfolioAcquisitionLine line) =>
        line.Allocation.SourceKind switch
        {
            PortfolioAllocationSourceKind.MarketListing => $"Review exact lot in Market Workbench ({line.SourceLabel})",
            PortfolioAllocationSourceKind.GilVendor => $"Buy explicitly from vendor ({line.SourceLabel})",
            PortfolioAllocationSourceKind.Craft => $"Craft explicitly from frozen plan ({line.SourceLabel})",
            _ => line.SourceLabel,
        };

    private void CompletePortfolioAcquisitionRecovery()
    {
        if (portfolioAcquisitionRecovery?.Status != PortfolioAcquisitionRecoveryStatus.Rebuilding ||
            portfolioPlan is null || portfolioAuthority is null)
            return;
        var transfer = TryBuildPortfolioAcquisition(portfolioPlan, portfolioAuthority);
        if (transfer is null)
        {
            portfolioAcquisitionRecovery = portfolioAcquisitionRecovery with
            {
                Status = PortfolioAcquisitionRecoveryStatus.Failed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Diagnostic = "Fresh portfolio evidence could not reconstruct every exact non-owned allocation.",
            };
        }
        else
        {
            portfolioAcquisitionRecovery = PortfolioAcquisitionRecovery.Reconciled(
                portfolioAcquisitionRecovery,
                portfolioAuthority,
                transfer,
                DateTimeOffset.UtcNow);
        }
        portfolioAcquisitionStatus = portfolioAcquisitionRecovery.Diagnostic;
        SavePortfolioAcquisitionRecovery();
        portfolioRecoveryBuildRequested = false;
        portfolioRecoveryTrigger = PortfolioAcquisitionRecoveryTrigger.Complete(portfolioRecoveryTrigger);
    }

    private void SavePortfolioAcquisitionRecovery()
    {
        config.Squire.OutfitterPortfolioAcquisitionRecoveryStateJson = portfolioAcquisitionRecovery is null
            ? null
            : Newtonsoft.Json.JsonConvert.SerializeObject(portfolioAcquisitionRecovery, Newtonsoft.Json.Formatting.None);
        config.Save();
    }

    private static PortfolioAcquisitionRecoveryState? LoadPortfolioAcquisitionRecovery(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var state = Newtonsoft.Json.JsonConvert.DeserializeObject<PortfolioAcquisitionRecoveryState>(json);
            return state?.SchemaVersion == PortfolioAcquisitionRecoveryState.CurrentSchemaVersion ? state : null;
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private void DrawPortfolioConsequenceRow(
        PortfolioAllocationConsequence consequence,
        IReadOnlyDictionary<string, string> targetLabels)
    {
        var presentation = PortfolioConsequencePresentationResolver.Resolve(consequence, targetLabels, PortfolioItemLabel);
        ImGui.TableNextRow();
        ImGui.TableNextColumn(); ImGui.TextUnformatted(presentation.LosingTargetLabel);
        ImGui.TableNextColumn(); ImGui.TextColored(MarketMafiosoUiTheme.Warning, presentation.ConflictSummary);
        ImGui.TableNextColumn(); ImGui.TextWrapped(presentation.WinnerSummary);
        ImGui.TableNextColumn(); ImGui.TextColored(MarketMafiosoUiTheme.Warning, "Deferred");
    }

    private static string ExactOfferInstanceId(EquipmentExactSolverOffer offer) => offer.Offer.Instance is { } instance
        ? ExactFingerprintInstanceId(instance.Fingerprint)
        : $"{offer.Offer.SourceCatalogKey}:{offer.ObservationId}:{offer.Offer.Definition.ItemId}:{offer.Offer.ResolvedQuality}";

    private static string ExactFingerprintInstanceId(EquipmentInstanceFingerprint fingerprint) =>
        $"{fingerprint.Character.LocalContentId}:{fingerprint.Container}:{fingerprint.SlotIndex}:{fingerprint.ItemId}:{fingerprint.IsHighQuality}";

    private static string HorizonLabel(PortfolioProgressionHorizon value) => value switch
    {
        PortfolioProgressionHorizon.Immediate => "Immediate goal",
        PortfolioProgressionHorizon.NearTerm => "Near-term progression",
        _ => "Long-term progression",
    };

    private bool ResolveRetainerVenture(OutfitterTarget target, string itemName)
    {
        retainerVentureItemName = itemName.Trim();
        retainerVentureResolutionKey = target.Key;
        retainerVentureResolution = retainerWorkflow.ResolveVenture(target, retainerVentureItemName);
        if (retainerVentureResolution.Options.Count != 1)
            return false;
        SelectRetainerVenture(target, retainerVentureResolution.Options[0]);
        return true;
    }

    private void SelectRetainerVenture(OutfitterTarget target, RetainerVentureObjectiveOption option)
    {
        retainerVentureItemName = option.ItemName;
        retainerWorkflow.SelectVenture(target, option);
        EnsureTargets();
    }

    private void EnsureRetainerVentureResolution(OutfitterTarget target)
    {
        if (string.Equals(retainerVentureResolutionKey, target.Key, StringComparison.Ordinal) &&
            retainerVentureResolution is not null)
            return;
        retainerVentureItemName = retainerWorkflow.CaptureVentureItemName(target.Key).Trim();
        retainerVentureResolutionKey = target.Key;
        retainerVentureResolution = string.IsNullOrWhiteSpace(retainerVentureItemName)
            ? null
            : retainerWorkflow.ResolveVenture(target, retainerVentureItemName);
        if (config.Squire.OutfitterRetainerVentureTaskIds.TryGetValue(target.Key, out var taskId) &&
            retainerVentureResolution?.Options.All(option => option.TaskId != taskId) == true)
        {
            targetStatus =
                $"Saved venture task {taskId} no longer matches the current installed definitions for {retainerVentureItemName}; choose one current exact task.";
        }
    }

    private void DrawControls(
        MinerBotanistAdvisorSessionState state,
        AdvisorWorkspacePresentation presentation,
        AdvisorCharacterSubject subject)
    {
        DrawTargetSelector(state);
        ImGui.SameLine();
        var family = subject.ClassJobId is { } classJobId ? AdvisorStatFamilies.Resolve(selectedTarget, classJobId) : null;
        var contexts = family?.ProfileDescriptor.Contexts ?? [];
        var selectedContext = family?.ResolveContext(context.Id) ?? context;
#if DEBUG
        if (s4GoldenFixture is not null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Context · Ordinary crafting benchmark · Blacksmith");
        }
        else
#endif
        if (contexts.Count <= 1)
        {
            DalamudUiChrome.DrawStatusFact("Context", selectedContext.Label, SquireUiTheme.Current.Palette);
        }
        else
        {
            ImGui.SetNextItemWidth(230f);
            if (ImGui.BeginCombo($"{presentation.FamilyLabel} context##SquireAdvisorContext", ContextLabel(selectedContext)))
            {
                foreach (var candidate in contexts)
                {
                    if (ImGui.Selectable(ContextLabel(candidate), candidate.Id == selectedContext.Id))
                        SetContext(candidate);
                }
                ImGui.EndCombo();
            }
            var contextMin = ImGui.GetItemRectMin();
            var contextMax = ImGui.GetItemRectMax();
            foreach (var candidate in contexts)
            {
                var captured = candidate;
                reviewRegistry.Register(
                    AdvisorReviewedControlIds.ContextPrefix + candidate.ConfigurationValue.ToLowerInvariant(),
                    $"Use {ContextLabel(candidate)}",
                    AgentBridgeUiControlKind.Select,
                    contextMin,
                    contextMax,
                    !state.IsBusy,
                    candidate.Id == selectedContext.Id,
                    ContextLabel(candidate),
                    () => SetContext(captured));
            }
        }
        ImGui.SameLine();
        if (state.IsBusy)
        {
            DrawCancelControl();
        }
        else if (state.Stage != MinerBotanistAdvisorSessionStage.Idle)
        {
            if (ImGuiUi.Button($"{presentation.PrimaryActionLabel}##SquireAdvisor", presentation.CanEvaluate))
                Begin();
            RegisterLastControl(
                AdvisorReviewedControlIds.Refresh,
                presentation.PrimaryActionReviewLabel,
                AgentBridgeUiControlKind.Button,
                presentation.CanEvaluate,
                false,
                null,
                Begin);
        }
#if DEBUG
        if (!state.IsBusy && config.EnableMarketAcquisitionDryRunTools)
        {
            ImGui.SameLine();
            var label = syntheticReviewAdvice is null
                ? "Load synthetic review##SquireAdvisorSynthetic"
                : "Return to live view##SquireAdvisorSynthetic";
            if (DalamudUiControls.Button(label, SquireUiTheme.Current, DalamudUiTone.Neutral, quiet: true))
                ToggleSyntheticReview();
            RegisterLastControl(
                AdvisorReviewedControlIds.SyntheticReview,
                syntheticReviewAdvice is null ? "Load synthetic advisor review" : "Return to live advisor view",
                AgentBridgeUiControlKind.Button,
                true,
                syntheticReviewAdvice is not null,
                syntheticReviewAdvice is null ? "live" : "synthetic",
                ToggleSyntheticReview);
        }
        if (syntheticReviewAdvice is not null && s4GoldenFixture is null)
        {
            DrawSyntheticScenarioControl();
            var canBuildDryRunFixture = config.EnableMarketAcquisitionDryRunTools && dryRunFixtureTask is null;
            if (DalamudUiControls.Button(
                    "Build current-listing dry run##SquireAdvisorDryRun",
                    SquireUiTheme.Current,
                    DalamudUiTone.Neutral,
                    quiet: true,
                    enabled: canBuildDryRunFixture))
                BeginDryRunFixture();
            RegisterLastControl(
                "squire.outfitter.advisor.build-dry-run-fixture",
                "Build a current-listing Squire integration fixture restricted to dry-run execution",
                AgentBridgeUiControlKind.Button,
                canBuildDryRunFixture,
                dryRunFixtureTask is not null,
                dryRunFixture is null ? "not-built" : "ready",
                BeginDryRunFixture);
            if (!string.IsNullOrWhiteSpace(dryRunFixtureStatus))
                ImGui.TextColored(dryRunFixture is null ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Success, dryRunFixtureStatus);
            if (syntheticScenarioKind == MinerBotanistAdvisorSyntheticScenarioKind.Abstention)
                return;
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "PLOT SERIES");
            ImGui.SameLine();
            foreach (var candidate in ContextOrder)
            {
                var captured = candidate;
                var visible = visibleSyntheticContexts.Contains(candidate);
                var canToggle = !visible || visibleSyntheticContexts.Count > 1;
                if (!canToggle)
                    ImGui.BeginDisabled();
                if (DalamudUiControls.SegmentedOption(
                        $"{ContextSeriesLabel(candidate)}##SquireAdvisorSyntheticSeries{candidate}",
                        visible,
                        SquireUiTheme.Current))
                    SetSyntheticSeriesVisible(candidate, !visible);
                if (!canToggle)
                    ImGui.EndDisabled();
                RegisterLastControl(
                    $"squire.outfitter.advisor.synthetic-series.{ContextSeriesId(candidate)}",
                    $"{(visible ? "Hide" : "Show")} {ContextLabel(candidate)} plot series",
                    AgentBridgeUiControlKind.Toggle,
                    canToggle,
                    visible,
                    visible ? "visible" : "hidden",
                    () => ToggleSyntheticSeries(captured));
                if (candidate != ContextOrder[^1])
                    ImGui.SameLine();
            }
        }
#endif
    }

    private void DrawTargetSelector(MinerBotanistAdvisorSessionState state)
    {
        EnsureTargets();
        ImGui.SetNextItemWidth(230f);
        var preview = TargetLabel(selectedTarget);
        if (ImGui.BeginCombo("Target##SquireAdvisorTarget", preview))
        {
            ImGui.TextDisabled("PLAYER TARGETS");
            if (ImGui.Selectable("Current equipped job", selectedTarget is null) && !state.IsBusy)
                SelectTarget(null);
            foreach (var target in (targets ?? []).Where(value => value.Kind != OutfitterTargetKind.Retainer))
                DrawTargetSelectorOption(target, state);
            foreach (var ownerGroup in (targets ?? [])
                         .Where(value => value.Kind == OutfitterTargetKind.Retainer)
                         .GroupBy(value => $"{value.OwnerCharacterName} @ {value.OwnerHomeWorld}", StringComparer.Ordinal)
                         .OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                ImGui.Separator();
                ImGui.TextDisabled(ownerGroup.Key.ToUpperInvariant());
                foreach (var target in ownerGroup.OrderBy(value => value.Name, StringComparer.Ordinal))
                    DrawTargetSelectorOption(target, state, withinOwnerGroup: true);
            }
            ImGui.EndCombo();
        }
        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        reviewRegistry.Register(
            "squire.outfitter.target.active",
            "Evaluate the current equipped job",
            AgentBridgeUiControlKind.Select,
            minimum,
            maximum,
            !state.IsBusy,
            selectedTarget is null,
            "active-loadout",
            () => SelectTarget(null));
        foreach (var target in targets ?? [])
        {
            var captured = target;
            var enabled = !state.IsBusy && (target.Kind == OutfitterTargetKind.Retainer
                ? target.IsCurrentCharacter && target.RetainerMetadata is not null
                : target.IsReady);
            reviewRegistry.Register(
                $"squire.outfitter.target.{target.Key.Replace(':', '.')}",
                $"Evaluate {target.Name}: {target.Subtitle}",
                AgentBridgeUiControlKind.Select,
                minimum,
                maximum,
                enabled,
                selectedTarget?.Key == target.Key,
                target.Key,
                () => SelectTarget(captured));
        }
        if (!string.IsNullOrWhiteSpace(targetStatus))
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, targetStatus);
    }

    private void DrawTargetSelectorOption(
        OutfitterTarget target,
        MinerBotanistAdvisorSessionState state,
        bool withinOwnerGroup = false)
    {
        var enabled = !state.IsBusy && (target.Kind == OutfitterTargetKind.Retainer
            ? target.IsCurrentCharacter && target.RetainerMetadata is not null
            : target.IsReady);
        if (!enabled)
            ImGui.BeginDisabled();
        var label = withinOwnerGroup ? $"{target.Name} · {target.Subtitle}" : TargetLabel(target);
        if (ImGui.Selectable($"{label}##{target.Key}", selectedTarget?.Key == target.Key) && enabled)
            SelectTarget(target);
        if (!enabled)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(target.Diagnostic))
            ImGui.SetTooltip(target.Diagnostic);
    }

    private void EnsureTargets()
    {
        try
        {
            var captured = captureTargets();
            if (selectedTarget is not null)
            {
                var refreshed = captured.SingleOrDefault(value =>
                    string.Equals(value.Key, selectedTarget.Key, StringComparison.Ordinal));
                if (refreshed is not null)
                    selectedTarget = refreshed;
            }
            targets = captured;
            targetStatus = null;
        }
        catch (Exception exception)
        {
            targets = [];
            targetStatus = $"Target discovery stopped safely: {exception.Message}";
        }
    }

    internal string SelectedTargetKey => selectedTarget?.Key ?? "active-loadout";
    internal string SelectedTargetKind => selectedTarget?.Kind.ToString() ?? "ActiveLoadout";
    internal string SelectedTargetLabel => TargetLabel(selectedTarget);
    internal int TargetCount
    {
        get
        {
            EnsureTargets();
            return 1 + (targets?.Count ?? 0);
        }
    }
    internal int ReadyTargetCount
    {
        get
        {
            EnsureTargets();
            return 1 + (targets?.Count(target => target.IsReady) ?? 0);
        }
    }
    internal bool PortfolioMode => showPortfolio;
    internal string PortfolioBuildStage => portfolioBuildQueue is not null
        ? "EvaluatingTargets"
        : portfolioPlanTask is not null
            ? "Planning"
            : portfolioPlanFailure is not null
                ? "Failed"
                : portfolioPlan is null ? "Idle" : "Complete";
    internal int PortfolioEvaluatedTargetCount => portfolioEvidence.Count;
    internal OutfitterPortfolioPlan? PortfolioPlan => portfolioPlan;
    internal PortfolioAuthorityEnvelope? PortfolioAuthority => portfolioAuthority;
    internal PortfolioEquipmentExecutionState? EquipmentExecution => equipmentExecution.State;
    private PortfolioAcquisitionTransfer? CurrentPortfolioAcquisitionTransfer =>
        portfolioAcquisitionRecovery?.Transfer ?? portfolioAcquisitionTransfer;
    internal string? PortfolioAcquisitionStatus => PortfolioAcquisitionBridgeProjection.Status(
        portfolioAcquisitionRecovery,
        portfolioAcquisitionTransfer,
        portfolioAcquisitionCompositionFailed);
    internal int PortfolioAcquisitionLineCount => CurrentPortfolioAcquisitionTransfer?.Lines.Count ?? 0;
    internal int PortfolioMarketLineCount => CurrentPortfolioAcquisitionTransfer?.MarketLots.Count ?? 0;
    internal int PortfolioVendorActionCount => CurrentPortfolioAcquisitionTransfer?.VendorActions.Count ?? 0;
    internal int PortfolioCraftHandoffCount => CurrentPortfolioAcquisitionTransfer?.ArtisanRecipes
        .Select(value => (value.TargetKey, value.CandidateKey)).Distinct().Count() ?? 0;
    internal string? PortfolioAcquisitionAuthoritySha256 => CurrentPortfolioAcquisitionTransfer?.AuthorityFingerprint.Sha256;
    internal string? PortfolioAcquisitionDiagnostic => PortfolioAcquisitionBridgeProjection.Diagnostic(
        portfolioAcquisitionRecovery,
        portfolioAcquisitionTransfer,
        portfolioAcquisitionStatus);
    internal bool PortfolioAcquisitionStageReachable => portfolioAcquisitionTransfer is { Lines.Count: > 0 } &&
        portfolioAcquisitionRecovery is null;
    internal bool PortfolioAcquisitionResumeReachable => PortfolioAcquisitionBridgeProjection.ResumeReachable(
        portfolioAcquisitionRecovery,
        portfolioBuildQueue is null,
        session.State.IsBusy);
    internal bool PortfolioVendorConfirmReachable => portfolioAcquisitionRecovery?.Progress.Any(value =>
        value.Kind == PortfolioAcquisitionActionKind.VendorChecklist &&
        value.Status == PortfolioAcquisitionActionStatus.Pending) == true;
    internal bool PortfolioArtisanExportReachable => portfolioAcquisitionRecovery?.Progress.Any(value =>
        value.Kind == PortfolioAcquisitionActionKind.ArtisanExport) == true;
    internal IReadOnlyList<SquireBridgePortfolioAcquisitionLineTruth> PortfolioAcquisitionLineTruth
    {
        get
        {
            var transfer = CurrentPortfolioAcquisitionTransfer;
            if (transfer is null)
                return [];
            var progress = portfolioAcquisitionRecovery?.Progress.ToDictionary(value => value.LineageKey, StringComparer.Ordinal) ?? [];
            var markets = transfer.MarketLots.Select(value => Truth(
                value.LineageKey, "Market", value.TargetKey, value.CandidateKey, value.ItemId, value.ItemName,
                value.RequiredQuantity, "Review in Market Workbench", progress));
            var vendors = transfer.VendorActions.Select(value => Truth(
                value.LineageKey, "Vendor", value.TargetKey, value.CandidateKey, value.ItemId, value.ItemName,
                value.Quantity, $"Acquire from {value.Vendor.VendorName} in {value.Vendor.TerritoryName}", progress));
            var artisan = transfer.ArtisanRecipes.GroupBy(value => (value.TargetKey, value.CandidateKey)).Select(group =>
            {
                var lineage = PortfolioAcquisitionComposition.ArtisanExportLineageKey(
                    transfer.AuthorityFingerprint, group.Key.TargetKey, group.Key.CandidateKey);
                return Truth(lineage, "Craft", group.Key.TargetKey, group.Key.CandidateKey, 0,
                    $"{group.Count():N0} recipe Artisan list", (uint)group.Sum(value => value.CraftCount),
                    "Copy reviewed Artisan list", progress);
            });
            return markets.Concat(vendors).Concat(artisan).ToArray();
        }
    }

    private static SquireBridgePortfolioAcquisitionLineTruth Truth(
        string lineageKey,
        string sourceKind,
        string targetKey,
        string candidateKey,
        uint itemId,
        string itemName,
        uint quantity,
        string nextAction,
        IReadOnlyDictionary<string, PortfolioAcquisitionActionProgress> progress)
    {
        progress.TryGetValue(lineageKey, out var state);
        nextAction = state switch
        {
            { Kind: PortfolioAcquisitionActionKind.MarketReview, Status: PortfolioAcquisitionActionStatus.StagedForReview } =>
                "Complete the reviewed Market Workbench route; Squire will reconcile fresh inventory on return",
            { Kind: PortfolioAcquisitionActionKind.VendorChecklist, Status: PortfolioAcquisitionActionStatus.UserConfirmed } =>
                "Await fresh inventory evidence; confirmation is not acquisition proof",
            { Kind: PortfolioAcquisitionActionKind.ArtisanExport, Status: PortfolioAcquisitionActionStatus.Exported } =>
                "Craft from the reviewed Artisan list; Squire will reconcile fresh inventory on return",
            _ => nextAction,
        };
        return new(lineageKey, sourceKind, state?.Status.ToString() ?? "ReadyForReview", targetKey,
            candidateKey, itemId, itemName, quantity, nextAction, state?.Receipt);
    }
    internal IReadOnlyList<SquireBridgePortfolioTargetTruth> PortfolioTargetTruth
    {
        get
        {
            EnsureTargets();
            var authorityTargets = (portfolioAuthority?.Targets ?? equipmentExecution.State?.AuthorityEnvelope?.Targets ?? [])
                .ToDictionary(value => value.TargetKey, StringComparer.Ordinal);
            return PortfolioPriorities().Select(priority =>
            {
                var setting = config.Squire.OutfitterPortfolioTargets.Single(value => value.TargetKey == priority.TargetKey);
                var target = (targets ?? []).SingleOrDefault(value => value.Key == priority.TargetKey);
                authorityTargets.TryGetValue(priority.TargetKey, out var authority);
                return new SquireBridgePortfolioTargetTruth(
                    priority.TargetKey,
                    setting.Included,
                    priority.Priority,
                    priority.ProgressionHorizon.ToString(),
                    authority?.SelectedCandidateKey,
                    portfolioEvidence.ContainsKey(priority.TargetKey),
                    portfolioTargetDiagnostics.GetValueOrDefault(priority.TargetKey) ??
                    (target is { IsReady: false } ? target.Diagnostic : null),
                    authority?.Disposition.ToString() ?? portfolioPlan?.DispositionFor(priority.TargetKey)?.Kind.ToString(),
                    authority?.DispositionReason ?? portfolioPlan?.DispositionFor(priority.TargetKey)?.Reason,
                    authority?.EvidenceGeneration ?? portfolioPlan?.DispositionFor(priority.TargetKey)?.EvidenceGeneration);
            }).ToArray();
        }
    }
    internal IReadOnlyList<SquireBridgePortfolioAllocationTruth> PortfolioAllocationTruth =>
        (portfolioAuthority?.Allocations ?? equipmentExecution.State?.AuthorityEnvelope?.Allocations ?? [])
        .Select(value => new SquireBridgePortfolioAllocationTruth(
            value.TargetKey,
            value.CandidateKey,
            value.Allocation.SourceKind.ToString(),
            value.Allocation.SourceKey,
            value.Allocation.ItemId,
            value.Allocation.IsHighQuality,
            value.Allocation.InstanceId,
            value.Quantity))
        .ToArray();
    internal IReadOnlyList<SquireBridgePortfolioHandMeDownTruth> PortfolioHandMeDownTruth =>
        (portfolioAuthority?.HandMeDownChain ?? equipmentExecution.State?.AuthorityEnvelope?.HandMeDownChain ?? [])
        .Select(value => new SquireBridgePortfolioHandMeDownTruth(
            value.UpstreamTargetKey,
            value.DownstreamTargetKey,
            value.ReleaseEvidenceGeneration,
            value.ReleasedItem.ItemId,
            value.ReleasedItem.IsHighQuality,
            value.ReleasedItem.InstanceId))
        .ToArray();

    private static string TargetLabel(OutfitterTarget? target) => target is null
        ? "Current equipped job"
        : target.Kind == OutfitterTargetKind.Retainer
            ? $"{target.OwnerCharacterName} / {target.Name} · {target.Subtitle}"
            : $"{target.Name} · {target.Subtitle}";

    private void SelectTarget(OutfitterTarget? target)
    {
        if (session.State.IsBusy || target is { Kind: not OutfitterTargetKind.Retainer, IsReady: false } ||
            target is { Kind: OutfitterTargetKind.Retainer } retainer &&
            (!retainer.IsCurrentCharacter || retainer.RetainerMetadata is null))
            return;
        if (string.Equals(selectedTarget?.Key, target?.Key, StringComparison.Ordinal))
            return;
        selectedTarget = target;
        retainerVentureItemName = target is { Kind: OutfitterTargetKind.Retainer }
            ? retainerWorkflow.CaptureVentureItemName(target.Key)
            : string.Empty;
        retainerVentureResolutionKey = target?.Key;
        retainerVentureResolution = target is { Kind: OutfitterTargetKind.Retainer } retainerTarget &&
            !string.IsNullOrWhiteSpace(retainerVentureItemName)
                ? retainerWorkflow.ResolveVenture(retainerTarget, retainerVentureItemName)
                : null;
        session.InvalidateForPlayerStateChange();
        var subject = SubjectForSelectedTarget(captureCharacter());
        var family = subject.ClassJobId is { } classJobId ? AdvisorStatFamilies.Resolve(selectedTarget, classJobId) : null;
        if (family is not null)
            context = family.ProfileDescriptor.DefaultContext;
        lastAdvice = null;
        selectedSolutionId = null;
        handoffStatus = null;
    }

    private AdvisorCharacterSubject SubjectForSelectedTarget(AdvisorCharacterSubject active)
    {
        if (selectedTarget?.Job is not { } job)
            return active;
        return new(
            true,
            job.ClassJobId,
            job.Abbreviation,
            checked((short)job.Level),
            selectedTarget.Kind == OutfitterTargetKind.Retainer
                ? $"retainer '{selectedTarget.Name}' ({job.Abbreviation})"
                : $"saved gearset '{selectedTarget.Name}' ({job.Abbreviation})");
    }

#if DEBUG
    private void DrawSyntheticReviewStatus(MinerBotanistAdvisorSyntheticPresentation presentation)
    {
        var title = s4GoldenFixture is not null
            ? "Frozen craft handoff review"
            : dryRunFixture is null
                ? "Deterministic advisor review"
                : "Current-listing route review";
        var detail = s4GoldenFixture is not null
            ? "Review production recipe, Artisan export, and material-only Workbench boundaries without purchase or crafting."
            : dryRunFixture?.Diagnostic ??
              "Review frozen model decisions and evidence without using the active character or current market listings.";
        DalamudUiChrome.DrawCallout(
            "SquireAdvisorSyntheticReview",
            title,
            detail,
            SquireUiTheme.Current,
            DalamudUiTone.Warning);
        ImGui.Spacing();
        var tone = presentation.Stage switch
        {
            MinerBotanistAdvisorSessionStage.Complete => DalamudUiTone.Success,
            MinerBotanistAdvisorSessionStage.Abstained => DalamudUiTone.Warning,
            _ => DalamudUiTone.Neutral,
        };
        DalamudUiChrome.DrawStatusFact(
            "Evidence",
            presentation.AdviceIsRetained
                ? $"Last valid frontier · {presentation.Label}"
                : presentation.Label,
            SquireUiTheme.Current.Palette,
            tone);
        if (presentation.ShowProgress)
            ImGui.ProgressBar(
                (float)presentation.Completed / presentation.Total,
                new Vector2(-1, 0),
                $"{presentation.Completed:N0} / {presentation.Total:N0}");
        using (ImRaii.PushColor(ImGuiCol.Text, StatusColor(presentation.Stage)))
            ImGui.TextWrapped(presentation.Message);
        if (dryRunFixture is null && s4GoldenFixture is null)
        {
            using var muted = ImRaii.PushColor(ImGuiCol.Text, MarketMafiosoUiTheme.Muted);
            ImGui.TextWrapped(MinerBotanistAdvisorSyntheticReview.PriceEvidenceLabel);
        }
    }
#endif

    private void Begin()
    {
        var subject = SubjectForSelectedTarget(captureCharacter());
        var family = subject.IsAvailable && subject.ClassJobId is { } classJobId
            ? AdvisorStatFamilies.Resolve(selectedTarget, classJobId)
            : null;
        if (family is null)
            return;
#if DEBUG
        syntheticReviewAdvice = null;
        s4GoldenFixture = null;
#endif
        handoffStatus = null;
        var region = resolveRegion();
        var selectedContext = family.ResolveContext(context.Id);
        if (selectedTarget is null)
            session.Begin(selectedContext, string.IsNullOrWhiteSpace(region) ? "North America" : region);
        else
            session.Begin(selectedTarget, selectedContext, string.IsNullOrWhiteSpace(region) ? "North America" : region);
    }

    private void SetContext(AdvisorUtilityContextDescriptor value)
    {
        if (session.State.IsBusy)
            return;
        var subject = SubjectForSelectedTarget(captureCharacter());
#if DEBUG
        if (syntheticReviewAdvice is not null)
            subject = new(true, MinerBotanistUtilityProfile.MinerClassJobId, "MIN", 100);
#endif
        var family = subject.IsAvailable && subject.ClassJobId is { } classJobId
            ? AdvisorStatFamilies.Resolve(selectedTarget, classJobId)
            : null;
        if (family is null || family.ProfileDescriptor.Contexts.All(candidate => candidate.Id != value.Id))
            return;
#if DEBUG
        if (dryRunFixtureTask is not null)
            return;
#endif
        context = value;
        config.Squire.OutfitterAdvisorContext = value.ConfigurationValue;
        config.Save();
        lastAdvice = null;
        selectedSolutionId = null;
        handoffStatus = null;
#if DEBUG
        if (syntheticReviewAdvice is not null)
        {
            s4GoldenFixture = null;
            dryRunFixture = null;
            dryRunFixtureStatus = null;
            syntheticReviewAdvice = BuildSyntheticReview(context);
            visibleSyntheticContexts.Add(context);
        }
#endif
    }

#if DEBUG
    private void ToggleSyntheticReview()
    {
        s4GoldenFixture = null;
        dryRunFixture = null;
        dryRunFixtureTask = null;
        dryRunFixtureStatus = null;
        syntheticReviewAdvice = syntheticReviewAdvice is null
            ? BuildSyntheticReview(context)
            : null;
        ResetVisibleSyntheticContexts();
        syntheticScenarioKind = MinerBotanistAdvisorSyntheticScenarioKind.Success;
        lastAdvice = null;
        selectedSolutionId = null;
    }

    public void LoadSyntheticReview()
    {
        dryRunFixture = null;
        dryRunFixtureTask = null;
        dryRunFixtureStatus = null;
        s4GoldenFixture = OutfitterS4GoldenFixture.Create();
        syntheticReviewAdvice = s4GoldenFixture.Advice;
        ResetVisibleSyntheticContexts();
        syntheticScenarioKind = MinerBotanistAdvisorSyntheticScenarioKind.Success;
        lastAdvice = null;
        selectedSolutionId = s4GoldenFixture.SelectedCraftSolutionId;
    }

    private void BeginDryRunFixture()
    {
        if (!config.EnableMarketAcquisitionDryRunTools || dryRunFixtureTask is not null)
            return;
        var region = config.ActiveMarketAcquisitionRequestDocument?.Region;
        if (string.IsNullOrWhiteSpace(region))
            region = config.ActiveMarketAcquisitionClaim?.Region;
        dryRunFixture = null;
        dryRunFixtureStatus = "Fetching one complete current listing generation for the marketable gathering set...";
        dryRunFixtureTask = MinerBotanistAdvisorSyntheticReview.BuildDryRunFixtureAsync(
            listingSource,
            string.IsNullOrWhiteSpace(region) ? "North America" : region,
            GathererAdvisorStatFamily.ContextKindFor(context.Id));
    }

    private void PumpDryRunFixture()
    {
        if (dryRunFixtureTask is not { IsCompleted: true } completed)
            return;
        dryRunFixtureTask = null;
        try
        {
            dryRunFixture = completed.GetAwaiter().GetResult();
            syntheticReviewAdvice = dryRunFixture.Advice;
            syntheticScenarioKind = MinerBotanistAdvisorSyntheticScenarioKind.Success;
            lastAdvice = null;
            selectedSolutionId = dryRunFixture.SelectedSolutionId;
            dryRunFixtureStatus = dryRunFixture.Diagnostic;
        }
        catch (Exception exception)
        {
            dryRunFixture = null;
            dryRunFixtureStatus = $"Live dry-run fixture stopped safely: {exception.Message}";
        }
    }

    private void ResetVisibleSyntheticContexts()
    {
        visibleSyntheticContexts.Clear();
        visibleSyntheticContexts.Add(context);
    }

    private void DrawSyntheticScenarioControl()
    {
        ImGui.SetNextItemWidth(230f);
        if (ImGui.BeginCombo("Evidence state##SquireAdvisorSyntheticScenario", SyntheticScenarioLabel(syntheticScenarioKind)))
        {
            foreach (var candidate in SyntheticScenarioOrder)
            {
                if (ImGui.Selectable(SyntheticScenarioLabel(candidate), candidate == syntheticScenarioKind))
                    SetSyntheticScenario(candidate);
            }
            ImGui.EndCombo();
        }
        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        foreach (var candidate in SyntheticScenarioOrder)
        {
            var captured = candidate;
            reviewRegistry.Register(
                $"squire.outfitter.advisor.synthetic-scenario.{SyntheticScenarioId(candidate)}",
                $"Show {SyntheticScenarioLabel(candidate)} advisor evidence state",
                AgentBridgeUiControlKind.Select,
                minimum,
                maximum,
                true,
                candidate == syntheticScenarioKind,
                SyntheticScenarioLabel(candidate),
                () => SetSyntheticScenario(captured));
        }
    }

    private void SetSyntheticScenario(MinerBotanistAdvisorSyntheticScenarioKind value)
    {
        syntheticScenarioKind = value;
        lastAdvice = null;
        selectedSolutionId = null;
    }

    private static string SyntheticScenarioLabel(MinerBotanistAdvisorSyntheticScenarioKind value) => value switch
    {
        MinerBotanistAdvisorSyntheticScenarioKind.Refreshing => "Refreshing with prior frontier",
        MinerBotanistAdvisorSyntheticScenarioKind.StaleEvidence => "Stale evidence rejected",
        MinerBotanistAdvisorSyntheticScenarioKind.IncompleteEvidence => "Incomplete generation",
        MinerBotanistAdvisorSyntheticScenarioKind.Abstention => "Advisor abstention",
        _ => "Complete generation",
    };

    private static string SyntheticScenarioId(MinerBotanistAdvisorSyntheticScenarioKind value) => value switch
    {
        MinerBotanistAdvisorSyntheticScenarioKind.Refreshing => "refreshing",
        MinerBotanistAdvisorSyntheticScenarioKind.StaleEvidence => "stale",
        MinerBotanistAdvisorSyntheticScenarioKind.IncompleteEvidence => "incomplete",
        MinerBotanistAdvisorSyntheticScenarioKind.Abstention => "abstention",
        _ => "success",
    };

    private void ToggleSyntheticSeries(AdvisorUtilityContextDescriptor value) =>
        SetSyntheticSeriesVisible(value, !visibleSyntheticContexts.Contains(value));

    private void SetSyntheticSeriesVisible(AdvisorUtilityContextDescriptor value, bool visible)
    {
        if (visible)
            visibleSyntheticContexts.Add(value);
        else if (visibleSyntheticContexts.Count > 1)
            visibleSyntheticContexts.Remove(value);
        selectedSolutionId = null;
    }
#endif

    private void EnsureSelection(MinerBotanistReadOnlyAdvice advice)
    {
        if (ReferenceEquals(lastAdvice, advice))
            return;
        lastAdvice = advice;
        frontierPresentation = new(
            advice.Frontier!.Pareto,
            advice.AuthorityBySolutionId,
            advice.Nomination?.Candidate.SolutionId);
        SelectSolution(advice, advice.Nomination?.Candidate.SolutionId ?? frontierPresentation.First.Candidate.SolutionId);
    }

    private void SelectSolution(MinerBotanistReadOnlyAdvice advice, string solutionId)
    {
        if (frontierPresentation is null || !frontierPresentation.TryGet(solutionId, out var selected))
            return;
        if (!string.Equals(selectedSolutionId, solutionId, StringComparison.Ordinal))
            handoffStatus = null;
        selectedSolutionId = solutionId;
        frontierWindow = frontierPresentation.WindowAround(solutionId);
        frontierPlot = plotBuilder.Build(frontierWindow.ToPlotResult(), "squire-advisor-frontier-window");
        frontierWarningIds = frontierWindow.Solutions
            .Where(value => advice.AuthorityBySolutionId.TryGetValue(value.Candidate.SolutionId, out var authority) &&
                !authority.AdvisorMayConsider)
            .Select(value => value.Candidate.SolutionId)
            .ToHashSet(StringComparer.Ordinal);
        adjacentTradeoffs = BuildAdjacentTradeoffs(frontierPresentation, selected);
    }

    private void DrawDecisionSummary(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        if (TryGetDirectMarketComparison(advice, selected, out var directMarketCost))
        {
            if (!ImGui.BeginTable("##SquireAdvisorSummary", 4,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
                return;
            SummaryCell("Craft materials", FormatCost(selected.AcquisitionCostGil), MarketMafiosoUiTheme.Header);
            SummaryCell("Same gear direct", FormatCost(directMarketCost), MarketMafiosoUiTheme.Muted);
            SummaryCell("You save", FormatCost(directMarketCost - selected.AcquisitionCostGil), MarketMafiosoUiTheme.Success);
            SummaryCell("Utility gain", FormatUtilityGain(advice, selected), MarketMafiosoUiTheme.Success);
            ImGui.EndTable();
            return;
        }

        var columnCount = selected.AcquisitionCostEstimate is null ? 4 : 5;
        if (!ImGui.BeginTable("##SquireAdvisorSummary", columnCount, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
            return;
        SummaryCell("Goal", ProfileContextLabel(selected.Utility), MarketMafiosoUiTheme.Header);
        SummaryCell("Recommended", advice.Nomination is null ? "No recommendation" : FormatCost(advice.Nomination.AcquisitionCostGil),
            advice.Nomination is null ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Success);
        SummaryCell(selected.AcquisitionCostEstimate is null ? "This option" : "This option expected", FormatCost(selected.AcquisitionCostGil), MarketMafiosoUiTheme.Link);
        if (selected.AcquisitionCostEstimate is { } estimate)
            SummaryCell($"Selected {estimate.PlanningConfidence:P0} plan", FormatCost(estimate.PlanningCostGil), MarketMafiosoUiTheme.Warning);
        SummaryCell("Improvement", FormatUtilityGain(advice, selected), MarketMafiosoUiTheme.Success);
        ImGui.EndTable();
    }

    private static bool TryGetDirectMarketComparison(
        MinerBotanistReadOnlyAdvice advice,
        EquipmentDecisionSolution selected,
        out ulong directMarketCost)
    {
        directMarketCost = selected.AcquisitionCostGil;
        var selectedCraftOffers = selected.Candidate.Selections
            .Select(selection => advice.OffersByAllocation.GetValueOrDefault(selection.AllocationKey))
            .Where(offer => offer?.Offer.SourceKind == EquipmentAcquisitionSourceKind.Craft)
            .DistinctBy(offer => offer!.AllocationKey)
            .Cast<EquipmentExactSolverOffer>()
            .ToArray();
        if (selectedCraftOffers.Length == 0)
            return false;

        foreach (var craftOffer in selectedCraftOffers)
        {
            var marketCost = advice.OffersByAllocation.Values
                .Where(offer => offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard &&
                                offer.Offer.Definition.ItemId == craftOffer.Offer.Definition.ItemId &&
                                offer.Offer.ResolvedQuality == craftOffer.Offer.ResolvedQuality)
                .Select(offer => (ulong?)offer.AcquisitionCostGil)
                .Min();
            if (marketCost is null || directMarketCost < craftOffer.AcquisitionCostGil)
                return false;
            directMarketCost = checked(directMarketCost - craftOffer.AcquisitionCostGil + marketCost.Value);
        }
        return directMarketCost > selected.AcquisitionCostGil;
    }

    private static string FormatUtilityGain(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        var baseline = advice.Frontier?.Pareto.Frontier.FirstOrDefault(solution => solution.Candidate.Selections.All(selection =>
            advice.OffersByAllocation.TryGetValue(selection.AllocationKey, out var offer) &&
            offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.Owned));
        return baseline is null
            ? selected.Utility.UtilityScore.ToString("N1")
            : $"{selected.Utility.UtilityScore - baseline.Utility.UtilityScore:+0.0;-0.0;0.0}";
    }

    private void DrawFrontier(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
#if DEBUG
        if (syntheticReviewAdvice is not null && dryRunFixture is null)
        {
            if (s4GoldenFixture is null)
            {
                DrawSyntheticOverlay(selected);
                return;
            }
        }
#endif
        var model = frontierPlot!;
        ImGui.TextColored(MarketMafiosoUiTheme.Muted,
            $"Options {frontierWindow!.Offset + 1:N0}–{frontierWindow.EndOffset:N0} of {frontierWindow.TotalCount:N0}");
        var interaction = new PlotInteractionState(
            new HashSet<string>(StringComparer.Ordinal) { selected.Candidate.SolutionId },
            advice.Nomination?.Candidate.SolutionId,
            frontierWarningIds,
            new HashSet<string>(StringComparer.Ordinal));
        var result = plotContainer.Draw(
            "SquireAdvisorFrontier",
            BuildPlotRenderRevision(model.Spec, interaction),
            model.Spec,
            new Vector2(0, 285f),
            interaction);
        RegisterPlotControls(result.Controls);
        if (result.ClickedDatumId is { } clicked && model.SolutionsByDatumId.ContainsKey(clicked))
            SelectSolution(advice, clicked);
        if (result.HoveredDatumId is { } hovered && model.SolutionsByDatumId.TryGetValue(hovered, out var solution))
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(MarketMafiosoUiTheme.Header,
                solution.VariantLabels.FirstOrDefault() ?? solution.Candidate.SolutionId);
            ImGui.TextUnformatted($"{FormatCost(solution.AcquisitionCostGil)}{(solution.AcquisitionCostEstimate is null ? "" : " expected")} · utility {solution.Utility.UtilityScore:N1}");
            DrawPlanningCost(solution);
            ImGui.TextColored(MarketMafiosoUiTheme.Muted,
                $"{solution.Burden.PurchaseTransactions:N0} purchase(s), {solution.Burden.WorldVisits:N0} world visit(s)");
            ImGui.EndTooltip();
        }
    }

#if DEBUG
    private void DrawSyntheticOverlay(EquipmentDecisionSolution selected)
    {
        var contexts = ContextOrder
            .Where(visibleSyntheticContexts.Contains)
            .ToArray();
        var adviceByContext = contexts.ToDictionary(
            value => value,
            value => value == context ? syntheticReviewAdvice! : BuildSyntheticReview(value));
        var models = adviceByContext.ToDictionary(
            value => value.Key,
            value => plotBuilder.Build(value.Value.Frontier!.Pareto, $"squire-min-btn-{ContextSeriesId(value.Key)}"));
        var overlay = PlotOverlayComposer.Compose(
            "squire-min-btn-context-overlay",
            contexts.Select(value => new PlotOverlaySeries(
                ContextSeriesId(value),
                models[value].Spec,
                OverlayStyle(value))).ToArray(),
            "Cost / utility frontiers by gathering context");
        var warningIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in adviceByContext)
        foreach (var authority in value.Value.AuthorityBySolutionId.Where(authority => !authority.Value.AdvisorMayConsider))
            warningIds.Add(PlotOverlayComposer.DatumId(ContextSeriesId(value.Key), authority.Key));
        var selectedDatumId = visibleSyntheticContexts.Contains(context)
            ? PlotOverlayComposer.DatumId(ContextSeriesId(context), selected.Candidate.SolutionId)
            : null;
        var nominatedDatumId = visibleSyntheticContexts.Contains(context) && adviceByContext[context].Nomination is { } nomination
            ? PlotOverlayComposer.DatumId(ContextSeriesId(context), nomination.Candidate.SolutionId)
            : null;
        var interaction = new PlotInteractionState(
            selectedDatumId is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal) { selectedDatumId },
            nominatedDatumId,
            warningIds,
            new HashSet<string>(StringComparer.Ordinal));

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Shape identifies context · point color remains NQ/HQ mix");
        var result = plotContainer.Draw(
            "SquireAdvisorFrontierOverlay",
            BuildPlotRenderRevision(overlay.Spec, interaction),
            overlay.Spec,
            new Vector2(0, 285f),
            interaction);
        RegisterPlotControls(result.Controls);
        if (result.ClickedDatumId is { } clicked && overlay.DatumIdentities.TryGetValue(clicked, out var clickedIdentity))
        {
            var clickedContext = ContextFromSeriesId(clickedIdentity.SeriesId);
            SetContext(clickedContext);
            selectedSolutionId = clickedIdentity.SourceDatumId;
            lastAdvice = syntheticReviewAdvice;
        }
        if (result.HoveredDatumId is { } hovered && overlay.DatumIdentities.TryGetValue(hovered, out var hoveredIdentity))
        {
            var hoveredContext = ContextFromSeriesId(hoveredIdentity.SeriesId);
            if (models[hoveredContext].SolutionsByDatumId.TryGetValue(hoveredIdentity.SourceDatumId, out var solution))
            {
                ImGui.BeginTooltip();
                ImGui.TextColored(MarketMafiosoUiTheme.Header,
                    solution.VariantLabels.FirstOrDefault() ?? solution.Candidate.SolutionId);
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, ContextLabel(hoveredContext));
                ImGui.TextUnformatted($"{FormatCost(solution.AcquisitionCostGil)}{(solution.AcquisitionCostEstimate is null ? "" : " expected")} · utility {solution.Utility.UtilityScore:N1}");
                DrawPlanningCost(solution);
                ImGui.TextColored(MarketMafiosoUiTheme.Muted,
                    $"{solution.Burden.PurchaseTransactions:N0} purchase(s), {solution.Burden.WorldVisits:N0} world visit(s)");
                ImGui.EndTooltip();
            }
        }
    }

    private static PlotOverlayStyle OverlayStyle(AdvisorUtilityContextDescriptor value) => value.Id switch
    {
        MinerBotanistUtilityProfile.LegendaryContextId =>
            new(new(.92f, .57f, .20f, .78f), PlotPointShape.Diamond),
        MinerBotanistUtilityProfile.CollectableContextId =>
            new(new(.67f, .45f, .94f, .78f), PlotPointShape.Triangle),
        _ => new(new(.35f, .67f, .98f, .78f), PlotPointShape.Circle),
    };

    private static string ContextSeriesId(AdvisorUtilityContextDescriptor value) => value.Id switch
    {
        MinerBotanistUtilityProfile.LegendaryContextId => "legendary",
        MinerBotanistUtilityProfile.CollectableContextId => "collectables",
        _ => "ordinary",
    };

    private static string ContextSeriesLabel(AdvisorUtilityContextDescriptor value) => value.Id switch
    {
        MinerBotanistUtilityProfile.LegendaryContextId => "Diamond Legendary",
        MinerBotanistUtilityProfile.CollectableContextId => "Triangle Collectables",
        _ => "Circle Ordinary",
    };

    private static AdvisorUtilityContextDescriptor ContextFromSeriesId(string value) => value switch
    {
        "legendary" => GathererAdvisorStatFamily.LegendaryNodeContext,
        "collectables" => GathererAdvisorStatFamily.CollectableContext,
        _ => GathererAdvisorStatFamily.OrdinaryResourceContext,
    };

    private static MinerBotanistReadOnlyAdvice BuildSyntheticReview(AdvisorUtilityContextDescriptor value) =>
        MinerBotanistAdvisorSyntheticReview.Build(GathererAdvisorStatFamily.ContextKindFor(value.Id));
#endif

    private void DrawSolutionRail(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        var selectedIndex = frontierPresentation!.IndexOf(selected.Candidate.SolutionId);
        var previousId = selectedIndex > 0 ? frontierPresentation.At(selectedIndex - 1).Candidate.SolutionId : null;
        if (ImGuiUi.Button("< Previous", previousId is not null))
            SelectSolution(advice, previousId!);
        RegisterLastControl(
            AdvisorReviewedControlIds.SolutionPrevious,
            "Select the previous visible frontier solution",
            AgentBridgeUiControlKind.Button,
            previousId is not null,
            false,
            previousId,
            () => SelectSolution(advice, previousId!));
        ImGui.SameLine();
        var nextId = selectedIndex + 1 < frontierPresentation.Count ? frontierPresentation.At(selectedIndex + 1).Candidate.SolutionId : null;
        if (ImGuiUi.Button("Next >", nextId is not null))
            SelectSolution(advice, nextId!);
        RegisterLastControl(
            AdvisorReviewedControlIds.SolutionNext,
            "Select the next visible frontier solution",
            AgentBridgeUiControlKind.Button,
            nextId is not null,
            false,
            nextId,
            () => SelectSolution(advice, nextId!));
        ImGui.SameLine();
        var previousPageId = frontierWindow!.HasPrevious
            ? frontierPresentation.At(selectedIndex - AdvisorFrontierPresentation.MaxFrameSolutionCount).Candidate.SolutionId
            : null;
        if (ImGuiUi.Button("Page <", previousPageId is not null))
            SelectSolution(advice, previousPageId!);
        RegisterLastControl(
            AdvisorReviewedControlIds.SolutionPreviousPage,
            "Select the solution one frontier page earlier",
            AgentBridgeUiControlKind.Button,
            previousPageId is not null,
            false,
            previousPageId,
            () => SelectSolution(advice, previousPageId!));
        ImGui.SameLine();
        var nextPageId = frontierWindow.HasNext
            ? frontierPresentation.At(selectedIndex + AdvisorFrontierPresentation.MaxFrameSolutionCount).Candidate.SolutionId
            : null;
        if (ImGuiUi.Button("Page >", nextPageId is not null))
            SelectSolution(advice, nextPageId!);
        RegisterLastControl(
            AdvisorReviewedControlIds.SolutionNextPage,
            "Select the solution one frontier page later",
            AgentBridgeUiControlKind.Button,
            nextPageId is not null,
            false,
            nextPageId,
            () => SelectSolution(advice, nextPageId!));
        if (advice.Nomination is { } nomination && nomination.Candidate.SolutionId != selected.Candidate.SolutionId)
        {
            ImGui.SameLine();
            if (ImGui.Button("Advisor pick"))
                SelectSolution(advice, nomination.Candidate.SolutionId);
            RegisterLastControl(
                AdvisorReviewedControlIds.SolutionNomination,
                "Select the Advisor-nominated frontier solution",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                nomination.Candidate.SolutionId,
                () => SelectSolution(advice, nomination.Candidate.SolutionId));
        }
        ImGui.SameLine();
        ImGui.TextDisabled(frontierPresentation.TotalExactCount == frontierPresentation.Count
            ? $"{selectedIndex + 1:N0} / {frontierPresentation.Count:N0}"
            : $"{selectedIndex + 1:N0} / {frontierPresentation.Count:N0} choices | {frontierPresentation.TotalExactCount:N0} exact loadouts");
        if (!ImGui.BeginTable("##SquireAdvisorRail", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Min(150f, 30f + frontierWindow.Solutions.Count * 25f))))
            return;
        ImGui.TableSetupColumn(selected.AcquisitionCostEstimate is null ? "Cost" : "Expected cost", ImGuiTableColumnFlags.WidthFixed, 105f);
        ImGui.TableSetupColumn("Utility", ImGuiTableColumnFlags.WidthFixed, 75f);
        ImGui.TableSetupColumn("No-loss gains", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Burden", ImGuiTableColumnFlags.WidthFixed, 120f);
        foreach (var solution in frontierWindow.Solutions)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{FormatCost(solution.AcquisitionCostGil)}##{solution.Candidate.SolutionId}",
                    solution.Candidate.SolutionId == selected.Candidate.SolutionId,
                    ImGuiSelectableFlags.SpanAllColumns))
                SelectSolution(advice, solution.Candidate.SolutionId);
            var capturedSolution = solution;
            RegisterLastControl(
                AdvisorReviewedControlIds.SolutionPrefix + solution.Candidate.SolutionId,
                $"Select frontier solution costing {FormatCost(solution.AcquisitionCostGil)} with utility {solution.Utility.UtilityScore:N1}",
                AgentBridgeUiControlKind.Select,
                true,
                solution.Candidate.SolutionId == selected.Candidate.SolutionId,
                solution.Candidate.SolutionId,
                () => SelectSolution(advice, capturedSolution.Candidate.SolutionId));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(solution.Utility.UtilityScore.ToString("N1"));
            ImGui.TableNextColumn();
            var authority = advice.AuthorityBySolutionId[solution.Candidate.SolutionId];
            ImGui.TextColored(authority.AdvisorMayConsider ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Warning,
                AuthorityLabel(authority));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{solution.Burden.PurchaseTransactions} buy · {solution.Burden.WorldVisits} world");
        }
        ImGui.EndTable();
    }

    private static string AuthorityLabel(AdvisorAuthorityAssessment authority)
    {
        if (!authority.AdvisorMayConsider)
            return "Doesn't qualify";
        if (authority.GainedCapabilityIds.Count == 0)
            return "No-loss improvement";
        return string.Join(", ", authority.GainedCapabilityIds.Select(id => id switch
        {
            "no-loss-strength-gain" => "STR",
            "no-loss-physical-damage-gain" => "Wpn dmg",
            "no-loss-vitality-gain" => "VIT",
            "no-loss-physical-defense-gain" => "P.Def",
            "no-loss-magical-defense-gain" => "M.Def",
            "no-loss-tenacity-gain" => "TEN",
            _ => id.Replace('-', ' '),
        }));
    }

    private void DrawAdjacentTradeoffs(MinerBotanistReadOnlyAdvice advice)
    {
        if (adjacentTradeoffs.Count == 0)
            return;
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "ADJACENT TRADEOFFS");
        foreach (var value in adjacentTradeoffs)
        {
            if (ImGui.SmallButton($"{value.Label}##{value.Solution.Candidate.SolutionId}"))
                SelectSolution(advice, value.Solution.Candidate.SolutionId);
            ImGui.SameLine();
            ImGui.TextUnformatted($"{FormatSignedGil(value.CostDeltaGil)}, {value.UtilityDelta:+0.0;-0.0;0.0} utility, {value.ChangedPositionCount} slot change(s)");
        }
    }

    private static IReadOnlyList<AdvisorAdjacentTradeoff> BuildAdjacentTradeoffs(
        AdvisorFrontierPresentation presentation,
        EquipmentDecisionSolution selected)
    {
        var result = new List<AdvisorAdjacentTradeoff>(2);
        if (presentation.Previous(selected.Candidate.SolutionId) is { } previous)
            result.Add(Create(previous.AcquisitionCostGil < selected.AcquisitionCostGil ? "Cheaper" : "Previous variant", previous, selected));
        if (presentation.Next(selected.Candidate.SolutionId) is { } next)
            result.Add(Create(
                next.Utility.UtilityScore > selected.Utility.UtilityScore ? "More capable" :
                next.AcquisitionCostGil > selected.AcquisitionCostGil ? "Higher-cost tradeoff" : "Next variant",
                next,
                selected));
        return result;

        static AdvisorAdjacentTradeoff Create(
            string label,
            EquipmentDecisionSolution adjacent,
            EquipmentDecisionSolution selected) => new(
                label,
                adjacent,
                checked((long)adjacent.AcquisitionCostGil - (long)selected.AcquisitionCostGil),
                adjacent.Utility.UtilityScore - selected.Utility.UtilityScore,
                EquipmentParetoFrontierBuilder.Diff(selected.Candidate, adjacent.Candidate).ChangedPositionCount);
    }

    private sealed record AdvisorAdjacentTradeoff(
        string Label,
        EquipmentDecisionSolution Solution,
        long CostDeltaGil,
        double UtilityDelta,
        int ChangedPositionCount);

    private static void DrawSelectedLoadout(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "SELECTED LOADOUT");
        if (!ImGui.BeginTable("##SquireAdvisorLoadout", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, 265f)))
            return;
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 65f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(selected.AcquisitionCostEstimate is null ? "Cost" : "Expected cost", ImGuiTableColumnFlags.WidthFixed, 95f);
        ImGui.TableHeadersRow();
        foreach (var selection in selected.Candidate.Selections.OrderBy(value => value.Position))
        {
            if (!advice.OffersByAllocation.TryGetValue(selection.AllocationKey, out var offer))
                continue;
            ImGui.TableNextRow();
            Cell(selection.Position.ToString());
            Cell(offer.Offer.Definition.Name);
            Cell(selection.OfferKey.Quality == EquipmentQuality.High ? "HQ" : "NQ");
            Cell(offer.Offer.SourceLabel);
            Cell(offer.AcquisitionCostGil == 0 ? "—" : $"{offer.AcquisitionCostGil:N0}");
        }
        ImGui.EndTable();
    }

    private void DrawAcquisitionHandoffComponent(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        var acquisitions = selected.Candidate.Selections
            .Select(value => advice.OffersByAllocation.GetValueOrDefault(value.AllocationKey))
            .Where(value => value is not null && value.Offer.SourceKind != EquipmentAcquisitionSourceKind.Owned)
            .DistinctBy(value => value!.AllocationKey)
            .Cast<EquipmentExactSolverOffer>()
            .ToArray();
        var containsCraft = acquisitions.Any(offer => offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.Craft);
#if DEBUG
        var s4Review = s4GoldenFixture is not null && ReferenceEquals(advice, s4GoldenFixture.Advice)
            ? s4GoldenFixture
            : null;
        var currentListingDryRunReview = dryRunFixture is not null && ReferenceEquals(advice, dryRunFixture.Advice)
            ? dryRunFixture
            : null;
#endif
        var evidence =
#if DEBUG
            s4Review?.MarketEvidence ??
#endif
            session.CurrentEvidence;
        OutfitterCraftHandoffProjection? craftHandoff = null;
#if DEBUG
        if (s4Review is not null && string.Equals(selected.Candidate.SolutionId, s4Review.SelectedCraftSolutionId, StringComparison.Ordinal))
            craftHandoff = s4Review.CraftHandoff;
        else
#endif
        if (containsCraft && evidence is not null &&
            session.TryGetCraftHandoffPresentation(advice, selected.Candidate.SolutionId, evidence, out var projectedCraft))
            craftHandoff = projectedCraft;
        if (acquisitions.Length == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Success,
                "No acquisition needed; current equipped items remain the selected loadout.");
            return;
        }
        foreach (var offer in acquisitions)
            ImGui.BulletText($"{offer.Offer.Definition.Name} {FormatQuality(offer.Offer.ResolvedQuality)} · {offer.Offer.SourceLabel} · {offer.AcquisitionCostGil:N0} gil");
        if (containsCraft)
            DrawCraftHandoffReview(craftHandoff, acquisitions);
        var canCopyArtisan = containsCraft && craftHandoff is not null &&
#if DEBUG
                             (s4Review is not null ||
#endif
                             session.State.Stage == MinerBotanistAdvisorSessionStage.Complete &&
                             !session.State.AdviceIsRetained &&
                             ReferenceEquals(advice, session.State.Advice) &&
                             evidence is not null &&
                             CraftMarketEvidenceFreshness.IsFresh(evidence, DateTimeOffset.UtcNow)
#if DEBUG
                             )
#endif
                             ;
        void CopyArtisanList()
        {
            if (!canCopyArtisan || evidence is null)
                return;
            if (!string.Equals(selectedSolutionId, selected.Candidate.SolutionId, StringComparison.Ordinal))
            {
                handoffStatus = "Artisan export stopped safely: the selected solution changed after review.";
                return;
            }
#if DEBUG
            if (s4Review is not null &&
                (!ReferenceEquals(s4GoldenFixture, s4Review) || !ReferenceEquals(syntheticReviewAdvice, advice)))
            {
                handoffStatus = "Artisan export stopped safely: the frozen S4 review is no longer active.";
                return;
            }
#endif
            OutfitterCraftHandoffProjection currentCraft;
#if DEBUG
            if (s4Review is not null)
            {
                currentCraft = s4Review.CraftHandoff;
            }
            else
#endif
            if (!session.TryBuildCraftHandoff(
                    advice,
                    selected.Candidate.SolutionId,
                    evidence,
                    out currentCraft,
                    out var diagnostic))
            {
                handoffStatus = diagnostic;
                return;
            }
            try
            {
                var gearNames = acquisitions
                    .Where(offer => offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.Craft)
                    .Select(offer => offer.Offer.Definition.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Take(3)
                    .ToArray();
                var name = $"Squire Outfitter - {string.Join(", ", gearNames)}";
                var export = ArtisanCraftingListExport.Create(
                    name,
                    currentCraft.Recipes.Select(recipe => new ArtisanCraftingListRecipeRequest(
                        recipe.RecipeId,
                        checked((int)recipe.CraftCount))));
                ImGui.SetClipboardText(export.Json);
                handoffStatus = $"Copied Artisan list: {export.RecipeCount:N0} recipe(s), {export.ExpandedEntryCount:N0} craft(s). Squire did not start crafting.";
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            {
                handoffStatus = $"Artisan export stopped safely: {exception.Message}";
            }
        }
        if (containsCraft)
        {
            if (ImGuiUi.Button("Copy Artisan list", canCopyArtisan))
                CopyArtisanList();
            RegisterLastControl(
                AdvisorReviewedControlIds.CopyArtisanList,
                "Copy the selected frozen gear and subcraft recipe list for user-directed Artisan import",
                AgentBridgeUiControlKind.Button,
                canCopyArtisan,
                false,
                selected.Candidate.SolutionId,
                () =>
                {
                    CopyArtisanList();
                    if (handoffStatus is null || !handoffStatus.StartsWith("Copied reviewed Artisan list", StringComparison.Ordinal))
                        throw new InvalidOperationException(handoffStatus ?? "Artisan export did not complete.");
                });
        }
        var canStage = session.State.Stage == MinerBotanistAdvisorSessionStage.Complete &&
                       !session.State.AdviceIsRetained &&
                       ReferenceEquals(advice, session.State.Advice) &&
                           evidence is not null &&
                           (containsCraft
                           ? craftHandoff is { MarketMaterials.Count: > 0 } && CraftMarketEvidenceFreshness.IsFresh(evidence, DateTimeOffset.UtcNow)
                           : acquisitions.Any(offer => offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard));
#if DEBUG
        if (s4Review is not null &&
            string.Equals(selected.Candidate.SolutionId, s4Review.SelectedCraftSolutionId, StringComparison.Ordinal) &&
            syntheticScenarioKind == MinerBotanistAdvisorSyntheticScenarioKind.Success &&
            config.EnableMarketAcquisitionDryRunTools)
        {
            evidence = s4Review.MarketEvidence;
            canStage = true;
        }
        else if (currentListingDryRunReview is not null &&
            syntheticScenarioKind == MinerBotanistAdvisorSyntheticScenarioKind.Success &&
            config.EnableMarketAcquisitionDryRunTools)
        {
            evidence = currentListingDryRunReview.Evidence;
            canStage = true;
        }
        else if (syntheticReviewAdvice is not null)
        {
            canStage = false;
        }
#endif
        void Stage()
        {
            if (!canStage || evidence is null)
                return;
            if (!string.Equals(selectedSolutionId, selected.Candidate.SolutionId, StringComparison.Ordinal))
            {
                handoffStatus = "Upgrade handoff stopped safely because the selected option changed.";
                return;
            }
            try
            {
#if DEBUG
                if (s4Review is not null)
                {
                    if (!ReferenceEquals(s4GoldenFixture, s4Review) ||
                        !ReferenceEquals(syntheticReviewAdvice, advice) ||
                        !config.EnableMarketAcquisitionDryRunTools)
                    {
                        throw new InvalidOperationException("The frozen S4 dry-run review is no longer active.");
                    }
                    var dryRunValidation = OutfitterWorkbenchPlayerValidation.CreateDryRun(
                        advice,
                        selected.Candidate.SolutionId,
                        evidence) with
                    {
                        RecapturedBaseline = s4Review.Baseline,
                    };
                    stageTransfer(OutfitterWorkbenchTransferBuilder.Build(
                        advice,
                        selected.Candidate.SolutionId,
                        evidence,
                        dryRunValidation,
                        s4Review.TimeProvider));
                    handoffStatus = "Golden-path craft materials added to the dry-run Workbench; gear and crafting remain manual.";
                    return;
                }
                if (currentListingDryRunReview is not null)
                {
                    if (!ReferenceEquals(dryRunFixture, currentListingDryRunReview) ||
                        !ReferenceEquals(syntheticReviewAdvice, advice) ||
                        !config.EnableMarketAcquisitionDryRunTools)
                    {
                        throw new InvalidOperationException("The current-listing dry-run review is no longer active.");
                    }
                    var dryRunValidation = OutfitterWorkbenchPlayerValidation.CreateDryRun(
                        advice,
                        selected.Candidate.SolutionId,
                        evidence);
                    stageTransfer(OutfitterWorkbenchTransferBuilder.Build(
                        advice,
                        selected.Candidate.SolutionId,
                        evidence,
                        dryRunValidation));
                    handoffStatus = containsCraft
                        ? "Exact craft-material dry-run lots added to the Market Acquisition Workbench for review; gear remains manual."
                        : "Exact-quality dry-run solution added to the Market Acquisition Workbench for review.";
                    return;
                }
#endif
                if (!session.RequestWorkbenchValidation(advice, selected.Candidate.SolutionId, evidence))
                    throw new InvalidOperationException("Your equipped gear changed; refresh the evaluation before continuing.");
                handoffStatus = "Checking that your equipped gear has not changed…";
            }
            catch (Exception exception)
            {
                handoffStatus = $"Upgrade handoff stopped safely: {exception.Message}";
            }
        }
        var workbenchLabel = containsCraft
            ? $"Get {craftHandoff?.MarketMaterials.Count ?? 0:N0} materials"
            : "Get these upgrades";
        if (containsCraft)
            ImGui.SameLine();
        if (ImGuiUi.PrimaryButton(workbenchLabel, canStage))
            Stage();
        RegisterLastControl(
            containsCraft
                ? AdvisorReviewedControlIds.StageMaterialsWorkbench
                : AdvisorReviewedControlIds.StageWorkbench,
            containsCraft
                ? "Prepare the market materials needed for the selected crafted upgrades"
                : "Prepare the selected upgrades for acquisition",
            AgentBridgeUiControlKind.Button,
            canStage,
            false,
            selected.Candidate.SolutionId,
            Stage);
        if (containsCraft)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted,
                "Copies a list and stages market materials for review. Never starts Artisan, crafts items, or buys gear.");
            if (evidence is not null &&
#if DEBUG
                s4Review is null &&
#endif
                !CraftMarketEvidenceFreshness.IsFresh(evidence, DateTimeOffset.UtcNow))
            {
                ImGui.TextColored(MarketMafiosoUiTheme.Warning,
                    "Market evidence expired; refresh before export or material review.");
            }
        }
        if (!string.IsNullOrWhiteSpace(handoffStatus))
            ImGui.TextColored(handoffStatus.Contains("stopped safely", StringComparison.OrdinalIgnoreCase) ||
                              handoffStatus.Contains("changed", StringComparison.OrdinalIgnoreCase)
                ? MarketMafiosoUiTheme.Error
                : MarketMafiosoUiTheme.Success, handoffStatus);
    }

    private static void DrawCraftHandoffReview(
        OutfitterCraftHandoffProjection? craftHandoff,
        IReadOnlyList<EquipmentExactSolverOffer> acquisitions)
    {
        ImGui.Spacing();
        if (craftHandoff is null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning,
                "Frozen craft details are unavailable for this selection. Refresh before export or material staging.");
            return;
        }

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "ARTISAN CRAFTING LIST");
        if (ImGui.BeginTable("##SquireAdvisorCraftRecipes", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Recipe", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Crafts", ImGuiTableColumnFlags.WidthFixed, 65f);
            foreach (var recipe in craftHandoff.Recipes)
            {
                ImGui.TableNextRow();
                Cell(recipe.ItemName);
                Cell(recipe.CraftCount.ToString("N0"));
            }
            ImGui.EndTable();
        }

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "MATERIALS TO BUY");
        if (ImGui.BeginTable("##SquireAdvisorCraftMaterials", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 45f);
            ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 45f);
            ImGui.TableSetupColumn("Surplus", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 75f);
            ImGui.TableHeadersRow();
            foreach (var material in craftHandoff.Materials)
            {
                var quality = material.Quality == EquipmentQuality.High ? "HQ" : "NQ";
                var source = material.Source switch
                {
                    OutfitterMarketMaterialSourceIdentity market => $"{market.WorldName} market",
                    OutfitterGilVendorMaterialSourceIdentity vendor => vendor.VendorName,
                    _ => "Manual",
                };
                ImGui.TableNextRow();
                Cell($"{material.ItemName} {quality}");
                Cell(material.ConsumedQuantity.ToString("N0"));
                Cell(material.PurchasedQuantity.ToString("N0"));
                Cell(material.SurplusQuantity.ToString("N0"));
                Cell(source);
                Cell(checked((ulong)material.PurchasedQuantity * material.Source.UnitPriceGil).ToString("N0"));
            }
            ImGui.EndTable();
        }

        var manualGearCount = acquisitions.Count(offer =>
            offer.Offer.SourceKind is EquipmentAcquisitionSourceKind.MarketBoard or EquipmentAcquisitionSourceKind.GilVendor);
        if (manualGearCount > 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning,
                $"{manualGearCount:N0} selected non-crafted gear acquisition(s) remain manual; material staging never buys gear.");
        }
    }

    private void CompletePendingWorkbenchTransfer()
    {
        if (!session.TryTakeWorkbenchValidation(out var validation))
            return;
        try
        {
            var evidence = session.CurrentEvidence
                ?? throw new InvalidOperationException("Market prices expired before the upgrade list was prepared.");
            var transfer = OutfitterWorkbenchTransferBuilder.Build(
                validation.Advice,
                validation.SelectedSolutionId,
                evidence,
                validation);
            stageTransfer(transfer);
            handoffStatus = transfer.SelectedLoadout.Any(line => line.OfferKey.SourceKind == EquipmentAcquisitionSourceKind.Craft)
                ? "Materials are ready for review. Gear selection and crafting remain under your control."
                : "Upgrades are ready for review.";
        }
        catch (Exception exception)
        {
            handoffStatus = $"Upgrade handoff stopped safely: {exception.Message}";
        }
    }

    private void DrawEmptyState(AdvisorWorkspacePresentation presentation)
    {
        Action? drawRecoveryActions = presentation.ShowCancel || presentation.ShowDeterministicReview
            ? () => DrawRecoveryActions(presentation)
            : null;
        if (AdvisorWorkspaceComponentRenderer.DrawEmptyState(presentation, drawRecoveryActions))
            Begin();
        if (presentation.ShowEmptyIntroduction)
        {
            RegisterLastControl(
                AdvisorReviewedControlIds.Refresh,
                presentation.PrimaryActionReviewLabel,
                AgentBridgeUiControlKind.Button,
                presentation.CanEvaluate,
                false,
                null,
                Begin);
        }
    }

    private void DrawRecoveryActions(AdvisorWorkspacePresentation presentation)
    {
        if (presentation.ShowCancel)
            DrawCancelControl();
#if DEBUG
        if (!presentation.ShowDeterministicReview)
            return;
        if (presentation.ShowCancel)
            ImGui.SameLine();
        if (DalamudUiControls.Button(
                "Load deterministic review##SquireAdvisorSyntheticRecovery",
                SquireUiTheme.Current,
                DalamudUiTone.Neutral,
                quiet: true))
            ToggleSyntheticReview();
        RegisterLastControl(
            AdvisorReviewedControlIds.SyntheticReview,
            "Load deterministic advisor review",
            AgentBridgeUiControlKind.Button,
            true,
            false,
            "recovery",
            ToggleSyntheticReview);
#endif
    }

    private void DrawCancelControl()
    {
        if (ImGui.Button("Cancel##SquireAdvisor"))
            session.Cancel();
        RegisterLastControl(
            AdvisorReviewedControlIds.Cancel,
            "Cancel the current advisor observation or market refresh",
            AgentBridgeUiControlKind.Button,
            true,
            false,
            null,
            session.Cancel);
    }

    /// <summary>Label for the context the solution was actually evaluated under — never the UI selector.</summary>
    private static string ProfileContextLabel(EquipmentUtilityEvaluation evaluation) =>
        AdvisorStatFamilies.Resolve(evaluation.Context.ClassJobId)?.ResolveContext(evaluation.Context.ContextId).Label
        ?? evaluation.Context.ContextId;

    private static string ContextLabel(AdvisorUtilityContextDescriptor value) => value.Label;

    private static Vector4 StatusColor(MinerBotanistAdvisorSessionStage stage) => stage switch
    {
        MinerBotanistAdvisorSessionStage.Complete => MarketMafiosoUiTheme.Success,
        MinerBotanistAdvisorSessionStage.Abstained => MarketMafiosoUiTheme.Warning,
        MinerBotanistAdvisorSessionStage.Failed => MarketMafiosoUiTheme.Error,
        _ => MarketMafiosoUiTheme.Muted,
    };

    private static string FormatCost(ulong value) => value == 0 ? "No gil" : $"{value:N0} gil";
    private static string FormatSignedGil(long value) => value switch
    {
        > 0 => $"+{value:N0} gil",
        < 0 => $"-{Math.Abs(value):N0} gil",
        _ => "same cost",
    };
    private static string FormatQuality(EquipmentQuality value) => value == EquipmentQuality.High ? "HQ" : "NQ";

    private static void DrawPlanningCost(EquipmentDecisionSolution solution)
    {
        if (solution.AcquisitionCostEstimate is not { } estimate || estimate.PlanningCostGil <= estimate.ExpectedCostGil)
            return;
        ImGui.TextColored(MarketMafiosoUiTheme.Muted,
            $"{estimate.PlanningConfidence:P0} whole-set stock: {FormatCost(estimate.PlanningCostGil)}");
    }

    private static string AcquisitionVerb(EquipmentAcquisitionSourceKind sourceKind) => sourceKind switch
    {
        EquipmentAcquisitionSourceKind.Craft => "Craft",
        EquipmentAcquisitionSourceKind.MarketBoard => "Buy",
        EquipmentAcquisitionSourceKind.GilVendor => "Buy",
        _ => "Use",
    };

    private static void SummaryCell(string label, string value, Vector4 color)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, label);
        ImGui.TextColored(color, value);
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private void RegisterLastControl(
        string id,
        string label,
        AgentBridgeUiControlKind kind,
        bool enabled,
        bool selected,
        string? value,
        Action invoke)
    {
        reviewRegistry.Register(
            id,
            label,
            kind,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            selected,
            value,
            invoke);
    }

    private static long BuildPlotRenderRevision(PlotSpec spec, PlotInteractionState interaction)
    {
        var hash = new HashCode();
        hash.Add(RuntimeHelpers.GetHashCode(spec));
        hash.Add(interaction.NominatedDatumId, StringComparer.Ordinal);
        foreach (var id in interaction.SelectedDatumIds.Order(StringComparer.Ordinal))
            hash.Add(id, StringComparer.Ordinal);
        foreach (var id in interaction.WarningDatumIds.Order(StringComparer.Ordinal))
            hash.Add(id, StringComparer.Ordinal);
        foreach (var id in interaction.FailureDatumIds.Order(StringComparer.Ordinal))
            hash.Add(id, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private void RegisterPlotControls(IReadOnlyList<DalamudPlotContainerControl> controls)
    {
        foreach (var control in controls)
        {
            reviewRegistry.Register(
                AdvisorReviewedControlIds.PlotPrefix + control.Id,
                control.Label,
                AgentBridgeUiControlKind.Button,
                control.Bounds.Minimum,
                control.Bounds.Maximum,
                control.Enabled,
                control.Selected,
                control.Value,
                control.Invoke);
        }
    }

    private enum AdvisorFrontierView
    {
        Solutions,
        Plot,
    }

    private sealed record PortfolioEvaluatedTarget(
        string TargetKey,
        string TargetLabel,
        MinerBotanistReadOnlyAdvice Advice,
        OutfitterMarketEvidenceBook? MarketEvidence);

    private sealed record PortfolioTerminalTargetEvidence(
        string EvidenceGeneration,
        string Reason,
        PortfolioTargetDispositionKind Kind);
}
