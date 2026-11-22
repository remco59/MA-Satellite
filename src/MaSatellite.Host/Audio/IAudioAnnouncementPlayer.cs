namespace MaSatellite.Host.Audio;

/// <summary>Decodes an encoded announcement and queues it for mixing into live Sendspin playback.</summary>
public interface IAudioAnnouncementPlayer
{
    Task PlayAsync(byte[] media, string contentType, int volumeLevel, CancellationToken cancellationToken = default);
}
