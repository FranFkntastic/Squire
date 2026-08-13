using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
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
    private readonly SquireCounterfactualBatchValidator batchValidator = new();
    private readonly SquireReviewState review = new();
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
    private string search = string.Empty;
    private bool showProtected;
    private bool showNonEquipment;
    private bool selectionMode;
    private readonly TableSelectionModel<EquipmentInstanceFingerprint> tableSelection = new(EquipmentInstanceFingerprintComparer.Instance);
    private readonly string[] columnFilters = new string[SquireCandidateTableProjection.ColumnCount];
    private EquipmentInstanceFingerprint? focusedItem;
    private bool showBatchOnly;
    private int hiddenBatchCount;
    private bool showSnapshotDiagnostics;
    private DateTimeOffset nextAutomaticRefreshAt = DateTimeOffset.MinValue;
    private volatile bool automaticRefreshRequested;
    private string automaticRefreshTrigger = "Automatic refresh";
    private string? reconciliationNotice;
    private string? lastAnalysisInputSignature;
    private readonly SquireOperationalStatusState operationalStatus = new();
    private bool runConfirmed;
    private string? confirmedBatchKey;
    private CancellationTokenSource? runCancellation;
    private Task? activeRun;
    private Task? activeRunRecovery;
    private string? batchValidationKey;
    private SquireBatchValidationResult? cachedBatchValidation;

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
            resolveAcquisitionRegion,
            transfer => stageOutfitterTransfer?.Invoke(transfer));
        workspaceState = new SquireWorkspaceState(config);
        ruleStore = new SquireCleanupRuleStore(config);
        evidencePanel = new SquireEvidencePanel(ruleStore, reviewRegistry, Refresh);
        routeDiagnosticsPanel = new SquireRouteDiagnosticsPanel(actionAdapter, reviewRegistry, uiStateCapture);
        settingsPanel = new SquireSettingsPanel(
            new SquireSettingsState(config, RequestPolicyRefresh, getAgentBridgeAudit, setAgentBridgeAudit),
            reviewRegistry,
            () => SelectWorkspace(SquireWorkspaces.Cleanup),
            () => routeDiagnosticsPanel.Draw(analysis, focusedItem));
        search = config.Squire.Search;
        showProtected = config.Squire.ShowProtected;
        showNonEquipment = config.Squire.ShowNonEquipment;
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
        DalamudUiChrome.DrawSectionHeading(
            "Equipment cleanup",
            "Safe recommendations from current character evidence",
            SquireUiTheme.Current.Palette,
            () =>
            {
                if (DalamudUiControls.Button(
                        "Check again##Squire",
                        SquireUiTheme.Current,
                        DalamudUiTone.Neutral,
                        quiet: true))
                    Refresh();
                RegisterLastControl("squire.refresh", "Refresh Squire analysis", AgentBridgeUiControlKind.Button, true, false, null, Refresh);
            },
            116f);
        ImGui.Spacing();
        if (DalamudUiControls.Button(
                "Export evaluation snapshot##Squire",
                SquireUiTheme.Current,
                DalamudUiTone.Neutral,
                quiet: true,
                enabled: analysis is not null))
            Export();
        RegisterLastControl("squire.export", "Export Squire evaluation snapshot", AgentBridgeUiControlKind.Button, analysis is not null, false, null, Export);
        DrawOperationalStatus();

        if (analysis is null)
        {
            DrawWaitingForAnalysis();
            return;
        }

        DrawSnapshotState(analysis);
        var surfaceState = ResolveCleanupSurfaceState(analysis);
        if (surfaceState is SquireCleanupSurfaceState.WaitingForCharacter or SquireCleanupSurfaceState.SnapshotIncomplete)
            return;

        DrawSummary(analysis);
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

        ImGui.SetNextItemWidth(280);
        if (ImGui.InputTextWithHint("##SquireSearch", "Search item, location, or reason", ref search, 160))
        {
            config.Squire.Search = search;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Show protected", ref showProtected))
        {
            config.Squire.ShowProtected = showProtected;
            config.Save();
        }
        RegisterLastControl(
            "squire.show-protected",
            "Show protected and evaluation-failure rows",
            AgentBridgeUiControlKind.Toggle,
            true,
            showProtected,
            null,
            () =>
            {
                showProtected = !showProtected;
                config.Squire.ShowProtected = showProtected;
                config.Save();
            });
        ImGui.SameLine();
        if (ImGui.Checkbox("Show non-equipment", ref showNonEquipment))
        {
            config.Squire.ShowNonEquipment = showNonEquipment;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.Checkbox("Selection mode", ref selectionMode);
        RegisterLastControl(
            "squire.selection-mode",
            "Toggle Squire selection mode",
            AgentBridgeUiControlKind.Toggle,
            true,
            selectionMode,
            null,
            () => selectionMode = !selectionMode);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Select any rows for inspection. Ctrl-click adds, Alt-click removes, and Shift-click selects the anchored range. Only executable candidates enter the action batch.");
        if (selectionMode && tableSelection.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear selection"))
            {
                ClearSelectionOnly();
            }
        }
        ImGui.SameLine();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Right-click a table header to choose columns.");
        DrawBatchBar();
        DrawTable(analysis);
        evidencePanel.Draw(analysis, focusedItem);
        DrawRunPanel(analysis);
        if (lastRun is { } runPresentation)
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
                reconciliation = review.Reconcile(refreshedAnalysis);
            else
                review.Adopt(refreshedAnalysis);

            analysis = refreshedAnalysis;
            lastAnalysisInputSignature = inputSignature;
            if (reconcileSelections && sameCharacter)
            {
                var currentFingerprints = analysis.Candidates.Select(candidate => candidate.Instance.Fingerprint)
                    .ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
                tableSelection.Retain(currentFingerprints);
                if (focusedItem is { } focused && !currentFingerprints.Contains(focused))
                    focusedItem = null;
            }
            else
            {
                tableSelection.Clear();
                focusedItem = null;
            }
            InvalidateRunAuthorization();
            hiddenBatchCount = 0;
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
                review.Invalidate();
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
        var currentStatus = operationalStatus.Current(DateTimeOffset.UtcNow);
        return new(
            ResolveCleanupSurfaceState(analysis).ToString(),
            analysis?.Snapshot.Identity.CapturedAt,
            analysis?.Snapshot.Diagnostics.IsComplete == true,
            analysis?.Candidates.Count ?? 0,
            analysis?.Candidates.Count(candidate => candidate.IsExecutable) ?? 0,
            review.Selections.Count,
            hiddenBatchCount,
            runConfirmed,
            activeRun is { IsCompleted: false },
            advisorSession.State.Stage.ToString(),
            currentStatus?.Kind.ToString(),
            currentStatus?.Source.ToString(),
            currentStatus?.Message,
            currentStatus?.CreatedAtUtc,
            currentStatus?.ExpiresAtUtc,
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

    private static void DrawWaitingForAnalysis()
    {
        ImGui.Spacing();
        DalamudUiChrome.DrawCallout(
            "SquireInitialAnalysisState",
            "Preparing equipment analysis",
            "Squire is obtaining the first current equipment snapshot. Cleanup controls will appear automatically when it is complete.",
            SquireUiTheme.Current,
            DalamudUiTone.Neutral);
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
            });
        if (showSnapshotDiagnostics)
        {
            ImGui.Spacing();
            DrawDiagnostics(value);
        }
    }

    private void ToggleSnapshotDiagnostics() => showSnapshotDiagnostics = !showSnapshotDiagnostics;

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

    private void DrawTable(SquireAnalysis value)
    {
        var baseRows = showBatchOnly
            ? value.Candidates.Where(candidate => review.Selections.ContainsKey(candidate.Instance.Fingerprint)).ToArray()
            : value.Candidates
                .Where(candidate => showNonEquipment || candidate.Definition.IsEquipment)
                .Where(candidate => showProtected || candidate.Assessment is not (SquireAssessment.Protected or SquireAssessment.EvaluationFailure))
                .Where(candidate => string.IsNullOrWhiteSpace(search)
                    || candidate.Definition.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || candidate.Instance.Fingerprint.Container.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || FormatLocation(candidate.Instance.Fingerprint).Contains(search, StringComparison.OrdinalIgnoreCase)
                    || candidate.Reasons.Any(reason => reason.Message.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.ScrollX | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable |
                         ImGuiTableFlags.Hideable | ImGuiTableFlags.Sortable;
        var tableHeight = Math.Max(260f, ImGui.GetContentRegionAvail().Y * 0.62f);
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
        ImGui.TableHeadersRow();
        if (showBatchOnly)
            ImGui.BeginDisabled();
        DrawColumnFilters();
        if (showBatchOnly)
            ImGui.EndDisabled();
        var filteredRows = showBatchOnly
            ? baseRows
            : SquireCandidateTableProjection.Filter(baseRows, columnFilters, FormatRowState);
        var visibleFingerprints = filteredRows.Select(candidate => candidate.Instance.Fingerprint)
            .ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
        hiddenBatchCount = review.Selections.Keys.Count(fingerprint => !visibleFingerprints.Contains(fingerprint));
        var rows = SquireCandidateTableProjection.Sort(filteredRows, ImGui.TableGetSortSpecs(), FormatRowState);
        var orderedFingerprints = rows.Select(row => row.Instance.Fingerprint).ToArray();
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var candidate = rows[rowIndex];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var fingerprint = candidate.Instance.Fingerprint;
            var selected = tableSelection.IsSelected(fingerprint);
            var itemCursor = ImGui.GetCursorPos();
            var itemWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);
            var itemHeight = Math.Max(ImGui.GetTextLineHeightWithSpacing(), ImGui.CalcTextSize(candidate.Definition.Name, false, itemWidth).Y);
            var interaction = DalamudTableSelectionRenderer.DrawRow(
                $"##SquireRow{fingerprint.Container}{fingerprint.SlotIndex}",
                selected,
                new System.Numerics.Vector2(0, itemHeight));
            if (interaction.Activated)
            {
                focusedItem = fingerprint;
                if (selectionMode)
                {
                    var io = ImGui.GetIO();
                    var previous = tableSelection.SelectedKeys.ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
                    tableSelection.ApplyClick(
                        orderedFingerprints,
                        rowIndex,
                        io.KeyCtrl,
                        io.KeyShift,
                        io.KeyAlt);
                    ReconcileTableSelection(value, previous);
                }
            }
            if (selectionMode &&
                tableSelection.IsDragging &&
                interaction.Hovered &&
                ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                var previous = tableSelection.SelectedKeys.ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
                tableSelection.ApplyDrag(orderedFingerprints, rowIndex);
                ReconcileTableSelection(value, previous);
            }
            RegisterLastControl(
                $"squire.focus.{fingerprint.Container}.{fingerprint.SlotIndex}",
                $"Inspect {candidate.Definition.Name}",
                AgentBridgeUiControlKind.Button,
                true,
                focusedItem is { } focused && EquipmentInstanceFingerprintComparer.Instance.Equals(focused, fingerprint),
                FormatAssessment(candidate.Assessment),
                () => focusedItem = fingerprint);
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
                        focusedItem = fingerprint;
                        SetSelection(value, candidate, !tableSelection.IsSelected(fingerprint));
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
                        focusedItem = fingerprint;
                        SetSelection(value, candidate, !tableSelection.IsSelected(fingerprint));
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
        if (tableSelection.IsDragging && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            tableSelection.EndDrag();
        ImGui.EndTable();
    }

    private void DrawBatchBar()
    {
        var selections = review.Selections;
        var expertDelivery = selections.Count(pair => pair.Value == SquireDisposition.ExpertDelivery);
        var desynthesis = selections.Count(pair => pair.Value == SquireDisposition.Desynthesize);
        var vendor = selections.Count(pair => pair.Value == SquireDisposition.VendorSell);
        var discard = selections.Count(pair => pair.Value == SquireDisposition.Discard);
        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Header,
            $"Cleanup batch: {selections.Count} | Expert Delivery {expertDelivery} | Desynthesize {desynthesis} | Vendor {vendor} | Discard {discard}");
        if (hiddenBatchCount > 0 && !showBatchOnly)
        {
            ImGui.SameLine();
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, $"{hiddenBatchCount} hidden by filters");
        }
        ImGui.SameLine();
        ImGui.Checkbox("Show batch only", ref showBatchOnly);
        RegisterLastControl(
            "squire.show-batch-only",
            "Show cleanup-batch rows only",
            AgentBridgeUiControlKind.Toggle,
            true,
            showBatchOnly,
            hiddenBatchCount > 0 ? $"{hiddenBatchCount} hidden" : null,
            () => showBatchOnly = !showBatchOnly);
        if (!string.IsNullOrWhiteSpace(reconciliationNotice))
            ImGui.TextWrapped(reconciliationNotice);
    }

    private string FormatRowState(SquireCandidate candidate)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        if (review.Selections.ContainsKey(fingerprint))
            return "Cleanup batch";
        if (tableSelection.IsSelected(fingerprint))
            return candidate.IsExecutable ? "Inspected" : "Inspection only";
        return focusedItem is { } focused && EquipmentInstanceFingerprintComparer.Instance.Equals(focused, fingerprint) ? "Focused" : "—";
    }

    private void ReconcileTableSelection(
        SquireAnalysis analysis,
        IReadOnlySet<EquipmentInstanceFingerprint> previous)
    {
        var changed = false;
        foreach (var candidate in analysis.Candidates)
        {
            var fingerprint = candidate.Instance.Fingerprint;
            var selected = tableSelection.IsSelected(fingerprint);
            if (selected != previous.Contains(fingerprint))
            {
                ReconcileSelectionReview(analysis, candidate, selected);
                changed = true;
            }
        }
        if (changed)
            InvalidateRunAuthorization();
    }

    private void SetSelection(SquireAnalysis analysis, SquireCandidate candidate, bool selected)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        var changed = tableSelection.SetSelected(fingerprint, selected);
        ReconcileSelectionReview(analysis, candidate, selected);
        if (changed)
            InvalidateRunAuthorization();
    }

    private void ReconcileSelectionReview(SquireAnalysis analysis, SquireCandidate candidate, bool selected)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        if (selected && candidate.IsExecutable && !review.Selections.ContainsKey(fingerprint))
            review.TrySelect(analysis, fingerprint, candidate.RecommendedDisposition);
        else if ((!selected || !candidate.IsExecutable) && review.Selections.ContainsKey(fingerprint))
            review.Remove(fingerprint);
    }

    public void DrawDiagnosticTools() => routeDiagnosticsPanel.Draw(analysis, focusedItem);

    private void ClearSelectionOnly()
    {
        tableSelection.Clear();
        review.Clear();
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
        for (var column = 0; column < columnFilters.Length; column++)
        {
            if (!ImGui.TableSetColumnIndex(column))
            {
                // A hidden column must not retain a filter that invisibly removes rows.
                columnFilters[column] = string.Empty;
                continue;
            }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint($"##SquireColumnFilter{column}", "Filter...", ref columnFilters[column], 96);
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
        var selections = review.Selections;
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
        if (hiddenBatchCount > 0 && !showBatchOnly)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, $"Execution blocked: {hiddenBatchCount} cleanup-batch item(s) are hidden by the current filters. Enable 'Show batch only' to inspect the complete batch.");
        var running = activeRun is { IsCompleted: false };
        var supportedBatch = selections.Values.All(disposition => disposition is
            SquireDisposition.ExpertDelivery or SquireDisposition.Desynthesize or SquireDisposition.VendorSell or SquireDisposition.Discard);
        var batchValidation = ValidateSelectedBatch(value, selections);
        var canRun = value.Snapshot.Diagnostics.IsComplete && selections.Count > 0 && supportedBatch &&
                     hiddenBatchCount == 0 &&
                     batchValidation?.Success == true && !running;
        if (!canRun)
            ImGui.BeginDisabled();
        if (ImGui.Checkbox("I confirm this cleanup batch", ref runConfirmed))
            confirmedBatchKey = runConfirmed ? batchValidationKey : null;
        RegisterLastControl(
            "squire.run.confirm",
            "Confirm the cleanup batch",
            AgentBridgeUiControlKind.Toggle,
            canRun,
            runConfirmed,
            batchValidation is null
                ? "No cleanup-batch selection."
                : $"{batchValidation.Code}: {batchValidation.Message}",
            () =>
            {
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
            "squire.run.diagnostic",
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
            "squire.run.cleanup",
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
        }

    }

    private void StartRun(SquireAnalysis value)
    {
        _ = ValidateSelectedBatch(value, review.Selections);
        if (!runConfirmed || !string.Equals(confirmedBatchKey, batchValidationKey, StringComparison.Ordinal) || activeRun is { IsCompleted: false })
            return;
        try
        {
            var plan = new SquireActionPlanner().Create(value, review.Selections, DateTimeOffset.UtcNow,
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
        _ = ValidateSelectedBatch(value, review.Selections);
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
            var plan = new SquireActionPlanner().Create(value, review.Selections, DateTimeOffset.UtcNow,
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
        if (lastRun is not { Retryable.Count: > 0 } run || activeRun is { IsCompleted: false })
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
        if (activeRunRecovery is { IsCompleted: false })
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
