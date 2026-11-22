namespace MaSatellite.Host.LocalApi;

/// <summary>Minimal surface exposed to the authenticated, LAN-only announcement endpoint.</summary>
public interface IAnnouncementApiSource
{
    string PlayerName { get; }
    string MacAddress { get; }
    string ApiToken { get; }
    bool IsStreamActive { get; }
    Task PlayAnnouncementAsync(byte[] media, string contentType, int volumeLevel, CancellationToken cancellationToken = default);
}
