using System;
using Franthropy.Dalamud.UI.Items;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public static class RequestLineInputValidator
{
    public static bool CanAddIntentLine(
        DalamudItemOption? selectedItem,
        string quantityMode,
        string targetQuantityBuffer,
        string maxQuantityBuffer,
        string maxUnitPriceBuffer,
        string gilCapBuffer) =>
        selectedItem is not null &&
        (string.IsNullOrWhiteSpace(maxUnitPriceBuffer) ||
         TryParseUInt(maxUnitPriceBuffer, out _)) &&
        (!string.Equals(quantityMode, "TargetQuantity", StringComparison.OrdinalIgnoreCase) ||
         TryParseUInt(targetQuantityBuffer, out var targetQuantity) && targetQuantity > 0) &&
        (string.IsNullOrWhiteSpace(gilCapBuffer) || TryParseUInt(gilCapBuffer, out _)) &&
        (string.IsNullOrWhiteSpace(maxQuantityBuffer) || TryParseUInt(maxQuantityBuffer, out _));

    private static bool TryParseUInt(string value, out uint parsed) =>
        uint.TryParse(value?.Trim(), out parsed);
}
