using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Observation;

public interface ICharacterEquipmentSnapshotSource
{
    CharacterEquipmentSnapshot Capture();
}
