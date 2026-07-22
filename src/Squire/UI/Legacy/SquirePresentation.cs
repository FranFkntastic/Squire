using System.Linq;
using System.Text;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;

namespace MarketMafioso.Windows.Squire;

internal static class SquirePresentation
{
    public static string FormatReasons(SquireCandidate candidate) =>
        string.Join("\n", candidate.Reasons.Select(reason => $"• {reason.Message}"));

    public static string FormatLocation(EquipmentInstanceFingerprint fingerprint) =>
        $"{FormatContainer(fingerprint.Container)}, Slot {fingerprint.SlotIndex}";

    public static string FormatContainer(string container) => container switch
    {
        "Inventory1" => "Inventory Bag 1",
        "Inventory2" => "Inventory Bag 2",
        "Inventory3" => "Inventory Bag 3",
        "Inventory4" => "Inventory Bag 4",
        "ArmoryMainHand" => "Armory Chest: Main Hand",
        "ArmoryOffHand" => "Armory Chest: Off Hand",
        "ArmoryHead" => "Armory Chest: Head",
        "ArmoryBody" => "Armory Chest: Body",
        "ArmoryHands" => "Armory Chest: Hands",
        "ArmoryLegs" => "Armory Chest: Legs",
        "ArmoryFeet" => "Armory Chest: Feet",
        "ArmoryEar" => "Armory Chest: Earrings",
        "ArmoryNeck" => "Armory Chest: Necklaces",
        "ArmoryWrist" => "Armory Chest: Wrists",
        "ArmoryRings" => "Armory Chest: Rings",
        "ArmorySoulCrystal" => "Armory Chest: Soul Crystals",
        _ => SplitCode(container),
    };

    public static string FormatDisposition(SquireDisposition disposition) => disposition switch
    {
        SquireDisposition.ExpertDelivery => "Expert Delivery",
        SquireDisposition.VendorSell => "Vendor Sale",
        SquireDisposition.Desynthesize => "Desynthesize",
        SquireDisposition.Discard => "Discard",
        SquireDisposition.Keep => "Keep",
        SquireDisposition.Unsupported => "No supported route",
        _ => disposition.ToString(),
    };

    public static string FormatAssessment(SquireAssessment assessment) => assessment switch
    {
        SquireAssessment.EvaluationFailure => "Evaluation Failure",
        SquireAssessment.Protected => "Protected",
        SquireAssessment.Candidate => "Candidate",
        SquireAssessment.Unsupported => "Unsupported",
        _ => assessment.ToString(),
    };

    public static string FormatReasonSummary(SquireCandidate candidate) => candidate.Reasons.Count switch
    {
        0 => "No evaluation result",
        1 => ReasonLabel(candidate.Reasons[0].Code),
        _ => $"{ReasonLabel(candidate.Reasons[0].Code)} (+{candidate.Reasons.Count - 1} rule{(candidate.Reasons.Count == 2 ? string.Empty : "s")})",
    };

    public static string ReasonLabel(string code) => code switch
    {
        "RetainedCoverageForAllUnlockedJobs" => "Retained coverage for every relevant job",
        "NoRetainedCoverage" => "No safely superseding baseline",
        "FutureUnlockedJobUse" => "Potential future use",
        "FutureLevelingUseNotProtected" => "Future-use protection disabled",
        "NoObtainedEligibleJob" => "No obtained eligible job",
        "MateriaRetrievalRequired" => "Materia retrieval risk accepted",
        "MateriaRetrievalNotUnlocked" => "Materia retrieval unavailable",
        "MateriaRetrievalUnlockUnknown" => "Materia retrieval unlock unknown",
        "MateriaRetrievalRiskNotAuthorized" => "Materia retrieval risk not authorized",
        "CurrentlyEquipped" => "Currently equipped",
        "ReferencedByGearset" => "Required by a saved gearset",
        "NotEquipment" => "Not equipment",
        "SoulCrystal" => "Soul crystal",
        "ProtectedItemFamily" => "Protected item family",
        "UnknownItemRarity" => "Unknown rarity",
        "HighRarityEquipment" => "High-rarity protection",
        "HighRarityProtectionDisabled" => "High-rarity protection disabled",
        "ItemProtectionRule" => "Explicit item protection",
        "InvalidRuleConfiguration" => "Invalid Squire rule",
        "DuplicateRetentionFloor" => "Duplicate minimum reached",
        "DuplicateRetentionSurplus" => "Duplicate minimum",
        "DuplicateRetentionFloorLost" => "Duplicate minimum would be crossed",
        "ExpertDeliveryEligibilityUnknown" => "Expert Delivery eligibility unknown",
        "PlayerSignature" => "Player signature protection",
        "ArmoireEligibilityUnknown" => "Armoire eligibility unknown",
        "ArmoireEligible" => "Armoire eligible",
        "RecoverabilityUnknown" => "Recoverability unknown",
        "StatlessAllClassesEquipment" => "Likely cosmetic",
        "SpecialPurposeEquipment" => "Special-purpose equipment",
        "DesynthesisNotUnlocked" => "Desynthesis unavailable",
        "NoSupportedDisposition" => "No authorized cleanup route",
        "PartialSnapshot" => "Incomplete snapshot",
        _ => SplitCode(code),
    };

    public static string SplitCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "Unclassified rule";
        var result = new StringBuilder(code.Length + 8);
        for (var index = 0; index < code.Length; index++)
        {
            var character = code[index];
            if (index > 0 && char.IsUpper(character) && !char.IsUpper(code[index - 1]))
                result.Append(' ');
            result.Append(character);
        }
        return result.ToString();
    }
}
