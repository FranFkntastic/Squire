using System.Text.Json;
using Squire.Persistence;
using Xunit;

namespace Squire.Tests;

public sealed class LegacyMmfImporterTests
{
    [Fact]
    public void Import_CopiesSquireSettingsAndWritesIdempotentReceipt()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.WriteMmf(new
        {
            Squire = new
            {
                ProtectBlueAndPurpleGear = false,
                CleanupRules = new[] { new { Id = "user.rule", Name = "Rule", Enabled = true } },
                RulesByCharacter = new Dictionary<string, object[]>
                {
                    ["123"] = [new { Id = Guid.NewGuid(), Kind = 0, ItemId = 42, Quality = 0, Enabled = true }],
                },
            },
            OutfitterRouteExecutionStateJson = (string?)null,
        });
        var saves = 0;
        var configuration = new PluginConfiguration();
        var importer = new LegacyMmfImporter(sourcePath, configuration, () => saves++);

        var imported = importer.Import();

        Assert.True(imported.AlreadyImported);
        Assert.False(configuration.Settings.ProtectBlueAndPurpleGear);
        Assert.Single(configuration.Settings.CleanupRules);
        Assert.Single(configuration.Settings.RulesByCharacter["123"]);
        Assert.NotNull(configuration.LegacyMmfMigration);
        Assert.Equal(1, saves);
        Assert.True(importer.Preview().AlreadyImported);
    }

    [Fact]
    public void Preview_BlocksActiveOrIndeterminateOutfitterAuthority()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.WriteMmf(new
        {
            Squire = new { CleanupRules = Array.Empty<object>() },
            OutfitterRouteExecutionStateJson = "{\"phase\":\"Paused\"}",
        });
        var importer = new LegacyMmfImporter(sourcePath, new PluginConfiguration(), () => { });

        var preview = importer.Preview();

        Assert.False(preview.CanImport);
        Assert.Contains("route authority", preview.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_RefusesToOverwriteStandaloneSettingsWhenSourceChangedAfterImport()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.WriteMmf(new { Squire = new { Search = "first" } });
        var configuration = new PluginConfiguration();
        var importer = new LegacyMmfImporter(sourcePath, configuration, () => { });
        importer.Import();
        File.WriteAllText(sourcePath, JsonSerializer.Serialize(new { Squire = new { Search = "changed" } }));

        var preview = importer.Preview();

        Assert.False(preview.CanImport);
        Assert.False(preview.AlreadyImported);
        Assert.Contains("will not overwrite", preview.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("first", configuration.Settings.Search);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"squire-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteMmf(object value)
        {
            var path = System.IO.Path.Combine(Path, "MarketMafioso.json");
            File.WriteAllText(path, JsonSerializer.Serialize(value));
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
