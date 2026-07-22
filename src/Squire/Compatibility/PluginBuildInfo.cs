using System.Reflection;

namespace MarketMafioso;

public static class PluginBuildInfo
{
    public static string AssemblyVersion =>
        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "Unknown";

    public static string InformationalVersion =>
        typeof(Plugin).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? string.Empty;

    public static string DisplayVersion => FormatDisplayVersion(InformationalVersion, AssemblyVersion);

    public static string FormatDisplayVersion(string? informationalVersion, string? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion;

        if (!string.IsNullOrWhiteSpace(assemblyVersion))
            return assemblyVersion;

        return "Unknown";
    }
}
