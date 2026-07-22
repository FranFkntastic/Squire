using System;
using System.Buffers.Binary;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace MarketMafioso.MarketAcquisition;

internal sealed unsafe class DalamudMarketPurchasePacketObserver : IDisposable
{
    private const int MinimumPacketLength = 12;
    private readonly IPluginLog log;
    private Hook<PacketDispatcher.Delegates.HandleMarketBoardPurchasePacket>? hook;

    public DalamudMarketPurchasePacketObserver(IGameInteropProvider interopProvider, IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(interopProvider);
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        try
        {
            var address = PacketDispatcher.Addresses.HandleMarketBoardPurchasePacket.Value;
            if (address == 0)
                throw new InvalidOperationException("HandleMarketBoardPurchasePacket address is unavailable.");
            Queue = new MarketPurchasePacketEvidenceQueue(true);
            hook = interopProvider.HookFromAddress<PacketDispatcher.Delegates.HandleMarketBoardPurchasePacket>(
                address,
                HandlePurchasePacketDetour);
            hook.Enable();
        }
        catch (Exception exception)
        {
            hook?.Dispose();
            hook = null;
            Queue = new MarketPurchasePacketEvidenceQueue(false);
            log.Error(exception, "[MarketMafioso] Server purchase evidence hook is unavailable; live confirmation remains blocked.");
        }
    }

    public IMarketPurchasePacketEvidenceQueue Queue { get; private set; }
    public bool IsAvailable => hook?.IsEnabled == true && Queue.IsAvailable;

    private void HandlePurchasePacketDetour(uint targetId, nint packetData)
    {
        try
        {
            if (packetData != 0 && TryDecode(new ReadOnlySpan<byte>((void*)packetData, MinimumPacketLength), out var packet))
            {
                Queue.Enqueue(
                    DateTimeOffset.UtcNow,
                    packet.RawCatalogId,
                    packet.IsHighQuality,
                    packet.Quantity);
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Failed to enqueue server purchase evidence.");
        }
        finally
        {
            hook!.Original(targetId, packetData);
        }
    }

    internal static bool TryDecode(ReadOnlySpan<byte> packetData, out DecodedMarketPurchasePacket packet)
    {
        packet = default;
        if (packetData.Length < MinimumPacketLength)
            return false;
        var rawCatalogId = BinaryPrimitives.ReadUInt32LittleEndian(packetData);
        var quantity = BinaryPrimitives.ReadUInt32LittleEndian(packetData[8..]);
        var itemId = MarketPurchaseCatalogId.Normalize(rawCatalogId);
        if (rawCatalogId == 0 || itemId == 0 || quantity == 0)
            return false;
        packet = new(rawCatalogId, itemId, rawCatalogId >= 1_000_000, quantity);
        return true;
    }

    public void Dispose()
    {
        hook?.Dispose();
        hook = null;
    }

    internal readonly record struct DecodedMarketPurchasePacket(
        uint RawCatalogId,
        uint ItemId,
        bool IsHighQuality,
        uint Quantity);
}
