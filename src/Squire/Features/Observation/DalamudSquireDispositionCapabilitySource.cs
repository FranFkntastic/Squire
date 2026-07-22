using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketMafioso.Squire.Observation;

public interface ISquireDispositionCapabilitySource
{
    SquireDispositionCapabilities Capture();
}

public sealed class DalamudSquireDispositionCapabilitySource : ISquireDispositionCapabilitySource
{
    public const uint GoneToPiecesQuestId = 65688;
    public const uint ForgingTheSpiritQuestId = 66174;

    public SquireDispositionCapabilities Capture() => new(
        QuestManager.IsQuestComplete(GoneToPiecesQuestId),
        QuestManager.IsQuestComplete(ForgingTheSpiritQuestId));
}
