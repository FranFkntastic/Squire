using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class AdvisorStatFamilyCompatibilityTests
{
    [Fact]
    public void GathererContextResolutionPreservesLegacyAndCanonicalValues()
    {
        Assert.Same(
            GathererAdvisorStatFamily.LegendaryNodeContext,
            GathererAdvisorStatFamily.Instance.ResolveContext("LegendaryNodeGeneralYield"));
        Assert.Same(
            GathererAdvisorStatFamily.LegendaryNodeContext,
            GathererAdvisorStatFamily.Instance.ResolveContext(MinerBotanistUtilityProfile.LegendaryContextId));
    }

    [Fact]
    public void CrafterContextResolutionFallsBackToDefault()
    {
        var family = CrafterAdvisorStatFamily.Instance;
        Assert.Same(
            CrafterAdvisorStatFamily.OrdinaryCraftContext,
            family.ResolveContext(MinerBotanistUtilityProfile.LegendaryContextId));
    }

    [Fact]
    public void FisherHasNoExpansionFamilyAndUsesTerminalScopeLanguage()
    {
        Assert.Null(AdvisorStatFamilies.Resolve(AdvisorStatFamilies.FisherClassJobId));
        Assert.Equal(
            "Fisher is permanently unsupported and out of scope for Squire Outfitter.",
            AdvisorStatFamilies.UnsupportedDiagnostic(AdvisorStatFamilies.FisherClassJobId));
    }

}
