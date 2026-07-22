using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed record MarketAcquisitionRequestBuilderContext(
    string CharacterName,
    string World,
    bool HasCharacterScope,
    bool CharacterScopeTemporarilyUnavailable,
    bool IsBusy,
    bool IsRouteActive,
    MarketAcquisitionClaimView? ClaimedRequest,
    MarketAcquisitionPlan? CurrentPlan,
    string? CurrentPlanHash);
