using System.Security.Cryptography;
using System.Text.Json;

namespace Squire.Persistence;

public sealed record LegacyMmfImportPreview(
    bool SourceFound,
    bool CanImport,
    bool AlreadyImported,
    string Message,
    string? SourceSha256 = null,
    int CleanupRuleCount = 0,
    int CharacterRuleCount = 0);

public sealed class LegacyMmfImporter
{
    private readonly string sourcePath;
    private readonly PluginConfiguration target;
    private readonly Action save;
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LegacyMmfImporter(string sourcePath, PluginConfiguration target, Action save)
    {
        this.sourcePath = sourcePath;
        this.target = target;
        this.save = save;
    }

    public LegacyMmfImportPreview Preview()
    {
        if (!File.Exists(sourcePath))
            return new(false, false, false, "No MarketMafioso configuration was found.");

        try
        {
            var sourceBytes = File.ReadAllBytes(sourcePath);
            var sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes));
            var document = JsonSerializer.Deserialize<LegacyMmfConfiguration>(sourceBytes, jsonOptions);
            if (document?.Squire is null)
                return new(true, false, false, "MarketMafioso has no readable Squire configuration.", sourceSha256);
            if (HasUnresolvedRouteAuthority(document.OutfitterRouteExecutionStateJson))
            {
                return new(
                    true,
                    false,
                    false,
                    "Migration is blocked while MarketMafioso retains active or indeterminate Outfitter route authority.",
                    sourceSha256);
            }

            var cleanupRuleCount = document.Squire.CleanupRules?.Count ?? 0;
            var characterRuleCount = document.Squire.RulesByCharacter?.Values.Sum(rules => rules?.Count ?? 0) ?? 0;
            if (target.LegacyMmfMigration is { } receipt)
            {
                if (string.Equals(receipt.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new(
                        true,
                        false,
                        true,
                        "This exact MarketMafioso configuration has already been imported.",
                        sourceSha256,
                        cleanupRuleCount,
                        characterRuleCount);
                }

                return new(
                    true,
                    false,
                    false,
                    "MarketMafioso changed after the completed import. Squire will not overwrite its standalone settings automatically.",
                    sourceSha256,
                    cleanupRuleCount,
                    characterRuleCount);
            }

            return new(
                true,
                true,
                false,
                "MarketMafioso Squire settings are ready to import.",
                sourceSha256,
                cleanupRuleCount,
                characterRuleCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(true, false, false, $"MarketMafioso configuration could not be read: {exception.Message}");
        }
    }

    public LegacyMmfImportPreview Import()
    {
        var preview = Preview();
        if (!preview.CanImport || preview.SourceSha256 is null)
            return preview;

        var sourceBytes = File.ReadAllBytes(sourcePath);
        var document = JsonSerializer.Deserialize<LegacyMmfConfiguration>(sourceBytes, jsonOptions)
            ?? throw new InvalidDataException("MarketMafioso configuration was empty.");
        target.Settings = document.Squire ?? throw new InvalidDataException("MarketMafioso Squire configuration was missing.");
        Normalize(target.Settings);
        target.LegacyMmfMigration = new(
            preview.SourceSha256,
            DateTimeOffset.UtcNow,
            preview.CleanupRuleCount,
            preview.CharacterRuleCount);
        save();
        return Preview();
    }

    private static bool HasUnresolvedRouteAuthority(string? routeStateJson) =>
        !string.IsNullOrWhiteSpace(routeStateJson) &&
        !string.Equals(routeStateJson.Trim(), "null", StringComparison.OrdinalIgnoreCase);

    private static void Normalize(SquireSettings settings)
    {
        settings.CleanupRules ??= [];
        settings.BuiltInRuleOverrides ??= new();
        settings.RulesByCharacter ??= new();
        settings.ExcludedItemIdsByCharacter ??= new();
        settings.DuplicateRetentionByCharacter ??= new();
        settings.HighRarityCleanupItemIdsByCharacter ??= new();
        settings.RuleSchemaVersion = Math.Max(2, settings.RuleSchemaVersion);
    }

    private sealed class LegacyMmfConfiguration
    {
        public SquireSettings? Squire { get; init; }
        public string? OutfitterRouteExecutionStateJson { get; init; }
    }
}

