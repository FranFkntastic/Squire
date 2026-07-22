using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterTargetCatalogTests
{
    [Fact]
    public void Build_KeepsJobsGearsetsAndIncompleteRetainersVisible()
    {
        var job = new CharacterJobSnapshot(
            1,
            "GLA",
            "gladiator",
            15,
            true,
            null,
            "Tank",
            EquipmentStatSemantic.Strength,
            EquipmentDiscipline.Combat);
        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(null, null, null, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            [job],
            [new(2, "Dungeon", 1, [], true)],
            [],
            new Dictionary<uint, EquipmentItemDefinition>(),
            new([new("jobs", SnapshotComponentStatus.Complete)]));
        var retainers = new Dictionary<ulong, CachedRetainer>
        {
            [9] = new()
            {
                RetainerId = 9,
                RetainerName = "Alice",
                LastUpdated = DateTime.UtcNow.AddHours(-3),
            },
        };

        var targets = new OutfitterTargetCatalog().Build(snapshot, retainers);

        Assert.Collection(
            targets,
            target =>
            {
                Assert.Equal(OutfitterTargetKind.Job, target.Kind);
                Assert.Equal("Gladiator", target.Name);
                Assert.True(target.IsReady);
            },
            target =>
            {
                Assert.Equal(OutfitterTargetKind.Gearset, target.Kind);
                Assert.Equal("Dungeon", target.Name);
            },
            target =>
            {
                Assert.Equal(OutfitterTargetKind.Retainer, target.Kind);
                Assert.Equal("Alice", target.Name);
                Assert.False(target.IsReady);
                Assert.Contains("worn equipment slots", target.Diagnostic);
            });
    }

    [Fact]
    public void Build_MergesAutoRetainerIdentityAndKeepsOwnerScopeExplicit()
    {
        var miner = new CharacterJobSnapshot(
            16,
            "MIN",
            "miner",
            5,
            false,
            null,
            "Gatherer",
            EquipmentStatSemantic.Gathering,
            EquipmentDiscipline.Gatherer);
        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(new(10, "Current Character", 57), 57, 16, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            [miner],
            [],
            [],
            new Dictionary<uint, EquipmentItemDefinition>(),
            new([new("jobs", SnapshotComponentStatus.Complete)]));
        var cache = new Dictionary<ulong, CachedRetainer>
        {
            [101] = new()
            {
                RetainerId = 101,
                RetainerName = "Current Retainer",
                OwnerCharacterName = "Current Character",
                OwnerHomeWorld = "Siren",
                LastUpdated = DateTime.UtcNow,
            },
            [303] = new()
            {
                RetainerId = 303,
                RetainerName = "Legacy Retainer",
                LastUpdated = DateTime.UtcNow.AddDays(-2),
            },
        };
        OutfitterRetainerMetadata[] metadata =
        [
            new(10, "Current Character", "Siren", 101, "Current Retainer", 16, 72),
            new(20, "Other Character", "Midgardsormr", 202, "Other Retainer", 16, 55),
        ];

        var targets = new OutfitterTargetCatalog().Build(snapshot, cache, metadata);

        var current = Assert.Single(targets, value => value.Key == "retainer:101");
        Assert.True(current.IsCurrentCharacter);
        Assert.Equal("MIN", current.Job?.Abbreviation);
        Assert.Equal((uint)72, current.Job?.Level);
        Assert.NotNull(current.Retainer);
        Assert.NotNull(current.RetainerMetadata);
        Assert.False(current.IsReady);
        Assert.Null(current.RetainerEquipmentEvidence);

        var other = Assert.Single(targets, value => value.Key == "retainer:202");
        Assert.False(other.IsCurrentCharacter);
        Assert.Equal("Other Character", other.OwnerCharacterName);
        Assert.Null(other.Retainer);
        Assert.Contains("no inventory snapshot", other.Diagnostic);

        var legacy = Assert.Single(targets, value => value.Key == "retainer:303");
        Assert.Null(legacy.OwnerCharacterName);
        Assert.Null(legacy.RetainerMetadata);

        var renderedEvidence = new RenderedRetainerEquipmentEvidence(
            RenderedRetainerEquipmentEvidenceStatus.Complete,
            "retainer:101",
            DateTimeOffset.UtcNow,
            "Current Character",
            "Siren",
            "Current Retainer",
            16,
            72,
            [],
            "synthetic complete evidence");
        var withBaseline = new OutfitterTargetCatalog().Build(
            snapshot,
            cache,
            metadata,
            new Dictionary<string, RenderedRetainerEquipmentEvidence>
            {
                [renderedEvidence.TargetKey] = renderedEvidence,
            });
        var observed = Assert.Single(withBaseline, value => value.Key == "retainer:101");
        Assert.Same(renderedEvidence, observed.RetainerEquipmentEvidence);
        Assert.False(observed.IsReady);
        Assert.Contains("outcome profile", observed.Diagnostic);
    }

    [Fact]
    public void Build_CollapsesUnlockedBaseClassIntoUpgradedJob()
    {
        var marauder = new CharacterJobSnapshot(
            3, "MRD", "marauder", 91, true, null, "Tank", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat);
        var warrior = new CharacterJobSnapshot(
            21, "WAR", "warrior", 91, true, 3, "Tank", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat);
        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(new(10, "Current Character", 57), 57, 21, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            [marauder, warrior],
            [],
            [],
            new Dictionary<uint, EquipmentItemDefinition>(),
            new([new("jobs", SnapshotComponentStatus.Complete)]));

        var targets = new OutfitterTargetCatalog().Build(snapshot, new Dictionary<ulong, CachedRetainer>());

        var job = Assert.Single(targets, target => target.Kind == OutfitterTargetKind.Job);
        Assert.Equal("WAR", job.Job?.Abbreviation);
    }
}
