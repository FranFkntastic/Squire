#if DEBUG
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterS4GoldenFixtureTests
{
    [Fact]
    public void Create_CrossesCraftAdvisorHandoffAndDryRunWorkbenchBoundaries()
    {
        var fixture = OutfitterS4GoldenFixture.Create();
        var repeated = OutfitterS4GoldenFixture.Create();

        Assert.Equal(PlayerAdvisorBaselineStatus.Complete, fixture.Baseline.Status);
        Assert.Equal(CrafterUtilityProfile.BlacksmithClassJobId, fixture.Baseline.ClassJobId);
        Assert.Equal((short)100, fixture.Baseline.Level);
        Assert.Equal(12, fixture.Baseline.EquippedSlots.Count);
        Assert.True(PlayerAdvisorBaselineAssembler.IsCompleteAndConsistent(
            fixture.Baseline,
            CrafterAdvisorStatFamily.Instance,
            out var baselineDiagnostic), baselineDiagnostic);
        Assert.Equal(OutfitterMarketEvidenceGenerationStatus.Complete, fixture.MarketEvidence.Status);
        Assert.True(fixture.MarketEvidence.IsPublishable);
        Assert.Equal(4, fixture.MarketEvidence.Items.Count);
        Assert.All(fixture.MarketEvidence.Items, item =>
        {
            Assert.Equal(OutfitterMarketEvidenceItemStatus.Fresh, item.Status);
            Assert.Single(item.Listings);
            Assert.True(item.Listings[0].ListingReviewedAtUtc <= item.CapturedAtUtc);
            Assert.True(item.CapturedAtUtc <= fixture.MarketEvidence.PublishedAtUtc);
        });

        Assert.Equal(MinerBotanistAdvisorStatus.Complete, fixture.Advice.Status);
        var nomination = Assert.IsType<EquipmentDecisionSolution>(fixture.Advice.Nomination);
        Assert.Equal(nomination.Candidate.SolutionId, fixture.SelectedCraftSolutionId);
        var nominatedOffers = nomination.Candidate.Selections
            .Select(selection => fixture.Advice.OffersByAllocation[selection.AllocationKey])
            .DistinctBy(offer => offer.AllocationKey)
            .ToArray();
        var craftOffer = Assert.Single(nominatedOffers, offer =>
            offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.Craft);
        var directMarketOffer = Assert.Single(fixture.Advice.OffersByAllocation.Values, offer =>
            offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard &&
            offer.Offer.Definition.Name == "Ceremonial Cross-pein Hammer");
        Assert.Equal(2_800ul, craftOffer.AcquisitionCostGil);
        Assert.Equal(50_000ul, directMarketOffer.AcquisitionCostGil);
        Assert.True(craftOffer.AcquisitionCostGil < directMarketOffer.AcquisitionCostGil);

        Assert.Equal(fixture.SelectedCraftSolutionId, fixture.CraftHandoff.SelectedSolutionId);
        Assert.Single(fixture.CraftHandoff.PlanIdentities);
        Assert.Collection(
            fixture.CraftHandoff.Recipes,
            subcraft =>
            {
                Assert.Equal("Ra'Kaznar Ingot", subcraft.ItemName);
                Assert.Equal(2u, subcraft.CraftCount);
                Assert.Equal(1, subcraft.Depth);
            },
            parent =>
            {
                Assert.Equal("Ceremonial Cross-pein Hammer", parent.ItemName);
                Assert.Equal(1u, parent.CraftCount);
                Assert.Equal(0, parent.Depth);
            });
        Assert.Empty(fixture.CraftHandoff.VendorMaterials);
        Assert.Equal(3, fixture.CraftHandoff.MarketMaterials.Count);
        Assert.Collection(
            fixture.CraftHandoff.Materials,
            lumber => AssertMaterial(lumber, "Claro Walnut Lumber", 1, 2, 1),
            powder => AssertMaterial(powder, "Magnesia Powder", 2, 3, 1),
            ore => AssertMaterial(ore, "Ra'Kaznar Ore", 10, 12, 2));

        var dryRunValidation = OutfitterWorkbenchPlayerValidation.CreateDryRun(
            fixture.Advice,
            fixture.SelectedCraftSolutionId,
            fixture.MarketEvidence) with
        {
            RecapturedBaseline = fixture.Baseline,
        };
        var transfer = OutfitterWorkbenchTransferBuilder.Build(
            fixture.Advice,
            fixture.SelectedCraftSolutionId,
            fixture.MarketEvidence,
            dryRunValidation,
            fixture.TimeProvider);

        Assert.True(transfer.DryRunOnly);
        Assert.Equal(2_800ul, transfer.ObservedMarketTotalGil);
        Assert.Collection(
            transfer.MarketLots,
            lumber => AssertLot(lumber, "Claro Walnut Lumber", 2, 500),
            powder => AssertLot(powder, "Magnesia Powder", 3, 200),
            ore => AssertLot(ore, "Ra'Kaznar Ore", 12, 100));
        Assert.All(transfer.MarketLots, lot => Assert.Equal("Crafting material", lot.ItemKind));
        Assert.DoesNotContain(transfer.MarketLots, lot => lot.ItemName == "Ceremonial Cross-pein Hammer");
        Assert.DoesNotContain(fixture.Baseline.EquippedSlots,
            slot => slot.Definition?.Name == "Ceremonial Cross-pein Hammer");

        Assert.Equal(fixture.SelectedCraftSolutionId, repeated.SelectedCraftSolutionId);
        Assert.Equal(fixture.CraftHandoff.PlanIdentities, repeated.CraftHandoff.PlanIdentities);
        Assert.Equal(fixture.TimeProvider.GetUtcNow(), repeated.TimeProvider.GetUtcNow());
    }

    private static void AssertMaterial(
        OutfitterCraftHandoffMaterial material,
        string itemName,
        uint consumed,
        uint purchased,
        uint surplus)
    {
        Assert.Equal(itemName, material.ItemName);
        Assert.Equal(EquipmentQuality.Normal, material.Quality);
        Assert.Equal(consumed, material.ConsumedQuantity);
        Assert.Equal(purchased, material.PurchasedQuantity);
        Assert.Equal(surplus, material.SurplusQuantity);
        Assert.IsType<OutfitterMarketMaterialSourceIdentity>(material.Source);
    }

    private static void AssertLot(
        OutfitterWorkbenchMarketLot lot,
        string itemName,
        uint quantity,
        uint unitPriceGil)
    {
        Assert.Equal(itemName, lot.ItemName);
        Assert.Equal(quantity, lot.RequiredQuantity);
        Assert.Equal(quantity, lot.ObservedAvailableQuantity);
        Assert.Equal(unitPriceGil, lot.ObservedUnitPriceGil);
        Assert.Equal((ulong)quantity * unitPriceGil, lot.ObservedTotalPriceGil);
    }
}
#endif
