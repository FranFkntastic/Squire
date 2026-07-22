using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Equipment;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace MarketMafioso.Squire.Observation;

/// <summary>
/// Version-matched static-data enrichment for name-first rendered equipment observations.
/// This catalog does not inspect character, inventory, agent, or gearset state.
/// </summary>
public sealed class LuminaRenderedEquipmentDefinitionLookup
{
    private readonly IDataManager dataManager;
    private readonly Dictionary<uint, EquipmentItemDefinition?> byItemId = [];

    public LuminaRenderedEquipmentDefinitionLookup(IDataManager dataManager)
    {
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
    }

    public IReadOnlyList<EquipmentItemDefinition> FindByExactName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return [];
        var items = dataManager.GetExcelSheet<Item>() ?? throw new InvalidOperationException("Item sheet is unavailable.");
        var baseParams = dataManager.GetExcelSheet<BaseParam>() ?? throw new InvalidOperationException("BaseParam sheet is unavailable.");
        var jobs = dataManager.GetExcelSheet<ClassJob>() ?? throw new InvalidOperationException("ClassJob sheet is unavailable.");
        return items
            .Where(value => value.RowId > 0 && string.Equals(value.Name.ToString(), name, StringComparison.Ordinal))
            .Select(value => Build(value, baseParams, jobs))
            .Where(value => value is not null)
            .Cast<EquipmentItemDefinition>()
            .ToArray();
    }

    public IReadOnlyList<EquipmentItemDefinition> FindByItemId(uint itemId)
    {
        if (itemId == 0)
            return [];
        if (byItemId.TryGetValue(itemId, out var cached))
            return cached is null ? [] : [cached];
        var items = dataManager.GetExcelSheet<Item>() ?? throw new InvalidOperationException("Item sheet is unavailable.");
        var value = items.GetRowOrDefault(itemId);
        if (value is null)
        {
            byItemId[itemId] = null;
            return [];
        }
        var baseParams = dataManager.GetExcelSheet<BaseParam>() ?? throw new InvalidOperationException("BaseParam sheet is unavailable.");
        var jobs = dataManager.GetExcelSheet<ClassJob>() ?? throw new InvalidOperationException("ClassJob sheet is unavailable.");
        var definition = Build(value.Value, baseParams, jobs);
        byItemId[itemId] = definition;
        return definition is null ? [] : [definition];
    }

    private static EquipmentItemDefinition? Build(
        Item value,
        ExcelSheet<BaseParam> baseParams,
        ExcelSheet<ClassJob> jobs)
    {
        var slot = DalamudCharacterEquipmentSnapshotSource.MapEquipSlot(value.EquipSlotCategory.RowId);
        if (slot is EquipmentSlot.Unknown or EquipmentSlot.SoulCrystal)
            return null;
        var eligible = jobs
            .Where(job => job.RowId > 0 && DalamudCharacterEquipmentSnapshotSource.IsEligible(value.ClassJobCategory.Value, job.Abbreviation.ToString()))
            .Select(job => job.RowId)
            .ToHashSet();
        var normal = BuildProfile(value, baseParams, highQuality: false);
        var high = value.CanBeHq ? BuildProfile(value, baseParams, highQuality: true) : null;
        var slotCategory = value.EquipSlotCategory.Value;
        return new(
            value.RowId,
            value.Name.ToString(),
            value.LevelEquip,
            value.LevelItem.RowId,
            slot,
            eligible,
            value.Rarity,
            true,
            false,
            value.Desynth > 0,
            value.PriceLow > 0 && !value.IsIndisposable,
            value.PriceLow,
            !value.IsIndisposable,
            null,
            null,
            false,
            StatProfile: normal,
            NormalizedRarity: DalamudCharacterEquipmentSnapshotSource.MapRarity(value.Rarity),
            HighQualityStatProfile: high,
            IsUnique: value.IsUnique,
            EquipSlotCategoryId: value.EquipSlotCategory.RowId,
            MainHandOccupancy: slotCategory.MainHand,
            OffHandOccupancy: slotCategory.OffHand,
            FitsLeftRing: slotCategory.FingerL != 0,
            FitsRightRing: slotCategory.FingerR != 0,
            IsAllClasses: value.ClassJobCategory.RowId == 1,
            ClassJobCategoryId: value.ClassJobCategory.RowId,
            ClassJobCategoryName: value.ClassJobCategory.Value.Name.ToString(),
            ItemUiCategoryId: value.ItemUICategory.RowId,
            ItemUiCategoryName: value.ItemUICategory.Value.Name.ToString(),
            ItemSearchCategoryId: value.ItemSearchCategory.RowId,
            ItemSearchCategoryName: value.ItemSearchCategory.Value.Name.ToString(),
            ItemSpecialBonusId: ReadRowId(value, "ItemSpecialBonus"),
            ItemSpecialBonusParam: ReadInt(value, "ItemSpecialBonusParam"),
            ItemActionId: ReadRowId(value, "ItemAction"),
            EquipRestrictionId: ReadRowId(value, "EquipRestriction"),
            GrandCompanyId: ReadRowId(value, "GrandCompany"),
            RequiredPvpRank: checked((uint)Math.Max(0, ReadInt(value, "RequiredPvpRank"))),
            ClassJobUseId: ReadRowId(value, "ClassJobUse"));
    }

    private static EquipmentStatProfile BuildProfile(Item item, ExcelSheet<BaseParam> baseParams, bool highQuality)
    {
        var values = new Dictionary<EquipmentStatSemantic, EquipmentStatValue>();
        var complete = true;
        void Add(uint baseParamId, int amount, bool special)
        {
            if (baseParamId == 0 || amount <= 0)
                return;
            var sourceName = baseParams.GetRowOrDefault(baseParamId)?.Name.ToString();
            var semantic = DalamudCharacterEquipmentSnapshotSource.MapStatSemantic(baseParamId, sourceName);
            if (semantic == EquipmentStatSemantic.Unknown)
                complete = false;
            if (values.TryGetValue(semantic, out var existing))
                values[semantic] = existing with { Value = checked(existing.Value + amount), IsSpecial = existing.IsSpecial || special };
            else
                values[semantic] = new(baseParamId, semantic, amount, special, sourceName);
        }

        for (var index = 0; index < item.BaseParam.Count; index++)
            Add(item.BaseParam[index].RowId, item.BaseParamValue[index], false);
        if (highQuality)
            for (var index = 0; index < item.BaseParamSpecial.Count; index++)
                Add(item.BaseParamSpecial[index].RowId, item.BaseParamValueSpecial[index], true);
        return new(
            values.Values.ToArray(),
            item.DamagePhys,
            item.DamageMag,
            item.DefensePhys,
            item.DefenseMag,
            complete);
    }

    private static uint ReadRowId(object source, string propertyName)
    {
        var value = source.GetType().GetProperty(propertyName)?.GetValue(source);
        if (value is null)
            return 0;
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
}
