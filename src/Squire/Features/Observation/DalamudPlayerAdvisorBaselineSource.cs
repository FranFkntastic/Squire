using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Player;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Observation;

/// <summary>
/// Captures one windowless player-advisor baseline. Callers must invoke <see cref="Capture"/>
/// synchronously on the framework thread; no native pointer escapes the call.
/// </summary>
public sealed unsafe class DalamudPlayerAdvisorBaselineSource : IOutfitterTargetAdvisorBaselineSource
{
    private readonly ICharacterEquipmentSnapshotSource snapshotSource;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;

    public DalamudPlayerAdvisorBaselineSource(
        ICharacterEquipmentSnapshotSource snapshotSource,
        IPlayerState playerState,
        IDataManager dataManager)
    {
        this.snapshotSource = snapshotSource;
        this.playerState = playerState;
        this.dataManager = dataManager;
    }

    public PlayerAdvisorBaseline Capture()
    {
        CharacterEquipmentSnapshot? snapshot = null;
        PlayerAdvisorCaptureHeader? before = null;
        try
        {
            if (!TryCaptureHeader(out before, out var diagnostic))
                return PlayerAdvisorBaselineAssembler.Failure(PlayerAdvisorBaselineStatus.Unavailable, diagnostic);

            snapshot = snapshotSource.Capture();
            if (!TryCaptureHeader(out var afterSnapshot, out diagnostic))
                return PlayerAdvisorBaselineAssembler.Failure(PlayerAdvisorBaselineStatus.Unavailable, diagnostic, snapshot, before);
            if (afterSnapshot != before)
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Inconsistent,
                    "The player identity, job, world, or level changed while the equipment snapshot was captured.",
                    snapshot,
                    before);

            var family = AdvisorStatFamilies.Resolve(before!.ClassJobId);
            if (family is null)
                return PlayerAdvisorBaselineAssembler.Assemble(
                    snapshot,
                    before,
                    null,
                    new Dictionary<EquipmentStatSemantic, int>(),
                    []);

            var totals = new Dictionary<EquipmentStatSemantic, int>();
            foreach (var semantic in family.RelevantSemantics)
            {
                if (!TryMapPlayerAttribute(semantic, out var attribute))
                    return PlayerAdvisorBaselineAssembler.Failure(
                        PlayerAdvisorBaselineStatus.Unavailable,
                        $"Advisor semantic {semantic} has no PlayerState attribute mapping.",
                        snapshot,
                        before);
                var value = playerState.GetAttribute(attribute);
                if (value < 0)
                    return PlayerAdvisorBaselineAssembler.Failure(
                        PlayerAdvisorBaselineStatus.Unavailable,
                        $"PlayerState returned an invalid negative {semantic} total.",
                        snapshot,
                        before);
                totals.Add(semantic, value);
            }

            var baseParamSheet = dataManager.GetExcelSheet<BaseParam>();
            if (baseParamSheet is null || !TryResolveBaseParamIds(
                    baseParamSheet.Select(value => (value.RowId, (string?)value.Name.ToString())),
                    family.RelevantSemantics,
                    out var baseParamIds,
                    out diagnostic))
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Unavailable,
                    baseParamSheet is null ? "The BaseParam sheet is unavailable." : diagnostic,
                    snapshot,
                    before);
            var materiaSheet = dataManager.GetExcelSheet<Materia>();

            var manager = InventoryManager.Instance();
            if (manager == null)
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Unavailable,
                    "InventoryManager is unavailable.",
                    snapshot,
                    before);
            var container = manager->GetInventoryContainer(InventoryType.EquippedItems);
            if (container == null || !container->IsLoaded || container->Size <= 12)
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Unavailable,
                    "The EquippedItems container is unavailable or has an unsupported layout.",
                    snapshot,
                    before);

            var equipped = new List<PlayerAdvisorEquippedItemCapture>(PlayerAdvisorEquippedSlotMap.All.Count);
            foreach (var position in PlayerAdvisorEquippedSlotMap.All)
            {
                var item = container->GetInventorySlot(position.EquippedIndex);
                if (item == null)
                    return PlayerAdvisorBaselineAssembler.Failure(
                        PlayerAdvisorBaselineStatus.Unavailable,
                        $"Equipped index {position.EquippedIndex} ({position.PositionKey}) could not be read.",
                        snapshot,
                        before);
                if (item->ItemId == 0)
                {
                    equipped.Add(new(
                        position.EquippedIndex,
                        0,
                        EquipmentQuality.Normal,
                        family.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0),
                        [],
                        []));
                    continue;
                }

                var itemId = item->GetBaseItemId();
                if (itemId == 0)
                    return PlayerAdvisorBaselineAssembler.Failure(
                        PlayerAdvisorBaselineStatus.Incomplete,
                        $"Equipped index {position.EquippedIndex} ({position.PositionKey}) has no base item identity.",
                        snapshot,
                        before);
                var contributions = new Dictionary<EquipmentStatSemantic, int>();
                var definitionProfile = snapshot.Definitions.TryGetValue(itemId, out var definition)
                    ? definition.ResolveStatProfile(item->IsHighQuality() ? EquipmentQuality.High : EquipmentQuality.Normal)
                    : null;
                var materiaIds = new List<uint>(5);
                var materiaGrades = new List<byte>(5);
                for (byte materiaIndex = 0; materiaIndex < 5; materiaIndex++)
                {
                    var materiaId = item->GetMateriaId(materiaIndex);
                    if (materiaId == 0)
                        continue;
                    materiaIds.Add(materiaId);
                    materiaGrades.Add(item->GetMateriaGrade(materiaIndex));
                }
                IReadOnlyList<uint>? materiaBaseParamIds = [];
                if (materiaIds.Count != 0)
                {
                    if (materiaSheet is null)
                    {
                        materiaBaseParamIds = null;
                    }
                    else
                    {
                        var resolvedMateria = new List<uint>(materiaIds.Count);
                        foreach (var materiaId in materiaIds)
                        {
                            var materia = materiaSheet.GetRow(materiaId);
                            if (materia.RowId != materiaId || materia.BaseParam.RowId == 0)
                            {
                                materiaBaseParamIds = null;
                                break;
                            }
                            resolvedMateria.Add(materia.BaseParam.RowId);
                        }
                        if (materiaBaseParamIds is not null)
                            materiaBaseParamIds = resolvedMateria;
                    }
                }
                foreach (var semantic in family.RelevantSemantics)
                {
                    if (definitionProfile is not null && family.TryGetNonParameterDefinitionValue(definitionProfile, semantic, out var scalar))
                    {
                        contributions.Add(semantic, scalar);
                        continue;
                    }
                    if (semantic is EquipmentStatSemantic.PhysicalDamage or
                        EquipmentStatSemantic.PhysicalDefense or EquipmentStatSemantic.MagicalDefense)
                    {
                        return PlayerAdvisorBaselineAssembler.Failure(
                            PlayerAdvisorBaselineStatus.Incomplete,
                            $"Equipped item {itemId} has no exact quality-specific {semantic} definition value.",
                            snapshot,
                            before);
                    }
                    var value = InventoryItem.GetParameterValue(
                        baseParamIds[semantic],
                        item,
                        includeMateria: true,
                        checkHQ: true,
                        checkPvPCharacterFlag: false,
                        checkPvPItemFlag: false);
                    if (value > int.MaxValue)
                    {
                        if (definition is null || AdvisorEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(definition) ||
                            !TryGetStaticUnmeldedContribution(
                                definitionProfile,
                                semantic,
                                baseParamIds[semantic],
                                materiaBaseParamIds,
                                out var staticValue))
                        {
                            return PlayerAdvisorBaselineAssembler.Failure(
                                PlayerAdvisorBaselineStatus.Unavailable,
                                $"Equipped index {position.EquippedIndex} ({position.PositionKey}) returned an invalid {semantic} contribution.",
                                snapshot,
                                before);
                        }
                        contributions.Add(semantic, staticValue);
                        continue;
                    }
                    contributions.Add(semantic, (int)value);
                }
                equipped.Add(new(
                    position.EquippedIndex,
                    itemId,
                    item->IsHighQuality() ? EquipmentQuality.High : EquipmentQuality.Normal,
                    contributions,
                    materiaIds,
                    materiaGrades));
            }

            if (!TryCaptureHeader(out var after, out diagnostic))
                return PlayerAdvisorBaselineAssembler.Failure(PlayerAdvisorBaselineStatus.Unavailable, diagnostic, snapshot, before);
            if (after != before)
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Inconsistent,
                    "The player identity, job, world, or level changed while the advisor baseline was captured.",
                    snapshot,
                    before);

            var trustedCapture = PlayerAdvisorTrustedCapture.Complete(Guid.NewGuid(), DateTimeOffset.UtcNow);
            return PlayerAdvisorBaselineAssembler.Assemble(snapshot, before, family, totals, equipped, trustedCapture);
        }
        catch (Exception ex)
        {
            return PlayerAdvisorBaselineAssembler.Failure(
                PlayerAdvisorBaselineStatus.Unavailable,
                $"Windowless player baseline capture failed safely: {ex.Message}",
                snapshot,
                before);
        }
    }

    internal static bool TryMapPlayerAttribute(EquipmentStatSemantic semantic, out PlayerAttribute attribute)
    {
        attribute = semantic switch
        {
            EquipmentStatSemantic.Strength => PlayerAttribute.Strength,
            EquipmentStatSemantic.Dexterity => PlayerAttribute.Dexterity,
            EquipmentStatSemantic.Vitality => PlayerAttribute.Vitality,
            EquipmentStatSemantic.Intelligence => PlayerAttribute.Intelligence,
            EquipmentStatSemantic.Mind => PlayerAttribute.Mind,
            EquipmentStatSemantic.CriticalHit => PlayerAttribute.CriticalHit,
            EquipmentStatSemantic.Determination => PlayerAttribute.Determination,
            EquipmentStatSemantic.DirectHit => PlayerAttribute.DirectHitRate,
            EquipmentStatSemantic.SkillSpeed => PlayerAttribute.SkillSpeed,
            EquipmentStatSemantic.SpellSpeed => PlayerAttribute.SpellSpeed,
            EquipmentStatSemantic.Tenacity => PlayerAttribute.Tenacity,
            EquipmentStatSemantic.Piety => PlayerAttribute.Piety,
            EquipmentStatSemantic.Craftsmanship => PlayerAttribute.Craftsmanship,
            EquipmentStatSemantic.Control => PlayerAttribute.Control,
            EquipmentStatSemantic.CraftingPoints => PlayerAttribute.CraftingPoints,
            EquipmentStatSemantic.Gathering => PlayerAttribute.Gathering,
            EquipmentStatSemantic.Perception => PlayerAttribute.Perception,
            EquipmentStatSemantic.GatheringPoints => PlayerAttribute.GatheringPoints,
            EquipmentStatSemantic.PhysicalDamage => PlayerAttribute.PhysicalDamage,
            EquipmentStatSemantic.MagicalDamage => PlayerAttribute.MagicDamage,
            EquipmentStatSemantic.PhysicalDefense => PlayerAttribute.Defense,
            EquipmentStatSemantic.MagicalDefense => PlayerAttribute.MagicDefense,
            EquipmentStatSemantic.PiercingResistance => PlayerAttribute.PiercingResistance,
            _ => default,
        };
        return semantic is
            EquipmentStatSemantic.Strength or EquipmentStatSemantic.Dexterity or EquipmentStatSemantic.Vitality or
            EquipmentStatSemantic.Intelligence or EquipmentStatSemantic.Mind or EquipmentStatSemantic.CriticalHit or
            EquipmentStatSemantic.Determination or EquipmentStatSemantic.DirectHit or EquipmentStatSemantic.SkillSpeed or
            EquipmentStatSemantic.SpellSpeed or EquipmentStatSemantic.Tenacity or EquipmentStatSemantic.Piety or
            EquipmentStatSemantic.Craftsmanship or EquipmentStatSemantic.Control or EquipmentStatSemantic.CraftingPoints or
            EquipmentStatSemantic.Gathering or EquipmentStatSemantic.Perception or EquipmentStatSemantic.GatheringPoints or
            EquipmentStatSemantic.PhysicalDamage or EquipmentStatSemantic.MagicalDamage or EquipmentStatSemantic.PhysicalDefense or
             EquipmentStatSemantic.MagicalDefense or EquipmentStatSemantic.PiercingResistance;
    }

    internal static bool TryResolveBaseParamIds(
        IEnumerable<(uint RowId, string? Name)> rows,
        IEnumerable<EquipmentStatSemantic> requiredSemantics,
        out IReadOnlyDictionary<EquipmentStatSemantic, uint> ids,
        out string diagnostic)
    {
        var bySemantic = rows
            .Where(value => value.RowId != 0)
            .Select(value => (value.RowId, Semantic: DalamudCharacterEquipmentSnapshotSource.MapStatSemantic(value.RowId, value.Name)))
            .Where(value => value.Semantic != EquipmentStatSemantic.Unknown)
            .GroupBy(value => value.Semantic)
            .ToDictionary(group => group.Key, group => group.Select(value => value.RowId).Distinct().ToArray());
        var resolved = new Dictionary<EquipmentStatSemantic, uint>();
        foreach (var semantic in requiredSemantics.Distinct())
        {
            if (!bySemantic.TryGetValue(semantic, out var matches) || matches.Length != 1)
            {
                ids = new Dictionary<EquipmentStatSemantic, uint>();
                diagnostic = matches is { Length: > 1 }
                    ? $"BaseParam has multiple rows for advisor semantic {semantic}."
                    : $"BaseParam has no row for advisor semantic {semantic}.";
                return false;
            }
            resolved.Add(semantic, matches[0]);
        }
        ids = resolved;
        diagnostic = string.Empty;
        return true;
    }

    public PlayerAdvisorBaseline Capture(OutfitterTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        CharacterEquipmentSnapshot? snapshot = null;
        try
        {
            snapshot = snapshotSource.Capture();
            var resolution = SavedGearsetTargetResolver.Resolve(snapshot, target);
            var classJobId = resolution.Fingerprint?.ClassJobId ?? target.Job?.ClassJobId;
            var family = classJobId is null ? null : AdvisorStatFamilies.Resolve(classJobId.Value);
            if (resolution.Status != SavedGearsetTargetResolutionStatus.Complete || family is null ||
                family is PhysicalRangedAdvisorStatFamily)
            {
                return SavedGearsetAdvisorBaselineAssembler.Assemble(snapshot, target, resolution, family, []);
            }

            var baseParamSheet = dataManager.GetExcelSheet<BaseParam>();
            var diagnostic = string.Empty;
            if (baseParamSheet is null || !TryResolveBaseParamIds(
                    baseParamSheet.Select(value => (value.RowId, (string?)value.Name.ToString())),
                    family.RelevantSemantics,
                    out var baseParamIds,
                    out diagnostic))
            {
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Unavailable,
                    baseParamSheet is null ? "The BaseParam sheet is unavailable." : diagnostic,
                    snapshot);
            }
            var materiaSheet = dataManager.GetExcelSheet<Materia>();
            var captures = new List<SavedGearsetItemStatCapture>();
            foreach (var resolved in resolution.Slots.Where(value => !value.IsEmpty))
            {
                if (resolved.Instance is null || resolved.Definition is null ||
                    !TryGetCurrentItem(resolved.Instance.Fingerprint, out var item, out diagnostic))
                {
                    return PlayerAdvisorBaselineAssembler.Failure(
                        PlayerAdvisorBaselineStatus.Inconsistent,
                        diagnostic,
                        snapshot);
                }

                var profile = resolved.Definition.ResolveStatProfile(
                    item->IsHighQuality() ? EquipmentQuality.High : EquipmentQuality.Normal);
                var materiaBaseParamIds = ResolveMateriaBaseParamIds(item, materiaSheet);
                var contributions = new Dictionary<EquipmentStatSemantic, int>();
                foreach (var semantic in family.RelevantSemantics)
                {
                    var value = InventoryItem.GetParameterValue(
                        baseParamIds[semantic],
                        item,
                        includeMateria: true,
                        checkHQ: true,
                        checkPvPCharacterFlag: false,
                        checkPvPItemFlag: false);
                    if (value > int.MaxValue)
                    {
                        if (AdvisorEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(resolved.Definition) ||
                            !TryGetStaticUnmeldedContribution(
                                profile,
                                semantic,
                                baseParamIds[semantic],
                                materiaBaseParamIds,
                                out var staticValue))
                        {
                            return PlayerAdvisorBaselineAssembler.Failure(
                                PlayerAdvisorBaselineStatus.Unavailable,
                                $"Saved-gearset position {resolved.Position} returned an invalid exact {semantic} contribution.",
                                snapshot);
                        }
                        contributions.Add(semantic, staticValue);
                        continue;
                    }
                    contributions.Add(semantic, checked((int)value));
                }
                captures.Add(new(resolved.Position, resolved.Instance.Fingerprint, contributions));
            }

            foreach (var resolved in resolution.Slots.Where(value => !value.IsEmpty))
            {
                if (resolved.Instance is null || !TryGetCurrentItem(resolved.Instance.Fingerprint, out _, out diagnostic))
                    return PlayerAdvisorBaselineAssembler.Failure(PlayerAdvisorBaselineStatus.Inconsistent, diagnostic, snapshot);
            }
            var gearsetDiagnostics = new List<SnapshotComponentDiagnostic>();
            var currentGearsets = DalamudCharacterEquipmentSnapshotSource.CaptureGearsets(gearsetDiagnostics);
            var currentSnapshot = snapshot with
            {
                Gearsets = currentGearsets,
                Diagnostics = new(snapshot.Diagnostics.Components
                    .Where(value => !string.Equals(value.Component, "gearsets", StringComparison.Ordinal))
                    .Concat(gearsetDiagnostics)
                    .ToArray()),
            };
            var currentResolution = SavedGearsetTargetResolver.Resolve(currentSnapshot, target);
            if (currentResolution.Status != SavedGearsetTargetResolutionStatus.Complete ||
                currentResolution.Fingerprint?.Value != resolution.Fingerprint?.Value)
            {
                return SavedGearsetAdvisorBaselineAssembler.Assemble(currentSnapshot, target, currentResolution, family, []);
            }
            var currentByPosition = currentResolution.Slots.ToDictionary(value => value.Position);
            if (resolution.Slots.Where(value => !value.IsEmpty).Any(previous =>
                    previous.Instance is null ||
                    !currentByPosition.TryGetValue(previous.Position, out var current) ||
                    current.Instance is null ||
                    !EquipmentInstanceFingerprintComparer.Instance.Equals(previous.Instance.Fingerprint, current.Instance.Fingerprint)))
            {
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Inconsistent,
                    "Saved gearset or owned instance identity changed while its baseline was captured.",
                    currentSnapshot);
            }
            if (!playerState.IsLoaded || playerState.ContentId != currentSnapshot.Identity.Scope?.LocalContentId ||
                playerState.HomeWorld.RowId != currentSnapshot.Identity.Scope?.HomeWorldId ||
                playerState.CurrentWorld.RowId != currentSnapshot.Identity.CurrentWorldId)
            {
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Inconsistent,
                    "Character identity or world changed while the saved-gearset baseline was captured.",
                    currentSnapshot);
            }

            var trustedCapture = PlayerAdvisorTrustedCapture.Complete(Guid.NewGuid(), DateTimeOffset.UtcNow);
            return SavedGearsetAdvisorBaselineAssembler.Assemble(
                currentSnapshot,
                target,
                currentResolution,
                family,
                captures,
                trustedCapture);
        }
        catch (Exception ex)
        {
            return PlayerAdvisorBaselineAssembler.Failure(
                PlayerAdvisorBaselineStatus.Unavailable,
                $"Windowless saved-gearset baseline capture failed safely: {ex.Message}",
                snapshot);
        }
    }

    private static IReadOnlyList<uint>? ResolveMateriaBaseParamIds(InventoryItem* item, ExcelSheet<Materia>? materiaSheet)
    {
        var result = new List<uint>(5);
        for (byte index = 0; index < 5; index++)
        {
            var materiaId = item->GetMateriaId(index);
            if (materiaId == 0)
                continue;
            if (materiaSheet is null)
                return null;
            var materia = materiaSheet.GetRow(materiaId);
            if (materia.RowId != materiaId || materia.BaseParam.RowId == 0)
                return null;
            result.Add(materia.BaseParam.RowId);
        }
        return result;
    }

    private static bool TryGetCurrentItem(
        EquipmentInstanceFingerprint expected,
        out InventoryItem* item,
        out string diagnostic)
    {
        item = null;
        if (!Enum.TryParse<InventoryType>(expected.Container, out var inventoryType))
        {
            diagnostic = $"Owned container '{expected.Container}' is not a supported inventory identity.";
            return false;
        }
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(inventoryType);
        if (container == null || !container->IsLoaded || expected.SlotIndex < 0 || expected.SlotIndex >= container->Size)
        {
            diagnostic = $"Owned instance {expected.Container}:{expected.SlotIndex} is no longer available.";
            return false;
        }
        item = container->GetInventorySlot(expected.SlotIndex);
        if (item == null || item->GetBaseItemId() != expected.ItemId ||
            item->IsHighQuality() != expected.IsHighQuality ||
            (item->GlamourId == 0 ? null : item->GlamourId) != expected.GlamourId ||
            !new[] { item->GetStain(0), item->GetStain(1) }.SequenceEqual(expected.Stains))
        {
            diagnostic = $"Owned instance {expected.Container}:{expected.SlotIndex} changed after gearset resolution.";
            item = null;
            return false;
        }
        var materiaIds = new List<uint>(5);
        var materiaGrades = new List<byte>(5);
        for (byte index = 0; index < 5; index++)
        {
            var materiaId = item->GetMateriaId(index);
            if (materiaId == 0)
                continue;
            materiaIds.Add(materiaId);
            materiaGrades.Add(item->GetMateriaGrade(index));
        }
        if (!materiaIds.SequenceEqual(expected.MateriaIds) ||
            !materiaGrades.SequenceEqual(expected.MateriaGrades ?? []))
        {
            diagnostic = $"Owned instance {expected.Container}:{expected.SlotIndex} materia changed after gearset resolution.";
            item = null;
            return false;
        }
        diagnostic = string.Empty;
        return true;
    }

    internal static bool TryGetStaticUnmeldedContribution(
        EquipmentStatProfile? profile,
        EquipmentStatSemantic semantic,
        uint semanticBaseParamId,
        IReadOnlyList<uint>? attachedMateriaBaseParamIds,
        out int value)
    {
        value = 0;
        if (profile is not { IsComplete: true } || semanticBaseParamId == 0 || attachedMateriaBaseParamIds is null ||
            attachedMateriaBaseParamIds.Contains(semanticBaseParamId))
        {
            return false;
        }
        try
        {
            value = profile.Parameters
                .Where(parameter => parameter.Semantic == semantic)
                .Aggregate(0, (sum, parameter) => checked(sum + parameter.Value));
            return value >= 0;
        }
        catch (OverflowException)
        {
            value = 0;
            return false;
        }
    }

    private bool TryCaptureHeader(out PlayerAdvisorCaptureHeader? header, out string diagnostic)
    {
        header = null;
        if (!playerState.IsLoaded || playerState.ContentId == 0)
        {
            diagnostic = "No active player character is loaded.";
            return false;
        }
        var classJobId = playerState.ClassJob.RowId;
        var homeWorldId = playerState.HomeWorld.RowId;
        var currentWorldId = playerState.CurrentWorld.RowId;
        var name = playerState.CharacterName.ToString();
        if (classJobId == 0 || homeWorldId == 0 || currentWorldId == 0 || string.IsNullOrWhiteSpace(name))
        {
            diagnostic = "The active player header is incomplete.";
            return false;
        }
        header = new(
            new CharacterScope(playerState.ContentId, name, homeWorldId),
            currentWorldId,
            classJobId,
            playerState.Level,
            playerState.EffectiveLevel,
            playerState.IsLevelSynced);
        diagnostic = string.Empty;
        return true;
    }
}
