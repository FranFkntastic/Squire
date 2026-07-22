using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Squire.Outfitter.Crafting;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterCraftingContractsTests
{
    private static readonly Guid EvidenceGeneration = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid EquipmentGeneration = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid CaptureId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
    private static readonly CharacterScope CrafterCharacter = new(1234, "Test Crafter", 74);
    private static readonly DateTimeOffset ReviewedAt = DateTimeOffset.Parse("2026-07-20T00:00:00Z");
    private static readonly DateTimeOffset CapturedAt = DateTimeOffset.Parse("2026-07-20T00:00:30Z");
    private static readonly DateTimeOffset PublishedAt = DateTimeOffset.Parse("2026-07-20T00:00:45Z");
    private static readonly DateTimeOffset PlanBuiltAt = DateTimeOffset.Parse("2026-07-20T00:01:00Z");
    private static readonly DateTimeOffset ComparisonBuiltAt = DateTimeOffset.Parse("2026-07-20T00:02:00Z");

    [Fact]
    public void CompleteNqPlanAndComparison_AreValidInternalContractOnlyValues()
    {
        var plan = Plan();
        var comparison = Comparison(plan);

        Assert.True(plan.Validate(requireEconomyReady: true).IsValid);
        Assert.True(comparison.Validate().IsValid);
    }

    [Fact]
    public void ContractOnlyRuntimeValues_RejectSystemTextJsonSerializationAndReplayDeserialization()
    {
        var plan = Plan();
        var comparison = Comparison(plan);

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(plan));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<OutfitterCraftPlan>("{}"));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(comparison));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<CraftCostComparison>("{}"));
    }

    [Fact]
    public void ContractOnlyRuntimeValues_RejectNewtonsoftJsonSerializationAndReplayDeserialization()
    {
        var plan = Plan();
        var comparison = Comparison(plan);

        Assert.Throws<NotSupportedException>(() => Newtonsoft.Json.JsonConvert.SerializeObject(plan));
        Assert.Throws<NotSupportedException>(() => Newtonsoft.Json.JsonConvert.DeserializeObject<OutfitterCraftPlan>("{}"));
        Assert.Throws<NotSupportedException>(() => Newtonsoft.Json.JsonConvert.SerializeObject(comparison));
        Assert.Throws<NotSupportedException>(() => Newtonsoft.Json.JsonConvert.DeserializeObject<CraftCostComparison>("{}"));
    }

    [Fact]
    public void PublishedBookFactory_DerivesCanonicalOrderIndependentContentIdentity()
    {
        var book = EvidenceBook(includeCrossWorldDuplicate: true);
        var reordered = book with
        {
            Coverage = book.Coverage with { QueriedItemIds = book.Coverage.QueriedItemIds.Reverse().ToArray() },
            Items = book.Items
                .Reverse()
                .Select(item => item with { Listings = item.Listings.Reverse().ToArray() })
                .ToArray(),
        };

        var first = CraftMarketEvidenceReference.FromPublishedBook(book);
        var second = CraftMarketEvidenceReference.FromPublishedBook(reordered);

        Assert.Equal(first.ContentIdentity, second.ContentIdentity);
        Assert.Equal(new uint[] { 100, 300 }, first.QueriedItemIds.ToArray());
        Assert.Equal(new uint[] { 100, 300 }, first.ItemSourceRevisions.Select(item => item.ItemId).ToArray());
    }

    [Fact]
    public void MarketIdentity_DistinguishesSameListingIdAcrossWorldsInCanonicalAllocationKeys()
    {
        var evidence = CraftMarketEvidenceReference.FromPublishedBook(EvidenceBook(includeCrossWorldDuplicate: true));
        var listings = evidence.Listings
            .Where(listing => listing.ItemId == 100 && listing.ListingId == "listing-100")
            .ToArray();
        var sources = listings
            .Select(listing => new ComparedGearMarketSourceIdentity(
                evidence,
                listing,
                1,
                listing.AvailableQuantity,
                listing.AvailableQuantity - 1,
                (ulong)listing.AvailableQuantity * listing.UnitPriceGil))
            .ToArray();
        var allocations = sources.Select(source => new ComparedGearAllocation(
            new(
                new(source.Listing.ItemId, source.Listing.Quality, source.Kind, source.SourceCatalogKey),
                source.ObservationId),
            1,
            source.TotalPriceGil,
            source)).ToArray();

        Assert.Equal(new uint[] { 74, 75 }, listings.Select(listing => listing.WorldId).ToArray());
        Assert.NotEqual(sources[0].SourceCatalogKey, sources[1].SourceCatalogKey);
        Assert.NotEqual(sources[0].ObservationId, sources[1].ObservationId);
        Assert.True(ComparedGearAllocationIdentity.TryCompute(allocations[0], out var first));
        Assert.True(ComparedGearAllocationIdentity.TryCompute(allocations[1], out var second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void PublishedBookIdentity_ChangesForEveryPublicationDecisionDimension()
    {
        var baseline = CraftMarketEvidenceReference.FromPublishedBook(EvidenceBook());
        var candidates = new[]
        {
            CraftMarketEvidenceReference.FromPublishedBook(EvidenceBook(publishedAt: PublishedAt.AddSeconds(1))),
            CraftMarketEvidenceReference.FromPublishedBook(EvidenceBook(materialSourceRevision: "material-r2")),
            CraftMarketEvidenceReference.FromPublishedBook(EvidenceBook(worldName: "Other World")),
            CraftMarketEvidenceReference.FromPublishedBook(EvidenceBook(unitPriceGil: 11)),
        };
        var coverageBook = EvidenceBook();
        var coverage = CraftMarketEvidenceReference.FromPublishedBook(coverageBook with
        {
            Coverage = coverageBook.Coverage with { ListingLimit = 99 },
        });

        Assert.All(candidates.Append(coverage), candidate => Assert.NotEqual(baseline.ContentIdentity, candidate.ContentIdentity));
    }

    [Fact]
    public void PublishedBookFactory_RejectsUnpublishedPartialIncoherentAndStaleEvidence()
    {
        var book = EvidenceBook();
        var candidates = new[]
        {
            book with { PublishedAtUtc = null },
            book with { Status = OutfitterMarketEvidenceGenerationStatus.Partial },
            book with { Coverage = book.Coverage with { QueriedItemIds = [100u, 999u] } },
            EvidenceBook(capturedAt: PublishedAt.AddTicks(1)),
            EvidenceBook(reviewedAt: CapturedAt.AddTicks(1)),
            EvidenceBook(
                reviewedAt: ReviewedAt,
                capturedAt: ReviewedAt,
                publishedAt: ReviewedAt + CraftMarketEvidenceFreshness.TimeToLive + TimeSpan.FromTicks(1)),
        };

        Assert.All(candidates, candidate =>
            Assert.Throws<InvalidOperationException>(() => CraftMarketEvidenceReference.FromPublishedBook(candidate)));
    }

    [Fact]
    public void PublishedBookFactory_RetainsStructurallyValidOldDepthRowsForIdentityOnly()
    {
        var book = EvidenceBook(
            reviewedAt: CapturedAt.AddMinutes(-20),
            capturedAt: CapturedAt,
            publishedAt: PublishedAt);

        var reference = CraftMarketEvidenceReference.FromPublishedBook(book);

        Assert.NotEmpty(reference.Listings);
        Assert.All(reference.Listings, listing => Assert.False(CraftMarketEvidenceFreshness.IsFresh(
            listing.ReviewedAtUtc,
            listing.CapturedAtUtc,
            reference.PublishedAtUtc,
            PlanBuiltAt)));
    }

    [Fact]
    public void PublishedBookFactory_RejectsOversizedCollectionsBeforeEnumeratingThem()
    {
        var book = EvidenceBook();
        var firstItem = book.Items[0];
        var firstListing = firstItem.Listings[0];
        var candidates = new[]
        {
            book with
            {
                Coverage = book.Coverage with
                {
                    QueriedItemIds = new EnumerationForbiddenReadOnlyList<uint>(3, 100),
                },
            },
            book with
            {
                Items = new EnumerationForbiddenReadOnlyList<OutfitterMarketItemEvidence>(3, firstItem),
            },
            book with
            {
                Coverage = book.Coverage with { ListingLimit = 1 },
                Items =
                [
                    firstItem with
                    {
                        Listings = new EnumerationForbiddenReadOnlyList<OutfitterMarketListingEvidence>(2, firstListing),
                    },
                    book.Items[1],
                ],
            },
        };

        Assert.All(candidates, candidate =>
            Assert.Throws<InvalidOperationException>(() => CraftMarketEvidenceReference.FromPublishedBook(candidate)));
    }

    [Fact]
    public void CrafterAuthority_RequiresTrustedFreshCaptureAndBindsCaptureIdentity()
    {
        var baseline = Baseline();
        var recaptured = Baseline(
            captureId: Guid.NewGuid(),
            equipmentGeneration: Guid.NewGuid(),
            captureCompletedAt: CapturedAt.AddSeconds(1));

        var first = OutfitterCrafterObservationIdentity.FromBaseline(baseline, PlanBuiltAt);
        var second = OutfitterCrafterObservationIdentity.FromBaseline(recaptured, PlanBuiltAt);

        Assert.Equal(first.BaselineAuthorityFingerprint, second.BaselineAuthorityFingerprint);
        Assert.NotEqual(first.CaptureId, second.CaptureId);
        Assert.NotEqual(first.EquipmentGenerationId, second.EquipmentGenerationId);
        Assert.False(first.IsLevelSynced);
    }

    [Fact]
    public void CrafterAuthority_RejectsFabricatedLoggedOutDefaultAndStaleBaselines()
    {
        var valid = Baseline();
        var fabricated = valid with { CaptureProvenance = null };
        var loggedOut = WithIdentity(valid, valid.EquipmentSnapshot!.Identity with { IsLoggedIn = false });
        var defaultCapture = valid with
        {
            CaptureProvenance = valid.CaptureProvenance! with { CaptureId = Guid.Empty },
        };

        Assert.Throws<InvalidOperationException>(() => OutfitterCrafterObservationIdentity.FromBaseline(fabricated, PlanBuiltAt));
        Assert.Throws<InvalidOperationException>(() => OutfitterCrafterObservationIdentity.FromBaseline(loggedOut, PlanBuiltAt));
        Assert.Throws<InvalidOperationException>(() => OutfitterCrafterObservationIdentity.FromBaseline(defaultCapture, PlanBuiltAt));
        Assert.Throws<InvalidOperationException>(() => OutfitterCrafterObservationIdentity.FromBaseline(
            valid,
            CapturedAt + PlayerAdvisorCaptureFreshness.TimeToLive + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void CrafterAuthority_RejectsIncompleteOrNonCanonicalEquipmentSnapshot()
    {
        var valid = Baseline(materiaId: 55);
        var wrongIdentity = WithIdentity(valid, valid.EquipmentSnapshot!.Identity with
        {
            Scope = new CharacterScope(9999, "Other Crafter", 74),
        });
        var incomplete = valid with
        {
            EquipmentSnapshot = valid.EquipmentSnapshot! with
            {
                Diagnostics = new([
                    new("identity", SnapshotComponentStatus.Complete),
                    new("equipped", SnapshotComponentStatus.Partial),
                ]),
            },
        };
        var duplicateSlot = valid with
        {
            EquippedSlots = [valid.EquippedSlots[0], .. valid.EquippedSlots.Take(11)],
        };
        var extraEquipped = valid with
        {
            EquipmentSnapshot = valid.EquipmentSnapshot! with
            {
                Instances = [.. valid.EquipmentSnapshot.Instances, EquipmentInstance(5, 999, false, ReviewedAt)],
            },
        };
        var changedFingerprint = valid with
        {
            EquipmentSnapshot = valid.EquipmentSnapshot! with
            {
                Instances = valid.EquipmentSnapshot.Instances.Select(instance =>
                    instance with { Fingerprint = instance.Fingerprint with { Condition = 29_999 } }).ToArray(),
            },
        };

        Assert.All([wrongIdentity, incomplete, duplicateSlot, extraEquipped, changedFingerprint], candidate =>
            Assert.Throws<InvalidOperationException>(() =>
                OutfitterCrafterObservationIdentity.FromBaseline(candidate, PlanBuiltAt)));
    }

    [Fact]
    public void CrafterAuthority_AcceptsOneProvenSupplementalSoulCrystal()
    {
        var valid = Baseline(materiaId: 55);
        const uint soulCrystalItemId = 9_999;
        var definitions = valid.EquipmentSnapshot!.Definitions.ToDictionary(pair => pair.Key, pair => pair.Value);
        definitions.Add(
            soulCrystalItemId,
            EquipmentDefinition(soulCrystalItemId, EquipmentSlot.SoulCrystal, valid.ClassJobId!.Value) with
            {
                IsSoulCrystal = true,
            });
        var withSoulCrystal = valid with
        {
            EquipmentSnapshot = valid.EquipmentSnapshot with
            {
                Instances = [.. valid.EquipmentSnapshot.Instances, EquipmentInstance(13, soulCrystalItemId, false, ReviewedAt)],
                Definitions = definitions,
            },
        };

        var observation = OutfitterCrafterObservationIdentity.FromBaseline(withSoulCrystal, PlanBuiltAt);

        Assert.Equal(valid.ClassJobId, observation.ClassJobId);
        Assert.Equal(valid.Character, observation.Character);
    }

    [Fact]
    public void CrafterAuthority_RevalidationRejectsStaleAuthorityReplayAndChangedState()
    {
        var original = Baseline(materiaId: 55);
        var plan = Plan(baseline: original);
        var recaptured = Baseline(
            materiaId: 55,
            captureId: Guid.NewGuid(),
            equipmentGeneration: Guid.NewGuid(),
            captureCompletedAt: CapturedAt.AddSeconds(1));
        var changed = Baseline(
            materiaId: 56,
            captureId: Guid.NewGuid(),
            equipmentGeneration: Guid.NewGuid(),
            captureCompletedAt: CapturedAt.AddSeconds(1));

        Assert.True(plan.RevalidateCrafterAuthority(recaptured, PlanBuiltAt));
        Assert.False(plan.RevalidateCrafterAuthority(changed, PlanBuiltAt));
        Assert.False(plan.RevalidateCrafterAuthority(original, CapturedAt + PlayerAdvisorCaptureFreshness.TimeToLive + TimeSpan.FromTicks(1)));
        Assert.False((plan with { BuiltAtUtc = CapturedAt + PlayerAdvisorCaptureFreshness.TimeToLive + TimeSpan.FromTicks(1) }).Validate().IsValid);
    }

    [Fact]
    public void Eligibility_MustMatchExactBaselineAuthorityCharacterJobAndLevel()
    {
        var baseline = Plan();
        var candidates = new[]
        {
            ChangeEligibility(baseline, "root", evidence => evidence with { CrafterAuthorityFingerprint = "forged" }),
            ChangeEligibility(baseline, "root", evidence => evidence with { Character = new(9999, "Other", 74) }),
            ChangeEligibility(baseline, "root", evidence => evidence with { ObservedClassJobId = 9 }),
            ChangeEligibility(baseline, "root", evidence => evidence with { ObservedLevel = 99 }),
        };

        Assert.All(candidates, candidate =>
            Assert.Contains(candidate.Validate().Errors, error => error.Contains("active-job eligibility", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("sub")]
    public void EconomyReadyPlan_RejectsMasterRootOrSubcraft(string nodeId)
    {
        var plan = Plan();
        plan = plan with
        {
            ExpandedNodes = plan.ExpandedNodes.Select(node => node.NodeId == nodeId
                ? node with
                {
                    RecipeUnlockItemId = 999,
                    ResolvedRecipe = node.ResolvedRecipe! with { RecipeUnlockItemId = 999 },
                }
                : node).ToImmutableArray(),
        };

        Assert.Contains(plan.Validate(requireEconomyReady: true).Errors, error => error.Contains("non-master", StringComparison.Ordinal));
    }

    [Fact]
    public void HqAttestation_RemainsDiagnosticOnlyAndNeverMakesEconomyReady()
    {
        var baseline = Plan();
        var attestation = HqAttestation(baseline.ExpandedNodes.Single(node => node.NodeId == "root"), baseline.CrafterObservation);
        var hq = WithRootQuality(baseline, EquipmentQuality.High, attestation);

        Assert.True(hq.Validate().IsValid);
        Assert.False(hq.Validate(requireEconomyReady: true).IsValid);
        Assert.True(Comparison(hq, CraftCostComparisonStatus.DisplayOnly, "HQ outcome is unverified.").Validate().IsValid);
        Assert.False(Comparison(hq).Validate().IsValid);
    }

    [Fact]
    public void MarketMaterialSource_RequiresExactPublishedFreshListingLineage()
    {
        var plan = Plan();
        var line = plan.TerminalMaterials.Single(value => value.Source is OutfitterMarketMaterialSourceIdentity);
        var market = (OutfitterMarketMaterialSourceIdentity)line.Source;
        var candidates = new[]
        {
            ReplaceMaterialSource(plan, line.MaterialKey, market with { EvidenceGenerationId = Guid.NewGuid() }),
            ReplaceMaterialSource(plan, line.MaterialKey, market with { EvidenceRevision = market.EvidenceRevision + 1 }),
            ReplaceMaterialSource(plan, line.MaterialKey, market with { SourceRevision = "other" }),
            ReplaceMaterialSource(plan, line.MaterialKey, market with { ListingId = "forged-listing" }),
            ReplaceMaterialSource(plan, line.MaterialKey, market with { CapturedAtUtc = market.CapturedAtUtc.AddTicks(1) }),
        };

        Assert.All(candidates, candidate => Assert.False(candidate.Validate().IsValid));
        var exactTtl = ReviewedAt + CraftMarketEvidenceFreshness.TimeToLive;
        var stale = exactTtl + TimeSpan.FromTicks(1);
        Assert.True(Plan(
            baseline: Baseline(captureCompletedAt: exactTtl.AddSeconds(-30), snapshotCapturedAt: exactTtl.AddSeconds(-31)),
            builtAt: exactTtl).Validate().IsValid);
        Assert.False(Plan(
            baseline: Baseline(captureCompletedAt: stale.AddSeconds(-30), snapshotCapturedAt: stale.AddSeconds(-31)),
            builtAt: stale).Validate().IsValid);
    }

    [Fact]
    public void PhysicalSourceBurden_CountsWholeMarketLotsAndDeduplicatesVendorLines()
    {
        var plan = Plan();
        var marketLine = plan.TerminalMaterials.Single(line => line.Source is OutfitterMarketMaterialSourceIdentity);
        var vendorLine = plan.TerminalMaterials.Single(line => line.Source is OutfitterGilVendorMaterialSourceIdentity);
        plan = plan with
        {
            TerminalMaterials =
            [
                marketLine,
                vendorLine with { ConsumedQuantity = 1, PurchasedQuantity = 1 },
                vendorLine with { ConsumedQuantity = 2, PurchasedQuantity = 2 },
            ],
        };

        var comparison = Comparison(plan);
        Assert.True(comparison.Validate().IsValid);
        Assert.Equal(1, comparison.Burden.MarketSourceCount);
        Assert.Equal(1, comparison.Burden.VendorSourceCount);
    }

    [Fact]
    public void ComparedMarketSource_RequiresKeysDerivedExactlyFromEvidence()
    {
        var plan = Plan();
        var comparison = Comparison(plan);
        var source = Assert.IsType<ComparedGearMarketSourceIdentity>(comparison.ComparedAllocation.Source);
        var wrongAllocation = comparison.ComparedAllocation with
        {
            AllocationKey = comparison.ComparedAllocation.AllocationKey with
            {
                OfferKey = comparison.ComparedAllocation.AllocationKey.OfferKey with { SourceCatalogKey = "already-wrong" },
            },
        };
        var wrongCatalog = ComparisonForAllocation(plan, wrongAllocation);
        var wrongObservation = comparison with
        {
            ComparedAllocation = comparison.ComparedAllocation with
            {
                AllocationKey = comparison.ComparedAllocation.AllocationKey with { ObservationId = "other-listing" },
            },
        };

        Assert.Equal("market:universalis:74:100:Normal", source.SourceCatalogKey);
        Assert.Equal("74:listing-100", source.ObservationId);
        Assert.False(wrongCatalog.Validate().IsValid);
        Assert.False(wrongObservation.Validate().IsValid);
    }

    [Fact]
    public void ComparedMarketSource_RejectsEveryExactListingMutation()
    {
        var comparison = Comparison(Plan());
        var source = Assert.IsType<ComparedGearMarketSourceIdentity>(comparison.ComparedAllocation.Source);
        var listing = source.Listing;
        var mutations = new[]
        {
            listing with { ItemId = 999 },
            listing with { Quality = EquipmentQuality.High },
            listing with { ListingId = "other-listing" },
            listing with { WorldId = 999 },
            listing with { WorldName = "Other World" },
            listing with { AvailableQuantity = 0 },
            listing with { UnitPriceGil = listing.UnitPriceGil + 1 },
            listing with { ReviewedAtUtc = listing.ReviewedAtUtc.AddTicks(1) },
            listing with { CapturedAtUtc = listing.CapturedAtUtc.AddTicks(1) },
            listing with { SourceRevision = "other" },
        };

        Assert.All(mutations, mutation =>
        {
            var candidate = WithComparedSource(comparison, source with { Listing = mutation });
            Assert.False(candidate.Validate().IsValid);
        });
        Assert.False(WithComparedSource(comparison, source with { ConsumedQuantity = 2 }).Validate().IsValid);
        Assert.False(WithComparedSource(comparison, source with { PurchasedQuantity = source.PurchasedQuantity + 1 }).Validate().IsValid);
        Assert.False(WithComparedSource(comparison, source with { SurplusQuantity = source.SurplusQuantity + 1 }).Validate().IsValid);
        Assert.False(WithComparedSource(comparison, source with { TotalPriceGil = source.TotalPriceGil + 1 }).Validate().IsValid);
        Assert.False(WithComparedSource(comparison, source with
        {
            Evidence = CraftMarketEvidenceReference.FromPublishedBook(EvidenceBook(materialSourceRevision: "r2")),
        }).Validate().IsValid);
    }

    [Fact]
    public void VendorMaterialPlan_PreservesOptionalBookForWholeStackMarketGearComparison()
    {
        var plan = VendorOnlyPlan(includeMarketEvidence: true);
        var comparison = Comparison(plan);
        var source = Assert.IsType<ComparedGearMarketSourceIdentity>(comparison.ComparedAllocation.Source);

        Assert.True(plan.Validate(requireEconomyReady: true).IsValid);
        Assert.True(comparison.Validate().IsValid);
        Assert.NotNull(plan.MarketEvidence);
        Assert.All(plan.TerminalMaterials, line => Assert.IsType<OutfitterGilVendorMaterialSourceIdentity>(line.Source));
        Assert.Equal(1u, source.ConsumedQuantity);
        Assert.Equal(2u, source.PurchasedQuantity);
        Assert.Equal(1u, source.SurplusQuantity);
        Assert.Equal(350ul, source.TotalPriceGil);
    }

    [Fact]
    public void ComparedVendorSource_RequiresExactCatalogMembershipVersionAndPrice()
    {
        var comparison = Comparison(VendorOnlyPlan());
        var source = Assert.IsType<ComparedGearGilVendorSourceIdentity>(comparison.ComparedAllocation.Source);
        var catalog = VendorCatalog();
        var member = catalog.FindOffers(100).Single();
        var fabricatedPrice = member with { UnitPriceGil = member.UnitPriceGil + 1 };

        Assert.Equal("vendor:20:30:40:100", source.SourceCatalogKey);
        Assert.StartsWith("sha256:", source.CatalogVersion, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            ComparedGearGilVendorSourceIdentity.FromCatalog(catalog, fabricatedPrice, 1));
    }

    [Fact]
    public void ComparedOwnedSource_BindsPlanCharacterGenerationTimeAndTarget()
    {
        var plan = VendorOnlyPlan();
        var allocation = OwnedAllocation(plan);
        var comparison = ComparisonForAllocation(plan, allocation);
        var source = Assert.IsType<ComparedGearOwnedSourceIdentity>(allocation.Source);

        Assert.True(comparison.Validate().IsValid);

        var wrongSources = new[]
        {
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(ownedContainer: "Inventory2"), "Inventory2", 3),
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(ownedSlot: 4), "Inventory1", 4),
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(ownedItemId: 999), "Inventory1", 3),
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(ownedHighQuality: true), "Inventory1", 3),
        };
        Assert.All(wrongSources, wrongSource =>
        {
            var alreadyWrongButSelfConsistent = OwnedAllocation(wrongSource);
            Assert.Equal(wrongSource.Instance.ItemId, alreadyWrongButSelfConsistent.AllocationKey.OfferKey.ItemId);
            Assert.Equal(wrongSource.SourceCatalogKey, alreadyWrongButSelfConsistent.AllocationKey.OfferKey.SourceCatalogKey);
            Assert.False(ComparisonForAllocation(plan, alreadyWrongButSelfConsistent).Validate().IsValid);
        });

        Assert.Throws<InvalidOperationException>(() =>
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(), "Inventory1", 4));
        Assert.Throws<InvalidOperationException>(() =>
            ComparedGearOwnedSourceIdentity.FromBaseline(
                Baseline(ownedCapturedAt: ReviewedAt.AddTicks(1)),
                "Inventory1",
                3));
    }

    [Fact]
    public void NarrowOwnedIdentityFingerprint_ChangesForEveryCarriedField()
    {
        var plan = VendorOnlyPlan();
        var allocation = OwnedAllocation(plan);
        var source = Assert.IsType<ComparedGearOwnedSourceIdentity>(allocation.Source);
        Assert.True(ComparedGearAllocationIdentity.TryCompute(allocation, out var baseline));
        var mutations = new[]
        {
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(captureId: Guid.NewGuid()), "Inventory1", 3),
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(equipmentGeneration: Guid.NewGuid()), "Inventory1", 3),
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(character: new(9999, "Other Crafter", 74)), "Inventory1", 3),
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(ownedContainer: "Inventory2"), "Inventory2", 3),
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(ownedSlot: 4), "Inventory1", 4),
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(ownedItemId: 101), "Inventory1", 3),
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(ownedHighQuality: true), "Inventory1", 3),
            ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(ownedQuantity: 2), "Inventory1", 3),
        };

        Assert.All(mutations, mutation =>
        {
            var candidate = OwnedAllocation(mutation);
            Assert.True(ComparedGearAllocationIdentity.TryCompute(candidate, out var changed));
            Assert.NotEqual(baseline, changed);
            Assert.False(ComparisonForAllocation(plan, candidate).Validate().IsValid);
        });
    }

    [Fact]
    public void CanonicalIdentities_AreCultureInvariantForNegativeSavings()
    {
        var plan = VendorOnlyPlan();
        var expensivePlanComparison = ComparisonForAllocation(plan, OwnedAllocation(plan)) with { SavingsGil = -60 };
        var expected = expensivePlanComparison.ComputeStructuralIdentity();
        var previousCulture = CultureInfo.CurrentCulture;
        var customCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        customCulture.NumberFormat.NegativeSign = "~";
        try
        {
            CultureInfo.CurrentCulture = customCulture;
            Assert.Equal(expected, expensivePlanComparison.ComputeStructuralIdentity());
            Assert.Equal(plan.ComputeStructuralIdentity(), (plan with { PlanId = "runtime-only" }).ComputeStructuralIdentity());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void Plan_RejectsCircularAmbiguousOverDepthAndIncompleteTrees()
    {
        var baseline = Plan();
        var candidates = new[]
        {
            baseline with
            {
                ExpandedNodes = baseline.ExpandedNodes.Select(node => node.NodeId == "root"
                    ? node with { ParentNodeId = "sub" }
                    : node).ToImmutableArray(),
            },
            baseline with { ExpandedNodes = baseline.ExpandedNodes.Add(baseline.ExpandedNodes[1]) },
            baseline with { MaximumDepth = 1 },
            baseline with { MaximumExpandedNodeCount = baseline.ExpandedNodes.Length - 1 },
            baseline with { MaximumExpandedNodeCount = OutfitterExactRecipeGraph.MaximumAllowedExpandedNodeCount + 1 },
            baseline with { TerminalMaterials = baseline.TerminalMaterials[..1] },
        };

        Assert.All(candidates, candidate => Assert.False(candidate.Validate().IsValid));
    }

    [Fact]
    public void Validation_FailsClosedWithoutThrowingForMalformedRuntimeRecords()
    {
        var plan = Plan();
        var malformedPlans = new[]
        {
            plan with { ExpandedNodes = default },
            plan with { TerminalMaterials = default },
            plan with { Diagnostics = default },
            plan with { CrafterObservation = null! },
            plan with { BuiltAtUtc = default },
            plan with { ExpandedNodes = [null!] },
            plan with { TerminalMaterials = [null!] },
        };
        var comparison = Comparison(plan);
        var market = Assert.IsType<ComparedGearMarketSourceIdentity>(comparison.ComparedAllocation.Source);
        var vendorComparison = Comparison(VendorOnlyPlan());
        var vendor = Assert.IsType<ComparedGearGilVendorSourceIdentity>(vendorComparison.ComparedAllocation.Source);
        var ownedComparison = ComparisonForAllocation(VendorOnlyPlan(), OwnedAllocation(VendorOnlyPlan()));
        var owned = Assert.IsType<ComparedGearOwnedSourceIdentity>(ownedComparison.ComparedAllocation.Source);
        var malformedComparisons = new[]
        {
            comparison with { Diagnostics = default },
            comparison with { Plan = null! },
            comparison with { ComparedAllocation = null! },
            comparison with { PlanIdentity = null! },
            comparison with { Burden = null! },
            comparison with { BuiltAtUtc = default },
            comparison with { Status = (CraftCostComparisonStatus)999 },
            comparison with { ComparedAllocation = comparison.ComparedAllocation with { Source = null! } },
            WithComparedSource(comparison, market with { Evidence = null! }),
            WithComparedSource(comparison, market with { Listing = null! }),
            WithComparedSource(ownedComparison, CorruptOwnedSource(owned)),
        };

        var marketLine = plan.TerminalMaterials.Single(line => line.Source is OutfitterMarketMaterialSourceIdentity);
        var marketMaterial = (OutfitterMarketMaterialSourceIdentity)marketLine.Source;
        var vendorLine = plan.TerminalMaterials.Single(line => line.Source is OutfitterGilVendorMaterialSourceIdentity);
        var vendorMaterial = (OutfitterGilVendorMaterialSourceIdentity)vendorLine.Source;
        malformedPlans =
        [
            .. malformedPlans,
            ReplaceMaterialSource(plan, marketLine.MaterialKey, marketMaterial with { ListingId = null! }),
            ReplaceMaterialSource(plan, marketLine.MaterialKey, marketMaterial with { SourceRevision = null! }),
            ReplaceMaterialSource(plan, vendorLine.MaterialKey, vendorMaterial with { UnitPriceGil = vendorMaterial.UnitPriceGil + 1 }),
        ];

        Assert.All(malformedPlans, candidate => AssertInvalidWithoutThrow(() => candidate.Validate()));
        Assert.All(malformedComparisons, candidate => AssertInvalidWithoutThrow(() => candidate.Validate()));
    }

    private static OutfitterCraftPlan Plan(
        PlayerAdvisorBaseline? baseline = null,
        OutfitterMarketEvidenceBook? book = null,
        DateTimeOffset? builtAt = null)
    {
        var asOf = builtAt ?? PlanBuiltAt;
        var authority = OutfitterCrafterObservationIdentity.FromBaseline(baseline ?? Baseline(), asOf);
        var evidence = CraftMarketEvidenceReference.FromPublishedBook(book ?? EvidenceBook());
        var nodes = ImmutableArray.Create(
            CraftNode("root", null, 100, 1, 0, 500, 1, 90,
            [
                new("sub", 200, EquipmentQuality.Normal, 2, "Ingot"),
                new("cloth", 400, EquipmentQuality.Normal, 3, "Cloth"),
            ], authority),
            CraftNode("sub", "root", 200, 2, 2, 501, 1, 80,
                [new("ore", 300, EquipmentQuality.Normal, 2, "Ore")], authority),
            new OutfitterCraftNode("ore", "sub", OutfitterCraftNodeKind.Material, 300, EquipmentQuality.Normal, 4, 2),
            new OutfitterCraftNode("cloth", "root", OutfitterCraftNodeKind.Material, 400, EquipmentQuality.Normal, 3, 3));
        return new(
            OutfitterCraftPlan.CurrentSchemaVersion,
            "runtime-plan",
            100,
            EquipmentQuality.Normal,
            1,
            "root",
            authority,
            4,
            16,
            nodes,
            [MarketMaterial(evidence, 300, 4, "listing-300"), VendorMaterial(400, 3, 20)],
            evidence,
            asOf,
            ImmutableArray<OutfitterCraftDiagnostic>.Empty);
    }

    private static OutfitterCraftPlan VendorOnlyPlan(bool includeMarketEvidence = false)
    {
        var authority = OutfitterCrafterObservationIdentity.FromBaseline(Baseline(), PlanBuiltAt);
        var root = CraftNode("root", null, 100, 1, 0, 500, 1, 90,
            [new("cloth", 400, EquipmentQuality.Normal, 3, "Cloth")], authority);
        return new(
            OutfitterCraftPlan.CurrentSchemaVersion,
            "vendor-only-plan",
            100,
            EquipmentQuality.Normal,
            1,
            "root",
            authority,
            2,
            4,
            [root, new("cloth", "root", OutfitterCraftNodeKind.Material, 400, EquipmentQuality.Normal, 3, 3)],
            [VendorMaterial(400, 3, 20)],
            includeMarketEvidence ? CraftMarketEvidenceReference.FromPublishedBook(EvidenceBook()) : null,
            PlanBuiltAt,
            ImmutableArray<OutfitterCraftDiagnostic>.Empty);
    }

    private static OutfitterCraftNode CraftNode(
        string nodeId,
        string? parentNodeId,
        uint itemId,
        uint requiredQuantity,
        uint quantityPerParentCraft,
        uint recipeId,
        uint outputQuantity,
        int requiredLevel,
        ImmutableArray<OutfitterResolvedRecipeIngredient> ingredients,
        OutfitterCrafterObservationIdentity authority)
    {
        var state = authority.IsLevelSynced
            ? OutfitterCraftEligibilityState.Unproven
            : authority.ClassJobId == 8 && authority.EffectiveLevel >= requiredLevel
                ? OutfitterCraftEligibilityState.ProvenEligible
                : OutfitterCraftEligibilityState.ProvenIneligible;
        var diagnostic = state == OutfitterCraftEligibilityState.ProvenEligible
            ? null
            : state == OutfitterCraftEligibilityState.Unproven
                ? "Level-synchronized craft eligibility is not modeled."
                : "The active crafter does not satisfy this recipe.";
        return new(
            nodeId,
            parentNodeId,
            OutfitterCraftNodeKind.Craft,
            itemId,
            EquipmentQuality.Normal,
            requiredQuantity,
            quantityPerParentCraft,
            recipeId,
            outputQuantity,
            0,
            new(
                "static-recipe-contract-fixture",
                "v1",
                $"recipe-{recipeId}-fingerprint",
                recipeId,
                itemId,
                outputQuantity,
                8,
                requiredLevel,
                0,
                ingredients,
                $"Item {itemId}"),
            new(
                state,
                authority.BaselineAuthorityFingerprint,
                authority.Character,
                nodeId,
                recipeId,
                8,
                requiredLevel,
                authority.ClassJobId,
                authority.EffectiveLevel,
                diagnostic));
    }

    private static OutfitterTerminalMaterialLine MarketMaterial(
        CraftMarketEvidenceReference evidence,
        uint itemId,
        uint quantity,
        string listingId)
    {
        var listing = evidence.Listings.Single(value => value.ItemId == itemId && value.ListingId == listingId);
        var source = new OutfitterMarketMaterialSourceIdentity(
            itemId,
            listing.Quality,
            listing.UnitPriceGil,
            listing.AvailableQuantity,
            listing.ListingId,
            listing.WorldId,
            listing.WorldName,
            listing.ReviewedAtUtc,
            listing.CapturedAtUtc,
            listing.SourceRevision,
            evidence.GenerationId,
            evidence.Revision);
        return new(
            OutfitterCraftPlan.MaterialKey(itemId, listing.Quality),
            itemId,
            listing.Quality,
            quantity,
            listing.AvailableQuantity,
            listing.AvailableQuantity - quantity,
            source);
    }

    private static OutfitterTerminalMaterialLine VendorMaterial(uint itemId, uint quantity, uint unitGil)
    {
        var catalog = OutfitterGilVendorCatalog.FromTrustedSnapshot(
        [
            new(itemId, 20, 30, "Test Merchant", 40, "Test Territory", unitGil),
        ]);
        var source = OutfitterGilVendorMaterialSourceIdentity.FromCatalog(
            catalog,
            catalog.FindOffers(itemId).Single());
        return new(
            OutfitterCraftPlan.MaterialKey(itemId, EquipmentQuality.Normal),
            itemId,
            EquipmentQuality.Normal,
            quantity,
            quantity,
            0,
            source);
    }

    private static CraftCostComparison Comparison(
        OutfitterCraftPlan plan,
        CraftCostComparisonStatus status = CraftCostComparisonStatus.Complete,
        string? diagnostic = null)
    {
        var total = MaterialTotal(plan);
        var comparedTotal = checked(total + 75);
        ComparedGearSourceIdentity source;
        if (plan.MarketEvidence is { } evidence)
        {
            var listing = evidence.Listings.Single(value => value.ItemId == plan.GearItemId && value.Quality == plan.GearQuality);
            comparedTotal = checked((ulong)listing.AvailableQuantity * listing.UnitPriceGil);
            source = new ComparedGearMarketSourceIdentity(
                evidence,
                listing,
                plan.GearQuantity,
                listing.AvailableQuantity,
                listing.AvailableQuantity - plan.GearQuantity,
                comparedTotal);
        }
        else
        {
            var catalog = VendorCatalog(checked((uint)comparedTotal));
            source = ComparedGearGilVendorSourceIdentity.FromCatalog(
                catalog,
                catalog.FindOffers(plan.GearItemId).Single(),
                plan.GearQuantity);
        }

        var allocation = Allocation(plan, comparedTotal, source);
        return new(
            CraftCostComparison.CurrentSchemaVersion,
            "comparison-1",
            status,
            plan,
            plan.ComputeStructuralIdentity(),
            total,
            checked((uint)((total + plan.GearQuantity - 1) / plan.GearQuantity)),
            allocation,
            checked((long)comparedTotal - (long)total),
            Burden(plan),
            ComparisonBuiltAt,
            diagnostic is null ? ImmutableArray<string>.Empty : [diagnostic]);
    }

    private static CraftCostComparison ComparisonForAllocation(
        OutfitterCraftPlan plan,
        ComparedGearAllocation allocation)
    {
        var total = MaterialTotal(plan);
        return new(
            CraftCostComparison.CurrentSchemaVersion,
            "comparison-custom-allocation",
            CraftCostComparisonStatus.Complete,
            plan,
            plan.ComputeStructuralIdentity(),
            total,
            checked((uint)((total + plan.GearQuantity - 1) / plan.GearQuantity)),
            allocation,
            checked((long)allocation.TotalGil - (long)total),
            Burden(plan),
            ComparisonBuiltAt,
            ImmutableArray<string>.Empty);
    }

    private static ComparedGearAllocation OwnedAllocation(OutfitterCraftPlan plan)
    {
        var source = ComparedGearOwnedSourceIdentity.FromBaseline(Baseline(), "Inventory1", 3);
        return OwnedAllocation(source);
    }

    private static ComparedGearAllocation OwnedAllocation(ComparedGearOwnedSourceIdentity source) => new(
        new(
            new(source.Instance.ItemId, source.Instance.Quality, source.Kind, source.SourceCatalogKey),
            source.ObservationId),
        source.Instance.Quantity,
        0,
        source);

    private static ComparedGearAllocation Allocation(
        OutfitterCraftPlan plan,
        ulong totalGil,
        ComparedGearSourceIdentity source) => new(
        new(
            new(plan.GearItemId, plan.GearQuality, source.Kind, source.SourceCatalogKey),
            source.ObservationId),
        plan.GearQuantity,
        totalGil,
        source);

    private static ulong MaterialTotal(OutfitterCraftPlan plan) =>
        plan.TerminalMaterials.Aggregate(0ul, (sum, line) => checked(sum + (ulong)line.PurchasedQuantity * line.Source.UnitPriceGil));

    private static OutfitterGilVendorCatalog VendorCatalog(uint unitPriceGil = 135) =>
        OutfitterGilVendorCatalog.FromTrustedSnapshot(
        [
            new(100, 20, 30, "Test Merchant", 40, "Test Territory", unitPriceGil),
        ]);

    private static CraftAcquisitionBurden Burden(OutfitterCraftPlan plan)
    {
        var craftNodes = plan.ExpandedNodes.Count(node => node.Kind == OutfitterCraftNodeKind.Craft);
        return new(
            craftNodes,
            Math.Max(0, craftNodes - 1),
            plan.TerminalMaterials.Select(line => line.MaterialKey).Distinct(StringComparer.Ordinal).Count(),
            plan.TerminalMaterials.Select(line => line.Source).OfType<OutfitterMarketMaterialSourceIdentity>().Select(source => source.PhysicalSourceKey).Distinct().Count(),
            plan.TerminalMaterials.Select(line => line.Source).OfType<OutfitterGilVendorMaterialSourceIdentity>().Select(source => source.PhysicalSourceKey).Distinct().Count());
    }

    private static PlayerAdvisorBaseline Baseline(
        PlayerAdvisorBaselineStatus status = PlayerAdvisorBaselineStatus.Complete,
        uint classJobId = 8,
        short actualLevel = 100,
        short effectiveLevel = 100,
        bool isLevelSynced = false,
        uint? materiaId = null,
        Guid? captureId = null,
        Guid? equipmentGeneration = null,
        DateTimeOffset? captureCompletedAt = null,
        CharacterScope? character = null,
        string ownedContainer = "Inventory1",
        int ownedSlot = 3,
        uint ownedItemId = 100,
        bool ownedHighQuality = false,
        uint ownedQuantity = 1,
        DateTimeOffset? ownedCapturedAt = null,
        DateTimeOffset? snapshotCapturedAt = null)
    {
        var scope = character ?? CrafterCharacter;
        var snapshotTime = snapshotCapturedAt ?? ReviewedAt;
        var stats = new Dictionary<EquipmentStatSemantic, int>
        {
            [EquipmentStatSemantic.Craftsmanship] = 5_399,
            [EquipmentStatSemantic.Control] = 5_200,
            [EquipmentStatSemantic.CraftingPoints] = 950,
        };
        var hasMainHand = materiaId is not null;
        var mainHandItemId = 9_000u;
        var instances = new List<EquipmentInstanceSnapshot>
        {
            EquipmentInstance(
                ownedSlot,
                ownedItemId,
                ownedHighQuality,
                ownedCapturedAt ?? snapshotTime,
                container: ownedContainer,
                quantity: ownedQuantity,
                character: scope,
                isEquipped: false),
        };
        if (hasMainHand)
            instances.Add(EquipmentInstance(0, mainHandItemId, false, snapshotTime, [materiaId!.Value], character: scope));
        var definitions = hasMainHand
            ? new Dictionary<uint, EquipmentItemDefinition>
            {
                [mainHandItemId] = EquipmentDefinition(mainHandItemId, EquipmentSlot.MainHand, classJobId),
            }
            : new Dictionary<uint, EquipmentItemDefinition>();
        var snapshot = new CharacterEquipmentSnapshot(
            equipmentGeneration ?? EquipmentGeneration,
            new(scope, 74, classJobId, snapshotTime, true, SnapshotComponentStatus.Complete),
            [],
            [],
            instances,
            definitions,
            new([
                new("identity", SnapshotComponentStatus.Complete),
                new("equipped", SnapshotComponentStatus.Complete),
            ]));
        var zeroStats = stats.ToDictionary(pair => pair.Key, _ => 0);
        var captures = PlayerAdvisorEquippedSlotMap.All.Select(position =>
            new PlayerAdvisorEquippedItemCapture(
                position.EquippedIndex,
                hasMainHand && position.EquippedIndex == 0 ? mainHandItemId : 0,
                EquipmentQuality.Normal,
                zeroStats,
                hasMainHand && position.EquippedIndex == 0 ? [materiaId!.Value] : [],
                hasMainHand && position.EquippedIndex == 0 ? [(byte)1] : [])).ToArray();
        var assembled = PlayerAdvisorBaselineAssembler.Assemble(
            snapshot,
            new(scope, 74, classJobId, actualLevel, effectiveLevel, isLevelSynced),
            AdvisorStatFamilies.Resolve(classJobId),
            stats,
            captures,
            PlayerAdvisorTrustedCapture.Complete(captureId ?? CaptureId, captureCompletedAt ?? CapturedAt));
        return status == PlayerAdvisorBaselineStatus.Complete
            ? assembled
            : assembled with { Status = status };
    }

    private static EquipmentInstanceSnapshot EquipmentInstance(
        int slot,
        uint itemId,
        bool highQuality,
        DateTimeOffset capturedAt,
        IReadOnlyList<uint>? materiaIds = null,
        string container = "EquippedItems",
        uint quantity = 1,
        CharacterScope? character = null,
        bool isEquipped = true) => new(
        new(
            character ?? CrafterCharacter,
            container,
            slot,
            itemId,
            highQuality,
            quantity,
            30_000,
            0,
             null,
             materiaIds ?? [],
             null,
             [],
             materiaIds?.Select(_ => (byte)1).ToArray() ?? []),
         capturedAt,
         isEquipped);

    private static EquipmentItemDefinition EquipmentDefinition(uint itemId, EquipmentSlot slot, uint classJobId) => new(
        itemId,
        $"Item {itemId}",
        1,
        1,
        slot,
        new HashSet<uint> { classJobId },
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

    private static OutfitterMarketEvidenceBook EvidenceBook(
        DateTimeOffset? reviewedAt = null,
        DateTimeOffset? capturedAt = null,
        DateTimeOffset? publishedAt = null,
        string materialSourceRevision = "material-r1",
        string worldName = "Test World",
        uint unitPriceGil = 10,
        bool includeCrossWorldDuplicate = false)
    {
        var reviewed = reviewedAt ?? ReviewedAt;
        var captured = capturedAt ?? CapturedAt;
        var published = publishedAt ?? PublishedAt;
        var gearListing = new OutfitterMarketListingEvidence(
            100, EquipmentQuality.Normal, "listing-100", worldName, 74, "Gear Retainer", "retainer-100",
            2, 175, reviewed, captured, "gear-r1");
        var hqGearListing = gearListing with
        {
            Quality = EquipmentQuality.High,
            ListingId = "listing-100-hq",
            RetainerName = "HQ Gear Retainer",
            RetainerId = "retainer-100-hq",
        };
        var materialListing = new OutfitterMarketListingEvidence(
            300, EquipmentQuality.Normal, "listing-300", worldName, 74, "Material Retainer", "retainer-300",
            10, unitPriceGil, reviewed, captured, materialSourceRevision);
        var gearListings = includeCrossWorldDuplicate
            ? new[]
            {
                gearListing,
                hqGearListing,
                gearListing with { WorldId = 75, WorldName = "Other World", RetainerId = "retainer-other-world" },
            }
            : [gearListing, hqGearListing];
        return new(
            EvidenceGeneration,
            7,
            OutfitterMarketEvidenceBook.CurrentSchemaVersion,
            "universalis",
            "North America",
            reviewed,
            published,
            OutfitterMarketEvidenceGenerationStatus.Complete,
            new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 2, 2, 100, [100u, 300u]),
            [
                new(100, OutfitterMarketEvidenceItemStatus.Fresh, gearListings, captured, "gear-r1"),
                new(300, OutfitterMarketEvidenceItemStatus.Fresh, [materialListing], captured, materialSourceRevision),
            ]);
    }

    private static PlayerAdvisorBaseline WithIdentity(
        PlayerAdvisorBaseline baseline,
        CharacterIdentitySnapshot identity) => baseline with
    {
        EquipmentSnapshot = baseline.EquipmentSnapshot! with { Identity = identity },
    };

    private static OutfitterCraftPlan ChangeEligibility(
        OutfitterCraftPlan plan,
        string nodeId,
        Func<OutfitterCraftEligibilityEvidence, OutfitterCraftEligibilityEvidence> change) => plan with
    {
        ExpandedNodes = plan.ExpandedNodes.Select(node => node.NodeId == nodeId
            ? node with { Eligibility = change(node.Eligibility!) }
            : node).ToImmutableArray(),
    };

    private static OutfitterCraftPlan ReplaceMaterialSource(
        OutfitterCraftPlan plan,
        string materialKey,
        OutfitterMaterialSourceIdentity source) => plan with
    {
        TerminalMaterials = plan.TerminalMaterials.Select(line => line.MaterialKey == materialKey
            ? line with { Source = source }
            : line).ToImmutableArray(),
    };

    private static OutfitterCraftPlan WithRootQuality(
        OutfitterCraftPlan plan,
        EquipmentQuality quality,
        OutfitterCraftHqCapabilityAttestation? attestation) => plan with
    {
        GearQuality = quality,
        ExpandedNodes = plan.ExpandedNodes.Select(node => node.NodeId == "root"
            ? node with { Quality = quality, HqCapabilityAttestation = attestation }
            : node).ToImmutableArray(),
    };

    private static OutfitterCraftHqCapabilityAttestation HqAttestation(
        OutfitterCraftNode node,
        OutfitterCrafterObservationIdentity authority) => new(
        "diagnostic-hq-attestation",
        "untrusted-diagnostic-model",
        "v1",
        authority.BaselineAuthorityFingerprint,
        PlanBuiltAt.AddSeconds(-1),
        node.NodeId,
        node.RecipeId,
        node.ItemId,
        EquipmentQuality.High,
        node.RequiredQuantity);

    private static CraftCostComparison WithComparedSource(
        CraftCostComparison comparison,
        ComparedGearSourceIdentity source) => comparison with
    {
        ComparedAllocation = comparison.ComparedAllocation with { Source = source },
    };

    private static ComparedGearOwnedSourceIdentity CorruptOwnedSource(ComparedGearOwnedSourceIdentity source)
    {
        var constructor = typeof(ComparedGearOwnedSourceIdentity)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(value => value.GetParameters().Length == 5);
        return (ComparedGearOwnedSourceIdentity)constructor.Invoke(
            [source.CaptureId, source.CaptureGenerationId, source.CapturedAtUtc, source.EquipmentSnapshotFingerprint, null]);
    }

    private static void AssertInvalidWithoutThrow(Func<OutfitterCraftPlanValidation> validate)
    {
        OutfitterCraftPlanValidation? validation = null;
        Assert.Null(Record.Exception(() => validation = validate()));
        Assert.NotNull(validation);
        Assert.False(validation.IsValid);
        Assert.NotEmpty(validation.Errors);
    }

    private static void AssertInvalidWithoutThrow(Func<CraftCostComparisonValidation> validate)
    {
        CraftCostComparisonValidation? validation = null;
        Assert.Null(Record.Exception(() => validation = validate()));
        Assert.NotNull(validation);
        Assert.False(validation.IsValid);
        Assert.NotEmpty(validation.Errors);
    }

    private sealed class EnumerationForbiddenReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly T value;

        public EnumerationForbiddenReadOnlyList(int count, T value)
        {
            Count = count;
            this.value = value;
        }

        public int Count { get; }

        public T this[int index] => index >= 0 && index < Count
            ? value
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<T> GetEnumerator() =>
            throw new NotSupportedException("Oversized collections must be rejected from Count without enumeration.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
