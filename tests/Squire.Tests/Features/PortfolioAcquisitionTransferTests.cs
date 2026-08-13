using System.Text.Json;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.Portfolio;
using MarketMafioso.Squire.Outfitter.Utility;
using MarketMafioso.Windows.Squire;
using JsonConvert = Newtonsoft.Json.JsonConvert;
using Squire.Interop;

namespace MarketMafioso.Tests.Squire;

public sealed class PortfolioAcquisitionTransferTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReviewedStageAndResumeControlIds_AreStable()
    {
        Assert.Equal("squire.outfitter.portfolio.acquisition.stage", PortfolioAcquisitionReviewedControlIds.Stage);
        Assert.Equal("squire.outfitter.portfolio.acquisition.resume", PortfolioAcquisitionReviewedControlIds.Resume);
        Assert.StartsWith("squire.outfitter.portfolio.acquisition.vendor-confirm.", PortfolioAcquisitionReviewedControlIds.VendorConfirm("vendor-line"));
        Assert.StartsWith("squire.outfitter.portfolio.acquisition.artisan-export.", PortfolioAcquisitionReviewedControlIds.ArtisanExport("craft-line"));
    }

    [Fact]
    public void BridgeProjection_MatchesRenderedFailedOnlyResumePredicateAndCompositionDiagnostic()
    {
        var fixture = Fixture(Target("player", 100, PortfolioProgressionHorizon.Immediate, Market("listing", 100, false)));
        var awaiting = PortfolioAcquisitionRecovery.Staged(fixture.Transfer, Now);
        var failed = awaiting with
        {
            Status = PortfolioAcquisitionRecoveryStatus.Failed,
            Diagnostic = "Exact recovery failed.",
        };

        Assert.False(PortfolioAcquisitionBridgeProjection.ResumeReachable(awaiting, buildQueueIdle: true, sessionBusy: false));
        Assert.False(PortfolioAcquisitionBridgeProjection.ResumeReachable(failed, buildQueueIdle: false, sessionBusy: false));
        Assert.False(PortfolioAcquisitionBridgeProjection.ResumeReachable(failed, buildQueueIdle: true, sessionBusy: true));
        Assert.True(PortfolioAcquisitionBridgeProjection.ResumeReachable(failed, buildQueueIdle: true, sessionBusy: false));
        Assert.Equal("CompositionFailed", PortfolioAcquisitionBridgeProjection.Status(null, null, compositionFailed: true));
        Assert.Equal(
            "Portfolio acquisition composition stopped safely: stale craft evidence.",
            PortfolioAcquisitionBridgeProjection.Diagnostic(
                null,
                null,
                "Portfolio acquisition composition stopped safely: stale craft evidence."));
    }

    [Fact]
    public void VendorSelectionIdentity_RoundTripsExactCatalogMembershipAndPrice()
    {
        var offer = new OutfitterGilVendorOffer(200, 10, 20, "Vendor", 30, "Ul'dah", 25);
        var encoded = OutfitterGilVendorSelectionIdentity.Encode("vendor-catalog-1", offer);

        Assert.True(OutfitterGilVendorSelectionIdentity.TryDecode(encoded, out var restored));
        Assert.NotNull(restored);
        Assert.Equal("vendor-catalog-1", restored!.CatalogVersion);
        Assert.Equal(offer.ItemId, restored.ItemId);
        Assert.Equal(offer.ShopId, restored.ShopId);
        Assert.Equal(offer.VendorId, restored.VendorId);
        Assert.Equal(offer.TerritoryId, restored.TerritoryId);
        Assert.Equal(offer.UnitPriceGil, restored.UnitPriceGil);
    }

    [Fact]
    public void MarketTransfer_PreservesExactPortfolioLineageThroughExistingWorkbenchFields()
    {
        var fixture = Fixture(Target("player", 100, PortfolioProgressionHorizon.Immediate, Market("listing-7", 100, true)));

        var workbench = PortfolioAcquisitionTransferBuilder.ToWorkbenchTransfer(
            fixture.Transfer,
            new("test-profile", "v1"),
            new("test-context", 16, 100, "Test", []),
            "North America",
            Now);

        Assert.StartsWith("portfolio:", workbench.SelectedSolutionId, StringComparison.Ordinal);
        Assert.Equal(fixture.Authority.Fingerprint.Sha256, workbench.SelectedSolutionId["portfolio:".Length..]);
        var lot = Assert.Single(workbench.MarketLots);
        Assert.Equal((ulong)25, workbench.ObservedMarketTotalGil);
        Assert.Equal((uint)1, lot.RequiredQuantity);
        Assert.True(PortfolioAcquisitionLineage.TryDecode(lot.OfferKey.SourceCatalogKey, out var lineage));
        Assert.NotNull(lineage);
        Assert.Equal(fixture.Authority.Fingerprint, lineage!.AuthorityFingerprint);
        Assert.Equal("player", lineage.TargetKey);
        Assert.Equal("player-choice", lineage.CandidateKey);
        Assert.Equal("physical-market-listing", lineage.ComponentKind);
        var consumer = Assert.Single(fixture.Transfer.MarketLots.Single().Consumers);
        Assert.True(PortfolioAcquisitionLineage.TryDecode(consumer.LineageKey, out var consumerLineage));
        Assert.Equal(fixture.Transfer.Lines.Single().Allocation, consumerLineage!.Allocation);
    }

    [Theory]
    [InlineData(PortfolioAllocationSourceKind.GilVendor)]
    [InlineData(PortfolioAllocationSourceKind.Craft)]
    public void NonMarketTransfer_RemainsExplicitAndCannotBeMisrepresentedAsWorkbenchPurchase(
        PortfolioAllocationSourceKind sourceKind)
    {
        var allocation = sourceKind == PortfolioAllocationSourceKind.GilVendor
            ? Vendor("vendor-armor", 200, false)
            : Craft("recipe-armor", 200, false);
        var fixture = Fixture(Target("player", 100, PortfolioProgressionHorizon.Immediate, allocation));

        var error = Assert.Throws<InvalidOperationException>(() =>
            PortfolioAcquisitionTransferBuilder.ToWorkbenchTransfer(
                fixture.Transfer,
                new("test-profile", "v1"),
                new("test-context", 16, 100, "Test", []),
                "North America",
                Now));
        var recovery = PortfolioAcquisitionRecovery.Staged(fixture.Transfer, Now);

        Assert.Contains("no exact market lots", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceKind == PortfolioAllocationSourceKind.GilVendor ? "vendor" : "craft", recovery.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no Market Workbench purchase", recovery.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MixedTransfer_PreservesPriorityAndAllActionsButSendsOnlyMarketLotsToWorkbench()
    {
        var fixture = Fixture(
            Target("player", 100, PortfolioProgressionHorizon.Immediate, Market("listing-1", 101, true)),
            Target("retainer", 50, PortfolioProgressionHorizon.NearTerm, Vendor("vendor-1", 201, false)),
            Target("saved:4", 25, PortfolioProgressionHorizon.LongTerm, Craft("recipe-1", 301, true)));

        var workbench = PortfolioAcquisitionTransferBuilder.ToWorkbenchTransfer(
            fixture.Transfer,
            new("test-profile", "v1"),
            new("test-context", 16, 100, "Test", []),
            "North America",
            Now);

        Assert.Equal(["player", "retainer", "saved:4"], fixture.Transfer.Lines.Select(value => value.TargetKey));
        Assert.Single(workbench.MarketLots);
        Assert.Equal(3, workbench.SelectedLoadout.Count);
        var decoded = workbench.SelectedLoadout.Select(value =>
        {
            Assert.True(PortfolioAcquisitionLineage.TryDecode(value.OfferKey.SourceCatalogKey, out var lineage));
            return lineage!;
        }).ToArray();
        Assert.Equal(["player", "retainer", "saved:4"], decoded.Select(value => value.TargetKey));
        Assert.Equal(
            [PortfolioAllocationSourceKind.MarketListing, PortfolioAllocationSourceKind.GilVendor, PortfolioAllocationSourceKind.Craft],
            decoded.Select(value => value.Allocation.SourceKind));
        Assert.Single(fixture.Transfer.VendorActions);
        Assert.Single(fixture.Transfer.ArtisanRecipes);
    }

    [Fact]
    public void SharedListing_KeepsPerTargetAllocationLineageAndAggregatesExactRequiredQuantity()
    {
        var listing = Market("shared-listing", 101, true);
        var fixture = Fixture(
            Target("player", 100, PortfolioProgressionHorizon.Immediate, listing, observedAvailableQuantity: 2),
            Target("retainer", 50, PortfolioProgressionHorizon.NearTerm, listing, observedAvailableQuantity: 2));

        var workbench = PortfolioAcquisitionTransferBuilder.ToWorkbenchTransfer(
            fixture.Transfer,
            new("test-profile", "v1"),
            new("test-context", 16, 100, "Test", []),
            "North America",
            Now);

        Assert.Single(workbench.MarketLots);
        Assert.Equal((uint)2, workbench.MarketLots.Single().RequiredQuantity);
        Assert.Equal(workbench.MarketLots.Single().RequiredQuantity, workbench.MarketLots.Single().ObservedAvailableQuantity);
        Assert.Equal((ulong)50, workbench.ObservedMarketTotalGil);
        Assert.Equal(["player", "retainer"], fixture.Transfer.MarketLots.Single().Consumers.Select(value => value.TargetKey));
    }

    [Fact]
    public void AggregatedPhysicalListing_IsAcceptedByCurrentMmfExecutionValidator()
    {
        var listing = Market("shared-listing", 101, true);
        var fixture = Fixture(
            Target("player", 100, PortfolioProgressionHorizon.Immediate, listing, observedAvailableQuantity: 2),
            Target("retainer", 50, PortfolioProgressionHorizon.NearTerm, listing, observedAvailableQuantity: 2));
        var transfer = PortfolioAcquisitionTransferBuilder.ToWorkbenchTransfer(
            fixture.Transfer,
            new("portfolio", "v1"),
            new("portfolio", 16, 100, "Portfolio", []),
            "North America",
            Now) with { DryRunOnly = true };
        var document = MarketAcquisitionRequestDocument.CreateDefault("Fran", "Siren") with
        {
            Region = "North America",
            WorldMode = "Recommended",
        };
        document = OutfitterWorkbenchAuthorityService.Stage(document, transfer);
        document = OutfitterWorkbenchAuthorityService.Finalize(document);
        var contract = document.OutfitterAuthority!.FinalizedContract!;
        var claim = new MarketAcquisitionClaimView
        {
            Id = "portfolio-request",
            ClaimToken = "claim-token",
            Status = "AcceptedInPlugin",
            TargetCharacterName = contract.TargetCharacterName,
            TargetWorld = contract.TargetWorld,
            Region = contract.Region,
            WorldMode = contract.WorldMode,
            Lines =
            [
                new MarketAcquisitionBatchLineView
                {
                    LineId = "line-1",
                    Ordinal = 0,
                    ItemId = 101,
                    ItemName = "Item 101",
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = 2,
                    HqPolicy = "HQOnly",
                    MaxUnitPrice = 25,
                    GilCap = 50,
                },
            ],
        };

        var plan = OutfitterDryRunPreparedPlanRestorer.Prepare(
            contract, document, claim, Now.AddSeconds(1));

        var planned = Assert.Single(Assert.Single(plan.WorldBatches).Listings);
        Assert.Equal((uint)2, planned.Quantity);
        Assert.Equal("retainer-1", planned.RetainerId);
        Assert.Equal("Seller", planned.RetainerName);
    }

    [Fact]
    public void MixedTransfer_ProductNeutralWireRetainsEncodedPortfolioLineageWithoutClaimingPortfolioExecution()
    {
        var fixture = Fixture(
            Target("player", 100, PortfolioProgressionHorizon.Immediate, Market("listing-1", 101, true)),
            Target("retainer", 50, PortfolioProgressionHorizon.NearTerm, Vendor("vendor-1", 201, false)),
            Target("saved:4", 25, PortfolioProgressionHorizon.LongTerm, Craft("recipe-1", 301, true)));
        var workbench = PortfolioAcquisitionTransferBuilder.ToWorkbenchTransfer(
            fixture.Transfer,
            new("test-profile", "v1"),
            new("test-context", 16, 100, "Test", []),
            "North America",
            Now);
        var adapter = new RecordingIpcAdapter();

        new MarketMafiosoAcquisitionIpcClient(adapter).Stage(workbench);

        using var document = JsonDocument.Parse(adapter.Request!);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("Portfolio", out _));
        Assert.Single(root.GetProperty("MarketLots").EnumerateArray());
        var selections = root.GetProperty("SelectedLoadout").EnumerateArray().ToArray();
        Assert.Equal(3, selections.Length);
        foreach (var selection in selections)
        {
            var sourceKey = selection.GetProperty("OfferKey").GetProperty("SourceCatalogKey").GetString();
            Assert.True(PortfolioAcquisitionLineage.TryDecode(sourceKey!, out var lineage));
            Assert.Equal(fixture.Authority.Fingerprint, lineage!.AuthorityFingerprint);
        }
    }

    [Fact]
    public void Recovery_RoundTripsAndRebindsToFreshFingerprintWhileRetainingInitialReviewLineage()
    {
        var first = Fixture(Target("player", 100, PortfolioProgressionHorizon.Immediate, Market("listing-old", 100, false)));
        var staged = PortfolioAcquisitionRecovery.Staged(first.Transfer, Now);
        var restored = JsonConvert.DeserializeObject<PortfolioAcquisitionRecoveryState>(
            JsonConvert.SerializeObject(staged))!;
        var second = Fixture(
            Lineage(inventory: "inventory-2", listings: "listings-2"),
            Target("player", 100, PortfolioProgressionHorizon.Immediate, Market("listing-new", 100, false)));

        var reconciled = PortfolioAcquisitionRecovery.Reconciled(
            PortfolioAcquisitionRecovery.BeginRebuild(restored, Now.AddMinutes(1)),
            second.Authority,
            second.Transfer,
            Now.AddMinutes(2));

        Assert.Equal(PortfolioAcquisitionRecoveryStatus.AwaitingUpdatedReview, reconciled.Status);
        Assert.Equal(first.Authority.Fingerprint, reconciled.StagedAuthorityFingerprint);
        Assert.Equal(second.Authority.Fingerprint, reconciled.AuthorityFingerprint);
        Assert.NotEqual(reconciled.StagedAuthorityFingerprint, reconciled.AuthorityFingerprint);
        Assert.Equal("listing-new", reconciled.Transfer.Lines.Single().Allocation.SourceKey);
        Assert.True(PortfolioAcquisitionRecovery.NeedsAutomaticRebuild(restored));
    }

    [Fact]
    public void Recovery_StopsWhenIncludedTargetPriorityDriftsAndKeepsLastReviewedTransfer()
    {
        var first = Fixture(Target("player", 100, PortfolioProgressionHorizon.Immediate, Market("listing-old", 100, false)));
        var changed = Fixture(Target("player", 99, PortfolioProgressionHorizon.Immediate, Market("listing-new", 100, false)));
        var rebuilding = PortfolioAcquisitionRecovery.BeginRebuild(
            PortfolioAcquisitionRecovery.Staged(first.Transfer, Now),
            Now.AddMinutes(1));

        var stopped = PortfolioAcquisitionRecovery.Reconciled(rebuilding, changed.Authority, changed.Transfer, Now.AddMinutes(2));

        Assert.Equal(PortfolioAcquisitionRecoveryStatus.Failed, stopped.Status);
        Assert.Contains("priority", stopped.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first.Authority.Fingerprint, stopped.AuthorityFingerprint);
        Assert.Equal("listing-old", stopped.Transfer.Lines.Single().Allocation.SourceKey);
    }

    [Fact]
    public void Build_RejectsTargetEvidenceGenerationDrift()
    {
        var target = Target("player", 100, PortfolioProgressionHorizon.Immediate, Market("listing-1", 100, false));
        var plan = Plan(target);
        var authority = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), plan);
        var evidence = Evidence(target) with { EvidenceGeneration = "player-evidence-drifted" };

        var error = Assert.Throws<InvalidOperationException>(() =>
            PortfolioAcquisitionTransferBuilder.Build(authority, plan, [evidence]));

        Assert.Contains("evidence generation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recovery_CompletesWhenFreshInventoryEliminatesEveryNonOwnedAllocation()
    {
        var first = Fixture(Target("player", 100, PortfolioProgressionHorizon.Immediate, Market("listing-1", 100, false)));
        var acquired = Fixture(
            Lineage(inventory: "inventory-after-acquisition"),
            Target("player", 100, PortfolioProgressionHorizon.Immediate, Owned("inventory-slot-2", 100, false)));
        var rebuilding = PortfolioAcquisitionRecovery.BeginRebuild(
            PortfolioAcquisitionRecovery.Staged(first.Transfer, Now),
            Now.AddMinutes(1));

        var ready = PortfolioAcquisitionRecovery.Reconciled(rebuilding, acquired.Authority, acquired.Transfer, Now.AddMinutes(2));

        Assert.Equal(PortfolioAcquisitionRecoveryStatus.ReadyToEquip, ready.Status);
        Assert.Empty(ready.Transfer.Lines);
        Assert.Equal(acquired.Authority.Fingerprint, ready.AuthorityFingerprint);
    }

    [Fact]
    public void Transfer_SystemTextJsonRoundTripPreservesAuthorityTargetsAllocationsAndEvidence()
    {
        var fixture = Fixture(
            Target("player", 100, PortfolioProgressionHorizon.Immediate, Market("listing-1", 101, true)),
            Target("retainer", 50, PortfolioProgressionHorizon.NearTerm, Vendor("vendor-1", 201, false)));

        var restored = JsonSerializer.Deserialize<PortfolioAcquisitionTransfer>(
            JsonSerializer.Serialize(fixture.Transfer))!;

        Assert.Equal(fixture.Transfer.AuthorityFingerprint, restored.AuthorityFingerprint);
        Assert.Equal(fixture.Transfer.Targets, restored.Targets);
        Assert.Equal(JsonSerializer.Serialize(fixture.Transfer), JsonSerializer.Serialize(restored));
    }

    [Fact]
    public void CraftProjection_ExportsFullRecipeTreeAndStagesOnlyExactMarketMaterials()
    {
        var target = Target("crafter", 100, PortfolioProgressionHorizon.Immediate, Craft("plan-gear", 300, true));
        var craft = CraftEvidence(target,
            recipes:
            [
                new("plan-sha", 900, 1, "Finished gear", 0),
                new("plan-sha", 901, 2, "Subcraft", 1),
            ],
            materials:
            [
                new("plan-sha", 400, "Market ore", false, 3, "material-listing", PortfolioAllocationSourceKind.MarketListing,
                    10, 3, target.EvidenceGeneration, Now, "Siren", "material-listing", "source-r1",
                    RetainerName: "Seller", RetainerId: "retainer-1"),
                new("plan-sha", 401, "Vendor flux", false, 2, "vendor-flux", PortfolioAllocationSourceKind.GilVendor,
                    7, 2, target.EvidenceGeneration, Now, Vendor: VendorIdentity()),
            ]);
        var fixture = Fixture(Lineage(), [craft], target);

        var workbench = PortfolioAcquisitionTransferBuilder.ToWorkbenchTransfer(
            fixture.Transfer,
            new("portfolio", "v1"),
            new("portfolio", 16, 100, "Portfolio", []),
            "North America",
            Now);

        Assert.Equal([901u, 900u], fixture.Transfer.ArtisanRecipes.Select(value => value.RecipeId));
        Assert.Single(workbench.MarketLots);
        Assert.Equal("CraftMaterial", workbench.MarketLots.Single().ItemKind);
        Assert.Equal((uint)400, workbench.MarketLots.Single().OfferKey.ItemId);
        var vendor = Assert.Single(fixture.Transfer.VendorActions);
        Assert.True(vendor.IsCraftMaterial);
        Assert.Equal((uint)401, vendor.ItemId);
        Assert.Equal(VendorIdentity(), vendor.Vendor);
    }

    [Fact]
    public void CraftProjection_RejectsSharedListingDemandBeyondExactCapacityAcrossTargets()
    {
        var first = Target("first", 100, PortfolioProgressionHorizon.Immediate, Craft("plan-a", 300, false));
        var second = Target("second", 50, PortfolioProgressionHorizon.NearTerm, Craft("plan-b", 301, false));
        var shared = new PortfolioCraftMaterialEvidence(
            "plan-sha", 400, "Shared ore", false, 1, "listing-shared",
            PortfolioAllocationSourceKind.MarketListing, 10, 1, "shared-evidence", Now,
            "Siren", "listing-shared", "source-r1", RetainerName: "Seller", RetainerId: "retainer-1");
        var firstCraft = CraftEvidence(first, materials: [shared with { EvidenceGeneration = first.EvidenceGeneration }]);
        var secondCraft = CraftEvidence(second, materials: [shared with { EvidenceGeneration = second.EvidenceGeneration }]);
        var materialAllocation = PortfolioAcquisitionComposition.MaterialAllocation(shared);
        var plan = Plan(first, second);
        plan = plan with
        {
            SelectedCandidates = plan.SelectedCandidates.Select(candidate => candidate with
            {
                Demands = candidate.Demands.Append(new(materialAllocation, 1)).ToArray(),
            }).ToArray(),
        };
        var authority = PortfolioAuthorityEnvelopeFactory.Create(Lineage(), plan);

        var error = Assert.Throws<InvalidOperationException>(() => PortfolioAcquisitionTransferBuilder.Build(
            authority,
            plan,
            [Evidence(first), Evidence(second)],
            [
                firstCraft,
                secondCraft,
            ]));

        Assert.Contains("capacity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recovery_PersistsVendorAndArtisanReceiptsAndDropsThemOnFingerprintDrift()
    {
        var target = Target("crafter", 100, PortfolioProgressionHorizon.Immediate, Craft("plan-gear", 300, true));
        var craft = CraftEvidence(target,
            materials:
            [new("plan-sha", 401, "Vendor flux", false, 2, "vendor-flux", PortfolioAllocationSourceKind.GilVendor,
                7, 2, target.EvidenceGeneration, Now, Vendor: VendorIdentity())]);
        var first = Fixture(Lineage(), [craft], target);
        var vendorLineage = Assert.Single(first.Transfer.VendorActions).LineageKey;
        var artisanLineage = PortfolioAcquisitionComposition.ArtisanExportLineageKey(
            first.Authority.Fingerprint, target.TargetKey, target.CandidateKey);
        var staged = PortfolioAcquisitionRecovery.Staged(first.Transfer, Now);
        staged = PortfolioAcquisitionRecovery.MarkVendorConfirmed(staged, vendorLineage, Now.AddSeconds(1));
        staged = PortfolioAcquisitionRecovery.MarkArtisanExported(staged, artisanLineage, "artisan-receipt", Now.AddSeconds(2));
        var restored = JsonConvert.DeserializeObject<PortfolioAcquisitionRecoveryState>(JsonConvert.SerializeObject(staged))!;

        Assert.Equal(PortfolioAcquisitionActionStatus.UserConfirmed,
            restored.Progress.Single(value => value.Kind == PortfolioAcquisitionActionKind.VendorChecklist).Status);
        Assert.Equal("artisan-receipt",
            restored.Progress.Single(value => value.Kind == PortfolioAcquisitionActionKind.ArtisanExport).Receipt);

        var changed = Fixture(Lineage(inventory: "inventory-after"), [craft], target);
        var reconciled = PortfolioAcquisitionRecovery.Reconciled(
            PortfolioAcquisitionRecovery.BeginRebuild(restored, Now.AddMinutes(1)),
            changed.Authority,
            changed.Transfer,
            Now.AddMinutes(2));
        Assert.All(reconciled.Progress, value => Assert.Equal(PortfolioAcquisitionActionStatus.Pending, value.Status));
    }

    [Fact]
    public void Recovery_ZeroLineFreshPlanBecomesReadyWithoutAnotherUserAction()
    {
        var first = Fixture(Target("player", 100, PortfolioProgressionHorizon.Immediate, Vendor("vendor", 200, false)));
        var restored = JsonConvert.DeserializeObject<PortfolioAcquisitionRecoveryState>(
            JsonConvert.SerializeObject(PortfolioAcquisitionRecovery.Staged(first.Transfer, Now)))!;
        var acquired = Fixture(
            Lineage(inventory: "inventory-after"),
            Target("player", 100, PortfolioProgressionHorizon.Immediate, Owned("slot-1", 200, false)));

        var result = PortfolioAcquisitionRecovery.Reconciled(
            PortfolioAcquisitionRecovery.BeginRebuild(restored, Now.AddMinutes(1)),
            acquired.Authority,
            acquired.Transfer,
            Now.AddMinutes(2));

        Assert.Equal(PortfolioAcquisitionRecoveryStatus.ReadyToEquip, result.Status);
        Assert.Empty(result.Progress);
        Assert.False(PortfolioAcquisitionRecovery.NeedsAutomaticRebuild(result));
    }

    private static FixtureData Fixture(params TargetInput[] targets) => Fixture(Lineage(), [], targets);

    private static FixtureData Fixture(PortfolioAuthorityLineage lineage, params TargetInput[] targets) =>
        Fixture(lineage, [], targets);

    private static FixtureData Fixture(
        PortfolioAuthorityLineage lineage,
        IReadOnlyList<PortfolioCraftAcquisitionEvidence> craftEvidence,
        params TargetInput[] targets)
    {
        var explicitCraft = craftEvidence.Count > 0
            ? craftEvidence
            : targets.Where(value => value.Allocation.SourceKind == PortfolioAllocationSourceKind.Craft)
                .Select(value => CraftEvidence(value))
                .ToArray();
        var plan = Plan(targets);
        plan = plan with
        {
            SelectedCandidates = plan.SelectedCandidates.Select(candidate => candidate with
            {
                Demands = candidate.Demands.Concat(explicitCraft
                        .Where(value => value.TargetKey == candidate.TargetKey && value.CandidateKey == candidate.CandidateKey)
                        .SelectMany(value => value.Materials)
                        .GroupBy(PortfolioAcquisitionComposition.MaterialAllocation)
                        .Select(group => new PortfolioAllocationDemand(
                            group.Key,
                            group.Aggregate(0u, (sum, value) => checked(sum + value.RequiredQuantity)))))
                    .ToArray(),
            }).ToArray(),
        };
        var authority = PortfolioAuthorityEnvelopeFactory.Create(lineage, plan);
        var evidence = targets
            .Where(value => value.Allocation.SourceKind is not (PortfolioAllocationSourceKind.OwnedInstance or PortfolioAllocationSourceKind.HandMeDown))
            .Select(Evidence)
            .ToArray();
        return new(plan, authority, PortfolioAcquisitionTransferBuilder.Build(authority, plan, evidence, explicitCraft));
    }

    private static OutfitterPortfolioPlan Plan(params TargetInput[] targets)
    {
        var priorities = targets.Select(value => new PortfolioTargetPriority(
            value.TargetKey,
            value.TargetKey,
            value.Priority,
            value.Horizon)).ToArray();
        var candidates = targets.Select(value => new PortfolioCandidate(
            value.TargetKey,
            value.CandidateKey,
            10,
            [new(value.Allocation, 1)],
            [],
            [])).ToArray();
        var dispositions = targets.Select(value => new PortfolioTargetDisposition(
            value.TargetKey,
            PortfolioTargetDispositionKind.SelectedCandidate,
            value.EvidenceGeneration,
            $"Selected {value.CandidateKey}.",
            value.CandidateKey)).ToArray();
        return new(priorities, candidates, [], TargetDispositions: dispositions);
    }

    private static PortfolioAcquisitionEvidenceLine Evidence(TargetInput target)
    {
        var line = new PortfolioAcquisitionEvidenceLine(
            target.TargetKey,
            target.CandidateKey,
            target.Allocation,
            $"Item {target.Allocation.ItemId}",
            target.Allocation.SourceKey,
            [new(EquipmentLoadoutPosition.Head, 1)],
            target.ObservedAvailableQuantity,
            target.Allocation.SourceKind == PortfolioAllocationSourceKind.Craft ? null : 25,
            target.EvidenceGeneration,
            Now,
            target.Allocation.SourceKind == PortfolioAllocationSourceKind.MarketListing ? "Siren" : null,
            target.Allocation.SourceKind == PortfolioAllocationSourceKind.MarketListing ? target.Allocation.SourceKey : null,
            target.Allocation.SourceKind == PortfolioAllocationSourceKind.MarketListing ? "source-r1" : null,
            target.Allocation.SourceKind == PortfolioAllocationSourceKind.MarketListing ? "Seller" : null,
            target.Allocation.SourceKind == PortfolioAllocationSourceKind.MarketListing ? "retainer-1" : null);
        return target.Allocation.SourceKind == PortfolioAllocationSourceKind.GilVendor
            ? line with { Vendor = new(10, 20, 30, "Vendor", "Ul'dah", 25, "vendor-catalog-1") }
            : line;
    }

    private static TargetInput Target(
        string key,
        int priority,
        PortfolioProgressionHorizon horizon,
        PortfolioAllocationKey allocation,
        uint observedAvailableQuantity = 1) =>
        new(key, $"{key}-choice", priority, horizon, $"{key}-evidence-1", allocation, observedAvailableQuantity);

    private static PortfolioAuthorityLineage Lineage(
        string inventory = "inventory-1",
        string listings = "listings-1") =>
        new("owner", "owner-1", inventory, listings, []);

    private static PortfolioAllocationKey Market(string source, uint itemId, bool highQuality) =>
        new(PortfolioAllocationSourceKind.MarketListing, source, itemId, highQuality);

    private static PortfolioAllocationKey Vendor(string source, uint itemId, bool highQuality) =>
        new(PortfolioAllocationSourceKind.GilVendor, source, itemId, highQuality);

    private static PortfolioAllocationKey Craft(string source, uint itemId, bool highQuality) =>
        new(PortfolioAllocationSourceKind.Craft, source, itemId, highQuality);

    private static PortfolioAllocationKey Owned(string source, uint itemId, bool highQuality) =>
        new(PortfolioAllocationSourceKind.OwnedInstance, source, itemId, highQuality, source);

    private static PortfolioCraftAcquisitionEvidence CraftEvidence(
        TargetInput target,
        IReadOnlyList<PortfolioCraftRecipeEvidence>? recipes = null,
        IReadOnlyList<PortfolioCraftMaterialEvidence>? materials = null) =>
        new(
            target.TargetKey,
            target.CandidateKey,
            [target.Allocation],
            recipes ?? [new("plan-sha", 900, 1, "Finished gear", 0)],
            materials ?? []);

    private static PortfolioVendorIdentity VendorIdentity() =>
        new(10, 20, 30, "Vendor", "Ul'dah", 7, "vendor-catalog-1");

    private sealed record TargetInput(
        string TargetKey,
        string CandidateKey,
        int Priority,
        PortfolioProgressionHorizon Horizon,
        string EvidenceGeneration,
        PortfolioAllocationKey Allocation,
        uint ObservedAvailableQuantity);

    private sealed record FixtureData(
        OutfitterPortfolioPlan Plan,
        PortfolioAuthorityEnvelope Authority,
        PortfolioAcquisitionTransfer Transfer);

    private sealed class RecordingIpcAdapter : IMarketMafiosoAcquisitionIpcAdapter
    {
        public bool HasFunction => true;
        public string? Request { get; private set; }

        public string Invoke(string requestJson)
        {
            Request = requestJson;
            return JsonSerializer.Serialize(new
            {
                Schema = MarketMafiosoAcquisitionIpcClient.ResponseSchema,
                Accepted = true,
                Error = (string?)null,
            });
        }
    }
}
