using System;

namespace MarketMafioso.Squire;

public sealed record SquireExecutionRecoveryPolicy(
    bool RecoverFromKnockout,
    bool WaitForCombatToEnd,
    TimeSpan CombatRecoveryTimeout,
    bool LeaveDutyToExecute,
    bool PauseGatherBuddyReborn,
    bool PauseQuestionable,
    bool PauseArtisan,
    bool CloseSafeUserMenus)
{
    public static SquireExecutionRecoveryPolicy From(SquireConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(
            configuration.RecoverFromKnockout,
            configuration.WaitForCombatToEnd,
            TimeSpan.FromSeconds(Math.Clamp(configuration.CombatRecoveryTimeoutSeconds, 10, 600)),
            configuration.LeaveDutyToExecute,
            configuration.PauseGatherBuddyReborn,
            configuration.PauseQuestionable,
            configuration.PauseArtisan,
            configuration.CloseSafeUserMenus);
    }
}
