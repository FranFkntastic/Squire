using System;

namespace MarketMafioso.Squire;

public static class SquireAdvisorConfigurationMigration
{
    private const int CurrentDefaultVersion = 1;

    public static bool Migrate(ISquireConfigurationStore configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Squire.OutfitterAdvisorContextDefaultVersion >= CurrentDefaultVersion)
            return false;

        configuration.Squire.OutfitterAdvisorContext = "OrdinaryResourceBenchmark";
        configuration.Squire.OutfitterAdvisorContextDefaultVersion = CurrentDefaultVersion;
        return true;
    }
}
