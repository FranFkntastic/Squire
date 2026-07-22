using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterWorkbenchAuthorityTests
{
    [Fact]
    public void Stage_ReplacesConflictingItemAndPreservesManualWorkbenchLines()
    {
        var document = MarketAcquisitionRequestDocument.CreateDefault() with
        {
            Lines =
            [
                new() { ItemId = 1, ItemName = "Manual item", HqPolicy = "Either" },
                new() { ItemId = 10, ItemName = "Old ambiguous ring", HqPolicy = "Either" },
            ],
        };

        var staged = OutfitterWorkbenchAuthorityService.Stage(document, Transfer(), 10);

        Assert.Equal(2, staged.Lines.Count);
        Assert.Contains(staged.Lines, line => line.ItemId == 1 && line.HqPolicy == "Either");
        var exact = Assert.Single(staged.Lines, line => line.ItemId == 10);
        Assert.Equal("HQOnly", exact.HqPolicy);
        Assert.Equal("TargetQuantity", exact.QuantityMode);
        Assert.Equal(2u, exact.TargetQuantity);
        Assert.Equal(110u, exact.MaxUnitPrice);
        Assert.Equal(220u, exact.GilCap);
        Assert.Equal(2, staged.LocalRevision);
        Assert.Equal(220ul, staged.OutfitterAuthority!.SquirePlanCapGil);
        Assert.Equal(OutfitterWorkbenchAuthority.CrossWorldExactQualityV1, staged.OutfitterAuthority.RecoveryPolicyId);
    }

    [Fact]
    public void UpdatePriceFlex_DerivesAndPersistsFixedAbsoluteCaps()
    {
        var staged = OutfitterWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault(), Transfer());

        var updated = OutfitterWorkbenchAuthorityService.UpdatePriceFlex(staged, 25);

        var line = Assert.Single(updated.Lines);
        Assert.Equal(125u, line.MaxUnitPrice);
        Assert.Equal(250u, line.GilCap);
        Assert.Equal(250ul, updated.OutfitterAuthority!.SquirePlanCapGil);
        Assert.Equal(25, updated.OutfitterAuthority.PriceFlexPercent);
        Assert.Null(updated.OutfitterAuthority.FinalizedContract);
    }

    [Fact]
    public void Stage_PreservesCraftingMaterialKindThroughVisibleWorkbenchLine()
    {
        var transfer = Transfer();
        transfer = transfer with
        {
            MarketLots = transfer.MarketLots.Select(lot => lot with { ItemKind = "Crafting material" }).ToArray(),
        };

        var staged = OutfitterWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault(),
            transfer);

        var line = Assert.Single(staged.Lines);
        Assert.Equal("Crafting material", line.ItemKind);
        Assert.Equal("Crafting material", Assert.Single(staged.OutfitterAuthority!.Lines).ItemKind);
    }

    [Fact]
    public void SemanticEdit_InvalidatesLineageButRetainsHistoricalTransfer()
    {
        var staged = OutfitterWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault(), Transfer());
        var changed = staged with
        {
            Lines = [staged.Lines[0] with { HqPolicy = "NQOnly" }],
            LocalRevision = staged.LocalRevision + 1,
        };

        var reconciled = OutfitterWorkbenchAuthorityService.ReconcileEdit(staged, changed);

        Assert.Equal(OutfitterWorkbenchLineageState.Invalidated, reconciled.OutfitterAuthority!.LineageState);
        Assert.Equal("selected-solution", reconciled.OutfitterAuthority.Transfer.SelectedSolutionId);
        Assert.Contains("missing or duplicated", reconciled.OutfitterAuthority.InvalidationReason, StringComparison.Ordinal);
        Assert.False(OutfitterWorkbenchAuthorityService.ValidateForFinalization(reconciled).IsValid);
    }

    [Fact]
    public void HiddenPricingMismatch_CannotMasqueradeAsVisibleApprovalEnvelope()
    {
        var staged = OutfitterWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault(), Transfer());
        var changed = staged with { Lines = [staged.Lines[0] with { MaxUnitPrice = 999 }] };
        var reconciled = OutfitterWorkbenchAuthorityService.ReconcileEdit(staged, changed);

        Assert.True(reconciled.OutfitterAuthority!.IsLineageValid);
        var validation = OutfitterWorkbenchAuthorityService.ValidateForFinalization(reconciled);
        Assert.False(validation.IsValid);
        Assert.Contains("approval envelope", validation.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Finalize_BindsVersionedVisibleAuthorityToDocumentRevisionAndIntent()
    {
        var staged = OutfitterWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault(), Transfer(), 15);

        var finalized = OutfitterWorkbenchAuthorityService.Finalize(staged);
        var contract = Assert.IsType<OutfitterExecutionContract>(finalized.OutfitterAuthority!.FinalizedContract);

        Assert.Equal(staged.LocalRequestId, contract.WorkbenchDocumentId);
        Assert.Equal(staged.LocalRevision, contract.WorkbenchRevision);
        Assert.Equal(OutfitterWorkbenchAuthority.CrossWorldExactQualityV1, contract.RecoveryPolicyId);
        Assert.Equal(230ul, contract.SquirePlanCapGil);
        Assert.Equal(OutfitterWorkbenchAuthorityService.ComputeCanonicalIntentHash(staged), contract.CanonicalIntentHash);
        Assert.Equal(OutfitterExecutionContract.CurrentSchemaVersion, contract.SchemaVersion);
        Assert.Equal(staged.TargetCharacterName, contract.TargetCharacterName);
        Assert.Equal(staged.TargetWorld, contract.TargetWorld);

        var repeated = OutfitterWorkbenchAuthorityService.Finalize(finalized);
        Assert.Same(finalized, repeated);
    }

    [Fact]
    public void Finalize_ReplacesLegacyContractThatLacksExplicitWorldAuthority()
    {
        var finalized = OutfitterWorkbenchAuthorityService.Finalize(
            OutfitterWorkbenchAuthorityService.Stage(
                MarketAcquisitionRequestDocument.CreateDefault("Fran", "Siren"), Transfer()));
        var current = finalized.OutfitterAuthority!.FinalizedContract!;
        var legacy = current with
        {
            SchemaVersion = "marketmafioso-squire-outfitter-execution-contract/v1",
            AuthorizedWorlds = [],
        };
        finalized = finalized with
        {
            OutfitterAuthority = finalized.OutfitterAuthority with { FinalizedContract = legacy },
        };

        var upgraded = OutfitterWorkbenchAuthorityService.Finalize(finalized);
        var contract = upgraded.OutfitterAuthority!.FinalizedContract!;

        Assert.Equal(OutfitterExecutionContract.CurrentSchemaVersion, contract.SchemaVersion);
        Assert.NotEqual(legacy.ContractId, contract.ContractId);
        Assert.NotEmpty(contract.AuthorizedWorlds);
    }

    [Fact]
    public void Persistence_RoundTripsAuthorityWhileServerIntentHashRemainsBuyListOnly()
    {
        var staged = OutfitterWorkbenchAuthorityService.Finalize(
            OutfitterWorkbenchAuthorityService.Stage(
                MarketAcquisitionRequestDocument.CreateDefault(), Transfer(), 5));
        var config = new Configuration();

        MarketAcquisitionRequestDocumentPersistence.Save(config, staged);
        var restored = MarketAcquisitionRequestDocumentPersistence.Restore(config);

        var restoredAuthority = Assert.IsType<OutfitterWorkbenchAuthority>(restored.OutfitterAuthority);
        var stagedAuthority = Assert.IsType<OutfitterWorkbenchAuthority>(staged.OutfitterAuthority);
        Assert.NotNull(restoredAuthority.FinalizedContract);
        Assert.Equal("selected-solution", restoredAuthority.Transfer.SelectedSolutionId);
        Assert.Equal(stagedAuthority.SquirePlanCapGil, restoredAuthority.SquirePlanCapGil);
        Assert.Equal(
            MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(staged with { OutfitterAuthority = null }),
            MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(staged));
    }

    [Fact]
    public async Task RemoteRefresh_PreservesHistoryButInvalidatesChangedExactSolution()
    {
        var staged = OutfitterWorkbenchAuthorityService.Finalize(
            OutfitterWorkbenchAuthorityService.Stage(
                MarketAcquisitionRequestDocument.CreateDefault(), Transfer()));
        var remote = new MarketAcquisitionRequestView
        {
            Id = "request-1",
            Revision = 2,
            Region = "North America",
            WorldMode = "Recommended",
            SweepScope = "Region",
            Lines =
            [
                new MarketAcquisitionBatchLineView
                {
                    ItemId = 10,
                    ItemName = "Exact HQ Ring",
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = 1,
                    HqPolicy = "HQOnly",
                    MaxUnitPrice = 100,
                    GilCap = 200,
                },
            ],
        };
        var remoteDocument = MarketAcquisitionRequestDocumentMapper.FromRequestView(remote);
        var initial = staged with { RemoteRequestId = remote.Id, RemoteRevision = 1, SyncStatus = "SyncedClean" };
        var controller = new MarketAcquisitionRequestBuilderController(
            initial,
            current => Task.FromResult(new MarketAcquisitionRequestBuilderSyncOutcome(current, string.Empty)),
            _ => Task.FromResult(new MarketAcquisitionRequestBuilderRefreshOutcome(remoteDocument, remote, "refreshed")),
            (_, _) => { },
            _ => { });

        await controller.RefreshAsync();

        var authority = Assert.IsType<OutfitterWorkbenchAuthority>(controller.Document.OutfitterAuthority);
        Assert.Equal("selected-solution", authority.Transfer.SelectedSolutionId);
        Assert.Equal(OutfitterWorkbenchLineageState.Invalidated, authority.LineageState);
        Assert.Null(authority.FinalizedContract);
    }

    internal static OutfitterWorkbenchTransfer Transfer()
    {
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var offerKey = new EquipmentOfferKey(
            10,
            EquipmentQuality.High,
            EquipmentAcquisitionSourceKind.MarketBoard,
            "market:test:10:High");
        return new(
            OutfitterWorkbenchTransfer.CurrentSchemaVersion,
            OutfitterWorkbenchTransfer.SquireOutfitterOrigin,
            "selected-solution",
            "nomination",
            new("test-profile", "v1"),
            new("ordinary", 16, 100, "ordinary nodes", ["gathering"]),
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 7, "evidence/v1", "universalis", "North America", OutfitterMarketCoverageMode.ExhaustiveWithinScope, now),
            [new(EquipmentLoadoutPosition.LeftRing, offerKey, 2, "listing-1", "Market board - Siren")],
            [new(offerKey, "Exact HQ Ring", 2, 2, "Siren", 100, 200, "listing-1", "source-r1", now, "Retainer", "retainer-1")],
            200);
    }
}
