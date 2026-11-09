using MaSatellite.Host.Library;
using MaSatellite.Host.NowPlaying;
using MaSatellite.MaApi;

namespace MaSatellite.Host.LocalApi;

/// <summary>Favourites for the local API, backed by Music Assistant: finds the playing track in this PC's queue and (un)favourites it.</summary>
public sealed class LibraryFavorites : ILocalApiFavorites
{
    private const string NotInQueue = "Favourites need the track to be in your Music Assistant queue.";
    private readonly LibraryController _library;
    private readonly NowPlayingController _nowPlaying;

    public LibraryFavorites(LibraryController library, NowPlayingController nowPlaying)
    {
        _library = library;
        _nowPlaying = nowPlaying;
    }

    public async Task<bool?> GetCurrentAsync(CancellationToken ct)
    {
        try
        {
            var uri = await CurrentTrackUriAsync(ct).ConfigureAwait(false);
            if (uri is null)
            {
                return null;
            }

            var details = await _nowPlaying.GetDetailsAsync(uri, ct).ConfigureAwait(false);
            return details.IsFavorite;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // the heart simply stays disabled; the widget must never break over this
        }
    }

    public async Task<bool> SetCurrentAsync(bool favorite, CancellationToken ct)
    {
        string? uri;
        try
        {
            uri = await CurrentTrackUriAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Music Assistant could not be reached. " + ex.Message, ex);
        }

        if (uri is null)
        {
            throw new InvalidOperationException(NotInQueue);
        }

        try
        {
            return await _nowPlaying.SetFavoriteAsync(uri, favorite, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>The Music Assistant URI of the track currently playing on this PC, from its queue, or null.</summary>
    private async Task<string?> CurrentTrackUriAsync(CancellationToken ct)
    {
        var targets = await _library.GetTargetsAsync(ct).ConfigureAwait(false);
        var me = targets.FirstOrDefault(t => t.IsThisPc);
        if (me is null)
        {
            return null;
        }

        var view = await _library.GetQueueAsync(me, ct).ConfigureAwait(false);
        var currentIndex = view.Queue?.CurrentIndex;
        var current = view.Queue?.CurrentItem is { Name.Length: > 0 } c
            ? c
            : view.Items.FirstOrDefault(i => i.Index == currentIndex);
        return string.IsNullOrEmpty(current?.Uri) ? null : current.Uri;
    }
}
