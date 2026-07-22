using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Acquisition;

internal readonly record struct PlayerAdvisorAuthorityFingerprint(string Value)
{
    public static PlayerAdvisorAuthorityFingerprint Capture(PlayerAdvisorBaseline baseline)
    {
        if (baseline is not
            {
                Status: PlayerAdvisorBaselineStatus.Complete,
                Character: { } character,
                ClassJobId: { } classJobId,
                Level: { } level,
                EffectiveLevel: { } effectiveLevel,
                IsLevelSynced: { } isLevelSynced,
            } || baseline.EquippedSlots.Count != PlayerAdvisorEquippedSlotMap.All.Count)
        {
            throw new InvalidOperationException("A complete player advisor baseline is required for Workbench authority.");
        }

        var lineage = new StringBuilder()
            .Append(character.LocalContentId).Append('|')
            .Append(character.HomeWorldId).Append('|')
            .Append(classJobId).Append('|')
            .Append(level).Append('|')
            .Append(effectiveLevel).Append('|')
            .Append(isLevelSynced ? '1' : '0').Append('|')
            .Append((int)(baseline.Target?.Kind ?? PlayerAdvisorBaselineTargetKind.ActiveLoadout)).Append('|')
            .Append(baseline.Target?.Key ?? "active-loadout").Append('|')
            .Append(baseline.Target?.AuthorityFingerprint ?? string.Empty);
        foreach (var stat in baseline.TotalStats.OrderBy(value => value.Key))
            lineage.Append('|').Append((int)stat.Key).Append(':').Append(stat.Value);
        foreach (var slot in baseline.EquippedSlots.OrderBy(value => value.Position))
        {
            lineage.Append('|')
                .Append((int)slot.Position).Append(':')
                .Append(slot.Definition?.ItemId ?? 0).Append(':')
                .Append(slot.Quality is { } quality ? (int)quality : -1);
            for (var index = 0; index < slot.MateriaIds.Count; index++)
            {
                lineage.Append(':')
                    .Append(index).Append('=')
                    .Append(slot.MateriaIds[index]).Append('@')
                    .Append(index < slot.MateriaGrades.Count ? slot.MateriaGrades[index] : byte.MaxValue);
            }
        }

        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lineage.ToString()))));
    }
}

internal sealed record OutfitterWorkbenchPlayerValidation(
    MinerBotanistReadOnlyAdvice Advice,
    string SelectedSolutionId,
    Guid EvidenceGenerationId,
    PlayerAdvisorAuthorityFingerprint CapturedPlayer,
    PlayerAdvisorAuthorityFingerprint RecapturedPlayer,
    bool DryRunOnly,
    PlayerAdvisorBaseline? RecapturedBaseline = null)
{
    public bool IsCurrentFor(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        OutfitterMarketEvidenceBook evidence) =>
        ReferenceEquals(Advice, advice) &&
        string.Equals(SelectedSolutionId, selectedSolutionId, StringComparison.Ordinal) &&
        EvidenceGenerationId == evidence.GenerationId &&
        CapturedPlayer == RecapturedPlayer;

    public static OutfitterWorkbenchPlayerValidation Create(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        OutfitterMarketEvidenceBook evidence,
        PlayerAdvisorAuthorityFingerprint capturedPlayer,
        PlayerAdvisorAuthorityFingerprint recapturedPlayer) =>
        new(advice, selectedSolutionId, evidence.GenerationId, capturedPlayer, recapturedPlayer, false);

#if DEBUG
    public static OutfitterWorkbenchPlayerValidation CreateDryRun(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        OutfitterMarketEvidenceBook evidence)
    {
        var fixture = new PlayerAdvisorAuthorityFingerprint("debug-dry-run-fixture");
        return new(advice, selectedSolutionId, evidence.GenerationId, fixture, fixture, true);
    }
#endif
}
