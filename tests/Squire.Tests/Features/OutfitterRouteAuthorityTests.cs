using Franthropy.Dalamud.Equipment;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.Acquisition;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterRouteAuthorityTests
{
    [Fact]
    public void ConsumeAndPreflight_BindsExactContractBeforeSpending()
    {
        var fixture = Fixture();

        var session = OutfitterRouteAuthoritySession.Consume(
            fixture.Contract, fixture.Document, fixture.Plan, fixture.Claim, fixture.Store);
        Assert.Equal(OutfitterRouteAuthorityPhase.Preparing, session.State.Phase);

        session.CompletePreflight(fixture.Plan);

        Assert.Equal(OutfitterRouteAuthorityPhase.Active, session.State.Phase);
        Assert.Equal(fixture.Contract.ContractId, fixture.Store.State!.ContractId);
        var line = Assert.Single(session.State.Lines);
        Assert.Equal("line-1", line.LineId);
        Assert.Equal(EquipmentQuality.High, line.Quality);
    }

    [Fact]
    public void ConsumeAndPreflight_AcceptsServerCanonicalExactQualityPolicy()
    {
        var fixture = Fixture();
        var canonicalClaim = fixture.Claim with
        {
            Lines = fixture.Claim.Lines.Select(line => line with { HqPolicy = "HqOnly" }).ToArray(),
        };
        var canonicalPlan = fixture.Plan with
        {
            Lines = fixture.Plan.Lines.Select(line => line with { HqPolicy = "HqOnly" }).ToArray(),
            WorldBatches = fixture.Plan.WorldBatches.Select(batch => batch with
            {
                ItemSubtasks = batch.ItemSubtasks.Select(subtask => subtask with { HqPolicy = "HqOnly" }).ToArray(),
            }).ToArray(),
        };

        var session = OutfitterRouteAuthoritySession.Consume(
            fixture.Contract, fixture.Document, canonicalPlan, canonicalClaim, fixture.Store);
        session.CompletePreflight(canonicalPlan);

        Assert.Equal(OutfitterRouteAuthorityPhase.Active, session.State.Phase);
    }

    [Fact]
    public void CandidateGuard_EnforcesExactQualityQuantityAndLayeredCaps()
    {
        var fixture = Fixture();
        var session = Active(fixture);
        var subtask = fixture.Plan.WorldBatches[0].ItemSubtasks[0];

        Assert.True(session.AuthorizeCandidate(subtask, Candidate(quantity: 1, unitPrice: 100, isHq: true)).IsValid);
        Assert.False(session.AuthorizeCandidate(subtask, Candidate(quantity: 1, unitPrice: 100, isHq: false)).IsValid);
        Assert.False(session.AuthorizeCandidate(subtask, Candidate(quantity: 3, unitPrice: 100, isHq: true)).IsValid);
        Assert.False(session.AuthorizeCandidate(subtask, Candidate(quantity: 1, unitPrice: 101, isHq: true)).IsValid);
        Assert.False(session.AuthorizeCandidate(subtask, Candidate(
            quantity: 0,
            unitPrice: 0,
            isHq: true,
            status: MarketAcquisitionLiveCandidateStatuses.IncompleteListingCoverage)).IsValid);
    }

    [Fact]
    public void ConfirmedPurchase_BecomesPersistedSunkStateAndRecoveryUsesOnlyRemainingAuthority()
    {
        var fixture = Fixture();
        var session = Active(fixture);
        session.RecordPurchase("line-1", Purchase(quantity: 1, unitPrice: 100, isHq: true), fixture.Plan);

        session.EvaluateRouteEnd(fixture.Plan);
        var remaining = session.CreateRecoveryClaim(fixture.Claim);

        Assert.Equal(OutfitterRouteAuthorityPhase.RecoveryNeeded, session.State.Phase);
        Assert.Equal(100ul, session.State.TotalSpentGil);
        var line = Assert.Single(remaining.Lines);
        Assert.Equal(1u, line.TargetQuantity);
        Assert.Equal(100u, line.GilCap);
        Assert.Equal(100u, line.MaxUnitPrice);
        Assert.Equal(1u, fixture.Store.State!.Lines[0].PurchasedQuantity);
        var receipt = Assert.Single(fixture.Store.State.SunkPurchases);
        Assert.Equal(OutfitterRouteSunkPurchase.CurrentSchemaVersion, receipt.SchemaVersion);
        Assert.Equal(OutfitterDryRunExecutionStateRestorer.ComputeReceiptId(receipt), receipt.ReceiptId);
        session.RecordPurchase("line-1", Purchase(quantity: 1, unitPrice: 100, isHq: true), fixture.Plan);
        Assert.Equal(100ul, session.State.TotalSpentGil);
        Assert.Single(session.State.SunkPurchases);
    }

    [Fact]
    public void RepeatedExhaustedRecoveryWithoutProgress_PausesInsteadOfLooping()
    {
        var fixture = Fixture();
        var session = Active(fixture);
        var recoveryPlan = WithPlannedTotals(fixture.Plan, 1, 100);

        session.EvaluateRouteEnd(fixture.Plan);
        session.BeginRecovery(recoveryPlan);
        session.EvaluateRouteEnd(recoveryPlan);
        session.BeginRecovery(recoveryPlan);
        session.EvaluateRouteEnd(recoveryPlan);

        Assert.Equal(OutfitterRouteAuthorityPhase.Paused, session.State.Phase);
        Assert.Contains("same exhausted route", session.State.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedRestartPreflight_PreservesPersistedSunkPurchases()
    {
        var fixture = Fixture();
        var session = Active(fixture);
        session.RecordPurchase("line-1", Purchase(1, 100, true), fixture.Plan);

        Assert.Throws<InvalidOperationException>(() => OutfitterRouteAuthoritySession.Consume(
            fixture.Contract,
            fixture.Document,
            fixture.Plan with { Status = "Incomplete" },
            fixture.Claim,
            fixture.Store));

        Assert.Equal(OutfitterRouteAuthorityPhase.Paused, fixture.Store.State!.Phase);
        Assert.Equal(1u, fixture.Store.State.Lines[0].PurchasedQuantity);
        Assert.Equal(100ul, fixture.Store.State.TotalSpentGil);
        Assert.Single(fixture.Store.State.SunkPurchases);
    }

    [Fact]
    public void Consume_RejectsChangedWorkbenchAndOutOfScopeWorld()
    {
        var fixture = Fixture();
        var changed = fixture.Document with { LocalRevision = fixture.Document.LocalRevision + 1 };
        Assert.Throws<InvalidOperationException>(() => OutfitterRouteAuthoritySession.Consume(
            fixture.Contract, changed, fixture.Plan, fixture.Claim));

        var badPlan = fixture.Plan with
        {
            WorldBatches = [fixture.Plan.WorldBatches[0] with { WorldName = "Ravana" }],
        };
        Assert.Throws<InvalidOperationException>(() => OutfitterRouteAuthoritySession.Consume(
            fixture.Contract, fixture.Document, badPlan, fixture.Claim));
    }

    [Fact]
    public void FinalizedSweepScope_EnforcesRegionCurrentDataCenterAndExplicitDataCenters()
    {
        var region = Fixture("Region");
        var primalPlan = WithWorld(region.Plan, "Behemoth", "Primal");
        Assert.NotNull(OutfitterRouteAuthoritySession.Consume(
            region.Contract, region.Document, primalPlan, region.Claim));

        var currentDataCenter = Fixture("CurrentDataCenter");
        Assert.Contains("Siren", currentDataCenter.Contract.AuthorizedWorlds);
        Assert.DoesNotContain("Behemoth", currentDataCenter.Contract.AuthorizedWorlds);
        Assert.Throws<InvalidOperationException>(() => OutfitterRouteAuthoritySession.Consume(
            currentDataCenter.Contract, currentDataCenter.Document, primalPlan, currentDataCenter.Claim));

        var explicitDataCenters = Fixture("DataCenters", ["Crystal"]);
        Assert.Contains("Balmung", explicitDataCenters.Contract.AuthorizedWorlds);
        Assert.DoesNotContain("Behemoth", explicitDataCenters.Contract.AuthorizedWorlds);
        Assert.Throws<InvalidOperationException>(() => OutfitterRouteAuthoritySession.Consume(
            explicitDataCenters.Contract, explicitDataCenters.Document, primalPlan, explicitDataCenters.Claim));
        Assert.NotNull(OutfitterRouteAuthoritySession.Consume(
            explicitDataCenters.Contract,
            explicitDataCenters.Document,
            WithWorld(explicitDataCenters.Plan, "Balmung", "Crystal"),
            explicitDataCenters.Claim));
    }

    [Fact]
    public void ConfigurationStore_RoundTripsVersionedSunkState()
    {
        var fixture = Fixture();
        var session = Active(fixture);
        session.RecordPurchase("line-1", Purchase(1, 100, true), fixture.Plan);
        var config = new Configuration();
        var store = new ConfigurationOutfitterRouteExecutionStateStore(config, () => { });

        store.Save(session.State);
        var restored = store.Restore();

        Assert.NotNull(restored);
        Assert.Equal(session.State.ContractId, restored!.ContractId);
        Assert.Equal(1u, restored.Lines[0].PurchasedQuantity);
        Assert.Equal(100ul, restored.TotalSpentGil);
        Assert.Single(restored.SunkPurchases);
    }

    private static OutfitterRouteAuthoritySession Active(FixtureData fixture)
    {
        var session = OutfitterRouteAuthoritySession.Consume(
            fixture.Contract, fixture.Document, fixture.Plan, fixture.Claim, fixture.Store);
        session.CompletePreflight(fixture.Plan);
        return session;
    }

    private static FixtureData Fixture(string sweepScope = "Region", IReadOnlyList<string>? sweepDataCenters = null)
    {
        var document = MarketAcquisitionRequestDocument.CreateDefault("Fran", "Siren") with
        {
            Region = "North America",
            WorldMode = "AllWorldSweep",
            SweepScope = sweepScope,
            SweepDataCenters = (sweepDataCenters ?? []).ToList(),
        };
        document = OutfitterWorkbenchAuthorityService.Stage(document, OutfitterWorkbenchAuthorityTests.Transfer());
        document = OutfitterWorkbenchAuthorityService.Finalize(document);
        var contract = document.OutfitterAuthority!.FinalizedContract!;
        var claim = new MarketAcquisitionClaimView
        {
            Id = "request-1",
            ClaimToken = "claim-token",
            Status = "AcceptedInPlugin",
            TargetCharacterName = "Fran",
            TargetWorld = "Siren",
            Region = "North America",
            WorldMode = "Recommended",
            Lines =
            [
                new MarketAcquisitionBatchLineView
                {
                    LineId = "line-1",
                    Ordinal = 0,
                    ItemId = 10,
                    ItemName = "Exact HQ Ring",
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = 2,
                    HqPolicy = "HQOnly",
                    MaxUnitPrice = 100,
                    GilCap = 200,
                },
            ],
        };
        var listing = new MarketAcquisitionPlannedListing
        {
            LineId = "line-1",
            ItemId = 10,
            ItemName = "Exact HQ Ring",
            ListingId = "listing-1",
            Quantity = 2,
            UnitPrice = 100,
            TotalGil = 200,
            IsHq = true,
        };
        var subtask = new MarketAcquisitionWorldItemSubtask
        {
            LineId = "line-1",
            ItemId = 10,
            ItemName = "Exact HQ Ring",
            WorldName = "Siren",
            DataCenter = "Aether",
            QuantityMode = "TargetQuantity",
            RequestedQuantity = 2,
            HqPolicy = "HQOnly",
            MaxUnitPrice = 100,
            GilCap = 200,
            PlannedQuantity = 2,
            PlannedGil = 200,
            Listings = [listing],
        };
        var plan = new MarketAcquisitionPlan
        {
            RequestId = claim.Id,
            Status = "Ready",
            WorldMode = "Recommended",
            PreparedAtUtc = DateTimeOffset.Parse("2026-07-21T18:00:00Z"),
            Lines =
            [
                new MarketAcquisitionPlanLine
                {
                    LineId = "line-1",
                    ItemId = 10,
                    ItemName = "Exact HQ Ring",
                    QuantityMode = "TargetQuantity",
                    RequestedQuantity = 2,
                    HqPolicy = "HQOnly",
                    MaxUnitPrice = 100,
                    GilCap = 200,
                    Status = "Ready",
                    PlannedQuantity = 2,
                    PlannedGil = 200,
                },
            ],
            WorldBatches =
            [
                new MarketAcquisitionWorldBatch
                {
                    WorldName = "Siren",
                    DataCenter = "Aether",
                    PlannedQuantity = 2,
                    PlannedGil = 200,
                    ItemSubtasks = [subtask],
                    Listings = [listing],
                },
            ],
        };
        return new(document, contract, claim, plan, new MemoryStore());
    }

    private static MarketAcquisitionPlan WithWorld(MarketAcquisitionPlan plan, string world, string dataCenter)
    {
        var batch = plan.WorldBatches[0];
        var subtask = batch.ItemSubtasks[0] with { WorldName = world, DataCenter = dataCenter };
        return plan with
        {
            WorldBatches = [batch with { WorldName = world, DataCenter = dataCenter, ItemSubtasks = [subtask] }],
        };
    }

    private static MarketAcquisitionLiveCandidatePlan Candidate(
        uint quantity,
        uint unitPrice,
        bool isHq,
        string status = "Ready") => new()
    {
        Status = status,
        ListingReadState = MarketBoardListingReadState.FreshComplete,
        WouldBuyQuantity = quantity,
        WouldSpendGil = quantity * unitPrice,
        Rows = quantity == 0
            ? []
            :
            [
                new MarketAcquisitionLiveCandidateRow
                {
                    Decision = "WouldBuy",
                    LiveListing = new MarketBoardLiveListing
                    {
                        ItemId = 10,
                        WorldName = "Siren",
                        ListingId = "live-1",
                        RetainerId = "retainer-1",
                        Quantity = quantity,
                        UnitPrice = unitPrice,
                        IsHq = isHq,
                    },
                },
            ],
    };

    private static MarketBoardPurchaseCandidate Purchase(uint quantity, uint unitPrice, bool isHq) => new()
    {
        ItemId = 10,
        WorldName = "Siren",
        ListingId = "live-1",
        RetainerId = "retainer-1",
        Quantity = quantity,
        UnitPrice = unitPrice,
        IsHq = isHq,
    };

    private static MarketAcquisitionPlan WithPlannedTotals(
        MarketAcquisitionPlan plan,
        uint quantity,
        uint gil)
    {
        var listing = plan.WorldBatches[0].Listings[0] with
        {
            Quantity = quantity,
            TotalGil = gil,
        };
        var subtask = plan.WorldBatches[0].ItemSubtasks[0] with
        {
            PlannedQuantity = quantity,
            PlannedGil = gil,
            Listings = [listing],
        };
        return plan with
        {
            Lines = [plan.Lines[0] with { PlannedQuantity = quantity, PlannedGil = gil }],
            WorldBatches =
            [
                plan.WorldBatches[0] with
                {
                    PlannedQuantity = quantity,
                    PlannedGil = gil,
                    ItemSubtasks = [subtask],
                    Listings = [listing],
                },
            ],
        };
    }

    private sealed record FixtureData(
        MarketAcquisitionRequestDocument Document,
        OutfitterExecutionContract Contract,
        MarketAcquisitionClaimView Claim,
        MarketAcquisitionPlan Plan,
        MemoryStore Store);

    private sealed class MemoryStore : IOutfitterRouteExecutionStateStore
    {
        public OutfitterRouteExecutionState? State { get; private set; }
        public OutfitterRouteExecutionState? Restore() => State;
        public void Save(OutfitterRouteExecutionState state) => State = state;
        public void Clear() => State = null;
    }
}
