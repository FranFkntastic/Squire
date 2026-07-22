using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.Squire;

internal sealed class SquireEvidencePanel(
    SquireCleanupRuleStore ruleStore,
    AgentBridgeUiReviewRegistry reviewRegistry,
    Action refresh)
{
    private uint? pendingExclusionRemovalItemId;

    public void Draw(SquireAnalysis analysis, EquipmentInstanceFingerprint? focusedItem)
    {
        if (focusedItem is not { } fingerprint)
            return;
        var candidate = analysis.Candidates.FirstOrDefault(value =>
            EquipmentInstanceFingerprintComparer.Instance.Equals(value.Instance.Fingerprint, fingerprint));
        if (candidate is null)
            return;

        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, candidate.Definition.Name);
        ImGui.SameLine();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted,
            $"{SquirePresentation.FormatLocation(fingerprint)} | {SquirePresentation.FormatAssessment(candidate.Assessment)} | {SquirePresentation.FormatDisposition(candidate.RecommendedDisposition)}");
        DrawItemEvidence(candidate);
        DrawRuleEvidence(analysis, candidate);
        DrawJobComparisonEvidence(candidate);
        DrawDuplicateRetentionControl(analysis, candidate);
        DrawExclusionControl(analysis, candidate);
    }

    public static void DrawItemLink(SquireAnalysis analysis, SquireCandidate candidate)
    {
        var start = ImGui.GetCursorScreenPos();
        ImGui.TextColored(MarketMafiosoUiTheme.Link, candidate.Definition.Name);
        var end = new Vector2(start.X + ImGui.GetItemRectSize().X, ImGui.GetItemRectMax().Y);
        ImGui.GetWindowDrawList().AddLine(new(start.X, end.Y), end, ImGui.GetColorU32(MarketMafiosoUiTheme.Link));
        if (!ImGui.IsItemHovered())
            return;

        var definition = candidate.Definition;
        var fingerprint = candidate.Instance.Fingerprint;
        var eligibleJobs = analysis.Snapshot.Jobs
            .Where(job => job.IsUnlocked == true && definition.EligibleClassJobIds.Contains(job.ClassJobId))
            .Select(job => $"{job.Abbreviation} Lv. {job.Level}")
            .Distinct().Order().ToArray();
        var effectiveProfile = EquipmentInstanceStats.Resolve(candidate.Instance, definition);
        var stats = effectiveProfile?.Parameters
            .Where(stat => stat.Value != 0)
            .GroupBy(stat => stat.Semantic)
            .Select(group => $"{group.Key} +{group.Max(stat => stat.Value)}")
            .ToList() ?? [];
        if (effectiveProfile is { } profile)
        {
            if (profile.PhysicalDamage > 0) stats.Add($"Physical Damage {profile.PhysicalDamage}");
            if (profile.MagicalDamage > 0) stats.Add($"Magical Damage {profile.MagicalDamage}");
            if (profile.PhysicalDefense > 0) stats.Add($"Physical Defense {profile.PhysicalDefense}");
            if (profile.MagicalDefense > 0) stats.Add($"Magical Defense {profile.MagicalDefense}");
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(360f, ImGui.GetMainViewport().Size.X * 0.4f));
        ImGui.TextColored(MarketMafiosoUiTheme.Header, definition.Name);
        ImGui.TextUnformatted($"Item {definition.ItemId} | {definition.NormalizedRarity} | {definition.Slot} | Equip Lv. {definition.EquipLevel} | Item Lv. {definition.ItemLevel}");
        ImGui.TextUnformatted($"Location: {SquirePresentation.FormatLocation(fingerprint)}{(fingerprint.IsHighQuality ? " | HQ" : string.Empty)}");
        ImGui.Separator();
        ImGui.TextUnformatted($"Eligible obtained jobs: {(eligibleJobs.Length == 0 ? "none" : string.Join(", ", eligibleJobs))}");
        ImGui.TextWrapped($"Stats: {(stats.Count == 0 ? "none" : string.Join(", ", stats))}");
        if (candidate.UseAnalysis is { Comparisons.Count: > 0 } use)
        {
            ImGui.Separator();
            ImGui.TextColored(MarketMafiosoUiTheme.Header, "Saved-gearset comparisons");
            foreach (var comparison in use.Comparisons)
            {
                var sets = comparison.ContributingGearsets.Count == 0
                    ? "no contributing gearset"
                    : string.Join(", ", comparison.ContributingGearsets.Select(set => set.Name).Distinct());
                var baseline = comparison.Baseline is null
                    ? "no baseline"
                    : $"{comparison.Baseline.Name} (iLv {comparison.Baseline.ItemLevel})";
                ImGui.BulletText($"{comparison.Job.Abbreviation}: {comparison.Status}; {baseline}; from {sets}");
                if (comparison.WitnessRequirement is { } requirement)
                    foreach (var witness in requirement.ViableWitnesses)
                        ImGui.BulletText($"  {(witness.IsGearsetReferenced ? "Saved-gearset" : "Owned loose-item")} witness: {witness.ItemName} at {SquirePresentation.FormatLocation(witness.Fingerprint)}{(witness.Fingerprint.IsHighQuality ? " HQ" : string.Empty)}");
            }
        }
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private void DrawExclusionControl(SquireAnalysis analysis, SquireCandidate candidate)
    {
        var itemId = candidate.Definition.ItemId;
        var contentId = analysis.Snapshot.Identity.Scope?.LocalContentId;
        var excluded = ruleStore.IsItemProtected(contentId, itemId);
        if (!excluded)
        {
            var protect = ImGui.Button($"Protect every copy of this item##SquireExclude{itemId}");
            RegisterLastControl(
                "squire.rule.protect-item",
                $"Protect every copy of {candidate.Definition.Name}",
                true,
                null,
                () =>
                {
                    ruleStore.SetItemProtection(contentId, itemId, true);
                    refresh();
                });
            if (protect)
            {
                ruleStore.SetItemProtection(contentId, itemId, true);
                refresh();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Protects every copy of this item ID from Squire cleanup for this character.");
            return;
        }

        var requestRemoval = ImGui.Button($"Stop protecting every copy##SquireExclude{itemId}");
        RegisterLastControl(
            "squire.rule.unprotect-item",
            $"Request removal of the protection rule for {candidate.Definition.Name}",
            true,
            null,
            () => pendingExclusionRemovalItemId = itemId);
        if (requestRemoval)
            pendingExclusionRemovalItemId = itemId;
        if (pendingExclusionRemovalItemId != itemId)
            return;
        ImGui.TextWrapped($"Remove the item protection rule for {candidate.Definition.Name}? All remaining protection rules will still be evaluated.");
        var confirmRemoval = ImGui.Button($"Confirm removal##SquireExcludeConfirm{itemId}");
        RegisterLastControl(
            "squire.rule.unprotect-item-confirm",
            $"Confirm removal of the protection rule for {candidate.Definition.Name}",
            true,
            null,
            () =>
            {
                ruleStore.SetItemProtection(contentId, itemId, false);
                pendingExclusionRemovalItemId = null;
                refresh();
            });
        if (confirmRemoval)
        {
            ruleStore.SetItemProtection(contentId, itemId, false);
            pendingExclusionRemovalItemId = null;
            refresh();
        }
        ImGui.SameLine();
        var cancelRemoval = ImGui.Button($"Cancel##SquireExcludeCancel{itemId}");
        RegisterLastControl(
            "squire.rule.unprotect-item-cancel",
            $"Cancel removal of the protection rule for {candidate.Definition.Name}",
            true,
            null,
            () => pendingExclusionRemovalItemId = null);
        if (cancelRemoval)
            pendingExclusionRemovalItemId = null;
    }

    private static void DrawItemEvidence(SquireCandidate candidate)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        var definition = candidate.Definition;
        var wearer = EquipmentWearerInference.Infer(definition);
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Item and cleanup route");
        if (!ImGui.BeginTable("##SquireItemEvidence", 11, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Location");
        ImGui.TableSetupColumn("Rarity", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Copies", ImGuiTableColumnFlags.WidthFixed, 125);
        ImGui.TableSetupColumn("Equip Lv.", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Item Lv.", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Stats", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Materia", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Inferred wearer", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Assessment", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Authorized route", ImGuiTableColumnFlags.WidthFixed, 165);
        ImGui.TableHeadersRow();
        ImGui.TableNextRow();
        Cell(SquirePresentation.FormatLocation(fingerprint));
        Cell(definition.NormalizedRarity.ToString());
        Cell(fingerprint.IsHighQuality ? "HQ" : "NQ");
        Cell(SquireCandidateTableProjection.FormatCopies(candidate));
        Cell(definition.EquipLevel.ToString());
        Cell(definition.ItemLevel.ToString());
        Cell(FormatEffectiveStats(candidate));
        Cell(fingerprint.MateriaIds.Count.ToString());
        Cell(wearer.Label);
        Cell(SquirePresentation.FormatAssessment(candidate.Assessment));
        Cell(candidate.SupportedDispositions.Count == 0
            ? "Keep"
            : $"{SquirePresentation.FormatDisposition(candidate.RecommendedDisposition)} (supported: {string.Join(", ", candidate.SupportedDispositions.Order().Select(SquirePresentation.FormatDisposition))})");
        ImGui.EndTable();
    }

    private void DrawDuplicateRetentionControl(SquireAnalysis analysis, SquireCandidate candidate)
    {
        if (candidate.DuplicateStatus is not { } status)
            return;
        var contentId = analysis.Snapshot.Identity.Scope?.LocalContentId;
        var itemId = candidate.Definition.ItemId;
        var isHighQuality = candidate.Instance.Fingerprint.IsHighQuality;
        var quality = isHighQuality ? "HQ" : "normal quality";
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Duplicate retention");
        ImGui.TextWrapped($"This character owns {status.OwnedCopies} {quality} cop{(status.OwnedCopies == 1 ? "y" : "ies")}. The minimum is scoped to this character, item ID, and quality, so it survives inventory movement.");
        if (ImGui.BeginTable("##SquireDuplicateRetentionEvidence", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Owned");
            ImGui.TableSetupColumn("Explicit minimum");
            ImGui.TableSetupColumn("Gearset minimum");
            ImGui.TableSetupColumn("Effective floor");
            ImGui.TableSetupColumn("Above floor");
            ImGui.TableHeadersRow();
            ImGui.TableNextRow();
            Cell(status.OwnedCopies.ToString());
            Cell(status.UserMinimumCopies.ToString());
            Cell(status.GearsetRequiredCopies.ToString());
            Cell(status.EffectiveMinimumCopies.ToString());
            Cell(status.CopiesAboveFloor.ToString());
            ImGui.EndTable();
        }

        var minimum = ruleStore.MinimumCopiesToKeep(contentId, itemId, isHighQuality);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
        if (ImGui.InputInt($"Minimum copies to keep##SquireDuplicateMinimum{itemId}{isHighQuality}", ref minimum))
        {
            ruleStore.SetRetention(contentId, itemId, isHighQuality, Math.Clamp(minimum, 0, 99));
            refresh();
        }
        ImGui.SameLine();
        if (ImGui.Button($"-##SquireDuplicateDecrease{itemId}{isHighQuality}"))
        {
            ruleStore.SetRetention(contentId, itemId, isHighQuality, Math.Max(0, minimum - 1));
            refresh();
        }
        RegisterLastControl(
            "squire.duplicate.decrease",
            $"Decrease the retained-copy minimum for {candidate.Definition.Name}",
            minimum > 0,
            minimum.ToString(),
            () =>
            {
                ruleStore.SetRetention(contentId, itemId, isHighQuality, Math.Max(0, minimum - 1));
                refresh();
            });
        ImGui.SameLine();
        if (ImGui.Button($"+##SquireDuplicateIncrease{itemId}{isHighQuality}"))
        {
            ruleStore.SetRetention(contentId, itemId, isHighQuality, Math.Min(99, minimum + 1));
            refresh();
        }
        RegisterLastControl(
            "squire.duplicate.increase",
            $"Increase the retained-copy minimum for {candidate.Definition.Name}",
            minimum < 99,
            minimum.ToString(),
            () =>
            {
                ruleStore.SetRetention(contentId, itemId, isHighQuality, Math.Min(99, minimum + 1));
                refresh();
            });
        if (ImGui.Button($"Keep all {status.OwnedCopies} current copies##SquireDuplicateKeepAll{itemId}{isHighQuality}"))
        {
            ruleStore.SetRetention(contentId, itemId, isHighQuality, status.OwnedCopies);
            refresh();
        }
        RegisterLastControl(
            "squire.duplicate.keep-all",
            $"Keep all current {quality} copies of {candidate.Definition.Name}",
            true,
            status.OwnedCopies.ToString(),
            () =>
            {
                ruleStore.SetRetention(contentId, itemId, isHighQuality, status.OwnedCopies);
                refresh();
            });
        if (minimum > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Clear explicit minimum##SquireDuplicateClear{itemId}{isHighQuality}"))
            {
                ruleStore.SetRetention(contentId, itemId, isHighQuality, 0);
                refresh();
            }
            RegisterLastControl(
                "squire.duplicate.clear",
                $"Clear the retained-copy minimum for {candidate.Definition.Name}",
                true,
                minimum.ToString(),
                () =>
                {
                    ruleStore.SetRetention(contentId, itemId, isHighQuality, 0);
                    refresh();
                });
        }
        ImGui.TextColored(MarketMafiosoUiTheme.Muted,
            "Saved gearsets and other protections still apply. Squire counts copies currently owned by this character; copies already transferred to retainers are outside this floor.");
    }

    private void RegisterLastControl(string id, string label, bool enabled, string? value, Action invoke) =>
        reviewRegistry.RegisterLastButton(id, label, enabled, invoke, value);

    private static string FormatEffectiveStats(SquireCandidate candidate)
    {
        var profile = EquipmentInstanceStats.Resolve(candidate.Instance, candidate.Definition);
        if (profile is null)
            return "Unavailable";
        var stats = profile.Parameters
            .Where(stat => stat.Value != 0)
            .GroupBy(stat => new { stat.Semantic, stat.SourceName })
            .Select(group => $"{group.Key.SourceName ?? FormatStatName(group.Key.Semantic)} +{group.Max(stat => stat.Value)}")
            .ToList();
        if (profile.PhysicalDamage > 0) stats.Add($"Physical Damage {profile.PhysicalDamage}");
        if (profile.MagicalDamage > 0) stats.Add($"Magical Damage {profile.MagicalDamage}");
        if (profile.PhysicalDefense > 0) stats.Add($"Physical Defense {profile.PhysicalDefense}");
        if (profile.MagicalDefense > 0) stats.Add($"Magical Defense {profile.MagicalDefense}");
        return stats.Count == 0 ? "None" : string.Join(", ", stats);
    }

    private static string FormatStatName(EquipmentStatSemantic semantic) => semantic switch
    {
        EquipmentStatSemantic.CriticalHit => "Critical Hit",
        EquipmentStatSemantic.DirectHit => "Direct Hit",
        EquipmentStatSemantic.SkillSpeed => "Skill Speed",
        EquipmentStatSemantic.SpellSpeed => "Spell Speed",
        EquipmentStatSemantic.CraftingPoints => "CP",
        EquipmentStatSemantic.GatheringPoints => "GP",
        _ => semantic.ToString(),
    };

    private static void DrawRuleEvidence(SquireAnalysis analysis, SquireCandidate candidate)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Evaluation rules");
        if (!ImGui.BeginTable("##SquireRuleEvidence", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableSetupColumn("Effect", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Observed evidence");
        ImGui.TableHeadersRow();
        foreach (var reason in candidate.Reasons)
        {
            ImGui.TableNextRow();
            Cell(SquirePresentation.ReasonLabel(reason.Code));
            Cell(DescribeReasonEffect(reason));
            Cell(DescribeReasonEvidence(analysis, candidate, reason));
        }
        ImGui.EndTable();

        DrawCleanupRuleTrace(candidate);
    }

    private static void DrawCleanupRuleTrace(SquireCandidate candidate)
    {
        if (candidate.RuleEvaluation is not { } evaluation)
            return;
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Cleanup rule proof");
        if (evaluation.Errors.Count > 0)
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, string.Join(" ", evaluation.Errors));
        if (evaluation.MatchedRules.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No configurable cleanup rule matched this item.");
            return;
        }
        if (!ImGui.BeginTable("##SquireCleanupRuleTrace", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Matched rule");
        ImGui.TableSetupColumn("Effect");
        ImGui.TableSetupColumn("Outcome", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableHeadersRow();
        foreach (var trace in evaluation.MatchedRules)
        {
            ImGui.TableNextRow();
            Cell(trace.Priority.ToString());
            Cell(trace.RuleName);
            Cell(DescribeCleanupRuleEffect(trace.Effect));
            Cell(trace.WonDecision && trace.WonDisposition
                ? "Won decision and route"
                : trace.WonDecision
                    ? "Won decision"
                    : trace.WonDisposition
                        ? "Won route"
                        : trace.Effect.MinimumCopies > 0 || trace.Effect.Authorizations != SquireCleanupAuthorization.None
                            ? "Contributed constraint"
                            : "Lower priority");
        }
        ImGui.EndTable();
    }

    private static string DescribeCleanupRuleEffect(SquireCleanupRuleEffect effect)
    {
        var values = new List<string>();
        if (effect.Decision != SquireCleanupDecision.NoChange) values.Add(effect.Decision.ToString());
        if (effect.PreferredDisposition is { } route) values.Add($"route: {SquirePresentation.FormatDisposition(route)}");
        if (effect.MinimumCopies > 0) values.Add($"retain at least {effect.MinimumCopies}");
        if (effect.Authorizations != SquireCleanupAuthorization.None) values.Add($"authorize {effect.Authorizations}");
        return values.Count == 0 ? "No effect" : string.Join("; ", values);
    }

    private static void DrawJobComparisonEvidence(SquireCandidate candidate)
    {
        if (candidate.UseAnalysis is not { Comparisons.Count: > 0 } use)
            return;
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Retained equipment proof");
        if (!ImGui.BeginTable("##SquireJobEvidence", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Retained witness");
        ImGui.TableSetupColumn("Evidence source");
        ImGui.TableSetupColumn("Covers jobs");
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthFixed, 135);
        ImGui.TableHeadersRow();
        var groups = use.Comparisons.GroupBy(comparison => new
        {
            BaselineItemId = comparison.Baseline?.ItemId ?? 0,
            BaselineName = comparison.Baseline?.Name ?? "No covering owned witness",
            BaselineLevel = comparison.Baseline?.ItemLevel,
            comparison.Status,
            Source = DescribeComparisonSource(comparison),
            comparison.Diagnostic,
        });
        foreach (var group in groups)
        {
            ImGui.TableNextRow();
            Cell(group.Key.BaselineLevel is { } itemLevel ? $"{group.Key.BaselineName} (iLv {itemLevel})" : group.Key.BaselineName);
            Cell(group.Key.Source);
            Cell(string.Join(", ", group.Select(value => $"{value.Job.Abbreviation} {value.Job.Level}").Distinct().Order()));
            Cell(FormatComparisonStatus(group.Key.Status));
        }
        ImGui.EndTable();
    }

    private static string DescribeComparisonSource(EquipmentJobComparison comparison)
    {
        var witnesses = comparison.WitnessRequirement?.ViableWitnesses ?? [];
        var rejected = comparison.RejectedGearsets is { Count: > 0 }
            ? $" Rejected saved source(s): {string.Join("; ", comparison.RejectedGearsets)}."
            : string.Empty;
        if (witnesses.Count > 0)
            return $"{(comparison.Basis == EquipmentComparisonBasis.SavedGearset ? "Matches a saved gearset item" : "Synthesized owned loadout")}: " +
                   string.Join("; ", witnesses.Select(value => $"{SquirePresentation.FormatLocation(value.Fingerprint)}{(value.Fingerprint.IsHighQuality ? " HQ" : string.Empty)} [{value.CoverageKind}]").Distinct().Order()) + rejected;
        if (comparison.Baseline is null && comparison.Diagnostic is { Length: > 0 } diagnostic)
            return diagnostic;
        if (comparison.ContributingGearsets.Count > 0)
            return $"Saved gearset: {string.Join(", ", comparison.ContributingGearsets.Select(value => value.Name).Distinct().Order())}";
        return comparison.Diagnostic ?? "No trusted source";
    }

    private static string FormatComparisonStatus(EquipmentUseStatus status) => status switch
    {
        EquipmentUseStatus.Obsolete => "Safely superseded",
        EquipmentUseStatus.FutureUse => "Future-use check",
        EquipmentUseStatus.BaselineNotBetter => "Does not cover without loss",
        EquipmentUseStatus.NoObtainedEligibleJob => "No obtained job",
        EquipmentUseStatus.LikelyCosmetic => "Likely cosmetic",
        EquipmentUseStatus.SpecialPurpose => "Special-purpose protection",
        EquipmentUseStatus.EvaluationFailure => "Evaluation failed",
        _ => status.ToString(),
    };

    private static string DescribeReasonEffect(SquireReason reason) => reason.Code switch
    {
        "MateriaRetrievalRequired" => "Pre-cleanup risk",
        "MateriaRetrievalNotUnlocked" or "MateriaRetrievalUnlockUnknown" or "MateriaRetrievalRiskNotAuthorized" => "Materia protection",
        "DesynthesisNotUnlocked" => "Limits route",
        "HighRarityProtectionDisabled" => "Policy note",
        "DuplicateRetentionSurplus" => "Policy note",
        _ => reason.Severity switch
        {
            SquireReasonSeverity.Blocking => "Protects item",
            SquireReasonSeverity.Warning => "Caution",
            _ => "Supports verdict",
        },
    };

    private static string DescribeReasonEvidence(SquireAnalysis analysis, SquireCandidate candidate, SquireReason reason)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        var comparisons = candidate.UseAnalysis?.Comparisons ?? [];
        return reason.Code switch
        {
            "RetainedCoverageForAllUnlockedJobs" => $"{comparisons.Count} relevant obtained job(s) checked; every comparison has a retained baseline that safely supersedes this item. See the proof table below.",
            "NoRetainedCoverage" => $"{comparisons.Count(value => value.Status == EquipmentUseStatus.BaselineNotBetter)} job comparison(s) found no retained baseline that safely supersedes this item. See the proof table below.",
            "FutureUnlockedJobUse" => string.Join(", ", comparisons.Where(value => value.Status == EquipmentUseStatus.FutureUse).Select(value => $"{value.Job.Abbreviation} {value.Job.Level} < equip {candidate.Definition.EquipLevel}")),
            "FutureLevelingUseNotProtected" => $"Future-use protection is disabled; {comparisons.Count(value => value.Status == EquipmentUseStatus.FutureUse)} lower-level obtained job comparison(s) do not block cleanup.",
            "NoObtainedEligibleJob" => DescribeEligibleJobs(analysis, candidate),
            "MateriaRetrievalRequired" => $"Exact slot currently contains {fingerprint.MateriaIds.Count} materia. Squire will attempt retrieval and revalidate after each attempt before {SquirePresentation.FormatDisposition(candidate.RecommendedDisposition)}; failed retrieval can destroy materia.",
            "MateriaRetrievalNotUnlocked" or "MateriaRetrievalUnlockUnknown" or "MateriaRetrievalRiskNotAuthorized" => reason.Message,
            "CurrentlyEquipped" => $"The exact {SquirePresentation.FormatLocation(fingerprint)} instance is equipped in the live snapshot.",
            "ReferencedByGearset" => DescribeGearsetReferences(analysis, candidate),
            "HighRarityEquipment" => $"The item is {candidate.Definition.NormalizedRarity}, and the default-on blue and purple gear protection setting is enabled.",
            "HighRarityProtectionDisabled" => $"The item is {candidate.Definition.NormalizedRarity}, but blue and purple gear protection is disabled. Other protections and explicit character rules still apply.",
            "ItemProtectionRule" or "InvalidRuleConfiguration" => reason.Message,
            "DuplicateRetentionFloor" or "DuplicateRetentionSurplus" when candidate.DuplicateStatus is { } duplicate =>
                $"Owned {duplicate.OwnedCopies}; explicit minimum {duplicate.UserMinimumCopies}; saved-gearset minimum {duplicate.GearsetRequiredCopies}; effective floor {duplicate.EffectiveMinimumCopies}; copies above floor {duplicate.CopiesAboveFloor}.",
            "DesynthesisNotUnlocked" => $"Desynthesis is absent from the supported routes; current authorized routes: {string.Join(", ", candidate.SupportedDispositions.Order().Select(SquirePresentation.FormatDisposition))}.",
            "StatlessAllClassesEquipment" => "The all-class item has no primary, crafting, or gathering attributes that identify a functional wearer; incidental defense does not make it leveling gear.",
            "SpecialPurposeEquipment" => "The item carries a game-defined special bonus, action, or otherwise non-comparable utility attribute, so ordinary stat dominance cannot authorize cleanup.",
            "NoSupportedDisposition" => "The eligibility evaluator produced no authorized cleanup route; execution remains disabled.",
            _ when candidate.UseAnalysis?.Diagnostic is { Length: > 0 } diagnostic => diagnostic,
            _ => reason.Message,
        };
    }

    private static string DescribeEligibleJobs(SquireAnalysis analysis, SquireCandidate candidate)
    {
        var eligible = analysis.Snapshot.Jobs
            .Where(job => candidate.Definition.EligibleClassJobIds.Contains(job.ClassJobId))
            .Select(job => $"{job.Abbreviation}: {(job.IsUnlocked == true ? $"obtained, level {job.Level}" : "unobtained")}")
            .Distinct().Order().ToArray();
        return eligible.Length == 0
            ? "The item definition exposes no class/job-specific consumer; its all-classes stat evaluation supplies the verdict."
            : $"Eligible jobs observed: {string.Join("; ", eligible)}. None of the eligible jobs are obtained by this character.";
    }

    private static string DescribeGearsetReferences(SquireAnalysis analysis, SquireCandidate candidate)
    {
        var sets = analysis.Snapshot.Gearsets
            .Where(set => set.IsValid && set.Items.Any(item => item.ItemId == candidate.Definition.ItemId))
            .Select(set => set.Name).Distinct().Order().ToArray();
        return sets.Length == 0
            ? $"Item {candidate.Definition.ItemId} is required to preserve saved-gearset multiplicity, but no named valid set was available in this presentation snapshot. Better owned equipment does not release this protection automatically."
            : $"Required by valid saved gearset(s): {string.Join(", ", sets)}. This protection follows the saved assignment; better owned equipment does not release it automatically.";
    }

    private static void Cell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "—" : value);
    }
}
