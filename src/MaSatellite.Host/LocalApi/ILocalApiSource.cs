using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;
using MaSatellite.Core.Volume;

namespace MaSatellite.Host.LocalApi;

/// <summary>What the local API needs from the player. Implemented by the satellite host; faked in tests.</summary>
public interface ILocalApiSource
{
    SatelliteStatus Status { get; }

    long ServerNowMicros { get; }

    IVolumeControl Volume { get; }

    event Action<SatelliteStatus>? StatusChanged;

    Task SendCommandAsync(ControllerCommand command);
}
