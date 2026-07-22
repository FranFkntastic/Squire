using System;

namespace MarketMafioso;

public static class WorkshopHostApiKeyRouting
{
    public const string MarketMafiosoClientPrefix = "mmf_client_";
    public const string CraftArchitectPrefix = "mmf_ca_";

    public static string ResolveAcquisitionKey(Configuration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return string.IsNullOrWhiteSpace(config.CommandPickupApiKey)
            ? config.ApiKey
            : config.CommandPickupApiKey;
    }

    public static bool NormalizeConfiguredKeys(Configuration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (IsCraftArchitectKey(config.ApiKey) && string.IsNullOrWhiteSpace(config.CommandPickupApiKey))
        {
            config.CommandPickupApiKey = config.ApiKey;
            config.ApiKey = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey) &&
            !string.IsNullOrWhiteSpace(config.CommandPickupApiKey) &&
            !IsCraftArchitectKey(config.CommandPickupApiKey))
        {
            config.ApiKey = config.CommandPickupApiKey;
            return true;
        }

        return false;
    }

    public static bool IsMarketMafiosoClientKey(string? value) =>
        HasPrefix(value, MarketMafiosoClientPrefix);

    public static bool IsCraftArchitectKey(string? value) =>
        HasPrefix(value, CraftArchitectPrefix);

    private static bool HasPrefix(string? value, string prefix) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
