using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons.Reflection;

namespace MarketMafioso.Squire.Outfitter;

public interface IOutfitterRetainerMetadataSource
{
    IReadOnlyList<OutfitterRetainerMetadata> ReadAll();
}

public sealed class AutoRetainerOutfitterMetadataSource : IOutfitterRetainerMetadataSource
{
    private const string AutoRetainerInternalName = "AutoRetainer";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private IReadOnlyList<OutfitterRetainerMetadata> cached = [];
    private DateTimeOffset refreshAfter = DateTimeOffset.MinValue;

    public AutoRetainerOutfitterMetadataSource(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IReadOnlyList<OutfitterRetainerMetadata> ReadAll()
    {
        if (DateTimeOffset.UtcNow < refreshAfter)
            return cached;
        var fresh = ReadFresh();
        if (fresh is not null)
        {
            cached = fresh;
            refreshAfter = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        else
        {
            refreshAfter = DateTimeOffset.UtcNow.AddSeconds(2);
        }
        return cached;
    }

    private IReadOnlyList<OutfitterRetainerMetadata>? ReadFresh()
    {
        if (!pluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded && string.Equals(plugin.InternalName, AutoRetainerInternalName, StringComparison.OrdinalIgnoreCase)))
            return null;

        try
        {
            if (!DalamudReflector.TryGetDalamudPlugin(
                    AutoRetainerInternalName,
                    out var instance,
                    out _,
                    suppressErrors: true,
                    ignoreCache: true) ||
                instance is null ||
                Read(instance, "config") is not { } autoRetainerConfig ||
                Read(autoRetainerConfig, "OfflineData") is not IEnumerable characters)
                return null;

            var values = new List<OutfitterRetainerMetadata>();
            foreach (var character in characters)
            {
                if (character is null || !TryRead(character, "CID", out ulong contentId) || contentId == 0)
                    continue;
                var characterName = Read<string>(character, "Name") ?? string.Empty;
                var homeWorld = Read<string>(character, "World") ?? string.Empty;
                if (Read(character, "RetainerData") is not IEnumerable retainers)
                    continue;
                foreach (var retainer in retainers)
                {
                    if (retainer is null || !TryRead(retainer, "RetainerID", out ulong retainerId) || retainerId == 0)
                        continue;
                    values.Add(new(
                        contentId,
                        characterName,
                        homeWorld,
                        retainerId,
                        Read<string>(retainer, "Name") ?? string.Empty,
                        TryRead(retainer, "Job", out uint job) ? job : 0,
                        TryRead(retainer, "Level", out uint level) ? level : 0));
                }
            }
            return values;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[MarketMafioso] AutoRetainer Outfitter metadata is unavailable");
            return null;
        }
    }

    private static object? Read(object source, string name)
    {
        var type = source.GetType();
        return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source)
               ?? type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
    }

    private static T? Read<T>(object source, string name) where T : class => Read(source, name) as T;

    private static bool TryRead<T>(object source, string name, out T value) where T : struct =>
        TryConvert(Read(source, name), out value);

    private static bool TryConvert<T>(object? source, out T value) where T : struct
    {
        try
        {
            if (source is not null)
            {
                value = (T)Convert.ChangeType(source, typeof(T));
                return true;
            }
        }
        catch (Exception) when (source is not null)
        {
        }
        value = default;
        return false;
    }
}
