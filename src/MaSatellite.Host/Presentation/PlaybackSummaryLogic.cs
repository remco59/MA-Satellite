using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;
using MaSatellite.Host.Library;

namespace MaSatellite.Host.Presentation;

/// <summary>
/// The track shown in the main window, independently of where playback metadata came from.
/// Artwork is supplied as bytes for this PC (Sendspin) or as a URL for other MA players.
/// </summary>
public sealed record PlaybackSummary(
    string Title,
    string Artist,
    string? ArtworkUrl,
    ArtworkImage? LocalArtwork,
    bool IsPlaying,
    bool HasTrack);

public static class PlaybackSummaryLogic
{
    /// <summary>
    /// The local Sendspin session is authoritative for this PC. A queue entry is not
    /// required: externally started playback may not be present in the MA queue list.
    /// </summary>
    public static PlaybackSummary FromLocal(SatelliteStatus status, string playerName)
    {
        var session = status.Session;
        var md = session.Metadata;
        var hasTrack = status.Connection == ConnectionState.Connected &&
                       !string.IsNullOrWhiteSpace(md.Title);

        return new PlaybackSummary(
            hasTrack ? md.Title! : "Nothing playing",
            hasTrack && !string.IsNullOrWhiteSpace(md.Artist) ? md.Artist! : playerName,
            hasTrack ? md.ArtworkUrl : null,
            hasTrack ? session.Artwork : null,
            hasTrack && session.Group.PlaybackState == GroupPlaybackState.Playing,
            hasTrack);
    }

    /// <summary>
    /// The server's current_item is more reliable than looking up current_index in
    /// the first page of queue items (the item may be beyond that page).
    /// </summary>
    public static PlaybackSummary FromQueue(QueueView view)
    {
        var queue = view.Queue;
        var current = queue?.CurrentItem;
        if (string.IsNullOrWhiteSpace(current?.Name) && queue?.CurrentIndex is int index)
        {
            current = view.Items.FirstOrDefault(item => item.Index == index);
        }

        var hasTrack = !string.IsNullOrWhiteSpace(current?.Name);
        return new PlaybackSummary(
            hasTrack ? current!.Name : "Nothing playing",
            hasTrack && !string.IsNullOrWhiteSpace(current!.Subtitle) ? current.Subtitle! : view.Target.Name,
            hasTrack ? current!.ImageUrl : null,
            null,
            hasTrack && (string.Equals(queue?.State, "play", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(queue?.State, "playing", StringComparison.OrdinalIgnoreCase)),
            hasTrack);
    }
}
