using Franthropy.Dalamud.UI.Styling;
using MarketMafioso.Windows.Main;

namespace Squire.UI;

internal static class SquireUiTheme
{
    public static DalamudUiTheme Current { get; } =
        DalamudUiTheme.Dark(
            MarketMafiosoUiTheme.Header,
            DalamudUiDensity.Compact,
            DalamudUiMotionLevel.Off) with
        {
            Palette = DalamudUiPalette.Dark(MarketMafiosoUiTheme.Header) with
            {
                Success = MarketMafiosoUiTheme.Success,
                Warning = MarketMafiosoUiTheme.Warning,
                Error = MarketMafiosoUiTheme.Error,
                Muted = MarketMafiosoUiTheme.Muted,
            },
        };
}
