#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Crafting;

internal sealed record OutfitterS4GoldenFixtureResult(
    PlayerAdvisorBaseline Baseline,
    OutfitterMarketEvidenceBook MarketEvidence,
    MinerBotanistReadOnlyAdvice Advice,
    string SelectedCraftSolutionId,
    OutfitterCraftHandoffProjection CraftHandoff,
    TimeProvider TimeProvider);

/// <summary>
/// Deterministic process-local S4 review path. It exercises production recipe adaptation, passive
/// offer construction, Advisor nomination, and craft handoff without any external or game action.
/// </summary>
internal static class OutfitterS4GoldenFixture
{
    private const uint BlacksmithClassJobId = CrafterUtilityProfile.BlacksmithClassJobId;
    private const uint CurrentGearItemIdBase = 48_000;
    private const uint CraftedGearItemId = 49_000;
    private const uint IngotItemId = 49_001;
    private const uint OreItemId = 49_002;
    private const uint PowderItemId = 49_003;
    private const uint LumberItemId = 49_004;
    private const uint RootRecipeId = 39_000;
    private const uint IngotRecipeId = 39_001;
    private const uint WorldId = 64;

    private static readonly Guid EquipmentGenerationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid CaptureId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
    private static readonly Guid EvidenceGenerationId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly DateTimeOffset ReviewAtUtc = DateTimeOffset.Parse("2026-07-21T18:00:00Z");
    private static readonly CharacterScope Character = new(7_777, "Myrna Smith", WorldId);

    public static OutfitterS4GoldenFixtureResult Create()
    {
        var timeProvider = new FixedTimeProvider(ReviewAtUtc);
        var baseline = BuildBaseline();
        var marketEvidence = BuildMarketEvidence();
        var craftedGear = EquipmentDefinition(
            CraftedGearItemId,
            "Ceremonial Cross-pein Hammer",
            EquipmentSlot.MainHand,
            itemLevel: 720,
            CraftingProfile(craftsmanship: 320, control: 220, craftingPoints: 0));
        var craftResult = new OutfitterPassiveCraftOfferService(
                OutfitterGilVendorCatalog.FromTrustedSnapshot(Array.Empty<OutfitterGilVendorOffer>()),
                timeProvider)
            .Build(
                BuildRecipeGraphResponse(),
                baseline,
                marketEvidence,
                craftedGear,
                CrafterAdvisorStatFamily.Instance);
        if (craftResult is not
            {
                Status: OutfitterPassiveCraftOfferStatus.OfferReady,
                Offer: { } craftOffer,
            })
        {
            throw new InvalidOperationException(
                $"S4 golden craft offer failed: {string.Join(" ", craftResult.Diagnostics)}");
        }

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            baseline,
            marketEvidence,
            itemId => itemId == CraftedGearItemId ? [craftedGear] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
            craftOffers: [craftOffer],
            equipmentMarketScope: new HashSet<uint> { CraftedGearItemId });
        if (advice is not
            {
                Status: MinerBotanistAdvisorStatus.Complete,
                Nomination: { } nomination,
            } ||
            !nomination.Candidate.Selections.Any(selection =>
                advice.OffersByAllocation.TryGetValue(selection.AllocationKey, out var offer) &&
                offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.Craft))
        {
            throw new InvalidOperationException(
                $"S4 golden Advisor did not nominate the complete craft offer: {advice.Diagnostic}");
        }

        var selectedCraftSolutionId = nomination.Candidate.SolutionId;
        var handoff = OutfitterCraftHandoffProjection.Build(
            advice,
            selectedCraftSolutionId,
            baseline,
            marketEvidence,
            timeProvider.GetUtcNow());
        return new(
            baseline,
            marketEvidence,
            advice,
            selectedCraftSolutionId,
            handoff,
            timeProvider);
    }

    private static PlayerAdvisorBaseline BuildBaseline()
    {
        var snapshotAtUtc = ReviewAtUtc.AddSeconds(-30);
        var captureAtUtc = ReviewAtUtc.AddSeconds(-20);
        var definitions = new Dictionary<uint, EquipmentItemDefinition>();
        var instances = new List<EquipmentInstanceSnapshot>();
        var captures = new List<PlayerAdvisorEquippedItemCapture>();
        foreach (var position in PlayerAdvisorEquippedSlotMap.All)
        {
            var itemId = checked(CurrentGearItemIdBase + (uint)position.EquippedIndex);
            var stats = CrafterStats(
                position.Position == EquipmentLoadoutPosition.MainHand ? 300 : 0,
                position.Position == EquipmentLoadoutPosition.MainHand ? 200 : 0,
                0);
            var definition = EquipmentDefinition(
                itemId,
                EquippedItemName(position.Position),
                Slot(position.Position),
                itemLevel: 690,
                CraftingProfile(
                    stats[EquipmentStatSemantic.Craftsmanship],
                    stats[EquipmentStatSemantic.Control],
                    stats[EquipmentStatSemantic.CraftingPoints]));
            var instance = new EquipmentInstanceSnapshot(
                new(
                    Character,
                    "EquippedItems",
                    position.EquippedIndex,
                    itemId,
                    false,
                    1,
                    30_000,
                    0,
                    null,
                    [],
                    null,
                    []),
                snapshotAtUtc,
                true);
            definitions.Add(itemId, definition);
            instances.Add(instance);
            captures.Add(new(
                position.EquippedIndex,
                itemId,
                EquipmentQuality.Normal,
                stats,
                [],
                []));
        }

        var snapshot = new CharacterEquipmentSnapshot(
            EquipmentGenerationId,
            new(Character, WorldId, BlacksmithClassJobId, snapshotAtUtc, true, SnapshotComponentStatus.Complete),
            [],
            [],
            instances,
            definitions,
            new([
                new("identity", SnapshotComponentStatus.Complete),
                new("equipped", SnapshotComponentStatus.Complete),
            ]));
        return PlayerAdvisorBaselineAssembler.Assemble(
            snapshot,
            new(Character, WorldId, BlacksmithClassJobId, 100, 100, false),
            CrafterAdvisorStatFamily.Instance,
            CrafterStats(craftsmanship: 4_000, control: 3_800, craftingPoints: 600),
            captures,
            PlayerAdvisorTrustedCapture.Complete(CaptureId, captureAtUtc));
    }

    private static OutfitterMarketEvidenceBook BuildMarketEvidence()
    {
        var reviewedAtUtc = ReviewAtUtc.AddMinutes(-4);
        var capturedAtUtc = ReviewAtUtc.AddMinutes(-3);
        var publishedAtUtc = ReviewAtUtc.AddMinutes(-2);
        OutfitterMarketListingEvidence Listing(
            uint itemId,
            string listingId,
            string retainerName,
            uint quantity,
            uint unitPriceGil,
            string sourceRevision) => new(
                itemId,
                EquipmentQuality.Normal,
                listingId,
                "Siren",
                WorldId,
                retainerName,
                $"retainer-{listingId}",
                quantity,
                unitPriceGil,
                reviewedAtUtc,
                capturedAtUtc,
                sourceRevision);
        OutfitterMarketItemEvidence Item(
            uint itemId,
            string sourceRevision,
            OutfitterMarketListingEvidence listing) => new(
                itemId,
                OutfitterMarketEvidenceItemStatus.Fresh,
                [listing],
                capturedAtUtc,
                sourceRevision);

        var gearRevision = "ceremonial-hammer-r1";
        var oreRevision = "rakaznar-ore-r1";
        var powderRevision = "magnesia-powder-r1";
        var lumberRevision = "claro-lumber-r1";
        var items = new OutfitterMarketItemEvidence[]
        {
            Item(CraftedGearItemId, gearRevision,
                Listing(CraftedGearItemId, "ceremonial-hammer-1", "Hammerfall", 1, 50_000, gearRevision)),
            Item(OreItemId, oreRevision,
                Listing(OreItemId, "rakaznar-ore-12", "Deep Delver", 12, 100, oreRevision)),
            Item(PowderItemId, powderRevision,
                Listing(PowderItemId, "magnesia-powder-3", "Bright Crucible", 3, 200, powderRevision)),
            Item(LumberItemId, lumberRevision,
                Listing(LumberItemId, "claro-lumber-2", "Branch Manager", 2, 500, lumberRevision)),
        };
        var itemIds = items.Select(item => item.ItemId).ToArray();
        return new(
            EvidenceGenerationId,
            4,
            OutfitterMarketEvidenceBook.CurrentSchemaVersion,
            "universalis",
            "North America",
            reviewedAtUtc,
            publishedAtUtc,
            OutfitterMarketEvidenceGenerationStatus.Complete,
            new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, itemIds.Length, itemIds.Length, 100, itemIds),
            items);
    }

    private static CraftRecipeGraphResponseV1 BuildRecipeGraphResponse() => new()
    {
        ProviderVersion = "s4-golden-7.51-v1",
        RecipeDataIdentity = $"sha256:{new string('A', 64)}",
        IsComplete = true,
        RootItemId = CraftedGearItemId,
        RootItemName = "Ceremonial Cross-pein Hammer",
        Limits = CraftRecipeGraphLimitsV1.Default,
        Recipes =
        [
            Recipe(
                RootRecipeId,
                CraftedGearItemId,
                "Ceremonial Cross-pein Hammer",
                1,
                100,
                [
                    new CraftRecipeIngredientV1 { ItemId = IngotItemId, ItemName = "Ra'Kaznar Ingot", QuantityPerCraft = 3 },
                    new CraftRecipeIngredientV1 { ItemId = LumberItemId, ItemName = "Claro Walnut Lumber", QuantityPerCraft = 1 },
                ]),
            Recipe(
                IngotRecipeId,
                IngotItemId,
                "Ra'Kaznar Ingot",
                2,
                98,
                [
                    new CraftRecipeIngredientV1 { ItemId = OreItemId, ItemName = "Ra'Kaznar Ore", QuantityPerCraft = 5 },
                    new CraftRecipeIngredientV1 { ItemId = PowderItemId, ItemName = "Magnesia Powder", QuantityPerCraft = 1 },
                ]),
        ],
        TerminalMaterialItemIds = [OreItemId, PowderItemId, LumberItemId],
        Diagnostics = [],
    };

    private static CraftRecipeDefinitionV1 Recipe(
        uint recipeId,
        uint outputItemId,
        string outputItemName,
        uint outputQuantity,
        int requiredLevel,
        IReadOnlyList<CraftRecipeIngredientV1> ingredients) => new()
        {
            RecipeId = recipeId,
            OutputItemId = outputItemId,
            OutputItemName = outputItemName,
            OutputQuantity = outputQuantity,
            RequiredClassJobId = BlacksmithClassJobId,
            RequiredClassJobName = "Blacksmith",
            RequiredLevel = requiredLevel,
            RecipeUnlockItemId = 0,
            UnlockEvidence = CraftRecipeUnlockEvidenceV1.NoUnlockRequired,
            ResolutionConfidence = CraftRecipeResolutionConfidenceV1.Exact,
            DataSource = CraftRecipeDataSourceV1.GarlandStandardCraft,
            Ingredients = ingredients,
            StructuralDiagnostics = [],
        };

    private static EquipmentItemDefinition EquipmentDefinition(
        uint itemId,
        string name,
        EquipmentSlot slot,
        uint itemLevel,
        EquipmentStatProfile profile) => new(
            itemId,
            name,
            100,
            itemLevel,
            slot,
            new HashSet<uint> { BlacksmithClassJobId },
            3,
            true,
            false,
            true,
            true,
            1,
            true,
            false,
            true,
            false,
            StatProfile: profile);

    private static EquipmentStatProfile CraftingProfile(
        int craftsmanship,
        int control,
        int craftingPoints)
    {
        var parameters = new List<EquipmentStatValue>();
        if (craftsmanship > 0)
            parameters.Add(new(70, EquipmentStatSemantic.Craftsmanship, craftsmanship, false, "Craftsmanship"));
        if (control > 0)
            parameters.Add(new(71, EquipmentStatSemantic.Control, control, false, "Control"));
        if (craftingPoints > 0)
            parameters.Add(new(11, EquipmentStatSemantic.CraftingPoints, craftingPoints, false, "CP"));
        return new(parameters, 0, 0, 0, 0, true);
    }

    private static Dictionary<EquipmentStatSemantic, int> CrafterStats(
        int craftsmanship,
        int control,
        int craftingPoints) => new()
        {
            [EquipmentStatSemantic.Craftsmanship] = craftsmanship,
            [EquipmentStatSemantic.Control] = control,
            [EquipmentStatSemantic.CraftingPoints] = craftingPoints,
        };

    private static EquipmentSlot Slot(EquipmentLoadoutPosition position) => position switch
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

    private static string EquippedItemName(EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => "Ra'Kaznar Cross-pein Hammer",
        EquipmentLoadoutPosition.OffHand => "Ra'Kaznar File",
        EquipmentLoadoutPosition.Head => "Thunderyards Silk Cap of Crafting",
        EquipmentLoadoutPosition.Body => "Thunderyards Silk Shirt of Crafting",
        EquipmentLoadoutPosition.Hands => "Gargantuaskin Halfgloves of Crafting",
        EquipmentLoadoutPosition.Legs => "Thunderyards Silk Culottes of Crafting",
        EquipmentLoadoutPosition.Feet => "Gargantuaskin Workboots of Crafting",
        EquipmentLoadoutPosition.Ears => "Black Star Earrings of Crafting",
        EquipmentLoadoutPosition.Neck => "Black Star Choker of Crafting",
        EquipmentLoadoutPosition.Wrists => "Black Star Bracelets of Crafting",
        EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing => "Black Star Ring of Crafting",
        _ => throw new ArgumentOutOfRangeException(nameof(position)),
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
#endif
