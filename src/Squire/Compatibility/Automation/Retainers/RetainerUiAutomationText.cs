using System;
using System.Collections.Generic;
using System.Globalization;

namespace MarketMafioso.Automation.Retainers;

public sealed record RetainerListEntry(string Name, bool IsActive);

public static class RetainerUiAutomationText
{
    public static int? FindRetainerListIndex(IReadOnlyList<RetainerListEntry> entries, string retainerName)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!entry.IsActive)
                continue;

            if (string.Equals(entry.Name, retainerName, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return null;
    }

    public static bool IsSelectStringEntryMatch(string entry, string targetText)
    {
        var normalizedEntry = NormalizeSelectStringEntry(entry);
        var normalizedTarget = NormalizeSelectStringEntry(targetText);
        return normalizedEntry.StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase);
    }

    public static int? FindContextMenuLabelIndex(IReadOnlyList<string> labels, string targetText)
    {
        for (var index = 0; index < labels.Count; index++)
        {
            if (IsSelectStringEntryMatch(labels[index], targetText))
                return index;
        }

        return null;
    }

    public static string NormalizeSelectStringEntry(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.PrivateUse)
                continue;

            builder.Append(character);
        }

        var normalized = builder.ToString().Trim();
        var detailStart = normalized.IndexOf(" (", StringComparison.Ordinal);
        if (detailStart >= 0)
            normalized = normalized[..detailStart].TrimEnd();

        return normalized.TrimEnd('.');
    }
}
