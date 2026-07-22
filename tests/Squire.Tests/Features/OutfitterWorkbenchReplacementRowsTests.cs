using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.MarketEvidence;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterWorkbenchReplacementRowsTests
{
    [Fact]
    public void Enumerate_ReturnsOnlyOtherExactQualityRowsWithUnsignedFactsAndSignedCostDeltas()
    {
        var fixture = Fixture();

        var rows = OutfitterWorkbenchReplacementRows.Enumerate(fixture.Transfer, fixture.CurrentEvidence);

        Assert.Collection(
            rows,
            sameWorld =>
            {
                Assert.Equal("same-world", sameWorld.Listing.ListingId);
                Assert.True(sameWorld.IsSameWorld);
                Assert.False(sameWorld.CanFulfillRequiredQuantityAlone);
                Assert.Equal(20, sameWorld.UnitPriceDeltaGil);
                Assert.Equal((ulong)240, sameWorld.RequiredQuantityCostGil);
                Assert.Equal(40m, sameWorld.RequiredQuantityCostDeltaGil);
            },
            crossWorld =>
            {
                Assert.Equal("cross-world", crossWorld.Listing.ListingId);
                Assert.False(crossWorld.IsSameWorld);
                Assert.True(crossWorld.CanFulfillRequiredQuantityAlone);
                Assert.Equal(-10, crossWorld.UnitPriceDeltaGil);
                Assert.Equal((ulong)180, crossWorld.RequiredQuantityCostGil);
                Assert.Equal(-20m, crossWorld.RequiredQuantityCostDeltaGil);
            });
        Assert.DoesNotContain(rows, row => row.Listing.ListingId is "accepted" or "nq");
    }

    [Fact]
    public void Enumerate_RejectsRefreshThatDoesNotCoverTheAcceptedScope()
    {
        var fixture = Fixture();
        var unrelated = fixture.CurrentEvidence with { Region = "Europe" };

        Assert.Throws<InvalidOperationException>(() =>
            OutfitterWorkbenchReplacementRows.Enumerate(fixture.Transfer, unrelated));
    }

    private static FixtureData Fixture()
    {
        var acceptedGeneration = Guid.NewGuid();
        var currentGeneration = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 17, 14, 0, 0, TimeSpan.Zero);
        var key = new EquipmentOfferKey(
            10,
            EquipmentQuality.High,
            EquipmentAcquisitionSourceKind.MarketBoard,
            "market:test:10:High");
        var transfer = new OutfitterWorkbenchTransfer(
            OutfitterWorkbenchTransfer.CurrentSchemaVersion,
            OutfitterWorkbenchTransfer.SquireOutfitterOrigin,
            "selected",
            null,
            new("min-btn", "1"),
            new("ordinary", 16, 100, "Ordinary resource nodes", []),
            new(
                acceptedGeneration,
                7,
                OutfitterMarketEvidenceBook.CurrentSchemaVersion,
                "universalis",
                "North America",
                OutfitterMarketCoverageMode.ExhaustiveWithinScope,
                now),
            [new(EquipmentLoadoutPosition.LeftRing, key, 2, "accepted", "Market board - Siren")],
            [new(key, "Exact HQ Ring", 2, 2, "Siren", 100, 200, "accepted", "source-r1", now)],
            200);
        var current = new OutfitterMarketEvidenceBook(
            currentGeneration,
            8,
            OutfitterMarketEvidenceBook.CurrentSchemaVersion,
            "universalis",
            "North America",
            now.AddMinutes(1),
            now.AddMinutes(1),
            OutfitterMarketEvidenceGenerationStatus.Complete,
            new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 1, 1, 100, [10]),
            [
                new(
                    10,
                    OutfitterMarketEvidenceItemStatus.Fresh,
                    [
                        Listing("accepted", EquipmentQuality.High, "Siren", 2, 100, now),
                        Listing("same-world", EquipmentQuality.High, "Siren", 1, 120, now),
                        Listing("cross-world", EquipmentQuality.High, "Gilgamesh", 2, 90, now),
                        Listing("nq", EquipmentQuality.Normal, "Siren", 2, 50, now),
                    ],
                    now,
                    "source-r2"),
            ]);
        return new(transfer, current);
    }

    private static OutfitterMarketListingEvidence Listing(
        string listingId,
        EquipmentQuality quality,
        string world,
        uint quantity,
        uint unitPrice,
        DateTimeOffset now) =>
        new(10, quality, listingId, world, 1, "Retainer", listingId, quantity, unitPrice, now, now, "source-r2");

    private sealed record FixtureData(
        OutfitterWorkbenchTransfer Transfer,
        OutfitterMarketEvidenceBook CurrentEvidence);
}
