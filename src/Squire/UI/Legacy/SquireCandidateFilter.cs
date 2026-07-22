using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using Franthropy.Filtering.Compilation;
using Franthropy.Filtering.Diagnostics;
using Franthropy.Filtering.Documentation;
using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Semantics;
using MarketMafioso.Squire;

namespace MarketMafioso.Windows.Squire;

internal sealed class SquireCandidateFilter
{
    private static readonly FilterField<string> Name = FilterFields.Text(
        "item.name", "Item", "Item name.", ["name"]);
    private static readonly FilterField<string> Location = FilterFields.Text(
        "instance.location", "Location", "Inventory or Armoury Chest location.", ["location"]);
    private static readonly FilterField<SquireItemQuality> Quality = FilterFields.Enumeration<SquireItemQuality>(
        "instance.quality", "Quality", "Normal or high quality.", ["quality"],
        new Dictionary<string, SquireItemQuality>(StringComparer.OrdinalIgnoreCase)
        {
            ["hq"] = SquireItemQuality.HighQuality,
            ["nq"] = SquireItemQuality.NormalQuality,
        });
    private static readonly FilterField<bool> Equipped = FilterFields.Boolean(
        "instance.equipped", "Equipped", "Whether the item is currently equipped.", ["equipped"]);
    private static readonly FilterField<long> EquipLevel = FilterFields.Integer(
        "item.equipLevel", "Equip level", "Required character level.", ["equiplevel"], minimum: 0);
    private static readonly FilterField<long> ItemLevel = FilterFields.Integer(
        "item.itemLevel", "Item level", "Item level.", ["itemlevel", "ilvl"], minimum: 0);
    private static readonly FilterField<decimal> Condition = FilterFields.Decimal(
        "instance.condition", "Condition", "Condition percentage.", ["condition"], minimum: 0, maximum: 100);
    private static readonly FilterField<long> Copies = FilterFields.Integer(
        "ownership.quantity", "Copies", "Owned copies of this item.", ["copies", "quantity"], minimum: 0);
    private static readonly FilterField<long> Materia = FilterFields.Integer(
        "instance.materia", "Materia", "Attached materia count.", ["materia"], minimum: 0);
    private static readonly FilterField<EquipmentRarity> Rarity = FilterFields.Enumeration<EquipmentRarity>(
        "item.rarity", "Rarity", "Item rarity.", ["rarity"],
        new Dictionary<string, EquipmentRarity>(StringComparer.OrdinalIgnoreCase)
        {
            ["white"] = EquipmentRarity.Normal,
            ["green"] = EquipmentRarity.Uncommon,
            ["blue"] = EquipmentRarity.Rare,
            ["purple"] = EquipmentRarity.Relic,
        });
    private static readonly FilterField<SquireAssessment> Assessment = FilterFields.Enumeration<SquireAssessment>(
        "squire.assessment", "Assessment", "Squire's safety assessment.", ["assessment"]);
    private static readonly FilterField<SquireDisposition> Disposition = FilterFields.Enumeration<SquireDisposition>(
        "squire.disposition", "Disposition", "Recommended cleanup route.", ["disposition", "route"],
        new Dictionary<string, SquireDisposition>(StringComparer.OrdinalIgnoreCase)
        {
            ["expert"] = SquireDisposition.ExpertDelivery,
            ["desynth"] = SquireDisposition.Desynthesize,
            ["vendor"] = SquireDisposition.VendorSell,
        });
    private static readonly FilterField<string> Reason = FilterFields.Text(
        "squire.reason", "Reason", "Squire's decision evidence.", ["reason"]);

    private static readonly FilterCatalog Catalog = new(
        [Name, Location, Quality, Equipped, EquipLevel, ItemLevel, Condition, Copies, Materia, Rarity, Assessment, Disposition, Reason],
        "squire-candidates-1",
        [
            new FilterPredicateAlias("is", "hq", Quality.Key, "hq", "High-quality items."),
            new FilterPredicateAlias("is", "nq", Quality.Key, "nq", "Normal-quality items."),
            new FilterPredicateAlias("is", "equipped", Equipped.Key, "true", "Currently equipped items."),
        ]);

    public static FilterContext<SquireCandidate> Context { get; } = new FilterContextBuilder<SquireCandidate>(Catalog)
        .Bind(Name, candidate => Evidence.Known(candidate.Definition.Name))
        .Bind(Location, candidate => Evidence.Known(FormatSearchableLocation(candidate.Instance.Fingerprint)))
        .Bind(Quality, candidate => Evidence.Known(candidate.Instance.Fingerprint.IsHighQuality
            ? SquireItemQuality.HighQuality
            : SquireItemQuality.NormalQuality))
        .Bind(Equipped, candidate => Evidence.Known(candidate.Instance.IsEquipped))
        .Bind(EquipLevel, candidate => Evidence.Known((long)candidate.Definition.EquipLevel))
        .Bind(ItemLevel, candidate => Evidence.Known((long)candidate.Definition.ItemLevel))
        .Bind(Condition, candidate => Evidence.Known(candidate.Instance.Fingerprint.Condition / 300m))
        .Bind(Copies, candidate => Evidence.Known((long)(candidate.DuplicateStatus?.OwnedCopies ?? 1)))
        .Bind(Materia, candidate => Evidence.Known((long)candidate.Instance.Fingerprint.MateriaIds.Count))
        .Bind(Rarity, candidate => Evidence.Known(candidate.Definition.NormalizedRarity))
        .Bind(Assessment, candidate => Evidence.Known(candidate.Assessment))
        .Bind(Disposition, candidate => Evidence.Known(candidate.RecommendedDisposition))
        .Bind(Reason, candidate => Evidence.Known(string.Join(' ', candidate.Reasons.Select(reason => reason.Message))))
        .UseDefaultText(Name)
        .Build("squire-candidates", "1");

    public static FilterReferenceModel Reference { get; } = FilterReferenceGenerator.Create(Context);

    private FilterCompilation<SquireCandidate> lastValid = FilterCompiler.Compile(string.Empty, Context);
    private FilterCompilation<SquireCandidate>? current;
    private string currentExpression = string.Empty;

    public string? Error => current?.Diagnostics
        .FirstOrDefault(diagnostic => diagnostic.Severity == FilterDiagnosticSeverity.Error)?.Message;

    public SquireCandidate[] Apply(IEnumerable<SquireCandidate> rows, string? expression)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var value = expression ?? string.Empty;
        if (current is null || !string.Equals(value, currentExpression, StringComparison.Ordinal))
        {
            currentExpression = value;
            current = FilterCompiler.Compile(value, Context);
            if (current.IsValid)
                lastValid = current;
        }

        return rows.Where(lastValid.Matches).ToArray();
    }

    private static string FormatSearchableLocation(EquipmentInstanceFingerprint fingerprint)
    {
        var location = SquirePresentation.FormatLocation(fingerprint);
        return $"{location} {fingerprint.Container} {location.Replace("Armory", "Armoury", StringComparison.OrdinalIgnoreCase)}";
    }

    private enum SquireItemQuality
    {
        NormalQuality,
        HighQuality,
    }
}
