using MaSatellite.Host.Presentation;
using MaSatellite.Host.Settings;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Library;

/// <summary>A player (queue) that media can be played on.</summary>
public sealed record PlayTarget(string QueueId, string Name, bool IsThisPc, bool Available, string State);

public sealed record QueueView(PlayTarget Target, MaQueueInfo? Queue, IReadOnlyList<MaQueueItem> Items);

/// <summary>What the library window does, without any UI: targets, playing items, queue view and transfer.</summary>
public sealed class LibraryController
{
    private readonly MaLibraryService _service;
    private readonly SettingsStore _settings;

    public LibraryController(MaLibraryService service, SettingsStore settings)
    {
        _service = service;
        _settings = settings;
    }

    public MaLibraryService Service => _service;

    /// <summary>All players, with this PC first. A player that is not (yet) known to MA still appears as "This PC".</summary>
    public async Task<IReadOnlyList<PlayTarget>> GetTargetsAsync(CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        var players = await client.GetPlayersAsync(ct).ConfigureAwait(false);
        var s = _settings.Current;
        var me = MaClient.FindThisPlayer(players, s.ClientId, s.PlayerName);

        var targets = new List<PlayTarget>();
        if (me is not null)
        {
            targets.Add(new PlayTarget(me.PlayerId, "This PC (" + me.Name + ")", true, me.Available, me.PlaybackState));
        }

        targets.AddRange(players
            .Where(p => p.PlayerId != me?.PlayerId && p.Available)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PlayTarget(p.PlayerId, p.Name, false, p.Available, p.PlaybackState)));
        return targets;
    }

    public async Task PlayAsync(MaItem item, PlayTarget target, MaQueueOption option, CancellationToken ct = default)
    {
        if (!item.IsPlayable || string.IsNullOrEmpty(item.Uri))
        {
            throw new MaException($"'{item.Name}' cannot be played.");
        }

        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.PlayMediaAsync(target.QueueId, item.Uri, option, ct).ConfigureAwait(false);
    }

    public async Task<QueueView> GetQueueAsync(PlayTarget target, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        var queues = await client.GetQueuesAsync(ct).ConfigureAwait(false);
        var queue = queues.FirstOrDefault(q => q.QueueId == target.QueueId);
        var items = queue is { ItemCount: > 0 }
            ? await client.GetQueueItemsAsync(target.QueueId, 300, 0, ct).ConfigureAwait(false)
            : [];
        return new QueueView(target, queue, items);
    }

    /// <summary>Moves the queue of <paramref name="from"/> to <paramref name="to"/> and continues playing there.</summary>
    public async Task TransferAsync(PlayTarget from, PlayTarget to, CancellationToken ct = default)
    {
        if (from.QueueId == to.QueueId)
        {
            throw new MaException("Choose a different player to transfer the queue to.");
        }

        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.TransferQueueAsync(from.QueueId, to.QueueId, autoPlay: true, ct).ConfigureAwait(false);
    }

    public async Task QueueControlAsync(PlayTarget target, string command, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.QueueCommandAsync(target.QueueId, command, ct).ConfigureAwait(false);
    }

    public async Task SeekAsync(PlayTarget target, double seconds, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.SeekQueueAsync(target.QueueId, seconds, ct).ConfigureAwait(false);
    }

    public async Task SetShuffleAsync(PlayTarget target, bool enabled, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.SetShuffleAsync(target.QueueId, enabled, ct).ConfigureAwait(false);
    }

    public async Task SetRepeatAsync(PlayTarget target, string mode, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.SetRepeatAsync(target.QueueId, mode, ct).ConfigureAwait(false);
    }

    public async Task SetVolumeAsync(PlayTarget target, int level, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.SetPlayerVolumeAsync(target.QueueId, level, ct).ConfigureAwait(false);
    }

    public async Task SetMutedAsync(PlayTarget target, bool muted, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.SetPlayerMutedAsync(target.QueueId, muted, ct).ConfigureAwait(false);
    }

    public async Task PlayQueueIndexAsync(PlayTarget target, int index, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.PlayQueueIndexAsync(target.QueueId, index, ct).ConfigureAwait(false);
    }

    /// <summary>The entry a queue is on right now. One cheap call; used to find the playing track's library item.</summary>
    public async Task<MaQueueItem?> GetCurrentQueueItemAsync(PlayTarget target, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        var queues = await client.GetQueuesAsync(ct).ConfigureAwait(false);
        return queues.FirstOrDefault(q => q.QueueId == target.QueueId)?.CurrentItem;
    }

    public async Task RemoveQueueItemAsync(PlayTarget target, string queueItemId, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.RemoveQueueItemAsync(target.QueueId, queueItemId, ct).ConfigureAwait(false);
    }

    public async Task MoveQueueItemAsync(PlayTarget target, string queueItemId, int positions, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.MoveQueueItemAsync(target.QueueId, queueItemId, positions, ct).ConfigureAwait(false);
    }

    public async Task ClearQueueAsync(PlayTarget target, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.ClearQueueAsync(target.QueueId, ct).ConfigureAwait(false);
    }

    /// <summary>The library item behind a queue entry (favourite flag, lyrics, genres), or null when unknown.</summary>
    public async Task<MaItem?> GetItemByUriAsync(string uri, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        return await client.GetItemByUriAsync(uri, ct).ConfigureAwait(false);
    }

    public async Task SetFavoriteAsync(MaItem item, bool favorite, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.SetFavoriteAsync(item, favorite, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Artists similar to the one named on the current track. The server is asked, never guessed: the artist is
    /// looked up by name and must match exactly, otherwise there is nothing related to show.
    /// </summary>
    public async Task<IReadOnlyList<MaItem>> GetRelatedArtistsAsync(string artistNames, CancellationToken ct = default)
    {
        var name = RelatedArtist.PrimaryName(artistNames);
        if (name is null)
        {
            return [];
        }

        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        var found = await client.SearchAsync(name, 10, ct).ConfigureAwait(false);
        var artist = RelatedArtist.Pick(found.Artists, name);
        return artist is null ? [] : await client.GetSimilarArtistsAsync(artist, 16, ct).ConfigureAwait(false);
    }
}
