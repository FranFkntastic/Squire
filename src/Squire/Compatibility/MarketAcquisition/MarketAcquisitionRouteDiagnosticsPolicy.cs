namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRouteDiagnosticsPolicy
{
    public static bool ShouldCreatePackage(bool settingEnabled, bool explicitDiagnosticStart) =>
        settingEnabled || explicitDiagnosticStart;
}
