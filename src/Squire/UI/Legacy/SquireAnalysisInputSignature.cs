using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;
using Newtonsoft.Json;

namespace MarketMafioso.Windows.Squire;

internal static class SquireAnalysisInputSignature
{
    public static string Create(
        CharacterEquipmentSnapshot snapshot,
        SquireDispositionCapabilities capabilities,
        SquireProtectionPolicy policy) =>
        JsonConvert.SerializeObject(new
        {
            Identity = new
            {
                snapshot.Identity.Scope,
                snapshot.Identity.CurrentWorldId,
                snapshot.Identity.ActiveClassJobId,
                snapshot.Identity.IsLoggedIn,
                snapshot.Identity.Status,
                snapshot.Identity.Diagnostic,
            },
            snapshot.Jobs,
            snapshot.Gearsets,
            Instances = snapshot.Instances.Select(instance => new
            {
                instance.Fingerprint,
                instance.IsEquipped,
            }).ToArray(),
            snapshot.Diagnostics,
            Capabilities = capabilities,
            Policy = new
            {
                policy.CharacterContentId,
                policy.ProtectSignedGear,
                policy.ProtectFutureLevelingGear,
                policy.ProtectBlueAndPurpleGear,
                policy.AllowRiskyMateriaRetrieval,
                LegacyRules = policy.Rules?
                    .OrderBy(rule => rule.Id)
                    .ToArray() ?? [],
                CleanupRules = policy.CleanupRules?
                    .OrderBy(rule => rule.Id, System.StringComparer.Ordinal)
                    .ToArray() ?? [],
            },
        }, Formatting.None);
}
