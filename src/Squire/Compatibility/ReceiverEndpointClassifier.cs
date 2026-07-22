using System;

namespace MarketMafioso;

public enum ReceiverEndpointKind
{
    Invalid,
    Local,
    KnownHosted,
    CustomRemote,
}

public readonly record struct ReceiverEndpointInfo(
    ReceiverEndpointKind Kind,
    Uri? Uri,
    string? DashboardBaseUrl)
{
    public bool RequiresApiKey =>
        Kind is ReceiverEndpointKind.KnownHosted or ReceiverEndpointKind.CustomRemote;
}

public static class ReceiverEndpointClassifier
{
    public static ReceiverEndpointInfo Classify(string? serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new ReceiverEndpointInfo(ReceiverEndpointKind.Invalid, null, null);

        if (IsLocalHost(uri.Host))
            return new ReceiverEndpointInfo(ReceiverEndpointKind.Local, uri, DeriveDashboardBaseUrl(uri));

        if (uri.Host.Equals("dev.xivcraftarchitect.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("xivcraftarchitect.com", StringComparison.OrdinalIgnoreCase))
        {
            if (IsRetiredHostedPath(uri))
                return new ReceiverEndpointInfo(ReceiverEndpointKind.Invalid, uri, null);

            var dashboardBaseUrl = $"{uri.Scheme}://{uri.Host}/marketmafioso";
            return new ReceiverEndpointInfo(ReceiverEndpointKind.KnownHosted, uri, dashboardBaseUrl);
        }

        return new ReceiverEndpointInfo(ReceiverEndpointKind.CustomRemote, uri, DeriveDashboardBaseUrl(uri));
    }

    public static string? BuildDashboardReportUrl(string? serverUrl, string? reportId)
    {
        if (string.IsNullOrWhiteSpace(reportId))
            return null;

        var dashboardBaseUrl = BuildDashboardBaseUrl(serverUrl);
        return string.IsNullOrWhiteSpace(dashboardBaseUrl)
            ? null
            : $"{dashboardBaseUrl}/reports/{Uri.EscapeDataString(reportId)}";
    }

    public static string? BuildDashboardBaseUrl(string? serverUrl)
    {
        var endpoint = Classify(serverUrl);
        return endpoint.DashboardBaseUrl ??
               (endpoint.Uri == null ? null : DeriveDashboardBaseUrl(endpoint.Uri));
    }

    public static string? BuildAcquisitionBaseUrl(string? serverUrl)
    {
        var apiBaseUrl = BuildApiBaseUrl(serverUrl);
        return string.IsNullOrWhiteSpace(apiBaseUrl)
            ? null
            : $"{apiBaseUrl}/acquisition";
    }

    public static string? BuildWorkshopHostCapabilitiesUrl(string? serverUrl)
    {
        var apiBaseUrl = BuildWorkshopHostApiBaseUrl(serverUrl);
        return string.IsNullOrWhiteSpace(apiBaseUrl)
            ? null
            : $"{apiBaseUrl}/capabilities";
    }

    public static string? BuildWorkshopHostCraftAppraiseUrl(string? serverUrl)
    {
        var apiBaseUrl = BuildWorkshopHostApiBaseUrl(serverUrl);
        return string.IsNullOrWhiteSpace(apiBaseUrl)
            ? null
            : $"{apiBaseUrl}/craft/appraise";
    }

    public static string? BuildDashboardUrl(string? serverUrl)
    {
        var endpoint = Classify(serverUrl);
        return string.IsNullOrWhiteSpace(endpoint.DashboardBaseUrl)
            ? null
            : $"{endpoint.DashboardBaseUrl}/";
    }

    public static string? BuildClientKeyManagerUrl(string? serverUrl)
    {
        var endpoint = Classify(serverUrl);
        return string.IsNullOrWhiteSpace(endpoint.DashboardBaseUrl)
            ? null
            : $"{endpoint.DashboardBaseUrl}/settings?tab=authentication";
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);

    private static bool IsRetiredHostedPath(Uri uri) =>
        uri.AbsolutePath.StartsWith("/api/marketmafioso", StringComparison.OrdinalIgnoreCase);

    private static string? BuildApiBaseUrl(string? serverUrl)
    {
        var endpoint = Classify(serverUrl);
        if (endpoint.Kind == ReceiverEndpointKind.Invalid || endpoint.Uri == null)
            return null;

        return DeriveApiBaseUrl(endpoint.Uri);
    }

    private static string? BuildWorkshopHostApiBaseUrl(string? serverUrl)
    {
        var endpoint = Classify(serverUrl);
        if (endpoint.Kind == ReceiverEndpointKind.Invalid || endpoint.Uri == null)
            return null;

        return DeriveWorkshopHostApiBaseUrl(endpoint.Uri);
    }

    private static string? DeriveDashboardBaseUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        const string apiInventorySuffix = "/api/inventory";
        if (path.EndsWith(apiInventorySuffix, StringComparison.OrdinalIgnoreCase))
        {
            var dashboardBasePathFromApi = path[..^apiInventorySuffix.Length].TrimEnd('/');
            return $"{uri.Scheme}://{uri.Authority}{dashboardBasePathFromApi}";
        }

        const string inventorySuffix = "/inventory";
        if (!path.EndsWith(inventorySuffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var dashboardBasePathFromInventory = path[..^inventorySuffix.Length].TrimEnd('/');
        return $"{uri.Scheme}://{uri.Authority}{dashboardBasePathFromInventory}";
    }

    private static string? DeriveApiBaseUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        const string apiInventorySuffix = "/api/inventory";
        if (path.EndsWith(apiInventorySuffix, StringComparison.OrdinalIgnoreCase))
        {
            var apiBasePathFromApi = path[..^"/inventory".Length].TrimEnd('/');
            return $"{uri.Scheme}://{uri.Authority}{apiBasePathFromApi}";
        }

        const string inventorySuffix = "/inventory";
        if (!path.EndsWith(inventorySuffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var apiBasePathFromInventory = path[..^inventorySuffix.Length].TrimEnd('/');
        return $"{uri.Scheme}://{uri.Authority}{apiBasePathFromInventory}";
    }

    private static string? DeriveWorkshopHostApiBaseUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        const string apiInventorySuffix = "/api/inventory";
        if (path.EndsWith(apiInventorySuffix, StringComparison.OrdinalIgnoreCase))
        {
            var apiBasePathFromApi = path[..^"/inventory".Length].TrimEnd('/');
            return $"{uri.Scheme}://{uri.Authority}{apiBasePathFromApi}";
        }

        const string inventorySuffix = "/inventory";
        if (!path.EndsWith(inventorySuffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var dashboardBasePathFromInventory = path[..^inventorySuffix.Length].TrimEnd('/');
        var apiBasePath = string.IsNullOrWhiteSpace(dashboardBasePathFromInventory)
            ? "/api"
            : $"{dashboardBasePathFromInventory}/api";
        return $"{uri.Scheme}://{uri.Authority}{apiBasePath}";
    }
}
