using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public static class SquireFingerprintMatcher
{
    public static bool ExactMatch(EquipmentInstanceFingerprint expected, EquipmentInstanceFingerprint observed) =>
        EquipmentInstanceFingerprintComparer.Instance.Equals(expected, observed);
}
