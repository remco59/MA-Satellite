using System.Text.Json;

namespace MaSatellite.Core.Protocol;

public sealed record TrackProgress(long TrackProgressMs, long TrackDurationMs, int PlaybackSpeed);

/// <summary>Track metadata. Updates arrive as deltas; null means "cleared".</summary>
public sealed record MetadataState
{
    public long Timestamp { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public string? ArtworkUrl { get; init; }
    public int? Year { get; init; }
    public int? Track { get; init; }
    public TrackProgress? Progress { get; init; }

    /// <summary>Position in ms at <paramref name="serverNowMicros"/>, per the spec's formula; null without progress.</summary>
    public long? PositionMs(long serverNowMicros)
    {
        if (Progress is not { } p)
        {
            return null;
        }

        var calculated = p.TrackProgressMs + ((serverNowMicros - Timestamp) * (double)p.PlaybackSpeed / 1_000_000d);
        var clamped = Math.Max(calculated, 0);
        if (p.TrackDurationMs != 0)
        {
            clamped = Math.Min(clamped, p.TrackDurationMs);
        }

        return (long)clamped;
    }

    public static MetadataState Merge(MetadataState current, JsonElement delta)
    {
        var s = current;
        foreach (var prop in delta.EnumerateObject())
        {
            var isNull = prop.Value.ValueKind == JsonValueKind.Null;
            switch (prop.Name)
            {
                case "timestamp" when prop.Value.ValueKind == JsonValueKind.Number:
                    s = s with { Timestamp = prop.Value.GetInt64() };
                    break;
                case "title":
                    s = s with { Title = Str(prop.Value) };
                    break;
                case "artist":
                    s = s with { Artist = Str(prop.Value) };
                    break;
                case "album_artist":
                    s = s with { AlbumArtist = Str(prop.Value) };
                    break;
                case "album":
                    s = s with { Album = Str(prop.Value) };
                    break;
                case "artwork_url":
                    s = s with { ArtworkUrl = Str(prop.Value) };
                    break;
                case "year":
                    s = s with { Year = Int(prop.Value) };
                    break;
                case "track":
                    s = s with { Track = Int(prop.Value) };
                    break;
                case "progress":
                    s = s with { Progress = isNull ? null : ParseProgress(prop.Value) };
                    break;
            }
        }

        return s;
    }

    private static TrackProgress? ParseProgress(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new TrackProgress(
            e.TryGetProperty("track_progress", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt64() : 0,
            e.TryGetProperty("track_duration", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : 0,
            e.TryGetProperty("playback_speed", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 1000);
    }

    private static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int? Int(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;
}

public enum RepeatMode
{
    Off,
    One,
    All,
}

/// <summary>Group-level controller state. Supported commands are the wire strings (play, pause, next, ...).</summary>
public sealed record ControllerState
{
    public IReadOnlyList<string> SupportedCommands { get; init; } = Array.Empty<string>();
    public int Volume { get; init; }
    public bool Muted { get; init; }
    public RepeatMode Repeat { get; init; }
    public bool Shuffle { get; init; }
    public long? SeekMaxMs { get; init; }

    public bool Supports(string command) => SupportedCommands.Contains(command);

    public static ControllerState Merge(ControllerState current, JsonElement delta)
    {
        var s = current;
        foreach (var prop in delta.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "supported_commands" when prop.Value.ValueKind == JsonValueKind.Array:
                    s = s with { SupportedCommands = prop.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() };
                    break;
                case "volume" when prop.Value.ValueKind == JsonValueKind.Number:
                    s = s with { Volume = prop.Value.GetInt32() };
                    break;
                case "muted" when prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    s = s with { Muted = prop.Value.GetBoolean() };
                    break;
                case "repeat" when prop.Value.ValueKind == JsonValueKind.String:
                    s = s with { Repeat = prop.Value.GetString() switch { "one" => RepeatMode.One, "all" => RepeatMode.All, _ => RepeatMode.Off } };
                    break;
                case "shuffle" when prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    s = s with { Shuffle = prop.Value.GetBoolean() };
                    break;
                case "seek_max_ms":
                    s = s with { SeekMaxMs = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetInt64() : null };
                    break;
            }
        }

        return s;
    }
}

public enum GroupPlaybackState
{
    Unknown,
    Playing,
    Stopped,
}

public sealed record GroupState
{
    public GroupPlaybackState PlaybackState { get; init; }
    public string? GroupId { get; init; }
    public string? GroupName { get; init; }

    public static GroupState Merge(GroupState current, JsonElement delta)
    {
        var s = current;
        foreach (var prop in delta.EnumerateObject())
        {
            var str = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
            switch (prop.Name)
            {
                case "playback_state":
                    s = s with { PlaybackState = str switch { "playing" => GroupPlaybackState.Playing, "stopped" => GroupPlaybackState.Stopped, _ => GroupPlaybackState.Unknown } };
                    break;
                case "group_id":
                    s = s with { GroupId = str };
                    break;
                case "group_name":
                    s = s with { GroupName = str };
                    break;
            }
        }

        return s;
    }
}
