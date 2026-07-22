using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MarketMafioso.AgentBridge;

namespace MarketMafioso.Squire.Observation;

public enum RenderedCharacterObservationStatus
{
    Unavailable,
    Partial,
    Complete,
}

public sealed record RenderedGatheringStatsObservation(
    Guid GenerationId,
    DateTimeOffset CapturedAtUtc,
    RenderedCharacterObservationStatus Status,
    string? JobName,
    int? Level,
    int? Gathering,
    int? Perception,
    int? GatheringPoints,
    IReadOnlyList<RenderedStatEvidence> Evidence,
    string Diagnostic);

public sealed record RenderedStatEvidence(
    string Semantic,
    string LabelNodePath,
    string ValueNodePath,
    float RowY);

public static class RenderedCharacterStatsParser
{
    private static readonly HashSet<string> SupportedJobs = new(StringComparer.Ordinal)
    {
        "Miner",
        "Botanist",
    };

    public static RenderedGatheringStatsObservation Parse(AgentBridgeRenderedUiSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var character = snapshot.Addons.FirstOrDefault(addon => addon.Name == "Character" && addon.Visible);
        if (character == null)
            return Unavailable(snapshot, "The rendered Character addon is not visible.");

        var jobName = character.TextNodes.Select(node => node.Text).FirstOrDefault(SupportedJobs.Contains);
        if (jobName == null)
            return Unavailable(snapshot, "The rendered active job is not Miner or Botanist.");

        var levelText = character.TextNodes.Select(node => node.Text)
            .FirstOrDefault(text => text.StartsWith("Level ", StringComparison.Ordinal));
        var level = levelText != null && int.TryParse(levelText.AsSpan("Level ".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLevel)
            ? parsedLevel
            : (int?)null;

        var statusAddon = snapshot.Addons.FirstOrDefault(addon => addon.Name == "CharacterStatus" && addon.Visible);
        if (statusAddon == null)
            return Partial(snapshot, jobName, level, null, null, null, [], "The hosted CharacterStatus addon is not visible.");

        var evidence = new List<RenderedStatEvidence>(3);
        var gathering = FindStat(statusAddon.TextNodes, "Gathering", evidence);
        var perception = FindStat(statusAddon.TextNodes, "Perception", evidence);
        var gp = FindStat(statusAddon.TextNodes, "GP", evidence);
        if (level is null || gathering is null || perception is null || gp is null)
            return Partial(snapshot, jobName, level, gathering, perception, gp, evidence, "One or more rendered gathering fields could not be paired with a numeric value.");

        return new(
            Guid.NewGuid(),
            snapshot.CapturedAtUtc,
            RenderedCharacterObservationStatus.Complete,
            jobName,
            level,
            gathering,
            perception,
            gp,
            evidence,
            "Active gathering stats were observed from the rendered Character and CharacterStatus addons.");
    }

    private static int? FindStat(
        IReadOnlyList<AgentBridgeRenderedTextNode> nodes,
        string label,
        ICollection<RenderedStatEvidence> evidence)
    {
        foreach (var labelNode in nodes.Where(node => string.Equals(node.Text, label, StringComparison.Ordinal)))
        {
            var parentPath = ParentPath(labelNode.NodePath);
            var valueNode = nodes.FirstOrDefault(node =>
                ParentPath(node.NodePath) == parentPath &&
                Math.Abs(node.Y - labelNode.Y) < 0.1f &&
                node.X > labelNode.X &&
                TryParseInteger(node.Text, out _));
            if (valueNode == null || !TryParseInteger(valueNode.Text, out var value))
                continue;
            evidence.Add(new(label, labelNode.NodePath, valueNode.NodePath, labelNode.Y));
            return value;
        }
        return null;
    }

    private static bool TryParseInteger(string text, out int value) =>
        int.TryParse(text.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static string ParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static RenderedGatheringStatsObservation Unavailable(AgentBridgeRenderedUiSnapshot snapshot, string diagnostic) =>
        new(Guid.NewGuid(), snapshot.CapturedAtUtc, RenderedCharacterObservationStatus.Unavailable, null, null, null, null, null, [], diagnostic);

    private static RenderedGatheringStatsObservation Partial(
        AgentBridgeRenderedUiSnapshot snapshot,
        string jobName,
        int? level,
        int? gathering,
        int? perception,
        int? gp,
        IReadOnlyList<RenderedStatEvidence> evidence,
        string diagnostic) =>
        new(Guid.NewGuid(), snapshot.CapturedAtUtc, RenderedCharacterObservationStatus.Partial, jobName, level, gathering, perception, gp, evidence, diagnostic);
}

public sealed class RenderedGatheringStatsStabilizer
{
    private readonly TimeSpan stabilityWindow;
    private ObservationSignature? candidate;
    private DateTimeOffset candidateFirstSeenUtc;

    public RenderedGatheringStatsStabilizer(TimeSpan stabilityWindow)
    {
        if (stabilityWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stabilityWindow));
        this.stabilityWindow = stabilityWindow;
    }

    public void Reset()
    {
        candidate = null;
        candidateFirstSeenUtc = default;
    }

    public RenderedGatheringStatsObservation Observe(RenderedGatheringStatsObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Status != RenderedCharacterObservationStatus.Complete)
        {
            Reset();
            return observation;
        }

        var signature = ObservationSignature.From(observation);
        if (candidate != signature)
        {
            candidate = signature;
            candidateFirstSeenUtc = observation.CapturedAtUtc;
            return AwaitingStability(observation);
        }

        var stableFor = observation.CapturedAtUtc - candidateFirstSeenUtc;
        if (stableFor < stabilityWindow)
            return AwaitingStability(observation);

        return observation with
        {
            Diagnostic = $"{observation.Diagnostic} The rendered values remained unchanged for at least {stabilityWindow.TotalSeconds:0.#} seconds.",
        };
    }

    private RenderedGatheringStatsObservation AwaitingStability(RenderedGatheringStatsObservation observation) =>
        observation with
        {
            Status = RenderedCharacterObservationStatus.Partial,
            Diagnostic = $"Rendered gathering fields are present but have not remained unchanged for {stabilityWindow.TotalSeconds:0.#} seconds; waiting for one coherent UI generation.",
        };

    private sealed record ObservationSignature(
        string JobName,
        int Level,
        int Gathering,
        int Perception,
        int GatheringPoints)
    {
        public static ObservationSignature From(RenderedGatheringStatsObservation observation) => new(
            observation.JobName!,
            observation.Level!.Value,
            observation.Gathering!.Value,
            observation.Perception!.Value,
            observation.GatheringPoints!.Value);
    }
}
