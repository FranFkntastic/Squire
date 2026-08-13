using System.Security.Cryptography;
using System.Text;
using MarketMafioso.Squire;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Squire.Persistence;

internal sealed record LegacySettingsArchivalResult(bool Changed, LegacySettingsCompatibilityReceipt? Receipt);

internal static class LegacySettingsArchivalConsolidator
{
    internal const int ReceiptSchemaVersion = 1;
    internal const string Resolution = "CanonicalPreservedLegacyArchived";

    private static readonly JsonSerializer SemanticSerializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        TypeNameHandling = TypeNameHandling.None,
    });

    public static LegacySettingsArchivalResult Consolidate(
        PluginConfiguration configuration,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var legacyInput = configuration.PendingLegacySettingsInput;
        if (legacyInput is null)
            return new(false, configuration.LegacySettingsCompatibility);

        if (!configuration.FeatureSettingsWasDeserialized)
        {
            throw new InvalidDataException(
                "Legacy Settings were present without FeatureSettings. Squire preserved the input in memory but will not apply or overwrite policy without explicit recovery.");
        }

        var existingArchive = RecoverArchive(configuration);
        if (existingArchive is not null && !JToken.DeepEquals(existingArchive, legacyInput))
        {
            throw new InvalidDataException(
                "Legacy Settings input differs from the existing archive. Squire will not overwrite either payload automatically.");
        }

        var canonicalToken = JToken.FromObject(configuration.FeatureSettings, SemanticSerializer);
        var legacySemantic = NormalizeSemanticToken(legacyInput);
        var canonicalSemantic = NormalizeSemanticToken(canonicalToken);
        var differingFields = FindDifferingTopLevelFields(legacySemantic, canonicalSemantic);
        var unresolvedFields = differingFields
            .Except(ResolvedCanonicalFields, StringComparer.Ordinal)
            .ToArray();
        var receipt = new LegacySettingsCompatibilityReceipt(
            ReceiptSchemaVersion,
            Resolution,
            ComputeSemanticSha256(legacySemantic),
            ComputeSemanticSha256(canonicalSemantic),
            differingFields,
            unresolvedFields,
            capturedAtUtc);

        configuration.LegacySettingsArchiveJson = JsonConvert.SerializeObject(legacyInput, Formatting.None);
        configuration.LegacySettingsCompatibility = receipt;
        configuration.Version = Math.Max(2, configuration.Version);
        configuration.ClearPendingLegacySettingsInput();
        return new(true, receipt);
    }

    public static JToken? RecoverArchive(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return string.IsNullOrWhiteSpace(configuration.LegacySettingsArchiveJson)
            ? null
            : JToken.Parse(configuration.LegacySettingsArchiveJson);
    }

    internal static string ComputeSemanticSha256(JToken value)
    {
        var normalized = JsonConvert.SerializeObject(NormalizeSemanticToken(value), Formatting.None);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static IReadOnlyList<string> FindDifferingTopLevelFields(JToken legacy, JToken canonical)
    {
        if (legacy is not JObject legacyObject || canonical is not JObject canonicalObject)
            return ["$"];

        return legacyObject.Properties()
            .Select(property => property.Name)
            .Concat(canonicalObject.Properties().Select(property => property.Name))
            .Distinct(StringComparer.Ordinal)
            .Where(name => !JToken.DeepEquals(legacyObject[name], canonicalObject[name]))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static readonly IReadOnlySet<string> ResolvedCanonicalFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "SelectedWorkspace",
        "OutfitterAdvisorContextDefaultVersion",
        "BuiltInRuleOverrides",
        "ProtectBlueAndPurpleGear",
        "AllowRiskyMateriaRetrieval",
        "ProtectFutureLevelingGearOptIn",
    };

    private static JToken NormalizeSemanticToken(JToken token) => token switch
    {
        JObject value => new JObject(value.Properties()
            .Where(property => !string.Equals(property.Name, "$type", StringComparison.Ordinal))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => new JProperty(property.Name, NormalizeSemanticToken(property.Value)))),
        JArray value => new JArray(value.Select(NormalizeSemanticToken)),
        _ => token.DeepClone(),
    };
}
