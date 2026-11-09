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

    /// <summary>Favourites for the playing track, when Music Assistant is available to provide them.</summary>
    ILocalApiFavorites? Favorites => null;

    Task SendCommandAsync(ControllerCommand command);
}
