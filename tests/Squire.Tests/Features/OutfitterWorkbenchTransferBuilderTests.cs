using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterWorkbenchTransferBuilderTests
{
    [Fact]
    public void Build_UsesSelectedSolutionInsteadOfNominationAndPreservesExactLotLineage()
    {
        var fixture = Fixture();

        var transfer = OutfitterWorkbenchTransferBuilder.Build(
            fixture.Advice,
            fixture.Selected.Candidate.SolutionId,
            fixture.Evidence,
            Validation(fixture));

        Assert.Equal(OutfitterWorkbenchTransfer.SquireOutfitterOrigin, transfer.Origin);
        Assert.Equal(OutfitterWorkbenchTransfer.CurrentSchemaVersion, transfer.SchemaVersion);
        Assert.Equal("user-selected", transfer.SelectedSolutionId);
        Assert.Equal("advisor-nomination", transfer.AdvisorNominationSolutionId);
        Assert.Equal(fixture.GenerationId, transfer.Evidence.GenerationId);
        Assert.Equal(7, transfer.Evidence.Revision);
        var lot = Assert.Single(transfer.MarketLots);
        Assert.Equal(EquipmentQuality.High, lot.OfferKey.Quality);
        Assert.Equal("market:test:10:High", lot.OfferKey.SourceCatalogKey);
        Assert.Equal((uint)2, lot.RequiredQuantity);
        Assert.Equal((uint)2, lot.ObservedAvailableQuantity);
        Assert.Equal("Siren", lot.WorldName);
        Assert.Equal((uint)100, lot.ObservedUnitPriceGil);
        Assert.Equal((ulong)200, lot.ObservedTotalPriceGil);
        Assert.Equal("listing-1", lot.DiscoveryObservationId);
        Assert.Equal("source-r1", lot.SourceRevision);
        Assert.Equal((ulong)200, transfer.ObservedMarketTotalGil);
        Assert.Collection(
            transfer.SelectedLoadout,
            left =>
            {
                Assert.Equal(EquipmentLoadoutPosition.LeftRing, left.Position);
                Assert.Equal(fixture.MarketKey, left.OfferKey);
                Assert.Equal("listing-1", left.ObservationId);
            },
            right =>
            {
                Assert.Equal(EquipmentLoadoutPosition.RightRing, right.Position);
                Assert.Equal(fixture.MarketKey, right.OfferKey);
                Assert.Equal("listing-1", right.ObservationId);
            });
    }

    [Fact]
    public void Build_PreservesPermanentDryRunRestriction()
    {
        var fixture = Fixture();

        var transfer = OutfitterWorkbenchTransferBuilder.Build(
            fixture.Advice,
            fixture.Selected.Candidate.SolutionId,
            fixture.Evidence,
            Validation(fixture, dryRunOnly: true));

        Assert.True(transfer.DryRunOnly);
    }

    [Fact]
    public void Build_DoesNotTreatRetainedDiagnosticPathCountAsAuthority()
    {
        var fixture = Fixture();
        var expected = OutfitterWorkbenchTransferBuilder.Build(
            fixture.Advice,
            fixture.Selected.Candidate.SolutionId,
            fixture.Evidence,
            Validation(fixture));
        var changedAdvice = fixture.Advice with
        {
            Frontier = fixture.Advice.Frontier! with
            {
                Diagnostics = fixture.Advice.Frontier.Diagnostics with
                {
                    RetainedCompletePathCount = 9_999_999,
                },
            },
        };
        var fingerprint = new PlayerAdvisorAuthorityFingerprint("fixture-player");

        var actual = OutfitterWorkbenchTransferBuilder.Build(
            changedAdvice,
            fixture.Selected.Candidate.SolutionId,
            fixture.Evidence,
            new(
                changedAdvice,
                fixture.Selected.Candidate.SolutionId,
                fixture.Evidence.GenerationId,
                fingerprint,
                fingerprint,
                DryRunOnly: false));

        Assert.Equal(expected.SelectedSolutionId, actual.SelectedSolutionId);
        Assert.Equal(expected.SelectedLoadout, actual.SelectedLoadout);
        Assert.Equal(expected.MarketLots, actual.MarketLots);
        Assert.Equal(expected.ObservedMarketTotalGil, actual.ObservedMarketTotalGil);
    }

    [Fact]
    public void Build_RejectsSolutionOutsideAuthoritativeFrontier()
    {
        var fixture = Fixture();

        var error = Assert.Throws<InvalidOperationException>(() =>
            OutfitterWorkbenchTransferBuilder.Build(
                fixture.Advice,
                "not-on-frontier",
                fixture.Evidence,
                Validation(fixture, selectedSolutionId: "not-on-frontier")));

        Assert.Contains("authoritative frontier", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsOfferFromDifferentEvidenceGeneration()
    {
        var fixture = Fixture(offerGenerationId: Guid.NewGuid());

        var error = Assert.Throws<InvalidOperationException>(() =>
            OutfitterWorkbenchTransferBuilder.Build(
                fixture.Advice,
                fixture.Selected.Candidate.SolutionId,
                fixture.Evidence,
                Validation(fixture)));

        Assert.Contains("evidence generation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_PreservesOwnedAndVendorSelectionsWithoutTurningThemIntoMarketLots()
    {
        var fixture = Fixture(includeOwnedAndVendor: true);

        var transfer = OutfitterWorkbenchTransferBuilder.Build(
            fixture.Advice,
            fixture.Selected.Candidate.SolutionId,
            fixture.Evidence,
            Validation(fixture));

        Assert.Single(transfer.MarketLots);
        Assert.Contains(transfer.SelectedLoadout, selection => selection.OfferKey.SourceKind == EquipmentAcquisitionSourceKind.Owned);
        Assert.Contains(transfer.SelectedLoadout, selection => selection.OfferKey.SourceKind == EquipmentAcquisitionSourceKind.GilVendor);
        Assert.Equal(4, transfer.SelectedLoadout.Count);
    }

    [Fact]
    public void Build_RejectsNoMarketSolutionInsteadOfStampingUnrelatedEvidence()
    {
        var fixture = Fixture(includeOwnedAndVendor: true, includeMarket: false);

        var error = Assert.Throws<InvalidOperationException>(() =>
            OutfitterWorkbenchTransferBuilder.Build(
                fixture.Advice,
                fixture.Selected.Candidate.SolutionId,
                fixture.Evidence,
                Validation(fixture)));

        Assert.Contains("no market acquisition", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_RejectsStalePlayerBaselineValidation()
    {
        var fixture = Fixture();
        var validation = Validation(fixture) with
        {
            RecapturedPlayer = new PlayerAdvisorAuthorityFingerprint("changed-player"),
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            OutfitterWorkbenchTransferBuilder.Build(
                fixture.Advice,
                fixture.Selected.Candidate.SolutionId,
                fixture.Evidence,
                validation));

        Assert.Contains("current player baseline revalidation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_RejectsValidationForDifferentAdviceInstance()
    {
        var fixture = Fixture();
        var differentAdvice = fixture.Advice with { Diagnostic = "different advice instance" };

        var error = Assert.Throws<InvalidOperationException>(() =>
            OutfitterWorkbenchTransferBuilder.Build(
                differentAdvice,
                fixture.Selected.Candidate.SolutionId,
                fixture.Evidence,
                Validation(fixture)));

        Assert.Contains("current player baseline revalidation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FixtureData Fixture(
        Guid? offerGenerationId = null,
        bool includeOwnedAndVendor = false,
        bool includeMarket = true)
    {
        var generationId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var ring = Definition(10, "Exact HQ Ring", EquipmentSlot.Ring);
        var marketKey = new EquipmentOfferKey(
            ring.ItemId,
            EquipmentQuality.High,
            EquipmentAcquisitionSourceKind.MarketBoard,
            "market:test:10:High");
        var observation = new EquipmentOfferObservation(
            marketKey,
            offerGenerationId ?? generationId,
            "listing-1",
            now,
            ObservableMarketRow: new("listing-1", ring.ItemId, EquipmentQuality.High, 2, 100, "Seller", "Retainer"),
            World: "Siren",
            AvailableQuantity: 2,
            UnitPriceGil: 100);
        var marketOffer = new EquipmentLoadoutOffer(
            ring,
            EquipmentAcquisitionSourceKind.MarketBoard,
            "Market board - Siren",
            100,
            Quality: EquipmentQuality.High,
            SourceCatalogKey: marketKey.SourceCatalogKey,
            Observation: observation);
        var exactMarket = new EquipmentExactSolverOffer(
            marketOffer,
            "listing-1",
            new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.LeftRing, EquipmentLoadoutPosition.RightRing },
            2,
            EquipmentSolverUtilityVector.Empty,
            100,
            "Siren",
            null,
            1,
            new(0, 0, 0),
            ["HQ", "Siren"]);

        var selections = new List<EquipmentLoadoutSelection>();
        var offers = new Dictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer>();
        if (includeMarket)
        {
            selections.Add(new(EquipmentLoadoutPosition.LeftRing, marketKey, 1, "listing-1"));
            selections.Add(new(EquipmentLoadoutPosition.RightRing, marketKey, 1, "listing-1"));
            offers.Add(exactMarket.AllocationKey, exactMarket);
        }
        if (includeOwnedAndVendor)
        {
            AddNonMarketOffer(EquipmentLoadoutPosition.Head, EquipmentAcquisitionSourceKind.Owned, 20, "Owned Hat", selections, offers);
            AddNonMarketOffer(EquipmentLoadoutPosition.Body, EquipmentAcquisitionSourceKind.GilVendor, 30, "Vendor Coat", selections, offers);
        }

        var utility = Utility();
        var selected = new EquipmentDecisionSolution(
            new("user-selected", selections),
            utility,
            200,
            new(1, 0, 1),
            new(0, 0, 0),
            ["Selected"]);
        var nomination = new EquipmentDecisionSolution(
            new("advisor-nomination", []),
            utility with { UtilityScore = utility.UtilityScore - 1 },
            0,
            new(0, 0, 0),
            new(0, 0, 0),
            ["Advisor"]);
        var frontier = new EquipmentExactFrontierResult(
            new([nomination, selected], [], [], []),
            new(0, 0, 0, 0, 0, 2, 2, 16, "baseline", TimeSpan.Zero),
            []);
        var advice = new MinerBotanistReadOnlyAdvice(
            MinerBotanistAdvisorStatus.Complete,
            "fixture",
            frontier,
            nomination,
            new Dictionary<string, AdvisorAuthorityAssessment>(),
            offers,
            "complete");
        var evidence = new OutfitterMarketEvidenceBook(
            generationId,
            7,
            OutfitterMarketEvidenceBook.CurrentSchemaVersion,
            "universalis",
            "North America",
            now,
            now,
            OutfitterMarketEvidenceGenerationStatus.Complete,
            new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 1, 1, 100, [ring.ItemId]),
            [
                new(
                    ring.ItemId,
                    OutfitterMarketEvidenceItemStatus.Fresh,
                    [new(ring.ItemId, EquipmentQuality.High, "listing-1", "Siren", 1, "Retainer", "retainer-1", 2, 100, now, now, "source-r1")],
                    now,
                    "source-r1"),
            ]);
        return new(generationId, marketKey, evidence, advice, selected);
    }

    private static OutfitterWorkbenchPlayerValidation Validation(
        FixtureData fixture,
        bool dryRunOnly = false,
        string? selectedSolutionId = null)
    {
        var fingerprint = new PlayerAdvisorAuthorityFingerprint("fixture-player");
        return new(
            fixture.Advice,
            selectedSolutionId ?? fixture.Selected.Candidate.SolutionId,
            fixture.Evidence.GenerationId,
            fingerprint,
            fingerprint,
            dryRunOnly);
    }

    private static void AddNonMarketOffer(
        EquipmentLoadoutPosition position,
        EquipmentAcquisitionSourceKind sourceKind,
        uint itemId,
        string name,
        ICollection<EquipmentLoadoutSelection> selections,
        IDictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer> offers)
    {
        var definition = Definition(itemId, name, position == EquipmentLoadoutPosition.Head ? EquipmentSlot.Head : EquipmentSlot.Body);
        var loadoutOffer = new EquipmentLoadoutOffer(
            definition,
            sourceKind,
            name,
            sourceKind == EquipmentAcquisitionSourceKind.GilVendor ? 50u : null,
            Quality: EquipmentQuality.Normal,
            SourceCatalogKey: $"fixture:{sourceKind}:{itemId}");
        var exact = new EquipmentExactSolverOffer(
            loadoutOffer,
            null,
            new HashSet<EquipmentLoadoutPosition> { position },
            1,
            EquipmentSolverUtilityVector.Empty,
            sourceKind == EquipmentAcquisitionSourceKind.GilVendor ? 50ul : 0ul,
            null,
            sourceKind == EquipmentAcquisitionSourceKind.GilVendor ? "vendor" : null,
            sourceKind == EquipmentAcquisitionSourceKind.GilVendor ? 1 : 0,
            new(0, 0, 0),
            [sourceKind.ToString()]);
        selections.Add(new(position, loadoutOffer.Key));
        offers.Add(exact.AllocationKey, exact);
    }

    private static EquipmentUtilityEvaluation Utility() => new(
        new("min-btn", "1"),
        new("ordinary", 16, 100, "Ordinary resource nodes", []),
        50,
        new(49, 51, []),
        UpgradeAssessment.ClearImprovement,
        [],
        [],
        [],
        EquipmentEvaluationConfidence.High,
        []);

    private static EquipmentItemDefinition Definition(uint itemId, string name, EquipmentSlot slot) => new(
        itemId,
        name,
        100,
        700,
        slot,
        new HashSet<uint> { 16, 17 },
        1,
        true,
        false,
        true,
        true,
        1,
        true,
        null,
        null,
        false);

    private sealed record FixtureData(
        Guid GenerationId,
        EquipmentOfferKey MarketKey,
        OutfitterMarketEvidenceBook Evidence,
        MinerBotanistReadOnlyAdvice Advice,
        EquipmentDecisionSolution Selected);
}
