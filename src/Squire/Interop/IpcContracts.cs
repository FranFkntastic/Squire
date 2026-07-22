namespace Squire.Interop;

public static class IpcChannels
{
    public const string GetCapabilities = "Squire.v1.GetCapabilities";
    public const string GetSnapshot = "Squire.v1.GetSnapshot";
    public const string Open = "Squire.v1.Open";
    public const string Changed = "Squire.v1.Changed";
}

public interface IIpcRegistrar
{
    void Register(string channel, Func<string> callback);
    void RegisterNotification(string channel);
    void SendNotification(string channel, string json);
    void Unregister(string channel);
}

