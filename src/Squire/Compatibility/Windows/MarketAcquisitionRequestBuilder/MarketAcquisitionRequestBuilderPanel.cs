using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Items;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Windows;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed class MarketAcquisitionRequestBuilderPanel
{
    private readonly IReadOnlyList<DalamudItemOption> itemOptions;
    private readonly CraftAppraisalRequestBuilderController craftAppraisal;
    private readonly MarketAcquisitionRequestBuilderController controller;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly DalamudItemAutocompleteState itemAutocomplete = new();
    private readonly HashSet<uint> expandedEvidenceItems = [];

    private string quantityMode = "AllBelowThreshold";
    private string targetQuantityBuffer = string.Empty;
    private string maxUnitPriceBuffer = string.Empty;
    private string gilCapBuffer = string.Empty;
    private string hqPolicy = "Either";
    private bool isAppraising;

    private MarketAcquisitionRequestDocument document => controller.Document;
    private string status => controller.Status;

    public MarketAcquisitionRequestBuilderPanel(
        Configuration config,
        IDataManager dataManager,
        CraftAppraisalRequestBuilderController craftAppraisal,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest,
        Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.craftAppraisal = craftAppraisal;
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        controller = new MarketAcquisitionRequestBuilderController(
            config,
            syncRequest,
            refreshRequest,
            documentAdopted);
        itemOptions = DalamudItemAutocompleteRenderer.LoadItemOptions(dataManager);
    }

    public MarketAcquisitionRequestDocument CurrentDocument => document;

    public string CurrentIntentHash => controller.CurrentIntentHash;

    public int LineCount => document.Lines.Count;

    public void MarkPlanPrepared(string planHash) => controller.MarkPlanPrepared(planHash);

    public void AdoptRequest(MarketAcquisitionRequestView request) => controller.AdoptRequest(request);

    public bool AdoptRestoredRequestIfSafe(MarketAcquisitionRequestView request) =>
        controller.AdoptRestoredRequestIfSafe(request);

    public int StageLines(IEnumerable<MarketAcquisitionRequestLineDocument> lines) =>
        controller.AddLines(lines);

    public void StageOutfitterTransfer(OutfitterWorkbenchTransfer transfer) =>
        controller.StageOutfitterTransfer(transfer);

    public bool FinalizeOutfitterAuthority() => controller.FinalizeOutfitterAuthority();

    public OutfitterWorkbenchAuthorityValidation OutfitterFinalizationValidation =>
        controller.OutfitterFinalizationValidation;

    public int ReturnLines(IEnumerable<uint> itemIds) =>
        controller.RemoveLinesByItemId(itemIds);

    public int MergeComposition(MarketAcquisitionWorkbenchComposition composition) =>
        controller.MergeComposition(composition);

    public void LoadComposition(
        MarketAcquisitionWorkbenchComposition composition,
        string characterName,
        string world) =>
        controller.LoadComposition(composition, characterName, world);

    public void Draw(MarketAcquisitionRequestBuilderContext context, float reservedFooterHeight = 0)
    {
        EnsureCharacterScope(context);
        controller.PumpAutomaticSynchronization(
            context.CharacterName,
            context.World,
            context.HasCharacterScope && !context.IsBusy && !context.IsRouteActive);

        DrawOutfitterAuthority(context);
        DrawRouteScope(context);
        ImGui.Spacing();
        DrawExceptionalStatus(context);
        ImGuiUi.SectionHeader("Buy list", MainWindow.ColHeader);
        DrawCompactLineTable(context, reservedFooterHeight);
    }

    public bool IsSynchronizing => controller.IsSyncing;

    public bool IsRefreshing => controller.IsRefreshing;

    public Task WaitForRefreshAsync() => controller.WaitForRefreshAsync();

    public string SyncStatus => document.SyncStatus;

    public string VisibleStatus => status;

    public MarketAcquisitionRequestValidationResult DraftValidation => controller.DraftValidation;

    public ulong TotalSpendCeiling => document.Lines.Aggregate(
        0ul,
        (total, line) => total + line.GilCap);

    public uint TargetQuantityTotal => document.Lines
        .Where(line => line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase))
        .Aggregate(0u, (total, line) =>
        {
            var sum = (ulong)total + line.TargetQuantity;
            return sum > uint.MaxValue ? uint.MaxValue : (uint)sum;
        });

    private void DrawOutfitterAuthority(MarketAcquisitionRequestBuilderContext context)
    {
        if (document.OutfitterAuthority is not { } authority)
            return;

        ImGuiUi.SectionHeader("Squire solution", MainWindow.ColHeader);
        if (!authority.IsLineageValid)
        {
            ImGui.TextColored(MainWindow.ColError, authority.InvalidationReason ?? "The selected gear solution changed; return to Advisor.");
            ImGui.TextColored(MainWindow.ColMuted, "Historical Advisor lineage is retained, but this Workbench cannot be finalized as that solution.");
            ImGui.Spacing();
            return;
        }

        ImGui.TextColored(MainWindow.ColHeader, authority.Transfer.SelectedSolutionId);
        ImGui.SameLine();
        ImGui.TextColored(MainWindow.ColMuted,
            $"{authority.Lines.Count:N0} exact-quality line(s) · observed {authority.Transfer.ObservedMarketTotalGil:N0} gil");
        if (authority.Transfer.DryRunOnly)
            ImGui.TextColored(MainWindow.ColWarning, "DIAGNOSTIC CONTRACT - permanently restricted to non-spending dry runs");
        var flex = authority.PriceFlexPercent;
        ImGui.SetNextItemWidth(105f);
        var canEdit = !context.IsBusy && !context.IsRouteActive && !IsSynchronizing;
        if (!canEdit)
            ImGui.BeginDisabled();
        if (ImGui.InputInt("Price flexibility %##SquireOutfitterFlex", ref flex, 1, 5))
            controller.UpdateOutfitterPriceFlex(flex);
        if (!canEdit)
            ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextColored(MainWindow.ColMuted,
            $"fixed plan ceiling {authority.SquirePlanCapGil:N0} gil · {authority.RecoveryPolicyId}");
        ImGui.Spacing();
    }

    public void ClearWorkbench(MarketAcquisitionRequestBuilderContext context) => ClearDraft(context);

    private void DrawExceptionalStatus(MarketAcquisitionRequestBuilderContext context)
    {
        if (context.IsRouteActive)
        {
            ImGui.TextColored(MainWindow.ColMuted, "Editing is paused while the active route finishes.");
            ImGui.Spacing();
            return;
        }

        if (!context.HasCharacterScope && !context.CharacterScopeTemporarilyUnavailable)
        {
            ImGui.TextColored(MainWindow.ColError, "Character scope is unavailable; the Workbench cannot synchronize or finalize.");
            ImGui.Spacing();
            return;
        }

        if (document.SyncStatus.Equals("SyncFailed", StringComparison.OrdinalIgnoreCase))
        {
            ImGui.TextColored(MainWindow.ColError, status);
            ImGui.Spacing();
            return;
        }

        if (IsPlanStale(context))
        {
            ImGui.TextColored(MainWindow.ColWarning, "The finalized plan is stale because the buy list changed.");
            ImGui.Spacing();
        }
    }

    private void DrawCompactLineTable(MarketAcquisitionRequestBuilderContext context, float reservedFooterHeight)
    {
        var tableHeight = Math.Max(150f, ImGui.GetContentRegionAvail().Y - Math.Max(0, reservedFooterHeight));
        var flags = AcquisitionRequestTableStyle.LineTableFlags |
                    ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.NoSavedSettings;
        if (!ImGui.BeginTable("AcquisitionWorkbenchLinesV2", 8, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.4f);
        ImGui.TableSetupColumn("Buying rule", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Unit ceiling", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Spend ceiling", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Evidence", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 72f);
        ImGui.TableHeadersRow();

        for (var index = 0; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            DrawCompactLineRow(context, line, index);
            if (expandedEvidenceItems.Contains(line.ItemId))
                DrawCompactEvidenceRow(context, line, index);
        }

        DrawCompactAddRow(context);
        ImGui.EndTable();
    }

    private void DrawCompactLineRow(
        MarketAcquisitionRequestBuilderContext context,
        MarketAcquisitionRequestLineDocument line,
        int index)
    {
        var canEdit = !context.IsBusy && !context.IsRouteActive && !IsSynchronizing;
        var isSquireLine = controller.IsOutfitterLine(index);
        ImGui.PushID($"AcquisitionWorkbenchLine{line.ItemId}_{index}");
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(FormatLineItemName(line));
        ImGui.SameLine();
        var detailsOpen = expandedEvidenceItems.Contains(line.ItemId);
        void ToggleDetails()
        {
            if (expandedEvidenceItems.Contains(line.ItemId))
                expandedEvidenceItems.Remove(line.ItemId);
            else
                expandedEvidenceItems.Add(line.ItemId);
        }
        if (ImGuiUi.Button(detailsOpen ? "Hide" : "Details", true))
            ToggleDetails();
        RegisterLastControl(
            $"acquisition.workbench.line.{line.ItemId}.details",
            $"{(detailsOpen ? "Hide" : "Show")} pricing evidence for {FormatLineItemName(line)}",
            true,
            detailsOpen,
            line.ItemId.ToString(),
            ToggleDetails);

        if (!canEdit || isSquireLine)
            ImGui.BeginDisabled();

        ImGui.TableNextColumn();
        DrawCompactModeCell(line, index);

        ImGui.TableNextColumn();
        DrawCompactQuantityCell(line, index);

        ImGui.TableNextColumn();
        DrawCompactUnitCell(line, index);

        ImGui.TableNextColumn();
        DrawCompactSpendCell(line, index);

        ImGui.TableNextColumn();
        DrawCompactHqCell(line, index);

        ImGui.TableNextColumn();
        DrawCompactEvidenceState(line);

        if (!canEdit || isSquireLine)
            ImGui.EndDisabled();

        ImGui.TableNextColumn();
        if (ImGuiUi.Button("Remove", canEdit))
            RemoveLine(index);
        RegisterLastControl(
            $"acquisition.workbench.line.{line.ItemId}.{line.HqPolicy.ToLowerInvariant()}.remove",
            $"Remove {FormatLineItemName(line)} from the Workbench",
            canEdit,
            false,
            line.ItemId.ToString(),
            () => RemoveLine(index));

        ImGui.PopID();
    }

    private void DrawCompactModeCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var current = string.IsNullOrWhiteSpace(line.QuantityMode) ? "AllBelowThreshold" : line.QuantityMode;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##Mode", FormatEditorOption(current)))
            return;

        foreach (var mode in new[] { "AllBelowThreshold", "TargetQuantity" })
        {
            var selected = string.Equals(mode, current, StringComparison.Ordinal);
            if (ImGui.Selectable(FormatEditorOption(mode), selected))
            {
                ApplyLineEdit(
                    index,
                    line,
                    quantityMode: mode,
                    targetQuantity: mode == "TargetQuantity" ? Math.Max(1u, line.TargetQuantity) : 0,
                    maxQuantity: 0,
                    message: "Buying rule updated.");
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawCompactQuantityCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        if (!line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase))
        {
            if (line.MaxQuantity == 0)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(MainWindow.ColMuted, "-");
                return;
            }

            ImGui.PushStyleColor(ImGuiCol.Text, MainWindow.ColWarning);
            if (ImGui.Button($"Clear cap {line.MaxQuantity:N0}"))
                ApplyLineEdit(index, line, maxQuantity: 0, message: "Legacy quantity cap cleared.");
            ImGui.PopStyleColor();
            return;
        }

        var quantity = ClampToInt(line.TargetQuantity);
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.InputInt("##Quantity", ref quantity))
            return;

        ApplyLineEdit(
            index,
            line,
            targetQuantity: Math.Max(1u, ClampToUInt(quantity)),
            maxQuantity: 0,
            message: "Target quantity updated.");
    }

    private void DrawCompactUnitCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var maxUnit = ClampToInt(line.MaxUnitPrice);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt("##Unit", ref maxUnit))
            ApplyLineEdit(index, line, maxUnitPrice: ClampToUInt(maxUnit), message: "Unit ceiling updated.");
    }

    private void DrawCompactSpendCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var gilCap = ClampToInt(line.GilCap);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt("##Spend", ref gilCap))
            ApplyLineEdit(index, line, gilCap: ClampToUInt(gilCap), message: "Spend ceiling updated.");
    }

    private void DrawCompactHqCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var current = string.IsNullOrWhiteSpace(line.HqPolicy) ? "Either" : line.HqPolicy;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##Quality", FormatEditorOption(current)))
            return;

        foreach (var policy in new[] { "Either", "HQOnly", "NQOnly" })
        {
            var selected = string.Equals(policy, current, StringComparison.Ordinal);
            if (ImGui.Selectable(FormatEditorOption(policy), selected))
                ApplyLineEdit(index, line, hqPolicy: policy, message: "Quality updated.");
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawCompactEvidenceState(MarketAcquisitionRequestLineDocument line)
    {
        var identity = CraftAppraisalRequestMapper.BuildLineIdentity(document, line);
        var threshold = craftAppraisal.State.TryGetLineQuoteThreshold(identity);
        if (threshold is > 0)
            ImGui.TextColored(MainWindow.ColSuccess, "Quote ready");
        else if (line.MaxUnitPrice > 0)
            ImGui.TextColored(MainWindow.ColMuted, "Manual");
        else
            ImGui.TextColored(MainWindow.ColWarning, "Missing");
    }

    private void DrawCompactEvidenceRow(
        MarketAcquisitionRequestBuilderContext context,
        MarketAcquisitionRequestLineDocument line,
        int index)
    {
        var identity = CraftAppraisalRequestMapper.BuildLineIdentity(document, line);
        var threshold = craftAppraisal.State.TryGetLineQuoteThreshold(identity);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColHeader, "Pricing evidence");
        ImGui.SameLine();
        ImGui.TextColored(MainWindow.ColMuted, FormatLineItemName(line));

        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColMuted, "Craft Architect");

        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColMuted, line.QuantityMode == "TargetQuantity" ? $"{line.TargetQuantity:N0} target" : "Unbounded");

        ImGui.TableNextColumn();
        if (threshold is > 0)
            ImGui.TextColored(MainWindow.ColSuccess, $"{threshold.Value:N0} gil");
        else
            ImGui.TextColored(MainWindow.ColMuted, "No quote");

        ImGui.TableNextColumn();
        ImGui.TextColored(
            IsPlanStale(context) ? MainWindow.ColWarning : MainWindow.ColMuted,
            IsPlanStale(context) ? "Plan stale" : context.CurrentPlan is null ? "Not finalized" : "Plan current");

        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColMuted, FormatEditorOption(line.HqPolicy));

        ImGui.TableNextColumn();
        var canQuote = craftAppraisal.State.WorkshopHostEnabled && !isAppraising && line.ItemId != 0;
        if (threshold is > 0)
        {
            if (ImGuiUi.Button("Use quote", canQuote && line.MaxUnitPrice != threshold.Value))
                SetLineMaxUnitPrice(index, threshold.Value, "Unit ceiling set from Craft Architect quote.");
        }
        else if (ImGuiUi.Button("Get quote", canQuote))
        {
            _ = CalculateMaxUnitFromCraftAsync(index);
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.Empty);
    }

    private void DrawCompactAddRow(MarketAcquisitionRequestBuilderContext context)
    {
        var canEdit = !context.IsBusy && !context.IsRouteActive && !IsSynchronizing;
        ImGui.PushID("AcquisitionWorkbenchAddRow");
        ImGui.TableNextRow();

        if (!canEdit)
            ImGui.BeginDisabled();

        ImGui.TableNextColumn();
        DalamudItemAutocompleteRenderer.DrawInline(
            "AcquisitionWorkbenchAdd",
            itemOptions,
            itemAutocomplete,
            MainWindow.ColMuted,
            MainWindow.ColSuccess,
            MainWindow.ColError);

        ImGui.TableNextColumn();
        DrawInlineCombo("##NewRule", ["AllBelowThreshold", "TargetQuantity"], ref quantityMode);

        ImGui.TableNextColumn();
        if (quantityMode == "TargetQuantity")
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##NewQuantity", "Required", ref targetQuantityBuffer, 32);
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(MainWindow.ColMuted, "-");
        }

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##NewUnit", "Unset", ref maxUnitPriceBuffer, 32);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##NewSpend", "Optional", ref gilCapBuffer, 32);

        ImGui.TableNextColumn();
        DrawInlineCombo("##NewQuality", ["Either", "HQOnly", "NQOnly"], ref hqPolicy);

        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColMuted, "After add");

        ImGui.TableNextColumn();
        var canAdd = canEdit && RequestLineInputValidator.CanAddIntentLine(
            itemAutocomplete.SelectedItem,
            quantityMode,
            targetQuantityBuffer,
            string.Empty,
            maxUnitPriceBuffer,
            gilCapBuffer);
        if (ImGuiUi.PrimaryButton("Add", canAdd))
        {
            ApplyEditorLine();
            ClearLineEditor();
        }
        RegisterLastControl(
            "acquisition.workbench.add",
            "Add the inline item to the Workbench",
            canAdd,
            false,
            itemAutocomplete.SelectedItem?.ItemId.ToString(),
            () =>
            {
                ApplyEditorLine();
                ClearLineEditor();
            });

        if (!canEdit)
            ImGui.EndDisabled();
        ImGui.PopID();
    }

    private static void DrawInlineCombo(string id, IReadOnlyList<string> values, ref string current)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(id, FormatEditorOption(current)))
            return;

        foreach (var value in values)
        {
            var selected = string.Equals(value, current, StringComparison.Ordinal);
            if (ImGui.Selectable(FormatEditorOption(value), selected))
                current = value;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private bool IsPlanStale(MarketAcquisitionRequestBuilderContext context) =>
        context.CurrentPlan is not null &&
        !string.IsNullOrWhiteSpace(context.CurrentPlanHash) &&
        !string.Equals(context.CurrentPlanHash, CurrentIntentHash, StringComparison.Ordinal);

    private void DrawRouteScope(MarketAcquisitionRequestBuilderContext context)
    {
        var scope = RequestRouteScope.FromDocument(document);
        RequestRouteScopeSelector.DrawCompact(
            "AcquisitionRequestBuilder",
            scope,
            controller.UpdateRouteScope,
            MainWindow.ColMuted,
            MainWindow.ColError);
    }

    private static string FormatEditorOption(string value) =>
        value switch
        {
            "AllBelowThreshold" => "Buy below ceiling",
            "TargetQuantity" => "Target quantity",
            "Either" => "Any",
            "HQOnly" => "HQ only",
            "NQOnly" => "NQ only",
            _ => value,
        };

    private void SetLineMaxUnitPrice(int index, uint maxUnitPrice, string message)
    {
        controller.SetLineMaxUnitPrice(index, maxUnitPrice, message);
    }

    private void ApplyLineEdit(
        int index,
        MarketAcquisitionRequestLineDocument line,
        string? quantityMode = null,
        uint? targetQuantity = null,
        uint? maxQuantity = null,
        string? hqPolicy = null,
        uint? maxUnitPrice = null,
        uint? gilCap = null,
        string message = "Line updated.")
    {
        controller.ApplyLineEdit(
            index,
            quantityMode ?? line.QuantityMode,
            targetQuantity ?? line.TargetQuantity,
            maxQuantity ?? line.MaxQuantity,
            hqPolicy ?? line.HqPolicy,
            maxUnitPrice ?? line.MaxUnitPrice,
            gilCap ?? line.GilCap,
            message);
    }

    private async Task CalculateMaxUnitFromCraftAsync(int index)
    {
        if (isAppraising || index < 0 || index >= document.Lines.Count)
            return;

        isAppraising = true;
        try
        {
            var line = document.Lines[index];
            var identity = CraftAppraisalRequestMapper.BuildLineIdentity(document, line);
            craftAppraisal.State.UpdateSelectedLine(identity);
            var quote = await craftAppraisal.FetchQuoteAsync(
                CraftAppraisalRequestMapper.Build(document, line)).ConfigureAwait(false);
            craftAppraisal.State.RecordLineQuote(
                identity,
                quote,
                craftAppraisal.State.LastCraftQuoteDiagnosticFilePath);
            var threshold = craftAppraisal.State.TryGetLineQuoteThreshold(identity);
            if (threshold is > 0)
            {
                var currentIndex = CraftAppraisalRequestMapper.FindMatchingLineIndex(document, identity);
                if (currentIndex < 0)
                {
                    controller.SetStatus("Craft Architect quote was kept as evidence but not applied because the Workbench line changed.");
                    return;
                }

                SetLineMaxUnitPrice(currentIndex, threshold.Value, "Unit cost ceiling set from Craft Architect quote.");
                return;
            }

            controller.SetStatus("Craft Architect did not return a usable unit cost ceiling for this line.");
        }
        catch (Exception ex)
        {
            controller.SetStatus($"Craft Architect quote failed: {ex.Message}");
        }
        finally
        {
            isAppraising = false;
        }
    }

    private void ApplyEditorLine()
    {
        if (itemAutocomplete.SelectedItem is not { } item)
            return;

        var line = new MarketAcquisitionRequestLineDocument
        {
            ItemId = item.ItemId,
            ItemName = item.Name,
            QuantityMode = quantityMode,
            TargetQuantity = quantityMode == "TargetQuantity" ? ParseUInt(targetQuantityBuffer) : 0,
            MaxQuantity = 0,
            HqPolicy = hqPolicy,
            MaxUnitPrice = ParseUInt(maxUnitPriceBuffer),
            GilCap = ParseUInt(gilCapBuffer),
        };
        controller.ApplyEditorLine(line);
    }

    private void RemoveLine(int index)
    {
        if (index >= 0 && index < document.Lines.Count)
            expandedEvidenceItems.Remove(document.Lines[index].ItemId);
        if (controller.RemoveLine(index))
            ClearLineEditor();
    }

    private void RemoveLineByItemId(uint itemId)
    {
        var index = document.Lines.FindIndex(line => line.ItemId == itemId);
        if (index >= 0)
            RemoveLine(index);
    }

    private void ClearDraft(MarketAcquisitionRequestBuilderContext context)
    {
        controller.ClearDraft(
            context.HasCharacterScope ? context.CharacterName : string.Empty,
            context.HasCharacterScope ? context.World : string.Empty);
        ClearLineEditor();
    }

    private void EnsureCharacterScope(MarketAcquisitionRequestBuilderContext context)
    {
        if (!context.HasCharacterScope)
            return;

        if (string.Equals(document.TargetCharacterName, context.CharacterName, StringComparison.Ordinal) &&
            string.Equals(document.TargetWorld, context.World, StringComparison.Ordinal))
        {
            return;
        }

        controller.EnsureCharacterScope(context.CharacterName, context.World);
    }

    private void ClearLineEditor()
    {
        itemAutocomplete.SelectedItem = null;
        itemAutocomplete.SearchBuffer = string.Empty;
        quantityMode = "AllBelowThreshold";
        targetQuantityBuffer = string.Empty;
        maxUnitPriceBuffer = string.Empty;
        gilCapBuffer = string.Empty;
        hqPolicy = "Either";
        controller.ClearSelection();
    }

    private static uint ParseUInt(string value) =>
        uint.TryParse(value?.Trim(), out var parsed) ? parsed : 0;

    private static int ClampToInt(uint value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;

    private static uint ClampToUInt(int value) =>
        value <= 0 ? 0u : (uint)value;

    private static string FormatLineItemName(MarketAcquisitionRequestLineDocument line) =>
        string.IsNullOrWhiteSpace(line.ItemName)
            ? $"Item {line.ItemId}"
            : line.ItemName;

    private void RegisterLastControl(
        string id,
        string label,
        bool enabled,
        bool selected,
        string? value,
        Action invoke) =>
        reviewRegistry.RegisterLastItem(
            id,
            label,
            AgentBridgeUiControlKind.Button,
            enabled,
            selected,
            value,
            invoke);

}
