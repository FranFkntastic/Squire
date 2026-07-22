using MarketMafioso.Squire.Observation;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireRecoveryPromptTests
{
    [Theory]
    [InlineData("Leave this duty?")]
    [InlineData("Möchtest du den Inhalt verlassen?")]
    [InlineData("Quitter cette mission ?")]
    [InlineData("コンテンツから退出しますか？")]
    public void DutyExitPromptRecognizesLeaveConfirmation(string prompt)
    {
        Assert.True(DalamudSquireRecoveryCoordinator.IsExpectedDutyExitPrompt(prompt));
    }

    [Theory]
    [InlineData("Purchase this item?")]
    [InlineData("Return to your home point?")]
    [InlineData("")]
    public void DutyExitPromptRejectsUnrelatedConfirmation(string prompt)
    {
        Assert.False(DalamudSquireRecoveryCoordinator.IsExpectedDutyExitPrompt(prompt));
    }
}
