using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireFingerprintMatcherTests
{
    [Fact]
    public void ExactMatch_RequiresEveryCapturedIdentityField()
    {
        var value = Fingerprint();
        Assert.True(SquireFingerprintMatcher.ExactMatch(value, Fingerprint()));
        Assert.False(SquireFingerprintMatcher.ExactMatch(value, Fingerprint() with { SlotIndex = 2 }));
        Assert.False(SquireFingerprintMatcher.ExactMatch(value, Fingerprint() with { MateriaIds = [9] }));
        Assert.False(SquireFingerprintMatcher.ExactMatch(value, Fingerprint() with { Condition = 29999 }));
    }

    private static EquipmentInstanceFingerprint Fingerprint() =>
        new(new CharacterScope(1, "Squire", 21), "ArmoryBody", 1, 100, false, 1, 30000, 500, 8, [7], 6, [1, 2]);
}
