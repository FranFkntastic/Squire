using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireDispositionEligibilityEvaluatorTests
{
    private readonly SquireDispositionEligibilityEvaluator evaluator = new();

    [Fact]
    public void DesynthesisRequiresBothItemEligibilityAndQuestUnlock()
    {
        var locked = evaluator.Evaluate(Definition(), new SquireDispositionCapabilities(false));
        var unlocked = evaluator.Evaluate(Definition(), new SquireDispositionCapabilities(true));

        Assert.DoesNotContain(SquireDisposition.Desynthesize, locked.SupportedDispositions);
        Assert.Contains(locked.Reasons, reason => reason.Code == "DesynthesisNotUnlocked");
        Assert.Contains(SquireDisposition.Desynthesize, unlocked.SupportedDispositions);
    }

    [Fact]
    public void IndisposableItemCannotBeDiscarded()
    {
        var result = evaluator.Evaluate(Definition() with { IsDiscardable = false }, new SquireDispositionCapabilities(true));

        Assert.DoesNotContain(SquireDisposition.Discard, result.SupportedDispositions);
    }

    [Fact]
    public void ExpertDeliveryRequiresProvenEligibility()
    {
        var eligible = evaluator.Evaluate(
            Definition() with { ExpertDeliveryEligibility = ExpertDeliveryEligibility.Eligible },
            new SquireDispositionCapabilities(true));
        var ineligible = evaluator.Evaluate(
            Definition() with { ExpertDeliveryEligibility = ExpertDeliveryEligibility.Ineligible },
            new SquireDispositionCapabilities(true));

        Assert.Contains(SquireDisposition.ExpertDelivery, eligible.SupportedDispositions);
        Assert.DoesNotContain(SquireDisposition.ExpertDelivery, ineligible.SupportedDispositions);
    }

    [Fact]
    public void UnknownSignalsFailClosedWithReasons()
    {
        var definition = Definition() with
        {
            IsDesynthesizable = null,
            IsVendorSellable = null,
            VendorSellPrice = null,
            IsDiscardable = null,
        };

        var result = evaluator.Evaluate(definition, new SquireDispositionCapabilities(true));

        Assert.Empty(result.SupportedDispositions);
        Assert.Contains(result.Reasons, reason => reason.Code == "DesynthesisEligibilityUnknown");
        Assert.Contains(result.Reasons, reason => reason.Code == "VendorEligibilityUnknown");
        Assert.Contains(result.Reasons, reason => reason.Code == "DiscardEligibilityUnknown");
    }

    private static EquipmentItemDefinition Definition() =>
        new(100, "Test Gear", 1, 1, EquipmentSlot.Body, new HashSet<uint> { 1 }, 1, true, false, true, true, 10, true, false, true, false);
}
