using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.Automation.Inventory;
using Lumina.Excel.Sheets;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudSquireActionGameAdapter : ISquireActionGameAdapter
{
    private static readonly DalamudContextMenuOptionSpec DiscardOption = new("Discard", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Discard", "捨てる", "Wegwerfen", "Jeter", "丢弃", "捨棄" });
    private static readonly DalamudContextMenuOptionSpec SellOption = new("Sell", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Sell", "売却する", "Verkaufen", "Vendre", "出售", "出售" });
    private static readonly string[] DiscardPromptTerms = ["Discard", "捨て", "wegwerfen", "Jeter", "丢弃", "捨棄"];
    private static readonly string[] SellPromptTerms = ["Sell", "売却", "verkaufen", "Vendre", "出售"];
    private readonly ICharacterEquipmentSnapshotSource snapshotSource;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly ISquireDispositionCapabilitySource capabilitySource;
    private readonly Func<SquireProtectionPolicy> currentPolicy;
    private readonly Func<SquireExecutionRecoveryPolicy> currentRecoveryPolicy;
    private readonly Func<string?> describeExternalConflict;
    private readonly DalamudSquireRecoveryCoordinator recoveryCoordinator;
    private readonly DalamudDesynthesisUiTransaction desynthesisUi;
    private readonly DalamudMateriaRetrievalUiTransaction materiaRetrievalUi;
    private readonly DalamudExpertDeliveryUiTransaction expertDeliveryUi;
    private readonly DalamudExpertDeliveryPreparation expertDeliveryPreparation;
    private readonly DalamudItemContextActionUiTransaction discardUi;
    private readonly DalamudItemContextActionUiTransaction vendorSaleUi;
    private readonly DalamudVendorSalePreparation vendorSalePreparation;
    private readonly SemaphoreSlim ownedStateRecoveryGate = new(1, 1);
    private SquireDisposition? diagnosticDisposition;

    public string DiagnosticStatus => diagnosticDisposition switch
    {
        SquireDisposition.ExpertDelivery => expertDeliveryPreparation.Status,
        SquireDisposition.VendorSell => vendorSalePreparation.Status,
        _ => string.Empty,
    };

    public DalamudSquireActionGameAdapter(
        ICharacterEquipmentSnapshotSource snapshotSource,
        IPlayerState playerState,
        ICondition condition,
        IGameGui gameGui,
        IFramework framework,
        IDataManager dataManager,
        ISquireDispositionCapabilitySource capabilitySource,
        ICommandManager commandManager,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        Func<SquireProtectionPolicy> currentPolicy,
        Func<SquireExecutionRecoveryPolicy> currentRecoveryPolicy,
        Func<string?>? describeExternalConflict = null)
    {
        this.snapshotSource = snapshotSource;
        this.playerState = playerState;
        this.condition = condition;
        this.framework = framework;
        this.capabilitySource = capabilitySource;
        this.currentPolicy = currentPolicy;
        this.currentRecoveryPolicy = currentRecoveryPolicy;
        this.describeExternalConflict = describeExternalConflict ?? (() => null);
        var externalAutomation = new DalamudSquireExternalAutomationCoordinator(pluginInterface, framework, condition, log);
        recoveryCoordinator = new DalamudSquireRecoveryCoordinator(condition, gameGui, framework, log, externalAutomation);
        desynthesisUi = new DalamudDesynthesisUiTransaction(gameGui);
        materiaRetrievalUi = new DalamudMateriaRetrievalUiTransaction(gameGui, IsExactFingerprintCurrent);
        var hqPrompt = dataManager.GetExcelSheet<Addon>().GetRow(102434).Text.ExtractText().Trim();
        expertDeliveryUi = new DalamudExpertDeliveryUiTransaction(
            gameGui,
            observed => string.Equals(observed, hqPrompt, StringComparison.Ordinal),
            IsExactFingerprintCurrent);
        expertDeliveryPreparation = new DalamudExpertDeliveryPreparation(
            commandManager,
            condition,
            objectTable,
            targetManager,
            gameGui,
            framework,
            dataManager,
            pluginInterface,
            log);
        discardUi = new DalamudItemContextActionUiTransaction(
            gameGui,
            DiscardOption,
            IsExactFingerprintCurrent,
            expectedConfirmation: (fingerprint, prompt) => IsExpectedItemConfirmation(fingerprint, prompt, DiscardPromptTerms));
        vendorSaleUi = new DalamudItemContextActionUiTransaction(
            gameGui,
            SellOption,
            IsExactFingerprintCurrent,
            requiredVisibleAddon: "Shop",
            expectedConfirmation: (fingerprint, prompt) => IsExpectedItemConfirmation(fingerprint, prompt, SellPromptTerms),
            requiredStableFrames: 2);
        vendorSalePreparation = new DalamudVendorSalePreparation(
            commandManager,
            objectTable,
            targetManager,
            gameGui,
            framework,
            dataManager,
            pluginInterface,
            log);
    }

    public CharacterScope? GetActiveCharacter() =>
        playerState.IsLoaded && playerState.ContentId != 0
            ? new CharacterScope(playerState.ContentId, playerState.CharacterName.ToString(), playerState.HomeWorld.RowId)
            : null;

    public Task<SquireActionResult> BeginDispositionGroupAsync(SquireDisposition disposition, CancellationToken cancellationToken) =>
        Task.FromResult(SquireActionResult.Completed(
            $"{disposition} requires no shared batch preparation; item-level readiness will be verified before each action."));

    public Task<SquireActionResult> RecoverExecutionStateAsync(CancellationToken cancellationToken) =>
        recoveryCoordinator.EnsureReadyAsync(currentRecoveryPolicy(), cancellationToken);

    public async Task EndDispositionGroupAsync(SquireDisposition disposition, CancellationToken cancellationToken)
    {
        var ownedNpcUi = disposition switch
        {
            SquireDisposition.ExpertDelivery => expertDeliveryPreparation.OwnsUi,
            SquireDisposition.VendorSell => vendorSalePreparation.OwnsUi,
            _ => false,
        };
        if (disposition == SquireDisposition.ExpertDelivery)
        {
            if (ownedNpcUi)
                await CloseOwnedExpertPreparationAsync(cancellationToken).ConfigureAwait(false);
        }
        if (ownedNpcUi && disposition == SquireDisposition.VendorSell)
            await CloseOwnedVendorPreparationAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool HasConflictingAutomation(SquireDisposition disposition) =>
        describeExternalConflict() is not null ||
        condition[ConditionFlag.BetweenAreas] ||
        condition[ConditionFlag.BetweenAreas51] ||
        condition[ConditionFlag.WatchingCutscene] ||
        condition[ConditionFlag.WatchingCutscene78] ||
        (disposition is not (SquireDisposition.ExpertDelivery or SquireDisposition.VendorSell) && condition[ConditionFlag.OccupiedInQuestEvent]) ||
        condition[ConditionFlag.BeingMoved];

    private string DescribeConflictingAutomation(SquireDisposition disposition)
    {
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return "The character is between areas.";
        if (condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78])
            return "A cutscene owns the game UI.";
        if (condition[ConditionFlag.BeingMoved])
            return "The character is still being moved.";
        if (disposition is not (SquireDisposition.ExpertDelivery or SquireDisposition.VendorSell) && condition[ConditionFlag.OccupiedInQuestEvent])
            return "A normal NPC or quest-event interaction is still active.";
        if (describeExternalConflict() is { } externalConflict)
            return externalConflict;
        return "Another automation or movement state owns the required game state.";
    }

    public SquireRevalidationResult Revalidate(EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition)
    {
        var snapshot = snapshotSource.Capture();
        if (!snapshot.Diagnostics.IsComplete)
            return SquireRevalidationResult.Fail("PartialSnapshot", "Fresh revalidation snapshot is incomplete.");
        if (snapshot.Identity.Scope != fingerprint.Character)
            return SquireRevalidationResult.Fail("CharacterScopeChanged", "The active character changed.");

        var observed = snapshot.Instances.FirstOrDefault(instance =>
            instance.Fingerprint.Container == fingerprint.Container && instance.Fingerprint.SlotIndex == fingerprint.SlotIndex);
        if (observed is null || !SquireFingerprintMatcher.ExactMatch(fingerprint, observed.Fingerprint))
            return SquireRevalidationResult.Fail("ExactSlotMismatch", "The approved exact slot identity changed.");
        var livePolicy = currentPolicy();
        var analysis = new SquireCandidateEvaluator().Evaluate(snapshot, capabilitySource.Capture(), livePolicy);
        var candidate = analysis.Candidates.FirstOrDefault(value =>
            EquipmentInstanceFingerprintComparer.Instance.Equals(value.Instance.Fingerprint, observed.Fingerprint));
        if (candidate is null)
            return SquireRevalidationResult.Fail("CandidateMissing", "The exact item is absent from the fresh rule evaluation.");
        if (!candidate.IsExecutable)
        {
            var reason = candidate.Reasons.FirstOrDefault(value => value.Severity == SquireReasonSeverity.Blocking)
                         ?? candidate.Reasons.FirstOrDefault();
            return SquireRevalidationResult.Fail(
                reason?.Code ?? "CandidateNoLongerExecutable",
                reason?.Message ?? "The exact item is no longer an executable cleanup candidate.");
        }
        if (!candidate.SupportedDispositions.Contains(disposition))
            return SquireRevalidationResult.Fail("DispositionUnavailable", "The approved disposition is no longer supported.");
        if (candidate.RecommendedDisposition != disposition)
            return SquireRevalidationResult.Fail(
                "DispositionPolicyChanged",
                $"Current cleanup rules select {candidate.RecommendedDisposition} instead of the approved {disposition} route.");
        return SquireRevalidationResult.Valid();
    }

    public SquireRevalidationResult RevalidateBatch(SquireActionPlan plan, IReadOnlyList<SquireReviewedSelection> remainingActions)
    {
        var snapshot = snapshotSource.Capture();
        if (snapshot.Identity.Scope != plan.Character)
            return SquireRevalidationResult.Fail("CharacterScopeChanged", "The active character changed before batch revalidation.");
        var removals = remainingActions.ToDictionary(action => action.Fingerprint, action => action.Disposition, EquipmentInstanceFingerprintComparer.Instance);
        var approvedPolicy = plan.Policy ?? new SquireProtectionPolicy();
        var livePolicy = currentPolicy();
        var validator = new SquireCounterfactualBatchValidator();
        var approvedResult = validator.Validate(
            snapshot,
            removals,
            capabilitySource.Capture(),
            approvedPolicy);
        if (!approvedResult.Success)
            return SquireRevalidationResult.Fail(approvedResult.Code, $"Approved policy no longer validates the batch: {approvedResult.Message}");
        var liveResult = validator.Validate(snapshot, removals, capabilitySource.Capture(), livePolicy);
        return liveResult.Success
            ? SquireRevalidationResult.Valid() with { Message = liveResult.Message }
            : SquireRevalidationResult.Fail(liveResult.Code, $"Current policy no longer validates the batch: {liveResult.Message}");
    }

    public SquireRevalidationResult RevalidateEvidence(SquireReviewedSelection selection)
    {
        if (selection.Witnesses is not { Count: > 0 })
            return SquireRevalidationResult.Valid();
        var snapshot = snapshotSource.Capture();
        if (!snapshot.Diagnostics.IsComplete)
            return SquireRevalidationResult.Fail("EvidenceSnapshotIncomplete", "The fresh witness snapshot is incomplete.");
        var target = snapshot.Instances.FirstOrDefault(instance =>
            instance.Fingerprint.Container == selection.Fingerprint.Container && instance.Fingerprint.SlotIndex == selection.Fingerprint.SlotIndex);
        if (target is null || !SquireFingerprintMatcher.ExactMatch(selection.Fingerprint, target.Fingerprint) ||
            !snapshot.Definitions.TryGetValue(target.Fingerprint.ItemId, out var targetDefinition))
            return SquireRevalidationResult.Fail("EvidenceTargetChanged", "The target changed before witness revalidation.");
        var targetStats = EquipmentInstanceStats.Resolve(target, targetDefinition);
        if (targetStats is not { IsComplete: true })
            return SquireRevalidationResult.Fail("EvidenceTargetStatsIncomplete", "The target's quality-adjusted stat profile is incomplete.");

        foreach (var proof in selection.Witnesses)
        {
            var job = snapshot.Jobs.FirstOrDefault(value => value.ClassJobId == proof.ClassJobId && value.IsUnlocked == true);
            if (job is null)
                return SquireRevalidationResult.Fail("EvidenceJobChanged", $"The obtained-state observation for {proof.JobAbbreviation} changed.");
            if (proof.Fingerprints.Count != (proof.Slot == EquipmentSlot.Ring ? 2 : 1))
                return SquireRevalidationResult.Fail("EvidenceCapacityInvalid", $"The retained witness count for {proof.JobAbbreviation} is invalid.");
            var observedWitnesses = new List<(EquipmentInstanceSnapshot Instance, EquipmentItemDefinition Definition)>();
            foreach (var fingerprint in proof.Fingerprints)
            {
                var observed = snapshot.Instances.FirstOrDefault(instance =>
                    instance.Fingerprint.Container == fingerprint.Container && instance.Fingerprint.SlotIndex == fingerprint.SlotIndex);
                if (observed is null || !SquireFingerprintMatcher.ExactMatch(fingerprint, observed.Fingerprint) ||
                    !snapshot.Definitions.TryGetValue(fingerprint.ItemId, out var definition))
                    return SquireRevalidationResult.Fail("EvidenceWitnessChanged", $"A retained {proof.JobAbbreviation} witness changed or disappeared.");
                if (definition.Slot != proof.Slot ||
                    !EquipmentUseAnalyzer.IsEligibleWitness(targetDefinition, definition, job, snapshot.Jobs))
                    return SquireRevalidationResult.Fail("EvidenceWitnessIneligible", $"A retained witness is no longer safely usable by {proof.JobAbbreviation}.");
                if (proof.Slot == EquipmentSlot.MainHand &&
                    (targetDefinition.MainHandOccupancy != definition.MainHandOccupancy ||
                     targetDefinition.OffHandOccupancy != definition.OffHandOccupancy))
                    return SquireRevalidationResult.Fail("EvidenceWeaponConfigurationChanged", $"A retained {proof.JobAbbreviation} weapon no longer matches the target's hand occupancy.");
                if (proof.Slot == EquipmentSlot.OffHand && definition.OffHandOccupancy != 1)
                    return SquireRevalidationResult.Fail("EvidenceOffHandConfigurationChanged", $"A retained {proof.JobAbbreviation} off-hand is no longer a proven off-hand configuration.");
                if (proof.Slot == EquipmentSlot.Ring && (!definition.FitsLeftRing || !definition.FitsRightRing))
                    return SquireRevalidationResult.Fail("EvidenceRingCompatibilityChanged", $"A retained {proof.JobAbbreviation} ring no longer has proven two-slot compatibility.");
                var stats = EquipmentInstanceStats.Resolve(observed, definition);
                if (stats is not { IsComplete: true } ||
                    EquipmentUseAnalyzer.EvaluateCoverage(definition, stats, targetDefinition, targetStats, job) == EquipmentCoverageKind.None)
                    return SquireRevalidationResult.Fail("EvidenceCoverageChanged", $"A retained witness no longer covers the target without relevant-stat loss for {proof.JobAbbreviation}.");
                observedWitnesses.Add((observed, definition));
            }
            if (proof.Slot == EquipmentSlot.Ring && observedWitnesses[0].Definition.ItemId == observedWitnesses[1].Definition.ItemId &&
                observedWitnesses[0].Definition.IsUnique)
                return SquireRevalidationResult.Fail("EvidenceRingPairInvalid", $"The retained ring pair for {proof.JobAbbreviation} is not jointly equippable.");
        }
        return SquireRevalidationResult.Valid();
    }

    public async Task<SquireActionResult> ExecuteAsync(
        SquireActionPlan plan,
        IReadOnlyList<SquireReviewedSelection> remainingActions,
        SquireReviewedSelection action,
        CancellationToken cancellationToken)
    {
        diagnosticDisposition = action.Disposition;
        var fingerprint = action.Fingerprint;
        var disposition = action.Disposition;
        if (disposition is not (SquireDisposition.Desynthesize or SquireDisposition.ExpertDelivery or SquireDisposition.VendorSell or SquireDisposition.Discard))
            return SquireActionResult.Fail("AdapterNotEnabled", $"The {disposition} UI adapter is not enabled.");

        var prepared = await RetrieveAllMateriaAsync(plan, remainingActions, action.Fingerprint, fingerprint, disposition, cancellationToken).ConfigureAwait(false);
        if (!prepared.Result.Success)
            return prepared.Result;
        fingerprint = prepared.Fingerprint;

        if (disposition == SquireDisposition.ExpertDelivery)
            return await ExecuteExpertDeliveryAsync(plan, remainingActions, action.Fingerprint, fingerprint, cancellationToken).ConfigureAwait(false);
        if (disposition == SquireDisposition.VendorSell)
        {
            var preparation = await vendorSalePreparation.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            if (!preparation.Success)
                return preparation;
            return await ExecuteContextActionAsync(plan, remainingActions, action.Fingerprint, fingerprint, disposition, vendorSaleUi, cancellationToken).ConfigureAwait(false);
        }
        if (disposition == SquireDisposition.Discard)
            return await ExecuteContextActionAsync(plan, remainingActions, action.Fingerprint, fingerprint, disposition, discardUi, cancellationToken).ConfigureAwait(false);
        var preparedBatch = RevalidatePreparedBatch(plan, remainingActions, action.Fingerprint, fingerprint);
        if (!preparedBatch.Success)
            return SquireActionResult.Fail(preparedBatch.Code, preparedBatch.Message);
        var started = await framework.RunOnTick(() => BeginDesynthesis(fingerprint)).ConfigureAwait(false);
        if (!started.Success)
            return started;

        for (var attempt = 0; attempt < 90; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var confirmation = await framework.RunOnTick(() => TryConfirmDesynthesis(
                plan, remainingActions, action.Fingerprint, fingerprint)).ConfigureAwait(false);
            if (confirmation.Code == "ConfirmationSubmitted")
                break;
            if (confirmation.Code != "ConfirmationPending")
                return confirmation;
            await framework.DelayTicks(1).ConfigureAwait(false);
            if (attempt == 89)
                return SquireActionResult.Fail("ConfirmationTimeout", $"The owned desynthesis dialog did not become ready. Last UI observation: {desynthesisUi.Status}");
        }

        for (var attempt = 0; attempt < 360; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transition = await framework.RunOnTick(() => ObserveSlotTransition(fingerprint)).ConfigureAwait(false);
            if (transition.Success)
            {
                desynthesisUi.Complete();
                await WaitForDesynthesisUiSettledAsync(cancellationToken).ConfigureAwait(false);
                return transition;
            }
            if (transition.Code != "TransitionPending")
                return transition;
            await framework.DelayTicks(1).ConfigureAwait(false);
        }

        return SquireActionResult.Fail("TransitionTimeout", "The exact slot did not transition after desynthesis confirmation.");
    }

    public async Task<SquireActionResult> ProbeAsync(
        EquipmentInstanceFingerprint fingerprint,
        SquireDisposition disposition,
        CancellationToken cancellationToken)
    {
        diagnosticDisposition = disposition;
        if (HasConflictingAutomation(disposition))
            return SquireActionResult.Fail("ConflictingAutomation", DescribeConflictingAutomation(disposition));
        if (disposition == SquireDisposition.ExpertDelivery)
        {
            var ownedUi = false;
            try
            {
                var preparation = await expertDeliveryPreparation.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
                if (!preparation.Success)
                    return preparation;
                ownedUi = expertDeliveryPreparation.OwnsUi;
                return await framework.RunOnTick(() => ProbeExpertDelivery(fingerprint)).ConfigureAwait(false);
            }
            finally
            {
                ownedUi = expertDeliveryPreparation.OwnsUi;
                if (ownedUi)
                    await CloseOwnedExpertPreparationAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        if (disposition is SquireDisposition.VendorSell or SquireDisposition.Discard)
        {
            if (disposition == SquireDisposition.VendorSell)
            {
                var ownedUi = false;
                try
                {
                    var preparation = await vendorSalePreparation.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
                    if (!preparation.Success)
                        return preparation;
                    ownedUi = vendorSalePreparation.OwnsUi;
                    return await ProbeContextActionAsync(fingerprint, disposition, vendorSaleUi, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (ownedUi)
                        await CloseOwnedVendorPreparationAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            return await ProbeContextActionAsync(fingerprint, disposition,
                discardUi, cancellationToken).ConfigureAwait(false);
        }
        if (disposition != SquireDisposition.Desynthesize)
            return SquireActionResult.Fail("DiagnosticAdapterNotEnabled", $"A non-destructive {disposition} probe is not enabled.");

        var opened = await framework.RunOnTick(() => BeginDesynthesisProbe(fingerprint)).ConfigureAwait(false);
        if (!opened.Success)
            return opened;
        for (var attempt = 0; attempt < 90; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await framework.RunOnTick(() => ProbeDesynthesisMenu(fingerprint)).ConfigureAwait(false);
            if (probe.Success)
            {
                await framework.RunOnTick(desynthesisUi.CloseVisibleUi).ConfigureAwait(false);
                await WaitForDesynthesisUiSettledAsync(cancellationToken).ConfigureAwait(false);
                return probe;
            }
            if (probe.Code != "Pending")
            {
                await framework.RunOnTick(desynthesisUi.CloseVisibleUi).ConfigureAwait(false);
                await WaitForDesynthesisUiSettledAsync(cancellationToken).ConfigureAwait(false);
                return probe;
            }
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        await framework.RunOnTick(desynthesisUi.CloseVisibleUi).ConfigureAwait(false);
        await WaitForDesynthesisUiSettledAsync(cancellationToken).ConfigureAwait(false);
        return SquireActionResult.Fail(
            "DiagnosticProbeTimeout",
            $"The Desynthesis context menu did not become stable for inspection. Last UI observation: {desynthesisUi.Status}");
    }

    private unsafe SquireActionResult BeginDesynthesisProbe(EquipmentInstanceFingerprint fingerprint)
    {
        var validation = Revalidate(fingerprint, SquireDisposition.Desynthesize);
        if (!validation.Success)
            return SquireActionResult.Fail(validation.Code, validation.Message);
        var result = desynthesisUi.OpenExactSlotContextMenu(fingerprint);
        return new(result.Success, result.Code, result.Message);
    }

    private unsafe SquireActionResult ProbeDesynthesisMenu(EquipmentInstanceFingerprint fingerprint)
    {
        var result = desynthesisUi.ProbeContextMenu(fingerprint);
        var message = result.Code == "Pending"
            ? result.Message
            : $"{result.Message} Observed context menu: {desynthesisUi.DescribeContextMenu()}";
        return new(result.Success, result.Code, message);
    }

    private unsafe SquireActionResult ProbeExpertDelivery(EquipmentInstanceFingerprint fingerprint)
    {
        var validation = Revalidate(fingerprint, SquireDisposition.ExpertDelivery);
        if (!validation.Success)
            return SquireActionResult.Fail(validation.Code, validation.Message);
        var gc = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance()->GrandCompany;
        if (gc == 0)
            return SquireActionResult.Fail("GrandCompanyUnavailable", "The active character is not employed by a Grand Company.");
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        var result = expertDeliveryUi.Probe(fingerprint, inventory->GetCompanySeals(gc), inventory->GetMaxCompanySeals(gc));
        return new(result.Success, result.Code, result.Message);
    }

    private async Task WaitForDesynthesisUiSettledAsync(CancellationToken cancellationToken)
    {
        var stableFrames = 0;
        for (var attempt = 0; attempt < 180; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settled = await framework.RunOnTick(desynthesisUi.IsUiSettled).ConfigureAwait(false);
            stableFrames = settled ? stableFrames + 1 : 0;
            if (stableFrames >= 12)
                return;
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Desynthesis UI did not settle after the completed inventory transition.");
    }

    private async Task CloseOwnedExpertPreparationAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await framework.RunOnTick(expertDeliveryPreparation.CloseOwnedUi).ConfigureAwait(false);
            if (!IsNpcInteractionOccupied())
            {
                await framework.RunOnTick(expertDeliveryPreparation.CompleteOwnedUiClose).ConfigureAwait(false);
                return;
            }
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        throw new InvalidOperationException("The normal Expert Delivery interaction did not settle after Squire closed its owned UI stack.");
    }

    private async Task CloseOwnedVendorPreparationAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await framework.RunOnTick(vendorSalePreparation.CloseOwnedUi).ConfigureAwait(false);
            if (!IsNpcInteractionOccupied())
            {
                await framework.RunOnTick(vendorSalePreparation.CompleteOwnedUiClose).ConfigureAwait(false);
                return;
            }
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        throw new InvalidOperationException("The normal vendor interaction did not settle after Squire closed its owned UI stack.");
    }

    private bool IsNpcInteractionOccupied() =>
        condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.OccupiedInEvent];

    private async Task<(SquireActionResult Result, EquipmentInstanceFingerprint Fingerprint)> RetrieveAllMateriaAsync(
        SquireActionPlan plan,
        IReadOnlyList<SquireReviewedSelection> remainingActions,
        EquipmentInstanceFingerprint originalFingerprint,
        EquipmentInstanceFingerprint fingerprint,
        SquireDisposition disposition,
        CancellationToken cancellationToken)
    {
        while (fingerprint.MateriaIds.Count > 0)
        {
            if (HasConflictingAutomation(disposition))
                return (SquireActionResult.Fail("ConflictingAutomation", "Another automation or movement state became active before materia retrieval."), fingerprint);
            var batchValidation = RevalidatePreparedBatch(plan, remainingActions, originalFingerprint, fingerprint);
            if (!batchValidation.Success)
                return (SquireActionResult.Fail(batchValidation.Code, batchValidation.Message), fingerprint);
            var validation = Revalidate(fingerprint, disposition);
            if (!validation.Success)
                return (SquireActionResult.Fail(validation.Code, validation.Message), fingerprint);

            var started = await framework.RunOnTick(() => materiaRetrievalUi.Begin(fingerprint)).ConfigureAwait(false);
            if (!started.Success)
                return (SquireActionResult.Fail(started.Code, started.Message), fingerprint);

            var transactionCompleted = false;
            try
            {
                var submitted = false;
                EquipmentInstanceFingerprint? updated = null;
                for (var attempt = 0; attempt < 120; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var earlyObservation = await framework.RunOnTick(() => ObserveMateriaRemoval(fingerprint)).ConfigureAwait(false);
                    if (earlyObservation.Result.Success)
                    {
                        updated = earlyObservation.Fingerprint;
                        break;
                    }
                    if (earlyObservation.Result.Code != "MateriaTransitionPending")
                        return (earlyObservation.Result, fingerprint);
                    var advanced = await framework.RunOnTick(() => materiaRetrievalUi.Advance(
                        fingerprint,
                        () => !HasConflictingAutomation(disposition) &&
                            RevalidatePreparedBatch(plan, remainingActions, originalFingerprint, fingerprint).Success)).ConfigureAwait(false);
                    if (advanced.Code == "RetrievalSubmitted")
                    {
                        submitted = true;
                        break;
                    }
                    if (!advanced.Success && advanced.Code != "Pending")
                        return (SquireActionResult.Fail(advanced.Code, advanced.Message), fingerprint);
                    await framework.DelayTicks(1).ConfigureAwait(false);
                }
                if (!submitted && updated is null)
                    return (SquireActionResult.Fail("MateriaRetrievalDialogTimeout", "The owned materia retrieval dialog did not become ready."), fingerprint);

                for (var attempt = 0; updated is null && attempt < 240; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var observation = await framework.RunOnTick(() => ObserveMateriaRemoval(fingerprint)).ConfigureAwait(false);
                    if (observation.Result.Success)
                    {
                        updated = observation.Fingerprint;
                        break;
                    }
                    if (observation.Result.Code != "MateriaTransitionPending")
                        return (observation.Result, fingerprint);
                    await framework.DelayTicks(1).ConfigureAwait(false);
                }
                if (updated is null)
                    return (SquireActionResult.Fail("MateriaRetrievalTransitionTimeout", "The exact slot did not lose one attached materia after confirmation."), fingerprint);

                await WaitForMateriaRetrievalUiSettledAsync(cancellationToken).ConfigureAwait(false);
                materiaRetrievalUi.Complete();
                transactionCompleted = true;
                fingerprint = updated;
            }
            finally
            {
                if (!transactionCompleted)
                    await CloseOwnedUiUntilSettledAsync(materiaRetrievalUi.CloseOwnedUi, materiaRetrievalUi.IsUiSettled).ConfigureAwait(false);
            }
        }

        return (new SquireActionResult(true, "MateriaReady", "The exact item has no attached materia."), fingerprint);
    }

    private (SquireActionResult Result, EquipmentInstanceFingerprint? Fingerprint) ObserveMateriaRemoval(EquipmentInstanceFingerprint expected)
    {
        var snapshot = snapshotSource.Capture();
        if (!snapshot.Diagnostics.IsComplete)
            return (SquireActionResult.Fail("PartialSnapshot", "The materia retrieval observation snapshot is incomplete."), null);
        if (snapshot.Identity.Scope != expected.Character)
            return (SquireActionResult.Fail("CharacterScopeChanged", "The active character changed during materia retrieval."), null);
        var observed = snapshot.Instances.FirstOrDefault(instance =>
            instance.Fingerprint.Container == expected.Container && instance.Fingerprint.SlotIndex == expected.SlotIndex)?.Fingerprint;
        if (observed is null || !SameIdentityExceptMateria(expected, observed))
            return (SquireActionResult.Fail("MateriaTargetChanged", "The exact slot changed identity during materia retrieval."), null);
        if (observed.MateriaIds.Count == expected.MateriaIds.Count)
            return (SquireActionResult.Fail("MateriaTransitionPending", "The exact item still has the previous materia count."), null);
        if (observed.MateriaIds.Count != expected.MateriaIds.Count - 1)
            return (SquireActionResult.Fail("UnexpectedMateriaTransition", "Materia retrieval changed the attached materia count by more than one."), null);
        return (new SquireActionResult(true, "MateriaRetrieved", "One attached materia was removed through the normal item UI."), observed);
    }

    private bool IsExactFingerprintCurrent(EquipmentInstanceFingerprint expected)
    {
        var snapshot = snapshotSource.Capture();
        if (!snapshot.Diagnostics.IsComplete || snapshot.Identity.Scope != expected.Character)
            return false;
        var observed = snapshot.Instances.FirstOrDefault(instance =>
            instance.Fingerprint.Container == expected.Container && instance.Fingerprint.SlotIndex == expected.SlotIndex);
        return observed is not null && SquireFingerprintMatcher.ExactMatch(expected, observed.Fingerprint);
    }

    private async Task WaitForMateriaRetrievalUiSettledAsync(CancellationToken cancellationToken)
    {
        var stableFrames = 0;
        for (var attempt = 0; attempt < 180; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settled = await framework.RunOnTick(materiaRetrievalUi.IsUiSettled).ConfigureAwait(false);
            stableFrames = settled ? stableFrames + 1 : 0;
            if (stableFrames >= 6)
                return;
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Materia retrieval UI did not settle after the observed inventory transition.");
    }

    private static bool SameIdentityExceptMateria(EquipmentInstanceFingerprint expected, EquipmentInstanceFingerprint observed) =>
        expected.Character == observed.Character &&
        expected.Container == observed.Container &&
        expected.SlotIndex == observed.SlotIndex &&
        expected.ItemId == observed.ItemId &&
        expected.IsHighQuality == observed.IsHighQuality &&
        expected.Quantity == observed.Quantity &&
        expected.Condition == observed.Condition &&
        expected.Spiritbond == observed.Spiritbond &&
        expected.CrafterContentId == observed.CrafterContentId &&
        expected.GlamourId == observed.GlamourId &&
        expected.Stains.SequenceEqual(observed.Stains);

    private async Task<SquireActionResult> ExecuteExpertDeliveryAsync(
        SquireActionPlan plan,
        IReadOnlyList<SquireReviewedSelection> remainingActions,
        EquipmentInstanceFingerprint approvedFingerprint,
        EquipmentInstanceFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var preparation = await expertDeliveryPreparation.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!preparation.Success)
            return preparation;

        var preparedBatch = RevalidatePreparedBatch(plan, remainingActions, approvedFingerprint, fingerprint);
        if (!preparedBatch.Success)
            return SquireActionResult.Fail(preparedBatch.Code, preparedBatch.Message);

        SquireActionResult? started = null;
        for (var attempt = 0; attempt < 90; attempt++)
        {
            started = await framework.RunOnTick(() => BeginExpertDelivery(fingerprint)).ConfigureAwait(false);
            if (started.Success)
                break;
            if (started.Code != "ExpertDeliveryListUnavailable")
                return started;
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        if (started is not { Success: true })
            return SquireActionResult.Fail("ExpertDeliveryListTimeout", "The visible Expert Delivery list did not become ready.");
        for (var attempt = 0; attempt < 360; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transition = await framework.RunOnTick(() => ObserveSlotTransition(fingerprint)).ConfigureAwait(false);
            if (transition.Success)
            {
                expertDeliveryUi.Complete();
                var listReturn = await expertDeliveryPreparation.WaitForListReturnAsync(cancellationToken).ConfigureAwait(false);
                return listReturn.Success
                    ? transition
                    : listReturn;
            }
            if (transition.Code != "TransitionPending")
                return transition;
            var advanced = await framework.RunOnTick(() => expertDeliveryUi.Advance(() =>
                !HasConflictingAutomation(SquireDisposition.ExpertDelivery) &&
                RevalidatePreparedBatch(plan, remainingActions, approvedFingerprint, fingerprint).Success)).ConfigureAwait(false);
            if (!advanced.Success && advanced.Code != "Pending")
                return SquireActionResult.Fail(advanced.Code, advanced.Message);
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        return SquireActionResult.Fail("TransitionTimeout", "The exact slot did not transition after Expert Delivery confirmation.");
    }

    private async Task<SquireActionResult> ExecuteContextActionAsync(
        SquireActionPlan plan,
        IReadOnlyList<SquireReviewedSelection> remainingActions,
        EquipmentInstanceFingerprint approvedFingerprint,
        EquipmentInstanceFingerprint fingerprint,
        SquireDisposition disposition,
        DalamudItemContextActionUiTransaction transaction,
        CancellationToken cancellationToken)
    {
        var preparedBatch = RevalidatePreparedBatch(plan, remainingActions, approvedFingerprint, fingerprint);
        if (!preparedBatch.Success)
            return SquireActionResult.Fail(preparedBatch.Code, preparedBatch.Message);

        DalamudUiTransactionResult? started = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            started = await framework.RunOnTick(() => transaction.Begin(fingerprint)).ConfigureAwait(false);
            if (started.Success)
                break;
            if (started.Code != "InventoryOwnerPending")
                return SquireActionResult.Fail(started.Code, started.Message);
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        if (started is not { Success: true })
            return SquireActionResult.Fail("ContextActionStartTimeout", $"The normal inventory UI did not become ready for {disposition}.");

        for (var attempt = 0; attempt < 360; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transition = await framework.RunOnTick(() => ObserveSlotTransition(fingerprint)).ConfigureAwait(false);
            if (transition.Success)
            {
                await WaitForContextActionUiSettledAsync(transaction, cancellationToken).ConfigureAwait(false);
                transaction.Complete();
                return transition;
            }
            if (transition.Code != "TransitionPending")
                return transition;
            var advanced = await framework.RunOnTick(() => transaction.Advance(fingerprint, () =>
                !HasConflictingAutomation(disposition) &&
                RevalidatePreparedBatch(plan, remainingActions, approvedFingerprint, fingerprint).Success)).ConfigureAwait(false);
            if (!advanced.Success && advanced.Code != "Pending")
                return SquireActionResult.Fail(advanced.Code, advanced.Message);
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        return SquireActionResult.Fail("TransitionTimeout", $"The exact slot did not transition after {disposition}.");
    }

    private async Task WaitForContextActionUiSettledAsync(
        DalamudItemContextActionUiTransaction transaction,
        CancellationToken cancellationToken)
    {
        var stableFrames = 0;
        for (var attempt = 0; attempt < 180; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settled = await framework.RunOnTick(transaction.IsUiSettled).ConfigureAwait(false);
            stableFrames = settled ? stableFrames + 1 : 0;
            if (stableFrames >= 2)
                return;
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        throw new InvalidOperationException("The owned item context UI did not settle after the inventory transition.");
    }

    private async Task<SquireActionResult> ProbeContextActionAsync(
        EquipmentInstanceFingerprint fingerprint,
        SquireDisposition disposition,
        DalamudItemContextActionUiTransaction transaction,
        CancellationToken cancellationToken)
    {
        var validation = Revalidate(fingerprint, disposition);
        if (!validation.Success)
            return SquireActionResult.Fail(validation.Code, validation.Message);
        DalamudUiTransactionResult? started = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            started = await framework.RunOnTick(() => transaction.Begin(fingerprint)).ConfigureAwait(false);
            if (started.Success)
                break;
            if (started.Code != "InventoryOwnerPending")
                return SquireActionResult.Fail(started.Code, started.Message);
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        if (started is not { Success: true })
            return SquireActionResult.Fail("ContextActionStartTimeout", $"The normal inventory UI did not become ready for {disposition} probe.");
        try
        {
            for (var attempt = 0; attempt < 90; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await framework.RunOnTick(() => transaction.Probe(fingerprint)).ConfigureAwait(false);
                if (result.Success)
                    return new SquireActionResult(true, result.Code, result.Message);
                if (result.Code != "Pending")
                    return SquireActionResult.Fail(result.Code, result.Message);
                await framework.DelayTicks(1).ConfigureAwait(false);
            }
            return SquireActionResult.Fail("DiagnosticProbeTimeout", $"The {disposition} context menu did not become stable for inspection.");
        }
        finally
        {
            await CloseOwnedUiUntilSettledAsync(transaction.CloseOwnedUi, transaction.IsUiSettled).ConfigureAwait(false);
        }
    }

    private SquireRevalidationResult RevalidatePreparedBatch(
        SquireActionPlan plan,
        IReadOnlyList<SquireReviewedSelection> remainingActions,
        EquipmentInstanceFingerprint approvedFingerprint,
        EquipmentInstanceFingerprint preparedFingerprint)
    {
        var preparedActions = remainingActions.Select(action =>
            EquipmentInstanceFingerprintComparer.Instance.Equals(action.Fingerprint, approvedFingerprint)
                ? action with { Fingerprint = preparedFingerprint }
                : action).ToArray();
        return RevalidateBatch(plan, preparedActions);
    }

    private unsafe SquireActionResult BeginExpertDelivery(EquipmentInstanceFingerprint fingerprint)
    {
        var validation = Revalidate(fingerprint, SquireDisposition.ExpertDelivery);
        if (!validation.Success)
            return SquireActionResult.Fail(validation.Code, validation.Message);
        var gc = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance()->GrandCompany;
        if (gc == 0)
            return SquireActionResult.Fail("GrandCompanyUnavailable", "The active character is not employed by a Grand Company.");
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        var result = expertDeliveryUi.Begin(fingerprint, inventory->GetCompanySeals(gc), inventory->GetMaxCompanySeals(gc));
        return new SquireActionResult(result.Success, result.Code, result.Message);
    }

    private unsafe SquireActionResult BeginDesynthesis(EquipmentInstanceFingerprint fingerprint)
    {
        var validation = Revalidate(fingerprint, SquireDisposition.Desynthesize);
        if (!validation.Success)
            return SquireActionResult.Fail(validation.Code, validation.Message);
        var result = desynthesisUi.Begin(fingerprint);
        return new SquireActionResult(result.Success, result.Code, result.Message);
    }

    private unsafe SquireActionResult TryConfirmDesynthesis(
        SquireActionPlan plan,
        IReadOnlyList<SquireReviewedSelection> remainingActions,
        EquipmentInstanceFingerprint approvedFingerprint,
        EquipmentInstanceFingerprint fingerprint)
    {
        // Once the context-menu command is submitted, the client reserves the item and the
        // inventory slot is no longer a valid identity oracle. Revalidate immediately before
        // that transition; thereafter the owned UI transaction and final slot transition are
        // the authoritative lifecycle signals.
        if (!desynthesisUi.MenuSelectionSubmitted)
        {
            var validation = Revalidate(fingerprint, SquireDisposition.Desynthesize);
            if (!validation.Success)
                return SquireActionResult.Fail(validation.Code, validation.Message);
        }

        var result = desynthesisUi.AdvanceToConfirmation(fingerprint, () =>
            !HasConflictingAutomation(SquireDisposition.Desynthesize) &&
            (desynthesisUi.MenuSelectionSubmitted ||
             RevalidatePreparedBatch(plan, remainingActions, approvedFingerprint, fingerprint).Success));
        if (!result.Success)
            return result.Code == "Pending"
                ? SquireActionResult.Fail("ConfirmationPending", result.Message)
                : SquireActionResult.Fail(result.Code, result.Message);
        return new SquireActionResult(true, result.Code, result.Message);
    }

    private SquireActionResult ObserveSlotTransition(EquipmentInstanceFingerprint fingerprint)
    {
        var snapshot = snapshotSource.Capture();
        if (snapshot.Identity.Scope != fingerprint.Character)
            return SquireActionResult.Fail("CharacterScopeChanged", "The active character changed while waiting for the item transition.");
        var observed = snapshot.Instances.FirstOrDefault(instance =>
            instance.Fingerprint.Container == fingerprint.Container && instance.Fingerprint.SlotIndex == fingerprint.SlotIndex);
        return observed is null || !SquireFingerprintMatcher.ExactMatch(fingerprint, observed.Fingerprint)
            ? SquireActionResult.Completed("Expected slot transition was observed.")
            : SquireActionResult.Fail("TransitionPending", "The exact item remains in its approved slot.");
    }

    public unsafe SquireActionResult OpenContextMenuProbe(EquipmentInstanceFingerprint fingerprint)
    {
        var snapshot = snapshotSource.Capture();
        var observed = snapshot.Instances.FirstOrDefault(instance =>
            instance.Fingerprint.Container == fingerprint.Container && instance.Fingerprint.SlotIndex == fingerprint.SlotIndex);
        if (!snapshot.Diagnostics.IsComplete || observed is null || !SquireFingerprintMatcher.ExactMatch(fingerprint, observed.Fingerprint))
            return SquireActionResult.Fail("ExactSlotMismatch", "The approved exact slot identity changed before the UI probe.");
        var result = desynthesisUi.OpenExactSlotContextMenu(fingerprint);
        return new SquireActionResult(result.Success, result.Code, result.Message);
    }

    public unsafe string DescribeContextMenuProbe()
    {
        return desynthesisUi.DescribeContextMenu();
    }

    public unsafe string CloseContextMenuProbe()
    {
        desynthesisUi.CloseVisibleUi();
        return "Closed visible Squire diagnostic item UI through its addon controls.";
    }

    public void ReleaseOwnedState()
    {
        recoveryCoordinator.ReleaseOwnedState();
        _ = ReleaseOwnedStateSafelyAsync();
    }

    public async Task<SquireActionResult> RecoverOwnedStateAsync(CancellationToken cancellationToken)
    {
        recoveryCoordinator.ReleaseOwnedState();
        try
        {
            await ReleaseOwnedStateSerializedAsync(cancellationToken).ConfigureAwait(false);
            return SquireActionResult.Completed("Squire closed its owned interaction and released its automation state.");
        }
        catch (OperationCanceledException)
        {
            return SquireActionResult.Fail("RecoveryCancelled", "Interaction recovery was cancelled.");
        }
        catch (Exception ex)
        {
            return SquireActionResult.Fail("RecoveryFailed", ex.Message);
        }
    }

    private async Task ReleaseOwnedStateSafelyAsync()
    {
        try { await ReleaseOwnedStateSerializedAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    private async Task ReleaseOwnedStateSerializedAsync(CancellationToken cancellationToken)
    {
        await ownedStateRecoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ReleaseOwnedStateAsync(cancellationToken).ConfigureAwait(false); }
        finally { ownedStateRecoveryGate.Release(); }
    }

    private async Task ReleaseOwnedStateAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settled = await framework.RunOnTick(() =>
            {
                desynthesisUi.CloseOwnedUi();
                materiaRetrievalUi.CloseOwnedUi();
                expertDeliveryUi.CloseOwnedUi();
                discardUi.CloseOwnedUi();
                vendorSaleUi.CloseOwnedUi();
                return desynthesisUi.IsUiSettled() && materiaRetrievalUi.IsUiSettled() &&
                       expertDeliveryUi.IsUiSettled() && discardUi.IsUiSettled() && vendorSaleUi.IsUiSettled();
            }).ConfigureAwait(false);
            if (settled)
                break;
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        if (expertDeliveryPreparation.OwnsUi)
            await CloseOwnedExpertPreparationAsync(cancellationToken).ConfigureAwait(false);
        if (vendorSalePreparation.OwnsUi)
            await CloseOwnedVendorPreparationAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseOwnedUiUntilSettledAsync(System.Action close, Func<bool> isSettled)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            await framework.RunOnTick(close).ConfigureAwait(false);
            if (await framework.RunOnTick(isSettled).ConfigureAwait(false))
                return;
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
    }

    public void CloseDiagnosticUi()
    {
        _ = framework.RunOnTick(() =>
        {
            desynthesisUi.CloseVisibleUi();
            materiaRetrievalUi.CloseOwnedUi();
            expertDeliveryUi.CloseOwnedUi();
            expertDeliveryPreparation.RecoverDiagnosticUi();
            discardUi.CloseOwnedUi();
            vendorSaleUi.CloseOwnedUi();
            vendorSalePreparation.RecoverDiagnosticUi();
        });
    }

    private bool IsExpectedItemConfirmation(
        EquipmentInstanceFingerprint fingerprint,
        string prompt,
        IReadOnlyList<string> semanticTerms)
    {
        var snapshot = snapshotSource.Capture();
        return snapshot.Diagnostics.IsComplete &&
               snapshot.Identity.Scope == fingerprint.Character &&
               snapshot.Definitions.TryGetValue(fingerprint.ItemId, out var definition) &&
               prompt.Contains(definition.Name, StringComparison.CurrentCultureIgnoreCase) &&
               semanticTerms.Any(term => prompt.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }
}
