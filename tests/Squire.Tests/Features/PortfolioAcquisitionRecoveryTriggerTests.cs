using MarketMafioso.Squire.Outfitter.Portfolio;

namespace MarketMafioso.Tests.Squire;

public sealed class PortfolioAcquisitionRecoveryTriggerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PortfolioAcquisitionRecoveryTriggerKind.WindowOpened)]
    [InlineData(PortfolioAcquisitionRecoveryTriggerKind.WorkspaceReturned)]
    public void WindowOrWorkspaceReturn_SchedulesOneDebouncedAttempt(
        PortfolioAcquisitionRecoveryTriggerKind kind)
    {
        var scheduled = PortfolioAcquisitionRecoveryTrigger.Schedule(
            PortfolioAcquisitionRecoveryTrigger.Empty, kind, Now);

        Assert.False(PortfolioAcquisitionRecoveryTrigger.IsDue(scheduled, Now));
        Assert.True(PortfolioAcquisitionRecoveryTrigger.IsDue(scheduled, Now + PortfolioAcquisitionRecoveryTrigger.Debounce));
        var begun = PortfolioAcquisitionRecoveryTrigger.Begin(scheduled);
        Assert.False(PortfolioAcquisitionRecoveryTrigger.IsDue(begun, Now.AddMinutes(1)));
        var completed = PortfolioAcquisitionRecoveryTrigger.Complete(begun);
        Assert.False(PortfolioAcquisitionRecoveryTrigger.IsDue(completed, Now.AddMinutes(1)));
    }

    [Fact]
    public void InventoryEvidence_OnlySchedulesForANewGeneration()
    {
        var first = PortfolioAcquisitionRecoveryTrigger.Schedule(
            PortfolioAcquisitionRecoveryTrigger.Empty,
            PortfolioAcquisitionRecoveryTriggerKind.InventoryEvidenceChanged,
            Now,
            "inventory-1");
        var same = PortfolioAcquisitionRecoveryTrigger.Schedule(
            first,
            PortfolioAcquisitionRecoveryTriggerKind.InventoryEvidenceChanged,
            Now.AddSeconds(1),
            "inventory-1");
        var changed = PortfolioAcquisitionRecoveryTrigger.Schedule(
            PortfolioAcquisitionRecoveryTrigger.Complete(PortfolioAcquisitionRecoveryTrigger.Begin(first)),
            PortfolioAcquisitionRecoveryTriggerKind.InventoryEvidenceChanged,
            Now.AddSeconds(2),
            "inventory-2");

        Assert.Equal(first, same);
        Assert.Equal("inventory-2", changed.LastInventoryEvidenceGeneration);
        Assert.Equal(Now.AddSeconds(2) + PortfolioAcquisitionRecoveryTrigger.Debounce, changed.DueAtUtc);
    }
}
