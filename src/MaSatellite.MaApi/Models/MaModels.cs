namespace MaSatellite.MaApi.Models;

public enum MaMediaType
{
    Artist,
    Album,
    Track,
    Playlist,
    Radio,
    Other,
}

/// <summary>A library or search result item, reduced to what the UI needs.</summary>
public sealed record MaItem(
    MaMediaType Type,
    string ItemId,
    string Provider,
    string Name,
    string Uri,
    string? Subtitle,
    string? AlbumName,
    int? Year,
    int? DurationSeconds,
    string? ImageUrl,
    bool IsPlayable = true)
{
    /// <summary>"Artist · Album" style one-liner for lists.</summary>
    public string Description => string.Join(" · ", new[] { Subtitle, AlbumName, Year?.ToString() }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed record MaSearchResults(
    IReadOnlyList<MaItem> Artists,
    IReadOnlyList<MaItem> Albums,
    IReadOnlyList<MaItem> Tracks,
    IReadOnlyList<MaItem> Playlists)
{
    public static MaSearchResults Empty { get; } = new([], [], [], []);

    public bool IsEmpty => Artists.Count + Albums.Count + Tracks.Count + Playlists.Count == 0;
}

public sealed record MaPlayer(string PlayerId, string Name, bool Available, bool? Powered, string PlaybackState, string Type, int? Volume);

public sealed record MaQueueInfo(
    string QueueId,
    string DisplayName,
    bool Active,
    bool Available,
    int ItemCount,
    int? CurrentIndex,
    string State,
    bool Shuffle,
    string Repeat,
    double ElapsedSeconds,
    MaQueueItem? CurrentItem);

public sealed record MaQueueItem(
    string QueueItemId,
    string Name,
    string? Subtitle,
    int? DurationSeconds,
    int Index,
    string? ImageUrl,
    string? Uri);

/// <summary>How new media is added to a queue (maps to MA's <c>QueueOption</c>).</summary>
public enum MaQueueOption
{
    /// <summary>Insert at the current position and start playing.</summary>
    Play,

    /// <summary>Replace the whole queue and start playing.</summary>
    Replace,

    /// <summary>Insert after the current item.</summary>
    Next,

    /// <summary>Append to the end.</summary>
    Add,
}

public sealed record MaServerInfo(string ServerId, string ServerVersion, int SchemaVersion, int MinSupportedSchemaVersion, string? Name);
