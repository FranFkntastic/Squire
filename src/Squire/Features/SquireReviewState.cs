using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed class SquireReviewState
{
    private Guid? generationId;
    private readonly Dictionary<EquipmentInstanceFingerprint, SquireDisposition> selections = new(EquipmentInstanceFingerprintComparer.Instance);

    public Guid? GenerationId => generationId;
    public IReadOnlyDictionary<EquipmentInstanceFingerprint, SquireDisposition> Selections => selections;

    public void Adopt(SquireAnalysis analysis)
    {
        generationId = analysis.Snapshot.GenerationId;
        selections.Clear();
    }

    public SquireSelectionReconciliation Reconcile(SquireAnalysis analysis)
    {
        var previous = selections.ToArray();
        generationId = analysis.Snapshot.GenerationId;
        selections.Clear();
        var removed = new List<string>();
        foreach (var (fingerprint, disposition) in previous)
        {
            var candidate = analysis.Candidates.FirstOrDefault(value =>
                EquipmentInstanceFingerprintComparer.Instance.Equals(value.Instance.Fingerprint, fingerprint));
            if (candidate is null)
            {
                removed.Add($"{fingerprint.Container}, slot {fingerprint.SlotIndex}: item is no longer present.");
                continue;
            }
            if (!candidate.IsExecutable)
            {
                removed.Add($"{candidate.Definition.Name}: item is no longer executable ({candidate.Assessment}).");
                continue;
            }
            if (candidate.RecommendedDisposition != disposition || !candidate.SupportedDispositions.Contains(disposition))
            {
                removed.Add($"{candidate.Definition.Name}: cleanup route changed from {disposition} to {candidate.RecommendedDisposition}.");
                continue;
            }
            selections[fingerprint] = disposition;
        }
        return new SquireSelectionReconciliation(selections.Count, removed);
    }

    public bool TrySelect(SquireAnalysis analysis, EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition)
    {
        if (generationId != analysis.Snapshot.GenerationId)
            return false;
        var candidate = analysis.Candidates.FirstOrDefault(value => EquipmentInstanceFingerprintComparer.Instance.Equals(value.Instance.Fingerprint, fingerprint));
        if (candidate is null || !candidate.IsExecutable || !candidate.SupportedDispositions.Contains(disposition))
            return false;
        selections[fingerprint] = disposition;
        return true;
    }

    public void Clear() => selections.Clear();

    public bool Remove(EquipmentInstanceFingerprint fingerprint) => selections.Remove(fingerprint);

    public void Invalidate()
    {
        generationId = null;
        selections.Clear();
    }
}

public sealed record SquireSelectionReconciliation(int PreservedCount, IReadOnlyList<string> RemovedReasons);
