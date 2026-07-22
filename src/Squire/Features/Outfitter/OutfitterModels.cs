using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;

namespace MarketMafioso.Squire.Outfitter;

public enum OutfitterTargetKind
{
    Job,
    Gearset,
    Retainer,
}

public sealed record OutfitterTarget(
    string Key,
    OutfitterTargetKind Kind,
    string Name,
    string Subtitle,
    CharacterJobSnapshot? Job = null,
    GearsetSnapshot? Gearset = null,
    CachedRetainer? Retainer = null,
    OutfitterRetainerMetadata? RetainerMetadata = null,
    string? OwnerCharacterName = null,
    string? OwnerHomeWorld = null,
    bool IsCurrentCharacter = false,
    bool IsReady = true,
    string? Diagnostic = null,
    RenderedRetainerEquipmentEvidence? RetainerEquipmentEvidence = null);

public sealed record OutfitterRetainerMetadata(
    ulong OwnerContentId,
    string OwnerCharacterName,
    string OwnerHomeWorld,
    ulong RetainerId,
    string RetainerName,
    uint ClassJobId,
    uint Level);
