using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using MarketMafioso.Squire;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Observation;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.AgentBridge;
using MarketMafioso.Windows.Main;
using Newtonsoft.Json;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.Dalamud.UI.Styling;
using Squire.UI;
using Squire.AgentBridge;
using MarketMafioso.Diagnostics;
using MarketMafioso.Squire.Outfitter.Utility;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.Crafting;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace MarketMafioso.Windows.Squire;

internal sealed class SquireTabPanel : IDisposable
{
    private readonly ICharacterEquipmentSnapshotSource snapshotSource;
    private readonly ISquireActionGameAdapter actionAdapter;
    private readonly ISquireDispositionCapabilitySource capabilitySource;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly ISquireConfigurationStore config;
    private readonly SquireCandidateEvaluator evaluator = new();
    private readonly SquireCleanupWorkbenchState cleanupWorkbench;
    private readonly SquireCounterfactualBatchValidator batchValidator = new();
    private readonly SquireCleanupRuleStore ruleStore;
    private readonly SquireEvidencePanel evidencePanel;
    private readonly SquireRouteDiagnosticsPanel routeDiagnosticsPanel;
    private readonly SquireRunResultPanel runResultPanel = new();
    private readonly string diagnosticDirectory;
    private readonly UiStateCaptureService uiStateCapture;
    private readonly SquireInventoryChangeMonitor inventoryChangeMonitor;
    private readonly MinerBotanistAdvisorSession advisorSession;
    private readonly MinerBotanistAdvisorPanel advisorPanel;
    private readonly SquireWorkspaceState workspaceState;
    private readonly SquireSettingsPanel settingsPanel;
    private readonly OutfitterPassiveCraftComposition? passiveCraftComposition;
    private Action<OutfitterWorkbenchTransfer>? stageOutfitterTransfer;
    private readonly Func<uint, string> resolveItemName;
    private SquireAnalysis? analysis;
    private SquireRunPresentation? lastRun;
    private bool showSnapshotDiagnostics;
    private DateTimeOffset nextAutomaticRefreshAt = DateTimeOffset.MinValue;
    private volatile bool automaticRefreshRequested;
    private string automaticRefreshTrigger = "Automatic refresh";
    private string? reconciliationNotice;
    private string? lastAnalysisInputSignature;
    private readonly SquireOperationalStatusState operationalStatus = new();
    private readonly SquireCleanupColumnMenuRequest cleanupColumnMenuRequest = new();
    private bool runConfirmed;
    private string? confirmedBatchKey;
    private CancellationTokenSource? runCancellation;
    private Task? activeRun;
    private Task? activeRunRecovery;
    private string? batchValidationKey;
    private SquireBatchValidationResult? cachedBatchValidation;
#if DEBUG
    private SquireCleanupSyntheticReview? cleanupSyntheticReview;
#endif

    public MinerBotanistAdvisorSessionState AdvisorState => advisorSession.State;

    public void InvalidateAdvisorForPlayerStateChange() => advisorSession.InvalidateForPlayerStateChange();

    public SquireTabPanel(
        ISquireConfigurationStore config,
        ICharacterEquipmentSnapshotSource snapshotSource,
        ISquireActionGameAdapter actionAdapter,
        ISquireDispositionCapabilitySource capabilitySource,
        AgentBridgeUiReviewRegistry reviewRegistry,
        string diagnosticDirectory,
        UiStateCaptureService uiStateCapture,
        IGameInventory gameInventory,
        IDataManager dataManager,
        IMarketAcquisitionListingSource marketListingSource,
        IPlayerAdvisorBaselineSource playerAdvisorBaselineSource,
        Func<AdvisorCharacterSubject> captureAdvisorCharacter,
        Func<string> resolveAcquisitionRegion,
        Func<bool> getAgentBridgeAudit,
        Action<bool> setAgentBridgeAudit)
    {
        this.config = config;
        this.snapshotSource = snapshotSource;
        this.actionAdapter = actionAdapter;
        this.capabilitySource = capabilitySource;
        this.reviewRegistry = reviewRegistry;
        this.diagnosticDirectory = diagnosticDirectory;
        this.uiStateCapture = uiStateCapture;
        resolveItemName = itemId =>
        {
            var name = dataManager.GetExcelSheet<LuminaItem>()?.GetRowOrDefault(itemId)?.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? "Unavailable item" : name;
        };
        inventoryChangeMonitor = new SquireInventoryChangeMonitor(
            gameInventory,
            dataManager,
            () => RequestAutomaticRefresh("Equipment changed", TimeSpan.FromMilliseconds(150)));
        OutfitterAdvisorCraftDiscovery? craftDiscovery = null;
        try
        {
            passiveCraftComposition = OutfitterPassiveCraftComposition.Create(dataManager);
            craftDiscovery = passiveCraftComposition.Discovery;
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "[Squire] Passive Craft Architect provider is unavailable; ordinary Advisor offers remain enabled.");
        }
        advisorSession = new(
            playerAdvisorBaselineSource,
            dataManager,
            marketListingSource,
            Path.Combine(diagnosticDirectory, "outfitter-market-evidence.json"),
            craftDiscovery);
        advisorPanel = new(
            config,
            advisorSession,
            reviewRegistry,
            marketListingSource,
            captureAdvisorCharacter,
            resolveAcquisitionRegion,
            transfer => stageOutfitterTransfer?.Invoke(transfer));
        workspaceState = new SquireWorkspaceState(config);
        ruleStore = new SquireCleanupRuleStore(config);
        cleanupWorkbench = new(
            config.Squire.Search,
            config.Squire.ShowProtected,
            config.Squire.ShowNonEquipment);
        evidencePanel = new SquireEvidencePanel(ruleStore, reviewRegistry, Refresh);
        routeDiagnosticsPanel = new SquireRouteDiagnosticsPanel(actionAdapter, reviewRegistry, uiStateCapture);
        settingsPanel = new SquireSettingsPanel(
            new SquireSettingsState(config, RequestPolicyRefresh, getAgentBridgeAudit, setAgentBridgeAudit),
            reviewRegistry,
            () => SelectWorkspace(SquireWorkspaces.Cleanup),
            () => routeDiagnosticsPanel.Draw(analysis, cleanupWorkbench.FocusedItem));
    }

    public void Draw()
    {
        MaybeRefreshAutomatically();
        DrawWorkspaceSelector();
        ImGui.Separator();
        if (workspaceState.SelectedWorkspace == SquireWorkspaces.Outfitter)
        {
            DrawOutfitter();
            return;
        }

        if (workspaceState.SelectedWorkspace == SquireWorkspaces.Settings)
        {
            settingsPanel.Draw();
            return;
        }

        DrawCleanup();
    }

    public void ConnectMarketAcquisition(Action<OutfitterWorkbenchTransfer> stageExactOutfitterTransfer)
    {
        stageOutfitterTransfer = stageExactOutfitterTransfer ?? throw new ArgumentNullException(nameof(stageExactOutfitterTransfer));
    }

    public void OpenOutfitterAdvisor()
    {
        SelectWorkspace(SquireWorkspaces.Outfitter);
    }

    public void OpenSettings() => workspaceState.OpenSettings();

#if DEBUG
    public void OpenSyntheticAdvisorReview()
    {
        SelectWorkspace(SquireWorkspaces.Outfitter);
        advisorPanel.LoadSyntheticReview();
    }
#endif

    private SquireAnalysis? DisplayedCleanupAnalysis =>
#if DEBUG
        cleanupSyntheticReview?.Analysis ??
#endif
        analysis;

    private SquireCleanupWorkbenchState ActiveCleanupWorkbench =>
#if DEBUG
        cleanupSyntheticReview?.Workbench ??
#endif
        cleanupWorkbench;

    private bool IsCleanupSyntheticReviewActive =>
#if DEBUG
        cleanupSyntheticReview is not null;
#else
        false;
#endif

    private void DrawWorkspaceSelector()
    {
        DrawWorkspaceButton(SquireWorkspaces.Cleanup, "Cleanup", "Review and execute equipment cleanup");
        ImGui.SameLine();
        DrawWorkspaceButton(SquireWorkspaces.Outfitter, "Gear upgrades", "Find and acquire complete gear upgrades");
        ImGui.SameLine();
        DrawWorkspaceButton(SquireWorkspaces.Settings, "Settings", "Configure Squire");
    }

    private void DrawWorkspaceButton(string workspace, string visibleLabel, string reviewLabel)
    {
        var selected = workspaceState.SelectedWorkspace == workspace;
        if (DalamudUiControls.SegmentedOption(
                $"{visibleLabel}##SquireWorkspace{workspace}",
                selected,
                SquireUiTheme.Current,
                new(112f, 0)))
            SelectWorkspace(workspace);
        RegisterLastControl(
            workspace == SquireWorkspaces.Settings ? SquireSettingsControlIds.Workspace : $"squire.workspace.{workspace.ToLowerInvariant()}",
            reviewLabel,
            AgentBridgeUiControlKind.Select,
            true,
            selected,
            workspace,
            () => SelectWorkspace(workspace));
    }

    private void SelectWorkspace(string workspace)
    {
        workspaceState.Select(workspace);
    }

    private void DrawOutfitter()
    {
        advisorPanel.Draw();
    }

    private void DrawCleanup()
    {
#if DEBUG
        if (!config.EnableMarketAcquisitionDryRunTools)
            cleanupSyntheticReview = null;
#endif
        var headingActionWidth = 116f;
#if DEBUG
        if (cleanupSyntheticReview is not null)
        {
            var style = ImGui.GetStyle();
            headingActionWidth = ImGui.CalcTextSize("Return to live cleanup").X +
                                 (style.FramePadding.X * 2f) +
                                 style.ItemSpacing.X;
        }
#endif
        DalamudUiChrome.DrawSectionHeading(
            "Equipment cleanup",
            "Safe recommendations from current character evidence",
            SquireUiTheme.Current.Palette,
            () =>
            {
#if DEBUG
                if (cleanupSyntheticReview is not null)
                {
                    DrawCleanupSyntheticReviewToggle();
                    return;
                }
#endif
                if (DalamudUiControls.Button(
                        "Check again##Squire",
                        SquireUiTheme.Current,
                        DalamudUiTone.Neutral,
                        quiet: true))
                    Refresh();
                RegisterLastControl("squire.refresh", "Refresh Squire analysis", AgentBridgeUiControlKind.Button, true, false, null, Refresh);
            },
            headingActionWidth);
        ImGui.Spacing();
#if DEBUG
        if (cleanupSyntheticReview is not null)
        {
            DrawCleanupSyntheticReviewHeader();
        }
        else
#endif
        {
            if (DalamudUiControls.Button(
                    "Export evaluation snapshot##Squire",
                    SquireUiTheme.Current,
                    DalamudUiTone.Neutral,
                    quiet: true,
                    enabled: analysis is not null))
                Export();
            RegisterLastControl("squire.export", "Export Squire evaluation snapshot", AgentBridgeUiControlKind.Button, analysis is not null, false, null, Export);
            DrawOperationalStatus();
        }

        var displayedAnalysis = DisplayedCleanupAnalysis;
        if (displayedAnalysis is null)
        {
            DrawWaitingForAnalysis();
            return;
        }

        DrawSnapshotState(displayedAnalysis);
        var surfaceState = ResolveCleanupSurfaceState(displayedAnalysis);
        if (surfaceState is SquireCleanupSurfaceState.WaitingForCharacter or SquireCleanupSurfaceState.SnapshotIncomplete)
            return;

        DrawSummary(displayedAnalysis);
        ImGui.Separator();
        if (surfaceState == SquireCleanupSurfaceState.ReadyEmpty)
        {
            DalamudUiChrome.DrawCallout(
                "SquireEmptyCandidateState",
                "No cleanup candidates",
                "The equipment snapshot is complete, but no items require cleanup review under the current rules.",
                SquireUiTheme.Current,
                DalamudUiTone.Success);
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(SquireCleanupToolbarPresentation.FilterLabel);
        ImGui.SameLine();
        var editedSearch = ActiveCleanupWorkbench.Search;
        ImGui.SetNextItemWidth(280);
        var toolbarStyle = ImGui.GetStyle();
        var toolbarPaddingY = SquireCleanupToolbarPresentation.ResolveFramePaddingY(
            ImGui.GetFontSize(),
            toolbarStyle.FramePadding.Y);
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(toolbarStyle.FramePadding.X, toolbarPaddingY)))
        {
            if (ImGui.InputTextWithHint("##SquireSearch", "Search or filter, e.g. quality:hq", ref editedSearch, 160))
                ApplyCleanupFilterExpression(editedSearch);
        }
        reviewRegistry.RegisterLastAction(
            SquireCleanupToolbarPresentation.FilterControlId,
            "Filter Cleanup candidates",
            AgentBridgeUiControlKind.Input,
            true,
            false,
            ActiveCleanupWorkbench.Search,
            new AgentBridgeActionArgumentSchema(
                [new("expression", AgentBridgeActionArgumentKind.String, Required: false)]),
            arguments =>
            {
                var expression = arguments is { ValueKind: System.Text.Json.JsonValueKind.Object } value &&
                                 value.TryGetProperty("expression", out var expressionValue)
                    ? expressionValue.GetString()
                    : string.Empty;
                ApplyCleanupFilterExpression(expression);
                return AgentBridgeUiActionResult.Ok(
                    ActiveCleanupWorkbench.Filter.IsValid
                        ? "Cleanup filter updated."
                        : "Cleanup filter updated; the last valid results remain visible.");
            });
        var editedShowProtected = ActiveCleanupWorkbench.ShowProtected;
        ImGui.SameLine();
        if (ImGui.Checkbox("Show protected", ref editedShowProtected))
        {
            ActiveCleanupWorkbench.ShowProtected = editedShowProtected;
            if (!IsCleanupSyntheticReviewActive)
            {
                config.Squire.ShowProtected = editedShowProtected;
                config.Save();
            }
        }
        RegisterLastControl(
            "squire.show-protected",
            "Show protected and evaluation-failure rows",
            AgentBridgeUiControlKind.Toggle,
            true,
            ActiveCleanupWorkbench.ShowProtected,
            null,
            () =>
            {
                ActiveCleanupWorkbench.ShowProtected = !ActiveCleanupWorkbench.ShowProtected;
                if (!IsCleanupSyntheticReviewActive)
                {
                    config.Squire.ShowProtected = ActiveCleanupWorkbench.ShowProtected;
                    config.Save();
                }
            });
        var editedShowNonEquipment = ActiveCleanupWorkbench.ShowNonEquipment;
        ImGui.SameLine();
        if (ImGui.Checkbox("Show non-equipment", ref editedShowNonEquipment))
        {
            ActiveCleanupWorkbench.ShowNonEquipment = editedShowNonEquipment;
            if (!IsCleanupSyntheticReviewActive)
            {
                config.Squire.ShowNonEquipment = editedShowNonEquipment;
                config.Save();
            }
        }
        var editedSelectionMode = ActiveCleanupWorkbench.SelectionMode;
        ImGui.SameLine();
        if (ImGui.Checkbox("Selection mode", ref editedSelectionMode))
            ActiveCleanupWorkbench.SelectionMode = editedSelectionMode;
        RegisterLastControl(
            "squire.selection-mode",
            "Toggle Squire selection mode",
            AgentBridgeUiControlKind.Toggle,
            true,
            ActiveCleanupWorkbench.SelectionMode,
            null,
            () => ActiveCleanupWorkbench.SelectionMode = !ActiveCleanupWorkbench.SelectionMode);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Select any rows for inspection. Ctrl-click adds, Alt-click removes, and Shift-click selects the anchored range. Only executable candidates enter the action batch.");
        if (ActiveCleanupWorkbench.SelectionMode && ActiveCleanupWorkbench.TableSelection.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear selection"))
            {
                ClearSelectionOnly();
            }
        }
        ImGui.SameLine();
        if (DalamudUiControls.Button(
                $"{SquireCleanupToolbarPresentation.ColumnsLabel}##SquireCleanupColumns",
                SquireUiTheme.Current,
                DalamudUiTone.Neutral,
                quiet: true,
                size: new(0f, SquireCleanupToolbarPresentation.ResolveControlHeight(ImGui.GetFrameHeight())),
                tooltip: "Show, hide, or reorder table columns."))
            cleanupColumnMenuRequest.Request();
        RegisterLastControl(
            SquireCleanupToolbarPresentation.ColumnsControlId,
            "Choose Cleanup table columns",
            AgentBridgeUiControlKind.Button,
            true,
            false,
            null,
            cleanupColumnMenuRequest.Request);
        ActiveCleanupWorkbench.Filter.SetExpression(ActiveCleanupWorkbench.Search);
        if (ActiveCleanupWorkbench.Filter.Error is { } filterError)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, $"Check the filter: {filterError}");
        var visibleCandidates = ResolveVisibleCandidates(displayedAnalysis);
        var visibleFingerprints = visibleCandidates.Select(candidate => candidate.Instance.Fingerprint)
            .ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
        ActiveCleanupWorkbench.HiddenBatchCount = ActiveCleanupWorkbench.Review.Selections.Keys.Count(fingerprint => !visibleFingerprints.Contains(fingerprint));
        DrawBatchBar();
        DrawTable(displayedAnalysis, visibleCandidates);
        if (visibleCandidates.Length == 0)
        {
            DalamudUiChrome.DrawCallout(
                "SquireFilteredEmptyState",
                "No rows match these filters",
                "Adjust the search or column filters to show candidates again.",
                SquireUiTheme.Current,
                DalamudUiTone.Neutral);
        }
        if (!IsCleanupSyntheticReviewActive)
            evidencePanel.Draw(displayedAnalysis, ActiveCleanupWorkbench.FocusedItem);
        DrawRunPanel(displayedAnalysis);
        if (!IsCleanupSyntheticReviewActive && lastRun is { } runPresentation)
            runResultPanel.Draw(
                runPresentation,
                resolveItemName,
                RecoverLastRunInteraction,
                activeRunRecovery is { IsCompleted: false },
                RetryLastRunFromCheckpoint,
                activeRun is { IsCompleted: false },
                () => lastRun = null,
                OpenLastRunAuditLocation);
    }

    private void Refresh() => Refresh(reconcileSelections: analysis is not null, "Manual refresh");

    private void ApplyCleanupFilterExpression(string? expression) =>
        SquireCleanupFilterEdit.Apply(
            ActiveCleanupWorkbench,
            expression,
            IsCleanupSyntheticReviewActive
                ? null
                : value =>
                {
                    config.Squire.Search = value;
                    config.Save();
                });

    private void MaybeRefreshAutomatically()
    {
        var runActive = activeRun is { IsCompleted: false };
        var refreshDuringRun = runActive && automaticRefreshRequested && automaticRefreshTrigger == "Equipment changed";
        if ((runActive && !refreshDuringRun) || runConfirmed)
            return;
        var now = DateTimeOffset.UtcNow;
        if (automaticRefreshRequested)
        {
            if (now < nextAutomaticRefreshAt)
                return;
            Refresh(reconcileSelections: analysis is not null, automaticRefreshTrigger, allowDuringRun: refreshDuringRun);
            return;
        }
        if (now >= nextAutomaticRefreshAt)
            Refresh(reconcileSelections: analysis is not null, "Automatic refresh");
    }

    internal SquireAnalysis? CurrentAnalysis => analysis;

    internal void RequestPolicyRefresh() => RequestAutomaticRefresh("Cleanup rules changed", TimeSpan.Zero);

    public void OnFrameworkUpdate()
    {
        _ = operationalStatus.Current(DateTimeOffset.UtcNow);
        advisorSession.Tick();
        if (automaticRefreshRequested)
            MaybeRefreshAutomatically();
    }

    private void RequestAutomaticRefresh(string trigger, TimeSpan delay)
    {
        var requestedAt = DateTimeOffset.UtcNow.Add(delay);
        if (!automaticRefreshRequested || requestedAt < nextAutomaticRefreshAt)
            nextAutomaticRefreshAt = requestedAt;
        if (!automaticRefreshRequested ||
            string.Equals(trigger, "Equipment changed", StringComparison.Ordinal) ||
            !string.Equals(automaticRefreshTrigger, "Equipment changed", StringComparison.Ordinal))
            automaticRefreshTrigger = trigger;
        automaticRefreshRequested = true;
    }

    private void Refresh(bool reconcileSelections, string trigger, bool allowDuringRun = false)
    {
        var runActive = activeRun is { IsCompleted: false };
        if (runActive && !allowDuringRun)
        {
            operationalStatus.ReportBoundary(
                SquireOperationalStatusSource.Refresh,
                "Refresh blocked while Squire owns an active run.",
                DateTimeOffset.UtcNow);
            return;
        }
        try
        {
            var previousAnalysis = analysis;
            var snapshot = snapshotSource.Capture();
            var policy = CreateProtectionPolicy(snapshot.Identity.Scope?.LocalContentId);
            var capabilities = capabilitySource.Capture();
            var inputSignature = SquireAnalysisInputSignature.Create(snapshot, capabilities, policy);
            if (trigger == "Automatic refresh" && string.Equals(lastAnalysisInputSignature, inputSignature, StringComparison.Ordinal))
            {
                automaticRefreshRequested = false;
                automaticRefreshTrigger = "Automatic refresh";
                nextAutomaticRefreshAt = DateTimeOffset.UtcNow.AddSeconds(2);
                return;
            }
            var refreshedAnalysis = evaluator.Evaluate(
                snapshot,
                capabilities,
                policy);
            var sameCharacter = previousAnalysis?.Snapshot.Identity.Scope?.LocalContentId is { } previousContentId &&
                                snapshot.Identity.Scope?.LocalContentId == previousContentId;
            SquireSelectionReconciliation? reconciliation = null;
            if (reconcileSelections && sameCharacter)
                reconciliation = cleanupWorkbench.Review.Reconcile(refreshedAnalysis);
            else
                cleanupWorkbench.Review.Adopt(refreshedAnalysis);

            analysis = refreshedAnalysis;
            lastAnalysisInputSignature = inputSignature;
            if (reconcileSelections && sameCharacter)
            {
                var currentFingerprints = analysis.Candidates.Select(candidate => candidate.Instance.Fingerprint)
                    .ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
                cleanupWorkbench.TableSelection.Retain(currentFingerprints);
                if (cleanupWorkbench.FocusedItem is { } focused && !currentFingerprints.Contains(focused))
                    cleanupWorkbench.FocusedItem = null;
            }
            else
            {
                cleanupWorkbench.TableSelection.Clear();
                cleanupWorkbench.FocusedItem = null;
            }
            InvalidateRunAuthorization();
            cleanupWorkbench.HiddenBatchCount = 0;
            automaticRefreshRequested = false;
            automaticRefreshTrigger = "Automatic refresh";
            nextAutomaticRefreshAt = DateTimeOffset.UtcNow.AddSeconds(2);
            if (!runActive)
                operationalStatus.Resolve(SquireOperationalStatusSource.Refresh, DateTimeOffset.UtcNow);
            reconciliationNotice = reconciliation?.RemovedReasons.Count > 0
                ? $"{trigger} removed {reconciliation.RemovedReasons.Count} stale cleanup-batch item(s): {string.Join(" ", reconciliation.RemovedReasons.Take(3))}"
                : reconciliation is { PreservedCount: > 0 }
                    ? $"{trigger} preserved {reconciliation.PreservedCount} exact cleanup-batch item(s); confirmation was reset."
                    : null;
        }
        catch (Exception ex)
        {
            if (analysis is null)
                cleanupWorkbench.Review.Invalidate();
            operationalStatus.ReportFailure(
                SquireOperationalStatusSource.Refresh,
                $"{trigger} failed: {ex.Message}",
                DateTimeOffset.UtcNow);
            automaticRefreshRequested = false;
            automaticRefreshTrigger = "Automatic refresh";
            nextAutomaticRefreshAt = DateTimeOffset.UtcNow.AddSeconds(5);
        }
    }

    public void RefreshForBridge() => Refresh();

    public AgentBridgeSquireTruth CreateAgentBridgeTruth() =>
        SquireBridgeTruthFactory.Create(
            analysis,
            operationalStatus.Current(DateTimeOffset.UtcNow)?.Message ?? string.Empty,
            actionAdapter);

    public SquireBridgeProductTruth CreateStandaloneBridgeTruth()
    {
        var currentStatus = IsCleanupSyntheticReviewActive
            ? null
            : operationalStatus.Current(DateTimeOffset.UtcNow);
        var displayedAnalysis = DisplayedCleanupAnalysis;
        var visibleCandidateCount = displayedAnalysis is null ? 0 : ResolveVisibleCandidates(displayedAnalysis).Length;
        return new(
            ResolveCleanupSurfaceState(displayedAnalysis).ToString(),
            displayedAnalysis?.Snapshot.Identity.CapturedAt,
            displayedAnalysis?.Snapshot.Diagnostics.IsComplete == true,
            displayedAnalysis?.Candidates.Count ?? 0,
            displayedAnalysis?.Candidates.Count(candidate => candidate.IsExecutable) ?? 0,
            ActiveCleanupWorkbench.Review.Selections.Count,
            ActiveCleanupWorkbench.HiddenBatchCount,
            !IsCleanupSyntheticReviewActive && runConfirmed,
            !IsCleanupSyntheticReviewActive && activeRun is { IsCompleted: false },
            advisorSession.State.Stage.ToString(),
            advisorSession.State.Message,
            advisorSession.State.Completed,
            advisorSession.State.Total,
            advisorSession.State.UpdatedAtUtc,
            currentStatus?.Kind.ToString(),
            currentStatus?.Source.ToString(),
            currentStatus?.Message,
            currentStatus?.CreatedAtUtc,
            currentStatus?.ExpiresAtUtc,
            ActiveCleanupWorkbench.Filter.Expression,
            ActiveCleanupWorkbench.Filter.IsValid,
            visibleCandidateCount,
            settingsPanel.CreateBridgeTruth());
    }

    private static SquireCleanupSurfaceState ResolveCleanupSurfaceState(SquireAnalysis? value) =>
        SquireCleanupSurfaceStateResolver.Resolve(
            value is not null,
            value?.Snapshot.Identity.Scope is not null,
            value?.Snapshot.Diagnostics.IsComplete == true,
            value?.Candidates.Count ?? 0);

    private void DrawOperationalStatus()
    {
        var current = operationalStatus.Current(DateTimeOffset.UtcNow);
        if (current is null)
            return;

        ImGui.SameLine();
        var tone = current.Kind switch
        {
            SquireOperationalStatusKind.Success => DalamudUiTone.Success,
            SquireOperationalStatusKind.Failure => DalamudUiTone.Error,
            SquireOperationalStatusKind.Boundary => DalamudUiTone.Warning,
            _ => DalamudUiTone.Neutral,
        };
        DalamudUiChrome.DrawBadge(current.Kind.ToString(), SquireUiTheme.Current.Palette, tone);
        ImGui.SameLine();
        ImGui.TextColored(SquireUiTheme.Current.Palette.Muted, current.Message);
        if (!current.CanDismiss)
            return;

        ImGui.SameLine();
        if (DalamudUiControls.Button(
                "Dismiss##SquireOperationalStatus",
                SquireUiTheme.Current,
                DalamudUiTone.Neutral,
                quiet: true))
            operationalStatus.Dismiss(DateTimeOffset.UtcNow);
        RegisterLastControl(
            "squire.status.dismiss",
            "Dismiss the current Squire operational message",
            AgentBridgeUiControlKind.Button,
            true,
            false,
            current.Kind.ToString(),
            () => operationalStatus.Dismiss(DateTimeOffset.UtcNow));
    }

    private void DrawWaitingForAnalysis()
    {
        ImGui.Spacing();
        DalamudUiChrome.DrawCallout(
            "SquireInitialAnalysisState",
            "Preparing equipment analysis",
            "Squire is obtaining the first current equipment snapshot. Cleanup controls will appear automatically when it is complete.",
            SquireUiTheme.Current,
            DalamudUiTone.Neutral,
#if DEBUG
            config.EnableMarketAcquisitionDryRunTools ? DrawCleanupSyntheticReviewToggle : null
#else
            null
#endif
        );
    }

    private static void DrawSummary(SquireAnalysis value)
    {
        var snapshot = value.Snapshot;
        var scope = snapshot.Identity.Scope;
        ImGui.TextUnformatted(scope is null ? "No active character" : $"{scope.Name} @ world {scope.HomeWorldId}");
        ImGui.TextUnformatted($"Captured: {snapshot.Identity.CapturedAt.LocalDateTime:G}");
        ImGui.TextUnformatted($"Unlocked jobs: {snapshot.Jobs.Count(job => job.IsUnlocked == true)} | Valid gearsets: {snapshot.Gearsets.Count(set => set.IsValid)} | Items: {snapshot.Instances.Count}");
    }

    private void DrawSnapshotState(SquireAnalysis value)
    {
        var incomplete = value.Snapshot.Diagnostics.Components
            .Where(component => component.Status != Franthropy.Dalamud.Characters.SnapshotComponentStatus.Complete)
            .ToArray();
        if (incomplete.Length == 0)
        {
            showSnapshotDiagnostics = false;
            return;
        }

        var waitingForCharacter = value.Snapshot.Identity.Scope is null;
        var title = waitingForCharacter
            ? "Waiting for an active character"
            : "Equipment snapshot incomplete";
        var detail = waitingForCharacter
            ? "Squire will analyze equipment automatically as soon as character data becomes available. Your cleanup rules remain unchanged."
            : $"{incomplete.Length} data source(s) need attention. Cleanup stays blocked until Squire can verify them.";
        DalamudUiChrome.DrawCallout(
            "SquireSnapshotState",
            title,
            detail,
            SquireUiTheme.Current,
            waitingForCharacter ? DalamudUiTone.Neutral : DalamudUiTone.Warning,
            () =>
            {
                var label = showSnapshotDiagnostics
                    ? "Hide diagnostic details##SquireSnapshotDiagnostics"
                    : "Show diagnostic details##SquireSnapshotDiagnostics";
                if (DalamudUiControls.Button(
                        label,
                        SquireUiTheme.Current,
                        DalamudUiTone.Neutral,
                        quiet: true))
                    ToggleSnapshotDiagnostics();
                RegisterLastControl(
                    "squire.diagnostics.toggle",
                    showSnapshotDiagnostics ? "Hide snapshot diagnostic details" : "Show snapshot diagnostic details",
                    AgentBridgeUiControlKind.Toggle,
                    true,
                    showSnapshotDiagnostics,
                    null,
                    ToggleSnapshotDiagnostics);
#if DEBUG
                if (config.EnableMarketAcquisitionDryRunTools)
                {
                    ImGui.SameLine();
                    DrawCleanupSyntheticReviewToggle();
                }
#endif
            });
        if (showSnapshotDiagnostics)
        {
            ImGui.Spacing();
            DrawDiagnostics(value);
        }
    }

    private void ToggleSnapshotDiagnostics() => showSnapshotDiagnostics = !showSnapshotDiagnostics;

#if DEBUG
    private static readonly SquireCleanupSyntheticScenarioKind[] CleanupSyntheticScenarioOrder =
    [
        SquireCleanupSyntheticScenarioKind.CompleteZero,
        SquireCleanupSyntheticScenarioKind.Populated,
        SquireCleanupSyntheticScenarioKind.FilteredZero,
        SquireCleanupSyntheticScenarioKind.InvalidLastValid,
        SquireCleanupSyntheticScenarioKind.HiddenSelected,
    ];

    private void DrawCleanupSyntheticReviewToggle()
    {
        var active = cleanupSyntheticReview is not null;
        var entry = ResolveCleanupSyntheticReviewEntry();
        var label = active
            ? "Return to live cleanup##SquireCleanupSynthetic"
            : "Load deterministic review##SquireCleanupSynthetic";
        if (DalamudUiControls.Button(
                label,
                SquireUiTheme.Current,
                DalamudUiTone.Neutral,
                quiet: true,
                enabled: entry.Enabled))
            ToggleCleanupSyntheticReview();
        RegisterLastControl(
            SquireCleanupReviewedControlIds.SyntheticReview,
            active ? "Return to live Cleanup" : "Load deterministic Cleanup review",
            AgentBridgeUiControlKind.Button,
            entry.Enabled,
            active,
            entry.Status,
            ToggleCleanupSyntheticReview);
    }

    private void DrawCleanupSyntheticReviewHeader()
    {
        DalamudUiChrome.DrawCallout(
            "SquireCleanupSyntheticReview",
            "Deterministic cleanup review",
            "Frozen equipment evidence exercises ready-state presentation. Cleanup confirmation and execution remain unavailable.",
            SquireUiTheme.Current,
            DalamudUiTone.Warning);
        ImGui.Spacing();
        foreach (var scenario in CleanupSyntheticScenarioOrder)
        {
            if (scenario != CleanupSyntheticScenarioOrder[0])
                ImGui.SameLine();
            var selected = cleanupSyntheticReview?.Scenario == scenario;
            if (DalamudUiControls.SegmentedOption(
                    $"{CleanupSyntheticScenarioLabel(scenario)}##SquireCleanupSynthetic{scenario}",
                    selected,
                    SquireUiTheme.Current,
                    new(112f, 0)))
                LoadCleanupSyntheticScenario(scenario);
            var captured = scenario;
            RegisterLastControl(
                SquireCleanupReviewedControlIds.ForScenario(scenario),
                $"Show {CleanupSyntheticScenarioLabel(scenario)} Cleanup review",
                AgentBridgeUiControlKind.Select,
                true,
                selected,
                CleanupSyntheticScenarioLabel(scenario),
                () => LoadCleanupSyntheticScenario(captured));
        }
        ImGui.Spacing();
    }

    private void ToggleCleanupSyntheticReview()
    {
        var entry = ResolveCleanupSyntheticReviewEntry();
        if (!entry.Enabled)
            return;
        if (cleanupSyntheticReview is not null)
        {
            cleanupSyntheticReview = null;
            return;
        }

        if (entry.InvalidateLiveAuthorization)
            InvalidateRunAuthorization();
        cleanupSyntheticReview = SquireCleanupSyntheticReview.Create(SquireCleanupSyntheticScenarioKind.CompleteZero);
    }

    private SquireCleanupSyntheticReviewEntry ResolveCleanupSyntheticReviewEntry() =>
        SquireCleanupSyntheticReviewAuthority.ResolveEntry(
            cleanupSyntheticReview is not null,
            activeRun is { IsCompleted: false },
            activeRunRecovery is { IsCompleted: false });

    private void LoadCleanupSyntheticScenario(SquireCleanupSyntheticScenarioKind scenario)
    {
        if (!SquireCleanupSyntheticReviewAuthority.AllowsScenarioMutation(cleanupSyntheticReview is not null))
            return;
        cleanupSyntheticReview = SquireCleanupSyntheticReview.Create(scenario);
    }

    private static string CleanupSyntheticScenarioLabel(SquireCleanupSyntheticScenarioKind scenario) => scenario switch
    {
        SquireCleanupSyntheticScenarioKind.Populated => "Populated",
        SquireCleanupSyntheticScenarioKind.FilteredZero => "No matches",
        SquireCleanupSyntheticScenarioKind.InvalidLastValid => "Invalid filter",
        SquireCleanupSyntheticScenarioKind.HiddenSelected => "Hidden selected",
        _ => "Complete zero",
    };

#endif

    private static void DrawDiagnostics(SquireAnalysis value)
    {
        var diagnostics = value.Snapshot.Diagnostics.Components
            .Where(component => component.Status != Franthropy.Dalamud.Characters.SnapshotComponentStatus.Complete)
            .ToArray();
        if (!ImGui.BeginTable("##SquireSnapshotDiagnosticGrid", 2, ImGuiTableFlags.SizingStretchSame))
            return;
        foreach (var diagnostic in diagnostics)
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Error, diagnostic.Component.ToString());
            ImGui.SameLine();
            ImGui.TextWrapped($"— {diagnostic.Status}: {diagnostic.Message}");
        }
        ImGui.EndTable();
    }

    private void DrawTable(SquireAnalysis value, SquireCandidate[] filteredRows)
    {
        var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.ScrollX | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable |
                         ImGuiTableFlags.Hideable | ImGuiTableFlags.Sortable;
        var tableHeight = filteredRows.Length == 0
            ? Math.Max(88f, ImGui.GetTextLineHeightWithSpacing() * 4f)
            : Math.Max(260f, ImGui.GetContentRegionAvail().Y * 0.62f);
        if (!ImGui.BeginTable("##SquireCandidatesV3", SquireCandidateTableProjection.ColumnCount, tableFlags, new System.Numerics.Vector2(0, tableHeight)))
            return;
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.NoHide, 180);
        ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthFixed, 135);
        ImGui.TableSetupColumn("Equip Lv", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 65);
        ImGui.TableSetupColumn("Item Lv", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 60);
        ImGui.TableSetupColumn("Rarity", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 105);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 65);
        ImGui.TableSetupColumn("Copies", ImGuiTableColumnFlags.WidthFixed, 125);
        ImGui.TableSetupColumn("Materia", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 65);
        ImGui.TableSetupColumn("Condition", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 75);
        ImGui.TableSetupColumn("Inferred wearer", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Assessment", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Disposition", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Row state", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 105);
        ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.WidthFixed, 320);
        ImGui.TableSetupColumn("Item ID", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 75);
        ImGui.TableSetupScrollFreeze(1, 2);
        if (cleanupColumnMenuRequest.Consume())
            ImGuiP.TableOpenContextMenu();
        ImGui.TableHeadersRow();
        if (ActiveCleanupWorkbench.ShowBatchOnly)
            ImGui.BeginDisabled();
        DrawColumnFilters();
        if (ActiveCleanupWorkbench.ShowBatchOnly)
            ImGui.EndDisabled();
        var rows = SquireCandidateTableProjection.Sort(filteredRows, ImGui.TableGetSortSpecs(), FormatRowState);
        var orderedFingerprints = rows.Select(row => row.Instance.Fingerprint).ToArray();
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var candidate = rows[rowIndex];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var fingerprint = candidate.Instance.Fingerprint;
            var selected = ActiveCleanupWorkbench.TableSelection.IsSelected(fingerprint);
            var itemCursor = ImGui.GetCursorPos();
            var itemWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);
            var itemHeight = Math.Max(ImGui.GetTextLineHeightWithSpacing(), ImGui.CalcTextSize(candidate.Definition.Name, false, itemWidth).Y);
            var interaction = DalamudTableSelectionRenderer.DrawRow(
                $"##SquireRow{fingerprint.Container}{fingerprint.SlotIndex}",
                selected,
                new System.Numerics.Vector2(0, itemHeight));
            if (interaction.Activated)
            {
                ActiveCleanupWorkbench.FocusedItem = fingerprint;
                if (ActiveCleanupWorkbench.SelectionMode)
                {
                    var io = ImGui.GetIO();
                    var previous = ActiveCleanupWorkbench.TableSelection.SelectedKeys.ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
                    ActiveCleanupWorkbench.TableSelection.ApplyClick(
                        orderedFingerprints,
                        rowIndex,
                        io.KeyCtrl,
                        io.KeyShift,
                        io.KeyAlt);
                    ReconcileTableSelection(value, previous);
                }
            }
            if (ActiveCleanupWorkbench.SelectionMode &&
                ActiveCleanupWorkbench.TableSelection.IsDragging &&
                interaction.Hovered &&
                ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                var previous = ActiveCleanupWorkbench.TableSelection.SelectedKeys.ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
                ActiveCleanupWorkbench.TableSelection.ApplyDrag(orderedFingerprints, rowIndex);
                ReconcileTableSelection(value, previous);
            }
            RegisterLastControl(
                $"squire.focus.{fingerprint.Container}.{fingerprint.SlotIndex}",
                $"Inspect {candidate.Definition.Name}",
                AgentBridgeUiControlKind.Button,
                true,
                ActiveCleanupWorkbench.FocusedItem is { } focused && EquipmentInstanceFingerprintComparer.Instance.Equals(focused, fingerprint),
                FormatAssessment(candidate.Assessment),
                () => ActiveCleanupWorkbench.FocusedItem = fingerprint);
            if (candidate.IsExecutable)
            {
                var controlId = $"squire.select.{fingerprint.Container}.{fingerprint.SlotIndex}";
                RegisterLastControl(
                    controlId,
                    $"Select {candidate.Definition.Name}",
                    AgentBridgeUiControlKind.Toggle,
                    true,
                    selected,
                    candidate.RecommendedDisposition.ToString(),
                    () =>
                    {
                        ActiveCleanupWorkbench.FocusedItem = fingerprint;
                        SetSelection(value, candidate, !ActiveCleanupWorkbench.TableSelection.IsSelected(fingerprint));
                    });
            }
            else
            {
                RegisterLastControl(
                    $"squire.inspect.{fingerprint.Container}.{fingerprint.SlotIndex}",
                    $"Toggle inspection-only selection for {candidate.Definition.Name}",
                    AgentBridgeUiControlKind.Toggle,
                    true,
                    selected,
                    FormatAssessment(candidate.Assessment),
                    () =>
                    {
                        ActiveCleanupWorkbench.FocusedItem = fingerprint;
                        SetSelection(value, candidate, !ActiveCleanupWorkbench.TableSelection.IsSelected(fingerprint));
                    });
            }
            ImGui.SetCursorPos(itemCursor);
            ImGui.PushTextWrapPos(itemCursor.X + itemWidth);
            SquireEvidencePanel.DrawItemLink(value, candidate);
            ImGui.PopTextWrapPos();
            Cell(FormatLocation(candidate.Instance.Fingerprint));
            Cell(candidate.Definition.EquipLevel.ToString());
            Cell(candidate.Definition.ItemLevel.ToString());
            Cell(SquireCandidateTableProjection.FormatRarity(candidate.Definition.NormalizedRarity));
            Cell(candidate.Instance.Fingerprint.IsHighQuality ? "HQ" : "Normal");
            Cell(SquireCandidateTableProjection.FormatCopies(candidate));
            if (ImGui.IsItemHovered() && candidate.DuplicateStatus is { } duplicate)
                ImGui.SetTooltip($"Owned: {duplicate.OwnedCopies}\nExplicit minimum: {duplicate.UserMinimumCopies}\nSaved-gearset minimum: {duplicate.GearsetRequiredCopies}\nEffective minimum: {duplicate.EffectiveMinimumCopies}\nCopies above this floor: {duplicate.CopiesAboveFloor}");
            Cell(candidate.Instance.Fingerprint.MateriaIds.Count.ToString());
            Cell(SquireCandidateTableProjection.FormatCondition(candidate.Instance.Fingerprint.Condition));
            var wearer = EquipmentWearerInference.Infer(candidate.Definition);
            Cell(wearer.Label);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Source: {wearer.Source}\nGame category: {candidate.Definition.ClassJobCategoryName ?? "unavailable"}");
            Cell(FormatAssessment(candidate.Assessment));
            Cell(FormatDisposition(candidate.RecommendedDisposition));
            Cell(FormatRowState(candidate));
            if (ImGui.TableNextColumn())
            {
                ImGui.TextUnformatted(FormatReasonSummary(candidate));
                if (ImGui.IsItemHovered())
                    DrawReasonTooltip(candidate);
            }
            Cell(candidate.Definition.ItemId.ToString());
        }
        if (ActiveCleanupWorkbench.TableSelection.IsDragging && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            ActiveCleanupWorkbench.TableSelection.EndDrag();
        ImGui.EndTable();
    }

    private SquireCandidate[] ResolveVisibleCandidates(SquireAnalysis value)
    {
        var baseRows = ActiveCleanupWorkbench.ShowBatchOnly
            ? value.Candidates.Where(candidate => ActiveCleanupWorkbench.Review.Selections.ContainsKey(candidate.Instance.Fingerprint)).ToArray()
            : ActiveCleanupWorkbench.Filter.Apply(
                value.Candidates
                    .Where(candidate => ActiveCleanupWorkbench.ShowNonEquipment || candidate.Definition.IsEquipment)
                    .Where(candidate => ActiveCleanupWorkbench.ShowProtected || candidate.Assessment is not (SquireAssessment.Protected or SquireAssessment.EvaluationFailure)),
                ActiveCleanupWorkbench.Search);
        return ActiveCleanupWorkbench.ShowBatchOnly
            ? baseRows
            : SquireCandidateTableProjection.Filter(baseRows, ActiveCleanupWorkbench.ColumnFilters, FormatRowState);
    }

    private void DrawBatchBar()
    {
        var selections = ActiveCleanupWorkbench.Review.Selections;
        var expertDelivery = selections.Count(pair => pair.Value == SquireDisposition.ExpertDelivery);
        var desynthesis = selections.Count(pair => pair.Value == SquireDisposition.Desynthesize);
        var vendor = selections.Count(pair => pair.Value == SquireDisposition.VendorSell);
        var discard = selections.Count(pair => pair.Value == SquireDisposition.Discard);
        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Header,
            $"Cleanup batch: {selections.Count} | Expert Delivery {expertDelivery} | Desynthesize {desynthesis} | Vendor {vendor} | Discard {discard}");
        if (ActiveCleanupWorkbench.HiddenBatchCount > 0 && !ActiveCleanupWorkbench.ShowBatchOnly)
        {
            ImGui.SameLine();
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, $"{ActiveCleanupWorkbench.HiddenBatchCount} hidden by filters");
        }
        var editedShowBatchOnly = ActiveCleanupWorkbench.ShowBatchOnly;
        ImGui.SameLine();
        if (ImGui.Checkbox("Show batch only", ref editedShowBatchOnly))
            ActiveCleanupWorkbench.ShowBatchOnly = editedShowBatchOnly;
        RegisterLastControl(
            "squire.show-batch-only",
            "Show cleanup-batch rows only",
            AgentBridgeUiControlKind.Toggle,
            true,
            ActiveCleanupWorkbench.ShowBatchOnly,
            ActiveCleanupWorkbench.HiddenBatchCount > 0 ? $"{ActiveCleanupWorkbench.HiddenBatchCount} hidden" : null,
            () => ActiveCleanupWorkbench.ShowBatchOnly = !ActiveCleanupWorkbench.ShowBatchOnly);
        if (!string.IsNullOrWhiteSpace(reconciliationNotice))
            ImGui.TextWrapped(reconciliationNotice);
    }

    private string FormatRowState(SquireCandidate candidate)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        if (ActiveCleanupWorkbench.Review.Selections.ContainsKey(fingerprint))
            return "Cleanup batch";
        if (ActiveCleanupWorkbench.TableSelection.IsSelected(fingerprint))
            return candidate.IsExecutable ? "Inspected" : "Inspection only";
        return ActiveCleanupWorkbench.FocusedItem is { } focused && EquipmentInstanceFingerprintComparer.Instance.Equals(focused, fingerprint) ? "Focused" : "—";
    }

    private void ReconcileTableSelection(
        SquireAnalysis analysis,
        IReadOnlySet<EquipmentInstanceFingerprint> previous)
    {
        var changed = false;
        foreach (var candidate in analysis.Candidates)
        {
            var fingerprint = candidate.Instance.Fingerprint;
            var selected = ActiveCleanupWorkbench.TableSelection.IsSelected(fingerprint);
            if (selected != previous.Contains(fingerprint))
            {
                ReconcileSelectionReview(analysis, candidate, selected);
                changed = true;
            }
        }
        if (changed && !IsCleanupSyntheticReviewActive)
            InvalidateRunAuthorization();
    }

    private void SetSelection(SquireAnalysis analysis, SquireCandidate candidate, bool selected)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        var changed = ActiveCleanupWorkbench.TableSelection.SetSelected(fingerprint, selected);
        ReconcileSelectionReview(analysis, candidate, selected);
        if (changed && !IsCleanupSyntheticReviewActive)
            InvalidateRunAuthorization();
    }

    private void ReconcileSelectionReview(SquireAnalysis analysis, SquireCandidate candidate, bool selected)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        if (selected && candidate.IsExecutable && !ActiveCleanupWorkbench.Review.Selections.ContainsKey(fingerprint))
            ActiveCleanupWorkbench.Review.TrySelect(analysis, fingerprint, candidate.RecommendedDisposition);
        else if ((!selected || !candidate.IsExecutable) && ActiveCleanupWorkbench.Review.Selections.ContainsKey(fingerprint))
            ActiveCleanupWorkbench.Review.Remove(fingerprint);
    }

    public void DrawDiagnosticTools() => routeDiagnosticsPanel.Draw(analysis, cleanupWorkbench.FocusedItem);

    private void ClearSelectionOnly()
    {
        ActiveCleanupWorkbench.TableSelection.Clear();
        ActiveCleanupWorkbench.Review.Clear();
        if (!IsCleanupSyntheticReviewActive)
            InvalidateRunAuthorization();
    }

    private void InvalidateRunAuthorization()
    {
        runConfirmed = false;
        confirmedBatchKey = null;
        batchValidationKey = null;
        cachedBatchValidation = null;
    }

    private void DrawColumnFilters()
    {
        ImGui.TableNextRow();
        var filters = ActiveCleanupWorkbench.ColumnFilters;
        for (var column = 0; column < filters.Length; column++)
        {
            if (!ImGui.TableSetColumnIndex(column))
            {
                // A hidden column must not retain a filter that invisibly removes rows.
                filters[column] = string.Empty;
                continue;
            }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint($"##SquireColumnFilter{column}", "Filter...", ref filters[column], 96);
        }
    }

    internal static SquireCandidate[] ApplyColumnFilters(IEnumerable<SquireCandidate> rows, IReadOnlyList<string> filters) =>
        SquireCandidateTableProjection.Filter(rows, filters);

    private SquireProtectionPolicy CreateProtectionPolicy(ulong? contentId) => ruleStore.CreatePolicy(contentId);

    internal static SquireCandidate[] SortCandidates(SquireCandidate[] rows, ImGuiTableSortSpecsPtr sortSpecs) =>
        SquireCandidateTableProjection.Sort(rows, sortSpecs);

    internal static string FormatReasons(SquireCandidate candidate) => SquirePresentation.FormatReasons(candidate);

    internal static string FormatLocation(EquipmentInstanceFingerprint fingerprint) => SquirePresentation.FormatLocation(fingerprint);

    internal static string FormatContainer(string container) => SquirePresentation.FormatContainer(container);

    internal static string FormatDisposition(SquireDisposition disposition) => SquirePresentation.FormatDisposition(disposition);

    internal static string FormatAssessment(SquireAssessment assessment) => SquirePresentation.FormatAssessment(assessment);

    internal static string FormatReasonSummary(SquireCandidate candidate) => SquirePresentation.FormatReasonSummary(candidate);

    internal static string ReasonLabel(string code) => SquirePresentation.ReasonLabel(code);

    private static void DrawReasonTooltip(SquireCandidate candidate)
    {
        var maximumWidth = Math.Max(1f, ImGui.GetMainViewport().Size.X * 0.5f);
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + maximumWidth);
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Rule outcomes");
        foreach (var reason in candidate.Reasons)
        {
            ImGui.BulletText($"{ReasonLabel(reason.Code)} — {reason.Severity}");
        }
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Select the row for the supporting evidence.");
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private void DrawRunPanel(SquireAnalysis value)
    {
        ImGui.Separator();
        var selections = ActiveCleanupWorkbench.Review.Selections;
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Cleanup batch authorization");
        ImGui.TextUnformatted($"Selected: {selections.Count} | Expert Delivery: {selections.Count(pair => pair.Value == SquireDisposition.ExpertDelivery)} | Desynthesize: {selections.Count(pair => pair.Value == SquireDisposition.Desynthesize)} | Vendor: {selections.Count(pair => pair.Value == SquireDisposition.VendorSell)} | Discard: {selections.Count(pair => pair.Value == SquireDisposition.Discard)}");
        if (!value.Snapshot.Diagnostics.IsComplete)
        {
            var blockText = value.Snapshot.Identity.Scope is null
                ? "Cleanup is unavailable while character data is unavailable."
                : "Execution blocked: snapshot is incomplete.";
            ImGui.TextColored(
                value.Snapshot.Identity.Scope is null ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Error,
                blockText);
        }
        if (ActiveCleanupWorkbench.HiddenBatchCount > 0 && !ActiveCleanupWorkbench.ShowBatchOnly)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, $"Execution blocked: {ActiveCleanupWorkbench.HiddenBatchCount} cleanup-batch item(s) are hidden by the current filters. Enable 'Show batch only' to inspect the complete batch.");
        var running = !IsCleanupSyntheticReviewActive && activeRun is { IsCompleted: false };
        var supportedBatch = selections.Values.All(disposition => disposition is
            SquireDisposition.ExpertDelivery or SquireDisposition.Desynthesize or SquireDisposition.VendorSell or SquireDisposition.Discard);
        var batchValidation = IsCleanupSyntheticReviewActive ? null : ValidateSelectedBatch(value, selections);
        var canRun = SquireCleanupRunAuthorization.Resolve(
            IsCleanupSyntheticReviewActive,
            value.Snapshot.Diagnostics.IsComplete,
            selections.Count,
            supportedBatch,
            ActiveCleanupWorkbench.HiddenBatchCount,
            batchValidation?.Success == true,
            running);
        var displayedRunConfirmed = IsCleanupSyntheticReviewActive ? false : runConfirmed;
        if (!canRun)
            ImGui.BeginDisabled();
        if (ImGui.Checkbox("I confirm this cleanup batch", ref displayedRunConfirmed))
        {
            if (SquireCleanupSyntheticReviewAuthority.AllowsLiveRunMutation(IsCleanupSyntheticReviewActive))
            {
                runConfirmed = displayedRunConfirmed;
                confirmedBatchKey = runConfirmed ? batchValidationKey : null;
            }
        }
        RegisterLastControl(
            SquireCleanupRunControlIds.Confirm,
            "Confirm the cleanup batch",
            AgentBridgeUiControlKind.Toggle,
            canRun,
            displayedRunConfirmed,
            IsCleanupSyntheticReviewActive
                ? "Deterministic review never authorizes cleanup."
                : batchValidation is null
                    ? "No cleanup-batch selection."
                : $"{batchValidation.Code}: {batchValidation.Message}",
            () =>
            {
                if (!SquireCleanupSyntheticReviewAuthority.AllowsLiveRunMutation(IsCleanupSyntheticReviewActive))
                    return;
                runConfirmed = !runConfirmed;
                confirmedBatchKey = runConfirmed ? batchValidationKey : null;
            });
        if (!canRun)
            ImGui.EndDisabled();

        var runEnabled = canRun && runConfirmed && string.Equals(confirmedBatchKey, batchValidationKey, StringComparison.Ordinal);
        if (!runEnabled)
            ImGui.BeginDisabled();
        if (ImGui.Button("Run cleanup with diagnostics##Squire"))
            StartDiagnosticRun(value);
        RegisterLastControl(
            SquireCleanupRunControlIds.Diagnostic,
            "Run the explicitly confirmed cleanup batch with catchall UI-state recording enabled",
            AgentBridgeUiControlKind.Button,
            runEnabled,
            false,
            selections.Count.ToString(),
            () => StartDiagnosticRun(value));
        if (!runEnabled)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (!runEnabled)
            ImGui.BeginDisabled();
        if (ImGui.Button("Run selected cleanup##Squire"))
            StartRun(value);
        RegisterLastControl(
            SquireCleanupRunControlIds.Cleanup,
            "Run the explicitly confirmed cleanup batch using each item's disposition",
            AgentBridgeUiControlKind.Button,
            runEnabled,
            false,
            selections.Count.ToString(),
            () => StartRun(value));
        if (!runEnabled)
            ImGui.EndDisabled();
        if (!supportedBatch && selections.Count > 0)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Execution is not implemented for one or more selected dispositions.");
        if (selections.Count > 0 && batchValidation?.Success == false)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, $"Batch is not safe: {batchValidation.Message}");
        if (selections.Values.Contains(SquireDisposition.ExpertDelivery))
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Expert deliveries travel to your Grand Company through Lifestream, then open the delivery desk automatically.");
        if (selections.Values.Contains(SquireDisposition.VendorSell))
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Vendor sales travel to the local market board, approach a sheet-classified gil vendor, and sell through the normal Shop item menu.");
        if (selections.Values.Contains(SquireDisposition.Discard))
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, "Discard is irreversible. Squire confirms the exact item and visible discard prompt immediately before each removal.");
        if (running)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel active Squire run##Squire"))
                runCancellation?.Cancel();
            RegisterLastControl(
                SquireCleanupRunControlIds.Cancel,
                "Cancel the active Squire cleanup run",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                null,
                () => runCancellation?.Cancel());
        }

    }

    private void StartRun(SquireAnalysis value)
    {
        if (!SquireCleanupSyntheticReviewAuthority.AllowsLiveRunMutation(IsCleanupSyntheticReviewActive))
            return;
        _ = ValidateSelectedBatch(value, cleanupWorkbench.Review.Selections);
        if (!runConfirmed || !string.Equals(confirmedBatchKey, batchValidationKey, StringComparison.Ordinal) || activeRun is { IsCompleted: false })
            return;
        try
        {
            var plan = new SquireActionPlanner().Create(value, cleanupWorkbench.Review.Selections, DateTimeOffset.UtcNow,
                CreateProtectionPolicy(value.Snapshot.Identity.Scope?.LocalContentId), capabilitySource.Capture());
            runConfirmed = false;
            runCancellation = new CancellationTokenSource();
            activeRun = RunAsync(plan, runCancellation.Token);
            operationalStatus.ReportProgress(
                SquireOperationalStatusSource.Run,
                $"Started explicitly confirmed cleanup run for {plan.Actions.Count} item(s).",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            operationalStatus.ReportBoundary(SquireOperationalStatusSource.Run, $"Run blocked: {ex.Message}", DateTimeOffset.UtcNow);
        }
    }

    private SquireBatchValidationResult? ValidateSelectedBatch(
        SquireAnalysis value,
        IReadOnlyDictionary<EquipmentInstanceFingerprint, SquireDisposition> selections)
    {
        if (selections.Count == 0)
            return null;
        var policy = CreateProtectionPolicy(value.Snapshot.Identity.Scope?.LocalContentId);
        var capabilities = capabilitySource.Capture();
        var key = SquireAnalysisInputSignature.Create(value.Snapshot, capabilities, policy) + "|selections=" + string.Join(";", selections
            .OrderBy(pair => pair.Key.Container, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.SlotIndex)
            .Select(pair => $"{pair.Key.Container}:{pair.Key.SlotIndex}:{pair.Key.ItemId}:{pair.Value}"));
        if (string.Equals(batchValidationKey, key, StringComparison.Ordinal))
            return cachedBatchValidation;
        batchValidationKey = key;
        cachedBatchValidation = batchValidator.Validate(value.Snapshot, selections, capabilities, policy);
        return cachedBatchValidation;
    }

    private void StartDiagnosticRun(SquireAnalysis value)
    {
        if (!SquireCleanupSyntheticReviewAuthority.AllowsLiveRunMutation(IsCleanupSyntheticReviewActive))
            return;
        _ = ValidateSelectedBatch(value, cleanupWorkbench.Review.Selections);
        if (!runConfirmed || !string.Equals(confirmedBatchKey, batchValidationKey, StringComparison.Ordinal) || activeRun is { IsCompleted: false })
            return;
        if (uiStateCapture.IsRecording)
        {
            operationalStatus.ReportBoundary(
                SquireOperationalStatusSource.Run,
                "Diagnostic run blocked: the catchall UI-state recorder is already active.",
                DateTimeOffset.UtcNow);
            return;
        }
        try
        {
            var plan = new SquireActionPlanner().Create(value, cleanupWorkbench.Review.Selections, DateTimeOffset.UtcNow,
                CreateProtectionPolicy(value.Snapshot.Identity.Scope?.LocalContentId), capabilitySource.Capture());
            runConfirmed = false;
            runCancellation = new CancellationTokenSource();
            uiStateCapture.Start("squire-cleanup-diagnostic");
            uiStateCapture.Mark("squire-diagnostic-start", new Dictionary<string, string?>
            {
                ["actionCount"] = plan.Actions.Count.ToString(),
                ["snapshotGenerationId"] = plan.SnapshotGenerationId.ToString(),
            });
            activeRun = DiagnosticRunAsync(plan, runCancellation.Token);
            operationalStatus.ReportProgress(
                SquireOperationalStatusSource.Run,
                $"Started destructive cleanup run with diagnostics for {plan.Actions.Count} item(s).",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            operationalStatus.ReportBoundary(SquireOperationalStatusSource.Run, $"Diagnostic run blocked: {ex.Message}", DateTimeOffset.UtcNow);
        }
    }

    private async Task DiagnosticRunAsync(SquireActionPlan plan, CancellationToken cancellationToken, bool checkpointResume = false)
    {
        SquireRunResult result;
        try
        {
            var runner = new SquireRunner(actionAdapter, runEvent =>
            {
                uiStateCapture.Mark($"squire-{runEvent.Kind}", new Dictionary<string, string?>
                {
                    ["code"] = runEvent.Code,
                    ["message"] = runEvent.Message,
                    ["container"] = runEvent.Item?.Container,
                    ["slotIndex"] = runEvent.Item?.SlotIndex.ToString(),
                    ["itemId"] = runEvent.Item?.ItemId.ToString(),
                });
                if (runEvent.Kind is "DispositionGroupStart" or "DiagnosticActionStart")
                    operationalStatus.ReportProgress(SquireOperationalStatusSource.Run, runEvent.Message, DateTimeOffset.UtcNow);
            });
            result = checkpointResume
                ? await runner.ResumeFromCheckpointAsync(plan, diagnostic: true, cancellationToken: cancellationToken)
                : await runner.RunDiagnosticAsync(plan, explicitlyConfirmed: true, cancellationToken);
        }
        finally
        {
            // Preserve at least one complete game/UI state sample even when recovery rejects the
            // run before the next ordinary framework update.
            await Plugin.Framework.DelayTicks(1).ConfigureAwait(false);
            uiStateCapture.Stop();
            RequestAutomaticRefresh("Post-run equipment refresh", TimeSpan.Zero);
        }
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
        var auditPath = new SquireAuditLog(Path.Combine(diagnosticDirectory, "runs")).Write(plan, result, version);
        lastRun = SquireRunPresentation.Create(plan, result, auditPath);
        var captureName = Path.GetFileName(uiStateCapture.LastCapturePath);
        if (result.Success)
            operationalStatus.ReportSuccess(
                SquireOperationalStatusSource.Run,
                $"Diagnostic cleanup completed. Audit: {Path.GetFileName(auditPath)} | UI capture: {captureName}",
                DateTimeOffset.UtcNow);
        else
            operationalStatus.ReportFailure(
                SquireOperationalStatusSource.Run,
                $"Diagnostic cleanup stopped ({result.Code}). Audit: {Path.GetFileName(auditPath)} | UI capture: {captureName}",
                DateTimeOffset.UtcNow);
    }

    private async Task RunAsync(SquireActionPlan plan, CancellationToken cancellationToken, bool checkpointResume = false)
    {
        try
        {
            var runner = new SquireRunner(actionAdapter, runEvent =>
            {
                if (runEvent.Kind is "DispositionGroupStart" or "ActionStart")
                    operationalStatus.ReportProgress(SquireOperationalStatusSource.Run, runEvent.Message, DateTimeOffset.UtcNow);
            });
            var result = checkpointResume
                ? await runner.ResumeFromCheckpointAsync(plan, diagnostic: false, cancellationToken: cancellationToken)
                : await runner.RunAsync(plan, explicitlyConfirmed: true, cancellationToken);
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown";
            var auditPath = new SquireAuditLog(Path.Combine(diagnosticDirectory, "runs")).Write(plan, result, version);
            lastRun = SquireRunPresentation.Create(plan, result, auditPath);
            if (result.Success)
                operationalStatus.ReportSuccess(SquireOperationalStatusSource.Run, $"Run completed. Audit: {Path.GetFileName(auditPath)}", DateTimeOffset.UtcNow);
            else
                operationalStatus.ReportFailure(SquireOperationalStatusSource.Run, $"Run stopped ({result.Code}). Audit: {Path.GetFileName(auditPath)}", DateTimeOffset.UtcNow);
        }
        finally
        {
            RequestAutomaticRefresh("Post-run equipment refresh", TimeSpan.Zero);
        }
    }

    public void Dispose()
    {
        runCancellation?.Cancel();
        advisorSession.Dispose();
        passiveCraftComposition?.Dispose();
        inventoryChangeMonitor.Dispose();
        routeDiagnosticsPanel.Dispose();
        actionAdapter.ReleaseOwnedState();
        runCancellation?.Dispose();
    }

    private void RetryLastRunFromCheckpoint()
    {
        if (!SquireCleanupSyntheticReviewAuthority.AllowsLiveRunMutation(IsCleanupSyntheticReviewActive) ||
            lastRun is not { Retryable.Count: > 0 } run ||
            activeRun is { IsCompleted: false })
            return;

        var checkpointPlan = run.CreateCheckpointPlan();
        if (run.WasDiagnostic && uiStateCapture.IsRecording)
        {
            operationalStatus.ReportBoundary(
                SquireOperationalStatusSource.Run,
                "Checkpoint retry blocked: the catchall UI-state recorder is already active.",
                DateTimeOffset.UtcNow);
            return;
        }

        runCancellation?.Dispose();
        runCancellation = new CancellationTokenSource();
        lastRun = null;
        if (run.WasDiagnostic)
        {
            uiStateCapture.Start("squire-cleanup-checkpoint-retry");
            uiStateCapture.Mark("squire-checkpoint-retry", new Dictionary<string, string?>
            {
                ["actionCount"] = checkpointPlan.Actions.Count.ToString(),
                ["snapshotGenerationId"] = checkpointPlan.SnapshotGenerationId.ToString(),
            });
            activeRun = DiagnosticRunAsync(checkpointPlan, runCancellation.Token, checkpointResume: true);
        }
        else
        {
            activeRun = RunAsync(checkpointPlan, runCancellation.Token, checkpointResume: true);
        }
        operationalStatus.ReportProgress(
            SquireOperationalStatusSource.Run,
            $"Retrying {checkpointPlan.Actions.Count} item(s) from the last approved checkpoint. Completed actions will not repeat.",
            DateTimeOffset.UtcNow);
    }

    private void RecoverLastRunInteraction()
    {
        if (!SquireCleanupSyntheticReviewAuthority.AllowsLiveRunMutation(IsCleanupSyntheticReviewActive) ||
            activeRunRecovery is { IsCompleted: false })
            return;
        activeRunRecovery = RecoverLastRunInteractionAsync();
    }

    private async Task RecoverLastRunInteractionAsync()
    {
        operationalStatus.ReportProgress(SquireOperationalStatusSource.Recovery, "Recovering Squire's owned interaction...", DateTimeOffset.UtcNow);
        var result = await actionAdapter.RecoverOwnedStateAsync(CancellationToken.None).ConfigureAwait(false);
        if (result.Success)
            operationalStatus.ReportSuccess(SquireOperationalStatusSource.Recovery, result.Message, DateTimeOffset.UtcNow);
        else
            operationalStatus.ReportFailure(SquireOperationalStatusSource.Recovery, result.Message, DateTimeOffset.UtcNow);
        if (result.Success)
        {
            lastRun = null;
            RequestAutomaticRefresh("Interaction recovery", TimeSpan.Zero);
        }
    }

    private void OpenLastRunAuditLocation()
    {
        if (lastRun is not { } run)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{run.AuditPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            operationalStatus.ReportFailure(SquireOperationalStatusSource.Audit, $"Could not open the audit location: {ex.Message}", DateTimeOffset.UtcNow);
        }
    }

    private void Export()
    {
        try
        {
            Directory.CreateDirectory(diagnosticDirectory);
            var path = Path.Combine(diagnosticDirectory, $"squire-snapshot-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(analysis, Formatting.Indented));
            operationalStatus.ReportSuccess(SquireOperationalStatusSource.Export, $"Exported {Path.GetFileName(path)}", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            operationalStatus.ReportFailure(SquireOperationalStatusSource.Export, $"Export failed: {ex.Message}", DateTimeOffset.UtcNow);
        }
    }

    private static void Cell(string text)
    {
        if (ImGui.TableNextColumn())
            ImGui.TextUnformatted(text);
    }

    private void RegisterLastControl(
        string id,
        string label,
        AgentBridgeUiControlKind kind,
        bool enabled,
        bool selected,
        string? value,
        Action invoke) =>
        reviewRegistry.RegisterLastItem(id, label, kind, enabled, selected, value, invoke);
}
