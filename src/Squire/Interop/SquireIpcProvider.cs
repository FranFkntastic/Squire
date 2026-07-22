using System.Text.Json;

namespace Squire.Interop;

public sealed class SquireIpcProvider : IDisposable
{
    private readonly IIpcRegistrar registrar;
    private readonly Func<string> createSnapshot;
    private readonly string providerInstanceId;
    private bool disposed;

    public SquireIpcProvider(IIpcRegistrar registrar, string providerInstanceId, Func<string> createSnapshot, Action openWindow)
    {
        this.registrar = registrar;
        this.providerInstanceId = providerInstanceId;
        this.createSnapshot = createSnapshot;
        registrar.Register(IpcChannels.GetCapabilities, GetCapabilities);
        registrar.Register(IpcChannels.GetSnapshot, createSnapshot);
        registrar.Register(IpcChannels.Open, () =>
        {
            openWindow();
            return createSnapshot();
        });
        registrar.RegisterNotification(IpcChannels.Changed);
    }

    public string GetCapabilities() => JsonSerializer.Serialize(new
    {
        schema = "gooseworks-squire-capabilities/v1",
        providerInstanceId,
        capabilities = new[] { "standalone-plugin", "configuration", "legacy-mmf-import", "open-window" },
    });

    public void PublishChanged()
    {
        if (!disposed)
            registrar.SendNotification(IpcChannels.Changed, createSnapshot());
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        registrar.Unregister(IpcChannels.GetCapabilities);
        registrar.Unregister(IpcChannels.GetSnapshot);
        registrar.Unregister(IpcChannels.Open);
        registrar.Unregister(IpcChannels.Changed);
    }
}
