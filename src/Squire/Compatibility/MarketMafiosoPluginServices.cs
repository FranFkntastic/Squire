using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace MarketMafioso;

internal static class Plugin
{
    internal static ICommandManager CommandManager => global::Squire.Plugin.CommandManager;
    internal static IClientState ClientState => global::Squire.Plugin.ClientState;
    internal static IFramework Framework => global::Squire.Plugin.Framework;
    internal static IGameGui GameGui => global::Squire.Plugin.GameGui;
    internal static IObjectTable ObjectTable => global::Squire.Plugin.ObjectTable;
    internal static ITargetManager TargetManager => global::Squire.Plugin.TargetManager;
    internal static IPluginLog Log => global::Squire.Plugin.Log;
}
