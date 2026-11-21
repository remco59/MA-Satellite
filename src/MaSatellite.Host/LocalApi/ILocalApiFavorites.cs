namespace MaSatellite.Host.LocalApi;

/// <summary>Favourite handling for the track playing on this PC. It needs Music Assistant, so the local API treats it as optional.</summary>
public interface ILocalApiFavorites
{
    /// <summary>Whether the playing track is a favourite, or null when that cannot be told (not in this PC's queue, or Music Assistant unreachable).</summary>
    Task<bool?> GetCurrentAsync(CancellationToken ct);

    /// <summary>Adds or removes the playing track and returns the new state. Throws <see cref="InvalidOperationException"/> with a user-facing message when it cannot.</summary>
    Task<bool> SetCurrentAsync(bool favorite, CancellationToken ct);
}
