using Dalamud.Game.Player;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class PlayerAdvisorBaselineTests
{
    private static readonly CharacterScope Scope = new(77, "Advisor", 21);
    private static readonly PlayerAdvisorCaptureHeader Header = new(
        Scope,
        CurrentWorldId: 22,
        ClassJobId: MinerBotanistUtilityProfile.MinerClassJobId,
        Level: 90,
        EffectiveLevel: 90,
        IsLevelSynced: false);

    [Fact]
    public void Equipped_index_map_has_all_twelve_positions_and_distinct_rings()
    {
        var expected = new (int Index, EquipmentLoadoutPosition Position, string Key)[]
        {
            (0, EquipmentLoadoutPosition.MainHand, "main-hand"),
            (1, EquipmentLoadoutPosition.OffHand, "off-hand"),
            (2, EquipmentLoadoutPosition.Head, "head"),
            (3, EquipmentLoadoutPosition.Body, "body"),
            (4, EquipmentLoadoutPosition.Hands, "hands"),
            (6, EquipmentLoadoutPosition.Legs, "legs"),
            (7, EquipmentLoadoutPosition.Feet, "feet"),
            (8, EquipmentLoadoutPosition.Ears, "ears"),
            (9, EquipmentLoadoutPosition.Neck, "neck"),
            (10, EquipmentLoadoutPosition.Wrists, "wrists"),
            (11, EquipmentLoadoutPosition.RightRing, "ring-right"),
            (12, EquipmentLoadoutPosition.LeftRing, "ring-left"),
        };

        Assert.Equal(expected, PlayerAdvisorEquippedSlotMap.All.Select(value =>
            (value.EquippedIndex, value.Position, value.PositionKey)));
        Assert.DoesNotContain(PlayerAdvisorEquippedSlotMap.All, value => value.EquippedIndex == 5);
        Assert.NotEqual(PlayerAdvisorEquippedSlotMap.Find(11)!.Position, PlayerAdvisorEquippedSlotMap.Find(12)!.Position);
    }

    [Fact]
    public void Family_semantic_vectors_preserve_gatherer_and_crafter_component_meaning()
    {
        var gatherer = GathererAdvisorStatFamily.Instance.VectorFromSemantics(new Dictionary<EquipmentStatSemantic, int>
        {
            [EquipmentStatSemantic.Gathering] = 501,
            [EquipmentStatSemantic.Perception] = 402,
            [EquipmentStatSemantic.GatheringPoints] = 33,
            [EquipmentStatSemantic.Control] = 999,
        });
        var crafter = CrafterAdvisorStatFamily.Instance.VectorFromSemantics(new Dictionary<EquipmentStatSemantic, int>
        {
            [EquipmentStatSemantic.Craftsmanship] = 601,
            [EquipmentStatSemantic.Control] = 502,
            [EquipmentStatSemantic.CraftingPoints] = 44,
            [EquipmentStatSemantic.Gathering] = 999,
        });

        Assert.Equal(501, gatherer.Get("gathering"));
        Assert.Equal(402, gatherer.Get("perception"));
        Assert.Equal(33, gatherer.Get("gathering-points"));
        Assert.Equal(0, gatherer.Get("control"));
        Assert.Equal(601, crafter.Get("craftsmanship"));
        Assert.Equal(502, crafter.Get("control"));
        Assert.Equal(44, crafter.Get("crafting-points"));
        Assert.Equal(0, crafter.Get("gathering"));
    }

    [Fact]
    public void Assemble_reconciles_exact_equipped_contributions_and_ignores_unrelated_partial_components()
    {
        var snapshot = Snapshot();
        var result = PlayerAdvisorBaselineAssembler.Assemble(
            snapshot,
            Header,
            GathererAdvisorStatFamily.Instance,
            Totals(gathering: 1_000, perception: 900, gp: 600),
            Captures());

        Assert.Equal(PlayerAdvisorBaselineStatus.Complete, result.Status);
        Assert.Equal(880, result.FixedStats[EquipmentStatSemantic.Gathering]);
        Assert.Equal(660, result.FixedStats[EquipmentStatSemantic.Perception]);
        Assert.Equal(588, result.FixedStats[EquipmentStatSemantic.GatheringPoints]);
        Assert.Equal(12, result.EquippedSlots.Count);
        Assert.Equal(10, result.EquippedSlots[0].Utility.Get("gathering"));
        Assert.Same(snapshot, result.EquipmentSnapshot);
        Assert.Same(snapshot.Instances.Single(value => value.Fingerprint.SlotIndex == 0), result.EquippedSlots[0].Instance);
    }

    [Fact]
    public void Assemble_rejects_negative_fixed_remainder()
    {
        var result = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(),
            Header,
            GathererAdvisorStatFamily.Instance,
            Totals(gathering: 119, perception: 900, gp: 600),
            Captures());

        Assert.Equal(PlayerAdvisorBaselineStatus.Inconsistent, result.Status);
        Assert.Contains("exceed", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assemble_reports_unsupported_job_family()
    {
        var result = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(),
            Header with { ClassJobId = 19 },
            family: null,
            new Dictionary<EquipmentStatSemantic, int>(),
            []);

        Assert.Equal(PlayerAdvisorBaselineStatus.Unsupported, result.Status);
        Assert.Equal(19u, result.ClassJobId);
    }

    [Fact]
    public void Assemble_reports_fisher_as_terminally_unsupported_and_out_of_scope()
    {
        var result = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(),
            Header with { ClassJobId = AdvisorStatFamilies.FisherClassJobId },
            family: null,
            new Dictionary<EquipmentStatSemantic, int>(),
            []);

        Assert.Equal(PlayerAdvisorBaselineStatus.Unsupported, result.Status);
        Assert.Equal("Fisher is permanently unsupported and out of scope for Squire Outfitter.", result.Diagnostic);
        Assert.DoesNotContain("yet", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assemble_accepts_authoritative_empty_slot_as_zero_stat_baseline()
    {
        var result = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(emptyIndex: 1),
            Header,
            GathererAdvisorStatFamily.Instance,
            Totals(1_000, 900, 600),
            Captures(emptyIndex: 1));

        Assert.Equal(PlayerAdvisorBaselineStatus.Complete, result.Status);
        var offHand = Assert.Single(result.EquippedSlots, value => value.Position == EquipmentLoadoutPosition.OffHand);
        Assert.Null(offHand.Definition);
        Assert.Null(offHand.Instance);
        Assert.Equal(0, offHand.Utility.Get("gathering"));
    }

    [Fact]
    public void Assemble_abstains_while_level_sync_is_active()
    {
        var result = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(),
            Header with { EffectiveLevel = 80, IsLevelSynced = true },
            GathererAdvisorStatFamily.Instance,
            Totals(1_000, 900, 600),
            Captures());

        Assert.Equal(PlayerAdvisorBaselineStatus.Unsupported, result.Status);
        Assert.Contains("level sync", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assemble_requires_every_explicit_equipped_slot()
    {
        var captures = Captures().Where(value => value.EquippedIndex != 12).ToArray();

        var result = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(),
            Header,
            GathererAdvisorStatFamily.Instance,
            Totals(1_000, 900, 600),
            captures);

        Assert.Equal(PlayerAdvisorBaselineStatus.Incomplete, result.Status);
        Assert.Contains("twelve", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assemble_requires_definition_for_every_current_equipped_item()
    {
        var result = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(missingDefinitionIndex: 12),
            Header,
            GathererAdvisorStatFamily.Instance,
            Totals(1_000, 900, 600),
            Captures());

        Assert.Equal(PlayerAdvisorBaselineStatus.Incomplete, result.Status);
        Assert.Contains("definition", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assemble_rejects_definition_mapped_to_wrong_position()
    {
        var result = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(mismatchedDefinitionIndex: 0),
            Header,
            GathererAdvisorStatFamily.Instance,
            Totals(1_000, 900, 600),
            Captures());

        Assert.Equal(PlayerAdvisorBaselineStatus.Inconsistent, result.Status);
        Assert.Contains("main-hand", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assemble_rejects_definition_ineligible_for_active_job()
    {
        var result = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(ineligibleDefinitionIndex: 0),
            Header,
            GathererAdvisorStatFamily.Instance,
            Totals(1_000, 900, 600),
            Captures());

        Assert.Equal(PlayerAdvisorBaselineStatus.Inconsistent, result.Status);
        Assert.Contains("not eligible", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assemble_rejects_definition_above_actual_level()
    {
        var result = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(overLevelDefinitionIndex: 0),
            Header,
            GathererAdvisorStatFamily.Instance,
            Totals(1_000, 900, 600),
            Captures());

        Assert.Equal(PlayerAdvisorBaselineStatus.Inconsistent, result.Status);
        Assert.Contains("actual level", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Authority_fingerprint_changes_for_materia_and_relevant_totals()
    {
        var baseline = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(),
            Header,
            GathererAdvisorStatFamily.Instance,
            Totals(1_000, 900, 600),
            Captures());
        var original = PlayerAdvisorAuthorityFingerprint.Capture(baseline);
        var first = baseline.EquippedSlots[0] with { MateriaIds = [123], MateriaGrades = [4] };
        var meldChanged = baseline with { EquippedSlots = [first, .. baseline.EquippedSlots.Skip(1)] };
        var totalsChanged = baseline with
        {
            TotalStats = new Dictionary<EquipmentStatSemantic, int>(baseline.TotalStats)
            {
                [EquipmentStatSemantic.Gathering] = baseline.TotalStats[EquipmentStatSemantic.Gathering] + 1,
            },
        };

        Assert.NotEqual(original, PlayerAdvisorAuthorityFingerprint.Capture(meldChanged));
        Assert.NotEqual(original, PlayerAdvisorAuthorityFingerprint.Capture(totalsChanged));
    }

    [Fact]
    public void Authority_fingerprint_ignores_capture_generation_and_cosmetics()
    {
        var baseline = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(),
            Header,
            GathererAdvisorStatFamily.Instance,
            Totals(1_000, 900, 600),
            Captures());
        var original = PlayerAdvisorAuthorityFingerprint.Capture(baseline);
        var changedSnapshot = baseline.EquipmentSnapshot! with { GenerationId = Guid.NewGuid() };
        var changedInstance = baseline.EquippedSlots[0].Instance! with
        {
            Fingerprint = baseline.EquippedSlots[0].Instance!.Fingerprint with
            {
                Condition = 1,
                GlamourId = 999,
                Stains = [1, 2],
            },
        };
        var changedSlot = baseline.EquippedSlots[0] with { Instance = changedInstance };
        var cosmeticChanged = baseline with
        {
            EquipmentSnapshot = changedSnapshot,
            EquippedSlots = [changedSlot, .. baseline.EquippedSlots.Skip(1)],
        };

        Assert.Equal(original, PlayerAdvisorAuthorityFingerprint.Capture(cosmeticChanged));
    }

    [Fact]
    public void Authority_fingerprint_tracks_saved_gearset_identity()
    {
        var baseline = PlayerAdvisorBaselineAssembler.Assemble(
            Snapshot(),
            Header,
            GathererAdvisorStatFamily.Instance,
            Totals(1_000, 900, 600),
            Captures());
        var first = baseline with
        {
            Target = new(PlayerAdvisorBaselineTargetKind.SavedGearset, "gearset:4", "fingerprint-a"),
        };
        var changed = first with
        {
            Target = new(PlayerAdvisorBaselineTargetKind.SavedGearset, "gearset:4", "fingerprint-b"),
        };

        Assert.NotEqual(
            PlayerAdvisorAuthorityFingerprint.Capture(first),
            PlayerAdvisorAuthorityFingerprint.Capture(changed));
    }

    [Theory]
    [InlineData(EquipmentStatSemantic.Gathering, PlayerAttribute.Gathering)]
    [InlineData(EquipmentStatSemantic.CraftingPoints, PlayerAttribute.CraftingPoints)]
    [InlineData(EquipmentStatSemantic.DirectHit, PlayerAttribute.DirectHitRate)]
    [InlineData(EquipmentStatSemantic.MagicalDamage, PlayerAttribute.MagicDamage)]
    [InlineData(EquipmentStatSemantic.Dexterity, PlayerAttribute.Dexterity)]
    [InlineData(EquipmentStatSemantic.Vitality, PlayerAttribute.Vitality)]
    [InlineData(EquipmentStatSemantic.PhysicalDamage, PlayerAttribute.PhysicalDamage)]
    [InlineData(EquipmentStatSemantic.PhysicalDefense, PlayerAttribute.Defense)]
    [InlineData(EquipmentStatSemantic.MagicalDefense, PlayerAttribute.MagicDefense)]
    public void Player_attribute_mapping_is_explicit(
        EquipmentStatSemantic semantic,
        PlayerAttribute expected)
    {
        Assert.True(DalamudPlayerAdvisorBaselineSource.TryMapPlayerAttribute(semantic, out var actual));
        Assert.Equal(expected, actual);
        Assert.False(DalamudPlayerAdvisorBaselineSource.TryMapPlayerAttribute(EquipmentStatSemantic.Unknown, out _));
    }

    [Fact]
    public void Item_contribution_mapping_uses_stable_base_parameter_rows()
    {
        var rows = new (uint RowId, string? Name)[]
        {
            (70, "Localized craftsmanship"),
            (71, "Localized control"),
            (11, "Localized crafting points"),
        };

        var resolved = DalamudPlayerAdvisorBaselineSource.TryResolveBaseParamIds(
            rows,
            [EquipmentStatSemantic.Craftsmanship, EquipmentStatSemantic.Control, EquipmentStatSemantic.CraftingPoints],
            out var ids,
            out var diagnostic);

        Assert.True(resolved, diagnostic);
        Assert.Equal(70u, ids[EquipmentStatSemantic.Craftsmanship]);
        Assert.Equal(71u, ids[EquipmentStatSemantic.Control]);
        Assert.Equal(11u, ids[EquipmentStatSemantic.CraftingPoints]);
    }

    [Fact]
    public void Item_contribution_mapping_rejects_unsupported_base_parameter_rows()
    {
        var resolved = DalamudPlayerAdvisorBaselineSource.TryResolveBaseParamIds(
            [(1u, "Control"), (2u, "Control")],
            [EquipmentStatSemantic.Control],
            out var ids,
            out var diagnostic);

        Assert.False(resolved);
        Assert.Empty(ids);
        Assert.Contains("no row", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_native_value_can_use_complete_static_stat_when_materia_target_other_stats()
    {
        var profile = new EquipmentStatProfile(
            [
                new(70, EquipmentStatSemantic.Craftsmanship, 1_720, false, "Craftsmanship"),
                new(71, EquipmentStatSemantic.Control, 900, false, "Control"),
            ],
            0,
            0,
            0,
            0,
            true);

        var resolved = DalamudPlayerAdvisorBaselineSource.TryGetStaticUnmeldedContribution(
            profile,
            EquipmentStatSemantic.Craftsmanship,
            70,
            [71, 11],
            out var value);

        Assert.True(resolved);
        Assert.Equal(1_720, value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Static_stat_fallback_rejects_same_stat_or_unresolved_materia(bool sameStatMateria)
    {
        var profile = new EquipmentStatProfile(
            [new(70, EquipmentStatSemantic.Craftsmanship, 1_720, false, "Craftsmanship")],
            0,
            0,
            0,
            0,
            true);

        var resolved = DalamudPlayerAdvisorBaselineSource.TryGetStaticUnmeldedContribution(
            profile,
            EquipmentStatSemantic.Craftsmanship,
            70,
            sameStatMateria ? [70] : null,
            out _);

        Assert.False(resolved);
    }

    private static IReadOnlyDictionary<EquipmentStatSemantic, int> Totals(int gathering, int perception, int gp) =>
        new Dictionary<EquipmentStatSemantic, int>
        {
            [EquipmentStatSemantic.Gathering] = gathering,
            [EquipmentStatSemantic.Perception] = perception,
            [EquipmentStatSemantic.GatheringPoints] = gp,
        };

    private static IReadOnlyList<PlayerAdvisorEquippedItemCapture> Captures(int? emptyIndex = null) =>
        PlayerAdvisorEquippedSlotMap.All.Select(position =>
        {
            var empty = position.EquippedIndex == emptyIndex;
            return new PlayerAdvisorEquippedItemCapture(
                position.EquippedIndex,
                empty ? 0 : ItemId(position.EquippedIndex),
                EquipmentQuality.Normal,
                new Dictionary<EquipmentStatSemantic, int>
                {
                    [EquipmentStatSemantic.Gathering] = empty ? 0 : 10,
                    [EquipmentStatSemantic.Perception] = empty ? 0 : 20,
                    [EquipmentStatSemantic.GatheringPoints] = empty ? 0 : 1,
                },
                [],
                []);
        }).ToArray();

    private static CharacterEquipmentSnapshot Snapshot(
        int? missingDefinitionIndex = null,
        int? mismatchedDefinitionIndex = null,
        int? ineligibleDefinitionIndex = null,
        int? overLevelDefinitionIndex = null,
        int? emptyIndex = null)
    {
        var instances = PlayerAdvisorEquippedSlotMap.All
            .Where(position => position.EquippedIndex != emptyIndex)
            .Select(position =>
            new EquipmentInstanceSnapshot(
                new EquipmentInstanceFingerprint(
                    Scope,
                    "EquippedItems",
                    position.EquippedIndex,
                    ItemId(position.EquippedIndex),
                    false,
                    1,
                    30_000,
                    0,
                    null,
                    [],
                    null,
                    []),
                DateTimeOffset.UtcNow,
                true)).ToArray();
        var definitions = PlayerAdvisorEquippedSlotMap.All
            .Where(position => position.EquippedIndex != missingDefinitionIndex && position.EquippedIndex != emptyIndex)
            .Select(position => Definition(
                ItemId(position.EquippedIndex),
                position.EquippedIndex == mismatchedDefinitionIndex ? EquipmentSlot.Body : SlotFor(position.Position),
                eligible: position.EquippedIndex != ineligibleDefinitionIndex,
                equipLevel: position.EquippedIndex == overLevelDefinitionIndex ? 91u : 1u))
            .ToDictionary(value => value.ItemId);
        return new(
            Guid.NewGuid(),
            new CharacterIdentitySnapshot(
                Scope,
                Header.CurrentWorldId,
                Header.ClassJobId,
                DateTimeOffset.UtcNow,
                true,
                SnapshotComponentStatus.Complete),
            [],
            [],
            instances,
            definitions,
            new CharacterEquipmentSnapshotDiagnostics(
            [
                new("identity", SnapshotComponentStatus.Complete),
                new("equipped", SnapshotComponentStatus.Complete),
                new("armoury", SnapshotComponentStatus.Partial, "Not required for the baseline."),
                new("inventory", SnapshotComponentStatus.Partial, "Not required for the baseline."),
                new("jobs", SnapshotComponentStatus.Partial, "Not required for the baseline."),
                new("gearsets", SnapshotComponentStatus.Partial, "Not required for the baseline."),
                new("definitions", missingDefinitionIndex is null ? SnapshotComponentStatus.Complete : SnapshotComponentStatus.Partial),
            ]));
    }

    private static EquipmentItemDefinition Definition(
        uint itemId,
        EquipmentSlot slot,
        bool eligible,
        uint equipLevel) =>
        new(
            itemId,
            $"Item {itemId}",
            equipLevel,
            1,
            slot,
            eligible ? new HashSet<uint> { Header.ClassJobId } : new HashSet<uint> { 19 },
            1,
            true,
            false,
            true,
            true,
            1,
            true,
            false,
            true,
            false);

    private static EquipmentSlot SlotFor(EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => EquipmentSlot.MainHand,
        EquipmentLoadoutPosition.OffHand => EquipmentSlot.OffHand,
        EquipmentLoadoutPosition.Head => EquipmentSlot.Head,
        EquipmentLoadoutPosition.Body => EquipmentSlot.Body,
        EquipmentLoadoutPosition.Hands => EquipmentSlot.Hands,
        EquipmentLoadoutPosition.Legs => EquipmentSlot.Legs,
        EquipmentLoadoutPosition.Feet => EquipmentSlot.Feet,
        EquipmentLoadoutPosition.Ears => EquipmentSlot.Ears,
        EquipmentLoadoutPosition.Neck => EquipmentSlot.Neck,
        EquipmentLoadoutPosition.Wrists => EquipmentSlot.Wrists,
        EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing => EquipmentSlot.Ring,
        _ => throw new ArgumentOutOfRangeException(nameof(position)),
    };

    private static uint ItemId(int equippedIndex) => checked((uint)(10_000 + equippedIndex));
}
