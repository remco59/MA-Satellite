using System.Text.Json;
using MaSatellite.MaApi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.MaApi;

/// <summary>Typed access to the parts of Music Assistant's API this app uses: library, search, queues and players.</summary>
public sealed class MaClient : IAsyncDisposable
{
    private static readonly string[] SearchMediaTypes = ["artist", "album", "track", "playlist"];
    private static readonly string[] RecentlyPlayedMediaTypes = ["track", "album"];

    private readonly MaConnection _connection;
    private readonly string _baseUrl;

    public MaClient(string host, int port, string? token, ILogger? logger = null)
    {
        _baseUrl = $"http://{host}:{port}";
        _connection = new MaConnection(MaConnection.BuildUri(host, port), token, logger ?? NullLogger.Instance);
        _connection.EventReceived += e => EventReceived?.Invoke(e);
        _connection.Disconnected += e => Disconnected?.Invoke(e);
    }

    public string BaseUrl => _baseUrl;

    public MaServerInfo? ServerInfo => _connection.ServerInfo;

    public bool IsConnected => _connection.IsConnected;

    public event Action<MaEvent>? EventReceived;

    public event Action<Exception?>? Disconnected;

    public Task ConnectAsync(CancellationToken ct = default) => _connection.ConnectAsync(ct);

    public async Task<MaSearchResults> SearchAsync(string query, int limit = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return MaSearchResults.Empty;
        }

        var result = await _connection.SendCommandAsync("music/search", new Dictionary<string, object?>
        {
            ["search_query"] = query.Trim(),
            ["media_types"] = SearchMediaTypes,
            ["limit"] = limit,
            ["library_only"] = false,
        }, cancellationToken: ct).ConfigureAwait(false);

        IReadOnlyList<MaItem> Items(string name) =>
            result.ValueKind == JsonValueKind.Object && result.TryGetProperty(name, out var arr) ? MaItemParser.ParseItems(arr, _baseUrl) : [];

        return new MaSearchResults(Items("artists"), Items("albums"), Items("tracks"), Items("playlists"));
    }

    /// <summary>Returns recent library listening history; the server is the source of truth.</summary>
    public async Task<IReadOnlyList<MaItem>> GetRecentlyPlayedAsync(int limit = 12, CancellationToken ct = default)
    {
        var result = await _connection.SendCommandAsync("music/recently_played_items", new Dictionary<string, object?>
        {
            ["limit"] = limit,
            ["media_types"] = RecentlyPlayedMediaTypes,
        }, cancellationToken: ct).ConfigureAwait(false);
        return MaItemParser.ParseItems(result, _baseUrl);
    }

    /// <summary>Looks up provider-backed artists similar to one from the listening history; may be unsupported by some providers.</summary>
    public async Task<IReadOnlyList<MaItem>> GetSimilarArtistsAsync(MaItem artist, int limit = 16, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artist);
        if (artist.Type != MaMediaType.Artist)
        {
            throw new ArgumentException("A seed artist is required.", nameof(artist));
        }

        var result = await _connection.SendCommandAsync("music/artists/similar_artists", new Dictionary<string, object?>
        {
            ["item_id"] = artist.ItemId,
            ["provider_instance_id_or_domain"] = artist.Provider,
            ["limit"] = limit,
        }, cancellationToken: ct).ConfigureAwait(false);

        return MaItemParser.ParseItems(result, _baseUrl);
    }

    /// <summary>Lists a library collection with optional server-side sorting across the whole library.</summary>
    public async Task<IReadOnlyList<MaItem>> GetLibraryAsync(MaMediaType type, string? search = null, int limit = 100, int offset = 0, CancellationToken ct = default, string? sortField = null, string? sortDirection = null)
    {
        var command = type switch
        {
            MaMediaType.Artist => "music/artists/library_items",
            MaMediaType.Album => "music/albums/library_items",
            MaMediaType.Track => "music/tracks/library_items",
            MaMediaType.Playlist => "music/playlists/library_items",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported library type"),
        };

        var args = new Dictionary<string, object?>
        {
            ["search"] = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            ["limit"] = limit,
            ["offset"] = offset,
        };
        if (!string.IsNullOrWhiteSpace(sortField))
        {
            args["sort_field"] = sortField;
            args["sort_direction"] = sortDirection is "asc" or "desc" ? sortDirection : "asc";
        }

        var result = await _connection.SendCommandAsync(command, args, cancellationToken: ct).ConfigureAwait(false);
        return MaItemParser.ParseItems(result, _baseUrl);
    }

    public async Task<IReadOnlyList<MaItem>> GetChildrenAsync(MaItem parent, CancellationToken ct = default)
    {
        var command = parent.Type switch
        {
            MaMediaType.Album => "music/albums/album_tracks",
            MaMediaType.Playlist => "music/playlists/playlist_tracks",
            MaMediaType.Artist => "music/artists/artist_albums",
            _ => throw new ArgumentOutOfRangeException(nameof(parent), parent.Type, "This item has no children"),
        };

        var result = await _connection.SendCommandAsync(command, new Dictionary<string, object?>
        {
            ["item_id"] = parent.ItemId,
            ["provider_instance_id_or_domain"] = parent.Provider,
        }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return MaItemParser.ParseItems(result, _baseUrl);
    }

    public async Task<IReadOnlyList<MaPlayer>> GetPlayersAsync(CancellationToken ct = default)
    {
        var result = await _connection.SendCommandAsync("players/all", null, cancellationToken: ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray().Select(MaItemParser.ParsePlayer).ToList()
            : [];
    }

    public async Task<IReadOnlyList<MaQueueInfo>> GetQueuesAsync(CancellationToken ct = default)
    {
        var result = await _connection.SendCommandAsync("player_queues/all", null, cancellationToken: ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray().Select(q => MaItemParser.ParseQueue(q, _baseUrl)).ToList()
            : [];
    }

    public async Task<IReadOnlyList<MaQueueItem>> GetQueueItemsAsync(string queueId, int limit = 200, int offset = 0, CancellationToken ct = default)
    {
        var result = await _connection.SendCommandAsync("player_queues/items", new Dictionary<string, object?>
        {
            ["queue_id"] = queueId,
            ["limit"] = limit,
            ["offset"] = offset,
        }, cancellationToken: ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray().Select(i => MaItemParser.ParseQueueItem(i, _baseUrl)).ToList()
            : [];
    }

    /// <summary>Plays or enqueues media (a URI) on a queue. The queue id of a player equals its player id.</summary>
    public Task PlayMediaAsync(string queueId, string uri, MaQueueOption option = MaQueueOption.Replace, CancellationToken ct = default) =>
        _connection.SendCommandAsync("player_queues/play_media", new Dictionary<string, object?>
        {
            ["queue_id"] = queueId,
            ["media"] = uri,
            ["option"] = option.ToString().ToLowerInvariant(),
        }, cancellationToken: ct);

    /// <summary>Moves the queue (items and position) from one player to another.</summary>
    public Task TransferQueueAsync(string sourceQueueId, string targetQueueId, bool autoPlay = true, CancellationToken ct = default) =>
        _connection.SendCommandAsync("player_queues/transfer", new Dictionary<string, object?>
        {
            ["source_queue_id"] = sourceQueueId,
            ["target_queue_id"] = targetQueueId,
            ["auto_play"] = autoPlay,
        }, cancellationToken: ct);

    public Task QueueCommandAsync(string queueId, string command, CancellationToken ct = default) =>
        _connection.SendCommandAsync($"player_queues/{command}", new Dictionary<string, object?> { ["queue_id"] = queueId }, cancellationToken: ct);

    public Task PlayQueueIndexAsync(string queueId, int index, CancellationToken ct = default) =>
        _connection.SendCommandAsync("player_queues/play_index", new Dictionary<string, object?> { ["queue_id"] = queueId, ["index"] = index }, cancellationToken: ct);

    public Task SeekQueueAsync(string queueId, double seconds, CancellationToken ct = default) =>
        _connection.SendCommandAsync("player_queues/seek", new Dictionary<string, object?>
        {
            ["queue_id"] = queueId,
            ["position"] = Math.Max(0, seconds),
        }, cancellationToken: ct);

    public Task SetShuffleAsync(string queueId, bool enabled, CancellationToken ct = default) =>
        _connection.SendCommandAsync("player_queues/shuffle", new Dictionary<string, object?>
        {
            ["queue_id"] = queueId,
            ["shuffle_enabled"] = enabled,
        }, cancellationToken: ct);

    public Task SetRepeatAsync(string queueId, string mode, CancellationToken ct = default) =>
        _connection.SendCommandAsync("player_queues/repeat", new Dictionary<string, object?>
        {
            ["queue_id"] = queueId,
            ["repeat_mode"] = mode is "one" or "all" ? mode : "off",
        }, cancellationToken: ct);

    public Task SetPlayerVolumeAsync(string playerId, int volume, CancellationToken ct = default) =>
        _connection.SendCommandAsync("players/cmd/volume_set", new Dictionary<string, object?>
        {
            ["player_id"] = playerId,
            ["volume_level"] = Math.Clamp(volume, 0, 100),
        }, cancellationToken: ct);

    public Task SetPlayerMutedAsync(string playerId, bool muted, CancellationToken ct = default) =>
        _connection.SendCommandAsync("players/cmd/volume_mute", new Dictionary<string, object?>
        {
            ["player_id"] = playerId,
            ["muted"] = muted,
        }, cancellationToken: ct);

    /// <summary>Finds the player that represents this PC: by client id, then by name.</summary>
    public static MaPlayer? FindThisPlayer(IEnumerable<MaPlayer> players, string clientId, string playerName)
    {
        var list = players.ToList();
        return list.FirstOrDefault(p => p.PlayerId.Equals(clientId, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(p => p.PlayerId.Contains(clientId, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(p => p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
