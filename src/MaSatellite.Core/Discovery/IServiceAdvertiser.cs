namespace MaSatellite.Core.Discovery;

/// <summary>Announces the player on the local network so servers can find and connect to it.</summary>
public interface IServiceAdvertiser
{
    /// <summary>Starts advertising; dispose the result to withdraw the announcement.</summary>
    IDisposable Advertise(string instanceName, int port, string path, string friendlyName);
}
