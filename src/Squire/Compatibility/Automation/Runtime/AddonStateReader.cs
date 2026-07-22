using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static ECommons.GenericHelpers;

namespace MarketMafioso.Automation.Runtime;

public sealed class AddonStateReader
{
    private readonly IGameGui _gameGui;

    public AddonStateReader(IGameGui gameGui)
    {
        _gameGui = gameGui;
    }

    public unsafe T* GetAddon<T>(string addonName)
        where T : unmanaged
    {
        return _gameGui.GetAddonByName<T>(addonName, 1);
    }

    public unsafe bool IsReady(AtkUnitBase* addon)
    {
        return addon != null && IsAddonReady(addon);
    }
}
