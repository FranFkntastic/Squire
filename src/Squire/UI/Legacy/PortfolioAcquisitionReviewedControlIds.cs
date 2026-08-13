namespace MarketMafioso.Windows.Squire;

internal static class PortfolioAcquisitionReviewedControlIds
{
    public const string Stage = "squire.outfitter.portfolio.acquisition.stage";
    public const string Resume = "squire.outfitter.portfolio.acquisition.resume";
    public const string VendorConfirmPrefix = "squire.outfitter.portfolio.acquisition.vendor-confirm.";
    public const string ArtisanExportPrefix = "squire.outfitter.portfolio.acquisition.artisan-export.";

    public static string VendorConfirm(string lineageKey) => VendorConfirmPrefix + StableSuffix(lineageKey);
    public static string ArtisanExport(string lineageKey) => ArtisanExportPrefix + StableSuffix(lineageKey);

    private static string StableSuffix(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
