using System.Text.Json.Serialization;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;

namespace MaSatellite.Host.LocalApi;

/// <summary>The JSON shape served to widgets. Field names are stable API; change them only additively.</summary>
public sealed record NowPlayingDto(
    bool Connected,
    string? Server,
    string? Group,
    string Playback,
    string? Title,
    string? Artist,
    string? AlbumArtist,
    string? Album,
    int? Year,
    int? Track,
    long? DurationMs,
    long? PositionMs,
    long PositionUpdatedAt,
    int PlaybackSpeed,
    string? ArtworkUrl,
    int Volume,
    bool Muted,
    bool InSync,
    bool Shuffle,
    string Repeat,
    IReadOnlyList<string> SupportedCommands);

public static class NowPlayingMapper
{
    /// <param name="status">Current status.</param>
    /// <param name="serverNowMicros">The server clock now, to extrapolate the playback position.</param>
    /// <param name="unixNowMs">Wall-clock time stamped on the position so clients can extrapolate too.</param>
    public static NowPlayingDto Map(SatelliteStatus status, long serverNowMicros, long unixNowMs)
    {
        var s = status.Session;
        var md = s.Metadata;
        var connected = status.Connection == ConnectionState.Connected;
        var hasTrack = !string.IsNullOrEmpty(md.Title);
        var playback = !connected ? "idle"
            : s.Group.PlaybackState == GroupPlaybackState.Playing ? "playing"
            : hasTrack ? "paused"
            : "stopped";

        long? duration = md.Progress is { TrackDurationMs: > 0 } p ? p.TrackDurationMs : null;
        var position = serverNowMicros > 0 ? md.PositionMs(serverNowMicros) : md.Progress?.TrackProgressMs;

        return new NowPlayingDto(
            connected,
            s.ServerName,
            s.Group.GroupName,
            playback,
            md.Title,
            md.Artist,
            md.AlbumArtist,
            md.Album,
            md.Year,
            md.Track,
            duration,
            position,
            unixNowMs,
            md.Progress?.PlaybackSpeed ?? 0,
            s.Artwork is null ? null : $"/api/artwork?v={s.Artwork.Version}",
            status.Volume,
            status.Muted,
            status.InSync,
            s.Controller.Shuffle,
            s.Controller.Repeat.ToString().ToLowerInvariant(),
            s.Controller.SupportedCommands);
    }
}

/// <summary>Envelope for messages on <c>/api/events</c>.</summary>
public sealed record ApiEvent(string Type, [property: JsonPropertyName("data")] object? Data);

/// <summary>Body of <c>POST /api/command</c>.</summary>
public sealed record ApiCommand(string Command, int? Volume = null, bool? Mute = null, long? PositionMs = null, long? OffsetMs = null);
