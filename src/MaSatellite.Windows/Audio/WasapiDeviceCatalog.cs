using MaSatellite.Host.Audio;
using NAudio.CoreAudioApi;

namespace MaSatellite.Windows.Audio;

public sealed class WasapiDeviceCatalog : IAudioDeviceCatalog
{
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = def.ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // no playback device at all
        }

        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }

        return result;
    }
}
