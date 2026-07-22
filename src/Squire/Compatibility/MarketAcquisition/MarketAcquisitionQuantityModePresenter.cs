namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionQuantityModePresenter
{
    public static string FormatMode(string quantityMode) =>
        quantityMode switch
        {
            "TargetQuantity" => "Target quantity",
            "AllBelowThreshold" => "Buy below ceiling",
            _ => quantityMode,
        };

    public static string FormatQuantity(string quantityMode, uint quantity) =>
        quantityMode switch
        {
            "TargetQuantity" => $"{quantity:N0} target item(s)",
            "AllBelowThreshold" when quantity == 0 => "No quantity cap",
            "AllBelowThreshold" => $"{quantity:N0} max item(s)",
            _ => quantity.ToString("N0"),
        };

    public static string FormatExecutionHint(string quantityMode) =>
        quantityMode == "AllBelowThreshold"
            ? "Buys every safe whole listing at or below max unit price. Whole-stack overage is expected when listings are larger than the remaining target."
            : "Buys safe whole listings until the target is satisfied. Whole-stack overage can happen when the final listing is larger than the remaining target.";
}
