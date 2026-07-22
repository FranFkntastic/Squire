using System.Collections.Generic;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed record SquireDispositionEligibility(
    IReadOnlySet<SquireDisposition> SupportedDispositions,
    IReadOnlyList<SquireReason> Reasons);

public sealed class SquireDispositionEligibilityEvaluator
{
    public SquireDispositionEligibility Evaluate(
        EquipmentItemDefinition definition,
        SquireDispositionCapabilities capabilities)
    {
        var supported = new HashSet<SquireDisposition>();
        var reasons = new List<SquireReason>();

        if (definition.ExpertDeliveryEligibility == ExpertDeliveryEligibility.Eligible)
            supported.Add(SquireDisposition.ExpertDelivery);

        if (definition.IsDesynthesizable == true)
        {
            if (capabilities.DesynthesisUnlocked == true)
                supported.Add(SquireDisposition.Desynthesize);
            else
                reasons.Add(capabilities.DesynthesisUnlocked == false
                    ? new("DesynthesisNotUnlocked", "Desynthesis requires completion of Gone to Pieces.", SquireReasonSeverity.Information)
                    : new("DesynthesisUnlockUnknown", "Desynthesis unlock state could not be proven.", SquireReasonSeverity.Warning));
        }
        else if (definition.IsDesynthesizable is null)
        {
            reasons.Add(new("DesynthesisEligibilityUnknown", "The item's desynthesis eligibility is unknown.", SquireReasonSeverity.Warning));
        }

        if (definition.IsVendorSellable == true && definition.VendorSellPrice is > 0)
            supported.Add(SquireDisposition.VendorSell);
        else if (definition.IsVendorSellable is null || definition.VendorSellPrice is null)
            reasons.Add(new("VendorEligibilityUnknown", "The item's NPC-sale eligibility is unknown.", SquireReasonSeverity.Warning));

        if (definition.IsDiscardable == true)
            supported.Add(SquireDisposition.Discard);
        else if (definition.IsDiscardable is null)
            reasons.Add(new("DiscardEligibilityUnknown", "The item's discard eligibility is unknown.", SquireReasonSeverity.Warning));

        return new SquireDispositionEligibility(supported, reasons);
    }
}
