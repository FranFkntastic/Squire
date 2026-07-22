using Squire.Interop;
using Xunit;

namespace Squire.Tests;

public sealed class IpcProviderTests
{
    [Fact]
    public void Provider_RegistersVersionedStandaloneChannelsAndUnregistersThemOnDispose()
    {
        var registrar = new RecordingRegistrar();
        var opened = false;
        var provider = new SquireIpcProvider(registrar, "provider-1", () => "{\"snapshot\":true}", () => opened = true);

        Assert.Equal(
            new[] { IpcChannels.Changed, IpcChannels.GetCapabilities, IpcChannels.GetSnapshot, IpcChannels.Open },
            registrar.Registered.OrderBy(value => value, StringComparer.Ordinal));
        Assert.Contains("standalone-plugin", registrar.Invoke(IpcChannels.GetCapabilities));
        Assert.Contains("provider-1", registrar.Invoke(IpcChannels.GetCapabilities));
        Assert.Contains("snapshot", registrar.Invoke(IpcChannels.Open));
        Assert.True(opened);

        provider.Dispose();

        Assert.Equal(registrar.Registered.OrderBy(value => value), registrar.Unregistered.OrderBy(value => value));
    }

    private sealed class RecordingRegistrar : IIpcRegistrar
    {
        private readonly Dictionary<string, Func<string>> callbacks = new(StringComparer.Ordinal);
        public HashSet<string> Registered { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Unregistered { get; } = new(StringComparer.Ordinal);

        public void Register(string channel, Func<string> callback)
        {
            Registered.Add(channel);
            callbacks[channel] = callback;
        }

        public void RegisterNotification(string channel) => Registered.Add(channel);
        public void SendNotification(string channel, string json) { }
        public void Unregister(string channel) => Unregistered.Add(channel);
        public string Invoke(string channel) => callbacks[channel]();
    }
}
