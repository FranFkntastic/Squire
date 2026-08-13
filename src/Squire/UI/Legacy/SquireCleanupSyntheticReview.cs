#if DEBUG
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;

namespace MarketMafioso.Windows.Squire;

internal enum SquireCleanupSyntheticScenarioKind
{
    CompleteZero,
    Populated,
    FilteredZero,
    InvalidLastValid,
    HiddenSelected,
}

internal static class SquireCleanupReviewedControlIds
{
    public const string SyntheticReview = "squire.cleanup.synthetic-review";
    public const string ScenarioPrefix = "squire.cleanup.synthetic-scenario.";

    public static IReadOnlyList<string> ScenarioIds { get; } =
    [
        ForScenario(SquireCleanupSyntheticScenarioKind.CompleteZero),
        ForScenario(SquireCleanupSyntheticScenarioKind.Populated),
        ForScenario(SquireCleanupSyntheticScenarioKind.FilteredZero),
        ForScenario(SquireCleanupSyntheticScenarioKind.InvalidLastValid),
        ForScenario(SquireCleanupSyntheticScenarioKind.HiddenSelected),
    ];

    public static string ForScenario(SquireCleanupSyntheticScenarioKind scenario) => ScenarioPrefix + (scenario switch
    {
        SquireCleanupSyntheticScenarioKind.Populated => "populated",
        SquireCleanupSyntheticScenarioKind.FilteredZero => "filtered-zero",
        SquireCleanupSyntheticScenarioKind.InvalidLastValid => "invalid-last-valid",
        SquireCleanupSyntheticScenarioKind.HiddenSelected => "hidden-selected",
        _ => "complete-zero",
    });
}

internal sealed class SquireCleanupSyntheticReview
{
    private static readonly CharacterScope Character = new(0xC0D3, "Review Character", 21);
    private static readonly DateTimeOffset CapturedAtUtc = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);

    private SquireCleanupSyntheticReview(SquireCleanupSyntheticScenarioKind scenario, SquireAnalysis analysis)
    {
        Scenario = scenario;
        Analysis = analysis;
        Workbench.Review.Adopt(analysis);

        switch (scenario)
        {
            case SquireCleanupSyntheticScenarioKind.FilteredZero:
                Workbench.Search = "name:does-not-exist";
                Workbench.Filter.SetExpression(Workbench.Search);
                break;
            case SquireCleanupSyntheticScenarioKind.InvalidLastValid:
                Workbench.Filter.SetExpression("quality:hq");
                Workbench.Search = "quality:";
                Workbench.Filter.SetExpression(Workbench.Search);
                break;
            case SquireCleanupSyntheticScenarioKind.HiddenSelected:
                var hidden = analysis.Candidates.First(candidate => candidate.Definition.Name.Contains("Barbut", StringComparison.Ordinal));
                Workbench.Review.TrySelect(analysis, hidden.Instance.Fingerprint, hidden.RecommendedDisposition);
                Workbench.TableSelection.SetSelected(hidden.Instance.Fingerprint, true);
                Workbench.Search = "name:gauntlets";
                Workbench.Filter.SetExpression(Workbench.Search);
                break;
        }
    }

    public SquireCleanupSyntheticScenarioKind Scenario { get; }
    public SquireAnalysis Analysis { get; }
    public SquireCleanupWorkbenchState Workbench { get; } = new(showProtected: true);
    public static SquireCleanupSyntheticReview Create(SquireCleanupSyntheticScenarioKind scenario) =>
        new(scenario, BuildAnalysis(scenario == SquireCleanupSyntheticScenarioKind.CompleteZero));

    public SquireCandidate[] ResolveVisibleCandidates()
    {
        var rows = Workbench.ShowBatchOnly
            ? Analysis.Candidates.Where(candidate => Workbench.Review.Selections.ContainsKey(candidate.Instance.Fingerprint)).ToArray()
            : Workbench.Filter.Apply(
                Analysis.Candidates
                    .Where(candidate => Workbench.ShowNonEquipment || candidate.Definition.IsEquipment)
                    .Where(candidate => Workbench.ShowProtected || candidate.Assessment is not (SquireAssessment.Protected or SquireAssessment.EvaluationFailure)),
                Workbench.Search);
        return Workbench.ShowBatchOnly
            ? rows
            : SquireCandidateTableProjection.Filter(rows, Workbench.ColumnFilters, FormatRowState);
    }

    private string FormatRowState(SquireCandidate candidate)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        if (Workbench.Review.Selections.ContainsKey(fingerprint))
            return "Cleanup batch";
        if (Workbench.TableSelection.IsSelected(fingerprint))
            return candidate.IsExecutable ? "Inspected" : "Inspection only";
        return Workbench.FocusedItem is { } focused && EquipmentInstanceFingerprintComparer.Instance.Equals(focused, fingerprint)
            ? "Focused"
            : "—";
    }

    private static SquireAnalysis BuildAnalysis(bool empty)
    {
        SquireCandidate[] candidates = empty
            ? []
            : [
                Candidate(71001, "Darksteel Barbut of Aiming", "ArmoryHead", 1, true, SquireAssessment.Candidate, SquireDisposition.Desynthesize),
                Candidate(71002, "Darksteel Gauntlets of Aiming", "Inventory1", 2, false, SquireAssessment.Candidate, SquireDisposition.VendorSell),
                Candidate(71003, "Signed Artisan's Goggles", "ArmoryHead", 3, false, SquireAssessment.Protected, SquireDisposition.Keep),
            ];
        var definitions = candidates.ToDictionary(candidate => candidate.Definition.ItemId, candidate => candidate.Definition);
        var snapshot = new CharacterEquipmentSnapshot(
            Guid.Parse(empty ? "30dd6ad6-f630-4c2e-9cd4-cf81b63ef600" : "ee99307e-5aba-4935-8ff1-3d2a6758f601"),
            new(Character, Character.HomeWorldId, 31, CapturedAtUtc, true, SnapshotComponentStatus.Complete),
            [new CharacterJobSnapshot(31, "MCH", "Machinist", 100, true, null, "Physical Ranged DPS", EquipmentStatSemantic.Dexterity, EquipmentDiscipline.Combat)],
            [],
            candidates.Select(candidate => candidate.Instance).ToArray(),
            definitions,
            new([
                new("identity", SnapshotComponentStatus.Complete),
                new("jobs", SnapshotComponentStatus.Complete),
                new("gearsets", SnapshotComponentStatus.Complete),
                new("equipped", SnapshotComponentStatus.Complete),
                new("armoury", SnapshotComponentStatus.Complete),
                new("inventory", SnapshotComponentStatus.Complete),
                new("definitions", SnapshotComponentStatus.Complete),
            ]));
        return new(snapshot, candidates, new SquireProtectionPolicy());
    }

    private static SquireCandidate Candidate(
        uint itemId,
        string name,
        string container,
        int slot,
        bool highQuality,
        SquireAssessment assessment,
        SquireDisposition disposition)
    {
        var fingerprint = new EquipmentInstanceFingerprint(
            Character,
            container,
            slot,
            itemId,
            highQuality,
            1,
            28000,
            0,
            name.StartsWith("Signed", StringComparison.Ordinal) ? 0xCAFEUL : null,
            highQuality ? [18u, 19u] : [],
            null,
            []);
        var definition = new EquipmentItemDefinition(
            itemId,
            name,
            50,
            itemId == 71002 ? 80u : 90u,
            name.Contains("Gauntlets", StringComparison.Ordinal) ? EquipmentSlot.Hands : EquipmentSlot.Head,
            new HashSet<uint> { 31 },
            3,
            true,
            false,
            true,
            true,
            125,
            true,
            false,
            true,
            false,
            NormalizedRarity: EquipmentRarity.Rare,
            ClassJobCategoryName: "Physical Ranged DPS");
        var executable = assessment == SquireAssessment.Candidate;
        return new(
            new(fingerprint, CapturedAtUtc, false),
            definition,
            assessment,
            disposition,
            executable ? new HashSet<SquireDisposition> { disposition } : new HashSet<SquireDisposition>(),
            [new(executable ? "StrictlyWorseForAllUnlockedJobs" : "PlayerSigned", executable
                ? "A trusted equipped baseline dominates this item."
                : "Player-signed equipment remains protected.", executable ? SquireReasonSeverity.Information : SquireReasonSeverity.Blocking)],
            null,
            new(1, 0, 0));
    }
}
#endif
