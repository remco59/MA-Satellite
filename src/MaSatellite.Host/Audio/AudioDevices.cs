namespace MaSatellite.Host.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public interface IAudioDeviceCatalog
{
    /// <summary>Active playback devices. The default device is flagged.</summary>
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
}
