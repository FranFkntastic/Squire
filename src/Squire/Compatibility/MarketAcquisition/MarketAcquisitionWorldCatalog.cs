using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionWorldCatalog
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> Regions =
        new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["North America"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Aether"] = ["Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren"],
                ["Primal"] = ["Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros"],
                ["Crystal"] = ["Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera"],
                ["Dynamis"] = ["Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph"],
            },
            ["Europe"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Chaos"] = ["Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan"],
                ["Light"] = ["Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark"],
            },
            ["Japan"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Elemental"] = ["Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon"],
                ["Gaia"] = ["Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima"],
                ["Mana"] = ["Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan"],
                ["Meteor"] = ["Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus"],
            },
            ["Oceania"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Materia"] = ["Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan"],
            },
        };

    public static IReadOnlyList<string> SupportedRegions { get; } = Regions.Keys.ToArray();

    public static string NormalizeRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
            throw new InvalidOperationException("Region is required for market acquisition routing.");

        var trimmed = region.Trim();
        if (trimmed.Equals("North-America", StringComparison.OrdinalIgnoreCase))
            return "North America";

        return Regions.ContainsKey(trimmed)
            ? trimmed
            : throw new InvalidOperationException($"Region {region} is not supported for market acquisition routing.");
    }

    public static IReadOnlyDictionary<string, string[]> ResolveDataCenters(string region)
    {
        var normalizedRegion = NormalizeRegion(region);
        return Regions[normalizedRegion];
    }

    public static string ResolveDataCenter(string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
            throw new InvalidOperationException("World name is required before route data center sorting.");

        var trimmed = worldName.Trim();
        foreach (var region in Regions.Values)
        {
            foreach (var dataCenter in region)
            {
                if (dataCenter.Value.Any(world => world.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
                    return dataCenter.Key;
            }
        }

        throw new InvalidOperationException($"World {worldName} is not mapped to a supported data center.");
    }

    public static IReadOnlyList<string> ResolveWorldsForDataCenters(string region, IEnumerable<string> dataCenters)
    {
        ArgumentNullException.ThrowIfNull(dataCenters);

        var normalizedRegion = NormalizeRegion(region);
        var regionDataCenters = Regions[normalizedRegion];
        var selectedDataCenters = dataCenters
            .Select(dataCenter => NormalizeDataCenterName(normalizedRegion, dataCenter))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedDataCenters.Count == 0)
            throw new InvalidOperationException("At least one data center is required for a scoped all-world sweep.");

        return regionDataCenters
            .Where(entry => selectedDataCenters.Contains(entry.Key))
            .SelectMany(entry => entry.Value)
            .ToArray();
    }

    public static string NormalizeDataCenterName(string region, string dataCenter)
    {
        if (string.IsNullOrWhiteSpace(dataCenter))
            throw new InvalidOperationException("Data center name is required for a scoped all-world sweep.");

        var normalizedRegion = NormalizeRegion(region);
        var regionDataCenters = Regions[normalizedRegion];
        var normalized = regionDataCenters.Keys
            .FirstOrDefault(candidate => candidate.Equals(dataCenter.Trim(), StringComparison.OrdinalIgnoreCase));
        return normalized ?? throw new InvalidOperationException($"{dataCenter} is not a {normalizedRegion} data center.");
    }
}
