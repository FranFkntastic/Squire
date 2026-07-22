using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.Automation.Travel;

public static class AutomationTravelPreflight
{
    public static readonly IReadOnlyList<string> BlockingAddonNames =
    [
        "ItemSearch",
        "ItemSearchResult",
        "SelectString",
        "SelectYesno",
        "InputNumeric",
        "ContextMenu",
        "RetainerList",
        "InventoryRetainer",
        "InventoryRetainerLarge",
        "InventoryRetainerSmall",
    ];

    public static AutomationTravelPreflightResult Check(IReadOnlyList<string> openBlockingAddons)
    {
        ArgumentNullException.ThrowIfNull(openBlockingAddons);

        var blockers = openBlockingAddons
            .Where(addon => !string.IsNullOrWhiteSpace(addon))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (blockers.Length == 0)
        {
            return new AutomationTravelPreflightResult
            {
                CanSendCommand = true,
                Message = "No blocking UI is open.",
                BlockingAddons = blockers,
            };
        }

        return new AutomationTravelPreflightResult
        {
            CanSendCommand = false,
            Message = $"Close blocking UI before Lifestream travel: {string.Join(", ", blockers)}.",
            BlockingAddons = blockers,
        };
    }
}

public sealed record AutomationTravelPreflightResult
{
    public bool CanSendCommand { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockingAddons { get; init; } = [];
}
