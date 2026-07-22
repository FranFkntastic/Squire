using System;
using System.Security.Cryptography;
using System.Text;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionUnlock
{
    private const string UnlockKeyHash = "f879eae7278b2ac58621ee507090943b06d57ea4311078337b3e75ad2689d26b";

    public static bool IsUnlocked(Configuration config) => config.EnableMarketAcquisition;

    public static bool TryUnlock(Configuration config, string key) =>
        TryUnlock(config, key, UnlockKeyHash, DateTime.UtcNow);

    public static void Lock(Configuration config)
    {
        config.EnableMarketAcquisition = false;
        config.MarketAcquisitionUnlockedAtUtc = null;
    }

    internal static bool TryUnlock(Configuration config, string key, string expectedHash, DateTime nowUtc)
    {
        if (!KeyMatches(key, expectedHash))
            return false;

        config.EnableMarketAcquisition = true;
        config.MarketAcquisitionUnlockedAtUtc = nowUtc;
        return true;
    }

    internal static bool KeyMatches(string key, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(expectedHash))
            return false;

        var suppliedHash = Hash(key.Trim());
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedHash);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedHash.Trim().ToLowerInvariant());
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static string Hash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
