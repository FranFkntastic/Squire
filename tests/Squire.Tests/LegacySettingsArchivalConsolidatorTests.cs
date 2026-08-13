using MarketMafioso.Squire;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Squire.Persistence;

namespace Squire.Tests;

public sealed class LegacySettingsArchivalConsolidatorTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 8, 13, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DivergentDualPayloadArchivesLegacyAndPreservesCanonical()
    {
        var input = CreateDivergentDualPayload();
        var expectedLegacy = input["Settings"]!.DeepClone();
        var expectedCanonical = input["FeatureSettings"]!.DeepClone();
        var configuration = Deserialize(input);

        var result = LegacySettingsArchivalConsolidator.Consolidate(configuration, CapturedAt);
        var output = Serialize(configuration);

        Assert.True(result.Changed);
        Assert.Equal(LegacySettingsArchivalConsolidator.Resolution, result.Receipt?.Resolution);
        Assert.Contains("AllowRiskyMateriaRetrieval", result.Receipt!.DifferingFields);
        Assert.Contains("CleanupRules", result.Receipt!.UnresolvedFields);
        Assert.DoesNotContain("AllowRiskyMateriaRetrieval", result.Receipt.UnresolvedFields);
        Assert.Null(output["Settings"]);
        Assert.True(JToken.DeepEquals(expectedCanonical, output["FeatureSettings"]));
        Assert.True(JToken.DeepEquals(expectedLegacy, LegacySettingsArchivalConsolidator.RecoverArchive(configuration)));
    }

    [Fact]
    public void ConsolidatedPayloadReloadIsByteStableNoOp()
    {
        var configuration = Deserialize(CreateDivergentDualPayload());
        Assert.True(LegacySettingsArchivalConsolidator.Consolidate(configuration, CapturedAt).Changed);
        var first = JsonConvert.SerializeObject(configuration, SerializerSettings);

        var reloaded = JsonConvert.DeserializeObject<PluginConfiguration>(first, SerializerSettings)!;
        var secondResult = LegacySettingsArchivalConsolidator.Consolidate(reloaded, CapturedAt.AddHours(1));
        var second = JsonConvert.SerializeObject(reloaded, SerializerSettings);

        Assert.False(secondResult.Changed);
        Assert.Equal(first, second);
    }

    [Fact]
    public void RecoveryReturnsArchiveWithoutApplyingIt()
    {
        var configuration = Deserialize(CreateDivergentDualPayload());
        var canonicalBefore = JsonConvert.SerializeObject(configuration.FeatureSettings);
        Assert.True(LegacySettingsArchivalConsolidator.Consolidate(configuration, CapturedAt).Changed);

        var recovered = LegacySettingsArchivalConsolidator.RecoverArchive(configuration);

        Assert.NotNull(recovered);
        Assert.Equal("Outfitter", recovered!["SelectedWorkspace"]?.Value<string>());
        Assert.Equal(canonicalBefore, JsonConvert.SerializeObject(configuration.FeatureSettings));
    }

    [Fact]
    public void LegacyOnlyPayloadFailsClosedWithoutApplyingIt()
    {
        var input = CreateDivergentDualPayload();
        input.Remove("FeatureSettings");
        var configuration = Deserialize(input);

        var exception = Assert.Throws<InvalidDataException>(() =>
            LegacySettingsArchivalConsolidator.Consolidate(configuration, CapturedAt));

        Assert.Contains("without FeatureSettings", exception.Message);
        Assert.Null(configuration.LegacySettingsArchiveJson);
    }

    [Fact]
    public void ExistingArchiveCannotBeOverwrittenByDifferentLegacyInput()
    {
        var first = Deserialize(CreateDivergentDualPayload());
        Assert.True(LegacySettingsArchivalConsolidator.Consolidate(first, CapturedAt).Changed);
        var output = Serialize(first);
        output["Settings"] = JObject.FromObject(new SquireSettings { Search = "different" });
        var reloaded = Deserialize(output);

        var exception = Assert.Throws<InvalidDataException>(() =>
            LegacySettingsArchivalConsolidator.Consolidate(reloaded, CapturedAt.AddMinutes(1)));

        Assert.Contains("differs from the existing archive", exception.Message);
    }

    [Fact]
    public void ExactExternalFixturePreservesCanonicalArchiveAndPolicyReads()
    {
        var fixturePath = Environment.GetEnvironmentVariable("SQUIRE_TERTIARY_CONFIG_FIXTURE");
        Assert.False(
            string.IsNullOrWhiteSpace(fixturePath),
            "Set SQUIRE_TERTIARY_CONFIG_FIXTURE to run the required exact external-fixture proof.");

        var inputText = File.ReadAllText(fixturePath!);
        var input = JObject.Parse(inputText);
        var expectedLegacy = input["Settings"]!.DeepClone();
        var expectedCanonical = input["FeatureSettings"]!.DeepClone();
        var configuration = JsonConvert.DeserializeObject<PluginConfiguration>(inputText, SerializerSettings)!;
        var expectedPolicy = SquireExecutionRecoveryPolicy.From(configuration.FeatureSettings);

        var firstResult = LegacySettingsArchivalConsolidator.Consolidate(configuration, CapturedAt);
        var first = JsonConvert.SerializeObject(configuration, SerializerSettings);
        var reloaded = JsonConvert.DeserializeObject<PluginConfiguration>(first, SerializerSettings)!;
        var secondResult = LegacySettingsArchivalConsolidator.Consolidate(reloaded, CapturedAt.AddMinutes(1));
        var second = JsonConvert.SerializeObject(reloaded, SerializerSettings);

        Assert.True(firstResult.Changed);
        Assert.Contains("AllowRiskyMateriaRetrieval", firstResult.Receipt!.DifferingFields);
        Assert.Contains("CleanupRules", firstResult.Receipt!.UnresolvedFields);
        Assert.True(JToken.DeepEquals(expectedLegacy, LegacySettingsArchivalConsolidator.RecoverArchive(configuration)));
        var firstOutput = JObject.Parse(first);
        Assert.Null(firstOutput["Settings"]);
        Assert.Equal(
            LegacySettingsArchivalConsolidator.ComputeSemanticSha256(expectedCanonical),
            LegacySettingsArchivalConsolidator.ComputeSemanticSha256(firstOutput["FeatureSettings"]!));
        Assert.Equal(expectedPolicy, SquireExecutionRecoveryPolicy.From(configuration.FeatureSettings));
        Assert.Equal(expectedPolicy, SquireExecutionRecoveryPolicy.From(reloaded.FeatureSettings));
        Assert.False(secondResult.Changed);
        Assert.Equal(first, second);
        var reloadedArchive = LegacySettingsArchivalConsolidator.RecoverArchive(reloaded)!;
        Assert.Equal(
            reloaded.LegacySettingsCompatibility!.LegacySemanticSha256,
            LegacySettingsArchivalConsolidator.ComputeSemanticSha256(reloadedArchive));
    }

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Auto,
        Formatting = Formatting.None,
    };

    private static PluginConfiguration Deserialize(JObject input) =>
        JsonConvert.DeserializeObject<PluginConfiguration>(JsonConvert.SerializeObject(input, Formatting.None), SerializerSettings)!;

    private static JObject Serialize(PluginConfiguration configuration) =>
        JObject.Parse(JsonConvert.SerializeObject(configuration, SerializerSettings));

    private static JObject CreateDivergentDualPayload()
    {
        var legacy = JObject.FromObject(new SquireSettings
        {
            SelectedWorkspace = "Outfitter",
            CleanupRules = [],
            AllowRiskyMateriaRetrieval = false,
        });
        var canonical = JObject.FromObject(new SquireConfiguration
        {
            SelectedWorkspace = "Cleanup",
            CleanupRules =
            [
                new MarketMafioso.Squire.SquireCleanupRuleConfiguration
                {
                    Id = "user.fixture",
                    Name = "Retain one fixture",
                    Effect = new() { MinimumCopies = 1 },
                },
            ],
            AllowRiskyMateriaRetrieval = true,
        });
        return new JObject
        {
            ["Version"] = 1,
            ["PluginInstanceId"] = "fixture",
            ["Settings"] = legacy,
            ["FeatureSettings"] = canonical,
        };
    }
}
