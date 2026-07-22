using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public sealed class UniversalisMarketFreshnessVerifier
{
    private static readonly Uri DefaultBaseUri = new("https://universalis.app/api/v2/");
    private readonly HttpClient httpClient;
    private readonly Uri baseUri;

    public UniversalisMarketFreshnessVerifier(HttpClient httpClient)
        : this(httpClient, DefaultBaseUri)
    {
    }

    public UniversalisMarketFreshnessVerifier(HttpClient httpClient, Uri baseUri)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
    }

    public async Task<UniversalisFreshnessResult> VerifyAsync(
        string worldName,
        uint itemId,
        DateTimeOffset observedAtUtc,
        IReadOnlyCollection<string> purchasedListingIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worldName))
            throw new InvalidOperationException("World name is required to verify Universalis freshness.");

        if (itemId == 0)
            throw new InvalidOperationException("Item id is required to verify Universalis freshness.");

        ArgumentNullException.ThrowIfNull(purchasedListingIds);

        var requestUri = new Uri(baseUri, $"{Uri.EscapeDataString(worldName)}/{itemId}?listings=100");
        using var response = await httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return UniversalisFreshnessResult.Unavailable($"HTTP {(int)response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var lastUploadTime = ReadOptionalUnixTime(json.RootElement, "lastUploadTime");
        if (lastUploadTime != null && lastUploadTime >= observedAtUtc)
            return UniversalisFreshnessResult.Confirmed("lastUploadTime is after local observation.");

        if (purchasedListingIds.Count > 0 && !ResponseContainsAnyListing(json.RootElement, purchasedListingIds))
            return UniversalisFreshnessResult.Confirmed("Purchased listings no longer appear in current listings.");

        return UniversalisFreshnessResult.Unconfirmed("Universalis did not reflect the local observation yet.");
    }

    private static DateTimeOffset? ReadOptionalUnixTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            !property.TryGetInt64(out var value))
        {
            return null;
        }

        return value > 9_999_999_999
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    private static bool ResponseContainsAnyListing(JsonElement element, IReadOnlyCollection<string> purchasedListingIds)
    {
        if (!element.TryGetProperty("listings", out var listingsElement) ||
            listingsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var ids = purchasedListingIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        return listingsElement
            .EnumerateArray()
            .Select(ReadOptionalListingId)
            .Where(id => id != null)
            .Any(id => ids.Contains(id!));
    }

    private static string? ReadOptionalListingId(JsonElement element)
    {
        if (!element.TryGetProperty("listingID", out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }
}

public sealed record UniversalisFreshnessResult
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    public static UniversalisFreshnessResult Confirmed(string message) =>
        new()
        {
            Status = "Confirmed",
            Message = message,
        };

    public static UniversalisFreshnessResult Unconfirmed(string message) =>
        new()
        {
            Status = "Unconfirmed",
            Message = message,
        };

    public static UniversalisFreshnessResult Unavailable(string message) =>
        new()
        {
            Status = "Unavailable",
            Message = message,
        };
}
