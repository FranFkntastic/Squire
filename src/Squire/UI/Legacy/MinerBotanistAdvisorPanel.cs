using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Plots;
using MarketMafioso.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.Utility;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.WorkshopPrep;
using MarketMafioso.Windows.Main;

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
    private readonly Func<string> resolveRegion;
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
        Func<string> resolveRegion,
        Action<OutfitterWorkbenchTransfer> stageTransfer)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        this.listingSource = listingSource ?? throw new ArgumentNullException(nameof(listingSource));
        this.resolveRegion = resolveRegion ?? throw new ArgumentNullException(nameof(resolveRegion));
        this.stageTransfer = stageTransfer ?? throw new ArgumentNullException(nameof(stageTransfer));
        context = GathererAdvisorStatFamily.Instance.ResolveContext(config.Squire.OutfitterAdvisorContext);
    }

    public void Draw()
    {
#if DEBUG
        PumpDryRunFixture();
#endif
        CompletePendingWorkbenchTransfer();
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
        DrawControls(state);
#if DEBUG
        if (syntheticReviewActive)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, s4GoldenFixture is not null
                ? "S4 CRAFT HANDOFF PROBE — FROZEN DATA, DRY RUN ONLY"
                : dryRunFixture is null
                    ? "DEBUG REPLAY — model decisions with frozen evidence prices"
                    : "ROUTE INTEGRATION PROBE — NOT A GEAR RECOMMENDATION");
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, s4GoldenFixture is not null
                ? "Production recipe, Advisor, Artisan export, and material-only Workbench boundaries; no live character, purchase, or crafting action."
                : dryRunFixture?.Diagnostic ??
                  "Item names are game data; only marketable components use Aether sale-history medians. No live character or live listing is used.");
            if (dryRunFixture is null && s4GoldenFixture is null)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, MinerBotanistAdvisorSyntheticReview.PriceEvidenceLabel);
            ImGui.TextColored(syntheticPresentation!.AdviceIsRetained ? MarketMafiosoUiTheme.Warning : StatusColor(syntheticPresentation.Stage),
                syntheticPresentation.AdviceIsRetained
                    ? $"LAST VALID FRONTIER · {syntheticPresentation.Label}"
                    : syntheticPresentation.Label);
            if (syntheticPresentation.ShowProgress)
                ImGui.ProgressBar((float)syntheticPresentation.Completed / syntheticPresentation.Total, new Vector2(-1, 0),
                    $"{syntheticPresentation.Completed:N0} / {syntheticPresentation.Total:N0}");
            ImGui.TextColored(StatusColor(syntheticPresentation.Stage), syntheticPresentation.Message);
        }
        else
#endif
        {
            if (state.AdviceIsRetained)
                ImGui.TextColored(MarketMafiosoUiTheme.Warning,
                    RetainedAdviceLabel(state.Stage));
            if (state.IsBusy)
            {
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, FriendlyProgressMessage(state.Stage));
                var fraction = state.Total is > 0 ? Math.Clamp((float)state.Completed / state.Total.Value, 0f, 1f) : 0f;
                ImGui.ProgressBar(fraction, new Vector2(-1, 0), state.Total is > 0
                    ? $"{state.Completed:N0} / {state.Total:N0}"
                    : string.Empty);
            }
            else if (state.Stage is MinerBotanistAdvisorSessionStage.Abstained or
                     MinerBotanistAdvisorSessionStage.Failed or
                     MinerBotanistAdvisorSessionStage.Cancelled)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, StatusColor(state.Stage));
                ImGui.TextWrapped(state.Message);
                ImGui.PopStyleColor();
            }
        }
        ImGui.Separator();

        if (displayedAdvice is not { Frontier: { } frontier } advice || frontier.Pareto.Frontier.Count == 0)
        {
#if DEBUG
            if (syntheticReviewActive)
            {
                ImGui.TextWrapped("No recommendation was produced; the advisor stopped at the displayed abstention boundary.");
                return;
            }
#endif
            DrawEmptyState(state);
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
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, string.Join(" · ", selected.VariantLabels.Skip(2)));
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
            DrawSelectedDecision(advice, selected);
            ImGui.Separator();
            DrawFrontierExplorer(advice, selected);
            return;
        }

        ImGui.TableSetupColumn("Selected decision", ImGuiTableColumnFlags.WidthStretch, 1.65f);
        ImGui.TableSetupColumn("Exact frontier", ImGuiTableColumnFlags.WidthStretch, 0.85f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawSelectedDecision(advice, selected);
        ImGui.TableNextColumn();
        DrawFrontierExplorer(advice, selected);
        ImGui.EndTable();
    }

    private void DrawSelectedDecision(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
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
        DrawAcquisitionChecklist(advice, selected);

        if (ImGui.CollapsingHeader($"Full selected loadout ({selected.Candidate.Selections.Count:N0} slots, {changedSlotCount:N0} changes)##SquireAdvisorLoadoutDisclosure"))
            DrawSelectedLoadout(advice, selected);
        if (adjacentTradeoffs.Count > 0 && ImGui.CollapsingHeader("Adjacent tradeoffs##SquireAdvisorTradeoffsDisclosure"))
            DrawAdjacentTradeoffs(advice);
    }

    private void DrawFrontierExplorer(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "COMPARE OPTIONS");
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
            $"squire.outfitter.advisor.frontier-view.{view.ToString().ToLowerInvariant()}",
            $"Show exact frontier as {label.ToLowerInvariant()}",
            AgentBridgeUiControlKind.Select,
            true,
            selected,
            view.ToString(),
            () => frontierView = view);
    }

    private void DrawControls(MinerBotanistAdvisorSessionState state)
    {
        var hasGathererContext = ContextOrder.Any(candidate => candidate.Id == state.Context.Id);
#if DEBUG
        if (s4GoldenFixture is not null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Context · Ordinary crafting benchmark · Blacksmith");
        }
        else
#endif
        if (!hasGathererContext)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Context · {state.Context.Label}");
        }
        else
        {
            ImGui.SetNextItemWidth(230f);
            if (ImGui.BeginCombo("MIN/BTN context##SquireAdvisorContext", ContextLabel(context)))
            {
                foreach (var candidate in ContextOrder)
                {
                    if (ImGui.Selectable(ContextLabel(candidate), candidate == context))
                        SetContext(candidate);
                }
                ImGui.EndCombo();
            }
            var contextMin = ImGui.GetItemRectMin();
            var contextMax = ImGui.GetItemRectMax();
            foreach (var candidate in ContextOrder)
            {
                var captured = candidate;
                reviewRegistry.Register(
                    $"squire.outfitter.advisor.context.{candidate.ConfigurationValue.ToLowerInvariant()}",
                    $"Use {ContextLabel(candidate)}",
                    AgentBridgeUiControlKind.Select,
                    contextMin,
                    contextMax,
                    !state.IsBusy,
                    candidate == context,
                    ContextLabel(candidate),
                    () => SetContext(captured));
            }
        }
        ImGui.SameLine();
        if (state.IsBusy)
        {
            if (ImGui.Button("Cancel##SquireAdvisor"))
                session.Cancel();
            RegisterLastControl(
                "squire.outfitter.advisor.cancel",
                "Cancel the current advisor observation or market refresh",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                null,
                session.Cancel);
        }
        else if (state.Stage != MinerBotanistAdvisorSessionStage.Idle)
        {
            var evaluationLabel = state.Advice is null
                ? "Evaluate gear upgrades##SquireAdvisor"
                : "Refresh evaluation##SquireAdvisor";
            if (ImGui.Button(evaluationLabel))
                Begin();
            RegisterLastControl(
                "squire.outfitter.advisor.refresh",
                state.Advice is null
                    ? "Evaluate gear upgrades from current player equipment and exact-quality evidence"
                    : "Refresh the current gear-upgrade evaluation",
                AgentBridgeUiControlKind.Button,
                true,
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
            if (ImGui.Button(label))
                ToggleSyntheticReview();
            RegisterLastControl(
                "squire.outfitter.advisor.synthetic-review",
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
            if (ImGuiUi.Button("Build live dry-run fixture", canBuildDryRunFixture))
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
                if (ImGui.Checkbox($"{ContextSeriesLabel(candidate)}##SquireAdvisorSyntheticSeries{candidate}", ref visible))
                    SetSyntheticSeriesVisible(candidate, visible);
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

    private void Begin()
    {
#if DEBUG
        syntheticReviewAdvice = null;
        s4GoldenFixture = null;
#endif
        handoffStatus = null;
        var region = resolveRegion();
        session.Begin(context, string.IsNullOrWhiteSpace(region) ? "North America" : region);
    }

    private void SetContext(AdvisorUtilityContextDescriptor value)
    {
        if (session.State.IsBusy)
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
        frontierPresentation = new(advice.Frontier!.Pareto);
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
        SummaryCell(selected.AcquisitionCostEstimate is null ? "Selected" : "Selected expected", FormatCost(selected.AcquisitionCostGil), MarketMafiosoUiTheme.Link);
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
        var result = plotContainer.Draw("SquireAdvisorFrontier", model.Spec, new Vector2(0, 285f), interaction);
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
        var result = plotContainer.Draw("SquireAdvisorFrontierOverlay", overlay.Spec, new Vector2(0, 285f), interaction);
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
            "squire.outfitter.advisor.solution.previous",
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
            "squire.outfitter.advisor.solution.next",
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
            "squire.outfitter.advisor.solution.previous-page",
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
            "squire.outfitter.advisor.solution.next-page",
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
                "squire.outfitter.advisor.solution.nomination",
                "Select the Advisor-nominated frontier solution",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                nomination.Candidate.SolutionId,
                () => SelectSolution(advice, nomination.Candidate.SolutionId));
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{selectedIndex + 1:N0} / {frontierPresentation.Count:N0}");
        if (!ImGui.BeginTable("##SquireAdvisorRail", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Min(150f, 30f + frontierWindow.Solutions.Count * 25f))))
            return;
        ImGui.TableSetupColumn(selected.AcquisitionCostEstimate is null ? "Cost" : "Expected cost", ImGuiTableColumnFlags.WidthFixed, 105f);
        ImGui.TableSetupColumn("Utility", ImGuiTableColumnFlags.WidthFixed, 75f);
        ImGui.TableSetupColumn("Authority", ImGuiTableColumnFlags.WidthStretch);
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
                $"squire.outfitter.advisor.solution.{solution.Candidate.SolutionId}",
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
                authority.AdvisorMayConsider ? "Supported capability" : "Visible, not nominated");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{solution.Burden.PurchaseTransactions} buy · {solution.Burden.WorldVisits} world");
        }
        ImGui.EndTable();
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

    private void DrawAcquisitionChecklist(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
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
                "squire.outfitter.advisor.copy-artisan-list",
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
                ? "squire.outfitter.advisor.stage-materials-workbench"
                : "squire.outfitter.advisor.stage-workbench",
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

    private void DrawEmptyState(MinerBotanistAdvisorSessionState state)
    {
        if (state.Stage == MinerBotanistAdvisorSessionStage.Idle)
        {
            ImGui.Dummy(new Vector2(0, 34f));
            ImGui.TextColored(MarketMafiosoUiTheme.Header, "Find your next gear upgrades");
            ImGui.TextWrapped("Squire compares your equipped MIN and BTN gear with items you own, vendor stock, crafting options, and current market listings.");
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Nothing is purchased or equipped unless you explicitly continue with an upgrade list.");
            ImGui.Spacing();
            if (ImGuiUi.PrimaryButton("Evaluate my gear", true))
                Begin();
            RegisterLastControl(
                "squire.outfitter.advisor.refresh",
                "Evaluate gear upgrades from the active player's equipment",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                null,
                Begin);
        }
        else if (state.Stage is MinerBotanistAdvisorSessionStage.Abstained or MinerBotanistAdvisorSessionStage.Failed)
            ImGui.TextWrapped("No recommendation was produced. The incomplete evidence remains visible above instead of being replaced by a guess.");
    }

    private static string FriendlyProgressMessage(MinerBotanistAdvisorSessionStage stage) => stage switch
    {
        MinerBotanistAdvisorSessionStage.CapturingPlayer => "Reading your current MIN and BTN equipment…",
        MinerBotanistAdvisorSessionStage.DiscoveringMarket => "Comparing owned, vendor, crafted, and market options…",
        _ => "Evaluating gear upgrades…",
    };

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

    private static string RetainedAdviceLabel(MinerBotanistAdvisorSessionStage stage) => stage switch
    {
        MinerBotanistAdvisorSessionStage.CapturingPlayer or
        MinerBotanistAdvisorSessionStage.DiscoveringMarket => "LAST VALID FRONTIER · refresh in progress",
        MinerBotanistAdvisorSessionStage.Cancelled => "LAST VALID FRONTIER · refresh cancelled",
        MinerBotanistAdvisorSessionStage.Failed => "LAST VALID FRONTIER · refresh failed",
        _ => "LAST VALID FRONTIER · refresh abstained",
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

    private void RegisterPlotControls(IReadOnlyList<DalamudPlotContainerControl> controls)
    {
        foreach (var control in controls)
        {
            reviewRegistry.Register(
                $"squire.outfitter.advisor.plot.{control.Id}",
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
}
