using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire.Outfitter;

namespace MarketMafioso.Squire.Observation;

public sealed record RetainerObservationOwnerScope(
    ulong LocalContentId,
    string CharacterName,
    string HomeWorld)
{
    public bool IsAvailable => LocalContentId != 0 &&
        !string.IsNullOrWhiteSpace(CharacterName) &&
        !string.IsNullOrWhiteSpace(HomeWorld);
}

/// <summary>
/// Parses the already-rendered RetainerCharacter identity fields and binds them to the
/// current player scope. Target metadata supplies the expected tuple, never missing UI
/// values: name, job, and level must each be visibly present before the identity is complete.
/// </summary>
public static partial class RenderedRetainerIdentityParser
{
    public static RenderedRetainerIdentityObservation Parse(
        AgentBridgeRenderedUiSnapshot snapshot,
        OutfitterTarget target,
        RetainerObservationOwnerScope owner)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);

        if (target.Kind != OutfitterTargetKind.Retainer || target.RetainerMetadata is not { } metadata)
            return Ambiguous(snapshot, owner, target.Name, "The selected target is not an exact retainer metadata target.");
        if (!owner.IsAvailable || !target.IsCurrentCharacter ||
            metadata.OwnerContentId != owner.LocalContentId ||
            !Same(owner.CharacterName, target.OwnerCharacterName) ||
            !Same(owner.HomeWorld, target.OwnerHomeWorld))
        {
            return Ambiguous(snapshot, owner, metadata.RetainerName,
                "The active character scope does not exactly own the selected retainer target.");
        }
        if (target.Job is not { ClassJobId: > 0 } job || job.ClassJobId != metadata.ClassJobId ||
            metadata.Level is < 1 or > 100)
        {
            return Ambiguous(snapshot, owner, metadata.RetainerName,
                "The selected retainer target has no coherent class/job and level expectation.");
        }

        var addon = snapshot.Addons.FirstOrDefault(value =>
            string.Equals(value.Name, "RetainerCharacter", StringComparison.Ordinal));
        if (addon is not { Present: true, Ready: true, Visible: true })
            return Unavailable(snapshot, owner, metadata.RetainerName,
                "The rendered RetainerCharacter addon is unavailable.");

        var texts = addon.TextNodes
            .Select(value => Normalize(value.Text))
            .Where(value => value.Length > 0)
            .ToArray();
        var namePresent = texts.Any(value => MatchesRenderedRetainerName(value, metadata.RetainerName));
        var jobLabels = new[] { job.Abbreviation, job.Name }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var jobPresent = jobLabels.Length > 0 && texts.Any(text =>
            jobLabels.Any(label => ContainsSemanticToken(text, label)));
        var levelPresent = texts.Any(text => ContainsRenderedLevel(text, metadata.Level));

        if (!namePresent || !jobPresent || !levelPresent)
        {
            var missing = string.Join(", ", new[]
            {
                namePresent ? null : "retainer name",
                jobPresent ? null : "class/job",
                levelPresent ? null : "level",
            }.Where(value => value is not null));
            return Ambiguous(snapshot, owner, metadata.RetainerName,
                $"The rendered retainer identity is missing or contradicts the expected {missing}; cached metadata will not fill it in.");
        }

        return new(
            RenderedRetainerIdentityStatus.Complete,
            snapshot.CapturedAtUtc,
            owner.CharacterName.Trim(),
            owner.HomeWorld.Trim(),
            metadata.RetainerName,
            metadata.ClassJobId,
            metadata.Level,
            "The active owner and rendered retainer name, class/job, and level exactly match the selected target.",
            FindLabeledStat(texts, "average item level", "item level"),
            FindLabeledStat(texts, "gathering"),
            FindLabeledStat(texts, "perception"));
    }

    private static int? FindLabeledStat(IReadOnlyList<string> texts, params string[] labels)
    {
        foreach (var text in texts)
        foreach (var label in labels)
        {
            var index = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;
            var match = NumberPattern().Match(text[(index + label.Length)..]);
            if (match.Success && int.TryParse(match.Value.Replace(",", string.Empty),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return value;
        }
        return null;
    }

    private static bool ContainsRenderedLevel(string text, uint expectedLevel)
    {
        foreach (Match match in LevelPattern().Matches(text))
        {
            if (uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var level) &&
                level == expectedLevel)
                return true;
        }
        return false;
    }

    private static bool ContainsSemanticToken(string text, string expected)
    {
        var normalizedExpected = Normalize(expected);
        if (normalizedExpected.Length == 0)
            return false;
        var index = text.IndexOf(normalizedExpected, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var beforeBoundary = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var end = index + normalizedExpected.Length;
            var afterBoundary = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (beforeBoundary && afterBoundary)
                return true;
            index = text.IndexOf(normalizedExpected, index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool MatchesRenderedRetainerName(string text, string expected)
    {
        var normalizedExpected = Normalize(expected);
        if (string.Equals(text, normalizedExpected, StringComparison.OrdinalIgnoreCase))
            return true;
        const string prefix = "Retainer:";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(text[prefix.Length..].Trim(), normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value) =>
        Regex.Replace(value?.Trim() ?? string.Empty, "\\s+", " ");

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static RenderedRetainerIdentityObservation Unavailable(
        AgentBridgeRenderedUiSnapshot snapshot,
        RetainerObservationOwnerScope owner,
        string retainerName,
        string diagnostic) =>
        new(RenderedRetainerIdentityStatus.Unavailable, snapshot.CapturedAtUtc,
            owner.CharacterName, owner.HomeWorld, retainerName, 0, 0, diagnostic);

    private static RenderedRetainerIdentityObservation Ambiguous(
        AgentBridgeRenderedUiSnapshot snapshot,
        RetainerObservationOwnerScope owner,
        string retainerName,
        string diagnostic) =>
        new(RenderedRetainerIdentityStatus.Ambiguous, snapshot.CapturedAtUtc,
            owner.CharacterName, owner.HomeWorld, retainerName, 0, 0, diagnostic);

    [GeneratedRegex(@"(?:\bLv\.?|\bLevel)\s*:?[\s]*(\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelPattern();

    [GeneratedRegex(@"\d[\d,]*", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
