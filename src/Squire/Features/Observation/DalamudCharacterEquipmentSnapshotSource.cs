using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using Lumina.Excel.Sheets;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudCharacterEquipmentSnapshotSource : ICharacterEquipmentSnapshotSource
{
    private static readonly (InventoryType Type, EquipmentSlot Slot, bool Equipped)[] Containers =
    [
        (InventoryType.EquippedItems, EquipmentSlot.Unknown, true),
        (InventoryType.ArmoryMainHand, EquipmentSlot.MainHand, false),
        (InventoryType.ArmoryOffHand, EquipmentSlot.OffHand, false),
        (InventoryType.ArmoryHead, EquipmentSlot.Head, false),
        (InventoryType.ArmoryBody, EquipmentSlot.Body, false),
        (InventoryType.ArmoryHands, EquipmentSlot.Hands, false),
        (InventoryType.ArmoryLegs, EquipmentSlot.Legs, false),
        (InventoryType.ArmoryFeets, EquipmentSlot.Feet, false),
        (InventoryType.ArmoryEar, EquipmentSlot.Ears, false),
        (InventoryType.ArmoryNeck, EquipmentSlot.Neck, false),
        (InventoryType.ArmoryWrist, EquipmentSlot.Wrists, false),
        (InventoryType.ArmoryRings, EquipmentSlot.Ring, false),
        (InventoryType.ArmorySoulCrystal, EquipmentSlot.SoulCrystal, false),
        (InventoryType.Inventory1, EquipmentSlot.Unknown, false),
        (InventoryType.Inventory2, EquipmentSlot.Unknown, false),
        (InventoryType.Inventory3, EquipmentSlot.Unknown, false),
        (InventoryType.Inventory4, EquipmentSlot.Unknown, false),
        (InventoryType.SaddleBag1, EquipmentSlot.Unknown, false),
        (InventoryType.SaddleBag2, EquipmentSlot.Unknown, false),
        (InventoryType.PremiumSaddleBag1, EquipmentSlot.Unknown, false),
        (InventoryType.PremiumSaddleBag2, EquipmentSlot.Unknown, false),
    ];

    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    public DalamudCharacterEquipmentSnapshotSource(IPlayerState playerState, IDataManager dataManager, IPluginLog log)
    {
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.log = log;
    }

    public CharacterEquipmentSnapshot Capture()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var diagnostics = new List<SnapshotComponentDiagnostic>();
        var identity = CaptureIdentity(capturedAt, diagnostics);
        if (identity.Scope is null)
            return Empty(identity, diagnostics);

        var instances = CaptureInventory(identity.Scope, capturedAt, diagnostics);
        var jobs = CaptureJobs(instances, diagnostics);
        var gearsets = CaptureGearsets(diagnostics);
        var definitions = CaptureDefinitions(instances, diagnostics);
        return new CharacterEquipmentSnapshot(Guid.NewGuid(), identity, jobs, gearsets, instances, definitions, new(diagnostics));
    }

    private CharacterIdentitySnapshot CaptureIdentity(DateTimeOffset capturedAt, List<SnapshotComponentDiagnostic> diagnostics)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
        {
            diagnostics.Add(new("identity", SnapshotComponentStatus.Unavailable, "No active character is loaded."));
            return new(null, null, null, capturedAt, false, SnapshotComponentStatus.Unavailable, "No active character is loaded.");
        }

        var scope = new CharacterScope(playerState.ContentId, playerState.CharacterName.ToString(), playerState.HomeWorld.RowId);
        diagnostics.Add(new("identity", SnapshotComponentStatus.Complete));
        return new(scope, playerState.CurrentWorld.RowId, playerState.ClassJob.RowId, capturedAt, true, SnapshotComponentStatus.Complete);
    }

    private IReadOnlyList<CharacterJobSnapshot> CaptureJobs(
        IReadOnlyList<EquipmentInstanceSnapshot> instances,
        List<SnapshotComponentDiagnostic> diagnostics)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<ClassJob>();
            if (sheet is null)
                throw new InvalidOperationException("ClassJob sheet is unavailable.");
            var baseParams = dataManager.GetExcelSheet<BaseParam>() ?? throw new InvalidOperationException("BaseParam sheet is unavailable.");
            var ownedItemIds = instances.Select(instance => instance.Fingerprint.ItemId).ToHashSet();
            var jobs = sheet
                .Where(job => job.RowId > 0 && !string.IsNullOrWhiteSpace(job.Abbreviation.ToString()))
                .Select(job =>
                {
                    var level = playerState.GetClassJobLevel(job);
                    var soulCrystalId = job.ItemSoulCrystal.RowId;
                    uint? parentClassJobId = job.ClassJobParent.RowId == 0 ? null : job.ClassJobParent.RowId;
                    var isUnlocked = IsJobUnlocked(level, job.RowId, parentClassJobId, soulCrystalId, ownedItemIds);
                    var primaryStat = MapStatSemantic(job.PrimaryStat, baseParams.GetRowOrDefault(job.PrimaryStat)?.Name.ToString());
                    return new CharacterJobSnapshot(
                        job.RowId,
                        job.Abbreviation.ToString(),
                        job.Name.ToString(),
                        checked((uint)Math.Max(0, (int)level)),
                        isUnlocked,
                        parentClassJobId,
                        FormatRole(job.Role, primaryStat),
                        primaryStat,
                        MapDiscipline(job.Abbreviation.ToString()));
                })
                .ToArray();
            diagnostics.Add(new("jobs", SnapshotComponentStatus.Complete));
            return jobs;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Squire] Failed to capture job levels");
            diagnostics.Add(new("jobs", SnapshotComponentStatus.Unavailable, ex.Message));
            return [];
        }
    }

    internal static bool IsJobUnlocked(
        int level,
        uint classJobId,
        uint? parentClassJobId,
        uint soulCrystalId,
        IReadOnlySet<uint> ownedItemIds)
    {
        var isUpgradedJob = parentClassJobId is not null && parentClassJobId != classJobId;
        return isUpgradedJob
            ? soulCrystalId != 0 && ownedItemIds.Contains(soulCrystalId)
            : level > 0;
    }

    internal static unsafe IReadOnlyList<GearsetSnapshot> CaptureGearsets(List<SnapshotComponentDiagnostic> diagnostics)
    {
        try
        {
            var ui = UIModule.Instance();
            var module = ui == null ? null : ui->GetRaptureGearsetModule();
            if (module == null)
                throw new InvalidOperationException("RaptureGearsetModule is unavailable.");

            var values = new List<GearsetSnapshot>();
            for (var index = 0; index < 100; index++)
            {
                var entry = module->GetGearset(index);
                if (entry == null || !entry->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                    continue;
                var items = new List<GearsetItemReference>();
                for (var itemIndex = 0; itemIndex < entry->Items.Length; itemIndex++)
                {
                    var item = entry->Items[itemIndex];
                    if (item.ItemId != 0)
                    {
                        var materiaIds = new List<uint>(5);
                        var materiaGrades = new List<byte>(5);
                        for (var materiaIndex = 0; materiaIndex < item.Materia.Length; materiaIndex++)
                        {
                            if (item.Materia[materiaIndex] == 0)
                                continue;
                            materiaIds.Add(item.Materia[materiaIndex]);
                            materiaGrades.Add(item.MateriaGrades[materiaIndex]);
                        }
                        var gearsetIndex = (RaptureGearsetModule.GearsetItemIndex)itemIndex;
                        items.Add(new(
                            MapGearsetSlot(gearsetIndex),
                            NormalizeItemId(item.ItemId),
                            item.ItemId >= 1_000_000,
                            MapGearsetPosition(gearsetIndex),
                            materiaIds,
                            materiaGrades,
                            item.GlamourId == 0 ? null : item.GlamourId,
                            [item.Stain0Id, item.Stain1Id],
                            item.Flags.HasFlag(RaptureGearsetModule.GearsetItemFlag.ItemMissing)));
                    }
                }
                values.Add(new(index, entry->NameString, entry->ClassJob, items, true));
            }
            diagnostics.Add(new("gearsets", SnapshotComponentStatus.Complete));
            return values;
        }
        catch (Exception ex)
        {
            diagnostics.Add(new("gearsets", SnapshotComponentStatus.Unavailable, ex.Message));
            return [];
        }
    }

    private static unsafe IReadOnlyList<EquipmentInstanceSnapshot> CaptureInventory(
        CharacterScope scope,
        DateTimeOffset capturedAt,
        List<SnapshotComponentDiagnostic> diagnostics)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            diagnostics.Add(new("inventory", SnapshotComponentStatus.Unavailable, "InventoryManager is unavailable."));
            return [];
        }

        var instances = new List<EquipmentInstanceSnapshot>();
        var statuses = new Dictionary<string, bool> { ["equipped"] = true, ["armoury"] = true, ["inventory"] = true };
        foreach (var (type, knownSlot, equipped) in Containers)
        {
            var container = manager->GetInventoryContainer(type);
            var component = equipped ? "equipped" : type.ToString().StartsWith("Armory", StringComparison.Ordinal) ? "armoury" : "inventory";
            if (container == null || !container->IsLoaded)
            {
                // Premium saddlebags are account-optional; their absence is normal, not partial coverage.
                if (type is not (InventoryType.PremiumSaddleBag1 or InventoryType.PremiumSaddleBag2))
                    statuses[component] = false;
                continue;
            }
            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var item = container->GetInventorySlot(slotIndex);
                if (item == null || item->ItemId == 0)
                    continue;
                var materia = new List<uint>(5);
                var materiaGrades = new List<byte>(5);
                for (byte materiaIndex = 0; materiaIndex < 5; materiaIndex++)
                {
                    var materiaId = item->GetMateriaId(materiaIndex);
                    if (materiaId == 0)
                        continue;
                    materia.Add(materiaId);
                    materiaGrades.Add(item->GetMateriaGrade(materiaIndex));
                }
                var slot = knownSlot;
                var fingerprint = new EquipmentInstanceFingerprint(
                    scope,
                    type.ToString(),
                    slotIndex,
                    NormalizeItemId(item->ItemId),
                    item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                    checked((uint)item->Quantity),
                    item->Condition,
                    item->SpiritbondOrCollectability,
                    item->CrafterContentId == 0 ? null : item->CrafterContentId,
                    materia,
                    item->GlamourId == 0 ? null : item->GlamourId,
                    [item->GetStain(0), item->GetStain(1)],
                    materiaGrades);
                instances.Add(new(fingerprint, capturedAt, equipped));
            }
        }
        foreach (var status in statuses)
            diagnostics.Add(new(status.Key, status.Value ? SnapshotComponentStatus.Complete : SnapshotComponentStatus.Partial, status.Value ? null : "One or more required containers were not loaded."));
        return instances;
    }

    private IReadOnlyDictionary<uint, EquipmentItemDefinition> CaptureDefinitions(
        IReadOnlyList<EquipmentInstanceSnapshot> instances,
        List<SnapshotComponentDiagnostic> diagnostics)
    {
        try
        {
            var itemSheet = dataManager.GetExcelSheet<Item>() ?? throw new InvalidOperationException("Item sheet unavailable.");
            var jobSheet = dataManager.GetExcelSheet<ClassJob>() ?? throw new InvalidOperationException("ClassJob sheet unavailable.");
            var cabinetSheet = dataManager.GetExcelSheet<Cabinet>() ?? throw new InvalidOperationException("Cabinet sheet unavailable.");
            var baseParamSheet = dataManager.GetExcelSheet<BaseParam>() ?? throw new InvalidOperationException("BaseParam sheet unavailable.");
            var jobs = jobSheet.Where(job => job.RowId > 0).ToArray();
            var cabinetItemIds = cabinetSheet.Select(entry => entry.Item.RowId).Where(id => id != 0).ToHashSet();
            var values = new Dictionary<uint, EquipmentItemDefinition>();
            foreach (var id in instances.Select(instance => instance.Fingerprint.ItemId).Distinct())
            {
                var item = itemSheet.GetRowOrDefault(id);
                if (item is null)
                    continue;
                var value = item.Value;
                var slot = MapEquipSlot(value.EquipSlotCategory.RowId);
                var slotCategory = value.EquipSlotCategory.Value;
                var category = value.ClassJobCategory.Value;
                var eligible = jobs.Where(job => IsEligible(category, job.Abbreviation.ToString())).Select(job => job.RowId).ToHashSet();
                var itemSpecialBonusId = ReadRowId(value, "ItemSpecialBonus");
                var itemSpecialBonusParam = ReadInt(value, "ItemSpecialBonusParam");
                var itemActionId = ReadRowId(value, "ItemAction");
                var equipRestrictionId = ReadRowId(value, "EquipRestriction");
                var grandCompanyId = ReadRowId(value, "GrandCompany");
                var requiredPvpRank = checked((uint)Math.Max(0, ReadInt(value, "RequiredPvpRank")));
                var classJobUseId = ReadRowId(value, "ClassJobUse");
                EquipmentStatProfile BuildProfile(bool highQuality)
                {
                    var bySemantic = new Dictionary<EquipmentStatSemantic, EquipmentStatValue>();
                    var normalSemantics = new HashSet<EquipmentStatSemantic>();
                    var profileComplete = true;
                    void Apply(uint baseParamId, short amount, bool special)
                    {
                        if (baseParamId == 0 || amount <= 0)
                            return;
                        var name = baseParamSheet.GetRowOrDefault(baseParamId)?.Name.ToString();
                        var semantic = MapStatSemantic(baseParamId, name);
                        if (!special)
                            normalSemantics.Add(semantic);
                        if (semantic == EquipmentStatSemantic.Unknown)
                            profileComplete = false;
                        if (bySemantic.TryGetValue(semantic, out var existing))
                        {
                            if (existing.BaseParamId != baseParamId)
                                profileComplete = false;
                            else if (special)
                            {
                                bySemantic[semantic] = existing with { Value = existing.Value + amount, IsSpecial = true };
                                return;
                            }
                        }
                        bySemantic[semantic] = new(baseParamId, semantic, amount, special, name);
                    }
                    for (var index = 0; index < value.BaseParam.Count; index++)
                        Apply(value.BaseParam[index].RowId, value.BaseParamValue[index], false);
                    if (highQuality)
                        for (var index = 0; index < value.BaseParamSpecial.Count; index++)
                            Apply(value.BaseParamSpecial[index].RowId, value.BaseParamValueSpecial[index], true);

                    var physicalDamage = ExtractScalar(EquipmentStatSemantic.PhysicalDamage, value.DamagePhys);
                    var magicalDamage = ExtractScalar(EquipmentStatSemantic.MagicalDamage, value.DamageMag);
                    var physicalDefense = ExtractScalar(EquipmentStatSemantic.PhysicalDefense, value.DefensePhys);
                    var magicalDefense = ExtractScalar(EquipmentStatSemantic.MagicalDefense, value.DefenseMag);
                    var blockStrength = ReadInt(value, "Block");
                    var blockRate = ReadInt(value, "BlockRate");
                    var delayMilliseconds = ReadInt(value, "Delayms");
                    if (delayMilliseconds == 0)
                        delayMilliseconds = ReadInt(value, "DelayMs");
                    int ExtractScalar(EquipmentStatSemantic semantic, int fallback)
                    {
                        if (!bySemantic.Remove(semantic, out var parameter))
                            return fallback;
                        return ResolveScalar(fallback, parameter.Value, highQuality, parameter.IsSpecial, normalSemantics.Contains(semantic));
                    }
                    return new(bySemantic.Values.ToArray(), physicalDamage, magicalDamage, physicalDefense, magicalDefense, profileComplete,
                        blockStrength, blockRate, delayMilliseconds);
                }
                var normalProfile = BuildProfile(false);
                var highQualityProfile = BuildProfile(true);
                values[id] = new(
                    id,
                    value.Name.ToString(),
                    value.LevelEquip,
                    value.LevelItem.RowId,
                    slot,
                    eligible,
                    value.Rarity,
                    slot != EquipmentSlot.Unknown,
                    slot == EquipmentSlot.SoulCrystal,
                    value.Desynth > 0,
                    value.PriceLow > 0 && !value.IsIndisposable,
                    value.PriceLow,
                    !value.IsIndisposable,
                    cabinetItemIds.Contains(id),
                    false,
                    value.IsUnique && value.IsUntradable && value.Rarity >= 4,
                    normalProfile,
                    MapRarity(value.Rarity),
                    MapExpertDeliveryEligibility(value.Rarity),
                    value.Rarity >= 2
                        ? "FFXIV equipment rarity rule: green and higher equipment is accepted by Grand Company Expert Delivery."
                        : "FFXIV equipment rarity rule: normal equipment is not an Expert Delivery item.",
                    highQualityProfile,
                    value.IsUnique,
                    value.EquipSlotCategory.RowId,
                    slotCategory.MainHand,
                    slotCategory.OffHand,
                    slotCategory.FingerL != 0,
                    slotCategory.FingerR != 0,
                    value.ClassJobCategory.RowId == 1,
                    value.ClassJobCategory.RowId,
                    category.Name.ToString(),
                    value.ItemUICategory.RowId,
                    value.ItemUICategory.Value.Name.ToString(),
                    value.ItemSearchCategory.RowId,
                    value.ItemSearchCategory.Value.Name.ToString(),
                    normalProfile.Parameters.Any(parameter => parameter.Semantic == EquipmentStatSemantic.Unknown && parameter.Value > 0) ||
                    itemSpecialBonusId > 1 || itemActionId != 0 || id is 8567 or 8568 or 14043,
                    itemSpecialBonusId,
                    itemSpecialBonusParam,
                    itemActionId,
                    equipRestrictionId,
                    grandCompanyId,
                    requiredPvpRank,
                    classJobUseId);
            }
            var complete = values.Count == instances.Select(instance => instance.Fingerprint.ItemId).Distinct().Count();
            diagnostics.Add(new("definitions", complete ? SnapshotComponentStatus.Complete : SnapshotComponentStatus.Partial, complete ? null : "One or more item definitions were unavailable."));
            return values;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Squire] Failed to resolve item definitions");
            diagnostics.Add(new("definitions", SnapshotComponentStatus.Unavailable, ex.Message));
            return new Dictionary<uint, EquipmentItemDefinition>();
        }
    }

    internal static bool IsEligible(ClassJobCategory category, string abbreviation)
    {
        var property = typeof(ClassJobCategory).GetProperty(abbreviation);
        return property?.PropertyType == typeof(bool) && property.GetValue(category) is true;
    }

    private static uint ReadRowId(object source, string propertyName)
    {
        var value = source.GetType().GetProperty(propertyName)?.GetValue(source);
        if (value is null) return 0;
        if (value is IConvertible convertible && value.GetType().IsPrimitive)
            return Convert.ToUInt32(convertible);
        var rowId = value.GetType().GetProperty("RowId")?.GetValue(value);
        return rowId is null ? 0 : Convert.ToUInt32(rowId);
    }

    private static int ReadInt(object source, string propertyName)
    {
        var value = source.GetType().GetProperty(propertyName)?.GetValue(source);
        return value is null ? 0 : Convert.ToInt32(value);
    }

    internal static EquipmentRarity MapRarity(byte rarity) => rarity switch
    {
        1 => EquipmentRarity.Normal,
        2 => EquipmentRarity.Uncommon,
        3 => EquipmentRarity.Rare,
        4 => EquipmentRarity.Relic,
        _ => EquipmentRarity.Unknown,
    };

    internal static ExpertDeliveryEligibility MapExpertDeliveryEligibility(byte rarity) => rarity switch
    {
        0 or 1 => ExpertDeliveryEligibility.Ineligible,
        >= 2 and <= 4 => ExpertDeliveryEligibility.Eligible,
        _ => ExpertDeliveryEligibility.Unknown,
    };

    internal static int ResolveScalar(int baseValue, int parameterValue, bool highQuality, bool parameterIsSpecial, bool normalProfileContainsSemantic) =>
        highQuality && parameterIsSpecial && !normalProfileContainsSemantic
            ? baseValue + parameterValue
            : parameterValue;

    internal static EquipmentDiscipline MapDiscipline(string abbreviation) => abbreviation switch
    {
        "CRP" or "BSM" or "ARM" or "GSM" or "LTW" or "WVR" or "ALC" or "CUL" => EquipmentDiscipline.Crafter,
        "MIN" or "BTN" or "FSH" => EquipmentDiscipline.Gatherer,
        "ADV" => EquipmentDiscipline.Unknown,
        _ => EquipmentDiscipline.Combat,
    };

    internal static string FormatRole(byte role, EquipmentStatSemantic primaryStat = EquipmentStatSemantic.Unknown) => role switch
    {
        1 => "Tank",
        2 => "Melee DPS",
        3 when primaryStat == EquipmentStatSemantic.Intelligence => "Magical Ranged DPS",
        3 when primaryStat == EquipmentStatSemantic.Dexterity => "Physical Ranged DPS",
        3 => "Ranged DPS",
        4 => "Healer",
        5 => "Magical Ranged DPS",
        _ => $"Role {role}",
    };

    internal static EquipmentStatSemantic MapStatSemantic(uint baseParamId, string? _) => baseParamId switch
    {
        1 => EquipmentStatSemantic.Strength,
        2 => EquipmentStatSemantic.Dexterity,
        3 => EquipmentStatSemantic.Vitality,
        4 => EquipmentStatSemantic.Intelligence,
        5 => EquipmentStatSemantic.Mind,
        6 => EquipmentStatSemantic.Piety,
        10 => EquipmentStatSemantic.GatheringPoints,
        11 => EquipmentStatSemantic.CraftingPoints,
        12 => EquipmentStatSemantic.PhysicalDamage,
        13 => EquipmentStatSemantic.MagicalDamage,
        19 => EquipmentStatSemantic.Tenacity,
        21 => EquipmentStatSemantic.PhysicalDefense,
        22 => EquipmentStatSemantic.DirectHit,
        24 => EquipmentStatSemantic.MagicalDefense,
        27 => EquipmentStatSemantic.CriticalHit,
        30 => EquipmentStatSemantic.PiercingResistance,
        44 => EquipmentStatSemantic.Determination,
        45 => EquipmentStatSemantic.SkillSpeed,
        46 => EquipmentStatSemantic.SpellSpeed,
        70 => EquipmentStatSemantic.Craftsmanship,
        71 => EquipmentStatSemantic.Control,
        72 => EquipmentStatSemantic.Gathering,
        73 => EquipmentStatSemantic.Perception,
        _ => EquipmentStatSemantic.Unknown,
    };

    private static uint NormalizeItemId(uint itemId) => itemId >= 1_000_000 ? itemId % 1_000_000 : itemId;

    internal static EquipmentSlot MapEquipSlot(uint rowId) => rowId switch
    {
        1 or 13 or 14 => EquipmentSlot.MainHand,
        2 => EquipmentSlot.OffHand,
        3 => EquipmentSlot.Head,
        4 => EquipmentSlot.Body,
        5 => EquipmentSlot.Hands,
        7 => EquipmentSlot.Legs,
        8 => EquipmentSlot.Feet,
        9 => EquipmentSlot.Ears,
        10 => EquipmentSlot.Neck,
        11 => EquipmentSlot.Wrists,
        12 => EquipmentSlot.Ring,
        17 => EquipmentSlot.SoulCrystal,
        _ => EquipmentSlot.Unknown,
    };

    private static EquipmentSlot MapGearsetSlot(RaptureGearsetModule.GearsetItemIndex slot) => slot switch
    {
        RaptureGearsetModule.GearsetItemIndex.MainHand => EquipmentSlot.MainHand,
        RaptureGearsetModule.GearsetItemIndex.OffHand => EquipmentSlot.OffHand,
        RaptureGearsetModule.GearsetItemIndex.Head => EquipmentSlot.Head,
        RaptureGearsetModule.GearsetItemIndex.Body => EquipmentSlot.Body,
        RaptureGearsetModule.GearsetItemIndex.Hands => EquipmentSlot.Hands,
        RaptureGearsetModule.GearsetItemIndex.Legs => EquipmentSlot.Legs,
        RaptureGearsetModule.GearsetItemIndex.Feet => EquipmentSlot.Feet,
        RaptureGearsetModule.GearsetItemIndex.Ears => EquipmentSlot.Ears,
        RaptureGearsetModule.GearsetItemIndex.Neck => EquipmentSlot.Neck,
        RaptureGearsetModule.GearsetItemIndex.Wrists => EquipmentSlot.Wrists,
        RaptureGearsetModule.GearsetItemIndex.RingLeft or RaptureGearsetModule.GearsetItemIndex.RingRight => EquipmentSlot.Ring,
        RaptureGearsetModule.GearsetItemIndex.SoulStone => EquipmentSlot.SoulCrystal,
        _ => EquipmentSlot.Unknown,
    };

    private static EquipmentLoadoutPosition? MapGearsetPosition(RaptureGearsetModule.GearsetItemIndex slot) => slot switch
    {
        RaptureGearsetModule.GearsetItemIndex.MainHand => EquipmentLoadoutPosition.MainHand,
        RaptureGearsetModule.GearsetItemIndex.OffHand => EquipmentLoadoutPosition.OffHand,
        RaptureGearsetModule.GearsetItemIndex.Head => EquipmentLoadoutPosition.Head,
        RaptureGearsetModule.GearsetItemIndex.Body => EquipmentLoadoutPosition.Body,
        RaptureGearsetModule.GearsetItemIndex.Hands => EquipmentLoadoutPosition.Hands,
        RaptureGearsetModule.GearsetItemIndex.Legs => EquipmentLoadoutPosition.Legs,
        RaptureGearsetModule.GearsetItemIndex.Feet => EquipmentLoadoutPosition.Feet,
        RaptureGearsetModule.GearsetItemIndex.Ears => EquipmentLoadoutPosition.Ears,
        RaptureGearsetModule.GearsetItemIndex.Neck => EquipmentLoadoutPosition.Neck,
        RaptureGearsetModule.GearsetItemIndex.Wrists => EquipmentLoadoutPosition.Wrists,
        RaptureGearsetModule.GearsetItemIndex.RingLeft => EquipmentLoadoutPosition.LeftRing,
        RaptureGearsetModule.GearsetItemIndex.RingRight => EquipmentLoadoutPosition.RightRing,
        _ => null,
    };

    private static CharacterEquipmentSnapshot Empty(CharacterIdentitySnapshot identity, List<SnapshotComponentDiagnostic> diagnostics)
    {
        foreach (var component in new[] { "jobs", "gearsets", "equipped", "armoury", "inventory", "definitions" })
            diagnostics.Add(new(component, SnapshotComponentStatus.Unavailable, "Character identity is unavailable."));
        return new(Guid.NewGuid(), identity, [], [], [], new Dictionary<uint, EquipmentItemDefinition>(), new(diagnostics));
    }
}
