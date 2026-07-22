using System;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedRetainerEquipmentEvidenceAssemblerTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 7, 18, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Assemble_refuses_metadata_without_a_complete_rendered_identity()
    {
        var identity = Identity(RenderedRetainerIdentityStatus.Unavailable);

        var result = RenderedRetainerEquipmentEvidenceAssembler.Assemble(Target(), identity, CompleteScan());

        Assert.Equal(RenderedRetainerEquipmentEvidenceStatus.Unavailable, result.Status);
        Assert.Empty(result.Equipment);
    }

    [Theory]
    [InlineData("Other Owner", "Gilgamesh", "Venture", 18u, 100u)]
    [InlineData("Fran Example", "Cactuar", "Venture", 18u, 100u)]
    [InlineData("Fran Example", "Gilgamesh", "Other Retainer", 18u, 100u)]
    [InlineData("Fran Example", "Gilgamesh", "Venture", 17u, 100u)]
    [InlineData("Fran Example", "Gilgamesh", "Venture", 18u, 99u)]
    public void Assemble_refuses_any_rendered_identity_mismatch(
        string owner,
        string world,
        string retainer,
        uint classJobId,
        uint level)
    {
        var identity = Identity() with
        {
            OwnerCharacterName = owner,
            OwnerHomeWorld = world,
            RetainerName = retainer,
            ClassJobId = classJobId,
            Level = level,
        };

        var result = RenderedRetainerEquipmentEvidenceAssembler.Assemble(Target(), identity, CompleteScan());

        Assert.Equal(RenderedRetainerEquipmentEvidenceStatus.IdentityMismatch, result.Status);
        Assert.Empty(result.Equipment);
    }

    [Fact]
    public void Assemble_refuses_an_incomplete_or_duplicate_slot_scan()
    {
        var observation = Equipped("main-hand", EquipmentSlot.MainHand);
        var scan = new RenderedRetainerEquipmentScanObservation(
            RenderedEquipmentScanStatus.Complete,
            CapturedAt,
            "Fran Example",
            "Gilgamesh",
            "Venture",
            CompletedSlots: 2,
            TotalSlots: 2,
            Observations: [observation, observation],
            Diagnostic: "synthetic duplicate");

        var result = RenderedRetainerEquipmentEvidenceAssembler.Assemble(Target(), Identity(), scan);

        Assert.Equal(RenderedRetainerEquipmentEvidenceStatus.Incomplete, result.Status);
        Assert.Empty(result.Equipment);
    }

    [Fact]
    public void Assemble_refuses_equipment_captured_from_another_retainer_context()
    {
        var scan = CompleteScan() with { RetainerName = "Someone Else" };

        var result = RenderedRetainerEquipmentEvidenceAssembler.Assemble(Target(), Identity(), scan);

        Assert.Equal(RenderedRetainerEquipmentEvidenceStatus.Incomplete, result.Status);
        Assert.Empty(result.Equipment);
    }

    [Fact]
    public void Assemble_binds_complete_rendered_identity_and_equipment_to_the_selected_target()
    {
        var result = RenderedRetainerEquipmentEvidenceAssembler.Assemble(Target(), Identity(), CompleteScan());

        Assert.Equal(RenderedRetainerEquipmentEvidenceStatus.Complete, result.Status);
        Assert.Equal("retainer:42", result.TargetKey);
        Assert.Equal("Fran Example", result.OwnerCharacterName);
        Assert.Equal("Venture", result.RetainerName);
        Assert.Equal(2, result.Equipment.Count);
    }

    private static OutfitterTarget Target() => new(
        "retainer:42",
        OutfitterTargetKind.Retainer,
        "Venture",
        "MIN · Lv. 100",
        RetainerMetadata: new(1, "Fran Example", "Gilgamesh", 42, "Venture", 18, 100),
        OwnerCharacterName: "Fran Example",
        OwnerHomeWorld: "Gilgamesh",
        IsReady: false);

    private static RenderedRetainerIdentityObservation Identity(
        RenderedRetainerIdentityStatus status = RenderedRetainerIdentityStatus.Complete) =>
        new(status, CapturedAt, "Fran Example", "Gilgamesh", "Venture", 18, 100, "synthetic rendered identity");

    private static RenderedRetainerEquipmentScanObservation CompleteScan() => new(
        RenderedEquipmentScanStatus.Complete,
        CapturedAt,
        "Fran Example",
        "Gilgamesh",
        "Venture",
        CompletedSlots: 2,
        TotalSlots: 2,
        Observations:
        [
            Equipped("main-hand", EquipmentSlot.MainHand),
            Equipped("head", EquipmentSlot.Head),
        ],
        Diagnostic: "synthetic complete scan");

    private static RenderedEquipmentSlotObservation Equipped(string key, EquipmentSlot slot) => new(
        key,
        slot,
        RenderedEquipmentSlotObservationStatus.Equipped,
        new RenderedItemDetailObservation(
            RenderedItemDetailStatus.Complete,
            "Synthetic Gear",
            RenderedItemQuality.Normal,
            ItemLevel: 1,
            EquipLevel: 1,
            JobCategory: "All Classes",
            SlotCategory: null,
            Stats: new System.Collections.Generic.Dictionary<string, int>(),
            MateriaStats: new System.Collections.Generic.Dictionary<string, int>(),
            Diagnostic: "synthetic rendered tooltip"));
}
