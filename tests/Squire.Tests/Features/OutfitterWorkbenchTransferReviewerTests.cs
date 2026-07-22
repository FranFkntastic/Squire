using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.MarketEvidence;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterWorkbenchTransferReviewerTests
{
    [Fact]
    public void Review_ReportsNoChangeForTheAcceptedPublishedTuple()
    {
        var fixture = Fixture();

        var review = OutfitterWorkbenchTransferReviewer.Review(fixture.Transfer, fixture.Evidence);

        var lot = Assert.Single(review.Lots);
        Assert.Equal(OutfitterWorkbenchLotChange.None, lot.Changes);
        Assert.NotNull(lot.CurrentListing);
    }

    [Fact]
    public void Review_ClassifiesPriceIncreaseAndQuantityShortfallWithoutAcceptingThem()
    {
        var fixture = Fixture();
        var next = Evidence(
            Guid.NewGuid(),
            8,
            fixture.Now.AddMinutes(1),
            [Listing(EquipmentQuality.High, "Siren", 1, 125, fixture.Now.AddMinutes(1), "source-r2")]);

        var review = OutfitterWorkbenchTransferReviewer.Review(fixture.Transfer, next);

        var changes = Assert.Single(review.Lots).Changes;
        Assert.True(changes.HasFlag(OutfitterWorkbenchLotChange.EvidenceGenerationChanged));
        Assert.True(changes.HasFlag(OutfitterWorkbenchLotChange.EvidenceRevisionChanged));
        Assert.True(changes.HasFlag(OutfitterWorkbenchLotChange.UnitPriceIncreased));
        Assert.True(changes.HasFlag(OutfitterWorkbenchLotChange.AvailableQuantityDecreased));
        Assert.True(changes.HasFlag(OutfitterWorkbenchLotChange.RequiredQuantityUnavailable));
        Assert.True(changes.HasFlag(OutfitterWorkbenchLotChange.SourceRevisionChanged));
    }

    [Fact]
    public void Review_ClassifiesMissingListing()
    {
        var fixture = Fixture();
        var next = Evidence(Guid.NewGuid(), 8, fixture.Now.AddMinutes(1), []);

        var review = OutfitterWorkbenchTransferReviewer.Review(fixture.Transfer, next);

        var lot = Assert.Single(review.Lots);
        Assert.Null(lot.CurrentListing);
        Assert.True(lot.Changes.HasFlag(OutfitterWorkbenchLotChange.ListingMissing));
    }

    [Fact]
    public void Review_ClassifiesExactQualityPriceAndQuantityWithinAcceptedWorld()
    {
        var fixture = Fixture();
        var next = Evidence(
            fixture.GenerationId,
            7,
            fixture.Now.AddMinutes(1),
            [Listing(EquipmentQuality.Normal, "Siren", 3, 90, fixture.Now.AddMinutes(1), "source-r1")]);

        var review = OutfitterWorkbenchTransferReviewer.Review(fixture.Transfer, next);

        var changes = Assert.Single(review.Lots).Changes;
        Assert.True(changes.HasFlag(OutfitterWorkbenchLotChange.ExactQualityChanged));
        Assert.False(changes.HasFlag(OutfitterWorkbenchLotChange.WorldChanged));
        Assert.True(changes.HasFlag(OutfitterWorkbenchLotChange.UnitPriceDecreased));
        Assert.True(changes.HasFlag(OutfitterWorkbenchLotChange.AvailableQuantityIncreased));
        Assert.False(changes.HasFlag(OutfitterWorkbenchLotChange.RequiredQuantityUnavailable));
    }

    [Fact]
    public void Review_TreatsSameRawListingIdOnAnotherWorldAsDifferentListing()
    {
        var fixture = Fixture();
        var next = Evidence(
            fixture.GenerationId,
            7,
            fixture.Now.AddMinutes(1),
            [Listing(EquipmentQuality.High, "Gilgamesh", 2, 100, fixture.Now.AddMinutes(1), "source-r1")]);

        var review = OutfitterWorkbenchTransferReviewer.Review(fixture.Transfer, next);

        var lot = Assert.Single(review.Lots);
        Assert.Null(lot.CurrentListing);
        Assert.True(lot.Changes.HasFlag(OutfitterWorkbenchLotChange.ListingMissing));
        Assert.False(lot.Changes.HasFlag(OutfitterWorkbenchLotChange.WorldChanged));
    }

    [Fact]
    public void Review_RejectsEvidenceFromAnotherScope()
    {
        var fixture = Fixture();
        var unrelated = fixture.Evidence with { Region = "Europe" };

        var error = Assert.Throws<InvalidOperationException>(() =>
            OutfitterWorkbenchTransferReviewer.Review(fixture.Transfer, unrelated));

        Assert.Contains("scope", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Review_RejectsCoverageThatDidNotQueryTheAcceptedItem()
    {
        var fixture = Fixture();
        var incomplete = fixture.Evidence with
        {
            Coverage = new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 1, 1, 100, [99]),
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            OutfitterWorkbenchTransferReviewer.Review(fixture.Transfer, incomplete));

        Assert.Contains("did not query", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FixtureData Fixture()
    {
        var generationId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 17, 13, 0, 0, TimeSpan.Zero);
        var key = new EquipmentOfferKey(
            10,
            EquipmentQuality.High,
            EquipmentAcquisitionSourceKind.MarketBoard,
            "market:test:10:High");
        var evidence = Evidence(
            generationId,
            7,
            now,
            [Listing(EquipmentQuality.High, "Siren", 2, 100, now, "source-r1")]);
        var transfer = new OutfitterWorkbenchTransfer(
            OutfitterWorkbenchTransfer.CurrentSchemaVersion,
            OutfitterWorkbenchTransfer.SquireOutfitterOrigin,
            "selected",
            "nomination",
            new("min-btn", "1"),
            new("ordinary", 16, 100, "Ordinary resource nodes", []),
            new(
                generationId,
                7,
                OutfitterMarketEvidenceBook.CurrentSchemaVersion,
                "universalis",
                "North America",
                OutfitterMarketCoverageMode.ExhaustiveWithinScope,
                now),
            [new(EquipmentLoadoutPosition.LeftRing, key, 2, "listing-1", "Market board - Siren")],
            [new(key, "Exact HQ Ring", 2, 2, "Siren", 100, 200, "listing-1", "source-r1", now)],
            200);
        return new(generationId, now, transfer, evidence);
    }

    private static OutfitterMarketEvidenceBook Evidence(
        Guid generationId,
        long revision,
        DateTimeOffset now,
        IReadOnlyList<OutfitterMarketListingEvidence> listings) =>
        new(
            generationId,
            revision,
            OutfitterMarketEvidenceBook.CurrentSchemaVersion,
            "universalis",
            "North America",
            now,
            now,
            OutfitterMarketEvidenceGenerationStatus.Complete,
            new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 1, 1, 100, [10]),
            [new(10, OutfitterMarketEvidenceItemStatus.Fresh, listings, now, "current")]);

    private static OutfitterMarketListingEvidence Listing(
        EquipmentQuality quality,
        string world,
        uint quantity,
        uint unitPrice,
        DateTimeOffset now,
        string sourceRevision) =>
        new(10, quality, "listing-1", world, 1, "Retainer", "retainer-1", quantity, unitPrice, now, now, sourceRevision);

    private sealed record FixtureData(
        Guid GenerationId,
        DateTimeOffset Now,
        OutfitterWorkbenchTransfer Transfer,
        OutfitterMarketEvidenceBook Evidence);
}
