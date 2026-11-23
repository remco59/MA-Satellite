using System.Text.Json;
using MaSatellite.MaApi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.MaApi;

/// <summary>Typed access to the parts of Music Assistant's API this app uses: library, search, queues and players.</summary>
public sealed class MaClient : IAsyncDisposable
{
    private static readonly string[] SearchMediaTypes = ["track", "artist", "album", "playlist", "radio", "podcast", "podcast_episode", "audiobook", "genre"];
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

    public async Task<MaSearchResults> SearchAsync(string query, int limit = 25, IReadOnlyList<string>? providers = null, CancellationToken ct = default)
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
            ["providers"] = providers is { Count: > 0 } ? providers : null,
        }, cancellationToken: ct).ConfigureAwait(false);

        IReadOnlyList<MaItem> Items(string name) =>
            result.ValueKind == JsonValueKind.Object && result.TryGetProperty(name, out var arr) ? MaItemParser.ParseItems(arr, _baseUrl) : [];

        return new MaSearchResults(Items("artists"), Items("albums"), Items("tracks"), Items("playlists"), Items("radio"),
            Items("podcasts"), Items("podcast_episodes"), Items("audiobooks"), Items("genres"));
    }

    /// <summary>Returns recent library listening history; the server is the source of truth.</summary>
    public async Task<IReadOnlyList<MaItem>> GetRecentlyPlayedAsync(int limit = 12, CancellationToken ct = default) =>
        await GetRecentlyPlayedAsync(limit, RecentlyPlayedMediaTypes, ct).ConfigureAwait(false);

    /// <summary>Recent listening history restricted to the given MA media types (e.g. "playlist", "album", "track").</summary>
    public async Task<IReadOnlyList<MaItem>> GetRecentlyPlayedAsync(int limit, IReadOnlyList<string> mediaTypes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mediaTypes);
        var result = await _connection.SendCommandAsync("music/recently_played_items", new Dictionary<string, object?>
        {
            ["limit"] = limit,
            ["media_types"] = mediaTypes,
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
    public async Task<IReadOnlyList<MaItem>> GetLibraryAsync(MaMediaType type, string? search = null, int limit = 100, int offset = 0, string? sortField = null, string? sortDirection = null, bool favoriteOnly = false, CancellationToken ct = default)
    {
        var command = type switch
        {
            MaMediaType.Artist => "music/artists/library_items",
            MaMediaType.Album => "music/albums/library_items",
            MaMediaType.Track => "music/tracks/library_items",
            MaMediaType.Playlist => "music/playlists/library_items",
            MaMediaType.Radio => "music/radios/library_items",
            MaMediaType.Podcast => "music/podcasts/library_items",
            MaMediaType.Audiobook => "music/audiobooks/library_items",
            MaMediaType.Genre => "music/genres/library_items",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported library type"),
        };

        var args = new Dictionary<string, object?>
        {
            ["search"] = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            ["limit"] = limit,
            ["offset"] = offset,
        };
        if (favoriteOnly)
        {
            args["favorite"] = true;
        }

        if (!string.IsNullOrWhiteSpace(sortField))
        {
            // Music Assistant 2.11 switched from the legacy order_by parameter
            // (e.g. "year_desc") to the typed sort_field/sort_direction API.
            // Older servers silently ignore unknown arguments, leaving the UI
            // looking alphabetically sorted even with a different sort selected.
            var direction = sortDirection is "asc" or "desc" ? sortDirection : "asc";
            if (SupportsTypedLibrarySorting(ServerInfo?.ServerVersion))
            {
                args["sort_field"] = sortField;
                args["sort_direction"] = direction;
            }
            else
            {
                args["order_by"] = direction == "desc" ? $"{sortField}_desc" : sortField;
            }
        }

        var result = await _connection.SendCommandAsync(command, args, cancellationToken: ct).ConfigureAwait(false);
        return MaItemParser.ParseItems(result, _baseUrl);
    }

    /// <summary>Typed sort parameters were introduced in Music Assistant 2.11.</summary>
    /// <remarks>Use legacy order_by when the server version is unavailable.</remarks>
    private static bool SupportsTypedLibrarySorting(string? serverVersion)
    {
        var parts = serverVersion?.TrimStart('v', 'V').Split('.');
        return parts is { Length: >= 2 }
            && int.TryParse(parts[0], out var major)
            && int.TryParse(parts[1], out var minor)
            && (major > 2 || major == 2 && minor >= 11);
    }

    /// <summary>Fetch the full server-side metadata instead of relying on a library/search item mapping.</summary>
    public async Task<MaItem> GetItemDetailsAsync(MaItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var result = await _connection.SendCommandAsync("music/item_by_uri", new Dictionary<string, object?>
        {
            ["uri"] = item.Uri,
        }, cancellationToken: ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Object
            ? MaItemParser.ParseItem(result, _baseUrl, imageSize: 512)
            : item;
    }

    /// <summary>Full metadata for one item (lyrics, external ids, label, credited artists).</summary>
    public async Task<MaItemDetails> GetDetailsAsync(string uri, CancellationToken ct = default)
    {
        var result = await _connection.SendCommandAsync("music/item_by_uri", new Dictionary<string, object?> { ["uri"] = uri }, cancellationToken: ct).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new MaException("Music Assistant returned no details for this item.");
        }

        return MaItemParser.ParseDetails(result, _baseUrl);
    }

    /// <summary>Adds an item (by URI) to the user's favourites.</summary>
    public Task AddFavoriteAsync(string uri, CancellationToken ct = default) =>
        _connection.SendCommandAsync("music/favorites/add_item", new Dictionary<string, object?> { ["item"] = uri }, cancellationToken: ct);

    /// <summary>Removes a favourite; Music Assistant identifies it by media type and library item id.</summary>
    public Task RemoveFavoriteAsync(MaMediaType type, string libraryItemId, CancellationToken ct = default) =>
        _connection.SendCommandAsync("music/favorites/remove_item", new Dictionary<string, object?>
        {
            ["media_type"] = type.ToString().ToLowerInvariant(),
            ["library_item_id"] = libraryItemId,
        }, cancellationToken: ct);

    /// <summary>Provider-backed similar tracks for a track; providers without support yield an error or an empty list.</summary>
    public async Task<IReadOnlyList<MaItem>> GetSimilarTracksAsync(string itemId, string provider, int limit = 20, CancellationToken ct = default)
    {
        var result = await _connection.SendCommandAsync("music/tracks/similar_tracks", new Dictionary<string, object?>
        {
            ["item_id"] = itemId,
            ["provider_instance_id_or_domain"] = provider,
            ["limit"] = limit,
        }, cancellationToken: ct).ConfigureAwait(false);
        return MaItemParser.ParseItems(result, _baseUrl);
    }

    /// <summary>Same as <see cref="GetSimilarArtistsAsync(MaItem,int,CancellationToken)"/> but from raw ids, for an artist that is not a full item yet.</summary>
    public async Task<IReadOnlyList<MaItem>> GetSimilarArtistsAsync(string itemId, string provider, int limit = 16, CancellationToken ct = default)
    {
        var result = await _connection.SendCommandAsync("music/artists/similar_artists", new Dictionary<string, object?>
        {
            ["item_id"] = itemId,
            ["provider_instance_id_or_domain"] = provider,
            ["limit"] = limit,
        }, cancellationToken: ct).ConfigureAwait(false);
        return MaItemParser.ParseItems(result, _baseUrl);
    }

    /// <summary>The artist's tracks as known to the provider (popular first where the provider orders them).</summary>
    public async Task<IReadOnlyList<MaItem>> GetArtistTracksAsync(string itemId, string provider, CancellationToken ct = default)
    {
        var result = await _connection.SendCommandAsync("music/artists/artist_tracks", new Dictionary<string, object?>
        {
            ["item_id"] = itemId,
            ["provider_instance_id_or_domain"] = provider,
        }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
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

    /// <summary>Returns unfinished audiobooks and podcast episodes, preserving Music Assistant resume metadata.</summary>
    public async Task<IReadOnlyList<MaItem>> GetInProgressItemsAsync(int limit = 30, CancellationToken ct = default)
    {
        var result = await _connection.SendCommandAsync("music/in_progress_items",
            new Dictionary<string, object?> { ["limit"] = Math.Clamp(limit, 1, 200) }, cancellationToken: ct).ConfigureAwait(false);
        return MaItemParser.ParseItems(result, _baseUrl);
    }

    /// <summary>Lists the episodes of a podcast.</summary>
    public async Task<IReadOnlyList<MaItem>> GetPodcastEpisodesAsync(MaItem podcast, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(podcast);
        if (podcast.Type != MaMediaType.Podcast) throw new ArgumentException("A podcast item is required.", nameof(podcast));
        var result = await _connection.SendCommandAsync("music/podcasts/podcast_episodes", new Dictionary<string, object?>
        {
            ["item_id"] = podcast.ItemId,
            ["provider_instance_id_or_domain"] = podcast.Provider,
            ["limit"] = Math.Clamp(limit, 1, 500),
            ["offset"] = Math.Max(0, offset),
        }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return MaItemParser.ParseItems(result, _baseUrl);
    }

    /// <summary>Adds or removes members from a Music Assistant sync/group player.</summary>
    public Task SetGroupMembersAsync(string targetPlayerId, IReadOnlyList<string>? add = null, IReadOnlyList<string>? remove = null, CancellationToken ct = default) =>
        _connection.SendCommandAsync("players/cmd/set_members", new Dictionary<string, object?>
        {
            ["player_id"] = targetPlayerId,
            ["target_player"] = targetPlayerId,
            ["player_ids_to_add"] = add,
            ["player_ids_to_remove"] = remove,
        }, cancellationToken: ct);

    public Task UngroupPlayerAsync(string playerId, CancellationToken ct = default) =>
        _connection.SendCommandAsync("players/cmd/ungroup", new Dictionary<string, object?> { ["player_id"] = playerId }, cancellationToken: ct);

    public Task SetQueueAutoplayAsync(string queueId, bool enabled, CancellationToken ct = default) =>
        _connection.SendCommandAsync("player_queues/autoplay", new Dictionary<string, object?>
        {
            ["queue_id"] = queueId,
            ["autoplay_enabled"] = enabled,
        }, cancellationToken: ct);

    public Task SetQueueCrossfadeAsync(string queueId, bool enabled, CancellationToken ct = default) =>
        _connection.SendCommandAsync("player_queues/crossfade", new Dictionary<string, object?>
        {
            ["queue_id"] = queueId,
            ["crossfade_enabled"] = enabled,
        }, cancellationToken: ct);

    /// <summary>Stores per-queue options such as Smart Shuffle and the autoplay/crossfade mode.</summary>
    public Task SaveQueueSettingsAsync(string queueId, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default) =>
        _connection.SendCommandAsync("config/player_queues/save", new Dictionary<string, object?>
        {
            ["queue_id"] = queueId,
            ["values"] = values,
        }, cancellationToken: ct);

    public Task<double?> SetSleepTimerAsync(string playerId, int? seconds, string? mode = null, CancellationToken ct = default) =>
        SetSleepTimerCoreAsync(playerId, seconds, mode, ct);

    private async Task<double?> SetSleepTimerCoreAsync(string playerId, int? seconds, string? mode, CancellationToken ct)
    {
        var args = new Dictionary<string, object?> { ["player_id"] = playerId };
        if (seconds.HasValue) args["seconds"] = Math.Clamp(seconds.Value, 1, 86400);
        if (!string.IsNullOrWhiteSpace(mode)) args["mode"] = mode;
        var result = await _connection.SendCommandAsync("players/sleep_timer/set", args, cancellationToken: ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Number ? (double?)result.GetDouble() : null;
    }

    public Task ClearSleepTimerAsync(string playerId, CancellationToken ct = default) =>
        _connection.SendCommandAsync("players/sleep_timer/clear", new Dictionary<string, object?> { ["player_id"] = playerId }, cancellationToken: ct);

    /// <summary>Sets playback speed for the current audiobook or podcast episode (0.5x–3.0x).</summary>
    public Task SetPlaybackSpeedAsync(string queueId, double speed, string? queueItemId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(queueId)) throw new ArgumentException("A queue id is required.", nameof(queueId));
        if (speed is < 0.5 or > 3.0) throw new ArgumentOutOfRangeException(nameof(speed), "Playback speed must be between 0.5x and 3.0x.");
        var args = new Dictionary<string, object?> { ["queue_id"] = queueId, ["speed"] = speed };
        if (!string.IsNullOrWhiteSpace(queueItemId)) args["queue_item_id"] = queueItemId;
        return _connection.SendCommandAsync("player_queues/set_playback_speed", args, cancellationToken: ct);
    }

    public async Task<MaItem?> CreatePlaylistAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A playlist name is required.", nameof(name));
        // Omitting the provider selects Music Assistant's built-in playlist provider.
        var result = await _connection.SendCommandAsync("music/playlists/create_playlist", new Dictionary<string, object?>
        {
            ["name"] = name.Trim(),
        }, cancellationToken: ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Object ? MaItemParser.ParseItem(result, _baseUrl) : null;
    }

    public async Task<MaItem?> ImportPlaylistAsync(string m3uData, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(m3uData)) throw new ArgumentException("M3U playlist data is required.", nameof(m3uData));
        var result = await _connection.SendCommandAsync("music/playlists/import_playlist", new Dictionary<string, object?>
        {
            ["m3u_data"] = m3uData,
            ["library_matching"] = true,
        }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Object ? MaItemParser.ParseItem(result, _baseUrl) : null;
    }

    public async Task<string> ExportPlaylistAsync(MaItem playlist, CancellationToken ct = default)
    {
        if (playlist.Type != MaMediaType.Playlist) throw new ArgumentException("A playlist is required.", nameof(playlist));
        var result = await _connection.SendCommandAsync("music/playlists/export_playlist", new Dictionary<string, object?>
        {
            ["db_playlist_id"] = playlist.ItemId,
        }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.String ? result.GetString() ?? string.Empty : result.ToString();
    }

    public Task RemovePlaylistTracksAsync(MaItem playlist, IReadOnlyList<int> positions, IReadOnlyList<string>? expectedUris = null, CancellationToken ct = default)
    {
        if (playlist.Type != MaMediaType.Playlist) throw new ArgumentException("A playlist is required.", nameof(playlist));
        if (positions.Count == 0) throw new ArgumentException("At least one playlist position is required.", nameof(positions));
        if (expectedUris is not null && expectedUris.Count != positions.Count)
            throw new ArgumentException("The URI list must match the selected playlist positions.", nameof(expectedUris));

        var args = new Dictionary<string, object?>
        {
            ["db_playlist_id"] = playlist.ItemId,
            ["positions_to_remove"] = positions,
        };
        // Music Assistant verifies these URIs against the listed positions before mutating the playlist.
        if (expectedUris is { Count: > 0 }) args["uris"] = expectedUris;
        return _connection.SendCommandAsync("music/playlists/remove_playlist_tracks", args, TimeSpan.FromSeconds(60), ct);
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
        // Music Assistant does not number queue items itself: their place in the returned list (plus the page
        // offset) is the index that current_index and play_index refer to.
        return result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray().Select((i, position) => MaItemParser.ParseQueueItem(i, _baseUrl) with { Index = offset + position }).ToList()
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

    /// <summary>
    /// Moves a queue item by <paramref name="positionShift"/> places (negative = up, positive = down);
    /// 0 moves it to play next. Music Assistant 2.x semantics.
    /// </summary>
    public Task MoveQueueItemAsync(string queueId, string queueItemId, int positionShift, CancellationToken ct = default) =>
        _connection.SendCommandAsync("player_queues/move_item", new Dictionary<string, object?>
        {
            ["queue_id"] = queueId,
            ["queue_item_id"] = queueItemId,
            ["pos_shift"] = positionShift,
        }, cancellationToken: ct);

    public Task DeleteQueueItemAsync(string queueId, string queueItemId, CancellationToken ct = default) =>
        _connection.SendCommandAsync("player_queues/delete_item", new Dictionary<string, object?>
        {
            ["queue_id"] = queueId,
            ["item_id_or_index"] = queueItemId,
        }, cancellationToken: ct);

    public Task ClearQueueAsync(string queueId, CancellationToken ct = default) => QueueCommandAsync(queueId, "clear", ct);

    /// <summary>Adds tracks (by URI) to a library playlist the server lets us edit.</summary>
    public Task AddToPlaylistAsync(string playlistItemId, IReadOnlyList<string> trackUris, CancellationToken ct = default) =>
        _connection.SendCommandAsync("music/playlists/add_playlist_tracks", new Dictionary<string, object?>
        {
            ["db_playlist_id"] = playlistItemId,
            ["uris"] = trackUris,
        }, cancellationToken: ct);

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
