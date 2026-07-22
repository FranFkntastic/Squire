namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public static class RequestPricingFormatter
{
    public static string FormatOptionalGil(uint gil) =>
        gil == 0 ? "Unset" : $"{gil:N0} gil";
}
